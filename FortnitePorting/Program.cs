global using EpicManifestParser;

using CUE4Parse.FileProvider;
using FortnitePorting.Services;

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
        Description = "CUE4Parse を利用した Fortnite アセット解析 API。アセットの取得（JSON／画像／音声）、locres ローカライズ、PAK 内ファイル一覧、接頭辞によるアイテム検索・プロパティ抽出を提供します。"
    });

    // English document.
    options.SwaggerDoc("en", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Fortnite Asset Export API",
        Version = "v1",
        Description = "A Fortnite asset analysis API powered by CUE4Parse. Provides asset retrieval (JSON/image/audio), locres localization, listing of files inside PAK archives, and item search and property extraction by prefix."
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
                  "X-Usmap-ParsedEnums", "X-Usmap-ParsedStructs"));
});

Console.WriteLine("=================================");
Console.WriteLine("Fortnite Asset Export API");
Console.WriteLine("=================================\n");

// Initialize the FileProvider at startup and register it as a singleton
Console.WriteLine("Initializing FileProvider...\n");
var initializationResult = FileProviderFactory.CreateFileProvider();
Console.WriteLine("\n✓ FileProvider initialization complete\n");

builder.Services.AddSingleton<IFileProvider>(initializationResult.FileProvider);
builder.Services.AddSingleton(initializationResult.ManifestService);

var app = builder.Build();

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
    options.DocumentTitle = "Fortnite Asset Export API";
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
