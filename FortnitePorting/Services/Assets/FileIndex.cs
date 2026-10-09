using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;

namespace FortnitePorting.Services;

/// <summary>
/// A sorted, immutable snapshot of one build's virtual paths, built once per mounted build and shared
/// by every request that reads it.
/// <para>
/// Without it each endpoint walked <see cref="FileProviderDictionary.Keys"/> from scratch — a million
/// paths per request, plus a file-name/extension substring per path — and every single-file lookup paid
/// <see cref="FileProviderDictionary.TryGetValue"/>, which sorts the whole archive list on each call.
/// The index answers the same questions from precomputed data:
/// </para>
/// <list type="bullet">
///   <item>a directory filter is a binary search over the sorted paths instead of a full scan;</item>
///   <item>an extension filter reads a precomputed bucket (the .locres/.ini universes are a few
///         thousand paths, not a million);</item>
///   <item>file names and extensions are exposed as spans over the existing path strings, so matching
///         allocates nothing;</item>
///   <item>a path lookup is one hash probe.</item>
/// </list>
/// <para>
/// The snapshot belongs to the build it was built from: it is rebuilt whenever the mounted file count
/// changes (newly decrypted paks, a provider rebuild) and dropped wholesale by
/// <see cref="CacheRegistry"/> when the provider is rebuilt for a new Fortnite build.
/// </para>
/// </summary>
public sealed class FileIndex
{
    /// <summary>Every distinct virtual path, sorted case-insensitively. Never mutated after construction.</summary>
    private readonly string[] _keys;

    /// <summary>Index of the first character of each path's file name (the char after the last '/').</summary>
    private readonly int[] _nameStart;

    /// <summary>Index of each path's extension dot, or -1 when the file name has no extension.</summary>
    private readonly int[] _extStart;

    /// <summary>Extension (with the dot, "" for none) to the ascending path indices that carry it.</summary>
    private readonly Dictionary<string, int[]> _byExtension;

    /// <summary>The file a lookup returns for a path, resolved once with the provider's own precedence.</summary>
    private readonly Dictionary<string, GameFile> _files;

    /// <summary>Built on first use by <see cref="TryResolvePluginAsset"/>; most requests never need it.</summary>
    private Dictionary<string, string>? _pluginSuffixes;
    private readonly object _pluginSuffixLock = new();
    private readonly Dictionary<string, string[]> _nameQueries = new(StringComparer.Ordinal);
    private int _cachedQueryPaths;

    /// <summary>The mounted file count this snapshot was built from; a change means it is stale.</summary>
    public int MountedFileCount { get; }

    /// <summary>The number of distinct paths in the index.</summary>
    public int Count => _keys.Length;

    /// <summary>Every distinct path, sorted case-insensitively.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Every extension present in the build (with the dot; "" covers the files without one).</summary>
    public IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    private FileIndex(FileProviderDictionary files)
    {
        MountedFileCount = files.Count;

        // Enumerating the provider yields archives from the highest read order down, exactly like
        // FileProviderDictionary.TryGetValue walks them, so the first entry seen for a path is the one
        // a lookup would return: keeping the first occurrence reproduces the provider's precedence.
        var resolved = new Dictionary<string, GameFile>(Math.Max(16, MountedFileCount), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            resolved.TryAdd(entry.Key, entry.Value);
        }
        _files = resolved;

        var keys = new string[resolved.Count];
        resolved.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
        _keys = keys;

        _nameStart = new int[keys.Length];
        _extStart = new int[keys.Length];

        // Bucket by extension while the name/extension offsets are computed, so the whole index is a
        // single pass over the sorted paths. Every path lands in exactly one bucket (the files without
        // an extension go into ""), which lets a caller cover the whole build by walking the buckets.
        var buckets = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var slash = key.LastIndexOf('/');
            var dot = key.LastIndexOf('.');
            var hasExtension = dot > slash;

            _nameStart[i] = slash + 1;
            _extStart[i] = hasExtension ? dot : -1;

            var extension = hasExtension ? key[dot..] : string.Empty;
            if (!buckets.TryGetValue(extension, out var bucket))
            {
                bucket = new List<int>();
                buckets[extension] = bucket;
            }
            bucket.Add(i);
        }

        _byExtension = new Dictionary<string, int[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (extension, bucket) in buckets)
        {
            _byExtension[extension] = bucket.ToArray();
        }
    }

    // --- Per-path accessors ---------------------------------------------------------------------
    // The name/stem/extension accessors return spans over the path string the index already holds, so
    // a scan over a million paths allocates nothing.

    /// <summary>The full virtual path at <paramref name="index"/>.</summary>
    public string PathAt(int index) => _keys[index];

    /// <summary>The file name at <paramref name="index"/> (no allocation).</summary>
    public ReadOnlySpan<char> NameAt(int index) => _keys[index].AsSpan(_nameStart[index]);

    /// <summary>The file name without its extension at <paramref name="index"/> (no allocation).</summary>
    public ReadOnlySpan<char> StemAt(int index)
    {
        var key = _keys[index];
        var start = _nameStart[index];
        var end = _extStart[index] >= 0 ? _extStart[index] : key.Length;
        return key.AsSpan(start, end - start);
    }

    /// <summary>The extension at <paramref name="index"/>, dot included, empty when there is none.</summary>
    public ReadOnlySpan<char> ExtensionAt(int index)
    {
        var dot = _extStart[index];
        return dot >= 0 ? _keys[index].AsSpan(dot) : ReadOnlySpan<char>.Empty;
    }

    // --- Lookups --------------------------------------------------------------------------------

    /// <summary>
    /// Resolves a path to its file in one hash probe, with the same precedence
    /// <see cref="FileProviderDictionary.TryGetValue"/> applies.
    /// </summary>
    public bool TryGetFile(string path, [MaybeNullWhen(false)] out GameFile file) => _files.TryGetValue(path, out file);

    /// <summary>True when the build contains this exact path.</summary>
    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>The ascending path indices carrying this extension; empty when the build has none.</summary>
    public int[] Bucket(string extension) =>
        _byExtension.TryGetValue(extension, out var bucket) ? bucket : Array.Empty<int>();

    /// <summary>
    /// The half-open index range [Start, End) of the paths starting with <paramref name="prefix"/> —
    /// a directory when it ends with '/'. The paths are sorted case-insensitively, so such a prefix is
    /// always one contiguous run and its start is a binary search rather than a scan. Null or empty
    /// selects the whole build.
    /// </summary>
    public (int Start, int End) PrefixRange(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return (0, _keys.Length);

        var start = LowerBound(prefix);
        return (start, UpperBound(prefix, start));
    }

    /// <summary>
    /// The indices of every path that passes the directory and extension filters, in the cheapest way
    /// available: an extension filter walks that extension's bucket, a bare directory filter walks the
    /// directory's range, and an unfiltered request walks the build once.
    /// </summary>
    /// <param name="directoryPrefix">Directory to restrict to (ending with '/'), or null for all.</param>
    /// <param name="extensions">Extensions to restrict to (with the dot), or null/empty for all.</param>
    /// <param name="pathPrefix">
    /// A second prefix the path itself must start with — a prefix or exact query, which the sorted
    /// index answers as a range instead of a scan. Intersected with the directory range.
    /// </param>
    public IEnumerable<int> Enumerate(string? directoryPrefix, IReadOnlyList<string>? extensions, string? pathPrefix = null)
    {
        var (start, end) = PrefixRange(directoryPrefix);
        if (pathPrefix != null)
        {
            var (prefixStart, prefixEnd) = PrefixRange(pathPrefix);
            start = Math.Max(start, prefixStart);
            end = Math.Min(end, prefixEnd);
        }
        if (start >= end) yield break;

        if (extensions == null || extensions.Count == 0)
        {
            for (var i = start; i < end; i++) yield return i;
            yield break;
        }

        // The buckets are disjoint (one per extension, compared case-insensitively), so concatenating
        // them cannot produce a duplicate. The result is only sorted within a bucket; every caller that
        // presents paths to a user sorts the matches it kept, which is a far smaller set.
        foreach (var extension in extensions)
        {
            var bucket = Bucket(extension);
            var position = Array.BinarySearch(bucket, start);
            if (position < 0) position = ~position;
            for (; position < bucket.Length && bucket[position] < end; position++)
            {
                yield return bucket[position];
            }
        }
    }

    public IReadOnlyList<string> MatchingNames(string[] prefixes, string extension, string[] excludedPrefixes, string[] excludedPaths)
    {
        var key = System.Text.Json.JsonSerializer.Serialize(new { prefixes, extension, excludedPrefixes, excludedPaths });
        lock (_nameQueries)
        {
            if (_nameQueries.TryGetValue(key, out var cached)) return cached;
        }

        var matches = new List<string>();
        IReadOnlyList<string>? extensions = string.IsNullOrEmpty(extension) ? null
            : new[] { extension.StartsWith('.') ? extension : "." + extension };
        foreach (var i in Enumerate(null, extensions))
        {
            var path = PathAt(i);
            if (excludedPaths.Any(fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase))) continue;
            var name = NameAt(i);
            if (StartsWithAny(name, excludedPrefixes) || !StartsWithAny(name, prefixes)) continue;
            matches.Add(path);
        }
        // One extension bucket, and the unfiltered index, already use the display order.
        var result = matches.ToArray();
        lock (_nameQueries)
        {
            if (_nameQueries.TryGetValue(key, out var cached)) return cached;
            if (_nameQueries.Count < 16 && _cachedQueryPaths + result.Length <= 250000)
            {
                _nameQueries.Add(key, result);
                _cachedQueryPaths += result.Length;
            }
        }
        return result;
    }

    private static bool StartsWithAny(ReadOnlySpan<char> name, string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Resolves a plugin asset addressed by its stable "/PluginName/Content/Path.uasset" tail, which is
    /// how an asset reference identifies one: the virtual mount can carry any number of feature folders
    /// before the plugin name. Built lazily, because only reference resolution needs it.
    /// </summary>
    /// <returns>The full virtual path, or null when no plugin asset ends with that tail.</returns>
    public string? TryResolvePluginAsset(string suffixWithExtension)
    {
        var suffixes = _pluginSuffixes;
        if (suffixes == null)
        {
            lock (_pluginSuffixLock)
            {
                suffixes = _pluginSuffixes ??= BuildPluginSuffixes();
            }
        }

        return suffixes.TryGetValue(suffixWithExtension, out var path) ? path : null;
    }

    private Dictionary<string, string> BuildPluginSuffixes()
    {
        const string plugins = "/Plugins/";
        const string content = "/Content/";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".uasset", ".umap" })
        {
            foreach (var i in Bucket(extension))
            {
                var key = _keys[i];
                if (key.IndexOf(plugins, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // A path can carry several "/Content/" segments and the caller's tail may be anchored at
                // any of them, so every one is indexed. The paths are sorted, so when two mounts expose
                // the same tail the first one alphabetically wins, deterministically.
                var at = key.IndexOf(content, StringComparison.OrdinalIgnoreCase);
                while (at > 0)
                {
                    var root = key.LastIndexOf('/', at - 1);
                    if (root >= 0) map.TryAdd(key[root..], key);
                    at = key.IndexOf(content, at + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return map;
    }

    /// <summary>The first index whose path is not ordered before <paramref name="value"/>.</summary>
    private int LowerBound(string value)
    {
        int low = 0, high = _keys.Length;
        while (low < high)
        {
            var mid = (int)(((uint)low + (uint)high) >> 1);
            if (StringComparer.OrdinalIgnoreCase.Compare(_keys[mid], value) < 0) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>
    /// The end of the run starting at <paramref name="start"/> (the prefix's lower bound). From there on
    /// the paths carry the prefix until they stop doing so and never again, so the end is a binary
    /// search too — a directory holding a hundred thousand files is bounded without touching any of them.
    /// </summary>
    private int UpperBound(string prefix, int start)
    {
        int low = start, high = _keys.Length;
        while (low < high)
        {
            var mid = (int)(((uint)low + (uint)high) >> 1);
            if (_keys[mid].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // --- Snapshot cache -------------------------------------------------------------------------

    private sealed class Holder
    {
        public FileIndex? Index;
    }

    // Keyed by the provider's own file dictionary and held weakly, so an archived build's index is
    // collected together with the build it describes.
    private static readonly ConditionalWeakTable<FileProviderDictionary, Holder> Snapshots = new();

    /// <summary>
    /// The index for this provider, building it if there is none or the mounted file count has moved
    /// since (paks decrypted later, or a provider rebuilt for a new build).
    /// </summary>
    public static FileIndex For(IFileProvider provider) => For(provider.Files);

    /// <inheritdoc cref="For(IFileProvider)"/>
    public static FileIndex For(FileProviderDictionary files)
    {
        var holder = Snapshots.GetOrCreateValue(files);
        var snapshot = Volatile.Read(ref holder.Index);
        if (snapshot != null && snapshot.MountedFileCount == files.Count) return snapshot;

        lock (holder)
        {
            snapshot = holder.Index;
            if (snapshot != null && snapshot.MountedFileCount == files.Count) return snapshot;

            var stopwatch = Stopwatch.StartNew();
            var built = new FileIndex(files);
            Volatile.Write(ref holder.Index, built);
            Console.WriteLine($"  Indexed {built.Count} paths ({built.Extensions.Count} extensions) in {stopwatch.ElapsedMilliseconds} ms");
            return built;
        }
    }

    /// <summary>Drops every snapshot. Registered with <see cref="CacheRegistry"/>.</summary>
    public static void ClearAll() => Snapshots.Clear();
}
