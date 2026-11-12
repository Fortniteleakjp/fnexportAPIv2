using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Downloads the live cloudstorage config files and turns them into a lookup of the edits Fortnite
/// applies on top of the shipped assets: the row/curve changes of the <c>[AssetHotfix]</c> sections
/// and the FText overrides of the <c>[/Script/FortniteGame.FortTextHotfixConfig]</c> sections. The
/// export endpoint uses it to serve hotfixed content when <c>hotfix=true</c> is requested.
/// </summary>
public static partial class HotfixService
{
    /// <summary>The cloudstorage listing used when HOTFIX_CLOUDSTORAGE_URL is not set.</summary>
    public const string DefaultListingUrl = "https://api.fljpapi.jp/api/v2/cloudstorage";

    /// <summary>Listing endpoint; each file is fetched from {ListingUrl}/{uniqueFilename}.</summary>
    public static string ListingUrl
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HOTFIX_CLOUDSTORAGE_URL")?.Trim();
            return string.IsNullOrEmpty(configured) ? DefaultListingUrl : configured.TrimEnd('/');
        }
    }

    /// <summary>How long a built index is served before the listing is checked again (HOTFIX_CACHE_MINUTES).</summary>
    private static TimeSpan CacheDuration
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HOTFIX_CACHE_MINUTES");
            return double.TryParse(configured, out var minutes) && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.FromMinutes(10);
        }
    }

    /// <summary>Cool-down after a failed refresh so a broken endpoint is not hammered by every request.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    /// <summary>Cloudstorage rejects requests without a User-Agent, so one is always sent.</summary>
    private static readonly HttpClient Http = CreateClient();

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HotfixIndex? _cached;
    private static DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("fnexportAPI", SelfUpdateService.CurrentVersionDisplay));
        return client;
    }

    /// <summary>The index built by the last successful refresh, or null when none has succeeded yet.</summary>
    public static HotfixIndex? Cached => _cached;

    /// <summary>Drops the in-memory index so the next request rebuilds it. The disk cache is kept.</summary>
    public static void ClearCache()
    {
        _cached = null;
        _nextAttemptUtc = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Blocking wrapper around <see cref="GetIndexAsync"/> for the synchronous export action.
    /// </summary>
    public static HotfixIndex GetIndex(bool forceRefresh = false, CancellationToken cancellationToken = default)
        => GetIndexAsync(forceRefresh, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Returns the current hotfix index, refreshing it from cloudstorage when the cached copy has expired.
    /// If a refresh fails but an older index is held, that one is served rather than failing the request.
    /// </summary>
    public static async Task<HotfixIndex> GetIndexAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var snapshot = _cached;
        if (!forceRefresh && IsFresh(snapshot))
        {
            return snapshot!;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _cached;
            if (!forceRefresh && IsFresh(snapshot))
            {
                return snapshot!;
            }

            // A previous refresh failed recently: keep serving what we have instead of retrying per request.
            if (!forceRefresh && snapshot != null && DateTimeOffset.UtcNow < _nextAttemptUtc)
            {
                return snapshot;
            }

            try
            {
                var built = await BuildAsync(cancellationToken);
                _cached = built;
                _nextAttemptUtc = DateTimeOffset.MinValue;
                return built;
            }
            catch
            {
                _nextAttemptUtc = DateTimeOffset.UtcNow + FailureBackoff;
                if (snapshot != null)
                {
                    return snapshot;
                }

                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsFresh(HotfixIndex? index)
        => index != null && DateTimeOffset.UtcNow - index.FetchedAt < CacheDuration;

    /// <summary>
    /// Builds the index from the cloudstorage files, reading each one from the disk cache when it is
    /// already there and downloading only what is new.
    /// </summary>
    private static async Task<HotfixIndex> BuildAsync(CancellationToken cancellationToken)
    {
        var cacheDir = EnsureCacheDirectory();
        var listing = await LoadListingAsync(cacheDir, cancellationToken);

        // Zero-length files cannot contain a hotfix; everything else is fetched and scanned, because
        // [AssetHotfix] sections are not limited to DefaultGame.ini.
        var files = listing.Data
            .Where(file => file.Length > 0 && !string.IsNullOrEmpty(file.UniqueFilename))
            .OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contents = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var reused = 0;
        var downloaded = 0;

        using (var throttle = new SemaphoreSlim(8, 8))
        {
            await Task.WhenAll(files.Select(async file =>
            {
                var cachePath = GetCachePath(cacheDir, file);
                if (cachePath != null && TryReadCachedFile(cachePath, file, out var cached))
                {
                    contents[file.Filename] = cached;
                    Interlocked.Increment(ref reused);
                    return;
                }

                await throttle.WaitAsync(cancellationToken);
                try
                {
                    var bytes = await Http.GetByteArrayAsync($"{ListingUrl}/{file.UniqueFilename}", cancellationToken);
                    contents[file.Filename] = bytes;
                    Interlocked.Increment(ref downloaded);
                    if (cachePath != null) TryWriteCachedFile(cachePath, bytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreachable file must not void the whole set; it is simply not scanned.
                    Console.WriteLine($"  ✗ Hotfix: failed to download '{file.Filename}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        PruneCache(cacheDir, files);
        Console.WriteLine($"  Hotfix: {contents.Count}/{files.Count} cloudstorage files ({reused} from cache, {downloaded} downloaded)");

        var byAsset = new Dictionary<string, List<AssetHotfixEntry>>(StringComparer.OrdinalIgnoreCase);
        var textByNamespaceKey = new Dictionary<string, TextHotfixEntry>(StringComparer.OrdinalIgnoreCase);
        var textByKey = new Dictionary<string, TextHotfixEntry>(StringComparer.OrdinalIgnoreCase);
        var hotfixFiles = new List<string>();
        var scannedFiles = new List<string>();
        var entryCount = 0;
        var textCount = 0;

        foreach (var file in files)
        {
            if (!contents.TryGetValue(file.Filename, out var bytes)) continue;
            scannedFiles.Add(file.Filename);

            // Several files carry a UTF-8 BOM; ParseIni strips it rather than decoding it away here,
            // so the bytes on disk stay byte-identical to what cloudstorage served (and still hash).
            var parsed = ParseIni(file.Filename, Encoding.UTF8.GetString(bytes));
            if (parsed.AssetEntries.Count == 0 && parsed.TextEntries.Count == 0) continue;

            hotfixFiles.Add(file.Filename);
            entryCount += parsed.AssetEntries.Count;
            textCount += parsed.TextEntries.Count;

            foreach (var entry in parsed.AssetEntries)
            {
                if (!byAsset.TryGetValue(entry.LookupKey, out var list))
                {
                    list = [];
                    byAsset[entry.LookupKey] = list;
                }

                list.Add(entry);
            }

            foreach (var entry in parsed.TextEntries)
            {
                // Files are processed in name order, so a key published by several files resolves
                // deterministically to the last one.
                textByNamespaceKey[entry.LookupKey] = entry;
                textByKey[entry.Key] = entry;
            }
        }

        // Fingerprint of the exact content that produced this index; part of the export cache key so a
        // republished hotfix invalidates previously cached responses.
        var fingerprint = new StringBuilder();
        foreach (var file in files)
        {
            fingerprint.Append(file.Filename).Append(':').Append(file.Hash256).Append('\n');
        }

        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))[..16].ToLowerInvariant();

        return new HotfixIndex
        {
            ByAsset = byAsset.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AssetHotfixEntry>)pair.Value, StringComparer.OrdinalIgnoreCase),
            ScannedFiles = scannedFiles,
            HotfixFiles = hotfixFiles,
            EntryCount = entryCount,
            TextByNamespaceKey = textByNamespaceKey,
            TextByKey = textByKey,
            TextReplacementCount = textCount,
            Version = version,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }
}
