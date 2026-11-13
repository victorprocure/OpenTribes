namespace OpenTribes.Platform.Audio;

using System.Threading;

/// <summary>One playing sound in a <see cref="SoundMixer"/>.</summary>
public sealed class MixVoice
{
    internal WavData Wave = null!;
    internal bool Loop;
    internal double Pos;               // source frames
    internal float TargetL, TargetR, CurL, CurR;
    internal bool Active;
    /// <summary>Gets whether the voice has finished or was stopped.</summary>
    public bool Done => !Active;
}

/// <summary>The mixer (docs/audio.md 4.2). Voices hold a decoded wave, a fractional source position, a loop flag and two gains.</summary>
/// <remarks>Each render call resamples to the device rate with linear interpolation, ramps gain changes over about 5 ms and sums into a stereo float buffer clipped at +-1. The game thread starts, stops and sets gains. The device callback or an offline caller renders. One lock guards the voice list, and the callback holds it for one block of a few thousand samples.</remarks>
public sealed class SoundMixer
{
    /// <summary>The number of voice slots.</summary>
    public const int Slots = 64;
    /// <summary>The device sample rate.</summary>
    public readonly int DeviceRate;
    private readonly Lock mLock = new();
    private readonly List<MixVoice> mVoices = [];
    private readonly int mRampFrames;
    private float mMaster = 1f;
    /// <summary>The number of frames rendered so far.</summary>
    public long FramesRendered;

    /// <summary>Creates a mixer.</summary>
    /// <param name="deviceRate">The device sample rate.</param>
    public SoundMixer(int deviceRate)
    {
        DeviceRate = deviceRate;
        mRampFrames = Math.Max(1, deviceRate * 5 / 1000);
    }

    /// <summary>Starts a voice at source position 0. The gains apply at once, with no ramp from silence.</summary>
    /// <param name="wave">The decoded wave.</param>
    /// <param name="loop">Whether the voice loops.</param>
    /// <param name="gainL">The left gain.</param>
    /// <param name="gainR">The right gain.</param>
    /// <returns>The voice, or null when all slots are in use.</returns>
    public MixVoice? Start(WavData wave, bool loop, float gainL, float gainR)
    {
        wave = wave.AtRate(DeviceRate, loop);
        lock (mLock)
        {
            if (mVoices.Count >= Slots)
                return null;
            var v = new MixVoice { Wave = wave, Loop = loop, Active = wave.Frames > 0, TargetL = gainL, TargetR = gainR, CurL = gainL, CurR = gainR };
            mVoices.Add(v);
            return v;
        }
    }

    /// <summary>Sets the target gains of a voice. The mixer ramps to them.</summary>
    /// <param name="v">The voice.</param>
    /// <param name="gainL">The left gain.</param>
    /// <param name="gainR">The right gain.</param>
    public void SetGains(MixVoice v, float gainL, float gainR)
    {
        lock (mLock)
        {
            v.TargetL = gainL;
            v.TargetR = gainR;
        }
    }

    /// <summary>Stops a voice.</summary>
    /// <param name="v">The voice.</param>
    public void Stop(MixVoice v)
    {
        lock (mLock)
            v.Active = false;
    }

    /// <summary>Sets the master gain.</summary>
    /// <param name="gain">The gain.</param>
    public void SetMaster(float gain)
    {
        lock (mLock)
            mMaster = gain;
    }

    /// <summary>Gets the number of active voices.</summary>
    public int ActiveCount
    {
        get
        {
            lock (mLock)
                return mVoices.Count(v => v.Active);
        }
    }

    /// <summary>Mixes stereo frames into a buffer, overwriting it.</summary>
    /// <param name="output">The buffer, at least 2 * frames values.</param>
    /// <param name="frames">The number of stereo frames.</param>
    public void Render(Span<float> output, int frames)
    {
        output[..(frames * 2)].Clear();
        lock (mLock)
        {
            foreach (var v in mVoices)
                if (v.Active)
                    MixOne(v, output, frames);
            mVoices.RemoveAll(v => !v.Active);
            var m = mMaster;
            for (var i = 0; i < frames * 2; ++i)
                output[i] = Math.Clamp(output[i] * m, -1f, 1f);
            FramesRendered += frames;
        }
    }

    private void MixOne(MixVoice v, Span<float> o, int frames)
    {
        var w = v.Wave;
        var step = (double)w.Rate / DeviceRate;
        var s = w.Samples;
        var ch = w.Channels;
        float dl = (v.TargetL - v.CurL) / mRampFrames, dr = (v.TargetR - v.CurR) / mRampFrames;
        var ramp = (v.TargetL != v.CurL || v.TargetR != v.CurR) ? mRampFrames : 0;
        for (var i = 0; i < frames; ++i)
        {
            var i0 = (int)v.Pos;
            if (i0 >= w.Frames)
            {
                if (!v.Loop)
                {
                    v.Active = false;
                    return;
                }
                v.Pos -= w.Frames * Math.Floor(v.Pos / w.Frames);
                i0 = (int)v.Pos;
            }
            var f = v.Pos - i0;
            var i1 = i0 + 1;
            if (i1 >= w.Frames)
                i1 = v.Loop ? 0 : i0;
            float l, r;
            if (ch == 1)
            {
                l = r = f == 0 ? s[i0] : (float)(s[i0] + ((s[i1] - s[i0]) * f));
            }
            else
            {
                l = f == 0 ? s[i0 * 2] : (float)(s[i0 * 2] + ((s[i1 * 2] - s[i0 * 2]) * f));
                r = f == 0 ? s[(i0 * 2) + 1] : (float)(s[(i0 * 2) + 1] + ((s[(i1 * 2) + 1] - s[(i0 * 2) + 1]) * f));
            }
            if (ramp > 0)
            {
                v.CurL = ramp == 1 ? v.TargetL : v.CurL + dl;
                v.CurR = ramp == 1 ? v.TargetR : v.CurR + dr;
                if (--ramp == 0)
                {
                    v.CurL = v.TargetL;
                    v.CurR = v.TargetR;
                }
            }
            o[i * 2] += l * v.CurL;
            o[(i * 2) + 1] += r * v.CurR;
            v.Pos += step;
        }
    }
}
