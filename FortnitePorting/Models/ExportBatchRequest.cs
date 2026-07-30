namespace FortnitePorting.Models;

/// <summary>
/// A batch of package paths to export as JSON.
/// </summary>
public sealed class ExportBatchRequest
{
    /// <summary>Asset paths. A maximum of 100 paths is accepted per request.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Localization language code, for example ja. Defaults to en.</summary>
    public string Lang { get; set; } = "en";
}
