using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FortnitePorting.Services.Local;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Works with a Fortnite installation that is already on this machine, rather than the build this
    /// API streams from Epic's manifests: find it, mount it, and read its AES keys.
    /// </summary>
    [ApiController]
    [Route("api/v1/local")]
    public class LocalController : ControllerBase
    {
        private readonly LocalBuildService _localBuilds;
        private readonly ILogger<LocalController> _logger;

        public LocalController(LocalBuildService localBuilds, ILogger<LocalController> logger)
        {
            _localBuilds = localBuilds;
            _logger = logger;
        }

        /// <summary>
        /// Lists the Fortnite installations this machine appears to have and which of them are mounted
        /// right now. Nothing is opened and no key is read, so this is safe to poll.
        /// </summary>
        [HttpGet]
        public IActionResult Status()
        {
            var detected = LocalInstallLocator.Detect().Select(d => new
            {
                directory = d.Directory,
                source = d.Source,
                displayName = d.DisplayName,
                appName = d.AppName,
                buildVersion = d.BuildVersion,
                hasArchives = d.HasArchives
            }).ToList();

            var mounted = _localBuilds.Loaded.Select(b => new
            {
                directory = b.Directory,
                mountedUtc = b.MountedUtc,
                mountSeconds = Math.Round(b.MountSeconds, 2),
                archives = b.ArchiveCount,
                mountedVfs = b.Provider.MountedVfs.Count,
                files = b.Provider.Files.Count,
                keys = b.Keys.Keys.Count(k => k.Key != null),
                inFlight = b.InFlight,
                lastUsedUtc = b.LastUsedUtc
            }).ToList();

            return Ok(new
            {
                detected = detected.Count,
                installations = detected,
                mounted,
                maxMounted = _localBuilds.MaxLoaded,
                idleMinutes = _localBuilds.IdleTimeout.TotalMinutes,
                directoryVariable = LocalInstallLocator.DirectoryVariable,
                hint = detected.Count == 0
                    ? "No installation was found. Pass 'dir' with the install directory (or the folder " +
                      $"holding the .pak/.utoc files), or set {LocalInstallLocator.DirectoryVariable}."
                    : "GET /api/v1/aes/local reads its AES keys; POST /api/v1/local/mount mounts it for dumping."
            });
        }

        /// <summary>
        /// Mounts a local installation so it can be dumped from, keeping it loaded until it goes idle.
        /// The AES keys are worked out the same way <c>GET /api/v1/aes/local</c> does.
        /// </summary>
        /// <remarks>
        /// An installation that is already mounted is returned as it is, keys included. To mount it again
        /// with different keys, unmount it first with DELETE /api/v1/local/mount.
        /// </remarks>
        /// <param name="dir">Installation directory, or the folder holding the .pak/.utoc files. Auto-detected when omitted.</param>
        /// <param name="key">Extra key(s) to try, as <c>hex</c> or <c>guid:hex</c>. Tried before anything else.</param>
        /// <param name="scan">Scan the installation's own binaries for compiled-in keys (default true).</param>
        /// <param name="deep">Also run the slower key-schedule scanner over those binaries.</param>
        /// <param name="api">Fall back to the live AES APIs for GUIDs the installation did not answer (default true).</param>
        /// <param name="cancellationToken">Request cancellation state.</param>
        [HttpPost("mount")]
        public async Task<IActionResult> Mount(
            [FromQuery] string? dir = null,
            [FromQuery] string[]? key = null,
            [FromQuery] bool scan = true,
            [FromQuery] bool deep = false,
            [FromQuery] bool api = true,
            CancellationToken cancellationToken = default)
        {
            var options = new LocalBuildService.OpenOptions
            {
                Directory = dir,
                Keys = new LocalKeyResolver.Options
                {
                    RequestedKeys = key ?? [],
                    ScanBinaries = scan,
                    DeepScan = deep,
                    UseLiveApi = api
                }
            };

            LocalBuildLease lease;
            try
            {
                lease = await _localBuilds.LeaseAsync(options,
                    msg => _logger.LogInformation("[LocalBuild] {Message}", msg), cancellationToken);
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
                _logger.LogError(ex, "Mounting the local build failed");
                return StatusCode(500, new { message = "Mounting the local build failed.", error = ex.Message });
            }

            using (lease)
            {
                var build = lease.Build;
                return Ok(new
                {
                    directory = build.Directory,
                    mountSeconds = Math.Round(build.MountSeconds, 2),
                    archives = build.ArchiveCount,
                    mountedVfs = build.Provider.MountedVfs.Count,
                    unmountedVfs = build.Provider.UnloadedVfs.Count,
                    files = build.Provider.Files.Count,
                    keys = build.Keys.Keys.Select(k => new
                    {
                        guid = k.Guid,
                        key = k.Key,
                        verified = k.Verified,
                        source = k.Source,
                        archives = k.ArchiveCount
                    }),
                    unresolvedKeys = build.Keys.Keys.Count(k => k.Key == null),
                    hint = "POST /api/v1/mappings/dump/local dumps a .usmap from it; DELETE /api/v1/local/mount frees it."
                });
            }
        }

        /// <summary>
        /// Unmounts a local installation and frees its memory. Without <paramref name="dir"/> every
        /// mounted installation is dropped. A build still being read is dropped once that read finishes.
        /// </summary>
        /// <param name="dir">The installation to unmount; all of them when omitted.</param>
        [HttpDelete("mount")]
        public IActionResult Unmount([FromQuery] string? dir = null)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                var count = _localBuilds.UnloadAll();
                return Ok(new { unmounted = count });
            }

            var removed = _localBuilds.Unload(dir);
            return removed
                ? Ok(new { unmounted = 1, directory = dir })
                : NotFound(new { message = $"'{dir}' is not mounted." });
        }
    }
}
