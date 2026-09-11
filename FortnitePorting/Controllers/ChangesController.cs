using FortnitePorting.Models;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// The exact changelist between two Fortnite builds: which virtual paths were added, removed or
/// modified, and which lines changed inside one of them.
/// <para>
/// A changelist is recorded automatically whenever an update is applied — the build that was just
/// replaced is compared against the new one before its data is dropped — and every older record is
/// extended through the new build at the same time, so a v40 → v41 record plus a fresh v41 → v42
/// record yields a v40 → v42 record even after v40's and v41's own data is gone.
/// </para>
/// <para>
/// An instance that never lived through the update can still answer: when the pair was not recorded
/// but both builds are mounted, the comparison runs inside the request and is recorded on the way
/// out. Comparing two mounted builds only intersects their file indexes, so nothing is downloaded.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/changes")]
public sealed class ChangesController : ControllerBase
{
    private readonly BuildHistoryStore _store;
    private readonly BuildDiffService _diffs;
    private readonly DiffJobService _jobs;
    private readonly HistoricalBuildService _historical;

    public ChangesController(BuildHistoryStore store, BuildDiffService diffs, DiffJobService jobs,
        HistoricalBuildService historical)
    {
        _store = store;
        _diffs = diffs;
        _jobs = jobs;
        _historical = historical;
    }

    /// <summary>
    /// Lists every changelist recorded on this instance.
    /// </summary>
    [HttpGet]
    public IActionResult GetRecorded()
    {
        var recorded = _store.ListDiffs()
            .Select(d => new { from = d.From, to = d.To, bytes = d.Bytes, recordedUtc = d.WrittenUtc })
            .ToList();

        return Ok(new { count = recorded.Count, changelists = recorded });
    }

    /// <summary>
    /// Returns the changelist between two builds: the file paths that were added, removed or modified.
    /// When the exact pair was never recorded directly, it is composed out of the recorded chain.
    /// </summary>
    /// <param name="from">Older build, e.g. <c>++Fortnite+Release-42.00-CL-56878558-Windows</c>, <c>42.00</c> or <c>previous</c>.</param>
    /// <param name="to">Newer build. Defaults to the live build.</param>
    /// <param name="kind">Filter by change type: <c>added</c>, <c>removed</c>, <c>modified</c>, or all when omitted.</param>
    /// <param name="pathFilter">Only return paths starting with this prefix.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Entries per page, from 1 to 10000.</param>
    /// <param name="force">Compare even when a container is readable in only one of the builds. Default false.</param>
    [HttpGet("list")]
    public IActionResult GetChangelist([FromQuery] string from, [FromQuery] string? to = null,
        [FromQuery] string? kind = null, [FromQuery] string? pathFilter = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 500, [FromQuery] bool force = false)
    {
        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        var (diff, diffError) = ResolveDiff(fromBuild!, toBuild!, force);
        if (diffError != null)
        {
            return diffError;
        }

        var entries = diff!.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<BuildChangeKind>(kind, ignoreCase: true, out var wanted))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "kindが不正です",
                    Detail = "kind must be one of: added, removed, modified, unverified.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            entries = entries.Where(e => e.Kind == wanted);
        }

        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            entries = entries.Where(e => e.Path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = entries.ToList();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 10000);

        return Ok(new
        {
            summary = DiffJobService.Summarize(diff),
            matched = filtered.Count,
            totalPages = (int)Math.Ceiling(filtered.Count / (double)pageSize),
            currentPage = page,
            pageSize,
            changes = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(e => new
            {
                path = e.Path,
                kind = e.Kind.ToString().ToLowerInvariant(),
                oldSize = e.OldSize,
                newSize = e.NewSize,
                oldArchive = e.OldArchive,
                newArchive = e.NewArchive,
                oldSha256 = e.OldHash,
                newSha256 = e.NewHash
            })
        });
    }

    /// <summary>
    /// Returns a file listing only the paths that were actually rewritten between two builds.
    /// <para>
    /// This is the intersection of the two builds' path lists with the unchanged files taken out:
    /// files that only exist in the newer build (added) and files that only exist in the older one
    /// (removed) are both excluded, so every path in the response exists in both builds and has
    /// different content in each.
    /// </para>
    /// </summary>
    /// <param name="from">Older build, e.g. <c>++Fortnite+Release-42.00-CL-56878558-Windows</c>, <c>42.00</c> or <c>previous</c>.</param>
    /// <param name="to">Newer build. Defaults to the live build.</param>
    /// <param name="pathFilter">Only include paths containing this fragment.</param>
    /// <param name="verifiedOnly">Include only paths whose rewrite was confirmed by hashing both copies. Default false.</param>
    /// <param name="format"><c>text</c> for one path per line (default) or <c>json</c> for the list plus its metadata.</param>
    /// <param name="download">Send the text form as a file attachment. Default true.</param>
    /// <param name="force">Compare even when a container is readable in only one of the builds. Default false.</param>
    [HttpGet("modified")]
    public IActionResult GetModifiedPaths([FromQuery] string from, [FromQuery] string? to = null,
        [FromQuery] string? pathFilter = null, [FromQuery] bool verifiedOnly = false,
        [FromQuery] string format = "text", [FromQuery] bool download = true, [FromQuery] bool force = false)
    {
        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        var (diff, diffError) = ResolveDiff(fromBuild!, toBuild!, force);
        if (diffError != null)
        {
            return diffError;
        }

        // Only Modified survives: Added exists in the newer build alone, Removed in the older one
        // alone, and Unverified is never recorded as an entry.
        var entries = diff!.Entries.Where(e => e.Kind == BuildChangeKind.Modified);

        if (verifiedOnly)
        {
            // A quick comparison infers a rewrite from the size changing. That is sound, but only an
            // entry carrying both hashes was actually read and compared.
            entries = entries.Where(e => e.OldHash != null && e.NewHash != null);
        }

        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            entries = entries.Where(e => e.Path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
        }

        var paths = entries
            .Select(e => e.Path)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The same-size files a quick comparison never hashed are the one thing that can make this list
        // short, so the count travels with the response instead of being left implicit.
        Response.Headers["X-Changes-Mode"] = diff.Mode;
        Response.Headers["X-Changes-Modified"] = paths.Count.ToString();
        Response.Headers["X-Changes-Unverified"] = diff.UnverifiedCount.ToString();
        Response.Headers["X-Changes-Excluded-Archives"] = diff.ExcludedArchives.Count.ToString();

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new
            {
                from = fromBuild,
                to = toBuild,
                mode = diff.Mode,
                via = diff.Via,
                computedUtc = diff.ComputedUtc,
                filesInFrom = diff.TotalFilesFrom,
                filesInTo = diff.TotalFilesTo,
                modified = paths.Count,
                excludedAdded = diff.AddedCount,
                excludedRemoved = diff.RemovedCount,
                unverifiedSameSize = diff.UnverifiedCount,
                excludedArchives = diff.ExcludedArchives,
                excludedFilesFrom = diff.ExcludedFilesFrom,
                excludedFilesTo = diff.ExcludedFilesTo,
                complete = diff.UnverifiedCount == 0 && diff.ExcludedArchives.Count == 0 && !diff.Truncated,
                notes = BuildNotes(diff),
                paths
            });
        }

        var body = paths.Count > 0 ? string.Join('\n', paths) + '\n' : string.Empty;

        if (!download)
        {
            return Content(body, "text/plain; charset=utf-8");
        }

        var name = $"modified_{Sanitize(fromBuild!)}_to_{Sanitize(toBuild!)}.txt";
        return File(System.Text.Encoding.UTF8.GetBytes(body), "text/plain; charset=utf-8", name);
    }

    /// <summary>Turns a build version into a file name that is safe on every platform.</summary>
    private static string Sanitize(string buildVersion)
    {
        var (version, changelist) = BuildHistoryStore.SplitBuildVersion(buildVersion);
        if (!string.IsNullOrEmpty(version))
        {
            return string.IsNullOrEmpty(changelist) ? version : $"{version}-CL-{changelist}";
        }

        var safe = buildVersion;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }

        return safe.Replace('+', '_');
    }

    /// <summary>
    /// Returns the lines that changed inside one file between two builds. Text files are diffed as
    /// they are; a <c>.uasset</c>/<c>.umap</c> is diffed through its JSON export, which is what makes
    /// "changed lines" meaningful for an asset. Both builds have to be readable.
    /// </summary>
    /// <param name="from">Older build.</param>
    /// <param name="to">Newer build. Defaults to the live build.</param>
    /// <param name="path">Virtual file path to diff.</param>
    /// <param name="format"><c>json</c> for structured hunks (default) or <c>patch</c> for unified-diff text.</param>
    /// <param name="context">Context lines around each hunk, 0 to 20. Default 3.</param>
    /// <param name="load">Mount the older build when it is archived but not loaded. Default true.</param>
    [HttpGet("file")]
    public async Task<IActionResult> GetFileChanges([FromQuery] string from, [FromQuery] string path,
        [FromQuery] string? to = null, [FromQuery] string format = "json", [FromQuery] int context = 3,
        [FromQuery] bool load = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "pathが必要です",
                Detail = "path is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        var (fromLease, fromError) = await ResolveProviderAsync(fromBuild!, load, cancellationToken);
        if (fromError != null)
        {
            return fromError;
        }

        using var oldBuild = fromLease!;

        var (toLease, toError) = await ResolveProviderAsync(toBuild!, load, cancellationToken);
        if (toError != null)
        {
            return toError;
        }

        using var newBuild = toLease!;

        var oldText = VersionedAssetReader.ReadAsDiffableText(oldBuild.Provider, path, out var oldKind, out var oldError);
        var newText = VersionedAssetReader.ReadAsDiffableText(newBuild.Provider, path, out var newKind, out var newError);

        if (oldKind == "missing" && newKind == "missing")
        {
            return NotFound(new ProblemDetails
            {
                Title = "どちらのビルドにもファイルがありません",
                Detail = $"'{path}' exists in neither '{fromBuild}' nor '{toBuild}'.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // One side missing is an add or a delete, not a line diff.
        if (oldKind == "missing" || newKind == "missing")
        {
            return Ok(new
            {
                from = fromBuild,
                to = toBuild,
                path,
                change = oldKind == "missing" ? "added" : "removed",
                lines = oldKind == "missing" ? CountLines(newText) : CountLines(oldText),
                message = oldKind == "missing"
                    ? "The file only exists in the newer build."
                    : "The file only exists in the older build."
            });
        }

        // Neither side rendered as text: fall back to describing the binary difference honestly
        // rather than inventing line numbers for content that has no lines.
        if (oldText == null || newText == null)
        {
            var oldSide = BuildDiffService.ReadSide(oldBuild.Provider, path);
            var newSide = BuildDiffService.ReadSide(newBuild.Provider, path);

            if (oldSide.Bytes == null || newSide.Bytes == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "ファイルを読み取れませんでした",
                    Detail = oldError ?? newError ?? oldSide.Error ?? newSide.Error ??
                             "The file could not be read from one of the two builds.",
                    Status = StatusCodes.Status502BadGateway
                });
            }

            return Ok(new
            {
                from = fromBuild,
                to = toBuild,
                path,
                change = "binary",
                message = "This file is not text and its package could not be exported, so it is reported as a byte-level difference.",
                oldError,
                newError,
                difference = BuildDiffService.DescribeBinaryDifference(oldSide.Bytes, newSide.Bytes)
            });
        }

        context = Math.Clamp(context, 0, 20);
        var result = TextDiff.Compute(oldText, newText, context);

        if (string.Equals(format, "patch", StringComparison.OrdinalIgnoreCase))
        {
            var patch = result.ToUnifiedDiff($"{fromBuild}/{path}", $"{toBuild}/{path}");
            return Content(patch, "text/plain; charset=utf-8");
        }

        return Ok(new
        {
            from = fromBuild,
            to = toBuild,
            path,
            change = result.Identical ? "unchanged" : "modified",
            comparedAs = oldKind == "package" ? "package-json" : "text",
            oldLineCount = result.OldLineCount,
            newLineCount = result.NewLineCount,
            addedLines = result.AddedLines,
            removedLines = result.RemovedLines,
            truncated = result.Truncated,
            hunks = result.Hunks.Select(h => new
            {
                header = h.Header,
                oldStart = h.OldStart,
                oldLines = h.OldLines,
                newStart = h.NewStart,
                newLines = h.NewLines,
                lines = h.Lines
            })
        });
    }

    /// <summary>
    /// Computes and records a changelist between two builds. Runs as a background job because it has
    /// to mount the older build, and in <c>full</c> mode stream every same-size candidate from the CDN.
    /// </summary>
    /// <param name="from">Older build.</param>
    /// <param name="to">Newer build. Defaults to the live build.</param>
    /// <param name="mode"><c>quick</c> (path, size and archive; no content is read) or <c>full</c> (same-size files are hashed on both sides). Default quick.</param>
    /// <param name="pathFilter">Restrict the comparison to paths starting with this prefix. Strongly recommended with mode=full.</param>
    /// <param name="maxEntries">Maximum entries to record, from 1 to 500000. Default 200000.</param>
    /// <param name="maxHashFiles">In full mode, maximum same-size files to hash, from 1 to 200000. Default 5000.</param>
    /// <param name="unloadWhenDone">Unmount the builds the job had to mount once it finishes. Default true.</param>
    [HttpPost("compute")]
    public IActionResult Compute([FromQuery] string from, [FromQuery] string? to = null,
        [FromQuery] string mode = "quick", [FromQuery] string? pathFilter = null,
        [FromQuery] int maxEntries = 200000, [FromQuery] int maxHashFiles = 5000,
        [FromQuery] bool unloadWhenDone = true, [FromQuery] bool force = false)
    {
        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        if (!string.Equals(mode, "quick", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "modeが不正です",
                Detail = "mode must be either quick or full.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        foreach (var build in new[] { fromBuild!, toBuild! })
        {
            if (!_diffs.IsLive(build) && !_historical.CanLoad(build))
            {
                return StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
                {
                    Title = "そのビルドのデータが残っていません",
                    Detail = $"Build '{build}' has no archived data, so it cannot be compared. " +
                             "Its already-recorded changelists remain available under GET /api/v1/changes/list.",
                    Status = StatusCodes.Status409Conflict
                });
            }
        }

        var job = _jobs.Start(fromBuild!, toBuild!, mode.ToLowerInvariant(), pathFilter,
            Math.Clamp(maxEntries, 1, 500000), Math.Clamp(maxHashFiles, 1, 200000), unloadWhenDone, force);

        return Accepted(new
        {
            jobId = job.Id,
            from = job.FromBuild,
            to = job.ToBuild,
            mode = job.Mode,
            status = job.Status,
            statusEndpoint = $"/api/v1/changes/jobs/{job.Id}"
        });
    }

    /// <summary>Lists the changelist computations started on this instance.</summary>
    [HttpGet("jobs")]
    public IActionResult GetJobs() => Ok(new { jobs = _jobs.Jobs.Select(Describe) });

    /// <summary>Returns the state and progress of one changelist computation.</summary>
    /// <param name="id">Job id returned by the compute endpoint.</param>
    [HttpGet("jobs/{id}")]
    public IActionResult GetJob(string id)
        => _jobs.TryGet(id, out var job)
            ? Ok(Describe(job))
            : NotFound(new ProblemDetails
            {
                Title = "ジョブが見つかりません",
                Detail = $"No changelist job with id '{id}'.",
                Status = StatusCodes.Status404NotFound
            });

    /// <summary>Cancels a running changelist computation.</summary>
    /// <param name="id">Job id returned by the compute endpoint.</param>
    [HttpDelete("jobs/{id}")]
    public IActionResult CancelJob(string id) => Ok(new { id, cancelled = _jobs.Cancel(id) });

    /// <summary>Deletes a recorded changelist.</summary>
    /// <param name="from">Older build of the recorded pair.</param>
    /// <param name="to">Newer build of the recorded pair.</param>
    [HttpDelete]
    public IActionResult DeleteChangelist([FromQuery] string from, [FromQuery] string to)
    {
        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        return Ok(new { from = fromBuild, to = toBuild, deleted = _store.DeleteDiff(fromBuild!, toBuild!) });
    }

    // ------------------------------------------------------------ helpers

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
