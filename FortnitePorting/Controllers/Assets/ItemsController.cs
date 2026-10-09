using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// A set of endpoints for listing and extracting assets whose names start with
    /// a specific prefix (WID_ / AGID_ / Athena_ / Figment_Athena_).
    /// </summary>
    [ApiController]
    [Route("api/v1/items")]
    public class ItemsController : ControllerBase
    {
        private readonly IFileProvider _provider;
        private readonly ILogger<ItemsController> _logger;

        /// <summary>
        /// The default set of target prefixes.
        /// </summary>
        private static readonly string[] DefaultPrefixes =
        {
            "WID_", "AGID_", "Athena_", "Figment_Athena_"
        };

        public ItemsController(IFileProvider provider, ILogger<ItemsController> logger)
        {
            _provider = provider;
            _logger = logger;
        }



        /// <summary>
        /// Loads files whose names start with the specified prefixes and, for each asset,
        /// extracts and returns Properties.ItemName.SourceString / DataList(...).Traits / LargeIcon.AssetPathName.
        /// </summary>
        /// <param name="prefixes">A comma-separated list of target prefixes (defaults to WID_,AGID_,Athena_,Figment_Athena_ when omitted).</param>
        /// <param name="excludePrefixes">A comma-separated list of file-name prefixes to exclude (e.g., WID_Harvest_). Excluded even when the name matches one of <paramref name="prefixes"/>.</param>
        /// <param name="excludePaths">A comma-separated list of substrings; a file is excluded when its full path contains any of them (e.g., /Juno/).</param>
        /// <param name="page">The page number (1-based).</param>
        /// <param name="pageSize">The number of items per page (maximum 500; a small value is recommended because asset parsing is expensive).</param>
        /// <returns>The list of extraction results for each file.</returns>
        /// <param name="path">Selects a single file instead of the filtered list.</param>
        [HttpGet("properties")]
        public IActionResult GetProperties(
            [FromQuery] string? prefixes = null,
            [FromQuery] string? excludePrefixes = null,
            [FromQuery] string? excludePaths = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100,
            [FromQuery] string? path = null)
        {
            if (path != null) return GetSingleProperties(path);
            if (page < 1) page = 1;
            pageSize = Math.Clamp(pageSize, 1, 500);

            var prefixList = ParsePrefixes(prefixes);
            var excludePrefixList = ParseList(excludePrefixes);
            var excludePathList = ParseList(excludePaths);

            var matched = EnumerateMatchingFiles(prefixList, ".uasset", excludePrefixList, excludePathList);

            var total = matched.Count;
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            var pagedPaths = PageSlice.From(matched, page, pageSize);

            var results = new List<object>(pagedPaths.Count);
            foreach (var filePath in pagedPaths)
            {
                results.Add(ExtractFromFile(filePath));
            }

            var payload = new
            {
                prefixes = prefixList,
                excludePrefixes = excludePrefixList,
                excludePaths = excludePathList,
                totalFiles = total,
                totalPages,
                currentPage = page,
                pageSize,
                results
            };

            return JsonResponse.Result(payload);
        }

        /// <summary>
        /// Extracts ItemName / Traits / LargeIcon for a single file specified by its path.
        /// </summary>
        /// <param name="path">The path of the target asset (e.g., FortniteGame/Content/.../WID_xxx.uasset).</param>
        private IActionResult GetSingleProperties(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("The 'path' parameter is required.");
            }

            var index = FileIndex.For(_provider);
            var normalized = path.Replace('\\', '/').Trim();
            if (!index.Contains(normalized))
            {
                // Try to complete the file extension
                if (!normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                    index.Contains(normalized + ".uasset"))
                {
                    normalized += ".uasset";
                }
                else
                {
                    return NotFound(new ProblemDetails
                    {
                        Title = "File Not Found",
                        Detail = $"The specified file '{path}' was not found.",
                        Status = StatusCodes.Status404NotFound
                    });
                }
            }

            var result = ExtractFromFile(normalized);
            return JsonResponse.Result(result);
        }


        private static string[] ParsePrefixes(string? prefixes)
        {
            if (string.IsNullOrWhiteSpace(prefixes))
            {
                return DefaultPrefixes;
            }

            var parsed = prefixes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return parsed.Length > 0 ? parsed : DefaultPrefixes;
        }

        /// <summary>
        /// Splits a comma-separated query value. Returns an empty array when nothing was specified.
        /// </summary>
        private static string[] ParseList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Filters file keys by prefix (the start of the file name) and by extension,
        /// then drops the ones excluded by file-name prefix or by a substring of the full path.
        /// </summary>
        private IReadOnlyList<string> EnumerateMatchingFiles(string[] prefixes, string ext, string[] excludePrefixes, string[] excludePaths)
            => FileIndex.For(_provider).MatchingNames(prefixes, ext, excludePrefixes, excludePaths);

        /// <summary>
        /// Loads a single file and extracts the target properties. On failure, returns a result that includes an error.
        /// </summary>
        private object ExtractFromFile(string path)
        {
            try
            {
                // Through the index: a page of results resolves a hundred paths, and the provider's own
                // lookup sorts its whole archive list on each one.
                if (!FileIndex.For(_provider).TryGetFile(path, out var gameFile))
                {
                    return new { path, error = "File not found." };
                }

                var package = _provider.LoadPackage(gameFile);
                var exports = package.GetExports().ToList();
                if (exports.Count == 0)
                {
                    return new { path, name = Path.GetFileNameWithoutExtension(path), error = "No exports were found." };
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });

                string? itemName = null;
                JToken? traits = null;
                string? largeIcon = null;
                string? exportType = null;

                foreach (var export in exports)
                {
                    JToken token;
                    try
                    {
                        token = JToken.FromObject(export, serializer);
                    }
                    catch
                    {
                        continue;
                    }

                    var props = GetChild(token, "Properties") ?? token;

                    // Properties.ItemName.SourceString
                    if (itemName == null)
                    {
                        var itemNameToken = GetChild(props, "ItemName") ?? FindFirst(token, "ItemName");
                        var src = GetChild(itemNameToken, "SourceString");
                        if (src != null && src.Type != JTokenType.Null)
                        {
                            itemName = src.ToString();
                            exportType = GetChild(token, "Type")?.ToString();
                        }
                    }

                    // DataList(...).Traits  (prefer Traits under DataList; otherwise search the entire asset)
                    if (traits == null)
                    {
                        var dataList = GetChild(props, "DataList") ?? FindFirst(token, "DataList");
                        var found = dataList != null ? FindFirst(dataList, "Traits") : null;
                        found ??= FindFirst(token, "Traits");
                        if (found != null && found.Type != JTokenType.Null)
                        {
                            traits = found;
                        }
                    }

                    // LargeIcon.AssetPathName
                    if (largeIcon == null)
                    {
                        var largeIconToken = GetChild(props, "LargeIcon") ?? FindFirst(token, "LargeIcon");
                        var assetPath = GetChild(largeIconToken, "AssetPathName");
                        if (assetPath != null && assetPath.Type != JTokenType.Null)
                        {
                            largeIcon = assetPath.ToString();
                        }
                    }

                    exportType ??= GetChild(token, "Type")?.ToString();
                }

                return new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    exportType,
                    itemName,
                    traits,
                    largeIcon
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract properties from {Path}", path);
                return new { path, name = Path.GetFileNameWithoutExtension(path), error = ex.Message };
            }
        }

        /// <summary>
        /// Gets the value of a direct child property of a JObject, ignoring case.
        /// </summary>
        private static JToken? GetChild(JToken? parent, string name)
        {
            if (parent is JObject obj)
            {
                var prop = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                return prop?.Value;
            }
            return null;
        }

        /// <summary>
        /// Recursively searches for a property with the given name and returns the first value found (ignoring case).
        /// </summary>
        private static JToken? FindFirst(JToken? root, string name)
        {
            if (root is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return prop.Value;
                    }
                }
                foreach (var prop in obj.Properties())
                {
                    var found = FindFirst(prop.Value, name);
                    if (found != null) return found;
                }
            }
            else if (root is JArray arr)
            {
                foreach (var item in arr)
                {
                    var found = FindFirst(item, name);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
