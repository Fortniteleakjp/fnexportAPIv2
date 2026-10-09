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
    string Reason);

/// <summary>
/// Checks the GitHub releases API at startup and, when a newer build exists, downloads it, stages it
/// next to the application, and hands over to a small script that swaps the files in once this process
/// has exited (a running executable cannot overwrite itself) and starts the new one.
/// </summary>
public static class SelfUpdateService
{
    /// <summary>Assets are named after the runtime identifier they were published for.</summary>
    private const string WindowsAsset = "FortnitePorting-win-x64.zip";
    private const string LinuxAsset = "FortnitePorting-linux-x64.tar.gz";

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
    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();

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

    /// <summary>
    /// Asks GitHub for the newest release and decides whether it applies to this installation.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var currentText = CurrentVersionDisplay;

        if (CurrentVersion is not { } current)
        {
            return new UpdateCheckResult(false, currentText, null, null, null, null,
                "development build (not stamped by the release workflow); update skipped");
        }

        if (IsContainer)
        {
            return new UpdateCheckResult(false, currentText, null, null, null, null,
                "running in a container; pull a new image instead");
        }

        using var client = CreateClient(ApiTimeout);
        var url = $"https://api.github.com/repos/{Repository}/releases/latest";
        using var response = await client.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = response.StatusCode == HttpStatusCode.Forbidden
                ? "GitHub API rate limit reached (set GITHUB_TOKEN to raise it)"
                : $"GitHub API returned {(int) response.StatusCode}";
            return new UpdateCheckResult(false, currentText, null, null, null, null, detail);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);

        if (release?.TagName is not { Length: > 0 } tag)
        {
            return new UpdateCheckResult(false, currentText, null, null, null, null, "no release published yet");
        }

        if (!TryParseVersion(tag, out var latest))
        {
            return new UpdateCheckResult(false, currentText, null, tag, null, release.HtmlUrl,
                $"latest release '{tag}' is not a comparable version");
        }

        if (latest <= current)
        {
            return new UpdateCheckResult(false, currentText, latest.ToString(), tag, null, release.HtmlUrl,
                "already up to date");
        }

        if (HasFailedAttemptFor(latest))
        {
            return new UpdateCheckResult(false, currentText, latest.ToString(), tag, null, release.HtmlUrl,
                $"v{latest} was already installed once but did not take effect; not retrying automatically");
        }

        var asset = SelectAsset(release.Assets);
        if (asset == null)
        {
            return new UpdateCheckResult(false, currentText, latest.ToString(), tag, null, release.HtmlUrl,
                $"v{latest} has no asset for this platform");
        }

        return new UpdateCheckResult(true, currentText, latest.ToString(), tag, asset, release.HtmlUrl,
            $"v{latest} is available");
    }

    /// <summary>
    /// Downloads and stages the release, then starts the apply script that performs the swap once this
    /// process exits. Returns true when the caller should shut down immediately.
    /// </summary>
    public static async Task<bool> ApplyAsync(UpdateCheckResult check, CancellationToken cancellationToken = default)
    {
        if (!check.Available || check.Asset == null || check.LatestVersion == null) return false;

        var updateRoot = Path.Combine(ApplicationDirectory, UpdateDirectoryName);
        var staging = Path.Combine(updateRoot, "staging");
        var archivePath = Path.Combine(updateRoot, check.Asset.Name);

        // Any leftovers are from an interrupted attempt and would mix two releases together.
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        Console.WriteLine($"Auto-update: downloading {check.Asset.Name} ({check.Asset.Size / 1024 / 1024} MB)");
        await DownloadAsync(check.Asset.DownloadUrl, archivePath, cancellationToken);

        Console.WriteLine("Auto-update: extracting");
        ExtractArchive(archivePath, staging);
        File.Delete(archivePath);

        // A partial or unexpected archive must never be copied over a working installation.
        var executableName = ExecutableName();
        if (!File.Exists(Path.Combine(staging, executableName)))
        {
            Directory.Delete(staging, true);
            Console.WriteLine($"Auto-update: aborted - the archive does not contain {executableName}");
            return false;
        }

        RecordAttempt(check.LatestVersion);

        var script = WriteApplyScript(updateRoot, staging, executableName);
        StartDetached(script, Environment.ProcessId);

        Console.WriteLine($"Auto-update: installing v{check.LatestVersion}; the API will "
                          + (RestartAfterUpdate ? "restart automatically." : "need to be started again."));
        return true;
    }

    /// <summary>Picks the asset published for the current platform.</summary>
    private static UpdateAssetInfo? SelectAsset(List<GitHubAsset>? assets)
    {
        if (assets == null || assets.Count == 0) return null;

        var wanted = OperatingSystem.IsWindows() ? WindowsAsset
            : OperatingSystem.IsLinux() ? LinuxAsset
            : null;
        if (wanted == null) return null;

        foreach (var asset in assets)
        {
            if (string.Equals(asset.Name, wanted, StringComparison.OrdinalIgnoreCase) &&
                asset.BrowserDownloadUrl is { Length: > 0 })
            {
                return new UpdateAssetInfo(asset.Name!, asset.BrowserDownloadUrl, asset.Size);
            }
        }
        return null;
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var client = CreateClient(DownloadTimeout);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var file = File.Create(destination);
        await response.Content.CopyToAsync(file, cancellationToken);
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
            return;
        }

        throw new NotSupportedException($"Unsupported release archive '{Path.GetFileName(archivePath)}'.");
    }

    /// <summary>
    /// Writes the script that waits for this process to exit, copies the staged files over the
    /// installation, and (optionally) starts the new build. Copying rather than mirroring is
    /// deliberate: the installation also holds files the archive does not carry — the Oodle and
    /// zlib-ng natives, mappings, caches, and local configuration — and those must survive.
    /// </summary>
    private static string WriteApplyScript(string updateRoot, string staging, string executableName)
    {
        var appDir = ApplicationDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(updateRoot, "apply.cmd");
            var relaunch = RestartAfterUpdate
                ? $"start \"\" \"{Path.Combine(appDir, executableName)}\" {string.Join(' ', arguments.Select(QuoteWindows))}"
                : "rem restart disabled (UPDATE_RESTART=false)";

            var script = $"""
                @echo off
                setlocal
                set "TARGETPID=%~1"

                :waitloop
                tasklist /fi "PID eq %TARGETPID%" 2>nul | find "%TARGETPID%" >nul
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >nul
                    goto waitloop
                )

                robocopy "{staging}" "{appDir}" /E /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >nul
                rd /s /q "{staging}" 2>nul
                {relaunch}
                endlocal
                """;

            // cmd.exe uses the system ANSI code page for a batch file without a BOM. That
            // turns non-ASCII installation paths (for example Japanese directory names) into
            // mojibake before robocopy/start can use them. UTF-16LE with a BOM is explicitly
            // recognized by cmd.exe and preserves the paths exactly.
            File.WriteAllText(scriptPath, script, Encoding.Unicode);
            return scriptPath;
        }

        var shPath = Path.Combine(updateRoot, "apply.sh");
        var target = Path.Combine(appDir, executableName);
        var relaunchSh = RestartAfterUpdate
            ? $"cd \"{appDir}\"\nexec \"{target}\" {string.Join(' ', arguments.Select(QuoteShell))}"
            : "# restart disabled (UPDATE_RESTART=false)";

        var sh = $"""
            #!/bin/sh
            TARGETPID="$1"
            while kill -0 "$TARGETPID" 2>/dev/null; do
                sleep 1
            done

            cp -a "{staging}/." "{appDir}/"
            chmod +x "{target}" 2>/dev/null
            rm -rf "{staging}"
            {relaunchSh}
            """;

        File.WriteAllText(shPath, sh);
        File.SetUnixFileMode(shPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return shPath;
    }

    /// <summary>
    /// Starts the apply script without a console window and without a handle back to this process, so
    /// it keeps running after we exit.
    /// </summary>
    private static void StartDetached(string scriptPath, int pid)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = ApplicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(scriptPath);
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add(scriptPath);
        }
        info.ArgumentList.Add(pid.ToString());

        Process.Start(info);
    }

    /// <summary>The file name the published archive uses for the application executable.</summary>
    private static string ExecutableName() => OperatingSystem.IsWindows() ? "FortnitePorting.exe" : "FortnitePorting";

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = timeout
        };

        // The GitHub API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("fnexportAPI", CurrentVersionDisplay));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // Optional: lifts the 60 requests/hour anonymous rate limit.
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static Version? ResolveCurrentVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Strip the '+<commit sha>' source-link suffix the SDK appends.
        var text = informational?.Split('+')[0];
        return TryParseVersion(text, out var version) ? version : null;
    }

    /// <summary>
    /// Parses a release version, accepting the <c>v1.1.42</c> tag form. A prerelease suffix (as used by
    /// the unstamped local default, <c>0.0.0-dev</c>) and <c>0.0.0</c> are both rejected: neither
    /// identifies a published release.
    /// </summary>
    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        if (trimmed.Contains('-')) return false;

        if (!Version.TryParse(trimmed, out var parsed)) return false;

        // Normalize so a 3-component tag and a 4-component assembly version compare sensibly.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return version > new Version(0, 0, 0);
    }

    private static void RecordAttempt(string version)
    {
        try
        {
            var path = Path.Combine(ApplicationDirectory, UpdateDirectoryName, AttemptMarkerName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, version);
        }
        catch
        {
            // The marker only guards against a retry loop; failing to write it is not fatal.
        }
    }

    private static bool HasFailedAttemptFor(Version latest)
    {
        try
        {
            var path = Path.Combine(ApplicationDirectory, UpdateDirectoryName, AttemptMarkerName);
            if (!File.Exists(path)) return false;

            // We are running an older build than the one we already staged, so the swap did not work.
            return TryParseVersion(File.ReadAllText(path), out var attempted) && attempted >= latest;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clears the retry guard so a previously failed version can be attempted again.</summary>
    public static void ClearAttemptMarker()
    {
        try
        {
            var path = Path.Combine(ApplicationDirectory, UpdateDirectoryName, AttemptMarkerName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Nothing to do: the marker is advisory.
        }
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

    private static bool ReadFlag(string name, bool defaultValue)
        => ReadOptionalFlag(name) ?? defaultValue;

    /// <summary>Reads a boolean environment variable, returning null when it is not set at all.</summary>
    private static bool? ReadOptionalFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    private static string QuoteWindows(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private static string QuoteShell(string argument) =>
        $"'{argument.Replace("'", "'\\''")}'";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
