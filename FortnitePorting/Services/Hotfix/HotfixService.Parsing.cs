using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

public static partial class HotfixService
{
    /// <summary>
    /// Extracts the <c>+DataTable=</c> / <c>+CurveTable=</c> / <c>+CurveFloat=</c> lines of the
    /// <c>[AssetHotfix]</c> section and the <c>+TextReplacements=</c> lines of the
    /// <c>[/Script/FortniteGame.FortTextHotfixConfig]</c> section. Other sections are ignored.
    /// </summary>
    public static ParsedConfig ParseIni(string fileName, string content)
    {
        var entries = new List<AssetHotfixEntry>();
        var textEntries = new List<TextHotfixEntry>();
        var inAssetHotfix = false;
        var inTextHotfix = false;
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            // Several cloudstorage files start with a UTF-8 BOM, which would otherwise hide the first
            // section header and make its lines look like they belong to no section at all.
            var line = rawLine.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                inAssetHotfix = line.Equals("[AssetHotfix]", StringComparison.OrdinalIgnoreCase);
                inTextHotfix = line.Equals(TextHotfixSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line[0] != '+') continue;

            if (inTextHotfix && line.StartsWith("+TextReplacements=", StringComparison.OrdinalIgnoreCase))
            {
                var textEntry = ParseTextReplacement(fileName, lineNumber, line["+TextReplacements=".Length..]);
                if (textEntry != null) textEntries.Add(textEntry);
                continue;
            }

            if (!inAssetHotfix) continue;

            HotfixTarget target;
            string body;
            if (line.StartsWith("+DataTable=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.DataTable;
                body = line["+DataTable=".Length..];
            }
            else if (line.StartsWith("+CurveTable=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.CurveTable;
                body = line["+CurveTable=".Length..];
            }
            else if (line.StartsWith("+CurveFloat=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.CurveFloat;
                body = line["+CurveFloat=".Length..];
            }
            else
            {
                continue;
            }

            // path;operation;payload — the payload itself may contain ';', so only three parts are split off.
            var parts = body.Split(';', 3);
            if (parts.Length < 3) continue;

            var assetPath = parts[0].Trim();
            var operationText = parts[1].Trim();
            var payload = parts[2];
            if (assetPath.Length == 0) continue;

            var lookupKey = NormalizeAssetPath(assetPath).ToLowerInvariant();
            string? rowName = null;
            string? field = null;
            string value;

            switch (operationText.ToLowerInvariant())
            {
                case "rowupdate":
                {
                    // DataTable: row;property;value   CurveTable: row;keyTime;value
                    var rowParts = payload.Split(';', 3);
                    if (rowParts.Length != 3) continue;
                    rowName = rowParts[0].Trim();
                    field = rowParts[1].Trim();
                    value = rowParts[2];
                    break;
                }
                case "addrow":
                case "tableupdate":
                case "curveupdate":
                    value = payload;
                    break;
                default:
                    continue;
            }

            var operation = operationText.ToLowerInvariant() switch
            {
                "rowupdate" => HotfixOperation.RowUpdate,
                "addrow" => HotfixOperation.AddRow,
                "tableupdate" => HotfixOperation.TableUpdate,
                _ => HotfixOperation.CurveUpdate
            };

            entries.Add(new AssetHotfixEntry
            {
                Target = target,
                Operation = operation,
                AssetPath = assetPath,
                LookupKey = lookupKey,
                RowName = rowName,
                Field = field,
                Value = value.Trim(),
                SourceFile = fileName,
                Line = lineNumber
            });
        }

        return new ParsedConfig(entries, textEntries);
    }

    /// <summary>The hotfix lines found in one config file.</summary>
    /// <param name="AssetEntries">DataTable / CurveTable / CurveFloat edits.</param>
    /// <param name="TextEntries">FText replacements.</param>
    public sealed record ParsedConfig(List<AssetHotfixEntry> AssetEntries, List<TextHotfixEntry> TextEntries);

    private const string TextHotfixSection = "[/Script/FortniteGame.FortTextHotfixConfig]";

    /// <summary>
    /// Parses the Unreal struct literal of a <c>+TextReplacements=</c> line, for example
    /// <c>(Category=Game, Namespace="", Key="540C…", NativeString="Gem Llama Sprite", LocalizedStrings=(("en","…"),("ja","…")))</c>.
    /// </summary>
    private static TextHotfixEntry? ParseTextReplacement(string fileName, int lineNumber, string value)
    {
        if (UnrealLiteral.Parse(value) is not Newtonsoft.Json.Linq.JObject parsed) return null;

        var key = parsed["Key"]?.ToString();
        if (string.IsNullOrEmpty(key)) return null;

        var localized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // LocalizedStrings=(("ar","…"),("en","…")) parses to an array of two-element arrays.
        if (parsed["LocalizedStrings"] is Newtonsoft.Json.Linq.JArray cultures)
        {
            foreach (var culture in cultures.OfType<Newtonsoft.Json.Linq.JArray>())
            {
                if (culture.Count < 2) continue;
                var code = culture[0].ToString();
                if (code.Length == 0) continue;
                localized[code] = culture[1].ToString();
            }
        }

        return new TextHotfixEntry
        {
            Category = parsed["Category"]?.ToString() ?? string.Empty,
            Namespace = parsed["Namespace"]?.ToString() ?? string.Empty,
            Key = key,
            NativeString = parsed["NativeString"]?.ToString() ?? string.Empty,
            LocalizedStrings = localized,
            SourceFile = fileName,
            Line = lineNumber
        };
    }

    /// <summary>
    /// Reduces a package path to the form used as the lookup key: forward slashes, no .uasset/.umap
    /// extension, no trailing <c>.ObjectName</c>, and a leading slash.
    /// Both <c>/Game/Athena/Items/Weapons/AthenaRangedWeapons</c> and
    /// <c>/AllegoryKeen/DataTables/AllegoryKeenGameData.AllegoryKeenGameData</c> normalize to their package path.
    /// </summary>
    public static string NormalizeAssetPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0) return string.Empty;

        if (normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".uasset".Length];
        }
        else if (normalized.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".umap".Length];
        }

        // Drop the object part of Package.Object (and Package.Object:SubObject).
        var lastSlash = normalized.LastIndexOf('/');
        var dot = normalized.IndexOf('.', lastSlash + 1);
        if (dot >= 0)
        {
            normalized = normalized[..dot];
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.Length > 0 && normalized[0] != '/')
        {
            normalized = '/' + normalized;
        }

        return normalized;
    }
}
