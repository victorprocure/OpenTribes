// Port of tests/event_reader.hpp: test helpers: read an event body or a packet with the BitStream reader.
using OpenTribes.Game;
using OpenTribes.Net;

namespace OpenTribes.Tests;

// tests/event_reader.hpp class Bits: reads bits from a copy of a byte buffer; Done() checks that exactly `bits` were
// consumed.
public sealed class Bits
{
    private readonly byte[] mBuf;
    private readonly uint mBits;
    private readonly BitStream mStream;

    public Bits(IReadOnlyList<byte> data, uint bits)
    {
        mBuf = new byte[data.Count + 8];
        for (int i = 0; i < data.Count; ++i)
            mBuf[i] = data[i];
        mBits = bits;
        mStream = new BitStream(mBuf, mBuf.Length);
    }

    public Bits(OutEvent e) : this(e.Body, e.BodyBits) { }

    public uint U(uint n) => mStream.ReadInt(n);
    public int S32() => (int)mStream.ReadInt(32);
    public bool Flag() => mStream.ReadFlag();
    public string Str() => mStream.ReadString();
    public float F32() => BitConverter.UInt32BitsToSingle(mStream.ReadInt(32));
    public uint Pos() => mStream.BitPosition();
    public uint Left() => mBits - mStream.BitPosition();
    public bool Done() => mStream.BitPosition() == mBits && !mStream.Error();

    // tests/event_reader.hpp strBits: bit count of a string as the stream writes it.
    public static uint StrBits(string s)
    {
        byte[] buf = new byte[1024];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteString(s);
        return bs.BitPosition();
    }
}
