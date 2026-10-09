using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse_Conversion.Textures;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RADADecoder;
using System.Text.RegularExpressions;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    public partial class ExportController
    {

        private List<string> GetAvailableLocresLanguages()
        {
            return LocalizationService.GetAvailableLanguages(_provider);
        }

        private static bool IsLocresLangMatch(string normalizedPath, string lang)
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                return false;
            }

            var candidate = lang.Replace('_', '-').Trim();
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (segment.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase) ||
                    segment.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var fileMatch = $".{candidate}.locres";
            var altFileMatch = $"_{candidate}.locres";
            return normalizedPath.Contains(fileMatch, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.Contains(altFileMatch, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Loads the localization data for the specified language.
        /// </summary>
        private ConcurrentDictionary<string, ConcurrentDictionary<string, string>> LoadLocalizationData(string lang, string chunkNo = null)
        {
            var mountSnapshot = GetMountSnapshot();
            var cacheKey = string.IsNullOrEmpty(chunkNo)
                ? $"{_scope}{lang}::mount={mountSnapshot}"
                : $"{_scope}{lang}::chunk{chunkNo}::mount={mountSnapshot}";
            if (_localizationCache.TryGetValue(cacheKey, out var cachedData))
            {
                return cachedData;
            }

            var result = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            // The .locres files are their own bucket in the index, so selecting a language's files costs
            // a few thousand comparisons instead of a walk over every path in the build.
            var index = FileIndex.For(_provider);
            var locresFiles = index.Bucket(".locres")
                .Select(index.PathAt)
                .Where(k =>
                {
                    var normalized = k.Replace('\\', '/');
                    var langMatch = IsLocresLangMatch(normalized, lang);
                    // When chunkNo is specified, prioritize matching the chunk name as well
                    if (!string.IsNullOrEmpty(chunkNo))
                    {
                        var chunkMatch = normalized.Contains($"locchunk{chunkNo}", StringComparison.OrdinalIgnoreCase);
                        if (!chunkMatch)
                        {
                            chunkMatch = normalized.Contains($"chunk{chunkNo}", StringComparison.OrdinalIgnoreCase);
                        }
                        return langMatch && chunkMatch;
                    }
                    return langMatch;
                })
                .ToList();

            if (locresFiles.Count == 0 && !string.IsNullOrEmpty(chunkNo))
            {
                // Fallback: if nothing is found with the chunk specified, search again by language only
                locresFiles = index.Bucket(".locres")
                    .Select(index.PathAt)
                    .Where(k =>
                    {
                        var normalized = k.Replace('\\', '/');
                        return IsLocresLangMatch(normalized, lang);
                    })
                    .ToList();
                // The fallback prefers the per-language cache
                cacheKey = $"{_scope}{lang}::mount={mountSnapshot}";
                if (_localizationCache.TryGetValue(cacheKey, out cachedData))
                {
                    return cachedData;
                }
            }

            if (locresFiles.Count == 0)
            {
                _localizationCache[cacheKey] = result;
                return result;
            }

            _logger.LogInformation($"Loading localization data for '{lang}' (chunk:{chunkNo}) from {locresFiles.Count} files...");
            Parallel.ForEach(locresFiles, path =>
            {
                if (_provider.TryCreateReader(path, out var reader))
                {
                    try
                    {
                        var locres = new FTextLocalizationResource(reader);
                        foreach (var ns in locres.Entries)
                        {
                            // FTextKey does not override ToString(); Str is the namespace itself.
                            var nsKey = ns.Key?.Str ?? string.Empty;
                            var nsDict = result.GetOrAdd(nsKey, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                            foreach (var val in ns.Value)
                            {
                                nsDict[val.Key.Str] = val.Value.LocalizedString;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error parsing .locres file: {Path}", path);
                    }
                }
            });
            _localizationCache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// Recursively traverses the JSON token and replaces LocalizedString values.
        /// </summary>
        private void ApplyLocalization(JToken token, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> locData)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                // Detect objects that have an FText structure
                if (obj["Key"] != null && obj["SourceString"] != null && obj["LocalizedString"] != null)
                {
                    // If Namespace is empty, try to obtain it from the parent object or other properties
                    var ns = obj["Namespace"]?.ToString();
                    if (string.IsNullOrEmpty(ns))
                    {
                        ns = obj.Property("Namespace")?.Value?.ToString() ?? "";
                    }
                    var key = obj["Key"]?.ToString();
                    if (key != null)
                    {
                        if (TryGetLocalizedString(locData, ns, key, out var localized))
                        {
                            obj["LocalizedString"] = localized;
                        }
                    }
                }
                foreach (var prop in obj.Properties())
                {
                    ApplyLocalization(prop.Value, locData);
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var child in token.Children())
                {
                    ApplyLocalization(child, locData);
                }
            }
        }

        private static bool TryGetLocalizedString(
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>> locData,
            string ns,
            string key,
            out string localized)
        {
            // Prefer an exact match
            if (locData.TryGetValue(ns, out var nsDict))
            {
                if (nsDict.TryGetValue(key, out localized!))
                    return true;
                // Case conversion
                if (nsDict.TryGetValue(key.ToUpperInvariant(), out localized!))
                    return true;
                if (nsDict.TryGetValue(key.ToLowerInvariant(), out localized!))
                    return true;
                // Trim
                var trimmed = key.Trim();
                if (nsDict.TryGetValue(trimmed, out localized!))
                    return true;
            }
            // Even if the Namespace does not match, search across everything
            foreach (var dict in locData.Values)
            {
                if (dict.TryGetValue(key, out localized!))
                    return true;
                if (dict.TryGetValue(key.ToUpperInvariant(), out localized!))
                    return true;
                if (dict.TryGetValue(key.ToLowerInvariant(), out localized!))
                    return true;
                var trimmed = key.Trim();
                if (dict.TryGetValue(trimmed, out localized!))
                    return true;
            }
            localized = string.Empty;
            return false;
        }

        private static List<string> GetKeyCandidates(string key)
        {
            var candidates = new List<string> { key };

            var trimmed = key.Trim();
            if (!string.Equals(trimmed, key, StringComparison.Ordinal))
            {
                candidates.Add(trimmed);
            }

            var noBraces = trimmed.Trim('{', '}');
            if (!string.Equals(noBraces, trimmed, StringComparison.Ordinal))
            {
                candidates.Add(noBraces);
            }

            var upper = noBraces.ToUpperInvariant();
            if (!string.Equals(upper, noBraces, StringComparison.Ordinal))
            {
                candidates.Add(upper);
            }

            var lower = noBraces.ToLowerInvariant();
            if (!string.Equals(lower, noBraces, StringComparison.Ordinal))
            {
                candidates.Add(lower);
            }

            var hexOnly = Regex.Replace(noBraces, "[^0-9A-Fa-f]", string.Empty);
            if (!string.IsNullOrEmpty(hexOnly) && !string.Equals(hexOnly, noBraces, StringComparison.Ordinal))
            {
                candidates.Add(hexOnly);
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }


        /// <summary>
        /// Loads all .locres files for the specified language, merges them, and returns the result.
        /// </summary>
        /// <param name="lang">Language code (e.g. ja, en)</param>
        /// <returns>The merged localization data</returns>
        [HttpGet("locres")]
        public IActionResult GetLocres([FromQuery] string lang = "ja")
        {
            _logger.LogInformation("Received request for all .locres files with language: {Lang}", lang);

            if (string.IsNullOrEmpty(lang))
            {
                return BadRequest("Language parameter is required.");
            }

            var result = LoadLocalizationData(lang);

            if (result.IsEmpty)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Localization Not Found",
                    Detail = $"No localization data found for language '{lang}'.",
                    Status = StatusCodes.Status404NotFound,
                    Extensions = { { "availableLanguages", GetAvailableLocresLanguages() } }
                });
            }

            return JsonResponse.Result(result);
        }

        /// <summary>
        /// Gets the list of available languages.
        /// </summary>
        [HttpGet("locres/languages")]
        public IActionResult GetLocresLanguages()
        {
            var languages = GetAvailableLocresLanguages();
            return Ok(new { languages });
        }


        /// <summary>
        /// Matches FortniteAPI's casing for FText values without changing normal Unreal property
        /// names such as Type, Properties, WeaponActorClass, or AssetPathName.
        /// </summary>
        private static void NormalizeFTextPropertyNames(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    NormalizeFTextPropertyNames(property.Value);
                }

                var isFText = obj.Property("Namespace") != null ||
                              obj.Property("SourceString") != null ||
                              obj.Property("LocalizedString") != null;
                if (isFText)
                {
                    RenameProperty(obj, "Namespace", "namespace");
                    RenameProperty(obj, "Key", "key");
                    RenameProperty(obj, "SourceString", "sourceString");
                    RenameProperty(obj, "LocalizedString", "localizedString");
                }

                // Some FText values carry only their culture-invariant source string.
                RenameProperty(obj, "CultureInvariantString", "cultureInvariantString");
                return;
            }

            if (token is JArray array)
            {
                foreach (var child in array)
                {
                    NormalizeFTextPropertyNames(child);
                }
            }
        }

        private static void RenameProperty(JObject obj, string currentName, string expectedName)
        {
            var property = obj.Property(currentName);
            if (property != null && obj.Property(expectedName) == null)
            {
                property.Replace(new JProperty(expectedName, property.Value.DeepClone()));
            }
        }

    }
}
