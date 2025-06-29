// docs/audio.md 4.2: the SDL3 audio stream. One float 32 stereo playback stream at the device rate; the callback pulls a block
// from the mixer. No device (or no SDL audio driver) leaves Device false and the caller runs silent (2.1).
using System.Runtime.InteropServices;
using OpenTribes.Platform.Sdl;

namespace OpenTribes.Platform.Audio;

public sealed unsafe class AudioOutput : IDisposable
{
    private static AudioOutput? sActive;
    private IntPtr mStream;
    public SoundMixer? Mixer { get; private set; }
    public string Description { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool Opened => mStream != IntPtr.Zero;
    public int DeviceRate { get; private set; }
    private float[] mBlock = new float[8192];

    public static AudioOutput Open(int preferredRate = 48000)
    {
        var o = new AudioOutput();
        Sdl3.EnsureLoaded();
        if (!Sdl3.InitSubSystem(Sdl3.InitAudio))
        {
            o.Error = "SDL audio init failed: " + Sdl3.GetError();
            return o;
        }
        Sdl3.AudioSpec want = new() { Format = Sdl3.AudioF32, Channels = 2, Freq = preferredRate };
        sActive = o;
        IntPtr cb = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, int, void>)&Callback;
        // The mixer must exist before the device starts pulling; its rate is the one we ask for (SDL converts if the device differs).
        o.Mixer = new SoundMixer(preferredRate);
        o.mStream = Sdl3.OpenAudioDeviceStream(Sdl3.AudioDeviceDefaultPlayback, &want, cb, IntPtr.Zero);
        if (o.mStream == IntPtr.Zero)
        {
            o.Error = "SDL_OpenAudioDeviceStream failed: " + Sdl3.GetError();
            o.Mixer = null;
            sActive = null;
            return o;
        }
        o.DeviceRate = preferredRate;
        uint dev = Sdl3.GetAudioStreamDevice(o.mStream);
        Sdl3.AudioSpec have = default;
        int frames = 0;
        string fmt = Sdl3.GetAudioDeviceFormat(dev, &have, &frames) ? $"{have.Freq} Hz, {have.Channels} ch, format 0x{have.Format:x4}, {frames} frame buffer" : "format unknown";
        o.Description = $"driver {Sdl3.CurrentAudioDriver()}, device \"{Sdl3.AudioDeviceName(dev)}\" ({fmt}); stream float32 stereo {preferredRate} Hz";
        Sdl3.ResumeAudioStreamDevice(o.mStream);
        return o;
    }

    [UnmanagedCallersOnly]
    private static void Callback(IntPtr userdata, IntPtr stream, int additional, int total)
    {
        AudioOutput? o = sActive;
        if (o == null || o.Mixer == null)
            return;
        int frames = additional / 8;
        while (frames > 0)
        {
            int n = Math.Min(frames, o.mBlock.Length / 2);
            o.Mixer.Render(o.mBlock, n);
            fixed (float* p = o.mBlock)
                Sdl3.PutAudioStreamData(stream, p, n * 8);
            frames -= n;
        }
    }

    public void Dispose()
    {
        if (mStream != IntPtr.Zero)
        {
            Sdl3.DestroyAudioStream(mStream);
            mStream = IntPtr.Zero;
        }
        if (sActive == this)
            sActive = null;
    }
}
