using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Remembers the engine addresses a UEFN dump needed, so finding them is a one-off per build.
/// </summary>
/// <remarks>
/// <see cref="UefnDumperInjector"/> can locate GObjects by walking memory for a structure that
/// behaves like the object array, but there is no equivalent for <c>FNameToString</c>: it is a
/// function, and the only way to know the right one is to call it and see whether names come back.
/// The dumper does exactly that and rejects a wrong candidate, which means a build whose signature
/// scan finds nothing cannot dump at all until an address is supplied once.
///
/// The address is fixed for the life of a build, so the one that worked is kept here and offered
/// back on later runs. A stale entry is harmless: the dumper validates it like any other candidate
/// and falls back to scanning when it does not resolve names.
/// </remarks>
public static class OffsetStore
{
    /// <summary>One build's addresses, all module-relative.</summary>
    public sealed class Entry
    {
        /// <summary>Module the addresses are relative to, e.g. the engine DLL of a modular build.</summary>
        public string? Module { get; set; }

        public ulong GObjectsRva { get; set; }

        public ulong FNameToStringRva { get; set; }

        /// <summary>When this was recorded, so a stale file is readable rather than mysterious.</summary>
        public DateTime SavedUtc { get; set; }
    }

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>The file the addresses live in, beside the dumper's other working files.</summary>
    public static string FilePath
        => Path.Combine(MappingsDumperService.MappingsDirectory, "dumper", "offsets.json");

    /// <summary>Addresses recorded for a build, or null when there are none.</summary>
    public static Entry? Load(string? build)
    {
        if (string.IsNullOrWhiteSpace(build)) return null;

        lock (Gate)
        {
            return ReadAll().TryGetValue(build, out var entry) ? entry : null;
        }
    }

    /// <summary>Records the addresses that worked for a build.</summary>
    public static void Save(string? build, string? module, ulong gObjectsRva, ulong fNameToStringRva)
    {
        if (string.IsNullOrWhiteSpace(build)) return;
        if (fNameToStringRva == 0 && gObjectsRva == 0) return;

        lock (Gate)
        {
            var all = ReadAll();

            all.TryGetValue(build, out var existing);

            var entry = new Entry
            {
                // Each field falls back to what is already recorded, so a run that only learned one
                // of them does not erase the other.
                Module = string.IsNullOrWhiteSpace(module) ? existing?.Module : module,
                GObjectsRva = gObjectsRva != 0 ? gObjectsRva : existing?.GObjectsRva ?? 0,
                FNameToStringRva = fNameToStringRva != 0 ? fNameToStringRva : existing?.FNameToStringRva ?? 0,
                SavedUtc = DateTime.UtcNow
            };

            if (existing != null &&
                existing.Module == entry.Module &&
                existing.GObjectsRva == entry.GObjectsRva &&
                existing.FNameToStringRva == entry.FNameToStringRva)
            {
                return;
            }

            all[build] = entry;

            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(all, Format));
            }
            catch (IOException)
            {
                // Losing the note only costs the next run a rediscovery; it must not fail the dump.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private static Dictionary<string, Entry> ReadAll()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new Dictionary<string, Entry>();

            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path))
                   ?? new Dictionary<string, Entry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file is treated as empty rather than blocking the dump.
            return new Dictionary<string, Entry>();
        }
    }
}
