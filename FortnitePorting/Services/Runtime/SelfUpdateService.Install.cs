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

public static partial class SelfUpdateService
{
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
            $"v{latest} is available", SelectAesFinderAsset(release.Assets));
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

        await StageAesFinderAsync(check.AesFinderAsset, updateRoot, staging, cancellationToken);

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

    /// <summary>Picks the AesFinder asset published for the current platform, if the release has one.</summary>
    private static UpdateAssetInfo? SelectAesFinderAsset(List<GitHubAsset>? assets)
    {
        var wanted = OperatingSystem.IsWindows() ? WindowsAesFinderAsset
            : OperatingSystem.IsLinux() ? LinuxAesFinderAsset
            : null;
        if (wanted == null || assets == null) return null;

        var asset = assets.FirstOrDefault(a =>
            string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase) && a.BrowserDownloadUrl is { Length: > 0 });
        return asset == null ? null : new UpdateAssetInfo(asset.Name!, asset.BrowserDownloadUrl!, asset.Size);
    }

    /// <summary>
    /// Puts the release's AesFinder into the staging directory, where the apply script finds it and
    /// replaces the installed copy. Not fatal: the API has a built-in scanner that covers the same
    /// patterns, so a missing or failed AesFinder download must not block the update itself. In that
    /// case nothing is staged and the installed AesFinder is left alone.
    /// </summary>
    private static async Task StageAesFinderAsync(UpdateAssetInfo? asset, string updateRoot, string staging,
        CancellationToken cancellationToken)
    {
        if (asset == null)
        {
            Console.WriteLine("Auto-update: this release has no AesFinder asset; keeping the installed one");
            return;
        }

        var downloadPath = Path.Combine(updateRoot, asset.Name);
        try
        {
            Console.WriteLine($"Auto-update: downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)");
            await DownloadAsync(asset.DownloadUrl, downloadPath, cancellationToken);

            if (asset.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractArchive(downloadPath, staging);
                File.Delete(downloadPath);
            }
            else
            {
                File.Move(downloadPath, Path.Combine(staging, asset.Name), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Auto-update: AesFinder could not be downloaded ({ex.Message}); keeping the installed one");
            try { File.Delete(downloadPath); } catch { /* ignore */ }
            try { File.Delete(Path.Combine(staging, AesFinderExecutableName())); } catch { /* ignore */ }
        }
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
        var aesFinder = AesFinderExecutableName();
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

                if exist "{Path.Combine(staging, aesFinder)}" del /f /q "{Path.Combine(appDir, aesFinder)}" 2>nul
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

            if [ -f "{staging}/{aesFinder}" ]; then
                rm -f "{appDir}/{aesFinder}"
            fi
            cp -a "{staging}/." "{appDir}/"
            chmod +x "{target}" 2>/dev/null
            chmod +x "{appDir}/{aesFinder}" 2>/dev/null
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

    /// <summary>The file name of the AesFinder executable inside its release asset.</summary>
    public static string AesFinderExecutableName() => OperatingSystem.IsWindows() ? "AesFinder.exe" : "AesFinder";

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
}
