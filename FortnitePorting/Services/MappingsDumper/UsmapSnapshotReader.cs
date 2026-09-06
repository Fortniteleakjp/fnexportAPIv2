using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Reads an existing <c>.usmap</c> back into a <see cref="ReflectionSnapshot"/> so it can be merged
/// with a fresh dump and re-serialized.
///
/// This is what makes a pak-side dump useful on its own: UnrealMappingsDumper sees every type in
/// GObjects, including the native <c>/Script/...</c> ones compiled into the executable, but those
/// types are not present in the cooked paks. Loading the build's published mapping as the base and
/// letting the dumped types win produces a mapping that has both halves.
/// </summary>
public static class UsmapSnapshotReader
{
    public static ReflectionSnapshot Read(byte[] usmap, string name = "base.usmap")
        => FromMappings(new UsmapParser(usmap, name).Mappings);

    public static ReflectionSnapshot ReadFile(string path)
        => FromMappings(new UsmapParser(path, System.IO.Path.GetFileName(path)).Mappings);

    public static ReflectionSnapshot FromMappings(TypeMappings? mappings)
    {
        var snapshot = new ReflectionSnapshot();
        if (mappings == null) return snapshot;

        foreach (var (enumName, members) in mappings.Enums)
        {
            var dumped = new DumpedEnum { Name = enumName };
            foreach (var (value, member) in members.OrderBy(m => m.Key))
            {
                dumped.Members.Add(new DumpedEnumMember { Name = member, Value = value });
            }
            snapshot.AddEnum(dumped);
        }

        foreach (var (typeName, type) in mappings.Types)
        {
            snapshot.AddStruct(ConvertStruct(typeName, type));
        }

        return snapshot;
    }

    private static DumpedStruct ConvertStruct(string name, Struct type)
    {
        var dumped = new DumpedStruct
        {
            Name = string.IsNullOrEmpty(type.Name) ? name : type.Name,
            Super = type.SuperType,
            PropertyCountOverride = type.PropertyCount
        };

        // The parser expands a static array into one entry per element, cloning the property and
        // numbering the clones 0..ArraySize-1. The clone numbered 0 sits at the schema index the file
        // actually stored, so those entries rebuild the original property list.
        if (type.Properties != null)
        {
            foreach (var (schemaIndex, info) in type.Properties.Where(p => p.Value.Index == 0).OrderBy(p => p.Key))
            {
                dumped.Properties.Add(new DumpedProperty
                {
                    Name = info.Name,
                    ArrayDim = info.ArraySize is > 0 ? info.ArraySize.Value : 1,
                    SchemaIndex = schemaIndex,
                    Type = ConvertType(info.MappingType)
                });
            }
        }

        return dumped;
    }

    private static DumpedPropertyType ConvertType(PropertyType? type)
    {
        if (type == null) return DumpedPropertyType.Of(EPropertyType.Unknown);

        return new DumpedPropertyType
        {
            Type = Enum.TryParse<EPropertyType>(type.Type, out var parsed) ? parsed : EPropertyType.Unknown,
            StructName = type.StructType,
            EnumName = type.EnumName,
            Inner = type.InnerType != null ? ConvertType(type.InnerType) : null,
            Value = type.ValueType != null ? ConvertType(type.ValueType) : null
        };
    }
}
