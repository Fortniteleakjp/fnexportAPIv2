using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Generates a binary .usmap mapping file from a StormForge-style mappings JSON
    /// ({ Version, Enums, Structs, Classes }), optionally hot-loading it into the provider.
    /// </summary>
    [ApiController]
    [Route("api/v1/mappings")]
    public class MappingsController : ControllerBase
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private readonly IFileProvider _provider;
        private readonly ILogger<MappingsController> _logger;

        public MappingsController(IFileProvider provider, ILogger<MappingsController> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        /// <summary>
        /// Generates a .usmap from a mappings JSON (downloaded from <paramref name="url"/> or read from a
        /// local <paramref name="path"/>), saves it under mappings/, verifies it parses back, and can
        /// hot-load it into the provider.
        /// </summary>
        /// <param name="url">URL of the mappings JSON to download (e.g. a StormForge JSON).</param>
        /// <param name="path">Local path of a mappings JSON (alternative to url).</param>
        /// <param name="fileName">Output .usmap file name (defaults to the source name).</param>
        /// <param name="load">Hot-load the generated mapping into the provider (default false).</param>
        /// <param name="verify">Parse the generated .usmap back and report counts/samples (default true).</param>
        [HttpPost("generate")]
        public async Task<IActionResult> Generate(
            [FromQuery] string? url = null,
            [FromQuery] string? path = null,
            [FromQuery] string? fileName = null,
            [FromQuery] bool load = false,
            [FromQuery] bool verify = true,
            [FromQuery] bool download = true)
        {
            byte[] json;
            string sourceName;
            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    json = await Http.GetByteArrayAsync(url);
                    sourceName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
                }
                else if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    json = await System.IO.File.ReadAllBytesAsync(path);
                    sourceName = Path.GetFileNameWithoutExtension(path);
                }
                else
                {
                    return BadRequest(new { message = "Provide 'url' or an existing local 'path' to a mappings JSON." });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { message = "Failed to read the mappings JSON.", error = ex.Message });
            }

            UsmapGenerator.Result gen;
            try
            {
                gen = UsmapGenerator.Generate(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "usmap generation failed");
                return StatusCode(500, new { message = "usmap generation failed.", error = ex.Message });
            }

            var rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
            var mappingsDir = Path.Combine(rootDir, "mappings");
            Directory.CreateDirectory(mappingsDir);

            var baseName = string.IsNullOrWhiteSpace(fileName) ? sourceName : fileName;
            var outName = baseName.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + ".usmap";
            var outPath = Path.Combine(mappingsDir, outName);
            await System.IO.File.WriteAllBytesAsync(outPath, gen.Usmap);

            int? parsedEnums = null, parsedStructs = null;
            object? verification = null;
            if (verify)
            {
                try
                {
                    var parser = new UsmapParser(gen.Usmap, outName);
                    var m = parser.Mappings;
                    parsedEnums = m?.Enums.Count ?? 0;
                    parsedStructs = m?.Types.Count ?? 0;
                    verification = new { parsedEnums, parsedStructs, samples = SampleTypes(m) };
                }
                catch (Exception ex)
                {
                    verification = new { error = "Parse-back failed: " + ex.Message };
                }
            }

            if (load)
            {
                try { _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(outPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "loading the generated usmap failed"); }
            }

            // Default: return the generated .usmap itself (with the stats in X-Usmap-* headers) so the
            // caller gets the mapping built from their JSON directly. download=false returns JSON stats.
            if (download)
            {
                var h = Response.Headers;
                h.Append("X-Usmap-Bytes", gen.Usmap.Length.ToString());
                h.Append("X-Usmap-Names", gen.Names.ToString());
                h.Append("X-Usmap-Enums", gen.Enums.ToString());
                h.Append("X-Usmap-Structs", gen.Structs.ToString());
                h.Append("X-Usmap-UnknownProps", gen.UnknownProps.ToString());
                h.Append("X-Usmap-OptionalProps", gen.OptionalProps.ToString());
                h.Append("X-Usmap-EditorOnlySkipped", gen.SkippedEditorOnlyProps.ToString());
                h.Append("X-Usmap-Output", outPath);
                h.Append("X-Usmap-Loaded", load ? "true" : "false");
                if (parsedEnums.HasValue) h.Append("X-Usmap-ParsedEnums", parsedEnums.Value.ToString());
                if (parsedStructs.HasValue) h.Append("X-Usmap-ParsedStructs", parsedStructs.Value.ToString());
                return File(gen.Usmap, "application/octet-stream", outName);
            }

            return Ok(new
            {
                source = url ?? path,
                output = outPath,
                usmapBytes = gen.Usmap.Length,
                names = gen.Names,
                enums = gen.Enums,
                structs = gen.Structs,
                unknownProps = gen.UnknownProps,
                optionalProps = gen.OptionalProps,
                skippedEditorOnlyProps = gen.SkippedEditorOnlyProps,
                skippedNamelessTypes = gen.SkippedNamelessTypes,
                skippedNamelessEnums = gen.SkippedNamelessEnums,
                loaded = load,
                verification
            });
        }

        private static List<object> SampleTypes(TypeMappings? m)
        {
            var list = new List<object>();
            if (m == null) return list;
            foreach (var n in new[] { "UintVector", "Field", "Vector2D", "Object", "Actor" })
            {
                if (m.Types.TryGetValue(n, out var s))
                {
                    var first = new List<string>();
                    foreach (var kv in s.Properties)
                    {
                        first.Add($"{kv.Key}:{kv.Value.Name}={kv.Value.MappingType.Type}");
                        if (first.Count >= 5) break;
                    }
                    list.Add(new { name = n, propertyCount = s.PropertyCount, propertyEntries = s.Properties.Count, super = s.SuperType, sampleProps = first });
                }
            }
            return list;
        }
    }
}
