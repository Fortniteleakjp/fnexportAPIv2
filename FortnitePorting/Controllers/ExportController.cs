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
    public class ExportController : ControllerBase
    {
        private readonly RequestBuildProvider _build;

        /// <summary>
        /// The build this request reads from: the live one, or the build named by <c>version</c>.
        /// Read lazily on purpose — MVC creates the controller before the filter that resolves the
        /// parameter runs, so a provider captured in the constructor would always be the live one.
        /// </summary>
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

                                var locresJsonString = JsonConvert.SerializeObject(locresJson, Formatting.Indented);
                                _logger.LogInformation("[.locres] JSON serialization complete ({Length} characters)", locresJsonString.Length);

                                var locresBytes = Encoding.UTF8.GetBytes(locresJsonString);
                                var contentType = "application/json; charset=utf-8";
                                var entryToCache = new CacheEntry { Content = locresBytes, ContentType = contentType };
                                _cache.Set(cacheKey, entryToCache, TimeSpan.FromMinutes(30));

                                return Content(locresJsonString, "application/json"); // ContentResult is fine here
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

                // Fallback: if loading by package path fails, attempt to load directly from the file path.
                // Useful for cases such as when a plugin's mount point is not recognized correctly.
                if (asset == null)
                {
                    var normalizedPath = path.Replace('\\', '/');
                    // If there is no extension, try to add one
                    if (!normalizedPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        !normalizedPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedPath += ".uasset";
                    }

                    if (_provider.Files.TryGetValue(normalizedPath, out var gameFile))
                    {
                        _logger.LogInformation("Fallback: Found file by direct path '{NormalizedPath}'. Loading package...", normalizedPath);
                        var package = _provider.LoadPackage(gameFile);
                        var exportName = Path.GetFileNameWithoutExtension(normalizedPath);
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
                var jsonOutputText = jsonOutput.ToString(Formatting.None);
                var jsonOutputBytes = Encoding.UTF8.GetBytes(jsonOutputText);
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

                var json = response.ToString(Formatting.None);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
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
                            var json = Encoding.UTF8.GetString(file.FileContents);
                            results.Add(new { path, success = true, data = JToken.Parse(json) });
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

        private byte[]? DecodeRada(byte[] radaData)
        {
            try
            {
                if (RadaDecoder.TryDecodeToWav(radaData, out var wavData))
                {
                    return wavData;
                }

                _logger.LogWarning("RADA decode failed in managed decoder. Returning null.");
                return null;
            }
            catch (Exception ex)
            {
                if (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _logger.LogWarning(ex, "RADA native library is not available. Returning raw RADA stream.");
                    return null;
                }

                _logger.LogError(ex, "Error decoding RADA");
                return null;
            }
        }

        /// <summary>
        /// Maps a CUE4Parse audio format string to an HTTP content type and file extension.
        /// </summary>
        private static (string contentType, string extension) MapAudioContentType(string? format)
        {
            var f = (format ?? string.Empty).ToUpperInvariant();
            return f switch
            {
                "" or "WAV" or "PCM" or "ADPCM" => ("audio/wav", "wav"),
                "RADA" => ("audio/x-rada", "rada"),     // raw (undecoded) RAD Audio
                "BINKA" => ("audio/x-binka", "binka"),
                "OPUS" => ("audio/opus", "opus"),
                "OGG" => ("audio/ogg", "ogg"),
                "WEM" => ("audio/x-wwise", "wem"),
                "AT9" => ("audio/x-at9", "at9"),
                _ => ("application/octet-stream", f.Length > 0 ? f.ToLowerInvariant() : "bin"),
            };
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

        /// <summary>
        /// Reports audio metadata for a sound asset (format, whether it can be decoded to WAV)
        /// without returning the binary payload. Useful for deciding how to request the audio.
        /// </summary>
        /// <param name="path">The path of the sound asset.</param>
        [HttpGet("audioinfo")]
        public IActionResult GetAudioInfo([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("The 'path' parameter is required.");
            }

            string processedPath;
            try
            {
                processedPath = ConvertToPackagePath(Uri.UnescapeDataString(path).Trim());
            }
            catch
            {
                processedPath = ConvertToPackagePath(path.Trim());
            }

            if (!_provider.TryLoadPackageObject(processedPath, out var asset))
            {
                var lastPart = processedPath.Split('/').Last();
                _provider.TryLoadPackageObject($"{processedPath}.{lastPart}", out asset);
            }

            if (asset == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Asset Not Found",
                    Detail = $"The requested asset '{path}' could not be found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            if (asset is not (USoundWave or UAkMediaAssetData))
            {
                return Ok(new { path, name = asset.Name, exportType = asset.ExportType, isAudio = false });
            }

            // shouldDecompress=false: report the raw container format without running the
            // decompression step. (The encoded payload is still assembled, so this is not free;
            // for PCM the reported format stays "PCM" rather than being promoted to "WAV".)
            asset.Decode(false, out var format, out var raw);
            var fmt = string.IsNullOrEmpty(format) ? "WAV" : format.ToUpperInvariant();
            var radaDecodable = fmt == "RADA" && RadaDecoder.IsNativeAvailable;
            var canDecodeToWav = fmt is "WAV" or "PCM" or "ADPCM" || radaDecodable;
            var (contentType, extension) = MapAudioContentType(radaDecodable ? "WAV" : fmt);

            return Ok(new
            {
                path,
                name = asset.Name,
                exportType = asset.ExportType,
                isAudio = true,
                audioFormat = fmt,
                encodedSizeBytes = raw?.Length ?? 0,
                canDecodeToWav,
                nativeRadaDecoderAvailable = RadaDecoder.IsNativeAvailable,
                nativeRadaLibraryPath = RadaDecoder.NativeLibraryPath,
                suggestedContentType = contentType,
                suggestedExtension = extension,
                hint = "Call /api/v1/export?path=...&audio=true to download. RADA decodes to WAV only when the native RAD Audio library is present."
            });
        }

        /// <summary>
        /// Exports a DataTable or CurveTable as CSV so its rows can be opened directly in a spreadsheet.
        /// A CurveTable is written in long form: one line per curve key.
        /// </summary>
        /// <param name="path">Path of the DataTable / CurveTable asset.</param>
        /// <param name="format">csv (default) or json. json returns the same table as structured rows.</param>
        /// <param name="rows">Optional comma-separated row names to keep. Every row is exported when omitted.</param>
        /// <param name="delimiter">CSV delimiter: comma (default), tab, semicolon, pipe, or a single character.</param>
        /// <param name="flatten">Flatten nested row properties into dotted columns (default true). When false each nested value is written as compact JSON in one cell.</param>
        /// <param name="bom">Prefix the CSV with a UTF-8 BOM so Excel reads localized text correctly (default true).</param>
        /// <param name="download">Send Content-Disposition with a .csv file name (default true).</param>
        /// <param name="hotfix">Apply the live cloudstorage [AssetHotfix] row/curve edits before exporting.</param>
        [HttpGet("datatable")]
        public IActionResult GetDataTable(
            [FromQuery] string? path,
            [FromQuery] string format = "csv",
            [FromQuery] string? rows = null,
            [FromQuery] string delimiter = ",",
            [FromQuery] bool flatten = true,
            [FromQuery] bool bom = true,
            [FromQuery] bool download = true,
            [FromQuery] bool hotfix = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { message = "The 'path' parameter is required." });
            }

            format = (format ?? "csv").Trim().ToLowerInvariant();
            if (format != "csv" && format != "json")
            {
                return BadRequest(new { message = "The 'format' parameter must be csv or json." });
            }

            if (!TryResolveDelimiter(delimiter, out var separator))
            {
                return BadRequest(new { message = "The 'delimiter' parameter must be comma, tab, semicolon, pipe, or a single character." });
            }

            // The hotfix set is fetched first because its fingerprint is part of the cache key: a
            // republished hotfix must not be answered from a response cached against the previous one.
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
                    hotfixError = ex.Message;
                    _logger.LogWarning(ex, "Could not load the cloudstorage hotfix set from {Url}", HotfixService.ListingUrl);
                }
            }

            var rowFilter = ParseRowFilter(rows);
            var cacheKey = string.Join("::", new[]
            {
                _scope + "datatable",
                path,
                format,
                separator.ToString(),
                flatten ? "flat" : "raw",
                bom ? "bom" : "nobom",
                download ? "attach" : "inline",
                rowFilter == null ? "*" : string.Join(",", rowFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                hotfix ? $"hotfix={hotfixIndex?.Version ?? "unavailable"}" : "nohotfix",
                GetMountSnapshot()
            });

            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached != null)
            {
                return BuildResultFromCache(cached);
            }

            try
            {
                if (!TryResolveAsset(path, out var asset, out var processedPath) || asset == null)
                {
                    return NotFound(new ProblemDetails
                    {
                        Title = "Asset Not Found",
                        Detail = $"The requested asset '{path}' could not be found.",
                        Status = StatusCodes.Status404NotFound,
                        Extensions = { { "requestedPath", path }, { "processedPath", processedPath } }
                    });
                }

                var isCurveTable = asset is UCurveTable;
                if (asset is not UDataTable && !isCurveTable)
                {
                    return StatusCode(StatusCodes.Status422UnprocessableEntity, new ProblemDetails
                    {
                        Title = "Not a table asset",
                        Detail = $"'{asset.Name}' is a {asset.ExportType}, not a DataTable or CurveTable. Use /api/v1/export for other asset types.",
                        Status = StatusCodes.Status422UnprocessableEntity
                    });
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                var token = JToken.FromObject(asset, serializer);

                // Rewrite the rows/curves with the live [AssetHotfix] values before the table is built,
                // so the CSV describes the table as the game currently runs it.
                List<HotfixApplier.HotfixResult>? hotfixResults = null;
                if (hotfix && hotfixIndex != null)
                {
                    var entries = hotfixIndex.For(HotfixService.NormalizeAssetPath(processedPath).ToLowerInvariant());
                    if (entries.Count == 0 && asset.Owner?.Name is { Length: > 0 } packageName)
                    {
                        entries = hotfixIndex.For(HotfixService.NormalizeAssetPath(packageName).ToLowerInvariant());
                    }

                    hotfixResults = entries.Count > 0 ? HotfixApplier.Apply(token, entries) : [];
                }

                if (token["Rows"] is not JObject rowMap)
                {
                    return StatusCode(StatusCodes.Status422UnprocessableEntity, new ProblemDetails
                    {
                        Title = "No rows",
                        Detail = $"'{asset.Name}' carries no serialized Rows. A mapping (.usmap) may be missing for its row struct.",
                        Status = StatusCodes.Status422UnprocessableEntity
                    });
                }

                var columns = new List<string>();
                var table = isCurveTable
                    ? BuildCurveTable(rowMap, rowFilter, columns)
                    : BuildDataTable(rowMap, rowFilter, flatten, columns);

                Dictionary<string, string>? headers = null;
                if (hotfix)
                {
                    var applied = hotfixResults?.Count(result => result.Applied) ?? 0;
                    headers = new Dictionary<string, string>
                    {
                        ["X-Hotfix-Status"] = hotfixIndex == null ? "unavailable" : applied > 0 ? "applied" : "none",
                        ["X-Hotfix-Applied"] = applied.ToString()
                    };
                }

                CacheEntry entry;
                if (format == "json")
                {
                    var payload = new JObject
                    {
                        ["path"] = path,
                        ["name"] = asset.Name,
                        ["exportType"] = asset.ExportType,
                        ["tableKind"] = isCurveTable ? "curveTable" : "dataTable",
                        ["rowStruct"] = (asset as UDataTable)?.RowStructName,
                        ["curveTableMode"] = isCurveTable ? token["CurveTableMode"]?.ToString() : null,
                        ["totalRows"] = rowMap.Count,
                        ["exportedLines"] = table.Count,
                        ["columns"] = new JArray(columns),
                        ["rows"] = new JArray(table.Select(line =>
                        {
                            var obj = new JObject();
                            foreach (var column in columns)
                            {
                                obj[column] = line.TryGetValue(column, out var value) ? value : string.Empty;
                            }
                            return obj;
                        }))
                    };

                    entry = new CacheEntry
                    {
                        Content = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)),
                        ContentType = "application/json; charset=utf-8",
                        Headers = headers
                    };
                }
                else
                {
                    var csv = new StringBuilder();
                    csv.Append(string.Join(separator, columns.Select(column => CsvEscape(column, separator)))).Append("\r\n");
                    foreach (var line in table)
                    {
                        csv.Append(string.Join(separator, columns.Select(column =>
                            CsvEscape(line.TryGetValue(column, out var value) ? value : string.Empty, separator)))).Append("\r\n");
                    }

                    var csvBytes = Encoding.UTF8.GetBytes(csv.ToString());
                    if (bom)
                    {
                        // Excel assumes the system code page for a BOM-less CSV, which mangles
                        // localized row values; the BOM makes it read the file as UTF-8.
                        csvBytes = Encoding.UTF8.GetPreamble().Concat(csvBytes).ToArray();
                    }

                    entry = new CacheEntry
                    {
                        Content = csvBytes,
                        ContentType = "text/csv; charset=utf-8",
                        FileDownloadName = download ? $"{asset.Name}.csv" : null,
                        Headers = headers
                    };
                }

                // A response produced while cloudstorage was unreachable is not hotfixed content, so it
                // is never cached: the next request must try the hotfix set again.
                if (hotfixError == null)
                {
                    _cache.Set(cacheKey, entry, TimeSpan.FromMinutes(30));
                }

                return BuildResultFromCache(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting table for path \"{Path}\"", path);
                return Problem(detail: ex.StackTrace, title: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>Parses the optional row-name filter; null means every row is exported.</summary>
        private static HashSet<string>? ParseRowFilter(string? rows)
        {
            if (string.IsNullOrWhiteSpace(rows)) return null;

            var names = rows
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Count > 0 ? names : null;
        }

        /// <summary>
        /// Resolves the CSV delimiter from its name (comma / tab / semicolon / pipe) or a single character.
        /// </summary>
        private static bool TryResolveDelimiter(string? value, out char delimiter)
        {
            delimiter = ',';
            if (string.IsNullOrEmpty(value)) return true;
            if (value == "\t") return Set('\t', out delimiter);

            var trimmed = value.Trim();
            if (trimmed.Length == 0) return true;

            switch (trimmed.ToLowerInvariant())
            {
                case "comma":
                case ",": return Set(',', out delimiter);
                case "tab":
                case "\\t": return Set('\t', out delimiter);
                case "semicolon":
                case ";": return Set(';', out delimiter);
                case "pipe":
                case "|": return Set('|', out delimiter);
            }

            // Any other single character is accepted, except the ones that would break the quoting rules.
            if (trimmed.Length == 1 && trimmed[0] is not ('"' or '\r' or '\n'))
            {
                return Set(trimmed[0], out delimiter);
            }

            return false;

            static bool Set(char value, out char target)
            {
                target = value;
                return true;
            }
        }

        /// <summary>
        /// Builds the DataTable rows: one line per row, with the row name in the first column and the
        /// union of every row's properties as the remaining columns (in first-seen order).
        /// </summary>
        private static List<Dictionary<string, string>> BuildDataTable(
            JObject rowMap, HashSet<string>? rowFilter, bool flatten, List<string> columns)
        {
            columns.Add("RowName");
            var lines = new List<Dictionary<string, string>>(rowMap.Count);

            foreach (var row in rowMap.Properties())
            {
                if (rowFilter != null && !rowFilter.Contains(row.Name)) continue;

                var line = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["RowName"] = row.Name };
                if (row.Value is JObject rowObject)
                {
                    foreach (var property in rowObject.Properties())
                    {
                        FlattenValue(property.Name, property.Value, flatten, line, columns);
                    }
                }
                else
                {
                    // A row that is not a struct (a plain value table) keeps a single Value column.
                    FlattenValue("Value", row.Value, flatten, line, columns);
                }

                lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// Builds the CurveTable rows in long form: one line per curve key, carrying the row name, the
        /// key's own fields, and the curve-level properties under a Curve. prefix. A row without keys
        /// still produces one line so it is not lost.
        /// </summary>
        private static List<Dictionary<string, string>> BuildCurveTable(
            JObject rowMap, HashSet<string>? rowFilter, List<string> columns)
        {
            columns.Add("RowName");
            columns.Add("Time");
            columns.Add("Value");
            var lines = new List<Dictionary<string, string>>();

            foreach (var row in rowMap.Properties())
            {
                if (rowFilter != null && !rowFilter.Contains(row.Name)) continue;

                var curve = row.Value as JObject;
                var curveLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (curve != null)
                {
                    foreach (var property in curve.Properties())
                    {
                        if (property.Name.Equals("Keys", StringComparison.OrdinalIgnoreCase)) continue;
                        // Prefixed because a RichCurve key carries some of the same field names.
                        FlattenValue("Curve." + property.Name, property.Value, true, curveLevel, columns);
                    }
                }

                var keys = curve?["Keys"] as JArray;
                if (keys == null || keys.Count == 0)
                {
                    var empty = new Dictionary<string, string>(curveLevel, StringComparer.OrdinalIgnoreCase)
                    {
                        ["RowName"] = row.Name,
                        ["Time"] = string.Empty,
                        ["Value"] = string.Empty
                    };
                    lines.Add(empty);
                    continue;
                }

                foreach (var key in keys)
                {
                    var line = new Dictionary<string, string>(curveLevel, StringComparer.OrdinalIgnoreCase) { ["RowName"] = row.Name };
                    if (key is JObject keyObject)
                    {
                        foreach (var property in keyObject.Properties())
                        {
                            FlattenValue(property.Name, property.Value, true, line, columns);
                        }
                    }
                    else
                    {
                        line["Value"] = Stringify(key);
                    }

                    line.TryAdd("Time", string.Empty);
                    line.TryAdd("Value", string.Empty);
                    lines.Add(line);
                }
            }

            return lines;
        }

        /// <summary>
        /// Writes one serialized value into the line under <paramref name="name"/>, registering the
        /// column the first time it appears. Nested objects are expanded into dotted columns when
        /// <paramref name="flatten"/> is set; arrays always stay as compact JSON in a single cell.
        /// </summary>
        private static void FlattenValue(
            string name, JToken? value, bool flatten, IDictionary<string, string> line, List<string> columns)
        {
            if (value is JObject nested && flatten && nested.Count > 0)
            {
                foreach (var property in nested.Properties())
                {
                    FlattenValue(name.Length == 0 ? property.Name : $"{name}.{property.Name}", property.Value, true, line, columns);
                }
                return;
            }

            if (!line.ContainsKey(name) && !columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                columns.Add(name);
            }

            line[name] = Stringify(value);
        }

        /// <summary>Renders one serialized value as a CSV cell: strings verbatim, everything else as JSON.</summary>
        private static string Stringify(JToken? value) => value?.Type switch
        {
            null or JTokenType.Null or JTokenType.Undefined => string.Empty,
            JTokenType.String => value.Value<string>() ?? string.Empty,
            _ => value.ToString(Formatting.None)
        };

        /// <summary>Quotes a CSV field when it contains the delimiter, a quote, a newline, or edge spaces.</summary>
        private static string CsvEscape(string? value, char delimiter)
        {
            value ??= string.Empty;
            var needsQuotes = value.Length > 0 &&
                              (value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0 ||
                               value.IndexOf(delimiter) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
            return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

        /// <summary>
        /// Resolves a request path to a loaded export with the same fallbacks as the main export
        /// endpoint: the package path, the package path plus its trailing object name, then the raw
        /// virtual file path.
        /// </summary>
        private bool TryResolveAsset(string path, out UObject? asset, out string processedPath)
        {
            try
            {
                processedPath = ConvertToPackagePath(Uri.UnescapeDataString(path).Trim());
            }
            catch
            {
                processedPath = ConvertToPackagePath(path.Trim());
            }

            if (_provider.TryLoadPackageObject(processedPath, out asset) && asset != null)
            {
                return true;
            }

            var lastPart = processedPath.Split('/').Last();
            if (_provider.TryLoadPackageObject($"{processedPath}.{lastPart}", out asset) && asset != null)
            {
                return true;
            }

            var normalized = path.Replace('\\', '/').Trim();
            if (!normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".uasset";
            }

            if (_provider.Files.TryGetValue(normalized, out var gameFile))
            {
                var package = _provider.LoadPackage(gameFile);
                asset = package.GetExportOrNull(Path.GetFileNameWithoutExtension(normalized), StringComparison.OrdinalIgnoreCase)
                        ?? package.GetExports().FirstOrDefault();
            }

            return asset != null;
        }

        private string ConvertToPackagePath(string filePath)
        {
            filePath = filePath.Replace('\\', '/');

            // Remove the extension
            var ext = Path.GetExtension(filePath);
            if (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || ext.Equals(".umap", StringComparison.OrdinalIgnoreCase))
            {
                filePath = filePath.Substring(0, filePath.Length - ext.Length);
            }

            const string contentStr = "/Content/";
            var contentIndex = filePath.IndexOf(contentStr, StringComparison.OrdinalIgnoreCase);
            if (contentIndex == -1)
            {
                // If it is already a package path or a path that cannot be processed, return it as is
                return filePath;
            }

            const string pluginsStr = "/Plugins/";
            var pluginsIndex = filePath.IndexOf(pluginsStr, StringComparison.OrdinalIgnoreCase);

            if (pluginsIndex != -1 && pluginsIndex < contentIndex)
            {
                // For plugin assets
                // Example: FortniteGame/Plugins/GameFeatures/Figment/Content/Widgets/WBP_Figment.uasset
                var pathAfterPlugins = filePath.Substring(pluginsIndex + pluginsStr.Length);
                var contentInPluginIndex = pathAfterPlugins.IndexOf(contentStr, StringComparison.OrdinalIgnoreCase);
                var pluginNamePath = pathAfterPlugins.Substring(0, contentInPluginIndex);
                var pluginName = pluginNamePath.Split('/').Last();
                var assetPath = pathAfterPlugins.Substring(contentInPluginIndex + contentStr.Length);
                return $"/{pluginName}/{assetPath}";
            }

            // For base game assets
            // Example: FortniteGame/Content/Athena/Items/Cosmetics/Characters/CID_001.uasset
            var pathAfterContent = filePath.Substring(contentIndex + contentStr.Length);
            return $"/Game/{pathAfterContent}";
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

            var json = JsonConvert.SerializeObject(result, Formatting.Indented);
            return Content(json, "application/json");
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
        /// Gets the list of file paths within the specified PAK/Chunk.
        /// Example: api/v1/export/filepath/1051
        /// </summary>
        /// <param name="pakName">PAK name or Chunk number (e.g. 1051)</param>
        [HttpGet("filepath/{pakName}")]
        public IActionResult GetFilePathsInPak(string pakName)
        {
            if (string.IsNullOrWhiteSpace(pakName))
            {
                return BadRequest("pakName is required.");
            }

            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest("The provider is not a VFS provider.");
            }

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
                    Detail = $"The specified PAK/Chunk '{pakName}' could not be found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            var files = matchedReaders
                .SelectMany(reader => reader.Files.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k)
                .ToList();

            return Ok(new
            {
                query = pakName,
                matchedPaks = matchedReaders.Select(x => x.Name).OrderBy(x => x).ToList(),
                totalFiles = files.Count,
                files
            });
        }

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
            var locresFiles = _provider.Files.Keys
                .Where(k => k.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
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
                locresFiles = _provider.Files.Keys
                    .Where(k => k.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
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

        private string GetMountSnapshot()
        {
            if (_provider is AbstractVfsFileProvider vfsProvider)
            {
                return $"vfs={vfsProvider.MountedVfs.Count};files={_provider.Files.Count}";
            }

            return $"files={_provider.Files.Count}";
        }
    }
}
