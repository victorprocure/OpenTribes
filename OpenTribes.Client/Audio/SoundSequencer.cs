namespace OpenTribes.Client.Audio;

/// <summary>The sound sequence manager: channels 0 to 31 queue sounds that play one after another.</summary>
/// <remarks>docs/audio.md 2.12. Channel -1 plays the pair now. <see cref="Tick"/> forgets finished voices and starts the next queued entry of an idle channel.</remarks>
public sealed class SoundSequencer
{
    private readonly SoundManager mSound;
    private readonly int[] mHandle = new int[32];
    private readonly Queue<(int Sound, SoundXf? Xf)>[] mQueue = new Queue<(int, SoundXf?)>[32];

    /// <summary>Creates the sequencer.</summary>
    /// <param name="sound">The sound manager the sounds play on.</param>
    public SoundSequencer(SoundManager sound)
    {
        mSound = sound;
        for (int i = 0; i < 32; ++i)
            mQueue[i] = new Queue<(int, SoundXf?)>();
    }

    /// <summary>Queues a sound on a channel, or plays it now for a channel outside 0 to 31.</summary>
    /// <param name="channel">The channel, or -1 to play now.</param>
    /// <param name="sound">The sound id.</param>
    /// <param name="xf">The 3D position, or null for a 2D sound.</param>
    public void Add(int channel, int sound, SoundXf? xf)
    {
        if (channel < 0 || channel > 31)
            mSound.PlayPair(sound, xf);
        else
            mQueue[channel].Enqueue((sound, xf));
    }

    /// <summary>Forgets finished voices and starts the next queued sound on each idle channel.</summary>
    public void Tick()
    {
        for (int c = 0; c < 32; ++c)
        {
            if (mHandle[c] != 0 && mSound.IsDone(mHandle[c]))
                mHandle[c] = 0;
            if (mHandle[c] == 0 && mQueue[c].Count > 0)
            {
                var (s, xf) = mQueue[c].Dequeue();
                mHandle[c] = mSound.PlayPair(s, xf);
            }
        }
    }
}
