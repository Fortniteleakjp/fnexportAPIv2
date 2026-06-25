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
/// Periodically fetches AES keys and, purely by GUID, submits any keys that are still required so
/// the matching (already-registered) VFS files mount automatically. No dependency on pak names, so
/// it works with both api.fortniteapi.com (primary) and uedb.dev (fallback).
/// </summary>
public class AesKeyMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AesKeyMonitorService> _logger;
    private readonly string _aesFilePath;
    private static readonly HttpClient _httpClient = new();

    public AesKeyMonitorService(IServiceProvider serviceProvider, ILogger<AesKeyMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        var projectRoot = Environment.GetEnvironmentVariable("PROJECT_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        _aesFilePath = Path.Combine(projectRoot, "aes.json");
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
        // 1. Fetch the latest AES keys: api.fortniteapi.com first, uedb.dev as fallback.
        var (aesData, source) = await AesKeyService.FetchAsync(
            _httpClient, msg => _logger.LogWarning("{Message}", msg), stoppingToken);

        if (aesData is null)
        {
            _logger.LogWarning("AES fetch failed from both sources; will retry next cycle.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IFileProvider>() is not AbstractVfsFileProvider provider)
        {
            _logger.LogError("FileProvider is not a VFS provider; cannot mount.");
            return;
        }

        // 2. Build a GUID -> key map from the main key and all dynamic keys.
        var keyMap = new Dictionary<FGuid, FAesKey>();
        TryAddKey(keyMap, null, aesData.MainKey); // the main key uses the zero GUID
        foreach (var dynamicKey in aesData.DynamicKeys)
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
                "Submitted {KeyCount} required AES key(s) by GUID (source: {Source}); newly mounted {Mounted} VFS file(s). Total files: {Total}",
                toSubmit.Count, source, mounted, provider.Files.Count);
        }
        else
        {
            _logger.LogInformation("No new AES keys to apply ({Required} GUID(s) still awaiting keys).", required.Count);
        }

        // 4. Keep the local backup fresh (primary source only — the fallback omits per-key metadata).
        if (source == AesKeyService.PrimarySource)
        {
            try
            {
                await File.WriteAllTextAsync(_aesFilePath, JsonConvert.SerializeObject(aesData, Formatting.Indented), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update aes.json.");
            }
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
