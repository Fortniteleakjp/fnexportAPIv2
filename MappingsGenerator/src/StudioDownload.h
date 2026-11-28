#pragma once

#include <filesystem>
#include <string>

namespace StudioDownload
{
    struct Options
    {
        std::filesystem::path Directory;
        unsigned int Connections = 32;
        std::wstring ManifestListUrl = L"https://api.fortniteapi.com/v1/manifests";
        std::wstring ManifestBaseUrl = L"https://fortnite-direct.dillycdn.com/manifests/";
        std::wstring ChunkBaseUrl = L"https://download.epicgames.com/Builds/Fortnite/CloudDir/";
        std::string ManifestId;
        bool InformationOnly = false;
    };

    std::filesystem::path Prepare(const Options& Settings);
}
