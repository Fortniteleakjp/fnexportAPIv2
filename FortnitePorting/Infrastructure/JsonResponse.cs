using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

public static class JsonResponse
{
    public const string ContentType = "application/json; charset=utf-8";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static byte[] Serialize(object? value, Formatting formatting = Formatting.Indented)
    {
        using var buffer = new MemoryStream();
        using (var text = new StreamWriter(buffer, Utf8, 4096, leaveOpen: true))
        using (var writer = new JsonTextWriter(text) { Formatting = formatting })
            JsonSerializer.CreateDefault().Serialize(writer, value);
        return buffer.ToArray();
    }

    public static FileContentResult Result(object? value, Formatting formatting = Formatting.Indented)
        => new(Serialize(value, formatting), ContentType);

    public static JToken Parse(byte[] data)
    {
        using var buffer = new MemoryStream(data, writable: false);
        using var text = new StreamReader(buffer, Utf8);
        using var reader = new JsonTextReader(text);
        return JToken.Load(reader);
    }
}
