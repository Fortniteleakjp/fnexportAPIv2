#include "pch.h"

#include "scanning.h"
#include "../Dependencies/Memcury/memcury.h"

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