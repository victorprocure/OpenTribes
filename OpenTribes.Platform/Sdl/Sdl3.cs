using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenTribes.Platform.Sdl;

// Our own minimal SDL3 bindings (D30): the subset the client needs for init, a window with an OpenGL
// context, event polling, timing and the GL proc address. Values checked against SDL 3.4.18 headers.
public static partial class Sdl3
{
    private const string Lib = "SDL3";

    public const uint InitVideo = 0x20;
    public const ulong WindowOpenGL = 0x2;
    public const ulong WindowHidden = 0x8;
    public const ulong WindowResizable = 0x20;

    // SDL_GLAttr
    public const int GlRedSize = 0, GlGreenSize = 1, GlBlueSize = 2, GlAlphaSize = 3, GlDoubleBuffer = 5, GlDepthSize = 6,
        GlContextMajorVersion = 17, GlContextMinorVersion = 18, GlContextFlags = 19, GlContextProfileMask = 20;
    public const int GlProfileCore = 1;

    // SDL_EventType
    public const uint EventQuit = 0x100, EventWindowPixelSizeChanged = 0x207, EventWindowResized = 0x206,
        EventWindowCloseRequested = 0x210, EventKeyDown = 0x300, EventKeyUp = 0x301,
        EventWindowMinimized = 0x209, EventWindowRestored = 0x20B, EventWindowFocusGained = 0x20E, EventWindowFocusLost = 0x20F,
        EventMouseMotion = 0x400, EventMouseButtonDown = 0x401, EventMouseButtonUp = 0x402, EventMouseWheel = 0x403;

    public const uint KeyEscape = 27;

    static Sdl3()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sdl3).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib)
            return IntPtr.Zero;
        string[] candidates = PlatformPaths.SdlLibraryNames(PlatformPaths.CurrentOs);
        // The copy next to the executable wins over a system copy.
        foreach (string c in candidates)
            if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, c), out IntPtr hb))
                return hb;
        foreach (string c in candidates)
            if (NativeLibrary.TryLoad(c, assembly, path, out IntPtr h))
                return h;
        return IntPtr.Zero;
    }

    // Forces the static constructor (the resolver) before the first P/Invoke from other classes.
    public static void EnsureLoaded() { }

    [LibraryImport(Lib, EntryPoint = "SDL_GetError")]
    private static partial IntPtr GetErrorPtr();

    public static string GetError() => Marshal.PtrToStringUTF8(GetErrorPtr()) ?? "";

    // SDL_SetHintWithPriority; priority 2 is SDL_HINT_OVERRIDE, which wins over an environment variable.
    [LibraryImport(Lib, EntryPoint = "SDL_SetHintWithPriority", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetHintWithPriority(string name, string value, int priority);

    public static bool SetHintOverride(string name, string value) => SetHintWithPriority(name, value, 2);

    [LibraryImport(Lib, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentVideoDriver")]
    private static partial IntPtr GetCurrentVideoDriverPtr();

    public static string CurrentVideoDriver() => Marshal.PtrToStringUTF8(GetCurrentVideoDriverPtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr CreateWindow(string title, int w, int h, ulong flags);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_ShowWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ShowWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSizeInPixels(IntPtr window, out int w, out int h);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_SetAttribute")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSetAttribute(int attr, int value);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_CreateContext")]
    public static partial IntPtr GlCreateContext(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_DestroyContext")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlDestroyContext(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_MakeCurrent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlMakeCurrent(IntPtr window, IntPtr context);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_SwapWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSwapWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_SetSwapInterval")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GlSetSwapInterval(int interval);

    [LibraryImport(Lib, EntryPoint = "SDL_GL_GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GlGetProcAddress(string name);

    [LibraryImport(Lib, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool PollEvent(SdlEvent* ev);

    public static unsafe bool PollEvent(out SdlEvent ev)
    {
        SdlEvent e;
        bool got = PollEvent(&e);
        ev = e;
        return got;
    }

    [LibraryImport(Lib, EntryPoint = "SDL_GetTicksNS")]
    public static partial ulong GetTicksNS();

    [LibraryImport(Lib, EntryPoint = "SDL_GetPerformanceCounter")]
    public static partial ulong GetPerformanceCounter();

    [LibraryImport(Lib, EntryPoint = "SDL_GetPerformanceFrequency")]
    public static partial ulong GetPerformanceFrequency();

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowRelativeMouseMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowRelativeMouseMode(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool enabled);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowTitle", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowTitle(IntPtr window, string title);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowSize(IntPtr window, int w, int h);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowFullscreen")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowFullscreen(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool fullscreen);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowResizable")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowResizable(IntPtr window, [MarshalAs(UnmanagedType.U1)] bool resizable);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowFlags")]
    public static partial ulong GetWindowFlags(IntPtr window);

    public const ulong WindowFullscreen = 0x1;

    [LibraryImport(Lib, EntryPoint = "SDL_Delay")]
    public static partial void Delay(uint ms);

    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplayForWindow")]
    private static partial uint GetDisplayForWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetFullscreenDisplayModes")]
    private static unsafe partial IntPtr* GetFullscreenDisplayModes(uint displayId, out int count);

    [LibraryImport(Lib, EntryPoint = "SDL_free")]
    private static partial void Free(IntPtr mem);

    // The full screen modes of the display the window is on, as (width, height) pairs without repeats (one per refresh rate and
    // pixel format in SDL), ascending. Empty when SDL lists none. SDL_DisplayMode: displayID, format, w, h at offsets 0, 4, 8, 12.
    public static unsafe List<(int W, int H)> FullscreenModes(IntPtr window)
    {
        var modes = new List<(int, int)>();
        IntPtr* list = GetFullscreenDisplayModes(GetDisplayForWindow(window), out int count);
        if (list == null)
            return modes;
        for (int i = 0; i < count; ++i)
        {
            int* m = (int*)list[i];
            if (m[2] > 0 && m[3] > 0 && !modes.Contains((m[2], m[3])))
                modes.Add((m[2], m[3]));
        }
        Free((IntPtr)list);
        modes.Sort();
        return modes;
    }
}

// SDL_Event is a 128-byte union; only the fields the client reads are exposed.
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct SdlEvent
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(16)] public uint WindowId;      // window and keyboard events
    [FieldOffset(20)] public int Data1;          // window events: new width
    [FieldOffset(24)] public int Data2;          // window events: new height
    [FieldOffset(24)] public uint Scancode;      // keyboard events
    [FieldOffset(28)] public uint Key;           // keyboard events: SDL_Keycode
    [FieldOffset(36)] public byte Down;          // keyboard events
    [FieldOffset(37)] public byte Repeat;        // keyboard events
    [FieldOffset(32)] public ushort Mod;         // keyboard events: SDL_Keymod
    [FieldOffset(36)] public float XRel;         // mouse motion
    [FieldOffset(40)] public float YRel;         // mouse motion
    [FieldOffset(24)] public byte Button;        // mouse button events: 1 left, 2 middle, 3 right
    [FieldOffset(25)] public byte ButtonDown;    // mouse button events
    [FieldOffset(28)] public float WheelY;       // mouse wheel
}
