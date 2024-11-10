// Port of src/net/bitstream.hpp and src/net/bitstream.cpp.
// Darkstar BitStream: bits are packed LSB first (bit n is bit n&7 of byte n>>3).
// Strings are byte strings (one char per byte, OpenTribes.Core.ByteString).
namespace OpenTribes.Net;

public sealed class BitStream
{
    private readonly byte[] mBuf;
    private readonly ulong mLimitBits;
    private uint mPos;
    private bool mError;

    // src/net/bitstream.hpp BitStream::BitStream.
    public BitStream(byte[] buffer, int sizeBytes)
    {
        mBuf = buffer;
        mLimitBits = (ulong)sizeBytes * 8;
    }

    public BitStream(byte[] buffer) : this(buffer, buffer.Length) { }

    // src/net/bitstream.cpp BitStream::writeBits.
    public void WriteBits(uint bitCount, ReadOnlySpan<byte> src)
    {
        if ((ulong)(mPos + bitCount) > mLimitBits)
        {
            mError = true;
            return;
        }
        for (uint i = 0; i < bitCount; ++i, ++mPos)
        {
            byte mask = (byte)(1u << (int)(mPos & 7));
            if (((src[(int)(i >> 3)] >> (int)(i & 7)) & 1) != 0)
                mBuf[mPos >> 3] |= mask;
            else
                mBuf[mPos >> 3] &= (byte)~mask;
        }
    }

    // src/net/bitstream.cpp BitStream::readBits.
    public void ReadBits(uint bitCount, Span<byte> dst)
    {
        for (uint i = 0; i < (bitCount + 7) / 8; ++i)
            dst[(int)i] = 0;
        if ((ulong)(mPos + bitCount) > mLimitBits)
        {
            mError = true;
            return;
        }
        for (uint i = 0; i < bitCount; ++i, ++mPos)
            if (((mBuf[mPos >> 3] >> (int)(mPos & 7)) & 1) != 0)
                dst[(int)(i >> 3)] |= (byte)(1u << (int)(i & 7));
    }

    // src/net/bitstream.cpp BitStream::writeInt.
    public void WriteInt(uint value, uint bitCount)
    {
        ReadOnlySpan<byte> bytes = stackalloc byte[4] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
        WriteBits(bitCount, bytes);
    }

    // src/net/bitstream.cpp BitStream::readInt.
    public uint ReadInt(uint bitCount)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadBits(bitCount, bytes);
        uint v = bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
        return bitCount == 32 ? v : v & ((1u << (int)bitCount) - 1);
    }

    // src/net/bitstream.cpp BitStream::writeFlag.
    public bool WriteFlag(bool value)
    {
        WriteInt(value ? 1u : 0u, 1);
        return value;
    }

    // src/net/bitstream.cpp BitStream::readFlag.
    public bool ReadFlag() => ReadInt(1) != 0;

    // src/net/bitstream.cpp BitStream::writeSignedInt.
    public void WriteSignedInt(int value, uint bitCount)
    {
        if (WriteFlag(value < 0))
            WriteInt(unchecked((uint)-value), bitCount - 1);
        else
            WriteInt((uint)value, bitCount - 1);
    }

    // src/net/bitstream.cpp BitStream::readSignedInt.
    public int ReadSignedInt(uint bitCount)
    {
        bool negative = ReadFlag();
        int magnitude = unchecked((int)ReadInt(bitCount - 1));
        return negative ? -magnitude : magnitude;
    }

    // src/net/bitstream.hpp BitStream::writeBytes.
    public void WriteBytes(ReadOnlySpan<byte> src) => WriteBits((uint)(src.Length * 8), src);

    // src/net/bitstream.hpp BitStream::readBytes.
    public void ReadBytes(Span<byte> dst) => ReadBits((uint)(dst.Length * 8), dst);

    // src/net/bitstream.cpp BitStream::writeString: Huffman-or-raw string, at most maxLen (<= 255) characters.
    public void WriteString(string s, int maxLen = 255)
    {
        int len = s.Length < maxLen ? s.Length : maxLen;
        if (len > 255)
            len = 255;
        uint huffBits = 0;
        for (int i = 0; i < len; ++i)
            huffBits += Huffman.HuffmanCode((byte)s[i]).Length;

        bool compressed = huffBits < (uint)len * 8;
        WriteFlag(compressed);
        WriteInt((uint)len, 8);
        if (!compressed)
        {
            Span<byte> raw = stackalloc byte[len];
            for (int i = 0; i < len; ++i)
                raw[i] = (byte)s[i];
            WriteBytes(raw);
            return;
        }
        for (int i = 0; i < len; ++i)
        {
            HuffmanCode code = Huffman.HuffmanCode((byte)s[i]);
            for (int b = 0; b < code.Length; ++b)
                WriteFlag(((code.Bits >> b) & 1) != 0);
        }
    }

    // src/net/bitstream.cpp BitStream::readString.
    public string ReadString()
    {
        bool compressed = ReadFlag();
        uint len = ReadInt(8);
        var output = new System.Text.StringBuilder((int)len);
        if (compressed)
        {
            // All `len` characters are decoded: past the end every bit reads 0, so the remaining
            // characters are the one whose code is all zeros ('t').
            for (uint i = 0; i < len; ++i)
                output.Append((char)Huffman.HuffmanDecode(ReadFlag));
            return output.ToString();
        }
        // Raw: one read of all the bytes. When they do not fit nothing is read (DECISIONS D20: empty here).
        if ((ulong)(mPos + len * 8) > mLimitBits)
        {
            mError = true;
            return output.ToString();
        }
        for (uint i = 0; i < len; ++i)
            output.Append((char)ReadInt(8));
        return output.ToString();
    }

    // src/net/bitstream.hpp bitPosition.
    public uint BitPosition() => mPos;

    // src/net/bitstream.hpp setBitPosition.
    public void SetBitPosition(uint pos) => mPos = pos;

    // src/net/bitstream.hpp bytePosition (getBytePtr).
    public int BytePosition() => (int)((mPos + 7) / 8);

    // src/net/bitstream.hpp error.
    public bool Error() => mError;
}
