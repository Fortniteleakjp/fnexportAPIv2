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
        /// <summary>
        /// Reads which build is live. Transient failures are retried: the build API answering 503 for a
        /// few seconds is ordinary, and without a retry it used to take the whole startup down with it.
        /// A failure that will not fix itself (a 404, an unparseable body) is reported immediately.
        /// </summary>
        private async Task<BuildInfo?> FetchBuildInfoAsync()
        {
            Exception? lastException = null;

            for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        Console.WriteLine($"Retrying the build information... (attempt: {attempt}/{MaxRetryAttempts})");
                    }

                    var response = await _httpClient.GetStringAsync(BuildApiUrl);
                    var apiResponse = JsonConvert.DeserializeObject<BuildApiResponse>(response);

                    if (apiResponse?.Elements != null && apiResponse.Elements.Count > 0)
                    {
                        return apiResponse.Elements[0];
                    }

                    Console.WriteLine("The API response does not contain any build information");
                    return null;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetryAttempts)
                {
                    lastException = ex;
                    Console.WriteLine($"✗ Build information unavailable (attempt {attempt}/{MaxRetryAttempts}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to retrieve build information: {ex.Message}");
                    throw;
                }
            }

            throw new Exception($"Could not retrieve build information after {MaxRetryAttempts} attempts", lastException);
        }

        /// <summary>
        /// True for a failure that is worth trying again: the server is overloaded or restarting (5xx),
        /// it is rate-limiting us, or the request never got an answer at all.
        /// </summary>
        private static bool IsTransient(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return true; // the HttpClient timeout surfaces as TaskCanceledException
            }

            if (exception is not HttpRequestException request)
            {
                return false;
            }

            if (request.StatusCode is not { } status)
            {
                return true; // DNS/connection level: no response was received
            }

            return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                   || (int) status >= 500;
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
            Manifest = DeserializeManifest(ManifestBytes);

            // Initialize the information. The build served before this manifest replaced it is
            // remembered so the update flow can diff the two and then drop the older one.
            var previousGameBuild = GameBuild;
            InitInformations(buildInfo);
            if (!string.IsNullOrEmpty(previousGameBuild) &&
                !string.Equals(previousGameBuild, GameBuild, StringComparison.OrdinalIgnoreCase))
            {
                _previousBuildVersion = previousGameBuild;
            }

            _currentBuildVersion = buildInfo.BuildVersion;

            // Archive the manifest so this build stays readable once the next update replaces it. A
            // manifest addresses chunks on the Epic CDN, so this ~10 MB file is all that is needed —
            // no pak content is copied anywhere.
            try
            {
                _history.Archive(GameBuild, ManifestId, ManifestBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not archive the manifest for {GameBuild}: {ex.Message}");
            }

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

        /// <summary>Parses manifest bytes against this instance's chunk cache and decompressor.</summary>
        private FBuildPatchAppManifest DeserializeManifest(byte[] manifestBytes)
            => FBuildPatchAppManifest.Deserialize(manifestBytes, options =>
            {
                options.ChunkBaseUrl = ChunkBaseUrl;
                options.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
                options.DecompressorState = _zlibng;
                options.ChunkCacheDirectory = _cacheDirectory;
                options.CacheChunksAsIs = false;
            });

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
