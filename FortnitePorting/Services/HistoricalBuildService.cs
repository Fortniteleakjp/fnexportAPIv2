using System.Diagnostics;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;
using CUE4Parse.UE4.Versions;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FortnitePorting.Models;
using ZlibngDotNet;

namespace FortnitePorting.Services;

/// <summary>One archived build that is currently materialized in memory.</summary>
public sealed class LoadedHistoricalBuild : IDisposable
{
    public required string BuildVersion { get; init; }
    public required DefaultFileProvider Provider { get; init; }
    public required FBuildPatchAppManifest Manifest { get; init; }
    public required DateTime LoadedUtc { get; init; }
    public required double LoadSeconds { get; init; }

    private long _lastUsedTicks = DateTime.UtcNow.Ticks;
    private int _inFlight;
    private int _disposed;
    private volatile bool _retired;

    /// <summary>When this build was last read from, used by the idle sweep.</summary>
    public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    /// <summary>Requests currently reading from this build. Never evicted while this is above zero.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    internal void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

    /// <summary>Registers a reader. Only ever called while the owner holds its dictionary lock.</summary>
    internal void Enter()
    {
        Interlocked.Increment(ref _inFlight);
        Touch();
    }

    /// <summary>Releases a reader, disposing the build when it was retired while still in use.</summary>
    internal void Exit()
    {
        Touch();
        if (Interlocked.Decrement(ref _inFlight) == 0 && _retired)
        {
            DisposeNow();
        }
    }

    /// <summary>
    /// Takes this build out of service. Disposal is deferred until the last in-flight reader is done,
    /// so evicting a build cannot pull the provider out from under a request that is mid-read.
    /// </summary>
    internal void Retire()
    {
        _retired = true;
        if (Volatile.Read(ref _inFlight) == 0)
        {
            DisposeNow();
        }
    }

    private void DisposeNow()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Provider.Dispose();
        }
    }

    public void Dispose() => Retire();
}

/// <summary>
/// A borrowed provider. Holding one keeps its build mounted; disposing it lets the build be evicted
/// again. The live build is leased too, so callers do not have to special-case it.
/// </summary>
public sealed class BuildLease : IDisposable
{
    private readonly LoadedHistoricalBuild? _build;

    internal BuildLease(string buildVersion, IFileProvider provider, LoadedHistoricalBuild? build)
    {
        BuildVersion = buildVersion;
        Provider = provider;
        _build = build;
    }

    public string BuildVersion { get; }
    public IFileProvider Provider { get; }

    /// <summary>True for the live build, which is owned by the application rather than by this lease.</summary>
    public bool IsLive => _build == null;

    public void Dispose() => _build?.Exit();
}

/// <summary>
/// Materializes previously archived builds so their files can be read alongside the live build.
/// <para>
/// Nothing is kept warm by default: a historical build is mounted on first use and dropped again once
/// it goes idle, because a mounted Fortnite build costs a few GB of file index. At most
/// <see cref="MaxLoaded"/> of them exist at a time (least-recently-used is evicted first), so serving
/// an old version never puts the live build at risk.
/// </para>
/// </summary>
public sealed class HistoricalBuildService : IAsyncDisposable
{
    private readonly BuildHistoryStore _store;
    private readonly Zlibng _zlibng;
    private readonly IFileProvider _liveProvider;
    private readonly ManifestService _manifestService;

    private readonly Dictionary<string, LoadedHistoricalBuild> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly Timer _idleSweep;

    /// <summary>How many archived builds may be mounted at once (env <c>HISTORICAL_BUILDS_MAX</c>).</summary>
    public int MaxLoaded { get; }

    /// <summary>How long an archived build may sit unused before it is dropped (env <c>HISTORICAL_BUILD_IDLE_MINUTES</c>, 0 disables).</summary>
    public TimeSpan IdleTimeout { get; }

    public HistoricalBuildService(BuildHistoryStore store, Zlibng zlibng, IFileProvider liveProvider,
        ManifestService manifestService)
    {
        _store = store;
        _zlibng = zlibng;
        _liveProvider = liveProvider;
        _manifestService = manifestService;

        MaxLoaded = int.TryParse(Environment.GetEnvironmentVariable("HISTORICAL_BUILDS_MAX"), out var max) && max >= 1
            ? max
            : 1;

        IdleTimeout = int.TryParse(Environment.GetEnvironmentVariable("HISTORICAL_BUILD_IDLE_MINUTES"), out var idle) && idle >= 0
            ? TimeSpan.FromMinutes(idle)
            : TimeSpan.FromMinutes(30);

        _idleSweep = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Build versions currently mounted, newest use first.</summary>
    public IReadOnlyList<LoadedHistoricalBuild> Loaded
    {
        get
        {
            lock (_loaded)
            {
                return _loaded.Values.OrderByDescending(x => x.LastUsedUtc).ToList();
            }
        }
    }

    /// <summary>
    /// Returns an already-mounted archived build without mounting anything. This is what the
    /// "return that build's content only if that build is loaded" behaviour of the read endpoints uses.
    /// </summary>
    public bool TryGetLoaded(string buildVersion, out LoadedHistoricalBuild build)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(buildVersion, out var found))
            {
                found.Touch();
                build = found;
                return true;
            }
        }

        build = null!;
        return false;
    }

    /// <summary>
    /// Borrows an already-mounted archived build. The returned lease must be disposed; until it is,
    /// the build cannot be evicted or swept away underneath the caller.
    /// </summary>
    public bool TryLease(string buildVersion, out BuildLease lease)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(buildVersion, out var found))
            {
                found.Enter();
                lease = new BuildLease(buildVersion, found.Provider, found);
                return true;
            }
        }

        lease = null!;
        return false;
    }

    /// <summary>Mounts the build if needed and borrows it. The returned lease must be disposed.</summary>
    public async Task<BuildLease> LeaseAsync(string buildVersion, CancellationToken cancellationToken = default)
    {
        if (TryLease(buildVersion, out var lease))
        {
            return lease;
        }

        await LoadAsync(buildVersion, cancellationToken);

        if (TryLease(buildVersion, out lease))
        {
            return lease;
        }

        // Only reachable if the build was evicted between the mount and the lease, which needs another
        // load to have raced this one. Mounting again is correct and, in practice, never happens twice.
        await LoadAsync(buildVersion, cancellationToken);
        if (TryLease(buildVersion, out lease))
        {
            return lease;
        }

        throw new InvalidOperationException(
            $"Build '{buildVersion}' keeps being evicted before it can be read. " +
            "Raise HISTORICAL_BUILDS_MAX so more than one archived build can be mounted at a time.");
    }

    /// <summary>True when this build can still be mounted (its manifest survived the retention policy).</summary>
    public bool CanLoad(string buildVersion) => _store.GetManifestPath(buildVersion) != null;

    /// <summary>
    /// Parses manifest bytes far enough to read which build they describe, without mounting anything.
    /// Used when a manifest file is imported, so the archive is keyed by the build the manifest itself
    /// names rather than by whatever the file happens to be called.
    /// </summary>
    public (string BuildVersion, string ManifestId) InspectManifest(byte[] manifestBytes)
    {
        var manifest = FBuildPatchAppManifest.Deserialize(manifestBytes, options =>
        {
            options.ChunkBaseUrl = ManifestService.ChunkBaseUrl;
            options.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
            options.DecompressorState = _zlibng;
            options.ChunkCacheDirectory = Path.GetTempPath();
            options.CacheChunksAsIs = false;
        });

        var buildVersion = manifest.Meta.BuildVersion;
        if (string.IsNullOrWhiteSpace(buildVersion))
        {
            throw new InvalidOperationException("The manifest does not name a build version.");
        }

        // The real manifest id is the CDN file name, which an imported file no longer carries. A
        // content hash stands in for it: it is stable, it distinguishes two manifests of the same
        // build version, and the "imported:" prefix keeps it from being mistaken for Epic's own id.
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(manifestBytes));
        return (buildVersion, $"imported:{digest}");
    }

    /// <summary>
    /// Mounts an archived build, reusing it when it is already loaded. Throws when the build was never
    /// archived or its data has been deleted by the retention policy.
    /// </summary>
    public async Task<LoadedHistoricalBuild> LoadAsync(string buildVersion, CancellationToken cancellationToken = default)
    {
        if (TryGetLoaded(buildVersion, out var already))
        {
            return already;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have finished the same load while we waited for the lock.
            if (TryGetLoaded(buildVersion, out already))
            {
                return already;
            }

            var manifestPath = _store.GetManifestPath(buildVersion)
                               ?? throw new InvalidOperationException(
                                   $"No archived manifest for '{buildVersion}'. Its data was deleted by the retention policy, " +
                                   "or the build was never seen by this instance.");

            EvictUntilRoomLocked();

            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine($"\n=== Mounting the archived build {buildVersion} ===");

            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var chunkCache = _store.ChunkCacheDirectoryFor(buildVersion);
            Directory.CreateDirectory(chunkCache);

            var manifest = FBuildPatchAppManifest.Deserialize(manifestBytes, options =>
            {
                options.ChunkBaseUrl = ManifestService.ChunkBaseUrl;
                options.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
                options.DecompressorState = _zlibng;
                options.ChunkCacheDirectory = chunkCache;
                options.CacheChunksAsIs = false;
            });

            var tempDir = Path.Combine(Path.GetTempPath(), "fortnite_manifest_dummy");
            Directory.CreateDirectory(tempDir);

            var provider = new DefaultFileProvider(
                tempDir,
                SearchOption.TopDirectoryOnly,
                versions: new VersionContainer(EGame.GAME_UE6_0),
                pathComparer: StringComparer.OrdinalIgnoreCase);

            // Old builds deserialize against the mapping of the build that shipped them; we only have
            // the live one. It is close enough for path/size/content reads, and the alternative — no
            // mapping at all — would fail far more assets.
            provider.MappingsContainer = _liveProvider.MappingsContainer;

            VfsLoader.LoadAllVfsFiles(provider, manifest);

            // Only now, with every container registered. SubmitKeys walks the containers that are still
            // unmounted and mounts the ones a submitted key opens — on an empty provider it matches
            // nothing and the keys are dropped, so submitting them any earlier does nothing at all.
            // VfsLoader has already submitted the live build's keys, which open whatever Fortnite has
            // not rotated since; these archived ones open the rest.
            SubmitArchivedKeys(provider, buildVersion);

            var loaded = new LoadedHistoricalBuild
            {
                BuildVersion = buildVersion,
                Provider = provider,
                Manifest = manifest,
                LoadedUtc = DateTime.UtcNow,
                LoadSeconds = stopwatch.Elapsed.TotalSeconds
            };

            lock (_loaded)
            {
                _loaded[buildVersion] = loaded;
            }

            Console.WriteLine($"✓ Mounted {buildVersion} in {stopwatch.Elapsed.TotalSeconds:F1}s " +
                              $"(files: {provider.Files.Count}, mounted VFS: {provider.MountedVfs.Count})\n");
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Drops a mounted archived build and frees its memory. Returns false when it was not loaded.</summary>
    public bool Unload(string buildVersion)
    {
        LoadedHistoricalBuild? build;
        lock (_loaded)
        {
            if (!_loaded.Remove(buildVersion, out build))
            {
                return false;
            }
        }

        DisposeQuietly(build);
        Console.WriteLine($"✓ Unmounted the archived build {buildVersion}");
        return true;
    }

    /// <summary>
    /// Records the AES keys the live build is mounted with, so the build stays readable after Fortnite
    /// rotates them. Called when a build is archived.
    /// </summary>
    public void ArchiveLiveKeys(string buildVersion)
    {
        if (_liveProvider is not CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider vfs)
        {
            return;
        }

        var keys = vfs.Keys
            .Where(kv => !kv.Value.IsDefault)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.KeyString, StringComparer.OrdinalIgnoreCase);

        _store.SaveKeys(buildVersion, keys);
    }

    private void SubmitArchivedKeys(DefaultFileProvider provider, string buildVersion)
    {
        var archived = _store.LoadKeys(buildVersion);
        if (archived.Count == 0)
        {
            Console.WriteLine($"  No archived AES keys for {buildVersion}; falling back to the live keys.");
            return;
        }

        var keys = new Dictionary<FGuid, FAesKey>();
        foreach (var (guidText, keyText) in archived)
        {
            try
            {
                keys[new FGuid(guidText)] = new FAesKey(keyText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Archived key {guidText} rejected: {ex.Message}");
            }
        }

        // Submitted in one call so the containers they open are mounted in parallel.
        var newMounts = keys.Count > 0 ? provider.SubmitKeys(keys) : 0;
        Console.WriteLine($"  Submitted {keys.Count} archived AES key(s) for {buildVersion}; " +
                          $"{newMounts} additional container(s) mounted.");
    }

    /// <summary>Evicts least-recently-used builds until one more fits. Caller holds <see cref="_loadLock"/>.</summary>
    private void EvictUntilRoomLocked()
    {
        while (true)
        {
            LoadedHistoricalBuild? victim;
            lock (_loaded)
            {
                if (_loaded.Count < MaxLoaded)
                {
                    return;
                }

                // A build with readers on it must not be pulled away from them; if every mounted build
                // is busy, going over the cap briefly is the lesser evil.
                victim = _loaded.Values.Where(x => x.InFlight == 0).OrderBy(x => x.LastUsedUtc).FirstOrDefault();
                if (victim == null)
                {
                    Console.WriteLine($"  All {_loaded.Count} mounted archived build(s) are in use; " +
                                      "loading another one anyway rather than interrupting a request.");
                    return;
                }

                _loaded.Remove(victim.BuildVersion);
            }

            Console.WriteLine($"  Evicting the archived build {victim.BuildVersion} to stay within HISTORICAL_BUILDS_MAX={MaxLoaded}.");
            DisposeQuietly(victim);
        }
    }

    private void SweepIdle()
    {
        if (IdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - IdleTimeout;
        List<LoadedHistoricalBuild> stale;
        lock (_loaded)
        {
            stale = _loaded.Values.Where(x => x.LastUsedUtc < cutoff && x.InFlight == 0).ToList();
            foreach (var build in stale)
            {
                _loaded.Remove(build.BuildVersion);
            }
        }

        foreach (var build in stale)
        {
            Console.WriteLine($"  Dropping the idle archived build {build.BuildVersion} (unused for {IdleTimeout.TotalMinutes:F0} min).");
            DisposeQuietly(build);
        }

        if (stale.Count > 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void DisposeQuietly(LoadedHistoricalBuild build)
    {
        try
        {
            build.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error while unmounting {build.BuildVersion}: {ex.Message}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public async ValueTask DisposeAsync()
    {
        await _idleSweep.DisposeAsync();

        List<LoadedHistoricalBuild> all;
        lock (_loaded)
        {
            all = _loaded.Values.ToList();
            _loaded.Clear();
        }

        foreach (var build in all)
        {
            DisposeQuietly(build);
        }

        _loadLock.Dispose();
    }

    /// <summary>The build version the live provider currently serves.</summary>
    public string LiveBuildVersion => _manifestService.GameBuild;
}
