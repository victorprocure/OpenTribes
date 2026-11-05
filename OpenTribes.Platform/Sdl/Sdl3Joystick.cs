using System.Runtime.InteropServices;

namespace OpenTribes.Platform.Sdl;

// The SDL3 joystick subset: enumerate, open, close and the event codes. Values checked against SDL 3.4 (SDL_joystick.h,
// SDL_events.h, SDL_init.h). The device event fields are in SdlEvent.
public static partial class Sdl3
{
    public const uint InitJoystick = 0x200;
    public const uint EventJoyAxisMotion = 0x600, EventJoyHatMotion = 0x602, EventJoyButtonDown = 0x603, EventJoyButtonUp = 0x604,
        EventJoyRemoved = 0x606;

    [LibraryImport(Lib, EntryPoint = "SDL_GetJoysticks")]
    private static unsafe partial uint* GetJoysticksPtr(out int count);

    [LibraryImport(Lib, EntryPoint = "SDL_OpenJoystick")]
    public static partial IntPtr OpenJoystick(uint instanceId);

    [LibraryImport(Lib, EntryPoint = "SDL_CloseJoystick")]
    public static partial void CloseJoystick(IntPtr joystick);

    [LibraryImport(Lib, EntryPoint = "SDL_GetJoystickName")]
    private static partial IntPtr GetJoystickNamePtr(IntPtr joystick);

    public static string JoystickName(IntPtr joystick) => Marshal.PtrToStringUTF8(GetJoystickNamePtr(joystick)) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickAxes")]
    public static partial int GetNumJoystickAxes(IntPtr joystick);

    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickButtons")]
    public static partial int GetNumJoystickButtons(IntPtr joystick);

    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickHats")]
    public static partial int GetNumJoystickHats(IntPtr joystick);

    // The instance ids of the attached joysticks, in SDL's order.
    public static unsafe List<uint> GetJoystickIds()
    {
        var ids = new List<uint>();
        uint* p = GetJoysticksPtr(out int count);
        if (p == null)
            return ids;
        for (int i = 0; i < count; ++i)
            ids.Add(p[i]);
        Free((IntPtr)p);
        return ids;
    }
}
