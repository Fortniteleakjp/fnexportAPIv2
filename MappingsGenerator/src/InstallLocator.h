#pragma once

#include <string>

/* インストール済み UEFN (Fortnite) の Binaries\Win64 を探す */
namespace InstallLocator
{
    /* Epic Games Launcher の記録と各ドライブの定番フォルダーから探す。見つからなければ空 */
    std::wstring Find();

    /* Fortnite のルート / FortniteGame / Binaries\Win64 のどれを渡されても Binaries\Win64 にする。
       該当しなければそのまま返す */
    std::wstring Normalize(const std::wstring& Directory);

    /* エンジン DLL があるか */
    bool IsValid(const std::wstring& BinariesDir);
}
