// The audio side of a client run: the resources, a script interpreter with the sfx* commands, the SoundManager and a mixer.
// Offline sessions (Device == null) render to a buffer without SDL; live sessions feed the SDL3 stream (docs/audio.md 4.2).
using OpenTribes.Core;
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

namespace OpenTribes.Client.Audio;

public sealed class AudioSession : IDisposable
{
    public ResourceManager Res = null!;
    public Interpreter Interp = null!;
    public SoundManager Sound = null!;
    public SfxCommands Commands = null!;
    public SoundMixer? Mixer;
    public AudioOutput? Device;
    public readonly List<string> Log = new();
    public int OfflineRate = 48000;

    public static byte[]? ReadWave(ResourceManager res, string name) => GfxAssets.Read(res, name, out byte[] d) ? d : null;

    // `live` opens the SDL3 stream; otherwise the mixer is a plain offline one at 48000 Hz.
    public static AudioSession Create(string root, bool live, ResourceManager? res = null)
    {
        var s = new AudioSession();
        s.Res = res ?? GfxAssets.Mount(root, out _);
        s.Interp = new Interpreter();
        s.Interp.SetResources(s.Res);
        Builtins.RegisterBuiltins(s.Interp, s.Res, root);
        s.Interp.SetOutput(line => s.Log.Add(line));
        s.Sound = new SoundManager { ReadWave = n => ReadWave(s.Res, n) };
        s.Commands = new SfxCommands(s.Interp, s.Sound)
        {
            OpenDevice = () =>
            {
                if (live)
                {
                    s.Device ??= AudioOutput.Open();
                    if (!s.Device.Opened)
                        return false;
                    s.Mixer = s.Device.Mixer;
                }
                else
                    s.Mixer ??= new SoundMixer(s.OfflineRate);
                s.Sound.Mixer = s.Mixer;
                return true;
            },
            CloseDevice = () => { s.Sound.Mixer = null; },
        };
        return s;
    }

    // The stock start-up: the tag strings, then sound.cs (newSfx, sfxSetFormat, the 8 profiles and 89 pairs).
    public bool RunSoundScripts()
    {
        Interp.SetVariable("$dedicated", "false");
        bool a = Interp.ExecFile("sfx.strings.cs");
        bool b = Interp.ExecFile("sound.cs");
        return a && b && Sound.DeviceOpen;
    }

    // C6 stub replacement: the GUI plays its waves through the sound manager.
    public void AttachGui(OpenTribes.Gui.GuiHost host) => host.Audio = new GuiAudio(Sound);

    public void Dispose() => Device?.Dispose();
}
