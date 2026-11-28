#pragma once

#include "Compression.h"
#include "Dumper.h"
#include "StudioDownload.h"

#include <string>
#include <vector>

constexpr const wchar_t* DefaultBinariesDir = L"C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame\\Binaries\\Win64";
constexpr const wchar_t* EngineModuleName = L"UnrealEditorFortnite-Engine-Win64-Shipping.dll";
constexpr const wchar_t* CommonModuleName = L"UnrealEditorFortnite-Common-Win64-Shipping.dll";

/* コマンドライン引数かコンソールメニューで決める実行設定 */
struct RunSettings
{
    std::wstring BinariesDir = DefaultBinariesDir;
    std::wstring CoreModuleName = EngineModuleName;
    std::vector<std::wstring> ModuleNames;
    std::wstring OutputPath;
    Dumper::Options Opts;
    CompressionOptions CompressionOpts;
    StudioDownload::Options Download;
    bool bWait = true;
    bool bDownload = false;
    bool bDownloadOnly = false;
    bool bDirectorySpecified = false;

    /* メニューから実行した (終了前やエラー時にも Enter を待つ) */
    bool bInteractive = false;
};
