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
    /// <summary>
    /// Asset export endpoints: retrieve assets as JSON, image (PNG), or audio, plus localization
    /// (locres) data and the file listing inside PAK archives.
    /// </summary>
    [ApiController]
    [VersionAware]
    [Route("api/v1/export")]
    public partial class ExportController : ControllerBase
    {
        private readonly RequestBuildProvider _build;
        // Read lazily: the version filter binds the provider after controller construction.
        private IFileProvider _provider => _build.Provider;

        /// <summary>Cache-key prefix that keeps an older build's content out of the live cache.</summary>
        private string _scope => _build.CacheScope;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ExportController> _logger;
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> _localizationCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Drops the cached localization tables. Called when the provider is rebuilt for a new build.
        /// </summary>
        public static void ClearCaches() => _localizationCache.Clear();

        private class CacheEntry
        {
            public byte[] Content { get; set; } = [];
            public string ContentType { get; set; } = string.Empty;
            public string? FileDownloadName { get; set; }
            public Dictionary<string, string>? Headers { get; set; }
        }

        private IActionResult BuildResultFromCache(CacheEntry entry)
        {
            if (entry.Headers != null)
            {
                foreach (var header in entry.Headers)
                {
                    Response.Headers[header.Key] = header.Value;
                }
            }

            return string.IsNullOrEmpty(entry.FileDownloadName)
                ? new FileContentResult(entry.Content, entry.ContentType)
                : File(entry.Content, entry.ContentType, entry.FileDownloadName);
        }

        // IFileProvider is injected by the DI container.
        public ExportController(RequestBuildProvider provider, IMemoryCache cache, ILogger<ExportController> logger)
        {
            _build = provider;
            _cache = cache;
            _logger = logger;
        }


        /// <summary>
        /// Exports an asset (JSON by default). Use image=true for a PNG texture, audio=true for sound,
        /// and lang to apply localization (e.g. ja).
        /// </summary>
        /// <param name="path">The path of the asset to export.</param>
        /// <param name="image">Return a PNG when the asset is a texture.</param>
        /// <param name="audio">Return audio when the asset is a sound.</param>
        /// <param name="lang">Localization language code (e.g. ja); en applies none.</param>
        /// <param name="hotfix">Apply the live cloudstorage hotfixes ([AssetHotfix] rows/curves and FText replacements) to the exported JSON.</param>
        [HttpGet]
        public IActionResult Get([FromQuery] string path, [FromQuery] bool image = false, [FromQuery] bool audio = false, [FromQuery] string lang = "en", [FromQuery] bool hotfix = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest("The file path cannot be empty.");
            }

            // The hotfix set is fetched up front because its content fingerprint is part of the cache key:
            // a republished hotfix must not be answered from a response cached against the previous one.
            HotfixIndex? hotfixIndex = null;
            string? hotfixError = null;
            if (hotfix)
            {
                try
                {
                    hotfixIndex = HotfixService.GetIndex();
                }
                catch (Exception ex)
                {
                    // Cloudstorage being unreachable must not turn into a failed export: the asset is
                    // still returned, and the response says the hotfix could not be applied.
                    hotfixError = ex.Message;
                    _logger.LogWarning(ex, "Could not load the cloudstorage hotfix set from {Url}", HotfixService.ListingUrl);
                }
            }

            var mountSnapshot = GetMountSnapshot();
            var hotfixKeyPart = hotfix ? $"::hotfix={hotfixIndex?.Version ?? "unavailable"}" : string.Empty;
            var cacheKey = $"{_scope}export::{path}::image={image}::audio={audio}::lang={lang}::mount={mountSnapshot}{hotfixKeyPart}";
            if (_cache.TryGetValue(cacheKey, out CacheEntry? cachedEntry) && cachedEntry is not null)
            {
                _logger.LogInformation("Cache hit for key: \"{CacheKey}\"", cacheKey);
                return BuildResultFromCache(cachedEntry);
            }
            _logger.LogInformation("Cache miss for key: \"{CacheKey}\". Processing request for path: \"{Path}\"", cacheKey, path);


            // Path normalization: URL-decode and remove any accidentally included query string (e.g. ?image=true).
            var originalPath = path;
            try
            {
                path = Uri.UnescapeDataString(path ?? string.Empty);
            }
            catch
            {
                // Ignore decode errors and use the raw path.
            }

            var qIndex = path?.IndexOf('?') ?? -1;
            if (qIndex >= 0)
            {
                path = path!.Substring(0, qIndex);
            }

            path = path?.Trim() ?? string.Empty;

            // CUE4Parse normally expects extension-less package paths for uasset/umap files.
            // However, for certain files such as .locres, the extension must be preserved.
            var isLocres = path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase);
            var isIni = path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
            var processedPath = path;

            if (!isLocres && !isIni)
            {
                processedPath = ConvertToPackagePath(path);
            }
            try
            {
                _logger.LogInformation("Attempting to load asset with processed path: \"{ProcessedPath}\"", processedPath);

                // Process .locres files
                if (isLocres)
                {
                    _logger.LogInformation("[.locres] Processing started: {Path}", path);

                    // Try path variations (keep the original path)
                    var pathVariations = new List<string>
                    {
                        path,
                        path.Replace("\\", "/"),
                        "/" + path.Replace("\\", "/"),
                        path.Replace("FortniteGame/Plugins/", ""),
                        path.Replace("FortniteGame/Content/", ""),
                        path.Replace("FortniteGame/", ""),
                        "Game/" + path.Replace("FortniteGame/", ""),
                        "/Game/" + path.Replace("FortniteGame/", "")
                    };

                    foreach (var variant in pathVariations)
                    {
                        if (_provider.TryCreateReader(variant, out var reader))
                        {
                            _logger.LogInformation("[.locres] Success: File found at '{Variant}'", variant);
                            try
                            {
                                var locres = new FTextLocalizationResource(reader);
                                var locresJson = new Dictionary<string, Dictionary<string, string>>();

                                foreach (var ns in locres.Entries)
                                {
                                    // FTextKey does not override ToString(); Str is the namespace itself.
                                    var nsKey = ns.Key?.Str ?? string.Empty;
                                    if (!locresJson.ContainsKey(nsKey))
                                    {
                                        locresJson[nsKey] = new Dictionary<string, string>();
                                    }

                                    foreach (var val in ns.Value)
                                    {
                                        locresJson[nsKey][val.Key.Str] = val.Value.LocalizedString;
                                    }
                                }

                                var locresBytes = JsonResponse.Serialize(locresJson);
                                _logger.LogInformation("[.locres] JSON serialization complete ({Length} bytes)", locresBytes.Length);
                                var contentType = "application/json; charset=utf-8";
                                var entryToCache = new CacheEntry { Content = locresBytes, ContentType = contentType };
                                _cache.Set(cacheKey, entryToCache, TimeSpan.FromMinutes(30));

                                return File(locresBytes, contentType);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[.locres] Parsing error");
                                return Problem($"Failed to parse the locres file: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
                            }
                        }
                    }

                    // Provide debugging information
                    _logger.LogWarning("[.locres] File not found. Tried paths: {TriedPaths}", pathVariations);
                    return NotFound(new ProblemDetails
                    {
                        Title = "locres file not found",
                        Detail = $"The requested .locres file '{originalPath}' could not be found.",
                        Status = StatusCodes.Status404NotFound,
                        Extensions =
                        {
                            { "requestedPath", originalPath },
                            { "triedPaths", pathVariations }
                        }
                    });
                }

                if (isIni)
                {
                    if (_provider.TryCreateReader(path, out var reader))
                    {
                        var iniBytes = reader.ReadBytes((int)reader.Length);
                        var contentType = "text/plain; charset=utf-8";

                        var entryToCache = new CacheEntry { Content = iniBytes, ContentType = contentType };
                        _cache.Set(cacheKey, entryToCache, TimeSpan.FromMinutes(30));

                        return new FileContentResult(iniBytes, contentType);
                    }
                }

                // LoadPackageObject throws KeyNotFoundException if path doesn't exist, so use TryLoadPackageObject
                // Fallback for cases such as character blueprints where the main export has the same name as the asset.
                if (!_provider.TryLoadPackageObject(processedPath, out var asset))
                {
                    var lastPart = processedPath.Split('/').Last();
                    var fallbackPath = $"{processedPath}.{lastPart}";
                    _logger.LogInformation("Asset not found. Attempting fallback: \"{FallbackPath}\"", fallbackPath);
                    _provider.TryLoadPackageObject(fallbackPath, out asset);
                }

                // Fallback: if loading by package path fails, resolve the request against the path index
                // and load the file directly. This covers a plugin whose mount point CUE4Parse does not
                // recognize, and an object path (.../T_Foo.T_Foo) — the object name is not part of any
                // stored path, so it has to be split off before the file can be looked up at all.
                if (asset == null)
                {
                    var gameFile = ResolveMountedAsset(path, out var exportName);
                    if (gameFile != null)
                    {
                        _logger.LogInformation("Fallback: resolved \"{Path}\" to the mounted file \"{File}\". Loading package...", path, gameFile.Path);
                        var package = _provider.LoadPackage(gameFile);
                        asset = package.GetExportOrNull(exportName, StringComparison.OrdinalIgnoreCase);

                        if (asset == null)
                        {
                            asset = package.GetExports().FirstOrDefault();
                        }
                    }
                }

                if (asset == null)
                {
                    _logger.LogWarning("Asset \"{OriginalPath}\" not found after all attempts.", originalPath);
                    return NotFound(new ProblemDetails
                    {
                        Title = "Asset Not Found",
                        Detail = $"The requested asset '{originalPath}' could not be found.",
                        Status = StatusCodes.Status404NotFound,
                        Extensions = { { "requestedPath", originalPath }, { "processedPath", processedPath } }
                    });
                }

                _logger.LogInformation("Successfully loaded asset: {AssetName}", asset.Name);

                // image=true only produces a PNG for actual textures (UTexture2D). For any
                // non-texture asset it falls through to the JSON serialization below, so callers
                // can safely pass image=true and still get JSON when the asset isn't a texture.
                if (image && asset is UTexture2D texture)
                {
                    var cTexture = texture.Decode();
                    if (cTexture == null)
                    {
                        return Problem("Failed to decode the texture into a CTexture.", statusCode: StatusCodes.Status500InternalServerError);
                    }

                    string ext;
                    var imageBytes = cTexture.Encode(ETextureFormat.Png, false, out ext);
                    var contentType = "image/png";

                    var entryToCache = new CacheEntry { Content = imageBytes, ContentType = contentType };
                    _cache.Set(cacheKey, entryToCache, TimeSpan.FromMinutes(30));

                    return File(imageBytes, contentType);
                }

                if (audio && asset is USoundWave or UAkMediaAssetData)
                {
                    _logger.LogInformation("Decoding audio asset: {AssetName}", asset.Name);
                    asset.Decode(true, out var format, out var soundBytes);
                    _logger.LogInformation("Decoded format: {Format}, Bytes length: {Length}", format, soundBytes?.Length ?? 0);

                    if (soundBytes == null || soundBytes.Length == 0)
                    {
                        _logger.LogWarning("No audio data could be extracted from: {AssetName}", asset.Name);
                        return Problem($"No audio data could be extracted from '{asset.Name}'.", statusCode: StatusCodes.Status422UnprocessableEntity);
                    }

                    var decoded = false;

                    // RADA -> WAV using the native RAD Audio decoder when it is available.
                    // If the native library is missing, fall through and return the raw RADA stream
                    // (HTTP 200) instead of failing, so callers still receive the data.
                    if (format.Equals("RADA", StringComparison.OrdinalIgnoreCase))
                    {
                        var decodedRada = DecodeRada(soundBytes);
                        if (decodedRada != null)
                        {
                            soundBytes = decodedRada;
                            format = "WAV";
                            decoded = true;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "RADA could not be decoded to WAV (native decoder available: {Available}); returning the raw RADA stream.",
                                RadaDecoder.IsNativeAvailable);
                        }
                    }
                    else if (format.Equals("PCM", StringComparison.OrdinalIgnoreCase) ||
                             format.Equals("WAV", StringComparison.OrdinalIgnoreCase) ||
                             format.Equals("ADPCM", StringComparison.OrdinalIgnoreCase))
                    {
                        // Already a RIFF/WAVE container; served directly as .wav.
                        decoded = true;
                    }

                    var (contentType, fileExtension) = MapAudioContentType(format);
                    var fileDownloadName = $"{asset.Name}.{fileExtension}";

                    var audioHeaders = new Dictionary<string, string>
                    {
                        ["X-Audio-Format"] = string.IsNullOrEmpty(format) ? "WAV" : format.ToUpperInvariant(),
                        ["X-Audio-Decoded"] = decoded ? "true" : "false",
                        ["X-Rada-Native-Decoder"] = RadaDecoder.IsNativeAvailable ? "available" : "unavailable",
                    };
                    foreach (var header in audioHeaders)
                    {
                        Response.Headers[header.Key] = header.Value;
                    }

                    var entryToCache = new CacheEntry
                    {
                        Content = soundBytes,
                        ContentType = contentType,
                        FileDownloadName = fileDownloadName,
                        Headers = audioHeaders
                    };
                    _cache.Set(cacheKey, entryToCache, TimeSpan.FromMinutes(30));

                    return File(soundBytes, contentType, fileDownloadName);
                }

                var jsonSettings = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                var jsonSerializer = JsonSerializer.Create(jsonSettings);

                // Serialize ALL exports of the package (matching FModel/the real asset), not just the
                // primary object — e.g. a cosmetic's variant exports are otherwise lost.
                JToken jToken;
                var allExports = asset.Owner?.GetExports()?.ToList();
                if (allExports is { Count: > 0 })
                {
                    var exportsArray = new JArray();
                    foreach (var export in allExports)
                    {
                        exportsArray.Add(JToken.FromObject(export, jsonSerializer));
                    }
                    jToken = exportsArray;
                }
                else
                {
                    jToken = JToken.FromObject(asset, jsonSerializer);
                }

                // Rewrite the exported rows/curves with the live [AssetHotfix] values before localization,
                // so the response describes the asset as the game currently runs it.
                List<HotfixApplier.HotfixResult>? hotfixResults = null;
                if (hotfix && hotfixIndex != null)
                {
                    var hotfixEntries = hotfixIndex.For(HotfixService.NormalizeAssetPath(processedPath).ToLowerInvariant());
                    if (hotfixEntries.Count == 0 && asset.Owner?.Name is { Length: > 0 } packageName)
                    {
                        // The requested path and the mounted package path can differ (plugin mount points,
                        // FortniteGame/Content/... input); the package's own name is the authoritative form.
                        hotfixEntries = hotfixIndex.For(HotfixService.NormalizeAssetPath(packageName).ToLowerInvariant());
                    }

                    hotfixResults = hotfixEntries.Count > 0
                        ? HotfixApplier.Apply(jToken, hotfixEntries)
                        : [];
                }

                // Apply localization when a language is specified and it is not English
                if (!string.IsNullOrEmpty(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the chunk number from the path (e.g. locchunk1052, chunk1052, pakchunk1052)
                    string chunkNo = null;
                    var chunkMatch = System.Text.RegularExpressions.Regex.Match(processedPath, @"chunk(\d+)", RegexOptions.IgnoreCase);
                    if (!chunkMatch.Success)
                    {
                        chunkMatch = System.Text.RegularExpressions.Regex.Match(processedPath, @"locchunk(\d+)", RegexOptions.IgnoreCase);
                    }
                    if (!chunkMatch.Success)
                    {
                        chunkMatch = System.Text.RegularExpressions.Regex.Match(processedPath, @"pakchunk(\d+)", RegexOptions.IgnoreCase);
                    }
                    if (chunkMatch.Success)
                    {
                        chunkNo = chunkMatch.Groups[1].Value;
                    }
                    var locData = LoadLocalizationData(lang, chunkNo);
                    if (!locData.IsEmpty)
                    {
                        ApplyLocalization(jToken, locData);
                    }
                }

                // Text hotfixes are not tied to an asset: they replace an FText wherever its namespace and
                // key appear. They run after localization because a hotfix overrides the .locres string,
                // and before the FText property names are lower-cased.
                if (hotfix && hotfixIndex != null && hotfixResults != null)
                {
                    var textResults = HotfixApplier.ApplyTextReplacements(jToken, hotfixIndex, lang);
                    hotfixResults.AddRange(textResults);
                }

                if (hotfixResults is { Count: > 0 })
                {
                    _logger.LogInformation("Applied {Applied}/{Total} hotfix entries to \"{Path}\"",
                        hotfixResults.Count(result => result.Applied), hotfixResults.Count, processedPath);
                }

                NormalizeFTextPropertyNames(jToken);

                // Preserve every serialized export while adding integrity and size metadata for
                // clients that need to validate or quickly inspect a large export result.
                var jsonOutput = jToken is JArray array ? array : new JArray(jToken);
                var jsonOutputBytes = JsonResponse.Serialize(jsonOutput, Formatting.None);
                // The body is the same with and without hotfix=true; only its values differ. Whether the
                // hotfix set could be applied is reported in headers so the JSON stays untouched.
                var response = new JObject
                {
                    ["hash"] = Convert.ToHexString(SHA256.HashData(jsonOutputBytes)).ToLowerInvariant(),
                    ["entries"] = jsonOutput.Count,
                    ["bytes"] = jsonOutputBytes.Length,
                    ["jsonOutput"] = jsonOutput
                };

                Dictionary<string, string>? hotfixHeaders = null;
                if (hotfix)
                {
                    var appliedCount = hotfixResults?.Count(result => result.Applied) ?? 0;
                    hotfixHeaders = new Dictionary<string, string>
                    {
                        ["X-Hotfix-Status"] = hotfixIndex == null ? "unavailable" : appliedCount > 0 ? "applied" : "none",
                        ["X-Hotfix-Applied"] = appliedCount.ToString()
                    };
                    foreach (var header in hotfixHeaders)
                    {
                        Response.Headers[header.Key] = header.Value;
                    }
                }

                var jsonBytes = JsonResponse.Serialize(response, Formatting.None);
                var jsonContentType = "application/json; charset=utf-8";

                // A response produced while cloudstorage was unreachable is not hotfixed content, so it
                // is never cached: the next request must try the hotfix set again.
                if (hotfixError == null)
                {
                    var jsonEntryToCache = new CacheEntry { Content = jsonBytes, ContentType = jsonContentType, Headers = hotfixHeaders };
                    _cache.Set(cacheKey, jsonEntryToCache, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                    });
                    _logger.LogInformation("Cached response for key: \"{CacheKey}\"", cacheKey);
                }

                return new FileContentResult(jsonBytes, jsonContentType);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing request for path \"{Path}\"", path);
                return Problem(detail: e.StackTrace, title: e.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>
        /// Exports multiple asset packages as JSON in one request. This is intended for local tools
        /// that would otherwise need to make many sequential calls to the single-asset endpoint.
        /// Images and audio are intentionally not embedded; use the single-asset endpoint for binary payloads.
        /// </summary>
        /// <param name="request">Asset paths and an optional localization language. Up to 100 paths are accepted.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPost("batch")]
        [Consumes("application/json")]
        public IActionResult Batch([FromBody] ExportBatchRequest? request, CancellationToken cancellationToken = default)
        {
            if (request == null || request.Paths == null || request.Paths.Count == 0)
            {
                return BadRequest(new { message = "At least one asset path is required." });
            }

            if (request.Paths.Count > 100)
            {
                return BadRequest(new { message = "A maximum of 100 asset paths can be exported per request." });
            }

            var lang = string.IsNullOrWhiteSpace(request.Lang) ? "en" : request.Lang.Trim();
            var results = new List<object>(request.Paths.Count);
            var succeeded = 0;

            foreach (var requestedPath in request.Paths)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new EmptyResult();
                }

                var path = requestedPath?.Trim() ?? string.Empty;
                if (path.Length == 0)
                {
                    results.Add(new { path, success = false, statusCode = StatusCodes.Status400BadRequest, error = "The asset path is empty." });
                    continue;
                }

                try
                {
                    // Reuse the single-asset implementation so path normalization, localization,
                    // export serialization, and its 30-minute cache remain consistent.
                    var action = Get(path, image: false, audio: false, lang, request.Hotfix);
                    switch (action)
                    {
                        case FileContentResult file when file.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true:
                        {
                            results.Add(new { path, success = true, data = JsonResponse.Parse(file.FileContents) });
                            succeeded++;
                            break;
                        }
                        case ContentResult content when !string.IsNullOrWhiteSpace(content.Content):
                        {
                            results.Add(new { path, success = true, data = JToken.Parse(content.Content!) });
                            succeeded++;
                            break;
                        }
                        case ObjectResult objectResult:
                            results.Add(new
                            {
                                path,
                                success = false,
                                statusCode = objectResult.StatusCode ?? StatusCodes.Status500InternalServerError,
                                error = objectResult.Value
                            });
                            break;
                        default:
                            results.Add(new { path, success = false, statusCode = StatusCodes.Status422UnprocessableEntity, error = "The asset did not produce a JSON response." });
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Batch export failed for {Path}", path);
                    results.Add(new { path, success = false, statusCode = StatusCodes.Status500InternalServerError, error = ex.Message });
                }
            }

            return Ok(new
            {
                language = lang,
                hotfix = request.Hotfix,
                total = results.Count,
                succeeded,
                failed = results.Count - succeeded,
                results
            });
        }

    }
}
