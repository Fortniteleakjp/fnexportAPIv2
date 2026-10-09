using System.Text.Json;
using System.Text.RegularExpressions;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

/// <summary>
/// Service that downloads and manages mapping files
/// </summary>
public static class MappingService
{
    private const string PrimaryApiUrl = "https://api.fortniteapi.com/v1/mappings";
    private const string FallbackApiUrl = "https://uedb.dev/svc/api/v1/fortnite/mappings";

    /// <summary>
    /// Downloads or verifies the mapping file. When <paramref name="forceDownload"/> is false and the
    /// latest mapping is already present locally, the download is skipped (used by the per-build
    /// retry loop so it doesn't re-download the same file every poll).
    /// </summary>
    /// <param name="gameBuild">
    /// The build the mapping is for. When the API has not published that build's mapping yet, a mapping
    /// already stored for it (dumped, generated or imported) is returned instead of the API's older one.
    /// </param>
    public static string EnsureMappingFile(string rootDir, bool forceDownload = true, string? gameBuild = null)
    {
        var mappingsDir = Path.Combine(rootDir, "mappings");
        Directory.CreateDirectory(mappingsDir);

        try
        {
            Console.Write("Fetching mapping information...");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = client.GetStringAsync(PrimaryApiUrl).GetAwaiter().GetResult();
            var mappings = JsonSerializer.Deserialize<List<MappingInfo>>(response);

            if (mappings == null || mappings.Count == 0)
            {
                throw new Exception("Failed to retrieve mapping information");
            }

            var latestMapping = mappings.FirstOrDefault(m => BelongsToBuild(m.fileName, gameBuild)) ?? mappings[0];
            var localPath = Path.Combine(mappingsDir, latestMapping.fileName);

            Console.WriteLine($" ✓");
            Console.WriteLine($"Latest mapping: {latestMapping.fileName}");

            // The API still serves an older build's mapping; one made here for the current build wins.
            if (!string.IsNullOrEmpty(gameBuild) && !BelongsToBuild(latestMapping.fileName, gameBuild))
            {
                var stored = FindStoredMapping(mappingsDir, gameBuild);
                if (stored != null)
                {
                    Console.WriteLine($"Using the stored mapping for {gameBuild}: {Path.GetFileName(stored)}");
                    return stored;
                }
            }

            // On a retry poll, if we already have the latest mapping by name, don't re-download it.
            if (!forceDownload && File.Exists(localPath))
            {
                Console.WriteLine($"Latest mapping already present (skipping download): {localPath}");
                return localPath;
            }

            Console.Write($"Downloading mapping file ({latestMapping.size / 1024 / 1024:F1} MB)...");
            var mappingData = client.GetByteArrayAsync(latestMapping.url).GetAwaiter().GetResult();
            File.WriteAllBytes(localPath, mappingData);
            Console.WriteLine($" ✓");
            Console.WriteLine($"Saved the latest mapping file: {localPath}");
            return localPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($" ✗");
            Console.WriteLine($"Mapping retrieval error: {ex.Message}");

            // Fallback 1: use the ZStandard mapping from the UEdb API
            try
            {
                Console.Write("Falling back to the UEdb (ZStandard) mapping...");
                var fallbackPath = DownloadUedbZstandardMapping(mappingsDir);
                Console.WriteLine(" ✓");
                Console.WriteLine($"Saved the UEdb mapping file: {fallbackPath}");
                return fallbackPath;
            }
            catch (Exception fallbackEx)
            {
                Console.WriteLine(" ✗");
                Console.WriteLine($"UEdb fallback failed: {fallbackEx.Message}");
            }

            // Fallback 2: look for an existing local file, the current build's first, else the newest
            var existingPath = FindStoredMapping(mappingsDir, gameBuild)
                               ?? new DirectoryInfo(mappingsDir).GetFiles("*.usmap")
                                   .OrderByDescending(f => f.LastWriteTimeUtc)
                                   .FirstOrDefault()?.FullName;
            if (existingPath != null)
            {
                Console.WriteLine($"Fallback: using the existing mapping {Path.GetFileName(existingPath)}");
                return existingPath;
            }

            throw new Exception("Failed to retrieve the mapping file, and no fallback was found", ex);
        }
    }

    /// <summary>
    /// Reduces "++Fortnite+Release-42.00-CL-56878558-Windows" to "FortniteGame_42_00", the name this API
    /// gives the mappings it dumps for a build.
    /// </summary>
    public static string? ShortBuildName(string? build)
    {
        if (string.IsNullOrWhiteSpace(build)) return null;

        var parts = build.Split('-');
        var version = (parts.Length > 2 ? parts[1] : build).Trim().Replace('.', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            version = version.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(version) ? null : $"FortniteGame_{version}";
    }

    /// <summary>
    /// True when a mapping file is named after <paramref name="build"/>: the API's own naming
    /// ("++Fortnite+Release-42.30-CL-58557680[-Windows]_zs.usmap", matched on the changelist so the
    /// platform suffix may be missing), or the one this API uses for dumps ("FortniteGame_42_30_*.usmap").
    /// Dump names only carry the version, so a hotfix build with a new changelist accepts them too.
    /// </summary>
    public static bool BelongsToBuild(string? fileName, string? build)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(build)) return false;

        var clEnd = Regex.Match(build, @"-CL-\d+");
        var core = clEnd.Success ? build[..(clEnd.Index + clEnd.Length)] : build;
        if (StartsWithToken(fileName, core)) return true;

        var shortName = ShortBuildName(build);
        return shortName != null && StartsWithToken(fileName, shortName);
    }

    /// <summary>The newest mapping in <paramref name="mappingsDir"/> named after <paramref name="build"/>, or null.</summary>
    public static string? FindStoredMapping(string mappingsDir, string? build)
    {
        if (string.IsNullOrEmpty(build) || !Directory.Exists(mappingsDir)) return null;

        return new DirectoryInfo(mappingsDir).GetFiles("*.usmap")
            .Where(f => BelongsToBuild(f.Name, build))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>Prefix match that refuses to stop inside a number ("42_3" must not match "42_30").</summary>
    private static bool StartsWithToken(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
           && (value.Length == prefix.Length || !char.IsDigit(value[prefix.Length]));

    private static string DownloadUedbZstandardMapping(string mappingsDir)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var response = client.GetStringAsync(FallbackApiUrl).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(response);

        if (!doc.RootElement.TryGetProperty("mappings", out var mappingsElement) ||
            !mappingsElement.TryGetProperty("ZStandard", out var zstdElement))
        {
            throw new Exception("The UEdb response does not contain a ZStandard mapping");
        }

        var zstdUrl = zstdElement.GetString();
        if (string.IsNullOrWhiteSpace(zstdUrl))
        {
            throw new Exception("The UEdb ZStandard URL is empty");
        }

        var uri = new Uri(zstdUrl);
        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "fallback_zstandard.usmap";
        }

        var localPath = Path.Combine(mappingsDir, fileName);
        var mappingData = client.GetByteArrayAsync(zstdUrl).GetAwaiter().GetResult();
        File.WriteAllBytes(localPath, mappingData);
        return localPath;
    }
}
