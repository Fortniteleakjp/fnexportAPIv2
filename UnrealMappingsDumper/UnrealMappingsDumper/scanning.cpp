#include "pch.h"

#include "scanning.h"
#include "../Dependencies/Memcury/memcury.h"

// ---------------------------------------------------------------------------
// fnexportAPI local patch: GObjects located by shape rather than by signature.
// ---------------------------------------------------------------------------
namespace
{
	// FChunkedFixedUObjectArray allocates its objects in fixed 64K chunks.
	constexpr int32_t ObjectsPerChunk = 64 * 1024;

	// A loaded editor holds far more than this; the bound only rejects noise that happens to look
	// self-consistent.
	constexpr int32_t MinPlausibleObjects = 1000;
	constexpr int32_t MaxPlausibleObjects = 100 * 1000 * 1000;

	// Mirrors ObjObjects' own fields. Kept local so the scan does not depend on that class being
	// constructible, and so the two definitions can be compared side by side.
	struct FChunkedFixedUObjectArray
	{
		uintptr_t* Objects;
		uintptr_t PreAllocatedObjects;
		int32_t MaxElements;
		int32_t NumElements;
		int32_t MaxChunks;
		int32_t NumChunks;
	};

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

	// The cheap half of the test: the counters have to describe a consistent chunked array. This
	// runs on every 8-byte offset, so it must not touch memory outside the candidate itself.
	bool CountersAreConsistent(const FChunkedFixedUObjectArray& Candidate)
	{
		if (Candidate.NumElements < MinPlausibleObjects || Candidate.NumElements > MaxPlausibleObjects)
			return false;

		if (Candidate.MaxElements < Candidate.NumElements || Candidate.NumChunks <= 0)
			return false;

		if (Candidate.MaxChunks < Candidate.NumChunks)
			return false;

		// The capacity is exactly the chunk capacity, which is what makes this shape recognisable.
		if (static_cast<int64_t>(Candidate.MaxChunks) * ObjectsPerChunk != Candidate.MaxElements)
			return false;

		// Chunks are allocated as elements need them, so enough of them must already exist.
		auto RequiredChunks = (Candidate.NumElements + ObjectsPerChunk - 1) / ObjectsPerChunk;
		if (Candidate.NumChunks < RequiredChunks)
			return false;

		// Both pointers are heap allocations, so they are at least pointer-aligned.
		return Candidate.Objects != nullptr && (reinterpret_cast<uintptr_t>(Candidate.Objects) & 7) == 0;
	}

	// The expensive half: follow the pointers and check that they really lead to objects.
	bool PointsAtRealObjects(const FChunkedFixedUObjectArray& Candidate)
	{
		auto ChunkTable = reinterpret_cast<uintptr_t>(Candidate.Objects);
		if (!IsReadable(ChunkTable, sizeof(uintptr_t) * static_cast<size_t>(Candidate.NumChunks)))
			return false;

		// Every chunk the element count claims to reach has to be allocated and readable.
		for (int32_t Chunk = 0; Chunk < Candidate.NumChunks; Chunk++)
		{
			auto ChunkAddress = Candidate.Objects[Chunk];
			if (!IsReadable(ChunkAddress, sizeof(uintptr_t)))
				return false;
		}

		if (Candidate.PreAllocatedObjects && !IsReadable(Candidate.PreAllocatedObjects, sizeof(uintptr_t)))
			return false;

		// An FUObjectItem is a UObject pointer plus three int32 fields. Slots can be empty, so the
		// first chunk is walked until a live object turns up; one with a readable vtable settles it.
		auto FirstChunk = Candidate.Objects[0];
		constexpr int32_t ItemSize = sizeof(uintptr_t) + sizeof(int32_t) * 3;
		auto SlotsToTry = Candidate.NumElements < 256 ? Candidate.NumElements : 256;

		if (!IsReadable(FirstChunk, static_cast<size_t>(ItemSize) * SlotsToTry))
			return false;

		for (int32_t Slot = 0; Slot < SlotsToTry; Slot++)
		{
			auto Object = *reinterpret_cast<uintptr_t*>(FirstChunk + static_cast<size_t>(ItemSize) * Slot);
			if (!Object)
				continue;

			if (!IsReadable(Object, sizeof(uintptr_t)))
				return false;

			// The vtable pointer of a real UObject points into loaded code.
			auto VTable = *reinterpret_cast<uintptr_t*>(Object);
			return IsReadable(VTable, sizeof(uintptr_t));
		}

		return false;
	}
}

uintptr_t GObjectsHeuristicScanObject::TryFind()
{
	auto ModuleBase = Memcury::PE::GetModuleBase();
	if (!ModuleBase)
		return 0;

	auto Headers = Memcury::PE::GetNTHeaders();
	auto SectionCount = Headers->FileHeader.NumberOfSections;
	auto Section = IMAGE_FIRST_SECTION(Headers);

	for (WORD Index = 0; Index < SectionCount; Index++, Section++)
	{
		// GObjects is a global, so only the initialised and zero-filled data lives are worth walking.
		if (!(Section->Characteristics & IMAGE_SCN_MEM_READ) || (Section->Characteristics & IMAGE_SCN_MEM_EXECUTE))
			continue;

		auto Start = ModuleBase + Section->VirtualAddress;
		auto Size = Section->Misc.VirtualSize;
		if (Size < sizeof(FChunkedFixedUObjectArray))
			continue;

		auto End = Start + Size - sizeof(FChunkedFixedUObjectArray);

		// Committed-region bounds are cached: a VirtualQuery for every 8-byte step would make this
		// scan far slower than the signature it replaces.
		uintptr_t RegionEnd = 0;

		for (auto Cursor = Start; Cursor <= End; Cursor += sizeof(uintptr_t))
		{
			if (Cursor + sizeof(FChunkedFixedUObjectArray) > RegionEnd)
			{
				MEMORY_BASIC_INFORMATION Info{};
				if (!VirtualQuery(reinterpret_cast<void*>(Cursor), &Info, sizeof(Info)))
					break;

				auto Base = reinterpret_cast<uintptr_t>(Info.BaseAddress);

				if (Info.State != MEM_COMMIT || (Info.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
				{
					// Skip the whole uncommitted region rather than stepping through it.
					Cursor = Base + Info.RegionSize;
					RegionEnd = 0;
					if (Cursor < Start) break;
					Cursor -= sizeof(uintptr_t);
					continue;
				}

				RegionEnd = Base + Info.RegionSize;
			}

			auto& Candidate = *reinterpret_cast<FChunkedFixedUObjectArray*>(Cursor);

			if (CountersAreConsistent(Candidate) && PointsAtRealObjects(Candidate))
			{
				return Cursor;
			}
		}
	}

	return 0;
}

uintptr_t GetScanModuleBase()
{
	return Memcury::PE::GetModuleBase();
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