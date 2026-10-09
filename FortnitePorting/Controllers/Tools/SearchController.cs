using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    /// <summary>
    /// Full-text search endpoints over all files: fast path/name search (substring, prefix,
    /// suffix, exact, wildcard, glob, regex, tokens) and a bounded content search inside parsed
    /// asset properties.
    /// </summary>
    [ApiController]
    [VersionAware]
    [Route("api/v1/search")]
    public partial class SearchController : ControllerBase
    {
        private readonly RequestBuildProvider _build;
        // Read lazily: the version filter binds the provider after controller construction.
        private IFileProvider _provider => _build.Provider;

        /// <summary>Cache-key prefix that keeps an older build's content out of the live cache.</summary>
        private string _scope => _build.CacheScope;

        /// <summary>
        /// Whether this request may use the shared byte/export caches. They are scoped to the request generation and path
        /// and are reset whenever the mounted file count changes, so they belong to the live build:
        /// a request reading an older build bypasses them rather than poisoning or clearing them.
        /// </summary>
        private bool _contentCacheUsable => ContentCacheEnabled && _build.IsLive;
        private readonly ILogger<SearchController> _logger;
        private readonly IMemoryCache _cache;

        // Cache of decompressed file bytes, shared across requests so a second (different) content
        // query does not have to re-read/re-decompress the same files. Bounded by ContentCacheBudget.
        private static readonly ConcurrentDictionary<string, byte[]> BytesCache = new(StringComparer.OrdinalIgnoreCase);
        // Cache serialized package exports per file so a different query does not re-parse a matched asset.
        // An empty string is used as the cached value for a file that has no serializable exports.
        private static readonly ConcurrentDictionary<string, string> AssetJsonCache = new(StringComparer.OrdinalIgnoreCase);
        private static long _bytesCacheUsed;
        private static int _bytesCacheFileCount = -1;
        private static readonly long ContentCacheBudget = ResolveCacheBudget();
        private static readonly bool ContentCacheEnabled = ContentCacheBudget > 0;
        private static readonly bool ContentCacheUnbounded = ContentCacheBudget == long.MaxValue;
        // How many files content search scans concurrently (defaults to every core).
        private static readonly int ScanParallelism = ResolveParallelism();
        // How long a content-search response is cached for an identical query. Long by default:
        // a cached response can only ever belong to the mounted build, because the mounted file
        // count is part of the cache key and a provider reload clears the whole response cache
        // through CacheRegistry. Scanning is by far the expensive part, so there is nothing to
        // gain from expiring a still-valid result.
        private static readonly TimeSpan ResultCacheTtl = ResolveCacheTtl("SEARCH_CONTENT_CACHE_MINUTES");
        // How long a path-search response is cached for an identical query.
        private static readonly TimeSpan PathResultCacheTtl = ResolveCacheTtl("SEARCH_PATH_CACHE_MINUTES");
        // Ceiling on how long a single cached response may live even if it keeps being hit, so a
        // hot query cannot pin its memory forever.
        private static readonly TimeSpan ResultCacheMaxLifetime = ResolveCacheTtl("SEARCH_CACHE_MAX_MINUTES", DefaultCacheMaxMinutes);
        // Default sliding lifetime of a cached search response (24 hours).
        private const int DefaultCacheMinutes = 24 * 60;
        // Default absolute lifetime of a cached search response (7 days).
        private const int DefaultCacheMaxMinutes = 7 * 24 * 60;
        // Lifetime granted to a result that the wall-clock budget cut short, so a retry can produce
        // the complete answer instead of being served the partial one for the rest of the day.
        private static readonly TimeSpan PartialResultCacheTtl = TimeSpan.FromMinutes(5);

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

        public SearchController(RequestBuildProvider provider, ILogger<SearchController> logger, IMemoryCache cache)
        {
            _build = provider;
            _logger = logger;
            _cache = cache;
        }


        /// <summary>
        /// Searches the paths/names of all loaded files for a word, string, or codename.
        /// </summary>
        /// <param name="q">The word, string, or codename to search for (required).</param>
        /// <param name="mode">Match mode: contains (default) / prefix / suffix / exact / wildcard / glob / regex / tokens.
        /// glob is path-aware: * and ? stop at '/', ** crosses directories, [abc] is a character class, {a,b} an alternation.</param>
        /// <param name="field">Match target: path (default) / name / stem (without extension).</param>
        /// <param name="caseSensitive">Match case-sensitively (default false).</param>
        /// <param name="ext">Filter by extension (comma-separated, e.g. .uasset,.umap; empty matches all).</param>
        /// <param name="dir">Restrict to paths under this directory (e.g. FortniteGame/Content/Athena).</param>
        /// <param name="dedupe">Collapse cooked-asset duplicates (.uasset/.uexp/.ubulk, etc.) into one (default false).</param>
        /// <param name="page">The page number (1-based).</param>
        /// <param name="pageSize">The number of items per page (maximum 10000).</param>
        /// <returns>The matching files (path / name / ext) and the total count.</returns>
        /// <param name="target">path for path matching, or content for full-text content search.</param>
        /// <param name="pathContains">Additional path-fragment filter for content candidates.</param>
        /// <param name="maxScan">Maximum candidates scanned during content search.</param>
        /// <param name="maxResults">Maximum files returned by content search.</param>
        /// <param name="snippetsPerFile">Maximum snippets per matching content file.</param>
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
            CancellationToken cancellationToken = default,
            [FromQuery] string target = "path",
            [FromQuery] string? pathContains = null,
            [FromQuery] int maxScan = 3_000_000,
            [FromQuery] int maxResults = 50,
            [FromQuery] int snippetsPerFile = 3)
        {
            target = (target ?? "path").Trim().ToLowerInvariant();
            if (target == "content")
                return SearchContent(q, dir, pathContains, ext ?? "", caseSensitive, maxScan, maxResults, snippetsPerFile, cancellationToken);
            if (target != "path") return BadRequest(new { message = "target must be path or content." });
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
            if ((mode == "regex" || mode == "wildcard" || mode == "glob") && needle.Length > MaxPatternLength)
            {
                return BadRequest(new { message = $"The pattern is too long (max {MaxPatternLength} characters for regex/wildcard/glob)." });
            }

            PathMatcher matcher;
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
            var pathCacheKey = string.Concat(
                _scope, "sp|", _provider.Files.Count.ToString(), "|",
                JsonConvert.SerializeObject(new
                {
                    query = needle,
                    mode,
                    field,
                    caseSensitive,
                    extensions,
                    directory = dirPrefix,
                    dedupe,
                    page,
                    pageSize
                }));

            if (_cache.TryGetValue(pathCacheKey, out byte[]? cachedPathJson) && cachedPathJson != null)
            {
                _logger.LogInformation("Path search cache hit: {Query}", needle);
                return File(cachedPathJson, JsonResponse.ContentType);
            }

            _logger.LogInformation("Path search cache miss: {Query}", needle);

            var matches = new List<string>();
            var truncated = false;
            // Set only when the wall-clock budget stopped the scan. Unlike the match cap, that
            // outcome depends on machine load rather than the query, so such a result must not be
            // cached for the long lifetime a complete one gets.
            var timedOut = false;
            var stopwatch = Stopwatch.StartNew();
            long seen = 0;

            // A prefix (and its special case, an exact path) is itself a range of the sorted index, so
            // such a query is answered by a binary search instead of a scan. The matcher still runs, so
            // a case-sensitive query still rejects what only matches case-insensitively.
            var pathPrefix = field == "path" && (mode == "prefix" || mode == "exact") ? needle : null;

            // The index applies both filters without touching the rest of the build: the directory is a
            // contiguous range of the sorted paths, and each extension is a precomputed bucket. What is
            // left is matched against spans over the path strings, so the scan allocates nothing.
            var index = FileIndex.For(_provider);
            foreach (var i in index.Enumerate(dirPrefix, hasExtFilter ? extensions : null, pathPrefix))
            {
                if (++seen % ScanCheckInterval == 0)
                {
                    if (cancellationToken.IsCancellationRequested || stopwatch.Elapsed > ScanTimeBudget)
                    {
                        truncated = true;
                        timedOut = true;
                        break;
                    }
                }

                var matched = field switch
                {
                    "name" => matcher(index.NameAt(i)),
                    "stem" => matcher(index.StemAt(i)),
                    _ => matcher(index.PathAt(i))
                };

                if (matched)
                {
                    matches.Add(index.PathAt(i));
                    if (matches.Count >= MaxCollectedMatches)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            // The index holds each virtual path once, so only the ordering is left to do (a bucketed
            // scan yields one extension after another).
            IEnumerable<string> ordered = matches.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

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
            var paged = PageSlice.From(orderedList, page, pageSize)
                .Select(k => new
                {
                    path = k,
                    name = GetFileName(k),
                    ext = GetExtension(k)
                })
                .ToList();

            var response = new
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
            };
            var responseJson = JsonResponse.Serialize(response);
            if (!cancellationToken.IsCancellationRequested)
            {
                var pathCacheOptions = BuildCacheOptions(timedOut ? Min(PathResultCacheTtl, PartialResultCacheTtl) : PathResultCacheTtl);
                if (pathCacheOptions != null) _cache.Set(pathCacheKey, responseJson, pathCacheOptions);
            }

            return File(responseJson, JsonResponse.ContentType);
        }


        // CONTENT_CACHE_MB: decompressed-bytes cache budget in MB. The default is unlimited so every
        // file read during content search remains cached until the mounted file set changes.
        // Set 0 to disable it, or set a positive value to impose a manual limit.
        private static long ResolveCacheBudget()
        {
            var v = Environment.GetEnvironmentVariable("CONTENT_CACHE_MB")?.Trim();
            if (!string.IsNullOrEmpty(v) && long.TryParse(v, out var mb) && mb >= 0)
            {
                return mb * 1024L * 1024L;
            }
            return long.MaxValue;
        }

        // SEARCH_CONTENT_CACHE_MINUTES / SEARCH_PATH_CACHE_MINUTES / SEARCH_CACHE_MAX_MINUTES: how long a
        // search response stays cached, in minutes. 0 disables caching for that kind of search.
        private static TimeSpan ResolveCacheTtl(string variable, int defaultMinutes = DefaultCacheMinutes)
        {
            var v = Environment.GetEnvironmentVariable(variable)?.Trim();
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var minutes) && minutes >= 0)
            {
                return TimeSpan.FromMinutes(minutes);
            }
            return TimeSpan.FromMinutes(defaultMinutes);
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        /// <summary>
        /// Cache options for a search response: a long sliding lifetime so a repeated query keeps
        /// hitting the cache, bounded by an absolute lifetime so it cannot be pinned indefinitely.
        /// Returns null when caching is turned off for this kind of search.
        /// </summary>
        private static MemoryCacheEntryOptions? BuildCacheOptions(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero) return null;

            var options = new MemoryCacheEntryOptions { SlidingExpiration = ttl };
            if (ResultCacheMaxLifetime > TimeSpan.Zero && ResultCacheMaxLifetime > ttl)
            {
                options.AbsoluteExpirationRelativeToNow = ResultCacheMaxLifetime;
            }
            return options;
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
            EnsureBytesCacheVersion();
            var cacheKey = $"{_scope}{path}";
            if (_contentCacheUsable && BytesCache.TryGetValue(cacheKey, out var hit)) return hit;

            // The paths handed here come from the index, so resolve the file there: the provider's own
            // path overload sorts its whole archive list up to three times per call, which a parallel
            // scan over thousands of files feels immediately.
            if (!FileIndex.For(_provider).TryGetFile(path, out var gameFile)) return null;
            if (!_provider.TrySaveAsset(gameFile, out var bytes) || bytes == null) return null;

            var canCache = _contentCacheUsable &&
                           (ContentCacheUnbounded ||
                            (Interlocked.Read(ref _bytesCacheUsed) <= long.MaxValue - bytes.Length &&
                             Interlocked.Read(ref _bytesCacheUsed) + bytes.Length <= ContentCacheBudget));
            if (canCache)
            {
                if (BytesCache.TryAdd(cacheKey, bytes)) Interlocked.Add(ref _bytesCacheUsed, bytes.Length);
            }
            return bytes;
        }

        /// <summary>
        /// Drops every cached byte blob and serialized export. Called when the provider is rebuilt for a
        /// new build: the cached content belongs to the previous build's containers.
        /// </summary>
        public static void ClearCaches()
        {
            lock (BytesCache)
            {
                BytesCache.Clear();
                AssetJsonCache.Clear();
                Interlocked.Exchange(ref _bytesCacheUsed, 0);
                Volatile.Write(ref _bytesCacheFileCount, -1);
            }
        }

        private void EnsureBytesCacheVersion()
        {
            if (!_contentCacheUsable) return;

            var fileCount = _provider.Files.Count;
            if (Volatile.Read(ref _bytesCacheFileCount) == fileCount) return;

            lock (BytesCache)
            {
                if (_bytesCacheFileCount == fileCount) return;
                BytesCache.Clear();
                AssetJsonCache.Clear();
                Interlocked.Exchange(ref _bytesCacheUsed, 0);
                Volatile.Write(ref _bytesCacheFileCount, fileCount);
                _logger.LogInformation("Search byte cache reset for mounted file count {FileCount}", fileCount);
            }
        }

    }
}
