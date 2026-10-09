using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using System.Reflection;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;
using FortnitePorting;
using FortnitePorting.Controllers;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

var output = args.FirstOrDefault();
var contract = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contract.json")));
var checks = 0;
void Check(bool valid, string name)
{
    if (!valid) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
var root = Path.Combine(Path.GetTempPath(), "fnexport-response-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var provider = new DefaultFileProvider(root, SearchOption.TopDirectoryOnly, pathComparer: StringComparer.OrdinalIgnoreCase);
var files = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < 200000; i++)
{
    var path = $"FortniteGame/Content/Group{i % 100:D3}/{(i % 5 == 0 ? "WID_" : "Athena_")}Item{i:D7}.uasset";
    files.Add(path, new MemoryFile(path, [1, 2, 3]));
}
var text = new MemoryFile("FortniteGame/Config/Game.ini", Encoding.UTF8.GetBytes("[Section]\nValue=日本語\nValue=second\n"));
files.Add(text.Path, text);
files.Add("FortniteGame/Content/Other/file.bin", new MemoryFile("FortniteGame/Content/Other/file.bin", [0, 1, 2]));
provider.Files.AddFiles(files);
var history = new BuildHistoryStore(root);
var manifest = new ManifestService(provider, root, null!, root, history);
await using var historical = new HistoricalBuildService(history, null!, provider, manifest);
var diffs = new BuildDiffService(history, historical, provider, manifest);
var build = new RequestBuildProvider(provider, manifest);
using var cache = new MemoryCache(new MemoryCacheOptions());
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton<IFileProvider>(provider);
builder.Services.AddSingleton(manifest);
builder.Services.AddSingleton(cache as IMemoryCache);
builder.Services.AddSingleton(history);
builder.Services.AddSingleton(historical);
builder.Services.AddSingleton(diffs);
builder.Services.AddApiEndpoints();
builder.Services.AddControllers().AddApplicationPart(typeof(ItemsController).Assembly);
await using var app = builder.Build();
app.UseApiEndpoints();
app.MapGet("/test/binary", () => Results.Bytes([0, 1, 2, 3], "application/octet-stream"));
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
var descriptors = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items.OfType<ControllerActionDescriptor>();
var routes = descriptors.Select(d => new
{
    controller = d.ControllerName, action = d.ActionName, route = d.AttributeRouteInfo?.Template,
    parameters = d.Parameters.Select(p => new { p.Name, type = p.ParameterType.FullName }).ToArray()
}).OrderBy(d => d.route, StringComparer.Ordinal).ThenBy(d => d.action, StringComparer.Ordinal).ToArray();
Check(JToken.DeepEquals(JArray.FromObject(routes), contract["routes"]), "68 routes and action parameters unchanged");
var catalog = descriptors.Select(d => new { route = d.AttributeRouteInfo?.Template, controller = d.ControllerName, action = d.ActionName, methods = d.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToArray() ?? [] }).OrderBy(d => d.route).ToArray();
var index = FileIndex.For(provider);
var items = new ItemsController(provider, NullLogger<ItemsController>.Instance);
var search = new SearchController(build, NullLogger<SearchController>.Instance, cache)
{ ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

object Measure(Func<object> action, int repetitions)
{
    for (var i = 0; i < 3; i++) action();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    for (var i = 0; i < repetitions; i++) action();
    return new { milliseconds = watch.Elapsed.TotalMilliseconds / repetitions,
        allocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / repetitions };
}

var metrics = new Dictionary<string, object>
{
    ["itemsPage"] = Measure(() => items.GetFiles(page: 2, pageSize: 1000), 20),
    ["prefixWithExtension"] = Measure(() => index.Enumerate("FortniteGame/Content/Group050/", new[] { ".uasset" }).Count(), 100)
};
var signatures = new Dictionary<string, string>();
foreach (var url in new[] {
    "/api/v1/items/files?prefixes=WID_&page=2&pageSize=100",
    "/api/v1/items/files?excludePrefixes=WID_&pageSize=20",
    "/api/v1/items/files?excludePaths=Group000&ext=&pageSize=20",
    "/api/v1/items/files?page=2147483647&pageSize=5000",
    "/api/v1/search?q=Item&field=name&ext=uasset&pageSize=5000",
    "/api/v1/search?q=FortniteGame/Content/Group050/&mode=prefix&field=path&ext=uasset",
    "/api/v1/config/query?file=Game.ini&section=Section&key=Value",
    "/api/v1/debug/stats?page=2",
    "/api/v1/mappings/uefn" })
{
    using var response = await client.GetAsync(url);
    var body = await response.Content.ReadAsStringAsync();
    signatures[url] = $"{(int)response.StatusCode}:" + JToken.Parse(body).ToString(Formatting.None);
    if (contract["responses"]?[url] is { } expected)
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatures[url]))) == expected.Value<string>(), "response contract: " + url);
    if (url.Contains("page=2147483647")) Check(!JToken.Parse(body)["files"]!.Any(), "large page returns no files without overflow");
}
metrics["cachedSearch"] = Measure(() => search.Search(q: "Item", field: "name", ext: "uasset", pageSize: 5000), 30);
text.Reads = 0;
var read = VersionedAssetReader.Read(provider, text.Path, raw: false);
if (read.Text != "[Section]\nValue=日本語\nValue=second\n") throw new Exception("Text response changed.");
metrics["textReads"] = text.Reads;
var payload = new JArray(Enumerable.Range(0, 25000).Select(i => new JObject { ["name"] = "日本語_" + i, ["value"] = i }));
metrics["jsonSerialize"] = Measure(() => JsonResponse.Serialize(payload), 10);
await VerifyInfrastructure();
var result = new { routes, catalog, signatures, metrics };
if (output != null) await File.WriteAllTextAsync(output, JsonConvert.SerializeObject(result, Formatting.Indented));
Console.WriteLine(JsonConvert.SerializeObject(metrics, Formatting.Indented));
Console.WriteLine($"Verified {checks} checks, {routes.Length} routes and {signatures.Count} HTTP responses.");
await app.StopAsync();

async Task VerifyInfrastructure()
{
    Check(text.Reads == 1, "text export reads file once");
    foreach (var formatting in new[] { Formatting.None, Formatting.Indented })
    {
        object?[] samples = [null, new { text = "日本語\n😀", number = 3.125m, timestamp = DateTime.UnixEpoch, nested = new[] { 1, 2 } }, payload];
        foreach (var sample in samples)
            Check(JsonResponse.Serialize(sample, formatting).SequenceEqual(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(sample, formatting))), "UTF-8 JSON matches Newtonsoft output");
    }
    Check(JToken.DeepEquals(JsonResponse.Parse(JsonResponse.Serialize(payload)), payload), "batch JSON parse round trip");
    var url = "/api/v1/items/files?prefixes=WID_&pageSize=100";
    var plain = await client.GetByteArrayAsync(url);
    foreach (var encoding in new[] { "gzip", "br" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await client.SendAsync(request);
        var encoded = await response.Content.ReadAsByteArrayAsync();
        Check(response.Content.Headers.ContentEncoding.Contains(encoding), encoding + " response encoding");
        using var input = new MemoryStream(encoded);
        using Stream decoder = encoding == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new BrotliStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await decoder.CopyToAsync(decoded);
        Check(decoded.ToArray().SequenceEqual(plain), encoding + " preserves response bytes");
        Check(encoded.Length < plain.Length / 2, encoding + " reduces transfer size");
        using var binary = new HttpRequestMessage(HttpMethod.Get, "/test/binary");
        binary.Headers.AcceptEncoding.ParseAdd(encoding);
        using var raw = await client.SendAsync(binary);
        Check(!raw.Content.Headers.ContentEncoding.Any(), encoding + " leaves binary content unchanged");
    }
    foreach (var lang in new[] { "ja", "en" })
    {
        using var response = await client.GetAsync("/swagger/" + lang + "/swagger.json");
        Check(response.IsSuccessStatusCode, lang + " Swagger document");
        var document = JObject.Parse(await response.Content.ReadAsStringAsync());
        Check(document["paths"]?["/api/v1/export"] != null, lang + " export appears in Swagger");
    }
    using (var unknown = await client.GetAsync("/api/v1/search?q=test&version=unknown-build"))
        Check((int)unknown.StatusCode == 404, "unknown build returns 404");
    foreach (var pageUrl in new[] { "/api/v1/search?q=Item&page=2147483647&pageSize=5000", "/api/v1/debug/stats?page=2147483647" })
    {
        var result = JObject.Parse(await client.GetStringAsync(pageUrl));
        Check(!(result["results"] ?? result["files"])!.Any(), "overflow-safe page: " + pageUrl);
    }
    var contentUrl = "/api/v1/search/content?q=second&ext=ini&dir=FortniteGame/Config/";
    var content = JObject.Parse(await client.GetStringAsync(contentUrl));
    Check(content["results"]!.Any(r => r["path"]!.Value<string>() == text.Path), "content search uses directory and extension index");
    var firstReadCount = text.Reads;
    await client.GetStringAsync(contentUrl);
    Check(text.Reads == firstReadCount, "cached content response avoids another file read");
    var gate = ProviderReloadGate.Instance;
    Check(gate.InFlightRequests == 0, "requests leave reload gate");
    await gate.BeginReloadAsync("test-reload", TimeSpan.Zero);
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://example.test");
        using var blocked = await client.SendAsync(request);
        Check((int)blocked.StatusCode == 503 && blocked.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(30), "reload returns 503 and Retry-After");
        Check(blocked.Headers.Contains("Access-Control-Allow-Origin"), "reload response includes CORS");
        using var versions = await client.GetAsync("/api/v1/versions");
        Check(versions.IsSuccessStatusCode, "build listing remains available during reload");
    }
    finally { gate.EndReload(); }
    var failingMiddleware = new ProviderReloadMiddleware(_ => throw new InvalidOperationException("test failure"));
    var context = new DefaultHttpContext();
    context.Request.Path = "/api/v1/items";
    try { await failingMiddleware.InvokeAsync(context); } catch (InvalidOperationException) { }
    Check(gate.InFlightRequests == 0, "exception releases reload gate");
    var before = new RequestBuildProvider(provider, manifest);
    var oldScope = before.CacheScope;
    CacheRegistry.Register("test file index", FileIndex.ClearAll);
    CacheRegistry.Register("test archive index", ArchiveFileIndex.Clear);
    CacheRegistry.ClearAll();
    Check(before.CacheScope == oldScope && new RequestBuildProvider(provider, manifest).CacheScope != oldScope, "in-flight cache scope stays isolated after invalidation");
    Check(!ReferenceEquals(index, FileIndex.For(provider)), "cache clear creates fresh index");
    var added = new MemoryFile("FortniteGame/Content/Group050/WID_Added.uasset", [1]);
    provider.Files.AddFiles(new Dictionary<string, GameFile> { [added.Path] = added });
    var updated = FileIndex.For(provider);
    Check(updated.Contains(added.Path) && updated.Count == index.Count + 1, "mount change rebuilds index");
    Check(updated.Enumerate("FortniteGame/Content/Group050/", new[] { ".uasset" }).Count() == 2001, "directory and extension boundaries");
    Check(updated.MatchingNames(["WID_"], ".uasset", [], []).Count == 40001, "name query reflects mounted files");
    var archive = new Dictionary<string, GameFile> { [text.Path] = text, [added.Path] = added };
    var paths = ArchiveFileIndex.For(archive);
    Check(paths.SequenceEqual(archive.Keys.Order(StringComparer.OrdinalIgnoreCase)), "archive file ordering");
    Check(ReferenceEquals(paths, ArchiveFileIndex.For(archive)), "archive index reuses sorted paths");
    archive.Remove(added.Path);
    Check(ArchiveFileIndex.For(archive).Count == 1, "archive count change invalidates paths");

    var mapping = Path.Combine(root, "empty.usmap");
    using (var writer = new BinaryWriter(File.Create(mapping)))
    {
        writer.Write((ushort)0x30c4);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((uint)12);
        writer.Write((uint)12);
        writer.Write(new byte[12]);
    }
    var previousPath = Environment.GetEnvironmentVariable("USMAP_PATH");
    var previousSkip = Environment.GetEnvironmentVariable("SKIP_MAPPING");
    try
    {
        Environment.SetEnvironmentVariable("USMAP_PATH", mapping);
        Environment.SetEnvironmentVariable("SKIP_MAPPING", "false");
        Check(FileProviderFactory.ReloadMappings(provider, root, null) == mapping, "mapping loads from local file");
        var container = provider.MappingsContainer;
        var generation = CacheRegistry.Generation;
        FileProviderFactory.ReloadMappings(provider, root, null);
        Check(ReferenceEquals(container, provider.MappingsContainer) && CacheRegistry.Generation == generation, "unchanged mapping skips parsing and invalidation");
        File.SetLastWriteTimeUtc(mapping, File.GetLastWriteTimeUtc(mapping).AddSeconds(2));
        FileProviderFactory.ReloadMappings(provider, root, null);
        Check(!ReferenceEquals(container, provider.MappingsContainer) && CacheRegistry.Generation != generation, "changed mapping reloads and invalidates responses");
        generation = CacheRegistry.Generation;
        manifest.ApplyMapping(mapping);
        Check(CacheRegistry.Generation != generation, "explicit mapping replacement invalidates responses");
        container = provider.MappingsContainer;
        generation = CacheRegistry.Generation;
        await File.WriteAllBytesAsync(mapping, [1]);
        Check(FileProviderFactory.ReloadMappings(provider, root, null) == null && ReferenceEquals(container, provider.MappingsContainer) && CacheRegistry.Generation == generation, "invalid mapping retains last usable container");
    }
    finally
    {
        Environment.SetEnvironmentVariable("USMAP_PATH", previousPath);
        Environment.SetEnvironmentVariable("SKIP_MAPPING", previousSkip);
    }
}

sealed class MemoryFile(string path, byte[] data) : GameFile(path, data.Length)
{
    public int Reads;
    public override bool IsEncrypted => false;
    public override CompressionMethod CompressionMethod => CompressionMethod.None;
    public override byte[] Read(FByteBulkDataHeader? header = null) { Reads++; return data; }
    public override FArchive CreateReader(FByteBulkDataHeader? header = null) { Reads++; return new FByteArchive(Path, data); }
}
