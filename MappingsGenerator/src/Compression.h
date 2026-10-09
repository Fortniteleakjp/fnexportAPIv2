#pragma once

#include "Utils.h"

#include <string>
#include <string_view>

/* usmap ヘッダの CompressionMethod (CUE4Parse の EUsmapCompressionMethod と同じ値) */
enum class EUsmapCompression : uint8
{
    None = 0,
    Oodle = 1,
    Brotli = 2,
    ZStandard = 3,
};

struct CompressionOptions
{
    EUsmapCompression Method = EUsmapCompression::ZStandard;

    /* -1 = 方式ごとの既定値 (zstd: 19 / brotli: 11 / oodle: 7 = Optimal3) */
    int Level = -1;

    /* Oodle: oo2core_*_win64.dll のパス。空なら SearchDir から探す */
    std::wstring OodleDll;
    std::wstring SearchDir;
};

namespace Compression
{
    /* "none" / "zstd" ("zs") / "brotli" ("br") / "oodle" ("oo") */
    bool ParseMethod(std::wstring_view Name, EUsmapCompression& OutMethod);

    const char* GetName(EUsmapCompression Method);

    /* 慣例的なファイル名の接尾辞 ("_zs" / "_br" / "_oo"、無圧縮は "") */
    const wchar_t* GetFileSuffix(EUsmapCompression Method);

    /* 圧縮し、伸長して元に戻ることまで確認する。失敗時は OutError に理由 */
    bool Compress(const std::string& Raw, std::string& OutCompressed, const CompressionOptions& Options, std::string& OutError);
}
