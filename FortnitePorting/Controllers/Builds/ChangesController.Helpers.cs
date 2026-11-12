using FortnitePorting.Models;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

public sealed partial class ChangesController
{
    /// <summary>
    /// Returns the changelist between two builds, computing it on the spot when it was never recorded
    /// but both builds happen to be mounted right now.
    /// <para>
    /// A quick comparison of two mounted builds is an intersection of their file indexes: no chunk is
    /// fetched and no build is mounted, so it belongs in the request rather than behind a job. The
    /// result is recorded on the way out, so the next caller does not pay for it again. This is what
    /// lets an instance that never lived through the update still answer for it.
    /// </para>
    /// </summary>
    private (BuildDiff? Diff, IActionResult? Error) ResolveDiff(string fromBuild, string toBuild, bool force = false)
    {
        var recorded = _diffs.GetOrComposeDiff(fromBuild, toBuild);
        if (recorded != null)
        {
            return (recorded, null);
        }

        if (_diffs.TryLease(fromBuild, out var fromLease))
        {
            using (fromLease)
            {
                if (_diffs.TryLease(toBuild, out var toLease))
                {
                    using (toLease)
                    {
                        // Containers only one of the two builds can open are left out, so their files
                        // are absent rather than misreported as added or removed. force keeps them in.
                        var excluded = force
                            ? null
                            : BuildDiffService.AsymmetricContainers(fromLease.Provider, toLease.Provider);

                        var computed = _diffs.Compute(fromLease.Provider, toLease.Provider, fromBuild,
                            toBuild, excludeArchives: excluded);
                        Response.Headers["X-Changes-Computed"] = "true";

                        // A forced comparison is known to be distorted by those containers, so it
                        // answers this request but is never written to the archive where a later caller
                        // would take it for a sound record.
                        if (force)
                        {
                            Response.Headers["X-Changes-Forced"] = "true";
                            return (computed, null);
                        }

                        _store.SaveDiff(computed);
                        return (computed, null);
                    }
                }
            }
        }

        return (null, NotRecorded(fromBuild, toBuild));
    }

    /// <summary>
    /// The 404 for a pair that is neither recorded nor comparable right now. It names what is missing
    /// and the exact call that fixes it, because the usual cause is simply that one of the two builds
    /// is not mounted.
    /// </summary>
    private IActionResult NotRecorded(string fromBuild, string toBuild)
    {
        var missing = new[] { fromBuild, toBuild }.Where(NotMounted).ToList();
        var mountable = missing.Where(b => _historical.CanLoad(b)).ToList();
        var names = string.Join(" and ", missing.Select(b => "'" + b + "'"));

        return NotFound(new ProblemDetails
        {
            Title = "変更リストが記録されていません",
            Detail = $"No changelist connects '{fromBuild}' to '{toBuild}', and they cannot be compared " +
                     $"right now because {names} " + (missing.Count == 1 ? "is" : "are") + " not mounted. " +
                     (mountable.Count > 0
                         ? "Mount " + (mountable.Count == 1 ? "it" : "them") +
                           " and repeat this request: two mounted builds are compared on the spot."
                         : "Their archived data is gone, so they can no longer be compared."),
            Status = StatusCodes.Status404NotFound,
            Extensions =
            {
                { "from", fromBuild },
                { "to", toBuild },
                { "notMounted", missing },
                { "mountWith", mountable.Select(b => "POST /api/v1/versions/load?version=" + Uri.EscapeDataString(b)).ToList() },
                { "orComputeInBackground", ComputeUrl(fromBuild, toBuild) }
            }
        });
    }

    /// <summary>True when this build is not mounted. Any lease taken to find out is released again.</summary>
    private bool NotMounted(string buildVersion)
    {
        if (!_diffs.TryLease(buildVersion, out var lease))
        {
            return true;
        }

        lease.Dispose();
        return false;
    }

    /// <summary>Everything about this changelist that keeps it from being the complete truth.</summary>
    private static List<string> BuildNotes(BuildDiff diff)
    {
        var notes = new List<string>();

        if (diff.UnverifiedCount > 0)
        {
            notes.Add($"{diff.UnverifiedCount} file(s) kept the same size and were never hashed, so a rewrite " +
                      "that did not change the size is not in this list. Re-run POST /api/v1/changes/compute " +
                      "with mode=full (scoped by pathFilter) to resolve them.");
        }

        if (diff.ExcludedArchives.Count > 0)
        {
            notes.Add($"{diff.ExcludedArchives.Count} container(s) were left out because only one of the two " +
                      $"builds could open them ({string.Join(", ", diff.ExcludedArchives.Take(5))}" +
                      (diff.ExcludedArchives.Count > 5 ? $", +{diff.ExcludedArchives.Count - 5} more" : "") +
                      $"), hiding {diff.ExcludedFilesFrom} file(s) of the older build and {diff.ExcludedFilesTo} " +
                      "of the newer one. Supply the missing AES keys with POST /api/v1/versions/keys to include them.");
        }

        if (diff.Truncated)
        {
            notes.Add("The comparison hit a limit and this changelist is incomplete.");
        }

        if (notes.Count == 0)
        {
            notes.Add("Every file present in both builds was compared.");
        }

        return notes;
    }

    private static string ComputeUrl(string fromBuild, string toBuild)
        => $"POST /api/v1/changes/compute?from={Uri.EscapeDataString(fromBuild)}&to={Uri.EscapeDataString(toBuild)}";

    private static object Describe(DiffJobService.DiffJob job) => new
    {
        id = job.Id,
        from = job.FromBuild,
        to = job.ToBuild,
        mode = job.Mode,
        pathFilter = job.PathFilter,
        status = job.Status,
        startedUtc = job.StartedUtc,
        finishedUtc = job.FinishedUtc,
        error = job.Error,
        progress = new
        {
            phase = job.Progress.Phase,
            compared = job.Progress.Compared,
            hashed = job.Progress.Hashed,
            total = job.Progress.Total
        },
        result = job.Result
    };

    private static int CountLines(string? text) => text == null ? 0 : TextDiff.SplitLines(text).Length;

    /// <summary>
    /// Resolves the <c>from</c>/<c>to</c> parameters. <c>to</c> defaults to the live build, which is
    /// what "what changed since build X" means in practice.
    /// </summary>
    private (string? From, string? To, IActionResult? Error) ResolvePair(string from, string? to)
    {
        var fromBuild = _diffs.ResolveBuildVersion(from);
        if (fromBuild == null)
        {
            return (null, null, UnknownBuild(from));
        }

        var toBuild = _diffs.ResolveBuildVersion(string.IsNullOrWhiteSpace(to) ? "latest" : to);
        if (toBuild == null)
        {
            return (null, null, UnknownBuild(to!));
        }

        if (string.Equals(fromBuild, toBuild, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, BadRequest(new ProblemDetails
            {
                Title = "同じビルド同士は比較できません",
                Detail = $"from and to both resolve to '{fromBuild}'.",
                Status = StatusCodes.Status400BadRequest
            }));
        }

        return (fromBuild, toBuild, null);
    }

    private async Task<(BuildLease? Lease, IActionResult? Error)> ResolveProviderAsync(
        string build, bool load, CancellationToken cancellationToken)
    {
        // This route bypasses the global reload gate so archived builds stay readable during an
        // update; the live provider therefore has to be checked here.
        if (_diffs.IsLive(build) && ProviderReloadGate.Instance.IsReloading)
        {
            Response.Headers.RetryAfter = "30";
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "ライブビルドを再読み込み中です",
                Detail = $"Build '{build}' is the live one and is being reloaded right now. Retry shortly.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Extensions = { { "status", ProviderReloadGate.Instance.State } }
            }));
        }

        if (_diffs.TryLease(build, out var lease))
        {
            return (lease, null);
        }

        if (!load)
        {
            return (null, StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
            {
                Title = "そのビルドは読み込まれていません",
                Detail = $"Build '{build}' is not loaded. Re-send with load=true, or mount it with " +
                         $"POST /api/v1/versions/load?version={Uri.EscapeDataString(build)}.",
                Status = StatusCodes.Status409Conflict
            }));
        }

        try
        {
            return (await _historical.LeaseAsync(build, cancellationToken), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, NotFound(new ProblemDetails
            {
                Title = "アーカイブされたビルドがありません",
                Detail = ex.Message,
                Status = StatusCodes.Status404NotFound
            }));
        }
    }

    private IActionResult UnknownBuild(string version) => NotFound(new ProblemDetails
    {
        Title = "ビルドが見つかりません",
        Detail = $"No known build matches '{version}'. Call GET /api/v1/versions to see what this instance has.",
        Status = StatusCodes.Status404NotFound,
        Extensions = { { "known", _diffs.KnownBuildVersions() } }
    });
}
