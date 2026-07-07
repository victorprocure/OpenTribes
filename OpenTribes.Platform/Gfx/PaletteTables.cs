namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 3.3 and 3.4: palette-to-RGBA conversion with the gamma table.
public static class PaletteTables
{
    public const float MinGamma = 0.05f, MaxGamma = 20f; // 1.9: gamma range

    // 3.3 item 1: g[i] = trunc(255 * pow(i / 255, e)). The exponent is e = 1 / gamma (Q1: assumed). A 1e-9
    // guard keeps pow(x, 1) * 255 from truncating to i - 1 through double rounding.
    public static byte[] GammaTable(float gamma)
    {
        gamma = Math.Clamp(gamma, MinGamma, MaxGamma);
        double e = 1.0 / gamma;
        var t = new byte[256];
        for (int i = 0; i < 256; ++i)
            t[i] = (byte)Math.Min(255, (int)(255.0 * Math.Pow(i / 255.0, e) + 1e-9));
        return t;
    }

    // 3.3: flat colors go through the float maps raised to the same exponent.
    public static float GammaFloat(float c, float gamma) =>
        gamma == 1f ? c : MathF.Pow(c, 1f / Math.Clamp(gamma, MinGamma, MaxGamma));

    // 3.4: converts a bitmap to RGBA8 through the subpalette selected by its PiDX and the gamma table.
    // Alpha: opaque 255; index 0 is 0 when attribute bit0 is set; the palette flags byte of a translucent
    // (kind 2) subpalette is the alpha of a translucent bitmap (3.3 item 3, 4.).
    public static byte[] ToRgba(PbmpBitmap bmp, SubPalette sub, byte[] gamma)
    {
        int w = bmp.Width, h = bmp.Height, stride = bmp.Stride;
        var rgba = new byte[w * h * 4];
        bool keyed = (bmp.Attributes & BitmapAttributes.Transparent) != 0;
        bool paletteAlpha = (bmp.Attributes & BitmapAttributes.Translucent) != 0 && sub.Kind == 2;
        if (bmp.Depth == 24)
        {
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                {
                    int s = y * stride + x * 3, d = (y * w + x) * 4;
                    rgba[d] = gamma[bmp.Pixels[s + 2]]; // stored B, G, R
                    rgba[d + 1] = gamma[bmp.Pixels[s + 1]];
                    rgba[d + 2] = gamma[bmp.Pixels[s]];
                    rgba[d + 3] = 255;
                }
            return rgba;
        }
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int idx = bmp.Pixels[y * stride + x], d = (y * w + x) * 4;
                rgba[d] = gamma[sub.Colors[idx * 4]];
                rgba[d + 1] = gamma[sub.Colors[idx * 4 + 1]];
                rgba[d + 2] = gamma[sub.Colors[idx * 4 + 2]];
                rgba[d + 3] = keyed && idx == 0 ? (byte)0 : paletteAlpha ? sub.Colors[idx * 4 + 3] : (byte)255;
            }
        return rgba;
    }

    // The packed 16-bit table the stock client uploads a palette-indexed bitmap through, selected by the kind of the bitmap's subpalette
    // (palette install 005d4880, upload 005d7148): kinds 0, 1, 6 and 8 give RGBA5551 (5 bit channels, a 1 bit alpha), kind 2
    // (translucent) gives RGBA4444 (4 bit channels and alpha); the other kinds have no table. The driver reads a 5 bit value v back as
    // (v << 3) | (v >> 2) and a 4 bit value n as n * 17. Transparent bitmaps (attribute 1) pick the table variant with alpha 0 for index 0, which
    // `ToRgba` already gave them, so only the colour channels (5551) or all four channels (4444) change here. In place.
    public static byte[] Pack(byte[] rgba, SubPalette sub)
    {
        switch (sub.Kind)
        {
            case 0 or 1 or 6 or 8:
                for (int i = 0; i < rgba.Length; i += 4)
                    for (int c = 0; c < 3; ++c)
                        rgba[i + c] = (byte)(((rgba[i + c] >> 3) << 3) | (rgba[i + c] >> 5));
                return rgba;
            case 2:
                for (int i = 0; i < rgba.Length; ++i)
                    rgba[i] = (byte)((rgba[i] >> 4) * 17);
                return rgba;
            default:
                return rgba;
        }
    }

    // A 256-entry gray ramp bank, the palette in use before a real one is installed.
    public static PaletteBank Grayscale()
    {
        var rgba = new byte[1024];
        for (int i = 0; i < 256; ++i)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = (byte)i;
        }
        return PaletteBank.FromColors(rgba, 256);
    }
}
