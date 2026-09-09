#pragma once

#include "app.h"
#include "hostConfig.h"
#include "unrealTypes.h"
#include "scanning.h"

/*
* The idea here is to make something that can easily be overriden for engine or game versions with different types
* that need to be overriden or handled differently.
*/

struct IUnrealVersion
{
private:
	static bool TryDynamicOffsets();
	static bool TryDeriveEnumNamesOffset();

	/// <summary>True when the currently installed FNameToString resolves real object names.</summary>
	static bool FNameToStringResolvesNames();

	/// <summary>Installs the first candidate that passes that check, pinned address first.</summary>
	static bool ResolveFNameToString(
		const std::vector<std::shared_ptr<IScanObject>>& Candidates, uintptr_t PinnedRva);

	// Tries the pinned address first, then each candidate in order, and reports what was used.
	static uintptr_t Resolve(
		const char* Name,
		const std::vector<std::shared_ptr<IScanObject>>& Candidates,
		uintptr_t PinnedRva)
	{
		if (PinnedRva)
		{
			auto Pinned = ManualAddressScanObject(PinnedRva).TryFind();
			if (Pinned)
			{
				UE_LOG("Using the pinned address for %s (+0x%llX)", Name, (unsigned long long)PinnedRva);
				return Pinned;
			}
		}

		auto ModuleBase = GetScanModuleBase();

		for (size_t Index = 0; Index < Candidates.size(); Index++)
		{
			auto Found = Candidates[Index]->TryFind();
			if (!Found)
				continue;

			UE_LOG("Found %s with candidate %zu (+0x%llX)", Name, Index + 1, (unsigned long long)(Found - ModuleBase));
			return Found;
		}

		return 0;
	}

public:

	template <typename Version>
	static bool InitTypes()
	{
		// fnexportAPI local patch: an address pinned in the host config wins over the scans, and
		// whichever candidate matched is logged as a module-relative address so a working one can be
		// pinned for the next run. Upstream only said "try overriding it" without a way to do so.
		auto GObjectsAddy = Resolve("GObjects", Version::GetGObjectsPatterns(), HostConfig::GObjectsRva);

		if (!GObjectsAddy)
		{
			UE_LOG("Could not find the address for GObjects. Pin it with 'gobjects=<rva>' in the dumper config, or add the correct sig for it.");
			return false;
		}

		ObjObjects::SetInstance(GObjectsAddy);

		// The scans so far ran against the process's main module. In a modular build that is a stub
		// which holds none of this, so everything after GObjects is looked for in the module that
		// actually turned out to contain it.
		auto Module = RetargetScanModule(GObjectsAddy);
		if (!Module.empty())
		{
			UE_LOG("Scanning %s from here on (+0x%llX for GObjects)",
				Module.c_str(), (unsigned long long)(GObjectsAddy - GetScanModuleBase()));
		}

		// Unlike GObjects, a wrong FNameToString cannot be spotted from its own address: the scan
		// happily matches an unrelated function and every name then comes back empty, which shows
		// up much later as "could not grab dynamic offsets". Each candidate is installed and asked
		// to resolve real names before it is accepted.
		if (!ResolveFNameToString(Version::GetFNameStringPatterns(), HostConfig::FNameToStringRva))
		{
			UE_LOG("Could not find a working address for FNameToString. Pin it with 'fnametostring=<rva>' in the dumper config, or add the correct sig for it.");
			return false;
		}

		using UObjectImpl = Version::Offsets::UObject;
		using UStructImpl = Version::Offsets::UStruct;

		UObject::NameOffset = UObjectImpl::NameOffset;
		FName::IsOptimized = Version::HasOptimizedFName;
		FProperty::FPropertySize = Version::FPropertySize;

		if (!TryDynamicOffsets())
		{
			UE_LOG("Could not grab dynamic offsets. Just gonna use the hardcoded ones.");

			UObject::ClassOffset = UObjectImpl::ClassOffset;
			UObject::OuterOffset = UObjectImpl::OuterOffset;

			UStruct::SuperOffset = UStructImpl::SuperOffset;
			UStruct::ChildPropertiesOffset = UStructImpl::ChildPropertiesOffset;
		}

		// Enum members moved into their own structure on UE6, and where it sits depends on the
		// build, so it is located rather than assumed. Structs still dump without it.
		if (!TryDeriveEnumNamesOffset())
		{
			UE_LOG("Could not locate the enum member data; enums will be skipped.");
		}

		// Every lookup after this point is built on these; a wrong one produces an empty dump
		// rather than an error, so they are reported.
		UE_LOG("Offsets: Name +0x%X, Class +0x%X, Outer +0x%X, Super +0x%X, ChildProperties +0x%X, FProperty size 0x%X, optimized FName %s",
			UObject::NameOffset, UObject::ClassOffset, UObject::OuterOffset,
			UStruct::SuperOffset, UStruct::ChildPropertiesOffset,
			FProperty::FPropertySize, FName::IsOptimized ? "yes" : "no");

		return true;
	}
};

/*
* Yes, it's true, UObject should pretty much always be the same across all games,
* however there are some games that use a custom UObject, so should they ever need mappings,
* this design pattern makes it easier to override the offsets.
*/

struct UnrealVersionBase : IUnrealVersion
{
	static constexpr int FPropertySize = 0x78;
	static constexpr bool HasOptimizedFName = false;

	struct Offsets
	{
		struct UObject
		{
			static constexpr int NameOffset = 0x18;
			static constexpr int ClassOffset = 0x10;
			static constexpr int OuterOffset = 0x20;
		};

		struct UStruct
		{
			static constexpr int SuperOffset = 0x40;
			static constexpr int ChildPropertiesOffset = 0x50;
		};
	};

	static std::vector<std::shared_ptr<IScanObject>> GetFNameStringPatterns()
	{
		return
		{
			std::make_shared<PatternScanObject>("E8 ? ? ? ? 83 7D C8 00 48 8D 15 ? ? ? ? 0F 5A DE", 1, true),
			std::make_shared<PatternScanObject>("E8 ? ? ? ? 48 8B 4C 24 ? 8B FD 48 85 C9", 1, true),// 4.12 - 5.0 EA
			std::make_shared<PatternScanObject>("E8 ? ? ? ? BD 01 00 00 00 41 39 6E ? 0F 8E", 1, true),// 4.25+ Backup
			std::make_shared<StringRefScanObject<std::wstring>>(
				L"%s %s SetTimer passed a negative or zero time. The associated timer may fail to be created/fire! If using InitialStartDelayVariance, be sure it is smaller than (Time + InitialStartDelay).",
				true, 1, true, 0xE8)
		};
	}


	static std::vector<std::shared_ptr<IScanObject>> GetGObjectsPatterns()
	{
		return
		{
			std::make_shared<PatternScanObject>("48 89 05 ? ? ? ? E8 ? ? ? ? ? ? ? 0F 84", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C 07 48 85 C9 74 20", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C", 3, true),
			std::make_shared<PatternScanObject>("48 03 ? ? ? ? ? ? ? ? ? ? 48 8B 10 48 85 D2 74 07", 3, true),

			// Last: exact signatures are cheaper, but they are also what breaks on a new engine.
			std::make_shared<GObjectsHeuristicScanObject>()
		};
	}
};

/*
* Use this if the games engine has UE_FNAME_OUTLINE_NUMBER defined as 1
*/
struct Version_OptimizedFName : UnrealVersionBase
{
	// fnexportAPI local patch: FField lost a word when FFieldVariant stopped carrying a separate
	// bool, and FProperty is measured from the end of FField, so it moves with it.
	static constexpr int FPropertySize = 0x68;
	static constexpr bool HasOptimizedFName = true;
};

struct Version_FortniteLatest : Version_OptimizedFName
{
	static std::vector<std::shared_ptr<IScanObject>> GetGObjectsPatterns()
	{
		// GObjects is read as `mov <reg>, [rip+disp]` followed by an indexed load out of the chunk
		// table. Only the register allocation changes between builds, so the same access shape is
		// listed for the encodings the compiler actually picks. Which one hit is logged as an RVA,
		// and a build none of them match can be pinned with 'gobjects=' in the dumper config.
		return
		{
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C C8", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C D0", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 14 C8", 3, true),
			std::make_shared<PatternScanObject>("48 8B 0D ? ? ? ? 48 8B 04 C1", 3, true),
			std::make_shared<PatternScanObject>("48 8B 15 ? ? ? ? 48 8B 0C C2", 3, true),
			std::make_shared<PatternScanObject>("4C 8B 05 ? ? ? ? 4D 8B 0C C8", 3, true),
			std::make_shared<PatternScanObject>("4C 8B 05 ? ? ? ? 4D 8B 04 C8", 3, true),

			// UE6 matches none of the above. The shape scan does not depend on codegen at all.
			std::make_shared<GObjectsHeuristicScanObject>()
		};
	}
};