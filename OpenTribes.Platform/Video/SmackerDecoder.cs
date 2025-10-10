// A Smacker (SMK2 and SMK4) movie decoder written from the public format description: header, packed Huffman trees, palette
// records, the 4x4 block video (mono, full, void and solid blocks) and the audio tracks (raw PCM and the Huffman DPCM packing).
// No third-party code and no smackw32.dll. The stock client plays its movies through that DLL (docs/gui.md SmackerCtrl); the
// decoder here gives the same pictures and sound from the file alone.
namespace OpenTribes.Platform.Video;

public sealed class SmackerAudioTrack
{
    public int Rate;
    public int Channels = 1;
    public int Bits = 8;
    public bool Packed;
    public bool HasData;
    // The largest unpacked buffer of one frame in bytes (header field).
    public int MaxBytes;
}

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

    public int Width { get; }
    public int Height { get; }
    // Frames to play, ring frame included.
    public int FrameCount { get; }
    // Header flags: bit 0 ring frame, bit 1 Y interlaced, bit 2 Y doubled.
    public uint Flags { get; }
    public bool YDoubled => (Flags & 6) != 0;
    // Frame duration in microseconds: a positive header value is milliseconds, a negative one is 10 microsecond units, 0 means 100 ms.
    public long FrameMicroseconds { get; }
    public double FramesPerSecond => 1_000_000.0 / FrameMicroseconds;
    public SmackerAudioTrack[] Audio { get; } = new SmackerAudioTrack[MaxTracks];

    // Decoder state: the picture (palette indices, row by row), the 256 entry RGB palette (768 bytes) and the next frame number.
    public byte[] Pixels { get; private set; }
    public byte[] Palette { get; } = new byte[768];
    public int NextFrame { get; private set; }
    // Set when the last decoded frame replaced the palette.
    public bool PaletteChanged { get; private set; }
    // Bits left unread at the end of the last frame's video data, for tests and diagnostics (negative: the frame ran past its data).
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

    public static bool IsSmacker(ReadOnlySpan<byte> d) =>
        d.Length >= 4 && d[0] == 'S' && d[1] == 'M' && d[2] == 'K' && (d[3] == '2' || d[3] == '4');

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
        int rate = (int)U(16);
        uint flags = U(20);
        if (w < 4 || h < 4 || w > 4096 || h > 4096 || frames == 0 || frames > 1_000_000)
        {
            error = "bad Smacker header";
            return null;
        }
        long ring = (flags & 1) != 0 ? 1 : 0;
        int total = (int)(frames + ring);
        long usec = rate > 0 ? rate * 1000L : rate < 0 ? -rate * 10L : 100_000;
        int treeSize = (int)U(0x34);
        int sizesAt = 0x68;
        int typesAt = sizesAt + total * 4;
        int treesAt = typesAt + total;
        int dataAt = treesAt + treeSize;
        if (dataAt > d.Length || treeSize < 0)
        {
            error = "Smacker header runs past the file";
            return null;
        }
        var starts = new int[total];
        var sizes = new int[total];
        var types = new byte[total];
        long pos = dataAt;
        for (int i = 0; i < total; ++i)
        {
            sizes[i] = (int)(U(sizesAt + i * 4) & ~3u);
            types[i] = d[typesAt + i];
            starts[i] = (int)pos;
            pos += sizes[i];
        }
        if (pos > d.Length)
        {
            // A truncated file plays what is there; trim the frame list to the last whole frame.
            int keep = 0;
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
        for (int t = 0; t < MaxTracks; ++t)
        {
            uint r = U(0x48 + t * 4);
            dec.Audio[t] = new SmackerAudioTrack
            {
                Rate = (int)(r & 0xFFFFFF),
                Packed = (r & 0x80000000) != 0,
                HasData = (r & 0x40000000) != 0,
                Bits = (r & 0x20000000) != 0 ? 16 : 8,
                Channels = (r & 0x10000000) != 0 ? 2 : 1,
                MaxBytes = (int)U(0x18 + t * 4),
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
        for (int i = 0; i < 59; ++i)
            r[i] = i + 1;
        for (int i = 59; i < 64; ++i)
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
            int v = (mD[Bit >> 3] >> (Bit & 7)) & 1;
            ++Bit;
            return v;
        }

        // Bits come least significant first.
        public int Get(int n)
        {
            int v = 0;
            for (int i = 0; i < n; ++i)
                v |= Get1() << i;
            return v;
        }
    }

    // A binary tree over a node array: Left[i] < 0 marks a leaf holding Value[i].
    private sealed class ByteTree
    {
        public readonly List<int> Left = new(), Right = new(), Value = new();

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
            int id = Left.Count;
            Left.Add(-1);
            Right.Add(-1);
            Value.Add(0);
            if (br.Get1() == 0)
            {
                Value[id] = br.Get(8);
                return id;
            }
            int l = Node(br, depth + 1);
            int r = Node(br, depth + 1);
            Left[id] = l;
            Right[id] = r;
            return id;
        }

        public int Decode(BitReader br)
        {
            int n = 0;
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
        public readonly List<int> Left = new(), Right = new(), Value = new();
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
            int id = Left.Count;
            Left.Add(-1);
            Right.Add(-1);
            Value.Add(0);
            if (br.Get1() == 0)
            {
                int lo = low?.Decode(br) ?? 0;
                int hi = high?.Decode(br) ?? 0;
                int v = lo | (hi << 8);
                int k = Array.IndexOf(esc, v);
                if (k >= 0)
                {
                    Slot[k] = id;
                    v = 0;
                }
                Value[id] = v;
                return id;
            }
            int l = Node(br, low, high, esc, depth + 1);
            int r = Node(br, low, high, esc, depth + 1);
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
            int n = 0;
            while (Left[n] >= 0)
                n = br.Get1() == 0 ? Left[n] : Right[n];
            int v = Value[n];
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
        for (int i = 0; i < 4; ++i)
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

    // Unread bits after the four trees (padding only in a good file).
    public int TreeBitsLeft { get; private set; }

    // ---- frames ----

    // Back to frame 0 with the state of a freshly opened file.
    public void Rewind()
    {
        NextFrame = 0;
        Array.Clear(Pixels);
        Array.Clear(Palette);
    }

    // Decodes the next frame into Pixels and Palette. Returns false at the end of the movie.
    public bool DecodeNext()
    {
        if (NextFrame >= FrameCount)
            return false;
        int i = NextFrame++;
        ReadOnlySpan<byte> chunk = new ReadOnlySpan<byte>(mData, mFrameStart[i], mFrameSize[i]);
        int p = 0;
        PaletteChanged = false;
        byte type = mFrameType[i];
        if ((type & 1) != 0 && chunk.Length > 0)
        {
            int len = chunk[0] * 4;
            ReadPalette(chunk.Slice(1, Math.Min(len, chunk.Length) - 1));
            PaletteChanged = true;
            p += len;
        }
        for (int t = 0; t < MaxTracks && p < chunk.Length; ++t)
        {
            if ((type & (2 << t)) == 0)
                continue;
            int alen = (int)BitConverter.ToUInt32(chunk.Slice(p, 4));
            p += Math.Max(alen, 4);
        }
        if (p < chunk.Length)
            DecodeVideo(chunk.Slice(Math.Min(p, chunk.Length)));
        return true;
    }

    private void ReadPalette(ReadOnlySpan<byte> d)
    {
        byte[] old = (byte[])Palette.Clone();
        int i = 0, j = 0;
        while (i < 256 && j < d.Length)
        {
            byte b = d[j++];
            if ((b & 0x80) != 0)
            {
                i += (b & 0x7F) + 1;
            }
            else if ((b & 0x40) != 0)
            {
                int n = (b & 0x3F) + 1;
                if (j >= d.Length)
                    break;
                int s = d[j++];
                for (int k = 0; k < n && i + k < 256 && s + k < 256; ++k)
                    Array.Copy(old, (s + k) * 3, Palette, (i + k) * 3, 3);
                i += n;
            }
            else
            {
                if (j + 1 >= d.Length)
                    break;
                int r = b & 0x3F, g = d[j++] & 0x3F, bl = d[j++] & 0x3F;
                Palette[i * 3] = Scale6(r);
                Palette[i * 3 + 1] = Scale6(g);
                Palette[i * 3 + 2] = Scale6(bl);
                ++i;
            }
        }
    }

    // 6 bit palette component to 8 bit: the high bits repeat into the low ones (0x3F -> 0xFF).
    private static byte Scale6(int v) => (byte)((v << 2) | (v >> 4));

    private void DecodeVideo(ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        var br = new BitReader(copy, 0, copy.Length);
        int bw = (Width + 3) / 4, bh = (Height + 3) / 4, blocks = bw * bh;
        int blk = 0;
        foreach (BigTree t in mTrees)
            t.ResetCache();
        try
        {
            while (blk < blocks)
            {
                int sym = mTrees[TreeType].Decode(br);
                int btype = sym & 3;
                int run = sBlockRuns[(sym >> 2) & 0x3F];
                int extra = sym >> 8;
                for (int k = 0; k < run && blk < blocks; ++k, ++blk)
                {
                    int bx = (blk % bw) * 4, by = (blk / bw) * 4;
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
        int colors = mTrees[TreeMclr].Decode(br);
        int map = mTrees[TreeMmap].Decode(br);
        byte hi = (byte)(colors >> 8), lo = (byte)colors;
        for (int row = 0; row < 4; ++row)
        {
            int y = by + row;
            for (int col = 0; col < 4; ++col)
            {
                int x = bx + col;
                if (x < Width && y < Height)
                    Pixels[y * Width + x] = (map & (1 << (row * 4 + col))) != 0 ? hi : lo;
            }
        }
    }

    private void FullBlock(BitReader br, int bx, int by)
    {
        for (int row = 0; row < 4; ++row)
        {
            int y = by + row;
            int right = mTrees[TreeFull].Decode(br);
            int left = mTrees[TreeFull].Decode(br);
            Put(bx + 2, y, (byte)right);
            Put(bx + 3, y, (byte)(right >> 8));
            Put(bx, y, (byte)left);
            Put(bx + 1, y, (byte)(left >> 8));
        }
    }

    private void Put(int x, int y, byte v)
    {
        if (x < Width && y < Height)
            Pixels[y * Width + x] = v;
    }

    private void SolidBlock(int bx, int by, byte c)
    {
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                Put(bx + col, by + row, c);
    }

    // ---- audio ----

    // Decodes the whole audio track to little endian PCM (8 bit unsigned or 16 bit signed, interleaved) without decoding any
    // video. Returns null when the track carries no audio.
    public byte[]? ReadAudio(int track)
    {
        SmackerAudioTrack a = Audio[track];
        if (!a.HasData || a.Rate <= 0)
            return null;
        var pcm = new MemoryStream();
        for (int i = 0; i < FrameCount; ++i)
        {
            if ((mFrameType[i] & (2 << track)) == 0)
                continue;
            ReadOnlySpan<byte> chunk = new ReadOnlySpan<byte>(mData, mFrameStart[i], mFrameSize[i]);
            int p = (mFrameType[i] & 1) != 0 && chunk.Length > 0 ? chunk[0] * 4 : 0;
            for (int t = 0; t < MaxTracks && p + 4 <= chunk.Length; ++t)
            {
                if ((mFrameType[i] & (2 << t)) == 0)
                    continue;
                int alen = (int)BitConverter.ToUInt32(chunk.Slice(p, 4));
                if (alen < 4 || p + alen > chunk.Length)
                    break;
                if (t == track)
                {
                    ReadOnlySpan<byte> body = chunk.Slice(p + 4, alen - 4);
                    if (a.Packed)
                    {
                        byte[]? dec = UnpackAudio(body, a);
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
        int unpacked = (int)BitConverter.ToUInt32(body.Slice(0, 4));
        if (unpacked <= 0 || unpacked > 64 * 1024 * 1024)
            return null;
        byte[] bytes = body.Slice(4).ToArray();
        var br = new BitReader(bytes, 0, bytes.Length);
        try
        {
            if (br.Get1() == 0)
                return new byte[unpacked];
            int stereo = br.Get1();
            int wide = br.Get1();
            int nTrees = 1 << (stereo + wide);
            var trees = new ByteTree[nTrees];
            for (int i = 0; i < nTrees; ++i)
            {
                if (br.Get1() == 0)
                    throw new InvalidDataException("audio tree missing");
                trees[i] = ByteTree.Read(br);
                br.Get1();
            }
            var pred = new int[2];
            for (int c = stereo; c >= 0; --c)
                pred[c] = wide == 1 ? Swap16(br.Get(16)) : br.Get(8);
            var o = new byte[unpacked];
            int bps = wide == 1 ? 2 : 1;
            int n = unpacked / bps;
            int idx = 0;
            for (int c = 0; c <= stereo && idx < n; ++c, ++idx)
                Store(o, idx, bps, pred[c]);
            for (; idx < n; ++idx)
            {
                int ch = idx & stereo;
                if (wide == 1)
                {
                    int lo = trees[ch * 2].Decode(br);
                    int hi = trees[ch * 2 + 1].Decode(br);
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
            o[idx * 2 + 1] = (byte)(v >> 8);
        }
        else
        {
            o[idx] = (byte)v;
        }
    }
}
