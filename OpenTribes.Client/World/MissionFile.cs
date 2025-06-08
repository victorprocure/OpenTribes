using System.Globalization;
using System.Text;

namespace OpenTribes.Client.World;

// A mission object of a `.mis` text file (docs/mission.md): `instant Class "name" { field = "value"; ... children }`.
// The client renders from a local copy of the mission without a server, so this reads only the structure; the objects
// themselves are the render-side data of world-render.md 6.1.
public sealed class MissionObject
{
    public string Class = "";
    public string Name = "";
    public readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<MissionObject> Children = new();

    public string Get(string key, string def = "") => Fields.TryGetValue(key, out string? v) ? v : def;

    public double GetDouble(string key, double def) =>
        Fields.TryGetValue(key, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : def;

    // "x y z": missing or malformed components are zero.
    public double[] GetVector(string key, int count = 3)
    {
        var r = new double[count];
        if (!Fields.TryGetValue(key, out string? v))
            return r;
        string[] parts = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < count && i < parts.Length; ++i)
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                r[i] = d;
        return r;
    }

    public bool GetBool(string key, bool def)
    {
        if (!Fields.TryGetValue(key, out string? v))
            return def;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    public IEnumerable<MissionObject> Descendants()
    {
        foreach (MissionObject c in Children)
        {
            yield return c;
            foreach (MissionObject d in c.Descendants())
                yield return d;
        }
    }
}

public static class MissionFile
{
    // Returns the top-level objects (normally the single MissionGroup); null when the text has no object.
    public static List<MissionObject> Parse(string text)
    {
        var tokens = Tokenize(text);
        int pos = 0;
        var roots = new List<MissionObject>();
        while (pos < tokens.Count)
        {
            MissionObject? o = ReadObject(tokens, ref pos);
            if (o == null)
                ++pos;
            else
                roots.Add(o);
        }
        return roots;
    }

    private static MissionObject? ReadObject(List<string> t, ref int pos)
    {
        // instant|new Class "name" {
        if (pos + 3 >= t.Count || (t[pos] != "instant" && t[pos] != "new"))
            return null;
        var o = new MissionObject { Class = t[pos + 1] };
        int p = pos + 2;
        if (t[p].StartsWith('"'))
            o.Name = Unquote(t[p++]);
        if (p >= t.Count || t[p] != "{")
            return null;
        ++p;
        while (p < t.Count && t[p] != "}")
        {
            if (t[p] == "instant" || t[p] == "new")
            {
                MissionObject? c = ReadObject(t, ref p);
                if (c == null)
                    return null;
                o.Children.Add(c);
                continue;
            }
            // key = value ;
            if (p + 2 < t.Count && t[p + 1] == "=")
            {
                o.Fields[t[p]] = Unquote(t[p + 2]);
                p += 3;
                if (p < t.Count && t[p] == ";")
                    ++p;
                continue;
            }
            ++p;
        }
        ++p; // }
        if (p < t.Count && t[p] == ";")
            ++p;
        pos = p;
        return o;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' ? s[1..^1].Replace("\\\\", "\\") : s;

    private static List<string> Tokenize(string s)
    {
        var r = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                ++i;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                    ++i;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder("\"");
                ++i;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                        sb.Append(s[i++]);
                    sb.Append(s[i++]);
                }
                ++i;
                sb.Append('"');
                r.Add(sb.ToString());
            }
            else if (c is '{' or '}' or ';' or '=')
            {
                r.Add(c.ToString());
                ++i;
            }
            else
            {
                int j = i;
                while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] is not ('{' or '}' or ';' or '=' or '"'))
                    ++j;
                r.Add(s[i..j]);
                i = j;
            }
        }
        return r;
    }
}
