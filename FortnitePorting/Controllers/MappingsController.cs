using System.Text.RegularExpressions;
using FortnitePorting.Services;
using FortnitePorting.Services.Mappings;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

[ApiController]
[Route("api/v1/mappings")]
public class MappingsController(ManifestService manifestService, ILogger<MappingsController> logger) : ControllerBase
{
    [HttpPost("generate")]
    [HttpPost("dump")]
    [HttpPost("dump/uefn")]
    [HttpPost("dump/local")]
    public async Task<IActionResult> Generate(
        [FromQuery] string? dir = null,
        [FromQuery] string compression = "zstd",
        [FromQuery] int? level = null,
        [FromQuery] string? oodle = null,
        [FromQuery] string? fileName = null,
        [FromQuery] int timeoutSeconds = 120,
        [FromQuery] bool load = false,
        [FromQuery] bool download = true,
        CancellationToken cancellationToken = default)
    {
        string[] retiredParameters = ["url", "path", "maxPackages", "merge", "baseMapping", "version", "pid",
            "console", "gobjects", "fnameToString", "probeSignatures", "key", "scan", "deep", "api", "verify"];
        if (retiredParameters.Any(Request.Query.ContainsKey))
            return BadRequest(new { message = "The old generation parameters have been removed. Use dir to select installed UEFN DLLs." });
        try
        {
            if (load && OperatingSystem.IsWindows())
            {
                var directory = StaticMappingsGenerator.BinariesDirectory(dir);
                if (StaticMappingsGenerator.MissingFiles(directory).Length == 0 &&
                    !MatchesCurrentBuild(StaticMappingsGenerator.ReadBuild(directory)))
                    return Conflict(new { message = "The UEFN installation does not match the mounted build. Generate with load=false." });
            }

            var result = await new StaticMappingsGenerator().GenerateAsync(
                new(dir, compression.Trim().ToLowerInvariant(), level, oodle, fileName, timeoutSeconds), cancellationToken);

            if (load)
            {
                if (!MatchesCurrentBuild(result.Build))
                    return Conflict(new { message = "The mounted build changed during generation. The mapping was saved but not loaded.", output = result.FilePath });
                manifestService.ApplyMapping(result.FilePath);
            }

            logger.LogInformation("Generated {File}: {Structs} structs, {Enums} enums in {Seconds:F1}s",
                result.FileName, result.Structs, result.Enums, result.ElapsedSeconds);
            if (download)
            {
                SetHeaders(result.Usmap, result.FilePath, load, result.Structs, result.Enums);
                Response.Headers["X-Usmap-Source"] = result.Source;
                Response.Headers["X-Usmap-Build"] = result.Build;
                return File(result.Usmap, "application/octet-stream", result.FileName);
            }

            return Ok(new
            {
                fileName = result.FileName, output = result.FilePath, source = result.Source,
                build = result.Build, usmapBytes = result.Usmap.Length, compression,
                structs = result.Structs, enums = result.Enums, elapsedSeconds = result.ElapsedSeconds,
                loaded = load, downloadUrl = DownloadUrl(result.FileName)
            });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { message = "Mapping generation was cancelled." }); }
        catch (PlatformNotSupportedException ex) { return StatusCode(501, new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (FileNotFoundException ex) { return StatusCode(424, new { message = ex.Message }); }
        catch (DirectoryNotFoundException ex) { return StatusCode(424, new { message = ex.Message }); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        catch (System.Text.Json.JsonException ex) { return BadRequest(new { message = "Invalid UEFN version file.", error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (TimeoutException ex) { return StatusCode(504, new { message = ex.Message }); }
        catch (StaticMappingsGenerator.GenerationException ex)
        {
            logger.LogError(ex, "Mapping generation failed");
            return StatusCode(502, new { message = ex.Message, log = ex.Log });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mapping generation failed");
            return StatusCode(500, new { message = "Mapping generation failed.", error = ex.Message });
        }
    }

    [HttpGet("uefn")]
    public IActionResult GetUefnStatus([FromQuery] string? dir = null)
    {
        try
        {
            var supported = OperatingSystem.IsWindows() && Environment.Is64BitProcess;
            var executable = StaticMappingsGenerator.FindExecutable();
            var directory = StaticMappingsGenerator.BinariesDirectory(dir);
            var missing = StaticMappingsGenerator.MissingFiles(directory);
            string? build = null, versionError = null;
            if (missing.Length == 0)
            {
                try { build = StaticMappingsGenerator.ReadBuild(directory); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
                { versionError = ex.Message; }
            }
            return Ok(new
            {
                supported, executableFound = executable != null, executablePath = executable,
                binariesDirectory = directory, missingFiles = missing, build, versionError,
                ready = supported && executable != null && missing.Length == 0 && build != null,
                hint = !supported ? "Mapping generation requires 64-bit Windows."
                    : executable == null ? "Run MappingsGenerator\\build.bat or set USMAP_GENERATOR_PATH."
                    : missing.Length > 0 ? "Set dir or UEFN_BINARIES_DIR to the installed UEFN Binaries\\Win64 directory."
                    : build == null ? "Check the UEFN .version file."
                    : "POST /api/v1/mappings/generate to create a mapping."
            });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromQuery] string path,
        [FromQuery] string? fileName = null,
        [FromQuery] bool load = false,
        [FromQuery] bool download = false,
        CancellationToken cancellationToken = default)
    {
        if (!System.IO.File.Exists(path)) return NotFound(new { message = $"File not found: {path}" });
        try
        {
            var name = MappingStore.FileName(fileName ?? Path.GetFileName(path));
            var data = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
            int structs, enums;
            try { (structs, enums) = MappingStore.Verify(data, name); }
            catch (Exception ex) { return BadRequest(new { message = "The file is not a readable .usmap.", error = ex.Message }); }
            var output = await MappingStore.SaveAsync(data, name, cancellationToken);
            if (load) manifestService.ApplyMapping(output);
            if (download)
            {
                SetHeaders(data, output, load, structs, enums);
                return File(data, "application/octet-stream", name);
            }
            return Ok(new
            {
                source = path, fileName = name, output, downloadUrl = DownloadUrl(name),
                usmapBytes = data.Length, loaded = load, verification = new { structs, enums }
            });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (OperationCanceledException) { return StatusCode(499, new { message = "Mapping import was cancelled." }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mapping import failed");
            return StatusCode(500, new { message = "Mapping import failed.", error = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult ListMappings()
    {
        var files = MappingStore.List().Select(file => new
        {
            fileName = file.Name, size = file.Length, modifiedUtc = file.LastWriteTimeUtc,
            downloadUrl = DownloadUrl(file.Name)
        }).ToList();
        return Ok(new { count = files.Count, directory = MappingStore.DirectoryPath, files });
    }

    [HttpGet("{fileName}")]
    public IActionResult DownloadMapping([FromRoute] string fileName)
    {
        var file = MappingStore.Find(fileName);
        return file == null ? NotFound(new { message = $"No stored mapping named '{fileName}'." })
            : PhysicalFile(file.FullName, "application/octet-stream", file.Name);
    }

    private bool MatchesCurrentBuild(string build)
    {
        var current = string.IsNullOrWhiteSpace(manifestService.AppliedBuildVersion)
            ? manifestService.GameBuild : manifestService.AppliedBuildVersion;
        var match = Regex.Match(current ?? "", @"^(.+-CL-\d+)(?:-|$)");
        return match.Success && string.Equals(build, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }

    private string? DownloadUrl(string name)
        => Url.Action(nameof(DownloadMapping), "Mappings", new { fileName = name });

    private void SetHeaders(byte[] data, string output, bool loaded, int structs, int enums)
    {
        Response.Headers["X-Usmap-Bytes"] = data.Length.ToString();
        Response.Headers["X-Usmap-Output"] = output;
        Response.Headers["X-Usmap-Loaded"] = loaded ? "true" : "false";
        Response.Headers["X-Usmap-Structs"] = structs.ToString();
        Response.Headers["X-Usmap-Enums"] = enums.ToString();
    }
}
