#include "pch.h"

#include "unrealVersion.h"

#pragma comment(lib, "Version.lib")

#define SCAN_LIMIT 0x300

#define SCAN_FOR_MEMBER_OFFSET(obj, member, outOffset) \
	for (uint8_t* i = (uint8_t*)obj; ; i++)\
	{\
		auto Count = i - (uint8_t*)obj;\
		if (Count >= SCAN_LIMIT)\
			return false;\
		\
		if (*(uintptr_t*)i == (uintptr_t)member)\
		{\
			outOffset = Count;\
			break;\
		}\
	}\


//this is super unsafe but hopefully stackoverflow comes in clutch https://stackoverflow.com/a/42389638
// fnexportAPI local patch: a resolved FNameToString has to be proven, not assumed.
//
// The scan matched an unrelated function on this build and returned it as the answer. Nothing
// downstream noticed: names simply came back empty, and the first visible symptom was the dynamic
// offset derivation failing several steps later. Asking the installed function to name real objects
// costs nothing and turns a silent wrong answer into a rejected candidate.
bool IUnrealVersion::FNameToStringResolvesNames()
{
	if (!FNameToString)
		return false;

	// Enough objects to be sure, spread over the start of the array where core types live.
	constexpr int RequiredNames = 8;
	constexpr int ObjectsToTry = 512;

	int Resolved = 0;

	for (int Index = 0; Index < ObjObjects::Num() && Index < ObjectsToTry && Resolved < RequiredNames; Index++)
	{
		try
		{
			auto Object = ObjObjects::GetObjectByIndex(Index);
			if (!Object)
				continue;

			auto Name = Object->GetName();
			if (!Name.empty() && Name != L"None")
				Resolved++;
		}
		catch (...)
		{
			// A wrong function can fault outright rather than just return nothing.
			return false;
		}
	}

	return Resolved >= RequiredNames;
}

bool IUnrealVersion::ResolveFNameToString(
	const std::vector<std::shared_ptr<IScanObject>>& Candidates, uintptr_t PinnedRva)
{
	auto ModuleBase = GetScanModuleBase();

	auto Install = [&](uintptr_t Address)
		{
			FNameToString = (_FNameToString)Address;
			return Address && FNameToStringResolvesNames();
		};

	if (PinnedRva)
	{
		auto Pinned = ManualAddressScanObject(PinnedRva).TryFind();
		if (Install(Pinned))
		{
			UE_LOG("Using the pinned address for FNameToString (+0x%llX)", (unsigned long long)PinnedRva);
			return true;
		}

		UE_LOG("The pinned FNameToString (+0x%llX) did not resolve names; falling back to scanning",
			(unsigned long long)PinnedRva);
	}

	if (!HostConfig::ProbeSignatures)
	{
		UE_LOG("No address was supplied for FNameToString, and signature hits are not called unless "
			"'probesignatures=true' is set: the only way to identify that function is to run it, and "
			"running the wrong one can take the editor down.");
		FNameToString = nullptr;
		return false;
	}

	for (size_t Index = 0; Index < Candidates.size(); Index++)
	{
		auto Found = Candidates[Index]->TryFind();
		if (!Found)
			continue;

		if (Install(Found))
		{
			UE_LOG("Found FNameToString with candidate %zu (+0x%llX)",
				Index + 1, (unsigned long long)(Found - ModuleBase));
			return true;
		}

		UE_LOG("  candidate %zu (+0x%llX) is not FNameToString; it resolved no names",
			Index + 1, (unsigned long long)(Found - ModuleBase));
	}

	FNameToString = nullptr;
	return false;
}

// fnexportAPI local patch: measure the two field offsets the property records are built from.
//
// Property names and their order come out right on this build, so FField's Next and NamePrivate are
// where the model says. The two values that were wrong are the ones whose position depends on how
// wide FName is and on a member UE6 added: the array dimension, which sits at the end of FField, and
// the field class id, which UE6 moved behind an EClassFlags. Both are read rather than called.
static void SampleProperties(void* Context, bool (*Visit)(void*, FProperty*), UClass* ClassClass, UClass* ScriptStructClass, int Limit)
{
	int Sampled = 0;

	for (int Index = 0; Index < ObjObjects::Num() && Sampled < Limit; Index++)
	{
		auto Object = ObjObjects::GetObjectByIndex(Index);
		if (!Object)
			continue;

		auto ObjectClass = Object->Class();
		if (ObjectClass != ClassClass && ObjectClass != ScriptStructClass)
			continue;

		auto Property = static_cast<UStruct*>(Object)->ChildProperties();

		for (int Walked = 0; Property && Walked < 4096 && Sampled < Limit; Walked++)
		{
			if (!Visit(Context, Property))
				return;

			Sampled++;
			Property = static_cast<FProperty*>(Property->GetNext());
		}
	}
}

static bool ReadInt32At(uintptr_t Address, int32_t& Out)
{
	__try
	{
		Out = *(int32_t*)Address;
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return false;
	}
}

static bool ReadUInt64At(uintptr_t Address, uint64_t& Out)
{
	__try
	{
		Out = *(uint64_t*)Address;
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return false;
	}
}

namespace
{
	struct FOffsetVotes
	{
		static constexpr int First = 0x20;
		static constexpr int Last = 0x48;
		static constexpr int Step = 4;
		static constexpr int Slots = (Last - First) / Step + 1;

		int ArrayDim[Slots] = {};

		// Class ids are 8-aligned inside the field class.
		static constexpr int IdFirst = 0x08;
		static constexpr int IdLast = 0x20;
		static constexpr int IdSlots = (IdLast - IdFirst) / 8 + 1;

		int Id[IdSlots] = {};

		int Count = 0;
	};

	bool VisitProperty(void* Context, FProperty* Property)
	{
		auto& Votes = *(FOffsetVotes*)Context;
		Votes.Count++;

		// An array dimension is 1 for all but a handful of properties, so the offset that reads 1
		// almost everywhere is the one.
		for (int Slot = 0; Slot < FOffsetVotes::Slots; Slot++)
		{
			int32_t Value = 0;
			if (ReadInt32At((uintptr_t)Property + FOffsetVotes::First + Slot * FOffsetVotes::Step, Value) && Value == 1)
			{
				Votes.ArrayDim[Slot]++;
			}
		}

		auto FieldClass = (uintptr_t)Property->GetClass();
		if (FieldClass)
		{
			for (int Slot = 0; Slot < FOffsetVotes::IdSlots; Slot++)
			{
				uint64_t Value = 0;
				if (ReadUInt64At(FieldClass + FOffsetVotes::IdFirst + Slot * 8, Value) &&
					FFieldClass::IsPlausibleId(Value))
				{
					Votes.Id[Slot]++;
				}
			}
		}

		return true;
	}
}

bool IUnrealVersion::TryDeriveFieldOffsets()
{
	auto ScriptStructClass = UScriptStruct::StaticClass();
	auto ClassClass = UClass::StaticClass();

	if (!ScriptStructClass || !ClassClass)
		return false;

	FOffsetVotes Votes;
	SampleProperties(&Votes, VisitProperty, ClassClass, ScriptStructClass, 20000);

	if (Votes.Count < 100)
		return false;

	int BestDim = 0, BestId = 0;
	for (int Slot = 1; Slot < FOffsetVotes::Slots; Slot++)
		if (Votes.ArrayDim[Slot] > Votes.ArrayDim[BestDim]) BestDim = Slot;
	for (int Slot = 1; Slot < FOffsetVotes::IdSlots; Slot++)
		if (Votes.Id[Slot] > Votes.Id[BestId]) BestId = Slot;

	// Most properties have to agree; a marginal winner means neither offset is really there.
	if (Votes.ArrayDim[BestDim] * 2 < Votes.Count || Votes.Id[BestId] * 2 < Votes.Count)
		return false;

	FProperty::ArrayDimOffset = FOffsetVotes::First + BestDim * FOffsetVotes::Step;
	FFieldClass::IdOffset = FOffsetVotes::IdFirst + BestId * 8;

	UE_LOG("Field offsets: ArrayDim +0x%X (%d/%d), field class id +0x%X (%d/%d)",
		FProperty::ArrayDimOffset, Votes.ArrayDim[BestDim], Votes.Count,
		FFieldClass::IdOffset, Votes.Id[BestId], Votes.Count);

	return true;
}

// fnexportAPI local patch: measure FProperty rather than assume it.
//
// Everything a property refers to — the struct behind a StructProperty, the enum behind an
// EnumProperty, the element type of an array — is stored immediately past the end of FProperty, so
// its size decides whether those come out right. UE6 makes that size unknowable from the headers
// alone: several members are conditional on how the build was configured (editor data, metadata,
// const-initialised objects), and a wrong guess does not fail loudly. It silently records the wrong
// type for every struct property, which is what made a mapping that loads cleanly unusable for
// reading assets.
//
// So it is measured. A StructProperty holds a pointer to a UScriptStruct at exactly that offset,
// and nothing else in the property does; the offset where that holds across thousands of properties
// is the size. Only memory is read.
static bool ProbeStructTarget(uintptr_t At, int32_t ClassOffset, const void* ScriptStructClass)
{
	__try
	{
		auto Target = *(uintptr_t*)At;

		// A heap object pointer, not a flag word or a small integer.
		if (Target < 0x10000 || (Target & 7))
			return false;

		if (!IsMemoryReadable(Target, (size_t)ClassOffset + sizeof(void*)))
			return false;

		return *(const void**)(Target + ClassOffset) == ScriptStructClass;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return false;
	}
}

bool IUnrealVersion::TryDerivePropertySize()
{
	auto ScriptStructClass = UScriptStruct::StaticClass();
	auto ClassClass = UClass::StaticClass();

	if (!ScriptStructClass || !ClassClass)
		return false;

	// FProperty starts where FField ends and cannot be far past it.
	constexpr int FirstOffset = 0x30;
	constexpr int LastOffset = 0xC0;
	constexpr int Step = 8;
	constexpr int Slots = (LastOffset - FirstOffset) / Step + 1;

	// Enough properties that the right offset wins clearly, without walking the whole array.
	constexpr int PropertiesToSample = 20000;
	constexpr int MaxPropertiesPerStruct = 4096;

	int Votes[Slots] = {};
	int Sampled = 0;

	for (int Index = 0; Index < ObjObjects::Num() && Sampled < PropertiesToSample; Index++)
	{
		auto Object = ObjObjects::GetObjectByIndex(Index);
		if (!Object)
			continue;

		auto ObjectClass = Object->Class();
		if (ObjectClass != ClassClass && ObjectClass != ScriptStructClass)
			continue;

		auto Property = static_cast<UStruct*>(Object)->ChildProperties();

		for (int Walked = 0; Property && Walked < MaxPropertiesPerStruct && Sampled < PropertiesToSample; Walked++)
		{
			for (int Slot = 0; Slot < Slots; Slot++)
			{
				if (ProbeStructTarget((uintptr_t)Property + FirstOffset + Slot * Step,
					UObject::ClassOffset, ScriptStructClass))
				{
					Votes[Slot]++;
				}
			}

			Sampled++;
			Property = static_cast<FProperty*>(Property->GetNext());
		}
	}

	int Best = -1;
	for (int Slot = 0; Slot < Slots; Slot++)
	{
		if (Best < 0 || Votes[Slot] > Votes[Best])
			Best = Slot;
	}

	// Struct properties are common but not universal, so the winner only has to stand out.
	if (Best < 0 || Votes[Best] < 32)
		return false;

	FProperty::FPropertySize = FirstOffset + Best * Step;

	UE_LOG("FProperty is 0x%X (%d of %d properties pointed at a script struct there)",
		FProperty::FPropertySize, Votes[Best], Sampled);

	return true;
}

// fnexportAPI local patch: find UEnum's member data by its shape, reading only.
//
// Its position depends on build configuration (whether the enum carries compiled-in metadata, and
// how wide the UField base is), so it is looked for rather than assumed.
//
// Nothing here calls the engine. An earlier version confirmed a candidate by asking FNameToString
// to render its first few names, which meant handing the engine hundreds of garbage FNames while
// sweeping offsets. Catching the resulting faults was not enough: the allocator and locks the
// engine touched on the way down were left inconsistent, and the editor came apart shortly after.
// A structure this specific can be recognised without running anything.
static bool ProbeEnumNames(uintptr_t Candidate)
{
	__try
	{
		auto Data = (const FEnumNameData*)Candidate;
		auto Count = Data->Num();

		// A real enum has members, and no enum has thousands of them.
		if (Count <= 0 || Count > 4096)
			return false;

		auto Names = (uintptr_t)Data->GetNames();
		auto Values = (uintptr_t)Data->GetValues();

		if (!Names || (Names & 7) || !Values || (Values & 7))
			return false;

		// Both arrays have to be addressable for exactly as many entries as the count claims.
		if (!IsMemoryReadable(Names, sizeof(FName) * (size_t)Count))
			return false;

		if (!IsMemoryReadable(Values, sizeof(int64_t) * (size_t)Count))
			return false;

		auto Checked = Count < 4 ? Count : 4;

		// Name entries are comparison indices: never zero for a declared member, and distinct from
		// each other. Two arrays of the right length full of repeats are not an enum.
		for (int Index = 0; Index < Checked; Index++)
		{
			auto Id = ((const FName*)Names)[Index].GetNumber();
			if (Id == 0)
				return false;

			for (int Other = 0; Other < Index; Other++)
			{
				if (((const FName*)Names)[Other].GetNumber() == Id)
					return false;
			}
		}

		// Enum values are small numbers written by hand, not pointers or lengths.
		constexpr int64_t PlausibleValue = 1LL << 32;
		for (int Index = 0; Index < Checked; Index++)
		{
			auto Value = ((const int64_t*)Values)[Index];
			if (Value < -PlausibleValue || Value > PlausibleValue)
				return false;
		}

		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return false;
	}
}

bool IUnrealVersion::TryDeriveEnumNamesOffset()
{
	auto EnumClass = UEnum::StaticClass();
	if (!EnumClass)
		return false;

	// Offsets are voted on across many enums: one enum could match at a wrong offset by chance,
	// but not the same wrong offset for all of them.
	constexpr int FirstOffset = 0x30;
	constexpr int LastOffset = 0xC0;
	constexpr int Step = 8;
	constexpr int Slots = (LastOffset - FirstOffset) / Step + 1;

	int Votes[Slots] = {};
	int Sampled = 0;

	for (int Index = 0; Index < ObjObjects::Num() && Sampled < 64; Index++)
	{
		auto Object = ObjObjects::GetObjectByIndex(Index);
		if (!Object || Object->Class() != EnumClass)
			continue;

		Sampled++;

		for (int Slot = 0; Slot < Slots; Slot++)
		{
			if (ProbeEnumNames((uintptr_t)Object + FirstOffset + Slot * Step))
				Votes[Slot]++;
		}
	}

	int Best = -1;
	for (int Slot = 0; Slot < Slots; Slot++)
	{
		if (Best < 0 || Votes[Slot] > Votes[Best])
			Best = Slot;
	}

	// Requiring most of the sample to agree keeps a stray match from being adopted.
	if (Best < 0 || Sampled == 0 || Votes[Best] * 2 < Sampled)
		return false;

	UEnum::NamesOffset = FirstOffset + Best * Step;

	UE_LOG("Enum member data at +0x%X (%d of %d enums agreed)",
		UEnum::NamesOffset, Votes[Best], Sampled);

	return true;
}

bool IUnrealVersion::TryDynamicOffsets()
{
	try
	{
		auto UClassPtr = ObjObjects::FindObjectByName<UClass>(L"Class");
		auto UObjectPtr = ObjObjects::FindObjectByName<UClass>(L"Object");
		auto ActorPtr = ObjObjects::FindObjectByName<UClass>(L"Actor");
		auto EnginePtr = ObjObjects::FindObjectByName(L"/Script/Engine");

		if (!UClassPtr or !UObjectPtr or !ActorPtr or !EnginePtr)
			return false;

		SCAN_FOR_MEMBER_OFFSET(UObjectPtr, UClassPtr, UObject::ClassOffset);

		if (!UObject::ClassOffset)
			return false;

		SCAN_FOR_MEMBER_OFFSET(ActorPtr, UObjectPtr, UStruct::SuperOffset);

		if (!UStruct::SuperOffset)
			return false;

		UStruct::ChildPropertiesOffset = UStruct::SuperOffset + (sizeof(void*) * 2);

		SCAN_FOR_MEMBER_OFFSET(ActorPtr, EnginePtr, UObject::OuterOffset);

		if (!UObject::OuterOffset)
			return false;

		// fnexportAPI local patch: these four were found by name, so their paths are what a correct
		// path looks like on this build. The dump matches classes by path, and when that fails
		// silently this is the only way to see whether the name or the outer chain is at fault.
		auto Words = UClassPtr->NameWords();
		UE_LOG("Name words at +0x%X of \"Class\": %08X %08X %08X %08X",
			UObject::NameOffset, Words[0], Words[1], Words[2], Words[3]);

		UE_LOG("Sample paths: Class=\"%S\" Object=\"%S\" Actor=\"%S\" Engine=\"%S\"",
			UClassPtr->GetPath().c_str(), UObjectPtr->GetPath().c_str(),
			ActorPtr->GetPath().c_str(), EnginePtr->GetPath().c_str());
	}
	catch (...)
	{
		return false;
	}

	return true;
}