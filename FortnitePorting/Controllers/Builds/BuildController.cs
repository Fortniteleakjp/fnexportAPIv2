using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// Reports which Fortnite build the API currently serves and forces a reload onto the newest one.
/// These endpoints stay reachable while a reload is running (everything else answers 503 meanwhile).
/// </summary>
[ApiController]
[Route("api/v1/build")]
public sealed class BuildController : ControllerBase
{
    private readonly IFileProvider _provider;
    private readonly ManifestService _manifestService;

    public BuildController(IFileProvider provider, ManifestService manifestService)
    {
        _provider = provider;
        _manifestService = manifestService;
    }

    /// <summary>
    /// Returns the build currently mounted, the build the manifest points at, and the reload state.
    /// </summary>
    [HttpGet]
    public IActionResult GetBuild()
    {
        var gate = ProviderReloadGate.Instance;
        return Ok(new
        {
            status = gate.State,
            reloading = gate.IsReloading,
            reloadStartedUtc = gate.ReloadStartedUtc,
            build = _manifestService.GameBuild,
            version = _manifestService.GameVersion,
            manifestId = _manifestService.ManifestId,
            appliedBuild = _manifestService.AppliedBuildVersion,
            appliedManifestId = _manifestService.AppliedManifestId,
            upToDate = _manifestService.IsUpToDate,
            mountedVfs = _provider is AbstractVfsFileProvider vfs ? vfs.MountedVfs.Count : 0,
            unmountedVfs = _provider is AbstractVfsFileProvider vfs2 ? vfs2.UnloadedVfs.Count : 0,
            keysStillRequired = _provider is AbstractVfsFileProvider vfs3 ? vfs3.RequiredKeys.Count : 0,
            files = _provider.Files.Count
        });
    }

    /// <summary>
    /// Rebuilds the provider from the newest manifest immediately instead of waiting for the poll.
    /// </summary>
    [HttpPost("reload")]
    public async Task<IActionResult> Reload()
    {
        if (ProviderReloadGate.Instance.IsReloading)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                status = ProviderReloadGate.Instance.State,
                message = "A reload is already running."
            });
        }

        var succeeded = await _manifestService.ForceReloadAsync();
        return Ok(new
        {
            succeeded,
            build = _manifestService.GameBuild,
            appliedBuild = _manifestService.AppliedBuildVersion,
            files = _provider.Files.Count,
            message = succeeded
                ? "The provider was rebuilt from the newest manifest."
                : "The rebuild failed; the poll will retry. Check the server log."
        });
    }
}
