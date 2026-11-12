namespace FortnitePorting.Services;

/// <summary>
/// The one place that decides where the API keeps its files (<c>libs/</c>, <c>mappings/</c>,
/// <c>chunk_cache/</c>, <c>build_history/</c>, <c>hotfix_cache/</c>, <c>aes.json</c> …).
/// </summary>
/// <remarks>
/// Every caller used to work this out on its own — some from the current directory, some from four
/// levels above the build output — so the same run could write <c>mappings/</c> to two places
/// depending on where it was started from. The order here is:
/// <list type="number">
/// <item><c>PROJECT_ROOT</c> when it is set (Docker uses <c>/app</c>).</item>
/// <item>The repository checkout the executable was built in, found by walking up to the folder
/// that holds <c>FortnitePorting/FortnitePorting.csproj</c>. This is what <c>dotnet run</c>,
/// <c>start.bat</c> and <c>build.bat</c> output use, wherever they are launched from.</item>
/// <item>The folder older versions computed (four levels above the base directory), but only when
/// it already holds data from them, so an existing cache and build history keep being used.</item>
/// <item>Otherwise the folder of the executable itself (a downloaded release).</item>
/// </list>
/// </remarks>
public static class AppPaths
{
    private const string ProjectMarker = "FortnitePorting/FortnitePorting.csproj";

    /// <summary>Folder the running executable lives in (not the single-file extraction folder).</summary>
    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();

    /// <summary>Root folder that all runtime data is kept under.</summary>
    /// <remarks><c>PROJECT_ROOT</c> is read on every access, as it was before this class existed, so a
    /// value set after startup (tests do this) still applies; only the auto-detected fallback is cached.</remarks>
    public static string ProjectRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PROJECT_ROOT")?.Trim();
            return string.IsNullOrEmpty(configured) ? DetectedRoot.Value : Path.GetFullPath(configured);
        }
    }

    private static readonly Lazy<string> DetectedRoot = new(DetectProjectRoot);

    private static string DetectProjectRoot()
    {
        foreach (var start in new[] { ApplicationDirectory, AppContext.BaseDirectory })
        {
            var checkout = FindCheckout(start);
            if (checkout != null) return checkout;
        }

        var legacy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (Directory.Exists(Path.Combine(legacy, "build_history")) || Directory.Exists(Path.Combine(legacy, "chunk_cache")))
        {
            return legacy;
        }

        return ApplicationDirectory;
    }

    private static string? FindCheckout(string? start)
    {
        if (string.IsNullOrEmpty(start)) return null;
        try
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, ProjectMarker)))
                {
                    return dir.FullName;
                }
            }
        }
        catch (Exception)
        {
            // An unreadable parent folder just means "not a checkout".
        }

        return null;
    }

    private static string ResolveApplicationDirectory()
    {
        // Environment.ProcessPath is the app's own executable for an apphost or single-file launch, but
        // the shared host ("dotnet FortnitePorting.dll") for a framework-dependent run.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(fileName, "FortnitePorting", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
}
