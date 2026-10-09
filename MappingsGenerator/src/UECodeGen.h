#pragma once

/*
* UHT が生成する UECodeGen_Private::F*Params のメモリレイアウト。
* Engine/Source/Runtime/CoreUObject/Public/UObject/UObjectGlobals.h (ue6-main) を元に、
* UEFN (WITH_METADATA=1, UE 6.0) のバイナリで実測して確認したもの。
*/

#include "Utils.h"

#include <cstddef>

namespace UE
{
    enum class ETypeConstructPhase : uint8
    {
        Inner,
        Outer,
    };

    /* UObject* (*)(ETypeConstructPhase) / UClass* (*)() など、全部これで呼ぶ (余分な引数は無害) */
    using FTypeConstructFunc = void* (*)(ETypeConstructPhase);

    enum class EPropertyGenFlags : uint8
    {
        Byte = 0x00,
        Int8 = 0x01,
        Int16 = 0x02,
        Int = 0x03,
        Int64 = 0x04,
        UInt16 = 0x05,
        UInt32 = 0x06,
        UInt64 = 0x07,
        Float = 0x0A,
        Double = 0x0B,
        Bool = 0x0C,
        SoftClass = 0x0D,
        WeakObject = 0x0E,
        LazyObject = 0x0F,
        SoftObject = 0x10,
        Class = 0x11,
        Object = 0x12,
        Interface = 0x13,
        Name = 0x14,
        Str = 0x15,
        Array = 0x16,
        Map = 0x17,
        Set = 0x18,
        Struct = 0x19,
        Delegate = 0x1A,
        InlineMulticastDelegate = 0x1B,
        SparseMulticastDelegate = 0x1C,
        Text = 0x1D,
        Enum = 0x1E,
        FieldPath = 0x1F,
        LargeWorldCoordinatesReal = 0x20,
        Optional = 0x21,
        VerseValue = 0x22,
        Utf8Str = 0x23,
        AnsiStr = 0x24,
        VerseString = 0x25,
        VerseCell = 0x26,
        VerseType = 0x27,
        VerseLegacyValue = 0x28,
        VerseFunction = 0x29,

        TypeMask = 0x3F,
    };

    constexpr uint64 CPF_EditorOnly = 0x0000000800000000ull;

    struct FMetaDataPairParam
    {
        const char* NameUTF8;
        const char* ValueUTF8;
    };

    struct FEnumeratorParam
    {
        const char* NameUTF8;
        int64 Value;
    };

    /* 全 F*PropertyParams の共通先頭部 */
    struct FPropertyParamsBase
    {
        const char* NameUTF8;
        const char* RepNotifyFuncUTF8;
        uint64 PropertyFlags;      // EPropertyFlags
        EPropertyGenFlags Flags;
        void* SetterFunc;
        void* GetterFunc;
        uint16 ArrayDim;
    };

    /* Byte / Struct / Enum / Object / Delegate ... は Offset の後に関数ポインタを 1 つ持つ */
    struct FPropertyParamsWithFunc
    {
        const char* NameUTF8;
        const char* RepNotifyFuncUTF8;
        uint64 PropertyFlags;
        EPropertyGenFlags Flags;
        void* SetterFunc;
        void* GetterFunc;
        uint16 ArrayDim;
        uint16 Offset;
        FTypeConstructFunc TypeFunc; // EnumFunc / ScriptStructFunc / ClassFunc ...
    };

    struct FEnumParams
    {
        FTypeConstructFunc OuterFunc;
        void* DisplayNameFunc;
        const char* NameUTF8;
        const char* CppTypeUTF8;
        const FEnumeratorParam* EnumeratorParams;
        uint32 ObjectFlags;
        int16 NumEnumerators;
        uint8 EnumFlags;       // EEnumFlags
        uint8 CppForm;         // UEnum::ECppForm
        uint8 UnderlyingType;  // UEnum::EUnderlyingType
        uint16 NumMetaData;
        const FMetaDataPairParam* MetaDataArray;
    };

    enum class ECppForm : uint8
    {
        Regular,
        Namespaced,
        EnumClass,
    };

    constexpr uint8 EEnumFlags_Flags = 0x01;

    struct FStructParams
    {
        FTypeConstructFunc OuterFunc;
        FTypeConstructFunc SuperFunc;
        void* StructOpsFunc;
        const char* NameUTF8;
        const FPropertyParamsBase* const* PropertyArray;
        uint16 NumProperties;
        uint32 UnpaddedSizeOf : 24;
        uint32 AlignOf : 8;
        uint32 ObjectFlags;
        uint32 StructFlags;
        uint16 NumMetaData;
        const FMetaDataPairParam* MetaDataArray;
    };

    struct FClassParams
    {
        FTypeConstructFunc ClassNoRegisterFunc;
        const char* ClassConfigNameUTF8;
        const void* CppClassInfo;
        const FTypeConstructFunc* DependencySingletonFuncArray;
        const void* FunctionLinkArray;
        const FPropertyParamsBase* const* PropertyArray;
        const void* ImplementedInterfaceArray;
        uint32 NumDependencySingletons : 4;
        uint32 NumFunctions : 11;
        uint32 NumProperties : 11;
        uint32 NumImplementedInterfaces : 6;
        uint32 ClassFlags;
        uint16 NumMetaData;
        const FMetaDataPairParam* MetaDataArray;
    };

    static_assert(offsetof(FPropertyParamsBase, Flags) == 0x18);
    static_assert(offsetof(FPropertyParamsBase, ArrayDim) == 0x30);
    static_assert(offsetof(FPropertyParamsWithFunc, TypeFunc) == 0x38);
    static_assert(offsetof(FEnumParams, NumEnumerators) == 0x2C);
    static_assert(offsetof(FEnumParams, CppForm) == 0x2F);
    static_assert(sizeof(FEnumParams) == 0x40);
    static_assert(offsetof(FStructParams, NumProperties) == 0x28);
    static_assert(sizeof(FStructParams) == 0x48);
    static_assert(offsetof(FClassParams, ClassFlags) == 0x3C);
    static_assert(sizeof(FClassParams) == 0x50);
}
