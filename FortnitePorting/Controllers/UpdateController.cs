using System.Threading;
using System.Threading.Tasks;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FortnitePorting.Controllers;

/// <summary>
/// Reports the running version against the newest GitHub release and installs it on demand.
/// The same check runs automatically at startup; these endpoints exist for a long-running process
/// that should not have to be restarted just to pick an update up.
/// </summary>
[ApiController]
[Route("api/v1/update")]
public sealed class UpdateController : ControllerBase
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateController> _logger;

    public UpdateController(IHostApplicationLifetime lifetime, ILogger<UpdateController> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Returns the running version, the newest release on GitHub, and whether an update applies here.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation state.</param>
    [HttpGet]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        var result = await SelfUpdateService.CheckAsync(cancellationToken);
        return Ok(new
        {
            currentVersion = result.CurrentVersion,
            latestVersion = result.LatestVersion,
            tag = result.Tag,
            updateAvailable = result.Available,
            reason = result.Reason,
            releaseUrl = result.ReleaseUrl,
            asset = result.Asset?.Name,
            assetSize = result.Asset?.Size,
            repository = SelfUpdateService.Repository,
            autoUpdate = SelfUpdateService.Enabled,
            // null means AUTO_UPDATE is unset, which is what makes the startup prompt appear.
            autoUpdatePreference = SelfUpdateService.AutoUpdatePreference,
            checkOnly = SelfUpdateService.CheckOnly,
            restartAfterUpdate = SelfUpdateService.RestartAfterUpdate,
            developmentBuild = SelfUpdateService.IsDevelopmentBuild,
            container = SelfUpdateService.IsContainer,
            applicationDirectory = SelfUpdateService.ApplicationDirectory
        });
    }

    /// <summary>
    /// Installs the newest release and shuts this process down so the swap can complete. The API stops
    /// answering for as long as the restart takes.
    /// </summary>
    /// <param name="force">Ignore the guard that suppresses retrying a version which previously failed to apply.</param>
    /// <param name="cancellationToken">Request cancellation state.</param>
    [HttpPost]
    public async Task<IActionResult> Apply([FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        if (force) SelfUpdateService.ClearAttemptMarker();

        var result = await SelfUpdateService.CheckAsync(cancellationToken);
        if (!result.Available)
        {
            return Ok(new
            {
                applied = false,
                currentVersion = result.CurrentVersion,
                latestVersion = result.LatestVersion,
                reason = result.Reason
            });
        }

        var applied = await SelfUpdateService.ApplyAsync(result, cancellationToken);
        if (!applied)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                applied = false,
                currentVersion = result.CurrentVersion,
                latestVersion = result.LatestVersion,
                message = "The release could not be staged. Check the server log."
            });
        }

        _logger.LogWarning("Update to v{Version} staged; shutting down so it can be applied", result.LatestVersion);

        // Stop only after the response has been written, otherwise the caller never learns what happened.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000, CancellationToken.None);
            _lifetime.StopApplication();
        }, CancellationToken.None);

        return Ok(new
        {
            applied = true,
            currentVersion = result.CurrentVersion,
            latestVersion = result.LatestVersion,
            releaseUrl = result.ReleaseUrl,
            willRestart = SelfUpdateService.RestartAfterUpdate,
            message = SelfUpdateService.RestartAfterUpdate
                ? "The update was staged. The API is shutting down and will restart on the new version."
                : "The update was staged. The API is shutting down; start it again to run the new version."
        });
    }
}
