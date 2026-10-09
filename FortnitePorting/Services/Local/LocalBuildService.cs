using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;

namespace FortnitePorting.Services.Local;

/// <summary>A local installation that is mounted right now.</summary>
public sealed class LoadedLocalBuild
{
    public required string Directory { get; init; }
    public required string Root { get; init; }
    public required DefaultFileProvider Provider { get; init; }
    public required LocalKeyResolver.Result Keys { get; init; }
    public required DateTime MountedUtc { get; init; }
    public required double MountSeconds { get; init; }
    public int ArchiveCount { get; init; }

    private long _lastUsedTicks = DateTime.UtcNow.Ticks;
    private int _inFlight;
    private int _disposed;
    private volatile bool _retired;

    public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
    public int InFlight => Volatile.Read(ref _inFlight);

    internal void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

    internal void Enter()
    {
        Interlocked.Increment(ref _inFlight);
        Touch();
    }

    internal void Exit()
    {
        Touch();
        if (Interlocked.Decrement(ref _inFlight) == 0 && _retired) DisposeNow();
    }

    /// <summary>
    /// Takes the build out of service, disposing it once the last reader is done. A dump can run for
    /// minutes, so an unmount must never pull the provider out from under one.
    /// </summary>
    internal void Retire()
    {
        _retired = true;
        if (Volatile.Read(ref _inFlight) == 0) DisposeNow();
    }

    private void DisposeNow()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Provider.Dispose();
    }
}

/// <summary>A borrowed local build. Holding one keeps it mounted; disposing it lets it be dropped.</summary>
public sealed class LocalBuildLease : IDisposable
{
    private readonly LoadedLocalBuild _build;

    internal LocalBuildLease(LoadedLocalBuild build) => _build = build;

    public string Directory => _build.Directory;
    public IFileProvider Provider => _build.Provider;
    public LoadedLocalBuild Build => _build;

    public void Dispose() => _build.Exit();
}

/// <summary>
/// Opens a Fortnite installation that is already on this machine, instead of the build this API
/// streams from Epic's manifests.
/// <para>
/// Two things are on offer and they cost very differently. Inspecting only registers the containers
/// and reads their headers, which is what deciding the AES keys needs — seconds, and nothing is kept.
/// Mounting builds the whole file index so the build can be read from and dumped, which costs a few
/// GB, so at most <see cref="MaxLoaded"/> installations are mounted at a time and an idle one is
/// dropped again.
/// </para>
/// </summary>
public sealed class LocalBuildService : IDisposable
{
    private readonly IFileProvider _liveProvider;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly Dictionary<string, LoadedLocalBuild> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly Timer _idleSweep;

    /// <summary>How many local installations may be mounted at once (env <c>LOCAL_BUILDS_MAX</c>).</summary>
    public int MaxLoaded { get; }

    /// <summary>How long a mounted installation may sit unused (env <c>LOCAL_BUILD_IDLE_MINUTES</c>, 0 disables).</summary>
    public TimeSpan IdleTimeout { get; }

    public LocalBuildService(IFileProvider liveProvider)
    {
        _liveProvider = liveProvider;

        MaxLoaded = int.TryParse(Environment.GetEnvironmentVariable("LOCAL_BUILDS_MAX"), out var max) && max >= 1
            ? max
            : 1;

        IdleTimeout = int.TryParse(Environment.GetEnvironmentVariable("LOCAL_BUILD_IDLE_MINUTES"), out var idle) && idle >= 0
            ? TimeSpan.FromMinutes(idle)
            : TimeSpan.FromMinutes(30);

        _idleSweep = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>What to open and how to key it.</summary>
    public sealed class OpenOptions
    {
        /// <summary>Installation directory; null falls back to detection.</summary>
        public string? Directory;

        /// <summary>How the AES keys are obtained.</summary>
        public LocalKeyResolver.Options Keys = new();
    }

    /// <summary>The outcome of inspecting an installation without mounting it.</summary>
    public sealed class InspectResult
    {
        public required LocalInstallLocator.Resolved Install { get; init; }
        public required LocalKeyResolver.Result Keys { get; init; }
        public int RegisteredArchives { get; init; }
        public int EncryptedArchives { get; init; }
        public double ElapsedSeconds { get; init; }
    }

    /// <summary>Local installations mounted right now, most recently used first.</summary>
    public IReadOnlyList<LoadedLocalBuild> Loaded
    {
        get
        {
            lock (_loaded)
            {
                return _loaded.Values.OrderByDescending(b => b.LastUsedUtc).ToList();
            }
        }
    }

    /// <summary>
    /// Registers an installation's containers, decides the key for every GUID they ask for, and throws
    /// the provider away again. This is the cheap path: no file index is built, so nothing is mounted
    /// and nothing is cached.
    /// </summary>
    public async Task<InspectResult> InspectAsync(OpenOptions options, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        log ??= _ => { };
        var watch = Stopwatch.StartNew();
        var install = LocalInstallLocator.Resolve(options.Directory);

        var provider = CreateProvider(install);
        try
        {
            provider.Initialize();

            options.Keys.Root = install.Root;
            var keys = await LocalKeyResolver.ResolveAsync(provider, options.Keys, _http, log, cancellationToken);

            return new InspectResult
            {
                Install = install,
                Keys = keys,
                RegisteredArchives = provider.UnloadedVfs.Count + provider.MountedVfs.Count,
                EncryptedArchives = provider.UnloadedVfs.Count(r => r.IsEncrypted),
                ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2)
            };
        }
        finally
        {
            provider.Dispose();
        }
    }

    /// <summary>
    /// Mounts an installation (reusing it when it is already mounted) and borrows it. The returned
    /// lease must be disposed; until it is, the build cannot be dropped underneath the caller.
    /// <para>
    /// An installation that is already mounted is handed back as it is: its keys were decided when it
    /// was mounted and changing them now would mean tearing the whole file index down. Unmount it first
    /// to mount it again with different keys.
    /// </para>
    /// </summary>
    public async Task<LocalBuildLease> LeaseAsync(OpenOptions options, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var install = LocalInstallLocator.Resolve(options.Directory);

        if (TryLease(install.Root, out var existing))
        {
            return existing;
        }

        await LoadAsync(install, options, log, cancellationToken);

        if (TryLease(install.Root, out existing))
        {
            return existing;
        }

        throw new InvalidOperationException(
            $"The local build at '{install.Root}' keeps being dropped before it can be read. " +
            "Raise LOCAL_BUILDS_MAX so more than one can stay mounted at a time.");
    }

    /// <summary>Borrows an already-mounted installation, or returns false when it is not mounted.</summary>
    public bool TryLease(string directory, out LocalBuildLease lease)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(directory, out var found))
            {
                found.Enter();
                lease = new LocalBuildLease(found);
                return true;
            }
        }

        lease = null!;
        return false;
    }

    /// <summary>Drops a mounted installation and frees its memory. False when it was not mounted.</summary>
    public bool Unload(string directory)
    {
        string key;
        try { key = Path.GetFullPath(directory); }
        catch { key = directory; }

        LoadedLocalBuild? build;
        lock (_loaded)
        {
            if (!_loaded.Remove(key, out build)) return false;
        }

        build.Retire();
        Console.WriteLine($"✓ Unmounted the local build {key}");
        return true;
    }

    /// <summary>Drops every mounted installation.</summary>
    public int UnloadAll()
    {
        List<LoadedLocalBuild> builds;
        lock (_loaded)
        {
            builds = _loaded.Values.ToList();
            _loaded.Clear();
        }

        foreach (var build in builds) build.Retire();
        return builds.Count;
    }

    private async Task LoadAsync(LocalInstallLocator.Resolved install, OpenOptions options,
        Action<string>? log, CancellationToken cancellationToken)
    {
        log ??= _ => { };

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            lock (_loaded)
            {
                if (_loaded.ContainsKey(install.Root)) return;
            }

            EvictUntilRoomLocked();

            var watch = Stopwatch.StartNew();
            Console.WriteLine($"\n=== Mounting the local build {install.Root} ===");

            var provider = CreateProvider(install);
            try
            {
                provider.Initialize();

                // Old and new builds alike deserialize against whatever mapping this process has. It is
                // only used for reads; a dump collects its types from the packages themselves.
                provider.MappingsContainer = _liveProvider.MappingsContainer;

                options.Keys.Root = install.Root;
                var keys = await LocalKeyResolver.ResolveAsync(provider, options.Keys, _http, log, cancellationToken);

                if (keys.Submittable.Count > 0)
                {
                    provider.SubmitKeys(keys.Submittable);
                }

                // Unencrypted containers need no key and are not covered by SubmitKeys.
                provider.Mount();

                // Reads the build's own ini configs, which is also how CUE4Parse notices that a
                // container was mounted with the wrong key. A local install can ship a config this
                // parser chokes on, and that is not a reason to refuse the whole build.
                try { provider.PostMount(); }
                catch (Exception ex) { log($"Reading the local build's configs failed: {ex.Message}"); }

                var loaded = new LoadedLocalBuild
                {
                    Directory = install.Root,
                    Root = install.Root,
                    Provider = provider,
                    Keys = keys,
                    MountedUtc = DateTime.UtcNow,
                    MountSeconds = watch.Elapsed.TotalSeconds,
                    ArchiveCount = install.ArchiveCount
                };

                lock (_loaded)
                {
                    _loaded[install.Root] = loaded;
                }

                Console.WriteLine($"✓ Mounted the local build in {watch.Elapsed.TotalSeconds:F1}s " +
                                  $"(files: {provider.Files.Count}, mounted VFS: {provider.MountedVfs.Count})\n");
            }
            catch
            {
                provider.Dispose();
                throw;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private DefaultFileProvider CreateProvider(LocalInstallLocator.Resolved install)
    {
        // The pak directories are resolved up front rather than handing CUE4Parse the installation root
        // with AllDirectories: a recursive walk of an install aborts on the first directory the process
        // may not read, and an installation always has a few of those.
        var directories = install.PakDirectories.Select(d => new DirectoryInfo(d)).ToArray();

        return new DefaultFileProvider(
            directories[0],
            directories.Skip(1).ToArray(),
            SearchOption.TopDirectoryOnly,
            versions: _liveProvider.Versions,
            pathComparer: StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Drops the least recently used builds until one more fits. Caller holds the load lock.</summary>
    private void EvictUntilRoomLocked()
    {
        while (true)
        {
            LoadedLocalBuild? victim;
            lock (_loaded)
            {
                if (_loaded.Count < MaxLoaded) return;

                victim = _loaded.Values
                    .Where(b => b.InFlight == 0)
                    .OrderBy(b => b.LastUsedUtc)
                    .FirstOrDefault();

                if (victim == null) return; // everything is in use; the new mount simply adds to the total
                _loaded.Remove(victim.Directory);
            }

            victim.Retire();
            Console.WriteLine($"✓ Dropped the idle local build {victim.Directory}");
        }
    }

    private void SweepIdle()
    {
        if (IdleTimeout <= TimeSpan.Zero) return;

        List<LoadedLocalBuild> expired;
        var cutoff = DateTime.UtcNow - IdleTimeout;
        lock (_loaded)
        {
            expired = _loaded.Values.Where(b => b.InFlight == 0 && b.LastUsedUtc < cutoff).ToList();
            foreach (var build in expired) _loaded.Remove(build.Directory);
        }

        foreach (var build in expired)
        {
            build.Retire();
            Console.WriteLine($"✓ Dropped the idle local build {build.Directory}");
        }
    }

    public void Dispose()
    {
        _idleSweep.Dispose();
        UnloadAll();
        _loadLock.Dispose();
        _http.Dispose();
    }
}
