global using EpicManifestParser;

using FortnitePorting.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration.AddEnvironmentVariables();
if (args != null) builder.Configuration.AddCommandLine(args);

builder.Host.UseContentRoot(Directory.GetCurrentDirectory());

var port = AppStartup.Port;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddApiEndpoints();
builder.Services.AddHostedService<AesKeyMonitorService>();
builder.Services.AddHostedService<AesFinderKeyService>();

AppStartup.PrintBanner();

if (SelfUpdateService.RunStartupUpdate())
{
    return;
}

var mounted = AppStartup.InitializeFileProvider();
if (mounted == null)
{
    return;
}

builder.Services.AddMountedBuild(mounted);

var app = builder.Build();
app.RegisterCaches();
app.UseApiEndpoints();

Console.WriteLine($"\n✓ Server ready to start");
Console.WriteLine($"Listening on http://0.0.0.0:{port}");
Console.WriteLine($"Swagger UI:  http://localhost:{port}/swagger\n");
app.Run();
