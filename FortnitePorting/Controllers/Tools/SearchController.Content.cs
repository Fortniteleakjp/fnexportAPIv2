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
    public partial class SearchController
    {

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

            // The index buckets every path by extension, so the candidate universe is the union of the
            // allowed buckets rather than the whole build: a default query never even looks at the
            // extensions it would have rejected one path at a time.
            var index = FileIndex.For(_provider);
            var allowedExtensions = allowAll
                ? index.Extensions.ToList()
                : useDefault
                    ? PackageExtensions.Concat(TextExtensions).ToList()
                    : explicitExts;
            var allowedPackageExtensions = allowedExtensions.Where(PackageExtensions.Contains).ToList();

            bool IsAllowedPackage(ReadOnlySpan<char> keyExt)
            {
                foreach (var packageExt in allowedPackageExtensions)
                {
                    if (keyExt.Equals(packageExt, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }

            // Result cache: an identical query returns instantly. The mounted file count is part of the
            // key, so a new build / newly decrypted paks transparently invalidate stale results.
            var cacheKey = string.Concat(
                _scope, "sc|", _provider.Files.Count.ToString(), "|", needle, "|", caseSensitive ? "1" : "0",
                "|", dirPrefix ?? "", "|", pathContains ?? "", "|", extTrim,
                "|", maxScan.ToString(), "|", maxResults.ToString(), "|", snippetsPerFile.ToString());
            if (_cache.TryGetValue(cacheKey, out byte[]? cachedJson) && cachedJson != null)
            {
                _logger.LogInformation("Content search cache hit: {Query}", needle);
                return File(cachedJson, JsonResponse.ContentType);
            }

            _logger.LogInformation("Content search cache miss: {Query}; byte cache enabled={Enabled}, budget={Budget}",
                needle,
                ContentCacheEnabled,
                ContentCacheUnbounded ? "unlimited" : $"{ContentCacheBudget / (1024 * 1024)} MB");

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

            // Pass 1: classify every allowed candidate. The directory filter is the index range the
            // sorted paths already provide, and the extension of a bucket is known up front, so nothing
            // is recomputed per file.
            var (dirStart, dirEnd) = index.PrefixRange(dirPrefix);
            foreach (var extension in allowedExtensions)
            {
                var isText = TextExtensions.Contains(extension);
                foreach (var i in index.Enumerate(dirPrefix, new[] { extension }))
                {
                    var key = index.PathAt(i);
                    if (!string.IsNullOrEmpty(pathContains) && !key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;

                    if (key.Contains(needle, StringComparison.OrdinalIgnoreCase)) AddTo(pathBucket, key);
                    else if (isText) AddTo(textBucket, key);
                    else AddTo(assetBucket, key);
                }
            }

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
                // A scope is a directory prefix, so the index hands back exactly the paths under it
                // instead of the build being walked once per query. Overlapping scopes can offer the
                // same path twice, which the dedupe below removes.
                var relatedLimitReached = false;
                foreach (var scope in scopes)
                {
                    var (scopeStart, scopeEnd) = index.PrefixRange(scope);
                    for (var i = scopeStart; i < scopeEnd; i++)
                    {
                        if (relatedBucket.Count >= MaxContentCandidates)
                        {
                            candidateLimitReached = relatedLimitReached = true;
                            break;
                        }

                        if (i < dirStart || i >= dirEnd) continue;
                        if (!IsAllowedPackage(index.ExtensionAt(i))) continue;                     // related = assets only

                        var key = index.PathAt(i);
                        if (key.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;     // already in pathBucket
                        if (inPath.Contains(key)) continue;
                        if (!string.IsNullOrEmpty(pathContains) && !key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;

                        relatedBucket.Add(key);
                    }

                    if (relatedLimitReached) break;
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
                cacheBytesMB = Interlocked.Read(ref _bytesCacheUsed) / (1024 * 1024),
                cacheFiles = BytesCache.Count,
                cachedAssetJsonFiles = AssetJsonCache.Count,
                resultCount = results.Count,
                results
            };

            var jsonOut = JsonResponse.Serialize(payload);

            // Cache the response unless the scan was cut short by client cancellation (partial result).
            if (!cancellationToken.IsCancellationRequested)
            {
                var contentCacheOptions = BuildCacheOptions(ResultCacheTtl);
                if (contentCacheOptions != null) _cache.Set(cacheKey, jsonOut, contentCacheOptions);
            }

            return File(jsonOut, JsonResponse.ContentType);
        }


        /// <summary>
        /// Loads an asset and serializes every export to an indented JSON string. Returns null on failure.
        /// </summary>
        private string? TryLoadAssetJson(string path)
        {
            EnsureBytesCacheVersion();
            var cacheKey = $"{_scope}{path}";
            if (_contentCacheUsable && AssetJsonCache.TryGetValue(cacheKey, out var cachedJson))
            {
                return cachedJson.Length == 0 ? null : cachedJson;
            }

            try
            {
                // Through the index: the provider's own lookup sorts its whole archive list on every call,
                // which is ruinous when a scan parses thousands of matched assets.
                if (!FileIndex.For(_provider).TryGetFile(path, out var gameFile))
                {
                    if (_contentCacheUsable) AssetJsonCache.TryAdd(cacheKey, string.Empty);
                    return null;
                }

                var package = _provider.LoadPackage(gameFile);
                var exports = package.GetExports().ToList();
                if (exports.Count == 0)
                {
                    if (_contentCacheUsable) AssetJsonCache.TryAdd(cacheKey, string.Empty);
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

                var serialized = array.Count > 0 ? array.ToString(Formatting.Indented) : string.Empty;
                if (_contentCacheUsable) AssetJsonCache.TryAdd(cacheKey, serialized);
                return serialized.Length == 0 ? null : serialized;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content search: failed to load {Path}", path);
                if (_contentCacheUsable) AssetJsonCache.TryAdd(cacheKey, string.Empty);
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
