namespace OpenTribes.Platform.Sdl;

using System.Runtime.InteropServices;

/// <summary>The SDL3 audio subset (docs/audio.md 4.2): one playback stream in float32 with a pull callback.</summary>
/// <remarks>Values were checked against the SDL 3.4 headers (SDL_audio.h, SDL_init.h).</remarks>
public static partial class Sdl3
{
    /// <summary>The SDL_INIT_AUDIO flag.</summary>
    public const uint InitAudio = 0x10;
    /// <summary>The SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK device id.</summary>
    public const uint AudioDeviceDefaultPlayback = 0xFFFFFFFF;
    /// <summary>The SDL_AUDIO_F32 format.</summary>
    public const uint AudioF32 = 0x8120;

    /// <summary>The SDL_AudioSpec structure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioSpec
    {
        /// <summary>The sample format.</summary>
        public uint Format;
        /// <summary>The channel count.</summary>
        public int Channels;
        /// <summary>The sample rate.</summary>
        public int Freq;
    }

    /// <summary>Initialises a subsystem (SDL_InitSubSystem).</summary>
    /// <param name="flags">The subsystem flags.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_InitSubSystem")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool InitSubSystem(uint flags);

    /// <summary>Shuts a subsystem down (SDL_QuitSubSystem).</summary>
    /// <param name="flags">The subsystem flags.</param>
    [LibraryImport(Lib, EntryPoint = "SDL_QuitSubSystem")]
    public static partial void QuitSubSystem(uint flags);

    /// <summary>Sets a hint (SDL_SetHint).</summary>
    /// <param name="name">The hint name.</param>
    /// <param name="value">The hint value.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetHint(string name, string value);

    /// <summary>Opens a playback device with a stream (SDL_OpenAudioDeviceStream).</summary>
    /// <param name="devid">The device id.</param>
    /// <param name="spec">The wanted format.</param>
    /// <param name="callback">A native function pointer <c>void (void* userdata, SDL_AudioStream* stream, int additional_amount, int total_amount)</c>.</param>
    /// <param name="userdata">The user data passed to the callback.</param>
    /// <returns>The stream, or zero on failure.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_OpenAudioDeviceStream")]
    public static unsafe partial IntPtr OpenAudioDeviceStream(uint devid, AudioSpec* spec, IntPtr callback, IntPtr userdata);

    /// <summary>Starts the stream's device (SDL_ResumeAudioStreamDevice).</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_ResumeAudioStreamDevice")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ResumeAudioStreamDevice(IntPtr stream);

    /// <summary>Queues data on a stream (SDL_PutAudioStreamData).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="buf">The data.</param>
    /// <param name="len">The data length in bytes.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_PutAudioStreamData")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool PutAudioStreamData(IntPtr stream, void* buf, int len);

    /// <summary>Destroys a stream and its device (SDL_DestroyAudioStream).</summary>
    /// <param name="stream">The stream.</param>
    [LibraryImport(Lib, EntryPoint = "SDL_DestroyAudioStream")]
    public static partial void DestroyAudioStream(IntPtr stream);

    /// <summary>Gets the device of a stream (SDL_GetAudioStreamDevice).</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The device id.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioStreamDevice")]
    public static partial uint GetAudioStreamDevice(IntPtr stream);

    /// <summary>Gets the preferred format of a device (SDL_GetAudioDeviceFormat).</summary>
    /// <param name="devid">The device id.</param>
    /// <param name="spec">Receives the format.</param>
    /// <param name="sampleFrames">Receives the device buffer size in sample frames.</param>
    /// <returns>True on success.</returns>
    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioDeviceFormat")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool GetAudioDeviceFormat(uint devid, AudioSpec* spec, int* sampleFrames);

    [LibraryImport(Lib, EntryPoint = "SDL_GetAudioDeviceName")]
    private static partial IntPtr GetAudioDeviceNamePtr(uint devid);

    /// <summary>Gets the name of an audio device.</summary>
    /// <param name="devid">The device id.</param>
    /// <returns>The name, empty when none.</returns>
    public static string AudioDeviceName(uint devid) => Marshal.PtrToStringUTF8(GetAudioDeviceNamePtr(devid)) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentAudioDriver")]
    private static partial IntPtr GetCurrentAudioDriverPtr();

    /// <summary>Gets the name of the current audio driver.</summary>
    /// <returns>The name, empty when none.</returns>
    public static string CurrentAudioDriver() => Marshal.PtrToStringUTF8(GetCurrentAudioDriverPtr()) ?? "";
}
