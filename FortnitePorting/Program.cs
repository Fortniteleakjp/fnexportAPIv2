global using EpicManifestParser;

using CUE4Parse.FileProvider;
using FortnitePorting.Controllers;
using FortnitePorting.Services;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration.AddEnvironmentVariables();
if (args != null) builder.Configuration.AddCommandLine(args);

builder.Host.UseContentRoot(Directory.GetCurrentDirectory());

var port = Environment.GetEnvironmentVariable("PORT") ?? "3849";

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddApiEndpoints();
builder.Services.AddHostedService<AesKeyMonitorService>();
builder.Services.AddHostedService<AesFinderKeyService>();

Console.WriteLine("=================================");
Console.WriteLine("Fortnite Asset Export API");
Console.WriteLine($"Version {SelfUpdateService.CurrentVersionDisplay}");
Console.WriteLine("=================================\n");

if (SelfUpdateService.RunStartupUpdate())
{
    return;
}

Console.WriteLine("Initializing FileProvider...\n");

FileProviderFactory.InitializationResult initializationResult;
try
{
    initializationResult = FileProviderFactory.CreateFileProvider();
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
    return;
}

Console.WriteLine("\n✓ FileProvider initialization complete\n");

builder.Services.AddSingleton<IFileProvider>(initializationResult.FileProvider);
builder.Services.AddSingleton(initializationResult.ManifestService);

builder.Services.AddSingleton(initializationResult.BuildHistory);
builder.Services.AddSingleton(initializationResult.HistoricalBuilds);
builder.Services.AddSingleton(initializationResult.BuildDiffs);
builder.Services.AddSingleton(initializationResult.DiffJobs);

builder.Services.AddSingleton(sp => new FortnitePorting.Services.Local.LocalBuildService(
    sp.GetRequiredService<IFileProvider>()));

var app = builder.Build();

CacheRegistry.Register("response cache", () => (app.Services.GetRequiredService<IMemoryCache>() as MemoryCache)?.Clear());
CacheRegistry.Register("path index", FileIndex.ClearAll);
CacheRegistry.Register("archive file index", ArchiveFileIndex.Clear);
CacheRegistry.Register("search bytes/exports", SearchController.ClearCaches);
CacheRegistry.Register("export localization", ExportController.ClearCaches);
CacheRegistry.Register("localization tables", LocalizationService.ClearCache);

app.UseApiEndpoints();

Console.WriteLine($"\n✓ Server ready to start");
Console.WriteLine($"Listening on http://0.0.0.0:{port}\n");
app.Run();
