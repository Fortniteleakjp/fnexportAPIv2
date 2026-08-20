using System.Diagnostics;
using CUE4Parse.FileProvider;
using EpicManifestParser.UE;

namespace FortnitePorting.Services;

/// <summary>
/// Rebuilds the FileProvider in place when Fortnite ships a new build.
/// <para>
/// A Fortnite update rewrites the existing containers (pakchunk*.utoc/.ucas keep their names but point
/// at different chunks), so simply registering the archives that are *new* in the manifest is not
/// enough: every already-mounted reader still streams the previous build's chunks, which is why the
/// API kept serving pre-update content until it was restarted. This drops all of them and re-registers
/// everything from the new manifest, which is what a restart used to do.
/// </para>
/// </summary>
public static class ProviderReloader
{
    // Only one rebuild at a time (the poller and the manual endpoint can both request one).
    private static readonly SemaphoreSlim ReloadLock = new(1, 1);

    // How long to wait for in-flight requests to finish before tearing the provider down.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tears down everything loaded from the previous build and re-registers/mounts the new build's
    /// VFS files, then refreshes derived state (mappings via <paramref name="afterMount"/>, caches).
    /// Throws when the rebuild fails so the caller can retry on its next poll.
    /// </summary>
    public static async Task ReloadAsync(DefaultFileProvider provider, FBuildPatchAppManifest manifest,
        string buildVersion, Action? afterMount = null)
    {
        await ReloadLock.WaitAsync();
        try
        {
            await ProviderReloadGate.Instance.BeginReloadAsync($"reloading:{buildVersion}", DrainTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Console.WriteLine($"\n=== Rebuilding the FileProvider for build {buildVersion} ===");

                // 1. Drop the previous build completely. Dispose() disposes every registered reader
                //    (their streams resolve chunks through the OLD manifest) and clears Files, keys and
                //    the global data, leaving this same provider instance empty but reusable — so no
                //    restart is needed and a second full provider never has to exist in memory.
                provider.Dispose();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // 2. Re-register and mount every VFS file of the new manifest. This also re-fetches the
                //    current AES keys; paks whose key is not published yet stay unmounted and are picked
                //    up later by AesKeyMonitorService / AesFinderKeyService.
                VfsLoader.LoadAllVfsFiles(provider, manifest);

                // 3. Swap in the .usmap that matches the new build before requests are let back in.
                afterMount?.Invoke();

                // 4. Everything cached from the previous build is stale by definition.
                CacheRegistry.ClearAll();

                Console.WriteLine($"✓ FileProvider rebuilt for {buildVersion} in {stopwatch.Elapsed.TotalSeconds:F1}s " +
                                  $"(files: {provider.Files.Count}, mounted VFS: {provider.MountedVfs.Count}, " +
                                  $"keys still required: {provider.RequiredKeys.Count})\n");
            }
            finally
            {
                ProviderReloadGate.Instance.EndReload();
            }
        }
        finally
        {
            ReloadLock.Release();
        }
    }
}
