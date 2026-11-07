#include "Dumper.h"
#include "ExtraMappings.h"
#include "Mappings.h"
#include "PE.h"

#include <chrono>
#include <cstdio>
#include <fstream>
#include <memory>
#include <sstream>
#include <vector>

namespace
{
    constexpr const wchar_t* DefaultBinariesDir = L"C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame\\Binaries\\Win64";
    constexpr const wchar_t* EngineModuleName = L"UnrealEditorFortnite-Engine-Win64-Shipping.dll";
    constexpr const wchar_t* CommonModuleName = L"UnrealEditorFortnite-Common-Win64-Shipping.dll";
    constexpr const wchar_t* VersionFileName = L"UnrealEditorFortnite-Win64-Shipping.version";

    void PrintUsage()
    {
        printf(
            "Usage: UEFNStaticMappingsGenerator [options]\n"
            "  --dir <path>           Binaries\\Win64 directory (default: Fortnite install)\n"
            "  --core <dll>           Module containing CoreUObject (default: %S)\n"
            "  --module <dll>         Module to dump, repeatable (default: core + %S)\n"
            "  --out <file.usmap>     Output path (default: <BranchName>-CL-<Changelist>[_zs|_br|_oo].usmap)\n"
            "  --compression <method> none | zstd (default) | brotli | oodle\n"
            "  --level <n>            Compression level (zstd 1-22, default 19 / brotli 0-11, default 11 / oodle 0-9, default 7)\n"
            "  --oodle <dll>          oo2core_*_win64.dll to use (default: newest one in --dir)\n"
            "  --include-editor-only  Keep CPF_EditorOnly properties (cooked games do NOT have them)\n"
            "  --verbose              Print extra diagnostics\n"
            "  --extra <file.usmap>   Uncompressed .usmap whose structs are added when missing from the DLLs, repeatable\n"
            "  --game <dir>           Installed game whose DataTables fill in the row structs the DLLs lack (default: the install --dir is in)\n"
            "  --no-infer             Do not guess those row structs (RowStructInferrer.exe next to this executable does it)\n"
            "  --no-wait              Do not wait for Enter before exiting\n",
            EngineModuleName, CommonModuleName);
    }

    std::wstring JoinPath(const std::wstring& Dir, const std::wstring& File)
    {
        if (File.find(L':') != std::wstring::npos || File.starts_with(L"\\\\"))
            return File; // 絶対パス
        if (Dir.empty() || Dir.ends_with(L'\\') || Dir.ends_with(L'/'))
            return Dir + File;
        return Dir + L"\\" + File;
    }

    /* "Key": "Value" / "Key": 123 を雑に取り出す (.version は単純な JSON) */
    std::string ReadJsonField(const std::string& Json, const std::string& Key)
    {
        const size_t KeyPos = Json.find("\"" + Key + "\"");
        if (KeyPos == std::string::npos)
            return {};

        size_t Pos = Json.find(':', KeyPos);
        if (Pos == std::string::npos)
            return {};
        Pos = Json.find_first_not_of(" \t\r\n", Pos + 1);
        if (Pos == std::string::npos)
            return {};

        if (Json[Pos] == '"')
        {
            const size_t End = Json.find('"', Pos + 1);
            return End == std::string::npos ? std::string() : Json.substr(Pos + 1, End - Pos - 1);
        }

        const size_t End = Json.find_first_of(",}\r\n", Pos);
        return Json.substr(Pos, End - Pos);
    }

    std::wstring DefaultOutputName(const std::wstring& BinariesDir, EUsmapCompression Method)
    {
        const std::wstring Suffix = Compression::GetFileSuffix(Method);
        std::ifstream Stream(JoinPath(BinariesDir, VersionFileName));
        if (Stream)
        {
            std::stringstream Ss;
            Ss << Stream.rdbuf();
            const std::string Json = Ss.str();
            const std::string Branch = ReadJsonField(Json, "BranchName");
            const std::string Changelist = ReadJsonField(Json, "Changelist");
            if (!Branch.empty() && !Changelist.empty())
                return Utils::Utf8ToWide(Branch + "-CL-" + Changelist) + Suffix + L".usmap";
        }
        return L"Mappings" + Suffix + L".usmap";
    }

    std::wstring ToFullPath(const std::wstring& Path)
    {
        wchar_t FullPath[MAX_PATH * 4];
        if (!Path.empty() && GetFullPathNameW(Path.c_str(), static_cast<DWORD>(std::size(FullPath)), FullPath, nullptr))
            return FullPath;
        return Path;
    }

    std::wstring ExecutableDirectory()
    {
        wchar_t Path[MAX_PATH * 4];
        const DWORD Length = GetModuleFileNameW(nullptr, Path, static_cast<DWORD>(std::size(Path)));
        std::wstring Result(Path, Length);
        const size_t Slash = Result.find_last_of(L"\\/");
        return Slash == std::wstring::npos ? std::wstring() : Result.substr(0, Slash);
    }

    /* CommandLineToArgvW の規則でクォートする */
    std::wstring QuoteArgument(const std::wstring& Arg)
    {
        if (!Arg.empty() && Arg.find_first_of(L" \t\"") == std::wstring::npos)
            return Arg;

        std::wstring Result = L"\"";
        size_t Backslashes = 0;
        for (const wchar_t C : Arg)
        {
            if (C == L'\\') { Backslashes++; continue; }
            Result.append(C == L'"' ? Backslashes * 2 + 1 : Backslashes, L'\\');
            Backslashes = 0;
            Result += C;
        }
        Result.append(Backslashes * 2, L'\\');
        return Result + L"\"";
    }

    /* 子プロセスの出力はこのコンソールにそのまま流す。戻り値は終了コード (起動できなければ -1) */
    int RunProcess(const std::wstring& Executable, const std::vector<std::wstring>& Args, DWORD TimeoutMs)
    {
        std::wstring CommandLine = QuoteArgument(Executable);
        for (const std::wstring& Arg : Args)
            CommandLine += L" " + QuoteArgument(Arg);

        fflush(stdout);
        STARTUPINFOW Startup{ sizeof(Startup) };
        PROCESS_INFORMATION Process{};
        if (!CreateProcessW(Executable.c_str(), CommandLine.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &Startup, &Process))
            return -1;

        DWORD ExitCode = static_cast<DWORD>(-1);
        if (WaitForSingleObject(Process.hProcess, TimeoutMs) == WAIT_TIMEOUT)
        {
            TerminateProcess(Process.hProcess, 1);
            WaitForSingleObject(Process.hProcess, INFINITE);
            printf("[-] RowStructInferrer did not finish in %lu s\n", static_cast<unsigned long>(TimeoutMs / 1000));
        }
        else
        {
            GetExitCodeProcess(Process.hProcess, &ExitCode);
        }
        CloseHandle(Process.hThread);
        CloseHandle(Process.hProcess);
        return static_cast<int>(ExitCode);
    }

    /*
    * UEFN の DLL には、ゲーム本体だけにコンパイルされている GameFeature プラグインの型が無い
    * (例: 43.00 の DelilahTreasureHuntSharedRuntime の DelilahTreasurePrizeRow)。
    * それを行構造体に使う DataTable の行を、インストール済みのゲームから推定して加える。
    * 推定は CUE4Parse でゲームを読む .NET の RowStructInferrer.exe (この exe の隣) に任せる。
    * 失敗しても DLL だけのマッピングは書き出す。
    */
    void InferRowStructs(const std::wstring& GameDir, const std::wstring& OodleDll)
    {
        const std::wstring Helper = JoinPath(ExecutableDirectory(), L"RowStructInferrer.exe");
        if (GetFileAttributesW(Helper.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            printf("[!] %S not found: DataTable row structs missing from the DLLs are not guessed (build MappingsGenerator\\build.bat)\n", Helper.c_str());
            return;
        }

        wchar_t TempDir[MAX_PATH + 1];
        GetTempPathW(static_cast<DWORD>(std::size(TempDir)), TempDir);
        const std::wstring Prefix = std::wstring(TempDir) + L"UEFNStaticMappingsGenerator-" + std::to_wstring(GetCurrentProcessId());
        const std::wstring BasePath = Prefix + L".base.usmap";
        const std::wstring ExtraPath = Prefix + L".rowstructs.usmap";

        printf("[+] Guessing the DataTable row structs missing from the DLLs from %S\n", GameDir.c_str());
        CompressionOptions Uncompressed;
        Uncompressed.Method = EUsmapCompression::None;
        if (!Mappings(BasePath, Uncompressed).GenerateMappings())
        {
            printf("[-] Could not write %S for RowStructInferrer\n", BasePath.c_str());
            DeleteFileW(BasePath.c_str());
            return;
        }

        const int ExitCode = RunProcess(Helper, { L"--game", GameDir, L"--mappings", BasePath, L"--out", ExtraPath, L"--oodle", OodleDll }, 15 * 60 * 1000);
        DeleteFileW(BasePath.c_str());
        if (ExitCode != 0)
        {
            printf("[!] RowStructInferrer failed (exit code %d): the mapping has no guessed row structs\n", ExitCode);
            DeleteFileW(ExtraPath.c_str());
            return;
        }

        std::vector<Dumper::Struct> Extra;
        std::string Error;
        if (!ExtraMappings::Load(ExtraPath, Extra, Error))
            printf("[!] Could not read the guessed row structs: %s\n", Error.c_str());
        else
        {
            const size_t Read = Extra.size();
            const size_t Added = Dumper::AddExtraStructs(std::move(Extra));
            printf("[+] Guessed row structs: %zu structs, %zu added\n", Read, Added);
        }
        DeleteFileW(ExtraPath.c_str());
    }
}

int wmain(int argc, wchar_t** argv)
{
    std::wstring BinariesDir = DefaultBinariesDir;
    std::wstring CoreModuleName = EngineModuleName;
    std::vector<std::wstring> ModuleNames;
    std::wstring OutputPath;
    std::vector<std::wstring> ExtraPaths;
    std::wstring GameDir;
    bool bInfer = true;
    Dumper::Options Opts;
    CompressionOptions CompressionOpts;
    bool bWait = true;

    for (int i = 1; i < argc; i++)
    {
        const std::wstring Arg = argv[i];
        auto Next = [&]() -> std::wstring { return i + 1 < argc ? argv[++i] : L""; };

        if (Arg == L"--dir") BinariesDir = Next();
        else if (Arg == L"--core") CoreModuleName = Next();
        else if (Arg == L"--module") ModuleNames.push_back(Next());
        else if (Arg == L"--out") OutputPath = Next();
        else if (Arg == L"--compression")
        {
            if (!Compression::ParseMethod(Next(), CompressionOpts.Method))
            {
                PrintUsage();
                return -1;
            }
        }
        else if (Arg == L"--level") CompressionOpts.Level = _wtoi(Next().c_str());
        else if (Arg == L"--oodle") CompressionOpts.OodleDll = Next();
        else if (Arg == L"--include-editor-only") Opts.bIncludeEditorOnly = true;
        else if (Arg == L"--verbose") Opts.bVerbose = true;
        else if (Arg == L"--no-wait") bWait = false;
        else if (Arg == L"--extra") ExtraPaths.push_back(Next());
        else if (Arg == L"--game") GameDir = Next();
        else if (Arg == L"--no-infer") bInfer = false;
        else
        {
            PrintUsage();
            return Arg == L"--help" || Arg == L"-h" ? 0 : -1;
        }
    }

    if (ModuleNames.empty())
        ModuleNames = { CoreModuleName, CommonModuleName };

    if (OutputPath.empty())
        OutputPath = DefaultOutputName(BinariesDir, CompressionOpts.Method);

    /* UE の静的初期化子がカレントディレクトリを Binaries\Win64 に変えるので、ロード前に絶対パスにしておく */
    BinariesDir = ToFullPath(BinariesDir);
    OutputPath = ToFullPath(OutputPath);
    for (std::wstring& ExtraPath : ExtraPaths)
        ExtraPath = ToFullPath(ExtraPath);
    CompressionOpts.OodleDll = ToFullPath(CompressionOpts.OodleDll);
    /* Binaries\Win64 の3つ上がインストール先 (FortniteGame\Binaries\Win64) */
    GameDir = ToFullPath(GameDir.empty() ? JoinPath(BinariesDir, L"..\\..\\..") : GameDir);
    CompressionOpts.SearchDir = BinariesDir;

    const auto StartTime = std::chrono::steady_clock::now();

    /* CoreUObject を含むモジュールを先にロードし、他モジュールの依存解決にも使わせる */
    PE CoreModule(JoinPath(BinariesDir, CoreModuleName));
    if (!CoreModule.IsValid()) {
        printf("Failed To Load Core Module (%S), LastError: %u\n", CoreModule.GetPath().c_str(), static_cast<unsigned int>(GetLastError()));
        return -1;
    }
    printf("[+] Loaded %S at 0x%llX\n", CoreModule.GetFileName().c_str(), static_cast<unsigned long long>(CoreModule.GetBase()));

    std::vector<std::unique_ptr<PE>> OtherModules;
    for (const std::wstring& Name : ModuleNames)
    {
        if (_wcsicmp(Name.c_str(), CoreModuleName.c_str()) == 0)
            continue;

        auto Module = std::make_unique<PE>(JoinPath(BinariesDir, Name));
        if (!Module->IsValid()) {
            printf("Failed To Load Module (%S), LastError: %u\n", Module->GetPath().c_str(), static_cast<unsigned int>(GetLastError()));
            return -1;
        }
        printf("[+] Loaded %S at 0x%llX\n", Module->GetFileName().c_str(), static_cast<unsigned long long>(Module->GetBase()));
        OtherModules.push_back(std::move(Module));
    }

    if (!Dumper::SetupHooks(CoreModule))
        return -1;

    for (const std::wstring& Name : ModuleNames)
    {
        if (_wcsicmp(Name.c_str(), CoreModuleName.c_str()) == 0)
        {
            Dumper::DumpModule(CoreModule);
            continue;
        }

        for (const auto& Module : OtherModules)
        {
            if (Module->GetPath() == JoinPath(BinariesDir, Name))
                Dumper::DumpModule(*Module);
        }
    }

    Dumper::Resolve(Opts);

    for (const std::wstring& ExtraPath : ExtraPaths)
    {
        std::vector<Dumper::Struct> Extra;
        std::string Error;
        if (!ExtraMappings::Load(ExtraPath, Extra, Error))
        {
            printf("[-] Failed to read extra mappings %S: %s\n", ExtraPath.c_str(), Error.c_str());
            return -1;
        }
        const size_t Read = Extra.size();
        const size_t Added = Dumper::AddExtraStructs(std::move(Extra));
        printf("[+] Extra mappings %S: %zu structs, %zu added\n", ExtraPath.c_str(), Read, Added);
    }

    if (bInfer)
        InferRowStructs(GameDir, CompressionOpts.OodleDll.empty() ? BinariesDir : CompressionOpts.OodleDll);

    Dumper::PrintInfo();

    Mappings Mapping(OutputPath, CompressionOpts);
    const bool bSuccess = Mapping.GenerateMappings();

    const auto Elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - StartTime).count();
    printf("Done in %lld ms\n", static_cast<long long>(Elapsed));

    if (bWait) {
        printf("Press Enter To Close.");
        getchar();
    }

    /* UE のモジュールを FreeLibrary / 静的デストラクタで片付けさせると落ちることがあるので、そのまま終了する */
    fflush(stdout);
    TerminateProcess(GetCurrentProcess(), bSuccess ? 0 : 1);
    return bSuccess ? 0 : 1;
}
