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
