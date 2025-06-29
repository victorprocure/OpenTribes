using System.Runtime.InteropServices;

namespace OpenTribes.Platform.Sdl;

// The SDL3 audio subset (docs/audio.md 4.2): one playback stream in float 32 with a pull callback. Values checked against the
// SDL 3.4 headers (SDL_audio.h, SDL_init.h).
public static partial class Sdl3
{
    public const uint InitAudio = 0x10;
    public const uint AudioDeviceDefaultPlayback = 0xFFFFFFFF;
    public const uint AudioF32 = 0x8120;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioSpec
    {
        public uint Format;
        public int Channels;
        public int Freq;
    }

    [LibraryImport(Lib, EntryPoint = "SDL_InitSubSystem")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool InitSubSystem(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_QuitSubSystem")]
    public static partial void QuitSubSystem(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetHint(string name, string value);

    // SDL_OpenAudioDeviceStream(devid, spec, callback, userdata); the callback is a native function pointer
    // void (void* userdata, SDL_AudioStream* stream, int additional_amount, int total_amount).
    [LibraryImport(Lib, EntryPoint = "SDL_OpenAudioDeviceStream")]
    public static unsafe partial IntPtr OpenAudioDeviceStream(uint devid, AudioSpec* spec, IntPtr callback, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "SDL_ResumeAudioStreamDevice")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ResumeAudioStreamDevice(IntPtr stream);

    [LibraryImport(Lib, EntryPoint = "SDL_PutAudioStreamData")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool PutAudioStreamData(IntPtr stream, void* buf, int len);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyAudioStream")]
    public static partial void DestroyAudioStream(IntPtr stream);

    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioStreamDevice")]
    public static partial uint GetAudioStreamDevice(IntPtr stream);

    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioDeviceFormat")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool GetAudioDeviceFormat(uint devid, AudioSpec* spec, int* sampleFrames);

    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioDeviceName")]
    private static partial IntPtr GetAudioDeviceNamePtr(uint devid);

    public static string AudioDeviceName(uint devid) => Marshal.PtrToStringUTF8(GetAudioDeviceNamePtr(devid)) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentAudioDriver")]
    private static partial IntPtr GetCurrentAudioDriverPtr();

    public static string CurrentAudioDriver() => Marshal.PtrToStringUTF8(GetCurrentAudioDriverPtr()) ?? "";
}
