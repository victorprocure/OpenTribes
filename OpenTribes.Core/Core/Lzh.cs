namespace OpenTribes.Core;

/// <summary>Adaptive-Huffman LZSS (LZHUF) decoder used by volume compression type 3.</summary>
/// <remarks>Port of src/core/lzh.cpp.</remarks>
public static class Lzh
{
    // src/core/lzh.cpp constants.
    private const int kRingSize = 4096;
    private const int kMaxMatch = 60;
    private const int kThreshold = 2;
    private const int kNumChars = 256 - kThreshold + kMaxMatch; // 314
    private const int kTableSize = kNumChars * 2 - 1;           // 627
    private const int kRoot = kTableSize - 1;
    private const ushort kMaxFreq = 0x8000;

    // src/core/lzh.cpp dCode: position decoding table.
    private static byte DCode(int i)
    {
        if (i < 32) return 0;
        if (i < 80) return (byte)(1 + (i - 32) / 16);
        if (i < 144) return (byte)(4 + (i - 80) / 8);
        if (i < 192) return (byte)(12 + (i - 144) / 4);
        if (i < 240) return (byte)(24 + (i - 192) / 2);
        return (byte)(48 + (i - 240));
    }

    // src/core/lzh.cpp dLen.
    private static byte DLen(int i)
    {
        if (i < 32) return 3;
        if (i < 80) return 4;
        if (i < 144) return 5;
        if (i < 192) return 6;
        if (i < 240) return 7;
        return 8;
    }

    // src/core/lzh.cpp class Decoder.
    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> mIn;
        private int mPos;
        private ushort mBits;
        private int mBitCount;
        private readonly ushort[] mFreq;
        private readonly ushort[] mParent;
        private readonly ushort[] mSon;

        // src/core/lzh.cpp Decoder::Decoder.
        public Decoder(ReadOnlySpan<byte> input)
        {
            mIn = input;
            mPos = 0;
            mBits = 0;
            mBitCount = 0;
            mFreq = new ushort[kTableSize + 1];
            mParent = new ushort[kTableSize + kNumChars];
            mSon = new ushort[kTableSize];
            for (int i = 0; i < kNumChars; ++i)
            {
                mFreq[i] = 1;
                mSon[i] = (ushort)(i + kTableSize);
                mParent[i + kTableSize] = (ushort)i;
            }
            for (int i = 0, j = kNumChars; j <= kRoot; i += 2, ++j)
            {
                mFreq[j] = (ushort)(mFreq[i] + mFreq[i + 1]);
                mSon[j] = (ushort)i;
                mParent[i] = mParent[i + 1] = (ushort)j;
            }
            mFreq[kTableSize] = 0xffff;
            mParent[kRoot] = 0;
        }

        // src/core/lzh.cpp Decoder::decodeChar.
        public int DecodeChar()
        {
            int c = mSon[kRoot];
            while (c < kTableSize)
                c = mSon[c + GetBit()];
            c -= kTableSize;
            Update(c);
            return c;
        }

        // src/core/lzh.cpp Decoder::consumed.
        public readonly int Consumed() => mPos;

        // src/core/lzh.cpp Decoder::decodePosition.
        public int DecodePosition()
        {
            int i = GetByte();
            int c = DCode(i) << 6;
            for (int j = DLen(i) - 2; j > 0; --j)
                i = (i << 1) + GetBit();
            return c | (i & 0x3f);
        }

        // src/core/lzh.cpp Decoder::nextInput: input past the end reads as zero.
        private byte NextInput() => mPos < mIn.Length ? mIn[mPos++] : (byte)0;

        // src/core/lzh.cpp Decoder::fill.
        private void Fill()
        {
            while (mBitCount <= 8)
            {
                mBits = (ushort)(mBits | (NextInput() << (8 - mBitCount)));
                mBitCount += 8;
            }
        }

        // src/core/lzh.cpp Decoder::getBit.
        private int GetBit()
        {
            Fill();
            int bit = mBits >> 15;
            mBits = (ushort)(mBits << 1);
            --mBitCount;
            return bit;
        }

        // src/core/lzh.cpp Decoder::getByte.
        private int GetByte()
        {
            Fill();
            int b = mBits >> 8;
            mBits = (ushort)(mBits << 8);
            mBitCount -= 8;
            return b;
        }

        // src/core/lzh.cpp Decoder::reconstruct: halves frequencies and rebuilds the tree.
        private readonly void Reconstruct()
        {
            int j = 0;
            for (int i = 0; i < kTableSize; ++i)
            {
                if (mSon[i] >= kTableSize)
                {
                    mFreq[j] = (ushort)((mFreq[i] + 1) / 2);
                    mSon[j] = mSon[i];
                    ++j;
                }
            }
            for (int i = 0, jj = kNumChars; jj < kTableSize; i += 2, ++jj)
            {
                ushort f = (ushort)(mFreq[i] + mFreq[i + 1]);
                mFreq[jj] = f;
                int k = jj - 1;
                while (f < mFreq[k])
                    --k;
                ++k;
                Array.Copy(mFreq, k, mFreq, k + 1, jj - k);
                mFreq[k] = f;
                Array.Copy(mSon, k, mSon, k + 1, jj - k);
                mSon[k] = (ushort)i;
            }
            for (int i = 0; i < kTableSize; ++i)
            {
                int k = mSon[i];
                if (k >= kTableSize)
                    mParent[k] = (ushort)i;
                else
                    mParent[k] = mParent[k + 1] = (ushort)i;
            }
        }

        // src/core/lzh.cpp Decoder::update: increments the symbol's frequency and keeps the tree ordered.
        private readonly void Update(int c)
        {
            if (mFreq[kRoot] == kMaxFreq)
                Reconstruct();
            c = mParent[c + kTableSize];
            do
            {
                ushort k = ++mFreq[c];
                int l = c + 1;
                if (k > mFreq[l])
                {
                    while (k > mFreq[l + 1])
                        ++l;
                    mFreq[c] = mFreq[l];
                    mFreq[l] = k;

                    int i = mSon[c];
                    mParent[i] = (ushort)l;
                    if (i < kTableSize)
                        mParent[i + 1] = (ushort)l;

                    int j = mSon[l];
                    mSon[l] = (ushort)i;
                    mParent[j] = (ushort)c;
                    if (j < kTableSize)
                        mParent[j + 1] = (ushort)c;
                    mSon[c] = (ushort)j;
                    c = l;
                }
                c = mParent[c];
            } while (c != 0);
        }
    }

    /// <summary>Decodes exactly <paramref name="output"/>.Length bytes. Input past the end reads as zero.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="output">Receives the decoded bytes.</param>
    public static void LzhDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        LzhDecodeCounted(input, output);
    }

    /// <summary>Decodes like <see cref="LzhDecode"/> and reports how much input was used.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="output">Receives the decoded bytes.</param>
    /// <returns>The number of input bytes consumed.</returns>
    public static int LzhDecodeCounted(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var dec = new Decoder(input);
        Span<byte> ring = stackalloc byte[kRingSize];
        ring.Fill((byte)' ');
        int r = kRingSize - kMaxMatch;

        int n = 0;
        while (n < output.Length)
        {
            int c = dec.DecodeChar();
            if (c < 256)
            {
                output[n++] = (byte)c;
                ring[r] = (byte)c;
                r = (r + 1) & (kRingSize - 1);
                continue;
            }
            int start = (r - dec.DecodePosition() - 1) & (kRingSize - 1);
            int length = c - 255 + kThreshold;
            for (int k = 0; k < length && n < output.Length; ++k)
            {
                byte b = ring[(start + k) & (kRingSize - 1)];
                output[n++] = b;
                ring[r] = b;
                r = (r + 1) & (kRingSize - 1);
            }
        }
        return dec.Consumed();
    }
}
