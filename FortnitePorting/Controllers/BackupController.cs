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
    /// <param name="includePayloads">Include payload files (.uexp/.ubulk/.uptnl); FModel excludes them.</param>
    [HttpGet]
    public IActionResult GetInfo([FromQuery] bool includePayloads = false)
    {
        var entries = CollectEntries(includePayloads);
        return Ok(new
        {
            fileName = BuildFileName(),
            magic = "FBKP",
            version = FbkpBackupWriter.Version,
            includePayloads,
            entryCount = entries.Count,
            totalFiles = _provider.Files.Count,
            build = MountedBuild(),
            downloadUrl = Url.Action(nameof(Download), "Backup", new { includePayloads })
        });
    }

    /// <summary>
    /// Builds and returns the <c>.fbkp</c> backup of the mounted build. The file is named after that
    /// build (<c>FortniteGame_42_00.fbkp</c>), which is what distinguishes one backup from another.
    /// </summary>
    /// <param name="includePayloads">Include payload files (.uexp/.ubulk/.uptnl); FModel excludes them.</param>
    /// <param name="compress">Write the LZ4 frame FModel produces (default). False writes the plain body,
    /// which FModel also accepts because it sniffs the LZ4 magic before decoding.</param>
    /// <param name="cancellationToken">Request cancellation state.</param>
    [HttpGet("fbkp")]
    public async Task<IActionResult> Download(
        [FromQuery] bool includePayloads = false,
        [FromQuery] bool compress = true,
        CancellationToken cancellationToken = default)
    {
        var entries = CollectEntries(includePayloads);
        var fileName = BuildFileName();

        _logger.LogInformation("Serving backup {FileName} with {EntryCount} entries (compress={Compress})",
            fileName, entries.Count, compress);

        // The format is written with a BinaryWriter, and Kestrel rejects synchronous writes to the
        // response body, so the backup is produced into a temp file and streamed back from there.
        // A file rather than a MemoryStream keeps a backup of several hundred thousand entries off
        // the heap, and it gives the response a Content-Length so clients can show progress.
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                FbkpBackupWriter.Write(temp, entries, compress, cancellationToken);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        Response.Headers["X-Backup-Entries"] = entries.Count.ToString();
        Response.Headers["X-Backup-Version"] = FbkpBackupWriter.Version.ToString();

        // DeleteOnClose: the result disposes the stream once the body has been written, and the file
        // goes with it whether the transfer succeeded or the client walked away.
        var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return File(stream, "application/octet-stream", fileName);
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
    /// Names the backup after the build it describes: <c>FortniteGame_42_00.fbkp</c>. Falls back to
    /// FModel's date-based name only when the build version is not known yet.
    /// </summary>
    private string BuildFileName()
    {
        var suffix = ShortVersion(MountedBuild());
        if (string.IsNullOrEmpty(suffix))
        {
            suffix = DateTime.Now.ToString("MM'_'dd'_'yyyy");
        }

        return $"FortniteGame_{suffix}.fbkp";
    }

    /// <summary>
    /// The build whose archives are actually mounted. It can lag the manifest while a reload is
    /// pending, and the backup describes what is mounted, not what is about to be.
    /// </summary>
    private string MountedBuild() =>
        !string.IsNullOrWhiteSpace(_manifestService.AppliedBuildVersion)
            ? _manifestService.AppliedBuildVersion
            : _manifestService.GameBuild;

    /// <summary>
    /// Reduces "++Fortnite+Release-42.00-CL-56878558-Windows" to "42_00", matching how
    /// ManifestService pulls the release number out of the build string.
    /// </summary>
    private static string ShortVersion(string? buildVersion)
    {
        if (string.IsNullOrWhiteSpace(buildVersion)) return string.Empty;

        var parts = buildVersion.Split('-');
        var version = parts.Length > 2 ? parts[1] : buildVersion;

        version = version.Trim().Replace('.', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            version = version.Replace(invalid, '_');
        }
        return version;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch
        {
            // A leftover temp file is not worth failing the request over.
        }
    }
}
