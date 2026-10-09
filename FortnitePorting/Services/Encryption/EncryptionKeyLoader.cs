using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using FortnitePorting.Models;
using Newtonsoft.Json;

namespace FortnitePorting.Services;

/// <summary>
/// Service that loads encryption keys.
/// </summary>
public static class EncryptionKeyLoader
{
    private static readonly HttpClient _httpClient = new();

    private static string GetAesFilePath(string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir))
        {
            rootDir = Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        }
        return Path.Combine(rootDir, "aes.json");
    }

    /// <summary>
    /// Retrieves encryption keys from FortniteAPI.com and registers them with the provider.
    /// </summary>
    public static async Task LoadEncryptionKeysAsync(DefaultFileProvider provider, string rootDir)
    {
        Console.WriteLine($"Retrieving AES keys (primary: {AesKeyService.PrimarySource}, fallback: {AesKeyService.FallbackSource})...");

        FortniteApiAesResponse? aesData = null;
        bool isLocalFallback = false;
        const int MaxRetries = 3;

        for (int i = 0; i <= MaxRetries; i++)
        {
            // Try api.fortniteapi.com first, then fall back to uedb.dev.
            var (data, source) = await AesKeyService.FetchAsync(_httpClient, msg => Console.WriteLine($"✗ {msg}"));
            if (data?.MainKey != null)
            {
                aesData = data;
                Console.WriteLine($"✓ Retrieved AES keys from {source}");
                break;
            }

            if (i < MaxRetries)
            {
                Console.WriteLine($"Both AES sources failed (attempt {i + 1}/{MaxRetries + 1}). Retrying in 30 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            else
            {
                Console.WriteLine("Attempting to load keys from the local backup...");
            }
        }

        // Load from local storage if the API request failed or the data is invalid
        if (aesData?.MainKey is null)
        {
            try
            {
                var aesFilePath = GetAesFilePath(rootDir);
                if (File.Exists(aesFilePath))
                {
                    var json = await File.ReadAllTextAsync(aesFilePath);
                    aesData = JsonConvert.DeserializeObject<FortniteApiAesResponse>(json);
                    isLocalFallback = true;
                    Console.WriteLine($"✓ Loaded keys from local '{Path.GetFileName(aesFilePath)}'.");
                }
                else
                {
                    Console.WriteLine($"✗ Local backup file not found: {aesFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to read the local file: {ex.Message}");
            }
        }

        if (aesData?.MainKey is null)
        {
            Console.WriteLine("✗ Error: Could not obtain a valid AES key.");
            throw new Exception("Could not obtain an AES key (from the API or locally)");
        }
            
        Console.WriteLine($"AES key version: {aesData.Version}");
            
        int loadedKeyCount = 0;
            
        // Register the main key (registered with a zero GUID)
        if (!string.IsNullOrEmpty(aesData.MainKey))
        {
            try
            {
                var mainGuid = new FGuid(0, 0, 0, 0);
                var mainAesKey = new FAesKey(aesData.MainKey);
                provider.SubmitKey(mainGuid, mainAesKey);
                loadedKeyCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error registering the main key: {ex.Message}");
            }
        }
        
        // Register the dynamic keys
        foreach (var dynamicKey in aesData.DynamicKeys)
        {
            try
            {
                if (string.IsNullOrEmpty(dynamicKey.Key) || string.IsNullOrEmpty(dynamicKey.Guid))
                    continue;
                
                FGuid guid;
                
                // Parse the GUID
                if (dynamicKey.Guid == "00000000-00000000-00000000-00000000" ||
                    dynamicKey.Guid.Replace("-", "").Replace("0", "") == "")
                {
                    guid = new FGuid(0, 0, 0, 0);
                }
                else
                {
                    var hexStr = dynamicKey.Guid.Replace("-", "");
                    
                    if (hexStr.Length == 32)
                    {
                        var a = Convert.ToUInt32(hexStr.Substring(0, 8), 16);
                        var b = Convert.ToUInt32(hexStr.Substring(8, 8), 16);
                        var c = Convert.ToUInt32(hexStr.Substring(16, 8), 16);
                        var d = Convert.ToUInt32(hexStr.Substring(24, 8), 16);
                        guid = new FGuid(a, b, c, d);
                    }
                    else
                    {
                        guid = new FGuid(dynamicKey.Guid);
                    }
                }
                
                var aesKey = new FAesKey(dynamicKey.Key);
                provider.SubmitKey(guid, aesKey);
                loadedKeyCount++;
            }
            catch (Exception keyEx)
            {
                Console.WriteLine($"Key registration error ({dynamicKey.Name}): {keyEx.Message}");
            }
        }
        
        Console.WriteLine($"✓ Registered encryption keys with the provider: {loadedKeyCount} key(s)");

        // Save only when the keys were retrieved from the API
        if (!isLocalFallback)
        {
            // Save the retrieved key information to a file
            try
            {
                var aesFilePath = GetAesFilePath(rootDir);
                var json = JsonConvert.SerializeObject(aesData, Formatting.Indented);
                await File.WriteAllTextAsync(aesFilePath, json);
                Console.WriteLine($"✓ Saved the latest AES key information to '{Path.GetFileName(aesFilePath)}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to save aes.json: {ex.Message}");
            }
        }
    }
}
