using System.Globalization;
using FortnitePorting.Models;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

public static partial class HotfixApplier
{
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
}
