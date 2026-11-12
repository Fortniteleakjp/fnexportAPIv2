using CUE4Parse.FileProvider;
using FortnitePorting.Controllers;
using FortnitePorting.Services.Local;
using Microsoft.Extensions.Caching.Memory;

namespace FortnitePorting.Services;

/// <summary>
/// The steps <c>Program.cs</c> runs at startup, kept here so the entry point reads as a short list.
/// </summary>
public static class AppStartup
{
    public const string DefaultPort = "3849";

    /// <summary>Port from <c>PORT</c>, or <see cref="DefaultPort"/>.</summary>
    public static string Port
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PORT")?.Trim();
            return string.IsNullOrEmpty(configured) ? DefaultPort : configured;
        }
    }

    public static void PrintBanner()
    {
        Console.WriteLine("=================================");
        Console.WriteLine("Fortnite Asset Export API");
        Console.WriteLine($"Version {SelfUpdateService.CurrentVersionDisplay}");
        Console.WriteLine("=================================\n");
    }

    /// <summary>
    /// Mounts the current Fortnite build. Returns null (after explaining why) when there is nothing
    /// to serve, in which case the process should exit.
    /// </summary>
    public static FileProviderFactory.InitializationResult? InitializeFileProvider()
    {
        Console.WriteLine("Initializing FileProvider...\n");
        try
        {
            var result = FileProviderFactory.CreateFileProvider();
            Console.WriteLine("\n✓ FileProvider initialization complete\n");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Startup failed: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Cause: {ex.InnerException.Message}");
            }
            Console.WriteLine("\nThe API needs one Fortnite build to serve. If the build API is temporarily");
            Console.WriteLine("unavailable, retry in a few minutes — a build this instance has served before is");
            Console.WriteLine("mounted from build_history/ automatically when it is.");
            return null;
        }
    }

    /// <summary>Registers the mounted build and the services built around it.</summary>
    public static IServiceCollection AddMountedBuild(this IServiceCollection services, FileProviderFactory.InitializationResult result)
    {
        services.AddSingleton<IFileProvider>(result.FileProvider);
        services.AddSingleton(result.ManifestService);
        services.AddSingleton(result.BuildHistory);
        services.AddSingleton(result.HistoricalBuilds);
        services.AddSingleton(result.BuildDiffs);
        services.AddSingleton(result.DiffJobs);
        services.AddSingleton(sp => new LocalBuildService(sp.GetRequiredService<IFileProvider>()));
        return services;
    }

    /// <summary>Caches that are dropped together when a new build is mounted.</summary>
    public static WebApplication RegisterCaches(this WebApplication app)
    {
        CacheRegistry.Register("response cache", () => (app.Services.GetRequiredService<IMemoryCache>() as MemoryCache)?.Clear());
        CacheRegistry.Register("path index", FileIndex.ClearAll);
        CacheRegistry.Register("archive file index", ArchiveFileIndex.Clear);
        CacheRegistry.Register("search bytes/exports", SearchController.ClearCaches);
        CacheRegistry.Register("export localization", ExportController.ClearCaches);
        CacheRegistry.Register("localization tables", LocalizationService.ClearCache);
        return app;
    }
}
