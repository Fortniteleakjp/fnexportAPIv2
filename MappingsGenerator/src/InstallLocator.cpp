#include "InstallLocator.h"
#include "Settings.h"
#include "Utils.h"

#include <nlohmann/json.hpp>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <vector>

namespace fs = std::filesystem;

namespace
{
    std::wstring ProgramData()
    {
        wchar_t Buffer[MAX_PATH];
        const DWORD Length = GetEnvironmentVariableW(L"ProgramData", Buffer, static_cast<DWORD>(std::size(Buffer)));
        return Length && Length < std::size(Buffer) ? std::wstring(Buffer, Length) : L"C:\\ProgramData";
    }

    std::optional<nlohmann::json> ReadJson(const fs::path& Path)
    {
        std::ifstream Stream(Path, std::ios::binary);
        if (!Stream)
            return std::nullopt;
        std::string Text((std::istreambuf_iterator<char>(Stream)), std::istreambuf_iterator<char>());
        if (Text.starts_with("\xEF\xBB\xBF"))
            Text.erase(0, 3);
        auto Json = nlohmann::json::parse(Text, nullptr, false);
        if (Json.is_discarded())
            return std::nullopt;
        return Json;
    }

    struct Candidate
    {
        std::wstring Location;
        bool bStudio;
    };

    void AddEntry(std::vector<Candidate>& Candidates, const nlohmann::json& Entry)
    {
        if (!Entry.is_object())
            return;
        const auto AppName = Entry.value("AppName", "");
        if (AppName != "Fortnite_Studio" && AppName != "Fortnite")
            return;
        const auto Location = Entry.value("InstallLocation", "");
        if (!Location.empty())
            Candidates.push_back({ Utils::Utf8ToWide(Location), AppName == "Fortnite_Studio" });
    }

    /* Epic Games Launcher がインストール先を記録しているファイルから集める */
    std::vector<Candidate> LauncherCandidates()
    {
        std::vector<Candidate> Candidates;
        const fs::path Root = ProgramData();
        if (const auto Installed = ReadJson(Root / L"Epic" / L"UnrealEngineLauncher" / L"LauncherInstalled.dat"))
            if (Installed->contains("InstallationList") && (*Installed)["InstallationList"].is_array())
                for (const auto& Entry : (*Installed)["InstallationList"])
                    AddEntry(Candidates, Entry);

        std::error_code Error;
        for (fs::directory_iterator It(Root / L"Epic" / L"EpicGamesLauncher" / L"Data" / L"Manifests", Error), End; !Error && It != End; It.increment(Error))
        {
            if (It->path().extension() != L".item")
                continue;
            if (const auto Item = ReadJson(It->path()))
                AddEntry(Candidates, *Item);
        }

        /* UEFN (Fortnite_Studio) を優先する */
        std::stable_partition(Candidates.begin(), Candidates.end(), [](const Candidate& Entry) { return Entry.bStudio; });
        return Candidates;
    }

    /* 記録が無い場合 (別 PC からのコピーなど) に備えて、各ドライブの定番フォルダーを見る */
    std::vector<std::wstring> DriveCandidates()
    {
        static const wchar_t* const Folders[] = {
            L"Program Files\\Epic Games\\Fortnite",
            L"Program Files (x86)\\Epic Games\\Fortnite",
            L"Epic Games\\Fortnite",
            L"Games\\Epic Games\\Fortnite",
            L"Games\\Fortnite",
            L"Fortnite",
        };
        std::vector<std::wstring> Candidates;
        const DWORD Drives = GetLogicalDrives();
        for (wchar_t Letter = L'A'; Letter <= L'Z'; ++Letter)
        {
            if (!(Drives & (1u << (Letter - L'A'))))
                continue;
            const std::wstring Drive = std::wstring(1, Letter) + L":\\";
            const UINT Type = GetDriveTypeW(Drive.c_str());
            if (Type != DRIVE_FIXED && Type != DRIVE_REMOVABLE)
                continue;
            for (const auto* Folder : Folders)
                Candidates.push_back(Drive + Folder);
        }
        return Candidates;
    }
}

bool InstallLocator::IsValid(const std::wstring& BinariesDir)
{
    std::error_code Error;
    return !BinariesDir.empty() && fs::is_regular_file(fs::path(BinariesDir) / EngineModuleName, Error);
}

std::wstring InstallLocator::Normalize(const std::wstring& Directory)
{
    if (Directory.empty())
        return Directory;
    const fs::path Base = Directory;
    for (const fs::path& Candidate : { Base, Base / L"Binaries" / L"Win64", Base / L"FortniteGame" / L"Binaries" / L"Win64" })
        if (IsValid(Candidate.wstring()))
            return Candidate.wstring();
    return Directory;
}

std::wstring InstallLocator::Find()
{
    for (const auto& Entry : LauncherCandidates())
    {
        const auto Directory = Normalize(Entry.Location);
        if (IsValid(Directory))
            return Directory;
    }
    for (const auto& Location : DriveCandidates())
    {
        const auto Directory = Normalize(Location);
        if (IsValid(Directory))
            return Directory;
    }
    return {};
}
