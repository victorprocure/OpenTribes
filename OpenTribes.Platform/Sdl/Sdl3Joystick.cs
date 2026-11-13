using System.Runtime.InteropServices;

namespace OpenTribes.Platform.Sdl;

// The SDL3 joystick subset: enumerate, open, close and the event codes. Values checked against SDL 3.4 (SDL_joystick.h,
// SDL_events.h, SDL_init.h). The device event fields are in SdlEvent.
public static partial class Sdl3
{
    /// <summary>The SDL_INIT_JOYSTICK flag.</summary>
    public const uint InitJoystick = 0x200;
    /// <summary>The SDL joystick event types: axis motion, hat motion, button down, button up and device removed.</summary>
    public const uint EventJoyAxisMotion = 0x600, EventJoyHatMotion = 0x602, EventJoyButtonDown = 0x603, EventJoyButtonUp = 0x604,
        EventJoyRemoved = 0x606;

    [LibraryImport(Lib, EntryPoint = "SDL_GetJoysticks")]
    private static unsafe partial uint* GetJoysticksPtr(out int count);

    /// <summary>Opens a joystick (SDL_OpenJoystick).</summary>
    /// <param name="instanceId">The SDL instance id.</param>
    /// <returns>The joystick, or zero on failure.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_OpenJoystick")]
    public static partial IntPtr OpenJoystick(uint instanceId);

    /// <summary>Closes a joystick (SDL_CloseJoystick).</summary>
    /// <param name="joystick">The joystick.</param>
    [LibraryImport(Lib, EntryPoint = "SDL_CloseJoystick")]
    public static partial void CloseJoystick(IntPtr joystick);

    [LibraryImport(Lib, EntryPoint = "SDL_GetJoystickName")]
    private static partial IntPtr GetJoystickNamePtr(IntPtr joystick);

    /// <summary>Gets the name of a joystick.</summary>
    /// <param name="joystick">The joystick.</param>
    /// <returns>The name, empty when none.</returns>
    public static string JoystickName(IntPtr joystick) => Marshal.PtrToStringUTF8(GetJoystickNamePtr(joystick)) ?? "";

    /// <summary>Gets the number of axes of a joystick (SDL_GetNumJoystickAxes).</summary>
    /// <param name="joystick">The joystick.</param>
    /// <returns>The count.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickAxes")]
    public static partial int GetNumJoystickAxes(IntPtr joystick);

    /// <summary>Gets the number of buttons of a joystick (SDL_GetNumJoystickButtons).</summary>
    /// <param name="joystick">The joystick.</param>
    /// <returns>The count.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickButtons")]
    public static partial int GetNumJoystickButtons(IntPtr joystick);

    /// <summary>Gets the number of hats of a joystick (SDL_GetNumJoystickHats).</summary>
    /// <param name="joystick">The joystick.</param>
    /// <returns>The count.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetNumJoystickHats")]
    public static partial int GetNumJoystickHats(IntPtr joystick);

    /// <summary>Lists the instance ids of the attached joysticks, in SDL's order.</summary>
    /// <returns>The ids.</returns>
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
