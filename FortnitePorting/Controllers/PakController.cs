using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// Public, paginated information about the PAK/UTOC archives currently mounted in this local API process.
/// </summary>
[ApiController]
[Route("api/v1/paks")]
public sealed class PakController : ControllerBase
{
    private readonly IFileProvider _provider;

    public PakController(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Lists mounted PAK/UTOC archives.</summary>
    /// <param name="q">Optional case-insensitive filter for archive name or path.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of archives per page, from 1 to 200.</param>
    [HttpGet]
    public IActionResult GetPaks([FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (_provider is not AbstractVfsFileProvider vfsProvider)
        {
            return BadRequest(new { message = "The configured provider is not a VFS provider." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var all = vfsProvider.MountedVfs
            .Where(x => q == null || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        x.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(x => new
            {
                name = x.Name,
                fileCount = x.FileCount,
                path = x.Path
            })
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = all.Count;
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        var paks = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(new
        {
            query = q,
            totalPaks = total,
            totalPages,
            currentPage = page,
            pageSize,
            paks
        });
    }

    /// <summary>Lists files contained in one mounted PAK/UTOC archive.</summary>
    /// <param name="pakName">Archive name, file name without extension, or an unambiguous name fragment.</param>
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

        var readers = vfsProvider.MountedVfs
            .Where(x => string.Equals(x.Name, query, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(x.Name), query, StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (readers.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Title = "PAKが見つかりません",
                Detail = $"指定されたPAK/UTOC '{pakName}' に一致するアーカイブがありません。",
                Status = StatusCodes.Status404NotFound
            });
        }

        var files = readers
            .SelectMany(x => x.Files.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            files = files.Skip((page - 1) * pageSize).Take(pageSize).ToList()
        });
    }
}
