using System.Diagnostics;
using System.Net;

namespace FortnitePorting.Services;

/// <summary>
/// Resolves the required native libraries (Oodle, zlib-ng), preferring a local copy and
/// downloading only as a last resort. Local lookup covers the project <c>libs/</c> folder,
/// the application base directory, and — importantly for single-file publishes — the real
/// executable directory (where AppContext.BaseDirectory may point at the bundle extraction dir).
/// </summary>
public static class LibraryDownloader
{
    private static readonly string LibsDirectory;

    static LibraryDownloader()
    {
        // Get the project root from an environment variable and set up the libs directory
        var projectRoot = Environment.GetEnvironmentVariable("PROJECT_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        LibsDirectory = Path.Combine(projectRoot, "libs");
        Directory.CreateDirectory(LibsDirectory);
    }

    /// <summary>
    /// Directories searched (in order) for an already-present native library.
    /// </summary>
    private static IEnumerable<string> SearchDirectories()
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d!)) dirs.Add(d!);
        }

        Add(LibsDirectory);
        Add(AppContext.BaseDirectory);
        Add(Path.Combine(AppContext.BaseDirectory, "libs"));

        // The real executable directory — for single-file publishes AppContext.BaseDirectory may
        // point at the temporary bundle-extraction directory rather than where the .exe lives.
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            var exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
            Add(exeDir);
            if (!string.IsNullOrEmpty(exeDir)) Add(Path.Combine(exeDir!, "libs"));
        }
        catch
        {
            // MainModule can be inaccessible in some hosts; ignore.
        }

        Add(Directory.GetCurrentDirectory());
        Add(Path.Combine(Directory.GetCurrentDirectory(), "libs"));
        return dirs;
    }

    /// <summary>
    /// Finds an existing native library by file name (optionally honoring an explicit env path).
    /// </summary>
    private static string? FindExisting(string fileName, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        foreach (var dir in SearchDirectories())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Downloads or verifies zlib-ng2.dll / libz-ng.so
    /// </summary>
    public static string EnsureZlibngDll()
    {
        const string baseUrl = "https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/";
        string fileName;

        if (OperatingSystem.IsWindows())
        {
            fileName = "zlib-ng2.dll";
        }
        else if (OperatingSystem.IsLinux())
        {
            fileName = "libz-ng.so";
        }
        else
        {
            throw new PlatformNotSupportedException("This platform is not supported");
        }

        var found = FindExisting(fileName);
        if (found != null)
        {
            Console.WriteLine($"Found zlib-ng DLL: {found}");
            return found;
        }

        var dest = Path.Combine(LibsDirectory, fileName);
        Console.WriteLine($"Downloading zlib-ng DLL: {baseUrl + fileName}");
        DownloadFile(baseUrl + fileName, dest);
        Console.WriteLine($"Downloaded zlib-ng DLL: {dest}");
        return dest;
    }

    /// <summary>
    /// Downloads or verifies the Oodle native library.
    /// Honors the OODLE_DLL_PATH environment variable when set.
    /// </summary>
    public static string EnsureOodleDll()
    {
        string fileName;
        string url;

        if (OperatingSystem.IsWindows())
        {
            fileName = "oo2core_9_win64.dll";
            url = "https://github.com/WorkingRobot/OodleUE/raw/main/Engine/Binaries/Win64/oo2core_9_win64.dll";
        }
        else if (OperatingSystem.IsLinux())
        {
            fileName = "liboo2corelinux64.so.9";
            url = "https://raw.githubusercontent.com/Fortniteleakjp/oo2core_9_Linux/refs/heads/main/liboo2corelinux64.so.9";
        }
        else
        {
            throw new PlatformNotSupportedException("This platform is not supported");
        }

        var envPath = Environment.GetEnvironmentVariable("OODLE_DLL_PATH");
        var found = FindExisting(fileName, envPath);
        if (found != null)
        {
            Console.WriteLine($"Found Oodle DLL: {found}");
            return found;
        }

        var dest = Path.Combine(LibsDirectory, fileName);
        Console.WriteLine($"Downloading Oodle DLL: {url}");
        DownloadFile(url, dest);
        Console.WriteLine($"Downloaded Oodle DLL: {dest}");
        return dest;
    }

    /// <summary>
    /// Downloads a file (shared logic)
    /// </summary>
    private static void DownloadFile(string url, string destinationPath)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        });

        client.Timeout = TimeSpan.FromMinutes(5);

        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var fs = File.Create(destinationPath);
        response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
    }
}
