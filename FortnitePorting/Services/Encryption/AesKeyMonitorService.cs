using System.Net.Http;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using FortnitePorting.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Periodically reads the local archive key endpoint and, purely by GUID, submits any keys that are
/// still required so the matching (already-registered) VFS files mount automatically. No dependency
/// on pak names; the endpoint is responsible for resolving the current archive/keychain data.
/// </summary>
public class AesKeyMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AesKeyMonitorService> _logger;
    private readonly string _archiveKeysUrl;
    private static readonly HttpClient _httpClient = new();

    public AesKeyMonitorService(IServiceProvider serviceProvider, ILogger<AesKeyMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        var port = Environment.GetEnvironmentVariable("PORT") ?? "3849";
        _archiveKeysUrl = $"http://127.0.0.1:{port}/api/v1/archives/keys";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for application initialization to complete.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        _logger.LogInformation("AES Key Monitor Service is starting (GUID-based auto-mount).");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckForNewKeysAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while checking for new AES keys.");
            }
        }
    }

    private async Task CheckForNewKeysAsync(CancellationToken stoppingToken)
    {
        // Skip while the provider is being rebuilt for a new build: its archives are being torn down and
        // re-registered, and the rebuild fetches the current keys itself. The next tick picks up whatever
        // the new build still needs.
        if (ProviderReloadGate.Instance.IsReloading)
        {
            _logger.LogInformation("Skipping the AES key check: the provider is reloading the latest build.");
            return;
        }

        // 1. Read the keys assembled by the local archive endpoint. That endpoint resolves the
        // current registered archives and their keychain entries, so this monitor does not fetch
        // the external AES APIs directly.
        using var response = await _httpClient.GetAsync(_archiveKeysUrl, stoppingToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var archiveKeys = JsonConvert.DeserializeObject<ArchiveKeysResponse>(json)
            ?? throw new InvalidOperationException("The archive keys endpoint returned an empty response.");

        using var scope = _serviceProvider.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IFileProvider>() is not AbstractVfsFileProvider provider)
        {
            _logger.LogError("FileProvider is not a VFS provider; cannot mount.");
            return;
        }

        // 2. Build a GUID -> key map from the main key and all dynamic keys.
        var keyMap = new Dictionary<FGuid, FAesKey>();
        TryAddKey(keyMap, null, archiveKeys.MainKey); // the main key uses the zero GUID
        foreach (var dynamicKey in archiveKeys.DynamicKeys)
        {
            TryAddKey(keyMap, dynamicKey.Guid, dynamicKey.Key);
        }

        // 3. Submit only keys whose GUID is still required (i.e. would actually mount an unloaded VFS).
        //    SubmitKeys mounts every unloaded reader whose EncryptionKeyGuid matches — no pak names needed.
        var required = new HashSet<FGuid>(provider.RequiredKeys);
        var toSubmit = keyMap.Where(kv => required.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        if (toSubmit.Count > 0)
        {
            var mounted = provider.SubmitKeys(toSubmit);
            _logger.LogInformation(
                "Submitted {KeyCount} required AES key(s) by GUID (source: /api/v1/archives/keys); newly mounted {Mounted} VFS file(s). Total files: {Total}",
                toSubmit.Count, mounted, provider.Files.Count);
        }
        else
        {
            _logger.LogInformation("No new AES keys to apply ({Required} GUID(s) still awaiting keys).", required.Count);
        }

    }

    private static void TryAddKey(Dictionary<FGuid, FAesKey> map, string? guidStr, string? keyStr)
    {
        if (string.IsNullOrEmpty(keyStr))
        {
            return;
        }

        if (TryParseGuid(guidStr, out var guid))
        {
            try
            {
                map[guid] = new FAesKey(keyStr);
            }
            catch
            {
                // Malformed key string; skip.
            }
        }
    }

    /// <summary>
    /// Parses a key GUID. Accepts 32-char hex (with or without dashes) and an empty/zero GUID for the main key.
    /// </summary>
    private static bool TryParseGuid(string? guidStr, out FGuid guid)
    {
        guid = new FGuid(0, 0, 0, 0);
        if (string.IsNullOrEmpty(guidStr))
        {
            return true; // main key -> zero GUID
        }

        var hex = guidStr.Replace("-", "");
        if (hex.Replace("0", "").Length == 0)
        {
            return true; // all zeros
        }

        try
        {
            if (hex.Length == 32)
            {
                var a = Convert.ToUInt32(hex.Substring(0, 8), 16);
                var b = Convert.ToUInt32(hex.Substring(8, 8), 16);
                var c = Convert.ToUInt32(hex.Substring(16, 8), 16);
                var d = Convert.ToUInt32(hex.Substring(24, 8), 16);
                guid = new FGuid(a, b, c, d);
            }
            else
            {
                guid = new FGuid(guidStr);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
