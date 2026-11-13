namespace OpenTribes.Net;

/// <summary>Darkstar bit stream: bits are packed LSB first (bit n is bit n&amp;7 of byte n&gt;&gt;3).</summary>
/// <remarks>Port of src/net/bitstream.cpp. Strings are byte strings (one char per byte, <see cref="OpenTribes.Core.ByteString"/>). Overruns set the error flag instead of throwing.</remarks>
public sealed class BitStream
{
    private readonly byte[] mBuf;
    private readonly ulong mLimitBits;
    private uint mPos;
    private bool mError;

    /// <summary>Creates a stream over a buffer.</summary>
    /// <param name="buffer">The buffer to read or write.</param>
    /// <param name="sizeBytes">The usable size in bytes.</param>
    public BitStream(byte[] buffer, int sizeBytes)
    {
        mBuf = buffer;
        mLimitBits = (ulong)sizeBytes * 8;
    }

    /// <summary>Creates a stream over a whole buffer.</summary>
    /// <param name="buffer">The buffer to read or write.</param>
    public BitStream(byte[] buffer) : this(buffer, buffer.Length) { }

    /// <summary>Writes bits from the start of a span. A write past the limit sets the error flag and writes nothing.</summary>
    /// <param name="bitCount">The number of bits.</param>
    /// <param name="src">The source bytes, read LSB first.</param>
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

    /// <summary>Reads bits into a span, which is zeroed first. A read past the limit sets the error flag and reads nothing.</summary>
    /// <param name="bitCount">The number of bits.</param>
    /// <param name="dst">Receives the bits LSB first.</param>
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

    /// <summary>Writes the low bits of an unsigned integer.</summary>
    /// <param name="value">The value.</param>
    /// <param name="bitCount">The number of bits, 1 to 32.</param>
    public void WriteInt(uint value, uint bitCount)
    {
        ReadOnlySpan<byte> bytes = [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
        WriteBits(bitCount, bytes);
    }

    /// <summary>Reads an unsigned integer.</summary>
    /// <param name="bitCount">The number of bits, 1 to 32.</param>
    /// <returns>The value, 0 after an error.</returns>
    public uint ReadInt(uint bitCount)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadBits(bitCount, bytes);
        uint v = bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
        return bitCount == 32 ? v : v & ((1u << (int)bitCount) - 1);
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>The flag, so writes can be chained in conditions.</returns>
    public bool WriteFlag(bool value)
    {
        WriteInt(value ? 1u : 0u, 1);
        return value;
    }

    /// <summary>Reads one bit.</summary>
    /// <returns>The flag.</returns>
    public bool ReadFlag() => ReadInt(1) != 0;

    /// <summary>Writes a sign bit followed by the magnitude.</summary>
    /// <param name="value">The value.</param>
    /// <param name="bitCount">The total bits including the sign bit.</param>
    public void WriteSignedInt(int value, uint bitCount)
    {
        if (WriteFlag(value < 0))
            WriteInt(unchecked((uint)-value), bitCount - 1);
        else
            WriteInt((uint)value, bitCount - 1);
    }

    /// <summary>Reads a sign bit followed by the magnitude.</summary>
    /// <param name="bitCount">The total bits including the sign bit.</param>
    /// <returns>The value.</returns>
    public int ReadSignedInt(uint bitCount)
    {
        bool negative = ReadFlag();
        int magnitude = unchecked((int)ReadInt(bitCount - 1));
        return negative ? -magnitude : magnitude;
    }

    /// <summary>Writes whole bytes at the current bit position.</summary>
    /// <param name="src">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> src) => WriteBits((uint)(src.Length * 8), src);

    /// <summary>Reads whole bytes from the current bit position.</summary>
    /// <param name="dst">Receives the bytes.</param>
    public void ReadBytes(Span<byte> dst) => ReadBits((uint)(dst.Length * 8), dst);

    /// <summary>Writes a string as a compression flag, an 8-bit length and either Huffman codes or raw bytes, whichever is shorter.</summary>
    /// <param name="s">The byte string.</param>
    /// <param name="maxLen">The maximum characters to write, at most 255.</param>
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

    /// <summary>Reads a string written by <see cref="WriteString"/>.</summary>
    /// <returns>The byte string. Empty when a raw string does not fit in the stream (DECISIONS D20).</returns>
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

    /// <summary>Gets the current bit position.</summary>
    /// <returns>The position in bits.</returns>
    public uint BitPosition() => mPos;

    /// <summary>Moves to a bit position.</summary>
    /// <param name="pos">The position in bits.</param>
    public void SetBitPosition(uint pos) => mPos = pos;

    /// <summary>Gets the current position rounded up to whole bytes (getBytePtr).</summary>
    /// <returns>The position in bytes.</returns>
    public int BytePosition() => (int)((mPos + 7) / 8);

    /// <summary>Tells whether a read or write ran past the limit.</summary>
    /// <returns>True after an overrun.</returns>
    public bool Error() => mError;
}
