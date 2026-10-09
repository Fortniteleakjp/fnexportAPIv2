using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse_Conversion.Textures;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RADADecoder;
using System.Text.RegularExpressions;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    public partial class ExportController
    {

        /// <summary>
        /// Exports a DataTable or CurveTable as CSV so its rows can be opened directly in a spreadsheet.
        /// A CurveTable is written in long form: one line per curve key.
        /// </summary>
        /// <param name="path">Path of the DataTable / CurveTable asset.</param>
        /// <param name="format">csv (default) or json. json returns the same table as structured rows.</param>
        /// <param name="rows">Optional comma-separated row names to keep. Every row is exported when omitted.</param>
        /// <param name="delimiter">CSV delimiter: comma (default), tab, semicolon, pipe, or a single character.</param>
        /// <param name="flatten">Flatten nested row properties into dotted columns (default true). When false each nested value is written as compact JSON in one cell.</param>
        /// <param name="bom">Prefix the CSV with a UTF-8 BOM so Excel reads localized text correctly (default true).</param>
        /// <param name="download">Send Content-Disposition with a .csv file name (default true).</param>
        /// <param name="hotfix">Apply the live cloudstorage [AssetHotfix] row/curve edits before exporting.</param>
        [HttpGet("datatable")]
        public IActionResult GetDataTable(
            [FromQuery] string? path,
            [FromQuery] string format = "csv",
            [FromQuery] string? rows = null,
            [FromQuery] string delimiter = ",",
            [FromQuery] bool flatten = true,
            [FromQuery] bool bom = true,
            [FromQuery] bool download = true,
            [FromQuery] bool hotfix = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { message = "The 'path' parameter is required." });
            }

            format = (format ?? "csv").Trim().ToLowerInvariant();
            if (format != "csv" && format != "json")
            {
                return BadRequest(new { message = "The 'format' parameter must be csv or json." });
            }

            if (!TryResolveDelimiter(delimiter, out var separator))
            {
                return BadRequest(new { message = "The 'delimiter' parameter must be comma, tab, semicolon, pipe, or a single character." });
            }

            // The hotfix set is fetched first because its fingerprint is part of the cache key: a
            // republished hotfix must not be answered from a response cached against the previous one.
            HotfixIndex? hotfixIndex = null;
            string? hotfixError = null;
            if (hotfix)
            {
                try
                {
                    hotfixIndex = HotfixService.GetIndex();
                }
                catch (Exception ex)
                {
                    hotfixError = ex.Message;
                    _logger.LogWarning(ex, "Could not load the cloudstorage hotfix set from {Url}", HotfixService.ListingUrl);
                }
            }

            var rowFilter = ParseRowFilter(rows);
            var cacheKey = string.Join("::", new[]
            {
                _scope + "datatable",
                path,
                format,
                separator.ToString(),
                flatten ? "flat" : "raw",
                bom ? "bom" : "nobom",
                download ? "attach" : "inline",
                rowFilter == null ? "*" : string.Join(",", rowFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                hotfix ? $"hotfix={hotfixIndex?.Version ?? "unavailable"}" : "nohotfix",
                GetMountSnapshot()
            });

            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached != null)
            {
                return BuildResultFromCache(cached);
            }

            try
            {
                if (!TryResolveAsset(path, out var asset, out var processedPath) || asset == null)
                {
                    return NotFound(new ProblemDetails
                    {
                        Title = "Asset Not Found",
                        Detail = $"The requested asset '{path}' could not be found.",
                        Status = StatusCodes.Status404NotFound,
                        Extensions = { { "requestedPath", path }, { "processedPath", processedPath } }
                    });
                }

                var isCurveTable = asset is UCurveTable;
                if (asset is not UDataTable && !isCurveTable)
                {
                    return StatusCode(StatusCodes.Status422UnprocessableEntity, new ProblemDetails
                    {
                        Title = "Not a table asset",
                        Detail = $"'{asset.Name}' is a {asset.ExportType}, not a DataTable or CurveTable. Use /api/v1/export for other asset types.",
                        Status = StatusCodes.Status422UnprocessableEntity
                    });
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                var token = JToken.FromObject(asset, serializer);

                // Rewrite the rows/curves with the live [AssetHotfix] values before the table is built,
                // so the CSV describes the table as the game currently runs it.
                List<HotfixApplier.HotfixResult>? hotfixResults = null;
                if (hotfix && hotfixIndex != null)
                {
                    var entries = hotfixIndex.For(HotfixService.NormalizeAssetPath(processedPath).ToLowerInvariant());
                    if (entries.Count == 0 && asset.Owner?.Name is { Length: > 0 } packageName)
                    {
                        entries = hotfixIndex.For(HotfixService.NormalizeAssetPath(packageName).ToLowerInvariant());
                    }

                    hotfixResults = entries.Count > 0 ? HotfixApplier.Apply(token, entries) : [];
                }

                if (token["Rows"] is not JObject rowMap)
                {
                    return StatusCode(StatusCodes.Status422UnprocessableEntity, new ProblemDetails
                    {
                        Title = "No rows",
                        Detail = $"'{asset.Name}' carries no serialized Rows. A mapping (.usmap) may be missing for its row struct.",
                        Status = StatusCodes.Status422UnprocessableEntity
                    });
                }

                var columns = new List<string>();
                var table = isCurveTable
                    ? BuildCurveTable(rowMap, rowFilter, columns)
                    : BuildDataTable(rowMap, rowFilter, flatten, columns);

                Dictionary<string, string>? headers = null;
                if (hotfix)
                {
                    var applied = hotfixResults?.Count(result => result.Applied) ?? 0;
                    headers = new Dictionary<string, string>
                    {
                        ["X-Hotfix-Status"] = hotfixIndex == null ? "unavailable" : applied > 0 ? "applied" : "none",
                        ["X-Hotfix-Applied"] = applied.ToString()
                    };
                }

                CacheEntry entry;
                if (format == "json")
                {
                    var payload = new JObject
                    {
                        ["path"] = path,
                        ["name"] = asset.Name,
                        ["exportType"] = asset.ExportType,
                        ["tableKind"] = isCurveTable ? "curveTable" : "dataTable",
                        ["rowStruct"] = (asset as UDataTable)?.RowStructName,
                        ["curveTableMode"] = isCurveTable ? token["CurveTableMode"]?.ToString() : null,
                        ["totalRows"] = rowMap.Count,
                        ["exportedLines"] = table.Count,
                        ["columns"] = new JArray(columns),
                        ["rows"] = new JArray(table.Select(line =>
                        {
                            var obj = new JObject();
                            foreach (var column in columns)
                            {
                                obj[column] = line.TryGetValue(column, out var value) ? value : string.Empty;
                            }
                            return obj;
                        }))
                    };

                    entry = new CacheEntry
                    {
                        Content = JsonResponse.Serialize(payload, Formatting.None),
                        ContentType = "application/json; charset=utf-8",
                        Headers = headers
                    };
                }
                else
                {
                    var csv = new StringBuilder();
                    csv.Append(string.Join(separator, columns.Select(column => CsvEscape(column, separator)))).Append("\r\n");
                    foreach (var line in table)
                    {
                        csv.Append(string.Join(separator, columns.Select(column =>
                            CsvEscape(line.TryGetValue(column, out var value) ? value : string.Empty, separator)))).Append("\r\n");
                    }

                    var csvBytes = Encoding.UTF8.GetBytes(csv.ToString());
                    if (bom)
                    {
                        // Excel assumes the system code page for a BOM-less CSV, which mangles
                        // localized row values; the BOM makes it read the file as UTF-8.
                        csvBytes = Encoding.UTF8.GetPreamble().Concat(csvBytes).ToArray();
                    }

                    entry = new CacheEntry
                    {
                        Content = csvBytes,
                        ContentType = "text/csv; charset=utf-8",
                        FileDownloadName = download ? $"{asset.Name}.csv" : null,
                        Headers = headers
                    };
                }

                // A response produced while cloudstorage was unreachable is not hotfixed content, so it
                // is never cached: the next request must try the hotfix set again.
                if (hotfixError == null)
                {
                    _cache.Set(cacheKey, entry, TimeSpan.FromMinutes(30));
                }

                return BuildResultFromCache(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting table for path \"{Path}\"", path);
                return Problem(detail: ex.StackTrace, title: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>Parses the optional row-name filter; null means every row is exported.</summary>
        private static HashSet<string>? ParseRowFilter(string? rows)
        {
            if (string.IsNullOrWhiteSpace(rows)) return null;

            var names = rows
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Count > 0 ? names : null;
        }

        /// <summary>
        /// Resolves the CSV delimiter from its name (comma / tab / semicolon / pipe) or a single character.
        /// </summary>
        private static bool TryResolveDelimiter(string? value, out char delimiter)
        {
            delimiter = ',';
            if (string.IsNullOrEmpty(value)) return true;
            if (value == "\t") return Set('\t', out delimiter);

            var trimmed = value.Trim();
            if (trimmed.Length == 0) return true;

            switch (trimmed.ToLowerInvariant())
            {
                case "comma":
                case ",": return Set(',', out delimiter);
                case "tab":
                case "\\t": return Set('\t', out delimiter);
                case "semicolon":
                case ";": return Set(';', out delimiter);
                case "pipe":
                case "|": return Set('|', out delimiter);
            }

            // Any other single character is accepted, except the ones that would break the quoting rules.
            if (trimmed.Length == 1 && trimmed[0] is not ('"' or '\r' or '\n'))
            {
                return Set(trimmed[0], out delimiter);
            }

            return false;

            static bool Set(char value, out char target)
            {
                target = value;
                return true;
            }
        }

        /// <summary>
        /// Builds the DataTable rows: one line per row, with the row name in the first column and the
        /// union of every row's properties as the remaining columns (in first-seen order).
        /// </summary>
        private static List<Dictionary<string, string>> BuildDataTable(
            JObject rowMap, HashSet<string>? rowFilter, bool flatten, List<string> columns)
        {
            columns.Add("RowName");
            var lines = new List<Dictionary<string, string>>(rowMap.Count);

            foreach (var row in rowMap.Properties())
            {
                if (rowFilter != null && !rowFilter.Contains(row.Name)) continue;

                var line = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["RowName"] = row.Name };
                if (row.Value is JObject rowObject)
                {
                    foreach (var property in rowObject.Properties())
                    {
                        FlattenValue(property.Name, property.Value, flatten, line, columns);
                    }
                }
                else
                {
                    // A row that is not a struct (a plain value table) keeps a single Value column.
                    FlattenValue("Value", row.Value, flatten, line, columns);
                }

                lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// Builds the CurveTable rows in long form: one line per curve key, carrying the row name, the
        /// key's own fields, and the curve-level properties under a Curve. prefix. A row without keys
        /// still produces one line so it is not lost.
        /// </summary>
        private static List<Dictionary<string, string>> BuildCurveTable(
            JObject rowMap, HashSet<string>? rowFilter, List<string> columns)
        {
            columns.Add("RowName");
            columns.Add("Time");
            columns.Add("Value");
            var lines = new List<Dictionary<string, string>>();

            foreach (var row in rowMap.Properties())
            {
                if (rowFilter != null && !rowFilter.Contains(row.Name)) continue;

                var curve = row.Value as JObject;
                var curveLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (curve != null)
                {
                    foreach (var property in curve.Properties())
                    {
                        if (property.Name.Equals("Keys", StringComparison.OrdinalIgnoreCase)) continue;
                        // Prefixed because a RichCurve key carries some of the same field names.
                        FlattenValue("Curve." + property.Name, property.Value, true, curveLevel, columns);
                    }
                }

                var keys = curve?["Keys"] as JArray;
                if (keys == null || keys.Count == 0)
                {
                    var empty = new Dictionary<string, string>(curveLevel, StringComparer.OrdinalIgnoreCase)
                    {
                        ["RowName"] = row.Name,
                        ["Time"] = string.Empty,
                        ["Value"] = string.Empty
                    };
                    lines.Add(empty);
                    continue;
                }

                foreach (var key in keys)
                {
                    var line = new Dictionary<string, string>(curveLevel, StringComparer.OrdinalIgnoreCase) { ["RowName"] = row.Name };
                    if (key is JObject keyObject)
                    {
                        foreach (var property in keyObject.Properties())
                        {
                            FlattenValue(property.Name, property.Value, true, line, columns);
                        }
                    }
                    else
                    {
                        line["Value"] = Stringify(key);
                    }

                    line.TryAdd("Time", string.Empty);
                    line.TryAdd("Value", string.Empty);
                    lines.Add(line);
                }
            }

            return lines;
        }

        /// <summary>
        /// Writes one serialized value into the line under <paramref name="name"/>, registering the
        /// column the first time it appears. Nested objects are expanded into dotted columns when
        /// <paramref name="flatten"/> is set; arrays always stay as compact JSON in a single cell.
        /// </summary>
        private static void FlattenValue(
            string name, JToken? value, bool flatten, IDictionary<string, string> line, List<string> columns)
        {
            if (value is JObject nested && flatten && nested.Count > 0)
            {
                foreach (var property in nested.Properties())
                {
                    FlattenValue(name.Length == 0 ? property.Name : $"{name}.{property.Name}", property.Value, true, line, columns);
                }
                return;
            }

            if (!line.ContainsKey(name) && !columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                columns.Add(name);
            }

            line[name] = Stringify(value);
        }

        /// <summary>Renders one serialized value as a CSV cell: strings verbatim, everything else as JSON.</summary>
        private static string Stringify(JToken? value) => value?.Type switch
        {
            null or JTokenType.Null or JTokenType.Undefined => string.Empty,
            JTokenType.String => value.Value<string>() ?? string.Empty,
            _ => value.ToString(Formatting.None)
        };

        /// <summary>Quotes a CSV field when it contains the delimiter, a quote, a newline, or edge spaces.</summary>
        private static string CsvEscape(string? value, char delimiter)
        {
            value ??= string.Empty;
            var needsQuotes = value.Length > 0 &&
                              (value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0 ||
                               value.IndexOf(delimiter) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
            return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

    }
}
