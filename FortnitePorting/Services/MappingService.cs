using System.Text.Json;
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
    /// Downloads or verifies the mapping file
    /// </summary>
    public static string EnsureMappingFile(string rootDir)
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
            
            var latestMapping = mappings[0];
            var localPath = Path.Combine(mappingsDir, latestMapping.fileName);
            
            Console.WriteLine($" ✓");
            Console.WriteLine($"Latest mapping: {latestMapping.fileName}");

            // Even if a local file already exists, always fetch the latest mapping file from the API
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

            // Fallback 2: look for an existing local file
            var existingFiles = Directory.GetFiles(mappingsDir, "*.usmap");
            if (existingFiles.Length > 0)
            {
                var fallbackPath = existingFiles[0];
                Console.WriteLine($"Fallback: using the existing mapping {Path.GetFileName(fallbackPath)}");
                return fallbackPath;
            }

            throw new Exception("Failed to retrieve the mapping file, and no fallback was found", ex);
        }
    }

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
