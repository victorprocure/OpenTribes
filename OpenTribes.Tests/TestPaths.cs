// Port of the TEST_TMP_DIR define of CMakeLists.txt: a scratch directory for synthetic fixtures. RepoFile and IncRows
// read the generated vector tables the C++ tests #include from tests/.
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenTribes.Tests;

internal static class TestPaths
{
    public static string TmpDir(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A file of the source tree, found from the test binary directory upwards.
    public static string RepoFile(string rel)
    {
        for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, rel);
            if (File.Exists(p))
                return p;
        }
        throw new FileNotFoundException(rel);
    }

    // The rows of an #include'd table `{0x...u, ...},`: one array of u32 values per brace group.
    public static uint[][] IncRows(string rel)
    {
        var rows = new List<uint[]>();
        string text = Regex.Replace(File.ReadAllText(RepoFile(rel)), "//[^\n]*", "");
        foreach (Match row in Regex.Matches(text, @"\{([^{}]*)\}"))
        {
            var values = new List<uint>();
            foreach (Match v in Regex.Matches(row.Groups[1].Value, @"0x([0-9a-fA-F]+)u?"))
                values.Add(uint.Parse(v.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            rows.Add(values.ToArray());
        }
        return rows.ToArray();
    }

    // The rows of a named #include'd table `name[] = { {...}, ... };` (or the single brace group of `name[3] = {...};`),
    // each row flattened to its value tokens (nested braces dropped). With `name` null: every top-level brace group of
    // the file (a table body included without a declaration).
    public static List<string[]> IncTokens(string rel, string? name)
    {
        string text = Regex.Replace(File.ReadAllText(RepoFile(rel)), "//[^\n]*", "");
        int start = 0, end = text.Length;
        bool single = false;
        if (name != null)
        {
            Match m = Regex.Match(text, @"\b" + Regex.Escape(name) + @"\s*\[[^\]]*\](\s*\[[^\]]*\])?\s*=\s*\{");
            if (!m.Success)
                throw new InvalidDataException(name);
            start = m.Index + m.Length;
            int depth = 1, i = start;
            for (; i < text.Length && depth > 0; ++i)
                depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
            end = i - 1;
            single = !text[start..end].Contains('{');
        }
        var rows = new List<string[]>();
        string body = text[start..end];
        if (single)
        {
            rows.Add(Split(body));
            return rows;
        }
        int d = 0, rowStart = -1;
        for (int i = 0; i < body.Length; ++i)
        {
            if (body[i] == '{')
            {
                if (d++ == 0)
                    rowStart = i + 1;
            }
            else if (body[i] == '}' && --d == 0)
            {
                rows.Add(Split(body[rowStart..i].Replace("{", "").Replace("}", "")));
            }
        }
        return rows;
    }

    private static string[] Split(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => t.Length != 0).ToArray();

    // One token of such a row: a hexadecimal or decimal integer (an optional u suffix), as a u32.
    public static uint IncU32(string t)
    {
        t = t.TrimEnd('u', 'U');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return unchecked((uint)int.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    }

    // A float literal token (`0.25f`, `-1.5f`, `0`), the nearest float as the C++ compiler reads it.
    public static float IncF32(string t) => float.Parse(t.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);
}
