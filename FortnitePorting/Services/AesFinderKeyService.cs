using System;
using System.Collections.Generic;
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
/// <c>*-Common-Win64-Shipping.dll</c>, extracts every key candidate from it, and submits the one that actually
/// decrypts the waiting archives, so the matching paks mount automatically. It does NOT download anything while
/// the main key is already applied, so it stays idle in normal operation and only acts as a fallback. Disable
/// with <c>AESFINDER_AUTO=false</c>.
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
    private int _seenReloadGeneration;

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
        // Skip while the provider is being rebuilt for a new build (its archives are in flux).
        if (ProviderReloadGate.Instance.IsReloading)
        {
            return;
        }

        // After a rebuild every key has to be submitted again, so forget which key was submitted last -
        // otherwise a key that happens to be unchanged would never be re-submitted and its paks would
        // stay unmounted.
        var generation = ProviderReloadGate.Instance.Generation;
        if (generation != _seenReloadGeneration)
        {
            _seenReloadGeneration = generation;
            _lastKeySubmitted = null;
        }

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

        // The external tool is optional — the built-in scanner covers the same instruction patterns — but when
        // it is present its answer is worth trying first, because it may have cross-checked the live AES API.
        var toolPath = ExternalAesFinder.ResolveToolPath();
        if (toolPath == null && !_warnedNoTool)
        {
            _logger.LogInformation("AesFinder auto-mount: external tool not found (set AESFINDER_PATH); using the built-in scanner.");
            _warnedNoTool = true;
        }

        _logger.LogInformation("[AesFinder] Main key not applied yet; extracting it from the Common DLL...");

        var dl = await UefnAesExtractor.DownloadAsync(
            _rootDir, Http, CommonDll, msg => _logger.LogInformation("[AesFinder] {Message}", msg), false, ct);

        ExternalAesFinder.Result? result = null;
        if (toolPath != null)
        {
            try
            {
                result = await ExternalAesFinder.RunAsync(toolPath, dl.LocalPath, noApi: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[AesFinder] External tool failed; falling back to the built-in scanner.");
            }
        }

        // Gather every key-shaped block in the DLL. The tool reports a single key picked by entropy whenever
        // the live AES API has no entry for the build yet, and on a fresh build that pick is often not the pak
        // key — so its answer is only the first thing we try, not the answer.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(result?.MainKey)) candidates.Add(result!.MainKey!);
        candidates.AddRange(AesImmediateScanner.FindInFile(dl.LocalPath).Select(c => c.Key));

        if (candidates.Count == 0)
        {
            _logger.LogWarning("[AesFinder] No key candidates found in {File}.", Path.GetFileName(dl.LocalPath));
            return;
        }

        // Settle it against the archives themselves: only a key that decrypts one of them is the real key.
        var pick = AesKeyPicker.Pick(provider, ZeroGuid, candidates);
        if (pick.Key == null)
        {
            _logger.LogWarning("[AesFinder] {Reason} Extracted {Count} candidate(s) from {File}.",
                pick.Reason, candidates.Count, Path.GetFileName(dl.LocalPath));
            return;
        }

        if (!pick.Validated)
        {
            _logger.LogInformation("[AesFinder] {Reason}", pick.Reason);
        }
        else if (result?.MainKey != null && !string.Equals(result.MainKey, pick.Key, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[AesFinder] The external tool reported {ToolKey}, which no archive accepted; using the verified key instead.",
                result.MainKey);
        }

        // Avoid resubmitting the identical key every cycle if it didn't clear the requirement.
        if (string.Equals(pick.Key, _lastKeySubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var mounted = provider.SubmitKey(ZeroGuid, new FAesKey(pick.Key));
        _lastKeySubmitted = pick.Key;
        _logger.LogInformation(
            "[AesFinder] Submitted {Verified} main key ({Build}); mounted {Mounted} VFS file(s). Total files: {Total}",
            pick.Validated ? "verified" : "unverified", result?.FullVersion ?? dl.Build, mounted, provider.Files.Count);
    }
}
