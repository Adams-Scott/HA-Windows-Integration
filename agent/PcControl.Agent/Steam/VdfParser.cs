using System.Text;

namespace PcControl.Agent.Steam;

/// <summary>
/// Minimal parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).
/// Objects are case-insensitive dictionaries whose values are either string or another dictionary.
/// </summary>
public static class VdfParser
{
    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseObject(text, ref pos, topLevel: true);
    }

    /// <summary>Gets a nested object by key, or null.</summary>
    public static Dictionary<string, object>? Obj(this Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    /// <summary>Gets a string value by key, or null.</summary>
    public static string? Str(this Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v as string : null;

    private static Dictionary<string, object> ParseObject(string s, ref int pos, bool topLevel)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = NextToken(s, ref pos, out bool keyQuoted);
            if (key is null)
            {
                if (topLevel) return result;
                throw new FormatException("Unexpected end of VDF data");
            }
            if (!keyQuoted && key == "}")
            {
                if (topLevel) throw new FormatException("Unexpected '}' in VDF data");
                return result;
            }
            if (!keyQuoted && key == "{") throw new FormatException("Unexpected '{' in VDF data");

            var value = NextToken(s, ref pos, out bool valueQuoted);
            if (value is null) throw new FormatException($"Missing value for key '{key}'");
            if (!valueQuoted && value == "{")
            {
                result[key] = ParseObject(s, ref pos, topLevel: false);
            }
            else if (!valueQuoted && value == "}")
            {
                throw new FormatException($"Missing value for key '{key}'");
            }
            else
            {
                result[key] = value;
            }
        }
    }

    private static string? NextToken(string s, ref int pos, out bool quoted)
    {
        quoted = false;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '[')
            {
                // Platform conditional such as [$WIN32]; ignore it.
                while (pos < s.Length && s[pos] != ']') pos++;
                pos++;
                continue;
            }
            break;
        }
        if (pos >= s.Length) return null;

        char first = s[pos];
        if (first is '{' or '}')
        {
            pos++;
            return first.ToString();
        }

        var sb = new StringBuilder();
        if (first == '"')
        {
            quoted = true;
            pos++;
            while (pos < s.Length && s[pos] != '"')
            {
                char c = s[pos++];
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                }
                else
                {
                    sb.Append(c);
                }
            }
            pos++; // closing quote
            return sb.ToString();
        }

        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"'))
            sb.Append(s[pos++]);
        return sb.ToString();
    }
}
