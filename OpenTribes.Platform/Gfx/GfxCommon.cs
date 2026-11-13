namespace OpenTribes.Platform.Gfx;

using System.Buffers.Binary;

/// <summary>The bitmap attribute bits (docs/renderer.md 2.2.1).</summary>
[Flags]
public enum BitmapAttributes : uint
{
    /// <summary>No attributes.</summary>
    None = 0,
    /// <summary>Palette index 0 is clear.</summary>
    Transparent = 1,
    /// <summary>The fuzzy blend.</summary>
    Fuzzy = 2,
    /// <summary>The translucent blend.</summary>
    Translucent = 4,
    /// <summary>The loader sets this bit to say the bitmap owns its memory.</summary>
    OwnsMemory = 8,
    /// <summary>The additive blend.</summary>
    Additive = 0x10,
    /// <summary>The subtractive blend.</summary>
    Subtractive = 0x20,
    /// <summary>The alpha 8-8 attribute (bit 6). It is outside the blend mask.</summary>
    Alpha88 = 0x40,
}

/// <summary>The blend mode of a blit (docs/renderer.md 4 "Bitmap blits"), from the attributes &amp; 0x37.</summary>
/// <remarks>Fuzzy (2) is not handled by the 2D path and falls to opaque. The priority when several bits are set is ours, since the spec names only single-bit and 4|1 cases: subtractive, additive, translucent, transparent.</remarks>
public enum BlendMode
{
    /// <summary>No blending.</summary>
    Opaque,
    /// <summary>Palette index 0 is not drawn.</summary>
    Transparent,
    /// <summary>Source alpha blending.</summary>
    Translucent,
    /// <summary>The source colour is added.</summary>
    Additive,
    /// <summary>The target is multiplied by one minus the source colour.</summary>
    Subtractive,
}

/// <summary>Extension methods for <see cref="BitmapAttributes"/>.</summary>
public static class BitmapAttributeExtensions
{
    /// <summary>Gets the blend mode of bitmap attributes.</summary>
    /// <param name="a">The attributes.</param>
    /// <returns>The blend mode.</returns>
    public static BlendMode Blend(this BitmapAttributes a)
    {
        var v = (uint)a & 0x37;
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

/// <summary>An inclusive rectangle (docs/renderer.md 1.7 and 4): right and bottom are the last column and row.</summary>
/// <param name="Left">The first column.</param>
/// <param name="Top">The first row.</param>
/// <param name="Right">The last column.</param>
/// <param name="Bottom">The last row.</param>
public readonly record struct GfxRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the width in columns.</summary>
    public int Width => Right - Left + 1;
    /// <summary>Gets the height in rows.</summary>
    public int Height => Bottom - Top + 1;
    /// <summary>Gets whether the rectangle holds no pixel.</summary>
    public bool IsEmpty => Right < Left || Bottom < Top;

    /// <summary>Intersects two rectangles.</summary>
    /// <param name="o">The other rectangle.</param>
    /// <returns>The overlap. It is empty when they do not touch.</returns>
    public GfxRect Intersect(GfxRect o) =>
        new(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
}

/// <summary>The flip of a blit (docs/renderer.md 4).</summary>
[Flags]
public enum BlitFlip
{
    /// <summary>No flip.</summary>
    None = 0,
    /// <summary>Flip horizontally.</summary>
    X = 1,
    /// <summary>Flip vertically.</summary>
    Y = 2,
}

/// <summary>The exception for a malformed graphics file.</summary>
public sealed class GfxFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
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

    public readonly void Need(long n)
    {
        if (n < 0 || n > Remaining)
            throw new GfxFormatException("truncated data");
    }

    public uint U32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(Data[Pos..]);
        Pos += 4;
        return v;
    }

    public ushort U16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(Data[Pos..]);
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
