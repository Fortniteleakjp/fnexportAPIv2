using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Localization;

namespace FortnitePorting.Services;

/// <summary>
/// Loads and caches Fortnite .locres localization data and resolves FText keys to localized
/// strings. Shared by the export and cosmetics endpoints.
/// </summary>
public static class LocalizationService
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads (and caches) the merged localization table for a language. When <paramref name="chunkNo"/>
    /// is supplied, only matching chunk locres files are used (with a language-only fallback).
    /// </summary>
    public static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Load(
        IFileProvider provider, string lang, string? chunkNo = null)
    {
        var mountSnapshot = GetMountSnapshot(provider);
        var cacheKey = string.IsNullOrEmpty(chunkNo)
            ? $"{lang}::mount={mountSnapshot}"
            : $"{lang}::chunk{chunkNo}::mount={mountSnapshot}";
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var locresFiles = provider.Files.Keys
            .Where(k => k.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
            .Where(k =>
            {
                var normalized = k.Replace('\\', '/');
                var langMatch = IsLocresLangMatch(normalized, lang);
                if (!string.IsNullOrEmpty(chunkNo))
                {
                    var chunkMatch = normalized.Contains($"locchunk{chunkNo}", StringComparison.OrdinalIgnoreCase)
                                     || normalized.Contains($"chunk{chunkNo}", StringComparison.OrdinalIgnoreCase);
                    return langMatch && chunkMatch;
                }
                return langMatch;
            })
            .ToList();

        if (locresFiles.Count == 0 && !string.IsNullOrEmpty(chunkNo))
        {
            // Fallback: nothing for the chunk -> match by language only.
            locresFiles = provider.Files.Keys
                .Where(k => k.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                .Where(k => IsLocresLangMatch(k.Replace('\\', '/'), lang))
                .ToList();
            cacheKey = $"{lang}::mount={mountSnapshot}";
            if (Cache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }
        }

        if (locresFiles.Count == 0)
        {
            Cache[cacheKey] = result;
            return result;
        }

        Parallel.ForEach(locresFiles, path =>
        {
            if (provider.TryCreateReader(path, out var reader))
            {
                try
                {
                    var locres = new FTextLocalizationResource(reader);
                    foreach (var ns in locres.Entries)
                    {
                        var nsKey = ns.Key?.ToString() ?? "";
                        var nsDict = result.GetOrAdd(nsKey, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                        foreach (var val in ns.Value)
                        {
                            nsDict[val.Key.Str] = val.Value.LocalizedString;
                        }
                    }
                }
                catch
                {
                    // Ignore individual locres parse failures.
                }
            }
        });

        Cache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Resolves an FText (namespace, key) to its localized string. Tries the namespace first,
    /// then searches across all namespaces; tolerant of case and surrounding whitespace.
    /// </summary>
    public static bool TryGetLocalizedString(
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> locData,
        string ns, string key, out string localized)
    {
        if (locData.TryGetValue(ns ?? string.Empty, out var nsDict) && TryFromDict(nsDict, key, out localized))
        {
            return true;
        }

        foreach (var dict in locData.Values)
        {
            if (TryFromDict(dict, key, out localized))
            {
                return true;
            }
        }

        localized = string.Empty;
        return false;
    }

    private static bool TryFromDict(ConcurrentDictionary<string, string> dict, string key, out string localized)
    {
        if (dict.TryGetValue(key, out localized!)) return true;
        if (dict.TryGetValue(key.ToUpperInvariant(), out localized!)) return true;
        if (dict.TryGetValue(key.ToLowerInvariant(), out localized!)) return true;
        if (dict.TryGetValue(key.Trim(), out localized!)) return true;
        localized = string.Empty;
        return false;
    }

    private static bool IsLocresLangMatch(string normalizedPath, string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return false;

        var candidate = lang.Replace('_', '-').Trim();
        foreach (var segment in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return normalizedPath.Contains($".{candidate}.locres", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Contains($"_{candidate}.locres", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMountSnapshot(IFileProvider provider)
    {
        if (provider is AbstractVfsFileProvider vfs)
        {
            return $"vfs={vfs.MountedVfs.Count};files={provider.Files.Count}";
        }
        return $"files={provider.Files.Count}";
    }
}
