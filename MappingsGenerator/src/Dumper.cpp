#include "Dumper.h"

#include "Hook.h"
#include "UECodeGen.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <deque>
#include <unordered_map>
#include <unordered_set>

using namespace UE;

namespace
{
    enum class ETokenKind : uint32
    {
        Class,      // GetPrivateStaticClassBody (= Z_Construct_UClass_X(Inner)) が作るはずだった UClass / intrinsic クラス
        ClassOuter, // ConstructUClass が作るはずだった UClass (Outer フェーズは呼ばないので通常は使われない)
        Struct,
        Enum,
    };

    /* フックが本物の UClass* / UScriptStruct* / UEnum* の代わりに返す偽オブジェクト */
    struct FToken
    {
        static constexpr uint64 MagicValue = 0x4E454B4F5450414Dull; // "MAPTOKEN"

        uint64 Magic = MagicValue;
        ETokenKind Kind = ETokenKind::Class;
        const void* Params = nullptr;

        /* ETokenKind::Class 用 */
        std::wstring Name;
        std::wstring Package;
        FTypeConstructFunc SuperClassFunc = nullptr;

        /* 呼び出し元が偽オブジェクトを読んでも落ちにくいように余白を取っておく */
        uint8 Padding[0x400] = {};
    };

    struct FStats
    {
        size_t CallSites[3] = {};
        size_t UnresolvedCallSites[3] = {};
        size_t FailedClassRegistrations = 0;
        size_t UnresolvedStructRefs = 0;
        size_t UnresolvedEnumRefs = 0;
        size_t UnknownPropertyTypes = 0;
        size_t SkippedEditorOnly = 0;
        size_t DuplicateNames = 0;
        size_t BrokenPropertyArrays = 0;
        size_t RealObjects = 0;
        size_t UnresolvedRealObjects = 0;
        size_t IntrinsicClasses = 0;
        size_t GeneratedEnumMax = 0;
    };

    std::vector<const PE*> Modules;
    bool bVerboseLog = false;

    uintptr_t ConstructUClassAddress = 0;
    uintptr_t ConstructUScriptStructAddress = 0;
    uintptr_t ConstructUEnumAddress = 0;
    uintptr_t GetPrivateStaticClassBodyAddress = 0;

    std::deque<FToken> Tokens; // deque なので要素のアドレスは不変
    std::unordered_set<const void*> TokenSet;
    std::unordered_map<const void*, FToken*> TokenByKey;

    std::vector<const FClassParams*> ClassParamsList;
    std::vector<const FStructParams*> StructParamsList;
    std::vector<const FEnumParams*> EnumParamsList;
    std::unordered_set<const void*> KnownParams;

    std::vector<Dumper::Struct> Structs;
    std::vector<Dumper::Enum> Enums;

    FStats Stats;


    bool IsInAnyModuleData(const void* Ptr)
    {
        const uintptr_t Address = reinterpret_cast<uintptr_t>(Ptr);
        for (const PE* Module : Modules)
        {
            if (Module->IsInData(Address))
                return true;
        }
        return false;
    }

    bool IsInAnyModuleCode(const void* Ptr)
    {
        const uintptr_t Address = reinterpret_cast<uintptr_t>(Ptr);
        for (const PE* Module : Modules)
        {
            if (Module->IsInCode(Address))
                return true;
        }
        return false;
    }

    bool IsValidName(const char* Str)
    {
        if (!IsInAnyModuleData(Str))
            return false;

        for (int i = 0; i < 1024; i++)
        {
            const uint8 C = static_cast<uint8>(Str[i]);
            if (C == 0)
                return i > 0;
            if (C < 0x20 || C == 0x7F)
                return false;
        }
        return false;
    }

    bool IsValidPropertyArray(const FPropertyParamsBase* const* Array, uint32 Num)
    {
        if (Num == 0)
            return true;

        if (!IsInAnyModuleData(Array) || !IsInAnyModuleData(Array + Num - 1))
            return false;

        for (const uint32 Index : { 0u, Num - 1 })
        {
            const FPropertyParamsBase* Prop = Array[Index];
            if (!IsInAnyModuleData(Prop) || !IsValidName(Prop->NameUTF8))
                return false;
        }
        return true;
    }

    bool IsValidClassParams(const FClassParams* Params)
    {
        return IsInAnyModuleData(Params)
            && IsInAnyModuleCode(reinterpret_cast<const void*>(Params->ClassNoRegisterFunc))
            && (Params->NumDependencySingletons == 0 || IsInAnyModuleData(Params->DependencySingletonFuncArray))
            && IsValidPropertyArray(Params->PropertyArray, Params->NumProperties);
    }

    bool IsValidStructParams(const FStructParams* Params)
    {
        return IsInAnyModuleData(Params)
            && IsValidName(Params->NameUTF8)
            && IsValidPropertyArray(Params->PropertyArray, Params->NumProperties);
    }

    bool IsValidEnumParams(const FEnumParams* Params)
    {
        return IsInAnyModuleData(Params)
            && IsValidName(Params->NameUTF8)
            && Params->NumEnumerators >= 0
            && (Params->NumEnumerators == 0 || (IsInAnyModuleData(Params->EnumeratorParams) && IsValidName(Params->EnumeratorParams[0].NameUTF8)));
    }


    FToken* GetOrCreateToken(const void* Key, ETokenKind Kind, const void* Params)
    {
        if (const auto It = TokenByKey.find(Key); It != TokenByKey.end())
            return It->second;

        FToken& Token = Tokens.emplace_back();
        Token.Kind = Kind;
        Token.Params = Params;
        TokenSet.insert(&Token);
        TokenByKey.emplace(Key, &Token);
        return &Token;
    }

    FToken* AsToken(void* Ptr)
    {
        return TokenSet.contains(Ptr) ? static_cast<FToken*>(Ptr) : nullptr;
    }

    void AddStructParams(const FStructParams* Params)
    {
        if (KnownParams.insert(Params).second)
            StructParamsList.push_back(Params);
    }

    void AddEnumParams(const FEnumParams* Params)
    {
        if (KnownParams.insert(Params).second)
            EnumParamsList.push_back(Params);
    }

    void AddClassParams(const FClassParams* Params)
    {
        if (KnownParams.insert(Params).second)
            ClassParamsList.push_back(Params);
    }


    void Hook_ConstructUClass(FToken*& OutClass, const FClassParams& Params)
    {
        if (!OutClass)
            OutClass = GetOrCreateToken(&Params, ETokenKind::ClassOuter, &Params);
    }

    void Hook_ConstructUScriptStruct(FToken*& OutStruct, const FStructParams& Params)
    {
        if (!OutStruct)
            OutStruct = GetOrCreateToken(&Params, ETokenKind::Struct, &Params);
        AddStructParams(&Params);
    }

    void Hook_ConstructUEnum(FToken*& OutEnum, const FEnumParams& Params)
    {
        if (!OutEnum)
            OutEnum = GetOrCreateToken(&Params, ETokenKind::Enum, &Params);
        AddEnumParams(&Params);
    }

    /*
    * void GetPrivateStaticClassBody(const TCHAR* PackageName, const TCHAR* Name, UClass*& ReturnClass, void(*RegisterNativeFunc)(),
    *     uint32 InSize, uint32 InAlignment, EClassFlags InClassFlags, EClassCastFlags InClassCastFlags, const TCHAR* InConfigName,
    *     ClassConstructorType, ClassVTableHelperCtorCallerType, FUObjectCppClassStaticFunctions&&,
    *     UClass*(*InSuperClassFn)(), UClass*(*InWithinClassFn)())
    */
    void Hook_GetPrivateStaticClassBody(const wchar_t* PackageName, const wchar_t* Name, FToken*& ReturnClass, void*, uint32, uint32, uint32, uint64,
        const wchar_t*, void*, void*, void*, FTypeConstructFunc InSuperClassFn, FTypeConstructFunc)
    {
        if (ReturnClass)
            return;

        FToken* Token = GetOrCreateToken(&ReturnClass, ETokenKind::Class, nullptr);
        Token->Name = Name ? Name : L"";
        Token->Package = PackageName ? PackageName : L"";
        Token->SuperClassFunc = InSuperClassFn;
        ReturnClass = Token;
    }

    /* Z_Construct_* を Inner フェーズで呼ぶ。フック済みなので UObject は作られず、FToken* が返る */
    void* SafeConstruct(FTypeConstructFunc Func)
    {
        __try
        {
            return Func(ETypeConstructPhase::Inner);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }
    }

    std::unordered_map<const void*, void*> TokenByRealObject;

    /* `jmp rel32` だけの thunk は辿る */
    const uint8* SkipJumpThunks(const void* Func)
    {
        const uint8* Code = static_cast<const uint8*>(Func);
        for (int i = 0; i < 4 && Code[0] == 0xE9; i++)
        {
            int32 Rel;
            memcpy(&Rel, Code + 1, sizeof(Rel));
            Code = Code + 5 + Rel;
        }
        return Code;
    }

    /* .pdata から関数の範囲を得る (無ければ先頭 0x200 バイト) */
    std::pair<const uint8*, const uint8*> GetFunctionRange(const uint8* Code)
    {
        DWORD64 ImageBase = 0;
        if (const PRUNTIME_FUNCTION Entry = RtlLookupFunctionEntry(reinterpret_cast<DWORD64>(Code), &ImageBase, nullptr))
            return { reinterpret_cast<const uint8*>(ImageBase + Entry->BeginAddress), reinterpret_cast<const uint8*>(ImageBase + Entry->EndAddress) };
        return { Code, Code + 0x200 };
    }

    /*
    * Inner フェーズで呼んでも安全な関数か = フック済みの GetPrivateStaticClassBody / ConstructUScriptStruct / ConstructUEnum を呼ぶか。
    * (ConstructUClass は Outer フェーズ側なので数えない。intrinsic クラスの Z_Construct は Outer で ConstructUClass を呼ぶが、
    *  Inner では UClass をその場で組み立てる本物の処理が走ってしまう)
    * StaticClass() のような小さなラッパーは呼び出し先を辿る。
    */
    bool IsSafeToConstruct(const uint8* Code, int Depth = 3)
    {
        const auto [Begin, End] = GetFunctionRange(Code);
        const bool bIsWrapper = End - Begin <= 0x40;
        const uintptr_t Hooked[] = { ConstructUScriptStructAddress, ConstructUEnumAddress, GetPrivateStaticClassBodyAddress };

        for (const uint8* Ip = Begin; Ip + 6 <= End; Ip++)
        {
            uintptr_t Target = 0;
            int32 Disp;
            if (Ip[0] == 0xE8 || Ip[0] == 0xE9)
            {
                memcpy(&Disp, Ip + 1, sizeof(Disp));
                Target = reinterpret_cast<uintptr_t>(Ip + 5 + Disp);
            }
            else if (Ip[0] == 0xFF && (Ip[1] == 0x15 || Ip[1] == 0x25))
            {
                memcpy(&Disp, Ip + 2, sizeof(Disp));
                const uint8* Slot = Ip + 6 + Disp;
                if (IsInAnyModuleData(Slot))
                    Target = *reinterpret_cast<const uintptr_t*>(Slot);
            }

            if (!Target)
                continue;

            for (const uintptr_t H : Hooked)
            {
                if (Target == H)
                    return true;
            }

            if (bIsWrapper && Depth > 0 && IsInAnyModuleCode(reinterpret_cast<const void*>(Target))
                && IsSafeToConstruct(SkipJumpThunks(reinterpret_cast<const void*>(Target)), Depth - 1))
                return true;
        }
        return false;
    }

    bool IsIdentifier(const wchar_t* Str, size_t MaxLen)
    {
        if (!(iswalpha(Str[0]) || Str[0] == L'_'))
            return false;
        for (size_t i = 0; i < MaxLen; i++)
        {
            if (Str[i] == 0)
                return true;
            if (!(iswalnum(Str[i]) || Str[i] == L'_'))
                return false;
        }
        return false;
    }

    /*
    * intrinsic クラス (UField, UStruct, UClass ...) の Z_Construct は GetPrivateStaticClassBody を使わず UClass をその場で組み立てる。
    * 実行はせず、コード中で最初に `lea r64, [rip+X]` される識別子っぽい UTF-16 文字列 (= FName(TEXT("Field")) 等) をクラス名とする。
    */
    std::wstring FindClassNameInCode(const void* Func)
    {
        const auto [Begin, End] = GetFunctionRange(SkipJumpThunks(Func));
        for (const uint8* Ip = Begin; Ip + 7 <= End; Ip++)
        {
            if ((Ip[0] != 0x48 && Ip[0] != 0x4C) || Ip[1] != 0x8D || (Ip[2] & 0xC7) != 0x05)
                continue;

            int32 Disp;
            memcpy(&Disp, Ip + 3, sizeof(Disp));
            const auto* Str = reinterpret_cast<const wchar_t*>(Ip + 7 + Disp);
            if (reinterpret_cast<uintptr_t>(Str) % 2 == 0 && IsInAnyModuleData(Str) && IsInAnyModuleData(Str + 64) && IsIdentifier(Str, 64))
                return Str;
        }
        return {};
    }

    /*
    * Func (Z_Construct_* / GetPrivateStaticClass) の先頭付近から、値が RealObject になっている
    * `mov r64, [rip+X]` / `cmp qword [rip+X], imm8` / `cmp [rip+X], r64` の X (= InnerSingleton) を探して null に戻す。
    */
    bool ResetSingletonSlot(FTypeConstructFunc Func, const void* RealObject)
    {
        const uint8* Code = SkipJumpThunks(reinterpret_cast<const void*>(Func));

        for (int i = 0; i < 0x100; i++)
        {
            const uint8* Ip = Code + i;
            if (!IsInAnyModuleCode(Ip + 8))
                break;

            const uint8* Slot = nullptr;
            int32 Disp;
            if ((Ip[0] == 0x48 || Ip[0] == 0x4C) && (Ip[1] == 0x8B || Ip[1] == 0x39) && (Ip[2] & 0xC7) == 0x05)
            {
                memcpy(&Disp, Ip + 3, sizeof(Disp));
                Slot = Ip + 7 + Disp;
            }
            else if (Ip[0] == 0x48 && Ip[1] == 0x83 && Ip[2] == 0x3D)
            {
                memcpy(&Disp, Ip + 3, sizeof(Disp));
                Slot = Ip + 8 + Disp;
            }

            if (!Slot || reinterpret_cast<uintptr_t>(Slot) % 8 != 0 || !IsInAnyModuleData(Slot))
                continue;

            void** SingletonPtr = const_cast<void**>(reinterpret_cast<void* const*>(Slot));
            if (*SingletonPtr != RealObject)
                continue;

            DWORD OldProtect;
            if (!VirtualProtect(SingletonPtr, sizeof(void*), PAGE_READWRITE, &OldProtect))
                return false;
            *SingletonPtr = nullptr;
            VirtualProtect(SingletonPtr, sizeof(void*), OldProtect, &OldProtect);
            return true;
        }
        return false;
    }

    /*
    * Z_Construct_* / StaticClass() を Inner フェーズで呼んで FToken* を得る。
    *
    * - フック済み関数を通る関数だけを実行する。
    * - DLL の静的初期化子の中で StaticClass() 等が呼ばれていた型は InnerSingleton に本物の UObject が入っていてフックを通らないので、
    *   InnerSingleton を null に戻してからもう一度呼ぶ (UObject のメモリレイアウトに依存しないで済む)。
    * - intrinsic クラスの関数は実行せず、コード中の名前文字列から FToken を作る (親は FClassParams::DependencySingletonFuncArray から辿る)。
    */
    std::unordered_map<const void*, FToken*> IntrinsicTokenByFunction;

    void* ConstructObject(FTypeConstructFunc Func)
    {
        if (!Func || !IsInAnyModuleCode(reinterpret_cast<const void*>(Func)))
            return nullptr;

        const uint8* Code = SkipJumpThunks(reinterpret_cast<const void*>(Func));
        if (const auto It = IntrinsicTokenByFunction.find(Code); It != IntrinsicTokenByFunction.end())
            return It->second;

        if (!IsSafeToConstruct(Code))
        {
            std::wstring Name = FindClassNameInCode(Code);
            if (Name.empty())
            {
                Stats.UnresolvedRealObjects++;
                return nullptr;
            }

            FToken* Token = GetOrCreateToken(Code, ETokenKind::Class, nullptr);
            Token->Name = std::move(Name);
            IntrinsicTokenByFunction.emplace(Code, Token);
            Stats.IntrinsicClasses++;
            if (bVerboseLog)
                printf("[*] Intrinsic class %S (func %p)\n", Token->Name.c_str(), Code);
            return Token;
        }

        void* Result = SafeConstruct(Func);
        if (!Result || AsToken(Result))
            return Result;

        if (const auto It = TokenByRealObject.find(Result); It != TokenByRealObject.end())
            return It->second;

        if (ResetSingletonSlot(Func, Result))
        {
            void* Retry = SafeConstruct(Func);
            if (AsToken(Retry))
            {
                Stats.RealObjects++;
                TokenByRealObject.emplace(Result, Retry);
                return Retry;
            }
        }

        Stats.UnresolvedRealObjects++;
        return nullptr;
    }


    std::unordered_map<const void*, const FClassParams*> ClassParamsByObject;
    std::vector<void*> ClassObjectsToEmit;
    std::unordered_set<const void*> ClassObjectsQueued;

    void QueueClassObject(void* Object)
    {
        if (ClassObjectsQueued.insert(Object).second)
            ClassObjectsToEmit.push_back(Object);
    }

    std::string NameOfClassObject(void* Object)
    {
        const FToken* Token = AsToken(Object);
        return Token && Token->Kind == ETokenKind::Class ? Utils::WideToUtf8(Token->Name) : std::string();
    }

    std::string NameOfTypeFunc(FTypeConstructFunc Func, ETokenKind ExpectedKind)
    {
        FToken* Token = AsToken(ConstructObject(Func));
        if (!Token)
            return {};

        if (ExpectedKind == ETokenKind::Enum && Token->Kind == ETokenKind::Enum)
            return static_cast<const FEnumParams*>(Token->Params)->NameUTF8;

        if (ExpectedKind == ETokenKind::Struct)
        {
            if (Token->Kind == ETokenKind::Struct)
                return static_cast<const FStructParams*>(Token->Params)->NameUTF8;

            if (Token->Kind == ETokenKind::Class)
            {
                QueueClassObject(Token);
                return Utils::WideToUtf8(Token->Name);
            }
        }
        return {};
    }

    std::unique_ptr<Dumper::PropertyType> MakeType(Dumper::EUsmapPropertyType Type)
    {
        auto Result = std::make_unique<Dumper::PropertyType>();
        Result->Type = Type;
        return Result;
    }

    /* UECodeGen_Private::ConstructFProperty と同じく配列を後ろから読む (コンテナの中身はコンテナ本体の直前にある) */
    bool ParseProperty(const FPropertyParamsBase* const*& Cursor, int32& Remaining, Dumper::PropertyType& OutType, const FPropertyParamsBase*& OutParams)
    {
        using Dumper::EUsmapPropertyType;

        if (Remaining <= 0)
            return false;

        const FPropertyParamsBase* Params = *--Cursor;
        --Remaining;

        if (!IsInAnyModuleData(Params) || !IsValidName(Params->NameUTF8))
            return false;

        OutParams = Params;

        const FTypeConstructFunc TypeFunc = reinterpret_cast<const FPropertyParamsWithFunc*>(Params)->TypeFunc;
        const auto Gen = static_cast<EPropertyGenFlags>(static_cast<uint8>(Params->Flags) & static_cast<uint8>(EPropertyGenFlags::TypeMask));

        int ReadMore = 0;
        switch (Gen)
        {
        case EPropertyGenFlags::Byte:
        {
            /* enum 付きの ByteProperty は「基底型 Byte の EnumProperty」として書く (Dumper-7 と同じ) */
            std::string EnumName = TypeFunc ? NameOfTypeFunc(TypeFunc, ETokenKind::Enum) : std::string();
            if (TypeFunc && EnumName.empty())
                Stats.UnresolvedEnumRefs++;

            if (!EnumName.empty())
            {
                OutType.Type = EUsmapPropertyType::EnumProperty;
                OutType.TypeName = std::move(EnumName);
                OutType.Inner = MakeType(EUsmapPropertyType::ByteProperty);
            }
            else
            {
                OutType.Type = EUsmapPropertyType::ByteProperty;
            }
            break;
        }
        case EPropertyGenFlags::Int8:      OutType.Type = EUsmapPropertyType::Int8Property; break;
        case EPropertyGenFlags::Int16:     OutType.Type = EUsmapPropertyType::Int16Property; break;
        case EPropertyGenFlags::Int:       OutType.Type = EUsmapPropertyType::IntProperty; break;
        case EPropertyGenFlags::Int64:     OutType.Type = EUsmapPropertyType::Int64Property; break;
        case EPropertyGenFlags::UInt16:    OutType.Type = EUsmapPropertyType::UInt16Property; break;
        case EPropertyGenFlags::UInt32:    OutType.Type = EUsmapPropertyType::UInt32Property; break;
        case EPropertyGenFlags::UInt64:    OutType.Type = EUsmapPropertyType::UInt64Property; break;
        case EPropertyGenFlags::Float:     OutType.Type = EUsmapPropertyType::FloatProperty; break;
        case EPropertyGenFlags::Double:
        case EPropertyGenFlags::LargeWorldCoordinatesReal:
                                           OutType.Type = EUsmapPropertyType::DoubleProperty; break;
        case EPropertyGenFlags::Bool:      OutType.Type = EUsmapPropertyType::BoolProperty; break;
        case EPropertyGenFlags::SoftClass:
        case EPropertyGenFlags::SoftObject:
                                           OutType.Type = EUsmapPropertyType::SoftObjectProperty; break;
        case EPropertyGenFlags::WeakObject: OutType.Type = EUsmapPropertyType::WeakObjectProperty; break;
        case EPropertyGenFlags::LazyObject: OutType.Type = EUsmapPropertyType::LazyObjectProperty; break;
        case EPropertyGenFlags::Class:
        case EPropertyGenFlags::Object:
                                           OutType.Type = EUsmapPropertyType::ObjectProperty; break;
        case EPropertyGenFlags::Interface: OutType.Type = EUsmapPropertyType::InterfaceProperty; break;
        case EPropertyGenFlags::Name:      OutType.Type = EUsmapPropertyType::NameProperty; break;
        case EPropertyGenFlags::Str:       OutType.Type = EUsmapPropertyType::StrProperty; break;
        case EPropertyGenFlags::Text:      OutType.Type = EUsmapPropertyType::TextProperty; break;
        case EPropertyGenFlags::Delegate:  OutType.Type = EUsmapPropertyType::DelegateProperty; break;
        case EPropertyGenFlags::InlineMulticastDelegate:
        case EPropertyGenFlags::SparseMulticastDelegate:
                                           OutType.Type = EUsmapPropertyType::MulticastDelegateProperty; break;
        case EPropertyGenFlags::FieldPath: OutType.Type = EUsmapPropertyType::FieldPathProperty; break;
        case EPropertyGenFlags::Utf8Str:   OutType.Type = EUsmapPropertyType::Utf8StrProperty; break;
        case EPropertyGenFlags::AnsiStr:   OutType.Type = EUsmapPropertyType::AnsiStrProperty; break;
        case EPropertyGenFlags::Struct:
        {
            OutType.Type = EUsmapPropertyType::StructProperty;
            OutType.TypeName = NameOfTypeFunc(TypeFunc, ETokenKind::Struct);
            if (OutType.TypeName.empty())
                Stats.UnresolvedStructRefs++;
            break;
        }
        case EPropertyGenFlags::Enum:
        {
            OutType.Type = EUsmapPropertyType::EnumProperty;
            OutType.TypeName = NameOfTypeFunc(TypeFunc, ETokenKind::Enum);
            if (OutType.TypeName.empty())
                Stats.UnresolvedEnumRefs++;
            ReadMore = 1; // 基底の整数プロパティ
            break;
        }
        case EPropertyGenFlags::Array:    OutType.Type = EUsmapPropertyType::ArrayProperty; ReadMore = 1; break;
        case EPropertyGenFlags::Set:      OutType.Type = EUsmapPropertyType::SetProperty; ReadMore = 1; break;
        case EPropertyGenFlags::Optional: OutType.Type = EUsmapPropertyType::OptionalProperty; ReadMore = 1; break;
        case EPropertyGenFlags::Map:      OutType.Type = EUsmapPropertyType::MapProperty; ReadMore = 2; break;

        /* Verse 系は usmap に対応する型が無い */
        case EPropertyGenFlags::VerseString:
        case EPropertyGenFlags::VerseLegacyValue:
            OutType.Type = EUsmapPropertyType::Unknown;
            Stats.UnknownPropertyTypes++;
            ReadMore = 1;
            break;
        default:
            OutType.Type = EUsmapPropertyType::Unknown;
            Stats.UnknownPropertyTypes++;
            break;
        }

        const FPropertyParamsBase* Unused = nullptr;
        if (ReadMore >= 1)
        {
            OutType.Inner = std::make_unique<Dumper::PropertyType>();
            if (!ParseProperty(Cursor, Remaining, *OutType.Inner, Unused))
                return false;
        }
        if (ReadMore >= 2)
        {
            OutType.Value = std::make_unique<Dumper::PropertyType>();
            if (!ParseProperty(Cursor, Remaining, *OutType.Value, Unused))
                return false;
        }
        return true;
    }

    std::vector<Dumper::Property> ParseProperties(const FPropertyParamsBase* const* Array, uint32 Num, const Dumper::Options& Opts)
    {
        std::vector<Dumper::Property> Result;

        const FPropertyParamsBase* const* Cursor = Array + Num;
        int32 Remaining = static_cast<int32>(Num);

        while (Remaining > 0)
        {
            Dumper::Property Prop;
            const FPropertyParamsBase* Params = nullptr;
            if (!ParseProperty(Cursor, Remaining, Prop.Type, Params))
            {
                Stats.BrokenPropertyArrays++;
                break;
            }

            if (!Opts.bIncludeEditorOnly && (Params->PropertyFlags & CPF_EditorOnly))
            {
                Stats.SkippedEditorOnly++;
                continue;
            }

            Prop.Name = Params->NameUTF8;
            Prop.ArrayDim = Params->ArrayDim ? Params->ArrayDim : 1;
            Prop.PropertyFlags = Params->PropertyFlags;
            Result.push_back(std::move(Prop));
        }

        /* 後ろから読んだので反転すると宣言順 (= UStruct::ChildProperties の順) になる */
        std::reverse(Result.begin(), Result.end());
        return Result;
    }

    std::string SuperNameOfClassObject(void* Object)
    {
        void* Super = nullptr;

        if (const auto It = ClassParamsByObject.find(Object); It != ClassParamsByObject.end())
        {
            /* UHT は DependentSingletons[] に { 親クラス, パッケージ } の順で並べる (親が無ければパッケージのみ) */
            const FClassParams* Params = It->second;
            if (Params->NumDependencySingletons >= 2)
                Super = ConstructObject(Params->DependencySingletonFuncArray[0]);
        }
        else if (FToken* Token = AsToken(Object); Token && Token->SuperClassFunc)
        {
            Super = ConstructObject(Token->SuperClassFunc);
        }

        if (!Super || Super == Object)
            return {};

        if (FToken* SuperToken = AsToken(Super); SuperToken && SuperToken->Kind != ETokenKind::Class)
            return {};

        QueueClassObject(Super);
        return NameOfClassObject(Super);
    }

    bool IsPowerOfTwo(int64 Value)
    {
        const uint64 V = static_cast<uint64>(Value);
        return (V & (V - 1)) == 0;
    }

    /*
    * UEnum::SetEnums(..., EAddMaxKeyIfMissing::Yes) が実行時に追加する "<Prefix>_MAX" を再現する。
    * (UHT の FEnumeratorParam には含まれないが、cook 済みデータはこの値を含む UEnum を前提にしている)
    */
    std::string ToLower(std::string Str)
    {
        std::transform(Str.begin(), Str.end(), Str.begin(), [](unsigned char C) { return static_cast<char>(tolower(C)); });
        return Str;
    }

    void AppendGeneratedMax(const FEnumParams& Params, std::vector<std::pair<std::string, int64>>& Names, const std::unordered_set<std::string>& PackageEnumNames)
    {
        const std::string EnumName = Params.NameUTF8;
        const bool bRegular = Params.CppForm == static_cast<uint8>(ECppForm::Regular);

        auto GenerateFullEnumName = [&](const std::string& Name)
        {
            return bRegular || Name.find("::") != std::string::npos ? Name : EnumName + "::" + Name;
        };

        /* UEnum::GenerateEnumPrefix */
        std::string Prefix;
        if (!Names.empty())
        {
            Prefix = Names[0].first;
            for (size_t i = 1; i < Names.size(); i++)
            {
                const std::string& Item = Names[i].first;
                size_t Len = 0;
                while (Len < Prefix.size() && Len < Item.size() && Prefix[Len] == Item[Len])
                    Len++;
                Prefix.resize(Len);
            }

            const size_t Underscore = Prefix.rfind('_');
            if (Underscore != std::string::npos && Underscore > 0)
                Prefix.resize(Underscore);
            else
                Prefix.clear();
        }
        if (Prefix.empty())
            Prefix = EnumName;

        /* UEnum::ContainsExistingMax */
        const std::string MaxName = GenerateFullEnumName(Prefix + "_MAX");
        const std::string PlainMax = GenerateFullEnumName("MAX");
        for (const auto& [Name, Value] : Names)
        {
            if (Name == MaxName || Name == PlainMax)
                return;
        }

        /*
        * 同じパッケージで先に登録された enum が同じ名前を持っていると LookupEnumName に引っかかり SetEnums は MAX を追加しない。
        * (例: /Script/Engine の ESetMaskConditionType と EFieldFalloffType はどちらも "Field_MAX" になるが、先に登録された前者だけが持つ)
        * FName 比較なので大文字小文字は区別しない。
        */
        if (PackageEnumNames.contains(ToLower(MaxName)))
            return;

        /* UE::GetMaxEnumValue + 1 */
        int64 MaxValue = 0;
        if (!Names.empty())
        {
            if (Params.EnumFlags & EEnumFlags_Flags)
            {
                for (const auto& [Name, Value] : Names)
                {
                    if (IsPowerOfTwo(Value))
                        MaxValue |= Value;
                }
            }
            else
            {
                MaxValue = Names[0].second;
                for (const auto& [Name, Value] : Names)
                    MaxValue = std::max(MaxValue, Value);
            }
            MaxValue += 1;
        }

        Names.emplace_back(MaxName, MaxValue);
        Stats.GeneratedEnumMax++;
    }

    void RegisterModule(const PE& Module)
    {
        if (std::find(Modules.begin(), Modules.end(), &Module) == Modules.end())
            Modules.push_back(&Module);
    }
}

bool Dumper::SetupHooks(const PE& CoreUObjectModule)
{
    RegisterModule(CoreUObjectModule);

    struct HookEntry
    {
        const char* MangledName;
        void* Detour;
        uintptr_t* OutAddress;
    };

    const HookEntry Entries[] =
    {
        { "?ConstructUClass@UECodeGen_Private@@", reinterpret_cast<void*>(&Hook_ConstructUClass), &ConstructUClassAddress },
        { "?ConstructUScriptStruct@UECodeGen_Private@@", reinterpret_cast<void*>(&Hook_ConstructUScriptStruct), &ConstructUScriptStructAddress },
        { "?ConstructUEnum@UECodeGen_Private@@", reinterpret_cast<void*>(&Hook_ConstructUEnum), &ConstructUEnumAddress },
        { "?GetPrivateStaticClassBody@@", reinterpret_cast<void*>(&Hook_GetPrivateStaticClassBody), &GetPrivateStaticClassBodyAddress },
    };

    for (const HookEntry& Entry : Entries)
    {
        const uintptr_t Address = CoreUObjectModule.FindExport(Entry.MangledName);
        if (!Address)
        {
            printf("[-] Export not found: %s\n", Entry.MangledName);
            return false;
        }

        if (!Hook::Install(reinterpret_cast<void*>(Address), Entry.Detour))
        {
            printf("[-] Failed to hook %s\n", Entry.MangledName);
            return false;
        }

        *Entry.OutAddress = Address;
        printf("[+] Hooked %-48s at %S+0x%llX\n", Entry.MangledName, CoreUObjectModule.GetFileName().c_str(), static_cast<unsigned long long>(Address - CoreUObjectModule.GetBase()));
    }
    return true;
}

void Dumper::DumpModule(const PE& Module)
{
    RegisterModule(Module);

    const uintptr_t Targets[3] = { ConstructUClassAddress, ConstructUScriptStructAddress, ConstructUEnumAddress };
    const std::vector<PE::CallSite> Calls = Module.FindCallsTo({ Targets[0], Targets[1], Targets[2] });

    const size_t ClassesBefore = ClassParamsList.size();
    const size_t StructsBefore = StructParamsList.size();
    const size_t EnumsBefore = EnumParamsList.size();

    for (const PE::CallSite& Call : Calls)
    {
        const int Kind = Call.Target == Targets[0] ? 0 : Call.Target == Targets[1] ? 1 : 2;
        Stats.CallSites[Kind]++;

        /* Z_Construct_* の中で `lea rdx, [UHT_STATICS::XxxParams]` → `call ConstructUXxx` という並びになっている */
        bool bFound = false;
        Module.ForEachLeaBefore(Call.Instruction, /*rdx*/ 2, 0x40, [&](uintptr_t Candidate)
        {
            if (Kind == 0 && IsValidClassParams(reinterpret_cast<const FClassParams*>(Candidate)))
                AddClassParams(reinterpret_cast<const FClassParams*>(Candidate)), bFound = true;
            else if (Kind == 1 && IsValidStructParams(reinterpret_cast<const FStructParams*>(Candidate)))
                AddStructParams(reinterpret_cast<const FStructParams*>(Candidate)), bFound = true;
            else if (Kind == 2 && IsValidEnumParams(reinterpret_cast<const FEnumParams*>(Candidate)))
                AddEnumParams(reinterpret_cast<const FEnumParams*>(Candidate)), bFound = true;
            return !bFound;
        });

        if (!bFound)
            Stats.UnresolvedCallSites[Kind]++;
    }

    printf("[+] %S: %zu call sites -> %zu classes, %zu structs, %zu enums\n", Module.GetFileName().c_str(), Calls.size(),
        ClassParamsList.size() - ClassesBefore, StructParamsList.size() - StructsBefore, EnumParamsList.size() - EnumsBefore);
}

void Dumper::Resolve(const Options& Opts)
{
    bVerboseLog = Opts.bVerbose;

    std::unordered_set<std::string> EmittedStructNames;
    std::unordered_set<std::string> EmittedEnumNames;

    auto EmitStruct = [&](Struct&& S)
    {
        if (!EmittedStructNames.insert(S.Name).second)
        {
            Stats.DuplicateNames++;
            if (Opts.bVerbose)
                printf("[!] Duplicate struct name: %s\n", S.Name.c_str());
            return;
        }
        Structs.push_back(std::move(S));
    };

    /*
    * 1. クラス: Z_Construct_UClass_X(Inner) を呼ぶと GetPrivateStaticClassBody フックがクラス名入りの FToken を返す。
    *    (静的初期化で既に作られていたクラスは本物の UClass* が返る)
    */
    for (const FClassParams* Params : ClassParamsList)
    {
        void* Object = ConstructObject(Params->ClassNoRegisterFunc);
        if (!Object || NameOfClassObject(Object).empty())
        {
            Stats.FailedClassRegistrations++;
            if (Opts.bVerbose)
            {
                for (const PE* Module : Modules)
                {
                    if (Module->Contains(reinterpret_cast<uintptr_t>(Params)))
                        printf("[!] Failed class registration: params %S+0x%llX, register func +0x%llX\n", Module->GetFileName().c_str(),
                            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(Params) - Module->GetBase()),
                            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(Params->ClassNoRegisterFunc) - Module->GetBase()));
                }
            }
            continue;
        }

        ClassParamsByObject.emplace(Object, Params);
        QueueClassObject(Object);
    }

    /*
    * 2. クラスと構造体。プロパティや親の解決中にフック経由で新しい型が見つかるとリスト末尾に追加されるので、
    *    インデックスで回して新規が無くなるまで繰り返す。
    */
    size_t NextClass = 0;
    size_t NextStruct = 0;
    while (NextClass < ClassObjectsToEmit.size() || NextStruct < StructParamsList.size())
    {
        for (; NextClass < ClassObjectsToEmit.size(); NextClass++)
        {
            void* Object = ClassObjectsToEmit[NextClass];

            Struct S;
            S.Name = NameOfClassObject(Object);
            S.SuperName = SuperNameOfClassObject(Object);
            S.bIsClass = true;

            if (S.Name.empty())
                continue;

            if (const auto It = ClassParamsByObject.find(Object); It != ClassParamsByObject.end())
                S.Properties = ParseProperties(It->second->PropertyArray, It->second->NumProperties, Opts);
            else
                S.bIsPlaceholder = true; // UHT の Params を持たない intrinsic クラス

            EmitStruct(std::move(S));
        }

        for (; NextStruct < StructParamsList.size(); NextStruct++)
        {
            const FStructParams* Params = StructParamsList[NextStruct];

            Struct S;
            S.Name = Params->NameUTF8;
            if (Params->SuperFunc)
                S.SuperName = NameOfTypeFunc(Params->SuperFunc, ETokenKind::Struct);
            S.Properties = ParseProperties(Params->PropertyArray, Params->NumProperties, Opts);

            EmitStruct(std::move(S));
        }
    }

    /* 3. 列挙型 (ここまでの処理でフック経由で見つかったものも含む) */
    /* UE の登録順 (= .gen.cpp 内の宣言順 = Params のアドレス順) に近づける。_MAX の衝突判定が順序に依存するため */
    std::vector<const FEnumParams*> SortedEnums = EnumParamsList;
    std::stable_sort(SortedEnums.begin(), SortedEnums.end());

    std::unordered_map<const void*, std::unordered_set<std::string>> EnumNamesByPackage; // キー: OuterFunc (= Z_Construct_UPackage_*)
    for (const FEnumParams* Params : SortedEnums)
    {
        std::vector<std::pair<std::string, int64>> FullNames;
        for (int i = 0; i < Params->NumEnumerators; i++)
            FullNames.emplace_back(Params->EnumeratorParams[i].NameUTF8, Params->EnumeratorParams[i].Value);

        std::unordered_set<std::string>& PackageEnumNames = EnumNamesByPackage[reinterpret_cast<const void*>(Params->OuterFunc)];
        AppendGeneratedMax(*Params, FullNames, PackageEnumNames);

        Enum E;
        E.Name = Params->NameUTF8;
        for (const auto& [Name, Value] : FullNames)
        {
            PackageEnumNames.insert(ToLower(Name));
            E.Values.emplace_back(Value, std::string(Utils::StripEnumPrefix(Name)));
        }

        if (!EmittedEnumNames.insert(E.Name).second)
        {
            Stats.DuplicateNames++;
            continue;
        }
        Enums.push_back(std::move(E));
    }

    std::sort(Structs.begin(), Structs.end(), [](const Struct& A, const Struct& B) { return A.Name < B.Name; });
    std::sort(Enums.begin(), Enums.end(), [](const Enum& A, const Enum& B) { return A.Name < B.Name; });
}

void Dumper::PrintInfo()
{
    size_t NumClasses = 0, NumPlaceholders = 0, NumProperties = 0;
    for (const Struct& S : Structs)
    {
        NumClasses += S.bIsClass;
        NumPlaceholders += S.bIsPlaceholder;
        NumProperties += S.Properties.size();
    }

    printf("\n");
    printf("Call sites            : ConstructUClass=%zu ConstructUScriptStruct=%zu ConstructUEnum=%zu\n", Stats.CallSites[0], Stats.CallSites[1], Stats.CallSites[2]);
    printf("Unresolved call sites : %zu / %zu / %zu\n", Stats.UnresolvedCallSites[0], Stats.UnresolvedCallSites[1], Stats.UnresolvedCallSites[2]);
    printf("Classes               : %zu (placeholders: %zu, failed: %zu)\n", NumClasses, NumPlaceholders, Stats.FailedClassRegistrations);
    printf("Structs               : %zu\n", Structs.size() - NumClasses);
    printf("Enums                 : %zu (generated _MAX: %zu)\n", Enums.size(), Stats.GeneratedEnumMax);
    printf("Pre-constructed types : %zu re-registered, %zu intrinsic, %zu unresolved\n", Stats.RealObjects, Stats.IntrinsicClasses, Stats.UnresolvedRealObjects);
    printf("Properties            : %zu (editor-only skipped: %zu)\n", NumProperties, Stats.SkippedEditorOnly);
    printf("Unresolved refs       : struct=%zu enum=%zu, unknown types=%zu, broken arrays=%zu, duplicate names=%zu\n",
        Stats.UnresolvedStructRefs, Stats.UnresolvedEnumRefs, Stats.UnknownPropertyTypes, Stats.BrokenPropertyArrays, Stats.DuplicateNames);
    printf("\n");
}

const std::vector<Dumper::Struct>& Dumper::GetStructs()
{
    return Structs;
}

const std::vector<Dumper::Enum>& Dumper::GetEnums()
{
    return Enums;
}
