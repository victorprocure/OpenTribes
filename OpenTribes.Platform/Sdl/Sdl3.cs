namespace OpenTribes.Platform.Sdl;

using System.Reflection;
using System.Runtime.InteropServices;

/// <summary>Our own minimal SDL3 bindings (D30): the subset the client needs for init, a window with an OpenGL context, event polling, timing and the GL proc address.</summary>
/// <remarks>Values were checked against the SDL 3.4.18 headers.</remarks>
public static partial class Sdl3
{
    private const string Lib = "SDL3";

    /// <summary>The SDL_INIT_VIDEO flag.</summary>
    public const uint InitVideo = 0x20;
    /// <summary>The SDL_WINDOW_OPENGL flag.</summary>
    public const ulong WindowOpenGL = 0x2;
    /// <summary>The SDL_WINDOW_HIDDEN flag.</summary>
    public const ulong WindowHidden = 0x8;
    /// <summary>The SDL_WINDOW_RESIZABLE flag.</summary>
    public const ulong WindowResizable = 0x20;

    /// <summary>The SDL_GLAttr values for the colour, depth and double buffer settings.</summary>
    public const int GlRedSize = 0, GlGreenSize = 1, GlBlueSize = 2, GlAlphaSize = 3, GlDoubleBuffer = 5, GlDepthSize = 6,
        GlContextMajorVersion = 17, GlContextMinorVersion = 18, GlContextFlags = 19, GlContextProfileMask = 20;
    /// <summary>The SDL_GL_CONTEXT_PROFILE_CORE value.</summary>
    public const int GlProfileCore = 1;

    /// <summary>The SDL_EventType values for quit, window, keyboard, mouse and joystick events.</summary>
    public const uint EventQuit = 0x100, EventWindowPixelSizeChanged = 0x207, EventWindowResized = 0x206,
        EventWindowCloseRequested = 0x210, EventKeyDown = 0x300, EventKeyUp = 0x301,
        EventWindowMinimized = 0x209, EventWindowRestored = 0x20B, EventWindowFocusGained = 0x20E, EventWindowFocusLost = 0x20F,
        EventMouseMotion = 0x400, EventMouseButtonDown = 0x401, EventMouseButtonUp = 0x402, EventMouseWheel = 0x403;

    /// <summary>The SDL keycode of the escape key.</summary>
    public const uint KeyEscape = 27;

    static Sdl3() => NativeLibrary.SetDllImportResolver(typeof(Sdl3).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib)
            return IntPtr.Zero;
        var candidates = PlatformPaths.SdlLibraryNames(PlatformPaths.CurrentOs);
        // The copy next to the executable wins over a system copy.
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, c), out var hb))
                return hb;
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, assembly, path, out var h))
                return h;
        return IntPtr.Zero;
    }

    /// <summary>Forces the static constructor, which installs the library resolver, before the first P/Invoke from other classes.</summary>
    public static void EnsureLoaded() { }

    [LibraryImport(Lib, EntryPoint = "SDL_GetError")]
    private static partial IntPtr GetErrorPtr();

    /// <summary>Gets the last SDL error message.</summary>
    /// <returns>The message, empty when none.</returns>
    public static string GetError() => Marshal.PtrToStringUTF8(GetErrorPtr()) ?? "";

    // SDL_SetHintWithPriority; priority 2 is SDL_HINT_OVERRIDE, which wins over an environment variable.
    [LibraryImport(Lib, EntryPoint = "SDL_SetHintWithPriority", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetHintWithPriority(string name, string value, int priority);

    /// <summary>Sets a hint with SDL_HINT_OVERRIDE priority, which wins over an environment variable (SDL_SetHintWithPriority).</summary>
    /// <param name="name">The hint name.</param>
    /// <param name="value">The hint value.</param>
    /// <returns>True on success.</returns>
    public static bool SetHintOverride(string name, string value) => SetHintWithPriority(name, value, 2);

    /// <summary>Initialises SDL subsystems (SDL_Init).</summary>
    /// <param name="flags">The subsystem flags.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    /// <summary>Shuts SDL down (SDL_Quit).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_Quit")]
    internal static partial void Quit();

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentVideoDriver")]
    private static partial IntPtr GetCurrentVideoDriverPtr();

    /// <summary>Gets the name of the current video driver.</summary>
    /// <returns>The name, empty when none.</returns>
    public static string CurrentVideoDriver() => Marshal.PtrToStringUTF8(GetCurrentVideoDriverPtr()) ?? "";

    /// <summary>Creates a window (SDL_CreateWindow).</summary>
    /// <param name="title">The title.</param>
    /// <param name="w">The width in screen units.</param>
    /// <param name="h">The height in screen units.</param>
    /// <param name="flags">The window flags.</param>
    /// <returns>The window, or zero on failure.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr CreateWindow(string title, int w, int h, ulong flags);

    /// <summary>Destroys a window (SDL_DestroyWindow).</summary>
    /// <param name="window">The window.</param>
    [LibraryImport(Lib, EntryPoint = "SDL_DestroyWindow")]
    internal static partial void DestroyWindow(IntPtr window);

    /// <summary>Shows a window (SDL_ShowWindow).</summary>
    /// <param name="window">The window.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_ShowWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ShowWindow(IntPtr window);

    /// <summary>Gets the window size in pixels (SDL_GetWindowSizeInPixels).</summary>
    /// <param name="window">The window.</param>
    /// <param name="w">Receives the width.</param>
    /// <param name="h">Receives the height.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSizeInPixels(IntPtr window, out int w, out int h);

    /// <summary>Sets an OpenGL attribute (SDL_GL_SetAttribute).</summary>
    /// <param name="attr">The attribute.</param>
    /// <param name="value">The value.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_SetAttribute")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSetAttribute(int attr, int value);

    /// <summary>Creates an OpenGL context for a window (SDL_GL_CreateContext).</summary>
    /// <param name="window">The window.</param>
    /// <returns>The context, or zero on failure.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_CreateContext")]
    internal static partial IntPtr GlCreateContext(IntPtr window);

    /// <summary>Destroys an OpenGL context (SDL_GL_DestroyContext).</summary>
    /// <param name="context">The context.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_DestroyContext")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlDestroyContext(IntPtr context);

    /// <summary>Makes an OpenGL context current (SDL_GL_MakeCurrent).</summary>
    /// <param name="window">The window.</param>
    /// <param name="context">The context.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_MakeCurrent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlMakeCurrent(IntPtr window, IntPtr context);

    /// <summary>Swaps the window's buffers (SDL_GL_SwapWindow).</summary>
    /// <param name="window">The window.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_SwapWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSwapWindow(IntPtr window);

    /// <summary>Sets the swap interval (SDL_GL_SetSwapInterval).</summary>
    /// <param name="interval">The interval, 0 for immediate and 1 for vsync.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_SetSwapInterval")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSetSwapInterval(int interval);

    /// <summary>Gets the address of an OpenGL function (SDL_GL_GetProcAddress).</summary>
    /// <param name="name">The function name.</param>
    /// <returns>The address, or zero.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GL_GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GlGetProcAddress(string name);

    /// <summary>Polls for a pending event (SDL_PollEvent).</summary>
    /// <param name="ev">Receives the event.</param>
    /// <returns>True when an event was returned.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool PollEvent(SdlEvent* ev);

    /// <summary>Polls for a pending event.</summary>
    /// <param name="ev">Receives the event.</param>
    /// <returns>True when an event was returned.</returns>
    public static unsafe bool PollEvent(out SdlEvent ev)
    {
        SdlEvent e;
        var got = PollEvent(&e);
        ev = e;
        return got;
    }

    /// <summary>Gets the time since SDL started in nanoseconds (SDL_GetTicksNS).</summary>
    /// <returns>The time.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetTicksNS")]
    internal static partial ulong GetTicksNS();

    /// <summary>Gets the high resolution counter (SDL_GetPerformanceCounter).</summary>
    /// <returns>The counter.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetPerformanceCounter")]
    internal static partial ulong GetPerformanceCounter();

    /// <summary>Gets the frequency of the high resolution counter (SDL_GetPerformanceFrequency).</summary>
    /// <returns>The frequency in counts per second.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetPerformanceFrequency")]
    internal static partial ulong GetPerformanceFrequency();

    /// <summary>Turns relative mouse mode on or off (SDL_SetWindowRelativeMouseMode).</summary>
    /// <param name="window">The window.</param>
    /// <param name="enabled">Whether relative mode is on.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowRelativeMouseMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowRelativeMouseMode(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool enabled);

    /// <summary>Sets the window title (SDL_SetWindowTitle).</summary>
    /// <param name="window">The window.</param>
    /// <param name="title">The title.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowTitle", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowTitle(IntPtr window, string title);

    /// <summary>Sets the window size (SDL_SetWindowSize).</summary>
    /// <param name="window">The window.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowSize(IntPtr window, int w, int h);

    /// <summary>Sets the window full screen state (SDL_SetWindowFullscreen).</summary>
    /// <param name="window">The window.</param>
    /// <param name="fullscreen">Whether the window is full screen.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowFullscreen")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowFullscreen(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool fullscreen);

    [LibraryImport(Lib, EntryPoint = "SDL_GetClosestFullscreenDisplayMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool GetClosestFullscreenDisplayMode(uint displayId, int w, int h, float refreshRate, [MarshalAs(UnmanagedType.U1)] bool highDensity, byte* closest);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowFullscreenMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool SetWindowFullscreenMode(IntPtr window, byte* mode);

    [LibraryImport(Lib, EntryPoint = "SDL_SyncWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SyncWindow(IntPtr window);

    /// <summary>Gets the window size in screen units (SDL_GetWindowSize).</summary>
    /// <param name="window">The window.</param>
    /// <param name="w">Receives the width.</param>
    /// <param name="h">Receives the height.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSize(IntPtr window, out int w, out int h);

    /// <summary>Enters exclusive full screen at the display mode closest to a size (SDL_GetClosestFullscreenDisplayMode, then SDL_SetWindowFullscreenMode and SDL_SetWindowFullscreen).</summary>
    /// <param name="window">The window.</param>
    /// <param name="w">The wanted width.</param>
    /// <param name="h">The wanted height.</param>
    /// <returns>The mode that was set, or null when the display has no such mode or the driver refuses the change. The window is then left windowed.</returns>
    public static unsafe (int W, int H)? EnterExclusiveFullscreen(IntPtr window, int w, int h)
    {
        var mode = stackalloc byte[128];
        if (!GetClosestFullscreenDisplayMode(GetDisplayForWindow(window), w, h, 0f, false, mode))
            return null;
        if (!SetWindowFullscreenMode(window, mode) || !SetWindowFullscreen(window, true))
            return null;
        SyncWindow(window);
        var m = (int*)mode;
        return (m[2], m[3]);
    }

    /// <summary>Enters borderless full screen at the desktop mode, without a display mode change.</summary>
    /// <param name="window">The window.</param>
    /// <returns>True on success.</returns>
    public static unsafe bool EnterDesktopFullscreen(IntPtr window)
    {
        if (!SetWindowFullscreenMode(window, null) || !SetWindowFullscreen(window, true))
            return false;
        SyncWindow(window);
        return true;
    }

    /// <summary>Leaves full screen.</summary>
    /// <param name="window">The window.</param>
    /// <returns>True on success.</returns>
    public static bool LeaveFullscreen(IntPtr window)
    {
        var ok = SetWindowFullscreen(window, false);
        SyncWindow(window);
        return ok;
    }

    /// <summary>Sets whether the window can be resized (SDL_SetWindowResizable).</summary>
    /// <param name="window">The window.</param>
    /// <param name="resizable">Whether it can be resized.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowResizable")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowResizable(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool resizable);

    /// <summary>Gets the window flags (SDL_GetWindowFlags).</summary>
    /// <param name="window">The window.</param>
    /// <returns>The flags.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowFlags")]
    internal static partial ulong GetWindowFlags(IntPtr window);

    /// <summary>The SDL_WINDOW_FULLSCREEN flag.</summary>
    public const ulong WindowFullscreen = 0x1;

    /// <summary>Waits for a number of milliseconds (SDL_Delay).</summary>
    /// <param name="ms">The time to wait.</param>
    [LibraryImport(Lib, EntryPoint = "SDL_Delay")]
    internal static partial void Delay(uint ms);

    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplayForWindow")]
    private static partial uint GetDisplayForWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetFullscreenDisplayModes")]
    private static unsafe partial IntPtr* GetFullscreenDisplayModes(uint displayId, out int count);

    [LibraryImport(Lib, EntryPoint = "SDL_free")]
    private static partial void Free(IntPtr mem);

    /// <summary>Lists the full screen modes of the display the window is on.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The (width, height) pairs without repeats, ascending. SDL lists one per refresh rate and pixel format. Empty when SDL lists none.</returns>
    /// <remarks>SDL_DisplayMode holds displayID, format, w and h at offsets 0, 4, 8 and 12.</remarks>
    public static unsafe List<(int W, int H)> FullscreenModes(IntPtr window)
    {
        var modes = new List<(int, int)>();
        var list = GetFullscreenDisplayModes(GetDisplayForWindow(window), out var count);
        if (list == null)
            return modes;
        for (var i = 0; i < count; ++i)
        {
            var m = (int*)list[i];
            if (m[2] > 0 && m[3] > 0 && !modes.Contains((m[2], m[3])))
                modes.Add((m[2], m[3]));
        }
        Free((IntPtr)list);
        modes.Sort();
        return modes;
    }
}

/// <summary>The SDL_Event union, 128 bytes. Only the fields the client reads are exposed, read from the native record at their offsets.</summary>
public readonly struct SdlEvent
{
    private readonly SdlEventBytes _raw;

    /// <summary>The event type.</summary>
    public uint Type => Read<uint>(0);
    /// <summary>The window id, for window and keyboard events.</summary>
    public uint WindowId => Read<uint>(16);
    /// <summary>The new width, for window events.</summary>
    public int Data1 => Read<int>(20);
    /// <summary>The new height, for window events.</summary>
    public int Data2 => Read<int>(24);
    /// <summary>The scancode, for keyboard events.</summary>
    public uint Scancode => Read<uint>(24);
    /// <summary>The SDL_Keycode, for keyboard events.</summary>
    public uint Key => Read<uint>(28);
    /// <summary>Whether the key is down, for keyboard events.</summary>
    public byte Down => Read<byte>(36);
    /// <summary>Whether the key is a repeat, for keyboard events.</summary>
    public byte Repeat => Read<byte>(37);
    /// <summary>The SDL_Keymod, for keyboard events.</summary>
    public ushort Mod => Read<ushort>(32);
    /// <summary>The x movement, for mouse motion.</summary>
    public float XRel => Read<float>(36);
    /// <summary>The y movement, for mouse motion.</summary>
    public float YRel => Read<float>(40);
    /// <summary>The button, for mouse button events: 1 left, 2 middle, 3 right.</summary>
    public byte Button => Read<byte>(24);
    /// <summary>Whether the button is down, for mouse button events.</summary>
    public byte ButtonDown => Read<byte>(25);
    /// <summary>The vertical wheel movement, for mouse wheel events.</summary>
    public float WheelY => Read<float>(28);
    /// <summary>The instance id, for joystick events.</summary>
    public uint JoyWhich => Read<uint>(16);
    /// <summary>The axis, hat or button index, for joystick events.</summary>
    public byte JoyIndex => Read<byte>(20);
    /// <summary>The SDL_HAT_* bits, for joystick hat events.</summary>
    public byte JoyHat => Read<byte>(21);
    /// <summary>Whether the button is down, for joystick button events.</summary>
    public byte JoyDown => Read<byte>(21);
    /// <summary>The axis value, -32768 to 32767, for joystick axis events.</summary>
    public short JoyAxisValue => Read<short>(24);

    private T Read<T>(int offset)
        where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.Read<T>(((ReadOnlySpan<byte>)_raw)[offset..]);
}

/// <summary>The 128 bytes of an SDL_Event.</summary>
[System.Runtime.CompilerServices.InlineArray(128)]
internal struct SdlEventBytes
{
    private byte _element;
}

