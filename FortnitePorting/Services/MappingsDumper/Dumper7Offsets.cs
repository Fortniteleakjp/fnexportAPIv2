using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Reads engine addresses out of a Dumper-7 run, when one is present on this machine.
/// </summary>
/// <remarks>
/// A first UEFN dump on a new build has no <c>FNameToString</c> address to work from: the signature
/// scan does not find it on UE6, and <see cref="OffsetStore"/> only knows addresses that have
/// already worked once. <a href="https://github.com/Encryqed/Dumper-7">Dumper-7</a> writes the
/// address it resolved into <c>Dumpspace/OffsetsInfo.json</c> beside its own output, so when its
/// results are on disk they can seed that first run.
///
/// Nothing here is trusted: the address is handed to the dumper as a candidate, and the dumper
/// accepts it only if it actually resolves object names.
/// </remarks>
public static class Dumper7Offsets
{
    /// <summary>Overrides where Dumper-7's output is looked for.</summary>
    public const string DirectoryVariable = "DUMPER7_DIR";

    private const string DefaultDirectory = @"C:\Dumper-7";

    private const string OffsetName = "OFFSET_TOSTRING";

    /// <summary>
    /// The FNameToString address Dumper-7 recorded for this build, or 0 when there is none.
    /// </summary>
    /// <param name="build">Build name as the API derives it, e.g. FortniteGame_42_10.</param>
    public static ulong FindFNameToString(string? build)
    {
        foreach (var file in FindOffsetFiles(build))
        {
            var value = ReadOffset(file);
            if (value != 0) return value;
        }

        return 0;
    }

    /// <summary>
    /// Dumper-7 names its output directories after the engine and build, e.g.
    /// <c>6.0.0-57566230+++Fortnite+Release-42.10-FortniteGame</c>. The API's build name carries the
    /// same version as underscores (<c>FortniteGame_42_10</c>), so directories are matched on that
    /// version and the newest is preferred; everything else is tried afterwards, newest first.
    /// </summary>
    private static string[] FindOffsetFiles(string? build)
    {
        var root = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(root)) root = DefaultDirectory;

        if (!Directory.Exists(root)) return [];

        try
        {
            var version = ExtractVersion(build);

            return new DirectoryInfo(root)
                .GetDirectories()
                .Select(d => new { Directory = d, Matches = version != null && d.Name.Contains(version, StringComparison.OrdinalIgnoreCase) })
                .OrderByDescending(x => x.Matches)
                .ThenByDescending(x => x.Directory.LastWriteTimeUtc)
                .Select(x => Path.Combine(x.Directory.FullName, "Dumpspace", "OffsetsInfo.json"))
                .Where(File.Exists)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Turns FortniteGame_42_10 into 42.10, which is how Dumper-7 spells it.</summary>
    private static string? ExtractVersion(string? build)
    {
        if (string.IsNullOrWhiteSpace(build)) return null;

        var underscore = build.IndexOf('_');
        if (underscore < 0 || underscore + 1 >= build.Length) return null;

        return build[(underscore + 1)..].Replace('_', '.');
    }

    /// <summary>
    /// Pulls OFFSET_TOSTRING out of the file, whose data is an array of [name, value] pairs.
    /// </summary>
    private static ulong ReadOffset(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            foreach (var pair in data.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;

                if (pair[0].ValueKind != JsonValueKind.String ||
                    !string.Equals(pair[0].GetString(), OffsetName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (pair[1].TryGetUInt64(out var value) && value != 0) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Another tool's output is optional input; an unreadable one is simply not used.
        }

        return 0;
    }
}
