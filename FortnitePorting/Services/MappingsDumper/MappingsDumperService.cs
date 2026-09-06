using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Runs a UnrealMappingsDumper-style mapping dump end to end: collect the reflected types of the
/// mounted build, optionally merge an existing mapping under them, serialize the result to a
/// <c>.usmap</c>, store it in the <c>mappings/</c> directory and hand it back for serving.
/// </summary>
public sealed class MappingsDumperService
{
    private readonly IFileProvider _provider;

    public MappingsDumperService(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>The directory generated and downloaded mappings live in.</summary>
    public static string MappingsDirectory
    {
        get
        {
            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
            var dir = Path.Combine(rootDir, "mappings");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public sealed class DumpRequest
    {
        /// <summary>Restrict the scan to packages whose path contains this fragment.</summary>
        public string? PathFilter;

        /// <summary>Maximum packages to open; 0 scans the whole build (slow).</summary>
        public int MaxPackages = 5000;

        public TimeSpan Timeout = TimeSpan.FromMinutes(2);

        /// <summary>Merge a base mapping so native /Script types are kept alongside the dumped ones.</summary>
        public bool Merge = true;

        /// <summary>Base mapping file name (inside mappings/) or absolute path. Defaults to the newest one.</summary>
        public string? BaseMapping;

        public EUsmapVersion Version = EUsmapVersion.Latest;
        public EUsmapCompressionMethod Compression = EUsmapCompressionMethod.None;

        /// <summary>Output file name; defaults to <c>{build}_dumped.usmap</c>.</summary>
        public string? FileName;

        /// <summary>Build name used for the default output file name.</summary>
        public string? Build;

        /// <summary>Parse the written file back and report what it contains.</summary>
        public bool Verify = true;
    }

    public sealed class DumpResult
    {
        public string FileName = string.Empty;
        public string FilePath = string.Empty;
        public byte[] Usmap = [];
        public PakReflectionCollector.Stats Collector = new();
        public UsmapSerializer.Result Serializer = new();
        public string? BaseMapping;
        public int MergedStructs;
        public int MergedEnums;
        public int? VerifiedStructs;
        public int? VerifiedEnums;
        public string? VerifyError;
    }

    public DumpResult Dump(DumpRequest request, CancellationToken cancellationToken = default)
    {
        var collector = new PakReflectionCollector(_provider);
        var stats = new PakReflectionCollector.Stats();
        var snapshot = collector.Collect(new PakReflectionCollector.Options
        {
            PathFilter = request.PathFilter,
            MaxPackages = request.MaxPackages,
            Timeout = request.Timeout
        }, stats, cancellationToken);

        var result = new DumpResult { Collector = stats };

        if (request.Merge)
        {
            var basePath = ResolveBaseMapping(request.BaseMapping);
            if (basePath != null)
            {
                // Dumped types win: the paks describe this exact build, the base mapping may not.
                var (structs, enums) = snapshot.MergeMissingFrom(UsmapSnapshotReader.ReadFile(basePath));
                result.BaseMapping = basePath;
                result.MergedStructs = structs;
                result.MergedEnums = enums;
            }
        }

        result.Serializer = UsmapSerializer.Serialize(snapshot, new UsmapSerializer.Options
        {
            Version = request.Version,
            Compression = request.Compression
        });
        result.Usmap = result.Serializer.Usmap;

        result.FileName = ResolveFileName(request);
        result.FilePath = Path.Combine(MappingsDirectory, result.FileName);
        File.WriteAllBytes(result.FilePath, result.Usmap);

        if (request.Verify)
        {
            try
            {
                var mappings = new UsmapParser(result.Usmap, result.FileName).Mappings;
                result.VerifiedStructs = mappings?.Types.Count ?? 0;
                result.VerifiedEnums = mappings?.Enums.Count ?? 0;
            }
            catch (Exception ex)
            {
                result.VerifyError = ex.Message;
            }
        }

        return result;
    }

    /// <summary>Lists the stored mapping files, newest first.</summary>
    public static IReadOnlyList<FileInfo> ListMappings()
        => new DirectoryInfo(MappingsDirectory)
            .GetFiles("*.usmap")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

    /// <summary>
    /// Resolves a stored mapping by file name, refusing anything that escapes the mappings directory.
    /// </summary>
    public static FileInfo? ResolveStoredMapping(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) return null;

        var path = Path.Combine(MappingsDirectory, fileName);
        var info = new FileInfo(path);
        return info.Exists ? info : null;
    }

    private static string? ResolveBaseMapping(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (File.Exists(requested)) return requested;
            var stored = ResolveStoredMapping(requested);
            if (stored != null) return stored.FullName;
            throw new FileNotFoundException($"Base mapping '{requested}' was not found.");
        }

        // The .usmap currently pinned by USMAP_PATH is the one the provider is running with.
        var pinned = Environment.GetEnvironmentVariable("USMAP_PATH");
        if (!string.IsNullOrWhiteSpace(pinned) && File.Exists(pinned)) return pinned;

        return ListMappings().FirstOrDefault()?.FullName;
    }

    private static string ResolveFileName(DumpRequest request)
    {
        var name = request.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var build = string.IsNullOrWhiteSpace(request.Build) ? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") : request.Build;
            name = $"{build}_dumped";
        }

        name = Path.GetFileName(name.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (string.IsNullOrWhiteSpace(name)) name = "dumped";
        return name.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? name : name + ".usmap";
    }
}
