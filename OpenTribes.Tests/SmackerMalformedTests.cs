// Malformed Smacker movies: header arithmetic, chunk slices, tree depth and the audio size limits. None of these may throw out of
// TryOpen, DecodeNext or ReadAudio, and well-formed movies keep decoding as before (SmackerTests).
namespace OpenTribes.Tests;

using OpenTribes.Platform.Video;

public class SmackerMalformedTests
{
    // A one track movie. sizeFields are the raw frame size words, data is written as given. tree is the tree area.
    private static byte[] Movie(uint[] sizeFields, byte[] types, byte[][] data, byte[]? tree = null, uint audioRate = 0, uint audioMax = 0, uint treeSizeField = uint.MaxValue)
    {
        tree ??= new byte[4];
        var m = new MemoryStream();
        void U32(uint v) => m.Write(BitConverter.GetBytes(v));
        m.Write("SMK2"u8);
        U32(16);
        U32(8);
        U32((uint)sizeFields.Length);
        U32(100);
        U32(0);
        for (var i = 0; i < 7; ++i)
            U32(i == 0 ? audioMax : 0);
        U32(treeSizeField == uint.MaxValue ? (uint)tree.Length : treeSizeField);
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        for (var i = 0; i < 7; ++i)
            U32(i == 0 ? audioRate : 0);
        U32(0);
        foreach (var s in sizeFields)
            U32(s);
        m.Write(types);
        m.Write(tree);
        foreach (var f in data)
            m.Write(f);
        return m.ToArray();
    }

    private static byte[] Frame(params byte[] b) => [.. b, .. new byte[(4 - (b.Length % 4)) % 4]];

    // The tree area of a big tree on the first tree slot only: present, no low or high byte tree, escapes 0, then a left chain of
    // `depth` internal nodes, the leaf at its end and the right leaf of every internal node, then the stop bit.
    private static byte[] ChainTree(int depth)
    {
        var bits = new List<bool> { true, false, false };
        bits.AddRange(Enumerable.Repeat(false, 48));
        bits.AddRange(Enumerable.Repeat(true, depth));
        bits.AddRange(Enumerable.Repeat(false, depth + 1));
        bits.Add(false);
        bits.AddRange([false, false, false]);      // the other three trees are absent
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; ++i)
            if (bits[i])
                bytes[i >> 3] |= (byte)(1 << (i & 7));
        return bytes;
    }

    [Fact]
    public void TreeSizeNearTwoToTheThirtyFirstIsRejected()
    {
        var f = Frame(0, 0, 0, 0);
        foreach (var size in new uint[] { 0x7FFFFFFF, 0x80000000, 0xFFFFFFF0 })
        {
            var d = SmackerDecoder.TryOpen(Movie([4], [0], [f], treeSizeField: size), out var err);
            Assert.Null(d);
            Assert.Contains("past the file", err);
        }
    }

    [Fact]
    public void FrameSizeAboveTwoToTheThirtyFirstEndsTheMovieAtTheLastWholeFrame()
    {
        var f = Frame(0, 0, 0, 0);
        var d = SmackerDecoder.TryOpen(Movie([4, 0xFFFFFFFC, 4], [0, 0, 0], [f, f, f]), out _);
        Assert.NotNull(d);
        Assert.Equal(1, d.FrameCount);
        Assert.True(d.DecodeNext());
        Assert.False(d.DecodeNext());
        Assert.Null(SmackerDecoder.TryOpen(Movie([0xFFFFFFFC], [0], [f]), out var err));
        Assert.Equal("Smacker frame data missing", err);
    }

    [Fact]
    public void FrameSizesThatWrapWhenSummedEndTheMovie()
    {
        var f = Frame(0, 0, 0, 0);
        var d = SmackerDecoder.TryOpen(Movie([4, 0x7FFFFFFC, 0x7FFFFFFC, 0x7FFFFFFC], [0, 0, 0, 0], [f]), out _);
        Assert.NotNull(d);
        Assert.Equal(1, d.FrameCount);
        while (d.DecodeNext())
        {
        }
    }

    [Fact]
    public void PaletteChunkWithALengthByteOfZeroDecodes()
    {
        var d = SmackerDecoder.TryOpen(Movie([8], [1], [Frame(0, 1, 2, 3, 4, 5, 6, 7)]), out _)!;
        Assert.True(d.DecodeNext());
        Assert.True(d.PaletteChanged);
    }

    [Fact]
    public void PaletteLengthPastTheChunkDecodes()
    {
        var d = SmackerDecoder.TryOpen(Movie([8], [1], [Frame(200, 0x3F, 0x3F, 0x3F, 0, 0, 0, 0)]), out _)!;
        Assert.True(d.DecodeNext());
        Assert.Equal(255, d.Palette[0]);
    }

    [Fact]
    public void AudioChunkLengthNearTwoToTheThirtyFirstDoesNotWrapTheCursor()
    {
        // Palette record of 4 bytes, then an audio length of 0x7FFFFFFF: p + length wraps negative in an int.
        var frame = Frame([1, 0, 0, 0, .. BitConverter.GetBytes(0x7FFFFFFFu), 0, 0, 0, 0]);
        var d = SmackerDecoder.TryOpen(Movie([(uint)frame.Length], [3], [frame], audioRate: 22050 | 0x40000000, audioMax: 100), out _)!;
        Assert.True(d.DecodeNext());
        Assert.Empty(d.ReadAudio(0)!);
    }

    [Fact]
    public void AudioChunkLengthPastTheFrameIsIgnoredInReadAudio()
    {
        var frame = Frame([.. BitConverter.GetBytes(0x7FFFFFFFu), 1, 2, 3, 4]);
        var d = SmackerDecoder.TryOpen(Movie([(uint)frame.Length], [2], [frame], audioRate: 22050 | 0x40000000, audioMax: 100), out _)!;
        Assert.Empty(d.ReadAudio(0)!);
        // A palette record first moves the audio chunk: p = 4 and p + length would wrap.
        frame = Frame([1, 0, 0, 0, .. BitConverter.GetBytes(0x7FFFFFFFu), 1, 2, 3, 4]);
        d = SmackerDecoder.TryOpen(Movie([(uint)frame.Length], [3], [frame], audioRate: 22050 | 0x40000000, audioMax: 100), out _)!;
        Assert.Empty(d.ReadAudio(0)!);
    }

    [Fact]
    public void ABigTreeThousandsOfLevelsDeepIsRejectedWithoutOverflowingTheStack()
    {
        // 12000 one bits: the old cap of 100000 levels was above what the tree area could hold, so the recursion ran to the end of the data.
        var tree = new byte[12000];
        Array.Fill(tree, (byte)0xFF);
        tree[0] = 0b11111001;                    // present, no low tree, no high tree, then ones
        var d = SmackerDecoder.TryOpen(Movie([4], [0], [Frame(0, 0, 0, 0)], tree), out var err);
        Assert.Null(d);
        Assert.Contains("trees", err);
    }

    [Fact]
    public void ABigTreeWithinTheDepthCapStillOpens()
    {
        Assert.NotNull(SmackerDecoder.TryOpen(Movie([4], [0], [Frame(0, 0, 0, 0)], ChainTree(400)), out _));
        var over = SmackerDecoder.TryOpen(Movie([4], [0], [Frame(0, 0, 0, 0)], ChainTree(600)), out var err);
        Assert.Null(over);
        Assert.Contains("too deep", err);
    }

    [Fact]
    public void PackedAudioChunkOverTheTrackBufferSizeIsSkippedBeforeItIsAllocated()
    {
        // Packed track, header buffer size 1000. A chunk that says it unpacks to 64 MB in five bytes of data is dropped, a chunk of 1000 is silence.
        byte[] Chunk(uint unpacked) => Frame([.. BitConverter.GetBytes(unpacked + 0), 0, 0, 0, 0]);
        byte[] Wrap(byte[] body) => Frame([.. BitConverter.GetBytes(body.Length + 4), .. body]);
        var big = Wrap(Chunk(64 * 1024 * 1024));
        var ok = Wrap(Chunk(1000));
        var d = SmackerDecoder.TryOpen(Movie([(uint)big.Length, (uint)ok.Length], [2, 2], [big, ok], audioRate: 22050 | 0x40000000 | 0x80000000, audioMax: 1000), out _)!;
        var pcm = d.ReadAudio(0)!;
        Assert.Equal(new byte[1000], pcm);
    }

    [Fact]
    public void RandomDamageNeverThrowsOutOfTheDecoder()
    {
        // The real movie is damaged when the game files are present, a synthetic one with trees and audio otherwise.
        var root = Environment.GetEnvironmentVariable("OT_GAME_ROOT") ?? "/mnt/software/Games/Tribes/Tribes";
        var path = Path.Combine(root, "base", "dynlogoF.smk");
        var baseFile = File.Exists(path) ? File.ReadAllBytes(path) : Movie([4, 4], [0, 0], [Frame(0, 0, 0, 0), Frame(0, 0, 0, 0)], ChainTree(20));
        var rng = new Random(1234);
        var rounds = File.Exists(path) ? 12 : 300;
        for (var n = 0; n < rounds; ++n)
        {
            var f = (byte[])baseFile.Clone();
            // Damage the header, the size table or the start of the data, never the multi-megabyte pixel work of the whole file.
            var hits = 1 + rng.Next(6);
            for (var h = 0; h < hits; ++h)
            {
                var at = rng.Next(3) == 0 ? rng.Next(0x68) : rng.Next(Math.Min(f.Length, 6000));
                f[at] = (byte)rng.Next(256);
            }
            var d = SmackerDecoder.TryOpen(f, out _);
            if (d == null)
                continue;
            for (var i = 0; i < 3 && d.DecodeNext(); ++i)
            {
            }
            _ = d.ReadAudio(0);
        }
    }
}
