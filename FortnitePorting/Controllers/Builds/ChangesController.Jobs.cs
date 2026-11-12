using FortnitePorting.Models;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

public sealed partial class ChangesController
{
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
}
