using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using FortnitePorting.Models;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;

namespace FortnitePorting.Controllers;

/// <summary>
/// The archive of Fortnite builds this instance can still read: what is kept, what is mounted, and
/// what gets deleted.
/// <para>
/// Every build served keeps its manifest archived, and a manifest is all it takes to read that build
/// again: the pak content itself is streamed from the Epic CDN, never copied here. Reading files is
/// not done here — the ordinary read endpoints (<c>/api/v1/export</c>, <c>/api/v1/search</c>,
/// <c>/api/v1/paks</c>, <c>/api/v1/localization</c>, <c>/api/v1/pak</c> and <c>/api/v1/config</c>)
/// take a <c>version</c> parameter and serve any build that is mounted.
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
    /// Returns the AES keys archived for a build. A build with no keys can only be mounted with the
    /// live build's keys, which decrypt nothing once Fortnite has rotated them.
    /// </summary>
    /// <param name="version">Build to inspect.</param>
    [HttpGet("keys")]
    public IActionResult GetKeys([FromQuery] string version)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return UnknownBuild(version);
        }

        var keys = _store.LoadKeys(resolved);
        return Ok(new
        {
            build = resolved,
            count = keys.Count,
            keys,
            message = keys.Count > 0
                ? "These keys are submitted when the build is mounted."
                : "No keys are archived for this build. It was imported rather than served by this instance, " +
                  "so mounting falls back to the live build's keys and every pak encrypted with a rotated key " +
                  "stays locked. Supply them with POST /api/v1/versions/keys."
        });
    }

    /// <summary>
    /// Supplies the AES keys of an archived build.
    /// <para>
    /// Fortnite rotates its keys every build and the live key APIs only publish the current ones, so a
    /// build whose manifest was imported has no way to obtain its own keys. Without them its paks stay
    /// locked, it exposes a fraction of its files, and comparing it reports most of the game as changed.
    /// </para>
    /// </summary>
    /// <param name="version">Build the keys belong to.</param>
    /// <param name="request">The main key and/or per-pak keys, or a raw GUID → key map.</param>
    /// <param name="merge">Keep keys already archived for this build and add to them. Default true.</param>
    [HttpPost("keys")]
    public IActionResult SetKeys([FromQuery] string version, [FromBody] ArchivedKeysRequest request,
        [FromQuery] bool merge = true)
    {
        var resolved = _diffs.ResolveBuildVersion(version);
        if (resolved == null)
        {
            return UnknownBuild(version);
        }

        var keys = merge
            ? _store.LoadKeys(resolved)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string zeroGuid = "00000000000000000000000000000000";
        var rejected = new List<string>();

        void Add(string guid, string key)
        {
            try
            {
                // Parsing both halves here means a typo is rejected now rather than at mount time,
                // where it would look like a missing key instead of a bad one.
                _ = new FGuid(NormalizeGuid(guid));
                _ = new FAesKey(key);
                keys[NormalizeGuid(guid)] = key;
            }
            catch (Exception ex)
            {
                rejected.Add($"{guid}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request?.MainKey))
        {
            Add(zeroGuid, request.MainKey.Trim());
        }

        foreach (var dynamic in request?.DynamicKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(dynamic.Key) && !string.IsNullOrWhiteSpace(dynamic.Guid))
            {
                Add(dynamic.Guid, dynamic.Key.Trim());
            }
        }

        foreach (var (guid, key) in request?.Keys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                Add(guid, key.Trim());
            }
        }

        if (keys.Count == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "鍵が指定されていません",
                Detail = "Supply at least one key as mainKey, dynamicKeys, or a guid-to-key map in keys.",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { { "rejected", rejected } }
            });
        }

        _store.SaveKeys(resolved, keys);

        // A build that is already mounted was mounted without these keys, so it has to be dropped for
        // them to take effect.
        var wasLoaded = _historical.Unload(resolved);

        return Ok(new
        {
            build = resolved,
            archivedKeys = keys.Count,
            rejected,
            remounted = wasLoaded,
            message = wasLoaded
                ? "The keys were archived and the build was unmounted so the next mount picks them up."
                : "The keys were archived and will be submitted the next time this build is mounted."
        });
    }

    /// <summary>Accepts a GUID with or without hyphens and returns the 32-hex-digit form.</summary>
    private static string NormalizeGuid(string guid) => guid.Replace("-", string.Empty).Trim();

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

    private IActionResult UnknownBuild(string version) => NotFound(new ProblemDetails
    {
        Title = "ビルドが見つかりません",
        Detail = $"No known build matches '{version}'. Call GET /api/v1/versions to see what this instance has.",
        Status = StatusCodes.Status404NotFound,
        Extensions = { { "known", _diffs.KnownBuildVersions() } }
    });
}
