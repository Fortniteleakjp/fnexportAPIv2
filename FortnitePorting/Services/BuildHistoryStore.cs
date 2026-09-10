using System.Text.RegularExpressions;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// On-disk archive of the builds this instance has seen, plus the changelists recorded between them.
/// <para>
/// Layout under <c>build_history/</c>:
/// <list type="bullet">
///   <item><c>index.json</c> — the known builds, newest first.</item>
///   <item><c>manifests/&lt;build&gt;.manifest</c> — the archived manifest bytes for one build.</item>
///   <item><c>diffs/&lt;from&gt;__&lt;to&gt;.json</c> — a recorded changelist.</item>
/// </list>
/// </para>
/// <para>
/// Only manifests are archived, never pak content: a manifest addresses chunks on the Epic CDN, so a
/// ~10 MB file is all it takes to read an old build again. "Deleting a build's data" therefore means
/// dropping its archived manifest and its private chunk cache — the recorded changelist survives.
/// </para>
/// </summary>
public sealed partial class BuildHistoryStore
{
    private readonly string _rootDir;
    private readonly object _sync = new();

    /// <summary>Number of builds whose manifest is kept (current + previous by default).</summary>
    public int RetainedBuilds { get; }

    public BuildHistoryStore(string rootDir)
    {
        _rootDir = Path.Combine(rootDir, "build_history");
        Directory.CreateDirectory(ManifestDirectory);
        Directory.CreateDirectory(DiffDirectory);

        RetainedBuilds = int.TryParse(Environment.GetEnvironmentVariable("BUILD_HISTORY_KEEP"), out var keep) && keep >= 1
            ? keep
            : 2;
    }

    /// <summary>Root of the history directory (<c>&lt;project&gt;/build_history</c>).</summary>
    public string Directory_ => _rootDir;

    private string ManifestDirectory => Path.Combine(_rootDir, "manifests");
    private string DiffDirectory => Path.Combine(_rootDir, "diffs");
    private string KeyDirectory => Path.Combine(_rootDir, "keys");
    private string IndexPath => Path.Combine(_rootDir, "index.json");

    /// <summary>Private chunk cache for a historical build, so purging it cannot affect the live build.</summary>
    public string ChunkCacheDirectoryFor(string buildVersion)
        => Path.Combine(_rootDir, "chunks", SafeName(buildVersion));

    // ---------------------------------------------------------------- index

    /// <summary>Reads the build index (newest first). Never throws: a corrupt index is treated as empty.</summary>
    public List<ArchivedBuild> GetBuilds()
    {
        lock (_sync)
        {
            return ReadIndexLocked().Builds;
        }
    }

    private BuildHistoryIndex ReadIndexLocked()
    {
        if (!File.Exists(IndexPath))
        {
            return new BuildHistoryIndex();
        }

        try
        {
            var index = JsonConvert.DeserializeObject<BuildHistoryIndex>(File.ReadAllText(IndexPath));
            return index ?? new BuildHistoryIndex();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Build history index unreadable ({ex.Message}); starting a new one.");
            return new BuildHistoryIndex();
        }
    }

    private void WriteIndexLocked(BuildHistoryIndex index)
    {
        index.Builds = index.Builds.OrderByDescending(b => b.ArchivedUtc).ToList();
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(index, Formatting.Indented));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    /// <summary>
    /// Stores the manifest bytes of a build and records it in the index. Re-archiving a build that is
    /// already stored only refreshes its manifest id, so a restart does not duplicate entries.
    /// </summary>
    public ArchivedBuild Archive(string buildVersion, string manifestId, byte[] manifestBytes)
    {
        lock (_sync)
        {
            var index = ReadIndexLocked();
            var fileName = SafeName(buildVersion) + ".manifest";
            var fullPath = Path.Combine(ManifestDirectory, fileName);

            System.IO.Directory.CreateDirectory(ManifestDirectory);
            File.WriteAllBytes(fullPath, manifestBytes);

            var existing = index.Builds.FirstOrDefault(
                b => string.Equals(b.BuildVersion, buildVersion, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new ArchivedBuild
                {
                    BuildVersion = buildVersion,
                    ArchivedUtc = DateTime.UtcNow
                };
                index.Builds.Add(existing);
            }

            existing.ManifestId = manifestId;
            existing.ManifestFile = fileName;
            existing.ManifestBytes = manifestBytes.LongLength;
            existing.PrunedUtc = null;
            (existing.Version, existing.Changelist) = SplitBuildVersion(buildVersion);

            WriteIndexLocked(index);
            Console.WriteLine($"✓ Archived the manifest for {buildVersion} ({manifestBytes.LongLength / 1024 / 1024} MB)");
            return existing;
        }
    }

    /// <summary>Path of an archived manifest, or null when this build was never archived or was pruned.</summary>
    public string? GetManifestPath(string buildVersion)
    {
        lock (_sync)
        {
            var build = ReadIndexLocked().Builds.FirstOrDefault(
                b => string.Equals(b.BuildVersion, buildVersion, StringComparison.OrdinalIgnoreCase));

            if (build is not { HasManifest: true })
            {
                return null;
            }

            var path = Path.Combine(ManifestDirectory, build.ManifestFile!);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Deletes a build's archived manifest and its private chunk cache — the "delete the old version's
    /// data" step of an update. The build stays in the index (marked pruned) and every recorded
    /// changelist that mentions it is kept, so the update history is not lost.
    /// </summary>
    public bool PruneBuildData(string buildVersion)
    {
        lock (_sync)
        {
            var index = ReadIndexLocked();
            var build = index.Builds.FirstOrDefault(
                b => string.Equals(b.BuildVersion, buildVersion, StringComparison.OrdinalIgnoreCase));

            if (build == null || !build.HasManifest)
            {
                return false;
            }

            var path = Path.Combine(ManifestDirectory, build.ManifestFile!);
            TryDeleteFile(path);
            TryDeleteFile(KeyPath(buildVersion));
            TryDeleteDirectory(ChunkCacheDirectoryFor(buildVersion));

            build.PrunedUtc = DateTime.UtcNow;
            build.ManifestBytes = 0;
            WriteIndexLocked(index);

            Console.WriteLine($"✓ Deleted the archived data for {buildVersion} (its changelists were kept)");
            return true;
        }
    }

    /// <summary>
    /// Applies the retention policy: every build except the newest <see cref="RetainedBuilds"/> loses
    /// its archived data. Returns the builds that were pruned.
    /// </summary>
    public List<string> ApplyRetention()
    {
        var stale = GetBuilds()
            .Where(b => b.HasManifest)
            .Skip(RetainedBuilds)
            .Select(b => b.BuildVersion)
            .ToList();

        return stale.Where(PruneBuildData).ToList();
    }

    // ---------------------------------------------------------------- AES keys

    private string KeyPath(string buildVersion) => Path.Combine(KeyDirectory, SafeName(buildVersion) + ".json");

    /// <summary>
    /// Archives the AES keys a build was mounted with, as GUID (32 hex digits) → key. Fortnite rotates
    /// its main key every build and the live key APIs only serve the current one, so without this an
    /// archived manifest could be parsed but none of its paks could be decrypted.
    /// </summary>
    public void SaveKeys(string buildVersion, IReadOnlyDictionary<string, string> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var path = KeyPath(buildVersion);
            var json = JsonConvert.SerializeObject(keys, Formatting.Indented);

            // This runs on every poll; skip the write when nothing was added since the last one.
            if (File.Exists(path) && File.ReadAllText(path) == json)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(KeyDirectory);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Returns the archived AES keys for a build, or an empty dictionary when none were kept.</summary>
    public Dictionary<string, string> LoadKeys(string buildVersion)
    {
        var path = KeyPath(buildVersion);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Archived AES keys for {buildVersion} unreadable: {ex.Message}");
            return [];
        }
    }

    // ---------------------------------------------------------------- diffs

    private string DiffPath(string fromBuild, string toBuild)
        => Path.Combine(DiffDirectory, $"{SafeName(fromBuild)}__{SafeName(toBuild)}.json");

    /// <summary>True when a changelist between these two builds is already recorded.</summary>
    public bool HasDiff(string fromBuild, string toBuild) => File.Exists(DiffPath(fromBuild, toBuild));

    public void SaveDiff(BuildDiff diff)
    {
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(DiffDirectory);
            var path = DiffPath(diff.FromBuild, diff.ToBuild);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(diff, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public BuildDiff? LoadDiff(string fromBuild, string toBuild)
    {
        var path = DiffPath(fromBuild, toBuild);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<BuildDiff>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Recorded changelist {fromBuild} -> {toBuild} unreadable: {ex.Message}");
            return null;
        }
    }

    public bool DeleteDiff(string fromBuild, string toBuild) => TryDeleteFile(DiffPath(fromBuild, toBuild));

    /// <summary>Lists every recorded changelist as a (from, to) pair, with its file size.</summary>
    public List<(string From, string To, long Bytes, DateTime WrittenUtc)> ListDiffs()
    {
        if (!System.IO.Directory.Exists(DiffDirectory))
        {
            return [];
        }

        var result = new List<(string, string, long, DateTime)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(DiffDirectory, "*.json"))
        {
            // Only the header is needed, but these files stay small enough that reading them is cheaper
            // than maintaining a second index that could drift out of sync with the directory.
            var diff = TryReadDiffHeader(file);
            if (diff != null)
            {
                var info = new FileInfo(file);
                result.Add((diff.FromBuild, diff.ToBuild, info.Length, info.LastWriteTimeUtc));
            }
        }

        return result.OrderByDescending(x => x.Item4).ToList();
    }

    private static BuildDiff? TryReadDiffHeader(string path)
    {
        try
        {
            using var reader = new JsonTextReader(new StreamReader(path));
            var header = new BuildDiff();
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var name = (string)reader.Value!;
                if (string.Equals(name, "Entries", StringComparison.OrdinalIgnoreCase))
                {
                    break; // everything before the (potentially huge) entry array is what we need
                }

                if (string.Equals(name, "FromBuild", StringComparison.OrdinalIgnoreCase))
                {
                    header.FromBuild = reader.ReadAsString() ?? string.Empty;
                }
                else if (string.Equals(name, "ToBuild", StringComparison.OrdinalIgnoreCase))
                {
                    header.ToBuild = reader.ReadAsString() ?? string.Empty;
                }
            }

            return string.IsNullOrEmpty(header.FromBuild) || string.IsNullOrEmpty(header.ToBuild) ? null : header;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Splits <c>++Fortnite+Release-42.10-CL-57566230-Windows</c> into <c>42.10</c> and <c>57566230</c>.</summary>
    public static (string Version, string Changelist) SplitBuildVersion(string buildVersion)
    {
        var match = BuildVersionRegex().Match(buildVersion ?? string.Empty);
        return match.Success
            ? (match.Groups["ver"].Value, match.Groups["cl"].Value)
            : (string.Empty, string.Empty);
    }

    /// <summary>Turns a build version into a file-system-safe name.</summary>
    private static string SafeName(string buildVersion)
    {
        var safe = buildVersion;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }

        // '+' is legal on both platforms but awkward in URLs and shell paths; the build string starts
        // with two of them, so normalising it keeps the archive directory readable.
        return safe.Replace('+', '_');
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not delete {path}: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"-(?<ver>\d+\.\d+)-CL-(?<cl>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BuildVersionRegex();
}
