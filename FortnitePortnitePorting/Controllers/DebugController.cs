
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.VirtualFileCache.Manifest;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers
{
    [ApiController]
    [Route("api/v1/debug")]
    public class DebugController : ControllerBase
    {
        private readonly IFileProvider _provider;

        public DebugController(IFileProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Lists currently mounted files.
        /// </summary>
        [HttpGet("files")]
        public IActionResult ListFiles([FromQuery] string? filter = null)
        {
            var fileCount = _provider.Files.Count;

            var result = new
            {
                stats = new
                {
                    total_files = fileCount,
                    game_version = _provider.Versions.Game.ToString()
                },
                files = string.IsNullOrEmpty(filter)
                    ? _provider.Files.Keys.OrderBy(k => k).Take(100).ToList()
                    : _provider.Files.Keys.Where(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).Take(100).ToList()
            };

            return Ok(result);
        }

        public class ManifestRequest
        {
            public string ManifestPath { get; set; }
        }

        /// <summary>
        /// Loads a manifest file and returns the file list within it.
        /// </summary>
        [HttpPost("load_manifest")]
        public async Task<IActionResult> LoadManifest([FromBody] ManifestRequest request)
        {
            var manifestPath = request.ManifestPath;
            if (string.IsNullOrEmpty(manifestPath) || !System.IO.File.Exists(manifestPath))
            {
                return BadRequest(new { message = "Manifest file not found or path is invalid." });
            }

            try
            {
                var manifestBytes = await System.IO.File.ReadAllBytesAsync(manifestPath);
                var manifest = new OptimizedContentBuildManifest(manifestBytes);

                var utocFile = manifest.HashNameMap.FirstOrDefault(x => x.Value.EndsWith(".utoc"));

                return Ok(new
                {
                    parseTime = manifest.ParseTime.ToString(),
                    fileCount = manifest.HashNameMap.Count,
                    utocFile = utocFile,
                    files = manifest.HashNameMap.OrderBy(x => x.Value).ToDictionary(x => x.Key, x => x.Value)
                });
            }
            catch (Exception e)
            {
                return StatusCode(500, new { message = "Failed to parse manifest file.", error = e.Message });
            }
        }
    }
}
