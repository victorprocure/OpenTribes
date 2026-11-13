// Port of tests/event_reader.hpp: test helpers: read an event body or a packet with the BitStream reader.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Net;

// tests/event_reader.hpp class Bits: reads bits from a copy of a byte buffer; Done() checks that exactly `bits` were
// consumed.
public sealed class Bits
{
    private readonly byte[] _buf;
    private readonly uint _bits;
    private readonly BitBuffer _stream;

    public Bits(IReadOnlyList<byte> data, uint bits)
    {
        _buf = new byte[data.Count + 8];
        for (var i = 0; i < data.Count; ++i)
            _buf[i] = data[i];
        _bits = bits;
        _stream = new BitBuffer(_buf, _buf.Length);
    }

    public Bits(OutEvent e) : this(e.Body, e.BodyBits) { }

    public uint U(uint n) => _stream.ReadInt(n);
    public int S32() => (int)_stream.ReadInt(32);
    public bool Flag() => _stream.ReadFlag();
    public string Str() => _stream.ReadString();
    public float F32() => BitConverter.UInt32BitsToSingle(_stream.ReadInt(32));
    public uint Pos() => _stream.BitPosition();
    public uint Left() => _bits - _stream.BitPosition();
    public bool Done() => _stream.BitPosition() == _bits && !_stream.Error();

    // tests/event_reader.hpp strBits: bit count of a string as the stream writes it.
    public static uint StrBits(string s)
    {
        var buf = new byte[1024];
        var bs = new BitBuffer(buf, buf.Length);
        bs.WriteString(s);
        return bs.BitPosition();
    }
}
