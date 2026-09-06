// Client side of the game stream (docs/client-net.md 2, 3): the field reader every client unpack routine uses.
// The wire widths are those of the server pack code (docs/game-stream.md 6.1, docs/game-stream-ghosts.md 4); the
// float helpers follow BitStream readFloat / readSignedFloat / readNormalVector (docs/net-protocol.md 4).
using System.Globalization;

namespace OpenTribes.Game;

// One decoded field of an event, a ghost update or a PSC block: the name and the value (long, double, string,
// double[] or a list of those).
public readonly record struct DecodedField(string Name, object? Value);

// A layout the decoder does not know (a class tag, a datablock group, a control state class). The bits that follow
// cannot be attributed, so the packet tail is counted as unknown bits (client-net.md 2.2).
public sealed class UnsupportedException : Exception
{
    public UnsupportedException(string message) : base(message) { }
}

// A read past the end of the packet: "Invalid packet" in the original (game-stream.md 1.2).
public sealed class OverrunException : Exception
{
    public OverrunException() : base("read past the end of the packet") { }
}

// A member reader called Net_invalidPacket with a text (0x00553630): the reader stops, the net object drops the connection
// (client-net.md 1.4, TribesNet_onPacketError 0x0042b7f8).
public sealed class PacketErrorException : Exception
{
    public PacketErrorException(string text) : base(text) { }
}

public sealed class ClientReader
{
    public readonly BitStream Bs;
    public readonly int SizeBits;
    public readonly ClientWorld World;
    public List<DecodedField> Out = new();

    public ClientReader(byte[] data, ClientWorld world)
    {
        Bs = new BitStream(data, data.Length);
        SizeBits = data.Length * 8;
        World = world;
    }

    public int Pos => (int)Bs.BitPosition();
    public int Left => SizeBits - Pos;

    private void Check()
    {
        if (Bs.Error())
            throw new OverrunException();
    }

    public bool Flag() { bool v = Bs.ReadFlag(); Check(); return v; }
    public uint Int(uint bits) { uint v = Bs.ReadInt(bits); Check(); return v; }

    // sint(n): sign flag, then n - 1 bits of magnitude (BitStream readSignedInt).
    public int SInt(uint bits) { int v = Bs.ReadSignedInt(bits); Check(); return v; }
    public float F32() { float v = Bs.ReadF32(); Check(); return v; }
    public string Str() { string v = Bs.ReadString(); Check(); return v; }

    // BitStream readFloat: n bits scaled to 0..1.
    public double Unit(uint bits) => Int(bits) / (double)((1u << (int)bits) - 1);

    // BitStream readSignedFloat: n bits scaled to -1..1.
    public double SUnit(uint bits) => Int(bits) / (double)((1u << (int)bits) - 1) * 2.0 - 1.0;

    // BitStream readNormalVector: two signed floats and the sign of z.
    public double[] Normal(uint bits)
    {
        double x = SUnit(bits), y = SUnit(bits);
        bool neg = Flag();
        double z = Math.Sqrt(Math.Max(0.0, 1.0 - x * x - y * y));
        return new[] { x, y, neg ? -z : z };
    }

    public double[] Vec() => new double[] { F32(), F32(), F32() };

    public byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; ++i)
            b[i] = (byte)Int(8);
        return b;
    }

    // Records the field in the update list (and returns the value for the caller's own use).
    public T Add<T>(string name, T value)
    {
        Out.Add(new DecodedField(name, value));
        return value;
    }

    public bool AddFlag(string name) { bool v = Flag(); Add(name, v ? 1L : 0L); return v; }
    public long AddInt(string name, uint bits) { long v = Int(bits); Add(name, v); return v; }
    public double AddF32(string name) { double v = F32(); Add(name, v); return v; }
    public double[] AddVec(string name) => Add(name, Vec());
    public double AddUnit(string name, uint bits, double scale = 1.0, double bias = 0.0) => Add(name, Unit(bits) * scale + bias);

    // Datablock index width: bit length of (blocks in the group + 1), group count from the DataBlockEvents seen so far
    // (00402b60). Unknown count: the layout cannot be followed.
    public uint DbBits(int group)
    {
        if (!World.DataBlocks.Counts.TryGetValue(group, out int count))
            throw new UnsupportedException("datablock count of group " + group + " unknown (no DataBlockEvent seen)");
        uint n = (uint)count + 1, bits = 1;
        while (bits < 32 && (n >> (int)bits) != 0)
            ++bits;
        return bits;
    }

    public static string G(double v) => ((float)v).ToString("G6", CultureInfo.InvariantCulture);
}
