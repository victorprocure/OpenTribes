// Client side of the game stream (docs/client-net.md 2, 3): the field reader every client unpack routine uses.
// The wire widths are those of the server pack code (docs/game-stream.md 6.1, docs/game-stream-ghosts.md 4); the
// float helpers follow BitStream readFloat / readSignedFloat / readNormalVector (docs/net-protocol.md 4).
namespace OpenTribes.Game;

using System.Globalization;

/// <summary>One decoded field of an event, a ghost update or a PSC block.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Value">The value: a long, double, string, double[] or a list of those.</param>
public readonly record struct DecodedField(string Name, object? Value);

/// <summary>A layout the decoder does not know (a class tag, a datablock group or a control state class).</summary>
/// <remarks>The bits that follow cannot be attributed, so the packet tail is counted as unknown bits (docs/client-net.md 2.2).</remarks>
public sealed class UnsupportedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public UnsupportedException(string message) : base(message) { }
}

/// <summary>A read past the end of the packet, "Invalid packet" in the original (docs/game-stream.md 1.2).</summary>
public sealed class OverrunException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OverrunException() : base("read past the end of the packet") { }
}

/// <summary>A member reader called Net_invalidPacket with a text (0x00553630). The reader stops and the net object drops the connection.</summary>
/// <remarks>See docs/client-net.md 1.4 and TribesNet_onPacketError (0x0042b7f8).</remarks>
public sealed class PacketErrorException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="text">The text of the error.</param>
    public PacketErrorException(string text) : base(text) { }
}

/// <summary>The field reader every client unpack routine uses.</summary>
/// <remarks>The wire widths are those of the server pack code (docs/game-stream.md 6.1, docs/game-stream-ghosts.md 4). The float helpers follow BitStream readFloat, readSignedFloat and readNormalVector (docs/net-protocol.md 4).</remarks>
public sealed class ClientReader
{
    /// <summary>The bit stream.</summary>
    public BitStream Bs { get; }
    /// <summary>The size of the data in bits.</summary>
    public int SizeBits { get; }
    /// <summary>The client world.</summary>
    public ClientWorld World { get; }
    /// <summary>The fields of the current update.</summary>
    public List<DecodedField> Out { get; set; } = [];

    /// <summary>Creates a reader.</summary>
    /// <param name="data">The packet bytes.</param>
    /// <param name="world">The client world.</param>
    public ClientReader(byte[] data, ClientWorld world)
    {
        Bs = new BitStream(data, data.Length);
        SizeBits = data.Length * 8;
        World = world;
    }

    /// <summary>Gets the current bit position.</summary>
    public int Pos => (int)Bs.BitPosition();
    /// <summary>Gets the number of bits left.</summary>
    public int Left => SizeBits - Pos;

    private void Check()
    {
        if (Bs.Error())
            throw new OverrunException();
    }

    /// <summary>Reads a flag.</summary>
    /// <returns>The flag.</returns>
    public bool Flag() { var v = Bs.ReadFlag(); Check(); return v; }
    /// <summary>Reads an unsigned integer.</summary>
    /// <param name="bits">The number of bits.</param>
    /// <returns>The value.</returns>
    public uint Int(uint bits) { var v = Bs.ReadInt(bits); Check(); return v; }

    /// <summary>Reads a signed integer: a sign flag, then n - 1 bits of magnitude (BitStream readSignedInt).</summary>
    /// <param name="bits">The number of bits.</param>
    /// <returns>The value.</returns>
    public int SInt(uint bits) { var v = Bs.ReadSignedInt(bits); Check(); return v; }
    /// <summary>Reads a raw 32-bit float.</summary>
    /// <returns>The value.</returns>
    public float F32() { var v = Bs.ReadF32(); Check(); return v; }
    /// <summary>Reads a string.</summary>
    /// <returns>The value.</returns>
    public string Str() { var v = Bs.ReadString(); Check(); return v; }

    /// <summary>Reads n bits scaled to 0 to 1 (BitStream readFloat).</summary>
    /// <param name="bits">The number of bits.</param>
    /// <returns>The value.</returns>
    public double Unit(uint bits) => Int(bits) / (double)((1u << (int)bits) - 1);

    /// <summary>Reads n bits scaled to -1 to 1 (BitStream readSignedFloat).</summary>
    /// <param name="bits">The number of bits.</param>
    /// <returns>The value.</returns>
    public double SUnit(uint bits) => (Int(bits) / (double)((1u << (int)bits) - 1) * 2.0) - 1.0;

    /// <summary>Reads a unit normal: two signed floats and the sign of z (BitStream readNormalVector).</summary>
    /// <param name="bits">The number of bits of x and y.</param>
    /// <returns>The normal.</returns>
    public double[] Normal(uint bits)
    {
        double x = SUnit(bits), y = SUnit(bits);
        var neg = Flag();
        var z = Math.Sqrt(Math.Max(0.0, 1.0 - (x * x) - (y * y)));
        return [x, y, neg ? -z : z];
    }

    /// <summary>Reads three raw floats.</summary>
    /// <returns>The vector.</returns>
    public double[] Vec() => [F32(), F32(), F32()];

    /// <summary>Reads bytes.</summary>
    /// <param name="n">The number of bytes.</param>
    /// <returns>The bytes.</returns>
    public byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; ++i)
            b[i] = (byte)Int(8);
        return b;
    }

    /// <summary>Records a field in the update list.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The value, for the caller's own use.</returns>
    public T Add<T>(string name, T value)
    {
        Out.Add(new DecodedField(name, value));
        return value;
    }

    /// <summary>Reads a flag and records it as 0 or 1.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The flag.</returns>
    public bool AddFlag(string name) { var v = Flag(); Add(name, v ? 1L : 0L); return v; }
    /// <summary>Reads an unsigned integer and records it.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="bits">The number of bits.</param>
    /// <returns>The value.</returns>
    public long AddInt(string name, uint bits) { long v = Int(bits); Add(name, v); return v; }
    /// <summary>Reads a raw float and records it.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value.</returns>
    public double AddF32(string name) { double v = F32(); Add(name, v); return v; }
    /// <summary>Reads three raw floats and records them.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The vector.</returns>
    public double[] AddVec(string name) => Add(name, Vec());
    /// <summary>Reads a unit float and records it, scaled and offset.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="bits">The number of bits.</param>
    /// <param name="scale">The scale.</param>
    /// <param name="bias">The offset added after scaling.</param>
    /// <returns>The recorded value.</returns>
    public double AddUnit(string name, uint bits, double scale = 1.0, double bias = 0.0) => Add(name, (Unit(bits) * scale) + bias);

    /// <summary>Returns the width of a datablock index: the bit length of the block count of the group plus 1.</summary>
    /// <param name="group">The datablock group.</param>
    /// <returns>The width in bits. The group count comes from the DataBlockEvents seen so far, and an unknown count means the layout cannot be followed.</returns>
    /// <remarks>Follows 00402b60.</remarks>
    public uint DbBits(int group)
    {
        if (!World.DataBlocks.Counts.TryGetValue(group, out var count))
            throw new UnsupportedException("datablock count of group " + group + " unknown (no DataBlockEvent seen)");
        uint n = (uint)count + 1, bits = 1;
        while (bits < 32 && (n >> (int)bits) != 0)
            ++bits;
        return bits;
    }

    /// <summary>Formats a value as a float with <c>%g</c> text (6 significant digits).</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string G(double v) => ((float)v).ToString("G6", CultureInfo.InvariantCulture);
}
