using System.Globalization;
using FortnitePorting.Models;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

/// <summary>
/// Rewrites a serialized export so it reflects the live hotfix lines — the <c>[AssetHotfix]</c> row and
/// curve edits plus the <c>+TextReplacements=</c> FText overrides — i.e. the values the game actually
/// runs with rather than the ones baked into the pak.
/// </summary>
public static partial class HotfixApplier
{
    /// <summary>Outcome of one hotfix line against the export it targeted.</summary>
    /// <param name="Target">DataTable, CurveTable, CurveFloat or TextReplacement.</param>
    /// <param name="Operation">RowUpdate, AddRow, TableUpdate, CurveUpdate or TextReplacement.</param>
    /// <param name="Row">Row name for row operations; FText namespace for a text replacement.</param>
    /// <param name="Field">Property name (DataTable), curve key time (CurveTable) or FText key.</param>
    /// <param name="Value">The raw value taken from the ini line, or the text that was applied.</param>
    /// <param name="Source">File and line the hotfix came from.</param>
    /// <param name="Result">What happened: valueUpdated, keyAdded, rowAdded, rowsReplaced, curveUpdated, textReplaced, rowNotFound, ...</param>
    /// <param name="Applied">False when the entry changed nothing (for example the row does not exist).</param>
    public sealed record HotfixResult(
        string Target,
        string Operation,
        string? Row,
        string? Field,
        string Value,
        string Source,
        string Result,
        bool Applied);

    private const float CurveKeyTimeEpsilon = 1e-4f;

    /// <summary>
    /// Applies every entry, in order, to the exports of one package. <paramref name="exports"/> is the
    /// serialized export array (or a single export) and is modified in place.
    /// </summary>
    public static List<HotfixResult> Apply(JToken exports, IReadOnlyList<AssetHotfixEntry> entries)
    {
        var results = new List<HotfixResult>(entries.Count);
        foreach (var entry in entries)
        {
            try
            {
                results.Add(ApplyEntry(exports, entry));
            }
            catch (Exception ex)
            {
                results.Add(Describe(entry, $"error: {ex.Message}", applied: false));
            }
        }

        return results;
    }

    private static HotfixResult ApplyEntry(JToken exports, AssetHotfixEntry entry) => entry.Target switch
    {
        HotfixTarget.CurveTable => entry.Operation switch
        {
            HotfixOperation.RowUpdate => ApplyCurveRowUpdate(exports, entry),
            HotfixOperation.TableUpdate => ApplyCurveTableUpdate(exports, entry),
            _ => Describe(entry, "unsupportedOperation", applied: false)
        },
        HotfixTarget.DataTable => entry.Operation switch
        {
            HotfixOperation.RowUpdate => ApplyDataRowUpdate(exports, entry),
            HotfixOperation.AddRow => ApplyDataAddRow(exports, entry),
            HotfixOperation.TableUpdate => ApplyDataTableUpdate(exports, entry),
            _ => Describe(entry, "unsupportedOperation", applied: false)
        },
        HotfixTarget.CurveFloat => entry.Operation == HotfixOperation.CurveUpdate
            ? ApplyCurveFloatUpdate(exports, entry)
            : Describe(entry, "unsupportedOperation", applied: false),
        _ => Describe(entry, "unsupportedTarget", applied: false)
    };

    private static HotfixResult Describe(AssetHotfixEntry entry, string result, bool applied) => new(
        entry.Target.ToString(),
        entry.Operation.ToString(),
        entry.RowName,
        entry.Field,
        Truncate(entry.Value),
        $"{entry.SourceFile}:{entry.Line}",
        result,
        applied);

    private static string Truncate(string value)
        => value.Length <= 512 ? value : value[..512] + "…";

    // ---------------------------------------------------------------- shared helpers

    private static IEnumerable<JObject> Enumerate(JToken exports) => exports switch
    {
        JArray array => array.OfType<JObject>(),
        JObject obj => [obj],
        _ => []
    };

    /// <summary>
    /// Returns the "Rows" maps of the exports a hotfix of this kind targets. Curve tables and data
    /// tables both serialize their rows under "Rows", so the export type decides; when no export
    /// carries a matching type (composite or renamed classes) every row map is used.
    /// </summary>
    private static List<JObject> FindRowMaps(JToken exports, HotfixTarget target)
    {
        var typeName = target == HotfixTarget.CurveTable ? "CurveTable" : "DataTable";
        var typed = new List<JObject>();
        var untyped = new List<JObject>();

        foreach (var export in Enumerate(exports))
        {
            if (export["Rows"] is not JObject rows) continue;

            if (export["Type"]?.ToString().Contains(typeName, StringComparison.OrdinalIgnoreCase) == true)
            {
                typed.Add(rows);
            }
            else
            {
                untyped.Add(rows);
            }
        }

        return typed.Count > 0 ? typed : untyped;
    }

    /// <summary>Decodes the quoted, backslash-escaped JSON payload used by AddRow/TableUpdate/CurveUpdate.</summary>
    private static JToken? ParsePayload(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = UnrealLiteral.Unescape(text[1..^1]);
        }

        try
        {
            return JToken.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseFloat(string? text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Combines a hotfix value with the value already in the export. Structs are merged property by
    /// property so untouched members survive; object references keep their JSON shape and only have
    /// their path rewritten. Anything else replaces the old value outright.
    /// </summary>
    private static JToken MergeValue(JToken? existing, JToken parsed)
    {
        // ("/Path/To/Asset.Asset") — a single-element group standing in for a scalar.
        if (parsed is JArray { Count: 1 } group && existing is not JArray)
        {
            parsed = group[0];
        }

        if (existing is not JObject target)
        {
            return parsed;
        }

        if (parsed is JObject source)
        {
            foreach (var property in source.Properties())
            {
                target[property.Name] = MergeValue(target[property.Name], property.Value);
            }

            return target;
        }

        if (parsed.Type == JTokenType.String)
        {
            var path = parsed.ToString();

            // {"ObjectName": "Class'Name'", "ObjectPath": "/Package/Path.0"}
            if (target["ObjectName"] != null && target["ObjectPath"] != null)
            {
                var packagePath = HotfixService.NormalizeAssetPath(path);
                var objectName = path.Contains('.')
                    ? path[(path.LastIndexOf('.') + 1)..]
                    : packagePath[(packagePath.LastIndexOf('/') + 1)..];

                var className = target["ObjectName"]?.ToString();
                var quote = className?.IndexOf('\'') ?? -1;
                target["ObjectName"] = quote > 0 ? $"{className![..quote]}'{objectName}'" : objectName;
                target["ObjectPath"] = packagePath;
                return target;
            }

            // {"AssetPathName": "/Package/Path.Object", "SubPathString": ""}
            if (target["AssetPathName"] != null)
            {
                target["AssetPathName"] = path;
                return target;
            }
        }

        return parsed;
    }
}
