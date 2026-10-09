#pragma once

#include "PE.h"
#include "Utils.h"

#include <memory>
#include <string>
#include <vector>

namespace Dumper
{
    /* usmap 内のプロパティ型 (CUE4Parse の EPropertyType と同じ並び) */
    enum class EUsmapPropertyType : uint8
    {
        ByteProperty,
        BoolProperty,
        IntProperty,
        FloatProperty,
        ObjectProperty,
        NameProperty,
        DelegateProperty,
        DoubleProperty,
        ArrayProperty,
        StructProperty,
        StrProperty,
        TextProperty,
        InterfaceProperty,
        MulticastDelegateProperty,
        WeakObjectProperty,
        LazyObjectProperty,
        AssetObjectProperty,
        SoftObjectProperty,
        UInt64Property,
        UInt32Property,
        UInt16Property,
        Int64Property,
        Int16Property,
        Int8Property,
        MapProperty,
        SetProperty,
        EnumProperty,
        FieldPathProperty,
        OptionalProperty,
        Utf8StrProperty,
        AnsiStrProperty,

        Unknown = 0xFF,
    };

    struct PropertyType
    {
        EUsmapPropertyType Type = EUsmapPropertyType::Unknown;
        std::string TypeName; // StructProperty: 構造体名 / EnumProperty: 列挙型名
        std::unique_ptr<PropertyType> Inner; // Array/Set/Optional の要素, Map のキー, Enum の基底型
        std::unique_ptr<PropertyType> Value; // Map の値
    };

    struct Property
    {
        std::string Name;
        uint16 ArrayDim = 1;
        uint64 PropertyFlags = 0;
        PropertyType Type;
    };

    struct Struct
    {
        std::string Name;
        std::string SuperName; // 空 = 親なし
        std::vector<Property> Properties;
        bool bIsClass = false;
        bool bIsPlaceholder = false; // 親クラスとして参照されただけで FClassParams が見つからなかったもの
    };

    struct Enum
    {
        std::string Name;
        std::vector<std::pair<int64, std::string>> Values;
    };

    struct Options
    {
        bool bIncludeEditorOnly = false;
        bool bVerbose = false;
    };

    /* CoreUObject を含むモジュールの UECodeGen_Private::Construct* / GetPrivateStaticClassBody をフックする */
    bool SetupHooks(const PE& CoreUObjectModule);

    /* モジュール内の Construct* 呼び出しを走査し、F*Params を収集する */
    void DumpModule(const PE& Module);

    /* 収集した Params から型情報を組み立てる (この中で Z_Construct_* 関数をフック下で呼び出す) */
    void Resolve(const Options& Opts);

    void PrintInfo();

    const std::vector<Struct>& GetStructs();
    const std::vector<Enum>& GetEnums();
}
