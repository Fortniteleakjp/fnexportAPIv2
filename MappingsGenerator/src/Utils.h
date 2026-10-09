#pragma once

#include <Windows.h>

#include <cstdint>
#include <string>
#include <string_view>

using uint8 = std::uint8_t;
using uint16 = std::uint16_t;
using uint32 = std::uint32_t;
using uint64 = std::uint64_t;
using int8 = std::int8_t;
using int16 = std::int16_t;
using int32 = std::int32_t;
using int64 = std::int64_t;

namespace Utils
{
    inline std::string WideToUtf8(std::wstring_view Str)
    {
        if (Str.empty())
            return {};

        const int Len = WideCharToMultiByte(CP_UTF8, 0, Str.data(), static_cast<int>(Str.size()), nullptr, 0, nullptr, nullptr);
        std::string Out(Len, '\0');
        WideCharToMultiByte(CP_UTF8, 0, Str.data(), static_cast<int>(Str.size()), Out.data(), Len, nullptr, nullptr);
        return Out;
    }

    inline std::wstring Utf8ToWide(std::string_view Str)
    {
        if (Str.empty())
            return {};

        const int Len = MultiByteToWideChar(CP_UTF8, 0, Str.data(), static_cast<int>(Str.size()), nullptr, 0);
        std::wstring Out(Len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, Str.data(), static_cast<int>(Str.size()), Out.data(), Len);
        return Out;
    }

    /* "EFoo::Bar" -> "Bar" (usmap は列挙子名を短い形で持つ) */
    inline std::string_view StripEnumPrefix(std::string_view Name)
    {
        const size_t Pos = Name.rfind("::");
        return Pos == std::string_view::npos ? Name : Name.substr(Pos + 2);
    }
}
