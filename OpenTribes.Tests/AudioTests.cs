// Unit tests of the client sound system (docs/audio.md, ROADMAP M9 C7) on synthetic waves and tables (R5, R9).
using System.Text;
using OpenTribes.Client.Audio;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Audio;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class AudioTests
{
    // ---- synthetic files ----

    private static byte[] Chunk(string id, byte[] body)
    {
        var l = new List<byte>(Encoding.ASCII.GetBytes(id));
        l.AddRange(BitConverter.GetBytes((uint)body.Length));
        l.AddRange(body);
        if (body.Length % 2 == 1)
            l.Add(0);
        return l.ToArray();
    }

    private static byte[] Fmt(int tag, int channels, int rate, int bits, int size = 16)
    {
        var l = new List<byte>();
        l.AddRange(BitConverter.GetBytes((ushort)tag));
        l.AddRange(BitConverter.GetBytes((ushort)channels));
        l.AddRange(BitConverter.GetBytes((uint)rate));
        l.AddRange(BitConverter.GetBytes((uint)(rate * channels * bits / 8)));
        l.AddRange(BitConverter.GetBytes((ushort)(channels * bits / 8)));
        l.AddRange(BitConverter.GetBytes((ushort)bits));
        while (l.Count < size)
            l.Add(0);
        return l.ToArray();
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("WAVE"));
        foreach (byte[] c in chunks)
            body.AddRange(c);
        var l = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
        l.AddRange(BitConverter.GetBytes((uint)body.Count));
        l.AddRange(body);
        return l.ToArray();
    }

    private static byte[] Pcm16(params short[] s) => s.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Wave16(int rate, params short[] s) => Riff(Chunk("fmt ", Fmt(1, 1, rate, 16)), Chunk("data", Pcm16(s)));

    // ---- 1.1 wave decoding ----

    [Fact]
    public void Decodes8BitMono() // 1.1
    {
        WavData? w = WavData.TryParse(Riff(Chunk("fmt ", Fmt(1, 1, 11025, 8)), Chunk("data", new byte[] { 128, 255, 0, 192 })));
        Assert.NotNull(w);
        Assert.Equal((1, 11025, 8, 4), (w.Channels, w.Rate, w.Bits, w.Frames));
        Assert.Equal(new[] { 0f, 127 / 128f, -1f, 0.5f }, w.Samples);
    }

    [Fact]
    public void Decodes16BitMono() // 1.1
    {
        WavData w = WavData.TryParse(Wave16(22050, 0, 16384, -32768, 32767))!;
        Assert.Equal((1, 22050, 16, 4), (w.Channels, w.Rate, w.Bits, w.Frames));
        Assert.Equal(new[] { 0f, 0.5f, -1f, 32767 / 32768f }, w.Samples);
    }

    [Fact]
    public void Decodes8BitStereoInterleaved() // 1.1
    {
        WavData w = WavData.TryParse(Riff(Chunk("fmt ", Fmt(1, 2, 11025, 8)), Chunk("data", new byte[] { 128, 0, 192, 255 })))!;
        Assert.Equal((2, 2), (w.Channels, w.Frames));
        Assert.Equal(new[] { 0f, -1f, 0.5f, 127 / 128f }, w.Samples);
    }

    [Fact]
    public void Decodes16BitStereoInterleaved() // 1.1
    {
        WavData w = WavData.TryParse(Riff(Chunk("fmt ", Fmt(1, 2, 22050, 16)), Chunk("data", Pcm16(8192, -8192, 16384, 0))))!;
        Assert.Equal((2, 2), (w.Channels, w.Frames));
        Assert.Equal(new[] { 0.25f, -0.25f, 0.5f, 0f }, w.Samples);
    }

    [Fact]
    public void SkipsExtraChunksWithEvenPadding() // 1.1: LIST, fact, cue, an odd sized chunk, an 18 byte fmt
    {
        byte[] file = Riff(Chunk("fmt ", Fmt(1, 1, 22050, 16, 18)), Chunk("LIST", new byte[] { 1, 2, 3 }), Chunk("fact", new byte[4]),
            Chunk("data", Pcm16(100, -100)), Chunk("smpl", new byte[36]));
        WavData w = WavData.TryParse(file)!;
        Assert.Equal(2, w.Frames);
        Assert.Equal(100 / 32768f, w.Samples[0]);
    }

    [Fact]
    public void FormatTagIsNotChecked() // 1.1: the tag and the channel count are not tested by the loader
    {
        Assert.NotNull(WavData.TryParse(Riff(Chunk("fmt ", Fmt(0x55, 1, 22050, 16)), Chunk("data", Pcm16(1, 2)))));
    }

    [Fact]
    public void TruncatedDataKeepsWholeFrames() // 1.1
    {
        byte[] file = Riff(Chunk("fmt ", Fmt(1, 1, 22050, 16)), Chunk("data", Pcm16(1, 2, 3)));
        Array.Resize(ref file, file.Length - 1);
        // The data chunk now claims more bytes than the file holds; the declared size runs past the end.
        Assert.Equal(2, WavData.TryParse(file)!.Frames);
    }

    [Fact]
    public void RejectsBadFiles() // 1.1: a failed load makes the play call return 0
    {
        Assert.Null(WavData.TryParse(Encoding.ASCII.GetBytes("RIFX....WAVE")));
        Assert.Null(WavData.TryParse(Riff(Chunk("data", Pcm16(1)), Chunk("fmt ", Fmt(1, 1, 22050, 16)))));        // fmt after data
        Assert.Null(WavData.TryParse(Riff(Chunk("fmt ", new byte[12]), Chunk("data", Pcm16(1)))));                // fmt shorter than 14 bytes
        Assert.Null(WavData.TryParse(Riff(Chunk("fmt ", Fmt(1, 1, 22050, 24)), Chunk("data", new byte[6]))));      // 24 bit
        Assert.Null(WavData.TryParse(Riff(Chunk("fmt ", Fmt(1, 1, 22050, 16)))));                                  // no data
        Assert.Null(WavData.TryParse(new byte[5]));
    }

    // ---- 2.7 gain, 2.8 prefs, 2.11 pan ----

    [Fact]
    public void PrefVolumeMapping() // 2.7 pref to attenuation
    {
        Assert.Equal(0, SoundGain.Master(1), 12);
        Assert.Equal(0.069, SoundGain.Master(0.5), 12);
        Assert.Equal(0.069 * Math.Log2(100), SoundGain.Master(0.01), 12);   // 0.458
        Assert.Equal(SoundGain.Master(0.01), SoundGain.Master(0.0005), 12); // floor at 0.01
        Assert.Equal(0.458, SoundGain.Master(0.01), 3);
        // F3: the slider floor is -45.8 dB (device units: trunc(-10000 * 0.4584) = -4584).
        Assert.Equal(-4584, SoundGain.DeviceVolume(SoundGain.Master(0.01)));
    }

    [Fact]
    public void DeviceUnits() // 2.7 device units
    {
        Assert.Equal(-690, SoundGain.DeviceVolume(0.069));
        Assert.Equal(Math.Pow(10, -690 / 2000.0), SoundGain.DeviceGain(0.069), 12);
        Assert.Equal(-10000, SoundGain.DeviceVolume(1));
        Assert.Equal(-10000, SoundGain.DeviceVolume(7));
        Assert.Equal(1.0, SoundGain.DeviceGain(0), 12);
        Assert.Equal(1e-5, SoundGain.DeviceGain(1), 12);
    }

    private static SoundProfile P3D(double min, double max, double b = 0) =>
        new() { Flags = SoundProfile.Software3D, MinDist = min, MaxDist = max, BaseVolume = b, ConeInside = 90, ConeOutside = 180, ConeY = -1 };

    [Fact]
    public void LinearInDecibelsBetweenMinAndMax() // 2.7 step 4, D39
    {
        SoundProfile p = P3D(5, 500);
        Listener l = Listener.Identity;
        double a(double d) => SoundGain.Attenuation3D(p, new SoundXf(0, d, 0), l, 1, out _);
        Assert.Equal(0, a(5), 12);
        Assert.Equal(0, a(1), 12);
        Assert.Equal(0.5, a(252.5), 12);             // the spec's own example
        Assert.Equal(1, a(500), 12);                 // silent at maxDist
        Assert.Equal(1, a(900), 12);
        Assert.Equal(-5000, SoundGain.DeviceVolume(a(252.5)));   // -50 dB at the midpoint of a 5 to 500 m profile
        // Base volume joins before the distance term: a = base + (1 - base) * t.
        Assert.Equal(0.2 + 0.8 * 0.5, SoundGain.Attenuation3D(P3D(10, 110, 0.2), new SoundXf(0, 60, 0), l, 1, out _), 12);
        // The 3D pref adds master(v).
        Assert.Equal(0.069, SoundGain.Attenuation3D(p, new SoundXf(0, 2, 0), l, 0.5, out _), 12);
    }

    [Fact]
    public void PanFormula() // 2.7 step 1: 0.2 * dot(right, source - listener) / d
    {
        Listener l = new() { X = 10, Y = 20, Z = 5, RightX = 1 };
        SoundProfile p = P3D(100, 200);
        SoundGain.Attenuation3D(p, new SoundXf(20, 20, 5), l, 1, out double pan);
        Assert.Equal(0.2, pan, 12);                  // 10 m to the right
        SoundGain.Attenuation3D(p, new SoundXf(0, 20, 5), l, 1, out pan);
        Assert.Equal(-0.2, pan, 12);
        SoundGain.Attenuation3D(p, new SoundXf(10, 50, 5), l, 1, out pan);
        Assert.Equal(0, pan, 12);
        SoundGain.Attenuation3D(p, new SoundXf(20, 30, 5), l, 1, out pan);
        Assert.Equal(0.2 * Math.Sqrt(0.5), pan, 12); // 45 degrees
        SoundGain.Attenuation3D(p, new SoundXf(10, 20, 5), l, 1, out pan);
        Assert.Equal(0, pan, 12);                    // d = 0 becomes 1e-5, the dot is 0
    }

    [Fact]
    public void ListenerRightFromYaw() // 2.6: yaw 0 looks along +Y, positive turns left
    {
        var (x, y, _) = SoundGain.RightOfYaw(0);
        Assert.Equal((1, 0), (x, y));
        (x, y, _) = SoundGain.RightOfYaw(Math.PI / 2);   // looking along -X, right is +Y
        Assert.Equal(0, x, 12);
        Assert.Equal(1, y, 12);
    }

    [Fact]
    public void DevicePanLaw() // 2.11: pan is hundredths of a dB on the far side
    {
        var (l, r) = SoundGain.PanGains(0.2);
        Assert.Equal(0.1, l, 12);                    // -20 dB on the left
        Assert.Equal(1, r, 12);
        (l, r) = SoundGain.PanGains(-0.2);
        Assert.Equal((1, 0.1), (l, r));
        Assert.Equal((1.0, 1.0), SoundGain.PanGains(0));
        (l, r) = SoundGain.PanGains(1);              // sfxSetPan 1.0: -100 dB
        Assert.Equal(1e-5, l, 12);
    }

    [Fact]
    public void ConeTerm() // 2.7 step 3
    {
        var p = P3D(100, 200);
        p.ConeOutsideVolume = 0.6;
        p.ConeX = 0; p.ConeY = 1; p.ConeZ = 0;
        Listener l = Listener.Identity;
        double a(double lx, double ly) => SoundGain.Attenuation3D(p, new SoundXf(0, 0, 0), new Listener { X = lx, Y = ly, RightX = 1 }, 1, out _);
        Assert.Equal(0, a(0, 10), 12);               // in front of the cone: inside
        Assert.Equal(0.05, a(0, -10), 12);           // behind: coneOutsideVolume / 12
        Assert.Equal(0.05, a(10, 0), 12);            // side: s = 0.5 = outside (180 / 360)
        p.ConeOutside = 270;
        Assert.Equal(0.6 / 12 * (0.5 - 0.25) / (0.75 - 0.25), a(10, 0), 12);   // between the angles
        // The sound's rotation turns the cone: a quarter turn about Z moves +Y to -X.
        var rot = new[] { 0.0, 1, 0, -1, 0, 0, 0, 0, 1 };
        double b = SoundGain.Attenuation3D(p, new SoundXf(0, 0, 0) { Rot = rot }, new Listener { X = -10, RightX = 1 }, 1, out _);
        Assert.Equal(0, b, 12);
    }

    // ---- mixer ----

    private static void Render(SoundMixer m, float[] buf, int frames) => m.Render(buf, frames);

    [Fact]
    public void Mixer2DUnityGivesExactSamples() // 4.2: mono at the device rate, unity gain, both channels
    {
        WavData w = WavData.TryParse(Wave16(22050, 0, 16384, -16384, 32767, -32768, 8192))!;
        var m = new SoundMixer(22050);
        m.Start(w, false, 1f, 1f);
        var o = new float[16];
        Render(m, o, 8);
        float[] expect = { 0, 0.5f, -0.5f, 32767 / 32768f, -1f, 0.25f };
        for (int i = 0; i < expect.Length; ++i)
            Assert.Equal((expect[i], expect[i]), (o[i * 2], o[i * 2 + 1]));
        Assert.Equal((0f, 0f), (o[12], o[13]));      // after the end: silence, the voice is gone
        Assert.Equal(0, m.ActiveCount);
    }

    [Fact]
    public void MixerSineToneAtUnityIsExact() // 4.2: a synthetic tone comes out sample for sample
    {
        var s = new short[200];
        for (int i = 0; i < s.Length; ++i)
            s[i] = (short)Math.Round(20000 * Math.Sin(2 * Math.PI * 440 * i / 22050));
        var m = new SoundMixer(22050);
        m.Start(WavData.TryParse(Wave16(22050, s))!, false, 1f, 1f);
        var o = new float[400];
        Render(m, o, 200);
        for (int i = 0; i < s.Length; ++i)
            Assert.Equal(s[i] / 32768f, o[i * 2]);
    }

    [Fact]
    public void MixerAppliesGainsAndClips()
    {
        WavData w = WavData.TryParse(Wave16(22050, 16384, 16384))!;
        var m = new SoundMixer(22050);
        m.Start(w, false, 0.5f, 0.25f);
        m.Start(w, false, 1f, 1f);
        m.Start(w, false, 1f, 1f);
        var o = new float[8];
        Render(m, o, 2);
        Assert.Equal(1f, o[0]);                      // 0.25 + 0.5 + 0.5 = 1.25 clips at 1
        Assert.Equal(1f, o[1]);                      // right: 0.125 + 0.5 + 0.5 = 1.125, clipped
    }

    [Fact]
    public void MixerResamplesBandLimited() // 4.2: 11025 Hz to 22050 Hz doubles the frames, windowed sinc (WavData.AtRate), symmetric around the peak
    {
        var m = new SoundMixer(22050);
        m.Start(WavData.TryParse(Wave16(11025, 0, 16384, 0))!, false, 1f, 1f);
        var o = new float[12];
        Render(m, o, 6);
        Assert.InRange(o[4], 0.47f, 0.5f);
        Assert.Equal(o[2], o[6], 4);
        Assert.InRange(o[2], 0.25f, 0.4f);
    }

    [Fact]
    public void MixerLoopsWholeWave() // 1.4: no loop points, the whole data chunk repeats
    {
        var m = new SoundMixer(22050);
        m.Start(WavData.TryParse(Wave16(22050, 1000, 2000, 3000))!, true, 1f, 1f);
        var o = new float[16];
        Render(m, o, 8);
        Assert.Equal(new[] { 1000, 2000, 3000, 1000, 2000, 3000, 1000, 2000 }, Enumerable.Range(0, 8).Select(i => (int)Math.Round(o[i * 2] * 32768)));
    }

    [Fact]
    public void MixerRampsGainChanges() // 4.2: about 5 ms, no step
    {
        var m = new SoundMixer(48000);
        short[] ones = Enumerable.Repeat((short)16384, 2000).ToArray();
        MixVoice v = m.Start(WavData.TryParse(Wave16(48000, ones))!, false, 1f, 1f)!;
        m.SetGains(v, 0f, 0f);
        var o = new float[2 * 400];
        Render(m, o, 400);
        Assert.True(o[0] < 0.5f && o[0] > 0.45f);    // first sample already moved one step down
        Assert.Equal(0f, o[2 * 399]);                // after 240 frames of ramp the gain is zero
        for (int i = 1; i < 240; ++i)
            Assert.True(o[i * 2] <= o[(i - 1) * 2]);
    }

    // ---- sound manager ----

    private static SoundManager Mgr(int rate = 22050, int frames = 4000, short level = 8192, int maxBuffers = 16, bool loopWave = false)
    {
        var s = new short[frames];
        for (int i = 0; i < frames; ++i)
            s[i] = (short)(level + i % 7);
        byte[] bytes = Wave16(rate, s);
        return new SoundManager { DeviceOpen = true, Mixer = new SoundMixer(rate), MaxBuffers = maxBuffers, ReadWave = n => n.StartsWith("miss") ? null : bytes };
    }

    private static SoundProfile P2D(bool loop = false) => new() { Flags = loop ? SoundProfile.Looping : 0, MinDist = 30, MaxDist = 300 };

    [Fact]
    public void AdmissionKeepsMaxBuffersPlusOneByPriority() // 2.5, D41
    {
        SoundManager sm = Mgr(maxBuffers: 3);
        var h = new int[8];
        for (int i = 0; i < 8; ++i)
        {
            sm.NowMs = 100 + i;
            h[i] = sm.PlayWave("a.wav", P2D(), 0.1 * (i + 1));
        }
        Assert.All(h, x => Assert.True(x > 0));
        sm.Tick(200);
        Assert.Equal(4, sm.PlayingCount);            // maxBuffers + 1
        for (int i = 0; i < 8; ++i)
            Assert.Equal(i >= 4, !sm.IsDone(h[i])); // the four highest priorities live; the others are one-shots and died
    }

    [Fact]
    public void EqualPriorityLaterStartWins() // 2.5 step 1
    {
        SoundManager sm = Mgr(maxBuffers: 1);
        var h = new int[5];
        for (int i = 0; i < 5; ++i)
        {
            sm.NowMs = 10 * (i + 1);
            h[i] = sm.PlayWave("a.wav", P2D(), 0.5);
        }
        sm.Tick(100);
        Assert.Equal(new[] { true, true, true, false, false }, h.Select(x => sm.IsDone(x)).ToArray());
    }

    [Fact]
    public void ExplicitPriorityBeatsLoudness() // 2.5: priority 0 voices rank by 1 - attenuation, below any explicit priority >= that rank
    {
        SoundManager sm = Mgr(maxBuffers: 0);
        sm.SetListener(0, 0, 0, 1, 0, 0);
        int near = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(0, 10, 0));
        int far = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(0, 300, 0));
        int explicitLow = sm.PlayWave("a.wav", P2D(), 0.001);
        sm.Tick(5);
        Assert.False(sm.IsDone(near));               // 1 - a is about 0.99, beats the explicit 0.001 and the far voice
        Assert.True(sm.IsDone(far));
        Assert.True(sm.IsDone(explicitLow));
        Assert.Equal(1, sm.PlayingCount);
    }

    [Fact]
    public void LoudnessRankingBetweenPriorityZeroVoices() // 2.5
    {
        SoundManager sm = Mgr(maxBuffers: 1);
        int a = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(0, 200, 0));
        int b = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(0, 50, 0));
        int c = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(0, 100, 0));
        sm.Tick(1);
        Assert.Equal(new[] { true, false, false }, new[] { a, b, c }.Select(x => sm.IsDone(x)).ToArray());
        // The two nearest survive; the voice at 200 m was cut.
    }

    [Fact]
    public void SuspendedLoopResumesAtSampleZero() // 2.5 step 3, D41
    {
        SoundManager sm = Mgr(maxBuffers: 0, level: 8000);
        int loop = sm.PlayWave("a.wav", P2D(loop: true), 0.2);
        sm.Tick(1);
        var o = new float[2 * 500];
        sm.Mixer!.Render(o, 500);                    // the loop has advanced to sample 500
        int shot = sm.PlayWave("a.wav", P2D(), 0.9);
        sm.Tick(2);
        Assert.False(sm.IsDone(loop));               // suspended, not dead
        Assert.Equal(1, sm.PlayingCount);
        sm.Stop(shot);
        sm.Tick(3);
        Assert.Equal(1, sm.PlayingCount);            // admitted again
        sm.Mixer.Render(o, 1);
        Assert.Equal(8000 / 32768f, o[0]);           // sample 0 of the wave (level + 0)
    }

    [Fact]
    public void OneShotCutByLimitIsGone() // 2.5
    {
        SoundManager sm = Mgr(maxBuffers: 0);
        int lo = sm.PlayWave("a.wav", P2D(), 0.1);
        int hi = sm.PlayWave("a.wav", P2D(), 0.9);
        sm.Tick(1);
        Assert.True(sm.IsDone(lo));
        sm.Stop(hi);
        sm.Tick(2);
        Assert.True(sm.IsDone(lo));                  // never comes back
        Assert.Equal(0, sm.PlayingCount);
    }

    [Fact]
    public void NonLoopingBeyondMaxDistNeverStarts() // 2.4 step 2
    {
        SoundManager sm = Mgr();
        sm.SetListener(0, 0, 0, 1, 0, 0);
        Assert.Equal(0, sm.PlayWave("a.wav", P3D(5, 100), 0, new SoundXf(0, 100.5, 0)));
        Assert.Empty(sm.Voices);
        Assert.True(sm.PlayWave("a.wav", P3D(5, 100), 0, new SoundXf(0, 99, 0)) > 0);
    }

    [Fact]
    public void LoopingBeyondMaxDistIsCreatedSilentAndStartsWhenInRange() // 2.4 step 2, 2.10
    {
        SoundManager sm = Mgr();
        sm.SetListener(0, 0, 0, 1, 0, 0);
        SoundProfile loop = P3D(5, 100);
        loop.Flags |= SoundProfile.Looping;
        int h = sm.PlayWave("a.wav", loop, 0, new SoundXf(0, 250, 0));
        Assert.True(h > 0);
        Assert.False(sm.IsDone(h));
        Assert.Equal(0, sm.PlayingCount);
        sm.Tick(1);
        Assert.Equal(0, sm.PlayingCount);
        sm.SetListener(0, 200, 0, 1, 0, 0);          // listener moves into range (50 m)
        sm.Tick(2);
        Assert.Equal(1, sm.PlayingCount);
        Assert.Equal(SoundGain.Attenuation3D(loop, new SoundXf(0, 250, 0), sm.Listener, 1, out _), sm.Find(h)!.Attenuation, 12);
    }

    [Fact]
    public void StaleHandlesDoNothing() // 2.3
    {
        SoundManager sm = Mgr();
        int h = sm.PlayWave("a.wav", P2D(), 0.5);
        sm.Stop(h);
        sm.Tick(1);
        Assert.True(sm.IsDone(h));
        sm.SetPosition(h, 1, 2, 3);
        sm.SetPan(h, 0.5);
        sm.Stop(h);
        Assert.Empty(sm.Voices);
        Assert.True(sm.IsDone(0));
        int h2 = sm.PlayWave("a.wav", P2D(), 0.5);
        Assert.True(h2 > h);                          // handles are positive and increase
    }

    [Fact]
    public void MissingWaveOrMutedOrNoDeviceReturnsZero() // 1.1, 2.3, 2.4 step 1, F1
    {
        SoundManager sm = Mgr();
        Assert.Equal(0, sm.PlayWave("miss1.wav"));
        sm.Mute(true);
        Assert.Equal(0, sm.PlayWave("a.wav"));
        sm.Mute(false);
        Assert.True(sm.PlayWave("a.wav") > 0);
        sm.DeviceOpen = false;
        Assert.Equal(0, sm.PlayWave("a.wav"));
        Assert.Equal(0, sm.PlayPair(12345));
    }

    [Fact]
    public void MuteStopsEveryVoice() // 2.3
    {
        SoundManager sm = Mgr();
        int h = sm.PlayWave("a.wav", P2D(loop: true));
        sm.Mute(true);
        sm.Tick(1);
        Assert.True(sm.IsDone(h));
        Assert.Equal(0, sm.Mixer!.ActiveCount);
    }

    [Fact]
    public void TwoDPrefAppliesOnceAtStartOnly() // 2.8, D42
    {
        SoundManager sm = Mgr();
        sm.Sfx2DVolume = 0.5;
        int h = sm.PlayWave("a.wav", P2D());
        Assert.Equal(0.069, sm.Find(h)!.Attenuation, 12);
        sm.Sfx2DVolume = 0.01;
        sm.Tick(1);
        Assert.Equal(0.069, sm.Find(h)!.Attenuation, 12);   // not updated while the voice plays
        SoundProfile withBase = P2D();
        withBase.BaseVolume = 0.9;
        Assert.Equal(1.0, sm.Find(sm.PlayWave("a.wav", withBase))!.Attenuation, 12);   // min(1, ...)
    }

    [Fact]
    public void ThreeDPrefIsLive() // 2.8
    {
        SoundManager sm = Mgr();
        sm.SetListener(0, 0, 0, 1, 0, 0);
        int h = sm.PlayWave("a.wav", P3D(50, 500), 0, new SoundXf(0, 10, 0));
        Assert.Equal(0, sm.Find(h)!.Attenuation, 12);
        sm.Sfx3DVolume = 0.5;
        sm.SetListener(0, 0, 0, 1, 0, 0);            // listener moved bit
        sm.Tick(1);
        Assert.Equal(0.069, sm.Find(h)!.Attenuation, 12);
    }

    [Fact]
    public void ThreeDVoiceFollowsPositionAndListener() // 2.6
    {
        SoundManager sm = Mgr();
        sm.SetListener(0, 0, 0, 1, 0, 0);
        int h = sm.PlayWave("a.wav", P3D(5, 500), 0, new SoundXf(10, 0, 0));
        Assert.Equal(0.2, sm.Find(h)!.Pan, 12);
        sm.SetPosition(h, -10, 0, 0);
        sm.Tick(1);
        Assert.Equal(-0.2, sm.Find(h)!.Pan, 12);
        sm.SetListener(-10, -3, 0, 1, 0, 0);
        sm.Tick(2);
        Assert.Equal(0, sm.Find(h)!.Pan, 12);
    }

    [Fact]
    public void SilentVoiceIsNotStartedButKeepsWantsToStart() // 2.4 step 5
    {
        SoundManager sm = Mgr();
        SoundProfile p = P2D();
        p.BaseVolume = 2;
        int h = sm.PlayWave("a.wav", p);
        Assert.False(sm.Find(h)!.Playing);
        Assert.True(sm.Find(h)!.WantsStart);
        Assert.Equal(0, sm.Mixer!.ActiveCount);
    }

    [Fact]
    public void PlaysThroughTheMixerWithPanGains() // 2.11 end to end: unity 2D, then panned 3D
    {
        SoundManager sm = Mgr(level: 16000);
        sm.PlayWave("a.wav", P2D(), 0);
        sm.Sfx2DVolume = 1;
        var o = new float[4];
        sm.Mixer!.Render(o, 1);
        Assert.Equal(16000 / 32768f, o[0]);
        Assert.Equal(o[0], o[1]);
        sm.StopAll();
        sm.Tick(1);
        sm.SetListener(0, 0, 0, 1, 0, 0);
        sm.PlayWave("a.wav", P3D(50, 500), 0, new SoundXf(10, 0, 0));
        sm.Mixer.Render(o, 1);
        Assert.Equal(16000 / 32768f * 0.1f, o[0], 6);   // left side attenuated 20 dB
        Assert.Equal(16000 / 32768f, o[1], 6);
    }

    // ---- datablocks (1.3, 8 D42) ----

    private static ClientDataBlocks Blocks()
    {
        var b = new ClientDataBlocks();
        b.Blocks[(0, 0)] = new ClientDataBlock { Group = 0, Index = 0, Fields = new List<DecodedField>
        {
            new("flags", 0x20L), new("baseVolume", 0.0), new("minDistance", 5.0), new("maxDistance", 40.0),
        } };
        b.Blocks[(0, 1)] = new ClientDataBlock { Group = 0, Index = 1, Fields = new List<DecodedField> { new("flags", 0L), new("baseVolume", 0.5) } };
        b.Blocks[(1, 0)] = new ClientDataBlock { Group = 1, Index = 0, Fields = new List<DecodedField> { new("wavFileName", "a.wav"), new("priority", 0.0), new("profile", 0L) } };
        b.Blocks[(1, 1)] = new ClientDataBlock { Group = 1, Index = 1, Fields = new List<DecodedField> { new("wavFileName", "a.wav"), new("priority", 0.0), new("profile", 1L) } };
        b.Blocks[(1, 2)] = new ClientDataBlock { Group = 1, Index = 2, Fields = new List<DecodedField> { new("wavFileName", "miss.wav"), new("priority", 0.0), new("profile", 0L) } };
        return b;
    }

    [Fact]
    public void DatablockSoundsUseTheProfileDefaultsAndStay2DFullVolume() // 1.3, 2.8, D42
    {
        SoundManager sm = Mgr();
        sm.DataBlocks = Blocks();
        sm.SetListener(0, 0, 0, 1, 0, 0);
        sm.Sfx2DVolume = 0.01;
        Assert.True(sm.TryDatablock(0, out _, out SoundProfile p, out _));
        Assert.Equal((5.0, 40.0, 90, 180), (p.MinDist, p.MaxDist, p.ConeInside, p.ConeOutside));
        Assert.True(p.Is3D);
        Assert.Equal(0, sm.PlayDatablock(0, new SoundXf(0, 41, 0)));      // beyond the profile maxDist
        Assert.True(sm.PlayDatablock(0, new SoundXf(0, 20, 0)) > 0);
        int h2d = sm.PlayDatablock(1, new SoundXf(0, 0, 0));              // 2D profile: baseVolume 0.5 is ignored, no pref
        Assert.Equal(0, sm.Find(h2d)!.Attenuation);
        Assert.Equal(0, sm.PlayDatablock(2, new SoundXf(0, 1, 0)));       // missing wave: silent
        Assert.Equal(0, sm.PlayDatablock(9, new SoundXf(0, 1, 0)));       // no such SoundData
    }

    // ---- sfx commands (client-scripts.md 2.8, F2) ----

    [Fact]
    public void SfxCommandsBuildProfilesAndPairsAndStaySilentOnMissingWaves() // 2.8, F1, F2
    {
        SoundManager sm = Mgr();
        sm.DeviceOpen = false;
        var interp = new Interpreter();
        var lines = new List<string>();
        interp.SetOutput(lines.Add);
        var cmds = new SfxCommands(interp, sm) { OpenDevice = () => true };
        interp.Evaluate("newSfx();");
        interp.Evaluate("sfxSetFormat(22050, 16, 1); sfxGetFormat();");
        interp.Evaluate("sfxAdd2DProfile(460000, 0.0); sfxAdd2DProfile(460001, 0.0, SFX_IS_LOOPING);");
        interp.Evaluate("sfxAdd3DProfile(460002, 0.0, 5, 500); sfxAdd3DProfile(460003, 0.1, 2, 100, 30, 60, 0, 0, 2, 0.5, SFX_IS_LOOPING);");
        interp.Evaluate("sfxAddPair(450001, 460002, \"a.wav\"); sfxAddPair(450002, 460000, \"miss.wav\"); sfxAddPair(450003, 460000, \"a.wav\", 0.5);");
        Assert.Contains("Sfx::Manager 22050kHz 16bit stereo", lines);
        Assert.Equal((5.0, 500.0, SoundProfile.Software3D), (sm.Profiles[460002].MinDist, sm.Profiles[460002].MaxDist, sm.Profiles[460002].Flags));
        SoundProfile c = sm.Profiles[460003];
        // F2: coneInside, coneOutside, x, y, z, coneOutsideVolume, then the flags; the vector is normalised.
        Assert.Equal((30, 60, 0.0, 0.0, 1.0, 0.5, SoundProfile.Software3D | SoundProfile.Looping), (c.ConeInside, c.ConeOutside, c.ConeX, c.ConeY, c.ConeZ, c.ConeOutsideVolume, c.Flags));
        Assert.True(sm.Profiles[460001].Loops);
        Assert.Equal(0.5, sm.Pairs[450003].Priority);
        lines.Clear();
        interp.Evaluate("sfxPlay(450002);");
        interp.Evaluate("sfxPlay(450003);");
        Assert.Equal(new[] { "sfxPlay handle = 0", "sfxPlay handle = 1" }, lines);
        lines.Clear();
        interp.Evaluate("sfxSetMaxBuffers(4); sfxGetMaxBuffers();");
        Assert.Contains("Sfx::Manager::maxBuffers = 4", lines);
    }

    [Fact]
    public void SfxCommandsFailWithoutNewSfxAndOnDeviceFailure() // 2.8, 2.1
    {
        var sm = new SoundManager();
        var interp = new Interpreter();
        var lines = new List<string>();
        interp.SetOutput(lines.Add);
        var cmds = new SfxCommands(interp, sm) { OpenDevice = () => false };
        interp.Evaluate("sfxPlay(1);");
        Assert.Contains("unable to find Sound object", lines);
        Assert.Equal("False", interp.Evaluate("newSfx();"));
        Assert.Contains("Sound System: error opening sound device.", lines);
        Assert.False(sm.DeviceOpen);
        Assert.False(sm.Query(1));
        Assert.False(sm.Query(2) || sm.Query(3) || sm.Query(4));   // D39
    }

    [Fact]
    public void SequencerPlaysChannelEntriesOneAfterAnother() // 2.12
    {
        SoundManager sm = Mgr(frames: 100);
        sm.Profiles[1] = P2D();
        sm.Pairs[10] = (1, "a.wav", 0);
        sm.Pairs[11] = (1, "a.wav", 0);
        var seq = new SoundSequencer(sm);
        seq.Add(3, 10, null);
        seq.Add(3, 11, null);
        seq.Tick();
        Assert.Single(sm.Voices);
        seq.Tick();
        Assert.Single(sm.Voices);            // the second waits
        sm.Mixer!.Render(new float[2 * 200], 200);   // the first finishes
        sm.Tick(1);
        seq.Tick();
        Assert.Single(sm.Voices);
        Assert.Equal(2, sm.Played);
        seq.Add(-1, 10, null);                       // channel -1 plays at once
        Assert.Equal(3, sm.Played);
    }

    [Fact]
    public void GuiWavesPlayThroughTheSoundManager() // 3.5: the GUI plays a named wave with the static 2D profile
    {
        SoundManager sm = Mgr();
        sm.Sfx2DVolume = 0.5;
        IGuiAudio gui = new GuiAudio(sm);
        gui.Play("Button1.wav");
        SoundVoice v = Assert.Single(sm.Voices);
        Assert.False(v.Profile.Is3D);
        Assert.Equal(0.069, v.Attenuation, 12);
        Assert.Equal(0, v.Priority);
    }

    [Fact]
    public void OfflineMixIsDeterministic() // 4.2: the same calls give the same bytes
    {
        float[] Run()
        {
            SoundManager sm = Mgr(level: 3000);
            sm.SetListener(0, 0, 0, 1, 0, 0);
            sm.PlayWave("a.wav", P3D(5, 300), 0, new SoundXf(30, 40, 0));
            sm.PlayWave("a.wav", P2D(loop: true), 0);
            var all = new List<float>();
            var o = new float[2 * 1000];
            for (int t = 0; t < 8; ++t)
            {
                sm.SetListener(t, 0, 0, 1, 0, 0);
                sm.Tick(t * 32);
                sm.Mixer!.Render(o, 1000);
                all.AddRange(o);
            }
            return all.ToArray();
        }
        Assert.Equal(Run(), Run());
    }
}
