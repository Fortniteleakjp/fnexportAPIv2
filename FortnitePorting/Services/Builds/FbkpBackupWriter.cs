using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace FortnitePorting.Services;

/// <summary>
/// Writes FModel's backup format (<c>.fbkp</c>): an LZ4 frame wrapping the magic, the backup version,
/// the entry count, and one (size, encrypted, path) record per file. Byte-for-byte the layout
/// <c>BackupManagerViewModel.CreateBackup</c> produces and <c>LoadCommand.ParseBackup</c> reads.
/// </summary>
public static class FbkpBackupWriter
{
    /// <summary>'FBKP' as a little-endian uint32, the first four bytes FModel looks for.</summary>
    public const uint Magic = 0x504B4246;

    /// <summary>
    /// EBackupVersion.PerfectPath — paths are stored verbatim, with no leading slash and no lowercasing.
    /// CUE4Parse virtual paths already have that shape, so nothing has to be rewritten.
    /// </summary>
    public const byte Version = 2;

    /// <summary>
    /// Writes the backup into <paramref name="stream"/>, wrapped in the LZ4 frame FModel produces.
    /// FModel sniffs the LZ4 magic before decoding, so <paramref name="compress"/> can be turned off
    /// and the plain body remains loadable.
    /// </summary>
    public static void Write(Stream stream, IReadOnlyList<GameFile> entries, bool compress = true,
        CancellationToken cancellationToken = default)
    {
        if (!compress)
        {
            WriteBody(stream, entries, cancellationToken);
            return;
        }

        using var lz4 = LZ4Stream.Encode(stream, LZ4Level.L00_FAST, leaveOpen: true);
        WriteBody(lz4, entries, cancellationToken);
    }

    private static void WriteBody(Stream stream, IReadOnlyList<GameFile> entries, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(entries.Count);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            writer.Write(entry.Size);
            writer.Write(entry.IsEncrypted);
            writer.Write(entry.Path);
        }
    }
}
