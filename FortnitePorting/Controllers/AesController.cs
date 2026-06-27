using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Statically extracts the Fortnite pak AES key from the Unreal Editor for Fortnite
    /// (appName "Fortnite_Studio") executable, without running or injecting into any process.
    /// </summary>
    [ApiController]
    [Route("api/v1/aes")]
    public class AesController : ControllerBase
    {
        // The exe download can take a while; allow plenty of time for the manifest fetch.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private readonly IFileProvider _provider;
        private readonly ILogger<AesController> _logger;

        public AesController(IFileProvider provider, ILogger<AesController> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        /// <summary>
        /// Downloads <c>UnrealEditorFortnite-Common-Win64-Shipping.dll</c> from the Fortnite_Studio manifest
        /// and runs the external AesFinder tool on it to extract the Fortnite MainAES key, returning it as the
        /// response. The key is embedded in the Common DLL as <c>mov imm32</c> instruction immediates — no game
        /// launch or injection. Configure the tool path with the AESFINDER_PATH environment variable.
        /// </summary>
        /// <param name="force">Re-download the Common DLL even if a cached copy exists (default false).</param>
        /// <param name="noApi">Pass --no-api to AesFinder (use the highest-entropy candidate, no fortnite-api lookup).</param>
        /// <param name="submit">Submit the extracted key to the provider (zero GUID) and mount matching paks (default true).</param>
        [HttpGet("/aes")]
        public async Task<IActionResult> Aes(
            [FromQuery] bool force = false,
            [FromQuery] bool noApi = false,
            [FromQuery] bool submit = true,
            CancellationToken ct = default)
        {
            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();

            var toolPath = ExternalAesFinder.ResolveToolPath();
            if (toolPath == null)
            {
                return StatusCode(500, new
                {
                    message = "AesFinder tool not found. Set AESFINDER_PATH to AesFinder.exe (or the directory containing it)."
                });
            }

            UefnAesExtractor.DownloadResult dl;
            try
            {
                dl = await UefnAesExtractor.DownloadAsync(
                    rootDir, Http, "UnrealEditorFortnite-Common-Win64-Shipping.dll",
                    msg => _logger.LogInformation("[AesFinder] {Message}", msg), force, ct);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Request cancelled." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Common DLL download failed");
                return StatusCode(502, new { message = "Failed to download the Common DLL from the manifest.", error = ex.Message });
            }

            ExternalAesFinder.Result r;
            try
            {
                r = await ExternalAesFinder.RunAsync(toolPath, dl.LocalPath, noApi, ct);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Request cancelled." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AesFinder run failed");
                return StatusCode(502, new { message = "AesFinder failed to extract the key.", error = ex.Message });
            }

            // Submit the extracted main key (zero GUID) to the provider and mount any matching paks.
            bool submitted = false;
            int mountedNewFiles = 0;
            string? submitError = null;
            if (submit && !string.IsNullOrWhiteSpace(r.MainKey) && _provider is AbstractVfsFileProvider vfs)
            {
                try
                {
                    int before = _provider.Files.Count;
                    mountedNewFiles = vfs.SubmitKey(new FGuid(0, 0, 0, 0), new FAesKey(r.MainKey));
                    submitted = true;
                    _logger.LogInformation("[AesFinder] Submitted main key; mounted {Mounted} VFS file(s). Total files: {Total}",
                        mountedNewFiles, _provider.Files.Count);
                }
                catch (Exception ex)
                {
                    submitError = ex.Message;
                    _logger.LogWarning(ex, "[AesFinder] Failed to submit the extracted key to the provider");
                }
            }

            return Ok(new
            {
                mainKey = r.MainKey,
                version = r.Version,
                build = r.Build,
                fullVersion = r.FullVersion,
                source = dl.FilePath,
                downloadBuild = dl.Build,
                downloaded = dl.Downloaded,
                tool = Path.GetFileName(r.ToolPath),
                submitted,
                mountedNewFiles,
                totalFiles = _provider.Files.Count,
                submitError
            });
        }

        /// <summary>
        /// Downloads UnrealEditorFortnite-Win64-Shipping.exe from the Fortnite_Studio manifest and scans it
        /// for the AES-256 key with AesFinder (no execution, no injection). Optionally verifies the result
        /// against the live AES key published by the AES API.
        /// </summary>
        /// <param name="verify">Cross-check the extracted key(s) against the live AES API (default true).</param>
        /// <param name="force">Re-download the exe even if a cached copy exists (default false).</param>
        /// <param name="file">Optional: scan a different manifest binary (file name or path suffix) instead of the default exe.</param>
        [HttpGet("extract")]
        public async Task<IActionResult> Extract(
            [FromQuery] bool verify = true,
            [FromQuery] bool force = false,
            [FromQuery] string? file = null,
            CancellationToken ct = default)
        {
            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();

            UefnAesExtractor.Result extraction;
            try
            {
                extraction = await UefnAesExtractor.ExtractAsync(
                    rootDir, Http, msg => _logger.LogInformation("[AesFinder] {Message}", msg), file, force, ct);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Request cancelled." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AES extraction failed");
                return StatusCode(502, new { message = "AES extraction failed.", error = ex.Message });
            }

            object? verification = null;
            if (verify)
            {
                verification = await VerifyAgainstLiveAsync(extraction.Keys, ct);
            }

            return Ok(new
            {
                source = "Fortnite_Studio (Unreal Editor for Fortnite) manifest",
                build = extraction.Build,
                version = extraction.VersionName,
                label = extraction.LabelName,
                downloadUrl = extraction.DownloadUrl,
                exe = extraction.ExePath,
                exeSizeBytes = extraction.ExeSize,
                downloaded = extraction.Downloaded,
                scanSeconds = Math.Round(extraction.ScanSeconds, 2),
                keyCount = extraction.Keys.Count,
                keys = extraction.Keys,
                verification
            });
        }

        /// <summary>
        /// Self-test for the AesFinder scanner: embeds a known key's expanded schedule in a buffer and
        /// confirms the scanner recovers exactly that key. Verifies the algorithm without any download.
        /// </summary>
        [HttpGet("finder/selftest")]
        public IActionResult SelfTest()
        {
            var ok = AesFinder.SelfTest();
            return Ok(new { selfTest = ok ? "passed" : "failed", passed = ok });
        }

        /// <summary>
        /// Runs AesFinder over local file(s) already on disk (e.g. an installed Fortnite build) — no
        /// download. Provide a single <paramref name="path"/> or a <paramref name="dir"/> (all .exe/.dll
        /// inside are scanned). Optionally cross-checks any found key against the live AES API.
        /// </summary>
        /// <param name="path">A single local file to scan.</param>
        /// <param name="dir">A local directory; every .exe/.dll inside is scanned (sorted largest first).</param>
        /// <param name="verify">Cross-check found keys against the live AES API (default true).</param>
        [HttpGet("scan/local")]
        public async Task<IActionResult> ScanLocal(
            [FromQuery] string? path = null,
            [FromQuery] string? dir = null,
            [FromQuery] bool verify = true,
            CancellationToken ct = default)
        {
            var files = new List<string>();
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!System.IO.File.Exists(path)) return NotFound(new { message = $"File not found: {path}" });
                files.Add(path);
            }
            else if (!string.IsNullOrWhiteSpace(dir))
            {
                if (!Directory.Exists(dir)) return NotFound(new { message = $"Directory not found: {dir}" });
                files.AddRange(Directory.EnumerateFiles(dir)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                return BadRequest(new { message = "Provide 'path' (a file) or 'dir' (a directory)." });
            }

            var ordered = files.OrderByDescending(f => new FileInfo(f).Length).ToList();
            var results = new List<object>();
            var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in ordered)
            {
                ct.ThrowIfCancellationRequested();
                long size = new FileInfo(f).Length;
                var sw = Stopwatch.StartNew();
                List<string> keys;
                try
                {
                    keys = AesFinder.FindKeysInFile(f);
                }
                catch (Exception ex)
                {
                    results.Add(new { file = Path.GetFileName(f), sizeBytes = size, error = ex.Message });
                    continue;
                }
                sw.Stop();
                foreach (var k in keys) allKeys.Add(k);
                if (keys.Count > 0)
                {
                    results.Add(new { file = Path.GetFileName(f), sizeBytes = size, scanSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2), keyCount = keys.Count, keys });
                    _logger.LogInformation("[AesFinder] {File}: {Count} key(s) found", Path.GetFileName(f), keys.Count);
                }
            }

            object? verification = verify ? await VerifyAgainstLiveAsync(allKeys.ToList(), ct) : null;

            return Ok(new
            {
                scanned = ordered.Count,
                filesWithKeys = results.Count,
                totalUniqueKeys = allKeys.Count,
                keys = allKeys.ToList(),
                hits = results,
                verification
            });
        }

        /// <summary>
        /// Diagnostic: lists the Fortnite_Studio build's Win64 binaries (exe/dll) with sizes, to identify
        /// which module carries the embedded AES key.
        /// </summary>
        [HttpGet("binaries")]
        public async Task<IActionResult> Binaries(CancellationToken ct = default)
        {
            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
            try
            {
                var list = await UefnAesExtractor.ListBinariesAsync(
                    rootDir, Http, msg => _logger.LogInformation("[AesFinder] {Message}", msg), ct);
                return Ok(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "listing binaries failed");
                return StatusCode(502, new { message = "Failed to list binaries.", error = ex.Message });
            }
        }

        private async Task<object> VerifyAgainstLiveAsync(List<string> extractedKeys, CancellationToken ct)
        {
            try
            {
                var (data, source) = await AesKeyService.FetchAsync(
                    Http, msg => _logger.LogWarning("{Message}", msg), ct);

                if (data is null)
                {
                    return new { available = false, message = "Live AES API unavailable; could not cross-check." };
                }

                var extracted = new HashSet<string>(extractedKeys.Select(Normalize), StringComparer.OrdinalIgnoreCase);

                var mainNorm = Normalize(data.MainKey);
                var mainMatched = !string.IsNullOrEmpty(mainNorm) && extracted.Contains(mainNorm);

                var dynamicMatches = (data.DynamicKeys ?? new List<DynamicKey>())
                    .Where(k => !string.IsNullOrEmpty(k.Key) && extracted.Contains(Normalize(k.Key)))
                    .Select(k => new { guid = k.Guid, key = k.Key })
                    .ToList();

                return new
                {
                    available = true,
                    source,
                    liveMainKey = data.MainKey,
                    mainKeyMatched = mainMatched,
                    dynamicKeysMatched = dynamicMatches.Count,
                    matchedDynamic = dynamicMatches,
                    message = mainMatched
                        ? "Extracted key matches the live main AES key."
                        : "Extracted keys did not include the live main key (the schedule may be stored differently in this build)."
                };
            }
            catch (Exception ex)
            {
                return new { available = false, error = ex.Message };
            }
        }

        private static string Normalize(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            var k = key.Trim();
            if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            return k.ToUpperInvariant();
        }
    }
}
