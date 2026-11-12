using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

/// <summary>
/// Computes, records and composes the changelists between Fortnite builds, and produces the
/// line-level diff of a single file between two of them.
/// </summary>
public sealed partial class BuildDiffService
{
    private readonly BuildHistoryStore _store;
    private readonly HistoricalBuildService _historical;
    private readonly IFileProvider _liveProvider;
    private readonly ManifestService _manifestService;

    public BuildDiffService(BuildHistoryStore store, HistoricalBuildService historical,
        IFileProvider liveProvider, ManifestService manifestService)
    {
        _store = store;
        _historical = historical;
        _liveProvider = liveProvider;
        _manifestService = manifestService;
    }

    // ------------------------------------------------------------ build resolution

    /// <summary>
    /// Resolves a build parameter to a full build version. Accepts the full string
    /// (<c>++Fortnite+Release-42.10-CL-57566230-Windows</c>), the version alone (<c>42.10</c>), a
    /// changelist (<c>57566230</c>), or <c>latest</c>/<c>current</c> and <c>previous</c>.
    /// Returns null when nothing matches.
    /// </summary>
    public string? ResolveBuildVersion(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var query = input.Trim();

        if (query.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            query.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            return _manifestService.GameBuild;
        }

        var known = KnownBuildVersions();

        if (query.Equals("previous", StringComparison.OrdinalIgnoreCase) ||
            query.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrEmpty(_manifestService.PreviousBuildVersion)
                ? _manifestService.PreviousBuildVersion
                : known.FirstOrDefault(b => !string.Equals(b, _manifestService.GameBuild, StringComparison.OrdinalIgnoreCase));
        }

        var exact = known.FirstOrDefault(b => string.Equals(b, query, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        // "42.10", "42.10-CL-57566230" or a bare changelist.
        var matches = known
            .Where(b => b.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        BuildHistoryStore.SplitBuildVersion(b).Version == query ||
                        BuildHistoryStore.SplitBuildVersion(b).Changelist == query)
            .ToList();

        // Ambiguity is resolved towards the newest match, which is what "42.10" means in practice
        // when the same version shipped under more than one changelist.
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Every build this instance knows about, newest first, live build included.</summary>
    public List<string> KnownBuildVersions()
    {
        var builds = new List<string>();
        if (!string.IsNullOrEmpty(_manifestService.GameBuild))
        {
            builds.Add(_manifestService.GameBuild);
        }

        foreach (var archived in _store.GetBuilds())
        {
            if (!builds.Contains(archived.BuildVersion, StringComparer.OrdinalIgnoreCase))
            {
                builds.Add(archived.BuildVersion);
            }
        }

        return builds;
    }

    /// <summary>True when this build version is the one the live provider serves.</summary>
    public bool IsLive(string buildVersion)
        => string.Equals(buildVersion, _manifestService.GameBuild, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Borrows the provider that serves a build, without mounting anything. Historical builds resolve
    /// only while they are already loaded — this is the "return that build's content if that build is
    /// loaded" rule the read endpoints follow. The lease must be disposed once the caller is done.
    /// </summary>
    public bool TryLease(string buildVersion, out BuildLease lease)
    {
        if (IsLive(buildVersion))
        {
            lease = new BuildLease(buildVersion, _liveProvider, null);
            return true;
        }

        return _historical.TryLease(buildVersion, out lease);
    }

    /// <summary>Unmounts a build, unless it is the live one (which is never unmounted here).</summary>
    public bool UnloadIfHistorical(string buildVersion) => !IsLive(buildVersion) && _historical.Unload(buildVersion);

    /// <summary>Same as <see cref="TryLease"/> but mounts the archived build when it is not loaded.</summary>
    public async Task<BuildLease> LeaseAsync(string buildVersion, CancellationToken cancellationToken = default)
    {
        return IsLive(buildVersion)
            ? new BuildLease(buildVersion, _liveProvider, null)
            : await _historical.LeaseAsync(buildVersion, cancellationToken);
    }

    /// <summary>
    /// Reports how much of a build actually mounted. A build whose containers stayed locked exposes
    /// only a fraction of its files, and comparing that against a fully mounted build produces a
    /// changelist that looks like "the whole game was rewritten" — so this is checked before, not after.
    /// </summary>
    public static MountHealth Inspect(IFileProvider provider)
    {
        if (provider is not CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider vfs)
        {
            return new MountHealth(provider.Files.Count, 0, 0, 0);
        }

        return new MountHealth(provider.Files.Count, vfs.MountedVfs.Count, vfs.UnloadedVfs.Count, vfs.RequiredKeys.Count);
    }

    /// <summary>Names of the containers that mounted, and of those that did not.</summary>
    private static (HashSet<string> Mounted, HashSet<string> Locked) ContainerNames(IFileProvider provider)
    {
        if (provider is not CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider vfs)
        {
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return (vfs.MountedVfs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                vfs.UnloadedVfs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Containers that can be read in one of these builds but not the other.
    /// <para>
    /// A locked container is not by itself a problem — the live build almost always has a few whose
    /// keys Epic has not published yet, and one locked on both sides simply appears in neither file
    /// list. What ruins a comparison is a container readable on one side only: every file in it looks
    /// added or removed when all that changed is whether it could be opened. Those containers are left
    /// out of the comparison and reported, rather than silently distorting it.
    /// </para>
    /// </summary>
    public static HashSet<string> AsymmetricContainers(IFileProvider fromProvider, IFileProvider toProvider)
    {
        var (fromMounted, fromLocked) = ContainerNames(fromProvider);
        var (toMounted, toLocked) = ContainerNames(toProvider);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result.UnionWith(fromLocked.Where(toMounted.Contains));
        result.UnionWith(toLocked.Where(fromMounted.Contains));
        return result;
    }
}
