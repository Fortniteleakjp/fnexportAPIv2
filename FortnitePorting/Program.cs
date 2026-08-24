global using EpicManifestParser;

using CUE4Parse.FileProvider;
using FortnitePorting.Controllers;
using FortnitePorting.Services;
using Microsoft.Extensions.Caching.Memory;

// Configuration for the Docker container environment
var builder = WebApplication.CreateBuilder(args);

// Disable file watching (workaround for inotify limits in Docker containers)
builder.Configuration.Sources.Clear();
builder.Configuration.AddEnvironmentVariables();
if (args != null) builder.Configuration.AddCommandLine(args);

// Also disable file watching in the host builder
builder.Host.UseContentRoot(Directory.GetCurrentDirectory());

// Get the port setting from an environment variable (default is 3849)
var port = Environment.GetEnvironmentVariable("PORT") ?? "3849";

// Set the URL explicitly (to avoid warnings, UseKestrel is not used)
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
// Gate that blocks requests while the provider is rebuilt for a new Fortnite build.
builder.Services.AddSingleton(ProviderReloadGate.Instance);
builder.Services.AddHostedService<AesKeyMonitorService>();
// Fallback self-sufficient main-key source: extracts the MainAES key from the UEFN Common DLL with the
// external AesFinder tool and submits it when the external AES API hasn't supplied it (e.g. a fresh build).
builder.Services.AddHostedService<AesFinderKeyService>();

// Swagger / OpenAPI (exposes all endpoints)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Japanese document (default). Operation summaries are localized by JapaneseOperationFilter.
    options.SwaggerDoc("ja", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Fortnite アセットエクスポート API",
        Version = "v1",
        Description = "ローカルで実行するCUE4ParseベースのFortniteアセット解析APIです。アセットのJSON・画像・音声取得、コスメ検索、ファイル検索、PAK/INI確認、依存関係解析、ローカライズを提供します。VPSなどへのホスティングを前提としません。"
    });

    // English document.
    options.SwaggerDoc("en", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Fortnite Asset Analysis API",
        Version = "v1",
        Description = "A local CUE4Parse-powered Fortnite asset analysis API. Provides JSON/image/audio export, cosmetic search, path/content search, PAK and INI inspection, dependency analysis, and localization. It is designed to run locally rather than as a VPS-hosted service."
    });

    // Include XML comments if they exist (these supply the English text used by the "en" document).
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    // Localize operation summaries AND descriptions per document ("ja" / "en").
    // Registered AFTER IncludeXmlComments so it overrides the XML text for both documents.
    options.OperationFilter<FortnitePorting.Swagger.LocalizedOperationFilter>();

    // Localize the controller (tag) descriptions per document.
    options.DocumentFilter<FortnitePorting.Swagger.LocalizedDocumentFilter>();
});

// CORS: allow the API to be called from any origin (browser apps, tools, etc.).
// Custom audio diagnostic headers are exposed so browser clients can read them.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("X-Audio-Format", "X-Audio-Decoded", "X-Rada-Native-Decoder", "Content-Disposition",
                  "X-Usmap-Bytes", "X-Usmap-Names", "X-Usmap-Enums", "X-Usmap-Structs",
                  "X-Usmap-UnknownProps", "X-Usmap-OptionalProps", "X-Usmap-Output", "X-Usmap-Loaded",
                  "X-Usmap-ParsedEnums", "X-Usmap-ParsedStructs",
                  "X-Backup-Entries", "X-Backup-Version"));
});

Console.WriteLine("=================================");
Console.WriteLine("Fortnite Asset Export API");
Console.WriteLine($"Version {SelfUpdateService.CurrentVersionDisplay}");
Console.WriteLine("=================================\n");

// Check GitHub for a newer release before anything expensive happens. When one is installed the
// swap is performed by a helper script that waits for this process to exit, so we stop right here
// rather than mounting a whole build we are about to throw away.
if (SelfUpdateService.RunStartupUpdate())
{
    return;
}

// Initialize the FileProvider at startup and register it as a singleton
Console.WriteLine("Initializing FileProvider...\n");
var initializationResult = FileProviderFactory.CreateFileProvider();
Console.WriteLine("\n✓ FileProvider initialization complete\n");

builder.Services.AddSingleton<IFileProvider>(initializationResult.FileProvider);
builder.Services.AddSingleton(initializationResult.ManifestService);

var app = builder.Build();

// Register the caches that hold data derived from the mounted build. They are all cleared whenever the
// provider is rebuilt for a new build, so a cache hit can never keep serving pre-update content.
CacheRegistry.Register("response cache", () => (app.Services.GetRequiredService<IMemoryCache>() as MemoryCache)?.Clear());
CacheRegistry.Register("search bytes/exports", SearchController.ClearCaches);
CacheRegistry.Register("export localization", ExportController.ClearCaches);
CacheRegistry.Register("localization tables", LocalizationService.ClearCache);

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    // Development environment configuration (no file watching)
}

// Enable Swagger in all environments. Two documents are exposed and selectable from the
// UI dropdown: 日本語 (default) and English.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    // The first endpoint is the default shown in the UI.
    options.SwaggerEndpoint("/swagger/ja/swagger.json", "日本語 (Japanese)");
    options.SwaggerEndpoint("/swagger/en/swagger.json", "English");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "Fortnite Asset Analysis API";
    options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
    options.DefaultModelsExpandDepth(1);
    options.DisplayRequestDuration();
    options.EnableFilter();
});

// While the provider is being rebuilt for a new build its archives are torn down and re-registered, so
// requests must not read from it: they are answered with 503 instead of stale or half-loaded content.
// The build/status endpoints stay reachable so clients can see why (and poll for completion).
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var exempt = path.Equals("/", StringComparison.Ordinal)
                 || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith("/api/v1/build", StringComparison.OrdinalIgnoreCase);

    if (exempt)
    {
        await next();
        return;
    }

    if (!ProviderReloadGate.Instance.TryEnter())
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new
        {
            status = ProviderReloadGate.Instance.State,
            message = "The API is reloading the latest Fortnite build. Retry shortly.",
            statusEndpoint = "/api/v1/build"
        });
        return;
    }

    try
    {
        await next();
    }
    finally
    {
        ProviderReloadGate.Instance.Exit();
    }
});

// Configuration for the Docker container
app.UseRouting();
app.UseCors();
app.MapControllers();

// Redirect to the Swagger UI when the root is accessed
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

var listeningPort = Environment.GetEnvironmentVariable("PORT") ?? "3849";
Console.WriteLine($"\n✓ Server ready to start");
Console.WriteLine($"Listening on http://0.0.0.0:{listeningPort}\n");

app.Run();
