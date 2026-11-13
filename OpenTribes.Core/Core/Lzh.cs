namespace OpenTribes.Core;

/// <summary>Adaptive-Huffman LZSS (LZHUF) decoder used by volume compression type 3.</summary>
/// <remarks>Port of src/core/lzh.cpp.</remarks>
public static class Lzh
{
    // src/core/lzh.cpp constants.
    private const int RingSize = 4096;
    private const int MaxMatch = 60;
    private const int Threshold = 2;
    private const int NumChars = 256 - Threshold + MaxMatch; // 314
    private const int TableSize = (NumChars * 2) - 1;           // 627
    private const int Root = TableSize - 1;
    private const ushort MaxFreq = 0x8000;

    // src/core/lzh.cpp dCode: position decoding table.
    private static byte DCode(int i)
    {
        if (i < 32)
            return 0;
        if (i < 80)
            return (byte)(1 + ((i - 32) / 16));
        if (i < 144)
            return (byte)(4 + ((i - 80) / 8));
        if (i < 192)
            return (byte)(12 + ((i - 144) / 4));
        if (i < 240)
            return (byte)(24 + ((i - 192) / 2));
        return (byte)(48 + (i - 240));
    }

    // src/core/lzh.cpp dLen.
    private static byte DLen(int i)
    {
        if (i < 32)
            return 3;
        if (i < 80)
            return 4;
        if (i < 144)
            return 5;
        if (i < 192)
            return 6;
        if (i < 240)
            return 7;
        return 8;
    }

    // src/core/lzh.cpp class Decoder.
    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> _in;
        private int _pos;
        private ushort _bits;
        private int _bitCount;
        private readonly ushort[] _freq;
        private readonly ushort[] _parent;
        private readonly ushort[] _son;

        // src/core/lzh.cpp Decoder::Decoder.
        public Decoder(ReadOnlySpan<byte> input)
        {
            _in = input;
            _pos = 0;
            _bits = 0;
            _bitCount = 0;
            _freq = new ushort[TableSize + 1];
            _parent = new ushort[TableSize + NumChars];
            _son = new ushort[TableSize];
            for (var i = 0; i < NumChars; ++i)
            {
                _freq[i] = 1;
                _son[i] = (ushort)(i + TableSize);
                _parent[i + TableSize] = (ushort)i;
            }
            for (int i = 0, j = NumChars; j <= Root; i += 2, ++j)
            {
                _freq[j] = (ushort)(_freq[i] + _freq[i + 1]);
                _son[j] = (ushort)i;
                _parent[i] = _parent[i + 1] = (ushort)j;
            }
            _freq[TableSize] = 0xffff;
            _parent[Root] = 0;
        }

        // src/core/lzh.cpp Decoder::decodeChar.
        public int DecodeChar()
        {
            int c = _son[Root];
            while (c < TableSize)
                c = _son[c + GetBit()];
            c -= TableSize;
            Update(c);
            return c;
        }

        // src/core/lzh.cpp Decoder::consumed.
        public readonly int Consumed() => _pos;

        // src/core/lzh.cpp Decoder::decodePosition.
        public int DecodePosition()
        {
            var i = GetByte();
            var c = DCode(i) << 6;
            for (var j = DLen(i) - 2; j > 0; --j)
                i = (i << 1) + GetBit();
            return c | (i & 0x3f);
        }

        // src/core/lzh.cpp Decoder::nextInput: input past the end reads as zero.
        private byte NextInput() => _pos < _in.Length ? _in[_pos++] : (byte)0;

        // src/core/lzh.cpp Decoder::fill.
        private void Fill()
        {
            while (_bitCount <= 8)
            {
                _bits = (ushort)(_bits | (NextInput() << (8 - _bitCount)));
                _bitCount += 8;
            }
        }

        // src/core/lzh.cpp Decoder::getBit.
        private int GetBit()
        {
            Fill();
            var bit = _bits >> 15;
            _bits = (ushort)(_bits << 1);
            --_bitCount;
            return bit;
        }

        // src/core/lzh.cpp Decoder::getByte.
        private int GetByte()
        {
            Fill();
            var b = _bits >> 8;
            _bits = (ushort)(_bits << 8);
            _bitCount -= 8;
            return b;
        }

        // src/core/lzh.cpp Decoder::reconstruct: halves frequencies and rebuilds the tree.
        private readonly void Reconstruct()
        {
            var j = 0;
            for (var i = 0; i < TableSize; ++i)
            {
                if (_son[i] >= TableSize)
                {
                    _freq[j] = (ushort)((_freq[i] + 1) / 2);
                    _son[j] = _son[i];
                    ++j;
                }
            }
            for (int i = 0, jj = NumChars; jj < TableSize; i += 2, ++jj)
            {
                var f = (ushort)(_freq[i] + _freq[i + 1]);
                _freq[jj] = f;
                var k = jj - 1;
                while (f < _freq[k])
                    --k;
                ++k;
                Array.Copy(_freq, k, _freq, k + 1, jj - k);
                _freq[k] = f;
                Array.Copy(_son, k, _son, k + 1, jj - k);
                _son[k] = (ushort)i;
            }
            for (var i = 0; i < TableSize; ++i)
            {
                int k = _son[i];
                if (k >= TableSize)
                    _parent[k] = (ushort)i;
                else
                    _parent[k] = _parent[k + 1] = (ushort)i;
            }
        }

        // src/core/lzh.cpp Decoder::update: increments the symbol's frequency and keeps the tree ordered.
        private readonly void Update(int c)
        {
            if (_freq[Root] == MaxFreq)
                Reconstruct();
            c = _parent[c + TableSize];
            do
            {
                var k = ++_freq[c];
                var l = c + 1;
                if (k > _freq[l])
                {
                    while (k > _freq[l + 1])
                        ++l;
                    _freq[c] = _freq[l];
                    _freq[l] = k;

                    int i = _son[c];
                    _parent[i] = (ushort)l;
                    if (i < TableSize)
                        _parent[i + 1] = (ushort)l;

                    int j = _son[l];
                    _son[l] = (ushort)i;
                    _parent[j] = (ushort)c;
                    if (j < TableSize)
                        _parent[j + 1] = (ushort)c;
                    _son[c] = (ushort)j;
                    c = l;
                }
                c = _parent[c];
            } while (c != 0);
        }
    }

    /// <summary>Decodes exactly <paramref name="output"/>.Length bytes. Input past the end reads as zero.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="output">Receives the decoded bytes.</param>
    public static void LzhDecode(ReadOnlySpan<byte> input, Span<byte> output) => LzhDecodeCounted(input, output);

    /// <summary>Decodes like <see cref="LzhDecode"/> and reports how much input was used.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="output">Receives the decoded bytes.</param>
    /// <returns>The number of input bytes consumed.</returns>
    public static int LzhDecodeCounted(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var dec = new Decoder(input);
        Span<byte> ring = stackalloc byte[RingSize];
        ring.Fill((byte)' ');
        var r = RingSize - MaxMatch;

        var n = 0;
        while (n < output.Length)
        {
            var c = dec.DecodeChar();
            if (c < 256)
            {
                output[n++] = (byte)c;
                ring[r] = (byte)c;
                r = (r + 1) & (RingSize - 1);
                continue;
            }
            var start = (r - dec.DecodePosition() - 1) & (RingSize - 1);
            var length = c - 255 + Threshold;
            for (var k = 0; k < length && n < output.Length; ++k)
            {
                var b = ring[(start + k) & (RingSize - 1)];
                output[n++] = b;
                ring[r] = b;
                r = (r + 1) & (RingSize - 1);
            }
        }
        return dec.Consumed();
    }
}
