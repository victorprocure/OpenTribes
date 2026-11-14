namespace OpenTribes.Client.Audio;

/// <summary>The voice handles of the eight item image slots of one player ghost.</summary>
/// <remarks>docs/audio.md 3.2. The slot keeps the handle of every image sound, one shots included (+0x1a04), and the per frame update moves it to the player (0041aea0 Player_animateImage). A state change stops the voice only when it loops (flag +0x1a00, 0041aa40 Player_setImageState). Replacing or removing the image stops it always (0041a64c Player_resetImage).</remarks>
public sealed class ImageVoiceSlots
{
    /// <summary>The number of image slots.</summary>
    public const int Count = 8;
    private readonly int[] _handle = new int[Count];
    private readonly bool[] _loops = new bool[Count];

    /// <summary>The voice handle of each slot, 0 for none.</summary>
    public IReadOnlyList<int> Handles => _handle;

    /// <summary>Stores the voice a slot just started.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="handle">The voice handle, 0 when the sound did not start.</param>
    /// <param name="loops">True when the sound loops.</param>
    public void Started(int slot, int handle, bool loops)
    {
        if (slot is < 0 or >= Count)
            return;
        _handle[slot] = handle;
        _loops[slot] = handle != 0 && loops;
    }

    /// <summary>Takes the handle to stop on a state change of a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <returns>The handle, or 0 when none: only a looping voice is stopped, then forgotten.</returns>
    public int StateChanged(int slot)
    {
        if (slot is < 0 or >= Count || !_loops[slot])
            return 0;
        var h = _handle[slot];
        _handle[slot] = 0;
        _loops[slot] = false;
        return h;
    }

    /// <summary>Takes the handle to stop when the image of a slot is replaced or removed.</summary>
    /// <param name="slot">The image slot.</param>
    /// <returns>The handle, or 0 when none: the stored voice is stopped whether it loops or not, then forgotten.</returns>
    /// <remarks>Follows 0041a64c Player_resetImage, called by 0041a168 Player_setImage.</remarks>
    public int Reset(int slot)
    {
        if (slot is < 0 or >= Count)
            return 0;
        var h = _handle[slot];
        _handle[slot] = 0;
        _loops[slot] = false;
        return h;
    }

    /// <summary>Forgets every handle.</summary>
    public void Clear()
    {
        Array.Clear(_handle);
        Array.Clear(_loops);
    }
}
