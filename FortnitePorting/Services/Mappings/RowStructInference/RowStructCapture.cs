using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

namespace FortnitePorting.Services.Mappings.RowStructInference;

/// <summary>The rows of a DataTable whose row struct is missing from the mappings being built</summary>
internal sealed record RowStructCapture(string Table, string Struct, int NumRows, byte[] Rows, LayoutSolverOptions Options);

/// <summary>
/// UDataTable that, while a <see cref="RowStructCaptureSink"/> is set on the current thread, only reads the row struct
/// and keeps the raw rows when that struct is missing from the target mappings. Otherwise it is a plain UDataTable.
/// The rows of a cooked table carry no schema, so this is the only way to get at their bytes through CUE4Parse.
/// </summary>
public class CapturingDataTable : UDataTable
{
    [ThreadStatic] internal static RowStructCaptureSink? Sink;

    private static int _registered;

    /// <summary>Replaces CUE4Parse's DataTable class; packages loaded before keep theirs</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            ObjectTypeRegistry.RegisterClass("DataTable", typeof(CapturingDataTable));
    }

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        if (Sink is not { } sink)
        {
            base.Deserialize(Ar, validPos);
            return;
        }

        // scanning: the object itself is thrown away, so the rows are never parsed
        try
        {
            if (Ar.HasUnversionedProperties && string.IsNullOrEmpty(RowStructName) && Ar.Game != EGame.GAME_HonorofKingsWorld)
                sink.TryCapture(this, Ar, validPos);
        }
        catch (Exception)
        {
            // a table that can't be read ahead is just not a candidate
        }

        Ar.Position = validPos;
    }
}

internal sealed class RowStructCaptureSink(TypeMappings target)
{
    public ConcurrentBag<RowStructCapture> Captures { get; } = [];
    public int DataTables => _dataTables;
    private int _dataTables;

    internal void TryCapture(UObject table, FAssetArchive Ar, long validPos)
    {
        Interlocked.Increment(ref _dataTables);
        if (!TryReadProperties(table, Ar, validPos, out var rowStruct)) return;
        var structName = rowStruct.Name;
        if (string.IsNullOrEmpty(structName) || target.Types.ContainsKey(structName)) return;
        // a struct asset (UserDefinedStruct) carries its own layout, only native structs need the mappings
        if (rowStruct.TryLoad<UStruct>(out var loaded) && loaded is not UScriptClass) return;
        if (!TryReadRows(Ar, validPos, out var numRows, out var rows, out var options)) return;

        Captures.Add(new RowStructCapture(Ar.Owner?.Name ?? table.Name, structName, numRows, rows, options));
    }

    /// <summary>Reads the properties of the table on a throwaway object, leaving the archive on the rows</summary>
    private static bool TryReadProperties(UObject table, FAssetArchive Ar, long validPos, [NotNullWhen(true)] out FPackageIndex? rowStruct)
    {
        var probe = new UObject
        {
            Name = table.Name,
            Class = table.Class,
            Outer = table.Outer,
            Super = table.Super,
            Template = table.Template,
            Flags = table.Flags
        };
        probe.Deserialize(Ar, validPos);

        rowStruct = probe.GetOrDefault<FPackageIndex?>("RowStruct");
        return rowStruct is { IsNull: false };
    }

    /// <summary>Reads the row count and the bytes of the rows, from where <see cref="TryReadProperties"/> left the archive</summary>
    private static bool TryReadRows(FAssetArchive Ar, long validPos, out int numRows, out byte[] rows, out LayoutSolverOptions options)
    {
        numRows = 0;
        rows = [];
        options = null!;
        if (Ar.Owner is not { } owner || validPos - Ar.Position < 4) return false;
        numRows = Ar.Read<int>();
        if (numRows <= 0 || validPos - Ar.Position > int.MaxValue) return false;
        rows = Ar.ReadBytes((int) (validPos - Ar.Position));

        options = new LayoutSolverOptions
        {
            Names = owner.NameMap.Select(entry => entry.Name ?? string.Empty).ToArray(),
            SoftObjectPathHasTwoNames = Ar.Ver >= EUnrealEngineObjectUE5Version.FSOFTOBJECTPATH_REMOVE_ASSET_PATH_FNAMES,
            SoftObjectPathIsIndex = !Ar.IsFilterEditorOnly && Ar.Ver >= EUnrealEngineObjectUE5Version.ADD_SOFTOBJECTPATH_LIST &&
                                    owner is Package { SoftObjectPaths.Length: > 0 },
            LargeWorldCoordinates = Ar.Ver >= EUnrealEngineObjectUE5Version.LARGE_WORLD_COORDINATES,
            ImportCount = owner.ImportMapLength,
            ExportCount = owner.ExportMapLength
        };
        return true;
    }
}
