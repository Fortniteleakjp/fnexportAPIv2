namespace FortnitePorting.Services;

public static class PageSlice
{
    public static List<T> From<T>(IReadOnlyList<T> source, int page, int pageSize)
    {
        var offset = (long)(Math.Max(1, page) - 1) * pageSize;
        if (offset >= source.Count || pageSize <= 0) return [];
        var count = Math.Min(pageSize, source.Count - (int)offset);
        var items = new List<T>(count);
        for (var i = 0; i < count; i++) items.Add(source[(int)offset + i]);
        return items;
    }
}
