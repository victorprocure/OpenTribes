using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Platform.Audio;

namespace OpenTribes.Client.Audio;

/// <summary>One voice of the sound manager.</summary>
public sealed class SoundVoice
{
    /// <summary>The voice handle.</summary>
    public int Handle;
    /// <summary>The profile of the voice.</summary>
    public SoundProfile Profile;
    /// <summary>The explicit priority. 0 ranks by loudness.</summary>
    public double Priority;
    /// <summary>The wave name.</summary>
    public string Wave = "";
    /// <summary>The wave data.</summary>
    public WavData Data = null!;
    /// <summary>True when the gain follows 2.7 each frame.</summary>
    public bool Spatial;
    /// <summary>The source transform.</summary>
    public SoundXf Xf;
    /// <summary>The attenuation, 0 full and 1 silent.</summary>
    public double Attenuation;
    /// <summary>The pan.</summary>
    public double Pan;
    /// <summary>The start time in milliseconds.</summary>
    public long StartMs;
    /// <summary>True while the voice plays.</summary>
    public bool Playing;
    /// <summary>True when the voice waits for a free buffer.</summary>
    public bool WantsStart;
    /// <summary>True once the voice was stopped.</summary>
    public bool Dead;
    /// <summary>True when the position changed since the last gain update.</summary>
    public bool PosChanged = true;
    /// <summary>The mixer voice, or null.</summary>
    public MixVoice? Mix;
    /// <summary>True when the voice loops or streams.</summary>
    public bool Loops => Profile.Loops;
    /// <summary>The rank for the priority order: the explicit priority, else 1 - attenuation.</summary>
    public double Rank => Priority != 0 ? Priority : 1 - Attenuation;
}

/// <summary>The sound manager on the game thread: profile and pair lists, the voice list, handles, the per frame tick (priority order, admission of maxBuffers + 1 voices, suspend), the listener and the gain update.</summary>
/// <remarks>docs/audio.md 2. It drives a SoundMixer (Platform/Audio) and has no device of its own: <see cref="Mixer"/> == null with <see cref="DeviceOpen"/> true keeps the bookkeeping only.</remarks>
public sealed class SoundManager
{
    /// <summary>True when the output device is open.</summary>
    public bool DeviceOpen;
    /// <summary>True while muted: later plays return 0.</summary>
    public bool Muted;
    /// <summary>The number of mixer buffers (maxBuffers).</summary>
    public int MaxBuffers = 16;
    /// <summary>The 2D volume preference.</summary>
    public double Sfx2DVolume = 1;
    /// <summary>The 3D volume preference.</summary>
    public double Sfx3DVolume = 1;
    /// <summary>The sample rate of the output format.</summary>
    public int FormatRate = 11025;
    /// <summary>The bits per sample of the output format.</summary>
    public int FormatBits = 8;
    /// <summary>True for a stereo output format.</summary>
    public bool FormatStereo = true;
    /// <summary>The mixer, or null for bookkeeping only.</summary>
    public SoundMixer? Mixer;
    /// <summary>The datablocks of the client, for the datablock sounds.</summary>
    public ClientDataBlocks? DataBlocks;
    /// <summary>Reads the bytes of a wave file by bare name, or null (resource manager lookup, 1.1).</summary>
    public Func<string, byte[]?>? ReadWave;
    /// <summary>The current time in milliseconds.</summary>
    public long NowMs;

    /// <summary>The sound profiles by id.</summary>
    public readonly Dictionary<int, SoundProfile> Profiles = new();
    /// <summary>The sound pairs by id: the profile, the wave and the priority.</summary>
    public readonly Dictionary<int, (int Profile, string Wave, double Priority)> Pairs = new();
    private readonly List<SoundVoice> mVoices = new();
    private readonly Dictionary<int, SoundVoice> mByHandle = new();
    private readonly Dictionary<string, WavData?> mWaves = new(StringComparer.OrdinalIgnoreCase);
    private int mNextHandle;
    private Listener mListener = Listener.Identity;
    private bool mListenerMoved;

    /// <summary>The number of waves loaded, for the census and the offline report.</summary>
    public int WavesLoaded;
    /// <summary>The number of waves that failed to load.</summary>
    public int WaveFailures;
    /// <summary>The number of sounds played.</summary>
    public int Played;
    /// <summary>The number of sounds rejected.</summary>
    public int Rejected;
    /// <summary>The names of the waves that could not be found.</summary>
    public readonly HashSet<string> MissingWaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The voices in priority order.</summary>
    public IReadOnlyList<SoundVoice> Voices => mVoices;
    /// <summary>The listener.</summary>
    public Listener Listener => mListener;
    /// <summary>The number of voices that play.</summary>
    public int PlayingCount => mVoices.Count(v => v.Playing && !v.Dead);

    /// <summary>Answers the device query (2.2): the stock answers (D39).</summary>
    /// <param name="driver">The driver code.</param>
    /// <returns>True for driver 1 while the device is open.</returns>
    public bool Query(int driver) => driver == 1 && DeviceOpen;

    /// <summary>Moves the listener.</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="rx">The right axis x.</param>
    /// <param name="ry">The right axis y.</param>
    /// <param name="rz">The right axis z.</param>
    public void SetListener(double x, double y, double z, double rx, double ry, double rz)
    {
        mListener = new Listener { X = x, Y = y, Z = z, RightX = rx, RightY = ry, RightZ = rz };
        mListenerMoved = true;
    }


    /// <summary>Loads a wave once and caches it (1.1).</summary>
    /// <param name="name">The wave name.</param>
    /// <returns>The wave, or null when it is missing or invalid.</returns>
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


    private int NewHandle()
    {
        if (++mNextHandle <= 0)
            mNextHandle = 1;
        return mNextHandle;
    }

    /// <summary>Finds a voice.</summary>
    /// <param name="handle">The voice handle.</param>
    /// <returns>The voice, or null for an unknown handle.</returns>
    public SoundVoice? Find(int handle) => handle > 0 && mByHandle.TryGetValue(handle, out SoundVoice? v) ? v : null;

    /// <summary>Tells whether a voice finished or does not exist.</summary>
    /// <param name="handle">The voice handle.</param>
    /// <returns>True when the voice is gone.</returns>
    public bool IsDone(int handle) => Find(handle) is not { Dead: false };
    /// <summary>Tells whether a voice loops.</summary>
    /// <param name="handle">The voice handle.</param>
    /// <returns>True when the voice exists and loops.</returns>
    public bool IsLooping(int handle) => Find(handle) is { Loops: true };

    /// <summary>Stops a voice.</summary>
    /// <param name="handle">The voice handle.</param>
    public void Stop(int handle)
    {
        if (Find(handle) is SoundVoice v)
            Kill(v);
    }

    /// <summary>Stops every voice.</summary>
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

    /// <summary>Moves a voice.</summary>
    /// <param name="handle">The voice handle.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="rot">The nine rotation floats, or null to keep the current rotation.</param>
    public void SetPosition(int handle, double x, double y, double z, double[]? rot = null)
    {
        if (Find(handle) is not SoundVoice v)
            return;
        if (v.Xf.X != x || v.Xf.Y != y || v.Xf.Z != z)
            v.PosChanged = true;
        v.Xf = new SoundXf(x, y, z) { Rot = rot ?? v.Xf.Rot };
    }

    /// <summary>Sets the volume of a voice (sfxSetVolume volume handle, 2.8): attenuation = min(1, baseVolume + volume).</summary>
    /// <param name="handle">The voice handle.</param>
    /// <param name="volume">The volume.</param>
    public void SetVolume(int handle, double volume)
    {
        if (Find(handle) is SoundVoice v)
        {
            v.Attenuation = Math.Min(1, v.Profile.BaseVolume + volume);
            Apply(v);
        }
    }

    /// <summary>Sets the pan of a voice (sfxSetPan). It is stored as is and applied as pan * 10000.</summary>
    /// <param name="handle">The voice handle.</param>
    /// <param name="pan">The pan.</param>
    public void SetPan(int handle, double pan)
    {
        if (Find(handle) is SoundVoice v)
        {
            v.Pan = pan;
            Apply(v);
        }
    }

    /// <summary>Mutes or unmutes (sfxMute, 2.3). Muting stops every voice and later plays return 0.</summary>
    /// <param name="on">True to mute.</param>
    public void Mute(bool on)
    {
        Muted = on;
        if (on)
            StopAll();
    }

    /// <summary>Sets the primary volume (sfxSetVolume volume without a handle): volume * -10000 hundredths of a decibel.</summary>
    /// <param name="volume">The volume.</param>
    public void SetMasterVolume(double volume) => Mixer?.SetMaster((float)Math.Pow(10, Math.Clamp(Math.Truncate(volume * -10000), -10000, 0) / 2000.0));


    /// <summary>Plays a wave: GUI sounds and chat voices. 2D profiles take min(1, master(sfx2DVolume) + baseVolume) once.</summary>
    /// <param name="wave">The wave name.</param>
    /// <param name="profile">The profile, or null for the default profile.</param>
    /// <param name="priority">The explicit priority.</param>
    /// <param name="at">The source transform, or null for the origin.</param>
    /// <returns>The voice handle, or 0 when the sound did not start.</returns>
    public int PlayWave(string wave, SoundProfile? profile = null, double priority = 0, SoundXf? at = null)
    {
        SoundProfile p = profile ?? SoundProfile.Default;
        return Acquire(wave, p, priority, at ?? new SoundXf(0, 0, 0), datablock2D: false);
    }

    /// <summary>Plays a pair by id (sfxPlay, precipitation, sequences): the profile and wave of the pair.</summary>
    /// <param name="id">The pair id.</param>
    /// <param name="at">The source transform, or null for the origin (Q3).</param>
    /// <returns>The voice handle, or 0 when the sound did not start.</returns>
    public int PlayPair(int id, SoundXf? at = null)
    {
        if (!Pairs.TryGetValue(id, out var pair) || !Profiles.TryGetValue(pair.Profile, out SoundProfile p))
            return Reject();
        return Acquire(pair.Wave, p, pair.Priority, at ?? new SoundXf(0, 0, 0), datablock2D: false);
    }

    /// <summary>Plays a datablock sound (SoundData group 1, profile group 0, 00485600).</summary>
    /// <param name="index">The SoundData index.</param>
    /// <param name="at">The source transform.</param>
    /// <param name="priorityOverride">A priority that replaces the one of the datablock, or null.</param>
    /// <returns>The voice handle, or 0 for a missing block or wave.</returns>
    public int PlayDatablock(long index, SoundXf at, double? priorityOverride = null)
    {
        if (!TryDatablock(index, out string wave, out SoundProfile p, out double priority))
            return Reject();
        return Acquire(wave, p, priorityOverride ?? priority, at, datablock2D: true);
    }

    /// <summary>Reads the wave, profile and priority of a datablock sound.</summary>
    /// <param name="index">The SoundData index.</param>
    /// <param name="wave">The wave name.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True when the block exists.</returns>
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

    /// <summary>Builds the profile view of a SoundProfileData block (1.3), with defaults for the fields the server did not send.</summary>
    /// <param name="b">The block.</param>
    /// <returns>The profile.</returns>
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


    private static int Compare(SoundVoice a, SoundVoice b)
    {
        double pa = a.Rank, pb = b.Rank;
        if (pa != pb)
            return pa > pb ? -1 : 1;
        return a.StartMs == b.StartMs ? 0 : a.StartMs > b.StartMs ? -1 : 1;
    }

    /// <summary>Runs one frame: forgets finished voices, orders the rest by priority, admits voices up to the buffer limit and updates the gains (2.5 and 2.6).</summary>
    /// <param name="nowMs">The time in milliseconds.</param>
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
