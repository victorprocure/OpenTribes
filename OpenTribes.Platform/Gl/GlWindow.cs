using OpenTribes.Platform.Sdl;

namespace OpenTribes.Platform.Gl;

// One SDL window with an OpenGL 3.3 core context made current on the creating thread, plus the loaded GL functions.
public sealed unsafe class GlWindow : IDisposable
{
    private static readonly object sLock = new();
    private static int sUsers;

    private IntPtr mWindow;
    private IntPtr mContext;

    public GlApi Gl { get; }
    public string VideoDriver { get; }
    public string GlVersion { get; }

    private GlWindow(IntPtr window, IntPtr context, GlApi gl, string driver)
    {
        mWindow = window;
        mContext = context;
        Gl = gl;
        VideoDriver = driver;
        GlVersion = gl.GetString(GlApi.Version);
    }

    // Returns null with `error` set when no window or no 3.3 core context can be made (no display, no GL driver).
    // `videoDriver` (for example "offscreen") is passed as the SDL_VIDEO_DRIVER hint before SDL is initialised.
    public static GlWindow? TryCreate(string title, int width, int height, bool hidden, string? videoDriver,
                                      out string error)
    {
        error = "";
        try
        {
            lock (sLock)
            {
                if (sUsers == 0)
                {
                    videoDriver ??= PlatformPaths.DefaultVideoDriver(PlatformPaths.CurrentOs, PlatformPaths.Env);
                    if (videoDriver != null)
                        Sdl3.SetHintOverride("SDL_VIDEO_DRIVER", videoDriver);
                    string? audioDriver = PlatformPaths.DefaultAudioDriver(PlatformPaths.CurrentOs, PlatformPaths.Env, videoDriver);
                    if (audioDriver != null)
                        Sdl3.SetHintOverride("SDL_AUDIO_DRIVER", audioDriver);
                    if (!Sdl3.Init(Sdl3.InitVideo))
                    {
                        error = "SDL_Init: " + Sdl3.GetError();
                        return null;
                    }
                }
                ++sUsers;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "SDL3 not available: " + e.Message;
            return null;
        }

        IntPtr window = IntPtr.Zero, ctx = IntPtr.Zero;
        try
        {
            Sdl3.GlSetAttribute(Sdl3.GlContextMajorVersion, 3);
            Sdl3.GlSetAttribute(Sdl3.GlContextMinorVersion, 3);
            Sdl3.GlSetAttribute(Sdl3.GlContextProfileMask, Sdl3.GlProfileCore);
            Sdl3.GlSetAttribute(Sdl3.GlDoubleBuffer, 1);
            Sdl3.GlSetAttribute(Sdl3.GlDepthSize, 24);
            ulong flags = Sdl3.WindowOpenGL | Sdl3.WindowResizable | PlatformPaths.HighDpiWindowFlag(PlatformPaths.CurrentOs, PlatformPaths.Env) | (hidden ? Sdl3.WindowHidden : 0);
            window = Sdl3.CreateWindow(title, width, height, flags);
            if (window == IntPtr.Zero)
            {
                error = "SDL_CreateWindow: " + Sdl3.GetError();
                Release();
                return null;
            }
            ctx = Sdl3.GlCreateContext(window);
            if (ctx == IntPtr.Zero)
            {
                error = "SDL_GL_CreateContext: " + Sdl3.GetError();
                Sdl3.DestroyWindow(window);
                Release();
                return null;
            }
            if (!Sdl3.GlMakeCurrent(window, ctx))
            {
                error = "SDL_GL_MakeCurrent: " + Sdl3.GetError();
                Sdl3.GlDestroyContext(ctx);
                Sdl3.DestroyWindow(window);
                Release();
                return null;
            }
            var gl = new GlApi(Sdl3.GlGetProcAddress);
            return new GlWindow(window, ctx, gl, Sdl3.CurrentVideoDriver());
        }
        catch (Exception e)
        {
            error = e.Message;
            if (ctx != IntPtr.Zero)
                Sdl3.GlDestroyContext(ctx);
            if (window != IntPtr.Zero)
                Sdl3.DestroyWindow(window);
            Release();
            return null;
        }
    }

    private static void Release()
    {
        lock (sLock)
        {
            if (--sUsers == 0)
                Sdl3.Quit();
        }
    }

    public IntPtr Handle => mWindow;

    public void Show() => Sdl3.ShowWindow(mWindow);

    public void SetRelativeMouse(bool on) => Sdl3.SetWindowRelativeMouseMode(mWindow, on);

    public void SetTitle(string title) => Sdl3.SetWindowTitle(mWindow, title);

    public void SetSize(int w, int h) => Sdl3.SetWindowSize(mWindow, w, h);

    public void SetFullscreen(bool on) => Sdl3.SetWindowFullscreen(mWindow, on);

    public bool IsFullscreen() => (Sdl3.GetWindowFlags(mWindow) & Sdl3.WindowFullscreen) != 0;

    public void SetSwapInterval(int interval) => Sdl3.GlSetSwapInterval(interval);

    public void Swap() => Sdl3.GlSwapWindow(mWindow);

    public (int w, int h) PixelSize()
    {
        Sdl3.GetWindowSizeInPixels(mWindow, out int w, out int h);
        return (w, h);
    }

    public void Dispose()
    {
        if (mWindow == IntPtr.Zero)
            return;
        Sdl3.GlDestroyContext(mContext);
        Sdl3.DestroyWindow(mWindow);
        mWindow = IntPtr.Zero;
        mContext = IntPtr.Zero;
        Release();
    }
}
