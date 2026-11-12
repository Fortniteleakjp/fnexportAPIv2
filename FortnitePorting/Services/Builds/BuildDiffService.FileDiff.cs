using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

public sealed partial class BuildDiffService
{
    // ------------------------------------------------------------ per-file line diff

    /// <summary>Extensions whose bytes are text and can be diffed line by line as they are.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ini", "txt", "json", "csv", "xml", "cfg", "log", "md", "html", "htm", "js", "css", "verse", "digest"
    };

    /// <summary>What a single file's content looked like in one build.</summary>
    public sealed record FileSide(bool Exists, string? Text, byte[]? Bytes, long Size, string? Archive, string? Error);

    /// <summary>Reads one file out of a build as text when it can be, and as bytes otherwise.</summary>
    public static FileSide ReadSide(IFileProvider provider, string path)
    {
        // Through the index: a changelist reads this for file after file, and both the provider's own
        // lookup and its path-based read sort the whole archive list on every call.
        if (!FileIndex.For(provider).TryGetFile(path, out var file))
        {
            return new FileSide(false, null, null, 0, null, null);
        }

        var archive = (file as VfsEntry)?.Vfs.Name;
        try
        {
            if (!provider.TrySaveAsset(file, out var bytes) || bytes == null)
            {
                return new FileSide(true, null, null, file.Size, archive, "The file could not be read (its archive may still be locked).");
            }

            var text = DecodeText(path, bytes);

            return new FileSide(true, text, bytes, bytes.LongLength, archive, null);
        }
        catch (Exception ex)
        {
            return new FileSide(true, null, null, file.Size, archive, ex.Message);
        }
    }

    /// <summary>
    /// Treats the content as text when the first few KB decode as UTF-8/UTF-16 with no NUL bytes.
    /// Assets are binary and fail this, which is why the endpoints diff their JSON export instead.
    /// </summary>
    internal static string? DecodeText(string path, byte[] bytes)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.');
        return TextExtensions.Contains(extension) || LooksLikeText(bytes) ? DecodeText(bytes) : null;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        // A UTF-16 BOM is text even though half its bytes are NUL.
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        var sample = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sample; i++)
        {
            if (bytes[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Describes where two binary blobs start and stop differing, when a line diff is meaningless.</summary>
    public static object DescribeBinaryDifference(byte[] oldBytes, byte[] newBytes, int maxRanges = 50)
    {
        var common = Math.Min(oldBytes.Length, newBytes.Length);
        var ranges = new List<object>();
        var differingBytes = 0L;

        var index = 0;
        while (index < common)
        {
            if (oldBytes[index] == newBytes[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < common && oldBytes[index] != newBytes[index])
            {
                index++;
            }

            differingBytes += index - start;
            if (ranges.Count < maxRanges)
            {
                ranges.Add(new { offset = start, length = index - start });
            }
        }

        if (oldBytes.Length != newBytes.Length)
        {
            differingBytes += Math.Abs((long)oldBytes.Length - newBytes.Length);
        }

        return new
        {
            oldBytes = oldBytes.Length,
            newBytes = newBytes.Length,
            oldSha256 = Convert.ToHexString(SHA256.HashData(oldBytes)),
            newSha256 = Convert.ToHexString(SHA256.HashData(newBytes)),
            differingBytes,
            truncatedRanges = ranges.Count >= maxRanges,
            changedRanges = ranges
        };
    }
}
