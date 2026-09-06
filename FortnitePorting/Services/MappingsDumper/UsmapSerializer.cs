using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Writes a <c>.usmap</c> file from a <see cref="ReflectionSnapshot"/>, following the exact
/// serialization UnrealMappingsDumper performs in <c>Dumper::Run</c> (dumper.cpp): a name table,
/// then every enum, then every struct with its property records, wrapped in the <c>0x30C4</c>
/// container header.
///
/// The dumper writes usmap version 0 (<see cref="EUsmapVersion.Initial"/>); this writer keeps that
/// layout available and additionally supports the newer revisions CUE4Parse/FModel read (16-bit name
/// lengths, enums with more than 255 members, explicit enum values), which is what
/// <see cref="EUsmapVersion.Latest"/> selects.
/// </summary>
public static class UsmapSerializer
{
    private const ushort FileMagic = 0x30C4;
    private const int MaxArrayDim = byte.MaxValue;  // the arrayDim field is one byte
    private const int MaxCount = ushort.MaxValue;   // property counts are 16-bit
    private const int InvalidNameIndex = -1;

    /// <summary>Serialization options for one dump.</summary>
    public sealed class Options
    {
        public EUsmapVersion Version = EUsmapVersion.Latest;
        public EUsmapCompressionMethod Compression = EUsmapCompressionMethod.None;

        /// <summary>Zstandard compression level (1-22); ignored for other methods.</summary>
        public int ZstdLevel = 17;
    }

    /// <summary>What a serialization run produced.</summary>
    public sealed class Result
    {
        public byte[] Usmap = [];
        public int Names;
        public int Enums;
        public int Structs;
        public int Properties;
        public int UnknownProperties;
        public int TruncatedNames;
        public int TruncatedEnums;
        public int UncompressedBytes;
        public EUsmapVersion Version;
        public EUsmapCompressionMethod Compression;
    }

    public static Result Serialize(ReflectionSnapshot snapshot, Options? options = null)
    {
        var o = options ?? new Options();
        var result = new Result { Version = o.Version, Compression = o.Compression };

        var names = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        // Names are interned while the enum/struct section is written into a temporary buffer, then the
        // finished table is emitted in front of it. Indices are append-only, so they stay valid.
        // Only a null name means "no reference" (index -1). "None" is deliberately not special-cased:
        // it is a legitimate name in the table, and some cooked types really do own a property called
        // None — writing that as -1 would lose the property when the file is read back.
        int AddName(string? s)
        {
            if (s == null) return InvalidNameIndex;
            if (nameIndex.TryGetValue(s, out var idx)) return idx;
            idx = names.Count;
            names.Add(s);
            nameIndex[s] = idx;
            return idx;
        }

        using var tail = new MemoryStream();
        using (var w = new BinaryWriter(tail, Encoding.UTF8, leaveOpen: true))
        {
            var enums = snapshot.Enums.Values;
            w.Write((uint) enums.Count);
            foreach (var e in enums)
            {
                w.Write(AddName(e.Name));

                var maxMembers = o.Version >= EUsmapVersion.LargeEnums ? ushort.MaxValue : byte.MaxValue;
                var count = e.Members.Count;
                if (count > maxMembers)
                {
                    count = maxMembers;
                    result.TruncatedEnums++;
                }

                if (o.Version >= EUsmapVersion.LargeEnums) w.Write((ushort) count);
                else w.Write((byte) count);

                for (var i = 0; i < count; i++)
                {
                    var member = e.Members[i];
                    // From ExplicitEnumValues on, each member carries its real value; before that the
                    // reader derives the value from the member position, which is what the original
                    // dumper relies on.
                    if (o.Version >= EUsmapVersion.ExplicitEnumValues) w.Write((ulong) member.Value);
                    w.Write(AddName(member.Name));
                }

                result.Enums++;
            }

            var structs = snapshot.Structs.Values;
            w.Write((uint) structs.Count);
            foreach (var s in structs)
            {
                WriteStruct(w, s, AddName, result);
            }
        }

        // Assemble the body: name table first, then the enum/struct section written above.
        byte[] body;
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write((uint) names.Count);
                foreach (var n in names)
                {
                    var bytes = Encoding.UTF8.GetBytes(n);
                    var maxLength = o.Version >= EUsmapVersion.LongFName ? ushort.MaxValue : byte.MaxValue;
                    if (bytes.Length > maxLength)
                    {
                        bytes = bytes[..maxLength];
                        result.TruncatedNames++;
                    }

                    if (o.Version >= EUsmapVersion.LongFName) bw.Write((ushort) bytes.Length);
                    else bw.Write((byte) bytes.Length);
                    bw.Write(bytes);
                }

                bw.Write(tail.GetBuffer(), 0, (int) tail.Length);
            }
            body = ms.ToArray();
        }

        result.Names = names.Count;
        result.UncompressedBytes = body.Length;

        var payload = Compress(body, o);

        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(FileMagic);
                bw.Write((byte) o.Version);
                // Versioning block: only present from version 1 on. A UE bool is a 4-byte int32.
                if (o.Version >= EUsmapVersion.PackageVersioning) bw.Write(0);
                bw.Write((byte) o.Compression);
                bw.Write((uint) payload.Length);
                bw.Write((uint) body.Length);
                bw.Write(payload);
            }
            result.Usmap = ms.ToArray();
        }

        return result;
    }

    private static void WriteStruct(BinaryWriter w, DumpedStruct s, Func<string?, int> addName, Result result)
    {
        w.Write(addName(s.Name));
        w.Write(addName(s.Super)); // -1 when there is no super, which the reader maps to null

        var props = s.Properties;
        var emit = Math.Min(props.Count, MaxCount);

        // Schema slots this struct owns: every static-array element takes a slot, exactly as the dumper
        // accumulates PropCount from each property ArrayDim.
        var slots = 0;
        for (var i = 0; i < emit; i++) slots += ClampDim(props[i].ArrayDim);

        w.Write((ushort) Math.Min(s.PropertyCountOverride ?? slots, MaxCount));
        w.Write((ushort) emit);

        var running = 0;
        for (var i = 0; i < emit; i++)
        {
            var p = props[i];
            var dim = ClampDim(p.ArrayDim);
            w.Write((ushort) Math.Min(p.SchemaIndex ?? running, MaxCount));
            w.Write((byte) dim);
            w.Write(addName(p.Name));
            WritePropertyType(w, p.Type, addName, result);
            running += dim;
            result.Properties++;
        }

        result.Structs++;
    }

    private static void WritePropertyType(BinaryWriter w, DumpedPropertyType? t, Func<string?, int> addName, Result result)
    {
        if (t == null)
        {
            // Keeps the record structurally valid when an inner type could not be resolved.
            w.Write((byte) EPropertyType.Unknown);
            result.UnknownProperties++;
            return;
        }

        if (t.Type == EPropertyType.Unknown) result.UnknownProperties++;
        w.Write((byte) t.Type);

        switch (t.Type)
        {
            case EPropertyType.EnumProperty:
                // Underlying numeric type first, then the enum name — the order the dumper writes, and
                // what UsmapProperties.ParsePropertyType expects when reading it back.
                WritePropertyType(w, t.Inner ?? DumpedPropertyType.Of(EPropertyType.ByteProperty), addName, result);
                w.Write(addName(t.EnumName));
                break;
            case EPropertyType.StructProperty:
                w.Write(addName(t.StructName));
                break;
            case EPropertyType.ArrayProperty:
            case EPropertyType.SetProperty:
            case EPropertyType.OptionalProperty:
                WritePropertyType(w, t.Inner, addName, result);
                break;
            case EPropertyType.MapProperty:
                WritePropertyType(w, t.Inner, addName, result);
                WritePropertyType(w, t.Value, addName, result);
                break;
        }
    }

    private static byte[] Compress(byte[] body, Options o) => o.Compression switch
    {
        EUsmapCompressionMethod.None => body,
        EUsmapCompressionMethod.ZStandard => CompressZstd(body, o.ZstdLevel),
        _ => throw new NotSupportedException(
            $"Compression method {o.Compression} cannot be produced here. Oodle and Brotli compressors " +
            "are not available in this process; use 'none' or 'zstd'.")
    };

    private static byte[] CompressZstd(byte[] body, int level)
    {
        using var compressor = new ZstdSharp.Compressor(Math.Clamp(level, 1, 22));
        return compressor.Wrap(body).ToArray();
    }

    private static int ClampDim(int dim) => Math.Clamp(dim, 1, MaxArrayDim);
}
