using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services.Local;

/// <summary>
/// Finds a Fortnite / UEFN installation that is already on this machine and the two parts of it the
/// API reads: the directories holding the .pak/.utoc containers, and the Win64 binaries the AES key
/// is compiled into.
///
/// Everything here walks the file system defensively. An installation sits next to directories the
/// service account cannot open (Saved, Cloud, per-user state), and <see cref="Directory"/>'s own
/// recursive enumeration aborts the whole walk on the first of those — which would make "point at
/// your install" fail for reasons that have nothing to do with the install.
/// </summary>
public static class LocalInstallLocator
{
    /// <summary>Environment variable naming the local installation, used when a request does not.</summary>
    public const string DirectoryVariable = "LOCAL_GAME_DIR";

    /// <summary>How deep below the given root pak containers and binaries are looked for.</summary>
    private const int MaxDepth = 8;

    /// <summary>Directories that never hold containers or game binaries, skipped to keep the walk short.</summary>
    private static readonly string[] SkippedDirectories =
    {
        "Saved", "Cloud", "Intermediate", "DerivedDataCache", "Logs", "Crashes", "CrashReportClient",
        "Backup", "ThirdParty", ".egstore"
    };

    /// <summary>An installation this machine appears to have, with how it was found.</summary>
    public sealed class Detected
    {
        public required string Directory { get; init; }
        public required string Source { get; init; }
        public string? DisplayName { get; init; }
        public string? AppName { get; init; }
        public string? BuildVersion { get; init; }
        public bool HasArchives { get; init; }
    }

    /// <summary>An installation resolved far enough to mount: where its containers and binaries are.</summary>
    public sealed class Resolved
    {
        public required string Root { get; init; }
        public required IReadOnlyList<string> PakDirectories { get; init; }
        public required int ArchiveCount { get; init; }
    }

    /// <summary>
    /// Lists the installations this machine appears to have: the one named by
    /// <see cref="DirectoryVariable"/>, whatever the Epic Games Launcher has recorded, and the
    /// default install locations. Nothing is mounted and no key is read.
    /// </summary>
    public static IReadOnlyList<Detected> Detect()
    {
        var found = new List<Detected>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory, string source, string? displayName = null, string? appName = null, string? build = null)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;

            string full;
            try { full = Path.GetFullPath(directory); }
            catch { return; }

            if (!Directory.Exists(full) || !seen.Add(full)) return;

            found.Add(new Detected
            {
                Directory = full,
                Source = source,
                DisplayName = displayName,
                AppName = appName,
                BuildVersion = build,
                HasArchives = HasArchives(full)
            });
        }

        Add(Environment.GetEnvironmentVariable(DirectoryVariable), $"{DirectoryVariable} environment variable");

        foreach (var installed in ReadLauncherManifests())
        {
            Add(installed.Directory, installed.Source, installed.DisplayName, installed.AppName, installed.BuildVersion);
        }

        foreach (var guess in DefaultInstallPaths())
        {
            Add(guess, "default install location");
        }

        return found;
    }

    /// <summary>
    /// Resolves the directory a request named — or, when it named none, the first detected
    /// installation that actually has containers — into the pak directories to register.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist, or holds no containers.</exception>
    public static Resolved Resolve(string? directory)
    {
        var root = directory;

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Detect().FirstOrDefault(d => d.HasArchives)?.Directory
                   ?? throw new DirectoryNotFoundException(
                       "No local Fortnite installation was found. Pass 'dir' with the install directory " +
                       $"(or the folder holding the .pak/.utoc files), or set {DirectoryVariable}.");
        }

        string full;
        try { full = Path.GetFullPath(root); }
        catch (Exception ex) { throw new DirectoryNotFoundException($"'{root}' is not a usable path: {ex.Message}"); }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Directory not found: {full}");
        }

        var pakDirectories = FindPakDirectories(full);
        if (pakDirectories.Count == 0)
        {
            throw new DirectoryNotFoundException(
                $"No .pak or .utoc containers were found under '{full}'. Point 'dir' at the installation " +
                "root or directly at its Paks folder.");
        }

        var archives = pakDirectories.Sum(CountArchives);

        return new Resolved
        {
            Root = full,
            PakDirectories = pakDirectories,
            ArchiveCount = archives
        };
    }

    /// <summary>
    /// The directories below <paramref name="root"/> that hold at least one .pak/.utoc, ordered so the
    /// main Paks folder comes first. The root itself is included when the caller pointed straight at a
    /// Paks folder.
    /// </summary>
    public static IReadOnlyList<string> FindPakDirectories(string root)
    {
        var directories = new List<string>();

        foreach (var directory in EnumerateDirectories(root))
        {
            if (CountArchives(directory.FullName) > 0)
            {
                directories.Add(directory.FullName);
            }
        }

        // The main Paks folder is the one holding the global container; register it first so its mount
        // point wins over a plugin's when two containers describe the same path.
        return directories
            .OrderByDescending(d => File.Exists(Path.Combine(d, "global.utoc")))
            .ThenByDescending(d => CountArchives(d))
            .ThenBy(d => d.Length)
            .ToList();
    }

    /// <summary>
    /// The Win64 binaries of an installation, ordered with the modules that carry the pak key first
    /// (the shipping executables and the UEFN Common DLL), then largest first.
    /// </summary>
    public static IReadOnlyList<FileInfo> FindBinaries(string root, int limit = 64)
    {
        var binaries = new List<FileInfo>();

        foreach (var directory in EnumerateDirectories(root))
        {
            FileInfo[] files;
            try { files = directory.GetFiles(); }
            catch { continue; }

            foreach (var file in files)
            {
                if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    binaries.Add(file);
                }
            }
        }

        return binaries
            .OrderByDescending(f => Rank(f.Name))
            .ThenByDescending(f => f.Length)
            .Take(Math.Max(1, limit))
            .ToList();

        static int Rank(string name)
        {
            // The UEFN Common DLL first: that is the module the pak key is compiled into, which is why
            // the manifest-side extractor (GET /aes) downloads that one file and nothing else. Putting
            // it ahead of the larger Engine DLL and the client executable ends the scan sooner.
            if (name.Contains("Common", StringComparison.OrdinalIgnoreCase)) return 4;
            if (name.Contains("Shipping", StringComparison.OrdinalIgnoreCase)) return 3;
            if (name.Contains("Fortnite", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Unreal", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }
    }

    /// <summary>
    /// Cheap "does this look like an installation?" check for the detection listing: the well-known
    /// container locations are tried first so a listing does not walk several whole installations.
    /// </summary>
    public static bool HasArchives(string root)
    {
        if (CountArchives(root) > 0) return true;

        foreach (var known in new[]
                 {
                     Path.Combine(root, "FortniteGame", "Content", "Paks"),
                     Path.Combine(root, "Engine", "Content", "Paks"),
                     Path.Combine(root, "Content", "Paks")
                 })
        {
            if (CountArchives(known) > 0) return true;
        }

        return FindPakDirectories(root).Count > 0;
    }

    /// <summary>Number of .pak/.utoc containers directly inside a directory.</summary>
    private static int CountArchives(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)
                            || f.Extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Depth-limited directory walk that skips what it may not read instead of failing. A single
    /// unreadable subdirectory would otherwise abort an AllDirectories enumeration of the whole install.
    /// </summary>
    private static IEnumerable<DirectoryInfo> EnumerateDirectories(string root)
    {
        DirectoryInfo start;
        try { start = new DirectoryInfo(root); }
        catch { yield break; }

        if (!start.Exists) yield break;

        var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
        pending.Enqueue((start, 0));

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();
            yield return current;

            if (depth >= MaxDepth) continue;

            DirectoryInfo[] children;
            try { children = current.GetDirectories(); }
            catch { continue; }

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (SkippedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    /// <summary>
    /// Reads the Epic Games Launcher's installation manifests. They record exactly where each app was
    /// installed, which beats guessing at default paths when the user moved the install to another drive.
    /// </summary>
    private static IEnumerable<(string Directory, string Source, string? DisplayName, string? AppName, string? BuildVersion)> ReadLauncherManifests()
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        string[] files;
        try
        {
            if (!Directory.Exists(manifestDir)) yield break;
            files = Directory.GetFiles(manifestDir, "*.item");
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            JObject item;
            try { item = JObject.Parse(File.ReadAllText(file)); }
            catch { continue; }

            var location = item.Value<string>("InstallLocation");
            if (string.IsNullOrWhiteSpace(location)) continue;

            var displayName = item.Value<string>("DisplayName");
            var appName = item.Value<string>("AppName");
            var catalogItem = item.Value<string>("MainGameAppName");

            var isFortnite = new[] { displayName, appName, catalogItem, location }
                .Any(v => v?.Contains("Fortnite", StringComparison.OrdinalIgnoreCase) == true);
            if (!isFortnite) continue;

            yield return (location, "Epic Games Launcher manifest", displayName, appName, item.Value<string>("AppVersionString"));
        }
    }

    /// <summary>The paths Fortnite installs into when nobody moved it, across the machine's drives.</summary>
    private static IEnumerable<string> DefaultInstallPaths()
    {
        var relative = new[]
        {
            Path.Combine("Program Files", "Epic Games", "Fortnite"),
            Path.Combine("Epic Games", "Fortnite"),
            "Fortnite"
        };

        string[] roots;
        try
        {
            roots = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToArray();
        }
        catch
        {
            roots = [];
        }

        foreach (var root in roots)
        {
            foreach (var tail in relative)
            {
                yield return Path.Combine(root, tail);
            }
        }
    }
}
