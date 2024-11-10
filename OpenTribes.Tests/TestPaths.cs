// Port of the TEST_TMP_DIR define of CMakeLists.txt: a scratch directory for synthetic fixtures.
namespace OpenTribes.Tests;

internal static class TestPaths
{
    public static string TmpDir(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
