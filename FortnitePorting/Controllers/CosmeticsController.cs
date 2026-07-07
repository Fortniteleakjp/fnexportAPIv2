using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Endpoints that read cosmetic item definitions and related offer display assets
    /// out of a specific PAK / chunk.
    /// </summary>
    [ApiController]
    [Route("api/v1/pak")]
    public class CosmeticsController : ControllerBase
    {
        private readonly IFileProvider _provider;
        private readonly ILogger<CosmeticsController> _logger;

        // The BRCosmetics cosmetics directory whose sub-folders hold the item definitions.
        private const string CosmeticsDir =
            "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics";

        // OfferCatalog display assets include bundle offer data such as DA_*_Bundle assets.
        private const string OfferCatalogDisplayAssetsDir =
            "FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets";

        // Optional OfferCatalog textures directory. When present in the same PAK, each cosmetic is
        // matched to its texture by skin ID (e.g. Character_HonestWasp -> T_AthenaSoldiers_HonestWasp).
        private const string OfferCatalogTexturesDir =
            "FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/Textures";

        private const string CosmeticAssetKind = "cosmetic";
        private const string OfferCatalogDisplayAssetKind = "offerCatalogDisplayAsset";

        private sealed record ResultSource(string Path, string AssetKind);

        public CosmeticsController(IFileProvider provider, ILogger<CosmeticsController> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        /// <summary>
        /// For the given PAK / chunk, scans every cosmetic under
        /// FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics plus
        /// FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets and returns
        /// cosmetic definitions together with bundle offer display data.
        /// </summary>
        /// <param name="pakName">PAK name or chunk number (e.g. 1051).</param>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Items per page (max 200; parsing is expensive).</param>
        /// <param name="lang">Localization language code (e.g. ja). The ItemName/Description/ShortDescription
        /// localization Keys are resolved to this language; omit or use en for the English source text.</param>
        [HttpGet("{pakName}/cosmetics")]
        public IActionResult GetCosmetics(string pakName, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? lang = null)
        {
            if (string.IsNullOrWhiteSpace(pakName))
            {
                return BadRequest("pakName is required.");
            }

            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest("The provider is not a VFS provider.");
            }

            if (page < 1) page = 1;
            pageSize = Math.Clamp(pageSize, 1, 200);

            var normalizedInput = pakName.Trim();
            var chunkNeedle = $"chunk{normalizedInput}";

            var matchedReaders = vfsProvider.MountedVfs
                .Where(x =>
                    string.Equals(x.Name, normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(x.Name), normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains(normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains(chunkNeedle, StringComparison.OrdinalIgnoreCase) ||
                    x.Path.Contains(normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                    x.Path.Contains(chunkNeedle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchedReaders.Count == 0)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Pak Not Found",
                    Detail = $"No PAK/chunk matched '{pakName}'.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            var cosmeticFiles = EnumerateUassetFiles(matchedReaders, CosmeticsDir);
            var displayAssetFiles = EnumerateUassetFiles(matchedReaders, OfferCatalogDisplayAssetsDir);
            var resultSources = cosmeticFiles
                .Select(path => new ResultSource(path, CosmeticAssetKind))
                .Concat(displayAssetFiles.Select(path => new ResultSource(path, OfferCatalogDisplayAssetKind)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var total = resultSources.Count;
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            var pagedSources = resultSources.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // Load localization data once when a non-English language is requested.
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData = null;
            if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                locData = LocalizationService.Load(_provider, lang);
            }

            // If this PAK also carries OfferCatalog textures, index them by skin ID (the trailing
            // segment of the texture name) so each cosmetic can be matched to its image.
            var offerCatalogIndex = BuildOfferCatalogIndex(matchedReaders);

            var results = new List<object>(pagedSources.Count);
            foreach (var source in pagedSources)
            {
                results.Add(source.AssetKind == OfferCatalogDisplayAssetKind
                    ? ExtractOfferCatalogDisplayAsset(source.Path, locData)
                    : ExtractCosmetic(source.Path, locData, offerCatalogIndex));
            }

            var payload = new
            {
                query = pakName,
                matchedPaks = matchedReaders.Select(x => x.Name).OrderBy(x => x).ToList(),
                directories = new[] { CosmeticsDir, OfferCatalogDisplayAssetsDir },
                lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang,
                totalCosmetics = total,
                totalBRCosmetics = cosmeticFiles.Count,
                totalOfferCatalogDisplayAssets = displayAssetFiles.Count,
                totalResults = total,
                totalPages,
                currentPage = page,
                pageSize,
                results
            };

            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            return Content(json, "application/json; charset=utf-8");
        }

        private static List<string> EnumerateUassetFiles(IEnumerable<IAesVfsReader> readers, string directory)
        {
            return readers
                .SelectMany(reader => reader.Files.Keys)
                .Where(k =>
                {
                    var norm = k.Replace('\\', '/');
                    return norm.Contains(directory, StringComparison.OrdinalIgnoreCase) &&
                           norm.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Maps a cosmetic prefix to the OfferCatalog texture category token used in
        /// T_Athena&lt;Category&gt;_&lt;ID&gt;. Unlisted prefixes default to the prefix itself
        /// (e.g. Backpack -> AthenaBackpack); Character maps to "Soldiers" (AthenaSoldiers).
        /// </summary>
        private static readonly Dictionary<string, string> PrefixToCategory = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Character"] = "Soldiers",
        };

        /// <summary>
        /// Index of OfferCatalog textures: full-name lookup plus the T_Athena* textures grouped by
        /// skin ID (the trailing "_" segment) for unambiguous fallback matching.
        /// </summary>
        private sealed class OfferCatalogIndex
        {
            public readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> AthenaById = new(StringComparer.OrdinalIgnoreCase);
            public bool IsEmpty => ByName.Count == 0;
        }

        /// <summary>
        /// Scans the given readers for OfferCatalog textures and builds the lookup index.
        /// Empty when the PAK has no OfferCatalog/Content/Textures directory.
        /// </summary>
        private static OfferCatalogIndex BuildOfferCatalogIndex(IEnumerable<IAesVfsReader> readers)
        {
            var index = new OfferCatalogIndex();
            foreach (var reader in readers)
            {
                foreach (var key in reader.Files.Keys)
                {
                    var norm = key.Replace('\\', '/');
                    if (!norm.Contains(OfferCatalogTexturesDir, StringComparison.OrdinalIgnoreCase) ||
                        !norm.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var texName = Path.GetFileNameWithoutExtension(norm); // e.g. T_AthenaSoldiers_HonestWasp
                    index.ByName[texName] = key;

                    if (texName.StartsWith("T_Athena", StringComparison.OrdinalIgnoreCase))
                    {
                        var u = texName.LastIndexOf('_');
                        var id = u >= 0 ? texName.Substring(u + 1) : texName; // HonestWasp
                        if (!string.IsNullOrEmpty(id))
                        {
                            if (!index.AthenaById.TryGetValue(id, out var list))
                            {
                                list = new List<string>();
                                index.AthenaById[id] = list;
                            }
                            list.Add(key);
                        }
                    }
                }
            }
            return index;
        }

        /// <summary>
        /// Resolves the OfferCatalog texture path for a cosmetic by skin ID + category, e.g.
        /// Character_HonestWasp -> T_AthenaSoldiers_HonestWasp, Backpack_HonestWasp -> T_AthenaBackpack_HonestWasp.
        /// Falls back to the sole T_Athena*_{ID} texture when only one exists; otherwise null.
        /// </summary>
        private static string? MatchOfferCatalog(string cosmeticPath, OfferCatalogIndex index)
        {
            if (index.IsEmpty)
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(cosmeticPath); // Character_HonestWasp
            var u = name.IndexOf('_');
            if (u < 0)
            {
                return null;
            }

            var prefix = name.Substring(0, u);          // Character
            var skinId = name.Substring(u + 1);         // HonestWasp
            if (string.IsNullOrEmpty(skinId))
            {
                return null;
            }

            var category = PrefixToCategory.TryGetValue(prefix, out var mapped) ? mapped : prefix;
            var expected = $"T_Athena{category}_{skinId}";
            if (index.ByName.TryGetValue(expected, out var path))
            {
                return path;
            }

            // Unambiguous fallback: a single T_Athena*_{ID} texture for this skin ID.
            if (index.AthenaById.TryGetValue(skinId, out var candidates) && candidates.Count == 1)
            {
                return candidates[0];
            }

            return null;
        }

        /// <summary>
        /// Loads a single cosmetic asset and extracts the requested fields. When <paramref name="locData"/>
        /// is provided, the localization Keys are resolved to that language; when an OfferCatalog texture
        /// matches the skin ID it is added under <c>offerCatalog</c>.
        /// </summary>
        private object ExtractCosmetic(string path, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData, OfferCatalogIndex offerCatalogIndex)
        {
            try
            {
                if (!_provider.Files.TryGetValue(path, out var gameFile))
                {
                    return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = CosmeticAssetKind, error = "File not found." };
                }

                var package = _provider.LoadPackage(gameFile);
                var exports = package.GetExports().ToList();
                if (exports.Count == 0)
                {
                    return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = CosmeticAssetKind, error = "No exports were found." };
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });

                string? exportType = null;
                string? itemNameKey = null, itemName = null;
                string? itemDescriptionKey = null, itemDescription = null;
                string? itemShortDescriptionKey = null, itemShortDescription = null;
                string? largeIcon = null;
                string? icon = null;
                JToken? tags = null;

                foreach (var export in exports)
                {
                    JToken token;
                    try
                    {
                        token = JToken.FromObject(export, serializer);
                    }
                    catch
                    {
                        continue;
                    }

                    var props = GetChild(token, "Properties") ?? token;
                    var dataList = GetChild(props, "DataList");

                    exportType ??= GetChild(token, "Type")?.ToString();

                    // FText fields: localization Key + resolved text (localized by lang, else the English source).
                    if (itemNameKey == null) (itemNameKey, itemName) = GetText(props, token, "ItemName", locData);
                    if (itemDescriptionKey == null) (itemDescriptionKey, itemDescription) = GetText(props, token, "ItemDescription", locData);
                    if (itemShortDescriptionKey == null) (itemShortDescriptionKey, itemShortDescription) = GetText(props, token, "ItemShortDescription", locData);

                    // Icons: AssetPathName of LargeIcon / Icon (search DataList, then Properties, then anywhere)
                    largeIcon ??= GetAssetPath(dataList, props, token, "LargeIcon");
                    icon ??= GetAssetPath(dataList, props, token, "Icon");

                    // Tags (gameplay tags). Accept "Tags" or fall back to "GameplayTags".
                    if (tags == null)
                    {
                        var t = (dataList != null ? FindFirst(dataList, "Tags") : null)
                                ?? FindFirst(props, "Tags")
                                ?? FindFirst(token, "GameplayTags");
                        if (t != null && t.Type != JTokenType.Null)
                        {
                            tags = t;
                        }
                    }
                }

                return new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    assetKind = CosmeticAssetKind,
                    exportType,
                    itemNameKey,
                    itemName,
                    itemDescriptionKey,
                    itemDescription,
                    itemShortDescriptionKey,
                    itemShortDescription,
                    largeIcon,
                    icon,
                    tags,
                    offerCatalog = MatchOfferCatalog(path, offerCatalogIndex)
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract cosmetic from {Path}", path);
                return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = CosmeticAssetKind, error = ex.Message };
            }
        }

        /// <summary>
        /// Loads a single OfferCatalog display asset and returns its serialized exports. These assets
        /// carry bundle offer data such as DisplayName, DetailsAttributes, images, and display size.
        /// </summary>
        private object ExtractOfferCatalogDisplayAsset(
            string path,
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData)
        {
            try
            {
                if (!_provider.Files.TryGetValue(path, out var gameFile))
                {
                    return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = OfferCatalogDisplayAssetKind, error = "File not found." };
                }

                var package = _provider.LoadPackage(gameFile);
                var exports = package.GetExports().ToList();
                if (exports.Count == 0)
                {
                    return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = OfferCatalogDisplayAssetKind, error = "No exports were found." };
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });

                var exportsArray = new JArray();
                string? exportType = null;
                string? displayNameKey = null, displayName = null;
                JToken? detailsAttributes = null;

                foreach (var export in exports)
                {
                    JToken token;
                    try
                    {
                        token = JToken.FromObject(export, serializer);
                    }
                    catch
                    {
                        continue;
                    }

                    if (locData != null)
                    {
                        ApplyLocalization(token, locData);
                    }

                    var props = GetChild(token, "Properties") ?? token;
                    exportType ??= GetChild(token, "Type")?.ToString();
                    if (displayNameKey == null) (displayNameKey, displayName) = GetText(props, token, "DisplayName", locData);
                    detailsAttributes ??= GetChild(props, "DetailsAttributes") ?? FindFirst(token, "DetailsAttributes");
                    exportsArray.Add(token);
                }

                return new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    assetKind = OfferCatalogDisplayAssetKind,
                    exportType,
                    displayNameKey,
                    displayName,
                    detailsAttributes,
                    exports = exportsArray
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract OfferCatalog display asset from {Path}", path);
                return new { path, name = Path.GetFileNameWithoutExtension(path), assetKind = OfferCatalogDisplayAssetKind, error = ex.Message };
            }
        }

        /// <summary>
        /// Gets a named FText property's (Key, text). When locData is provided the Key is resolved to
        /// that language; otherwise the English SourceString is returned. Searches recursively as a fallback.
        /// </summary>
        private static (string? key, string? text) GetText(
            JToken? props, JToken root, string propertyName,
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData)
        {
            var node = GetChild(props, propertyName) ?? FindFirst(root, propertyName);
            if (node == null)
            {
                return (null, null);
            }

            var key = GetChild(node, "Key")?.ToString();
            var source = GetChild(node, "SourceString")?.ToString();

            string? text = null;
            if (locData != null && !string.IsNullOrEmpty(key))
            {
                var ns = GetChild(node, "Namespace")?.ToString() ?? string.Empty;
                if (LocalizationService.TryGetLocalizedString(locData, ns, key, out var localized) && !string.IsNullOrEmpty(localized))
                {
                    text = localized;
                }
            }

            // Fall back to the English source string when no localized value is available.
            text ??= source;
            return (key, text);
        }

        /// <summary>Gets the AssetPathName of a named icon property, searching DataList, Properties, then anywhere.</summary>
        private static string? GetAssetPath(JToken? dataList, JToken? props, JToken root, string propertyName)
        {
            var node = (dataList != null ? FindFirst(dataList, propertyName) : null)
                       ?? GetChild(props, propertyName)
                       ?? FindFirst(root, propertyName);
            var assetPath = GetChild(node, "AssetPathName");
            return assetPath != null && assetPath.Type != JTokenType.Null ? assetPath.ToString() : null;
        }

        /// <summary>Recursively replaces FText LocalizedString values with the requested language.</summary>
        private static void ApplyLocalization(
            JToken token,
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>> locData)
        {
            if (token is JObject obj)
            {
                var key = GetChild(obj, "Key")?.ToString();
                var source = GetChild(obj, "SourceString");
                var localizedProperty = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, "LocalizedString", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(key) && source != null && localizedProperty != null)
                {
                    var ns = GetChild(obj, "Namespace")?.ToString() ?? string.Empty;
                    if (LocalizationService.TryGetLocalizedString(locData, ns, key, out var localized) && !string.IsNullOrEmpty(localized))
                    {
                        localizedProperty.Value = localized;
                    }
                }

                foreach (var prop in obj.Properties())
                {
                    ApplyLocalization(prop.Value, locData);
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    ApplyLocalization(item, locData);
                }
            }
        }

        /// <summary>Direct, case-insensitive child lookup on a JObject.</summary>
        private static JToken? GetChild(JToken? parent, string name)
        {
            if (parent is JObject obj)
            {
                return obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            }
            return null;
        }

        /// <summary>Recursively finds the first property with the given name (case-insensitive).</summary>
        private static JToken? FindFirst(JToken? root, string name)
        {
            if (root is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return prop.Value;
                    }
                }
                foreach (var prop in obj.Properties())
                {
                    var found = FindFirst(prop.Value, name);
                    if (found != null) return found;
                }
            }
            else if (root is JArray arr)
            {
                foreach (var item in arr)
                {
                    var found = FindFirst(item, name);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
