#pragma once

#include <array>
#include <filesystem>
#include <span>
#include <string>
#include <vector>
#include <cstdint>

namespace BuildPatch
{
    using Bytes = std::vector<unsigned char>;
    using Hash = std::array<unsigned char, 20>;

    class Sha1
    {
    public:
        Sha1();
        ~Sha1();
        Sha1(const Sha1&) = delete;
        Sha1& operator=(const Sha1&) = delete;
        void Update(std::span<const unsigned char> Data);
        Hash Finish();
    private:
        void* Handle = nullptr;
    };

    Hash Digest(std::span<const unsigned char> Data);
    bool VerifyFile(const std::filesystem::path& Path, uint64_t Size, const Hash& Expected);
    Bytes ReadFile(const std::filesystem::path& Path, uint64_t Maximum);

    struct Chunk
    {
        std::array<uint32_t, 4> Guid{};
        uint64_t RollingHash = 0;
        Hash Sha{};
        unsigned char Group = 0;
        uint32_t WindowSize = 0;
        uint64_t DownloadSize = 0;
        std::string Name() const;
    };

    struct Part
    {
        size_t ChunkIndex = 0;
        uint32_t Offset = 0;
        uint32_t Size = 0;
    };

    struct File
    {
        std::filesystem::path Path;
        Hash Sha{};
        uint64_t Size = 0;
        std::vector<Part> Parts;
    };

    struct Manifest
    {
        std::string Build;
        std::string ChunkDirectory;
        std::vector<Chunk> Chunks;
        std::vector<File> Files;
        uint64_t DownloadSize = 0;
        uint64_t CacheSize = 0;
        uint64_t InstallSize = 0;
        static Manifest Parse(std::span<const unsigned char> Data, bool Studio = true);
    };

    Bytes DecodeChunk(std::span<const unsigned char> Data, const Chunk& Expected);
}
