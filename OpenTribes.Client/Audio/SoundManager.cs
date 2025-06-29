// docs/audio.md 2: the sound manager on the game thread. Profile and pair lists, the voice list, handles, the per-frame
// tick (priority order, admission of maxBuffers + 1 voices, suspend), the listener and the gain update. It drives a
// SoundMixer (Platform/Audio) and has no device of its own: Mixer == null with DeviceOpen true keeps the bookkeeping only.
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Platform.Audio;

namespace OpenTribes.Client.Audio;

public sealed class SoundVoice
{
    public int Handle;
    public SoundProfile Profile;
    public double Priority;            // explicit priority; 0 ranks by loudness
    public string Wave = "";
    public WavData Data = null!;
    public bool Spatial;               // gain from 2.7 each frame
    public SoundXf Xf;
    public double Attenuation;         // 0 full, 1 silent
    public double Pan;
    public long StartMs;
    public bool Playing, WantsStart, Dead, PosChanged = true;
    public MixVoice? Mix;
    public bool Loops => Profile.Loops;
    public double Rank => Priority != 0 ? Priority : 1 - Attenuation;
}

public sealed class SoundManager
{
    public bool DeviceOpen;
    public bool Muted;
    public int MaxBuffers = 16;
    public double Sfx2DVolume = 1, Sfx3DVolume = 1;
    public int FormatRate = 11025, FormatBits = 8;
    public bool FormatStereo = true;
    public SoundMixer? Mixer;
    public ClientDataBlocks? DataBlocks;
    // The bytes of a wave file by bare name, or null (resource manager lookup, 1.1).
    public Func<string, byte[]?>? ReadWave;
    public long NowMs;

    public readonly Dictionary<int, SoundProfile> Profiles = new();
    public readonly Dictionary<int, (int Profile, string Wave, double Priority)> Pairs = new();
    private readonly List<SoundVoice> mVoices = new();
    private readonly Dictionary<int, SoundVoice> mByHandle = new();
    private readonly Dictionary<string, WavData?> mWaves = new(StringComparer.OrdinalIgnoreCase);
    private int mNextHandle;
    private Listener mListener = Listener.Identity;
    private bool mListenerMoved;

    // Statistics for the census and the offline report.
    public int WavesLoaded, WaveFailures, Played, Rejected;
    public readonly HashSet<string> MissingWaves = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SoundVoice> Voices => mVoices;
    public Listener Listener => mListener;
    public int PlayingCount => mVoices.Count(v => v.Playing && !v.Dead);

    // 2.2: the stock answers (D39).
    public bool Query(int driver) => driver == 1 && DeviceOpen;

    public void SetListener(double x, double y, double z, double rx, double ry, double rz)
    {
        mListener = new Listener { X = x, Y = y, Z = z, RightX = rx, RightY = ry, RightZ = rz };
        mListenerMoved = true;
    }

    // ---- waves (1.1) ----

    public WavData? LoadWave(string name)
    {
        if (mWaves.TryGetValue(name, out WavData? w))
            return w;
        byte[]? bytes = ReadWave?.Invoke(name);
        w = bytes == null ? null : WavData.TryParse(bytes);
        mWaves[name] = w;
        if (w == null)
        {
            ++WaveFailures;
            MissingWaves.Add(name);
        }
        else
            ++WavesLoaded;
        return w;
    }

    // ---- handles (2.3) ----

    private int NewHandle()
    {
        if (++mNextHandle <= 0)
            mNextHandle = 1;
        return mNextHandle;
    }

    public SoundVoice? Find(int handle) => handle > 0 && mByHandle.TryGetValue(handle, out SoundVoice? v) ? v : null;

    public bool IsDone(int handle) => Find(handle) is not { Dead: false };
    public bool IsLooping(int handle) => Find(handle) is { Loops: true };

    public void Stop(int handle)
    {
        if (Find(handle) is SoundVoice v)
            Kill(v);
    }

    public void StopAll()
    {
        foreach (SoundVoice v in mVoices)
            Kill(v);
    }

    private void Kill(SoundVoice v)
    {
        if (v.Mix != null)
            Mixer?.Stop(v.Mix);
        v.Mix = null;
        v.Playing = false;
        v.WantsStart = false;
        v.Dead = true;
    }

    public void SetPosition(int handle, double x, double y, double z, double[]? rot = null)
    {
        if (Find(handle) is not SoundVoice v)
            return;
        if (v.Xf.X != x || v.Xf.Y != y || v.Xf.Z != z)
            v.PosChanged = true;
        v.Xf = new SoundXf(x, y, z) { Rot = rot ?? v.Xf.Rot };
    }

    // sfxSetVolume volume handle (2.8): attenuation = min(1, baseVolume + volume).
    public void SetVolume(int handle, double volume)
    {
        if (Find(handle) is SoundVoice v)
        {
            v.Attenuation = Math.Min(1, v.Profile.BaseVolume + volume);
            Apply(v);
        }
    }

    // sfxSetPan: stored as is, applied as pan * 10000.
    public void SetPan(int handle, double pan)
    {
        if (Find(handle) is SoundVoice v)
        {
            v.Pan = pan;
            Apply(v);
        }
    }

    // sfxMute (2.3): stops every voice; later plays return 0.
    public void Mute(bool on)
    {
        Muted = on;
        if (on)
            StopAll();
    }

    // sfxSetVolume volume without a handle: the primary volume is volume * -10000 hundredths of a decibel.
    public void SetMasterVolume(double volume) => Mixer?.SetMaster((float)Math.Pow(10, Math.Clamp(Math.Truncate(volume * -10000), -10000, 0) / 2000.0));

    // ---- play entry points (2.4, 2.8) ----

    // Wave play: GUI sounds and chat voices. 2D profiles take min(1, master(sfx2DVolume) + baseVolume) once.
    public int PlayWave(string wave, SoundProfile? profile = null, double priority = 0, SoundXf? at = null)
    {
        SoundProfile p = profile ?? SoundProfile.Default;
        return Acquire(wave, p, priority, at ?? new SoundXf(0, 0, 0), datablock2D: false);
    }

    // Pair id play (sfxPlay, precipitation, sequences): the pair's profile and wave, optional transform (Q3: origin without one).
    public int PlayPair(int id, SoundXf? at = null)
    {
        if (!Pairs.TryGetValue(id, out var pair) || !Profiles.TryGetValue(pair.Profile, out SoundProfile p))
            return Reject();
        return Acquire(pair.Wave, p, pair.Priority, at ?? new SoundXf(0, 0, 0), datablock2D: false);
    }

    // Datablock sound (SoundData group 1, profile group 0) [code 00485600]. Returns 0 for a missing block or wave.
    public int PlayDatablock(long index, SoundXf at, double? priorityOverride = null)
    {
        if (!TryDatablock(index, out string wave, out SoundProfile p, out double priority))
            return Reject();
        return Acquire(wave, p, priorityOverride ?? priority, at, datablock2D: true);
    }

    public bool TryDatablock(long index, out string wave, out SoundProfile profile, out double priority)
    {
        wave = "";
        profile = SoundProfile.Default;
        priority = 0;
        ClientDataBlock? sd = Block(1, index);
        if (sd == null)
            return false;
        wave = Field(sd, "wavFileName") as string ?? "";
        priority = ToD(Field(sd, "priority"));
        ClientDataBlock? pb = Block(0, (long)ToD(Field(sd, "profile")));
        if (pb == null || wave.Length == 0)
            return false;
        profile = ProfileOf(pb);
        return true;
    }

    // The profile view of a SoundProfileData block (1.3): defaults for the fields the server did not send.
    public static SoundProfile ProfileOf(ClientDataBlock b)
    {
        SoundProfile p = SoundProfile.Default;
        p.Flags = (uint)ToD(Field(b, "flags"));
        p.BaseVolume = ToD(Field(b, "baseVolume"));
        if (Field(b, "coneInsideAngle") != null) p.ConeInside = (int)ToD(Field(b, "coneInsideAngle"));
        if (Field(b, "coneOutsideAngle") != null) p.ConeOutside = (int)ToD(Field(b, "coneOutsideAngle"));
        if (Field(b, "coneVector") is double[] cv && cv.Length >= 3) { p.ConeX = cv[0]; p.ConeY = cv[1]; p.ConeZ = cv[2]; }
        if (Field(b, "coneOutsideVolume") != null) p.ConeOutsideVolume = ToD(Field(b, "coneOutsideVolume"));
        if (Field(b, "minDistance") != null) p.MinDist = ToD(Field(b, "minDistance"));
        if (Field(b, "maxDistance") != null) p.MaxDist = ToD(Field(b, "maxDistance"));
        return p;
    }

    private ClientDataBlock? Block(int group, long index) =>
        DataBlocks != null && DataBlocks.Blocks.TryGetValue((group, (int)index), out ClientDataBlock? b) ? b : null;

    private static object? Field(ClientDataBlock b, string name)
    {
        foreach (DecodedField f in b.Fields)
            if (f.Name == name)
                return f.Value;
        return null;
    }

    private static double ToD(object? o) => o switch
    {
        double d => d, long l => l, int i => i, float f => f, uint u => u, _ => 0,
    };

    private int Reject()
    {
        ++Rejected;
        return 0;
    }

    // 2.4 Sfx_acquireVoice. Dead-voice reuse and device buffer creation are Internal and not modelled.
    private int Acquire(string wave, SoundProfile p, double priority, SoundXf at, bool datablock2D)
    {
        if (!DeviceOpen || Muted)
            return Reject();
        if (p.Is3D && !p.Loops)
        {
            double dx = at.X - mListener.X, dy = at.Y - mListener.Y, dz = at.Z - mListener.Z;
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > p.MaxDist)
                return Reject();
        }
        WavData? data = LoadWave(wave);
        if (data == null)
            return Reject();
        var v = new SoundVoice
        {
            Handle = NewHandle(), Profile = p, Priority = priority, Wave = wave, Data = data, Xf = at, StartMs = NowMs,
            Spatial = p.Is3D, WantsStart = true,
        };
        // 2.8: wave and pair plays fix the 2D attenuation at start; a datablock 2D sound is at full volume.
        if (!p.Is3D)
            v.Attenuation = datablock2D ? 0 : SoundGain.Attenuation2D(Sfx2DVolume, p.BaseVolume);
        mVoices.Add(v);
        mByHandle[v.Handle] = v;
        ++Played;
        StartVoice(v);
        return v.Handle;
    }

    // 2.4 step 5: recompute gain and pan, play from sample 0; a silent voice (attenuation 1) is not started.
    private void StartVoice(SoundVoice v)
    {
        if (v.Mix != null)
            Mixer?.Stop(v.Mix);
        v.Mix = null;
        Recompute(v);
        if (v.Attenuation >= 1)
        {
            v.Playing = false;
            v.WantsStart = true;
            return;
        }
        var (l, r) = SoundGain.Gains(v.Attenuation, v.Pan);
        v.Mix = Mixer?.Start(v.Data, v.Loops, (float)l, (float)r);
        v.Playing = true;
        v.WantsStart = false;
        v.StartMs = NowMs;
    }

    private void Recompute(SoundVoice v)
    {
        if (!v.Spatial)
            return;
        v.Attenuation = SoundGain.Attenuation3D(v.Profile, v.Xf, mListener, Sfx3DVolume, out v.Pan);
        v.PosChanged = false;
    }

    private void Apply(SoundVoice v)
    {
        if (v.Mix == null || Mixer == null)
            return;
        var (l, r) = SoundGain.Gains(v.Attenuation, v.Pan);
        Mixer.SetGains(v.Mix, (float)l, (float)r);
    }

    // ---- the per-frame tick (2.5, 2.6) ----

    private static int Compare(SoundVoice a, SoundVoice b)
    {
        double pa = a.Rank, pb = b.Rank;
        if (pa != pb)
            return pa > pb ? -1 : 1;
        return a.StartMs == b.StartMs ? 0 : a.StartMs > b.StartMs ? -1 : 1;
    }

    public void Tick(long nowMs)
    {
        NowMs = nowMs;
        foreach (SoundVoice v in mVoices)
            if (v.Playing && !v.Dead && !v.Loops && v.Mix != null && v.Mix.Done)
                Kill(v);
        Borland.BorlandQsort<SoundVoice>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mVoices), Compare);
        int n = 0;
        foreach (SoundVoice v in mVoices)
        {
            if (v.Dead)
                continue;
            if (v.Playing)
            {
                double pr = v.Rank;
                if (pr * pr < 1e-12 || n > MaxBuffers)
                    Suspend(v);
                else
                    ++n;
            }
            else if (v.WantsStart && n <= MaxBuffers)
            {
                StartVoice(v);
                if (v.Playing)
                    ++n;
            }
        }
        // 2.6 update: gain and pan of every voice when the listener moved or the voice moved.
        foreach (SoundVoice v in mVoices)
        {
            if (v.Dead || !v.Spatial || !(mListenerMoved || v.PosChanged))
                continue;
            Recompute(v);
            if (v.Playing)
                Apply(v);
        }
        mListenerMoved = false;
        for (int i = mVoices.Count - 1; i >= 0; --i)
            if (mVoices[i].Dead)
            {
                mByHandle.Remove(mVoices[i].Handle);
                mVoices.RemoveAt(i);
            }
    }

    // 2.5 step 3: a looping voice keeps "wants to start" and stops; a one-shot dies.
    private void Suspend(SoundVoice v)
    {
        if (v.Mix != null)
            Mixer?.Stop(v.Mix);
        v.Mix = null;
        v.Playing = false;
        if (v.Loops)
            v.WantsStart = true;
        else
            v.Dead = true;
    }
}
