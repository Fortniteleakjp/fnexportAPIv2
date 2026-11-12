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
///
/// Newer builds (seen from 43.00) no longer build the pak key from immediates at all: it is a 32-byte
/// constant in <c>.rdata</c>, handed out by a tiny function that copies it with two RIP-relative 16-byte
/// loads (<see cref="CandidateKind.ConstantReturn"/>), and copied elsewhere with a 32-byte memcpy
/// (<see cref="CandidateKind.ConstantCopy"/>). The immediate blocks that remain are other keys, so without
/// these patterns the scanner never sees the real key.
/// </summary>
public static class AesImmediateScanner
{
    /// <summary>How a candidate was found; also its rank (lower is more likely to be the pak key).</summary>
    public enum CandidateKind
    {
        /// <summary>A function that writes a 32-byte <c>.rdata</c> constant to <c>[rcx]</c> with two 16-byte loads and returns.</summary>
        ConstantReturn = 0,
        /// <summary>Eight <c>mov imm32</c> instructions (the classic layout).</summary>
        Immediate = 1,
        /// <summary>A 32-byte memcpy whose source is a <c>.rdata</c> constant. Common for non-key data, so tried last.</summary>
        ConstantCopy = 2,
    }

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
        /// <summary>Which kind of code holds the key.</summary>
        public CandidateKind Kind { get; init; } = CandidateKind.Immediate;
        /// <summary>Index of the instruction pattern that matched (within its <see cref="Kind"/>).</summary>
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

        var image = PeSections.TryParse(data);
        if (image != null)
        {
            FindConstantLoads(data, image, best);
        }

        return best.Values
            .OrderBy(c => c.Kind)
            .ThenByDescending(c => c.Entropy)
            .ThenBy(c => c.Offset)
            .ToList();
    }

    /// <summary>
    /// Finds keys stored as a 32-byte constant and read through RIP-relative addressing:
    /// <code>
    /// ConstantReturn: 0F 28|10 05 rel32   movaps/movups xmm0, [rip+key+16]
    ///                 0F 11|29 41 10      movups/movaps [rcx+10h], xmm0
    ///                 0F 28|10 05 rel32   movaps/movups xmm0, [rip+key]
    ///                 0F 11|29 01         movups/movaps [rcx], xmm0
    ///                 C3                  ret
    /// ConstantCopy:   48 8D 15 rel32      lea rdx, [rip+key]
    ///                 41 B8 20 00 00 00   mov r8d, 32
    ///                 E8                  call (memcpy)
    /// </code>
    /// A candidate already found by another pattern keeps its stronger kind.
    /// </summary>
    private static void FindConstantLoads(byte[] data, PeSections image, Dictionary<string, Candidate> best)
    {
        const int returnLength = 22;
        const int copyLength = 14;

        for (int i = 0; i + returnLength <= data.Length; i++)
        {
            if (data[i] == 0x0F && IsVectorLoad(data[i + 1]) && data[i + 2] == 0x05
                && data[i + 7] == 0x0F && IsVectorStore(data[i + 8]) && data[i + 9] == 0x41 && data[i + 10] == 0x10
                && data[i + 11] == 0x0F && IsVectorLoad(data[i + 12]) && data[i + 13] == 0x05
                && data[i + 18] == 0x0F && IsVectorStore(data[i + 19]) && data[i + 20] == 0x01
                && data[i + 21] == 0xC3)
            {
                var high = image.RipTarget(i + 7, BitConverter.ToInt32(data, i + 3));
                var low = image.RipTarget(i + 18, BitConverter.ToInt32(data, i + 14));
                if (high != null && low != null && high == low + 16)
                {
                    Add(data, image, low.Value, CandidateKind.ConstantReturn, 0, i, best);
                }
            }
            else if (data[i] == 0x48 && data[i + 1] == 0x8D && data[i + 2] == 0x15
                     && data[i + 7] == 0x41 && data[i + 8] == 0xB8 && data[i + 9] == 0x20
                     && data[i + 10] == 0x00 && data[i + 11] == 0x00 && data[i + 12] == 0x00
                     && data[i + 13] == 0xE8 && i + copyLength <= data.Length)
            {
                var source = image.RipTarget(i + 7, BitConverter.ToInt32(data, i + 3));
                if (source != null)
                {
                    Add(data, image, source.Value, CandidateKind.ConstantCopy, 0, i, best);
                }
            }
        }
    }

    private static bool IsVectorLoad(byte opcode) => opcode is 0x28 or 0x10;
    private static bool IsVectorStore(byte opcode) => opcode is 0x11 or 0x29;

    private static void Add(byte[] data, PeSections image, long rva, CandidateKind kind, int pattern, long offset,
        Dictionary<string, Candidate> best)
    {
        var fileOffset = image.RvaToOffset(rva, KeyBytes);
        if (fileOffset == null) return;

        var key = new byte[KeyBytes];
        Buffer.BlockCopy(data, (int)fileOffset.Value, key, 0, KeyBytes);
        if (IsObviouslyNotAKey(key)) return;

        var hex = "0x" + Convert.ToHexString(key);
        if (best.TryGetValue(hex, out var existing) && existing.Kind <= kind) return;

        best[hex] = new Candidate
        {
            Key = hex,
            Kind = kind,
            PatternIndex = pattern,
            Offset = offset,
            Entropy = ShannonEntropy(key)
        };
    }

    /// <summary>Just enough of the PE section table to turn RIP-relative operands into file offsets.</summary>
    private sealed class PeSections
    {
        private readonly (long Rva, long VirtualSize, long RawOffset, long RawSize)[] _sections;

        private PeSections((long, long, long, long)[] sections) => _sections = sections;

        public static PeSections? TryParse(byte[] data)
        {
            try
            {
                if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z') return null;
                int pe = BitConverter.ToInt32(data, 0x3C);
                if (pe <= 0 || pe + 24 > data.Length || BitConverter.ToUInt32(data, pe) != 0x00004550) return null;

                int count = BitConverter.ToUInt16(data, pe + 6);
                int optionalSize = BitConverter.ToUInt16(data, pe + 20);
                int table = pe + 24 + optionalSize;
                var sections = new (long, long, long, long)[count];
                for (int s = 0; s < count; s++)
                {
                    int o = table + s * 40;
                    if (o + 40 > data.Length) return null;
                    sections[s] = (
                        BitConverter.ToUInt32(data, o + 12),
                        BitConverter.ToUInt32(data, o + 8),
                        BitConverter.ToUInt32(data, o + 20),
                        BitConverter.ToUInt32(data, o + 16));
                }
                return new PeSections(sections);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>RVA addressed by a rel32 whose instruction ends at file offset <paramref name="nextInstruction"/>.</summary>
        public long? RipTarget(long nextInstruction, int rel32)
        {
            foreach (var (rva, _, raw, rawSize) in _sections)
            {
                if (nextInstruction >= raw && nextInstruction < raw + rawSize)
                {
                    return rva + (nextInstruction - raw) + rel32;
                }
            }
            return null;
        }

        /// <summary>File offset of <paramref name="length"/> bytes at <paramref name="rva"/>, when all of them are on disk.</summary>
        public long? RvaToOffset(long rva, int length)
        {
            foreach (var (start, virtualSize, raw, rawSize) in _sections)
            {
                var size = Math.Min(virtualSize, rawSize);
                if (rva >= start && rva + length <= start + size)
                {
                    return raw + (rva - start);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Merges the external AesFinder tool's answer with this scanner's candidates in the order they should
    /// be tried: the scanner's <see cref="CandidateKind.ConstantReturn"/> hits first (the tool does not look
    /// for them, and its "main key" is then a different key-shaped block), then the tool's keys, then the
    /// remaining scanner candidates. Duplicates are dropped.
    /// </summary>
    public static List<string> OrderCandidates(string? toolMainKey, IEnumerable<string>? toolOtherKeys, IEnumerable<Candidate> scanned)
    {
        var scannedList = scanned.ToList();
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key.Trim())) ordered.Add(key.Trim());
        }

        foreach (var c in scannedList.Where(c => c.Kind == CandidateKind.ConstantReturn)) Add(c.Key);
        Add(toolMainKey);
        foreach (var k in toolOtherKeys ?? Enumerable.Empty<string>()) Add(k);
        foreach (var c in scannedList) Add(c.Key);
        return ordered;
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
