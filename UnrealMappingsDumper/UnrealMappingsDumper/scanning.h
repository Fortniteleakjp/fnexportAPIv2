#pragma once

struct IScanObject
{
	virtual uintptr_t TryFind() = 0;
};

// Base of the module being scanned. Wrapped here because Memcury defines non-inline free
// functions, so its header can only be included from scanning.cpp.
uintptr_t GetScanModuleBase();

// True when the whole span is committed and readable. Used to validate a pointer before
// following it, so a derivation can reject a candidate instead of faulting on it.
bool IsMemoryReadable(uintptr_t Address, size_t Size);

// Points every later scan at the module that owns Address.
//
// UEFN is a modular build: the executable is a small stub and the engine lives in DLLs, so the
// process's main module contains none of the globals or functions being looked for. Once GObjects
// has been located, the module holding it is the one worth searching, and retargeting is what makes
// the FNameToString scan look somewhere it can actually succeed.
//
// Returns the module's file name, or an empty string when the address belongs to no module.
std::string RetargetScanModule(uintptr_t Address);

// Points every later scan at a module named directly, for when its addresses are already known.
// Returns false when no such module is loaded.
bool RetargetScanModuleByName(const std::string& FileName);

// Works out how the object array at this address is laid out and records it, so that reading it
// does not depend on having searched for it. A pinned address skips the search entirely, and the
// layout is not something that can be assumed: UE6 reordered both the array and its entries.
bool DetectObjectArrayLayout(uintptr_t Address);

// Finds the engine's name pool in the module currently being scanned, so names can be read out of
// it instead of being asked for through FName::ToString. Returns its address, or 0.
uintptr_t FindNamePool();

// Resolves a pinned GObjects address when the module holding it was not named, by trying it against
// each loaded module and keeping the one where it lands on a real object array. Retargets to that
// module on success. Returns the address, or 0.
uintptr_t ResolvePinnedGObjects(uintptr_t Rva);

struct PatternScanObject : public IScanObject
{
	PatternScanObject(
		std::string sig,
		int relativeAddressOffset = 0,
		bool relative = true,
		int resultOffset = 0)
		: 
		Sig(sig), 
		ResultOffset(resultOffset), 
		bRelative(relative), 
		RelativeAddressOffset(relativeAddressOffset)
	{
	}

	std::string Sig;
	bool bRelative;
	int RelativeAddressOffset;
	int ResultOffset;

	uintptr_t TryFind() override;
};

// fnexportAPI local patch: resolves an address the host pinned in the config instead of scanning
// for it. Signature scans break on every engine bump; this is the escape hatch the upstream error
// message ("Try overriding it") always assumed but never provided.
struct ManualAddressScanObject : public IScanObject
{
	explicit ManualAddressScanObject(uintptr_t rva) : Rva(rva) {}

	uintptr_t Rva;

	uintptr_t TryFind() override;
};

// fnexportAPI local patch: finds GObjects by its shape instead of by a byte signature.
//
// Every engine bump moves the instructions the upstream patterns match, which is exactly how UE6
// broke them. The layout of FChunkedFixedUObjectArray has not changed, so the module's data
// sections are searched for a structure that is internally consistent as one — the chunk count has
// to agree with the element count, every live chunk pointer has to be readable, and the first
// object it yields has to have a readable vtable. That holds across engine versions.
struct GObjectsHeuristicScanObject : public IScanObject
{
	uintptr_t TryFind() override;
};

template <typename StrType = std::wstring>
struct StringRefScanObject : public IScanObject
{
	StringRefScanObject(
		StrType stringRef,
		bool scanBackwards = false,
		int offset = 0,
		bool relative = true,
		uint8_t opcodeToFind = 0,
		int opcodeFindsToSkip = 0)
		:
		StringRef(stringRef),
		OpcodeToFind(opcodeToFind),
		OpcodeFindsToSkip(opcodeFindsToSkip),
		bRelative(relative),
		Offset(offset),
		bScanBackwards(scanBackwards)
	{
	}

	StrType StringRef;
	uint8_t OpcodeToFind;
	int OpcodeFindsToSkip;
	bool bRelative;
	int Offset;
	bool bScanBackwards;

	uintptr_t TryFind() override;
};