
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Readers;
using Microsoft.AspNetCore.Mvc;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.Api;
using EpicManifestParser.ZlibngDotNetDecompressor;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Diagnostic endpoints for inspecting the loaded virtual file system: file listing,
    /// substring search, and the mounted PAK / UTOC archives.
    /// </summary>
    [ApiController]
    [Route("api/v1/debug")]
    public class DebugController : ControllerBase
    {
        private readonly IFileProvider _provider;
        private static readonly Regex FnLiveRegex = new(@".*FortniteGame/Content/Paks/.*\.utoc", RegexOptions.Compiled);

        public DebugController(IFileProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Downloads the latest Fortnite manifest and loads files from it into the provider.
        /// </summary>
        /*
        [HttpPost("load_from_fortnite_live")]
        public async Task<IActionResult> LoadFromFortniteLive()
        {
            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The configured file provider is not a VFS provider."});
            }

            try
            {
                var manifestDownloader = new ManifestDownloader();
                var manifestInfo = await manifestDownloader.DownloadManifestInfoAsync("Windows");
                
                // Find the latest Fortnite manifest
                var fortniteManifest = manifestInfo.FirstOrDefault(x => x.AppName == "Fortnite");
                if (fortniteManifest == null)
                {
                    return NotFound(new { message = "Could not find Fortnite manifest." });
                }
                
                FBuildPatchAppManifest manifest = await fortniteManifest.DownloadAndParseAsync(ManifestZlibngDotNetDecompressor.Decompress);
                
                var filesToLoad = manifest.Files.Where(x => FnLiveRegex.IsMatch(x.FileName)).ToList();
                
                Parallel.ForEach(filesToLoad, fileManifest =>
                {
                    vfsProvider.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                        it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), vfsProvider.Versions));
                });
                
                return Ok(new
                {
                    message = $"Successfully loaded {filesToLoad.Count} file(s) from '{fortniteManifest.BuildVersion}' into the provider.",
                    totalMountedFiles = _provider.Files.Count,
                    buildVersion = fortniteManifest.BuildVersion
                });
            }
            catch (Exception e)
            {
                return StatusCode(500, new { message = "Failed to process remote manifest.", error = e.Message });
            }
        }
        */

        /// <summary>
        /// Retrieves the file paths of the loaded files (with pagination support).
        /// </summary>
        /// <param name="page">Page number (starting from 1)</param>
        /// <returns>A list of file paths and the total count</returns>
        [HttpGet("stats")]
        public IActionResult GetStats([FromQuery] int page = 1)
        {
            const int pageSize = 1000;
            
            if (page < 1)
            {
                return BadRequest(new { message = "The page number must be 1 or greater." });
            }

            try
            {
                var allFiles = _provider.Files.Keys.OrderBy(k => k).ToList();
                var totalFiles = allFiles.Count;
                var totalPages = (int)Math.Ceiling(totalFiles / (double)pageSize);
                
                var skip = (page - 1) * pageSize;
                var pagedFiles = allFiles.Skip(skip).Take(pageSize).ToList();

                return Ok(new
                {
                    totalFiles = totalFiles,
                    totalPages = totalPages,
                    currentPage = page,
                    pageSize = pageSize,
                    files = pagedFiles
                });
            }
            catch (Exception e)
            {
                return StatusCode(500, new { message = "Failed to retrieve file information.", error = e.Message });
            }
        }

        /// <summary>
        /// Searches file paths.
        /// </summary>
        /// <param name="query">Search keyword</param>
        /// <returns>A list of file paths matching the search</returns>
        [HttpGet("search")]
        public IActionResult SearchFiles([FromQuery] string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return BadRequest(new { message = "Please specify a search keyword." });
            }

            try
            {
                var searchResults = _provider.Files.Keys
                    .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k)
                    .ToList();

                return Ok(new
                {
                    query = query,
                    totalResults = searchResults.Count,
                    files = searchResults
                });
            }
            catch (Exception e)
            {
                return StatusCode(500, new { message = "File search failed.", error = e.Message });
            }
        }

        /// <summary>
        /// Retrieves the list of mounted PAK/UTOC files.
        /// </summary>
        /// <returns>A list of PAK/UTOC files</returns>
        [HttpGet("paks")]
        public IActionResult GetMountedPaks()
        {
            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The provider is not a VFS provider." });
            }

            var paks = vfsProvider.MountedVfs.Select(x => new
            {
                Name = x.Name,
                FileCount = x.FileCount,
                Path = x.Path
            }).OrderBy(x => x.Name).ToList();

            return Ok(new
            {
                totalPaks = paks.Count,
                paks = paks
            });
        }

        /// <summary>
        /// Retrieves the list of files within the specified PAK/UTOC.
        /// </summary>
        /// <param name="pakName">PAK file name (without extension)</param>
        /// <returns>A list of file paths</returns>
        [HttpGet("paks/{pakName}/files")]
        public IActionResult GetFilesInPak(string pakName)
        {
            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The provider is not a VFS provider." });
            }

            var reader = vfsProvider.MountedVfs.FirstOrDefault(x => 
                string.Equals(x.Name, pakName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(x.Name), pakName, StringComparison.OrdinalIgnoreCase));

            if (reader == null)
            {
                return NotFound(new { message = $"The specified PAK file '{pakName}' was not found." });
            }

            var files = reader.Files.Keys.OrderBy(k => k).ToList();

            return Ok(new
            {
                pakName = reader.Name,
                totalFiles = files.Count,
                files = files
            });
        }
    }
}
