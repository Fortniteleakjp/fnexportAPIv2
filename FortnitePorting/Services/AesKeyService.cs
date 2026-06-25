using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Fetches Fortnite AES keys, preferring api.fortniteapi.com and falling back to uedb.dev
/// only when the primary source is unavailable.
///
/// Both responses deserialize into <see cref="FortniteApiAesResponse"/> (Newtonsoft matches
/// JSON property names case-insensitively):
///   - api.fortniteapi.com : { version, mainKey, dynamicKeys: [{ name, guid, key, ... }] }
///   - uedb.dev            : { version, mainKey, dynamicKeys: [{ guid, key }] }   (no per-key name)
/// </summary>
public static class AesKeyService
{
    public const string PrimaryUrl = "https://api.fortniteapi.com/v1/aes";
    public const string FallbackUrl = "https://uedb.dev/svc/api/v1/fortnite/aes";

    public const string PrimarySource = "api.fortniteapi.com";
    public const string FallbackSource = "uedb.dev";

    /// <summary>
    /// Fetches AES keys from the primary source, falling back to the secondary source on failure.
    /// Returns the parsed data and the name of the source that succeeded (empty when both failed).
    /// </summary>
    public static async Task<(FortniteApiAesResponse? Data, string Source)> FetchAsync(
        HttpClient client, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var sources = new[]
        {
            (Url: PrimaryUrl, Name: PrimarySource),
            (Url: FallbackUrl, Name: FallbackSource),
        };

        foreach (var (url, name) in sources)
        {
            try
            {
                var response = await client.GetStringAsync(url, cancellationToken);
                var data = JsonConvert.DeserializeObject<FortniteApiAesResponse>(response);
                if (!string.IsNullOrEmpty(data?.MainKey))
                {
                    return (data, name);
                }

                log?.Invoke($"AES source '{name}' returned no usable key.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"AES source '{name}' failed: {ex.Message}");
            }
        }

        return (null, string.Empty);
    }
}
