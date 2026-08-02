using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.VirtualFileSystem;
using FortnitePorting.Models;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace FortnitePorting.Controllers;

/// <summary>
/// Returns metadata for registered archives and live AES keys for pak files.
/// </summary>
[ApiController]
[Route("api/v1/archives")]
public sealed class ArchivesController : ControllerBase
{
    private readonly IFileProvider _provider;
    private readonly ManifestService _manifestService;

    public ArchivesController(IFileProvider provider, ManifestService manifestService)
    {
        _provider = provider;
        _manifestService = manifestService;
    }

    /// <summary>
    /// Returns metadata for all registered PAK/UTOC archives.
    /// </summary>
    [HttpGet]
    public IActionResult GetArchives()
    {
        if (_provider is not AbstractVfsFileProvider vfsProvider)
        {
            return StatusCode(500, new { message = "The configured file provider is not a VFS provider." });
        }

        var archives = GetArchives(vfsProvider)
            .Select(archive => ToArchiveInfo(archive, vfsProvider))
            .OrderBy(archive => archive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(archives);
    }

    /// <summary>
    /// Returns AES information in the same shape as the Fortnite AES response.
    /// </summary>
    [HttpGet("keys")]
    public async Task<IActionResult> GetPakKeys(CancellationToken cancellationToken)
    {
        if (_provider is not AbstractVfsFileProvider vfsProvider)
        {
            return StatusCode(500, new { message = "The configured file provider is not a VFS provider." });
        }

        IReadOnlyDictionary<string, KeychainEntry> keychain;
        try
        {
            keychain = await KeychainService.FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { message = "The request was cancelled." });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new
            {
                message = "Failed to retrieve the live keychain API.",
                source = KeychainService.Url,
                error = ex.Message
            });
        }

        var archives = GetArchives(vfsProvider).Where(IsKeychainArchive).ToList();
        var unloadedPaths = vfsProvider.UnloadedVfs
            .Select(archive => archive.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archiveKeys = archives
            .Select(archive => new
            {
                Archive = archive,
                Key = GetKey(vfsProvider, keychain, archive.EncryptionKeyGuid)
            })
            .ToList();

        var dynamicKeys = archiveKeys
            .Where(entry => entry.Key != null)
            .Select(entry =>
            {
                var archive = entry.Archive;
                var guid = FormatGuid(archive.EncryptionKeyGuid);
                return new ArchiveDynamicKey
                {
                    Name = archive.Name,
                    Key = entry.Key!,
                    Guid = guid,
                    Keychain = CreateKeychain(guid, entry.Key!),
                    FileCount = archive.FileCount,
                    Size = CreateSize(archive.Length)
                };
            })
            .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unloaded = archiveKeys
            .Where(entry => entry.Key == null && unloadedPaths.Contains(entry.Archive.Path))
            .Select(entry => new ArchiveUnloadedArchive
            {
                Name = entry.Archive.Name,
                Guid = FormatGuid(entry.Archive.EncryptionKeyGuid),
                Size = CreateSize(entry.Archive.Length)
            })
            .OrderBy(archive => archive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new ArchiveKeysResponse
        {
            Version = _manifestService.GameVersion,
            MainKey = GetLoadedKey(vfsProvider, new CUE4Parse.UE4.Objects.Core.Misc.FGuid(0, 0, 0, 0)) ?? string.Empty,
            DynamicKeys = dynamicKeys,
            Unloaded = unloaded
        });
    }

    private static IEnumerable<IAesVfsReader> GetArchives(AbstractVfsFileProvider provider)
    {
        return provider.MountedVfs
            .Concat(provider.UnloadedVfs)
            .GroupBy(archive => archive.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static ArchiveInfo ToArchiveInfo(IAesVfsReader archive, AbstractVfsFileProvider provider)
    {
        var loadedKey = GetLoadedKey(provider, archive.EncryptionKeyGuid) ?? string.Empty;

        return new ArchiveInfo
        {
            Name = archive.Name,
            Length = archive.Length,
            FileCount = archive.FileCount,
            MountPoint = archive.MountPoint,
            IsEncrypted = archive.IsEncrypted,
            IsEnabled = provider.MountedVfs.Any(mounted =>
                string.Equals(mounted.Path, archive.Path, StringComparison.OrdinalIgnoreCase)),
            IsLooseFilesContainer = false,
            Key = loadedKey,
            Guid = archive.EncryptionKeyGuid.ToString(CUE4Parse.UE4.Objects.Core.Misc.EGuidFormats.UniqueObjectGuid),
            CompressionMethods = GetCompressionMethods(archive)
        };
    }

    private static bool IsKeychainArchive(IAesVfsReader archive)
    {
        var isPakOrUtoc = archive.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                          || archive.Name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase);
        var isOnDemandUtoc = archive.Name.EndsWith(".o.utoc", StringComparison.OrdinalIgnoreCase);
        return isPakOrUtoc && !isOnDemandUtoc && archive.EncryptionKeyGuid.IsValid();
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

    private static string? GetLoadedKey(AbstractVfsFileProvider provider, CUE4Parse.UE4.Objects.Core.Misc.FGuid guid)
    {
        return provider.Keys.TryGetValue(guid, out var key) && key is not null ? key.KeyString : null;
    }

    private static string? GetKey(
        AbstractVfsFileProvider provider,
        IReadOnlyDictionary<string, KeychainEntry> keychain,
        CUE4Parse.UE4.Objects.Core.Misc.FGuid guid)
    {
        var guidText = FormatGuid(guid);
        if (keychain.TryGetValue(guidText, out var keychainEntry) && !string.IsNullOrWhiteSpace(keychainEntry.Aes))
        {
            return keychainEntry.Aes;
        }

        return GetLoadedKey(provider, guid);
    }

    private static string CreateKeychain(string guid, string key)
    {
        var normalized = key.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        try
        {
            return $"{guid}:{Convert.ToBase64String(Convert.FromHexString(normalized))}";
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static ArchiveSize CreateSize(long raw)
    {
        const double Kilo = 1024;
        const double Mega = Kilo * 1024;
        const double Giga = Mega * 1024;

        if (raw < (long)Kilo)
        {
            return new ArchiveSize { Raw = raw, Formatted = $"{raw} B" };
        }

        var (value, unit) = raw switch
        {
            >= (long)Giga => (raw / Giga, "GB"),
            >= (long)Mega => (raw / Mega, "MB"),
            _ => (raw / Kilo, "KB")
        };
        var formatted = $"{value.ToString("0.00", CultureInfo.InvariantCulture)} {unit}";

        return new ArchiveSize { Raw = raw, Formatted = formatted };
    }

    private static string FormatGuid(CUE4Parse.UE4.Objects.Core.Misc.FGuid guid)
    {
        return guid.ToString();
    }
}
