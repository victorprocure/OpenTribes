namespace OpenTribes.Client.Audio;

using OpenTribes.Gui;

/// <summary>Plays the sounds of the GUI screens through the sound manager.</summary>
/// <remarks>Follows docs/audio.md 3.5: GUI sounds are wave plays with the static 2D profile (flags 0, base volume 0) and priority 0, so the 2D pref applies.</remarks>
public sealed class GuiAudio : IGuiAudio
{
    private readonly SoundManager _sound;
    /// <summary>Creates the player.</summary>
    /// <param name="sound">The sound manager the waves play on.</param>
    public GuiAudio(SoundManager sound) => _sound = sound;
    /// <inheritdoc/>
    public void Play(string name) => _sound.PlayWave(name);
}
