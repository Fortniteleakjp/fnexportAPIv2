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
        /// Produces the AES keys of a Fortnite installation that is already on this machine: the
        /// installation's own binaries are scanned for compiled-in keys and its own containers decide
        /// which candidate is right, one key per encryption GUID they ask for.
        /// </summary>
        /// <remarks>
        /// Nothing here depends on the live key APIs, which is the point of naming a local build: an
        /// installation that is newer or older than whatever those APIs publish still yields its keys,
        /// because <c>TestAesKey</c> decrypts the container's own mount-point check bytes and only the
        /// real key does that. The APIs are still consulted, last, for any GUID the binaries did not
        /// answer (pass <c>api=false</c> to stay entirely offline).
        /// </remarks>
        /// <param name="dir">Installation directory, or the folder holding the .pak/.utoc files. Auto-detected when omitted.</param>
        /// <param name="key">Extra key(s) to try, as <c>hex</c> or <c>guid:hex</c>. Tried before anything else — use this to verify a key you already have.</param>
        /// <param name="scan">Scan the installation's own binaries for compiled-in keys (default true).</param>
        /// <param name="deep">Also run the slower key-schedule scanner, for builds that store the key expanded.</param>
        /// <param name="binary">Only scan binaries whose file name contains this.</param>
        /// <param name="binaries">How many binaries to scan, most promising first (default 8).</param>
        /// <param name="api">Consult the live AES APIs for GUIDs the installation did not answer (default true).</param>
        /// <param name="mount">Keep the installation mounted afterwards, ready for a dump (default false).</param>
        /// <param name="submit">Submit the verified keys to this API's own provider as well (default false).</param>
        /// <param name="save">Write the keys to <c>aes.local.json</c> in the project root (default false).</param>
        /// <param name="ct">Request cancellation state.</param>
        [HttpGet("local")]
        public async Task<IActionResult> Local(
            [FromQuery] string? dir = null,
            [FromQuery] string[]? key = null,
            [FromQuery] bool scan = true,
            [FromQuery] bool deep = false,
            [FromQuery] string? binary = null,
            [FromQuery] int binaries = 8,
            [FromQuery] bool api = true,
            [FromQuery] bool mount = false,
            [FromQuery] bool submit = false,
            [FromQuery] bool save = false,
            CancellationToken ct = default)
        {
            var options = new LocalBuildService.OpenOptions
            {
                Directory = dir,
                Keys = new LocalKeyResolver.Options
                {
                    RequestedKeys = key ?? [],
                    ScanBinaries = scan,
                    DeepScan = deep,
                    BinaryFilter = binary,
                    BinaryLimit = Math.Clamp(binaries, 1, 256),
                    UseLiveApi = api
                }
            };

            string root;
            int archiveCount;
            LocalKeyResolver.Result keys;
            double elapsed;
            int registered, encrypted, mountedVfs = 0, files = 0;

            try
            {
                if (mount)
                {
                    using var lease = await _localBuilds.LeaseAsync(options,
                        msg => _logger.LogInformation("[LocalAes] {Message}", msg), ct);

                    var build = lease.Build;
                    root = build.Root;
                    archiveCount = build.ArchiveCount;
                    keys = build.Keys;
                    elapsed = Math.Round(build.MountSeconds, 2);
                    registered = build.Provider.MountedVfs.Count + build.Provider.UnloadedVfs.Count;
                    encrypted = build.Provider.UnloadedVfs.Count(r => r.IsEncrypted);
                    mountedVfs = build.Provider.MountedVfs.Count;
                    files = build.Provider.Files.Count;
                }
                else
                {
                    var inspection = await _localBuilds.InspectAsync(options,
                        msg => _logger.LogInformation("[LocalAes] {Message}", msg), ct);

                    root = inspection.Install.Root;
                    archiveCount = inspection.Install.ArchiveCount;
                    keys = inspection.Keys;
                    elapsed = inspection.ElapsedSeconds;
                    registered = inspection.RegisteredArchives;
                    encrypted = inspection.EncryptedArchives;
                }
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Request cancelled." });
            }
            catch (DirectoryNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reading the local build's AES keys failed");
                return StatusCode(500, new { message = "Reading the local build's AES keys failed.", error = ex.Message });
            }

            // The live provider mounts whatever these keys open. A wrong key mounts nothing, so this is
            // only useful when the local installation is the build this API is serving.
            int submittedMounts = 0;
            string? submitError = null;
            if (submit && keys.Submittable.Count > 0 && _provider is AbstractVfsFileProvider vfs)
            {
                try
                {
                    submittedMounts = vfs.SubmitKeys(keys.Submittable);
                }
                catch (Exception ex)
                {
                    submitError = ex.Message;
                    _logger.LogWarning(ex, "[LocalAes] Submitting the local keys to the provider failed");
                }
            }

            string? savedTo = null;
            if (save)
            {
                try
                {
                    var rootDir = AppPaths.ProjectRoot;
                    savedTo = LocalKeyResolver.Save(keys, rootDir, Path.GetFileName(root));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LocalAes] Saving the local keys failed");
                }
            }

            var unresolved = keys.Keys.Where(k => k.Key == null).ToList();

            return Ok(new
            {
                directory = root,
                archives = archiveCount,
                registeredArchives = registered,
                encryptedArchives = encrypted,
                mounted = mount,
                mountedVfs,
                totalFiles = files,
                elapsedSeconds = elapsed,
                mainKey = keys.Main?.Key,
                mainKeyVerified = keys.Main?.Verified ?? false,
                requiredKeys = keys.RequiredGuids,
                resolvedKeys = keys.Keys.Count(k => k.Key != null),
                keys = keys.Keys.Select(k => new
                {
                    guid = k.Guid,
                    isMain = k.IsMain,
                    key = k.Key,
                    verified = k.Verified,
                    source = k.Source,
                    reason = k.Reason,
                    archiveCount = k.ArchiveCount,
                    archives = k.Archives
                }),
                unresolved = unresolved.Select(k => new { guid = k.Guid, reason = k.Reason, archives = k.Archives }),
                candidatesTried = keys.CandidateCount,
                scanStoppedEarly = keys.ScanStoppedEarly,
                scannedBinaries = keys.Binaries.Select(b => new
                {
                    file = b.File,
                    path = b.Path,
                    sizeBytes = b.SizeBytes,
                    candidates = b.Candidates,
                    scanSeconds = b.ScanSeconds,
                    error = b.Error
                }),
                liveApi = new { used = api, source = keys.ApiSource, error = keys.ApiError },
                submitted = submit,
                submittedMounts,
                submitError,
                savedTo,
                aesJson = LocalKeyResolver.ToAesJson(keys, Path.GetFileName(root))
            });
        }
    }
}
