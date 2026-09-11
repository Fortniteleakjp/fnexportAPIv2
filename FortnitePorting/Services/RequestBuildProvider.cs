using CUE4Parse.FileProvider;

namespace FortnitePorting.Services;

/// <summary>
/// Marks a controller as accepting the <c>version</c> query parameter, so
/// <see cref="VersionParameterFilter"/> resolves it and points
/// <see cref="RequestBuildProvider"/> at the build the caller named.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class VersionAwareAttribute : Attribute;

/// <summary>
/// The provider a single request reads from: the live build by default, or the build named by the
/// request's <c>version</c> parameter.
/// <para>
/// Registered per request and left pointing at the live build until
/// <see cref="VersionParameterFilter"/> binds it. Controllers must therefore read
/// <see cref="Provider"/> lazily — MVC creates the controller before action filters run, so a
/// provider captured in a constructor would always be the live one.
/// </para>
/// </summary>
public sealed class RequestBuildProvider
{
    private readonly IFileProvider _liveProvider;
    private readonly ManifestService _manifestService;
    private BuildLease? _lease;

    public RequestBuildProvider(IFileProvider liveProvider, ManifestService manifestService)
    {
        _liveProvider = liveProvider;
        _manifestService = manifestService;
    }

    /// <summary>The provider this request reads from.</summary>
    public IFileProvider Provider => _lease?.Provider ?? _liveProvider;

    /// <summary>The build this request reads from.</summary>
    public string BuildVersion => _lease?.BuildVersion ?? _manifestService.GameBuild;

    /// <summary>True when this request reads the live build, which is the default.</summary>
    public bool IsLive => _lease is null or { IsLive: true };

    /// <summary>
    /// Prefix for any cache key that holds content read through <see cref="Provider"/>. Empty for the
    /// live build, so live requests keep the cache entries they always had; a per-build prefix
    /// otherwise, so an older build's content can never be served from — or poison — the live cache.
    /// </summary>
    public string CacheScope => IsLive ? string.Empty : $"@{BuildVersion}::";

    /// <summary>Called by <see cref="VersionParameterFilter"/> once it has resolved the parameter.</summary>
    internal void Bind(BuildLease lease) => _lease = lease;
}
