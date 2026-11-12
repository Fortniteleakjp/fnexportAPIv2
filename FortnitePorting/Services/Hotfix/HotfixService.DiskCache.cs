using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

public static partial class HotfixService
{
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

            return Path.Combine(AppPaths.ProjectRoot, "hotfix_cache");
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
}
