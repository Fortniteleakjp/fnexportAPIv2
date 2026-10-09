using System.Diagnostics;
using System.Collections.Concurrent;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.FileProvider.Vfs;
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
var catalog = descriptors.Select(d => new { route = d.AttributeRouteInfo?.Template, controller = d.ControllerName, action = d.ActionName, methods = d.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToArray() ?? [] }).OrderBy(d => d.route, StringComparer.Ordinal).ToArray();
Check(catalog.Length == 53 && JToken.DeepEquals(JArray.FromObject(catalog), contract["endpoints"]), "53 canonical endpoints, including HTTP methods");
var index = FileIndex.For(provider);
var filesController = new FilesController(build);
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
    ["itemsPage"] = Measure(() => filesController.GetFiles(prefixes: "WID_,AGID_,Athena_,Figment_Athena_", ext: ".uasset", page: 2, pageSize: 1000), 20),
    ["prefixWithExtension"] = Measure(() => index.Enumerate("FortniteGame/Content/Group050/", new[] { ".uasset" }).Count(), 100)
};
var signatures = new Dictionary<string, string>();
foreach (var url in new[] {
    "/api/v1/files?prefixes=WID_&ext=.uasset&page=2&pageSize=100",
    "/api/v1/files?prefixes=WID_,AGID_,Athena_,Figment_Athena_&ext=.uasset&excludePrefixes=WID_&pageSize=20",
    "/api/v1/files?prefixes=WID_,AGID_,Athena_,Figment_Athena_&ext=.uasset&excludePaths=Group000&pageSize=20",
    "/api/v1/files?page=2147483647&pageSize=5000",
    "/api/v1/search?q=Item&field=name&ext=uasset&pageSize=5000",
    "/api/v1/search?q=FortniteGame/Content/Group050/&mode=prefix&field=path&ext=uasset",
    "/api/v1/config/query?file=Game.ini&section=Section&key=Value",
    "/api/v1/files?page=2",
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
await VerifyConsolidatedEndpoints();
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
    var url = "/api/v1/files?prefixes=WID_&ext=.uasset&pageSize=100";
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
    foreach (var pageUrl in new[] { "/api/v1/search?q=Item&page=2147483647&pageSize=5000", "/api/v1/files?page=2147483647" })
    {
        var result = JObject.Parse(await client.GetStringAsync(pageUrl));
        Check(!(result["results"] ?? result["files"])!.Any(), "overflow-safe page: " + pageUrl);
    }
    var contentUrl = "/api/v1/search?target=content&q=second&ext=ini&dir=FortniteGame/Config/";
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

async Task VerifyConsolidatedEndpoints()
{
    using var monitor = new AesKeyMonitorService(app.Services, NullLogger<AesKeyMonitorService>.Instance);
    var sourceUrl = (string)typeof(AesKeyMonitorService).GetField("_archiveKeysUrl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor)!;
    Check(catalog.Any(endpoint => "/" + endpoint.route == new Uri(sourceUrl).AbsolutePath && endpoint.methods.Contains("GET")), "AES monitor calls an existing canonical endpoint");

    foreach (var url in new[] { "/api/v1/search?q=test&target=unknown", "/api/v1/paks?state=unknown", "/api/v1/backup?format=unknown" })
    {
        using var response = await client.GetAsync(url);
        Check((int)response.StatusCode == 400, "invalid option: " + url);
    }
    var all = JObject.Parse(await client.GetStringAsync("/api/v1/files?pageSize=1"));
    Check(all["totalFiles"]!.Value<int>() == provider.Files.Count && all["extension"]!.Value<string>() == "(all)", "unfiltered file listing covers every extension");
    var binary = JObject.Parse(await client.GetStringAsync("/api/v1/files?ext=.bin"));
    Check(binary["totalFiles"]!.Value<int>() == 1, "file extension filter");
    using (var result = await client.GetAsync("/api/v1/items/properties?path=missing.uasset"))
        Check((int)result.StatusCode == 404, "single item path uses the properties endpoint");
    using (var result = await client.GetAsync("/api/v1/items/properties?path=FortniteGame/Content/Group000/WID_Item0000000.uasset"))
        Check(result.IsSuccessStatusCode && JObject.Parse(await result.Content.ReadAsStringAsync())["error"] != null, "single item reports parse failures without a second route");
    var backup = JObject.Parse(await client.GetStringAsync("/api/v1/backup"));
    var downloadUrl = backup["downloadUrl"]!.Value<string>()!;
    Check(downloadUrl.Contains("format=fbkp"), "backup download URL selects the representation");
    using (var result = await client.GetAsync(downloadUrl + "&compress=false"))
    {
        var bytes = await result.Content.ReadAsByteArrayAsync();
        Check(result.IsSuccessStatusCode && Encoding.ASCII.GetString(bytes, 0, 4) == "FBKP", "fbkp download on the backup endpoint");
    }
    foreach (var state in new[] { "mounted", "unloaded", "all" })
    {
        var result = JObject.Parse(await client.GetStringAsync("/api/v1/paks?state=" + state));
        Check(result["state"]!.Value<string>() == state && result["totalPaks"]!.Value<int>() == 0, "archive state filter: " + state);
    }
    var cosmetics = JObject.Parse(await client.GetStringAsync("/api/v1/cosmetics?includeOffers=true"));
    Check(cosmetics["total"]!.Value<int>() == 0 && cosmetics["includeOffers"]!.Value<bool>(), "cosmetics collection includes optional offers");
    using (var result = await client.GetAsync("/api/v1/cosmetics?pakName=missing"))
        Check((int)result.StatusCode == 404, "archive-scoped cosmetics rejects unknown archives");
    using (var result = await client.GetAsync("/api/v1/localization?lang=ja"))
        Check((int)result.StatusCode == 404, "merged localization reports absent language data");
    using (var result = await client.GetAsync("/api/v1/localization?langs=ja,en"))
        Check((int)result.StatusCode == 400, "merged localization rejects multiple languages");
    var cosmetic = new MemoryFile("FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics/Character_Test.uasset", [1, 2, 3]);
    var offer = new MemoryFile("FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets/DA_TestBundle.uasset", [1, 2, 3]);
    var mounted = new MemoryArchive("pakchunk55-Windows.pak", [cosmetic, offer]);
    var unloaded = new MemoryArchive("pakchunk99-unloaded.pak", [new MemoryFile("unloaded/file.bin", [0])]);
    void RegisterArchive(string fieldName, MemoryArchive archive)
    {
        var field = typeof(AbstractVfsFileProvider).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ConcurrentDictionary<IAesVfsReader, object?>)field.GetValue(provider)!).TryAdd(archive, null);
    }
    RegisterArchive("_mountedVfs", mounted);
    RegisterArchive("_unloadedVfs", unloaded);
    provider.Files.AddFiles(new Dictionary<string, GameFile> { [cosmetic.Path] = cosmetic, [offer.Path] = offer });
    var mountedResult = JObject.Parse(await client.GetStringAsync("/api/v1/paks"));
    Check(mountedResult["totalPaks"]!.Value<int>() == 1 && mountedResult["paks"]![0]!["isEnabled"]!.Value<bool>(), "mounted PAK metadata and pagination");
    var unloadedResult = JObject.Parse(await client.GetStringAsync("/api/v1/paks?state=unloaded"));
    Check(unloadedResult["totalPaks"]!.Value<int>() == 1 && !unloadedResult["paks"]![0]!["isEnabled"]!.Value<bool>(), "unmounted metadata retained after consolidation");
    var registered = JObject.Parse(await client.GetStringAsync("/api/v1/paks?state=all"));
    Check(registered["totalPaks"]!.Value<int>() == 2, "all registered archives listed");
    var chunkFiles = JObject.Parse(await client.GetStringAsync("/api/v1/paks/55/files?page=2&pageSize=1"));
    Check(chunkFiles["totalFiles"]!.Value<int>() == 2 && chunkFiles["files"]!.Count() == 1, "chunk file listing remains paginated");
    var scoped = JObject.Parse(await client.GetStringAsync("/api/v1/cosmetics?pakName=55&q=Test&includeOffers=true"));
    Check(scoped["total"]!.Value<int>() == 2 && scoped["totalOfferCatalogDisplayAssets"]!.Value<int>() == 1, "archive-scoped cosmetics retains bundle displays");
    var definitions = JObject.Parse(await client.GetStringAsync("/api/v1/cosmetics?q=Test&category=Character"));
    Check(definitions["total"]!.Value<int>() == 1 && definitions["totalOfferCatalogDisplayAssets"]!.Value<int>() == 0, "global cosmetics filtering retains definitions-only default");

    byte[] Locres(string value)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true);
        void String(string text) { writer.Write(-(text.Length + 1)); writer.Write(Encoding.Unicode.GetBytes(text + "\0")); }
        writer.Write(0x7574140Eu); writer.Write(0xFC034A67u); writer.Write(0x9D90154Au); writer.Write(0x1B7F37C3u);
        writer.Write((byte)0); writer.Write((uint)1); String("TestNamespace"); writer.Write((uint)1); String("TestKey"); writer.Write((uint)0); String(value);
        return buffer.ToArray();
    }
    var japanese = new MemoryFile("FortniteGame/Content/Localization/Game/ja/Game.locres", Locres("日本語のテスト"));
    var english = new MemoryFile("FortniteGame/Content/Localization/Game/en/Game.locres", Locres("Test translation"));
    provider.Files.AddFiles(new Dictionary<string, GameFile> { [japanese.Path] = japanese, [english.Path] = english });
    var table = JObject.Parse(await client.GetStringAsync("/api/v1/localization?lang=ja"));
    Check(table["TestNamespace"]?["TestKey"]?.Value<string>() == "日本語のテスト", "merged table retains namespace and key shape");
    var translations = JObject.Parse(await client.GetStringAsync("/api/v1/localization?key=TestKey"));
    Check(translations["results"]![0]!["translations"]!["en"]!.Value<string>() == "Test translation", "key lookup works on the unified localization endpoint");
    var reverse = JObject.Parse(await client.GetStringAsync("/api/v1/localization?text=" + Uri.EscapeDataString("日本語") + "&lang=ja"));
    Check(reverse["totalMatches"]!.Value<int>() == 1, "reverse lookup works on the unified localization endpoint");
    var languages = JObject.Parse(await client.GetStringAsync("/api/v1/localization/languages"));
    Check(languages["languages"]!.Count() == 2, "language listing remains available");
    foreach (var lang in new[] { "ja", "en" })
    {
        var document = JObject.Parse(await client.GetStringAsync("/swagger/" + lang + "/swagger.json"));
        var paths = (JObject)document["paths"]!;
        Check(paths["/api/v1/files"] != null && paths["/api/v1/cosmetics"] != null && paths["/api/v1/localization"] != null, lang + " canonical collections documented");
        Check(!paths.Properties().Any(p => p.Name.Contains("/debug/") || p.Name.Contains("/dump") || p.Name == "/api/v1/archives" || p.Name == "/api/v1/search/content" || p.Name == "/api/v1/backup/fbkp"), lang + " duplicate routes removed from Swagger");
        var parameters = (JArray)paths["/api/v1/search"]!["get"]!["parameters"]!;
        Check(parameters.Any(p => p["name"]!.Value<string>() == "target"), lang + " search target documented");
    }
    foreach (var url in new[] { "/api/v1/debug/stats", "/api/v1/debug/search?query=test", "/api/v1/archives", "/api/v1/archives/keys", "/api/v1/items/files", "/api/v1/items/properties/single", "/api/v1/search/content?q=test", "/api/v1/export/filepath/test", "/api/v1/export/locres", "/api/v1/export/locres/languages", "/api/v1/backup/fbkp", "/api/v1/pak/test/cosmetics", "/aes" })
    {
        using var result = await client.GetAsync(url);
        Check((int)result.StatusCode == 404, "removed URL: " + url);
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

sealed class MemoryArchive : AbstractAesVfsReader
{
    public MemoryArchive(string path, GameFile[] files) : base(path, new VersionContainer())
    {
        Files = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        CompressionMethods = [CompressionMethod.None];
    }
    public override long Length { get; set; } = 100;
    public override FGuid EncryptionKeyGuid => default;
    public override bool IsEncrypted => false;
    public override string MountPoint { get; protected set; } = "../../../";
    public override bool HasDirectoryIndex => true;
    public override byte[] MountPointCheckBytes() => [];
    protected override byte[] ReadAndDecrypt(int length) => new byte[length];
    public override void Mount(StringComparer pathComparer) { }
    public override byte[] Extract(VfsEntry entry, FByteBulkDataHeader? header = null) => entry.Read(header);
    public override void Dispose() { }
}
