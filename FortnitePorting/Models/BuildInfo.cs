using Newtonsoft.Json;

namespace FortnitePorting.Models;

public class BuildApiResponse
{
    [JsonProperty("elements")]
    public List<BuildInfo> Elements { get; set; } = new();
}

public class BuildInfo
{
    [JsonProperty("appName")]
    public string AppName { get; set; } = string.Empty;

    [JsonProperty("labelName")]
    public string LabelName { get; set; } = string.Empty;

    [JsonProperty("buildVersion")]
    public string BuildVersion { get; set; } = string.Empty;

    [JsonProperty("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonProperty("manifests")]
    public List<ManifestInfo> Manifests { get; set; } = new();
}

public class ManifestInfo
{
    [JsonProperty("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonProperty("queryParams")]
    public List<QueryParam> QueryParams { get; set; } = new();
}

public class QueryParam
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("value")]
    public string Value { get; set; } = string.Empty;
}
