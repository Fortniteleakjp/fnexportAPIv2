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
using FortnitePorting.Services.Local;

namespace FortnitePorting.Controllers
{
    public partial class AesController
    {
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
            var rootDir = AppPaths.ProjectRoot;

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
                immediateKeyCount = extraction.ImmediateKeyCount,
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
            var rootDir = AppPaths.ProjectRoot;
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
    }
}
