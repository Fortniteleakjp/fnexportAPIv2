namespace FortnitePorting.Models;

/// <summary>
/// Helper class for deserializing enc.json
/// </summary>
public class EncryptionKeyEntry
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
}
