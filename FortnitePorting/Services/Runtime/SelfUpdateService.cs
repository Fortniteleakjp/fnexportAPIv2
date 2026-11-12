using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortnitePorting.Services;

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record UpdateAssetInfo(string Name, string DownloadUrl, long Size);

/// <summary>
/// The result of asking GitHub what the newest release is. <see cref="Available"/> is only true when a
/// newer version exists <em>and</em> it ships an asset for this platform; <see cref="Reason"/> explains
/// every other outcome (already current, no matching asset, check disabled, request failed, ...).
/// </summary>
public sealed record UpdateCheckResult(
    bool Available,
    string CurrentVersion,
    string? LatestVersion,
    string? Tag,
    UpdateAssetInfo? Asset,
    string? ReleaseUrl,
    string Reason,
    UpdateAssetInfo? AesFinderAsset = null);

/// <summary>
/// Checks the GitHub releases API at startup and, when a newer build exists, downloads it, stages it
/// next to the application, and hands over to a small script that swaps the files in once this process
/// has exited (a running executable cannot overwrite itself) and starts the new one.
/// </summary>
public static partial class SelfUpdateService
{
    /// <summary>Assets are named after the runtime identifier they were published for.</summary>
    private const string WindowsAsset = "FortnitePorting-win-x64.zip";
    private const string LinuxAsset = "FortnitePorting-linux-x64.tar.gz";

    /// <summary>
    /// The AesFinder tool ships as its own release assets. It is replaced on every update so an old
    /// build (one that does not understand <c>--json</c>, say) never outlives the API that drives it.
    /// </summary>
    private const string WindowsAesFinderAsset = "AesFinder.exe";
    private const string LinuxAesFinderAsset = "AesFinder-linux-x64.tar.gz";

    /// <summary>Staging lives inside the application directory so the swap is a same-volume move.</summary>
    private const string UpdateDirectoryName = ".update";

    /// <summary>
    /// Records the version we last handed to the apply script. If we come back up still older than
    /// that, the swap did not take effect and retrying would spin forever, so the attempt is skipped.
    /// </summary>
    private const string AttemptMarkerName = "last-attempt.txt";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// AUTO_UPDATE: true always updates, false never contacts GitHub, and leaving it unset asks at
    /// startup. Null therefore means "not configured", which is what makes the prompt appear.
    /// </summary>
    public static bool? AutoUpdatePreference => ReadOptionalFlag("AUTO_UPDATE");

    /// <summary>Whether updating is permitted at all — false only when AUTO_UPDATE says so.</summary>
    public static bool Enabled => AutoUpdatePreference != false;

    /// <summary>
    /// How long the startup prompt waits for an answer before assuming yes. A terminal that nobody
    /// is watching must not hold the API down indefinitely.
    /// </summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the declined-update notice stays on screen before startup continues.</summary>
    private static readonly TimeSpan DeclinedNoticeDelay = TimeSpan.FromSeconds(5);

    /// <summary>UPDATE_CHECK_ONLY: report a newer release but never install it.</summary>
    public static bool CheckOnly => ReadFlag("UPDATE_CHECK_ONLY", false);

    /// <summary>UPDATE_RESTART: relaunch after the files are swapped (false leaves that to the operator).</summary>
    public static bool RestartAfterUpdate => ReadFlag("UPDATE_RESTART", true);

    /// <summary>UPDATE_REPO: the <c>owner/name</c> the releases are read from.</summary>
    public static string Repository =>
        Environment.GetEnvironmentVariable("UPDATE_REPO")?.Trim() is { Length: > 0 } repo
            ? repo
            : "Fortniteleakjp/fnexportAPIv2";

    /// <summary>
    /// True for a build that was not stamped by the release workflow (a local <c>dotnet run</c> or
    /// <c>dotnet build</c>). Such a build has no meaningful version to compare, and replacing a
    /// developer's working copy with a release archive would be destructive, so it is never updated.
    /// </summary>
    public static bool IsDevelopmentBuild => CurrentVersion is null;

    /// <summary>
    /// Running inside a container the published archive is the wrong shape entirely: the image runs a
    /// framework-dependent <c>dotnet FortnitePorting.dll</c>, the release assets are self-contained
    /// builds, and anything written to the container layer is lost on the next <c>docker run</c>.
    /// Updating an image means pulling a new one.
    /// </summary>
    public static bool IsContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The stamped release version, or null for an unstamped local build.</summary>
    public static Version? CurrentVersion { get; } = ResolveCurrentVersion();

    /// <summary>The version string as reported to clients, including the development placeholder.</summary>
    public static string CurrentVersionDisplay => CurrentVersion?.ToString() ?? "0.0.0-dev";

    /// <summary>
    /// The directory holding the deployed application. For a single-file publish
    /// <see cref="AppContext.BaseDirectory"/> is the bundle extraction directory rather than where the
    /// executable lives, so the real process path wins whenever it is the app's own executable.
    /// </summary>
    public static string ApplicationDirectory => AppPaths.ApplicationDirectory;

    /// <summary>
    /// Runs the startup update flow: checks GitHub and, if a newer build is available, installs it and
    /// terminates this process so the apply script can take over. Returns true when the process is
    /// about to be replaced, in which case the caller must not continue starting up.
    /// Never throws — a failed update check must not stop the API from starting.
    /// </summary>
    public static bool RunStartupUpdate()
    {
        if (AutoUpdatePreference == false)
        {
            Console.WriteLine("Auto-update: disabled (AUTO_UPDATE=false)\n");
            return false;
        }

        try
        {
            var result = CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Auto-update: current {result.CurrentVersion}, {result.Reason}");

            if (!result.Available)
            {
                Console.WriteLine();
                return false;
            }

            if (CheckOnly)
            {
                Console.WriteLine($"Auto-update: v{result.LatestVersion} is available at {result.ReleaseUrl}");
                Console.WriteLine("Auto-update: not installing it (UPDATE_CHECK_ONLY=true)\n");
                return false;
            }

            // Only now is there a decision worth making, so this is where the operator gets asked.
            if (!ConfirmUpdate(result))
            {
                return false;
            }

            var applied = ApplyAsync(result).GetAwaiter().GetResult();
            Console.WriteLine();
            return applied;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Auto-update: skipped ({ex.Message})\n");
            return false;
        }
    }

    /// <summary>
    /// Asks at the console whether to install the release that was found. AUTO_UPDATE answers for
    /// the operator when it is set either way, and a redirected stdin (a service, a container, a
    /// pipe) means there is nobody to ask, so the update proceeds as before. Declining prints how
    /// to make the answer permanent, holds it on screen briefly, and lets startup continue.
    /// </summary>
    private static bool ConfirmUpdate(UpdateCheckResult result)
    {
        if (AutoUpdatePreference == true) return true;

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Auto-update: no interactive console; installing automatically "
                              + "(set AUTO_UPDATE=false to disable)");
            return true;
        }

        Console.Write($"Update to v{result.LatestVersion} now? [Y/n] "
                      + $"(Y after {PromptTimeout.TotalSeconds:0}s): ");

        var answer = ReadLineWithTimeout(PromptTimeout);
        Console.WriteLine();

        var declined = answer != null &&
                       answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
        if (!declined) return true;

        PrintHowToChangeTheSetting(result);
        CountDown(DeclinedNoticeDelay);
        return false;
    }

    /// <summary>
    /// Explains how to stop being asked, in both directions, naming the environment variable so the
    /// answer survives the next start.
    /// </summary>
    private static void PrintHowToChangeTheSetting(UpdateCheckResult result)
    {
        Console.WriteLine($"Auto-update: staying on v{result.CurrentVersion} for this start.");
        Console.WriteLine();
        Console.WriteLine("  To stop being asked, set the AUTO_UPDATE environment variable:");
        Console.WriteLine("    AUTO_UPDATE=false   never ask, never update");
        Console.WriteLine("    AUTO_UPDATE=true    never ask, always update");
        Console.WriteLine("    (unset)             ask again on every start");
        Console.WriteLine();
        Console.WriteLine("  Windows : setx AUTO_UPDATE false      (this window only: set AUTO_UPDATE=false)");
        Console.WriteLine("  Linux   : export AUTO_UPDATE=false    (or add it to the service unit)");
        Console.WriteLine();
        Console.WriteLine($"  Release notes: {result.ReleaseUrl}");
        Console.WriteLine($"  To update later without restarting: POST http://localhost:{PortForHelp()}/api/v1/update");
        Console.WriteLine();
    }

    /// <summary>The port the help text points at, matching how Program.cs resolves it.</summary>
    private static string PortForHelp() =>
        Environment.GetEnvironmentVariable("PORT")?.Trim() is { Length: > 0 } port ? port : "3849";

    /// <summary>
    /// Reads one line, giving up after <paramref name="timeout"/>. The read runs on its own
    /// background thread so an unanswered prompt cannot keep the process alive or block the pool.
    /// </summary>
    private static string? ReadLineWithTimeout(TimeSpan timeout)
    {
        string? line = null;
        var done = new ManualResetEventSlim(false);

        var reader = new Thread(() =>
        {
            try
            {
                line = Console.ReadLine();
            }
            catch
            {
                // No usable console: treated the same as no answer.
            }
            finally
            {
                done.Set();
            }
        }) { IsBackground = true };

        reader.Start();
        return done.Wait(timeout) ? line : null;
    }

    /// <summary>Counts the notice down on one line so the delay reads as deliberate, not as a hang.</summary>
    private static void CountDown(TimeSpan duration)
    {
        for (var remaining = (int) duration.TotalSeconds; remaining > 0; remaining--)
        {
            Console.Write($"  Starting in {remaining}... " + CarriageReturn);
            Thread.Sleep(1000);
        }
        Console.WriteLine("  Starting...            ");
        Console.WriteLine();
    }

    private const string CarriageReturn = "\r";
}
