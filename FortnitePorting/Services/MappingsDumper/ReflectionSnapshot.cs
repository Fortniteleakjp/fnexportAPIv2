using System.Collections.Generic;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// The recursive usmap type record of a single property, mirroring what UnrealMappingsDumper's
/// <c>WriteProperty</c> emits (dumper.cpp): a type byte plus the extra fields that type needs.
/// </summary>
public sealed class DumpedPropertyType
{
    /// <summary>The usmap property type byte.</summary>
    public EPropertyType Type = EPropertyType.Unknown;

    /// <summary>Target struct of a StructProperty (for example <c>Vector2D</c>).</summary>
    public string? StructName;

    /// <summary>Target enum of an EnumProperty, including the TEnumAsByte form.</summary>
    public string? EnumName;

    /// <summary>
    /// Array/Set element, Map key, Optional value, or the underlying numeric type of an EnumProperty
    /// (UnrealMappingsDumper writes the underlying type first, then the enum name).
    /// </summary>
    public DumpedPropertyType? Inner;

    /// <summary>Map value type.</summary>
    public DumpedPropertyType? Value;

    public static DumpedPropertyType Of(EPropertyType type) => new() { Type = type };
}

/// <summary>One serialized property of a struct/class — UnrealMappingsDumper's <c>FPropertyData</c>.</summary>
public sealed class DumpedProperty
{
    public string Name = string.Empty;

    /// <summary>Static array length (<c>ArrayDim</c>); each element takes its own schema slot.</summary>
    public int ArrayDim = 1;

    public DumpedPropertyType Type = DumpedPropertyType.Of(EPropertyType.Unknown);

    /// <summary>
    /// Schema index to write instead of the running slot counter. Only set when the property was read
    /// back from an existing .usmap, so a merged mapping reproduces that file's indices exactly.
    /// </summary>
    public int? SchemaIndex;
}

/// <summary>A UClass or UScriptStruct as the dumper sees it.</summary>
public sealed class DumpedStruct
{
    public string Name = string.Empty;

    /// <summary>Super struct name, or null when the type has no super (written as index -1).</summary>
    public string? Super;

    public List<DumpedProperty> Properties = [];

    /// <summary>
    /// Own schema-slot count to write instead of the sum of the property array dims. Only set when the
    /// struct came from an existing .usmap, for byte-exact round-tripping.
    /// </summary>
    public int? PropertyCountOverride;
}

/// <summary>A UEnum and its members.</summary>
public sealed class DumpedEnum
{
    public string Name = string.Empty;

    /// <summary>Members in declaration order. Values are only written from usmap version 4 onwards.</summary>
    public List<DumpedEnumMember> Members = [];
}

public sealed class DumpedEnumMember
{
    public string Name = string.Empty;
    public long Value;
}

/// <summary>
/// The set of reflected types a dump run collected — the server-side stand-in for the
/// <c>GObjects</c> walk UnrealMappingsDumper performs inside a running game
/// (<c>ObjObjects::ForEach</c> in dumper.cpp). Everything the serializer needs lives here, so the
/// same writer can serve types collected from the mounted paks, from an existing .usmap, or both.
/// </summary>
public sealed class ReflectionSnapshot
{
    public readonly Dictionary<string, DumpedStruct> Structs = new(StringComparer.Ordinal);
    public readonly Dictionary<string, DumpedEnum> Enums = new(StringComparer.Ordinal);

    /// <summary>Adds a struct. Existing entries are kept unless <paramref name="overwrite"/> is set.</summary>
    public bool AddStruct(DumpedStruct s, bool overwrite = true)
    {
        if (string.IsNullOrEmpty(s.Name)) return false;
        if (!overwrite && Structs.ContainsKey(s.Name)) return false;
        Structs[s.Name] = s;
        return true;
    }

    /// <summary>Adds an enum. Existing entries are kept unless <paramref name="overwrite"/> is set.</summary>
    public bool AddEnum(DumpedEnum e, bool overwrite = true)
    {
        if (string.IsNullOrEmpty(e.Name)) return false;
        if (!overwrite && Enums.ContainsKey(e.Name)) return false;
        Enums[e.Name] = e;
        return true;
    }

    /// <summary>
    /// Merges <paramref name="other"/> into this snapshot. Types already present here win, so a
    /// freshly dumped type is never replaced by the one from the base mapping.
    /// </summary>
    public (int Structs, int Enums) MergeMissingFrom(ReflectionSnapshot other)
    {
        var structs = 0;
        var enums = 0;
        foreach (var s in other.Structs.Values)
            if (AddStruct(s, overwrite: false)) structs++;
        foreach (var e in other.Enums.Values)
            if (AddEnum(e, overwrite: false)) enums++;
        return (structs, enums);
    }
}
