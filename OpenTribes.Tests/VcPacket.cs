// Port of tests/vc_packet.hpp: builds and parses VC packets (docs/net-protocol.md 3.2) independently
// of Connection, so tests check the wire format against the spec.
using OpenTribes.Core;
using OpenTribes.Net;

namespace OpenTribes.Tests;

internal sealed class VcPacket
{
    public uint IdBit;
    public uint Seq;      // 9 bits
    public uint AckAck;   // 5 bits
    public List<(uint count, uint start)> Ranges = new();
    public uint Type;     // 5 bits including flags 0x08 / 0x10
    public bool HasId;
    public uint Id;
    public byte[] Payload = Array.Empty<byte>();
    public int HeaderBytes;

    // tests/vc_packet.hpp carriesId.
    public static bool CarriesId(uint type)
    {
        uint b = type & ~0x08u;
        return b >= 2 && b <= 5;
    }

    // tests/vc_packet.hpp build.
    public static byte[] Build(VcPacket p)
    {
        byte[] buf = new byte[1500];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteFlag(true);
        bs.WriteInt(p.IdBit, 1);
        bs.WriteInt(p.Seq, 9);
        bs.WriteInt(p.AckAck, 5);
        foreach (var (count, start) in p.Ranges)
        {
            bs.WriteInt(count, 3);
            bs.WriteInt(start, 5);
        }
        bs.WriteInt(0, 3);
        bs.WriteInt(p.Type, 5);
        if (CarriesId(p.Type & ~0x10u))
            bs.WriteInt(p.Id, 32);
        var output = new List<byte>(buf.AsSpan(0, bs.BytePosition()).ToArray());
        output.AddRange(p.Payload);
        return output.ToArray();
    }

    // tests/vc_packet.hpp parse.
    public static VcPacket Parse(byte[] data)
    {
        byte[] copy = (byte[])data.Clone();
        var bs = new BitStream(copy, copy.Length);
        var p = new VcPacket();
        bs.ReadFlag();
        p.IdBit = bs.ReadInt(1);
        p.Seq = bs.ReadInt(9);
        p.AckAck = bs.ReadInt(5);
        uint count;
        while ((count = bs.ReadInt(3)) != 0)
        {
            uint start = bs.ReadInt(5);
            p.Ranges.Add((count, start));
        }
        p.Type = bs.ReadInt(5);
        if (CarriesId(p.Type & ~0x10u))
        {
            p.HasId = true;
            p.Id = bs.ReadInt(32);
        }
        p.HeaderBytes = bs.BytePosition();
        p.Payload = data.AsSpan(p.HeaderBytes).ToArray();
        return p;
    }

    // tests/vc_packet.hpp bytes.
    public static byte[] Bytes(string s, bool withNul = false)
    {
        byte[] v = ByteString.ToBytes(s);
        return withNul ? v.Append((byte)0).ToArray() : v;
    }
}
