using System.Globalization;
using FortnitePorting.Models;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

/// <summary>
/// Rewrites a serialized export so it reflects the live hotfix lines — the <c>[AssetHotfix]</c> row and
/// curve edits plus the <c>+TextReplacements=</c> FText overrides — i.e. the values the game actually
/// runs with rather than the ones baked into the pak.
/// </summary>
public static class HotfixApplier
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

    // ---------------------------------------------------------------- CurveTable

    /// <summary>
    /// <c>+CurveTable=Path;RowUpdate;Row;KeyTime;Value</c> — sets one key of one curve row, inserting the
    /// key when the row has no key at that time.
    /// </summary>
    private static HotfixResult ApplyCurveRowUpdate(JToken exports, AssetHotfixEntry entry)
    {
        if (entry.RowName == null ||
            !TryParseFloat(entry.Field, out var time) ||
            !TryParseFloat(entry.Value, out var value))
        {
            return Describe(entry, "invalidValue", applied: false);
        }

        var applied = false;
        var result = "rowNotFound";

        foreach (var rows in FindRowMaps(exports, HotfixTarget.CurveTable))
        {
            if (rows[entry.RowName] is not JObject row) continue;

            if (row["Keys"] is not JArray keys)
            {
                keys = [];
                row["Keys"] = keys;
            }

            var existing = keys.OfType<JObject>()
                .FirstOrDefault(key => TryParseFloat(key["Time"]?.ToString(), out var keyTime) &&
                                       Math.Abs(keyTime - time) <= CurveKeyTimeEpsilon);

            if (existing != null)
            {
                existing["Value"] = new JValue(value);
                result = "valueUpdated";
            }
            else
            {
                // Keep the shape the table already uses (a rich curve key carries tangents, a simple one does not).
                var inserted = keys.OfType<JObject>().FirstOrDefault()?.DeepClone() as JObject ?? [];
                inserted["Time"] = new JValue(time);
                inserted["Value"] = new JValue(value);

                var index = 0;
                while (index < keys.Count &&
                       TryParseFloat(keys[index]["Time"]?.ToString(), out var keyTime) &&
                       keyTime < time)
                {
                    index++;
                }

                keys.Insert(index, inserted);
                result = "keyAdded";
            }

            applied = true;
        }

        return Describe(entry, result, applied);
    }

    /// <summary>
    /// <c>+CurveTable=Path;TableUpdate;"[{\"Name\":\"Row\",\"0\":1,\"1\":1}, ...]"</c> — replaces every row.
    /// Each row object maps key times (as property names) to values.
    /// </summary>
    private static HotfixResult ApplyCurveTableUpdate(JToken exports, AssetHotfixEntry entry)
    {
        if (ParsePayload(entry.Value) is not JArray payload)
        {
            return Describe(entry, "invalidPayload", applied: false);
        }

        var applied = false;
        foreach (var rows in FindRowMaps(exports, HotfixTarget.CurveTable))
        {
            var keyTemplate = rows.Properties()
                .Select(property => property.Value["Keys"])
                .OfType<JArray>()
                .SelectMany(keys => keys.OfType<JObject>())
                .FirstOrDefault();

            var replacement = new JObject();
            foreach (var rowToken in payload.OfType<JObject>())
            {
                var name = rowToken["Name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                // Reuse the curve's own metadata (interp mode, default value, extrapolation) when it exists.
                var row = rows[name]?.DeepClone() as JObject ?? NewSimpleCurveRow();
                var keys = new JArray();
                foreach (var property in rowToken.Properties())
                {
                    if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!TryParseFloat(property.Name, out var time)) continue;
                    if (!TryParseFloat(property.Value.ToString(), out var value)) continue;

                    var key = (row["Keys"] as JArray)?.OfType<JObject>().FirstOrDefault()?.DeepClone() as JObject
                              ?? keyTemplate?.DeepClone() as JObject
                              ?? [];
                    key["Time"] = new JValue(time);
                    key["Value"] = new JValue(value);
                    keys.Add(key);
                }

                row["Keys"] = new JArray(keys.OfType<JObject>()
                    .OrderBy(key => TryParseFloat(key["Time"]?.ToString(), out var time) ? time : 0f));
                replacement[name] = row;
            }

            rows.RemoveAll();
            foreach (var property in replacement.Properties())
            {
                rows[property.Name] = property.Value;
            }

            applied = true;
        }

        return Describe(entry, applied ? "rowsReplaced" : "tableNotFound", applied);
    }

    /// <summary>The shape CUE4Parse produces for a simple-curve row, used when a hotfix introduces a new row.</summary>
    private static JObject NewSimpleCurveRow() => new()
    {
        ["InterpMode"] = "ERichCurveInterpMode::RCIM_Linear",
        ["Keys"] = new JArray(),
        ["DefaultValue"] = new JValue(float.MaxValue),
        ["PreInfinityExtrap"] = "ERichCurveExtrapolation::RCCE_Constant",
        ["PostInfinityExtrap"] = "ERichCurveExtrapolation::RCCE_Constant"
    };

    // ---------------------------------------------------------------- DataTable

    /// <summary>
    /// <c>+DataTable=Path;RowUpdate;Row;Property;Value</c> — sets one property of one existing row.
    /// Rows the pak does not contain are reported instead of invented, matching what the game does.
    /// </summary>
    private static HotfixResult ApplyDataRowUpdate(JToken exports, AssetHotfixEntry entry)
    {
        if (entry.RowName == null || string.IsNullOrEmpty(entry.Field))
        {
            return Describe(entry, "invalidValue", applied: false);
        }

        var applied = false;
        var result = "rowNotFound";

        foreach (var rows in FindRowMaps(exports, HotfixTarget.DataTable))
        {
            if (rows[entry.RowName] is not JObject row) continue;

            var parsed = UnrealLiteral.Parse(entry.Value);
            row[entry.Field] = MergeValue(row[entry.Field], parsed);
            result = "valueUpdated";
            applied = true;
        }

        return Describe(entry, result, applied);
    }

    /// <summary>
    /// <c>+DataTable=Path;AddRow;"{\"Name\":\"Row\", ...}"</c> — adds (or replaces) a row supplied as JSON.
    /// </summary>
    private static HotfixResult ApplyDataAddRow(JToken exports, AssetHotfixEntry entry)
    {
        if (ParsePayload(entry.Value) is not JObject payload)
        {
            return Describe(entry, "invalidPayload", applied: false);
        }

        var name = payload["Name"]?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            return Describe(entry, "missingRowName", applied: false);
        }

        var applied = false;
        var replaced = false;
        foreach (var rows in FindRowMaps(exports, HotfixTarget.DataTable))
        {
            replaced |= rows[name] != null;
            rows[name] = BuildRow(payload);
            applied = true;
        }

        return Describe(entry, applied ? (replaced ? "rowReplaced" : "rowAdded") : "tableNotFound", applied);
    }

    /// <summary>
    /// <c>+DataTable=Path;TableUpdate;"[{\"Name\":\"Row\", ...}, ...]"</c> — replaces every row of the table.
    /// </summary>
    private static HotfixResult ApplyDataTableUpdate(JToken exports, AssetHotfixEntry entry)
    {
        if (ParsePayload(entry.Value) is not JArray payload)
        {
            return Describe(entry, "invalidPayload", applied: false);
        }

        var applied = false;
        foreach (var rows in FindRowMaps(exports, HotfixTarget.DataTable))
        {
            var replacement = new JObject();
            foreach (var rowToken in payload.OfType<JObject>())
            {
                var name = rowToken["Name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                replacement[name] = BuildRow(rowToken);
            }

            rows.RemoveAll();
            foreach (var property in replacement.Properties())
            {
                rows[property.Name] = property.Value;
            }

            applied = true;
        }

        return Describe(entry, applied ? "rowsReplaced" : "tableNotFound", applied);
    }

    /// <summary>Row payloads carry the row name in a "Name" property; the export keeps it as the map key.</summary>
    private static JObject BuildRow(JObject payload)
    {
        var row = (JObject)payload.DeepClone();
        row.Remove("Name");
        return row;
    }

    // ---------------------------------------------------------------- CurveFloat

    /// <summary>
    /// <c>+CurveFloat=Path;CurveUpdate;"{\"FloatCurve\":{ ... }}"</c> — replaces the curve of a UCurveFloat.
    /// </summary>
    private static HotfixResult ApplyCurveFloatUpdate(JToken exports, AssetHotfixEntry entry)
    {
        if (ParsePayload(entry.Value) is not JObject payload)
        {
            return Describe(entry, "invalidPayload", applied: false);
        }

        NormalizeCurveEnums(payload);

        var applied = false;
        foreach (var export in Enumerate(exports))
        {
            if (export["FloatCurve"] == null &&
                export["Type"]?.ToString().Contains("Curve", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            foreach (var property in payload.Properties())
            {
                export[property.Name] = property.Value.DeepClone();
            }

            applied = true;
        }

        return Describe(entry, applied ? "curveUpdated" : "curveNotFound", applied);
    }

    /// <summary>
    /// Hotfix payloads use the bare enum names (RCIM_Cubic); CUE4Parse writes them fully qualified
    /// (ERichCurveInterpMode::RCIM_Cubic). Rewriting them keeps hotfixed curves shaped like untouched ones.
    /// </summary>
    private static void NormalizeCurveEnums(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                {
                    NormalizeCurveEnums(property.Value);
                }

                break;
            case JArray array:
                foreach (var item in array)
                {
                    NormalizeCurveEnums(item);
                }

                break;
            case JValue { Type: JTokenType.String } value:
            {
                var text = value.ToString();
                var qualified = text switch
                {
                    _ when text.StartsWith("RCIM_", StringComparison.Ordinal) => $"ERichCurveInterpMode::{text}",
                    _ when text.StartsWith("RCTWM_", StringComparison.Ordinal) => $"ERichCurveTangentWeightMode::{text}",
                    _ when text.StartsWith("RCTM_", StringComparison.Ordinal) => $"ERichCurveTangentMode::{text}",
                    _ when text.StartsWith("RCCE_", StringComparison.Ordinal) => $"ERichCurveExtrapolation::{text}",
                    _ => null
                };

                if (qualified != null)
                {
                    value.Value = qualified;
                }

                break;
            }
        }
    }

    // ---------------------------------------------------------------- TextReplacements

    /// <summary>
    /// Applies the <c>+TextReplacements=</c> lines to every FText in the export. Unlike the
    /// <c>[AssetHotfix]</c> edits these are not bound to one asset: they match on the FText's
    /// namespace and key, so any asset that displays a hotfixed string is rewritten.
    /// Run this after localization, because a text hotfix overrides the .locres value.
    /// </summary>
    public static List<HotfixResult> ApplyTextReplacements(JToken exports, HotfixIndex index, string? lang)
    {
        var applied = new Dictionary<string, (TextHotfixEntry Entry, string Text, int Count)>(StringComparer.Ordinal);
        if (index.TextReplacementCount > 0)
        {
            ReplaceText(exports, index, lang, applied);
        }

        return applied.Values
            .Select(hit => new HotfixResult(
                "TextReplacement",
                "TextReplacement",
                hit.Entry.Namespace.Length == 0 ? null : hit.Entry.Namespace,
                hit.Entry.Key,
                Truncate(hit.Text),
                $"{hit.Entry.SourceFile}:{hit.Entry.Line}",
                hit.Count > 1 ? $"textReplaced×{hit.Count}" : "textReplaced",
                true))
            .ToList();
    }

    private static void ReplaceText(
        JToken token,
        HotfixIndex index,
        string? lang,
        Dictionary<string, (TextHotfixEntry Entry, string Text, int Count)> applied)
    {
        switch (token)
        {
            case JObject obj:
            {
                // FText serializes as Namespace/Key/SourceString/LocalizedString; a bare Key belongs to
                // something else and must not be touched.
                var keyProperty = obj.Property("Key", StringComparison.OrdinalIgnoreCase);
                var sourceProperty = obj.Property("SourceString", StringComparison.OrdinalIgnoreCase);
                var localizedProperty = obj.Property("LocalizedString", StringComparison.OrdinalIgnoreCase);
                var namespaceProperty = obj.Property("Namespace", StringComparison.OrdinalIgnoreCase);

                if (keyProperty != null && (sourceProperty != null || localizedProperty != null || namespaceProperty != null))
                {
                    var entry = index.FindText(namespaceProperty?.Value?.ToString(), keyProperty.Value?.ToString() ?? string.Empty);
                    if (entry != null)
                    {
                        var text = entry.Resolve(lang);
                        if (sourceProperty != null) sourceProperty.Value = entry.NativeString;
                        if (localizedProperty != null) localizedProperty.Value = text;
                        else obj["LocalizedString"] = text;

                        var hit = applied.GetValueOrDefault(entry.LookupKey);
                        applied[entry.LookupKey] = (entry, text, hit.Count + 1);
                    }
                }

                foreach (var property in obj.Properties())
                {
                    ReplaceText(property.Value, index, lang, applied);
                }

                break;
            }
            case JArray array:
            {
                foreach (var item in array)
                {
                    ReplaceText(item, index, lang, applied);
                }

                break;
            }
        }
    }

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
