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

namespace FortnitePorting
{
    public partial class ManifestService
    {
        private const string BuildApiUrl = "https://fljpapi.jp/api/v2/build/Windows";
        private const int PollingIntervalMinutes = 3;
        private const int MaxRetryAttempts = 5;
        private const int RetryDelaySeconds = 5;
        
        private readonly IFileProvider _cue4ParseProvider;
        private readonly string _cacheDirectory;
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

        public ManifestService(IFileProvider cue4ParseProvider, string cacheDirectory, Zlibng zlibng)
        {
            _cue4ParseProvider = cue4ParseProvider;
            _cacheDirectory = cacheDirectory;
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

            // Start polling every 3 minutes
            _pollingTimer.Change(TimeSpan.FromMinutes(PollingIntervalMinutes), TimeSpan.FromMinutes(PollingIntervalMinutes));
            Console.WriteLine($"Started manifest polling (interval: {PollingIntervalMinutes} minutes)");
        }

        private async void CheckForUpdates(object? state)
        {
            try
            {
                Console.WriteLine("Checking for build version updates...");
                var buildInfo = await FetchBuildInfoAsync();

                if (buildInfo != null && buildInfo.BuildVersion != _currentBuildVersion)
                {
                    Console.WriteLine($"Detected a new build version: {_currentBuildVersion} -> {buildInfo.BuildVersion}");
                    await DownloadAndLoadManifestAsync();

                    // Auto-apply the new build: register & mount any newly-added VFS archives (no restart).
                    MountNewArchives();
                }
                else
                {
                    Console.WriteLine($"Build version is unchanged: {_currentBuildVersion}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while checking the build version: {ex.Message}");
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

            var uri = buildInfo.Manifests[0].Uri;
            ManifestId = uri.Split('/').Last().Split('?')[0];
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

        /// <summary>
        /// Registers and mounts any VFS archives in the (updated) manifest that the provider does not
        /// yet know about, so a new build's content comes online without a restart. Newly-encrypted
        /// paks (with brand-new key GUIDs) mount once their key arrives via the AES monitor.
        /// </summary>
        public void MountNewArchives()
        {
            if (_cue4ParseProvider is not AbstractVfsFileProvider vfsProvider || Manifest is null)
            {
                return;
            }

            var known = new HashSet<string>(
                vfsProvider.MountedVfs.Select(r => r.Name).Concat(vfsProvider.UnloadedVfs.Select(r => r.Name)),
                StringComparer.OrdinalIgnoreCase);

            var versions = _cue4ParseProvider.Versions;
            int registered = 0;
            foreach (var file in Manifest.Files.Where(x => VfsRegex().IsMatch(x.FileName)))
            {
                var name = Path.GetFileName(file.FileName);
                if (known.Contains(name))
                {
                    continue;
                }

                try
                {
                    vfsProvider.RegisterVfs(file.FileName, [file.GetStream()],
                        it => new FRandomAccessStreamArchive(it, GetStream(it), versions));
                    known.Add(name);
                    registered++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to register new VFS '{name}': {ex.Message}");
                }
            }

            if (registered == 0)
            {
                Console.WriteLine("No new VFS archives in the updated build.");
                return;
            }

            Console.WriteLine($"Registered {registered} new VFS archive(s) from the updated build. Mounting...");
            var mounted = vfsProvider.Mount();                   // non-encrypted / globally-available archives
            mounted += vfsProvider.SubmitKeys(vfsProvider.Keys); // re-apply known keys -> mount new paks with known GUIDs
            Console.WriteLine($"Mounted {mounted} new VFS file(s). Total files: {_cue4ParseProvider.Files.Count}. " +
                              "(Newly-encrypted paks mount as their keys arrive via the AES monitor.)");
        }

        [GeneratedRegex(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex MyRegex();

        [GeneratedRegex(@"FortniteGame[/\\](Content|Plugins)[/\\].*\.(utoc|pak)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex VfsRegex();
    }
}