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
public sealed partial class ChangesController : ControllerBase
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
            changes = PageSlice.From(filtered, page, pageSize).Select(e => new
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
}
