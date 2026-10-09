#include "Mappings.h"

#include <cstdio>
#include <fstream>
#include <unordered_map>

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

    constexpr uint16 UsmapMagic = 0x30C4;

    class FBuffer
    {
    public:
        std::string Data;

        template<typename T>
        void Write(T Value)
        {
            Data.append(reinterpret_cast<const char*>(&Value), sizeof(T));
        }

        void WriteBytes(const std::string& Bytes)
        {
            Data.append(Bytes);
        }
    };

    class FNameTable
    {
    private:
        std::unordered_map<std::string, int32> Indices;

    public:
        FBuffer Buffer;
        uint32 Count = 0;

        int32 Add(const std::string& Name)
        {
            const auto [It, bInserted] = Indices.emplace(Name, static_cast<int32>(Count));
            if (bInserted)
            {
                Buffer.Write(static_cast<uint16>(Name.size()));
                Buffer.WriteBytes(Name);
                Count++;
            }
            return It->second;
        }
    };

    void WritePropertyType(const Dumper::PropertyType& Type, FBuffer& Out, FNameTable& Names)
    {
        using Dumper::EUsmapPropertyType;

        Out.Write(static_cast<uint8>(Type.Type));

        switch (Type.Type)
        {
        case EUsmapPropertyType::EnumProperty:
            if (Type.Inner)
                WritePropertyType(*Type.Inner, Out, Names);
            else
                Out.Write(static_cast<uint8>(EUsmapPropertyType::ByteProperty));
            Out.Write(Names.Add(Type.TypeName));
            break;

        case EUsmapPropertyType::StructProperty:
            Out.Write(Names.Add(Type.TypeName));
            break;

        case EUsmapPropertyType::ArrayProperty:
        case EUsmapPropertyType::SetProperty:
        case EUsmapPropertyType::OptionalProperty:
            WritePropertyType(*Type.Inner, Out, Names);
            break;

        case EUsmapPropertyType::MapProperty:
            WritePropertyType(*Type.Inner, Out, Names);
            WritePropertyType(*Type.Value, Out, Names);
            break;

        default:
            break;
        }
    }
}

bool Mappings::GenerateMappings() const
{
    FNameTable Names;
    FBuffer EnumData;
    FBuffer StructData;

    const auto& Enums = Dumper::GetEnums();
    const auto& Structs = Dumper::GetStructs();

    for (const Dumper::Enum& E : Enums)
    {
        EnumData.Write(Names.Add(E.Name));
        EnumData.Write(static_cast<uint16>(E.Values.size()));
        for (const auto& [Value, Name] : E.Values)
        {
            EnumData.Write(static_cast<int64>(Value));
            EnumData.Write(Names.Add(Name));
        }
    }

    for (const Dumper::Struct& S : Structs)
    {
        StructData.Write(Names.Add(S.Name));
        StructData.Write(S.SuperName.empty() ? static_cast<int32>(-1) : Names.Add(S.SuperName));

        uint16 PropertyCount = 0;
        for (const Dumper::Property& Prop : S.Properties)
            PropertyCount += Prop.ArrayDim;

        StructData.Write(PropertyCount);
        StructData.Write(static_cast<uint16>(S.Properties.size()));

        uint16 Index = 0;
        for (const Dumper::Property& Prop : S.Properties)
        {
            StructData.Write(Index);
            StructData.Write(static_cast<uint8>(Prop.ArrayDim));
            StructData.Write(Names.Add(Prop.Name));
            WritePropertyType(Prop.Type, StructData, Names);
            Index += Prop.ArrayDim;
        }
    }

    FBuffer Payload;
    Payload.Write(Names.Count);
    Payload.WriteBytes(Names.Buffer.Data);
    Payload.Write(static_cast<uint32>(Enums.size()));
    Payload.WriteBytes(EnumData.Data);
    Payload.Write(static_cast<uint32>(Structs.size()));
    Payload.WriteBytes(StructData.Data);

    std::string Compressed;
    std::string Error;
    if (!Compression::Compress(Payload.Data, Compressed, CompressionOpts, Error))
    {
        printf("[-] %s compression failed: %s\n", Compression::GetName(CompressionOpts.Method), Error.c_str());
        return false;
    }

    FBuffer File;
    File.Write(UsmapMagic);
    File.Write(EUsmapVersion::ExplicitEnumValues);
    File.Write(static_cast<int32>(0)); // bHasVersioning
    File.Write(CompressionOpts.Method);
    File.Write(static_cast<uint32>(Compressed.size()));
    File.Write(static_cast<uint32>(Payload.Data.size()));
    File.WriteBytes(Compressed);

    std::ofstream Stream(FilePath, std::ios::binary | std::ios::trunc);
    if (!Stream)
    {
        printf("[-] Failed to open %S for writing\n", FilePath.c_str());
        return false;
    }
    Stream.write(File.Data.data(), static_cast<std::streamsize>(File.Data.size()));

    printf("[+] Wrote %S (%s, %zu -> %zu bytes, %u names, %zu enums, %zu structs)\n", FilePath.c_str(), Compression::GetName(CompressionOpts.Method),
        Payload.Data.size(), File.Data.size(), Names.Count, Enums.size(), Structs.size());
    return true;
}
