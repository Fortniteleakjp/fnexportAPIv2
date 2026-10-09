using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services.Local;

/// <summary>
/// Works out the AES keys a local installation's containers are encrypted with, one key per GUID the
/// containers ask for.
///
/// The installation answers the question itself: its binaries carry the key as compiled-in immediates,
/// and its own containers say whether a candidate is right — <c>TestAesKey</c> decrypts the archive's
/// mount-point check bytes, which only the real key does. So a local build can be opened with no
/// network access at all, which is the point of pointing at one: an install that is newer (or older)
/// than whatever the live key APIs publish still opens.
/// </summary>
public static class LocalKeyResolver
{
    private static readonly FGuid ZeroGuid = new(0, 0, 0, 0);

    /// <summary>Above this size a binary is skipped by the immediate scanner, which reads it whole.</summary>
    private const long MaxScanBytes = 2_000_000_000L;

    public sealed class Options
    {
        /// <summary>Installation root, used to find the binaries to scan.</summary>
        public string? Root;

        /// <summary>Keys supplied by the caller, as <c>hex</c> or <c>guid:hex</c>. Tried first.</summary>
        public IReadOnlyList<string> RequestedKeys = [];

        /// <summary>Scan the installation's own binaries for compiled-in keys (default true).</summary>
        public bool ScanBinaries = true;

        /// <summary>Also run the key-schedule scanner, which is slower but finds keys stored expanded.</summary>
        public bool DeepScan;

        /// <summary>How many binaries to scan, highest-ranked first.</summary>
        public int BinaryLimit = 8;

        /// <summary>Only scan binaries whose file name contains this.</summary>
        public string? BinaryFilter;

        /// <summary>Fall back to the live key APIs for GUIDs the install itself did not answer.</summary>
        public bool UseLiveApi = true;
    }

    /// <summary>What was decided for one encryption GUID.</summary>
    public sealed class KeyResult
    {
        public required string Guid { get; init; }
        public string? Key { get; init; }
        public bool Verified { get; init; }
        public required string Source { get; init; }
        public required string Reason { get; init; }
        public int ArchiveCount { get; init; }
        public IReadOnlyList<string> Archives { get; init; } = [];
        public bool IsMain { get; init; }
    }

    /// <summary>One binary the scan looked at.</summary>
    public sealed class ScannedBinary
    {
        public required string File { get; init; }
        public required string Path { get; init; }
        public long SizeBytes { get; init; }
        public int Candidates { get; init; }
        public double ScanSeconds { get; init; }
        public string? Error { get; init; }
    }

    public sealed class Result
    {
        public List<KeyResult> Keys { get; } = [];
        public List<ScannedBinary> Binaries { get; } = [];

        /// <summary>How many candidates the most contested GUID was decided between.</summary>
        public int CandidateCount { get; set; }

        public int RequiredGuids { get; set; }

        /// <summary>True when a binary produced the main key and the remaining ones were skipped.</summary>
        public bool ScanStoppedEarly { get; set; }

        public string? ApiSource { get; set; }
        public string? ApiError { get; set; }

        /// <summary>The verified keys, ready to be submitted to a provider.</summary>
        public Dictionary<FGuid, FAesKey> Submittable { get; } = [];

        /// <summary>The main (zero-GUID) key, which is what "the AES key" of a build means.</summary>
        public KeyResult? Main => Keys.FirstOrDefault(k => k.IsMain);
    }

    /// <summary>
    /// Decides a key for every GUID the provider's registered containers are waiting on. The provider
    /// must already have its containers registered (so their headers are readable) and nothing mounted
    /// yet — an unmounted encrypted reader is the only one that can refute a wrong key.
    /// </summary>
    public static async Task<Result> ResolveAsync(
        AbstractVfsFileProvider provider, Options options, HttpClient http,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= _ => { };
        var result = new Result();

        // Containers name the GUID they need in their header, so the set of keys to find is known
        // before a single one is tried.
        var required = provider.UnloadedVfs
            .Where(reader => reader.IsEncrypted)
            .GroupBy(reader => reader.EncryptionKeyGuid)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Name).OrderBy(n => n).ToList());

        result.RequiredGuids = required.Count;

        var (guidScoped, loose) = ParseRequestedKeys(options.RequestedKeys);

        // Scanning is pointless when nothing is encrypted, and expensive enough to be worth skipping.
        var scanned = new List<string>();
        if (options.ScanBinaries && required.Count > 0 && !string.IsNullOrWhiteSpace(options.Root))
        {
            // Once a binary has yielded the main key there is nothing left for the others to find, and
            // each one costs tens of seconds — a shipping executable is several hundred megabytes and
            // regularly carries no key at all. Only the main key can end the scan early: a dynamic key
            // is not compiled into the build, so its absence says nothing about the remaining binaries.
            Func<IReadOnlyList<string>, bool>? enough = required.ContainsKey(ZeroGuid)
                ? found => found.Any(candidate => AesKeyPicker.Validate(provider, ZeroGuid, candidate))
                : null;

            scanned = ScanBinaries(options, result, enough, log, cancellationToken);
        }

        FortniteApiAesResponse? live = null;
        if (options.UseLiveApi && required.Count > 0)
        {
            try
            {
                var (data, source) = await AesKeyService.FetchAsync(http, msg => log(msg), cancellationToken);
                live = data;
                result.ApiSource = data == null ? null : source;
                if (data == null) result.ApiError = "The live AES APIs did not answer.";
            }
            catch (Exception ex)
            {
                result.ApiError = ex.Message;
            }
        }

        var liveByGuid = BuildLiveKeyMap(live);

        foreach (var (guid, archives) in required.OrderByDescending(kv => kv.Key == ZeroGuid).ThenBy(kv => kv.Key.ToString()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ordered by how much we trust the source: what the caller passed for this exact GUID,
            // then what the install's own binaries hold, then what the APIs publish. Every one of them
            // still has to decrypt an archive before it is reported as the key.
            var candidates = new List<(string Key, string Source)>();

            if (guidScoped.TryGetValue(guid, out var forGuid))
            {
                candidates.AddRange(forGuid.Select(k => (k, "request")));
            }

            candidates.AddRange(loose.Select(k => (k, "request")));
            candidates.AddRange(scanned.Select(k => (k, "binary scan")));

            if (liveByGuid.TryGetValue(guid, out var apiKey))
            {
                candidates.Add((apiKey, "live AES API"));
            }

            var ordered = candidates
                .GroupBy(c => Normalize(c.Key), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            result.CandidateCount = Math.Max(result.CandidateCount, ordered.Count);

            var pick = AesKeyPicker.Pick(provider, guid, ordered.Select(c => c.Key));
            var source = pick.Key == null
                ? "none"
                : ordered.First(c => Normalize(c.Key).Equals(Normalize(pick.Key), StringComparison.OrdinalIgnoreCase)).Source;

            result.Keys.Add(new KeyResult
            {
                Guid = guid.ToString(),
                Key = pick.Key,
                Verified = pick.Validated,
                Source = source,
                Reason = pick.Reason,
                ArchiveCount = archives.Count,
                Archives = archives.Take(10).ToList(),
                IsMain = guid == ZeroGuid
            });

            if (pick.Key != null)
            {
                try { result.Submittable[guid] = new FAesKey(pick.Key); }
                catch (Exception ex) { log($"Key for {guid} could not be parsed: {ex.Message}"); }
            }
        }

        return result;
    }

    /// <summary>Serializes resolved keys in the shape the rest of this API reads AES keys in.</summary>
    public static FortniteApiAesResponse ToAesJson(Result result, string? version)
    {
        var response = new FortniteApiAesResponse
        {
            Version = version ?? string.Empty,
            MainKey = result.Main?.Key ?? string.Empty
        };

        foreach (var key in result.Keys.Where(k => !k.IsMain && k.Key != null))
        {
            response.DynamicKeys.Add(new DynamicKey
            {
                Name = key.Archives.FirstOrDefault() ?? key.Guid,
                Guid = key.Guid,
                Key = key.Key!,
                FileCount = key.ArchiveCount
            });
        }

        return response;
    }

    /// <summary>Writes resolved keys next to the project's own aes.json, without overwriting it.</summary>
    public static string Save(Result result, string rootDir, string? version, string? fileName = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "aes.local.json" : Path.GetFileName(fileName);
        var path = Path.Combine(rootDir, name);
        File.WriteAllText(path, JsonConvert.SerializeObject(ToAesJson(result, version), Formatting.Indented));
        return path;
    }

    /// <summary>
    /// Scans the installation's binaries and returns every key-shaped block found, most promising
    /// binary first. Which of them is a real key is settled by the containers, not here.
    /// </summary>
    private static List<string> ScanBinaries(Options options, Result result,
        Func<IReadOnlyList<string>, bool>? enough, Action<string> log, CancellationToken cancellationToken)
    {
        var report = result.Binaries;
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var binaries = LocalInstallLocator.FindBinaries(options.Root!, options.BinaryLimit);
        if (!string.IsNullOrWhiteSpace(options.BinaryFilter))
        {
            binaries = binaries
                .Where(f => f.Name.Contains(options.BinaryFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var binary in binaries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (binary.Length > MaxScanBytes)
            {
                report.Add(new ScannedBinary
                {
                    File = binary.Name,
                    Path = binary.FullName,
                    SizeBytes = binary.Length,
                    Error = $"Skipped: larger than the {MaxScanBytes / 1_000_000_000d:F1} GB scan limit."
                });
                continue;
            }

            var watch = Stopwatch.StartNew();
            var before = candidates.Count;
            string? error = null;

            try
            {
                foreach (var candidate in AesImmediateScanner.FindInFile(binary.FullName))
                {
                    if (seen.Add(Normalize(candidate.Key))) candidates.Add(candidate.Key);
                }

                if (options.DeepScan)
                {
                    foreach (var key in AesFinder.FindKeysInFile(binary.FullName))
                    {
                        if (seen.Add(Normalize(key))) candidates.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                log($"[LocalAes] Scanning {binary.Name} failed: {ex.Message}");
            }

            watch.Stop();
            report.Add(new ScannedBinary
            {
                File = binary.Name,
                Path = binary.FullName,
                SizeBytes = binary.Length,
                Candidates = candidates.Count - before,
                ScanSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
                Error = error
            });

            var fresh = candidates.GetRange(before, candidates.Count - before);
            if (fresh.Count > 0 && enough?.Invoke(fresh) == true)
            {
                result.ScanStoppedEarly = true;
                log($"[LocalAes] {binary.Name} yielded the main key; skipping the remaining binaries.");
                break;
            }
        }

        return candidates;
    }

    /// <summary>Splits caller-supplied keys into the GUID-scoped ones and the ones to try everywhere.</summary>
    private static (Dictionary<FGuid, List<string>> Scoped, List<string> Loose) ParseRequestedKeys(IReadOnlyList<string> keys)
    {
        var scoped = new Dictionary<FGuid, List<string>>();
        var loose = new List<string>();

        foreach (var entry in keys ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            var text = entry.Trim();
            var separator = text.LastIndexOf(':');
            if (separator > 0 && TryParseGuid(text[..separator], out var guid))
            {
                if (!scoped.TryGetValue(guid, out var list)) scoped[guid] = list = [];
                list.Add(text[(separator + 1)..].Trim());
                continue;
            }

            loose.Add(text);
        }

        return (scoped, loose);
    }

    /// <summary>The live APIs' keys, indexed by the GUID the containers would ask for.</summary>
    private static Dictionary<FGuid, string> BuildLiveKeyMap(FortniteApiAesResponse? live)
    {
        var map = new Dictionary<FGuid, string>();
        if (live == null) return map;

        if (!string.IsNullOrWhiteSpace(live.MainKey)) map[ZeroGuid] = live.MainKey;

        foreach (var dynamic in live.DynamicKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(dynamic.Key) || string.IsNullOrWhiteSpace(dynamic.Guid)) continue;
            if (TryParseGuid(dynamic.Guid, out var guid)) map[guid] = dynamic.Key;
        }

        return map;
    }

    /// <summary>
    /// Parses the GUID spellings this API is given: Epic's 32 hex digits, optionally dash-grouped.
    /// </summary>
    public static bool TryParseGuid(string? value, out FGuid guid)
    {
        guid = ZeroGuid;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var hex = value.Trim().Replace("-", string.Empty);
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return false;

        try
        {
            guid = new FGuid(
                Convert.ToUInt32(hex[..8], 16),
                Convert.ToUInt32(hex[8..16], 16),
                Convert.ToUInt32(hex[16..24], 16),
                Convert.ToUInt32(hex[24..], 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var text = key.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return text.ToUpperInvariant();
    }
}
