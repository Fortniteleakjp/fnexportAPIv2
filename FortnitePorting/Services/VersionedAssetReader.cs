using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.VirtualFileSystem;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Reads one virtual path out of any mounted build — live or archived — as raw bytes, as text, or as
/// the JSON export of its package. Kept separate from <c>ExportController</c> because that controller
/// is bound to the live provider; here the provider is always the one for the requested build.
/// </summary>
public static class VersionedAssetReader
{
    /// <summary>What a read produced. Exactly one of the payload fields is set.</summary>
    public sealed record ReadResult(
        bool Found,
        string ResolvedPath,
        string Kind,
        byte[]? Bytes,
        string? Text,
        object? Json,
        long Size,
        string? Archive,
        string? Error);

    /// <summary>
    /// Resolves a requested path against a provider, accepting the exact virtual path, the same path
    /// without an extension, and the object path form used by the export endpoints.
    /// </summary>
    public static string? ResolvePath(IFileProvider provider, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');

        if (provider.Files.ContainsKey(normalized))
        {
            return normalized;
        }

        foreach (var extension in new[] { ".uasset", ".umap", ".uexp", ".ubulk" })
        {
            if (provider.Files.ContainsKey(normalized + extension))
            {
                return normalized + extension;
            }
        }

        // "/Game/Foo/Bar" or "/Game/Foo/Bar.Bar" — drop the object name and let CUE4Parse's own
        // mount-point mapping resolve the rest.
        var withoutObject = normalized.Contains('.') ? normalized[..normalized.LastIndexOf('.')] : normalized;
        foreach (var extension in new[] { ".uasset", ".umap" })
        {
            if (provider.Files.ContainsKey(withoutObject + extension))
            {
                return withoutObject + extension;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a path out of one build. <paramref name="raw"/> forces the stored bytes; otherwise a
    /// package is returned as its JSON export and anything else as text or bytes.
    /// </summary>
    public static ReadResult Read(IFileProvider provider, string path, bool raw)
    {
        var resolved = ResolvePath(provider, path);
        if (resolved == null)
        {
            return new ReadResult(false, path, "missing", null, null, null, 0, null, null);
        }

        var file = provider.Files[resolved];
        var archive = (file as VfsEntry)?.Vfs.Name;

        byte[]? bytes;
        try
        {
            if (!provider.TrySaveAsset(resolved, out bytes) || bytes == null)
            {
                return new ReadResult(true, resolved, "unreadable", null, null, null, file.Size, archive,
                    "The file exists in this build but could not be read; its archive may still be locked.");
            }
        }
        catch (Exception ex)
        {
            return new ReadResult(true, resolved, "unreadable", null, null, null, file.Size, archive, ex.Message);
        }

        if (raw)
        {
            return new ReadResult(true, resolved, "binary", bytes, null, null, bytes.LongLength, archive, null);
        }

        var isPackage = resolved.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                        resolved.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

        if (isPackage)
        {
            try
            {
                var package = provider.LoadPackage(file);
                var exports = package.GetExports().ToList();
                return new ReadResult(true, resolved, "package", null, null, exports, bytes.LongLength, archive, null);
            }
            catch (Exception ex)
            {
                // Falling back to bytes is more useful than a 500: the caller can still hash or
                // download it, and the reason the package would not deserialize is reported.
                return new ReadResult(true, resolved, "binary", bytes, null, null, bytes.LongLength, archive,
                    $"The package could not be deserialized ({ex.Message}); returning the raw bytes instead.");
            }
        }

        var side = BuildDiffService.ReadSide(provider, resolved);
        return side.Text != null
            ? new ReadResult(true, resolved, "text", null, side.Text, null, bytes.LongLength, archive, null)
            : new ReadResult(true, resolved, "binary", bytes, null, null, bytes.LongLength, archive, null);
    }

    /// <summary>
    /// Renders a path as the text a line diff runs on: real text as-is, a package as its indented JSON
    /// export. Returns null when neither is possible.
    /// </summary>
    public static string? ReadAsDiffableText(IFileProvider provider, string path, out string kind, out string? error)
    {
        kind = "missing";
        error = null;

        var result = Read(provider, path, raw: false);
        if (!result.Found)
        {
            return null;
        }

        kind = result.Kind;
        error = result.Error;

        switch (result.Kind)
        {
            case "text":
                return result.Text;

            case "package":
                try
                {
                    return JsonConvert.SerializeObject(result.Json, Formatting.Indented,
                        new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                }
                catch (Exception ex)
                {
                    error = $"The package was loaded but could not be serialized: {ex.Message}";
                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>Best-effort content type for a raw download.</summary>
    public static string ContentTypeFor(string path) => path[(path.LastIndexOf('.') + 1)..].ToLowerInvariant() switch
    {
        "ini" or "txt" or "cfg" or "log" => "text/plain; charset=utf-8",
        "json" => "application/json; charset=utf-8",
        "csv" => "text/csv; charset=utf-8",
        "xml" => "application/xml; charset=utf-8",
        "png" => "image/png",
        _ => "application/octet-stream"
    };
}
