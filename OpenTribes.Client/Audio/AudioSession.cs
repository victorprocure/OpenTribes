using OpenTribes.Core;
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

namespace OpenTribes.Client.Audio;

/// <summary>The audio side of a client run: the resources, a script interpreter with the sfx* commands, the <see cref="SoundManager"/> and a mixer.</summary>
/// <remarks>Offline sessions (<see cref="Device"/> is null) render to a buffer without SDL. Live sessions feed the SDL3 stream (docs/audio.md 4.2).</remarks>
public sealed class AudioSession : IDisposable
{
    /// <summary>The mounted resources.</summary>
    public ResourceManager Res = null!;
    /// <summary>The script interpreter that runs the sound scripts.</summary>
    public Interpreter Interp = null!;
    /// <summary>The sound manager.</summary>
    public SoundManager Sound = null!;
    /// <summary>The sfx* console commands.</summary>
    public SfxCommands Commands = null!;
    /// <summary>The mixer the sound manager plays on.</summary>
    public SoundMixer? Mixer;
    /// <summary>The SDL3 output, or null for an offline session.</summary>
    public AudioOutput? Device;
    /// <summary>The lines the commands printed.</summary>
    public readonly List<string> Log = new();
    /// <summary>The sample rate of an offline mixer.</summary>
    public int OfflineRate = 48000;

    /// <summary>Reads a wave resource.</summary>
    /// <param name="res">The mounted resources.</param>
    /// <param name="name">The wave name.</param>
    /// <returns>The file bytes, or null when the wave is missing.</returns>
    public static byte[]? ReadWave(ResourceManager res, string name) => GfxAssets.Read(res, name, out byte[] d) ? d : null;

    /// <summary>Creates a session.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="live">True to open the SDL3 stream. Otherwise the mixer is a plain offline one at 48000 Hz.</param>
    /// <param name="res">The resources to use, or null to mount them from the root.</param>
    /// <returns>The session.</returns>
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

    /// <summary>Runs the stock start-up: the tag strings, then sound.cs (newSfx, sfxSetFormat, the 8 profiles and 89 pairs).</summary>
    /// <returns>True when the scripts ran.</returns>
    public bool RunSoundScripts()
    {
        Interp.SetVariable("$dedicated", "false");
        bool a = Interp.ExecFile("sfx.strings.cs");
        bool b = Interp.ExecFile("sound.cs");
        return a && b && Sound.DeviceOpen;
    }

    /// <summary>Lets the GUI play its waves through the sound manager.</summary>
    /// <param name="host">The GUI host.</param>
    public void AttachGui(OpenTribes.Gui.GuiHost host) => host.Audio = new GuiAudio(Sound);

    /// <inheritdoc/>
    public void Dispose() => Device?.Dispose();
}
