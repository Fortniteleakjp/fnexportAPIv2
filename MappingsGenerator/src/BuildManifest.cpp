#include "BuildManifest.h"
#include "Utils.h"

#include <bcrypt.h>
#include <zlib.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <fstream>
#include <limits>
#include <set>
#include <stdexcept>
#include <unordered_map>

namespace
{
    using namespace BuildPatch;

    class Reader
    {
    public:
        explicit Reader(std::span<const unsigned char> Bytes) : Data(Bytes) {}

        template<class T> T Read()
        {
            T Value{};
            const auto Bytes = Take(sizeof(T));
            memcpy(&Value, Bytes.data(), sizeof(T));
            return Value;
        }

        std::span<const unsigned char> Take(size_t Size)
        {
            if (Size > Data.size() - Position) throw std::runtime_error("Truncated manifest or chunk.");
            const auto Result = Data.subspan(Position, Size);
            Position += Size;
            return Result;
        }

        Reader Section()
        {
            const auto Size = Read<uint32_t>();
            if (Size < 5) throw std::runtime_error("Invalid manifest section length.");
            return Reader(Take(Size - 4));
        }

        uint32_t Count()
        {
            const auto Value = Read<uint32_t>();
            if (Value > 1000000) throw std::runtime_error("Invalid manifest array length.");
            return Value;
        }

        std::string String()
        {
            const auto Length = Read<int32_t>();
            if (Length == 0) return {};
            if (Length > 1048576 || Length < -1048576) throw std::runtime_error("Invalid manifest string length.");
            if (Length > 0)
            {
                const auto Bytes = Take(Length);
                if (Bytes.back() != 0) throw std::runtime_error("Unterminated manifest string.");
                return std::string(reinterpret_cast<const char*>(Bytes.data()), Bytes.size() - 1);
            }
            const auto Bytes = Take(static_cast<size_t>(-Length) * sizeof(wchar_t));
            std::wstring Wide(static_cast<size_t>(-Length), L'\0');
            memcpy(Wide.data(), Bytes.data(), Bytes.size());
            if (Wide.back() != 0) throw std::runtime_error("Unterminated manifest string.");
            Wide.pop_back();
            return Utils::WideToUtf8(Wide);
        }

        void Strings()
        {
            const auto Size = Count();
            for (uint32_t Index = 0; Index < Size; ++Index) String();
        }

        size_t Remaining() const { return Data.size() - Position; }

    private:
        std::span<const unsigned char> Data;
        size_t Position = 0;
    };

    Bytes Inflate(std::span<const unsigned char> Input, uint32_t Size)
    {
        Bytes Output(Size);
        uLongf Length = Size;
        if (uncompress(Output.data(), &Length, Input.data(), static_cast<uLong>(Input.size())) != Z_OK || Length != Size)
            throw std::runtime_error("Failed to decompress manifest or chunk.");
        return Output;
    }

    BCRYPT_ALG_HANDLE ShaProvider()
    {
        struct Provider
        {
            BCRYPT_ALG_HANDLE Handle = nullptr;
            Provider()
            {
                if (BCryptOpenAlgorithmProvider(&Handle, BCRYPT_SHA1_ALGORITHM, nullptr, 0) < 0)
                    throw std::runtime_error("Could not initialize SHA-1 verification.");
            }
            ~Provider() { BCryptCloseAlgorithmProvider(Handle, 0); }
        };
        static Provider Instance;
        return Instance.Handle;
    }

    std::filesystem::path RelativePath(const std::string& Name)
    {
        if (Name.empty() || Name.find('\0') != std::string::npos || Name.find(':') != std::string::npos)
            throw std::runtime_error("Invalid manifest file path.");
        std::filesystem::path Path(Utils::Utf8ToWide(Name));
        Path.make_preferred();
        if (Path.is_absolute() || Path.has_root_path()) throw std::runtime_error("Manifest file path must be relative.");
        for (const auto& Element : Path)
        {
            const auto Text = Element.wstring();
            if (Text == L".." || Text == L"." || Text.empty() || Text.back() == L'.' || Text.back() == L' ' ||
                Text.find_first_of(L"<>\"|?*") != std::wstring::npos)
                throw std::runtime_error("Invalid manifest file path component.");
        }
        return Path;
    }
}

namespace BuildPatch
{
    Sha1::Sha1()
    {
        BCRYPT_HASH_HANDLE HashHandle = nullptr;
        if (BCryptCreateHash(ShaProvider(), &HashHandle, nullptr, 0, nullptr, 0, 0) < 0)
            throw std::runtime_error("Could not create SHA-1 hash.");
        Handle = HashHandle;
    }

    Sha1::~Sha1() { if (Handle) BCryptDestroyHash(Handle); }

    void Sha1::Update(std::span<const unsigned char> Data)
    {
        if (Data.size() > std::numeric_limits<ULONG>::max() || BCryptHashData(Handle, const_cast<PUCHAR>(Data.data()), static_cast<ULONG>(Data.size()), 0) < 0)
            throw std::runtime_error("SHA-1 verification failed.");
    }

    Hash Sha1::Finish()
    {
        Hash Result{};
        if (BCryptFinishHash(Handle, Result.data(), static_cast<ULONG>(Result.size()), 0) < 0)
            throw std::runtime_error("SHA-1 verification failed.");
        return Result;
    }

    Hash Digest(std::span<const unsigned char> Data)
    {
        Sha1 Hasher;
        Hasher.Update(Data);
        return Hasher.Finish();
    }

    bool VerifyFile(const std::filesystem::path& Path, uint64_t Size, const Hash& Expected)
    {
        std::error_code Error;
        if (std::filesystem::file_size(Path, Error) != Size || Error) return false;
        std::ifstream Stream(Path, std::ios::binary);
        if (!Stream) return false;
        Sha1 Hasher;
        std::array<unsigned char, 65536> Buffer{};
        uint64_t Read = 0;
        while (Stream)
        {
            Stream.read(reinterpret_cast<char*>(Buffer.data()), Buffer.size());
            const auto Count = static_cast<size_t>(Stream.gcount());
            Hasher.Update(std::span(Buffer).first(Count));
            Read += Count;
        }
        return Stream.eof() && Read == Size && Hasher.Finish() == Expected;
    }

    Bytes ReadFile(const std::filesystem::path& Path, uint64_t Maximum)
    {
        const auto Size = std::filesystem::file_size(Path);
        if (Size > Maximum) throw std::runtime_error("File exceeds its expected size: " + Utils::WideToUtf8(Path.wstring()));
        Bytes Data(static_cast<size_t>(Size));
        std::ifstream Stream(Path, std::ios::binary);
        Stream.read(reinterpret_cast<char*>(Data.data()), Data.size());
        if (!Stream) throw std::runtime_error("Could not read " + Utils::WideToUtf8(Path.wstring()));
        return Data;
    }

    std::string Chunk::Name() const
    {
        char Buffer[64];
        snprintf(Buffer, sizeof(Buffer), "%016llX_%08X%08X%08X%08X.chunk",
            static_cast<unsigned long long>(RollingHash), Guid[0], Guid[1], Guid[2], Guid[3]);
        return Buffer;
    }

    Manifest Manifest::Parse(std::span<const unsigned char> Data, bool Studio)
    {
        Reader Header(Data);
        if (Header.Read<uint32_t>() != 0x44BEC00C) throw std::runtime_error("Invalid manifest magic.");
        const auto HeaderSize = Header.Read<uint32_t>();
        const auto RawSize = Header.Read<uint32_t>();
        const auto StoredSize = Header.Read<uint32_t>();
        const auto ExpectedHash = Header.Read<Hash>();
        const auto Flags = Header.Read<unsigned char>();
        if (HeaderSize < 41 || HeaderSize > Data.size() || RawSize > 128 * 1024 * 1024 ||
            StoredSize != Data.size() - HeaderSize || Header.Read<uint32_t>() < 14 || Flags > 1)
            throw std::runtime_error("Unsupported or invalid binary manifest header.");
        const auto Payload = Data.subspan(HeaderSize);
        Bytes Raw = Flags == 1 ? Inflate(Payload, RawSize) : Bytes(Payload.begin(), Payload.end());
        if (Raw.size() != RawSize || Digest(Raw) != ExpectedHash) throw std::runtime_error("Manifest SHA-1 mismatch.");

        Manifest Result;
        Reader Body(Raw);
        auto Meta = Body.Section();
        const auto MetaVersion = Meta.Read<unsigned char>();
        if (MetaVersion > 2) throw std::runtime_error("Unsupported manifest metadata version.");
        const auto Feature = Meta.Read<uint32_t>();
        if (Meta.Read<unsigned char>() != 0) throw std::runtime_error("File-data manifests are not supported.");
        Meta.Read<uint32_t>();
        const auto App = Meta.String();
        if (Studio ? (App != "Fortnite_Studio" && App != "FortniteReleaseBuilds_Studio") :
            (App != "Fortnite" && App != "FortniteReleaseBuilds"))
            throw std::runtime_error("Unexpected manifest application: " + App);
        Result.Build = Meta.String();
        Result.ChunkDirectory = Feature >= 15 ? "ChunksV4" : Feature >= 6 ? "ChunksV3" : "ChunksV2";

        auto Chunks = Body.Section();
        if (Chunks.Read<unsigned char>() != 0) throw std::runtime_error("Unsupported chunk-list version.");
        const auto ChunkCount = Chunks.Count();
        if (static_cast<uint64_t>(ChunkCount) * 57 != Chunks.Remaining()) throw std::runtime_error("Invalid chunk-list length.");
        Result.Chunks.resize(ChunkCount);
        std::unordered_map<std::string, size_t> ByGuid;
        for (size_t Index = 0; Index < ChunkCount; ++Index)
        {
            auto& Guid = Result.Chunks[Index].Guid;
            Guid = Chunks.Read<std::array<uint32_t, 4>>();
            if (!ByGuid.emplace(std::string(reinterpret_cast<const char*>(Guid.data()), sizeof(Guid)), Index).second)
                throw std::runtime_error("Duplicate chunk GUID.");
        }
        for (auto& Chunk : Result.Chunks) Chunk.RollingHash = Chunks.Read<uint64_t>();
        for (auto& Chunk : Result.Chunks) Chunk.Sha = Chunks.Read<Hash>();
        for (auto& Chunk : Result.Chunks) Chunk.Group = Chunks.Read<unsigned char>();
        for (auto& Chunk : Result.Chunks) Chunk.WindowSize = Chunks.Read<uint32_t>();
        for (auto& Chunk : Result.Chunks)
        {
            Chunk.DownloadSize = Chunks.Read<uint64_t>();
            if (Chunk.WindowSize == 0 || Chunk.WindowSize > 64 * 1024 * 1024 ||
                Chunk.DownloadSize < 41 || Chunk.DownloadSize > 64 * 1024 * 1024)
                throw std::runtime_error("Invalid chunk size.");
            Result.CacheSize += Chunk.WindowSize;
            Result.DownloadSize += Chunk.DownloadSize;
        }

        auto Files = Body.Section();
        if (Files.Read<unsigned char>() > 2) throw std::runtime_error("Unsupported file-list version.");
        Result.Files.resize(Files.Count());
        std::set<std::wstring> Paths;
        for (auto& File : Result.Files)
        {
            File.Path = RelativePath(Files.String());
            auto Key = File.Path.wstring();
            std::transform(Key.begin(), Key.end(), Key.begin(), towlower);
            if (!Paths.insert(Key).second) throw std::runtime_error("Duplicate manifest file path.");
        }
        for (size_t Index = 0; Index < Result.Files.size(); ++Index)
            if (!Files.String().empty()) throw std::runtime_error("Symlinks are not supported in Windows Studio manifests.");
        for (auto& File : Result.Files) File.Sha = Files.Read<Hash>();
        Files.Take(Result.Files.size());
        for (size_t Index = 0; Index < Result.Files.size(); ++Index) Files.Strings();
        for (auto& File : Result.Files)
        {
            const auto PartCount = Files.Count();
            File.Parts.reserve(PartCount);
            for (uint32_t Index = 0; Index < PartCount; ++Index)
            {
                auto PartData = Files.Section();
                const auto Guid = PartData.Read<std::array<uint32_t, 4>>();
                const auto Match = ByGuid.find(std::string(reinterpret_cast<const char*>(Guid.data()), sizeof(Guid)));
                if (Match == ByGuid.end()) throw std::runtime_error("File references an unknown chunk.");
                Part Part{ Match->second, PartData.Read<uint32_t>(), PartData.Read<uint32_t>() };
                const auto Size = Result.Chunks[Part.ChunkIndex].WindowSize;
                if (Part.Offset > Size || Part.Size > Size - Part.Offset) throw std::runtime_error("File part exceeds its chunk.");
                File.Size += Part.Size;
                File.Parts.push_back(Part);
            }
            Result.InstallSize += File.Size;
        }
        if (Result.Chunks.empty() || Result.Files.empty()) throw std::runtime_error("Empty Studio manifest.");
        return Result;
    }

    Bytes DecodeChunk(std::span<const unsigned char> Data, const Chunk& Expected)
    {
        Reader Header(Data);
        if (Header.Read<uint32_t>() != 0xB1FE3AA2) throw std::runtime_error("Invalid chunk magic.");
        const auto Version = Header.Read<uint32_t>();
        const auto HeaderSize = Header.Read<uint32_t>();
        const auto StoredSize = Header.Read<uint32_t>();
        const auto Guid = Header.Read<std::array<uint32_t, 4>>();
        const auto Rolling = Header.Read<uint64_t>();
        const auto Flags = Header.Read<unsigned char>();
        const auto Sha = Version >= 2 ? Header.Read<Hash>() : Expected.Sha;
        if (Version >= 2) Header.Read<unsigned char>();
        const auto RawSize = Version >= 3 ? Header.Read<uint32_t>() : 1048576;
        const uint32_t MinimumHeader = Version == 1 ? 41 : Version == 2 ? 62 : 66;
        if (Version < 1 || Version > 3 || HeaderSize < MinimumHeader || HeaderSize > Data.size() ||
            StoredSize != Data.size() - HeaderSize || Flags > 1 || Guid != Expected.Guid ||
            Rolling != Expected.RollingHash || RawSize != Expected.WindowSize || Sha != Expected.Sha)
            throw std::runtime_error("Chunk header does not match the manifest.");
        const auto Payload = Data.subspan(HeaderSize);
        Bytes Raw = Flags == 1 ? Inflate(Payload, RawSize) : Bytes(Payload.begin(), Payload.end());
        if (Raw.size() != RawSize || Digest(Raw) != Expected.Sha) throw std::runtime_error("Chunk SHA-1 mismatch.");
        return Raw;
    }
}
