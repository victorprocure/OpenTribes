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
}
