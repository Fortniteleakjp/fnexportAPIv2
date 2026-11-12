using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using ZlibngDotNet;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting
{
    public partial class ManifestService
    {
        private const string DefaultBuildApiUrl = "https://fljpapi.jp/api/v2/build/Windows";

        /// <summary>
        /// Where "which build is live?" is asked. Overridable with <c>BUILD_API_URL</c> so an instance
        /// can be pointed at a mirror while the default source is down, rather than having to wait it out.
        /// </summary>
        private static readonly string BuildApiUrl =
            Environment.GetEnvironmentVariable("BUILD_API_URL") is { Length: > 0 } configured
                ? configured
                : DefaultBuildApiUrl;

        /// <summary>Epic CDN root the manifests address their chunks against.</summary>
        public const string ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/";

        private const int PollingIntervalSeconds = 30;
        private const int MaxRetryAttempts = 5;
        private const int RetryDelaySeconds = 5;
        
        private readonly IFileProvider _cue4ParseProvider;
        private readonly string _cacheDirectory;
        private readonly string _rootDir;
        private readonly HttpClient _httpClient;
        private readonly Zlibng _zlibng;
        private readonly Timer _pollingTimer;
        private readonly BuildHistoryStore _history;

        public FBuildPatchAppManifest Manifest { get; private set; } = null!;
        public byte[]? ManifestBytes { get; private set; }

        public string GameVersion { get; private set; } = string.Empty;
        public string GameBuild { get; private set; } = string.Empty;
        public string ManifestId { get; private set; } = string.Empty;
        public bool IsReady { get; private set; }

        private string _currentBuildVersion = string.Empty;
        private string _previousBuildVersion = string.Empty;   // build served before the current one
        private string _appliedBuildVersion = string.Empty;    // build whose VFS files are currently mounted
        private string _appliedManifestId = string.Empty;      // manifest whose VFS files are currently mounted
        private string _mappedBuildVersion = string.Empty;   // build whose .usmap is currently applied
        private int _mappingAttempts;                          // retries for the current build's mapping
        private const int MaxMappingAttemptsPerBuild = 120;    // ~1h at 30s, then stop retrying

        public ManifestService(IFileProvider cue4ParseProvider, string cacheDirectory, Zlibng zlibng, string rootDir,
            BuildHistoryStore history)
        {
            _history = history;
            _cue4ParseProvider = cue4ParseProvider;
            _cacheDirectory = cacheDirectory;
            _rootDir = rootDir;
            _zlibng = zlibng;
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All
            });
            
            _pollingTimer = new Timer(CheckForUpdates, null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task InitializeAsync()
        {
            Console.WriteLine("Initializing ManifestService...");

            try
            {
                await DownloadAndLoadManifestAsync();
            }
            catch (Exception ex)
            {
                // The build API being down must not stop this one from starting. Every build this
                // instance has served is archived as a manifest, and a manifest is all it takes to mount
                // that build again - so the previous build is served until a poll finds the live one.
                Console.WriteLine($"✗ Could not load the live build: {ex.Message}");

                if (!TryLoadArchivedManifest())
                {
                    throw new Exception(
                        "Could not load a build: the build API is unavailable and this instance has no " +
                        "archived manifest to fall back on. Retry once it answers again, or put a manifest " +
                        "file in build_history/manifests/ (POST /api/v1/versions/import writes one).", ex);
                }
            }

            // Polling is started later via StartPolling() once the provider is fully initialized
            // (see FileProviderFactory) so a poll can't race the remaining startup steps.
        }

        /// <summary>
        /// Mounts the newest build whose manifest is still archived, for the case where the build API
        /// cannot be reached at startup. A build served this way is a real, complete build - the
        /// manifest addresses the same chunks on the Epic CDN - it is just not necessarily the newest
        /// one. The next successful poll switches to the live build without a restart.
        /// </summary>
        private bool TryLoadArchivedManifest()
        {
            // The newest build, not the most recently archived one: importing an old build's manifest
            // puts it at the top of the index by archive time, and falling back to it would quietly
            // serve outdated content. The changelist is what actually orders Fortnite builds.
            var archived = _history.GetBuilds()
                .Where(b => b.HasManifest)
                .OrderByDescending(b => long.TryParse(b.Changelist, out var cl) ? cl : 0L)
                .ThenByDescending(b => b.ArchivedUtc)
                .FirstOrDefault();

            var manifestPath = archived == null ? null : _history.GetManifestPath(archived.BuildVersion);

            if (archived == null || manifestPath == null)
            {
                Console.WriteLine("  No archived build is available to fall back on.");
                return false;
            }

            try
            {
                Console.WriteLine($"  Falling back to the archived build {archived.BuildVersion}...");

                ManifestBytes = File.ReadAllBytes(manifestPath);
                Manifest = DeserializeManifest(ManifestBytes);

                GameBuild = Manifest.Meta.BuildVersion;
                var parts = GameBuild.Split('-');
                if (parts.Length > 2)
                {
                    GameVersion = parts[1];
                }

                // The manifest id comes from the build info, which is exactly what is unavailable here,
                // so the one recorded when this build was archived stands in. It is the same value, and
                // it keeps the first successful poll from treating the live build as a change when it
                // is the build already mounted.
                ManifestId = archived.ManifestId ?? string.Empty;
                _currentBuildVersion = GameBuild;

                IsReady = true;
                Console.WriteLine($"✓ Serving the archived build {GameBuild} until the build API answers again.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  The archived manifest could not be loaded either: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the build-update poll. Uses a one-shot timer that is re-armed after each run, so
        /// polls can never overlap (a slow build switch + mapping download cannot collide with the
        /// next tick) and the period never drifts.
        /// </summary>
        public void StartPolling()
        {
            _pollingTimer.Change(TimeSpan.FromSeconds(PollingIntervalSeconds), Timeout.InfiniteTimeSpan);
            Console.WriteLine($"Started manifest polling (interval: {PollingIntervalSeconds} seconds)");
        }

        /// <summary>
        /// Records that the VFS files and the .usmap of the current build are applied (called once the
        /// startup load has finished), so the first poll does not trigger a redundant rebuild.
        /// </summary>
        public void MarkCurrentBuildApplied()
        {
            _appliedBuildVersion = _currentBuildVersion;
            _appliedManifestId = ManifestId;
            _mappedBuildVersion = _currentBuildVersion;
        }

        /// <summary>
        /// True when the mounted content no longer matches the manifest that is loaded. The manifest id
        /// is part of the check so a re-published manifest (same build version, different content) is
        /// applied as well.
        /// </summary>
        private bool NeedsProviderReload =>
            _appliedBuildVersion != _currentBuildVersion || _appliedManifestId != ManifestId;

        /// <summary>The build version whose content is currently mounted (empty before the first load).</summary>
        public string AppliedBuildVersion => _appliedBuildVersion;

        /// <summary>The build this instance served before the current one (empty until the first update).</summary>
        public string PreviousBuildVersion => _previousBuildVersion;

        /// <summary>
        /// Invoked with (previousBuild, currentBuild) once the provider has been rebuilt onto a new
        /// build. Wired up by <see cref="Services.FileProviderFactory"/> to record the changelist
        /// between the two builds and apply the retention policy.
        /// </summary>
        public Func<string, string, Task>? BuildUpdated { get; set; }

        /// <summary>Invoked at the end of every poll, after the mapping refresh.</summary>
        public Action? PollCompleted { get; set; }

        private async Task RaiseBuildUpdatedAsync()
        {
            var handler = BuildUpdated;
            var previous = _previousBuildVersion;

            if (handler == null || string.IsNullOrEmpty(previous) ||
                string.Equals(previous, GameBuild, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                await handler(previous, GameBuild);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Recording the changelist {previous} -> {GameBuild} failed: {ex.Message}");
            }
        }

        /// <summary>The manifest whose content is currently mounted (empty before the first load).</summary>
        public string AppliedManifestId => _appliedManifestId;

        /// <summary>True when the mounted content matches the manifest that is currently loaded.</summary>
        public bool IsUpToDate => !NeedsProviderReload;

        /// <summary>True while the provider is being rebuilt for a new build.</summary>
        public bool IsReloading => ProviderReloadGate.Instance.IsReloading;

        /// <summary>
        /// Rebuilds the provider from the newest manifest on demand (used by the manual reload endpoint).
        /// Re-fetches the build info first so a build that appeared since the last poll is picked up.
        /// </summary>
        public async Task<bool> ForceReloadAsync()
        {
            var buildInfo = await FetchBuildInfoAsync();
            if (buildInfo != null &&
                (buildInfo.BuildVersion != _currentBuildVersion || GetManifestId(buildInfo) != ManifestId))
            {
                await DownloadAndLoadManifestAsync(buildInfo);
            }

            _appliedBuildVersion = string.Empty;  // force the rebuild even when the build is unchanged
            await ReloadProviderAsync();
            return !NeedsProviderReload;
        }

        private async void CheckForUpdates(object? state)
        {
            try
            {
                Console.WriteLine("Checking for build version updates...");
                var buildInfo = await FetchBuildInfoAsync();

                // A new manifest id with the same build version also means new content, so both are checked.
                if (buildInfo != null &&
                    (buildInfo.BuildVersion != _currentBuildVersion || GetManifestId(buildInfo) != ManifestId))
                {
                    Console.WriteLine($"Detected a new build: {_currentBuildVersion} -> {buildInfo.BuildVersion} (manifest {GetManifestId(buildInfo)})");
                    await DownloadAndLoadManifestAsync(buildInfo);   // reuses the build info just fetched
                }
                else
                {
                    Console.WriteLine($"Build version is unchanged: {_currentBuildVersion}");
                }

                // Apply the build the manifest now describes. An update rewrites the existing containers,
                // so the whole VFS is rebuilt from the new manifest rather than only adding archives that
                // appeared - otherwise the mounted readers keep streaming the previous build's chunks.
                if (NeedsProviderReload)
                {
                    await ReloadProviderAsync();
                }

                // Refresh the .usmap so the new build's (possibly changed) types deserialize correctly.
                // Retries (bounded) on later polls if the matching mapping isn't published yet, then hot-swaps.
                RefreshMappingsIfNeeded();

                // Keep the archived AES keys of the live build current. Keys for paks that were still
                // locked at mount time arrive on later polls, and once Fortnite rotates them the live
                // key APIs stop serving them - archiving here is what keeps old builds decryptable.
                PollCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while checking the build version: {ex.Message}");
            }
            finally
            {
                // Re-arm the one-shot timer so the next poll runs only after this one completes (no overlap).
                try { _pollingTimer.Change(TimeSpan.FromSeconds(PollingIntervalSeconds), Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Rebuilds the provider so it serves the build described by the current manifest. On failure the
        /// applied build is left untouched, so the next poll retries instead of keeping the previous
        /// build's content online indefinitely.
        /// </summary>
        private async Task ReloadProviderAsync()
        {
            if (_cue4ParseProvider is not DefaultFileProvider provider)
            {
                Console.WriteLine("Error: the provider is not a DefaultFileProvider; cannot rebuild it for the new build.");
                return;
            }

            var target = _currentBuildVersion;
            try
            {
                // The new build ships its own .usmap; force a fresh fetch and apply it inside the reload
                // (while requests are still blocked) so nothing is deserialized with the old mappings.
                _mappedBuildVersion = string.Empty;
                _mappingAttempts = 0;

                await ProviderReloader.ReloadAsync(provider, Manifest, GameBuild, RefreshMappingsIfNeeded);
                _appliedBuildVersion = target;
                _appliedManifestId = ManifestId;

                // The new build is live now, so record what changed against the build it replaced and
                // let the retention policy drop whatever is too old to keep.
                await RaiseBuildUpdatedAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to rebuild the provider for {target}: {ex.Message}");
                Console.WriteLine("  The rebuild will be retried on the next poll.");
            }
        }

        /// <summary>
        /// Re-downloads and hot-swaps the .usmap when the applied mapping does not match the current
        /// build. Bounded retries handle the window before the mapping API publishes the new build's
        /// file. A pinned USMAP_PATH or SKIP_MAPPING counts as "applied" (no retry).
        /// </summary>
        private void RefreshMappingsIfNeeded()
        {
            if (_mappedBuildVersion == _currentBuildVersion || _mappingAttempts >= MaxMappingAttemptsPerBuild)
            {
                return;
            }

            _mappingAttempts++;
            try
            {
                var usmapPath = FileProviderFactory.ReloadMappings(_cue4ParseProvider, _rootDir, GameBuild);
                if (usmapPath == null)
                {
                    Console.WriteLine($"No .usmap available yet for {GameBuild}; will retry ({_mappingAttempts}/{MaxMappingAttemptsPerBuild}).");
                    return;
                }

                var envUsmap = Environment.GetEnvironmentVariable("USMAP_PATH");
                var pinned = !string.IsNullOrWhiteSpace(envUsmap) && File.Exists(envUsmap);
                var skipped = string.Equals(usmapPath, FileProviderFactory.MappingSkipSentinel, StringComparison.Ordinal);
                // Both the API's file names and this API's own dumps are named after the build, so this
                // tells us whether the file we loaded actually belongs to the current build.
                var matchesBuild = MappingService.BelongsToBuild(Path.GetFileName(usmapPath), GameBuild);

                if (skipped || pinned || matchesBuild)
                {
                    _mappedBuildVersion = _currentBuildVersion;
                    _mappingAttempts = 0;
                }
                else
                {
                    Console.WriteLine($"Mapping for {GameBuild} not published yet (have {Path.GetFileName(usmapPath)}); will retry ({_mappingAttempts}/{MaxMappingAttemptsPerBuild}).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mapping refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads a mapping that was asked for explicitly (dumped, generated or imported with load=true)
        /// and counts it as the current build's, so the retry loop in <see cref="RefreshMappingsIfNeeded"/>
        /// does not swap the API's older mapping back in on the next poll. A new build resets this.
        /// </summary>
        public void ApplyMapping(string usmapPath)
        {
            _cue4ParseProvider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            CacheRegistry.ClearAll();
            _mappedBuildVersion = _currentBuildVersion;
            _mappingAttempts = 0;
            Console.WriteLine($"✓ Applied the mapping {Path.GetFileName(usmapPath)} for {GameBuild}");
        }
    }
}
