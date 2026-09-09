#include "pch.h"

#include "app.h"
#include "dumper.h"
#include "hostConfig.h"
#include "writer.h"
#include "oodle.h"

static EPropertyType GetPropertyType(FProperty* Prop)
{
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

			auto Inner = EnumProp->GetUnderlying();
			auto InnerType = GetPropertyType(Inner);
			WritePropertyWrapper(Inner, InnerType);
			Buffer.Write(NameMap[EnumProp->GetEnum()->GetFName()]);

			break;
		}
		case EPropertyType::EnumAsByteProperty:
		{
			Buffer.Write(EPropertyType::ByteProperty);
			Buffer.Write(NameMap[static_cast<FByteProperty*>(Prop)->GetEnum()->GetFName()]);

			break;
		}
		case EPropertyType::StructProperty:
		{
			Buffer.Write(NameMap[static_cast<FStructProperty*>(Prop)->GetStruct()->GetFName()]);
			break;
		}
		case EPropertyType::SetProperty:
		case EPropertyType::ArrayProperty:
		{
			auto Inner = static_cast<FArrayProperty*>(Prop)->GetInner();
			auto InnerType = GetPropertyType(Inner);
			WritePropertyWrapper(Inner, InnerType);

			break;
		}
		case EPropertyType::MapProperty:
		{
			auto Inner = static_cast<FMapProperty*>(Prop)->GetKey();
			auto InnerType = GetPropertyType(Inner);
			WritePropertyWrapper(Inner, InnerType);

			auto Value = static_cast<FMapProperty*>(Prop)->GetValue();
			auto ValueType = GetPropertyType(Value);
			WritePropertyWrapper(Value, ValueType);

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
					constexpr int MaxPropertiesPerStruct = 65536;

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

					for (auto i = 0; i < EnumNames.Num(); i++)
					{
						NameMap.insert_or_assign(EnumNames[i].Key.GetNumber(), 0);
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

		Buffer.Write<uint8_t>(NameView.length());
		Buffer.WriteString(NameView);

		CurrentNameIndex++;
	}

	Buffer.Write<uint32_t>(Enums.size());

	for (auto Enum : Enums)
	{
		Buffer.Write(NameMap[Enum->GetFName()]);

		auto& EnumNames = Enum->Names();
		Buffer.Write<uint8_t>(EnumNames.Num());

		for (size_t i = 0; i < EnumNames.Num(); i++)
		{
			Buffer.Write<int>(NameMap[EnumNames[i].Key]);
		}
	}

	Buffer.Write<uint32_t>(Structs.size());

	for (auto Struct : Structs)
	{
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
	FileOutput.Write<uint8_t>(0); //version
	FileOutput.Write(CompressionMethod); //compression
	FileOutput.Write<uint32_t>(UsmapData.size()); //compressed size
	FileOutput.Write<uint32_t>(Buffer.Size()); //decompressed size

	FileOutput.Write(UsmapData.data(), UsmapData.size());
}