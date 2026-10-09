using System.Collections.Concurrent;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

/// <summary>
/// Runs changelist computations in the background.
/// <para>
/// Comparing two builds means mounting the older one and, in <c>full</c> mode, streaming every
/// same-size candidate from the CDN — minutes at best. Holding an HTTP request open for that is not
/// workable, so a computation is started as a job and polled.
/// </para>
/// </summary>
public sealed class DiffJobService
{
    /// <summary>One queued, running or finished changelist computation.</summary>
    public sealed class DiffJob
    {
        public required string Id { get; init; }
        public required string FromBuild { get; init; }
        public required string ToBuild { get; init; }
        public required string Mode { get; init; }
        public string? PathFilter { get; init; }

        /// <summary>Set when the caller chose to compare a build that did not fully mount.</summary>
        public bool Force { get; init; }

        /// <summary>How much of each build mounted, once the job has looked.</summary>
        public MountHealth? FromHealth { get; set; }
        public MountHealth? ToHealth { get; set; }

        /// <summary><c>queued</c>, <c>running</c>, <c>done</c>, <c>failed</c> or <c>cancelled</c>.</summary>
        public string Status { get; set; } = "queued";

        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public string? Error { get; set; }

        public BuildDiffService.DiffProgress Progress { get; } = new();

        /// <summary>Summary of the recorded changelist, once the job has finished.</summary>
        public object? Result { get; set; }

        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly BuildDiffService _diffs;
    private readonly BuildHistoryStore _store;
    private readonly ConcurrentDictionary<string, DiffJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    // One computation at a time: each holds a whole extra build in memory.
    private readonly SemaphoreSlim _slot = new(1, 1);

    public DiffJobService(BuildDiffService diffs, BuildHistoryStore store)
    {
        _diffs = diffs;
        _store = store;
    }

    public IReadOnlyList<DiffJob> Jobs => _jobs.Values.OrderByDescending(j => j.StartedUtc).ToList();

    public bool TryGet(string id, out DiffJob job) => _jobs.TryGetValue(id, out job!);

    /// <summary>Starts a computation and returns immediately with the job to poll.</summary>
    public DiffJob Start(string fromBuild, string toBuild, string mode, string? pathFilter,
        int maxEntries, int maxHashFiles, bool unloadWhenDone, bool force = false)
    {
        var job = new DiffJob
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            FromBuild = fromBuild,
            ToBuild = toBuild,
            Mode = mode,
            PathFilter = pathFilter,
            Force = force
        };

        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, maxEntries, maxHashFiles, unloadWhenDone));
        return job;
    }

    /// <summary>Requests cancellation of a running job.</summary>
    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.Status is "done" or "failed" or "cancelled")
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(DiffJob job, int maxEntries, int maxHashFiles, bool unloadWhenDone)
    {
        await _slot.WaitAsync();
        try
        {
            job.Status = "running";
            job.StartedUtc = DateTime.UtcNow;

            var token = job.Cancellation.Token;

            // Both builds stay leased for the whole comparison, so neither can be evicted to make
            // room for something else while it is being read.
            using var from = await _diffs.LeaseAsync(job.FromBuild, token);
            using var to = await _diffs.LeaseAsync(job.ToBuild, token);

            job.FromHealth = BuildDiffService.Inspect(from.Provider);
            job.ToHealth = BuildDiffService.Inspect(to.Provider);

            // Containers only one of the two builds can open are left out: every file in them would
            // otherwise be reported as added or removed when all that changed is whether the container
            // could be decrypted. force keeps them in, for a caller who wants the raw comparison.
            var excluded = job.Force
                ? null
                : BuildDiffService.AsymmetricContainers(from.Provider, to.Provider);

            var diff = await Task.Run(() => _diffs.Compute(from.Provider, to.Provider, job.FromBuild,
                job.ToBuild, job.Mode, job.PathFilter, maxEntries, maxHashFiles, job.Progress,
                excluded, token), token);

            _store.SaveDiff(diff);
            job.Result = Summarize(diff);
            job.Status = "done";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            Console.WriteLine($"✗ Changelist job {job.Id} failed: {ex}");
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;

            if (unloadWhenDone)
            {
                // Only historical builds can be unloaded; the live one is ignored by Unload.
                _diffs.UnloadIfHistorical(job.FromBuild);
                _diffs.UnloadIfHistorical(job.ToBuild);
            }

            _slot.Release();
        }
    }

    /// <summary>The header of a changelist, without its (potentially very large) entry list.</summary>
    public static object Summarize(BuildDiff diff) => new
    {
        from = diff.FromBuild,
        to = diff.ToBuild,
        mode = diff.Mode,
        via = diff.Via,
        computedUtc = diff.ComputedUtc,
        durationSeconds = Math.Round(diff.DurationSeconds, 1),
        pathFilter = diff.PathFilter,
        truncated = diff.Truncated,
        totalFilesFrom = diff.TotalFilesFrom,
        totalFilesTo = diff.TotalFilesTo,
        unmountedVfsFrom = diff.UnmountedVfsFrom,
        unmountedVfsTo = diff.UnmountedVfsTo,
        excludedArchives = diff.ExcludedArchives,
        excludedFilesFrom = diff.ExcludedFilesFrom,
        excludedFilesTo = diff.ExcludedFilesTo,
        added = diff.AddedCount,
        removed = diff.RemovedCount,
        modified = diff.ModifiedCount,
        unverifiedSameSize = diff.UnverifiedCount,
        unreadable = diff.UnreadableCount,
        entries = diff.Entries.Count
    };
}
