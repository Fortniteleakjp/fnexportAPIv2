using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.Mappings;

public static class MappingStore
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetEnvironmentVariable("PROJECT_ROOT") ?? Directory.GetCurrentDirectory(), "mappings");

    public static string FileName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.'))
            throw new ArgumentException("'fileName' must be a file name without directory components.");

        return name.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? name : name + ".usmap";
    }

    public static (int Structs, int Enums) Verify(byte[] data, string name)
    {
        using var stream = new MemoryStream(data, writable: false);
        var mappings = new UsmapParser(stream, name).Mappings;
        if (mappings == null || mappings.Types.Count == 0)
            throw new InvalidDataException("The mapping contains no structs or classes.");
        return (mappings.Types.Count, mappings.Enums.Count);
    }

    public static async Task<string> SaveAsync(byte[] data, string name, CancellationToken cancellationToken)
    {
        name = FileName(name);
        Directory.CreateDirectory(DirectoryPath);
        var output = Path.Combine(DirectoryPath, name);
        var temporary = Path.Combine(DirectoryPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return output;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static IEnumerable<FileInfo> List()
        => Directory.Exists(DirectoryPath)
            ? new DirectoryInfo(DirectoryPath).GetFiles("*.usmap").OrderByDescending(f => f.LastWriteTimeUtc)
            : [];

    public static FileInfo? Find(string name)
    {
        try
        {
            if (!name.Equals(FileName(name), StringComparison.Ordinal)) return null;
            var file = new FileInfo(Path.Combine(DirectoryPath, name));
            return file.Exists ? file : null;
        }
        catch (ArgumentException) { return null; }
    }
}
