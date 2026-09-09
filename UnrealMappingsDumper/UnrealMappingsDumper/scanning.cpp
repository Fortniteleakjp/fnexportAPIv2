#include "pch.h"

#include "app.h"
#include "scanning.h"
#include "unrealTypes.h"
#include "../Dependencies/Memcury/memcury.h"

// ---------------------------------------------------------------------------
// fnexportAPI local patch: GObjects located by shape rather than by signature.
//
// UE6 matches none of the byte patterns, and adding more of them only postpones the next break, so
// the array is recognised by its own invariants instead. The layout is not assumed either: UE6
// swapped Num/Max in FChunkedFixedUObjectArray, moved PreAllocatedObjects to the end, and pushed
// the object pointer inside FUObjectItem from +0 to +8 (possibly packed). Each known layout is
// tried against real memory, and the one that actually resolves objects is recorded for the dump.
// ---------------------------------------------------------------------------
namespace
{
	// A loaded editor holds far more than this; the bounds only reject noise that happens to look
	// self-consistent.
	constexpr int32_t MinPlausibleObjects = 1000;
	constexpr int32_t MaxPlausibleObjects = 100 * 1000 * 1000;

	// Chunk sizes the engine could plausibly have been built with. 64K is what every version has
	// used, but the value is derived rather than assumed so a changed one is found, not missed.
	constexpr int32_t MinChunkSize = 1024;
	constexpr int32_t MaxChunkSize = 1024 * 1024;

	// Regions far larger than the array are not worth walking end to end.
	constexpr size_t MaxRegionBytesToScan = 256ull * 1024 * 1024;

	// The two field orders FChunkedFixedUObjectArray has shipped with. Objects is at 0 in both.
	struct FArrayFieldOrder
	{
		const char* Name;
		int32_t NumElements;
		int32_t MaxElements;
		int32_t NumChunks;
		int32_t MaxChunks;
	};

	// UE6 first: that is what this dumper is being asked about, and testing it first means the
	// legacy order never gets a chance to match a UE6 array by coincidence.
	constexpr FArrayFieldOrder ArrayFieldOrders[] =
	{
		{ "UE6",    0x08, 0x0C, 0x10, 0x14 },
		{ "legacy", 0x14, 0x10, 0x1C, 0x18 },
	};

	// How the object pointer is stored inside FUObjectItem. The stride is 24 in every version.
	struct FItemLayout
	{
		const char* Name;
		int32_t ObjectOffset;
		bool Packed;
	};

	constexpr FItemLayout ItemLayouts[] =
	{
		{ "UE6",           0x08, false },
		{ "UE6 packed",    0x08, true  },
		{ "legacy",        0x00, false },
	};

	constexpr int32_t ItemStride = 24;

	bool IsReadable(uintptr_t Address, size_t Size)
	{
		if (!Address)
			return false;

		MEMORY_BASIC_INFORMATION Info{};
		if (!VirtualQuery(reinterpret_cast<void*>(Address), &Info, sizeof(Info)))
			return false;

		if (Info.State != MEM_COMMIT)
			return false;

		constexpr DWORD ReadableProtection =
			PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
			PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

		if (!(Info.Protect & ReadableProtection) || (Info.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
			return false;

		// The whole span has to sit inside this one region; a span crossing into an uncommitted
		// neighbour would still fault.
		auto RegionEnd = reinterpret_cast<uintptr_t>(Info.BaseAddress) + Info.RegionSize;
		return Address + Size <= RegionEnd;
	}

	FORCEINLINE int32_t ReadInt(uintptr_t Base, int32_t Offset)
	{
		return *reinterpret_cast<int32_t*>(Base + Offset);
	}

	// The cheap half of the test: the counters have to describe a consistent chunked array under
	// this field order, and they have to agree on one chunk size. Runs on every 8-byte offset, so it
	// touches no memory beyond the candidate. Returns the chunk size, or 0 when this is not one.
	int32_t DeriveChunkSize(uintptr_t Candidate, const FArrayFieldOrder& Order)
	{
		auto NumElements = ReadInt(Candidate, Order.NumElements);
		auto MaxElements = ReadInt(Candidate, Order.MaxElements);
		auto NumChunks = ReadInt(Candidate, Order.NumChunks);
		auto MaxChunks = ReadInt(Candidate, Order.MaxChunks);

		if (NumElements < MinPlausibleObjects || NumElements > MaxPlausibleObjects)
			return 0;

		if (MaxElements < NumElements || NumChunks <= 0 || MaxChunks < NumChunks)
			return 0;

		// Capacity is exactly the chunk capacity: MaxElements = MaxChunks * chunk size.
		if (MaxElements % MaxChunks != 0)
			return 0;

		auto ChunkSize = MaxElements / MaxChunks;
		if (ChunkSize < MinChunkSize || ChunkSize > MaxChunkSize)
			return 0;

		// The engine sizes chunks as a power of two.
		if ((ChunkSize & (ChunkSize - 1)) != 0)
			return 0;

		// Chunks are allocated as elements need them, so enough of them must already exist.
		if (NumChunks < (NumElements + ChunkSize - 1) / ChunkSize)
			return 0;

		// The chunk table is a heap allocation, so it is at least pointer-aligned.
		auto ChunkTable = *reinterpret_cast<uintptr_t*>(Candidate);
		return (ChunkTable && (ChunkTable & 7) == 0) ? ChunkSize : 0;
	}

	// Reads an object pointer out of one item under the given layout, without trusting it.
	uintptr_t ReadItemObject(uintptr_t Item, const FItemLayout& Layout)
	{
		if (!Layout.Packed)
			return *reinterpret_cast<uintptr_t*>(Item + Layout.ObjectOffset);

		auto Low = static_cast<uintptr_t>(*reinterpret_cast<uint32_t*>(Item + Layout.ObjectOffset));
		auto FlagsAndRefCount = *reinterpret_cast<int64_t*>(Item);
		auto PtrMask = static_cast<uintptr_t>(~(0xFFFFFFFFu << GObjectFlagsMinBitIndex));
		auto High = (static_cast<uintptr_t>(FlagsAndRefCount >> 32) & PtrMask) << (32 + GObjectPtrTrailingZeroes);

		return High | (Low << GObjectPtrTrailingZeroes);
	}

	// The expensive half: follow the pointers and check that they lead to real objects. On success
	// the item layout that worked is reported through OutItemLayout.
	bool PointsAtRealObjects(uintptr_t Candidate, const FArrayFieldOrder& Order, const FItemLayout*& OutItemLayout)
	{
		auto ChunkTable = *reinterpret_cast<uintptr_t*>(Candidate);
		auto NumChunks = ReadInt(Candidate, Order.NumChunks);
		auto NumElements = ReadInt(Candidate, Order.NumElements);

		if (!IsReadable(ChunkTable, sizeof(uintptr_t) * static_cast<size_t>(NumChunks)))
			return false;

		// Every chunk the element count claims to reach has to be allocated and readable.
		auto Chunks = reinterpret_cast<uintptr_t*>(ChunkTable);
		for (int32_t Chunk = 0; Chunk < NumChunks; Chunk++)
		{
			if (!IsReadable(Chunks[Chunk], sizeof(uintptr_t)))
				return false;
		}

		// Slots can be empty, so the first chunk is walked until live objects turn up. Several are
		// required to agree before a layout is accepted: one lucky readable value is not proof.
		auto SlotsToTry = NumElements < 512 ? NumElements : 512;
		if (!IsReadable(Chunks[0], static_cast<size_t>(ItemStride) * SlotsToTry))
			return false;

		constexpr int32_t RequiredHits = 8;

		for (auto& Layout : ItemLayouts)
		{
			int32_t Hits = 0;

			for (int32_t Slot = 0; Slot < SlotsToTry && Hits < RequiredHits; Slot++)
			{
				auto Object = ReadItemObject(Chunks[0] + static_cast<size_t>(ItemStride) * Slot, Layout);

				if (!Object || (Object & 7) != 0)
					continue;

				if (!IsReadable(Object, sizeof(uintptr_t)))
					break;

				// The vtable pointer of a real UObject points into loaded code.
				if (!IsReadable(*reinterpret_cast<uintptr_t*>(Object), sizeof(uintptr_t)))
					break;

				Hits++;
			}

			if (Hits >= RequiredHits)
			{
				OutItemLayout = &Layout;
				return true;
			}
		}

		return false;
	}

	// How many candidates got past the counter test, so a run that finds nothing still says whether
	// it saw anything array-shaped at all.
	int32_t GNearMisses = 0;

	// Bytes actually walked, so "found nothing" can be told apart from "never looked".
	uint64_t GBytesScanned = 0;

	// The scan reads addresses it has only checked at region granularity, and it runs inside the
	// editor. A page that is unmapped between the check and the read must not crash the host, so the
	// counter test is entered through a filter. Kept free of C++ objects for __try.
	int32_t SafeDeriveChunkSize(uintptr_t Candidate, const FArrayFieldOrder& Order)
	{
		__try
		{
			return DeriveChunkSize(Candidate, Order);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			return 0;
		}
	}

	/// <summary>
	/// The pointer-following test, behind the same filter. It checks each address before reading it,
	/// but the editor keeps allocating and freeing while this runs, so a page can still go away in
	/// between — over minutes of scanning that is not a rare event.
	/// </summary>
	bool SafePointsAtRealObjects(uintptr_t Candidate, const FArrayFieldOrder& Order, const FItemLayout*& OutItemLayout)
	{
		__try
		{
			return PointsAtRealObjects(Candidate, Order, OutItemLayout);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	/// <summary>Reads one pointer-sized word, or 0 if the page has gone away.</summary>
	uintptr_t SafeReadPointer(uintptr_t Address)
	{
		__try
		{
			return *reinterpret_cast<uintptr_t*>(Address);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			return 0;
		}
	}

	// Walks one committed span. Returns the address of the array, or 0.
	uintptr_t ScanSpan(uintptr_t Start, uintptr_t End)
	{
		if (End <= Start || End - Start < 0x20)
			return 0;

		auto Last = End - 0x20;
		GBytesScanned += End - Start;

		// Whole-process scanning covers gigabytes, so the common case has to be rejected without
		// entering the counter test at all. Objects is at offset 0 in every layout and is a heap
		// allocation, so anything that is not a plausible pointer there cannot be the array.
		for (auto Cursor = Start; Cursor <= Last; Cursor += sizeof(uintptr_t))
		{
			auto ChunkTable = SafeReadPointer(Cursor);
			if (ChunkTable < 0x10000 || (ChunkTable & 7) != 0)
				continue;

			for (auto& Order : ArrayFieldOrders)
			{
				auto ChunkSize = SafeDeriveChunkSize(Cursor, Order);
				if (!ChunkSize)
					continue;

				GNearMisses++;

				const FItemLayout* ItemLayout = nullptr;
				if (!SafePointsAtRealObjects(Cursor, Order, ItemLayout))
					continue;

				GObjectArrayLayout =
				{
					Order.NumElements, Order.MaxElements, Order.NumChunks, Order.MaxChunks,
					ItemStride, ItemLayout->ObjectOffset, ItemLayout->Packed, ChunkSize
				};

				UE_LOG("GObjects: %s array layout, %s item layout, %d objects across %d/%d chunks of %d",
					Order.Name, ItemLayout->Name,
					ReadInt(Cursor, Order.NumElements), ReadInt(Cursor, Order.NumChunks),
					ReadInt(Cursor, Order.MaxChunks), ChunkSize);

				return Cursor;
			}
		}

		return 0;
	}
}

uintptr_t GObjectsHeuristicScanObject::TryFind()
{
	GNearMisses = 0;

	auto ModuleBase = Memcury::PE::GetModuleBase();

	// First pass: the module's own data, where a global lives in an ordinary build.
	if (ModuleBase)
	{
		auto Headers = Memcury::PE::GetNTHeaders();
		auto Section = IMAGE_FIRST_SECTION(Headers);

		for (WORD Index = 0; Index < Headers->FileHeader.NumberOfSections; Index++, Section++)
		{
			// Only executable code is skipped. Characteristics are not trusted any further: a
			// protected build can leave them looking odd while the data is mapped and readable.
			if (Section->Characteristics & IMAGE_SCN_MEM_EXECUTE)
				continue;

			auto Before = GNearMisses;
			auto Start = ModuleBase + Section->VirtualAddress;
			auto Found = ScanSpan(Start, Start + Section->Misc.VirtualSize);

			UE_LOG("  section %.8s: %u KB, %d array-shaped",
				Section->Name, Section->Misc.VirtualSize / 1024, GNearMisses - Before);

			if (Found)
			{
				UE_LOG("GObjects found in section %.8s (+0x%llX)",
					Section->Name, (unsigned long long)(Found - ModuleBase));
				return Found;
			}
		}
	}

	UE_LOG("GObjects was not in the module's data sections (%d array-shaped candidates, %llu MB scanned); scanning process memory",
		GNearMisses, (unsigned long long)(GBytesScanned / (1024 * 1024)));

	// Second pass: the rest of the process. A packed or protected build can move its globals out of
	// the image, which is exactly the case the first pass cannot cover.
	SYSTEM_INFO SystemInfo{};
	GetSystemInfo(&SystemInfo);

	auto Address = reinterpret_cast<uintptr_t>(SystemInfo.lpMinimumApplicationAddress);
	auto Ceiling = reinterpret_cast<uintptr_t>(SystemInfo.lpMaximumApplicationAddress);

	while (Address < Ceiling)
	{
		MEMORY_BASIC_INFORMATION Info{};
		if (!VirtualQuery(reinterpret_cast<void*>(Address), &Info, sizeof(Info)))
			break;

		auto Base = reinterpret_cast<uintptr_t>(Info.BaseAddress);
		auto Next = Base + Info.RegionSize;

		constexpr DWORD Writable = PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

		// The array is written to, so only writable committed memory can hold it. Image regions are
		// included: if the first pass missed the data because of how the sections describe
		// themselves, excluding them here would miss it a second time.
		auto Scannable =
			Info.State == MEM_COMMIT &&
			(Info.Protect & Writable) &&
			!(Info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) &&
			Info.RegionSize <= MaxRegionBytesToScan;

		if (Scannable)
		{
			auto BeforeMB = GBytesScanned / (512 * 1024 * 1024);
			auto Found = ScanSpan(Base, Next);

			if (GBytesScanned / (512 * 1024 * 1024) != BeforeMB)
			{
				UE_LOG("  ... %llu MB scanned, %d array-shaped so far",
					(unsigned long long)(GBytesScanned / (1024 * 1024)), GNearMisses);
			}
			if (Found)
			{
				UE_LOG("GObjects found outside the module image at 0x%llX", (unsigned long long)Found);
				return Found;
			}
		}

		if (Next <= Address)
			break;

		Address = Next;
	}

	UE_LOG("GObjects: no candidate passed validation (%d were array-shaped, %llu MB scanned)",
		GNearMisses, (unsigned long long)(GBytesScanned / (1024 * 1024)));
	return 0;
}

uintptr_t GetScanModuleBase()
{
	return Memcury::PE::GetModuleBase();
}

bool IsMemoryReadable(uintptr_t Address, size_t Size)
{
	return IsReadable(Address, Size);
}

bool RetargetScanModuleByName(const std::string& FileName)
{
	if (FileName.empty() || !GetModuleHandleA(FileName.c_str()))
		return false;

	// Memcury resolves the module by name on every call, so the string has to outlive this scope.
	static std::string Target;
	Target = FileName;

	Memcury::PE::SetCurrentModule(Target.c_str());
	return Memcury::PE::GetModuleBase() != 0;
}

std::string RetargetScanModule(uintptr_t Address)
{
	MEMORY_BASIC_INFORMATION Info{};
	if (!VirtualQuery(reinterpret_cast<void*>(Address), &Info, sizeof(Info)) || !Info.AllocationBase)
		return {};

	// A mapped image's allocation base is its module handle.
	auto Module = reinterpret_cast<HMODULE>(Info.AllocationBase);

	char Path[MAX_PATH]{};
	if (!GetModuleFileNameA(Module, Path, MAX_PATH))
		return {};

	std::string FileName = Path;
	auto Slash = FileName.find_last_of("\\/");
	if (Slash != std::string::npos)
		FileName = FileName.substr(Slash + 1);

	// Memcury looks the module up by name on every call, so the string has to outlive this scope.
	static std::string Target;
	Target = FileName;

	Memcury::PE::SetCurrentModule(Target.c_str());

	// Only report success when the name actually resolves back to the module the address is in.
	return Memcury::PE::GetModuleBase() == reinterpret_cast<uintptr_t>(Info.AllocationBase) ? FileName : std::string{};
}

uintptr_t PatternScanObject::TryFind()
{
	auto Addy = Memcury::Scanner::FindPattern(Sig.c_str());
	
	if (!Addy.IsValid())
		return 0;

	if (bRelative)
	{
		Addy.RelativeOffset(RelativeAddressOffset);
	}

	Addy.AbsoluteOffset(ResultOffset);

	return Addy.Get();
}

uintptr_t ManualAddressScanObject::TryFind()
{
	if (!Rva)
		return 0;

	// The host supplies a module-relative address, so it survives ASLR across runs.
	return Memcury::PE::GetModuleBase() + Rva;
}

template <typename T>
uintptr_t StringRefScanObject<T>::TryFind()
{
	Memcury::Scanner Addy = Memcury::Scanner::FindStringRef(StringRef);

	if (!Addy.IsValid())
		return 0;
	
	if (OpcodeToFind)
	{
		auto BeforeOpcodeScan = Addy.Get();

		Addy.ScanFor({ OpcodeToFind }, !bScanBackwards, OpcodeFindsToSkip);

		if (Addy.Get() == BeforeOpcodeScan)
			return 0;
	}

	if (bRelative)
		Addy.RelativeOffset(Offset);
	else Addy.AbsoluteOffset(Offset);

	return Addy.Get();
}

template struct StringRefScanObject<std::string>;
template struct StringRefScanObject<std::wstring>;
template struct StringRefScanObject<const char*>;
template struct StringRefScanObject<const wchar_t*>;