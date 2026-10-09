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
    /// from the mounted build, optionally restricted to a PAK / chunk.
    /// </summary>
    [ApiController]
    [VersionAware]
    [Route("api/v1/cosmetics")]
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
        /// Lists cosmetic definitions, optionally filtering the ID, category or source archive.
        /// </summary>
        /// <param name="q">Optional ID or asset-name fragment, for example HonestWasp.</param>
        /// <param name="category">Optional category prefix, for example Character, Backpack, or Pickaxe.</param>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Items per page (maximum 200).</param>
        /// <param name="lang">Localization language code, for example ja.</param>
        /// <param name="pakName">Optional archive name or chunk number.</param>
        /// <param name="includeOffers">Includes bundle display assets in the collection.</param>
        [HttpGet]
        public IActionResult SearchCosmetics(
            [FromQuery] string? q = null,
            [FromQuery] string? category = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? lang = null,
            [FromQuery] string? pakName = null,
            [FromQuery] bool includeOffers = false)
        {
            if (_provider is not AbstractVfsFileProvider vfsProvider)
                return BadRequest(new { message = "The provider is not a VFS provider." });
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            pakName = string.IsNullOrWhiteSpace(pakName) ? null : pakName.Trim();
            var readers = ArchiveCatalog.Match(vfsProvider.MountedVfs, pakName).ToList();
            if (pakName != null && readers.Count == 0)
                return NotFound(new ProblemDetails { Title = "PAKが見つかりません", Status = 404 });
            bool Matches(string path)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var separator = name.IndexOf('_');
                var prefix = separator > 0 ? name[..separator] : name;
                return (category == null || prefix.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
                       (q == null || name.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            var cosmetics = EnumerateUassetFiles(readers, CosmeticsDir).Where(Matches).ToList();
            var offers = includeOffers ? EnumerateUassetFiles(readers, OfferCatalogDisplayAssetsDir).Where(Matches).ToList() : [];
            var sources = cosmetics.Select(path => new ResultSource(path, CosmeticAssetKind))
                .Concat(offers.Select(path => new ResultSource(path, OfferCatalogDisplayAssetKind)))
                .OrderBy(source => source.Path, StringComparer.OrdinalIgnoreCase).ToList();
            ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? locData = null;
            if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                locData = LocalizationService.Load(_provider, lang, scope: _scope);
            var icons = BuildOfferCatalogIndex(readers);
            var results = PageSlice.From(sources, page, pageSize).Select(source => source.AssetKind == OfferCatalogDisplayAssetKind
                ? ExtractOfferCatalogDisplayAsset(source.Path, locData) : ExtractCosmetic(source.Path, locData, icons)).ToList();
            return JsonResponse.Result(new
            {
                query = q, category, pakName, includeOffers,
                matchedPaks = readers.Select(reader => reader.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang,
                total = sources.Count, totalCosmetics = cosmetics.Count, totalOfferCatalogDisplayAssets = offers.Count,
                totalPages = (int)Math.Ceiling(sources.Count / (double)pageSize), currentPage = page, pageSize, results
            });
        }

        /// <summary>
        /// Returns one cosmetic by ID, without needing to know which PAK holds it. The ID may be the
        /// full asset name (CID_028_Athena_Commando_F, Character_HonestWasp, EID_Floss) or just the
        /// skin ID (HonestWasp), in which case the category prefix is matched for you.
        /// </summary>
        /// <param name="id">Cosmetic ID or asset name.</param>
        /// <param name="lang">Localization language code, for example ja.</param>
        [HttpGet("{id}")]
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
