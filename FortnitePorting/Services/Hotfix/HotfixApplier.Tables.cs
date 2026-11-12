using System.Globalization;
using FortnitePorting.Models;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

public static partial class HotfixApplier
{
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
}
