#pragma once

struct IScanObject
{
	virtual uintptr_t TryFind() = 0;
};

// Base of the module being scanned. Wrapped here because Memcury defines non-inline free
// functions, so its header can only be included from scanning.cpp.
uintptr_t GetScanModuleBase();

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