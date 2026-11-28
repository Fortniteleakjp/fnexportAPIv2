#include "ConsoleMenu.h"
#include "InstallLocator.h"
#include "ThirdPartyNotices.h"

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <optional>
#include <stdexcept>
#include <string_view>

namespace fs = std::filesystem;

namespace
{
    /* 入力が閉じられた (Ctrl+Z / Ctrl+C など) ときにメニューを抜ける */
    struct MenuClosed {};

    struct Choice
    {
        int Number;
        const char* Label;
        const char* Description;
    };

    enum EMode
    {
        ModeExit = 0,
        ModeInstalled = 1,
        ModeDownload = 2,
        ModeDownloadOnly = 3,
        ModeManifestInfo = 4,
        ModeLicenses = 5,
    };

    bool IsConsole(DWORD StdHandle)
    {
        DWORD Mode = 0;
        return GetConsoleMode(GetStdHandle(StdHandle), &Mode) != 0;
    }

    void Write(std::string_view Utf8)
    {
        fflush(stdout); // printf で出した分と順番が入れ替わらないように
        const std::wstring Text = Utils::Utf8ToWide(Utf8);
        const HANDLE Output = GetStdHandle(STD_OUTPUT_HANDLE);
        for (size_t Offset = 0; Offset < Text.size();)
        {
            const DWORD Length = static_cast<DWORD>(std::min<size_t>(Text.size() - Offset, 8192));
            DWORD Written = 0;
            if (!WriteConsoleW(Output, Text.data() + Offset, Length, &Written, nullptr) || Written == 0)
                return;
            Offset += Written;
        }
    }

    /* 説明文を字下げして出す (説明は "\n" 区切りの複数行) */
    void WriteIndented(const char* Text, const char* Indent)
    {
        if (!Text || !*Text)
            return;
        std::string_view Rest = Text;
        std::string Out;
        while (!Rest.empty())
        {
            const size_t End = Rest.find('\n');
            Out += Indent;
            Out += Rest.substr(0, End);
            Out += '\n';
            if (End == std::string_view::npos)
                break;
            Rest.remove_prefix(End + 1);
        }
        Write(Out);
    }

    std::wstring Trim(const std::wstring& Text)
    {
        const size_t Begin = Text.find_first_not_of(L" \t\r\n　");
        if (Begin == std::wstring::npos)
            return {};
        return Text.substr(Begin, Text.find_last_not_of(L" \t\r\n　") - Begin + 1);
    }

    std::wstring ReadLine()
    {
        const HANDLE Input = GetStdHandle(STD_INPUT_HANDLE);
        std::wstring Line;
        wchar_t Buffer[512];
        while (!Line.ends_with(L'\n'))
        {
            DWORD Read = 0;
            if (!ReadConsoleW(Input, Buffer, static_cast<DWORD>(std::size(Buffer)), &Read, nullptr) || Read == 0)
                throw MenuClosed();
            Line.append(Buffer, Read);
            if (Line.find(L'\x1a') != std::wstring::npos) // Ctrl+Z
                throw MenuClosed();
        }
        return Trim(Line);
    }

    std::optional<int> ParseInt(const std::wstring& Text)
    {
        if (Text.empty() || Text.size() > 9 || Text.find_first_not_of(L"0123456789") != std::wstring::npos)
            return std::nullopt;
        return std::stoi(Text);
    }

    std::string ToUtf8(const std::wstring& Text) { return Utils::WideToUtf8(Text); }

    void Heading(const char* Title, const char* Description)
    {
        ConsoleMenu::Print("\n■ %s\n", Title);
        WriteIndented(Description, "  ");
    }

    int AskChoice(const char* Title, const char* Description, std::initializer_list<Choice> Choices, int Default)
    {
        Heading(Title, Description);
        for (const Choice& Item : Choices)
        {
            ConsoleMenu::Print("  %d. %s\n", Item.Number, Item.Label);
            WriteIndented(Item.Description, "       ");
        }
        for (;;)
        {
            ConsoleMenu::Print("番号を入力 (Enter で %d) > ", Default);
            const std::wstring Line = ReadLine();
            if (Line.empty())
                return Default;
            if (const auto Value = ParseInt(Line))
            {
                for (const Choice& Item : Choices)
                {
                    if (Item.Number == *Value)
                        return *Value;
                }
            }
            ConsoleMenu::Print("  ※ 一覧にある番号を入力してください。\n");
        }
    }

    /* メニューで選ばない設定は固定 */
    void ApplyFixedSettings(RunSettings& Settings)
    {
        Settings = RunSettings();
        Settings.Opts.bIncludeEditorOnly = false;
        Settings.Opts.bVerbose = true;
        Settings.Download.Connections = 64;
        Settings.Download.ManifestId.clear(); // 最新のビルド
        /* OutputPath は空 = ビルド名から自動で名前を付けてカレントディレクトリに保存 */

        wchar_t ExecutablePath[32768];
        const DWORD Length = GetModuleFileNameW(nullptr, ExecutablePath, static_cast<DWORD>(std::size(ExecutablePath)));
        if (Length == 0 || Length == std::size(ExecutablePath))
            throw std::runtime_error("Could not determine the executable directory.");
        Settings.Download.Directory = fs::path(ExecutablePath).parent_path() / L"uefn_cache";
    }

    void AskCompression(RunSettings& Settings)
    {
        switch (AskChoice("圧縮方式", "作成する .usmap の圧縮方式です。どれも FModel でそのまま読み込めます。", {
            { 1, "Zstandard",
                "圧縮・展開が速く、サイズも十分小さくなります (約 2.2 MB)。ファイル名の末尾は _zs です。" },
            { 2, "Brotli",
                "Zstandard とほぼ同じサイズ (約 2.15 MB) ですが、圧縮に十数秒かかります。末尾は _br です。" },
            { 3, "Oodle",
                "UEFN に同梱されている oo2core_*_win64.dll で圧縮します (約 2.15 MB)。末尾は _oo です。\n"
                "DLL が見つからない場合は失敗します。" },
            { 4, "無圧縮",
                "圧縮しません (約 7.6 MB)。ファイル名に末尾は付きません。" },
        }, 4))
        {
        case 1: Settings.CompressionOpts.Method = EUsmapCompression::ZStandard; break;
        case 2: Settings.CompressionOpts.Method = EUsmapCompression::Brotli; break;
        case 3: Settings.CompressionOpts.Method = EUsmapCompression::Oodle; break;
        default: Settings.CompressionOpts.Method = EUsmapCompression::None; break;
        }
    }

    /* 自動で見つからなかったときに UEFN (Fortnite) のフォルダーを入力してもらう */
    bool AskInstallFolder(RunSettings& Settings)
    {
        Heading("UEFN のフォルダー",
            "インストール済みの UEFN (Fortnite) が自動で見つかりませんでした。\n"
            "Fortnite をインストールしたフォルダーを入力してください (例: D:\\Epic Games\\Fortnite)。\n"
            "エクスプローラーからフォルダーをドラッグ＆ドロップしても入力できます。\n"
            "何も入力せずに Enter を押すとメニューに戻ります。\n"
            "UEFN をインストールしていない場合は「2. 最新の UEFN をダウンロードして生成」を選んでください。");
        for (;;)
        {
            Write("  > ");
            auto Input = ReadLine();
            if (Input.size() >= 2 && Input.front() == L'"' && Input.back() == L'"')
                Input = Trim(Input.substr(1, Input.size() - 2));
            if (Input.empty())
                return false;
            const auto Directory = InstallLocator::Normalize(Input);
            if (InstallLocator::IsValid(Directory))
            {
                Settings.BinariesDir = Directory;
                Settings.bDirectorySpecified = true;
                return true;
            }
            ConsoleMenu::Print("  ※ %s が見つかりません: %s\n", ToUtf8(EngineModuleName).c_str(), ToUtf8(Input).c_str());
        }
    }

    void PrintSummary(const RunSettings& Settings, int Mode)
    {
        static const char* const ModeNames[] = { "", "インストール済みの UEFN から生成", "最新の UEFN をダウンロードして生成",
            "ダウンロードのみ", "最新ビルドの情報を確認" };
        ConsoleMenu::Print("\n==================== 実行内容 ====================\n");
        ConsoleMenu::Print("  処理               : %s\n", ModeNames[Mode]);
        if (Mode == ModeInstalled)
            ConsoleMenu::Print("  UEFN のフォルダー  : %s\n", ToUtf8(Settings.BinariesDir).c_str());
        if (Settings.bDownload)
        {
            ConsoleMenu::Print("  ダウンロード先     : %s\n", ToUtf8(Settings.Download.Directory.wstring()).c_str());
            if (Mode != ModeManifestInfo)
                ConsoleMenu::Print("  同時接続数         : %u\n", Settings.Download.Connections);
            ConsoleMenu::Print("  ビルド             : 最新\n");
        }
        if (Mode == ModeInstalled || Mode == ModeDownload)
        {
            const EUsmapCompression Method = Settings.CompressionOpts.Method;
            std::error_code Error;
            ConsoleMenu::Print("  圧縮方式           : %s\n", Method == EUsmapCompression::None ? "無圧縮" : Compression::GetName(Method));
            ConsoleMenu::Print("  保存先             : %s (名前は自動)\n", ToUtf8(fs::current_path(Error).wstring()).c_str());
            ConsoleMenu::Print("  エディター専用     : いいえ\n");
            ConsoleMenu::Print("  詳細ログ           : 出す\n");
        }
        ConsoleMenu::Print("==================================================\n\n");
    }
}

bool ConsoleMenu::IsAvailable()
{
    return IsConsole(STD_INPUT_HANDLE) && IsConsole(STD_OUTPUT_HANDLE);
}

void ConsoleMenu::Print(const char* Format, ...)
{
    va_list Args;
    va_start(Args, Format);
    va_list Copy;
    va_copy(Copy, Args);
    const int Length = vsnprintf(nullptr, 0, Format, Copy);
    va_end(Copy);
    if (Length > 0)
    {
        std::string Text(static_cast<size_t>(Length) + 1, '\0');
        vsnprintf(Text.data(), Text.size(), Format, Args);
        Text.resize(static_cast<size_t>(Length));
        Write(Text);
    }
    va_end(Args);
}

void ConsoleMenu::WaitForEnter()
{
    Print("\nEnter キーを押すと終了します。");
    try { ReadLine(); }
    catch (const MenuClosed&) {}
}

bool ConsoleMenu::Configure(RunSettings& Settings)
{
    try
    {
        for (;;)
        {
            Print("\n========================================================\n"
                "  UEFNStaticMappingsGenerator\n"
                "  UEFN の DLL から FModel / CUE4Parse 用のマッピングファイル (.usmap) を作ります。\n"
                "  Fortnite 本体を起動したり書き換えたりすることはありません。\n"
                "========================================================\n");

            const int Mode = AskChoice("実行する処理", nullptr, {
                { ModeInstalled, "インストール済みの UEFN から生成",
                    "この PC にインストールされている UEFN (Fortnite) の DLL を読み込んで .usmap を作ります。\n"
                    "ダウンロードは行わないので、数秒で終わります。" },
                { ModeDownload, "最新の UEFN をダウンロードして生成",
                    "Epic のサーバーから最新の UEFN (Fortnite_Studio) を取得し、その DLL から .usmap を作ります。\n"
                    "UEFN のインストールは不要ですが、大きなダウンロードと空き容量が必要です。\n"
                    "取得したデータは EXE と同じフォルダーの uefn_cache に保存し、次回は検証済みのデータを再利用します。" },
                { ModeDownloadOnly, "ダウンロードのみ",
                    "最新の UEFN の全ファイルを uefn_cache に取得・復元するところまで行います。.usmap は作りません。" },
                { ModeManifestInfo, "最新ビルドの情報を確認",
                    "最新 UEFN のビルド名・ファイル数・ダウンロード量・保存先を表示します。ファイルは取得しません。" },
                { ModeLicenses, "ライセンスを表示",
                    "このツールに組み込まれているライブラリ (zstd / Brotli / zlib / nlohmann/json) のライセンスを表示します。" },
                { ModeExit, "終了", "何もせずに終了します。" },
            }, ModeInstalled);

            if (Mode == ModeExit)
                return false;
            if (Mode == ModeLicenses)
            {
                Write(ThirdPartyNotices());
                continue;
            }

            ApplyFixedSettings(Settings);
            if (Mode == ModeInstalled)
            {
                if (!InstallLocator::IsValid(Settings.BinariesDir) && !AskInstallFolder(Settings))
                    continue;
            }
            else
            {
                Settings.bDownload = true;
                Settings.bDownloadOnly = Mode == ModeDownloadOnly;
                Settings.Download.InformationOnly = Mode == ModeManifestInfo;
            }
            if (Mode == ModeInstalled || Mode == ModeDownload)
                AskCompression(Settings);

            PrintSummary(Settings, Mode);
            Settings.bInteractive = true;
            return true;
        }
    }
    catch (const MenuClosed&)
    {
        return false;
    }
}
