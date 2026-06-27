using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using Newtonsoft.Json.Linq;
using ZlibngDotNet;

namespace FortnitePorting.Services;

/// <summary>
/// Obtains the Fortnite AES key WITHOUT running or injecting into any process: it downloads the
/// Unreal Editor for Fortnite (appName "Fortnite_Studio") build's
/// <c>UnrealEditorFortnite-Win64-Shipping.exe</c> from the manifest and statically scans it with
/// <see cref="AesFinder"/>. UEFN reads the same encrypted Fortnite paks, so its editor binary embeds the
/// same pak AES key, and (unlike the anti-cheat-protected game client) the key can be recovered by a
/// purely static file scan.
/// </summary>
public static class UefnAesExtractor
{
    private const string ManifestsApi = "https://export-service-new.dillyapis.com/v1/manifests";
    private const string AppName = "Fortnite_Studio";
    private const string ExeName = "UnrealEditorFortnite-Win64-Shipping.exe";

    // UEFN is distributed through the same Fortnite CloudDir as the live client (same public CDN used by
    // ManifestService). Overridable in case the build is mirrored elsewhere.
    private static readonly string ChunkBaseUrl =
        Environment.GetEnvironmentVariable("UEFN_CHUNK_BASE")
        ?? "http://download.epicgames.com/Builds/Fortnite/CloudDir/";

    private static Zlibng? _zlibng;
    private static readonly object ZlibLock = new();

    public sealed class Result
    {
        public string AppName { get; set; } = UefnAesExtractor.AppName;
        public string? LabelName { get; set; }
        public string? Build { get; set; }
        public string? VersionName { get; set; }
        public string? DownloadUrl { get; set; }
        public string? ExePath { get; set; }
        public long ExeSize { get; set; }
        public string? ExeLocalPath { get; set; }
        public bool Downloaded { get; set; }
        public List<string> Keys { get; set; } = new();
        public double ScanSeconds { get; set; }
    }

    private static Zlibng GetZlibng()
    {
        if (_zlibng != null) return _zlibng;
        lock (ZlibLock)
        {
            _zlibng ??= new Zlibng(LibraryDownloader.EnsureZlibngDll());
            return _zlibng;
        }
    }

    /// <summary>Resolves the Fortnite_Studio entry and downloads + parses its build manifest.</summary>
    private static async Task<(Result Result, FBuildPatchAppManifest Manifest, string CacheDir)> LoadManifestAsync(
        string rootDir, HttpClient http, Action<string> log, CancellationToken ct)
    {
        // dillyapis manifest list -> the Fortnite_Studio (UEFN) build, preferring the Live-Windows label.
        var listJson = await http.GetStringAsync(ManifestsApi, ct);
        var arr = JArray.Parse(listJson);
        var entry = arr.OfType<JObject>()
            .Where(e => string.Equals((string?)e["appName"], AppName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => string.Equals((string?)e["labelName"], "Live-Windows", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"appName '{AppName}' not found in {ManifestsApi}");

        var downloadUrl = (string?)entry["downloadUrl"]
            ?? throw new InvalidOperationException("downloadUrl missing for Fortnite_Studio entry");

        var result = new Result
        {
            LabelName = (string?)entry["labelName"],
            Build = (string?)entry["fullBuild"],
            VersionName = (string?)entry["versionName"],
            DownloadUrl = downloadUrl
        };
        log($"{AppName} [{result.LabelName}] {result.Build} -> {downloadUrl}");

        var manifestBytes = await http.GetByteArrayAsync(downloadUrl, ct);
        log($"Manifest downloaded: {manifestBytes.Length:N0} bytes; parsing...");

        var cacheDir = Path.Combine(rootDir, "uefn_cache");
        var chunkCacheDir = Path.Combine(cacheDir, "chunks");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(chunkCacheDir);

        var manifest = FBuildPatchAppManifest.Deserialize(manifestBytes, o =>
        {
            o.ChunkBaseUrl = ChunkBaseUrl;
            o.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
            o.DecompressorState = GetZlibng();
            o.ChunkCacheDirectory = chunkCacheDir;
            o.CacheChunksAsIs = false;
        });

        return (result, manifest, cacheDir);
    }

    /// <summary>
    /// Lists the build's Win64 binaries (exe/dll) with sizes — diagnostic for locating which module
    /// actually carries the embedded AES key.
    /// </summary>
    public static async Task<object> ListBinariesAsync(string rootDir, HttpClient http, Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };
        var (result, manifest, _) = await LoadManifestAsync(rootDir, http, log, ct);

        var binaries = manifest.Files
            .Where(f =>
            {
                var n = f.FileName.Replace('\\', '/');
                return n.Contains("/Binaries/Win64/", StringComparison.OrdinalIgnoreCase)
                       && (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                           || n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(f => f.FileSize)
            .Select(f => new { file = f.FileName, sizeBytes = f.FileSize })
            .ToList();

        return new
        {
            build = result.Build,
            version = result.VersionName,
            label = result.LabelName,
            totalFiles = manifest.Files.Count,
            win64BinaryCount = binaries.Count,
            binaries
        };
    }

    public sealed class DownloadResult
    {
        public string? Build { get; set; }
        public string? VersionName { get; set; }
        public string? LabelName { get; set; }
        public string? DownloadUrl { get; set; }
        public string? FilePath { get; set; }   // path inside the manifest
        public long FileSize { get; set; }
        public string LocalPath { get; set; } = "";
        public bool Downloaded { get; set; }
    }

    /// <summary>
    /// Downloads a single binary from the Fortnite_Studio manifest to a local cache file (no scanning).
    /// By default targets <c>UnrealEditorFortnite-Win64-Shipping.exe</c>; pass <paramref name="targetFileName"/>
    /// (a file name or path suffix) to fetch a different module (e.g. the Common DLL).
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string rootDir, HttpClient http, string? targetFileName = null,
        Action<string>? log = null, bool forceDownload = false, CancellationToken ct = default)
    {
        log ??= _ => { };
        var (result, manifest, cacheDir) = await LoadManifestAsync(rootDir, http, log, ct);

        var wanted = string.IsNullOrWhiteSpace(targetFileName) ? ExeName : targetFileName!.Replace('\\', '/');
        var file = manifest.Files.FirstOrDefault(f =>
                       string.Equals(Path.GetFileName(f.FileName), wanted, StringComparison.OrdinalIgnoreCase))
                   ?? manifest.Files.FirstOrDefault(f =>
                       f.FileName.Replace('\\', '/').EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"'{wanted}' not found in manifest ({manifest.Files.Count} files).");

        var localPath = Path.Combine(cacheDir, $"{result.Build}__{Path.GetFileName(file.FileName)}");
        var dl = new DownloadResult
        {
            Build = result.Build,
            VersionName = result.VersionName,
            LabelName = result.LabelName,
            DownloadUrl = result.DownloadUrl,
            FilePath = file.FileName,
            FileSize = file.FileSize,
            LocalPath = localPath
        };
        log($"Target: {file.FileName} ({file.FileSize:N0} bytes)");

        var haveCached = File.Exists(localPath) && new FileInfo(localPath).Length == file.FileSize;
        if (haveCached && !forceDownload)
        {
            log("Using cached download.");
        }
        else
        {
            log("Downloading via manifest chunks...");
            var stream = file.GetStream();
            await stream.SaveFileAsync(localPath, maxDegreeOfParallelism: Environment.ProcessorCount, cancellationToken: ct);
            dl.Downloaded = true;
            log($"Download complete: {localPath}");
        }

        return dl;
    }

    /// <summary>
    /// Downloads a binary from the Fortnite_Studio manifest and statically scans it with the built-in
    /// (key-schedule) <see cref="AesFinder"/>. Note: for current Fortnite the MainAES key is stored as
    /// <c>mov imm32</c> instruction immediates in the Common DLL, which the external AesFinder tool detects;
    /// this built-in scan finds only key-schedule-style embeddings.
    /// </summary>
    public static async Task<Result> ExtractAsync(string rootDir, HttpClient http, Action<string>? log = null,
        string? targetFileName = null, bool forceDownload = false, CancellationToken ct = default)
    {
        log ??= _ => { };
        var dl = await DownloadAsync(rootDir, http, targetFileName, log, forceDownload, ct);

        var result = new Result
        {
            Build = dl.Build,
            VersionName = dl.VersionName,
            LabelName = dl.LabelName,
            DownloadUrl = dl.DownloadUrl,
            ExePath = dl.FilePath,
            ExeSize = dl.FileSize,
            ExeLocalPath = dl.LocalPath,
            Downloaded = dl.Downloaded
        };

        log("Scanning for AES-256 key schedules (no execution / no injection)...");
        var sw = Stopwatch.StartNew();
        result.Keys = AesFinder.FindKeysInFile(dl.LocalPath);
        sw.Stop();
        result.ScanSeconds = sw.Elapsed.TotalSeconds;
        log($"Scan complete in {result.ScanSeconds:F1}s; found {result.Keys.Count} candidate key(s).");

        return result;
    }
}
