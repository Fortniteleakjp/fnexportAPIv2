using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FortnitePorting.Services;

/// <summary>
/// Static, signature-less AES-256 key finder for Unreal Engine binaries (an AesFinder/AESDumpster-style
/// scanner re-implemented in C#).
///
/// It does NOT run, inject into, or read the memory of any process — it only scans the bytes of a file
/// that is already on disk/in memory. The technique exploits the fact that UE precomputes and stores the
/// expanded AES-256 key schedule (15 round keys = 240 bytes) inside the executable when the pak key is a
/// compile-time constant. Every 240-byte window is tested against the FIPS-197 AES-256 key-expansion
/// relations; a window that satisfies all 52 word relations is, with overwhelming probability, a real key
/// schedule, and its first 32 bytes are the AES-256 key.
/// </summary>
public static class AesFinder
{
    // AES forward S-box (FIPS-197).
    private static readonly byte[] Sbox =
    {
        0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
        0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
        0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
        0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
        0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
        0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
        0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
        0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
        0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
        0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
        0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
        0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
        0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
        0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
        0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
        0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
    };

    // Round constants for the key-schedule core. AES-256 only ever needs Rcon[1..7].
    private static readonly byte[] Rcon = { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40 };

    private const int KeyBytes = 32;          // AES-256 key
    private const int Words = 60;             // 4 * (Nr + 1) = 4 * 15
    private const int ScheduleBytes = 240;    // Words * 4

    /// <summary>
    /// Verifies that the 240-byte window <paramref name="w"/> is a valid AES-256 key schedule
    /// (i.e. it equals the FIPS-197 expansion of its own first 32 bytes). Bails on the first
    /// mismatching word, so a random window is rejected almost immediately.
    /// </summary>
    private static bool IsKeySchedule(ReadOnlySpan<byte> w)
    {
        // t = the transformed previous word, reused per iteration.
        Span<byte> t = stackalloc byte[4];
        for (int i = 8; i < Words; i++)
        {
            int pm1 = (i - 1) * 4;
            byte t0 = w[pm1], t1 = w[pm1 + 1], t2 = w[pm1 + 2], t3 = w[pm1 + 3];

            int m = i & 7;
            if (m == 0)
            {
                // RotWord -> SubWord -> XOR Rcon
                byte r0 = Sbox[t1];
                byte r1 = Sbox[t2];
                byte r2 = Sbox[t3];
                byte r3 = Sbox[t0];
                t[0] = (byte)(r0 ^ Rcon[i >> 3]);
                t[1] = r1; t[2] = r2; t[3] = r3;
            }
            else if (m == 4)
            {
                // SubWord only (AES-256 extra application)
                t[0] = Sbox[t0]; t[1] = Sbox[t1]; t[2] = Sbox[t2]; t[3] = Sbox[t3];
            }
            else
            {
                t[0] = t0; t[1] = t1; t[2] = t2; t[3] = t3;
            }

            int p8 = (i - 8) * 4, pi = i * 4;
            if ((byte)(w[p8] ^ t[0]) != w[pi]) return false;
            if ((byte)(w[p8 + 1] ^ t[1]) != w[pi + 1]) return false;
            if ((byte)(w[p8 + 2] ^ t[2]) != w[pi + 2]) return false;
            if ((byte)(w[p8 + 3] ^ t[3]) != w[pi + 3]) return false;
        }
        return true;
    }

    /// <summary>
    /// Standard FIPS-197 AES-256 key expansion. Produces the 240-byte schedule for a 32-byte key.
    /// Used by <see cref="SelfTest"/> to synthesize a known-good schedule.
    /// </summary>
    public static byte[] ExpandKey256(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes) throw new ArgumentException("AES-256 key must be 32 bytes", nameof(key));
        var w = new byte[ScheduleBytes];
        key.CopyTo(w);
        Span<byte> t = stackalloc byte[4];
        for (int i = 8; i < Words; i++)
        {
            int pm1 = (i - 1) * 4;
            byte t0 = w[pm1], t1 = w[pm1 + 1], t2 = w[pm1 + 2], t3 = w[pm1 + 3];
            int m = i & 7;
            if (m == 0)
            {
                t[0] = (byte)(Sbox[t1] ^ Rcon[i >> 3]);
                t[1] = Sbox[t2]; t[2] = Sbox[t3]; t[3] = Sbox[t0];
            }
            else if (m == 4)
            {
                t[0] = Sbox[t0]; t[1] = Sbox[t1]; t[2] = Sbox[t2]; t[3] = Sbox[t3];
            }
            else
            {
                t[0] = t0; t[1] = t1; t[2] = t2; t[3] = t3;
            }
            int p8 = (i - 8) * 4, pi = i * 4;
            w[pi] = (byte)(w[p8] ^ t[0]);
            w[pi + 1] = (byte)(w[p8 + 1] ^ t[1]);
            w[pi + 2] = (byte)(w[p8 + 2] ^ t[2]);
            w[pi + 3] = (byte)(w[p8 + 3] ^ t[3]);
        }
        return w;
    }

    /// <summary>
    /// Scans an in-memory buffer for AES-256 keys and returns each unique key as a 0x-prefixed,
    /// upper-case hex string (the format accepted by CUE4Parse's FAesKey).
    /// </summary>
    public static List<string> FindKeys(byte[] data)
    {
        if (data == null || data.Length < ScheduleBytes) return new List<string>();

        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        long lastOffset = data.Length - ScheduleBytes; // inclusive
        long count = lastOffset + 1;
        int partitions = Math.Max(1, Environment.ProcessorCount);

        Parallel.For(0, partitions, p =>
        {
            int start = (int)(count * p / partitions);
            int end = (int)(count * (p + 1) / partitions); // exclusive
            var span = data.AsSpan();
            for (int o = start; o < end; o++)
            {
                if (IsKeySchedule(span.Slice(o, ScheduleBytes)))
                {
                    var key = "0x" + Convert.ToHexString(data, o, KeyBytes);
                    found.TryAdd(key, 0);
                }
            }
        });

        return found.Keys.ToList();
    }

    /// <summary>
    /// Scans a file on disk for AES-256 keys without loading the whole file into memory at once
    /// (reads overlapping blocks so a schedule that straddles a block boundary is not missed).
    /// Suitable for very large binaries; <see cref="FindKeys(byte[])"/> is faster for files that fit
    /// comfortably in memory.
    /// </summary>
    public static List<string> FindKeysInFile(string path, int blockSize = 64 * 1024 * 1024)
    {
        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < ScheduleBytes) return new List<string>();

        // Whole-file fast path when it fits in a single array.
        if (fi.Length <= 1_500_000_000L)
        {
            return FindKeys(File.ReadAllBytes(path));
        }

        int overlap = ScheduleBytes - 1;
        var buffer = new byte[blockSize + overlap];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

        long fileOffset = 0;
        int carried = 0; // bytes kept from the previous block (the overlap tail)
        while (true)
        {
            int read = ReadFull(fs, buffer, carried, blockSize);
            int available = carried + read;
            if (available < ScheduleBytes) break;

            int scanEnd = available - ScheduleBytes; // inclusive last start within this window
            int partitions = Math.Max(1, Environment.ProcessorCount);
            int local = scanEnd + 1;
            Parallel.For(0, partitions, p =>
            {
                int s = (int)((long)local * p / partitions);
                int e = (int)((long)local * (p + 1) / partitions);
                var span = buffer.AsSpan();
                for (int o = s; o < e; o++)
                {
                    if (IsKeySchedule(span.Slice(o, ScheduleBytes)))
                    {
                        var key = "0x" + Convert.ToHexString(buffer, o, KeyBytes);
                        found.TryAdd(key, 0);
                    }
                }
            });

            if (read == 0) break;

            // Carry the trailing overlap bytes to the front of the next window.
            Array.Copy(buffer, available - overlap, buffer, 0, overlap);
            carried = overlap;
            fileOffset += read;
        }

        return found.Keys.ToList();
    }

    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Sanity check used at startup/diagnostics: embeds the expansion of a known key in a buffer of
    /// non-matching bytes and confirms the scanner recovers exactly that key.
    /// </summary>
    public static bool SelfTest()
    {
        var key = new byte[KeyBytes];
        for (int i = 0; i < KeyBytes; i++) key[i] = (byte)(i * 7 + 3);
        var schedule = ExpandKey256(key);

        var buf = new byte[4096];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i * 13 + 1);
        Array.Copy(schedule, 0, buf, 1000, schedule.Length);

        var keys = FindKeys(buf);
        var expected = "0x" + Convert.ToHexString(key);
        return keys.Contains(expected);
    }
}
