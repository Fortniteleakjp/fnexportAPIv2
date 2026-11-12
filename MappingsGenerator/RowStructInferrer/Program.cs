using System.Diagnostics;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;
using FortnitePorting.Services.Local;
using FortnitePorting.Services.Mappings.RowStructInference;

// RowStructInferrer --game <install or Paks dir> --mappings <base.usmap> --out <extra.usmap>
//                   [--oodle <dll or dir>] [--key <hex | guid:hex>]... [--offline]
//
// Mounts the installed game with the keys compiled into its own binaries, finds the DataTables whose row struct
// is missing from <base.usmap> and writes the structs guessed from their cooked rows to <extra.usmap>
// (uncompressed), which UEFNStaticMappingsGenerator.exe adds to its output with --extra.
// Exit code 0: <extra.usmap> was written (possibly with no structs), anything else: nothing was written.

string? game = null, mappings = null, output = null, oodle = null;
var keys = new List<string>();
var offline = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--game": game = Next(); break;
        case "--mappings": mappings = Next(); break;
        case "--out": output = Next(); break;
        case "--oodle": oodle = Next(); break;
        case "--key": keys.Add(Next()); break;
        case "--offline": offline = true; break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}");
            Console.Error.WriteLine("Usage: RowStructInferrer --game <dir> --mappings <base.usmap> --out <extra.usmap> [--oodle <dll|dir>] [--key <hex|guid:hex>]... [--offline]");
            return 2;
    }
}
if (mappings is null || output is null)
{
    Console.Error.WriteLine("--mappings and --out are required");
    return 2;
}

try
{
    var timer = Stopwatch.StartNew();
    InitializeOodle(oodle);

    // the API's local install support: finds every Paks directory (plugins included) under the install
    var install = LocalInstallLocator.Resolve(game);
    var directories = install.PakDirectories.Select(d => new DirectoryInfo(d)).ToArray();
    using var provider = new DefaultFileProvider(directories[0], directories.Skip(1).ToArray(), SearchOption.TopDirectoryOnly,
        versions: new VersionContainer(EGame.GAME_UE6_0), pathComparer: StringComparer.OrdinalIgnoreCase);
    provider.Initialize();
    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappings);

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var resolved = await LocalKeyResolver.ResolveAsync(provider,
        new LocalKeyResolver.Options { Root = install.Root, RequestedKeys = keys, UseLiveApi = !offline }, http);
    if (resolved.Submittable.Count > 0) provider.SubmitKeys(resolved.Submittable);
    provider.Mount();
    try { provider.PostMount(); }
    catch (Exception e) { Console.WriteLine($"    Reading the game's configs failed: {e.Message}"); }

    var missingKeys = resolved.Keys.Count(k => !k.Verified);
    Console.WriteLine($"    Mounted {install.Root}: {provider.Files.Count} files, {provider.MountedVfs.Count} archives " +
                      $"({resolved.Submittable.Count} keys{(missingKeys > 0 ? $", {missingKeys} without a key" : "")}) in {timer.Elapsed.TotalSeconds:F1}s");
    if (provider.MappingsForGame is not { } target)
        throw new InvalidDataException($"{mappings} could not be read");

    var result = RowStructScanner.Scan(provider, target, line => Console.WriteLine("    " + line), CancellationToken.None);
    File.WriteAllBytes(output, ExtraUsmapWriter.Write(result.Structs));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"    {e.GetType().Name}: {e.Message}");
    return 1;
}

// Fortnite's archives are Oodle compressed; the game ships the library next to its binaries
static void InitializeOodle(string? path)
{
    if (path is not null && Directory.Exists(path))
        path = Directory.GetFiles(path, "oo2core_*_win64.dll").OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new FileNotFoundException($"No oo2core_*_win64.dll in {path}");
    if (path is not null && !File.Exists(path))
        throw new FileNotFoundException($"Oodle library not found: {path}");

    path ??= new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..") }
        .Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d, "oo2core_*_win64.dll"))
        .FirstOrDefault() ?? throw new FileNotFoundException("Pass --oodle with the game's oo2core_*_win64.dll");
    OodleHelper.Initialize(path);
}
