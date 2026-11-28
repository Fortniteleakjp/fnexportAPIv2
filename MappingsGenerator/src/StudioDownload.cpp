#include "StudioDownload.h"
#include "BuildManifest.h"
#include "Utils.h"

#include <winhttp.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <fstream>
#include <functional>
#include <mutex>
#include <cwctype>
#include <optional>
#include <set>
#include <unordered_map>
#include <stdexcept>
#include <thread>

namespace
{
    using namespace BuildPatch;
    namespace fs = std::filesystem;
    std::atomic_bool Cancelled = false;

    BOOL WINAPI ConsoleHandler(DWORD Event)
    {
        if (Event != CTRL_C_EVENT && Event != CTRL_BREAK_EVENT) return FALSE;
        Cancelled = true;
        return TRUE;
    }

    void CheckCancelled()
    {
        if (Cancelled) throw std::runtime_error("Download cancelled. Verified chunks are retained for the next run.");
    }

    struct HttpHandle
    {
        HINTERNET Value = nullptr;
        explicit HttpHandle(HINTERNET Handle) : Value(Handle)
        {
            if (!Handle) throw std::runtime_error("WinHTTP initialization failed: " + std::to_string(GetLastError()));
        }
        ~HttpHandle() { WinHttpCloseHandle(Value); }
        HttpHandle(const HttpHandle&) = delete;
        HttpHandle& operator=(const HttpHandle&) = delete;
    };

    class HttpError : public std::runtime_error
    {
    public:
        bool Retry;
        HttpError(const std::string& Message, bool ShouldRetry) : std::runtime_error(Message), Retry(ShouldRetry) {}
    };

    class HttpClient
    {
    public:
        HttpClient() : Session(WinHttpOpen(L"UEFNStaticMappingsGenerator/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0))
        {
            WinHttpSetTimeouts(Session.Value, 10000, 15000, 30000, 30000);
            DWORD Connections = 64;
            WinHttpSetOption(Session.Value, WINHTTP_OPTION_MAX_CONNS_PER_SERVER, &Connections, sizeof(Connections));
        }

        Bytes Get(const std::wstring& Url, uint64_t Maximum) const
        {
            for (unsigned int Attempt = 0; ; ++Attempt)
            {
                CheckCancelled();
                try { return Request(Url, Maximum); }
                catch (const HttpError& Error)
                {
                    if (!Error.Retry || Attempt >= 4 || Cancelled) throw;
                    const auto Delay = 250u << Attempt;
                    for (unsigned int Waited = 0; Waited < Delay; Waited += 50)
                    {
                        CheckCancelled();
                        std::this_thread::sleep_for(std::chrono::milliseconds(50));
                    }
                }
            }
        }

    private:
        HttpHandle Session;

        static void Require(BOOL Success, const char* Operation)
        {
            if (!Success) throw HttpError(std::string(Operation) + " failed: " + std::to_string(GetLastError()), true);
        }

        Bytes Request(const std::wstring& Url, uint64_t Maximum) const
        {
            URL_COMPONENTS Components{};
            Components.dwStructSize = sizeof(Components);
            Components.dwHostNameLength = Components.dwUrlPathLength = Components.dwExtraInfoLength = static_cast<DWORD>(-1);
            if (!WinHttpCrackUrl(Url.c_str(), static_cast<DWORD>(Url.size()), 0, &Components) ||
                (Components.nScheme != INTERNET_SCHEME_HTTP && Components.nScheme != INTERNET_SCHEME_HTTPS))
                throw HttpError("Invalid HTTP URL.", false);
            const std::wstring Host(Components.lpszHostName, Components.dwHostNameLength);
            std::wstring Path(Components.lpszUrlPath, Components.dwUrlPathLength);
            if (Components.dwExtraInfoLength) Path.append(Components.lpszExtraInfo, Components.dwExtraInfoLength);
            HttpHandle Connection(WinHttpConnect(Session.Value, Host.c_str(), Components.nPort, 0));
            HttpHandle RequestHandle(WinHttpOpenRequest(Connection.Value, L"GET", Path.c_str(), nullptr,
                WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, Components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0));
            Require(WinHttpSendRequest(RequestHandle.Value, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0), "HTTP send");
            Require(WinHttpReceiveResponse(RequestHandle.Value, nullptr), "HTTP response");
            DWORD Status = 0, StatusSize = sizeof(Status);
            Require(WinHttpQueryHeaders(RequestHandle.Value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &Status, &StatusSize, WINHTTP_NO_HEADER_INDEX), "HTTP status");
            if (Status != 200)
                throw HttpError("HTTP " + std::to_string(Status) + ": " + Utils::WideToUtf8(Url), Status == 408 || Status == 429 || Status >= 500);

            DWORD Length = 0, LengthSize = sizeof(Length);
            const bool HasLength = WinHttpQueryHeaders(RequestHandle.Value, WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &Length, &LengthSize, WINHTTP_NO_HEADER_INDEX) != FALSE;
            if (HasLength && Length > Maximum)
                throw HttpError("HTTP response exceeds " + std::to_string(Maximum) + " bytes: " + Utils::WideToUtf8(Url), false);
            Bytes Data;
            if (HasLength) Data.reserve(Length);
            std::array<unsigned char, 65536> Buffer{};
            for (;;)
            {
                CheckCancelled();
                DWORD Count = 0;
                Require(WinHttpReadData(RequestHandle.Value, Buffer.data(), static_cast<DWORD>(Buffer.size()), &Count), "HTTP read");
                if (Count == 0) break;
                if (Count > Maximum - Data.size())
                    throw HttpError("HTTP response exceeds " + std::to_string(Maximum) + " bytes: " + Utils::WideToUtf8(Url), false);
                Data.insert(Data.end(), Buffer.begin(), Buffer.begin() + Count);
            }
            if (HasLength && Data.size() != Length) throw HttpError("Incomplete HTTP response.", true);
            return Data;
        }
    };

    void EnsureDirectory(const fs::path& Directory)
    {
        fs::create_directories(Directory);
        auto Current = Directory;
        while (Current != Current.root_path() && !Current.empty())
        {
            const auto Attributes = GetFileAttributesW(Current.c_str());
            if (Attributes == INVALID_FILE_ATTRIBUTES || (Attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("Download directories must not contain junctions or symbolic links.");
            Current = Current.parent_path();
        }
    }

    class AtomicFile
    {
    public:
        explicit AtomicFile(const fs::path& Target) : Destination(Target), Temporary(Target.wstring() + L".part")
        {
            EnsureDirectory(Target.parent_path());
            const auto Attributes = GetFileAttributesW(Target.c_str());
            const auto TempAttributes = GetFileAttributesW(Temporary.c_str());
            if ((Attributes != INVALID_FILE_ATTRIBUTES && (Attributes & FILE_ATTRIBUTE_REPARSE_POINT)) ||
                (TempAttributes != INVALID_FILE_ATTRIBUTES && (TempAttributes & FILE_ATTRIBUTE_REPARSE_POINT)))
                throw std::runtime_error("Download files must not be symbolic links.");
            Stream.open(Temporary, std::ios::binary | std::ios::trunc);
            if (!Stream) throw std::runtime_error("Could not create " + Utils::WideToUtf8(Temporary.wstring()));
        }
        ~AtomicFile()
        {
            Stream.close();
            std::error_code Error;
            fs::remove(Temporary, Error);
        }
        void Write(std::span<const unsigned char> Data)
        {
            Stream.write(reinterpret_cast<const char*>(Data.data()), Data.size());
            if (!Stream) throw std::runtime_error("Disk write failed: " + Utils::WideToUtf8(Temporary.wstring()));
        }
        void Commit()
        {
            Stream.close();
            if (Stream.fail() || !MoveFileExW(Temporary.c_str(), Destination.c_str(), MOVEFILE_REPLACE_EXISTING))
                throw std::runtime_error("Could not save " + Utils::WideToUtf8(Destination.wstring()));
        }
    private:
        fs::path Destination, Temporary;
        std::ofstream Stream;
    };

    void Save(const fs::path& Path, std::span<const unsigned char> Data)
    {
        AtomicFile File(Path);
        File.Write(Data);
        File.Commit();
    }

    struct DownloadLock
    {
        HANDLE Handle;
        explicit DownloadLock(const fs::path& Root)
            : Handle(CreateFileW((Root / L".download.lock").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr))
        {
            if (Handle == INVALID_HANDLE_VALUE) throw std::runtime_error("Another downloader is using this directory, or it is not writable.");
        }
        ~DownloadLock() { CloseHandle(Handle); }
    };

    std::wstring BaseUrl(std::wstring Url)
    {
        if (Url.empty()) throw std::runtime_error("A download base URL is empty.");
        if (Url.back() != L'/') Url += L'/';
        return Url;
    }

    void ValidateId(const std::string& Id)
    {
        if (Id.empty() || Id.size() > 128 || Id.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-") != std::string::npos)
            throw std::runtime_error("Invalid manifest ID.");
    }

    std::pair<std::string, std::string> LatestManifest(const HttpClient& Client, const std::wstring& Url)
    {
        const auto Data = Client.Get(Url, 16 * 1024 * 1024);
        const auto List = nlohmann::json::parse(Data);
        if (!List.is_array()) throw std::runtime_error("The manifest API did not return an array.");
        std::string Id, LatestDate;
        for (const auto& Entry : List)
        {
            if (Entry.value("appName", "") != "Fortnite_Studio" || Entry.value("labelName", "") != "Live-Windows") continue;
            const auto Date = Entry.value("discoveredAt", Entry.value("completedAt", ""));
            if (Id.empty() || Date > LatestDate)
            {
                Id = Entry.at("manifestId").get<std::string>();
                LatestDate = Date;
            }
        }
        ValidateId(Id);
        std::string ExpectedHash;
        for (const auto& Entry : List)
            if (Entry.value("manifestId", "") == Id) ExpectedHash = Entry.value("hash", "");
        return { Id, ExpectedHash };
    }

    std::string Hex(const Hash& Value)
    {
        std::string Result;
        for (const auto Byte : Value)
        {
            constexpr char Digits[] = "0123456789abcdef";
            Result += Digits[Byte >> 4];
            Result += Digits[Byte & 15];
        }
        return Result;
    }

    struct Progress
    {
        std::atomic_uint64_t Finished = 0, Bytes = 0, Reused = 0;
    };

    void Parallel(size_t Count, unsigned int Connections, Progress& State, const char* Label,
        const std::function<void(size_t)>& Work)
    {
        std::atomic_size_t Next = 0;
        std::atomic_bool Failed = false;
        std::atomic_uint Running = std::min(Connections, static_cast<unsigned int>(Count));
        std::mutex Mutex;
        std::condition_variable Updated;
        std::exception_ptr Error;
        const auto Start = std::chrono::steady_clock::now();
        std::vector<std::jthread> Workers;
        for (unsigned int Index = 0; Index < std::min(Connections, static_cast<unsigned int>(Count)); ++Index)
        {
            Workers.emplace_back([&]
            {
                try
                {
                    while (!Failed && !Cancelled)
                    {
                        const auto Index = Next.fetch_add(1);
                        if (Index >= Count) break;
                        Work(Index);
                        ++State.Finished;
                    }
                }
                catch (...)
                {
                    Failed = true;
                    std::lock_guard Guard(Mutex);
                    if (!Error) Error = std::current_exception();
                }
                --Running;
                Updated.notify_one();
            });
        }
        std::unique_lock Guard(Mutex);
        do
        {
            Updated.wait_for(Guard, std::chrono::seconds(1), [&] { return Running == 0; });
            const auto Seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - Start).count();
            printf("\r[%s] %llu/%zu, reused %llu, %.1f MB/s    ", Label,
                static_cast<unsigned long long>(State.Finished.load()), Count,
                static_cast<unsigned long long>(State.Reused.load()), State.Bytes.load() / 1000000.0 / std::max(Seconds, 0.001));
            fflush(stdout);
        } while (Running != 0);
        Guard.unlock();
        Workers.clear();
        printf("\n");
        if (Error) std::rethrow_exception(Error);
        CheckCancelled();
    }

    Manifest LoadManifest(const HttpClient& Client, const StudioDownload::Options& Settings,
        const fs::path& Root, const std::string& Id, const std::string& ExpectedHash, bool Studio)
    {
        ValidateId(Id);
        const auto Path = Root / L"manifests" / (Utils::Utf8ToWide(Id) + L".manifest");
        if (fs::exists(Path))
        {
            try
            {
                const auto Data = ReadFile(Path, 128 * 1024 * 1024);
                if (!ExpectedHash.empty() && Hex(Digest(Data)) != ExpectedHash) throw std::runtime_error("Manifest checksum changed.");
                return Manifest::Parse(Data, Studio);
            }
            catch (const std::exception&) { }
        }
        const auto Url = BaseUrl(Settings.ManifestBaseUrl) + Utils::Utf8ToWide(Id) + L".manifest";
        const auto Data = Client.Get(Url, 128 * 1024 * 1024);
        if (!ExpectedHash.empty() && Hex(Digest(Data)) != ExpectedHash)
            throw std::runtime_error("Downloaded manifest does not match the API SHA-1.");
        auto Result = Manifest::Parse(Data, Studio);
        Save(Path, Data);
        return Result;
    }

    std::wstring PathKey(const fs::path& Path)
    {
        auto Key = Path.wstring();
        std::transform(Key.begin(), Key.end(), Key.begin(), towlower);
        return Key;
    }

    // "++Fortnite+Release-42.40-CL-59247843-Windows" -> "++Fortnite+Release-42.40"
    std::string ReleaseOf(const std::string& Build)
    {
        const auto Position = Build.find("-CL-");
        return Position == std::string::npos ? std::string() : Build.substr(0, Position);
    }

    Manifest SharedRuntime(const HttpClient& Client, const StudioDownload::Options& Settings,
        const fs::path& Root, const Manifest& Studio)
    {
        const auto Data = Client.Get(Settings.ManifestListUrl, 16 * 1024 * 1024);
        const auto List = nlohmann::json::parse(Data);
        if (!List.is_array()) throw std::runtime_error("The manifest API did not return an array.");
        // Studio and the game are often published from different CLs of the same release.
        // The shared DLLs are redistributables (CRT, TBB, ...), so the same release is sufficient.
        const auto Release = ReleaseOf(Studio.Build);
        std::string Id, ExpectedHash, Build, Date;
        bool Exact = false;
        for (const auto& Entry : List)
        {
            if (Entry.value("appName", "") != "Fortnite" || Entry.value("labelName", "") != "Live-Windows") continue;
            const auto Candidate = Entry.value("buildVersion", "");
            const bool CandidateExact = Candidate == Studio.Build;
            if (!CandidateExact && (Release.empty() || ReleaseOf(Candidate) != Release)) continue;
            const auto Discovered = Entry.value("discoveredAt", Entry.value("completedAt", ""));
            if (Exact && !CandidateExact) continue;
            if (Id.empty() || (CandidateExact && !Exact) || Discovered > Date)
            {
                Id = Entry.at("manifestId").get<std::string>();
                ExpectedHash = Entry.value("hash", "");
                Build = Candidate;
                Date = Discovered;
                Exact = CandidateExact;
            }
        }
        if (Id.empty()) throw std::runtime_error("No matching Fortnite Windows manifest was found for the Studio runtime: " + Studio.Build);
        if (!Exact) printf("[!] Fortnite %s is not published; using %s for shared runtime DLLs\n", Studio.Build.c_str(), Build.c_str());
        auto Game = LoadManifest(Client, Settings, Root, Id, ExpectedHash, false);
        if (Game.Build != Build) throw std::runtime_error("Fortnite runtime manifest does not match the manifest API.");
        std::set<std::wstring> StudioPaths;
        for (const auto& File : Studio.Files) StudioPaths.insert(PathKey(File.Path));
        std::set<size_t> Needed;
        Manifest Result;
        Result.Build = Game.Build;
        Result.ChunkDirectory = Game.ChunkDirectory;
        for (const auto& File : Game.Files)
        {
            if (PathKey(File.Path.parent_path()) != L"fortnitegame\\binaries\\win64" ||
                PathKey(File.Path.extension()) != L".dll" || StudioPaths.contains(PathKey(File.Path))) continue;
            Result.Files.push_back(File);
            Result.InstallSize += File.Size;
            for (const auto& Part : File.Parts) Needed.insert(Part.ChunkIndex);
        }
        std::unordered_map<size_t, size_t> IndexMap;
        for (const auto Index : Needed)
        {
            IndexMap[Index] = Result.Chunks.size();
            const auto& Chunk = Game.Chunks[Index];
            Result.Chunks.push_back(Chunk);
            Result.CacheSize += Chunk.WindowSize;
            Result.DownloadSize += Chunk.DownloadSize;
        }
        for (auto& File : Result.Files)
            for (auto& Part : File.Parts) Part.ChunkIndex = IndexMap.at(Part.ChunkIndex);
        return Result;
    }

    void InstallManifest(const Manifest& Manifest, const StudioDownload::Options& Settings,
        const HttpClient& Client, const fs::path& Root, const fs::path& Install)
    {
        const auto Cache = Root / L"chunks";
        EnsureDirectory(Cache);
        EnsureDirectory(Install);
        std::vector<unsigned char> CachedChunks(Manifest.Chunks.size()), InstalledFiles(Manifest.Files.size());
        Progress VerifiedChunks, VerifiedFiles;
        Parallel(Manifest.Chunks.size(), Settings.Connections, VerifiedChunks, "verify chunks", [&](size_t Index)
        {
            const auto& Chunk = Manifest.Chunks[Index];
            CachedChunks[Index] = VerifyFile(Cache / Chunk.Name(), Chunk.WindowSize, Chunk.Sha);
            if (CachedChunks[Index]) ++VerifiedChunks.Reused;
        });
        Parallel(Manifest.Files.size(), std::min(Settings.Connections, 8u), VerifiedFiles, "verify files", [&](size_t Index)
        {
            const auto& File = Manifest.Files[Index];
            InstalledFiles[Index] = VerifyFile(Install / File.Path, File.Size, File.Sha);
            if (InstalledFiles[Index]) ++VerifiedFiles.Reused;
        });

        uint64_t Required = 128 * 1024 * 1024;
        for (size_t Index = 0; Index < Manifest.Chunks.size(); ++Index)
            if (!CachedChunks[Index]) Required += Manifest.Chunks[Index].WindowSize;
        for (size_t Index = 0; Index < Manifest.Files.size(); ++Index)
            if (!InstalledFiles[Index]) Required += Manifest.Files[Index].Size;
        // Each .part becomes its destination by renaming, so its size is already included.
        // Invalid existing files remain intact until replacement; count the full repair size.
        const auto Available = fs::space(Root).available;
        printf("[+] Disk space: %.2f GB required, %.2f GB free\n", Required / 1e9, Available / 1e9);
        if (Available < Required)
        {
            char Message[256];
            snprintf(Message, sizeof(Message), "Not enough disk space: need %.2f GB free, have %.2f GB (%.2f GB short). Use --download-dir on a drive with enough free space.",
                Required / 1e9, Available / 1e9, (Required - Available) / 1e9);
            throw std::runtime_error(Message);
        }

        Progress Chunks;
        const auto ChunkBase = BaseUrl(Settings.ChunkBaseUrl) + Utils::Utf8ToWide(Manifest.ChunkDirectory) + L"/";
        Parallel(Manifest.Chunks.size(), Settings.Connections, Chunks, "download", [&](size_t Index)
        {
            const auto& Chunk = Manifest.Chunks[Index];
            const auto CachedPath = Cache / Chunk.Name();
            if (CachedChunks[Index]) { ++Chunks.Reused; return; }
            char Group[4];
            snprintf(Group, sizeof(Group), "%02u", Chunk.Group);
            const auto Url = ChunkBase + Utils::Utf8ToWide(Group) + L"/" + Utils::Utf8ToWide(Chunk.Name());
            // Epic may serve a newer chunk header than the size recorded in older manifests.
            // Bound the transfer by its window size and verify the header, GUID and raw SHA-1.
            const auto Maximum = std::max<uint64_t>(Chunk.DownloadSize, Chunk.WindowSize + Chunk.WindowSize / 100 + 128);
            const auto Data = Client.Get(Url, Maximum);
            const auto Raw = DecodeChunk(Data, Chunk);
            CheckCancelled();
            Save(CachedPath, Raw);
            Chunks.Bytes += Data.size();
        });

        Progress Files;
        Parallel(Manifest.Files.size(), std::min(Settings.Connections, 8u), Files, "restore", [&](size_t Index)
        {
            const auto& File = Manifest.Files[Index];
            const auto Target = Install / File.Path;
            if (InstalledFiles[Index]) { ++Files.Reused; return; }
            AtomicFile Output(Target);
            Sha1 Hasher;
            Bytes Data;
            std::ifstream Source;
            size_t PreviousChunk = static_cast<size_t>(-1);
            for (const auto& Part : File.Parts)
            {
                CheckCancelled();
                if (PreviousChunk != Part.ChunkIndex)
                {
                    const auto& Chunk = Manifest.Chunks[Part.ChunkIndex];
                    Source.close();
                    Source.clear();
                    Source.open(Cache / Chunk.Name(), std::ios::binary);
                    if (!Source) throw std::runtime_error("Could not read cached chunk: " + Chunk.Name());
                    PreviousChunk = Part.ChunkIndex;
                }
                // Chunks were verified before restoration; read only the needed range.
                // The completed file is still checked against its own manifest SHA-1.
                Data.resize(Part.Size);
                Source.seekg(Part.Offset);
                Source.read(reinterpret_cast<char*>(Data.data()), Data.size());
                if (!Source) throw std::runtime_error("Could not read a complete chunk part.");
                Output.Write(Data);
                Hasher.Update(Data);
                Files.Bytes += Data.size();
            }
            if (Hasher.Finish() != File.Sha) throw std::runtime_error("Restored file SHA-1 mismatch: " + Utils::WideToUtf8(File.Path.wstring()));
            CheckCancelled();
            Output.Commit();
        });
    }

}

namespace StudioDownload
{
    fs::path Prepare(const Options& Settings)
    {
        if (Settings.Connections < 1 || Settings.Connections > 64) throw std::runtime_error("--connections must be 1-64.");
        Cancelled = false;
        struct CancellationHandler
        {
            CancellationHandler() { SetConsoleCtrlHandler(ConsoleHandler, TRUE); }
            ~CancellationHandler() { SetConsoleCtrlHandler(ConsoleHandler, FALSE); }
        } Handler;
        HttpClient Client;
        auto Id = Settings.ManifestId;
        std::string ExpectedHash;
        if (Id.empty()) std::tie(Id, ExpectedHash) = LatestManifest(Client, Settings.ManifestListUrl);
        ValidateId(Id);
        printf("[+] Fortnite_Studio manifest: %s\n", Id.c_str());

        const auto Root = fs::absolute(Settings.Directory).lexically_normal();
        EnsureDirectory(Root);
        DownloadLock Lock(Root);
        auto Manifest = LoadManifest(Client, Settings, Root, Id, ExpectedHash, true);
        printf("[+] %s: %zu chunks, %zu files, %.2f GB download, %.2f GB installed\n", Manifest.Build.c_str(),
            Manifest.Chunks.size(), Manifest.Files.size(), Manifest.DownloadSize / 1e9, Manifest.InstallSize / 1e9);
        const auto Install = Root / Utils::Utf8ToWide(Id);
        const auto Binaries = Install / L"FortniteGame" / L"Binaries" / L"Win64";
        printf("[+] Install: %S\n", Install.c_str());
        if (Settings.InformationOnly) return Binaries;

        std::optional<BuildPatch::Manifest> Runtime;
        const auto EngineName = L"UnrealEditorFortnite-Engine-Win64-Shipping.dll";
        if (std::any_of(Manifest.Files.begin(), Manifest.Files.end(), [&](const auto& File) { return File.Path.filename() == EngineName; }))
            Runtime = SharedRuntime(Client, Settings, Root, Manifest);

        InstallManifest(Manifest, Settings, Client, Root, Install);
        if (Runtime && !Runtime->Files.empty())
        {
            printf("[+] Shared runtime: %zu DLLs, %zu chunks, %.2f MB download\n",
                Runtime->Files.size(), Runtime->Chunks.size(), Runtime->DownloadSize / 1e6);
            InstallManifest(*Runtime, Settings, Client, Root, Install);
        }
        printf("[+] All chunks and files verified.\n");
        return Binaries;
    }
}
