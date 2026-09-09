#include "pch.h"

#include "app.h"
#include "dumper.h"
#include "scanning.h"
#include "hostConfig.h"
#include "writer.h"
#include "oodle.h"

static EPropertyType GetPropertyType(FProperty* Prop)
{
	// fnexportAPI local patch: the field class is a pointer inside the property, and a property
	// reached through a container or an enum's underlying type is not guaranteed to be a real one.
	// Reading the class id through a bad pointer faults, and the dump is well past the point where
	// that can be recovered from, so it is checked here instead.
	auto FieldClass = (uintptr_t)Prop->GetClass();
	if (FieldClass < 0x10000 || (FieldClass & 7) || !IsMemoryReadable(FieldClass, FFieldClass::IdOffset + sizeof(uint64_t)))
	{
		return EPropertyType::Unknown;
	}

	switch (Prop->GetClass()->GetId())
	{
	case CASTCLASS_FObjectProperty:
	case CASTCLASS_FClassProperty:
	case CASTCLASS_FObjectPtrProperty:
	case CASTCLASS_FClassPtrProperty:
	{
		return EPropertyType::ObjectProperty;
	}
	case CASTCLASS_FStructProperty:
	{
		return EPropertyType::StructProperty;
	}
	case CASTCLASS_FInt8Property:
	{
		return EPropertyType::Int8Property;
	}
	case CASTCLASS_FInt16Property:
	{
		return EPropertyType::Int16Property;
	}
	case CASTCLASS_FIntProperty:
	{
		return EPropertyType::IntProperty;
	}
	case CASTCLASS_FInt64Property:
	{
		return EPropertyType::Int64Property;
	}
	case CASTCLASS_FUInt16Property:
	{
		return EPropertyType::UInt16Property;
	}
	case CASTCLASS_FUInt32Property:
	{
		return EPropertyType::UInt32Property;
	}
	case CASTCLASS_FUInt64Property:
	{
		return EPropertyType::UInt64Property;
	}
	case CASTCLASS_FArrayProperty:
	{
		return EPropertyType::ArrayProperty;
	}
	case CASTCLASS_FFloatProperty:
	{
		return EPropertyType::FloatProperty;
	}
	case CASTCLASS_FDoubleProperty:
	{
		return EPropertyType::DoubleProperty;
	}
	case CASTCLASS_FBoolProperty:
	{
		return EPropertyType::BoolProperty;
	}
	case CASTCLASS_FStrProperty:
	{
		return EPropertyType::StrProperty;
	}
	case CASTCLASS_FNameProperty:
	{
		return EPropertyType::NameProperty;
	}
	case CASTCLASS_FTextProperty:
	{
		return EPropertyType::TextProperty;
	}
	case CASTCLASS_FEnumProperty:
	{
		return EPropertyType::EnumProperty;
	}
	case CASTCLASS_FInterfaceProperty:
	{
		return EPropertyType::InterfaceProperty;
	}
	case CASTCLASS_FMapProperty:
	{
		return EPropertyType::MapProperty;
	}
	case CASTCLASS_FByteProperty:
	{
		FByteProperty* ByteProp = static_cast<FByteProperty*>(Prop);

		if (ByteProp->GetEnum())
			return EPropertyType::EnumAsByteProperty;

		return EPropertyType::ByteProperty;
	}
	case CASTCLASS_FMulticastDelegateProperty:
	case CASTCLASS_FMulticastInlineDelegateProperty:
	case CASTCLASS_FMulticastSparseDelegateProperty:
	{
		return EPropertyType::MulticastDelegateProperty;
	}
	case CASTCLASS_FDelegateProperty:
	{
		return EPropertyType::DelegateProperty;
	}
	case CASTCLASS_FSoftObjectProperty:
	case CASTCLASS_FSoftClassProperty:
	{
		return EPropertyType::SoftObjectProperty;
	}
	case CASTCLASS_FWeakObjectProperty:
	{
		return EPropertyType::WeakObjectProperty;
	}
	case CASTCLASS_FLazyObjectProperty:
	{
		return EPropertyType::LazyObjectProperty;
	}
	case CASTCLASS_FSetProperty:
	{
		return EPropertyType::SetProperty;
	}
	case CASTCLASS_FFieldPathProperty:
	{
		return EPropertyType::FieldPathProperty;
	}
	default:
	{
		return EPropertyType::Unknown;
	}
	}
}

struct FPropertyData
{
	FProperty* Prop;
	uint16_t Index;
	uint8_t ArrayDim;
	FName Name;
	EPropertyType PropertyType;

	FPropertyData(FProperty* P, int Idx) :
		Prop(P),
		Index(Idx),
		ArrayDim(P->GetArrayDim()),
		Name(P->GetFName()),
		PropertyType(GetPropertyType(P))
	{
	}
};

void Dumper::Run(ECompressionMethod CompressionMethod)
{
	StreamWriter Buffer;
	phmap::parallel_flat_hash_map<FName, int> NameMap;

	std::vector<UEnum*> Enums;
	std::vector<UStruct*> Structs; // TODO: a better way than making this completely dynamic

	std::function<void(class FProperty*&, EPropertyType)> WritePropertyWrapper{}; // hacky.. i know

	// fnexportAPI local patch: what a property points at is not always there.
	//
	// The struct behind a StructProperty, the enum behind an EnumProperty and the element type of a
	// container all live past the end of FProperty, and a few editor-only classes — the
	// MaterialExpression family among them — reach ones that cannot be followed. Dereferencing them
	// took the whole dump down at the last step, after everything had been collected.
	//
	// A record still has to be written for such a property or the file stops making sense, so the
	// target is checked first and an unusable one is recorded as absent. The reader understands
	// both: an invalid name index reads back as no name, and an unknown type carries no payload.
	constexpr int32_t InvalidNameIndex = -1;

	auto IsFollowable = [](const void* Pointer)
	{
		auto Address = (uintptr_t)Pointer;
		return Address >= 0x10000 && (Address & 7) == 0 && IsMemoryReadable(Address, 0x30);
	};

	auto WriteTypeName = [&](UObject* Object)
	{
		if (IsFollowable(Object))
			Buffer.Write(NameMap[Object->GetFName()]);
		else
			Buffer.Write<int32_t>(InvalidNameIndex);
	};

	auto WriteInner = [&](FProperty* Inner)
	{
		if (IsFollowable(Inner))
			WritePropertyWrapper(Inner, GetPropertyType(Inner));
		else
			Buffer.Write(EPropertyType::Unknown);
	};

	auto WriteProperty = [&](FProperty*& Prop, EPropertyType Type)
	{
		if (Type == EPropertyType::EnumAsByteProperty)
			Buffer.Write(EPropertyType::EnumProperty);
		else Buffer.Write(Type);

		switch (Type)
		{
		case EPropertyType::EnumProperty:
		{
			auto EnumProp = static_cast<FEnumProperty*>(Prop);

			WriteInner(EnumProp->GetUnderlying());
			WriteTypeName(EnumProp->GetEnum());

			break;
		}
		case EPropertyType::EnumAsByteProperty:
		{
			Buffer.Write(EPropertyType::ByteProperty);
			WriteTypeName(static_cast<FByteProperty*>(Prop)->GetEnum());

			break;
		}
		case EPropertyType::StructProperty:
		{
			WriteTypeName(static_cast<FStructProperty*>(Prop)->GetStruct());
			break;
		}
		case EPropertyType::SetProperty:
		{
			WriteInner(static_cast<FSetProperty*>(Prop)->GetElement());
			break;
		}
		case EPropertyType::ArrayProperty:
		{
			WriteInner(static_cast<FArrayProperty*>(Prop)->GetInner());
			break;
		}
		case EPropertyType::MapProperty:
		{
			WriteInner(static_cast<FMapProperty*>(Prop)->GetKey());
			WriteInner(static_cast<FMapProperty*>(Prop)->GetValue());

			break;
		}
		}
	};

	WritePropertyWrapper = WriteProperty;

	// The walk matches objects against these three classes. Resolving them goes through the whole
	// offset chain (name, outer, path), so when one comes back null the dump silently collects
	// nothing — which is exactly what an empty .usmap looks like afterwards.
	auto ClassClass = UClass::StaticClass();
	auto StructClass = UScriptStruct::StaticClass();
	auto EnumClass = UEnum::StaticClass();

	// Path matching depends on the whole outer chain rendering exactly as expected. The three names
	// are unique among UClass objects, so falling back to a name lookup keeps the dump working on a
	// build whose package paths read differently.
	if (!ClassClass) ClassClass = ObjObjects::FindObjectByName<UClass>(L"Class");
	if (!StructClass) StructClass = ObjObjects::FindObjectByName<UClass>(L"ScriptStruct");
	if (!EnumClass) EnumClass = ObjObjects::FindObjectByName<UClass>(L"Enum");

	UE_LOG("Walking %d objects; Class=%p ScriptStruct=%p Enum=%p",
		ObjObjects::Num(), (void*)ClassClass, (void*)StructClass, (void*)EnumClass);

	if (!ClassClass || !StructClass || !EnumClass)
	{
		// Show what the paths actually look like, so a wrong outer or name offset is visible
		// instead of having to be inferred.
		// Low slots are often unnamed, so report objects that resolved to a real name instead.
		int Shown = 0;
		for (int Index = 0; Index < ObjObjects::Num() && Shown < 5; Index++)
		{
			auto Sample = ObjObjects::GetObjectByIndex(Index);
			if (!Sample)
				continue;

			auto Name = Sample->GetName();
			if (Name.empty() || Name == L"None")
				continue;

			UE_LOG("  object %d: name=\"%S\" path=\"%S\"", Index, std::wstring(Name).c_str(), Sample->GetPath().c_str());
			Shown++;
		}

		if (!Shown)
			UE_LOG("  no object in the array resolved to a name at all");

		throw std::runtime_error(
			"the core classes could not be resolved by path or by name (Class / ScriptStruct / Enum); "
			"the object name offset does not match this build");
	}

	// fnexportAPI local patch: the walk reads offsets that a new engine version can move, and the
	// project is built with /EHa, so a bad offset arrives here as a caught access violation with no
	// indication of where it came from. Failures are attributed to a phase and an object instead,
	// and an object that faults is left out rather than being carried into serialization.
	int FailedStructs = 0;
	int FailedEnums = 0;
	int FailuresReported = 0;

	auto ReportFailure = [&](const char* Phase, UObject* Object)
		{
			if (FailuresReported++ >= 3)
				return;

			std::wstring Path;
			try { Path = Object->GetPath(); } catch (...) { Path = L"<unreadable>"; }

			UE_LOG("  failed while reading %s of \"%S\"", Phase, Path.c_str());
		};

	int Walked = 0;

	ObjObjects::ForEach([&](UObject*& Object)
		{
			// The walk is the slow part and caught faults make it slower, so it says where it is.
			if (++Walked % 50000 == 0)
			{
				UE_LOG("  ... %d/%d objects, %llu structs, %llu enums",
					Walked, ObjObjects::Num(),
					(unsigned long long)Structs.size(), (unsigned long long)Enums.size());
			}

			UClass* ObjectClass = nullptr;

			try
			{
				ObjectClass = Object->Class();
			}
			catch (...)
			{
				ReportFailure("the class pointer", Object);
				return;
			}

			if (ObjectClass == ClassClass || ObjectClass == StructClass)
			{
				auto Struct = static_cast<UStruct*>(Object);

				try
				{
					NameMap.insert_or_assign(Struct->GetFName(), 0);

					if (Struct->Super() && !NameMap.contains(Struct->Super()->GetFName()))
						NameMap.insert_or_assign(Struct->Super()->GetFName(), 0);

					// A chain read through a wrong offset does not always fault: it can point back
					// into itself and loop forever, which is what a hung dump looks like. No real
					// struct comes close to this many properties.
					// Kept small on purpose: a looping chain is walked to this bound before it is
					// given up on, and at 64K per struct that alone made a run take twenty minutes.
					constexpr int MaxPropertiesPerStruct = 4096;

					auto Props = Struct->ChildProperties();
					auto First = Props;
					int Count = 0;

					while (Props)
					{
						if (++Count > MaxPropertiesPerStruct)
							throw std::runtime_error("property chain did not terminate");

						NameMap.insert_or_assign(Props->GetFName(), 0);
						Props = static_cast<FProperty*>(Props->GetNext());

						if (Props == First)
							throw std::runtime_error("property chain loops back on itself");
					}
				}
				catch (...)
				{
					// Kept out of Structs: serializing it would fault again, further from the cause.
					FailedStructs++;
					ReportFailure("the properties", Object);
					return;
				}

				Structs.push_back(Struct);
			}
			else if (ObjectClass == EnumClass)
			{
				auto Enum = static_cast<UEnum*>(Object);

				try
				{
					NameMap.insert_or_assign(Enum->GetFName(), 0);

					auto& EnumNames = Enum->Names();

					// Without the derived offset this would read whatever happens to sit there.
					if (!UEnum::NamesOffset)
						throw std::runtime_error("the enum member data offset was not derived");

					// A garbage count would walk off the end of the arrays.
					constexpr int MaxEnumMembers = 4096;
					if (EnumNames.Num() <= 0 || EnumNames.Num() > MaxEnumMembers)
						throw std::runtime_error("implausible enum member count");

					for (auto i = 0; i < EnumNames.Num(); i++)
					{
						// The whole FName, not just its index: the other words are needed to render it.
						NameMap.insert_or_assign(EnumNames[i].Key, 0);
					}
				}
				catch (...)
				{
					FailedEnums++;
					ReportFailure("the enum members", Object);
					return;
				}

				Enums.push_back(Enum);
			}
		});

	if (FailedStructs || FailedEnums)
	{
		UE_LOG("Skipped %d structs and %d enums that could not be read", FailedStructs, FailedEnums);
	}

	Buffer.Write<int>(NameMap.size());

	int CurrentNameIndex = 0;

	for (auto&& N : NameMap)
	{
		NameMap[N.first] = CurrentNameIndex;

		auto Name = N.first.ToString();
		std::string_view NameView = Name;

		auto Find = Name.find("::");
		if (Find != std::string::npos)
		{
			NameView = NameView.substr(Find + 2);
		}

		Buffer.Write<uint16_t>((uint16_t)NameView.length());
		Buffer.WriteString(NameView);

		CurrentNameIndex++;
	}

	Buffer.Write<uint32_t>(Enums.size());

	for (auto Enum : Enums)
	{
		Buffer.Write(NameMap[Enum->GetFName()]);

		auto& EnumNames = Enum->Names();
		Buffer.Write<uint16_t>((uint16_t)EnumNames.Num());

		for (size_t i = 0; i < EnumNames.Num(); i++)
		{
			Buffer.Write<int>(NameMap[EnumNames[i].Key]);
		}
	}

	Buffer.Write<uint32_t>(Structs.size());

	// Names the struct being written, so a fault at this stage says where it came from.
	UStruct* CurrentStruct = nullptr;

	try
	{

	for (auto Struct : Structs)
	{
		CurrentStruct = Struct;
		Buffer.Write(NameMap[Struct->GetFName()]);
		Buffer.Write<int32_t>(Struct->Super() ? NameMap[Struct->Super()->GetFName()] : 0xffffffff);

		std::vector<FPropertyData> Properties;

		auto Props = Struct->ChildProperties();
		uint16_t PropCount = 0;
		uint16_t SerializablePropCount = 0;

		while (Props)
		{
			FPropertyData Data(Props, PropCount);

			Properties.push_back(Data);
			Props = static_cast<FProperty*>(Props->GetNext());

			PropCount += Data.ArrayDim;
			SerializablePropCount++;
		}

		Buffer.Write(PropCount);
		Buffer.Write(SerializablePropCount);

		for (auto P : Properties)
		{
			Buffer.Write<uint16_t>(P.Index);
			Buffer.Write(P.ArrayDim);
			Buffer.Write(NameMap[P.Name]);

			WriteProperty(P.Prop, P.PropertyType);
		}
	}

	}
	catch (...)
	{
		std::wstring Where = L"<unknown>";
		try { if (CurrentStruct) Where = CurrentStruct->GetPath(); } catch (...) {}

		UE_LOG("Failed while writing \"%S\"", Where.c_str());
		throw;
	}

	std::vector<uint8_t> UsmapData;

	switch (CompressionMethod)
	{
	case ECompressionMethod::Oodle:
	{
		UsmapData = Oodle::Compress(Buffer.GetBuffer());
		break;
	}
	default:
	{
		std::string UncompressedStream = Buffer.GetBuffer().str();
		UsmapData.resize(UncompressedStream.size());
		memcpy(UsmapData.data(), UncompressedStream.data(), UsmapData.size());
	}
	}

	// fnexportAPI local patch: the output path comes from the host config (upstream wrote
	// Mappings.usmap into the game's working directory).
	UE_LOG("Collected %llu structs and %llu enums",
		(unsigned long long)Structs.size(), (unsigned long long)Enums.size());

	if (Structs.empty() && Enums.empty())
	{
		throw std::runtime_error(
			"the object walk collected nothing; GObjects resolved but the class comparison never "
			"matched, so the offsets do not describe this build");
	}

	auto FileOutput = FileWriter(HostConfig::Output.c_str());
	if (!FileOutput.IsOpen())
	{
		// Thrown rather than returned: dllmain turns the exception into a HOST_RESULT failed line, so
		// the host reports the unwritable path instead of "succeeded but wrote nothing".
		throw std::runtime_error("could not open the output file for writing: " + HostConfig::Output);
	}

	FileOutput.Write<uint16_t>(0x30C4); //magic

	// fnexportAPI local patch: written as LargeEnums (3) rather than the original 0.
	//
	// Version 0 stores an enum's member count in a single byte, so an enum with more than 255
	// members writes a truncated count followed by the full list of entries — the reader then
	// resumes mid-list and every following record is garbage. UE6 Fortnite has such enums, which
	// is why a dump that collected everything correctly still failed to parse back.
	//
	// The three steps up to LargeEnums are cumulative: PackageVersioning adds the flag below,
	// LongFName widens name lengths to 16 bits, LargeEnums widens the member count the same way.
	FileOutput.Write<uint8_t>(3); //version
	// The reader takes this flag as a UE-serialized bool, which is four bytes wide, not one.
	FileOutput.Write<uint32_t>(0); //no package versioning payload follows
	FileOutput.Write(CompressionMethod); //compression
	FileOutput.Write<uint32_t>(UsmapData.size()); //compressed size
	FileOutput.Write<uint32_t>(Buffer.Size()); //decompressed size

	FileOutput.Write(UsmapData.data(), UsmapData.size());
}