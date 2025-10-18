// Tests of the Smacker decoder (Platform/Video) and the movie control (Gui/GuiMovie). Synthetic movies need no game data. The checks
// on the stock intro movie run only when base/dynlogoF.smk exists (OT_GAME_ROOT or the default install path) and return otherwise;
// nothing of the game is stored here, the frame checksums are computed from the decoded picture.
using System.Security.Cryptography;
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Video;

namespace OpenTribes.Tests;

public class SmackerTests
{
    // A movie with empty Huffman trees: every video byte stream decodes to blocks of color 0, so only the header, the palette
    // records and the raw audio need real data. rate: the header frame rate field; sizes are rounded up to 4.
    private static byte[] Synthetic(int w, int h, int rate, uint flags, byte[][] frames, byte[] types, uint audioRate = 0, int audioMax = 0)
    {
        int total = frames.Length;
        var m = new MemoryStream();
        void U32(uint v) => m.Write(BitConverter.GetBytes(v));
        m.Write("SMK2"u8);
        U32((uint)w); U32((uint)h);
        U32((uint)(total - ((flags & 1) != 0 ? 1 : 0)));
        U32((uint)rate); U32(flags);
        for (int i = 0; i < 7; ++i) U32(i == 0 ? (uint)audioMax : 0);   // audio buffer sizes
        U32(4);                                                          // tree size
        U32(0); U32(0); U32(0); U32(0);                                  // unpacked tree sizes
        for (int i = 0; i < 7; ++i) U32(i == 0 ? audioRate : 0);
        U32(0);
        foreach (byte[] f in frames) U32((uint)f.Length);
        m.Write(types);
        m.Write(new byte[4]);                                            // four absent trees
        foreach (byte[] f in frames) m.Write(f);
        return m.ToArray();
    }

    private static byte[] Pad4(params byte[] b) => b.Concat(new byte[(4 - b.Length % 4) % 4]).ToArray();

    [Fact]
    public void HeaderGivesSizeFramesAndRate()
    {
        byte[] f = Pad4(0, 0, 0, 0);
        SmackerDecoder? d = SmackerDecoder.TryOpen(Synthetic(16, 8, 100, 0, new[] { f, f, f }, new byte[3]), out string err);
        Assert.NotNull(d);
        Assert.Equal("", err);
        Assert.Equal((16, 8, 3), (d.Width, d.Height, d.FrameCount));
        Assert.Equal(100_000, d.FrameMicroseconds);
        // A negative field counts 10 microsecond units: -6666 is the 15 frames a second of the stock movie.
        d = SmackerDecoder.TryOpen(Synthetic(16, 8, -6666, 0, new[] { f }, new byte[1]), out _);
        Assert.Equal(66_660, d!.FrameMicroseconds);
        Assert.InRange(d.FramesPerSecond, 14.99, 15.01);
        // 0 means 100 ms; the ring flag adds one frame to the count.
        d = SmackerDecoder.TryOpen(Synthetic(16, 8, 0, 1, new[] { f, f }, new byte[2]), out _);
        Assert.Equal((100_000, 2), (d!.FrameMicroseconds, d.FrameCount));
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Null(SmackerDecoder.TryOpen(new byte[200], out string err));
        Assert.Contains("not a Smacker", err);
        Assert.False(SmackerDecoder.IsSmacker("RIFF"u8));
    }

    [Fact]
    public void PaletteRecordsNewCopySkip()
    {
        // Frame 0: entry 0 = (63, 0, 31) in 6 bit steps, entries 1 and 2 copy entries 0 and 1 of the previous palette (black),
        // the rest is skipped. Frame 1: skip entry 0, copy 1 entry from index 0 of the previous palette (the red).
        byte[] rec0 = { 63, 0, 31, 0x40 | 1, 0, 0x80 | 252 };
        byte[] pal0 = Pad4(new byte[] { 0 }.Concat(rec0).ToArray());
        pal0[0] = (byte)(pal0.Length / 4);
        byte[] rec1 = { 0x80 | 0, 0x40 | 0, 0, 0x80 | 253 };
        byte[] pal1 = Pad4(new byte[] { 0 }.Concat(rec1).ToArray());
        pal1[0] = (byte)(pal1.Length / 4);
        var d = SmackerDecoder.TryOpen(Synthetic(8, 8, 100, 0, new[] { pal0, pal1 }, new byte[] { 1, 1 }), out _)!;
        Assert.True(d.DecodeNext());
        Assert.True(d.PaletteChanged);
        // 6 bit to 8 bit: v << 2 | v >> 4.
        Assert.Equal(new byte[] { 255, 0, 0x7D }, d.Palette[..3]);
        Assert.Equal(new byte[] { 0, 0, 0 }, d.Palette[3..6]);
        Assert.True(d.DecodeNext());
        Assert.Equal(new byte[] { 255, 0, 0x7D }, d.Palette[..3]);
        Assert.Equal(new byte[] { 255, 0, 0x7D }, d.Palette[3..6]);
        Assert.False(d.DecodeNext());
    }

    [Fact]
    public void RawAudioTrackIsReturnedAsIs()
    {
        // Track 0: 22050 Hz, has data (bit 30), not packed, 16 bit mono (bit 29). Frame type bit 1 marks the track.
        byte[] pcm = { 1, 0, 2, 0, 3, 0, 4, 0 };
        byte[] frame = BitConverter.GetBytes(pcm.Length + 4).Concat(pcm).ToArray();
        var d = SmackerDecoder.TryOpen(Synthetic(8, 8, 100, 0, new[] { frame }, new byte[] { 2 }, 22050 | 0x40000000 | 0x20000000, 8), out _)!;
        Assert.True(d.Audio[0].HasData);
        Assert.Equal((22050, 1, 16, false), (d.Audio[0].Rate, d.Audio[0].Channels, d.Audio[0].Bits, d.Audio[0].Packed));
        Assert.Equal(pcm, d.ReadAudio(0));
        Assert.Null(d.ReadAudio(1));
    }

    [Fact]
    public void MovieControlPlaysAtTheFrameRateAndPostsDone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ot-smk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "base"));
        try
        {
            byte[] f = Pad4(1, 2, 3, 4);
            File.WriteAllBytes(Path.Combine(dir, "base", "t.smk"), Synthetic(16, 8, 100, 0, new[] { f, f, f, f, f }, new byte[5]));
            var fx = new GuiFixture();
            var res = new ResourceManager(dir);
            res.SetSearchPath("base");
            fx.Host.Res = res;
            fx.Interp.Evaluate("IDSMK_T = 200201, \"t.smk\";");
            fx.Interp.Evaluate("function done() { echo(\"movie done\"); }");
            var dev = new NullDevice();
            GuiControl c = GuiFixture.Make("FearGui::SmackerCtrl", 0, 0, 640, 480,
                ("movieTag", 200201u), ("loop", (byte)0), ("movieDoneTag", 0u), ("palStart", 1u), ("palColors", 253u), ("stretch", (byte)1));
            c.Command = "done();";
            fx.Content(c);
            var smk = (GuiSmacker)c;
            Assert.True(smk.HasMovie);
            Assert.Equal(1, smk.FramesShown);           // frame 0 shows at once
            fx.Canvas.Render(dev);
            fx.Clock += 250;
            fx.Canvas.Frame();
            Assert.Equal(3, smk.FramesShown);           // 250 ms at 100 ms a frame: frames 0..2
            fx.Canvas.Render(dev);
            fx.Clock += 400;
            fx.Canvas.Frame();
            Assert.Equal(5, smk.FramesShown);
            Assert.DoesNotContain("movie done", fx.Output);
            fx.Canvas.Render(dev);                      // the last frame is drawn once
            fx.Clock += 16;
            fx.Canvas.Frame();
            Assert.Contains("movie done", fx.Output);
            Assert.Equal(3, dev.Stretches);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MovieControlSkipsOnKeyOrClickAndPostsDoneOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ot-smk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "base"));
        try
        {
            byte[] f = Pad4(1, 2, 3, 4);
            File.WriteAllBytes(Path.Combine(dir, "base", "t.smk"), Synthetic(16, 8, 100, 0, Enumerable.Repeat(f, 50).ToArray(), new byte[50]));
            foreach (bool key in new[] { true, false })
            {
                var fx = new GuiFixture();
                var res = new ResourceManager(dir);
                res.SetSearchPath("base");
                fx.Host.Res = res;
                fx.Interp.Evaluate("IDSMK_T = 200201, \"t.smk\"; function done() { echo(\"movie done\"); }");
                GuiControl c = GuiFixture.Make("FearGui::SmackerCtrl", 0, 0, 640, 480, ("movieTag", 200201u), ("movieDoneTag", 0u));
                c.Command = "done();";
                fx.Content(c);
                fx.Frame();
                Assert.DoesNotContain("movie done", fx.Output);
                if (key)
                    fx.Tap("space");
                else
                    fx.Click(100, 100);
                fx.Frame();
                fx.Frame();
                Assert.Single(fx.Output, "movie done");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MovieThatDoesNotOpenIsOverAtOnce()
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("function done() { echo(\"movie done\"); }");
        GuiControl c = GuiFixture.Make("FearGui::SmackerCtrl", 0, 0, 640, 480, ("movieTag", 0u), ("movieDoneTag", 0u));
        c.Command = "done();";
        fx.Content(c);
        fx.Frame();
        fx.Frame();
        Assert.Single(fx.Output, "movie done");
    }

    // ---- the stock movie (only with the game files) ----

    private static byte[]? Stock()
    {
        string root = Environment.GetEnvironmentVariable("OT_GAME_ROOT") ?? "/mnt/software/Games/Tribes/Tribes";
        string path = Path.Combine(root, "base", "dynlogoF.smk");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void StockMovieHeaderAndPalette()
    {
        byte[]? file = Stock();
        if (file == null)
            return;
        SmackerDecoder d = SmackerDecoder.TryOpen(file, out string err)!;
        Assert.NotNull(d);
        Assert.Equal((640, 480, 207), (d.Width, d.Height, d.FrameCount));
        Assert.Equal(66_660, d.FrameMicroseconds);
        Assert.Equal((22050, 2, 16, true, true), (d.Audio[0].Rate, d.Audio[0].Channels, d.Audio[0].Bits, d.Audio[0].Packed, d.Audio[0].HasData));
        Assert.All(d.Audio.Skip(1), a => Assert.False(a.HasData));
        // The trees use the whole tree area but its zero padding.
        Assert.InRange(d.TreeBitsLeft, 0, 7 * 8 + 82 * 8);
        Assert.True(d.DecodeNext());
        // Frame 0 is a black picture with a palette record.
        Assert.True(d.PaletteChanged);
        Assert.All(d.Pixels, p => Assert.Equal(d.Pixels[0], p));
        Assert.Equal(0, d.Palette[d.Pixels[0] * 3]);
    }

    [Fact]
    public void StockMovieDecodesEveryFrameExactly()
    {
        byte[]? file = Stock();
        if (file == null)
            return;
        SmackerDecoder d = SmackerDecoder.TryOpen(file, out _)!;
        string? at100 = null, at206 = null;
        int n = 0;
        while (d.DecodeNext())
        {
            // A frame ends within the padding of its last 32 bit word.
            Assert.InRange(d.LastVideoSlackBits, 0, 63);
            if (n == 100) at100 = Hash(d);
            if (n == 206) at206 = Hash(d);
            ++n;
        }
        Assert.Equal(207, n);
        // SHA-256 of the 640x480 RGB picture after frames 100 (the Dynamix logo) and 206 (the Tribes title); the pictures were
        // also checked against an independent decoder.
        Assert.Equal("126E93CE27978B7D0740831D2A8249BCD3B78953505617DADD9629486E56F529", at100);
        Assert.Equal("5D05926BF845C6066303C74644376ED08B7475FC9CE967F68E35A062009AA5C7", at206);
    }

    private static string Hash(SmackerDecoder d)
    {
        // The picture as RGB, so the palette counts too.
        var rgb = new byte[d.Pixels.Length * 3];
        for (int i = 0; i < d.Pixels.Length; ++i)
            Array.Copy(d.Palette, d.Pixels[i] * 3, rgb, i * 3, 3);
        return Convert.ToHexString(SHA256.HashData(rgb));
    }

    [Fact]
    public void StockMovieRewindReproducesTheSameFrames()
    {
        byte[]? file = Stock();
        if (file == null)
            return;
        SmackerDecoder d = SmackerDecoder.TryOpen(file, out _)!;
        var first = new List<string>();
        for (int i = 0; i < 120 && d.DecodeNext(); ++i)
            if (i % 40 == 0 || i == 119)
                first.Add(Hash(d));
        d.Rewind();
        var second = new List<string>();
        for (int i = 0; i < 120 && d.DecodeNext(); ++i)
            if (i % 40 == 0 || i == 119)
                second.Add(Hash(d));
        Assert.Equal(first, second);
        Assert.Equal(4, first.Count);
        Assert.True(first.Distinct().Count() >= 3);
    }

    [Fact]
    public void StockMovieAudioIsTheLengthOfTheMovieAndAudible()
    {
        byte[]? file = Stock();
        if (file == null)
            return;
        SmackerDecoder d = SmackerDecoder.TryOpen(file, out _)!;
        byte[] pcm = d.ReadAudio(0)!;
        int frames = pcm.Length / 4;
        double seconds = frames / 22050.0;
        // 207 frames at 66.66 ms: 13.8 s (the audio of the last frame may be a few samples short).
        Assert.InRange(seconds, 13.7, 13.9);
        // The first seconds are silent (the picture is black), the rest has sound.
        for (int i = 0; i < 22050 * 4 * 2; ++i)
            Assert.Equal(0, pcm[i]);
        double sum = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            double v = (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768.0;
            sum += v * v;
        }
        double rms = Math.Sqrt(sum / (pcm.Length / 2));
        Assert.InRange(rms, 0.05, 0.5);
        // Through the mixer, offline at the device rate: the output has the level of the movie and ends with the movie.
        WavData wav = GuiSmacker.ToWave(pcm, d.Audio[0]);
        var mixer = new SoundMixer(48000);
        Assert.NotNull(mixer.Start(wav, false, 1f, 1f));
        var block = new float[4800 * 2];
        float peak = 0;
        int blocks = 0;
        while (mixer.ActiveCount > 0 && blocks < 200)
        {
            mixer.Render(block, 4800);
            peak = Math.Max(peak, block.Max(Math.Abs));
            ++blocks;
        }
        Assert.InRange(blocks, 137, 141);              // 13.8 s in 100 ms blocks
        Assert.InRange(peak, 0.2f, 1f);
        Assert.Equal(blocks * 4800L, mixer.FramesRendered);
    }

    private sealed class NullDevice : IGfxDevice
    {
        public int Stretches;
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) { }
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) { }
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) { }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) => ++Stretches;
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => Array.Empty<byte>();
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }
}
