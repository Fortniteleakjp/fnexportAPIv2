using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using EpicManifestParser.ZlibngDotNetDecompressor;
using ZlibngDotNet;

namespace FortnitePorting.Services;

/// <summary>
/// Factory that initializes the CUE4Parse FileProvider
/// </summary>
public static class FileProviderFactory
{
    public sealed class InitializationResult
    {
        public required DefaultFileProvider FileProvider { get; init; }
        public required ManifestService ManifestService { get; init; }
    }

    /// <summary>
    /// Creates and initializes the FileProvider
    /// </summary>
    public static InitializationResult CreateFileProvider()
    {
        // Get the project root from an environment variable
        var projectRoot = Environment.GetEnvironmentVariable("PROJECT_ROOT");
        var rootDir = !string.IsNullOrEmpty(projectRoot)
            ? projectRoot  // In Docker environments, use the environment variable as-is
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        Console.WriteLine($"Project root: {rootDir}\n");

        // 1. Initialize the Oodle compression library
        InitializeOodle();

        // 2. Enable the AssetRipper texture decoder (Detex not required)
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        Console.WriteLine("✓ Enabled the AssetRipper texture decoder\n");

        // 3. Prepare the zlib-ng DLL
        var zlibngPath = LibraryDownloader.EnsureZlibngDll();
        var zlibng = new Zlibng(zlibngPath);

        // 4. Set up the chunk cache directory
        var chunkCacheDir = Path.Combine(rootDir, "chunk_cache");
        Directory.CreateDirectory(chunkCacheDir);

        // 5. Initialize the FileProvider
        var tempDir = Path.Combine(Path.GetTempPath(), "fortnite_manifest_dummy");
        Directory.CreateDirectory(tempDir);
        
        var provider = new DefaultFileProvider(
            tempDir, 
            SearchOption.TopDirectoryOnly, 
            versions: new VersionContainer(EGame.GAME_UE5_LATEST), 
            pathComparer: StringComparer.OrdinalIgnoreCase);

        // 6. Initialize the ManifestService
        var manifestService = new ManifestService(provider, chunkCacheDir, zlibng, rootDir);
        manifestService.InitializeAsync().GetAwaiter().GetResult();

        if (!manifestService.IsReady)
        {
            throw new Exception("Failed to initialize the manifest");
        }

        Console.WriteLine("✓ ManifestService initialization complete\n");

        // Clean up memory
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 7. Load the encryption keys
        EncryptionKeyLoader.LoadEncryptionKeysAsync(provider, rootDir).GetAwaiter().GetResult();

        // Clean up memory
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // 8. Load all VFS files
        VfsLoader.LoadAllVfsFiles(provider, manifestService.Manifest);

        // 9. Load the mapping file (optional - controlled by an environment variable)
        var skipMapping = Environment.GetEnvironmentVariable("SKIP_MAPPING")?.ToLower() == "true";
        if (!skipMapping)
        {
            LoadMappingFile(provider, rootDir);
        }
        else
        {
            Console.WriteLine("Skipped loading the mapping file (SKIP_MAPPING=true)\n");
        }

        // Record that mappings were handled for the current build (prevents a spurious reload on the
        // first poll; the poll only re-fetches when the build actually changes).
        manifestService.MarkMappingsApplied();

        // 10. Print statistics
        PrintStatistics(provider);

        // 11. Start polling only now that the provider is fully initialized — avoids any race between
        // a poll-triggered mount/mapping-swap and the remaining startup steps above.
        manifestService.StartPolling();

        return new InitializationResult
        {
            FileProvider = provider,
            ManifestService = manifestService
        };
    }

    /// <summary>
    /// Initializes the Oodle compression library
    /// </summary>
    private static void InitializeOodle()
    {
        try
        {
            var oodlePath = LibraryDownloader.EnsureOodleDll();
            Console.WriteLine($"Retrieved the Oodle library: {oodlePath}");

            OodleHelper.Initialize(oodlePath);
            Console.WriteLine("✓ Oodle initialization succeeded\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Oodle initialization error: {ex.Message}");
            throw new Exception("Failed to initialize the Oodle library", ex);
        }
    }

    /// <summary>
    /// Loads the .usmap mapping into the provider. Loading is the default; if no mapping
    /// can be resolved (e.g. USMAP_PATH points at a missing file, or no download/local file
    /// is available) the step is skipped gracefully rather than failing startup.
    /// </summary>
    private static string? LoadMappingFile(IFileProvider provider, string rootDir, bool forceDownload = true)
    {
        var usmapPath = ResolveUsmapPath(rootDir, forceDownload);
        if (usmapPath == null)
        {
            Console.WriteLine("No .usmap mapping available — skipping (some assets may not deserialize).\n");
            return null;
        }

        try
        {
            // Free memory before loading the mappings
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            Console.WriteLine($"✓ Loaded the mapping file: {usmapPath}\n");

            // Free memory after loading as well
            GC.Collect();
            return usmapPath;
        }
        catch (OutOfMemoryException)
        {
            Console.WriteLine($"✗ Failed to load the mapping file: out of memory");
            Console.WriteLine("Warning: some assets cannot be deserialized without mappings");
            Console.WriteLine("Hint: increase memory or exclude unnecessary VFS files\n");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to load the mapping file: {ex.Message}");
            Console.WriteLine("Warning: some assets cannot be deserialized without mappings\n");
            return null;
        }
    }

    /// <summary>Returned by <see cref="ReloadMappings"/> when SKIP_MAPPING is set (treated as "applied").</summary>
    public const string MappingSkipSentinel = "__skipped__";

    /// <summary>
    /// Re-resolves the .usmap (re-downloading the latest only when a newer file is available, unless
    /// USMAP_PATH is pinned to an existing file) and swaps it into the provider. Called when a new
    /// build is detected so the mapping matches the new build without a restart. Respects SKIP_MAPPING.
    /// Returns the loaded .usmap path (or <see cref="MappingSkipSentinel"/> when skipped, or null when
    /// nothing could be loaded).
    /// </summary>
    public static string? ReloadMappings(IFileProvider provider, string rootDir)
    {
        var skip = Environment.GetEnvironmentVariable("SKIP_MAPPING")?.ToLower() == "true";
        if (skip)
        {
            Console.WriteLine("Mapping reload skipped (SKIP_MAPPING=true).");
            return MappingSkipSentinel;
        }

        Console.WriteLine("Refreshing .usmap mapping for the current build...");
        return LoadMappingFile(provider, rootDir, forceDownload: false);
    }

    /// <summary>
    /// Resolves which .usmap file to load, or null only when nothing can be obtained:
    ///   1. USMAP_PATH env var — used directly when the file exists.
    ///   2. Otherwise (USMAP_PATH unset or its file missing) the latest mapping is downloaded
    ///      automatically (falling back to an existing local file). Only if no mapping can be
    ///      downloaded or found locally is null returned so loading is skipped.
    /// </summary>
    private static string? ResolveUsmapPath(string rootDir, bool forceDownload = true)
    {
        var envPath = Environment.GetEnvironmentVariable("USMAP_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            Console.WriteLine($"Using mapping from USMAP_PATH: {envPath}");
            return envPath;
        }

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            Console.WriteLine($"USMAP_PATH not found ({envPath}); downloading the latest mapping automatically.");
        }

        // No usable mapping yet -> auto-download the latest (with an existing-local-file fallback).
        try
        {
            return MappingService.EnsureMappingFile(rootDir, forceDownload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not auto-download a .usmap mapping: {ex.Message}");
            return null; // truly nothing available -> skip
        }
    }

    /// <summary>
    /// Prints the FileProvider statistics
    /// </summary>
    private static void PrintStatistics(DefaultFileProvider provider)
    {
        Console.WriteLine("=== FileProvider statistics ===");
        Console.WriteLine($"Mounted VFS: {provider.MountedVfs.Count}");
        Console.WriteLine($"Available files: {provider.Files.Count}");
        Console.WriteLine($"Encryption keys: {provider.Keys.Count}");

        var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
        Console.WriteLine($"Memory usage: {memoryMB} MB");
        Console.WriteLine("========================\n");
    }
}
