using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FortnitePorting.Services;

/// <summary>
/// Static scanner for AES-256 keys that current Fortnite/UEFN builds embed as <c>mov [reg+disp], imm32</c>
/// instruction immediates (the AESDumpster / AesFinder technique) rather than as a precomputed key schedule.
/// It only reads bytes of a file already on disk — no process is launched, injected into, or read.
///
/// Unlike the external AesFinder tool — which prints a single "main key" chosen by an entropy heuristic —
/// this returns <em>every</em> candidate it finds, ordered by score. Which candidate is the real pak key is
/// then decided by <see cref="AesKeyPicker"/>, which tests them against the actual encrypted archives.
/// That matters because a build's Common DLL contains several key-shaped immediate blocks and the
/// highest-entropy one is not always the pak key.
/// </summary>
public static class AesImmediateScanner
{
    // Instruction patterns carrying the eight imm32 halves of a 32-byte key. '?' is a wildcard byte.
    private static readonly string[] Patterns =
    {
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? 48 ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? C3"
    };

    // Offset of each imm32 within its pattern. Pattern 3 stores the words in reverse order.
    private static readonly int[][] KeyOffsets =
    {
        new[] { 3, 10, 17, 24, 35, 42, 49, 56 },
        new[] { 2, 9, 16, 23, 30, 37, 44, 51 },
        new[] { 3, 10, 21, 28, 35, 42, 49, 56 },
        new[] { 51, 45, 38, 31, 24, 17, 10, 3 }
    };

    private const int KeyBytes = 32;

    /// <summary>A key-shaped immediate block found in a binary.</summary>
    public sealed class Candidate
    {
        /// <summary>The key as a 0x-prefixed, upper-case hex string (the format FAesKey accepts).</summary>
        public string Key { get; init; } = "";
        /// <summary>Index of the instruction pattern that matched.</summary>
        public int PatternIndex { get; init; }
        /// <summary>Byte offset of the match within the scanned file.</summary>
        public long Offset { get; init; }
        /// <summary>Shannon entropy of the 32 key bytes, in bits (max 5.0 for 32 samples).</summary>
        public double Entropy { get; init; }
    }

    /// <summary>
    /// Scans a buffer and returns the distinct candidates, highest entropy first. A key found by several
    /// patterns is reported once, at its first offset.
    /// </summary>
    public static List<Candidate> Find(byte[] data)
    {
        var best = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        if (data == null || data.Length < KeyBytes) return new List<Candidate>();

        for (int p = 0; p < Patterns.Length; p++)
        {
            var pattern = ParsePattern(Patterns[p]);
            var offsets = KeyOffsets[p];
            // The largest immediate offset decides how far past a match we still read.
            int reach = offsets.Max() + 4;
            int limit = data.Length - Math.Max(pattern.Length, reach);

            for (int i = 0; i <= limit; i++)
            {
                if (!Matches(pattern, data, i)) continue;

                var key = new byte[KeyBytes];
                for (int j = 0; j < offsets.Length; j++)
                {
                    Buffer.BlockCopy(data, i + offsets[j], key, j * 4, 4);
                }

                if (IsObviouslyNotAKey(key)) continue;

                var hex = "0x" + Convert.ToHexString(key);
                if (best.ContainsKey(hex)) continue;

                best[hex] = new Candidate
                {
                    Key = hex,
                    PatternIndex = p,
                    Offset = i,
                    Entropy = ShannonEntropy(key)
                };
            }
        }

        return best.Values
            .OrderByDescending(c => c.Entropy)
            .ThenBy(c => c.Offset)
            .ToList();
    }

    /// <summary>Reads a file into memory and scans it with <see cref="Find(byte[])"/>.</summary>
    public static List<Candidate> FindInFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return new List<Candidate>();
        return Find(File.ReadAllBytes(path));
    }

    private static bool Matches(int[] pattern, byte[] data, int at)
    {
        for (int j = 0; j < pattern.Length; j++)
        {
            if (pattern[j] >= 0 && data[at + j] != pattern[j]) return false;
        }
        return true;
    }

    /// <summary>Turns "C7 ? ? 48" into byte values with -1 for each wildcard.</summary>
    private static int[] ParsePattern(string pattern)
    {
        var result = new List<int>(pattern.Length / 2);
        foreach (var token in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(token == "?" ? -1 : int.Parse(token, System.Globalization.NumberStyles.HexNumber));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Cheap rejection of matches that are plainly not a key: a long zero run is zero-initialised
    /// storage, and a key made of a handful of distinct byte values is a memset/marker pattern.
    /// Deliberately lenient — the authoritative check is decrypting a real archive.
    /// </summary>
    private static bool IsObviouslyNotAKey(byte[] key)
    {
        int zeroRun = 0;
        foreach (var b in key)
        {
            if (b == 0)
            {
                if (++zeroRun >= 8) return true;
            }
            else zeroRun = 0;
        }

        var distinct = new HashSet<byte>(key);
        return distinct.Count < 8;
    }

    /// <summary>Shannon entropy of the key bytes, in bits. Higher means a more key-like byte distribution.</summary>
    private static double ShannonEntropy(byte[] key)
    {
        Span<int> counts = stackalloc int[256];
        counts.Clear();
        foreach (var b in key) counts[b]++;

        double entropy = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / key.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
