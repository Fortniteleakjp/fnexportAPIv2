using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using FortnitePorting;
using FortnitePorting.Controllers;
using FortnitePorting.Services;
using FortnitePorting.Services.Mappings;

var directory = StaticMappingsGenerator.BinariesDirectory(Environment.GetEnvironmentVariable("UEFN_BINARIES_DIR"));
var build = StaticMappingsGenerator.ReadBuild(directory);
var tool = StaticMappingsGenerator.FindExecutable() ?? throw new Exception("Build the generator before running these tests.");
var root = Path.Combine(Path.GetTempPath(), "fnexport-mapping-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("PROJECT_ROOT", root);
Environment.SetEnvironmentVariable("USMAP_GENERATOR_PATH", tool);
using var provider = new DefaultFileProvider(root, SearchOption.TopDirectoryOnly, pathComparer: StringComparer.OrdinalIgnoreCase);
var manifest = new ManifestService(provider, root, null!, root, new BuildHistoryStore(root));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(manifest);
builder.Services.AddControllers().AddApplicationPart(typeof(MappingsController).Assembly);
await using var app = builder.Build();
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromMinutes(2) };
var checks = 0;

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS " + label);
    checks++;
}

async Task<HttpResponseMessage> Post(string query, HttpStatusCode expected)
{
    var response = await client.PostAsync("/api/v1/mappings/" + query, null);
    if (response.StatusCode != expected)
        throw new Exception($"{query}: expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    return response;
}

using (var response = await client.GetAsync("/api/v1/mappings/uefn"))
{
    using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(status.RootElement.GetProperty("ready").GetBoolean(), "UEFN readiness without a running editor");
    Check(status.RootElement.GetProperty("build").GetString() == build, "build identity comes from installed UEFN");
}

using (await Post("generate?compression=invalid", HttpStatusCode.BadRequest)) { checks++; }
using (await Post("generate?compression=zstd&level=0", HttpStatusCode.BadRequest)) { checks++; }
using (await Post("generate?timeoutSeconds=0", HttpStatusCode.BadRequest)) { checks++; }
using (await Post("generate?fileName=../outside.usmap", HttpStatusCode.BadRequest)) { checks++; }
using (await Post("generate?url=https://example.invalid/mapping.json", HttpStatusCode.BadRequest)) { checks++; }
using (await Post("generate?load=true", HttpStatusCode.Conflict)) { checks++; }
using (await Post("generate?dir=" + Uri.EscapeDataString(root), HttpStatusCode.FailedDependency)) { checks++; }
Check(!Directory.Exists(MappingStore.DirectoryPath), "invalid requests do not store mappings");

var brokenInstallation = Path.Combine(root, "broken-version");
Directory.CreateDirectory(brokenInstallation);
await File.WriteAllBytesAsync(Path.Combine(brokenInstallation, StaticMappingsGenerator.EngineModule), []);
await File.WriteAllBytesAsync(Path.Combine(brokenInstallation, StaticMappingsGenerator.CommonModule), []);
await File.WriteAllTextAsync(Path.Combine(brokenInstallation, StaticMappingsGenerator.VersionFile), "{\"BranchName\":\"test\",\"Changelist\":\"invalid\"}");
using (await Post("generate?dir=" + Uri.EscapeDataString(brokenInstallation), HttpStatusCode.BadRequest))
    Check(!Directory.Exists(MappingStore.DirectoryPath), "invalid version file is rejected before DLL loading");

int? expectedStructs = null, expectedEnums = null;
byte[]? original = null;
foreach (var (compression, id) in new[] { ("none", 0), ("zstd", 3), ("brotli", 2), ("oodle", 1) })
{
    using var response = await Post($"generate?compression={compression}&fileName={compression}.usmap", HttpStatusCode.OK);
    var data = await response.Content.ReadAsByteArrayAsync();
    var parser = new UsmapParser(data, compression);
    var counts = MappingStore.Verify(data, compression);
    expectedStructs ??= counts.Structs;
    expectedEnums ??= counts.Enums;
    Check((int)parser.CompressionMethod == id && counts.Structs > 10000 && counts.Enums > 1000,
        $"{compression}: verified {counts.Structs} structs / {counts.Enums} enums ({data.Length} bytes)");
    Check(counts.Structs == expectedStructs && counts.Enums == expectedEnums, compression + ": consistent type counts");
    var stored = await File.ReadAllBytesAsync(Path.Combine(MappingStore.DirectoryPath, compression + ".usmap"));
    Check(data.SequenceEqual(stored),
        compression + ": response matches the stored file");
    if (compression == "none") original = data;
}

using (var response = await Post("generate?compression=brotli&timeoutSeconds=1&fileName=none.usmap", HttpStatusCode.GatewayTimeout))
{
    var preserved = await File.ReadAllBytesAsync(Path.Combine(MappingStore.DirectoryPath, "none.usmap"));
    Check(original!.SequenceEqual(preserved),
        "timeout preserves the existing mapping");
}

var generator = new StaticMappingsGenerator();
using (var cancellation = new CancellationTokenSource())
{
    var pending = generator.GenerateAsync(new(directory, "brotli", 11, null, "cancelled.usmap", 120), cancellation.Token);
    await Task.Delay(150);
    using (await Post("generate", HttpStatusCode.Conflict)) { Check(true, "overlapping generation is rejected"); }
    cancellation.Cancel();
    try { await pending; throw new Exception("Generation did not cancel."); }
    catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(MappingStore.DirectoryPath, "cancelled.usmap")), "cancellation stores no partial output"); }
}

foreach (var alias in new[] { "dump", "dump/uefn", "dump/local" })
{
    using var response = await client.PostAsync("/api/v1/mappings/" + alias, null);
    Check(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, alias + ": duplicate route removed");
}

typeof(ManifestService).GetField("_appliedBuildVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manifest, build + "-Windows");
typeof(ManifestService).GetField("_currentBuildVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manifest, build + "-Windows");
using (var response = await Post("generate?compression=none&load=true", HttpStatusCode.OK))
    Check(provider.MappingsContainer != null && response.Headers.GetValues("X-Usmap-Loaded").Single() == "true", "matching build is loaded into the provider");

using (var response = await client.GetAsync("/api/v1/mappings/none.usmap"))
{
    var downloaded = await response.Content.ReadAsByteArrayAsync();
    Check(response.IsSuccessStatusCode && original!.SequenceEqual(downloaded), "stored mapping download");
}

var invalid = Path.Combine(root, "invalid.usmap");
await File.WriteAllTextAsync(invalid, "invalid");
using (await Post("import?path=" + Uri.EscapeDataString(invalid), HttpStatusCode.BadRequest))
    Check(!File.Exists(Path.Combine(MappingStore.DirectoryPath, "invalid.usmap")), "invalid import is rejected before storage");
using (await Post("import?path=" + Uri.EscapeDataString(Path.Combine(MappingStore.DirectoryPath, "none.usmap")) + "&fileName=imported.usmap", HttpStatusCode.OK))
    Check(File.Exists(Path.Combine(MappingStore.DirectoryPath, "imported.usmap")), "verified mapping import");
using (var response = await client.GetAsync("/api/v1/mappings"))
{
    using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(list.RootElement.GetProperty("count").GetInt32() >= 5, "stored mapping listing");
}
Check(Process.GetProcessesByName("UEFNStaticMappingsGenerator").Length == 0, "generator processes exited after completion, timeout and cancellation");
await app.StopAsync();
Console.WriteLine($"{checks} checks passed. Test outputs: {root}");
