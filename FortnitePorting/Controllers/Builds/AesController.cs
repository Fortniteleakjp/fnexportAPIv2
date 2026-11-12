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
    /// <summary>
    /// Statically extracts the Fortnite pak AES key from the Unreal Editor for Fortnite
    /// (appName "Fortnite_Studio") executable, without running or injecting into any process.
    /// </summary>
    [ApiController]
    [Route("api/v1/aes")]
    public partial class AesController : ControllerBase
    {
        // The exe download can take a while; allow plenty of time for the manifest fetch.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private static readonly FGuid ZeroGuid = new(0, 0, 0, 0);

        private readonly IFileProvider _provider;
        private readonly ManifestService _manifestService;
        private readonly LocalBuildService _localBuilds;
        private readonly ILogger<AesController> _logger;

        public AesController(IFileProvider provider, LocalBuildService localBuilds, ILogger<AesController> logger, ManifestService manifestService)
        {
            _provider = provider;
            _manifestService = manifestService;
            _localBuilds = localBuilds;
            _logger = logger;
        }

        /// <summary>
        /// Downloads <c>UnrealEditorFortnite-Common-Win64-Shipping.dll</c> from the Fortnite_Studio manifest and
        /// extracts the Fortnite MainAES key from it. The key is embedded as <c>mov imm32</c> instruction
        /// immediates — no game launch or injection.
        ///
        /// The DLL holds several key-shaped immediate blocks, so every candidate is collected and then tested
        /// against the encrypted archives that are still waiting for the main key; the one that actually
        /// decrypts is returned. That is what makes the result trustworthy on a brand-new build, where the
        /// external tool has no published key to cross-check against and falls back to the highest-entropy
        /// candidate — which is frequently the wrong one. The external tool is optional (AESFINDER_PATH); when
        /// present its answer is simply tried first.
        /// </summary>
        /// <param name="force">Re-download the Common DLL even if a cached copy exists (default false).</param>
        /// <param name="submit">Submit the verified key to the provider (zero GUID) and mount matching paks (default true).</param>
        [HttpGet]
        public async Task<IActionResult> Aes(
            [FromQuery] bool force = false,
            [FromQuery] bool submit = true,
            CancellationToken ct = default)
        {
            var rootDir = AppPaths.ProjectRoot;

            // The external tool is optional: the built-in immediate scanner finds the same candidates, and the
            // key is chosen by decrypting a real archive rather than by whatever the tool ranks first.
            var toolPath = ExternalAesFinder.ResolveToolPath();

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

            ExternalAesFinder.Result? r = null;
            string? toolError = null;
            if (toolPath != null)
            {
                try
                {
                    r = await ExternalAesFinder.RunAsync(toolPath, dl.LocalPath, ct);
                }
                catch (OperationCanceledException)
                {
                    return StatusCode(499, new { message = "Request cancelled." });
                }
                catch (Exception ex)
                {
                    // Not fatal any more: the built-in scanner below covers the same patterns.
                    toolError = ex.Message;
                    _logger.LogWarning(ex, "AesFinder run failed; falling back to the built-in scanner");
                }
            }
            else
            {
                toolError = "AesFinder tool not found (set AESFINDER_PATH); used the built-in scanner.";
            }

            // Collect every key-shaped block in the DLL, not just the one the tool ranks first. Without an
            // external API to label it, the tool returns the highest-entropy candidate, and that candidate
            // is regularly not the pak key.
            List<AesImmediateScanner.Candidate> scanned;
            try
            {
                scanned = AesImmediateScanner.FindInFile(dl.LocalPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AesFinder] Built-in scan of {File} failed", Path.GetFileName(dl.LocalPath));
                scanned = new List<AesImmediateScanner.Candidate>();
            }

            // Constant-function hits first (the tool cannot see them), then the tool's answer, then the rest.
            var candidates = AesImmediateScanner.OrderCandidates(r?.MainKey, r?.Candidates.Skip(1), scanned);

            if (candidates.Count == 0)
            {
                return StatusCode(502, new
                {
                    message = "No AES key candidates were found in the Common DLL.",
                    error = toolError
                });
            }

            // Decide by decrypting a real archive rather than by ranking.
            var vfs = _provider as AbstractVfsFileProvider;
            AesKeyPicker.Result pick;
            if (vfs != null)
            {
                pick = AesKeyPicker.Pick(vfs, ZeroGuid, candidates);
                _logger.LogInformation("[AesFinder] Key selection: {Reason} ({Count} candidate(s) extracted)",
                    pick.Reason, candidates.Count);
            }
            else
            {
                pick = new AesKeyPicker.Result { Key = candidates[0], Reason = "Provider is not a VFS provider; cannot verify." };
            }

            if (pick.Key == null)
            {
                return StatusCode(502, new
                {
                    message = "Every extracted key candidate was rejected by the encrypted archives.",
                    reason = pick.Reason,
                    candidateCount = candidates.Count,
                    candidates,
                    toolMainKey = r?.MainKey,
                    toolError
                });
            }

            // Submit the verified main key (zero GUID) to the provider and mount any matching paks.
            bool submitted = false;
            int mountedNewFiles = 0;
            string? submitError = null;
            if (submit && vfs != null)
            {
                try
                {
                    mountedNewFiles = vfs.SubmitKey(ZeroGuid, new FAesKey(pick.Key));
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
                mainKey = pick.Key,
                verified = pick.Validated,
                selection = pick.Reason,
                candidateCount = candidates.Count,
                candidatesTried = pick.Tested,
                rejectedKeys = pick.Rejected,
                toolMainKey = r?.MainKey,
                toolKeyWasWrong = r?.MainKey != null && pick.Validated
                                  && !string.Equals(Normalize(r.MainKey), Normalize(pick.Key), StringComparison.OrdinalIgnoreCase),
                toolError,
                version = r?.Version,
                build = r?.Build,
                fullVersion = r?.FullVersion,
                downloadBuild = dl.Build,
                downloaded = dl.Downloaded,
                submitted,
                mountedNewFiles,
                totalFiles = _provider.Files.Count,
                submitError
            });
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
