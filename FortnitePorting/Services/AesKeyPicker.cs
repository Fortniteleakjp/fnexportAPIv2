using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;

namespace FortnitePorting.Services;

/// <summary>
/// Decides which of several extracted AES key candidates is the real one by testing them against the
/// encrypted archives themselves, instead of trusting a heuristic ranking.
///
/// A build's binaries contain more than one key-shaped block, so the highest-entropy candidate — what the
/// AesFinder tool reports as "the" main key when it cannot cross-check against a published key — is not
/// necessarily the pak key. <c>IAesVfsReader.TestAesKey</c> decrypts the archive's mount-point check bytes,
/// which only succeeds for the correct key, so it settles the question with no external dependency and
/// without mounting anything.
/// </summary>
public static class AesKeyPicker
{
    /// <summary>The outcome of choosing a key from a candidate list.</summary>
    public sealed class Result
    {
        /// <summary>The key to use, or null when every candidate was rejected by a real archive.</summary>
        public string? Key { get; init; }
        /// <summary>True when <see cref="Key"/> was proven correct against an encrypted archive.</summary>
        public bool Validated { get; init; }
        /// <summary>Number of candidates actually tested.</summary>
        public int Tested { get; init; }
        /// <summary>Candidates that an archive rejected (in the order they were tried).</summary>
        public IReadOnlyList<string> Rejected { get; init; } = Array.Empty<string>();
        /// <summary>Human-readable explanation of how the key was chosen.</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// Tests <paramref name="keyHex"/> against every encrypted, still-unmounted archive that needs
    /// <paramref name="guid"/>. Returns true as soon as one of them decrypts.
    /// </summary>
    public static bool Validate(AbstractVfsFileProvider provider, FGuid guid, string keyHex)
    {
        FAesKey key;
        try { key = new FAesKey(keyHex); }
        catch { return false; }

        foreach (var reader in Encrypted(provider.UnloadedVfs, guid).Concat(Encrypted(provider.MountedVfs, guid)))
        {
            try
            {
                if (reader.TestAesKey(key)) return true;
            }
            catch
            {
                // A reader that cannot produce check bytes tells us nothing; try the next one.
            }
        }

        return false;
    }

    /// <summary>
    /// Picks the first candidate that an encrypted archive accepts. Candidates are tried in the order
    /// given, so callers should pass their most likely candidate first.
    ///
    /// When no archive is available to test against (nothing encrypted is still waiting for this GUID),
    /// the first candidate is returned unvalidated rather than nothing — that is the old behaviour, and it
    /// is the best available answer in that situation.
    /// </summary>
    public static Result Pick(AbstractVfsFileProvider provider, FGuid guid, IEnumerable<string> candidates)
    {
        var ordered = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            return new Result { Reason = "No key candidates were extracted." };
        }

        // Archives still waiting for this key are the authoritative judges: if one of them exists and no
        // candidate opens it, every candidate really is wrong.
        var waiting = Encrypted(provider.UnloadedVfs, guid);
        if (waiting.Count > 0)
        {
            var (key, reader, rejected) = FirstAccepted(ordered, waiting);
            if (key != null)
            {
                return new Result
                {
                    Key = key,
                    Validated = true,
                    Tested = rejected.Count + 1,
                    Rejected = rejected,
                    Reason = $"Verified by decrypting '{reader!.Name}'."
                };
            }

            return new Result
            {
                Key = null,
                Validated = false,
                Tested = rejected.Count,
                Rejected = rejected,
                Reason = $"None of the {rejected.Count} candidate(s) could decrypt any of the {waiting.Count} " +
                         "encrypted archive(s) waiting for this key."
            };
        }

        // Nothing is waiting (the key is already applied, which is the normal state). Already-mounted
        // archives can still confirm a candidate, but they cannot refute one: some readers drop the bytes
        // TestAesKey needs once they are mounted. So a match here is proof, and no match is only "unknown".
        var mounted = Encrypted(provider.MountedVfs, guid);
        if (mounted.Count > 0)
        {
            var (key, reader, _) = FirstAccepted(ordered, mounted);
            if (key != null)
            {
                return new Result
                {
                    Key = key,
                    Validated = true,
                    Tested = ordered.Count,
                    Reason = $"Verified against the already-mounted '{reader!.Name}'."
                };
            }
        }

        return new Result
        {
            Key = ordered[0],
            Validated = false,
            Tested = 0,
            Reason = "No encrypted archive is waiting for this key, so the candidate could not be verified; " +
                     "using the highest-ranked one."
        };
    }

    /// <summary>
    /// Returns the first candidate that any of <paramref name="readers"/> accepts, together with the reader
    /// that accepted it, plus the candidates tried and rejected before it.
    /// </summary>
    private static (string? Key, IAesVfsReader? Reader, List<string> Rejected) FirstAccepted(
        List<string> candidates, List<IAesVfsReader> readers)
    {
        var rejected = new List<string>();
        foreach (var candidate in candidates)
        {
            FAesKey key;
            try { key = new FAesKey(candidate); }
            catch
            {
                rejected.Add(candidate);
                continue;
            }

            foreach (var reader in readers)
            {
                bool ok;
                try { ok = reader.TestAesKey(key); }
                catch { continue; }
                if (ok) return (candidate, reader, rejected);
            }

            rejected.Add(candidate);
        }

        return (null, null, rejected);
    }

    /// <summary>
    /// The archives that can answer "is this the right key?": actually encrypted and keyed on this GUID.
    /// Unencrypted readers are excluded because TestAesKey trivially accepts any key for them, which would
    /// "validate" a wrong key.
    /// </summary>
    private static List<IAesVfsReader> Encrypted(IEnumerable<IAesVfsReader> readers, FGuid guid)
        => readers.Where(r => r.EncryptionKeyGuid == guid && r.IsEncrypted).ToList();
}
