// Voice handles of the eight item image slots of one player ghost (docs/audio.md 3.2). The slot keeps the handle of every image
// sound, one shots included (+0x1a04), and the per frame update moves it to the player [code 0041aea0 Player_animateImage].
// A state change or a reset stops the voice only when it loops (flag +0x1a00) [code 0041aa40 Player_setImageState].
namespace OpenTribes.Client.Audio;

public sealed class ImageVoiceSlots
{
    public const int Count = 8;
    private readonly int[] mHandle = new int[Count];
    private readonly bool[] mLoops = new bool[Count];

    public IReadOnlyList<int> Handles => mHandle;

    // Stores the voice a slot just started (0 when the sound did not start).
    public void Started(int slot, int handle, bool loops)
    {
        if (slot is < 0 or >= Count)
            return;
        mHandle[slot] = handle;
        mLoops[slot] = handle != 0 && loops;
    }

    // The handle to stop on a state change of the slot, or 0 when none: only a looping voice is stopped, then forgotten.
    public int StateChanged(int slot)
    {
        if (slot is < 0 or >= Count || !mLoops[slot])
            return 0;
        int h = mHandle[slot];
        mHandle[slot] = 0;
        mLoops[slot] = false;
        return h;
    }

    public void Clear()
    {
        Array.Clear(mHandle);
        Array.Clear(mLoops);
    }
}
