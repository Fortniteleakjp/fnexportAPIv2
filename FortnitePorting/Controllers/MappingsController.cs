using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FortnitePorting.Services;
using FortnitePorting.Services.MappingsDumper;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Produces and serves .usmap mapping files: dumped from the mounted build with the
    /// UnrealMappingsDumper algorithm, or converted from a StormForge-style mappings JSON.
    /// </summary>
    [ApiController]
    [Route("api/v1/mappings")]
    public class MappingsController : ControllerBase
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private readonly IFileProvider _provider;
        private readonly ManifestService _manifestService;
        private readonly ILogger<MappingsController> _logger;

        public MappingsController(IFileProvider provider, ManifestService manifestService, ILogger<MappingsController> logger)
        {
            _provider = provider;
            _manifestService = manifestService;
            _logger = logger;
        }

        /// <summary>
        /// Generates a .usmap from a mappings JSON (downloaded from <paramref name="url"/> or read from a
        /// local <paramref name="path"/>), saves it under mappings/, verifies it parses back, and can
        /// hot-load it into the provider.
        /// </summary>
        /// <param name="url">URL of the mappings JSON to download (e.g. a StormForge JSON).</param>
        /// <param name="path">Local path of a mappings JSON (alternative to url).</param>
        /// <param name="fileName">Output .usmap file name (defaults to the source name).</param>
        /// <param name="load">Hot-load the generated mapping into the provider (default false).</param>
        /// <param name="verify">Parse the generated .usmap back and report counts/samples (default true).</param>
        [HttpPost("generate")]
        public async Task<IActionResult> Generate(
            [FromQuery] string? url = null,
            [FromQuery] string? path = null,
            [FromQuery] string? fileName = null,
            [FromQuery] bool load = false,
            [FromQuery] bool verify = true,
            [FromQuery] bool download = true)
        {
            byte[] json;
            string sourceName;
            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    json = await Http.GetByteArrayAsync(url);
                    sourceName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
                }
                else if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    json = await System.IO.File.ReadAllBytesAsync(path);
                    sourceName = Path.GetFileNameWithoutExtension(path);
                }
                else
                {
                    return BadRequest(new { message = "Provide 'url' or an existing local 'path' to a mappings JSON." });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { message = "Failed to read the mappings JSON.", error = ex.Message });
            }

            UsmapGenerator.Result gen;
            try
            {
                gen = UsmapGenerator.Generate(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "usmap generation failed");
                return StatusCode(500, new { message = "usmap generation failed.", error = ex.Message });
            }

            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
            var mappingsDir = Path.Combine(rootDir, "mappings");
            Directory.CreateDirectory(mappingsDir);

            var baseName = string.IsNullOrWhiteSpace(fileName) ? sourceName : fileName;
            var outName = baseName.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + ".usmap";
            var outPath = Path.Combine(mappingsDir, outName);
            await System.IO.File.WriteAllBytesAsync(outPath, gen.Usmap);

            int? parsedEnums = null, parsedStructs = null;
            object? verification = null;
            if (verify)
            {
                try
                {
                    var parser = new UsmapParser(gen.Usmap, outName);
                    var m = parser.Mappings;
                    parsedEnums = m?.Enums.Count ?? 0;
                    parsedStructs = m?.Types.Count ?? 0;
                    verification = new { parsedEnums, parsedStructs, samples = SampleTypes(m) };
                }
                catch (Exception ex)
                {
                    verification = new { error = "Parse-back failed: " + ex.Message };
                }
            }

            if (load)
            {
                try { _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(outPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "loading the generated usmap failed"); }
            }

            // Default: return the generated .usmap itself (with the stats in X-Usmap-* headers) so the
            // caller gets the mapping built from their JSON directly. download=false returns JSON stats.
            if (download)
            {
                var h = Response.Headers;
                h.Append("X-Usmap-Bytes", gen.Usmap.Length.ToString());
                h.Append("X-Usmap-Names", gen.Names.ToString());
                h.Append("X-Usmap-Enums", gen.Enums.ToString());
                h.Append("X-Usmap-Structs", gen.Structs.ToString());
                h.Append("X-Usmap-UnknownProps", gen.UnknownProps.ToString());
                h.Append("X-Usmap-OptionalProps", gen.OptionalProps.ToString());
                h.Append("X-Usmap-EditorOnlySkipped", gen.SkippedEditorOnlyProps.ToString());
                h.Append("X-Usmap-Output", outPath);
                h.Append("X-Usmap-Loaded", load ? "true" : "false");
                if (parsedEnums.HasValue) h.Append("X-Usmap-ParsedEnums", parsedEnums.Value.ToString());
                if (parsedStructs.HasValue) h.Append("X-Usmap-ParsedStructs", parsedStructs.Value.ToString());
                return File(gen.Usmap, "application/octet-stream", outName);
            }

            return Ok(new
            {
                source = url ?? path,
                output = outPath,
                usmapBytes = gen.Usmap.Length,
                names = gen.Names,
                enums = gen.Enums,
                structs = gen.Structs,
                unknownProps = gen.UnknownProps,
                optionalProps = gen.OptionalProps,
                skippedEditorOnlyProps = gen.SkippedEditorOnlyProps,
                skippedNamelessTypes = gen.SkippedNamelessTypes,
                skippedNamelessEnums = gen.SkippedNamelessEnums,
                loaded = load,
                verification
            });
        }

        /// <summary>
        /// Dumps a .usmap from the mounted build with the UnrealMappingsDumper algorithm and serves it.
        /// The dumper walks a running game's GObjects; there is no game process here, so the same
        /// UClass/UScriptStruct/UEnum objects are read out of the cooked packages through CUE4Parse and
        /// written with the dumper's own .usmap serialization. Blueprint types come from the paks;
        /// native /Script types are kept by merging the build's existing mapping underneath (merge=true).
        /// </summary>
        /// <param name="path">Only scan packages whose path contains this fragment (e.g. FortniteGame/Content/Athena).</param>
        /// <param name="maxPackages">Maximum packages to open; 0 scans the whole build (very slow).</param>
        /// <param name="timeoutSeconds">Scan budget; the dump serializes whatever it collected when it expires.</param>
        /// <param name="merge">Merge a base .usmap so native /Script types stay present (default true).</param>
        /// <param name="baseMapping">Base mapping file name in mappings/ or an absolute path; defaults to the newest.</param>
        /// <param name="version">usmap version to write: 0 is the dumper's own format, 4 (default) is the latest.</param>
        /// <param name="compression">none (default) or zstd. Oodle/Brotli compressors are unavailable here.</param>
        /// <param name="fileName">Output file name; defaults to {build}_dumped.usmap.</param>
        /// <param name="load">Hot-load the dumped mapping into the provider (default false).</param>
        /// <param name="download">Return the .usmap binary (default) instead of JSON statistics.</param>
        /// <param name="cancellationToken">Request cancellation state.</param>
        [HttpPost("dump")]
        public IActionResult Dump(
            [FromQuery] string? path = null,
            [FromQuery] int maxPackages = 5000,
            [FromQuery] int timeoutSeconds = 120,
            [FromQuery] bool merge = true,
            [FromQuery] string? baseMapping = null,
            [FromQuery] int version = (int) EUsmapVersion.Latest,
            [FromQuery] string compression = "none",
            [FromQuery] string? fileName = null,
            [FromQuery] bool load = false,
            [FromQuery] bool download = true,
            CancellationToken cancellationToken = default)
        {
            if (version < 0 || version > (int) EUsmapVersion.Latest)
            {
                return BadRequest(new { message = $"'version' must be between 0 and {(int) EUsmapVersion.Latest}." });
            }

            EUsmapCompressionMethod compressionMethod;
            switch ((compression ?? "none").Trim().ToLowerInvariant())
            {
                case "none": compressionMethod = EUsmapCompressionMethod.None; break;
                case "zstd":
                case "zstandard": compressionMethod = EUsmapCompressionMethod.ZStandard; break;
                default:
                    return BadRequest(new { message = "'compression' must be 'none' or 'zstd'. Oodle and Brotli compressors are not available in this process." });
            }

            var request = new MappingsDumperService.DumpRequest
            {
                PathFilter = path,
                MaxPackages = Math.Max(0, maxPackages),
                Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)),
                Merge = merge,
                BaseMapping = baseMapping,
                Version = (EUsmapVersion) version,
                Compression = compressionMethod,
                FileName = fileName,
                Build = ShortBuild()
            };

            MappingsDumperService.DumpResult result;
            try
            {
                result = new MappingsDumperService(_provider).Dump(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "The dump was cancelled." });
            }
            catch (FileNotFoundException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "usmap dump failed");
                return StatusCode(500, new { message = "usmap dump failed.", error = ex.Message });
            }

            _logger.LogInformation(
                "Dumped {File}: {Structs} structs / {Enums} enums from {Scanned} packages in {Seconds:F1}s",
                result.FileName, result.Serializer.Structs, result.Serializer.Enums,
                result.Collector.PackagesScanned, result.Collector.ElapsedSeconds);

            if (load)
            {
                try { _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(result.FilePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "loading the dumped usmap failed"); }
            }

            if (download)
            {
                var h = Response.Headers;
                h.Append("X-Usmap-Bytes", result.Usmap.Length.ToString());
                h.Append("X-Usmap-Names", result.Serializer.Names.ToString());
                h.Append("X-Usmap-Enums", result.Serializer.Enums.ToString());
                h.Append("X-Usmap-Structs", result.Serializer.Structs.ToString());
                h.Append("X-Usmap-Output", result.FilePath);
                h.Append("X-Usmap-Loaded", load ? "true" : "false");
                h.Append("X-Usmap-Dumped-Packages", result.Collector.PackagesScanned.ToString());
                h.Append("X-Usmap-Dumped-Structs", result.Collector.StructsCollected.ToString());
                h.Append("X-Usmap-Dumped-Enums", result.Collector.EnumsCollected.ToString());
                h.Append("X-Usmap-Merged-Structs", result.MergedStructs.ToString());
                h.Append("X-Usmap-Merged-Enums", result.MergedEnums.ToString());
                return File(result.Usmap, "application/octet-stream", result.FileName);
            }

            return Ok(new
            {
                fileName = result.FileName,
                output = result.FilePath,
                downloadUrl = Url.Action(nameof(DownloadMapping), "Mappings", new { fileName = result.FileName }),
                usmapBytes = result.Usmap.Length,
                usmapVersion = (int) result.Serializer.Version,
                compression = result.Serializer.Compression.ToString(),
                uncompressedBytes = result.Serializer.UncompressedBytes,
                loaded = load,
                totals = new
                {
                    names = result.Serializer.Names,
                    enums = result.Serializer.Enums,
                    structs = result.Serializer.Structs,
                    properties = result.Serializer.Properties,
                    unknownProperties = result.Serializer.UnknownProperties
                },
                dumped = new
                {
                    structs = result.Collector.StructsCollected,
                    enums = result.Collector.EnumsCollected,
                    packagesMatched = result.Collector.PackagesMatched,
                    packagesScanned = result.Collector.PackagesScanned,
                    packagesFailed = result.Collector.PackagesFailed,
                    exportsInspected = result.Collector.ExportsInspected,
                    limitReached = result.Collector.LimitReached,
                    timedOut = result.Collector.TimedOut,
                    elapsedSeconds = Math.Round(result.Collector.ElapsedSeconds, 2)
                },
                merged = new
                {
                    baseMapping = result.BaseMapping,
                    structs = result.MergedStructs,
                    enums = result.MergedEnums
                },
                verification = new { structs = result.VerifiedStructs, enums = result.VerifiedEnums, error = result.VerifyError }
            });
        }

        /// <summary>
        /// Lists the mapping files this instance holds (dumped, generated, or downloaded), newest first.
        /// </summary>
        [HttpGet]
        public IActionResult ListMappings()
        {
            var files = MappingsDumperService.ListMappings().Select(f => new
            {
                fileName = f.Name,
                size = f.Length,
                modifiedUtc = f.LastWriteTimeUtc,
                downloadUrl = Url.Action(nameof(DownloadMapping), "Mappings", new { fileName = f.Name })
            }).ToList();

            return Ok(new { count = files.Count, directory = MappingsDumperService.MappingsDirectory, files });
        }

        /// <summary>
        /// Serves one stored mapping file by name.
        /// </summary>
        /// <param name="fileName">File name as listed by GET /api/v1/mappings.</param>
        [HttpGet("{fileName}")]
        public IActionResult DownloadMapping([FromRoute] string fileName)
        {
            var file = MappingsDumperService.ResolveStoredMapping(fileName);
            if (file == null)
            {
                return NotFound(new { message = $"No stored mapping named '{fileName}'." });
            }

            return PhysicalFile(file.FullName, "application/octet-stream", file.Name);
        }

        /// <summary>
        /// Reduces "++Fortnite+Release-42.00-CL-56878558-Windows" to "FortniteGame_42_00", so a dumped
        /// mapping is named after the build it describes.
        /// </summary>
        private string? ShortBuild()
        {
            var build = !string.IsNullOrWhiteSpace(_manifestService.AppliedBuildVersion)
                ? _manifestService.AppliedBuildVersion
                : _manifestService.GameBuild;

            if (string.IsNullOrWhiteSpace(build)) return null;

            var parts = build.Split('-');
            var version = (parts.Length > 2 ? parts[1] : build).Trim().Replace('.', '_');
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                version = version.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(version) ? null : $"FortniteGame_{version}";
        }

        private static List<object> SampleTypes(TypeMappings? m)
        {
            var list = new List<object>();
            if (m == null) return list;
            foreach (var n in new[] { "UintVector", "Field", "Vector2D", "Object", "Actor" })
            {
                if (m.Types.TryGetValue(n, out var s))
                {
                    var first = new List<string>();
                    foreach (var kv in s.Properties)
                    {
                        first.Add($"{kv.Key}:{kv.Value.Name}={kv.Value.MappingType.Type}");
                        if (first.Count >= 5) break;
                    }
                    list.Add(new { name = n, propertyCount = s.PropertyCount, propertyEntries = s.Properties.Count, super = s.SuperType, sampleProps = first });
                }
            }
            return list;
        }
    }
}
