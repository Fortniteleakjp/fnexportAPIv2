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
public static class HotfixService
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

    // ---------------------------------------------------------------- disk cache

    /// <summary>
    /// Where the downloaded cloudstorage files are kept between runs. A file's <c>uniqueFilename</c>
    /// changes whenever Epic republishes it, so the cache is content-addressed: a name that is already
    /// on disk can never be stale, and a republished hotfix simply arrives under a new name.
    /// Set HOTFIX_CACHE_DIR to move it, or HOTFIX_DISK_CACHE=false to download everything each time.
    /// </summary>
    public static string? CacheDirectory
    {
        get
        {
            var disabled = Environment.GetEnvironmentVariable("HOTFIX_DISK_CACHE");
            if (string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase) || disabled == "0")
            {
                return null;
            }

            var configured = Environment.GetEnvironmentVariable("HOTFIX_CACHE_DIR")?.Trim();
            if (!string.IsNullOrEmpty(configured)) return configured;

            var projectRoot = Environment.GetEnvironmentVariable("PROJECT_ROOT");
            var rootDir = !string.IsNullOrEmpty(projectRoot)
                ? projectRoot
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

            return Path.Combine(rootDir, "hotfix_cache");
        }
    }

    private const string ListingFileName = "listing.json";

    /// <summary>Creates the cache directory; returns null when it is disabled or cannot be used.</summary>
    private static string? EnsureCacheDirectory()
    {
        var directory = CacheDirectory;
        if (directory == null) return null;

        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex)
        {
            // An unwritable location must not break hotfixes; they are just fetched every time.
            Console.WriteLine($"  ! Hotfix: cache directory '{directory}' is unusable ({ex.Message}); caching is off.");
            return null;
        }
    }

    /// <summary>
    /// Fetches the file listing and stores it, falling back to the stored copy when cloudstorage cannot
    /// be reached — that is what lets a cold start serve hotfixes entirely from the cache.
    /// </summary>
    private static async Task<CloudStorageListing> LoadListingAsync(string? cacheDir, CancellationToken cancellationToken)
    {
        try
        {
            var json = await Http.GetStringAsync(ListingUrl, cancellationToken);
            var listing = JsonConvert.DeserializeObject<CloudStorageListing>(json)
                          ?? throw new InvalidOperationException("The cloudstorage listing returned an empty response.");

            if (cacheDir != null)
            {
                TryWriteCachedFile(Path.Combine(cacheDir, ListingFileName), Encoding.UTF8.GetBytes(json));
            }

            return listing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (cacheDir != null)
            {
                try
                {
                    var path = Path.Combine(cacheDir, ListingFileName);
                    if (File.Exists(path) &&
                        JsonConvert.DeserializeObject<CloudStorageListing>(File.ReadAllText(path)) is { Data.Count: > 0 } cached)
                    {
                        Console.WriteLine($"  ! Hotfix: cloudstorage is unreachable ({ex.Message}); using the cached listing.");
                        return cached;
                    }
                }
                catch
                {
                    // A damaged listing file is treated as no listing at all.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// The cache path for one file, or null when its name cannot be trusted as a file name. The name
    /// comes from a remote service, so only a plain identifier is accepted and it can never escape the
    /// cache directory.
    /// </summary>
    private static string? GetCachePath(string? cacheDir, CloudStorageFile file)
    {
        if (cacheDir == null) return null;

        var name = file.UniqueFilename;
        if (name.Length is 0 or > 64) return null;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return null;
        }

        return Path.Combine(cacheDir, name + ".ini");
    }

    /// <summary>
    /// Reads a cached file, rejecting it unless its size and SHA-256 still match the listing. That check
    /// makes the cache self-healing: a truncated or damaged file is simply downloaded again.
    /// </summary>
    private static bool TryReadCachedFile(string path, CloudStorageFile file, out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (!File.Exists(path)) return false;

            var data = File.ReadAllBytes(path);
            if (data.LongLength != file.Length) return false;
            if (!string.IsNullOrEmpty(file.Hash256) &&
                !Convert.ToHexString(SHA256.HashData(data)).Equals(file.Hash256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bytes = data;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes through a temporary file so an interrupted run cannot leave a half-written entry.</summary>
    private static void TryWriteCachedFile(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Nothing more to do; the entry is simply not cached.
            }
        }
    }

    /// <summary>Deletes cached files the listing no longer references, so superseded hotfixes do not pile up.</summary>
    private static void PruneCache(string? cacheDir, List<CloudStorageFile> files)
    {
        if (cacheDir == null) return;

        try
        {
            var keep = files.Select(file => file.UniqueFilename + ".ini").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(cacheDir, "*.ini"))
            {
                if (keep.Contains(Path.GetFileName(path))) continue;
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // A locked leftover is retried on the next refresh.
                }
            }
        }
        catch
        {
            // Pruning is housekeeping: never let it fail a refresh.
        }
    }

    /// <summary>
    /// Extracts the <c>+DataTable=</c> / <c>+CurveTable=</c> / <c>+CurveFloat=</c> lines of the
    /// <c>[AssetHotfix]</c> section and the <c>+TextReplacements=</c> lines of the
    /// <c>[/Script/FortniteGame.FortTextHotfixConfig]</c> section. Other sections are ignored.
    /// </summary>
    public static ParsedConfig ParseIni(string fileName, string content)
    {
        var entries = new List<AssetHotfixEntry>();
        var textEntries = new List<TextHotfixEntry>();
        var inAssetHotfix = false;
        var inTextHotfix = false;
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            // Several cloudstorage files start with a UTF-8 BOM, which would otherwise hide the first
            // section header and make its lines look like they belong to no section at all.
            var line = rawLine.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                inAssetHotfix = line.Equals("[AssetHotfix]", StringComparison.OrdinalIgnoreCase);
                inTextHotfix = line.Equals(TextHotfixSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line[0] != '+') continue;

            if (inTextHotfix && line.StartsWith("+TextReplacements=", StringComparison.OrdinalIgnoreCase))
            {
                var textEntry = ParseTextReplacement(fileName, lineNumber, line["+TextReplacements=".Length..]);
                if (textEntry != null) textEntries.Add(textEntry);
                continue;
            }

            if (!inAssetHotfix) continue;

            HotfixTarget target;
            string body;
            if (line.StartsWith("+DataTable=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.DataTable;
                body = line["+DataTable=".Length..];
            }
            else if (line.StartsWith("+CurveTable=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.CurveTable;
                body = line["+CurveTable=".Length..];
            }
            else if (line.StartsWith("+CurveFloat=", StringComparison.OrdinalIgnoreCase))
            {
                target = HotfixTarget.CurveFloat;
                body = line["+CurveFloat=".Length..];
            }
            else
            {
                continue;
            }

            // path;operation;payload — the payload itself may contain ';', so only three parts are split off.
            var parts = body.Split(';', 3);
            if (parts.Length < 3) continue;

            var assetPath = parts[0].Trim();
            var operationText = parts[1].Trim();
            var payload = parts[2];
            if (assetPath.Length == 0) continue;

            var lookupKey = NormalizeAssetPath(assetPath).ToLowerInvariant();
            string? rowName = null;
            string? field = null;
            string value;

            switch (operationText.ToLowerInvariant())
            {
                case "rowupdate":
                {
                    // DataTable: row;property;value   CurveTable: row;keyTime;value
                    var rowParts = payload.Split(';', 3);
                    if (rowParts.Length != 3) continue;
                    rowName = rowParts[0].Trim();
                    field = rowParts[1].Trim();
                    value = rowParts[2];
                    break;
                }
                case "addrow":
                case "tableupdate":
                case "curveupdate":
                    value = payload;
                    break;
                default:
                    continue;
            }

            var operation = operationText.ToLowerInvariant() switch
            {
                "rowupdate" => HotfixOperation.RowUpdate,
                "addrow" => HotfixOperation.AddRow,
                "tableupdate" => HotfixOperation.TableUpdate,
                _ => HotfixOperation.CurveUpdate
            };

            entries.Add(new AssetHotfixEntry
            {
                Target = target,
                Operation = operation,
                AssetPath = assetPath,
                LookupKey = lookupKey,
                RowName = rowName,
                Field = field,
                Value = value.Trim(),
                SourceFile = fileName,
                Line = lineNumber
            });
        }

        return new ParsedConfig(entries, textEntries);
    }

    /// <summary>The hotfix lines found in one config file.</summary>
    /// <param name="AssetEntries">DataTable / CurveTable / CurveFloat edits.</param>
    /// <param name="TextEntries">FText replacements.</param>
    public sealed record ParsedConfig(List<AssetHotfixEntry> AssetEntries, List<TextHotfixEntry> TextEntries);

    private const string TextHotfixSection = "[/Script/FortniteGame.FortTextHotfixConfig]";

    /// <summary>
    /// Parses the Unreal struct literal of a <c>+TextReplacements=</c> line, for example
    /// <c>(Category=Game, Namespace="", Key="540C…", NativeString="Gem Llama Sprite", LocalizedStrings=(("en","…"),("ja","…")))</c>.
    /// </summary>
    private static TextHotfixEntry? ParseTextReplacement(string fileName, int lineNumber, string value)
    {
        if (UnrealLiteral.Parse(value) is not Newtonsoft.Json.Linq.JObject parsed) return null;

        var key = parsed["Key"]?.ToString();
        if (string.IsNullOrEmpty(key)) return null;

        var localized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // LocalizedStrings=(("ar","…"),("en","…")) parses to an array of two-element arrays.
        if (parsed["LocalizedStrings"] is Newtonsoft.Json.Linq.JArray cultures)
        {
            foreach (var culture in cultures.OfType<Newtonsoft.Json.Linq.JArray>())
            {
                if (culture.Count < 2) continue;
                var code = culture[0].ToString();
                if (code.Length == 0) continue;
                localized[code] = culture[1].ToString();
            }
        }

        return new TextHotfixEntry
        {
            Category = parsed["Category"]?.ToString() ?? string.Empty,
            Namespace = parsed["Namespace"]?.ToString() ?? string.Empty,
            Key = key,
            NativeString = parsed["NativeString"]?.ToString() ?? string.Empty,
            LocalizedStrings = localized,
            SourceFile = fileName,
            Line = lineNumber
        };
    }

    /// <summary>
    /// Reduces a package path to the form used as the lookup key: forward slashes, no .uasset/.umap
    /// extension, no trailing <c>.ObjectName</c>, and a leading slash.
    /// Both <c>/Game/Athena/Items/Weapons/AthenaRangedWeapons</c> and
    /// <c>/AllegoryKeen/DataTables/AllegoryKeenGameData.AllegoryKeenGameData</c> normalize to their package path.
    /// </summary>
    public static string NormalizeAssetPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0) return string.Empty;

        if (normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".uasset".Length];
        }
        else if (normalized.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".umap".Length];
        }

        // Drop the object part of Package.Object (and Package.Object:SubObject).
        var lastSlash = normalized.LastIndexOf('/');
        var dot = normalized.IndexOf('.', lastSlash + 1);
        if (dot >= 0)
        {
            normalized = normalized[..dot];
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.Length > 0 && normalized[0] != '/')
        {
            normalized = '/' + normalized;
        }

        return normalized;
    }
}
