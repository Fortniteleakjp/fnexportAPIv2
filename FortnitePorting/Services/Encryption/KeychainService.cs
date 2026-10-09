using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Retrieves the live GUID-to-AES mapping used by the archive key endpoint.
/// </summary>
public static class KeychainService
{
    public const string Url = "https://fljpapi.jp/api/v2/keychain?rou=false";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static async Task<IReadOnlyDictionary<string, KeychainEntry>> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(Url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var data = JsonConvert.DeserializeObject<Dictionary<string, KeychainEntry>>(json)
                   ?? throw new InvalidOperationException("The keychain API returned an empty response.");

        return new Dictionary<string, KeychainEntry>(data, StringComparer.OrdinalIgnoreCase);
    }
}

