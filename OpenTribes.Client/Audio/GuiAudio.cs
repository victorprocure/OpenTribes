// docs/audio.md 3.5: GUI sounds are wave plays with the static 2D profile (flags 0, base volume 0) and priority 0, so the 2D pref applies.
using OpenTribes.Gui;

namespace OpenTribes.Client.Audio;

public sealed class GuiAudio : IGuiAudio
{
    private readonly SoundManager mSound;
    public GuiAudio(SoundManager sound) => mSound = sound;
    public void Play(string name) => mSound.PlayWave(name);
}
