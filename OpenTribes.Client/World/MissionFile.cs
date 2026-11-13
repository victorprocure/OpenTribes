namespace OpenTribes.Client.World;

using System.Globalization;
using System.Text;

/// <summary>A mission object of a <c>.mis</c> text file: <c>instant Class "name" { field = "value"; ... children }</c>.</summary>
/// <remarks>docs/mission.md. The client renders from a local copy of the mission without a server, so this reads only the structure. The objects themselves are the render-side data of world-render.md 6.1.</remarks>
public sealed class MissionObject
{
    /// <summary>The class name.</summary>
    public string Class = "";
    /// <summary>The object name.</summary>
    public string Name = "";
    /// <summary>The fields by name, ignoring case.</summary>
    public readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The child objects in file order.</summary>
    public readonly List<MissionObject> Children = [];

    /// <summary>Reads a field as text.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The field text or the default.</returns>
    public string Get(string key, string def = "") => Fields.TryGetValue(key, out var v) ? v : def;

    /// <summary>Reads a field as a number.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="def">The value when the field is missing or not a number.</param>
    /// <returns>The number or the default.</returns>
    public double GetDouble(string key, double def) =>
        Fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : def;

    /// <summary>Reads a field as a vector of numbers separated by spaces. Missing or malformed components are zero.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="count">The number of components.</param>
    /// <returns>The components.</returns>
    public double[] GetVector(string key, int count = 3)
    {
        var r = new double[count];
        if (!Fields.TryGetValue(key, out var v))
            return r;
        var parts = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < count && i < parts.Length; ++i)
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                r[i] = d;
        return r;
    }

    /// <summary>Reads a field as a flag: <c>true</c> in any case or <c>1</c>.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The flag or the default.</returns>
    public bool GetBool(string key, bool def)
    {
        if (!Fields.TryGetValue(key, out var v))
            return def;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>Walks all descendants, each object before its children.</summary>
    /// <returns>The descendants.</returns>
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

/// <summary>Reads <c>.mis</c> text.</summary>
public static class MissionFile
{
    /// <summary>Parses mission text.</summary>
    /// <param name="text">The contents of a <c>.mis</c> file.</param>
    /// <returns>The top level objects (normally the single MissionGroup), or null when the text has no object.</returns>
    public static List<MissionObject> Parse(string text)
    {
        var tokens = Tokenize(text);
        var pos = 0;
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
        var p = pos + 2;
        if (t[p].StartsWith('"'))
            o.Name = Unquote(t[p++]);
        if (p >= t.Count || t[p] != "{")
            return null;
        ++p;
        while (p < t.Count && t[p] != "}")
        {
            if (t[p] is "instant" or "new")
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
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
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
                var j = i;
                while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] is not ('{' or '}' or ';' or '=' or '"'))
                    ++j;
                r.Add(s[i..j]);
                i = j;
            }
        }
        return r;
    }
}
