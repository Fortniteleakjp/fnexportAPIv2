using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services;

/// <summary>
/// Generates a binary <c>.usmap</c> mappings file from a "StormForge"-style mappings JSON dump
/// (an object shaped like <c>{ Version, Enums[], Structs[], Classes[] }</c>). The output is a
/// standard, uncompressed .usmap at version <c>ExplicitEnumValues</c> that CUE4Parse can load
/// directly (FileUsmapTypeMappingsProvider / UsmapParser).
///
/// This is a pure format conversion — it does NOT derive type layouts from cooked paks (which is
/// impossible); it serializes the layouts already present in the JSON into the compact binary form.
/// </summary>
public static class UsmapGenerator
{
    private const ushort FileMagic = 0x30C4;
    private const byte UsmapVersion = 4; // EUsmapVersion.ExplicitEnumValues (== Latest)
    private const int MaxArrayDim = 255;  // the .usmap arrayDim field is a single byte
    private const int MaxCount = ushort.MaxValue; // enum-member / property counts are 16-bit

    // Editor-only / cooked-stripped property flag. The StormForge JSON is a FULL (editor) reflection
    // dump, but the cooked runtime serialization schema (which a .usmap describes) omits these
    // properties. Including them shifts every later property's schema index and breaks deserialization.
    // Empirically this bit cleanly separates the cooked schema (verified: excluding it reproduces the
    // official .usmap for 52,911 / 52,914 structs; the few remainders are cross-CL content drift).
    private const ulong EditorOnlyPropertyFlag = 0x0000000800000000UL;

    public sealed class Result
    {
        public byte[] Usmap = Array.Empty<byte>();
        public int Names;
        public int Enums;
        public int Structs;
        public int UnknownProps;
        public int OptionalProps;
        public int SkippedNamelessTypes;
        public int SkippedNamelessEnums;
        public int SkippedEditorOnlyProps;
    }

    public static Result Generate(byte[] jsonBytes)
    {
        using var doc = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 512 });
        var root = doc.RootElement;

        var names = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        int AddName(string? s)
        {
            if (s == null) return -1;
            if (nameIndex.TryGetValue(s, out var idx)) return idx;
            idx = names.Count;
            names.Add(s);
            nameIndex[s] = idx;
            return idx;
        }

        var result = new Result();

        // Build the enums + structs section into a temp buffer, populating the name table as we go.
        // (Name indices are append-only, so they stay valid once the table is emitted in front.)
        using var tail = new MemoryStream();
        using (var w = new BinaryWriter(tail, Encoding.Latin1, leaveOpen: true))
        {
            // --- Enums --- (skip nameless entries: a -1 name index crashes the reader's dictionary)
            var allEnums = GetArray(root, "Enums");
            var enums = allEnums == null
                ? new List<JsonElement>()
                : allEnums.Where(e => !string.IsNullOrEmpty(GetStr(e, "Name"))).ToList();
            if (allEnums != null) result.SkippedNamelessEnums = allEnums.Count - enums.Count;

            w.Write((uint)enums.Count);
            foreach (var en in enums)
            {
                w.Write(AddName(GetStr(en, "Name")));
                var members = GetArray(en, "Members");
                var count = Math.Min(members?.Count ?? 0, MaxCount);
                w.Write((ushort)count);
                for (var j = 0; j < count; j++)
                {
                    var m = members![j];
                    long val = m.TryGetProperty("Value", out var v) && v.TryGetInt64(out var lv) ? lv : j;
                    w.Write((ulong)val);
                    w.Write(AddName(GetStr(m, "Name"))); // member name is a dictionary value -> null is tolerated
                }
                result.Enums++;
            }

            // --- Structs + Classes --- (both serialize as usmap "structs"; skip nameless entries)
            var types = new List<JsonElement>();
            var structs = GetArray(root, "Structs");
            var classes = GetArray(root, "Classes");
            if (structs != null) types.AddRange(structs);
            if (classes != null) types.AddRange(classes);
            var validTypes = types.Where(t => !string.IsNullOrEmpty(GetStr(t, "Name"))).ToList();
            result.SkippedNamelessTypes = types.Count - validTypes.Count;

            w.Write((uint)validTypes.Count);
            foreach (var t in validTypes)
            {
                WriteType(w, t, AddName, result);
            }
        }

        result.Names = names.Count;

        // Assemble the uncompressed body: name table, then the enums/structs section.
        byte[] body;
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
            {
                bw.Write((uint)names.Count);
                foreach (var n in names)
                {
                    var bytes = Encoding.Latin1.GetBytes(n); // 1 byte/char, preserves 0x00-0xFF (reader is byte->char)
                    bw.Write((ushort)Math.Min(bytes.Length, MaxCount)); // LongFName: 16-bit length
                    bw.Write(bytes, 0, Math.Min(bytes.Length, MaxCount));
                }
                bw.Write(tail.ToArray());
            }
            body = ms.ToArray();
        }

        // Container header (CompressionMethod.None) + body.
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
            {
                bw.Write(FileMagic);             // ushort magic
                bw.Write(UsmapVersion);          // byte version
                bw.Write(0);                     // bHasVersioning = false — UE bool is a 4-byte int32
                bw.Write((byte)0);               // EUsmapCompressionMethod.None
                bw.Write((uint)body.Length);     // compressed size
                bw.Write((uint)body.Length);     // decompressed size
                bw.Write(body);
            }
            result.Usmap = ms.ToArray();
        }

        return result;
    }

    private static void WriteType(BinaryWriter w, JsonElement t, Func<string?, int> addName, Result result)
    {
        w.Write(addName(GetStr(t, "Name")));               // non-null (pre-filtered)
        w.Write(addName(SimpleName(GetStr(t, "Parent"))));

        // Keep only the cooked-serialized properties (drop editor-only ones) so the schema indices
        // match the runtime layout — exactly what the official .usmap encodes.
        var allProps = GetArray(t, "Properties");
        List<JsonElement>? props = null;
        if (allProps != null)
        {
            props = new List<JsonElement>(allProps.Count);
            foreach (var p in allProps)
            {
                if ((GetFlags(p) & EditorOnlyPropertyFlag) != 0) { result.SkippedEditorOnlyProps++; continue; }
                props.Add(p);
            }
        }

        // The serialized-property count and the property records emitted MUST be identical.
        var emit = Math.Min(props?.Count ?? 0, MaxCount);

        // Schema-slot count, accumulated with the SAME clamped dim the reader will expand.
        var totalSlots = 0;
        if (props != null)
        {
            for (var i = 0; i < emit; i++) totalSlots += ClampedDim(props[i]);
        }

        w.Write((ushort)Math.Min(totalSlots, MaxCount));   // propertyCount (own schema slots)
        w.Write((ushort)emit);                             // serializablePropertyCount

        if (props != null)
        {
            var running = 0;
            for (var i = 0; i < emit; i++)
            {
                var p = props[i];
                var dim = ClampedDim(p);
                w.Write((ushort)Math.Min(running, MaxCount)); // schema index
                w.Write((byte)dim);                           // array dim (byte field; clamped consistently)
                w.Write(addName(GetStr(p, "Name")));
                WritePropType(w, p, addName, result);
                running += dim;
            }
        }

        result.Structs++;
    }

    private static void WritePropType(BinaryWriter w, JsonElement p, Func<string?, int> addName, Result result)
    {
        var e = MapType(GetStr(p, "Type"));

        // TEnumAsByte: the JSON encodes it as a ByteProperty carrying the enum in InnerType, but a
        // .usmap (and the official mapping) represents it as an EnumProperty with a Byte underlying.
        if (e == EPropertyType.ByteProperty && GetStr(p, "InnerType") != null)
        {
            e = EPropertyType.EnumProperty;
        }

        // Multicast delegates (inline / sparse) carry no serialized data; the official .usmap encodes
        // them all as MulticastDelegateProperty. The dumper marks sparse ones as Unknown, so recover
        // those from CppType. (Matches the official encoding; harmless for deserialization either way.)
        if (e == EPropertyType.MulticastInlineDelegateProperty)
        {
            e = EPropertyType.MulticastDelegateProperty;
        }
        else if (e == EPropertyType.Unknown)
        {
            var cpp = GetStr(p, "CppType");
            if (cpp != null && cpp.Contains("Multicast") && cpp.Contains("Delegate"))
            {
                e = EPropertyType.MulticastDelegateProperty;
            }
        }

        // OptionalProperty's inner type isn't given structurally in the JSON; recover it from CppType
        // (e.g. "TOptional<FBox>" -> Struct<Box>, "TOptional<double>" -> Double). Unresolvable inners
        // (Verse/empty) fall back to Unknown. This keeps optionals deserializable for the common cases.
        if (e == EPropertyType.OptionalProperty)
        {
            result.OptionalProps++;
            w.Write((byte)EPropertyType.OptionalProperty);
            WriteCppInner(w, ExtractGeneric(GetStr(p, "CppType")), addName);
            return;
        }
        if (e == EPropertyType.Unknown) result.UnknownProps++;

        w.Write((byte)e);
        switch (e)
        {
            case EPropertyType.EnumProperty:
                w.Write((byte)UnderlyingFromSize(p));                  // underlying numeric type
                w.Write(addName(SimpleName(GetStr(p, "InnerType"))));  // enum name
                break;
            case EPropertyType.StructProperty:
                w.Write(addName(SimpleName(GetStr(p, "InnerType"))));  // struct name
                break;
            case EPropertyType.ArrayProperty:
            case EPropertyType.SetProperty:
                WriteInner(w, p, "ArrayInnerType", addName, result);
                break;
            case EPropertyType.MapProperty:
                WriteInner(w, p, "KeyProperty", addName, result);
                WriteInner(w, p, "ValueProperty", addName, result);
                break;
        }
    }

    private static void WriteInner(BinaryWriter w, JsonElement p, string field, Func<string?, int> addName, Result result)
    {
        if (p.TryGetProperty(field, out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            WritePropType(w, inner, addName, result);
        }
        else
        {
            // Missing inner type — keep the format valid.
            w.Write((byte)EPropertyType.Unknown);
        }
    }

    /// <summary>Extracts the inner of a single-arg template, e.g. "TOptional&lt;FBox&gt;" -> "FBox".</summary>
    private static string ExtractGeneric(string? cppType)
    {
        if (string.IsNullOrEmpty(cppType)) return "";
        var lt = cppType.IndexOf('<');
        var gt = cppType.LastIndexOf('>');
        return (lt >= 0 && gt > lt) ? cppType.Substring(lt + 1, gt - lt - 1).Trim() : "";
    }

    /// <summary>Writes a best-effort usmap property type for a C++ inner type name (used for TOptional&lt;T&gt;).</summary>
    private static void WriteCppInner(BinaryWriter w, string inner, Func<string?, int> addName)
    {
        inner = inner.Trim();
        if (inner.Length == 0) { w.Write((byte)EPropertyType.Unknown); return; }
        if (inner.EndsWith("*")) { w.Write((byte)EPropertyType.ObjectProperty); return; } // U.../A... pointer

        EPropertyType prim = inner switch
        {
            "double" => EPropertyType.DoubleProperty,
            "float" => EPropertyType.FloatProperty,
            "bool" => EPropertyType.BoolProperty,
            "int64" or "int64_t" => EPropertyType.Int64Property,
            "uint64" => EPropertyType.UInt64Property,
            "int32" or "int" => EPropertyType.IntProperty,
            "uint32" => EPropertyType.UInt32Property,
            "int16" => EPropertyType.Int16Property,
            "uint16" => EPropertyType.UInt16Property,
            "int8" => EPropertyType.Int8Property,
            "uint8" or "char" or "byte" => EPropertyType.ByteProperty,
            "FName" => EPropertyType.NameProperty,
            "FString" => EPropertyType.StrProperty,
            "FText" => EPropertyType.TextProperty,
            _ => (EPropertyType)0xFF,
        };
        if (prim != (EPropertyType)0xFF && Enum.IsDefined(prim)) { w.Write((byte)prim); return; }

        if (inner.StartsWith("TArray") || inner.StartsWith("TSet"))
        {
            // element type isn't recoverable from the bare CppType
            w.Write((byte)(inner.StartsWith("TSet") ? EPropertyType.SetProperty : EPropertyType.ArrayProperty));
            w.Write((byte)EPropertyType.Unknown);
            return;
        }
        if (inner.Length > 1 && inner[0] == 'F') // F-prefixed struct -> Struct<Name without F>
        {
            w.Write((byte)EPropertyType.StructProperty);
            w.Write(addName(inner.Substring(1)));
            return;
        }

        w.Write((byte)EPropertyType.Unknown);
    }

    private static EPropertyType UnderlyingFromSize(JsonElement p)
    {
        var size = p.TryGetProperty("Size", out var s) && s.TryGetInt32(out var v) ? v : 1;
        return size switch
        {
            8 => EPropertyType.Int64Property,
            4 => EPropertyType.IntProperty,
            2 => EPropertyType.UInt16Property,
            _ => EPropertyType.ByteProperty,
        };
    }

    private static EPropertyType MapType(string? type)
        => !string.IsNullOrEmpty(type) && Enum.TryParse<EPropertyType>(type, out var e) ? e : EPropertyType.Unknown;

    private static int ClampedDim(JsonElement p) => Math.Clamp(ArrayDim(p), 1, MaxArrayDim);

    private static int ArrayDim(JsonElement p)
        => p.TryGetProperty("ArrayDim", out var a) && a.TryGetInt32(out var v) && v > 0 ? v : 1;

    private static ulong GetFlags(JsonElement p)
    {
        if (!p.TryGetProperty("Flags", out var f)) return 0;
        if (f.ValueKind == JsonValueKind.Number && f.TryGetUInt64(out var n)) return n;
        if (f.ValueKind == JsonValueKind.String && ulong.TryParse(f.GetString(), out var s)) return s;
        return 0;
    }

    private static string? GetStr(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<JsonElement>? GetArray(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var list = new List<JsonElement>(a.GetArrayLength());
        foreach (var e in a.EnumerateArray()) list.Add(e);
        return list;
    }

    /// <summary>"/Script/CoreUObject.Vector2D" -> "Vector2D"; "None"/null -> null.</summary>
    private static string? SimpleName(string? full)
    {
        if (string.IsNullOrEmpty(full) || full == "None") return null;
        var dot = full.LastIndexOf('.');
        var name = dot >= 0 ? full[(dot + 1)..] : full;
        return name.Length == 0 ? null : name;
    }
}
