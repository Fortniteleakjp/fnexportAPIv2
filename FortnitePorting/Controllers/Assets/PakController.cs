using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using CUE4Parse.FileProvider.Vfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// Public, paginated information about the PAK/UTOC archives currently mounted in this local API process.
/// </summary>
[ApiController]
[VersionAware]
[Route("api/v1/paks")]
public sealed class PakController : ControllerBase
{
    private readonly RequestBuildProvider _build;
        // Read lazily: the version filter binds the provider after controller construction.
    private IFileProvider _provider => _build.Provider;

    /// <summary>Cache-key prefix that keeps an older build's content out of the live cache.</summary>
    private string _scope => _build.CacheScope;

    public PakController(RequestBuildProvider provider)
    {
        _build = provider;
    }

    /// <summary>Lists mounted PAK/UTOC archives.</summary>
    /// <param name="q">Optional case-insensitive filter for archive name or path.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of archives per page, from 1 to 200.</param>
    [HttpGet]
    public IActionResult GetPaks([FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string state = "mounted")
    {
        if (_provider is not AbstractVfsFileProvider vfsProvider)
        {
            return BadRequest(new { message = "The configured provider is not a VFS provider." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        state = (state ?? "mounted").Trim().ToLowerInvariant();
        IEnumerable<CUE4Parse.UE4.VirtualFileSystem.IAesVfsReader> readers = state switch
        {
            "mounted" => vfsProvider.MountedVfs,
            "unloaded" => vfsProvider.UnloadedVfs,
            "all" => ArchiveCatalog.All(vfsProvider),
            _ => []
        };
        if (state is not ("mounted" or "unloaded" or "all"))
            return BadRequest(new { message = "state must be mounted, unloaded or all." });
        var mountedPaths = vfsProvider.MountedVfs.Select(reader => reader.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = ArchiveCatalog.Match(readers, q).Select(reader =>
        {
            var metadata = ArchiveCatalog.Metadata(reader, vfsProvider, mountedPaths.Contains(reader.Path));
            return new
            {
                name = reader.Name, fileCount = reader.FileCount, path = reader.Path,
                metadata.Length, metadata.MountPoint, metadata.IsEncrypted, metadata.IsEnabled,
                metadata.IsLooseFilesContainer, metadata.Key, metadata.Guid, metadata.CompressionMethods
            };
        }).OrderBy(reader => reader.name, StringComparer.OrdinalIgnoreCase).ToList();

        var total = all.Count;
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        var paks = PageSlice.From(all, page, pageSize);

        return Ok(new
        {
            query = q,
            state,
            totalPaks = total,
            totalPages,
            currentPage = page,
            pageSize,
            paks
        });
    }

    /// <summary>Lists files contained in one mounted PAK/UTOC archive.</summary>
    /// <param name="pakName">Archive name, file stem, name fragment, or chunk number.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of files per page, from 1 to 10000.</param>
    [HttpGet("{pakName}/files")]
    public IActionResult GetFilesInPak(string pakName, [FromQuery] int page = 1, [FromQuery] int pageSize = 1000)
    {
        if (_provider is not AbstractVfsFileProvider vfsProvider)
        {
            return BadRequest(new { message = "The configured provider is not a VFS provider." });
        }

        if (string.IsNullOrWhiteSpace(pakName))
        {
            return BadRequest(new { message = "pakName is required." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 10000);
        var query = pakName.Trim();

        var readers = ArchiveCatalog.Match(vfsProvider.MountedVfs, query).ToList();

        if (readers.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Title = "PAKが見つかりません",
                Detail = $"指定されたPAK/UTOC '{pakName}' に一致するアーカイブがありません。",
                Status = StatusCodes.Status404NotFound
            });
        }

        var files = ArchiveFileIndex.For(readers);
        var total = files.Count;
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        return Ok(new
        {
            query,
            matchedPaks = readers.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            totalFiles = total,
            totalPages,
            currentPage = page,
            pageSize,
            files = PageSlice.From(files, page, pageSize)
        });
    }
}
