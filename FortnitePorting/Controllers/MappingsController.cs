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
    /// UnrealMappingsDumper algorithm, dumped out of a running UEFN with the UnrealMappingsDumper
    /// DLL itself, or converted from a StormForge-style mappings JSON.
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
        /// Reports whether a UEFN dump can run right now: where the dumper DLL is (or how to build
        /// it) and which UEFN processes are available to inject into.
        /// </summary>
        [HttpGet("uefn")]
        public IActionResult GetUefnStatus()
        {
            var dll = UefnDumperInjector.FindDll();
            var windows = OperatingSystem.IsWindows();
            var processes = windows
                ? UefnDumperInjector.FindTargets().Select(p => new { pid = p.Id, name = p.ProcessName }).ToList<object>()
                : [];

            // The same selection a dump without 'pid' would make, so the caller can see up front
            // which process it is about to inject into.
            var editor = windows ? UefnDumperInjector.FindEditor() : null;

            return Ok(new
            {
                supported = windows,
                dllFound = dll != null,
                dllPath = dll,
                dllFileName = UefnDumperInjector.DllFileName,
                overrideVariable = UefnDumperInjector.DllPathVariable,
                processes,
                target = editor == null ? null : new { pid = editor.Id, name = editor.ProcessName },
                ready = windows && dll != null && editor != null,
                hint = !windows
                    ? "Dumping from UEFN needs Windows. Use POST /api/v1/mappings/dump instead."
                    : dll == null
                        ? "Build the DLL with UnrealMappingsDumper\\build.bat (it lands in libs/), or set " +
                          $"{UefnDumperInjector.DllPathVariable} to an existing copy."
                        : processes.Count == 0
                            ? "Start Unreal Editor for Fortnite and let it finish loading, then POST /api/v1/mappings/dump/uefn."
                            : editor == null
                                ? "UEFN is running but is not loaded far enough to dump from. Wait for it to finish loading and retry."
                                : "POST /api/v1/mappings/dump/uefn to dump the mapping."
            });
        }

        /// <summary>
        /// Dumps a .usmap out of a running UEFN by injecting the UnrealMappingsDumper DLL into it.
        /// </summary>
        /// <remarks>
        /// Unlike the pak-side dump, this one reads the engine's own reflection data, so the mapping
        /// covers native /Script types as well and needs no base mapping merged under it. UEFN has to
        /// be running and fully loaded, and the API has to run as the same Windows user.
        /// </remarks>
        /// <param name="pid">Target UEFN process id. Leave unset: the editor is identified automatically.</param>
        /// <param name="fileName">Output file name; defaults to {build}_uefn.usmap.</param>
        /// <param name="compression">none (default) or oodle. Oodle runs inside the game, which has the encoder.</param>
        /// <param name="console">Let the dumper open a console window inside UEFN (default false).</param>
        /// <param name="timeoutSeconds">How long to wait for the dump after the DLL is loaded (default 120).</param>
        /// <param name="load">Hot-load the dumped mapping into the provider (default false).</param>
        /// <param name="download">Return the .usmap binary (default) instead of JSON statistics.</param>
        /// <param name="gobjects">Hex module-relative address of GObjects, when the dumper's signature scan fails on this build.</param>
        /// <param name="fnameToString">Hex module-relative address of FNameToString; same fallback as gobjects.</param>
        /// <param name="probeSignatures">Let the dumper call signature hits to identify FNameToString. Off by default: a wrong call can crash UEFN.</param>
        /// <param name="cancellationToken">Request cancellation state.</param>
        [HttpPost("dump/uefn")]
        public IActionResult DumpFromUefn(
            [FromQuery] int? pid = null,
            [FromQuery] string compression = "none",
            [FromQuery] string? fileName = null,
            [FromQuery] bool console = false,
            [FromQuery] int timeoutSeconds = 120,
            [FromQuery] bool load = false,
            [FromQuery] bool download = true,
            [FromQuery] string? gobjects = null,
            [FromQuery] string? fnameToString = null,
            [FromQuery] bool probeSignatures = false,
            CancellationToken cancellationToken = default)
        {
            if (!TryParseRva(gobjects, out var gObjectsRva))
            {
                return BadRequest(new { message = "'gobjects' must be a hex module-relative address, for example 1a2b3c4 or 0x1a2b3c4." });
            }

            if (!TryParseRva(fnameToString, out var fNameToStringRva))
            {
                return BadRequest(new { message = "'fnameToString' must be a hex module-relative address, for example 1a2b3c4 or 0x1a2b3c4." });
            }

            bool oodle;
            switch ((compression ?? "none").Trim().ToLowerInvariant())
            {
                case "none": oodle = false; break;
                case "oodle": oodle = true; break;
                default:
                    return BadRequest(new { message = "'compression' must be 'none' or 'oodle' for a UEFN dump." });
            }

            var request = new UefnDumperInjector.DumpRequest
            {
                ProcessId = pid,
                FileName = fileName,
                Build = ShortBuild(),
                Oodle = oodle,
                Console = console,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 3600)),
                GObjectsRva = gObjectsRva,
                FNameToStringRva = fNameToStringRva,
                ProbeSignatures = probeSignatures
            };

            UefnDumperInjector.DumpResult result;
            try
            {
                result = new UefnDumperInjector().Dump(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "The dump was cancelled." });
            }
            catch (PlatformNotSupportedException ex)
            {
                return StatusCode(StatusCodes.Status501NotImplemented, new { message = ex.Message });
            }
            catch (FileNotFoundException ex)
            {
                // The DLL has not been built yet; say so rather than reporting a generic failure.
                return StatusCode(StatusCodes.Status424FailedDependency, new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (TimeoutException ex)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = ex.Message });
            }
            catch (UefnDumperInjector.DumpFailedException ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message, log = ex.Log });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UEFN usmap dump failed");
                return StatusCode(500, new { message = "The UEFN dump failed.", error = ex.Message });
            }

            _logger.LogInformation(
                "Dumped {File} from {Process}:{Pid}: {Structs} structs / {Enums} enums in {Seconds:F1}s",
                result.FileName, result.ProcessName, result.ProcessId,
                result.VerifiedStructs, result.VerifiedEnums, result.ElapsedSeconds);

            if (load)
            {
                try { _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(result.FilePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "loading the dumped usmap failed"); }
            }

            if (download)
            {
                var h = Response.Headers;
                h.Append("X-Usmap-Bytes", result.Usmap.Length.ToString());
                h.Append("X-Usmap-Output", result.FilePath);
                h.Append("X-Usmap-Loaded", load ? "true" : "false");
                h.Append("X-Usmap-Source", $"{result.ProcessName}:{result.ProcessId}");
                if (result.VerifiedStructs is { } structs) h.Append("X-Usmap-Structs", structs.ToString());
                if (result.VerifiedEnums is { } enums) h.Append("X-Usmap-Enums", enums.ToString());
                return File(result.Usmap, "application/octet-stream", result.FileName);
            }

            return Ok(new
            {
                fileName = result.FileName,
                output = result.FilePath,
                downloadUrl = Url.Action(nameof(DownloadMapping), "Mappings", new { fileName = result.FileName }),
                usmapBytes = result.Usmap.Length,
                compression = oodle ? "oodle" : "none",
                loaded = load,
                source = new
                {
                    processName = result.ProcessName,
                    pid = result.ProcessId,
                    dll = result.DllPath,
                    elapsedSeconds = Math.Round(result.ElapsedSeconds, 2)
                },
                verification = new { structs = result.VerifiedStructs, enums = result.VerifiedEnums, error = result.VerifyError },
                logPath = result.LogPath,
                log = result.Log
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

        /// <summary>
        /// Parses an optional hex module-relative address. An absent value is accepted as zero, which
        /// leaves the dumper scanning for the address itself.
        /// </summary>
        private static bool TryParseRva(string? value, out ulong rva)
        {
            rva = 0;
            if (string.IsNullOrWhiteSpace(value)) return true;

            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];

            return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out rva);
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
