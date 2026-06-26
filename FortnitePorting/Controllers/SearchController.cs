using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Full-text search endpoints over all files: fast path/name search (substring, prefix,
    /// suffix, exact, wildcard, regex, tokens) and a bounded content search inside parsed
    /// asset properties.
    /// </summary>
    [ApiController]
    [Route("api/v1/search")]
    public class SearchController : ControllerBase
    {
        private readonly IFileProvider _provider;
        private readonly ILogger<SearchController> _logger;
        private readonly IMemoryCache _cache;

        // --- Caching / parallelism ---------------------------------------------------------------
        // Cache of decompressed file bytes, shared across requests so a second (different) content
        // query does not have to re-read/re-decompress the same files. Bounded by ContentCacheBudget.
        private static readonly ConcurrentDictionary<string, byte[]> BytesCache = new(StringComparer.OrdinalIgnoreCase);
        private static long _bytesCacheUsed;
        private static readonly long ContentCacheBudget = ResolveCacheBudget();
        private static readonly bool ContentCacheEnabled = ContentCacheBudget > 0;
        // How many files content search scans concurrently (defaults to every core).
        private static readonly int ScanParallelism = ResolveParallelism();
        // How long a content-search response is cached for an identical query.
        private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromMinutes(15);

        // Cap regex evaluation per candidate so a single pathological match cannot dominate.
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
        // Overall wall-clock budget for a single path scan, so a slow query can't run for minutes.
        private static readonly TimeSpan ScanTimeBudget = TimeSpan.FromSeconds(8);
        // Hard cap on collected matches before paging — bounds memory/CPU for very broad queries.
        private const int MaxCollectedMatches = 200_000;
        // Cap on the candidate set gathered (and sorted) by the content search. Set above the total
        // file count so an exhaustive scan is never silently capped.
        private const int MaxContentCandidates = 3_000_000;
        // Reject overly long regex/wildcard patterns (compilation cost is attacker-amplifiable).
        private const int MaxPatternLength = 1000;
        // Check cancellation / the time budget every this many keys during the path scan.
        private const int ScanCheckInterval = 50_000;

        // Extensions that belong to the same logical cooked asset (used by dedupe).
        private static readonly string[] CookedExtensions =
        {
            ".uasset", ".uexp", ".ubulk", ".uptnl", ".umap"
        };

        // Content search: files parsed as UE packages (their exports are serialized to JSON).
        private static readonly HashSet<string> PackageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".uasset", ".umap" };

        // Content search: files read as raw text/bytes (config, registry, plain text, etc.).
        private static readonly HashSet<string> TextExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".ini", ".txt", ".json", ".csv", ".xml", ".cfg", ".uplugin", ".uproject",
                ".bin", ".pem", ".cer", ".crt", ".log", ".md", ".verse"
            };

        // Cap how many bytes of a raw (non-package) file are decoded for content search
        // (large enough to cover the AssetRegistry, which can be tens of MB).
        private const int MaxRawBytes = 64 * 1024 * 1024;

        public SearchController(IFileProvider provider, ILogger<SearchController> logger, IMemoryCache cache)
        {
            _provider = provider;
            _logger = logger;
            _cache = cache;
        }

        // CONTENT_CACHE_MB: decompressed-bytes cache budget in MB. Default off: on warm storage the
        // scan is bandwidth-bound, not decompression-bound, so caching bytes wastes RAM for no gain.
        // Set e.g. CONTENT_CACHE_MB=8192 to enable it (useful on slow/network storage).
        private static long ResolveCacheBudget()
        {
            var v = Environment.GetEnvironmentVariable("CONTENT_CACHE_MB")?.Trim();
            if (!string.IsNullOrEmpty(v) && long.TryParse(v, out var mb) && mb > 0)
            {
                return mb * 1024L * 1024L;
            }
            return 0;
        }

        // SEARCH_THREADS: content-scan parallelism (default = logical CPU count).
        private static int ResolveParallelism()
        {
            var v = Environment.GetEnvironmentVariable("SEARCH_THREADS")?.Trim();
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var t) && t > 0) return t;
            return Math.Max(1, Environment.ProcessorCount);
        }

        /// <summary>
        /// Returns the file's decompressed bytes, serving from (and populating) the shared cache so a
        /// later scan over the same file avoids re-reading/decompressing it.
        /// </summary>
        private byte[]? GetFileBytes(string path)
        {
            if (ContentCacheEnabled && BytesCache.TryGetValue(path, out var hit)) return hit;

            if (!_provider.TrySaveAsset(path, out var bytes) || bytes == null) return null;

            if (ContentCacheEnabled && Interlocked.Read(ref _bytesCacheUsed) + bytes.Length <= ContentCacheBudget)
            {
                if (BytesCache.TryAdd(path, bytes)) Interlocked.Add(ref _bytesCacheUsed, bytes.Length);
            }
            return bytes;
        }

        /// <summary>
        /// Searches the paths/names of all loaded files for a word, string, or codename.
        /// </summary>
        /// <param name="q">The word, string, or codename to search for (required).</param>
        /// <param name="mode">Match mode: contains (default) / prefix / suffix / exact / wildcard / regex / tokens.</param>
        /// <param name="field">Match target: path (default) / name / stem (without extension).</param>
        /// <param name="caseSensitive">Match case-sensitively (default false).</param>
        /// <param name="ext">Filter by extension (comma-separated, e.g. .uasset,.umap; empty matches all).</param>
        /// <param name="dir">Restrict to paths under this directory (e.g. FortniteGame/Content/Athena).</param>
        /// <param name="dedupe">Collapse cooked-asset duplicates (.uasset/.uexp/.ubulk, etc.) into one (default false).</param>
        /// <param name="page">The page number (1-based).</param>
        /// <param name="pageSize">The number of items per page (maximum 10000).</param>
        /// <returns>The matching files (path / name / ext) and the total count.</returns>
        [HttpGet]
        public IActionResult Search(
            [FromQuery] string? q = null,
            [FromQuery] string mode = "contains",
            [FromQuery] string field = "path",
            [FromQuery] bool caseSensitive = false,
            [FromQuery] string? ext = null,
            [FromQuery] string? dir = null,
            [FromQuery] bool dedupe = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest(new { message = "The 'q' parameter is required." });
            }

            if (page < 1) page = 1;
            pageSize = Math.Clamp(pageSize, 1, 10000);

            mode = (mode ?? "contains").Trim().ToLowerInvariant();
            field = (field ?? "path").Trim().ToLowerInvariant();
            if (field != "path" && field != "name" && field != "stem")
            {
                return BadRequest(new { message = "The 'field' parameter must be one of: path, name, stem." });
            }

            // Trim once and use this value for both matching and the echoed response (kept consistent).
            var needle = q.Trim();
            if ((mode == "regex" || mode == "wildcard") && needle.Length > MaxPatternLength)
            {
                return BadRequest(new { message = $"The pattern is too long (max {MaxPatternLength} characters for regex/wildcard)." });
            }

            Func<string, bool> matcher;
            try
            {
                matcher = BuildMatcher(needle, mode, caseSensitive);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var extensions = ParseExtensions(ext);
            var hasExtFilter = extensions.Count > 0;
            var dirPrefix = NormalizeDirPrefix(dir);

            var matches = new List<string>();
            var truncated = false;
            var stopwatch = Stopwatch.StartNew();
            long seen = 0;

            foreach (var key in _provider.Files.Keys)
            {
                if (++seen % ScanCheckInterval == 0)
                {
                    if (cancellationToken.IsCancellationRequested || stopwatch.Elapsed > ScanTimeBudget)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (dirPrefix != null && !key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Cheap, allocation-free extension filter (the keys are '/'-delimited virtual paths).
                if (hasExtFilter && !extensions.Any(e => key.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = field switch
                {
                    "name" => GetFileName(key),
                    "stem" => GetFileStem(key),
                    _ => key
                };

                if (matcher(value))
                {
                    matches.Add(key);
                    if (matches.Count >= MaxCollectedMatches)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            // The provider can enumerate the same virtual path from multiple mounted archives.
            IEnumerable<string> ordered = matches
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            if (dedupe)
            {
                // Collapse the cooked variants of one asset (e.g. Foo.uasset + Foo.uexp + Foo.ubulk)
                // into a single representative entry, preferring the canonical primary file
                // (.uasset/.umap) over side files (.uexp/.ubulk/.uptnl).
                ordered = ordered
                    .GroupBy(RemoveCookedExtension, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(CanonicalRank).ThenBy(x => x.Length).First())
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            }

            var orderedList = ordered.ToList();
            var total = orderedList.Count;
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            var paged = orderedList
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(k => new
                {
                    path = k,
                    name = GetFileName(k),
                    ext = GetExtension(k)
                })
                .ToList();

            return Ok(new
            {
                query = needle,
                mode,
                field,
                caseSensitive,
                extensions = hasExtFilter ? extensions : new List<string> { "(all)" },
                directory = dirPrefix?.TrimEnd('/'),
                dedupe,
                totalMatches = total,
                // True when the scan was stopped early (match cap / time budget / cancellation),
                // so there may be additional matches not represented here.
                truncated,
                totalPages,
                currentPage = page,
                pageSize,
                results = paged
            });
        }

        /// <summary>
        /// Searches inside the contents (serialized properties) of candidate assets.
        /// </summary>
        /// <param name="q">The string to find inside asset contents (required).</param>
        /// <param name="dir">Restrict candidate assets to this directory.</param>
        /// <param name="pathContains">Further narrow candidates whose path contains this text.</param>
        /// <param name="ext">Candidate extensions (comma-separated). Empty = the default set (assets .uasset/.umap plus text/config such as .ini/.bin/.json); "*" or "all" = every file.</param>
        /// <param name="caseSensitive">Match case-sensitively (default false).</param>
        /// <param name="maxScan">Maximum number of candidate files to scan (default 3000000 = the whole game, ~40 s; pass a smaller value for a faster partial scan).</param>
        /// <param name="maxResults">Maximum number of matching files to return (default 50, max 500).</param>
        /// <param name="snippetsPerFile">Number of snippet lines returned per file (default 3, max 20).</param>
        /// <returns>The matching files and their snippet lines.</returns>
        [HttpGet("content")]
        public IActionResult SearchContent(
            [FromQuery] string? q = null,
            [FromQuery] string? dir = null,
            [FromQuery] string? pathContains = null,
            [FromQuery] string ext = "",
            [FromQuery] bool caseSensitive = false,
            [FromQuery] int maxScan = 3_000_000,
            [FromQuery] int maxResults = 50,
            [FromQuery] int snippetsPerFile = 3,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest(new { message = "The 'q' parameter is required." });
            }

            maxScan = Math.Clamp(maxScan, 1, 3_000_000);
            maxResults = Math.Clamp(maxResults, 1, 2000);
            snippetsPerFile = Math.Clamp(snippetsPerFile, 0, 20);

            var needle = q.Trim();
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var dirPrefix = NormalizeDirPrefix(dir);

            // Resolve the allowed extension set: empty = default (assets + text/config),
            // "*"/"all" = every file, otherwise the explicit comma-separated list.
            var extTrim = (ext ?? "").Trim();
            var allowAll = extTrim == "*" || extTrim.Equals("all", StringComparison.OrdinalIgnoreCase);
            var explicitExts = allowAll ? new List<string>() : ParseExtensions(ext);
            var explicitSet = new HashSet<string>(explicitExts, StringComparer.OrdinalIgnoreCase);
            var useDefault = !allowAll && explicitSet.Count == 0;

            bool IsAllowed(string keyExt)
            {
                if (allowAll) return true;
                if (useDefault) return PackageExtensions.Contains(keyExt) || TextExtensions.Contains(keyExt);
                return explicitSet.Contains(keyExt);
            }

            // Result cache: an identical query returns instantly. The mounted file count is part of the
            // key, so a new build / newly decrypted paks transparently invalidate stale results.
            var cacheKey = string.Concat(
                "sc|", _provider.Files.Count.ToString(), "|", needle, "|", caseSensitive ? "1" : "0",
                "|", dirPrefix ?? "", "|", pathContains ?? "", "|", extTrim,
                "|", maxScan.ToString(), "|", maxResults.ToString(), "|", snippetsPerFile.ToString());
            if (_cache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson != null)
            {
                return Content(cachedJson, "application/json; charset=utf-8");
            }

            // Buckets, scanned in priority order so the most relevant files are parsed within the
            // maxScan budget:
            //   (0) pathBucket    — the path itself contains the query (a codename in its own path)
            //   (1) relatedBucket — assets sharing a plugin/folder with a path match (a content-only
            //                        asset such as a GameFeatureData usually lives beside them)
            //   (2) textBucket    — text/config files (.ini/.bin/... — a small, cheap universe)
            //   (3) assetBucket   — the remaining package assets (the huge universe, scanned last)
            var pathBucket = new List<string>();
            var textBucket = new List<string>();
            var assetBucket = new List<string>();
            var candidateLimitReached = false;

            void AddTo(List<string> bucket, string key)
            {
                if (bucket.Count < MaxContentCandidates) bucket.Add(key);
                else candidateLimitReached = true;
            }

            // Pass 1: classify every allowed candidate.
            foreach (var key in _provider.Files.Keys)
            {
                if (dirPrefix != null && !key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(pathContains) && !key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;

                var keyExt = GetExtension(key);
                if (!IsAllowed(keyExt)) continue;

                if (key.Contains(needle, StringComparison.OrdinalIgnoreCase)) AddTo(pathBucket, key);
                else if (TextExtensions.Contains(keyExt)) AddTo(textBucket, key);
                else AddTo(assetBucket, key);
            }

            // The provider can enumerate the same virtual path from multiple mounted archives.
            pathBucket = pathBucket.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            textBucket = textBucket.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            assetBucket = assetBucket.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Derive "related" scopes (plugin root or parent folder) from the path matches, then do a
            // second pass to collect the package assets under those scopes — this reaches content-only
            // assets that sit next to a match (e.g. CrewCore.uasset beside XpBooster_CrewTier*) without
            // having to parse every asset in the game.
            var scopes = pathBucket
                .Select(GetRelatedScope)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            var relatedBucket = new List<string>();
            if (scopes.Count > 0)
            {
                var inPath = new HashSet<string>(pathBucket, StringComparer.OrdinalIgnoreCase);
                foreach (var key in _provider.Files.Keys)
                {
                    if (relatedBucket.Count >= MaxContentCandidates) { candidateLimitReached = true; break; }

                    var keyExt = GetExtension(key);
                    if (!PackageExtensions.Contains(keyExt)) continue;                         // related = assets only
                    if (!IsAllowed(keyExt)) continue;
                    if (key.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;     // already in pathBucket
                    if (inPath.Contains(key)) continue;
                    if (dirPrefix != null && !key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(pathContains) && !key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;

                    if (scopes.Any(s => key.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        relatedBucket.Add(key);
                    }
                }
                relatedBucket = relatedBucket.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            pathBucket.Sort(StringComparer.OrdinalIgnoreCase);
            relatedBucket.Sort(StringComparer.OrdinalIgnoreCase);
            textBucket.Sort(StringComparer.OrdinalIgnoreCase);
            assetBucket.Sort(StringComparer.OrdinalIgnoreCase);

            var candidateTotal = pathBucket.Count + relatedBucket.Count + textBucket.Count + assetBucket.Count;

            // Build the priority-ordered, de-duplicated candidate list, capped at maxScan.
            var ordered = new List<string>(Math.Min(maxScan, candidateTotal));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanCapped = false;
            foreach (var path in pathBucket.Concat(relatedBucket).Concat(textBucket).Concat(assetBucket))
            {
                if (ordered.Count >= maxScan) { scanCapped = true; break; }
                if (visited.Add(path)) ordered.Add(path);   // related ⊂ assets; never scan a file twice
            }
            var scanned = ordered.Count;

            // Scan in parallel — the per-file cost is dominated by reading/decompressing the package,
            // which parallelizes well, so a whole-game scan stays tractable. Each match keeps its
            // priority index so the output remains in priority order.
            var bag = new ConcurrentBag<(int Order, object Result)>();
            var matchCount = 0;
            var parallelism = ScanParallelism;
            try
            {
                Parallel.For(0, ordered.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    (i, state) =>
                    {
                        if (Volatile.Read(ref matchCount) >= maxResults) { state.Stop(); return; }

                        var path = ordered[i];

                        // Detect with an allocation-free byte-level scan (no per-file string decode →
                        // far less GC, so the parallel scan actually scales across cores).
                        if (!RawFileContains(path, needle, caseSensitive)) return;

                        var isPackage = PackageExtensions.Contains(GetExtension(path));
                        string? snippetSource = null;
                        if (isPackage)
                        {
                            // Upgrade the snippet to a clean parsed JSON view (only matched files are parsed).
                            var json = TryLoadAssetJson(path);
                            if (json != null && json.IndexOf(needle, cmp) >= 0) snippetSource = json;
                        }
                        snippetSource ??= ReadRawTextIfMatches(path, needle, cmp) ?? "(match)";

                        var snippets = ExtractSnippets(snippetSource, needle, cmp, snippetsPerFile);
                        if (snippets.Count == 0 && snippetsPerFile > 0)
                        {
                            snippets.Add("(match spans multiple lines)");
                        }

                        if (Interlocked.Increment(ref matchCount) > maxResults) { state.Stop(); return; }
                        bag.Add((i, new
                        {
                            path,
                            name = GetFileStem(path),
                            type = isPackage ? "asset" : "text",
                            matches = snippets
                        }));
                    });
            }
            catch (OperationCanceledException) { }

            var results = bag.OrderBy(x => x.Order).Take(maxResults).Select(x => x.Result).ToList();
            var stoppedAtResultLimit = matchCount > maxResults;

            var payload = new
            {
                query = needle,
                directory = dirPrefix?.TrimEnd('/'),
                pathContains,
                extensions = allowAll ? "(all)" : useDefault ? "(default: assets + text/config)" : string.Join(",", explicitExts),
                caseSensitive,
                candidatesMatched = candidateTotal,
                // How many candidates had the query in their path and were therefore scanned first.
                pathPrioritized = pathBucket.Count,
                // Assets pulled in because they share a plugin/folder with a path match.
                relatedCandidates = relatedBucket.Count,
                textCandidates = textBucket.Count,
                assetCandidates = assetBucket.Count,
                // True when there were more candidates than the gather cap allowed.
                candidateLimitReached,
                scanned,
                scanLimit = maxScan,
                // True when not every candidate file was read (hit maxScan, the candidate gather cap,
                // or stopped at maxResults). Raise maxScan to scan the whole game.
                truncated = scanCapped || stoppedAtResultLimit || candidateLimitReached,
                parallelism = ScanParallelism,
                cacheBytesMB = (int)(Interlocked.Read(ref _bytesCacheUsed) / (1024 * 1024)),
                resultCount = results.Count,
                results
            };

            var jsonOut = JsonConvert.SerializeObject(payload, Formatting.Indented);

            // Cache the response unless the scan was cut short by client cancellation (partial result).
            if (!cancellationToken.IsCancellationRequested)
            {
                _cache.Set(cacheKey, jsonOut, new MemoryCacheEntryOptions { SlidingExpiration = ResultCacheTtl });
            }

            return Content(jsonOut, "application/json; charset=utf-8");
        }

        // --- Internal helpers ---

        /// <summary>
        /// Builds a predicate for the requested match mode. Throws <see cref="ArgumentException"/>
        /// for an unknown mode or an invalid regular expression.
        /// </summary>
        private static Func<string, bool> BuildMatcher(string q, string mode, bool caseSensitive)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var regexOptions = (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Compiled | RegexOptions.CultureInvariant;

            switch (mode)
            {
                case "contains":
                    return v => v.Contains(q, cmp);
                case "prefix":
                    return v => v.StartsWith(q, cmp);
                case "suffix":
                    return v => v.EndsWith(q, cmp);
                case "exact":
                    return v => v.Equals(q, cmp);
                case "tokens":
                {
                    // Whitespace-separated words; every word must be present (AND).
                    var tokens = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length == 0)
                    {
                        throw new ArgumentException("The 'q' parameter contains no search tokens.");
                    }
                    return v => tokens.All(t => v.Contains(t, cmp));
                }
                case "wildcard":
                {
                    // Glob: * matches any run of characters, ? matches a single character. Anchored.
                    var pattern = "^" + Regex.Escape(q).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    var rx = new Regex(pattern, regexOptions, RegexTimeout);
                    return v => SafeIsMatch(rx, v);
                }
                case "regex":
                {
                    Regex rx;
                    try
                    {
                        rx = new Regex(q, regexOptions, RegexTimeout);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException($"Invalid regular expression: {ex.Message}");
                    }
                    return v => SafeIsMatch(rx, v);
                }
                default:
                    throw new ArgumentException("The 'mode' parameter must be one of: contains, prefix, suffix, exact, wildcard, regex, tokens.");
            }
        }

        private static bool SafeIsMatch(Regex rx, string value)
        {
            try
            {
                return rx.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static List<string> ParseExtensions(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
            {
                return new List<string>();
            }

            return ext
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? NormalizeDirPrefix(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            return dir.Replace('\\', '/').Trim().TrimEnd('/') + "/";
        }

        // The provider keys are forward-slash-delimited virtual paths, so slice on '/' directly
        // (avoids Path.* which also scans for '\\' on Windows and allocates more eagerly).
        private static string GetFileName(string key)
        {
            var slash = key.LastIndexOf('/');
            return slash >= 0 ? key.Substring(slash + 1) : key;
        }

        private static string GetExtension(string key)
        {
            var slash = key.LastIndexOf('/');
            var dot = key.LastIndexOf('.');
            return dot > slash ? key.Substring(dot) : string.Empty;
        }

        private static string GetFileStem(string key)
        {
            var slash = key.LastIndexOf('/');
            var start = slash + 1;
            var dot = key.LastIndexOf('.');
            var end = dot > slash ? dot : key.Length;
            return key.Substring(start, end - start);
        }

        private static int CanonicalRank(string path)
        {
            var ext = GetExtension(path);
            return (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".umap", StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
        }

        /// <summary>
        /// Returns a "neighbourhood" prefix for a path: the GameFeature/plugin root when the path is
        /// under one, otherwise the immediate parent directory. Used to pull in content-only assets
        /// that sit beside a path match.
        /// </summary>
        private static string GetRelatedScope(string path)
        {
            const string gf = "/Plugins/GameFeatures/";
            const string pl = "/Plugins/";

            var i = path.IndexOf(gf, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var after = i + gf.Length;
                var slash = path.IndexOf('/', after);
                if (slash > 0) return path.Substring(0, slash + 1);
            }

            i = path.IndexOf(pl, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var after = i + pl.Length;
                var slash = path.IndexOf('/', after);
                if (slash > 0) return path.Substring(0, slash + 1);
            }

            var last = path.LastIndexOf('/');
            return last >= 0 ? path.Substring(0, last + 1) : string.Empty;
        }

        private static string RemoveCookedExtension(string path)
        {
            var ext = GetExtension(path);
            if (ext.Length > 0 && CookedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return path.Substring(0, path.Length - ext.Length);
            }
            return path;
        }

        /// <summary>
        /// Loads an asset and serializes every export to an indented JSON string. Returns null on failure.
        /// </summary>
        private string? TryLoadAssetJson(string path)
        {
            try
            {
                if (!_provider.Files.TryGetValue(path, out var gameFile))
                {
                    return null;
                }

                var package = _provider.LoadPackage(gameFile);
                var exports = package.GetExports().ToList();
                if (exports.Count == 0)
                {
                    return null;
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });

                var array = new JArray();
                foreach (var export in exports)
                {
                    try
                    {
                        array.Add(JToken.FromObject(export, serializer));
                    }
                    catch
                    {
                        // Skip exports that fail to serialize.
                    }
                }

                return array.Count > 0 ? array.ToString(Formatting.Indented) : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content search: failed to load {Path}", path);
                return null;
            }
        }

        /// <summary>
        /// Allocation-free content detection: reads the file's raw bytes and looks for the query as
        /// a single-byte (ASCII/UTF-8) sequence and as UTF-16LE, without decoding the whole file to a
        /// string. Keeps GC pressure low so the parallel scan scales across cores.
        /// </summary>
        private bool RawFileContains(string path, string needle, bool caseSensitive)
        {
            try
            {
                var bytes = GetFileBytes(path);
                if (bytes == null || bytes.Length == 0)
                {
                    return false;
                }

                var len = Math.Min(bytes.Length, MaxRawBytes);
                var span = bytes.AsSpan(0, len);

                if (IsAsciiQuery(needle))
                {
                    Span<byte> lo = stackalloc byte[needle.Length];
                    Span<byte> up = stackalloc byte[needle.Length];
                    for (var k = 0; k < needle.Length; k++)
                    {
                        lo[k] = (byte)(caseSensitive ? needle[k] : char.ToLowerInvariant(needle[k]));
                        up[k] = (byte)(caseSensitive ? needle[k] : char.ToUpperInvariant(needle[k]));
                    }
                    return ContainsSingleByte(span, lo, up) || ContainsUtf16(span, lo, up);
                }

                // Non-ASCII query: fall back to decoding (rare path).
                var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (System.Text.Encoding.UTF8.GetString(span).IndexOf(needle, cmp) >= 0) return true;
                var even = len - (len % 2);
                return System.Text.Encoding.Unicode.GetString(span.Slice(0, even)).IndexOf(needle, cmp) >= 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content search: raw read failed {Path}", path);
                return false;
            }
        }

        private static bool IsAsciiQuery(string s)
        {
            foreach (var c in s)
            {
                if (c > 127) return false;
            }
            return s.Length > 0;
        }

        // Vectorized first-byte scan (IndexOf/IndexOfAny) + short verify; case-insensitive via lo/up bytes.
        private static bool ContainsSingleByte(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> lo, ReadOnlySpan<byte> up)
        {
            var n = lo.Length;
            if (n == 0 || hay.Length < n) return false;

            var from = 0;
            while (from + n <= hay.Length)
            {
                var rest = hay.Slice(from);
                var idx = lo[0] == up[0] ? rest.IndexOf(lo[0]) : rest.IndexOfAny(lo[0], up[0]);
                if (idx < 0) return false;
                var pos = from + idx;
                if (pos + n > hay.Length) return false;

                var j = 1;
                for (; j < n; j++)
                {
                    var b = hay[pos + j];
                    if (b != lo[j] && b != up[j]) break;
                }
                if (j == n) return true;
                from = pos + 1;
            }
            return false;
        }

        // Same idea for UTF-16LE: each query char is one byte (lo/up) followed by 0x00.
        private static bool ContainsUtf16(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> lo, ReadOnlySpan<byte> up)
        {
            var n = lo.Length;
            if (n == 0 || hay.Length < n * 2) return false;

            var from = 0;
            while (from + n * 2 <= hay.Length)
            {
                var rest = hay.Slice(from);
                var idx = lo[0] == up[0] ? rest.IndexOf(lo[0]) : rest.IndexOfAny(lo[0], up[0]);
                if (idx < 0) return false;
                var pos = from + idx;
                if (pos + n * 2 > hay.Length) return false;

                var j = 0;
                for (; j < n; j++)
                {
                    var l = hay[pos + j * 2];
                    var h = hay[pos + j * 2 + 1];
                    if (h != 0 || (l != lo[j] && l != up[j])) break;
                }
                if (j == n) return true;
                from = pos + 1;
            }
            return false;
        }

        /// <summary>
        /// Reads a non-package file as raw bytes and returns a decoded text view that contains the
        /// query, or null if the file does not contain it. Tries a BOM-aware / UTF-8 decode first,
        /// then UTF-16LE (for files such as the AssetRegistry whose FStrings are stored as UTF-16).
        /// </summary>
        private string? ReadRawTextIfMatches(string path, string needle, StringComparison cmp)
        {
            try
            {
                var bytes = GetFileBytes(path);
                if (bytes == null || bytes.Length == 0)
                {
                    return null;
                }

                var len = Math.Min(bytes.Length, MaxRawBytes);

                var primary = DecodePrimary(bytes, len, out var hadBom);
                if (primary.IndexOf(needle, cmp) >= 0)
                {
                    return primary;
                }

                if (!hadBom)
                {
                    var utf16 = System.Text.Encoding.Unicode.GetString(bytes, 0, len - (len % 2));
                    if (utf16.IndexOf(needle, cmp) >= 0)
                    {
                        return utf16;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content search: failed to read raw file {Path}", path);
                return null;
            }
        }

        /// <summary>
        /// Decodes bytes to text honoring a UTF-8/UTF-16 BOM, falling back to lenient UTF-8
        /// (which preserves ASCII content and never throws).
        /// </summary>
        private static string DecodePrimary(byte[] bytes, int len, out bool hadBom)
        {
            if (len >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                hadBom = true;
                return System.Text.Encoding.UTF8.GetString(bytes, 3, len - 3);
            }
            if (len >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                hadBom = true;
                return System.Text.Encoding.Unicode.GetString(bytes, 2, len - 2);
            }
            if (len >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                hadBom = true;
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, len - 2);
            }

            hadBom = false;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, len);
        }

        /// <summary>
        /// Returns up to <paramref name="max"/> snippets from the text: each is a line containing the
        /// query, trimmed; for very long lines (e.g. minified text or a binary blob) a window around
        /// the first match is returned instead.
        /// </summary>
        private static List<string> ExtractSnippets(string text, string needle, StringComparison cmp, int max)
        {
            var snippets = new List<string>();
            if (max <= 0)
            {
                return snippets;
            }

            using var reader = new StringReader(text);
            string? line;
            while (snippets.Count < max && (line = reader.ReadLine()) != null)
            {
                var idx = line.IndexOf(needle, cmp);
                if (idx < 0)
                {
                    continue;
                }

                var trimmed = line.Trim();
                string snippet;
                if (trimmed.Length <= 300)
                {
                    snippet = trimmed;
                }
                else
                {
                    // Window around the first match so the snippet actually shows the hit.
                    var start = Math.Max(0, idx - 120);
                    var end = Math.Min(line.Length, idx + needle.Length + 120);
                    snippet = (start > 0 ? "…" : "") + line.Substring(start, end - start).Trim() + (end < line.Length ? "…" : "");
                }

                snippets.Add(snippet);
            }

            return snippets;
        }
    }
}
