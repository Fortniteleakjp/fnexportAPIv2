using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FortnitePorting.Controllers;

/// <summary>
/// Serves an FModel backup (<c>.fbkp</c>) built from the file list of the currently mounted build,
/// so FModel can diff a later build against this one ("All But New" / "All But Modified").
/// </summary>
[ApiController]
[Route("api/v1/backup")]
public sealed class BackupController : ControllerBase
{
    private readonly IFileProvider _provider;
    private readonly ManifestService _manifestService;
    private readonly ILogger<BackupController> _logger;

    public BackupController(IFileProvider provider, ManifestService manifestService, ILogger<BackupController> logger)
    {
        _provider = provider;
        _manifestService = manifestService;
        _logger = logger;
    }

    /// <summary>
    /// Reports what the generated backup would contain, without producing it.
    /// </summary>
    /// <param name="name">Base name used for the suggested file name (default FortniteGame).</param>
    /// <param name="includePayloads">Include payload files (.uexp/.ubulk/.uptnl); FModel excludes them.</param>
    [HttpGet]
    public IActionResult GetInfo([FromQuery] string? name = null, [FromQuery] bool includePayloads = false)
    {
        var entries = CollectEntries(includePayloads);
        return Ok(new
        {
            fileName = BuildFileName(name),
            magic = "FBKP",
            version = FbkpBackupWriter.Version,
            includePayloads,
            entryCount = entries.Count,
            totalFiles = _provider.Files.Count,
            build = _manifestService.AppliedBuildVersion ?? _manifestService.GameBuild,
            downloadUrl = Url.Action(nameof(Download), "Backup", new { name, includePayloads })
        });
    }

    /// <summary>
    /// Builds and streams the <c>.fbkp</c> backup of the mounted build.
    /// </summary>
    /// <param name="name">Base name used for the file name (default FortniteGame).</param>
    /// <param name="includePayloads">Include payload files (.uexp/.ubulk/.uptnl); FModel excludes them.</param>
    /// <param name="compress">Write the LZ4 frame FModel produces (default). False writes the plain body,
    /// which FModel also accepts because it sniffs the LZ4 magic before decoding.</param>
    /// <param name="cancellationToken">Request cancellation state.</param>
    [HttpGet("fbkp")]
    public async Task Download(
        [FromQuery] string? name = null,
        [FromQuery] bool includePayloads = false,
        [FromQuery] bool compress = true,
        CancellationToken cancellationToken = default)
    {
        var entries = CollectEntries(includePayloads);
        var fileName = BuildFileName(name);

        _logger.LogInformation("Serving backup {FileName} with {EntryCount} entries (compress={Compress})",
            fileName, entries.Count, compress);

        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        // The compressed length is unknown up front, so the body is streamed instead of buffered.
        Response.Headers["X-Backup-Entries"] = entries.Count.ToString();
        Response.Headers["X-Backup-Version"] = FbkpBackupWriter.Version.ToString();

        FbkpBackupWriter.Write(Response.Body, entries, compress, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Snapshots the files to back up. Taking the list up front keeps the count in the header and the
    /// records that follow consistent even if the provider is rebuilt while the response streams.
    /// </summary>
    private List<GameFile> CollectEntries(bool includePayloads)
    {
        var entries = new List<GameFile>(_provider.Files.Count);
        foreach (var file in _provider.Files.Values)
        {
            if (!includePayloads && file.IsUePackagePayload) continue;
            entries.Add(file);
        }
        return entries;
    }

    /// <summary>
    /// FModel's own naming scheme: <c>{game}_{MM_dd_yyyy}.fbkp</c>.
    /// </summary>
    private static string BuildFileName(string? name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "FortniteGame" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalid, '_');
        }
        if (baseName.Length == 0) baseName = "FortniteGame";

        return $"{baseName}_{DateTime.Now:MM'_'dd'_'yyyy}.fbkp";
    }
}
