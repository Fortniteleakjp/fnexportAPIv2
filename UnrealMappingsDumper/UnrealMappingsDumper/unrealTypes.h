#pragma once

#include <string>
#include <winnt.h>
#include <functional>

#include "unrealEnums.h"
#include "unrealFunctions.h"

#define QUICK_OFFSET(type, offset) (*(type*)((uintptr_t)this + offset))

#define DECLARE_STATIC_CLASS(PATH) \
    static FORCEINLINE class UClass* StaticClass() \
	{ \
		static auto Inst = ObjObjects::FindObject<class UClass>(PATH); \
		return Inst; \
	} \

class FName
{
private:

	// fnexportAPI local patch: upstream models FName as two words. On this UE6 build it is three,
	// and the third one is load-bearing — zero-filling it made every name resolve to "None", while
	// carrying it through made them all resolve. The size also has to be right for its own sake:
	// FName is embedded by value in FField and in the enum name pairs, so modelling it too narrow
	// or too wide moves every field behind it.
	//
	// Only the first word identifies the name, so that stays the hash and comparison key; the rest
	// is carried so ToString sees the whole value.
	uint32_t Number = 0;
	uint32_t Word1 = 0;
	uint32_t Word2 = 0;

public:

	static inline bool IsOptimized = false;

	__forceinline FName(int InNum) : Number(InNum), Word1(0), Word2(0)
	{
	}

	__forceinline static std::string GetString(int Number)
	{
		return FName(Number).ToString();
	}

	__forceinline uint32_t GetNumber()
	{
		return Number;
	}

	bool operator== (FName n) const
	{
		return Number == n.Number;
	}

	friend size_t hash_value(const FName& p)
	{
		return size_t(p.Number);
	}

	std::wstring_view AsString() const
	{
		// Padded to four words so ToString cannot read past the value, whatever it expects.
		alignas(8) uint32_t Wide[4] = { Number, Word1, Word2, 0 };

		FString Ret;
		FNameToString((const FName*)Wide, Ret);

		if (Ret.Data() != nullptr)
		{
			return std::wstring_view(Ret.Data());
		}

		return {};
	}

	std::string ToString() const
	{
		auto Ret = AsString();

		return std::string(Ret.begin(), Ret.end());
	}
};

class UObject
{
private:

	static inline int NameOffset = 0;
	static inline int ClassOffset = 0;
	static inline int OuterOffset = 0;

	friend struct IUnrealVersion;

public:

	void GetPathName(std::wstring& Result, UObject* StopOuter = nullptr)
	{
		if (this == StopOuter || this == NULL)
		{
			Result += L"None";
			return;
		}

		if (Outer() && Outer() != StopOuter)
		{
			Outer()->GetPathName(Result, StopOuter);
			Result += L".";
		}

		// Read in place: a copy is what broke path building on UE6 (see FName above).
		Result += GetName();
	}

	FORCEINLINE std::wstring_view GetName()
	{
		auto& Name = QUICK_OFFSET(FName, NameOffset);
		return Name.AsString();
	}

	FORCEINLINE FName GetFName()
	{
		return QUICK_OFFSET(FName, NameOffset);
	}

	/// <summary>The first four words at the name offset, for reporting the real FName layout.</summary>
	FORCEINLINE const uint32_t* NameWords()
	{
		return (const uint32_t*)((uintptr_t)this + NameOffset);
	}

	FORCEINLINE std::wstring GetPath()
	{
		std::wstring Ret;

		GetPathName(Ret);

		return Ret;
	}

	FORCEINLINE class UClass* Class()
	{
		return QUICK_OFFSET(class UClass*, ClassOffset);
	}

	FORCEINLINE UObject* Outer()
	{
		return QUICK_OFFSET(UObject*, OuterOffset);
	}
};

// ---------------------------------------------------------------------------
// fnexportAPI local patch: the object array is read through a described layout instead of a fixed
// struct.
//
// UE6 changed FChunkedFixedUObjectArray and FUObjectItem in ways that silently produce garbage
// rather than failing loudly:
//
//   * the array swapped Num/Max for both elements and chunks, and moved PreAllocatedObjects to the
//     end (it used to sit right after Objects)
//   * FUObjectItem gained a 64-bit FlagsAndRefCount at offset 0, pushing the object pointer to +8
//     where the pointer used to be
//   * that pointer may be packed: its low bits live in ObjectPtrLow shifted right by 3, and its
//     high bits in the top half of FlagsAndRefCount (see UE_ENABLE_FUOBJECT_ITEM_PACKING)
//
// Rather than pick one, the scan in scanning.cpp tries the known layouts against real memory and
// records the one that actually resolves objects.
// ---------------------------------------------------------------------------
struct FUObjectArrayLayout
{
	// Offsets inside FChunkedFixedUObjectArray. Objects is at 0 in every version.
	int32_t NumElementsOffset;
	int32_t MaxElementsOffset;
	int32_t NumChunksOffset;
	int32_t MaxChunksOffset;

	// FUObjectItem stride, and where the object pointer sits inside it.
	int32_t ItemStride;
	int32_t ItemObjectOffset;

	// The pointer is split across FlagsAndRefCount and ObjectPtrLow.
	bool ItemIsPacked;

	int32_t ChunkSize;
};

// Pre-UE6 layout, which is also what upstream assumed. Replaced once the scan identifies the array.
inline FUObjectArrayLayout GObjectArrayLayout = { 0x14, 0x10, 0x1C, 0x18, 24, 0, false, 64 * 1024 };

// EInternalObjectFlags_MinFlagBitIndex; the object's high bits occupy everything below it.
constexpr int32_t GObjectFlagsMinBitIndex = 14;

// UObjects are 8-byte aligned, so the low three bits are free to drop when packing.
constexpr int32_t GObjectPtrTrailingZeroes = 3;

class ObjObjects
{
	static inline uintptr_t Inst;

	static FORCEINLINE int32_t ReadInt(int32_t Offset)
	{
		return *(int32_t*)(Inst + Offset);
	}

public:

	ObjObjects& operator=(const ObjObjects&) = delete;

	/// <summary>Resolves one FUObjectItem to the object it holds, honoring the detected packing.</summary>
	static FORCEINLINE UObject* ReadItemObject(uintptr_t Item)
	{
		auto& Layout = GObjectArrayLayout;

		if (!Layout.ItemIsPacked)
			return *(UObject**)(Item + Layout.ItemObjectOffset);

		// Low 32 bits of the pointer, minus the alignment bits that are always zero.
		auto Low = (uintptr_t) * (uint32_t*)(Item + Layout.ItemObjectOffset);

		// High bits ride along in the flags word, above the flag bits themselves.
		auto FlagsAndRefCount = *(int64_t*)Item;
		auto PtrMask = (uintptr_t)(~(0xFFFFFFFFu << GObjectFlagsMinBitIndex));
		auto High = ((uintptr_t)(FlagsAndRefCount >> 32) & PtrMask) << (32 + GObjectPtrTrailingZeroes);

		return (UObject*)(High | (Low << GObjectPtrTrailingZeroes));
	}

	static UObject* GetObjectByIndex(int Index)
	{
		if (!Inst || Index < 0)
			return nullptr;

		auto& Layout = GObjectArrayLayout;

		int ChunkIndex = Index / Layout.ChunkSize;
		int WithinChunkIndex = Index % Layout.ChunkSize;

		if (
			Index < ReadInt(Layout.NumElementsOffset) &&
			Index < ReadInt(Layout.MaxElementsOffset) &&
			ChunkIndex < ReadInt(Layout.NumChunksOffset)
			)
		{
			auto Chunk = ((uintptr_t*)Inst)[0] ? ((uintptr_t*)*(uintptr_t*)Inst)[ChunkIndex] : 0;

			if (Chunk)
				return ReadItemObject(Chunk + (uintptr_t)Layout.ItemStride * WithinChunkIndex);
		}

		return nullptr;
	}

	static FORCEINLINE int Num()
	{
		return Inst ? ReadInt(GObjectArrayLayout.NumElementsOffset) : 0;
	}

	static void SetInstance(uintptr_t Val)
	{
		if (Val)
			Inst = Val;
	}

	template <class T = UObject>
	static T* FindObjectByName(const wchar_t* ObjectName)
	{
		for (int i = 0; i < Num(); i++)
		{
			auto Obj = GetObjectByIndex(i);

			if (!Obj) continue;

			if (Obj->GetName() == ObjectName)
				return (T*)Obj;
		}

		return nullptr;
	}

	static void ForEach(std::function<void(UObject*&)> Action)
	{
		for (int i = 0; i < Num(); i++)
		{
			auto Obj = GetObjectByIndex(i);

			if (!Obj) continue;

			Action(Obj);
		}
	}

	template <class T>
	static T* FindObject(std::wstring FullName)
	{
		for (int i = 0; i < Num(); i++)
		{
			auto Obj = GetObjectByIndex(i);

			if (!Obj) continue;

			auto Path = Obj->GetPath();

			if (FullName.size() != Path.size())
				continue;

			bool Same = wcsncmp(FullName.c_str(), Path.c_str(), FullName.size()) == 0;

			if (Same)
				return (T*)Obj;
		}

		return nullptr;
	}
};

class UStruct : public UObject
{
private:

	static inline int SuperOffset = 0;
	static inline int ChildPropertiesOffset = 0;

	friend struct IUnrealVersion;

public:

	FORCEINLINE UStruct* Super()
	{
		return QUICK_OFFSET(UStruct*, SuperOffset);
	}

	FORCEINLINE int32_t PropertiesSize()
	{
		return QUICK_OFFSET(int32_t, ChildPropertiesOffset + sizeof(void*));
	}

	FORCEINLINE class FProperty* ChildProperties()
	{
		return QUICK_OFFSET(class FProperty*, ChildPropertiesOffset);
	}
};

class UClass : public UStruct
{
public:

	DECLARE_STATIC_CLASS(L"/Script/CoreUObject.Class");
};

class UScriptStruct : public UStruct
{
public:

	DECLARE_STATIC_CLASS(L"/Script/CoreUObject.ScriptStruct");
};

class FFieldClass
{
	FName Name;
	EClassCastFlags Id;

public:

	FORCEINLINE FName GetFName()
	{
		return Name;
	}

	FORCEINLINE std::wstring_view GetName()
	{
		return Name.AsString();
	}

	FORCEINLINE EClassCastFlags GetId()
	{
		return Id;
	}
};

class FField
{
public:

	class Variant
	{
		// fnexportAPI local patch: UE6 tags "this owner is a UObject" in the pointer's own low bit
		// (FFieldVariant::UObjectMask) instead of keeping a separate bool beside it, which halves
		// this to one word. Everything behind it — Next and NamePrivate, which the property walk
		// depends on — sat 8 bytes too far with the old model, so the chain read garbage and looped.
		uintptr_t Container;
	};

private:

	void* Vtbl;
	FFieldClass* ClassPrivate;
	Variant Owner;
	FField* Next;
	FName NamePrivate;
	EObjectFlags FlagsPrivate;

public:

	FORCEINLINE FName& GetFName()
	{
		return NamePrivate;
	}

	FORCEINLINE FField* GetNext() const
	{
		return Next;
	}

	FORCEINLINE FFieldClass* GetClass() const
	{
		return ClassPrivate;
	}

	FORCEINLINE EObjectFlags GetFlags() const
	{
		if (FName::IsOptimized)
		{
			return QUICK_OFFSET(EObjectFlags, offsetof(FField, NamePrivate) + 4);
		}

		return FlagsPrivate;
	}
};

class FProperty : public FField
{
private:

	int32_t ArrayDim;

protected:

	static inline int FPropertySize = 0;

public:

	friend struct IUnrealVersion;

	FORCEINLINE int32_t GetArrayDim()
	{
		// The upstream adjustment compensated for FField being modelled at the wrong width; with
		// the layout above matching the engine, the declared member is already in the right place.
		return ArrayDim;
	}
};

/// <summary>
/// UE6's UEnum::FNameData, which replaced the TArray&lt;TPair&lt;FName, int64&gt;&gt; the enum members
/// used to live in. Names and values are now two separate allocations addressed by tagged pointers
/// — the low bit records whether the array was allocated dynamically or points at static data from
/// the header tool — with the count beside them.
/// </summary>
class FEnumNameData
{
	static constexpr uintptr_t PointerMask = ~(uintptr_t)1;

	uintptr_t TaggedNames = 0;
	uintptr_t TaggedValues = 0;
	int32_t NumValues = 0;

public:

	/// <summary>One member, in the shape the writer already expects.</summary>
	struct FEntry
	{
		FName Key;
		int64_t Value;
	};

	FORCEINLINE int Num() const
	{
		return NumValues;
	}

	FORCEINLINE FName* GetNames() const
	{
		return (FName*)(TaggedNames & PointerMask);
	}

	FORCEINLINE int64_t* GetValues() const
	{
		return (int64_t*)(TaggedValues & PointerMask);
	}

	FORCEINLINE FEntry operator[](int Index) const
	{
		auto Values = GetValues();
		return { GetNames()[Index], Values ? Values[Index] : 0 };
	}
};

class UEnum : public UObject
{
public:

	/// <summary>Offset of the member data, derived at startup because it moves with the build.</summary>
	static inline int NamesOffset = 0;

	FEnumNameData& Names()
	{
		return QUICK_OFFSET(FEnumNameData, NamesOffset);
	}

	DECLARE_STATIC_CLASS(L"/Script/CoreUObject.Enum");
};

class FStructProperty : public FProperty
{
	UScriptStruct* Struct;

public:

	FORCEINLINE UScriptStruct* GetStruct()
	{
		return QUICK_OFFSET(UScriptStruct*, FPropertySize);
	}
};

class FByteProperty : public FProperty
{
	UEnum* Enum;

public:

	FORCEINLINE UEnum* GetEnum()
	{
		return QUICK_OFFSET(UEnum*, FPropertySize);
	}
};

class FArrayProperty : public FProperty
{
	enum class EArrayPropertyFlags
	{
		None,
		UsesMemoryImageAllocator
	};

	FProperty* Inner;
	EArrayPropertyFlags ArrayFlags;

public:

	FORCEINLINE FProperty* GetInner()
	{
		return QUICK_OFFSET(FProperty*, FPropertySize);
	}
};

class FMapProperty : public FProperty
{
	FProperty* KeyProp;
	FProperty* ValueProp;

public:

	FORCEINLINE FProperty* GetKey()
	{
		return QUICK_OFFSET(FProperty*, FPropertySize);
	}

	FORCEINLINE FProperty* GetValue()
	{
		return QUICK_OFFSET(FProperty*, FPropertySize + sizeof(FProperty*));
	}
};

class FEnumProperty : public FProperty
{
	FProperty* UnderlyingProp;
	UEnum* Enum;

public:

	FORCEINLINE FProperty* GetUnderlying()
	{
		return QUICK_OFFSET(FProperty*, FPropertySize);
	}

	FORCEINLINE UEnum* GetEnum()
	{
		return QUICK_OFFSET(UEnum*, FPropertySize + sizeof(FProperty*));
	}
};