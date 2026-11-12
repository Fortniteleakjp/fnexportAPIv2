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
}
