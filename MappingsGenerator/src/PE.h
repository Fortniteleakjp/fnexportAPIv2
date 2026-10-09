#pragma once

#include "Utils.h"

#include <functional>
#include <string>
#include <unordered_set>
#include <vector>

/* LoadLibrary でロードした PE モジュール (静的初期化子は実行済み = モジュール間参照も解決済み) */
class PE
{
public:
    struct Section
    {
        std::string Name;
        uintptr_t Start = 0;
        uintptr_t End = 0;
        uint32 Characteristics = 0;

        bool IsCode() const { return Characteristics & IMAGE_SCN_MEM_EXECUTE; }
        bool Contains(uintptr_t Address) const { return Address >= Start && Address < End; }
    };

    /* 呼び出し命令の位置と、そのターゲット */
    struct CallSite
    {
        uintptr_t Instruction = 0;
        uintptr_t Target = 0;
    };

private:
    std::wstring Path;
    HMODULE Module = nullptr;
    uintptr_t Base = 0;
    uintptr_t Size = 0;
    std::vector<Section> Sections;

public:
    explicit PE(const std::wstring& InPath);

    PE(const PE&) = delete;
    PE& operator=(const PE&) = delete;

public:
    bool IsValid() const { return Module != nullptr; }
    const std::wstring& GetPath() const { return Path; }
    std::wstring GetFileName() const;
    uintptr_t GetBase() const { return Base; }
    uintptr_t GetSize() const { return Size; }
    const std::vector<Section>& GetSections() const { return Sections; }

    bool Contains(uintptr_t Address) const { return Address >= Base && Address < Base + Size; }
    bool IsInCode(uintptr_t Address) const;
    bool IsInData(uintptr_t Address) const;

    /* マングル名の部分一致でエクスポートを探す (シグネチャ変更に多少強くするため) */
    uintptr_t FindExport(std::string_view MangledNamePart) const;

    /*
    * Targets のいずれかを呼ぶ命令を .text から全部探す。
    *   call/jmp rel32            (E8 / E9)
    *   call/jmp qword [rip+X]    (FF 15 / FF 25)  ... X が IAT や関数ポインタテーブル
    */
    std::vector<CallSite> FindCallsTo(const std::unordered_set<uintptr_t>& Targets) const;

    /* CallInstruction の直前 MaxDistance バイト以内にある `lea <Reg>, [rip+X]` の X を近い順に列挙する */
    void ForEachLeaBefore(uintptr_t CallInstruction, uint8 ModRmReg, uint32 MaxDistance, const std::function<bool(uintptr_t)>& Callback) const;
};
