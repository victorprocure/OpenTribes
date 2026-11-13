namespace OpenTribes.Platform.Gfx;

using System.Numerics;

/// <summary>Converts palettes to RGBA with the gamma table (docs/renderer.md 3.3 and 3.4).</summary>
public static class PaletteTables
{
    /// <summary>The lowest and highest gamma (docs/renderer.md 1.9).</summary>
    public const float MinGamma = 0.05f, MaxGamma = 20f;

    /// <summary>Builds the gamma table of 005d4880 (docs/renderer.md 3.3 item 1).</summary>
    /// <param name="gamma">The surface gamma.</param>
    /// <returns>The 256 entries, g[i] = trunc(255 * pow(i * (1/255), e)) with e = 1.0f / gamma.</returns>
    /// <remarks>The x87 sequence runs with the control word of the ftol helper (0f7f, 48813c, which stays loaded): extended precision, round toward zero. So e is the quotient truncated to a float, and i * (1/255) is a product with the 80-bit constant 3ff7:8080808080808081 (1/255 rounded up) truncated to 64 bits and then, stored as the pow argument, to 53 bits. pow (606d90) of an integer exponent below 4096 is a square and multiply chain in extended precision that returns x itself for e = 1. At gamma 1 that makes 255 * x fall just below i for every i from 1 to 254, so the stock default table is i - 1 there (0 and 255 stay). Other exponents go through fyl2x and exp, modelled with a double pow (only a result within 1e-15 of an integer could differ).</remarks>
    public static byte[] GammaTable(float gamma)
    {
        gamma = Math.Clamp(gamma, MinGamma, MaxGamma);
        var e = InverseAsFloat(gamma);
        var t = new byte[256];
        for (var i = 1; i < 256; ++i)
        {
            BigInteger x = Chop(Chop(i * OneOver255, 0, 64).Item1, 0, 53).Item1; // value x * 2^-71
            var xd = (double)x * Math.Pow(2, -71);
            if (e == Math.Floor(e) && e < 4096)
            {
                // extended chain: acc = x, then for each bit of e below the leading one: acc = acc * acc, and acc * x when the bit is set
                BigInteger acc = x;
                var sh = 71; // acc * 2^-sh
                for (var bit = BitLength((long)e) - 2; bit >= 0; --bit)
                {
                    acc *= acc;
                    sh *= 2;
                    (acc, sh) = Chop(acc, sh, 64);
                    if ((((long)e >> bit) & 1) != 0)
                    {
                        acc *= x;
                        sh += 71;
                        (acc, sh) = Chop(acc, sh, 64);
                    }
                }
                t[i] = (byte)(int)((acc * 255) >> sh); // ftol: trunc(255 * acc)
            }
            else
                t[i] = (byte)(int)(255.0 * Math.Pow(xd, e));
        }
        return t;
    }

    private static readonly BigInteger OneOver255 = BigInteger.Parse("9259542123273814145"); // 0x8080808080808081 * 2^-71

    private static int BitLength(long v) => 64 - BitOperations.LeadingZeroCount((ulong)v);

    // Truncates acc * 2^-sh to `bits` significant bits.
    private static (BigInteger, int) Chop(BigInteger acc, int sh, int bits)
    {
        var len = (int)acc.GetBitLength();
        if (len <= bits)
            return (acc, sh);
        var drop = len - bits;
        return (acc >> drop << drop, sh);
    }

    // 1.0f / gamma truncated to a float (extended quotient, then stored as a float, both round toward zero).
    private static double InverseAsFloat(float gamma)
    {
        int bits = BitConverter.SingleToInt32Bits(gamma), ex = (bits >> 23) & 0xff;
        long mg = ex == 0 ? (bits & 0x7fffff) << 1 : (bits & 0x7fffff) | 0x800000;
        var eg = (ex == 0 ? 1 : ex) - 150; // gamma = mg * 2^eg
        BigInteger q = (BigInteger.One << 54) / mg; // 2^54 / mg has at least 30 bits
        var drop = (int)q.GetBitLength() - 24;
        q = q >> drop << drop;
        return (double)q * Math.Pow(2, -54 - eg);
    }

    /// <summary>Applies the gamma to a flat colour through the float maps raised to the same exponent (docs/renderer.md 3.3).</summary>
    /// <param name="c">The colour channel, 0 to 1.</param>
    /// <param name="gamma">The surface gamma.</param>
    /// <returns>The corrected channel.</returns>
    public static float GammaFloat(float c, float gamma) =>
        gamma == 1f ? c : MathF.Pow(c, 1f / Math.Clamp(gamma, MinGamma, MaxGamma));

    /// <summary>Converts a bitmap to RGBA8 through the subpalette selected by its PiDX and the gamma table (docs/renderer.md 3.4).</summary>
    /// <param name="bmp">The bitmap.</param>
    /// <param name="sub">The subpalette.</param>
    /// <param name="gamma">The gamma table.</param>
    /// <returns>The RGBA8 pixels. Alpha is opaque 255, but index 0 is 0 when attribute bit 0 is set. The palette flags byte of a translucent (kind 2) subpalette is the alpha of a translucent bitmap (3.3 item 3 and 4).</returns>
    public static byte[] ToRgba(PbmpBitmap bmp, SubPalette sub, byte[] gamma)
    {
        int w = bmp.Width, h = bmp.Height, stride = bmp.Stride;
        var rgba = new byte[w * h * 4];
        var keyed = (bmp.Attributes & BitmapAttributes.Transparent) != 0;
        var paletteAlpha = (bmp.Attributes & BitmapAttributes.Translucent) != 0 && sub.Kind == 2;
        if (bmp.Depth == 24)
        {
            for (var y = 0; y < h; ++y)
                for (var x = 0; x < w; ++x)
                {
                    int s = (y * stride) + (x * 3), d = ((y * w) + x) * 4;
                    rgba[d] = gamma[bmp.Pixels[s + 2]]; // stored B, G, R
                    rgba[d + 1] = gamma[bmp.Pixels[s + 1]];
                    rgba[d + 2] = gamma[bmp.Pixels[s]];
                    rgba[d + 3] = 255;
                }
            return rgba;
        }
        for (var y = 0; y < h; ++y)
            for (var x = 0; x < w; ++x)
            {
                int idx = bmp.Pixels[(y * stride) + x], d = ((y * w) + x) * 4;
                rgba[d] = gamma[sub.Colors[idx * 4]];
                rgba[d + 1] = gamma[sub.Colors[(idx * 4) + 1]];
                rgba[d + 2] = gamma[sub.Colors[(idx * 4) + 2]];
                rgba[d + 3] = keyed && idx == 0 ? (byte)0 : paletteAlpha ? sub.Colors[(idx * 4) + 3] : (byte)255;
            }
        return rgba;
    }

    /// <summary>Applies the packed 16-bit table the stock client uploads a palette-indexed bitmap through, in place (palette install 005d4880, upload 005d7148).</summary>
    /// <param name="rgba">The RGBA8 pixels.</param>
    /// <param name="sub">The subpalette that selects the table by its kind.</param>
    /// <returns>The same array.</returns>
    /// <remarks>Kinds 0, 1, 6 and 8 give RGBA5551 (5-bit channels and a 1-bit alpha). Kind 2 (translucent) gives RGBA4444. The other kinds have no table. The driver reads a 5-bit value v back as (v &lt;&lt; 3) | (v &gt;&gt; 2) and a 4-bit value n as n * 17. Transparent bitmaps (attribute 1) pick the table variant with alpha 0 for index 0, which <see cref="ToRgba"/> already gave them, so only the colour channels (5551) or all four channels (4444) change.</remarks>
    public static byte[] Pack(byte[] rgba, SubPalette sub)
    {
        switch (sub.Kind)
        {
            case 0 or 1 or 6 or 8:
                for (var i = 0; i < rgba.Length; i += 4)
                    for (var c = 0; c < 3; ++c)
                        rgba[i + c] = (byte)(((rgba[i + c] >> 3) << 3) | (rgba[i + c] >> 5));
                return rgba;
            case 2:
                for (var i = 0; i < rgba.Length; ++i)
                    rgba[i] = (byte)((rgba[i] >> 4) * 17);
                return rgba;
            default:
                return rgba;
        }
    }

    /// <summary>Creates a 256-entry gray ramp bank, the palette in use before a real one is installed.</summary>
    /// <returns>The bank.</returns>
    public static PaletteBank Grayscale()
    {
        var rgba = new byte[1024];
        for (var i = 0; i < 256; ++i)
        {
            rgba[i * 4] = rgba[(i * 4) + 1] = rgba[(i * 4) + 2] = (byte)i;
        }
        return PaletteBank.FromColors(rgba, 256);
    }
}
