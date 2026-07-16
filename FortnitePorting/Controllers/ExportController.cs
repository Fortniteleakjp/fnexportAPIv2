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
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RADADecoder;
using System.Text.RegularExpressions;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Asset export endpoints: retrieve assets as JSON, image (PNG), or audio, plus localization
    /// (locres) data and the file listing inside PAK archives.
    /// </summary>
    [ApiController]
    [Route("api/v1/export")]
    public class ExportController : ControllerBase
    {
        private readonly IFileProvider _provider;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ExportController> _logger;
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> _localizationCache = new(StringComparer.OrdinalIgnoreCase);

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
        public ExportController(IFileProvider provider, IMemoryCache cache, ILogger<ExportController> logger)
        {
            _provider = provider;
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
        [HttpGet]
        public IActionResult Get([FromQuery] string path, [FromQuery] bool image = false, [FromQuery] bool audio = false, [FromQuery] string lang = "en")
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest("The file path cannot be empty.");
            }

            var mountSnapshot = GetMountSnapshot();
            var cacheKey = $"export::{path}::image={image}::audio={audio}::lang={lang}::mount={mountSnapshot}";
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
                                    var nsKey = ns.Key?.ToString() ?? "";
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

                // Preserve every serialized export while adding integrity and size metadata for
                // clients that need to validate or quickly inspect a large export result.
                var jsonOutput = jToken is JArray array ? array : new JArray(jToken);
                var jsonOutputText = jsonOutput.ToString(Formatting.Indented);
                var jsonOutputBytes = Encoding.UTF8.GetBytes(jsonOutputText);
                var response = new JObject
                {
                    ["hash"] = Convert.ToHexString(SHA256.HashData(jsonOutputBytes)).ToLowerInvariant(),
                    ["entries"] = jsonOutput.Count,
                    ["bytes"] = jsonOutputBytes.Length,
                    ["jsonOutput"] = jsonOutput
                };
                var json = response.ToString(Formatting.Indented);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var jsonContentType = "application/json; charset=utf-8";

                var jsonEntryToCache = new CacheEntry { Content = jsonBytes, ContentType = jsonContentType };
                _cache.Set(cacheKey, jsonEntryToCache, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                });
                _logger.LogInformation("Cached response for key: \"{CacheKey}\"", cacheKey);

                return new FileContentResult(jsonBytes, jsonContentType);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing request for path \"{Path}\"", path);
                return Problem(detail: e.StackTrace, title: e.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
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
            return _provider.Files.Keys
                .Where(k => k.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                .Select(k =>
                {
                    var parts = k.Replace('\\', '/').Split('/');
                    // Use the parent directory name as the language code
                    return parts.Length >= 2 ? parts[parts.Length - 2] : null;
                })
                .OfType<string>() // Exclude nulls and convert to IEnumerable<string>
                .Where(l => l.Length < 10 && !l.Contains(".")) // Simple filter
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
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
                ? $"{lang}::mount={mountSnapshot}"
                : $"{lang}::chunk{chunkNo}::mount={mountSnapshot}";
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
                cacheKey = $"{lang}::mount={mountSnapshot}";
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
                            var nsKey = ns.Key?.ToString() ?? "";
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
