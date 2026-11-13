// Port of tests/test_lzh.cpp: LZH (adaptive-Huffman LZSS) decoder tests against a synthetic encoder
// written from the stream description in docs/terrain.md 1.4 (4096-byte ring of spaces, r = 4036,
// 60-byte matches, threshold 2, 314-symbol adaptive tree, byte-wise read-ahead).
namespace OpenTribes.Tests;

using OpenTribes.Core;

public class LzhTests
{
    private const int kN = 4096;
    private const int kF = 60;
    private const int kThreshold = 2;
    private const int kNumChars = 256 - kThreshold + kF; // 314
    private const int kT = (kNumChars * 2) - 1;            // 627
    private const int kR = kT - 1;
    private const ushort kMaxFreq = 0x8000;

    // Encoder-side copy of the adaptive Huffman tree (same update rules as the decoder).
    private sealed class Tree
    {
        public readonly ushort[] Freq = new ushort[kT + 1];
        public readonly ushort[] Parent = new ushort[kT + kNumChars];
        public readonly ushort[] Son = new ushort[kT];

        public Tree()
        {
            for (var i = 0; i < kNumChars; ++i)
            {
                Freq[i] = 1;
                Son[i] = (ushort)(i + kT);
                Parent[i + kT] = (ushort)i;
            }
            for (int i = 0, j = kNumChars; j <= kR; i += 2, ++j)
            {
                Freq[j] = (ushort)(Freq[i] + Freq[i + 1]);
                Son[j] = (ushort)i;
                Parent[i] = Parent[i + 1] = (ushort)j;
            }
            Freq[kT] = 0xffff;
            Parent[kR] = 0;
        }

        private void Reconstruct()
        {
            var j = 0;
            for (var i = 0; i < kT; ++i)
                if (Son[i] >= kT)
                {
                    Freq[j] = (ushort)((Freq[i] + 1) / 2);
                    Son[j] = Son[i];
                    ++j;
                }
            for (int i = 0, k2 = kNumChars; k2 < kT; i += 2, ++k2)
            {
                var f = (ushort)(Freq[i] + Freq[i + 1]);
                Freq[k2] = f;
                var k = k2 - 1;
                while (f < Freq[k])
                    --k;
                ++k;
                Array.Copy(Freq, k, Freq, k + 1, k2 - k);
                Freq[k] = f;
                Array.Copy(Son, k, Son, k + 1, k2 - k);
                Son[k] = (ushort)i;
            }
            for (var i = 0; i < kT; ++i)
            {
                int k = Son[i];
                if (k >= kT)
                    Parent[k] = (ushort)i;
                else
                    Parent[k] = Parent[k + 1] = (ushort)i;
            }
        }

        public void Update(int c)
        {
            if (Freq[kR] == kMaxFreq)
                Reconstruct();
            c = Parent[c + kT];
            do
            {
                var k = ++Freq[c];
                var l = c + 1;
                if (k > Freq[l])
                {
                    while (k > Freq[l + 1])
                        ++l;
                    Freq[c] = Freq[l];
                    Freq[l] = k;
                    int i = Son[c];
                    Parent[i] = (ushort)l;
                    if (i < kT)
                        Parent[i + 1] = (ushort)l;
                    int j = Son[l];
                    Son[l] = (ushort)i;
                    Parent[j] = (ushort)c;
                    if (j < kT)
                        Parent[j + 1] = (ushort)c;
                    Son[c] = (ushort)j;
                    c = l;
                }
                c = Parent[c];
            } while (c != 0);
        }
    }

    private sealed class BitWriter
    {
        public readonly List<byte> Out = [];
        private byte mCur;
        private int mCount;

        public void Put(uint value, int bits) // MSB first
        {
            for (var i = bits - 1; i >= 0; --i)
            {
                mCur = (byte)((mCur << 1) | (int)((value >> i) & 1));
                if (++mCount == 8)
                {
                    Out.Add(mCur);
                    mCur = 0;
                    mCount = 0;
                }
            }
        }

        public void Flush()
        {
            if (mCount != 0)
                Out.Add((byte)(mCur << (8 - mCount)));
            mCur = 0;
            mCount = 0;
        }
    }

    // Position code tables inverted from the decoder's.
    private static int DLen(int i) => i < 32 ? 3 : i < 80 ? 4 : i < 144 ? 5 : i < 192 ? 6 : i < 240 ? 7 : 8;

    private static int DCode(int i)
    {
        if (i < 32)
            return 0;
        if (i < 80)
            return 1 + ((i - 32) / 16);
        if (i < 144)
            return 4 + ((i - 80) / 8);
        if (i < 192)
            return 12 + ((i - 144) / 4);
        if (i < 240)
            return 24 + ((i - 192) / 2);
        return 48 + (i - 240);
    }

    private sealed class Encoded
    {
        public byte[] Bytes = [];
        public int Matches;
        public int Symbols;
    }

    // Greedy LZSS over a virtual history of 4096 spaces, matches searched up to `window` back.
    private static Encoded Encode(byte[] input, int window)
    {
        var tree = new Tree();
        var bw = new BitWriter();
        var e = new Encoded();
        void PutChar(int c)
        {
            uint code = 0;
            var len = 0;
            for (int k = tree.Parent[c + kT]; k != kR; k = tree.Parent[k])
            {
                code |= (uint)(k - tree.Son[tree.Parent[k]]) << len;
                ++len;
            }
            bw.Put(code, len);
            tree.Update(c);
            ++e.Symbols;
        }
        byte ByteAt(long i) => i < 0 ? (byte)' ' : input[i];
        var n = 0;
        while (n < input.Length)
        {
            int bestLen = 0, bestDist = 0;
            for (var d = 1; d <= window && d <= kN; ++d)
            {
                var len = 0;
                while (len < kF && n + len < input.Length && ByteAt((long)n + len - d) == input[n + len])
                    ++len;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestDist = d;
                }
            }
            if (bestLen > kThreshold)
            {
                PutChar(bestLen + 255 - kThreshold);
                var pos = bestDist - 1; // decoder: start = r - pos - 1
                var upper = pos >> 6;
                var i0 = 0;
                while (DCode(i0) != upper)
                    ++i0;
                var len = DLen(i0);
                bw.Put((uint)(((i0 >> (8 - len)) << 6) | (pos & 0x3f)), len + 6);
                n += bestLen;
                ++e.Matches;
            }
            else
            {
                PutChar(input[n]);
                ++n;
            }
        }
        bw.Flush();
        e.Bytes = [.. bw.Out];
        return e;
    }

    private static byte[] Decode(byte[] enc, int size) => Decode(enc, size, out _);

    private static byte[] Decode(byte[] enc, int size, out int consumed)
    {
        var output = new byte[size];
        consumed = Lzh.LzhDecodeCounted(enc, output);
        return output;
    }

    private uint mSeed = 12345;

    private uint Rnd()
    {
        mSeed = (mSeed * 1103515245u) + 12345u;
        return mSeed >> 16;
    }

    private static byte[] Bytes(string s) => ByteString.ToBytes(s);

    [Fact]
    public void TestLzh()
    {
        var cases = 0;

        // Literals: every byte value through the adaptive tree.
        {
            var input = new byte[256];
            for (var i = 0; i < 256; ++i)
                input[i] = (byte)i;
            var e = Encode(input, 0);
            Assert.Equal(input, Decode(e.Bytes, input.Length));
            ++cases;
        }

        // Matches: repeated text, max-length (60) copies, overlapping copies (distance 1) and
        // copies from the initial ring of spaces (input starting with spaces).
        {
            var text = "                    leading spaces come from the ring. ";
            for (var i = 0; i < 40; ++i)
                text += "Starsiege Tribes LZH section " + (i % 7) + " ";
            text += new string('z', 200);
            var input = Bytes(text);
            var e = Encode(input, kN);
            Assert.True(e.Matches > 10 && e.Bytes.Length < input.Length / 2);
            Assert.Equal(input, Decode(e.Bytes, input.Length));
            ++cases;
        }

        // Maximum distance: a match exactly 4096 bytes back (position code 4095).
        {
            var input = new List<byte>();
            for (var i = 0; i < kN; ++i)
                input.Add((byte)('a' + (Rnd() % 26)));
            input.AddRange(input.GetRange(0, 40));
            var inArr = input.ToArray();
            var e = Encode(inArr, kN);
            Assert.Equal(inArr, Decode(e.Bytes, inArr.Length));
            ++cases;
        }

        // More than 32767 symbols: the root frequency reaches 0x8000 and the tree is rebuilt.
        {
            var input = new byte[40000];
            for (var i = 0; i < 40000; ++i)
                input[i] = Rnd() % 7 == 0 ? (byte)'e' : (byte)Rnd();
            var e = Encode(input, 0);
            Assert.True(e.Symbols >= 32768);
            Assert.Equal(input, Decode(e.Bytes, input.Length));
            ++cases;
        }

        // terrain.md 1.4: the decoder pulls bytes while its bit buffer holds 8 bits or fewer, so it
        // consumes the encoded bytes plus at most 2 bytes of read-ahead; a following field is untouched.
        {
            var text = "";
            for (var i = 0; i < 50; ++i)
                text += "section " + i + ";";
            var input = Bytes(text);
            var e = Encode(input, kN);
            var stream = new List<byte>(e.Bytes);
            stream.AddRange(Enumerable.Repeat((byte)0xee, 16)); // next section
            Assert.Equal(input, Decode([.. stream], input.Length, out var used));
            Assert.True(used >= e.Bytes.Length && used <= e.Bytes.Length + 2);
            ++cases;
        }

        // lzh.hpp: input past inSize reads as zero bits.
        {
            var zeros = new byte[64];
            byte[] a = new byte[100], b = new byte[100];
            Lzh.LzhDecode(zeros, a);
            Lzh.LzhDecode(zeros.AsSpan(0, 0), b);
            Assert.Equal(a, b);
            ++cases;
        }

    }
}
