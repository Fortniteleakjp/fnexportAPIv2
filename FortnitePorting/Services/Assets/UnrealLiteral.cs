using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

/// <summary>
/// Parses the Unreal property-literal syntax used by DataTable RowUpdate values, for example
/// <c>(X=1,Y=1)</c>, <c>(45.000000,55.000000)</c>, <c>"text"</c>, <c>True</c> or <c>1.5</c>.
/// Anything that does not fit is returned unchanged as a string.
/// </summary>
internal static class UnrealLiteral
{
    public static JToken Parse(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return string.Empty;

        if (text[0] == '(' && text[^1] == ')')
        {
            return ParseGroup(text[1..^1]);
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return Unescape(text[1..^1]);
        }

        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return text;
    }

    /// <summary>
    /// Splits the body of a (...) group at top level. Elements shaped <c>Key=Value</c> produce an
    /// object, positional elements produce an array.
    /// </summary>
    private static JToken ParseGroup(string body)
    {
        var elements = SplitTopLevel(body);
        if (elements.Count == 0) return new JObject();

        var keyed = elements.Count(element => KeyOf(element) != null);
        if (keyed == elements.Count)
        {
            var obj = new JObject();
            foreach (var element in elements)
            {
                var separator = KeyOf(element)!.Value;
                obj[element[..separator].Trim()] = Parse(element[(separator + 1)..]);
            }

            return obj;
        }

        var array = new JArray();
        foreach (var element in elements)
        {
            array.Add(Parse(element));
        }

        return array;
    }

    /// <summary>Index of the '=' that makes this element a Key=Value pair, or null when it is positional.</summary>
    private static int? KeyOf(string element)
    {
        for (var i = 0; i < element.Length; i++)
        {
            var c = element[i];
            if (c == '=') return i > 0 ? i : null;
            if (c is '(' or ')' or '"' or ',') return null;
        }

        return null;
    }

    private static List<string> SplitTopLevel(string body)
    {
        var elements = new List<string>();
        var depth = 0;
        var inQuotes = false;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (inQuotes)
            {
                if (c == '\\') i++;
                else if (c == '"') inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    elements.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }

        elements.Add(body[start..]);
        return elements.Where(element => element.Trim().Length > 0).ToList();
    }

    /// <summary>Removes the backslash escapes Unreal writes inside quoted literals and JSON payloads.</summary>
    public static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                builder.Append(value[++i]);
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }
}
