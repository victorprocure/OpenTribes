// docs/audio.md 2.12: the sound sequence manager. Channel -1 plays the pair now; channels 0..31 queue sounds that play one after
// another. Tick forgets finished voices and starts the next queued entry of an idle channel.
namespace OpenTribes.Client.Audio;

public sealed class SoundSequencer
{
    private readonly SoundManager mSound;
    private readonly int[] mHandle = new int[32];
    private readonly Queue<(int Sound, SoundXf? Xf)>[] mQueue = new Queue<(int, SoundXf?)>[32];

    public SoundSequencer(SoundManager sound)
    {
        mSound = sound;
        for (int i = 0; i < 32; ++i)
            mQueue[i] = new Queue<(int, SoundXf?)>();
    }

    public void Add(int channel, int sound, SoundXf? xf)
    {
        if (channel < 0 || channel > 31)
            mSound.PlayPair(sound, xf);
        else
            mQueue[channel].Enqueue((sound, xf));
    }

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
