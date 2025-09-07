namespace OpenTribes.Platform;

public enum OsKind { Windows, Linux, MacOS }

// Platform choices of ROADMAP M9 C9: library file names, per-user directories, SDL driver defaults (docs/client-app.md 5.2, D54).
public static class PlatformPaths
{
    public static OsKind CurrentOs =>
        OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux;

    // File names tried for the SDL3 native library, in order. The one the platform's official package uses comes first.
    public static string[] SdlLibraryNames(OsKind os) => os switch
    {
        OsKind.Windows => new[] { "SDL3.dll", "SDL3" },
        OsKind.MacOS => new[] { "libSDL3.0.dylib", "libSDL3.dylib", "SDL3.framework/SDL3", "SDL3" },
        _ => new[] { "libSDL3.so.0", "libSDL3.so", "SDL3" },
    };

    // D54: the per-user directory the client uses as its game directory overlay when the install is read-only.
    // `env` returns an environment variable or null.
    public static string UserDataDir(OsKind os, Func<string, string?> env, string home)
    {
        switch (os)
        {
            case OsKind.Windows:
            {
                string? appData = env("APPDATA");
                return Path.Combine(string.IsNullOrEmpty(appData) ? Path.Combine(home, "AppData", "Roaming") : appData, "OpenTribes");
            }
            case OsKind.MacOS:
                return Path.Combine(home, "Library", "Application Support", "OpenTribes");
            default:
            {
                string? xdg = env("XDG_DATA_HOME");
                return Path.Combine(string.IsNullOrEmpty(xdg) || !Path.IsPathRooted(xdg) ? Path.Combine(home, ".local", "share") : xdg,
                                    "opentribes");
            }
        }
    }

    // SDL video driver when the user gave none: a Linux session without X11 or Wayland falls back to the offscreen driver.
    public static string? DefaultVideoDriver(OsKind os, Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(env("SDL_VIDEO_DRIVER")))
            return null;
        if (os == OsKind.Linux && string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
            return "offscreen";
        return null;
    }

    // SDL audio driver when the user gave none: a Linux session without a display or a sound server socket gets the dummy driver
    // only when the video driver is the offscreen one (a headless run).
    public static string? DefaultAudioDriver(OsKind os, Func<string, string?> env, string? videoDriver)
    {
        if (!string.IsNullOrEmpty(env("SDL_AUDIO_DRIVER")))
            return null;
        return videoDriver == "offscreen" || videoDriver == "dummy" ? "dummy" : null;
    }

    // macOS: the window is made in points at a pixel density of 1 (the game is 640x480 and the GL viewport equals the window size);
    // OT_HIGH_DPI=1 asks for SDL_WINDOW_HIGH_PIXEL_DENSITY (0x2000).
    public static ulong HighDpiWindowFlag(OsKind os, Func<string, string?> env) =>
        os == OsKind.MacOS && env("OT_HIGH_DPI") == "1" ? 0x2000UL : 0;

    public static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    public static bool IsDirWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".ot-write-" + Environment.ProcessId);
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsWriteDir(string name) =>
        name.Equals("config", StringComparison.OrdinalIgnoreCase) || name.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("recordings", StringComparison.OrdinalIgnoreCase);

    // D54: when `root` is not writeable, returns a per-user overlay directory: config, temp and recordings are real
    // directories (config starts as a copy of the install's), every other top-level entry of the install is a link to it.
    // Returns `root` when it is writeable, and null with `error` set when the overlay cannot be made.
    public static string? EnsureWritableRoot(string root, string overlay, out string error)
    {
        error = "";
        if (!Directory.Exists(root) || IsDirWritable(root))
            return root;
        try
        {
            Directory.CreateDirectory(overlay);
            foreach (string full in Directory.EnumerateFileSystemEntries(root))
            {
                string name = Path.GetFileName(full);
                string target = Path.Combine(overlay, name);
                if (Directory.Exists(full) && IsWriteDir(name))
                {
                    if (Directory.Exists(target))
                        continue;
                    Directory.CreateDirectory(target);
                    if (name.Equals("config", StringComparison.OrdinalIgnoreCase))
                        foreach (string f in Directory.EnumerateFiles(full))
                            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), false);
                    continue;
                }
                if (Directory.Exists(target) || File.Exists(target) || new FileInfo(target).LinkTarget != null)
                    continue;
                if (Directory.Exists(full))
                    Directory.CreateSymbolicLink(target, full);
                else
                    File.CreateSymbolicLink(target, full);
            }
            foreach (string d in new[] { "config", "temp", "recordings" })
                if (!Directory.Exists(Path.Combine(overlay, d)))
                    Directory.CreateDirectory(Path.Combine(overlay, d));
            return overlay;
        }
        catch (Exception e)
        {
            error = $"{root} is read-only and the overlay {overlay} cannot be made ({e.Message}); pass a writeable --root";
            return null;
        }
    }
}
