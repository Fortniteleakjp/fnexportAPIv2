namespace FortnitePorting.Models;

/// <summary>
/// AES response from FortniteAPI.com
/// </summary>
public class FortniteApiAesResponse
{
    public string Version { get; set; } = string.Empty;
    public string MainKey { get; set; } = string.Empty;
    public List<DynamicKey> DynamicKeys { get; set; } = new();
}

public class DynamicKey
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string? Keychain { get; set; }
    public int? FileCount { get; set; }
    public KeySize? Size { get; set; }
}

public class KeySize
{
    public long Raw { get; set; }
    public string Formatted { get; set; } = string.Empty;
}
