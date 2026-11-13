namespace OpenTribes.Platform.Video;

/// <summary>One audio track of a Smacker movie.</summary>
public sealed class SmackerAudioTrack
{
    /// <summary>The sample rate.</summary>
    public int Rate;
    /// <summary>The channel count.</summary>
    public int Channels = 1;
    /// <summary>The bits per sample, 8 or 16.</summary>
    public int Bits = 8;
    /// <summary>Whether the track is Huffman DPCM packed rather than raw PCM.</summary>
    public bool Packed;
    /// <summary>Whether the track carries data.</summary>
    public bool HasData;
    /// <summary>The largest unpacked buffer of one frame in bytes (header field).</summary>
    public int MaxBytes;
}

/// <summary>A Smacker (SMK2 and SMK4) movie decoder.</summary>
/// <remarks>It is written from the public format description: header, packed Huffman trees, palette records, the 4x4 block video (mono, full, void and solid blocks) and the audio tracks (raw PCM and the Huffman DPCM packing). There is no third-party code and no smackw32.dll. The stock client plays its movies through that DLL (docs/gui.md SmackerCtrl). This decoder gives the same pictures and sound from the file alone.</remarks>
public sealed class SmackerDecoder
{
    private const int MaxTracks = 7;

    private readonly byte[] mData;
    private readonly int[] mFrameStart;
    private readonly int[] mFrameSize;
    private readonly byte[] mFrameType;
    private readonly BigTree[] mTrees = new BigTree[4];
    private const int TreeMmap = 0, TreeMclr = 1, TreeFull = 2, TreeType = 3;
    private static readonly int[] sBlockRuns = BuildRuns();

    /// <summary>Gets the picture width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the picture height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the number of frames to play, ring frame included.</summary>
    public int FrameCount { get; }
    /// <summary>Gets the header flags: bit 0 ring frame, bit 1 Y interlaced, bit 2 Y doubled.</summary>
    public uint Flags { get; }
    /// <summary>Gets whether the picture is Y interlaced or Y doubled.</summary>
    public bool YDoubled => (Flags & 6) != 0;
    /// <summary>Gets the frame duration in microseconds.</summary>
    /// <remarks>A positive header value is milliseconds, a negative one is 10 microsecond units, and 0 means 100 ms.</remarks>
    public long FrameMicroseconds { get; }
    /// <summary>Gets the frame rate.</summary>
    public double FramesPerSecond => 1_000_000.0 / FrameMicroseconds;
    /// <summary>Gets the audio tracks.</summary>
    public SmackerAudioTrack[] Audio { get; } = new SmackerAudioTrack[MaxTracks];

    /// <summary>Gets the picture as palette indices, row by row.</summary>
    public byte[] Pixels { get; private set; }
    /// <summary>Gets the 256-entry RGB palette, 768 bytes.</summary>
    public byte[] Palette { get; } = new byte[768];
    /// <summary>Gets the number of the next frame to decode.</summary>
    public int NextFrame { get; private set; }
    /// <summary>Gets whether the last decoded frame replaced the palette.</summary>
    public bool PaletteChanged { get; private set; }
    /// <summary>Gets the bits left unread at the end of the last frame's video data, for tests and diagnostics. Negative means the frame ran past its data.</summary>
    public int LastVideoSlackBits { get; private set; }

    private SmackerDecoder(byte[] data, int w, int h, int frames, uint flags, long usec, int[] starts, int[] sizes, byte[] types)
    {
        mData = data;
        Width = w;
        Height = h;
        FrameCount = frames;
        Flags = flags;
        FrameMicroseconds = usec;
        mFrameStart = starts;
        mFrameSize = sizes;
        mFrameType = types;
        Pixels = new byte[w * h];
    }

    private int mTreesAt, mTreeSize;

    /// <summary>Tests whether data starts with a Smacker signature.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>True for SMK2 and SMK4.</returns>
    public static bool IsSmacker(ReadOnlySpan<byte> d) =>
        d.Length >= 4 && d[0] == 'S' && d[1] == 'M' && d[2] == 'K' && (d[3] == '2' || d[3] == '4');

    /// <summary>Opens a movie.</summary>
    /// <param name="d">The file bytes.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>The decoder, or null on failure. A truncated file plays what is there.</returns>
    public static SmackerDecoder? TryOpen(byte[] d, out string error)
    {
        error = "";
        if (!IsSmacker(d) || d.Length < 0x68)
        {
            error = "not a Smacker file";
            return null;
        }
        uint U(int o) => BitConverter.ToUInt32(d, o);
        long w = U(4), h = U(8), frames = U(12);
        var rate = (int)U(16);
        var flags = U(20);
        if (w < 4 || h < 4 || w > 4096 || h > 4096 || frames == 0 || frames > 1_000_000)
        {
            error = "bad Smacker header";
            return null;
        }
        long ring = (flags & 1) != 0 ? 1 : 0;
        var total = (int)(frames + ring);
        var usec = rate > 0 ? rate * 1000L : rate < 0 ? -rate * 10L : 100_000;
        var treeSize = (int)U(0x34);
        var sizesAt = 0x68;
        var typesAt = sizesAt + (total * 4);
        var treesAt = typesAt + total;
        var dataAt = treesAt + treeSize;
        if (dataAt > d.Length || treeSize < 0)
        {
            error = "Smacker header runs past the file";
            return null;
        }
        var starts = new int[total];
        var sizes = new int[total];
        var types = new byte[total];
        long pos = dataAt;
        for (var i = 0; i < total; ++i)
        {
            sizes[i] = (int)(U(sizesAt + (i * 4)) & ~3u);
            types[i] = d[typesAt + i];
            starts[i] = (int)pos;
            pos += sizes[i];
        }
        if (pos > d.Length)
        {
            // A truncated file plays what is there; trim the frame list to the last whole frame.
            var keep = 0;
            while (keep < total && starts[keep] + sizes[keep] <= d.Length)
                ++keep;
            if (keep == 0)
            {
                error = "Smacker frame data missing";
                return null;
            }
            total = keep;
        }
        var dec = new SmackerDecoder(d, (int)w, (int)h, total, flags, usec, starts, sizes, types);
        for (var t = 0; t < MaxTracks; ++t)
        {
            var r = U(0x48 + (t * 4));
            dec.Audio[t] = new SmackerAudioTrack
            {
                Rate = (int)(r & 0xFFFFFF),
                Packed = (r & 0x80000000) != 0,
                HasData = (r & 0x40000000) != 0,
                Bits = (r & 0x20000000) != 0 ? 16 : 8,
                Channels = (r & 0x10000000) != 0 ? 2 : 1,
                MaxBytes = (int)U(0x18 + (t * 4)),
            };
        }
        try
        {
            dec.mTreesAt = treesAt;
            dec.mTreeSize = treeSize;
            dec.ReadTrees();
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            error = "bad Smacker trees: " + e.Message;
            return null;
        }
        return dec;
    }

    private static int[] BuildRuns()
    {
        var r = new int[64];
        for (var i = 0; i < 59; ++i)
            r[i] = i + 1;
        for (var i = 59; i < 64; ++i)
            r[i] = 128 << (i - 59);
        return r;
    }

    // ---- bits and trees ----

    private sealed class BitReader
    {
        private readonly byte[] mD;
        private readonly int mEnd;
        public int Bit;                // bit offset from the start of mD

        public BitReader(byte[] d, int start, int length)
        {
            mD = d;
            Bit = start * 8;
            mEnd = (start + length) * 8;
        }

        public int BitsLeft => mEnd - Bit;

        public int Get1()
        {
            if (Bit >= mEnd)
                throw new InvalidDataException("bit stream ended");
            var v = (mD[Bit >> 3] >> (Bit & 7)) & 1;
            ++Bit;
            return v;
        }

        // Bits come least significant first.
        public int Get(int n)
        {
            var v = 0;
            for (var i = 0; i < n; ++i)
                v |= Get1() << i;
            return v;
        }
    }

    // A binary tree over a node array: Left[i] < 0 marks a leaf holding Value[i].
    private sealed class ByteTree
    {
        public readonly List<int> Left = [], Right = [], Value = [];

        public static ByteTree Read(BitReader br)
        {
            var t = new ByteTree();
            t.Node(br, 0);
            return t;
        }

        private int Node(BitReader br, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("tree too deep");
            var id = Left.Count;
            Left.Add(-1);
            Right.Add(-1);
            Value.Add(0);
            if (br.Get1() == 0)
            {
                Value[id] = br.Get(8);
                return id;
            }
            var l = Node(br, depth + 1);
            var r = Node(br, depth + 1);
            Left[id] = l;
            Right[id] = r;
            return id;
        }

        public int Decode(BitReader br)
        {
            var n = 0;
            while (Left[n] >= 0)
                n = br.Get1() == 0 ? Left[n] : Right[n];
            return Value[n];
        }
    }

    // The 16 bit tree of the video: each leaf joins a low and a high byte. The three leaves whose value equals an escape
    // value are not literals: they hold the three most recent distinct values decoded from this tree (newest first), starting
    // at 0 at the start of every frame.
    private sealed class BigTree
    {
        public readonly List<int> Left = [], Right = [], Value = [];
        public readonly int[] Slot = new int[3];

        public static BigTree ReadEmpty()
        {
            var t = new BigTree();
            t.Left.Add(-1);
            t.Right.Add(-1);
            t.Value.Add(0);
            return t;
        }

        public static BigTree Read(BitReader br, ByteTree? low, ByteTree? high, int[] esc)
        {
            var t = new BigTree();
            t.Node(br, low, high, esc, 0);
            return t;
        }

        private int Node(BitReader br, ByteTree? low, ByteTree? high, int[] esc, int depth)
        {
            if (depth > 100000)
                throw new InvalidDataException("tree too deep");
            var id = Left.Count;
            Left.Add(-1);
            Right.Add(-1);
            Value.Add(0);
            if (br.Get1() == 0)
            {
                var lo = low?.Decode(br) ?? 0;
                var hi = high?.Decode(br) ?? 0;
                var v = lo | (hi << 8);
                var k = Array.IndexOf(esc, v);
                if (k >= 0)
                {
                    Slot[k] = id;
                    v = 0;
                }
                Value[id] = v;
                return id;
            }
            var l = Node(br, low, high, esc, depth + 1);
            var r = Node(br, low, high, esc, depth + 1);
            Left[id] = l;
            Right[id] = r;
            return id;
        }

        // Every frame starts with empty caches.
        public void ResetCache()
        {
            Value[Slot[0]] = 0;
            Value[Slot[1]] = 0;
            Value[Slot[2]] = 0;
        }

        public int Decode(BitReader br)
        {
            var n = 0;
            while (Left[n] >= 0)
                n = br.Get1() == 0 ? Left[n] : Right[n];
            var v = Value[n];
            if (v != Value[Slot[0]])
            {
                Value[Slot[2]] = Value[Slot[1]];
                Value[Slot[1]] = Value[Slot[0]];
                Value[Slot[0]] = v;
            }
            return v;
        }
    }

    private void ReadTrees()
    {
        var br = new BitReader(mData, mTreesAt, mTreeSize);
        for (var i = 0; i < 4; ++i)
        {
            if (br.Get1() == 0)
            {
                mTrees[i] = BigTree.ReadEmpty();
                continue;
            }
            ByteTree? low = null, high = null;
            if (br.Get1() != 0)
            {
                low = ByteTree.Read(br);
                br.Get1();
            }
            if (br.Get1() != 0)
            {
                high = ByteTree.Read(br);
                br.Get1();
            }
            var esc = new[] { br.Get(16), br.Get(16), br.Get(16) };
            mTrees[i] = BigTree.Read(br, low, high, esc);
            br.Get1();
        }
        TreeBitsLeft = br.BitsLeft;
    }

    /// <summary>Gets the unread bits after the four trees. They are padding only in a good file.</summary>
    public int TreeBitsLeft { get; private set; }

    // ---- frames ----

    /// <summary>Goes back to frame 0 with the state of a freshly opened file.</summary>
    public void Rewind()
    {
        NextFrame = 0;
        Array.Clear(Pixels);
        Array.Clear(Palette);
    }

    /// <summary>Decodes the next frame into <see cref="Pixels"/> and <see cref="Palette"/>.</summary>
    /// <returns>False at the end of the movie.</returns>
    public bool DecodeNext()
    {
        if (NextFrame >= FrameCount)
            return false;
        var i = NextFrame++;
        ReadOnlySpan<byte> chunk = new ReadOnlySpan<byte>(mData, mFrameStart[i], mFrameSize[i]);
        var p = 0;
        PaletteChanged = false;
        var type = mFrameType[i];
        if ((type & 1) != 0 && chunk.Length > 0)
        {
            var len = chunk[0] * 4;
            ReadPalette(chunk[1..Math.Min(len, chunk.Length)]);
            PaletteChanged = true;
            p += len;
        }
        for (var t = 0; t < MaxTracks && p < chunk.Length; ++t)
        {
            if ((type & (2 << t)) == 0)
                continue;
            var alen = (int)BitConverter.ToUInt32(chunk.Slice(p, 4));
            p += Math.Max(alen, 4);
        }
        if (p < chunk.Length)
            DecodeVideo(chunk[Math.Min(p, chunk.Length)..]);
        return true;
    }

    private void ReadPalette(ReadOnlySpan<byte> d)
    {
        var old = (byte[])Palette.Clone();
        int i = 0, j = 0;
        while (i < 256 && j < d.Length)
        {
            var b = d[j++];
            if ((b & 0x80) != 0)
            {
                i += (b & 0x7F) + 1;
            }
            else if ((b & 0x40) != 0)
            {
                var n = (b & 0x3F) + 1;
                if (j >= d.Length)
                    break;
                int s = d[j++];
                for (var k = 0; k < n && i + k < 256 && s + k < 256; ++k)
                    Array.Copy(old, (s + k) * 3, Palette, (i + k) * 3, 3);
                i += n;
            }
            else
            {
                if (j + 1 >= d.Length)
                    break;
                int r = b & 0x3F, g = d[j++] & 0x3F, bl = d[j++] & 0x3F;
                Palette[i * 3] = Scale6(r);
                Palette[(i * 3) + 1] = Scale6(g);
                Palette[(i * 3) + 2] = Scale6(bl);
                ++i;
            }
        }
    }

    // 6 bit palette component to 8 bit: the high bits repeat into the low ones (0x3F -> 0xFF).
    private static byte Scale6(int v) => (byte)((v << 2) | (v >> 4));

    private void DecodeVideo(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        var br = new BitReader(copy, 0, copy.Length);
        int bw = (Width + 3) / 4, bh = (Height + 3) / 4, blocks = bw * bh;
        var blk = 0;
        foreach (BigTree t in mTrees)
            t.ResetCache();
        try
        {
            while (blk < blocks)
            {
                var sym = mTrees[TreeType].Decode(br);
                var btype = sym & 3;
                var run = sBlockRuns[(sym >> 2) & 0x3F];
                var extra = sym >> 8;
                for (var k = 0; k < run && blk < blocks; ++k, ++blk)
                {
                    int bx = blk % bw * 4, by = blk / bw * 4;
                    switch (btype)
                    {
                        case 0:
                            MonoBlock(br, bx, by);
                            break;
                        case 1:
                            FullBlock(br, bx, by);
                            break;
                        case 3:
                            SolidBlock(bx, by, (byte)extra);
                            break;
                            // 2: void, the block keeps the previous picture.
                    }
                }
            }
            LastVideoSlackBits = br.BitsLeft;
        }
        catch (InvalidDataException)
        {
            LastVideoSlackBits = -1;
        }
    }

    private void MonoBlock(BitReader br, int bx, int by)
    {
        var colors = mTrees[TreeMclr].Decode(br);
        var map = mTrees[TreeMmap].Decode(br);
        byte hi = (byte)(colors >> 8), lo = (byte)colors;
        for (var row = 0; row < 4; ++row)
        {
            var y = by + row;
            for (var col = 0; col < 4; ++col)
            {
                var x = bx + col;
                if (x < Width && y < Height)
                    Pixels[(y * Width) + x] = (map & (1 << ((row * 4) + col))) != 0 ? hi : lo;
            }
        }
    }

    private void FullBlock(BitReader br, int bx, int by)
    {
        for (var row = 0; row < 4; ++row)
        {
            var y = by + row;
            var right = mTrees[TreeFull].Decode(br);
            var left = mTrees[TreeFull].Decode(br);
            Put(bx + 2, y, (byte)right);
            Put(bx + 3, y, (byte)(right >> 8));
            Put(bx, y, (byte)left);
            Put(bx + 1, y, (byte)(left >> 8));
        }
    }

    private void Put(int x, int y, byte v)
    {
        if (x < Width && y < Height)
            Pixels[(y * Width) + x] = v;
    }

    private void SolidBlock(int bx, int by, byte c)
    {
        for (var row = 0; row < 4; ++row)
            for (var col = 0; col < 4; ++col)
                Put(bx + col, by + row, c);
    }

    // ---- audio ----

    /// <summary>Decodes a whole audio track to little endian PCM, 8-bit unsigned or 16-bit signed, interleaved, without decoding any video.</summary>
    /// <param name="track">The track index.</param>
    /// <returns>The PCM bytes, or null when the track carries no audio.</returns>
    public byte[]? ReadAudio(int track)
    {
        SmackerAudioTrack a = Audio[track];
        if (!a.HasData || a.Rate <= 0)
            return null;
        var pcm = new MemoryStream();
        for (var i = 0; i < FrameCount; ++i)
        {
            if ((mFrameType[i] & (2 << track)) == 0)
                continue;
            ReadOnlySpan<byte> chunk = new ReadOnlySpan<byte>(mData, mFrameStart[i], mFrameSize[i]);
            var p = (mFrameType[i] & 1) != 0 && chunk.Length > 0 ? chunk[0] * 4 : 0;
            for (var t = 0; t < MaxTracks && p + 4 <= chunk.Length; ++t)
            {
                if ((mFrameType[i] & (2 << t)) == 0)
                    continue;
                var alen = (int)BitConverter.ToUInt32(chunk.Slice(p, 4));
                if (alen < 4 || p + alen > chunk.Length)
                    break;
                if (t == track)
                {
                    ReadOnlySpan<byte> body = chunk.Slice(p + 4, alen - 4);
                    if (a.Packed)
                    {
                        var dec = UnpackAudio(body, a);
                        if (dec != null)
                            pcm.Write(dec);
                    }
                    else
                    {
                        pcm.Write(body);
                    }
                    break;
                }
                p += alen;
            }
        }
        return pcm.ToArray();
    }

    // One Huffman DPCM audio chunk: the unpacked byte count, three flag bits (data, stereo, 16 bit), 1 << (stereo + 16 bit) byte
    // trees, the start value of each channel (the right channel first) and then one delta per sample.
    private static byte[]? UnpackAudio(ReadOnlySpan<byte> body, SmackerAudioTrack a)
    {
        if (body.Length < 5)
            return null;
        var unpacked = (int)BitConverter.ToUInt32(body[..4]);
        if (unpacked is <= 0 or > (64 * 1024 * 1024))
            return null;
        var bytes = body[4..].ToArray();
        var br = new BitReader(bytes, 0, bytes.Length);
        try
        {
            if (br.Get1() == 0)
                return new byte[unpacked];
            var stereo = br.Get1();
            var wide = br.Get1();
            var nTrees = 1 << (stereo + wide);
            var trees = new ByteTree[nTrees];
            for (var i = 0; i < nTrees; ++i)
            {
                if (br.Get1() == 0)
                    throw new InvalidDataException("audio tree missing");
                trees[i] = ByteTree.Read(br);
                br.Get1();
            }
            var pred = new int[2];
            for (var c = stereo; c >= 0; --c)
                pred[c] = wide == 1 ? Swap16(br.Get(16)) : br.Get(8);
            var o = new byte[unpacked];
            var bps = wide == 1 ? 2 : 1;
            var n = unpacked / bps;
            var idx = 0;
            for (var c = 0; c <= stereo && idx < n; ++c, ++idx)
                Store(o, idx, bps, pred[c]);
            for (; idx < n; ++idx)
            {
                var ch = idx & stereo;
                if (wide == 1)
                {
                    var lo = trees[ch * 2].Decode(br);
                    var hi = trees[(ch * 2) + 1].Decode(br);
                    pred[ch] = (short)(pred[ch] + (short)(lo | (hi << 8)));
                }
                else
                {
                    pred[ch] = (byte)(pred[ch] + (sbyte)trees[ch].Decode(br));
                }
                Store(o, idx, bps, pred[ch]);
            }
            return o;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int Swap16(int v) => (short)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));

    private static void Store(byte[] o, int idx, int bps, int v)
    {
        if (bps == 2)
        {
            o[idx * 2] = (byte)v;
            o[(idx * 2) + 1] = (byte)(v >> 8);
        }
        else
        {
            o[idx] = (byte)v;
        }
    }
}
