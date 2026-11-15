// #47: isFile and the File::copy source must not reach files outside the game directory. Stock passes both paths straight to
// CopyFileA (File::copy 004011a8) and to a file stream open (isFile, sim plugin command 0x6a); paths that stay inside the game
// directory keep working, with either slash and any case.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;

public class FilePathEscapeTests
{
    private readonly string _root;
    private readonly Interpreter _interp;

    public FilePathEscapeTests()
    {
        var top = TestPaths.TmpDir("file_path_escape");
        Directory.Delete(top, true);
        _root = Path.Combine(top, "game");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
        Directory.CreateDirectory(Path.Combine(_root, "base", "Missions"));
        File.WriteAllText(Path.Combine(_root, "base", "Missions", "A.mis"), "mission");
        File.WriteAllText(Path.Combine(top, "secret.txt"), "secret");
        var strings = new StringTable();
        _interp = new Interpreter(strings);
        var res = new ResourceManager(_root, strings);
        _interp.SetResources(res);
        Builtins.RegisterBuiltins(_interp, res, _root);
    }

    private string Call(string name, params string[] args) => _interp.CallFunction(name, args);

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("config/../../secret.txt")]
    [InlineData("BASE\\Missions\\..\\..\\..\\secret.txt")]
    public void PathsLeavingTheGameDirectoryAreRefused(string path)
    {
        Assert.Equal("False", Call("isFile", path));
        Assert.Equal("False", Call("isFile", "base\\missions\\a.mis", path));
        Assert.Equal("FALSE", Call("File::copy", path, "config\\stolen.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "config", "stolen.txt")));
    }

    [Theory]
    [InlineData("base\\missions\\a.mis")]
    [InlineData("BASE/MISSIONS/A.MIS")]
    [InlineData("base/Missions\\A.mis")]
    [InlineData("config\\..\\base\\missions\\a.mis")]
    [InlineData("./base/./missions/a.mis")]
    [InlineData("/base/missions/a.mis")]
    public void PathsInsideTheGameDirectoryWorkAsBefore(string path)
    {
        Assert.Equal("True", Call("isFile", path));
        Assert.Equal("TRUE", Call("File::copy", path, "temp\\copy.mis"));
        Assert.Equal("mission", File.ReadAllText(Path.Combine(_root, "temp", "copy.mis")));
    }

    [Fact]
    public void AMissingFileInsideIsStillFalse()
    {
        Assert.Equal("False", Call("isFile", "base\\missions\\b.mis"));
        Assert.Equal("FALSE", Call("File::copy", "base\\missions\\b.mis", "temp\\copy.mis"));
    }
}
