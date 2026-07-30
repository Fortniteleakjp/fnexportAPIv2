using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// Reads text-based Unreal configuration files that are already loaded into the local VFS.
/// </summary>
[ApiController]
[Route("api/v1/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly IFileProvider _provider;

    public ConfigController(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Lists loaded .ini files under Config directories.</summary>
    /// <param name="q">Optional case-insensitive filter for file name or path.</param>
    [HttpGet("files")]
    public IActionResult GetFiles([FromQuery] string? q = null)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var files = _provider.Files.Keys
            .Where(x => x.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                        x.Contains("Config/", StringComparison.OrdinalIgnoreCase))
            .Where(x => q == null || x.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { name = Path.GetFileName(x), path = x })
            .ToList();

        return Ok(new { query = q, totalFiles = files.Count, files });
    }

    /// <summary>Finds a key value in a text .ini file and section.</summary>
    /// <param name="file">File name, for example Game.ini, or a loaded virtual path.</param>
    /// <param name="section">Section name, with or without square brackets.</param>
    /// <param name="key">Configuration key name.</param>
    [HttpGet("query")]
    public IActionResult Query([FromQuery] string? file, [FromQuery] string? section, [FromQuery] string? key)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new { message = "file, section, and key are required." });
        }

        var fileQuery = file.Trim().Replace('\\', '/');
        var filePath = _provider.Files.Keys.FirstOrDefault(x =>
            x.Equals(fileQuery, StringComparison.OrdinalIgnoreCase) ||
            (Path.GetFileName(x).Equals(fileQuery, StringComparison.OrdinalIgnoreCase) &&
             x.Contains("Config/", StringComparison.OrdinalIgnoreCase)));

        if (filePath == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "設定ファイルが見つかりません",
                Detail = $"'{file}' に一致する .ini ファイルがありません。",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (!_provider.TryCreateReader(filePath, out var reader))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                message = "設定ファイルをテキストとして読み込めません。",
                path = filePath
            });
        }

        var targetSection = section.Trim();
        if (!targetSection.StartsWith('[')) targetSection = $"[{targetSection}";
        if (!targetSection.EndsWith(']')) targetSection += "]";

        var values = new List<string>();
        var currentSection = string.Empty;
        using (reader)
        using (var textReader = new StreamReader(reader))
        {
            while (textReader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    currentSection = trimmed;
                    continue;
                }

                if (!currentSection.Equals(targetSection, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0) continue;
                var currentKey = trimmed[..separator].Trim();
                if (currentKey.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(trimmed[(separator + 1)..].Trim());
                }
            }
        }

        return Ok(new
        {
            file = Path.GetFileName(filePath),
            path = filePath,
            section = targetSection,
            key = key.Trim(),
            found = values.Count > 0,
            values,
            value = values.LastOrDefault()
        });
    }
}
