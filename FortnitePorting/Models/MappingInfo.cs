namespace FortnitePorting.Models;

/// <summary>
/// Metadata for a mapping file
/// </summary>
public class MappingInfo
{
    public string hash { get; set; } = "";
    public string fileName { get; set; } = "";
    public long size { get; set; }
    public string url { get; set; } = "";
    public string? jsonUrl { get; set; }
    public string uploadedAt { get; set; } = "";
}
