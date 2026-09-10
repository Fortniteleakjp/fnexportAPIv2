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
    [HttpGet("list")]
    public IActionResult GetChangelist([FromQuery] string from, [FromQuery] string? to = null,
        [FromQuery] string? kind = null, [FromQuery] string? pathFilter = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 500)
    {
        var (fromBuild, toBuild, error) = ResolvePair(from, to);
        if (error != null)
        {
            return error;
        }

        var diff = _diffs.GetOrComposeDiff(fromBuild!, toBuild!);
        if (diff == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "変更リストが記録されていません",
                Detail = $"No changelist connects '{fromBuild}' to '{toBuild}'. " +
                         "Compute one with POST /api/v1/changes/compute (both builds have to be readable for that).",
                Status = StatusCodes.Status404NotFound,
                Extensions = { { "from", fromBuild! }, { "to", toBuild! } }
            });
        }

        var entries = diff.Entries.AsEnumerable();

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
        [FromQuery] bool unloadWhenDone = true)
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
            Math.Clamp(maxEntries, 1, 500000), Math.Clamp(maxHashFiles, 1, 200000), unloadWhenDone);

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
