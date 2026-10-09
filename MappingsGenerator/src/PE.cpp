#include "PE.h"

#include <cstring>

PE::PE(const std::wstring& InPath)
    : Path(InPath)
{
    /* 依存 DLL (tbb, oo2core, EOSSDK ...) をモジュールと同じフォルダから解決させる */
    const size_t Slash = Path.find_last_of(L"\\/");
    if (Slash != std::wstring::npos)
        SetDllDirectoryW(Path.substr(0, Slash).c_str());

    Module = LoadLibraryW(Path.c_str());
    if (!Module)
        return;

    Base = reinterpret_cast<uintptr_t>(Module);

    const auto* Dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(Base);
    const auto* Nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(Base + Dos->e_lfanew);
    Size = Nt->OptionalHeader.SizeOfImage;

    const IMAGE_SECTION_HEADER* Header = IMAGE_FIRST_SECTION(Nt);
    for (int i = 0; i < Nt->FileHeader.NumberOfSections; i++, Header++)
    {
        Section Sec;
        Sec.Name.assign(reinterpret_cast<const char*>(Header->Name), strnlen(reinterpret_cast<const char*>(Header->Name), IMAGE_SIZEOF_SHORT_NAME));
        Sec.Start = Base + Header->VirtualAddress;
        Sec.End = Sec.Start + Header->Misc.VirtualSize;
        Sec.Characteristics = Header->Characteristics;
        Sections.push_back(std::move(Sec));
    }
}

std::wstring PE::GetFileName() const
{
    const size_t Slash = Path.find_last_of(L"\\/");
    return Slash == std::wstring::npos ? Path : Path.substr(Slash + 1);
}

bool PE::IsInCode(uintptr_t Address) const
{
    for (const Section& Sec : Sections)
    {
        if (Sec.IsCode() && Sec.Contains(Address))
            return true;
    }
    return false;
}

bool PE::IsInData(uintptr_t Address) const
{
    for (const Section& Sec : Sections)
    {
        if (!Sec.IsCode() && (Sec.Characteristics & IMAGE_SCN_MEM_READ) && Sec.Contains(Address))
            return true;
    }
    return false;
}

uintptr_t PE::FindExport(std::string_view MangledNamePart) const
{
    const auto* Dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(Base);
    const auto* Nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(Base + Dos->e_lfanew);
    const IMAGE_DATA_DIRECTORY& Dir = Nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!Dir.VirtualAddress)
        return 0;

    const auto* Exports = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(Base + Dir.VirtualAddress);
    const auto* Names = reinterpret_cast<const uint32*>(Base + Exports->AddressOfNames);
    const auto* Ordinals = reinterpret_cast<const uint16*>(Base + Exports->AddressOfNameOrdinals);
    const auto* Functions = reinterpret_cast<const uint32*>(Base + Exports->AddressOfFunctions);

    for (uint32 i = 0; i < Exports->NumberOfNames; i++)
    {
        const std::string_view Name(reinterpret_cast<const char*>(Base + Names[i]));
        if (Name.find(MangledNamePart) != std::string_view::npos)
            return Base + Functions[Ordinals[i]];
    }
    return 0;
}

std::vector<PE::CallSite> PE::FindCallsTo(const std::unordered_set<uintptr_t>& Targets) const
{
    std::vector<CallSite> Result;

    for (const Section& Sec : Sections)
    {
        if (!Sec.IsCode() || Sec.End - Sec.Start < 6)
            continue;

        const uint8* Bytes = reinterpret_cast<const uint8*>(Sec.Start);
        const size_t Len = Sec.End - Sec.Start - 6;

        for (size_t i = 0; i < Len; i++)
        {
            const uint8 Op = Bytes[i];
            const uintptr_t Ip = Sec.Start + i;

            if (Op == 0xE8 || Op == 0xE9)
            {
                int32 Rel;
                memcpy(&Rel, Bytes + i + 1, sizeof(Rel));
                const uintptr_t Target = Ip + 5 + Rel;
                if (Targets.contains(Target))
                    Result.push_back({ Ip, Target });
            }
            else if (Op == 0xFF && (Bytes[i + 1] == 0x15 || Bytes[i + 1] == 0x25))
            {
                int32 Disp;
                memcpy(&Disp, Bytes + i + 2, sizeof(Disp));
                const uintptr_t Slot = Ip + 6 + Disp;
                if (!Contains(Slot) || !Contains(Slot + 7))
                    continue;

                const uintptr_t Target = *reinterpret_cast<const uintptr_t*>(Slot);
                if (Targets.contains(Target))
                    Result.push_back({ Ip, Target });
            }
        }
    }

    return Result;
}

void PE::ForEachLeaBefore(uintptr_t CallInstruction, uint8 ModRmReg, uint32 MaxDistance, const std::function<bool(uintptr_t)>& Callback) const
{
    /* lea r64, [rip+disp32] = 48/4C 8D (00 reg 101) disp32 */
    const uint8 Rex = ModRmReg >= 8 ? 0x4C : 0x48;
    const uint8 ModRm = static_cast<uint8>(((ModRmReg & 7) << 3) | 0x05);

    for (uint32 Back = 7; Back <= MaxDistance; Back++)
    {
        const uintptr_t Ip = CallInstruction - Back;
        if (!IsInCode(Ip))
            break;

        const uint8* Bytes = reinterpret_cast<const uint8*>(Ip);
        if (Bytes[0] != Rex || Bytes[1] != 0x8D || Bytes[2] != ModRm)
            continue;

        int32 Disp;
        memcpy(&Disp, Bytes + 3, sizeof(Disp));
        if (!Callback(Ip + 7 + Disp))
            return;
    }
}
