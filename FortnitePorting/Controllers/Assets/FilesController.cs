using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

[ApiController]
[VersionAware]
[Route("api/v1/files")]
public sealed class FilesController(RequestBuildProvider build) : ControllerBase
{
    /// <summary>Lists virtual files, optionally filtering item-name prefixes and extensions.</summary>
    [HttpGet]
    public IActionResult GetFiles(
        [FromQuery] string? prefixes = null,
        [FromQuery] string? excludePrefixes = null,
        [FromQuery] string? excludePaths = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 1000,
        [FromQuery] string? ext = null)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 10000);

        var prefixList = ParseList(prefixes);
        var excludePrefixList = ParseList(excludePrefixes);
        var excludePathList = ParseList(excludePaths);

        var matched = FileIndex.For(build.Provider).MatchingNames(prefixList, ext ?? string.Empty, excludePrefixList, excludePathList);

        var total = matched.Count;
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        var paged = PageSlice.From(matched, page, pageSize);

        return Ok(new
        {
            prefixes = prefixList,
            excludePrefixes = excludePrefixList,
            excludePaths = excludePathList,
            extension = string.IsNullOrEmpty(ext) ? "(all)" : ext,
            totalFiles = total,
            totalPages,
            currentPage = page,
            pageSize,
            files = paged
        });
    }

    private static string[] ParseList(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
