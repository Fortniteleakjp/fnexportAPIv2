using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;

namespace FortnitePorting.Services;

public static class ArchiveFileIndex
{
    private sealed class Snapshot
    {
        public string[] Paths = [];
        public int FileCount = -1;
    }

    private static readonly ConditionalWeakTable<object, Snapshot> Snapshots = new();

    public static IReadOnlyList<string> For(IReadOnlyDictionary<string, GameFile> files)
    {
        var snapshot = Snapshots.GetOrCreateValue(files);
        lock (snapshot)
        {
            if (snapshot.FileCount != files.Count)
            {
                snapshot.Paths = files.Keys.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                snapshot.FileCount = files.Count;
            }
            return snapshot.Paths;
        }
    }

    public static IReadOnlyList<string> For(IEnumerable<IAesVfsReader> readers)
    {
        var archives = readers.ToArray();
        if (archives.Length == 1) return For(archives[0].Files);
        return archives.SelectMany(reader => For(reader.Files)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Clear() => Snapshots.Clear();
}
