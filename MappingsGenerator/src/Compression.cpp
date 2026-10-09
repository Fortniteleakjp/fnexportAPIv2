#include "Compression.h"

#include <brotli/decode.h>
#include <brotli/encode.h>
#include <zstd.h>

#include <cstdio>
#include <cwctype>

namespace
{


    constexpr int OodleLZ_Compressor_Kraken = 8;
    constexpr int OodleLZ_FuzzSafe_Yes = 1;
    constexpr int OodleLZ_CheckCRC_No = 0;
    constexpr int OodleLZ_Verbosity_None = 0;
    constexpr int OodleLZ_Decode_Unthreaded = 3;

    /* 2.5 (oo2core_5) と 2.9 (oo2core_9) のどちらでも呼べるよう末尾の引数まで渡す (x64 では余分な引数は無害) */
    using OodleLZ_CompressFn = intptr_t (*)(int Compressor, const void* RawBuf, intptr_t RawLen, void* CompBuf, int Level,
        const void* Options, const void* DictionaryBase, const void* Lrm, void* ScratchMem, intptr_t ScratchSize);
    using OodleLZ_DecompressFn = intptr_t (*)(const void* CompBuf, intptr_t CompBufSize, void* RawBuf, intptr_t RawLen,
        int FuzzSafe, int CheckCRC, int Verbosity, void* DecBufBase, intptr_t DecBufSize, void* FpCallback, void* CallbackUserData,
        void* DecoderMemory, intptr_t DecoderMemorySize, int ThreadPhase);

    std::wstring FindOodleDll(const std::wstring& Dir)
    {
        WIN32_FIND_DATAW Data;
        const HANDLE Find = FindFirstFileW((Dir + L"\\oo2core_*_win64.dll").c_str(), &Data);
        if (Find == INVALID_HANDLE_VALUE)
            return {};

        /* 一番新しいバージョン (oo2core_<N>_win64.dll の N が最大) を選ぶ */
        std::wstring Best;
        int BestVersion = -1;
        do
        {
            const int Version = _wtoi(Data.cFileName + wcslen(L"oo2core_"));
            if (Version > BestVersion)
            {
                BestVersion = Version;
                Best = Dir + L"\\" + Data.cFileName;
            }
        } while (FindNextFileW(Find, &Data));
        FindClose(Find);
        return Best;
    }

    bool CompressOodle(const std::string& Raw, std::string& Out, const CompressionOptions& Options, std::string& Error)
    {
        const std::wstring DllPath = !Options.OodleDll.empty() ? Options.OodleDll : FindOodleDll(Options.SearchDir);
        if (DllPath.empty())
        {
            Error = "oo2core_*_win64.dll not found (use --oodle <dll>)";
            return false;
        }

        const HMODULE Oodle = LoadLibraryW(DllPath.c_str());
        if (!Oodle)
        {
            Error = "failed to load " + Utils::WideToUtf8(DllPath);
            return false;
        }

        const auto CompressFn = reinterpret_cast<OodleLZ_CompressFn>(GetProcAddress(Oodle, "OodleLZ_Compress"));
        const auto DecompressFn = reinterpret_cast<OodleLZ_DecompressFn>(GetProcAddress(Oodle, "OodleLZ_Decompress"));
        if (!CompressFn || !DecompressFn)
        {
            Error = "OodleLZ_Compress / OodleLZ_Decompress not exported by " + Utils::WideToUtf8(DllPath);
            return false;
        }

        const int Level = Options.Level >= 0 ? Options.Level : 7; // OodleLZ_CompressionLevel_Optimal3
        printf("[+] Oodle: %S (Kraken, level %d)\n", DllPath.c_str(), Level);

        /* Kraken の最悪ケースは 256KB ごとに数百バイト増える程度 */
        Out.resize(Raw.size() + Raw.size() / 16 + 0x10000);
        const intptr_t Size = CompressFn(OodleLZ_Compressor_Kraken, Raw.data(), static_cast<intptr_t>(Raw.size()), Out.data(), Level,
            nullptr, nullptr, nullptr, nullptr, 0);
        if (Size <= 0)
        {
            Error = "OodleLZ_Compress failed";
            return false;
        }
        Out.resize(static_cast<size_t>(Size));

        std::string Check(Raw.size(), '\0');
        const intptr_t Decoded = DecompressFn(Out.data(), static_cast<intptr_t>(Out.size()), Check.data(), static_cast<intptr_t>(Check.size()),
            OodleLZ_FuzzSafe_Yes, OodleLZ_CheckCRC_No, OodleLZ_Verbosity_None, nullptr, 0, nullptr, nullptr, nullptr, 0, OodleLZ_Decode_Unthreaded);
        if (Decoded != static_cast<intptr_t>(Raw.size()) || Check != Raw)
        {
            Error = "Oodle round-trip check failed";
            return false;
        }
        return true;
    }


    bool CompressZstd(const std::string& Raw, std::string& Out, const CompressionOptions& Options, std::string& Error)
    {
        const int Level = Options.Level >= 0 ? Options.Level : 19;
        if (Level < 1 || Level > ZSTD_maxCLevel())
        {
            Error = "zstd level must be 1-" + std::to_string(ZSTD_maxCLevel());
            return false;
        }

        Out.resize(ZSTD_compressBound(Raw.size()));
        const size_t Size = ZSTD_compress(Out.data(), Out.size(), Raw.data(), Raw.size(), Level);
        if (ZSTD_isError(Size))
        {
            Error = std::string("ZSTD_compress: ") + ZSTD_getErrorName(Size);
            return false;
        }
        Out.resize(Size);

        std::string Check(Raw.size(), '\0');
        const size_t Decoded = ZSTD_decompress(Check.data(), Check.size(), Out.data(), Out.size());
        if (ZSTD_isError(Decoded) || Decoded != Raw.size() || Check != Raw)
        {
            Error = "zstd round-trip check failed";
            return false;
        }
        return true;
    }


    bool CompressBrotli(const std::string& Raw, std::string& Out, const CompressionOptions& Options, std::string& Error)
    {
        const int Level = Options.Level >= 0 ? Options.Level : BROTLI_MAX_QUALITY;
        if (Level > BROTLI_MAX_QUALITY)
        {
            Error = "brotli level must be 0-11";
            return false;
        }

        size_t Size = BrotliEncoderMaxCompressedSize(Raw.size());
        Out.resize(Size);
        if (!BrotliEncoderCompress(Level, BROTLI_MAX_WINDOW_BITS, BROTLI_MODE_GENERIC, Raw.size(), reinterpret_cast<const uint8_t*>(Raw.data()),
            &Size, reinterpret_cast<uint8_t*>(Out.data())))
        {
            Error = "BrotliEncoderCompress failed";
            return false;
        }
        Out.resize(Size);

        std::string Check(Raw.size(), '\0');
        size_t Decoded = Check.size();
        if (BrotliDecoderDecompress(Out.size(), reinterpret_cast<const uint8_t*>(Out.data()), &Decoded, reinterpret_cast<uint8_t*>(Check.data())) != BROTLI_DECODER_RESULT_SUCCESS
            || Decoded != Raw.size() || Check != Raw)
        {
            Error = "brotli round-trip check failed";
            return false;
        }
        return true;
    }
}

bool Compression::ParseMethod(std::wstring_view Name, EUsmapCompression& OutMethod)
{
    std::wstring Lower(Name);
    for (wchar_t& C : Lower)
        C = static_cast<wchar_t>(towlower(C));

    if (Lower == L"none" || Lower == L"raw") OutMethod = EUsmapCompression::None;
    else if (Lower == L"zstd" || Lower == L"zs" || Lower == L"zstandard") OutMethod = EUsmapCompression::ZStandard;
    else if (Lower == L"brotli" || Lower == L"br") OutMethod = EUsmapCompression::Brotli;
    else if (Lower == L"oodle" || Lower == L"oo") OutMethod = EUsmapCompression::Oodle;
    else return false;
    return true;
}

const char* Compression::GetName(EUsmapCompression Method)
{
    switch (Method)
    {
    case EUsmapCompression::Oodle: return "Oodle";
    case EUsmapCompression::Brotli: return "Brotli";
    case EUsmapCompression::ZStandard: return "ZStandard";
    default: return "None";
    }
}

const wchar_t* Compression::GetFileSuffix(EUsmapCompression Method)
{
    switch (Method)
    {
    case EUsmapCompression::Oodle: return L"_oo";
    case EUsmapCompression::Brotli: return L"_br";
    case EUsmapCompression::ZStandard: return L"_zs";
    default: return L"";
    }
}

bool Compression::Compress(const std::string& Raw, std::string& OutCompressed, const CompressionOptions& Options, std::string& OutError)
{
    switch (Options.Method)
    {
    case EUsmapCompression::None:
        OutCompressed = Raw;
        return true;
    case EUsmapCompression::Oodle:
        return CompressOodle(Raw, OutCompressed, Options, OutError);
    case EUsmapCompression::Brotli:
        return CompressBrotli(Raw, OutCompressed, Options, OutError);
    case EUsmapCompression::ZStandard:
        return CompressZstd(Raw, OutCompressed, Options, OutError);
    }
    OutError = "unknown compression method";
    return false;
}
