using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace FortnitePorting.Controllers;

/// <summary>
/// Reads files out of a specific Fortnite build instead of only the newest one.
/// <para>
/// Every build this instance has served keeps its manifest archived, and a manifest is all it takes to
/// read that build again: the pak content itself is streamed from the Epic CDN, never copied here. So
/// the previous build and the live build can both be read, and any build whose manifest is still
/// retained can be brought back with <c>POST /api/v1/versions/load</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/versions")]
public sealed class VersionsController : ControllerBase
{
    private readonly BuildHistoryStore _store;
    private readonly HistoricalBuildService _historical;
    private readonly BuildDiffService _diffs;
    private readonly ManifestService _manifestService;
    private readonly IFileProvider _liveProvider;

    public VersionsController(BuildHistoryStore store, HistoricalBuildService historical,
        BuildDiffService diffs, ManifestService manifestService, IFileProvider liveProvider)
    {
        _store = store;
        _historical = historical;
        _diffs = diffs;
        _manifestService = manifestService;
        _liveProvider = liveProvider;
    }

    /// <summary>
    /// Lists every build this instance knows about and whether it can still be read.
    /// </summary>
    [HttpGet]
    public IActionResult GetVersions()
    {
        var loaded = _historical.Loaded.ToDictionary(x => x.BuildVersion, StringComparer.OrdinalIgnoreCase);
        var liveBuild = _manifestService.GameBuild;

        var builds = _store.GetBuilds().Select(build =>
        {
            var isLive = string.Equals(build.BuildVersion, liveBuild, StringComparison.OrdinalIgnoreCase);
            loaded.TryGetValue(build.BuildVersion, out var mounted);

            return new
            {
                build = build.BuildVersion,
                version = build.Version,
                changelist = build.Changelist,
                manifestId = build.ManifestId,
                archivedUtc = build.ArchivedUtc,
                isLive,
                // "Readable" means a request naming this build can be answered: the live build always,
                // an archived one for as long as the retention policy keeps its manifest.
                readable = isLive || build.HasManifest,
                loaded = isLive || mounted != null,
                manifestBytes = build.ManifestBytes,
                prunedUtc = build.PrunedUtc,
                files = isLive ? _liveProvider.Files.Count : mounted?.Provider.Files.Count,
                lastUsedUtc = mounted?.LastUsedUtc
            };
        }).ToList();

        return Ok(new
        {
            liveBuild,
            previousBuild = _manifestService.PreviousBuildVersion,
            retainedBuilds = _store.RetainedBuilds,
            maxLoadedHistorical = _historical.MaxLoaded,
            idleUnloadMinutes = _historical.IdleTimeout.TotalMinutes,
            historyDirectory = _store.Directory_,
            builds
        });
    }

    /// <summary>
    /// Adds a build to the archive from a manifest file, so a build this instance never served itself
    /// becomes readable and comparable. The manifest can be uploaded, or read from a path on the server.
    /// </summary>
    /// <param name="file">Manifest file to upload (multipart/form-data).</param>
    /// <param name="path">Alternatively, the path of a manifest file already on the server.</param>
    [HttpPost("import")]
    [RequestSizeLimit(256L * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file, [FromQuery] string? path = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        string source;

        if (file is { Length: > 0 })
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
            source = file.FileName;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            if (!System.IO.File.Exists(path))
            {
                return NotFound(new ProblemDetails
                {
                    Title = "マニフェストファイルが見つかりません",
                    Detail = $"No file at '{path}'.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
            source = path;
        }
        else
        {
            return BadRequest(new ProblemDetails
            {
                Title = "マニフェストが必要です",
                Detail = "Upload the manifest as 'file', or pass the server-side 'path' of one.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            // Parsing before archiving means a file that is not a manifest is rejected here rather
            // than at mount time, and the build version comes from the manifest itself rather than
            // from a file name that may have been renamed.
            var (buildVersion, manifestId) = _historical.InspectManifest(bytes);
            var archived = _store.Archive(buildVersion, manifestId, bytes);

            return Ok(new
            {
                build = archived.BuildVersion,
                version = archived.Version,
                changelist = archived.Changelist,
                manifestId = archived.ManifestId,
                manifestBytes = archived.ManifestBytes,
                source,
                hasArchivedKeys = _store.LoadKeys(buildVersion).Count > 0,
                message = _store.LoadKeys(buildVersion).Count > 0
                    ? "The build was imported and can be mounted with POST /api/v1/versions/load."
                    : "The build was imported. No AES keys are archived for it, so mounting falls back to " +
                      "the live keys; paks encrypted with a key Fortnite has since rotated will stay unmounted."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "マニフェストを読み込めません",
                Detail = $"'{source}' could not be parsed as a Fortnite manifest: {ex.Message}",
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    /// <summary>
    /// Mounts an archived build so its files can be read alongside the live one.
    /// </summary>
    /// <param name="version">Build to mount, e.g. <c>++Fortnite+Release-42.10-CL-57566230-Windows</c>, <c>42.10</c>, or <c>previous</c>.</param>
    [HttpPost("load")]
    public async Task<IActionResult> Load([FromQuery] string version, CancellationToken cancellationToken)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return UnknownBuild(version);
        }

        if (_diffs.IsLive(resolved))
        {
            return Ok(new { build = resolved, loaded = true, isLive = true, message = "This build is the live one; it is always loaded." });
        }

        try
        {
            var loaded = await _historical.LoadAsync(resolved, cancellationToken);
            return Ok(new
            {
                build = loaded.BuildVersion,
                loaded = true,
                isLive = false,
                files = loaded.Provider.Files.Count,
                mountedVfs = loaded.Provider.MountedVfs.Count,
                keysStillRequired = loaded.Provider.RequiredKeys.Count,
                loadSeconds = Math.Round(loaded.LoadSeconds, 1)
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ProblemDetails
            {
                Title = "アーカイブされたビルドがありません",
                Detail = ex.Message,
                Status = StatusCodes.Status404NotFound
            });
        }
    }

    /// <summary>
    /// Unmounts an archived build and frees its memory. Its archived manifest is kept, so it can be
    /// mounted again later.
    /// </summary>
    /// <param name="version">Build to unmount.</param>
    [HttpDelete("unload")]
    public IActionResult Unload([FromQuery] string version)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return UnknownBuild(version);
        }

        if (_diffs.IsLive(resolved))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "ライブビルドはアンロードできません",
                Detail = "The live build cannot be unmounted; use POST /api/v1/build/reload to move to another build.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(new { build = resolved, unloaded = _historical.Unload(resolved) });
    }

    /// <summary>
    /// Deletes an archived build's data — its manifest, its archived keys and its chunk cache. This is
    /// the "delete the old version" step of an update; the changelists recorded for that build survive,
    /// so its history stays queryable even though its files no longer are.
    /// </summary>
    /// <param name="version">Build whose archived data is deleted.</param>
    [HttpDelete("data")]
    public IActionResult DeleteData([FromQuery] string version)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return UnknownBuild(version);
        }

        if (_diffs.IsLive(resolved))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "ライブビルドは削除できません",
                Detail = "The live build's data cannot be deleted while it is being served.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        _historical.Unload(resolved);
        var deleted = _store.PruneBuildData(resolved);

        return Ok(new
        {
            build = resolved,
            deleted,
            message = deleted
                ? "The archived data was deleted. Recorded changelists for this build were kept."
                : "This build had no archived data left to delete."
        });
    }

    /// <summary>
    /// Lists the virtual file paths of one build.
    /// </summary>
    /// <param name="version">Build to list, e.g. <c>++Fortnite+Release-42.10-CL-57566230-Windows</c> or <c>previous</c>.</param>
    /// <param name="q">Optional case-insensitive path filter.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Paths per page, from 1 to 10000.</param>
    /// <param name="load">Mount the build when it is archived but not loaded. Default false.</param>
    [HttpGet("files")]
    public async Task<IActionResult> GetFiles([FromQuery] string version, [FromQuery] string? q = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 1000, [FromQuery] bool load = false,
        CancellationToken cancellationToken = default)
    {
        var (lease, error) = await ResolveProviderAsync(version, load, cancellationToken);
        if (error != null)
        {
            return error;
        }

        using var borrowed = lease!;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 10000);

        var paths = borrowed.Provider.Files.Keys
            .Where(p => q == null || p.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            build = _diffs.ResolveBuildVersion(version),
            query = q,
            totalFiles = paths.Count,
            totalPages = (int)Math.Ceiling(paths.Count / (double)pageSize),
            currentPage = page,
            pageSize,
            files = paths.Skip((page - 1) * pageSize).Take(pageSize).ToList()
        });
    }

    /// <summary>
    /// Returns one file's content as it is in a specific build.
    /// </summary>
    /// <param name="version">Build to read from, e.g. <c>++Fortnite+Release-42.10-CL-57566230-Windows</c>, <c>42.10</c>, <c>previous</c> or <c>latest</c>.</param>
    /// <param name="path">Virtual file path, with or without its extension.</param>
    /// <param name="raw">Return the stored bytes instead of the JSON export. Default false.</param>
    /// <param name="load">Mount the build when it is archived but not loaded. Default false, so a request naming an unloaded build is refused rather than triggering a multi-minute mount.</param>
    [HttpGet("file")]
    public async Task<IActionResult> GetFile([FromQuery] string version, [FromQuery] string path,
        [FromQuery] bool raw = false, [FromQuery] bool load = false, CancellationToken cancellationToken = default)
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

        var (lease, error) = await ResolveProviderAsync(version, load, cancellationToken);
        if (error != null)
        {
            return error;
        }

        using var borrowed = lease!;

        var resolvedBuild = _diffs.ResolveBuildVersion(version)!;
        var result = VersionedAssetReader.Read(borrowed.Provider, path, raw);

        if (!result.Found)
        {
            return NotFound(new ProblemDetails
            {
                Title = "そのビルドにファイルがありません",
                Detail = $"'{path}' does not exist in build '{resolvedBuild}'.",
                Status = StatusCodes.Status404NotFound,
                Extensions = { { "build", resolvedBuild }, { "requestedPath", path } }
            });
        }

        Response.Headers["X-Build-Version"] = resolvedBuild;
        Response.Headers["X-Build-Is-Live"] = _diffs.IsLive(resolvedBuild) ? "true" : "false";

        if (raw && result.Bytes != null)
        {
            return File(result.Bytes, VersionedAssetReader.ContentTypeFor(result.ResolvedPath),
                Path.GetFileName(result.ResolvedPath));
        }

        return result.Kind switch
        {
            "package" => Content(
                JsonConvert.SerializeObject(result.Json, Formatting.Indented,
                    new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }),
                "application/json; charset=utf-8"),

            "text" => Content(result.Text!, "text/plain; charset=utf-8"),

            "binary" => File(result.Bytes!, VersionedAssetReader.ContentTypeFor(result.ResolvedPath),
                Path.GetFileName(result.ResolvedPath)),

            _ => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "ファイルを読み取れませんでした",
                Detail = result.Error ?? "The file could not be read from this build.",
                Status = StatusCodes.Status502BadGateway,
                Extensions = { { "build", resolvedBuild }, { "resolvedPath", result.ResolvedPath } }
            })
        };
    }

    /// <summary>
    /// Resolves the build parameter to a mounted provider, mounting it first when
    /// <paramref name="load"/> is set. Returns the error response to send when it cannot be served.
    /// </summary>
    private async Task<(BuildLease? Lease, IActionResult? Error)> ResolveProviderAsync(
        string version, bool load, CancellationToken cancellationToken)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return (null, UnknownBuild(version));
        }

        // These routes bypass the global reload gate so archived builds stay readable during an
        // update, which means the live provider has to be checked here instead: it is being torn
        // down and re-registered, so nothing may read from it.
        if (_diffs.IsLive(resolved) && ProviderReloadGate.Instance.IsReloading)
        {
            return (null, ReloadingResponse(resolved));
        }

        if (_diffs.TryLease(resolved, out var lease))
        {
            return (lease, null);
        }

        if (!load)
        {
            return (null, StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
            {
                Title = "そのビルドは読み込まれていません",
                Detail = _historical.CanLoad(resolved)
                    ? $"Build '{resolved}' is archived but not loaded. Re-send with load=true, or mount it first with POST /api/v1/versions/load?version={Uri.EscapeDataString(resolved)}."
                    : $"Build '{resolved}' has no archived data left; its files cannot be read. Its recorded changelists are still available under /api/v1/changes.",
                Status = StatusCodes.Status409Conflict,
                Extensions = { { "build", resolved }, { "loadable", _historical.CanLoad(resolved) } }
            }));
        }

        try
        {
            return (await _historical.LeaseAsync(resolved, cancellationToken), null);
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

    private IActionResult ReloadingResponse(string build)
    {
        Response.Headers.RetryAfter = "30";
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
        {
            Title = "ライブビルドを再読み込み中です",
            Detail = $"Build '{build}' is the live one and is being reloaded right now. Retry shortly, " +
                     "or name an archived build instead.",
            Status = StatusCodes.Status503ServiceUnavailable,
            Extensions = { { "status", ProviderReloadGate.Instance.State } }
        });
    }

    private IActionResult UnknownBuild(string version) => NotFound(new ProblemDetails
    {
        Title = "ビルドが見つかりません",
        Detail = $"No known build matches '{version}'. Call GET /api/v1/versions to see what this instance has.",
        Status = StatusCodes.Status404NotFound,
        Extensions = { { "known", _diffs.KnownBuildVersions() } }
    });
}
