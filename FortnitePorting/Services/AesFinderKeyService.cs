using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FortnitePorting.Services;

/// <summary>
/// Self-sufficient MainAES source. Whenever the provider still needs the main key (zero GUID) — e.g. a new
/// build whose key the external AES API has not published yet — this downloads the Fortnite_Studio (UEFN)
/// <c>*-Common-Win64-Shipping.dll</c>, extracts the key with the external AesFinder tool, and submits it to
/// the provider so the matching paks mount automatically. It does NOT download anything while the main key is
/// already applied, so it stays idle in normal operation and only acts as a fallback. Disable with
/// <c>AESFINDER_AUTO=false</c>.
/// </summary>
public class AesFinderKeyService : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly FGuid ZeroGuid = new(0, 0, 0, 0);
    private const string CommonDll = "UnrealEditorFortnite-Common-Win64-Shipping.dll";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AesFinderKeyService> _logger;
    private readonly string _rootDir;
    private readonly bool _enabled;

    private string? _lastKeySubmitted;
    private bool _warnedNoTool;

    public AesFinderKeyService(IServiceProvider serviceProvider, ILogger<AesFinderKeyService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
        _enabled = !string.Equals(Environment.GetEnvironmentVariable("AESFINDER_AUTO"), "false", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("AesFinder auto-mount is disabled (AESFINDER_AUTO=false).");
            return;
        }

        // Let startup + the initial AES load settle so we don't redundantly download when the key is already applied.
        try { await Task.Delay(TimeSpan.FromSeconds(75), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("AesFinder auto-mount is active (fallback main-key source; only acts when the main key is missing).");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AesFinder auto-mount cycle failed.");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IFileProvider>() is not AbstractVfsFileProvider provider)
        {
            return;
        }

        // Only act when the main key is still required. This is the guard that keeps us idle (no download)
        // whenever the key has already been supplied (by the external AES API at startup or otherwise).
        if (!provider.RequiredKeys.Contains(ZeroGuid))
        {
            return;
        }

        var toolPath = ExternalAesFinder.ResolveToolPath();
        if (toolPath == null)
        {
            if (!_warnedNoTool)
            {
                _logger.LogWarning("AesFinder auto-mount: tool not found (set AESFINDER_PATH). Will not auto-extract the key.");
                _warnedNoTool = true;
            }
            return;
        }

        _logger.LogInformation("[AesFinder] Main key not applied yet; extracting it from the Common DLL...");

        var dl = await UefnAesExtractor.DownloadAsync(
            _rootDir, Http, CommonDll, msg => _logger.LogInformation("[AesFinder] {Message}", msg), false, ct);

        var result = await ExternalAesFinder.RunAsync(toolPath, dl.LocalPath, noApi: false, ct);
        if (string.IsNullOrWhiteSpace(result.MainKey))
        {
            _logger.LogWarning("[AesFinder] No key extracted from {File}.", Path.GetFileName(dl.LocalPath));
            return;
        }

        // Avoid resubmitting the identical key every cycle if it didn't clear the requirement.
        if (string.Equals(result.MainKey, _lastKeySubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var mounted = provider.SubmitKey(ZeroGuid, new FAesKey(result.MainKey));
        _lastKeySubmitted = result.MainKey;
        _logger.LogInformation(
            "[AesFinder] Submitted extracted main key ({Build}); mounted {Mounted} VFS file(s). Total files: {Total}",
            result.FullVersion, mounted, provider.Files.Count);
    }
}
