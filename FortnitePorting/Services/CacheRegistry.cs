namespace FortnitePorting.Services;

/// <summary>
/// Registry of the caches that hold data derived from the mounted build (response caches, decompressed
/// bytes, serialized exports, localization tables). Everything in them belongs to one build, so a
/// provider reload clears them all — otherwise a cache hit would keep serving the previous build.
/// </summary>
public static class CacheRegistry
{
    private static readonly List<(string Name, Action Clear)> Entries = new();

    /// <summary>Registers a cache-clearing action (called once at startup from Program.cs).</summary>
    public static void Register(string name, Action clear)
    {
        lock (Entries)
        {
            Entries.Add((name, clear));
        }
    }

    /// <summary>Clears every registered cache. Never throws: a failing cache must not abort a reload.</summary>
    public static void ClearAll()
    {
        (string Name, Action Clear)[] entries;
        lock (Entries)
        {
            entries = Entries.ToArray();
        }

        foreach (var (name, clear) in entries)
        {
            try
            {
                clear();
                Console.WriteLine($"  Cleared cache: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to clear cache '{name}': {ex.Message}");
            }
        }
    }
}
