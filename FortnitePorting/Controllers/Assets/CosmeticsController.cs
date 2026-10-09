using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
    [VersionAware]
    [Route("api/v1/pak")]
    public partial class CosmeticsController : ControllerBase
    {
        private readonly RequestBuildProvider _build;
        // Read lazily: the version filter binds the provider after controller construction.
        private IFileProvider _provider => _build.Provider;

        /// <summary>Cache-key prefix that keeps an older build's content out of the live cache.</summary>
        private string _scope => _build.CacheScope;
        private readonly ILogger<CosmeticsController> _logger;
        private readonly IMemoryCache _cache;

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

        public CosmeticsController(RequestBuildProvider provider, ILogger<CosmeticsController> logger, IMemoryCache cache)
        {
            _build = provider;
            _logger = logger;
            _cache = cache;
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
            var pagedSources = PageSlice.From(resultSources, page, pageSize);

            // Load localization data once when a non-English language is requested.
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData = null;
            if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                locData = LocalizationService.Load(_provider, lang, scope: _scope);
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

            return JsonResponse.Result(payload);
        }

        /// <summary>
        /// Searches cosmetic definitions across every currently mounted PAK/chunk. Unlike the
        /// PAK-scoped endpoint, callers do not need to know which archive contains the cosmetic.
        /// </summary>
        /// <param name="q">Optional ID or asset-name fragment, for example HonestWasp.</param>
        /// <param name="category">Optional category prefix, for example Character, Backpack, or Pickaxe.</param>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Items per page (maximum 200).</param>
        /// <param name="lang">Localization language code, for example ja.</param>
        [HttpGet("~/api/v1/cosmetics/search")]
        public IActionResult SearchCosmetics(
            [FromQuery] string? q = null,
            [FromQuery] string? category = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? lang = null)
        {
            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The provider is not a VFS provider." });
            }

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

            var allCosmetics = EnumerateUassetFiles(vfsProvider.MountedVfs, CosmeticsDir)
                .Where(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var separator = name.IndexOf('_');
                    var prefix = separator > 0 ? name[..separator] : name;
                    return (category == null || prefix.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
                           (q == null || name.Contains(q, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            var total = allCosmetics.Count;
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            var pagePaths = PageSlice.From(allCosmetics, page, pageSize);

            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData = null;
            if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                locData = LocalizationService.Load(_provider, lang, scope: _scope);
            }

            var offerCatalogIndex = BuildOfferCatalogIndex(vfsProvider.MountedVfs);
            var results = pagePaths.Select(path => ExtractCosmetic(path, locData, offerCatalogIndex)).ToList();

            return Ok(new
            {
                query = q,
                category,
                lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang,
                total,
                totalPages,
                currentPage = page,
                pageSize,
                results
            });
        }

        /// <summary>
        /// Returns one cosmetic by ID, without needing to know which PAK holds it. The ID may be the
        /// full asset name (CID_028_Athena_Commando_F, Character_HonestWasp, EID_Floss) or just the
        /// skin ID (HonestWasp), in which case the category prefix is matched for you.
        /// </summary>
        /// <param name="id">Cosmetic ID or asset name.</param>
        /// <param name="lang">Localization language code, for example ja.</param>
        [HttpGet("~/api/v1/cosmetics/{id}")]
        public IActionResult GetCosmeticById(string id, [FromQuery] string? lang = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest(new { message = "The 'id' parameter is required." });
            }

            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The provider is not a VFS provider." });
            }

            var match = FindCosmeticById(vfsProvider, id);
            if (match == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Cosmetic Not Found",
                    Detail = $"No cosmetic matched the ID '{id}'.",
                    Status = StatusCodes.Status404NotFound,
                    Extensions = { { "searchedDirectory", CosmeticsDir } }
                });
            }

            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData = null;
            if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                locData = LocalizationService.Load(_provider, lang, scope: _scope);
            }

            var offerCatalogIndex = BuildOfferCatalogIndex(vfsProvider.MountedVfs);

            return Ok(new
            {
                id,
                lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang,
                matchType = match.MatchType,
                // Every candidate is listed so an ambiguous ID can be narrowed by the caller.
                matchCount = match.Candidates.Count,
                matches = match.Candidates,
                result = ExtractCosmetic(match.Path, locData, offerCatalogIndex)
            });
        }

    }
}
