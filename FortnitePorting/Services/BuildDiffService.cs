using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

/// <summary>
/// Computes, records and composes the changelists between Fortnite builds, and produces the
/// line-level diff of a single file between two of them.
/// </summary>
public sealed class BuildDiffService
{
    private readonly BuildHistoryStore _store;
    private readonly HistoricalBuildService _historical;
    private readonly IFileProvider _liveProvider;
    private readonly ManifestService _manifestService;

    public BuildDiffService(BuildHistoryStore store, HistoricalBuildService historical,
        IFileProvider liveProvider, ManifestService manifestService)
    {
        _store = store;
        _historical = historical;
        _liveProvider = liveProvider;
        _manifestService = manifestService;
    }

    // ------------------------------------------------------------ build resolution

    /// <summary>
    /// Resolves a build parameter to a full build version. Accepts the full string
    /// (<c>++Fortnite+Release-42.10-CL-57566230-Windows</c>), the version alone (<c>42.10</c>), a
    /// changelist (<c>57566230</c>), or <c>latest</c>/<c>current</c> and <c>previous</c>.
    /// Returns null when nothing matches.
    /// </summary>
    public string? ResolveBuildVersion(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var query = input.Trim();

        if (query.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            query.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            return _manifestService.GameBuild;
        }

        var known = KnownBuildVersions();

        if (query.Equals("previous", StringComparison.OrdinalIgnoreCase) ||
            query.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrEmpty(_manifestService.PreviousBuildVersion)
                ? _manifestService.PreviousBuildVersion
                : known.FirstOrDefault(b => !string.Equals(b, _manifestService.GameBuild, StringComparison.OrdinalIgnoreCase));
        }

        var exact = known.FirstOrDefault(b => string.Equals(b, query, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        // "42.10", "42.10-CL-57566230" or a bare changelist.
        var matches = known
            .Where(b => b.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        BuildHistoryStore.SplitBuildVersion(b).Version == query ||
                        BuildHistoryStore.SplitBuildVersion(b).Changelist == query)
            .ToList();

        // Ambiguity is resolved towards the newest match, which is what "42.10" means in practice
        // when the same version shipped under more than one changelist.
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Every build this instance knows about, newest first, live build included.</summary>
    public List<string> KnownBuildVersions()
    {
        var builds = new List<string>();
        if (!string.IsNullOrEmpty(_manifestService.GameBuild))
        {
            builds.Add(_manifestService.GameBuild);
        }

        foreach (var archived in _store.GetBuilds())
        {
            if (!builds.Contains(archived.BuildVersion, StringComparer.OrdinalIgnoreCase))
            {
                builds.Add(archived.BuildVersion);
            }
        }

        return builds;
    }

    /// <summary>True when this build version is the one the live provider serves.</summary>
    public bool IsLive(string buildVersion)
        => string.Equals(buildVersion, _manifestService.GameBuild, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Borrows the provider that serves a build, without mounting anything. Historical builds resolve
    /// only while they are already loaded — this is the "return that build's content if that build is
    /// loaded" rule the read endpoints follow. The lease must be disposed once the caller is done.
    /// </summary>
    public bool TryLease(string buildVersion, out BuildLease lease)
    {
        if (IsLive(buildVersion))
        {
            lease = new BuildLease(buildVersion, _liveProvider, null);
            return true;
        }

        return _historical.TryLease(buildVersion, out lease);
    }

    /// <summary>Unmounts a build, unless it is the live one (which is never unmounted here).</summary>
    public bool UnloadIfHistorical(string buildVersion) => !IsLive(buildVersion) && _historical.Unload(buildVersion);

    /// <summary>Same as <see cref="TryLease"/> but mounts the archived build when it is not loaded.</summary>
    public async Task<BuildLease> LeaseAsync(string buildVersion, CancellationToken cancellationToken = default)
    {
        return IsLive(buildVersion)
            ? new BuildLease(buildVersion, _liveProvider, null)
            : await _historical.LeaseAsync(buildVersion, cancellationToken);
    }

    /// <summary>
    /// Reports how much of a build actually mounted. A build whose containers stayed locked exposes
    /// only a fraction of its files, and comparing that against a fully mounted build produces a
    /// changelist that looks like "the whole game was rewritten" — so this is checked before, not after.
    /// </summary>
    public static MountHealth Inspect(IFileProvider provider)
    {
        if (provider is not CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider vfs)
        {
            return new MountHealth(provider.Files.Count, 0, 0, 0);
        }

        return new MountHealth(provider.Files.Count, vfs.MountedVfs.Count, vfs.UnloadedVfs.Count, vfs.RequiredKeys.Count);
    }

    /// <summary>Names of the containers that mounted, and of those that did not.</summary>
    private static (HashSet<string> Mounted, HashSet<string> Locked) ContainerNames(IFileProvider provider)
    {
        if (provider is not CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider vfs)
        {
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return (vfs.MountedVfs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                vfs.UnloadedVfs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Containers that can be read in one of these builds but not the other.
    /// <para>
    /// A locked container is not by itself a problem — the live build almost always has a few whose
    /// keys Epic has not published yet, and one locked on both sides simply appears in neither file
    /// list. What ruins a comparison is a container readable on one side only: every file in it looks
    /// added or removed when all that changed is whether it could be opened. Those containers are left
    /// out of the comparison and reported, rather than silently distorting it.
    /// </para>
    /// </summary>
    public static HashSet<string> AsymmetricContainers(IFileProvider fromProvider, IFileProvider toProvider)
    {
        var (fromMounted, fromLocked) = ContainerNames(fromProvider);
        var (toMounted, toLocked) = ContainerNames(toProvider);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result.UnionWith(fromLocked.Where(toMounted.Contains));
        result.UnionWith(toLocked.Where(fromMounted.Contains));
        return result;
    }

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

    // ------------------------------------------------------------ composition

    /// <summary>
    /// Composes two consecutive changelists into one, so a recorded v40 → v41 and v41 → v42 become
    /// v40 → v42 without either older build still being available.
    /// <para>
    /// A path missing from a changelist means it did not change across that step, which is what makes
    /// the composition sound: an entry present in only one of the two steps keeps that step's sizes
    /// and archives, because the other step left the file alone.
    /// </para>
    /// </summary>
    public static BuildDiff Compose(BuildDiff first, BuildDiff second)
    {
        if (!string.Equals(first.ToBuild, second.FromBuild, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Cannot compose {first.FromBuild}→{first.ToBuild} with {second.FromBuild}→{second.ToBuild}: " +
                "the two changelists do not meet at the same build.");
        }

        var result = new BuildDiff
        {
            FromBuild = first.FromBuild,
            ToBuild = second.ToBuild,
            // A composition is only as exact as its weaker half.
            Mode = first.Mode == "full" && second.Mode == "full" ? "full" : "quick",
            Via = [.. first.Via, first.ToBuild, .. second.Via],
            ComputedUtc = DateTime.UtcNow,
            PathFilter = first.PathFilter ?? second.PathFilter,
            Truncated = first.Truncated || second.Truncated,
            TotalFilesFrom = first.TotalFilesFrom,
            TotalFilesTo = second.TotalFilesTo,
            UnverifiedCount = Math.Max(first.UnverifiedCount, second.UnverifiedCount),
            UnreadableCount = first.UnreadableCount + second.UnreadableCount
        };

        var byPath = new Dictionary<string, BuildChangeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in first.Entries)
        {
            byPath[entry.Path] = Clone(entry);
        }

        foreach (var entry in second.Entries)
        {
            if (!byPath.TryGetValue(entry.Path, out var head))
            {
                // Untouched by the first step, so the file was identical in build A and build B and
                // the second step's record applies unchanged from A's point of view.
                byPath[entry.Path] = Clone(entry);
                continue;
            }

            var merged = Merge(head, entry);
            if (merged == null)
            {
                byPath.Remove(entry.Path); // added then removed again: no net change
            }
            else
            {
                byPath[entry.Path] = merged;
            }
        }

        result.Entries = byPath.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        result.AddedCount = result.Entries.Count(e => e.Kind == BuildChangeKind.Added);
        result.RemovedCount = result.Entries.Count(e => e.Kind == BuildChangeKind.Removed);
        result.ModifiedCount = result.Entries.Count(e => e.Kind == BuildChangeKind.Modified);
        return result;
    }

    private static BuildChangeEntry Clone(BuildChangeEntry e) => new()
    {
        Path = e.Path,
        Kind = e.Kind,
        OldSize = e.OldSize,
        NewSize = e.NewSize,
        OldArchive = e.OldArchive,
        NewArchive = e.NewArchive,
        OldHash = e.OldHash,
        NewHash = e.NewHash
    };

    /// <summary>Combines what happened to one path in A→B with what happened to it in B→C.</summary>
    private static BuildChangeEntry? Merge(BuildChangeEntry a2b, BuildChangeEntry b2c)
    {
        var kind = (a2b.Kind, b2c.Kind) switch
        {
            // Appeared in B and was gone again in C: from A's point of view nothing happened.
            (BuildChangeKind.Added, BuildChangeKind.Removed) => (BuildChangeKind?)null,

            // Present in A, absent in B, back in C: A and C both have it, with different content.
            (BuildChangeKind.Removed, BuildChangeKind.Added) => BuildChangeKind.Modified,

            // Whatever happened in between, the file is new in C if it was absent from A.
            (BuildChangeKind.Added, _) => BuildChangeKind.Added,

            // Absent from B means absent from C as well unless it came back, handled above.
            (BuildChangeKind.Removed, _) => BuildChangeKind.Removed,

            // Present in A; the second step decides whether it survives into C.
            (_, BuildChangeKind.Removed) => BuildChangeKind.Removed,
            (_, BuildChangeKind.Added) => BuildChangeKind.Modified,
            (BuildChangeKind.Unverified, BuildChangeKind.Unverified) => BuildChangeKind.Unverified,
            _ => BuildChangeKind.Modified
        };

        if (kind == null)
        {
            return null;
        }

        return new BuildChangeEntry
        {
            Path = a2b.Path,
            Kind = kind.Value,
            OldSize = a2b.OldSize ?? b2c.OldSize,
            NewSize = b2c.NewSize ?? a2b.NewSize,
            OldArchive = a2b.OldArchive ?? b2c.OldArchive,
            NewArchive = b2c.NewArchive ?? a2b.NewArchive,
            OldHash = a2b.OldHash,
            NewHash = b2c.NewHash
        };
    }

    // ------------------------------------------------------------ the update flow

    /// <summary>
    /// Runs the whole bookkeeping of an update, after the provider has been rebuilt onto
    /// <paramref name="currentBuild"/>:
    /// <list type="number">
    ///   <item>mount the build that was just replaced, from its archived manifest;</item>
    ///   <item>record the changelist between it and the build that is now live;</item>
    ///   <item>extend every changelist that ended at the replaced build through to the new one, so a
    ///         v40 → v41 record plus the fresh v41 → v42 record becomes a v40 → v42 record;</item>
    ///   <item>unmount it again and let the retention policy delete whatever is now too old to keep.</item>
    /// </list>
    /// Failures are logged and swallowed: a changelist is worth far less than the live build staying up.
    /// </summary>
    public async Task RecordUpdateAsync(string previousBuild, string currentBuild,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(previousBuild) || string.IsNullOrEmpty(currentBuild) ||
            string.Equals(previousBuild, currentBuild, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_store.HasDiff(previousBuild, currentBuild))
        {
            Console.WriteLine($"Changelist {previousBuild} → {currentBuild} is already recorded; skipping.");
            return;
        }

        if (!_historical.CanLoad(previousBuild))
        {
            Console.WriteLine($"Cannot record the changelist: no archived manifest for {previousBuild}.");
            return;
        }

        Console.WriteLine($"\n=== Recording the changelist {previousBuild} → {currentBuild} ===");
        try
        {
            using var oldBuild = await LeaseAsync(previousBuild, cancellationToken);

            var excluded = AsymmetricContainers(oldBuild.Provider, _liveProvider);
            if (excluded.Count > 0)
            {
                Console.WriteLine($"  Leaving {excluded.Count} container(s) out of the comparison: they can be " +
                                  "read in only one of the two builds, so their files would look added or removed.");
            }

            var diff = Compute(oldBuild.Provider, _liveProvider, previousBuild, currentBuild,
                mode: "quick", excludeArchives: excluded, cancellationToken: cancellationToken);

            // Another update can land while this comparison is running, which would have swapped the
            // live provider out from under it and made the result a mix of two builds. Recording
            // nothing is better than recording that; the next update records the pair that is current.
            if (!IsLive(currentBuild))
            {
                Console.WriteLine($"✗ Discarding the changelist {previousBuild} → {currentBuild}: " +
                                  $"the live build moved on to {_manifestService.GameBuild} while it was being computed.");
                return;
            }

            _store.SaveDiff(diff);
            Console.WriteLine($"✓ Recorded {previousBuild} → {currentBuild}: " +
                              $"+{diff.AddedCount} / -{diff.RemovedCount} / ~{diff.ModifiedCount} " +
                              $"({diff.UnverifiedCount} same-size files not hashed) in {diff.DurationSeconds:F1}s");

            ExtendChainsThrough(previousBuild, diff);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Could not record the changelist {previousBuild} → {currentBuild}: {ex.Message}");
        }
        finally
        {
            // The replaced build does not need to stay mounted; it is re-mountable from its archived
            // manifest for as long as the retention policy keeps it.
            _historical.Unload(previousBuild);

            foreach (var pruned in _store.ApplyRetention())
            {
                _historical.Unload(pruned);
            }
        }
    }

    /// <summary>
    /// Extends every recorded changelist that ends at <paramref name="middleBuild"/> through
    /// <paramref name="tail"/>, so older builds keep a direct changelist to the newest one even after
    /// their own data is gone.
    /// </summary>
    private void ExtendChainsThrough(string middleBuild, BuildDiff tail)
    {
        foreach (var (from, to, _, _) in _store.ListDiffs())
        {
            if (!string.Equals(to, middleBuild, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(from, tail.ToBuild, StringComparison.OrdinalIgnoreCase) ||
                _store.HasDiff(from, tail.ToBuild))
            {
                continue;
            }

            var head = _store.LoadDiff(from, to);
            if (head == null)
            {
                continue;
            }

            try
            {
                var composed = Compose(head, tail);
                _store.SaveDiff(composed);
                Console.WriteLine($"✓ Composed {composed.FromBuild} → {composed.ToBuild} " +
                                  $"(via {string.Join(", ", composed.Via)}): " +
                                  $"+{composed.AddedCount} / -{composed.RemovedCount} / ~{composed.ModifiedCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Could not compose {from} → {tail.ToBuild}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns a recorded changelist, composing one out of the recorded chain when the exact pair was
    /// never recorded directly. Returns null when the two builds cannot be connected.
    /// </summary>
    public BuildDiff? GetOrComposeDiff(string fromBuild, string toBuild)
    {
        var direct = _store.LoadDiff(fromBuild, toBuild);
        if (direct != null)
        {
            return direct;
        }

        // Breadth-first over the recorded edges: the chain is short (a handful of builds), so the
        // simple search is more than enough and always finds the fewest compositions.
        var edges = _store.ListDiffs();
        var queue = new Queue<List<(string From, string To)>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromBuild };

        foreach (var edge in edges.Where(e => string.Equals(e.From, fromBuild, StringComparison.OrdinalIgnoreCase)))
        {
            queue.Enqueue([(edge.From, edge.To)]);
        }

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var last = path[^1].To;

            if (string.Equals(last, toBuild, StringComparison.OrdinalIgnoreCase))
            {
                BuildDiff? composed = null;
                foreach (var (from, to) in path)
                {
                    var step = _store.LoadDiff(from, to);
                    if (step == null)
                    {
                        return null;
                    }

                    composed = composed == null ? step : Compose(composed, step);
                }

                return composed;
            }

            if (!seen.Add(last))
            {
                continue;
            }

            foreach (var edge in edges.Where(e => string.Equals(e.From, last, StringComparison.OrdinalIgnoreCase)))
            {
                queue.Enqueue([.. path, (edge.From, edge.To)]);
            }
        }

        return null;
    }

    // ------------------------------------------------------------ per-file line diff

    /// <summary>Extensions whose bytes are text and can be diffed line by line as they are.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ini", "txt", "json", "csv", "xml", "cfg", "log", "md", "html", "htm", "js", "css", "verse", "digest"
    };

    /// <summary>What a single file's content looked like in one build.</summary>
    public sealed record FileSide(bool Exists, string? Text, byte[]? Bytes, long Size, string? Archive, string? Error);

    /// <summary>Reads one file out of a build as text when it can be, and as bytes otherwise.</summary>
    public static FileSide ReadSide(IFileProvider provider, string path)
    {
        if (!provider.Files.TryGetValue(path, out var file))
        {
            return new FileSide(false, null, null, 0, null, null);
        }

        var archive = (file as VfsEntry)?.Vfs.Name;
        try
        {
            if (!provider.TrySaveAsset(path, out var bytes) || bytes == null)
            {
                return new FileSide(true, null, null, file.Size, archive, "The file could not be read (its archive may still be locked).");
            }

            var extension = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : string.Empty;
            var text = TextExtensions.Contains(extension) || LooksLikeText(bytes)
                ? DecodeText(bytes)
                : null;

            return new FileSide(true, text, bytes, bytes.LongLength, archive, null);
        }
        catch (Exception ex)
        {
            return new FileSide(true, null, null, file.Size, archive, ex.Message);
        }
    }

    /// <summary>
    /// Treats the content as text when the first few KB decode as UTF-8/UTF-16 with no NUL bytes.
    /// Assets are binary and fail this, which is why the endpoints diff their JSON export instead.
    /// </summary>
    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        // A UTF-16 BOM is text even though half its bytes are NUL.
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        var sample = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sample; i++)
        {
            if (bytes[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Describes where two binary blobs start and stop differing, when a line diff is meaningless.</summary>
    public static object DescribeBinaryDifference(byte[] oldBytes, byte[] newBytes, int maxRanges = 50)
    {
        var common = Math.Min(oldBytes.Length, newBytes.Length);
        var ranges = new List<object>();
        var differingBytes = 0L;

        var index = 0;
        while (index < common)
        {
            if (oldBytes[index] == newBytes[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < common && oldBytes[index] != newBytes[index])
            {
                index++;
            }

            differingBytes += index - start;
            if (ranges.Count < maxRanges)
            {
                ranges.Add(new { offset = start, length = index - start });
            }
        }

        if (oldBytes.Length != newBytes.Length)
        {
            differingBytes += Math.Abs((long)oldBytes.Length - newBytes.Length);
        }

        return new
        {
            oldBytes = oldBytes.Length,
            newBytes = newBytes.Length,
            oldSha256 = Convert.ToHexString(SHA256.HashData(oldBytes)),
            newSha256 = Convert.ToHexString(SHA256.HashData(newBytes)),
            differingBytes,
            truncatedRanges = ranges.Count >= maxRanges,
            changedRanges = ranges
        };
    }
}
