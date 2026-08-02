namespace FortnitePorting.Models;

/// <summary>
/// Value returned by the fljpapi keychain endpoint.
/// </summary>
public sealed class KeychainEntry
{
    public string Aes { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
}

