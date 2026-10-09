using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Pak;
using FortnitePorting.Models;

namespace FortnitePorting.Services;

public static class ArchiveCatalog
{
    public static IEnumerable<IAesVfsReader> All(AbstractVfsFileProvider provider)
        => provider.MountedVfs.Concat(provider.UnloadedVfs).GroupBy(reader => reader.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First());

    public static IEnumerable<IAesVfsReader> Match(IEnumerable<IAesVfsReader> readers, string? query)
        => string.IsNullOrWhiteSpace(query) ? readers : readers.Where(reader =>
            reader.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || reader.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            reader.Name.Contains("chunk" + query, StringComparison.OrdinalIgnoreCase));

    public static ArchiveInfo Metadata(IAesVfsReader archive, AbstractVfsFileProvider provider, bool enabled)
    {
        var loadedKey = provider.Keys.TryGetValue(archive.EncryptionKeyGuid, out var key) ? key.KeyString : string.Empty;

        return new ArchiveInfo
        {
            Name = archive.Name,
            Length = archive.Length,
            FileCount = archive.FileCount,
            MountPoint = archive.MountPoint,
            IsEncrypted = archive.IsEncrypted,
            IsEnabled = enabled,
            IsLooseFilesContainer = false,
            Key = loadedKey,
            Guid = archive.EncryptionKeyGuid.ToString(CUE4Parse.UE4.Objects.Core.Misc.EGuidFormats.UniqueObjectGuid),
            CompressionMethods = GetCompressionMethods(archive)
        };
    }

    private static IReadOnlyList<string> GetCompressionMethods(IAesVfsReader archive)
    {
        return archive switch
        {
            PakFileReader pak => pak.Info.CompressionMethods
                .Select(method => method.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IoStoreReader ioStore => ioStore.TocResource.CompressionMethods
                .Select(method => method.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => Array.Empty<string>()
        };
    }
}
