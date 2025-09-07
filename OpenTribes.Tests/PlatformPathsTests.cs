using OpenTribes.Platform;
using OpenTribes.Script;

namespace OpenTribes.Tests;

// ROADMAP M9 C9: platform-dependent choices of OpenTribes.Platform and the case rules on case-sensitive file systems.
public class PlatformPathsTests
{
    private static Func<string, string?> Env(params (string k, string v)[] vars) =>
        name => vars.Where(x => x.k == name).Select(x => x.v).FirstOrDefault();

    [Theory]
    [InlineData(OsKind.Windows, "SDL3.dll")]
    [InlineData(OsKind.Linux, "libSDL3.so.0")]
    [InlineData(OsKind.MacOS, "libSDL3.0.dylib")]
    public void SdlLibraryNameFirstChoice(OsKind os, string first) => Assert.Equal(first, PlatformPaths.SdlLibraryNames(os)[0]);

    [Fact]
    public void UserDataDirPerPlatform()
    {
        Assert.Equal(Path.Combine("/h", "Library", "Application Support", "OpenTribes"),
                     PlatformPaths.UserDataDir(OsKind.MacOS, Env(), "/h"));
        Assert.Equal(Path.Combine("/h", ".local", "share", "opentribes"), PlatformPaths.UserDataDir(OsKind.Linux, Env(), "/h"));
        Assert.Equal(Path.Combine("/x", "opentribes"), PlatformPaths.UserDataDir(OsKind.Linux, Env(("XDG_DATA_HOME", "/x")), "/h"));
        // A relative XDG_DATA_HOME is ignored (XDG base directory rule).
        Assert.Equal(Path.Combine("/h", ".local", "share", "opentribes"),
                     PlatformPaths.UserDataDir(OsKind.Linux, Env(("XDG_DATA_HOME", "rel")), "/h"));
        Assert.Equal(Path.Combine("C:\\Roam", "OpenTribes"), PlatformPaths.UserDataDir(OsKind.Windows, Env(("APPDATA", "C:\\Roam")), "C:\\u"));
        Assert.Equal(Path.Combine("C:\\u", "AppData", "Roaming", "OpenTribes"), PlatformPaths.UserDataDir(OsKind.Windows, Env(), "C:\\u"));
    }

    [Fact]
    public void SdlDriverDefaults()
    {
        Assert.Equal("offscreen", PlatformPaths.DefaultVideoDriver(OsKind.Linux, Env()));
        Assert.Null(PlatformPaths.DefaultVideoDriver(OsKind.Linux, Env(("DISPLAY", ":0"))));
        Assert.Null(PlatformPaths.DefaultVideoDriver(OsKind.Linux, Env(("WAYLAND_DISPLAY", "wayland-0"))));
        Assert.Null(PlatformPaths.DefaultVideoDriver(OsKind.Linux, Env(("SDL_VIDEO_DRIVER", "x11"))));
        Assert.Null(PlatformPaths.DefaultVideoDriver(OsKind.Windows, Env()));
        Assert.Null(PlatformPaths.DefaultVideoDriver(OsKind.MacOS, Env()));
        Assert.Equal("dummy", PlatformPaths.DefaultAudioDriver(OsKind.Linux, Env(), "offscreen"));
        Assert.Null(PlatformPaths.DefaultAudioDriver(OsKind.Linux, Env(), null));
        Assert.Null(PlatformPaths.DefaultAudioDriver(OsKind.Linux, Env(("SDL_AUDIO_DRIVER", "alsa")), "offscreen"));
    }

    [Fact]
    public void HighDpiIsOptInOnMac()
    {
        Assert.Equal(0UL, PlatformPaths.HighDpiWindowFlag(OsKind.MacOS, Env()));
        Assert.Equal(0x2000UL, PlatformPaths.HighDpiWindowFlag(OsKind.MacOS, Env(("OT_HIGH_DPI", "1"))));
        Assert.Equal(0UL, PlatformPaths.HighDpiWindowFlag(OsKind.Linux, Env(("OT_HIGH_DPI", "1"))));
    }

    [Fact]
    public void WriteablePathRuleUsesBothSeparators()
    {
        Assert.True(Builtins.IsWriteablePath("config/ClientPrefs.cs"));
        Assert.True(Builtins.IsWriteablePath("CONFIG\\x.cs"));
        Assert.True(Builtins.IsWriteablePath("base/missions/a.mis"));
        Assert.False(Builtins.IsWriteablePath("base/missions/sub/a.mis"));
        Assert.False(Builtins.IsWriteablePath("x.cs"));
    }

    [Fact]
    public void GamePathResolvesMixedCase()
    {
        string root = Path.Combine(TestPaths.TmpDir("platform"), "case_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "Base", "Missions"));
        File.WriteAllText(Path.Combine(root, "Base", "Missions", "Broadside.MIS"), "x");
        string host = Builtins.ResolveGamePath(root, "base\\missions\\broadside.mis");
        Assert.True(File.Exists(host), host);
    }

    [Fact]
    public void ReadOnlyRootGetsOverlay()
    {
        string dir = TestPaths.TmpDir("platform");
        string root = Path.Combine(dir, "ro_root"), overlay = Path.Combine(dir, "ro_overlay");
        foreach (string d in new[] { root, overlay })
            if (Directory.Exists(d))
                Directory.Delete(d, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "console.cs"), "c");
        File.WriteAllText(Path.Combine(root, "Config", "a.cs"), "a");
        // A writeable root is used as it is.
        Assert.Equal(root, PlatformPaths.EnsureWritableRoot(root, overlay, out _));
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.Equal(overlay, PlatformPaths.EnsureWritableRoot(root, overlay, out string err));
                Assert.Equal("", err);
                Assert.True(File.Exists(Path.Combine(overlay, "console.cs")));
                Assert.True(Directory.Exists(Path.Combine(overlay, "base")));
                Assert.Equal("a", File.ReadAllText(Path.Combine(overlay, "Config", "a.cs")));
                Assert.True(Directory.Exists(Path.Combine(overlay, "temp")));
                Assert.True(Directory.Exists(Path.Combine(overlay, "recordings")));
                Assert.True(PlatformPaths.IsDirWritable(Path.Combine(overlay, "Config")));
            }
            finally
            {
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
