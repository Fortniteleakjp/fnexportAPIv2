#include "ExtraMappings.h"

#include <cstring>
#include <fstream>
#include <iterator>
#include <stdexcept>

namespace
{
    enum class EUsmapVersion : uint8
    {
        Initial,
        PackageVersioning,
        LongFName,
        LargeEnums,
        ExplicitEnumValues,
    };

    class FReader
    {
    public:
        explicit FReader(const std::string& InData) : Data(InData) {}

        template<typename T>
        T Read()
        {
            if (Offset + sizeof(T) > Data.size())
                throw std::runtime_error("unexpected end of file");
            T Value;
            memcpy(&Value, Data.data() + Offset, sizeof(T));
            Offset += sizeof(T);
            return Value;
        }

        std::string ReadString(size_t Length)
        {
            if (Offset + Length > Data.size())
                throw std::runtime_error("unexpected end of file");
            std::string Value = Data.substr(Offset, Length);
            Offset += Length;
            return Value;
        }

    private:
        const std::string& Data;
        size_t Offset = 0;
    };

    const std::string& NameAt(const std::vector<std::string>& Names, int32 Index)
    {
        if (Index < 0 || static_cast<size_t>(Index) >= Names.size())
            throw std::runtime_error("name index out of range");
        return Names[Index];
    }

    void ReadPropertyType(FReader& Reader, const std::vector<std::string>& Names, Dumper::PropertyType& Out, int Depth = 0)
    {
        using Dumper::EUsmapPropertyType;
        if (Depth > 16)
            throw std::runtime_error("property type nested too deeply");

        Out.Type = static_cast<EUsmapPropertyType>(Reader.Read<uint8>());
        switch (Out.Type)
        {
        case EUsmapPropertyType::EnumProperty:
            Out.Inner = std::make_unique<Dumper::PropertyType>();
            ReadPropertyType(Reader, Names, *Out.Inner, Depth + 1);
            Out.TypeName = NameAt(Names, Reader.Read<int32>());
            break;
        case EUsmapPropertyType::StructProperty:
            Out.TypeName = NameAt(Names, Reader.Read<int32>());
            break;
        case EUsmapPropertyType::ArrayProperty:
        case EUsmapPropertyType::SetProperty:
        case EUsmapPropertyType::OptionalProperty:
            Out.Inner = std::make_unique<Dumper::PropertyType>();
            ReadPropertyType(Reader, Names, *Out.Inner, Depth + 1);
            break;
        case EUsmapPropertyType::MapProperty:
            Out.Inner = std::make_unique<Dumper::PropertyType>();
            ReadPropertyType(Reader, Names, *Out.Inner, Depth + 1);
            Out.Value = std::make_unique<Dumper::PropertyType>();
            ReadPropertyType(Reader, Names, *Out.Value, Depth + 1);
            break;
        default:
            break;
        }
    }
}

bool ExtraMappings::Load(const std::wstring& Path, std::vector<Dumper::Struct>& OutStructs, std::string& OutError)
{
    std::ifstream Stream(Path, std::ios::binary);
    if (!Stream)
    {
        OutError = "cannot open the file";
        return false;
    }
    const std::string Data((std::istreambuf_iterator<char>(Stream)), std::istreambuf_iterator<char>());

    try
    {
        FReader Header(Data);
        if (Header.Read<uint16>() != 0x30C4)
            throw std::runtime_error("not a .usmap file");
        const auto Version = static_cast<EUsmapVersion>(Header.Read<uint8>());
        if (Version > EUsmapVersion::ExplicitEnumValues)
            throw std::runtime_error("unsupported .usmap version");
        if (Version >= EUsmapVersion::PackageVersioning && Header.Read<int32>() != 0)
            throw std::runtime_error("package versioning is not supported");
        if (Header.Read<uint8>() != 0)
            throw std::runtime_error("only uncompressed files are supported");
        const uint32 CompressedSize = Header.Read<uint32>();
        const uint32 DecompressedSize = Header.Read<uint32>();
        const std::string Payload = Header.ReadString(CompressedSize);
        if (CompressedSize != DecompressedSize)
            throw std::runtime_error("size mismatch");

        FReader Reader(Payload);
        std::vector<std::string> Names(Reader.Read<uint32>());
        for (std::string& Name : Names)
        {
            const size_t Length = Version >= EUsmapVersion::LongFName ? Reader.Read<uint16>() : Reader.Read<uint8>();
            Name = Reader.ReadString(Length);
        }

        const uint32 EnumCount = Reader.Read<uint32>();
        for (uint32 i = 0; i < EnumCount; i++)
        {
            Reader.Read<int32>();
            const uint32 Count = Version >= EUsmapVersion::LargeEnums ? Reader.Read<uint16>() : Reader.Read<uint8>();
            for (uint32 j = 0; j < Count; j++)
            {
                if (Version >= EUsmapVersion::ExplicitEnumValues)
                    Reader.Read<int64>();
                Reader.Read<int32>();
            }
        }

        const uint32 StructCount = Reader.Read<uint32>();
        for (uint32 i = 0; i < StructCount; i++)
        {
            Dumper::Struct S;
            S.Name = NameAt(Names, Reader.Read<int32>());
            const int32 Super = Reader.Read<int32>();
            if (Super >= 0)
                S.SuperName = NameAt(Names, Super);
            const uint16 PropertyCount = Reader.Read<uint16>();
            const uint16 SerializableCount = Reader.Read<uint16>();

            /* 書き出し側は Index を宣言順に振り直すので、抜けている Index は無いものとして順に並べる */
            uint16 Expected = 0;
            for (uint16 j = 0; j < SerializableCount; j++)
            {
                Dumper::Property Prop;
                const uint16 Index = Reader.Read<uint16>();
                Prop.ArrayDim = Reader.Read<uint8>();
                if (Prop.ArrayDim == 0)
                    Prop.ArrayDim = 1;
                Prop.Name = NameAt(Names, Reader.Read<int32>());
                ReadPropertyType(Reader, Names, Prop.Type);
                if (Index != Expected)
                    throw std::runtime_error("struct " + S.Name + " has a gap in its property indices");
                Expected = static_cast<uint16>(Expected + Prop.ArrayDim);
                S.Properties.push_back(std::move(Prop));
            }
            if (Expected != PropertyCount)
                throw std::runtime_error("struct " + S.Name + " has a property count that doesn't match its properties");

            OutStructs.push_back(std::move(S));
        }
    }
    catch (const std::exception& Ex)
    {
        OutError = Ex.what();
        return false;
    }
    return true;
}
