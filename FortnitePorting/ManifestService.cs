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
using ZlibngDotNet;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting
{
    public partial class ManifestService
    {
        private const string BuildApiUrl = "https://fljpapi.jp/api/v2/build/Windows";
        private const int PollingIntervalSeconds = 30;
        private const int MaxRetryAttempts = 5;
        private const int RetryDelaySeconds = 5;
        
        private readonly IFileProvider _cue4ParseProvider;
        private readonly string _cacheDirectory;
        private readonly string _rootDir;
        private readonly HttpClient _httpClient;
        private readonly Zlibng _zlibng;
        private readonly Timer _pollingTimer;

        public FBuildPatchAppManifest Manifest { get; private set; } = null!;
        public byte[]? ManifestBytes { get; private set; }

        public string GameVersion { get; private set; } = string.Empty;
        public string GameBuild { get; private set; } = string.Empty;
        public string ManifestId { get; private set; } = string.Empty;
        public bool IsReady { get; private set; }

        private string _currentBuildVersion = string.Empty;
        private string _appliedBuildVersion = string.Empty;    // build whose VFS files are currently mounted
        private string _appliedManifestId = string.Empty;      // manifest whose VFS files are currently mounted
        private string _mappedBuildVersion = string.Empty;   // build whose .usmap is currently applied
        private int _mappingAttempts;                          // retries for the current build's mapping
        private const int MaxMappingAttemptsPerBuild = 120;    // ~1h at 30s, then stop retrying

        public ManifestService(IFileProvider cue4ParseProvider, string cacheDirectory, Zlibng zlibng, string rootDir)
        {
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
            await DownloadAndLoadManifestAsync();
            // Polling is started later via StartPolling() once the provider is fully initialized
            // (see FileProviderFactory) so a poll can't race the remaining startup steps.
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
                var usmapPath = FileProviderFactory.ReloadMappings(_cue4ParseProvider, _rootDir);
                if (usmapPath == null)
                {
                    Console.WriteLine($"No .usmap available yet for {GameBuild}; will retry ({_mappingAttempts}/{MaxMappingAttemptsPerBuild}).");
                    return;
                }

                var envUsmap = Environment.GetEnvironmentVariable("USMAP_PATH");
                var pinned = !string.IsNullOrWhiteSpace(envUsmap) && File.Exists(envUsmap);
                var skipped = string.Equals(usmapPath, FileProviderFactory.MappingSkipSentinel, StringComparison.Ordinal);
                // The fortniteapi/uedb mapping file name starts with the build version, so this tells us
                // whether the file we loaded actually belongs to the current build.
                var matchesBuild = !string.IsNullOrEmpty(GameBuild)
                                   && Path.GetFileName(usmapPath).StartsWith(GameBuild, StringComparison.OrdinalIgnoreCase);

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

        private async Task<BuildInfo?> FetchBuildInfoAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(BuildApiUrl);
                var apiResponse = JsonConvert.DeserializeObject<BuildApiResponse>(response);
                
                if (apiResponse?.Elements != null && apiResponse.Elements.Count > 0)
                {
                    return apiResponse.Elements[0];
                }
                
                Console.WriteLine("The API response does not contain any build information");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to retrieve build information: {ex.Message}");
                throw;
            }
        }

        private async Task DownloadAndLoadManifestAsync()
        {
            var buildInfo = await FetchBuildInfoAsync();
            await DownloadAndLoadManifestAsync(buildInfo);
        }

        private async Task DownloadAndLoadManifestAsync(BuildInfo? buildInfo)
        {
            if (buildInfo == null || buildInfo.Manifests.Count == 0)
            {
                throw new Exception("Could not retrieve manifest information");
            }

            // Get the first manifest URI and its query parameters
            var manifestInfo = buildInfo.Manifests[0];
            var manifestUrl = manifestInfo.Uri;

            // Append the query parameters
            if (manifestInfo.QueryParams.Count > 0)
            {
                var queryString = string.Join("&", manifestInfo.QueryParams.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
                manifestUrl = $"{manifestUrl}?{queryString}";
            }

            Console.WriteLine($"Downloading the manifest: {manifestUrl}");

            // Download the manifest file (with retry support)
            ManifestBytes = await DownloadManifestWithRetryAsync(manifestUrl);
            Console.WriteLine($"Downloaded the manifest: {ManifestBytes.Length} bytes");

            // Clean up memory before parsing
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Parse the manifest
            Manifest = FBuildPatchAppManifest.Deserialize(ManifestBytes, options =>
            {
                options.ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/";
                options.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
                options.DecompressorState = _zlibng;
                options.ChunkCacheDirectory = _cacheDirectory;
                options.CacheChunksAsIs = false;
            });

            // Initialize the information
            InitInformations(buildInfo);
            _currentBuildVersion = buildInfo.BuildVersion;

            // Clean up memory
            GC.Collect();
            GC.WaitForPendingFinalizers();

            IsReady = true;
            Console.WriteLine($"Finished loading the manifest: BuildVersion={GameBuild}");
        }

        private async Task<byte[]> DownloadManifestWithRetryAsync(string url)
        {
            int attempt = 0;
            Exception? lastException = null;

            while (attempt < MaxRetryAttempts)
            {
                attempt++;
                try
                {
                    if (attempt > 1)
                    {
                        Console.WriteLine($"Retrying the manifest download... (attempt: {attempt}/{MaxRetryAttempts})");
                    }

                    var response = await _httpClient.GetAsync(url);
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine($"403 Forbidden error: the token may have expired");

                        if (attempt < MaxRetryAttempts)
                        {
                            Console.WriteLine($"Retrying in {RetryDelaySeconds} seconds after fetching new build information...");
                            await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));

                            // Fetch new build information and generate a new URL
                            var buildInfo = await FetchBuildInfoAsync();
                            if (buildInfo != null && buildInfo.Manifests.Count > 0)
                            {
                                var manifestInfo = buildInfo.Manifests[0];
                                url = manifestInfo.Uri;
                                
                                if (manifestInfo.QueryParams.Count > 0)
                                {
                                    var queryString = string.Join("&", manifestInfo.QueryParams.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
                                    url = $"{url}?{queryString}";
                                }
                                
                                Console.WriteLine($"New manifest URL: {url}");
                            }
                            continue;
                        }
                    }
                    
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync();
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    lastException = ex;
                    Console.WriteLine($"✗ Manifest download failed (attempt {attempt}/{MaxRetryAttempts}): 403 Forbidden");
                    
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Console.WriteLine($"✗ Manifest download failed (attempt {attempt}/{MaxRetryAttempts}): {ex.Message}");
                    
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                    }
                }
            }

            throw new Exception($"Manifest download failed {MaxRetryAttempts} times", lastException);
        }

        private void InitInformations(BuildInfo buildInfo)
        {
            GameBuild = Manifest.Meta.BuildVersion;

            var data = GameBuild.Split('-');
            if (data.Length > 2)
            {
                GameVersion = data[1];
            }

            ManifestId = GetManifestId(buildInfo);
        }

        /// <summary>Returns the manifest file name (its id) that the build info points at.</summary>
        private static string GetManifestId(BuildInfo buildInfo)
        {
            if (buildInfo.Manifests.Count == 0)
            {
                return string.Empty;
            }

            return buildInfo.Manifests[0].Uri.Split('/').Last().Split('?')[0];
        }

        public void LoadManifestArchives()
        {
            if (_cue4ParseProvider is not AbstractVfsFileProvider vfsProvider)
            {
                Console.WriteLine("Error: CUE4Parse provider is not a VFS provider. Cannot load manifest archives.");
                return;
            }

            Manifest.Files
                .Where(x => MyRegex().IsMatch(x.FileName))
                .AsParallel()
                .WithDegreeOfParallelism(8)
                .ForAll(file => LoadFileManifest(file, vfsProvider));
        }

        private void LoadFileManifest(FFileManifest file, AbstractVfsFileProvider vfsProvider)
        {
            var versions = _cue4ParseProvider.Versions;
            vfsProvider.RegisterVfs(file.FileName, [file.GetStream()],
                it => new FRandomAccessStreamArchive(it, GetStream(it), versions));
        }

        private FFileManifestStream GetStream(string fileName)
        {
            return Manifest.FindFile(fileName)!.GetStream();
        }

        [GeneratedRegex(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex MyRegex();
    }
}