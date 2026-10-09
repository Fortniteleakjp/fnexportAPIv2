#pragma once

#include "Compression.h"
#include "Dumper.h"

#include <string>

/*
* .usmap 書き出し (CUE4Parse / FModel 互換、Dumper-7 の MappingGenerator と同じ形式)
*
* Header:
*   uint16 Magic = 0x30C4
*   uint8  Version = ExplicitEnumValues (4)
*   int32  bHasVersioning = 0
*   uint8  CompressionMethod (0 = None, 1 = Oodle, 2 = Brotli, 3 = ZStandard)
*   uint32 CompressedSize
*   uint32 DecompressedSize
*
* Data:
*   uint32 NameCount; { uint16 Len; char[Len] }
*   uint32 EnumCount; { int32 Name; uint16 Count; { int64 Value; int32 Name } }
*   uint32 StructCount; { int32 Name; int32 Super; uint16 PropertyCount; uint16 SerializablePropertyCount;
*                         { uint16 Index; uint8 ArrayDim; int32 Name; PropertyType } }
*   PropertyType: uint8 Type;
*     EnumProperty     -> PropertyType Inner; int32 EnumName
*     StructProperty   -> int32 StructName
*     Array/Set/Optional -> PropertyType Inner
*     MapProperty      -> PropertyType Key; PropertyType Value
*/
class Mappings
{
private:
    std::wstring FilePath;
    CompressionOptions CompressionOpts;

public:
    Mappings(std::wstring InFilePath, CompressionOptions InCompression)
        : FilePath(std::move(InFilePath))
        , CompressionOpts(std::move(InCompression))
    {
    }

    bool GenerateMappings() const;
};
