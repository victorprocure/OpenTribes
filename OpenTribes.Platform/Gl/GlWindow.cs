namespace OpenTribes.Platform.Gl;

using OpenTribes.Platform.Sdl;

/// <summary>One SDL window with an OpenGL 3.3 core context, made current on the creating thread, and the loaded GL functions.</summary>
public sealed unsafe class GlWindow : IDisposable
{
    private static readonly Lock sLock = new();
    private static int sUsers;
    private IntPtr mContext;

    /// <summary>Gets the GL function table.</summary>
    public GlApi Gl { get; }
    /// <summary>Gets the SDL video driver name.</summary>
    public string VideoDriver { get; }
    /// <summary>Gets the GL version string.</summary>
    public string GlVersion { get; }
    /// <summary>Gets the GL renderer string.</summary>
    public string Renderer { get; }

    private GlWindow(IntPtr window, IntPtr context, GlApi gl, string driver)
    {
        Handle = window;
        mContext = context;
        Gl = gl;
        VideoDriver = driver;
        GlVersion = gl.GetString(GlApi.Version);
        Renderer = gl.GetString(GlApi.Renderer);
    }

    /// <summary>Creates a window with a GL 3.3 core context.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="hidden">Whether the window is hidden.</param>
    /// <param name="videoDriver">The SDL video driver, for example "offscreen". It is passed as the <c>SDL_VIDEO_DRIVER</c> hint before SDL is initialised. Null for the default.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>The window, or null when no window or no 3.3 core context can be made, for example with no display or GL driver.</returns>
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
                    var audioDriver = PlatformPaths.DefaultAudioDriver(PlatformPaths.CurrentOs, PlatformPaths.Env, videoDriver);
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
            var flags = Sdl3.WindowOpenGL | Sdl3.WindowResizable | PlatformPaths.HighDpiWindowFlag(PlatformPaths.CurrentOs, PlatformPaths.Env) | (hidden ? Sdl3.WindowHidden : 0);
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

    /// <summary>Gets the SDL window handle.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>Shows the window.</summary>
    public void Show() => Sdl3.ShowWindow(Handle);

    /// <summary>Turns relative mouse mode on or off.</summary>
    /// <param name="on">Whether relative mode is on.</param>
    public void SetRelativeMouse(bool on) => Sdl3.SetWindowRelativeMouseMode(Handle, on);

    /// <summary>Sets the window title.</summary>
    /// <param name="title">The title.</param>
    public void SetTitle(string title) => Sdl3.SetWindowTitle(Handle, title);

    /// <summary>Sets the window size.</summary>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    public void SetSize(int w, int h) => Sdl3.SetWindowSize(Handle, w, h);

    // Full screen state. mExclusive is the display mode that was set; mScaled is the requested size rendered scaled into
    // the window when no mode could be set. Neither is set windowed.
    private (int W, int H)? mExclusive, mScaled;
    private (int W, int H) mWindowedSize;

    /// <summary>Switches full screen on or off.</summary>
    /// <param name="on">Whether the window is full screen.</param>
    /// <param name="w">The wanted width.</param>
    /// <param name="h">The wanted height.</param>
    /// <param name="desktopMode">Whether to use borderless desktop mode.</param>
    /// <remarks>Stock goes full screen at the chosen resolution (see <see cref="FullscreenPlan"/>). This tries the display mode closest to w x h, else the desktop mode with the canvas kept at w x h and scaled (letterboxed) into the window.</remarks>
    public void SetFullscreen(bool on, int w, int h, bool desktopMode = false)
    {
        if (on)
        {
            if (!IsFullscreen())
            {
                Sdl3.GetWindowSize(Handle, out var ww, out var wh);
                mWindowedSize = (ww, wh);
            }
            mExclusive = mScaled = null;
            if (!desktopMode)
                mExclusive = Sdl3.EnterExclusiveFullscreen(Handle, w, h);
            if (mExclusive == null)
            {
                Sdl3.EnterDesktopFullscreen(Handle);
                if (!desktopMode)
                    mScaled = (w, h);
            }
        }
        else
        {
            var was = IsFullscreen();
            mExclusive = mScaled = null;
            Sdl3.LeaveFullscreen(Handle);
            if (was && mWindowedSize.W > 0)
                Sdl3.SetWindowSize(Handle, mWindowedSize.W, mWindowedSize.H);
        }
    }

    /// <summary>Gets the full screen resolution the canvas uses, or null windowed.</summary>
    public (int W, int H)? FullscreenCanvas => IsFullscreen() ? mExclusive ?? mScaled : null;

    /// <summary>Gets whether the canvas is scaled into a larger window because no display mode could be set.</summary>
    public bool Scaled => mScaled != null && IsFullscreen();

    /// <summary>Lists the full screen modes of the display the window is on, ascending.</summary>
    /// <returns>The (width, height) pairs.</returns>
    public List<(int W, int H)> FullscreenModes() => Sdl3.FullscreenModes(Handle);

    /// <summary>Tests whether the window is full screen.</summary>
    /// <returns>True when full screen.</returns>
    public bool IsFullscreen() => (Sdl3.GetWindowFlags(Handle) & Sdl3.WindowFullscreen) != 0;

    /// <summary>Sets the swap interval.</summary>
    /// <param name="interval">The interval, 0 for immediate and 1 for vsync.</param>
    public static void SetSwapInterval(int interval) => Sdl3.GlSetSwapInterval(interval);

    /// <summary>Swaps the window's buffers.</summary>
    public void Swap() => Sdl3.GlSwapWindow(Handle);

    /// <summary>Gets the window's real pixel size.</summary>
    /// <returns>The width and height.</returns>
    public (int w, int h) WindowPixels()
    {
        Sdl3.GetWindowSizeInPixels(Handle, out var w, out var h);
        return (w, h);
    }

    /// <summary>Gets the canvas size: the full screen resolution in full screen, else the window's pixel size (<see cref="FullscreenPlan.CanvasSize"/>).</summary>
    /// <returns>The width and height.</returns>
    public (int w, int h) PixelSize()
    {
        (var w, var h) = WindowPixels();
        (int W, int H)? fs = IsFullscreen() ? mExclusive ?? mScaled : null;
        (var cw, var ch) = FullscreenPlan.CanvasSize(fs != null, fs, fs ?? (w, h), (w, h));
        return (cw, ch);
    }

    /// <summary>Destroys the context and the window.</summary>
    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;
        Sdl3.GlDestroyContext(mContext);
        Sdl3.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
        mContext = IntPtr.Zero;
        Release();
    }
}
