namespace FortnitePorting.Models;

/// <summary>
/// Information about an archive registered with the VFS provider.
/// </summary>
public sealed class ArchiveInfo
{
    public string Name { get; init; } = string.Empty;
    public long Length { get; init; }
    public int FileCount { get; init; }
    public string? MountPoint { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsLooseFilesContainer { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Guid { get; init; } = string.Empty;
    public IReadOnlyList<string> CompressionMethods { get; init; } = Array.Empty<string>();
}

/// <summary>
/// AES information response compatible with the Fortnite AES response format.
/// </summary>
public sealed class ArchiveKeysResponse
{
    public string Version { get; init; } = string.Empty;
    public string MainKey { get; init; } = string.Empty;
    public IReadOnlyList<ArchiveDynamicKey> DynamicKeys { get; init; } = Array.Empty<ArchiveDynamicKey>();
    public IReadOnlyList<ArchiveUnloadedArchive> Unloaded { get; init; } = Array.Empty<ArchiveUnloadedArchive>();
}

public sealed class ArchiveDynamicKey
{
    public string Name { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Guid { get; init; } = string.Empty;
    public string Keychain { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public ArchiveSize Size { get; init; } = new();
}

public sealed class ArchiveUnloadedArchive
{
    public string Name { get; init; } = string.Empty;
    public string Guid { get; init; } = string.Empty;
    public ArchiveSize Size { get; init; } = new();
}

public sealed class ArchiveSize
{
    public long Raw { get; init; }
    public string Formatted { get; init; } = string.Empty;
}
