using System.Text;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.Mappings.RowStructInference;

/// <summary>
/// Writes structs as an uncompressed .usmap (version ExplicitEnumValues, no enums), the format the generator's
/// <c>--extra</c> option reads. Property indices must be contiguous there, so an index no row ever mentioned becomes
/// a placeholder.
/// </summary>
public static class ExtraUsmapWriter
{
    private const ushort Magic = 0x30C4;

    public static byte[] Write(IEnumerable<Struct> structs)
    {
        var names = new List<string>();
        var nameIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        int Name(string name)
        {
            if (!nameIndices.TryGetValue(name, out var index))
            {
                nameIndices[name] = index = names.Count;
                names.Add(name);
            }
            return index;
        }

        using var structData = new MemoryStream();
        using var writer = new BinaryWriter(structData);
        var count = 0;
        foreach (var st in structs)
        {
            count++;
            writer.Write(Name(st.Name));
            writer.Write(string.IsNullOrEmpty(st.SuperType) ? -1 : Name(st.SuperType));

            var propertyCount = Math.Max(st.PropertyCount, st.Properties.Count == 0 ? 0 : st.Properties.Keys.Max() + 1);
            var entries = new List<(int Index, int ArrayDim, string Name, PropertyType Type)>();
            for (var index = 0; index < propertyCount;)
            {
                if (st.Properties.TryGetValue(index, out var info))
                {
                    var arrayDim = Math.Max(1, info.ArraySize ?? 1);
                    entries.Add((index, arrayDim, info.Name, info.MappingType));
                    index += arrayDim;
                }
                else
                {
                    entries.Add((index, 1, $"Unknown_{index}", new PropertyType("IntProperty")));
                    index++;
                }
            }

            writer.Write((ushort) entries.Sum(e => e.ArrayDim));
            writer.Write((ushort) entries.Count);
            foreach (var (index, arrayDim, name, type) in entries)
            {
                writer.Write((ushort) index);
                writer.Write((byte) arrayDim);
                writer.Write(Name(name));
                WriteType(writer, type, Name);
            }
        }
        writer.Flush();

        using var payload = new MemoryStream();
        using (var p = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write((uint) names.Count);
            foreach (var name in names)
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                p.Write((ushort) bytes.Length);
                p.Write(bytes);
            }
            p.Write(0u); // enums
            p.Write((uint) count);
            p.Write(structData.ToArray());
        }

        using var file = new MemoryStream();
        using (var f = new BinaryWriter(file))
        {
            f.Write(Magic);
            f.Write((byte) EUsmapVersion.ExplicitEnumValues);
            f.Write(0); // bHasVersioning
            f.Write((byte) EUsmapCompressionMethod.None);
            f.Write((uint) payload.Length);
            f.Write((uint) payload.Length);
            f.Write(payload.ToArray());
        }
        return file.ToArray();
    }

    private static void WriteType(BinaryWriter writer, PropertyType type, Func<string, int> name)
    {
        if (!Enum.TryParse<EPropertyType>(type.Type, out var kind))
            throw new InvalidDataException($"Unknown property type {type.Type}");
        writer.Write((byte) kind);
        switch (kind)
        {
            case EPropertyType.EnumProperty:
                WriteType(writer, type.InnerType ?? new PropertyType("ByteProperty"), name);
                writer.Write(name(type.EnumName ?? string.Empty));
                break;
            case EPropertyType.StructProperty:
                writer.Write(name(type.StructType ?? throw new InvalidDataException("A struct property without its struct")));
                break;
            case EPropertyType.ArrayProperty:
            case EPropertyType.SetProperty:
            case EPropertyType.OptionalProperty:
                WriteType(writer, type.InnerType ?? throw new InvalidDataException($"{type.Type} without its element type"), name);
                break;
            case EPropertyType.MapProperty:
                WriteType(writer, type.InnerType ?? throw new InvalidDataException("A map without its key type"), name);
                WriteType(writer, type.ValueType ?? throw new InvalidDataException("A map without its value type"), name);
                break;
        }
    }
}
