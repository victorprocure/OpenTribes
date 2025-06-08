using System.Buffers.Binary;

namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.2.1: bitmap attribute bits.
[Flags]
public enum BitmapAttributes : uint
{
    None = 0,
    Transparent = 1,   // index 0 is clear
    Fuzzy = 2,
    Translucent = 4,
    OwnsMemory = 8,    // the loader ORs it in
    Additive = 0x10,
    Subtractive = 0x20,
    Alpha88 = 0x40,
}

// docs/renderer.md 4 "Bitmap blits": blend mode from the attributes `& 0x37`. Fuzzy (2) is not
// handled by the 2D path and falls to opaque. The priority when several bits are set is ours
// (the spec names only single-bit and 4|1 cases): subtractive, additive, translucent, transparent.
public enum BlendMode
{
    Opaque,
    Transparent,
    Translucent,
    Additive,
    Subtractive,
}

public static class BitmapAttributeExtensions
{
    public static BlendMode Blend(this BitmapAttributes a)
    {
        uint v = (uint)a & 0x37;
        if ((v & (uint)BitmapAttributes.Subtractive) != 0)
            return BlendMode.Subtractive;
        if ((v & (uint)BitmapAttributes.Additive) != 0)
            return BlendMode.Additive;
        if ((v & (uint)BitmapAttributes.Translucent) != 0)
            return BlendMode.Translucent;
        if ((v & (uint)BitmapAttributes.Transparent) != 0)
            return BlendMode.Transparent;
        return BlendMode.Opaque;
    }
}

// Inclusive rectangle (docs/renderer.md 1.7 and 4: right and bottom are the last column and row).
public readonly record struct GfxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
    public bool IsEmpty => Right < Left || Bottom < Top;

    public GfxRect Intersect(GfxRect o) =>
        new(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
}

// docs/renderer.md 4: flip 0 none, 1 flip X, 2 flip Y, 3 both.
[Flags]
public enum BlitFlip
{
    None = 0,
    X = 1,
    Y = 2,
}

public sealed class GfxFormatException : Exception
{
    public GfxFormatException(string message) : base(message) { }
}

// Little-endian cursor over a byte span; every read is bounds checked and throws GfxFormatException.
internal ref struct Cursor
{
    public readonly ReadOnlySpan<byte> Data;
    public int Pos;

    public Cursor(ReadOnlySpan<byte> data, int pos = 0)
    {
        Data = data;
        Pos = pos;
    }

    public readonly int Remaining => Data.Length - Pos;

    public void Need(long n)
    {
        if (n < 0 || n > Remaining)
            throw new GfxFormatException("truncated data");
    }

    public uint U32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(Data[Pos..]);
        Pos += 4;
        return v;
    }

    public ushort U16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(Data[Pos..]);
        Pos += 2;
        return v;
    }

    public byte U8()
    {
        Need(1);
        return Data[Pos++];
    }

    public ReadOnlySpan<byte> Bytes(long n)
    {
        Need(n);
        var s = Data.Slice(Pos, (int)n);
        Pos += (int)n;
        return s;
    }

    public string Tag() => System.Text.Encoding.Latin1.GetString(Bytes(4));

    public readonly string PeekTag()
    {
        if (Remaining < 4)
            throw new GfxFormatException("truncated data");
        return System.Text.Encoding.Latin1.GetString(Data.Slice(Pos, 4));
    }
}
