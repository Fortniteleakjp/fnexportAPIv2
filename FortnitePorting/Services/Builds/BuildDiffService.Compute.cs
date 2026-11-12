using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

public sealed partial class BuildDiffService
{
    // ------------------------------------------------------------ changelist computation

    /// <summary>Progress of a running changelist computation.</summary>
    public sealed class DiffProgress
    {
        public int Compared { get; set; }
        public int Total { get; set; }
        public int Hashed { get; set; }
        public string Phase { get; set; } = "starting";
    }

    /// <summary>
    /// Compares two mounted builds file by file.
    /// <para>
    /// <c>quick</c> mode uses the virtual path, the file size and the containing archive, all of which
    /// come out of the mounted index without touching the CDN. It finds every added and removed file
    /// exactly, and every modified file whose size changed; files that kept their exact size are
    /// counted as unverified rather than claimed unchanged.
    /// </para>
    /// <para>
    /// <c>full</c> mode additionally reads and hashes both copies of every same-size file, which is
    /// exact but streams the content of each candidate from the Epic CDN. Use
    /// <paramref name="pathFilter"/> and <paramref name="maxHashFiles"/> to keep it bounded.
    /// </para>
    /// </summary>
    public BuildDiff Compute(IFileProvider from, IFileProvider to, string fromBuild, string toBuild,
        string mode = "quick", string? pathFilter = null, int maxEntries = 200000, int maxHashFiles = 5000,
        DiffProgress? progress = null, IReadOnlySet<string>? excludeArchives = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var full = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);

        var fromAll = Snapshot(from, pathFilter);
        var toAll = Snapshot(to, pathFilter);

        // Files living in a container only one build could open are dropped from both sides, so they
        // are absent from the changelist rather than misreported as added or removed.
        var fromFiles = Exclude(fromAll, excludeArchives);
        var toFiles = Exclude(toAll, excludeArchives);

        var diff = new BuildDiff
        {
            FromBuild = fromBuild,
            ToBuild = toBuild,
            Mode = full ? "full" : "quick",
            PathFilter = pathFilter,
            ComputedUtc = DateTime.UtcNow,
            TotalFilesFrom = fromFiles.Count,
            TotalFilesTo = toFiles.Count,
            UnmountedVfsFrom = Inspect(from).UnmountedVfs,
            UnmountedVfsTo = Inspect(to).UnmountedVfs,
            ExcludedArchives = excludeArchives?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            ExcludedFilesFrom = fromAll.Count - fromFiles.Count,
            ExcludedFilesTo = toAll.Count - toFiles.Count
        };

        var entries = new List<BuildChangeEntry>();
        var sameSize = new List<string>();

        if (progress != null)
        {
            progress.Phase = "comparing";
            progress.Total = fromFiles.Count + toFiles.Count;
        }

        foreach (var (path, to_) in toFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!fromFiles.TryGetValue(path, out var from_))
            {
                entries.Add(new BuildChangeEntry
                {
                    Path = path,
                    Kind = BuildChangeKind.Added,
                    NewSize = to_.Size,
                    NewArchive = to_.Archive
                });
                diff.AddedCount++;
            }
            else if (from_.Size != to_.Size)
            {
                entries.Add(new BuildChangeEntry
                {
                    Path = path,
                    Kind = BuildChangeKind.Modified,
                    OldSize = from_.Size,
                    NewSize = to_.Size,
                    OldArchive = from_.Archive,
                    NewArchive = to_.Archive
                });
                diff.ModifiedCount++;
            }
            else
            {
                sameSize.Add(path);
            }

            if (progress != null)
            {
                progress.Compared++;
            }
        }

        foreach (var (path, from_) in fromFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!toFiles.ContainsKey(path))
            {
                entries.Add(new BuildChangeEntry
                {
                    Path = path,
                    Kind = BuildChangeKind.Removed,
                    OldSize = from_.Size,
                    OldArchive = from_.Archive
                });
                diff.RemovedCount++;
            }

            if (progress != null)
            {
                progress.Compared++;
            }
        }

        if (full && sameSize.Count > 0)
        {
            if (progress != null)
            {
                progress.Phase = "hashing";
                progress.Total = Math.Min(sameSize.Count, maxHashFiles);
            }

            var candidates = sameSize.Take(maxHashFiles).ToList();
            diff.Truncated |= candidates.Count < sameSize.Count;
            diff.UnverifiedCount = sameSize.Count - candidates.Count;

            var changed = new ConcurrentBag<BuildChangeEntry>();
            var unreadable = 0;

            // Content streams from the CDN, so a handful of readers keeps the pipe busy without
            // hammering it; the chunk cache absorbs the repeats.
            Parallel.ForEach(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
                path =>
                {
                    var oldHash = TryHash(from, path);
                    var newHash = TryHash(to, path);

                    if (oldHash == null || newHash == null)
                    {
                        Interlocked.Increment(ref unreadable);
                    }
                    else if (!string.Equals(oldHash, newHash, StringComparison.Ordinal))
                    {
                        changed.Add(new BuildChangeEntry
                        {
                            Path = path,
                            Kind = BuildChangeKind.Modified,
                            OldSize = fromFiles[path].Size,
                            NewSize = toFiles[path].Size,
                            OldArchive = fromFiles[path].Archive,
                            NewArchive = toFiles[path].Archive,
                            OldHash = oldHash,
                            NewHash = newHash
                        });
                    }

                    if (progress != null)
                    {
                        lock (progress)
                        {
                            progress.Hashed++;
                        }
                    }
                });

            entries.AddRange(changed);
            diff.ModifiedCount += changed.Count;
            diff.UnreadableCount = unreadable;
        }
        else
        {
            diff.UnverifiedCount = sameSize.Count;
        }

        if (entries.Count > maxEntries)
        {
            diff.Truncated = true;
            entries = entries.Take(maxEntries).ToList();
        }

        diff.Entries = entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        diff.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

        if (progress != null)
        {
            progress.Phase = "done";
        }

        return diff;
    }

    private readonly record struct FileSnapshot(long Size, string? Archive);

    private static Dictionary<string, FileSnapshot> Snapshot(IFileProvider provider, string? pathFilter)
    {
        var result = new Dictionary<string, FileSnapshot>(provider.Files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in provider.Files)
        {
            if (pathFilter != null && !path.StartsWith(pathFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[path] = new FileSnapshot(file.Size, (file as VfsEntry)?.Vfs.Name);
        }

        return result;
    }

    /// <summary>Drops the entries whose containing archive is excluded.</summary>
    private static Dictionary<string, FileSnapshot> Exclude(Dictionary<string, FileSnapshot> files,
        IReadOnlySet<string>? excludeArchives)
    {
        if (excludeArchives == null || excludeArchives.Count == 0)
        {
            return files;
        }

        var kept = new Dictionary<string, FileSnapshot>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, snapshot) in files)
        {
            if (snapshot.Archive == null || !excludeArchives.Contains(snapshot.Archive))
            {
                kept[path] = snapshot;
            }
        }

        return kept;
    }

    private static string? TryHash(IFileProvider provider, string path)
    {
        try
        {
            return provider.TrySaveAsset(path, out var bytes) && bytes != null
                ? Convert.ToHexString(SHA256.HashData(bytes))
                : null;
        }
        catch
        {
            return null;
        }
    }
}
