using System.Text.RegularExpressions;
using System.Reflection;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.Assets;
using EpicManifestParser.UE;

namespace FortnitePorting.Services;

/// <summary>
/// Service that handles loading of the VFS (Virtual File System)
/// </summary>
public static class VfsLoader
{
    /// <summary>
    /// Loads only the specified VFS files from the manifest (optimized for 2GB environments)
    /// </summary>
    public static void LoadAllVfsFiles(DefaultFileProvider provider, FBuildPatchAppManifest manifest)
    {
        // Extract .utoc and .pak files from the manifest
        var vfsRegex = new Regex(@"FortniteGame[/\\](Content|Plugins)[/\\].*\.(utoc|pak)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var allVfsFiles = manifest.Files
            .Where(x => vfsRegex.IsMatch(x.FileName))
            .OrderBy(x => x.FileName)
            .ToList();
        
        // Load all files
        var vfsFiles = allVfsFiles;
        Console.WriteLine($"\nLoading all {allVfsFiles.Count} VFS files");

        Console.WriteLine($"Total manifest.Files: {manifest.Files.Count}\n");
        // Load the VFS
        LoadVfsFiles(provider, manifest, vfsFiles);
        // Show final statistics
        ShowFinalStatistics(provider);
    }
    /// <summary>
    /// Loads the VFS files
    /// </summary>
    private static void LoadVfsFiles(DefaultFileProvider provider, FBuildPatchAppManifest manifest, List<FFileManifest> vfsFiles)
    {
        int successCount = 0;
        int errorCount = 0;
        int batchSize = 20; // Increased batch size to improve loading speed

        // Batch processing 2
        for (int batchStart = 0; batchStart < vfsFiles.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, vfsFiles.Count);
            var batch = vfsFiles.Skip(batchStart).Take(batchEnd - batchStart).ToList();

            Console.WriteLine($"--- Batch {batchStart / batchSize + 1} ({batchStart + 1}-{batchEnd}/{vfsFiles.Count}) ---");
            
            foreach (var fileManifest in batch)
            {
                try
                {
                    var fileName = Path.GetFileName(fileManifest.FileName);
                    var fileSize = fileManifest.FileSize / 1024.0 / 1024.0; // MB
                    Console.Write($"  [{successCount + errorCount + 1}/{vfsFiles.Count}] {fileName} ({fileSize:F1}MB)...");
                    
                    if (fileManifest.FileSize == 0)
                    {
                        Console.WriteLine(" ⚠ Skipped because it is 0 bytes");
                        continue;
                    }

                    // Get the stream
                    var vfsStream = fileManifest.GetStream();

                    // For .utoc files, verify that the .ucas file exists
                    if (fileManifest.FileName.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = fileManifest.FileName.Substring(0, fileManifest.FileName.Length - 5);
                        var ucasFileName = baseName + ".ucas";
                        var ucasFile = manifest.FindFile(ucasFileName);
                        
                        if (ucasFile == null)
                        {
                            Console.WriteLine(" ✗ No ucas");
                            errorCount++;
                            continue;
                        }
                    }

                    // Pass the stream to RegisterVfs
                    provider.RegisterVfs(fileManifest.FileName, [vfsStream],
                        new Func<string, FArchive>(it => 
                        {
                            var file = manifest.FindFile(it);
                            if (file != null)
                            {
                                return new FRandomAccessStreamArchive(it, file.GetStream(), provider.Versions);
                            }
                            throw new FileNotFoundException($"File not found: {it}");
                        }));
                    
                    successCount++;
                    Console.WriteLine(" ✓");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($" ✗ {ex.GetType().Name}: {ex.Message}");
                }
            }
            
            // Release memory after batch processing (every 2 batches)
            if ((batchStart / batchSize + 1) % 2 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var currentMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                Console.WriteLine($"  Current memory usage: {currentMemoryMB} MB");
            }
        }

        // Mount only once after all VFS have been registered
        try
        {
            // Register the .ini files before mounting
            LoadIniFiles(provider, manifest);

            // Retrieve and apply encryption keys before mounting
            EncryptionKeyLoader.LoadEncryptionKeysAsync(provider, "").GetAwaiter().GetResult();

            Console.WriteLine("\nAll VFS registration complete. Starting mount...");
            provider.Mount();
            Console.WriteLine("✓ All VFS mounting completed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Mount error: {ex.Message}");
        }

        // Release memory
        GC.Collect(2, GCCollectionMode.Aggressive, true);
        GC.WaitForPendingFinalizers();

        Console.WriteLine($"VFS load complete: {successCount} succeeded, {errorCount} errors");
    }

    /// <summary>
    /// Loads the .ini files (configuration files).
    /// First checks within the existing VFS, then loads additional files from the manifest as needed.
    /// CUE4Parse does not automatically mount .ini files into the VFS, so they are registered manually here.
    /// </summary>
    private static void LoadIniFiles(DefaultFileProvider provider, FBuildPatchAppManifest manifest)
    {
        Console.WriteLine("=== Starting to load configuration files (.ini) ===");

        var iniFiles = manifest.Files
            .Where(f => f.FileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (iniFiles.Count == 0)
        {
            Console.WriteLine("  No .ini files were found in the manifest.");
            return;
        }

        Console.WriteLine($"  Registering {iniFiles.Count} .ini files from the manifest...");

        foreach (var file in iniFiles)
        {
            try
            {
                // Read the byte array from the FFileManifestStream
                using var stream = file.GetStream();
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);
                var fileBytes = memoryStream.ToArray();

                // Create an FByteArchive and register it with RegisterVfs
                var archive = new FByteArchive(file.FileName, fileBytes, provider.Versions);
                provider.RegisterVfs(file.FileName, [archive]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to register .ini file: {file.FileName} ({ex.Message})");
            }
        }

        Console.WriteLine($"✓ Registered {iniFiles.Count} .ini files with the provider.");
    }

    /// <summary>
    /// Displays detailed information for all configuration files (for debugging)
    /// </summary>
    /// <param name="provider">File provider</param>
    /// <param name="showContent">Whether to also display the configuration content</param>
    public static void ShowAllIniFilesDetail(DefaultFileProvider provider, bool showContent = false)
    {
        var iniFiles = GetAvailableIniFiles(provider);
        
        Console.WriteLine($"\n=== All configuration file details ({iniFiles.Count} files) ===");

        if (iniFiles.Count == 0)
        {
            Console.WriteLine("No configuration files were found.");
            return;
        }

        foreach (var (path, fileName) in iniFiles)
        {
            Console.WriteLine($"\n📄 {fileName}");
            Console.WriteLine($"   Path: {path}");

            try
            {
                var config = GetIniFile(provider, fileName);

                if (config is FConfigCacheIni configCache)
                {
                    Console.WriteLine($"   Type: binary configuration cache");
                    Console.WriteLine($"   File count: {configCache.OtherFiles?.Count ?? 0}");
                    Console.WriteLine($"   Ready for use: {configCache.bIsReadyForUse}");
                    Console.WriteLine($"   Platform: {configCache.PlatformName}");

                    if (showContent && configCache.OtherFileNames?.Length > 0)
                    {
                        Console.WriteLine("   Included files:");
                        foreach (var file in configCache.OtherFileNames.Take(10))
                        {
                            Console.WriteLine($"     • {file}");
                        }
                        if (configCache.OtherFileNames.Length > 10)
                        {
                            Console.WriteLine($"     ... {configCache.OtherFileNames.Length - 10} more files");
                        }
                    }
                }
                else if (config is string textContent)
                {
                    var lines = textContent.Split('\n');
                    var sections = lines.Where(line => line.Trim().StartsWith("[") && line.Trim().EndsWith("]")).ToList();

                    Console.WriteLine($"   Type: text configuration");
                    Console.WriteLine($"   Line count: {lines.Length}");
                    Console.WriteLine($"   Section count: {sections.Count}");

                    if (showContent && sections.Count > 0)
                    {
                        Console.WriteLine("   Main sections:");
                        foreach (var section in sections.Take(5))
                        {
                            Console.WriteLine($"     {section.Trim()}");
                        }
                        if (sections.Count > 5)
                        {
                            Console.WriteLine($"     ... {sections.Count - 5} more sections");
                        }
                    }

                    if (showContent && textContent.Length < 2000) // Only display content for small files
                    {
                        Console.WriteLine("   Content:");
                        var previewLines = lines.Take(20);
                        foreach (var line in previewLines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                Console.WriteLine($"     {line.Trim()}");
                            }
                        }
                        if (lines.Length > 20)
                        {
                            Console.WriteLine($"     ... ({lines.Length - 20} more lines)");
                        }
                    }
                }
                else if (config != null)
                {
                    Console.WriteLine($"   Type: {config.GetType().Name}");
                }
                else
                {
                    Console.WriteLine("   Error: could not be loaded");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Searches for configuration files (partial match)
    /// </summary>
    /// <param name="provider">File provider</param>
    /// <param name="searchTerm">Search keyword</param>
    /// <returns>List of matching configuration files</returns>
    public static List<(string Path, string FileName, object Config)> SearchIniFiles(DefaultFileProvider provider, string searchTerm)
    {
        var iniFiles = GetAvailableIniFiles(provider);
        var results = new List<(string Path, string FileName, object Config)>();
        
        foreach (var (path, fileName) in iniFiles)
        {
            if (fileName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                var config = GetIniFile(provider, fileName);
                if (config != null)
                {
                    results.Add((path, fileName, config));
                }
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Displays the final statistics
    /// </summary>
    private static void ShowFinalStatistics(DefaultFileProvider provider)
    {
        var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
        Console.WriteLine($"\n  Final memory usage: {memoryMB} MB");

        Console.WriteLine($"Mounted VFS: {provider.MountedVfs.Count}");
        Console.WriteLine($"Available files: {provider.Files.Count}");

        var iniFileCount = provider.Files.Keys.Count(k => k.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"Configuration files (.ini): {iniFileCount}");

        Console.WriteLine();
    }

    /// <summary>
    /// Retrieves the .ini file with the specified name
    /// </summary>
    /// <param name="provider">File provider</param>
    /// <param name="iniFileName">Name of the .ini file to retrieve (e.g. "Engine.ini", "Game.ini")</param>
    /// <returns>The content of the configuration file (text or a binary configuration object), or null if not found</returns>
    public static object? GetIniFile(DefaultFileProvider provider, string iniFileName)
    {
        try
        {
            // Append .ini if the name does not already end with it
            if (!iniFileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            {
                iniFileName += ".ini";
            }

            // Search for a matching file
            var iniPath = provider.Files.Keys
                .FirstOrDefault(path => 
                    Path.GetFileName(path).Equals(iniFileName, StringComparison.OrdinalIgnoreCase) &&
                    path.Contains("Config/", StringComparison.OrdinalIgnoreCase));

            if (iniPath == null)
            {
                return null;
            }

            // Attempt to load as a binary configuration
            if (provider.TryLoadPackageObject(iniPath, out var configData))
            {
                return configData;
            }

            // Attempt to load as a text file
            if (provider.TryCreateReader(iniPath, out var stream))
            using (stream)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    /// <summary>
    /// Retrieves a list of all available .ini files
    /// </summary>
    /// <param name="provider">File provider</param>
    /// <returns>A list of path and file-name pairs for the ini files</returns>
    public static List<(string Path, string FileName)> GetAvailableIniFiles(DefaultFileProvider provider)
    {
        return provider.Files
            .Where(kvp => 
                kvp.Key.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                kvp.Key.Contains("Config/", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => (kvp.Key, Path.GetFileName(kvp.Key)))
            .OrderBy(item => item.Item2)
            .ToList();
    }
    
    /// <summary>
    /// Retrieves a specific configuration value from an .ini file (for the text format)
    /// </summary>
    /// <param name="iniContent">The content of the ini file (text)</param>
    /// <param name="section">Section name (e.g. "[/Script/Engine.Engine]")</param>
    /// <param name="key">Key name</param>
    /// <returns>The configuration value, or null if not found</returns>
    public static string? GetIniValue(string iniContent, string section, string key)
    {
        try
        {
            var lines = iniContent.Split('\n');
            bool inTargetSection = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Detect the start of a section
                if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                {
                    inTargetSection = trimmedLine.Equals(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                // Search for the key within the target section
                if (inTargetSection && trimmedLine.Contains("="))
                {
                    var parts = trimmedLine.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[1].Trim();
                    }
                }
            }
            
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}