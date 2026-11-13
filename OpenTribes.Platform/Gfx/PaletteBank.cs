namespace OpenTribes.Platform.Gfx;

/// <summary>One subpalette of a PL98 set, or the single palette of a RIFF .pal (docs/renderer.md 2.4).</summary>
public sealed class SubPalette
{
    /// <summary>The id that selects the first subpalette.</summary>
    public const uint NoId = 0xFFFFFFFF;

    /// <summary>The 256 entries of 4 bytes: red, green, blue and flags. The flags byte is the alpha of kind 2 palettes.</summary>
    public byte[] Colors = new byte[1024];
    /// <summary>The palette id.</summary>
    public uint Id = NoId;
    /// <summary>The kind: 0 no remap, 1 shade and haze, 2 translucent, 3 color quant, 4 alpha quant, 5 additive quant, 6 additive, 7 subtractive quant, 8 subtractive.</summary>
    public uint Kind;
    /// <summary>The shade and haze table of kind 1: (hazeSteps + 1) * shadeSteps * 256 bytes, indexed (haze * shadeSteps + shade) * 256 + color.</summary>
    public byte[]? ShadeHazeTable;
    /// <summary>The blend table of kinds 2, 6 and 8: 256 x 256 bytes.</summary>
    public byte[]? BlendTable;
    /// <summary>The 256 identity-map bytes of kinds 1, 2, 6 and 8.</summary>
    public byte[]? IdentityMap;
    /// <summary>The red, green, blue and alpha float maps of kinds 1, 2, 6 and 8.</summary>
    public float[]? IndexToRed, IndexToGreen, IndexToBlue, IndexToAlpha;

    /// <summary>Reads the colour of an entry.</summary>
    /// <param name="index">The palette index.</param>
    /// <param name="r">Receives the red.</param>
    /// <param name="g">Receives the green.</param>
    /// <param name="b">Receives the blue.</param>
    public void Rgb(int index, out byte r, out byte g, out byte b)
    {
        r = Colors[index * 4];
        g = Colors[(index * 4) + 1];
        b = Colors[(index * 4) + 2];
    }

    /// <summary>Gets the flags byte of an entry.</summary>
    /// <param name="index">The palette index.</param>
    /// <returns>The flags byte.</returns>
    public byte Flags(int index) => Colors[(index * 4) + 3];
}

/// <summary>A palette bank: a PL98 <c>.ppl</c> set (docs/renderer.md 2.4) or a <c>.pal</c> (2.5).</summary>
public sealed class PaletteBank
{
    /// <summary>The number of shade bits.</summary>
    public uint ShadeBits;
    /// <summary>The number of haze steps.</summary>
    public uint HazeSteps;
    /// <summary>The haze colour.</summary>
    public uint HazeColor;
    /// <summary>The bit set of allowed colours, 256 bits, most significant bit first.</summary>
    public uint[] AllowedColors = new uint[8];
    /// <summary>The subpalettes.</summary>
    public List<SubPalette> SubPalettes = [];
    /// <summary>The colour weights, or null.</summary>
    public float[]? ColorWeights;
    /// <summary>The first and last used palette index.</summary>
    public uint UsedStart, UsedEnd;

    /// <summary>Gets the number of shade steps.</summary>
    public uint ShadeSteps => 1u << (int)ShadeBits;

    /// <summary>Finds a subpalette by id (docs/renderer.md 2.4 "Palette selection by id").</summary>
    /// <param name="id">The id.</param>
    /// <returns>The subpalette. 0xffffffff or no match returns palette 0.</returns>
    public SubPalette FindSubPalette(uint id)
    {
        if (id != SubPalette.NoId)
            foreach (var p in SubPalettes)
                if (p.Id == id)
                    return p;
        return SubPalettes[0];
    }

    /// <summary>Tests whether a palette index is an allowed colour.</summary>
    /// <param name="index">The palette index.</param>
    /// <returns>True when allowed.</returns>
    public bool IsAllowed(int index) => (AllowedColors[index >> 5] & (0x80000000u >> (index & 31))) != 0;

    /// <summary>Finds the allowed palette index nearest to a colour (getNearestColor, docs/renderer.md 2.4).</summary>
    /// <param name="r">The red.</param>
    /// <param name="g">The green.</param>
    /// <param name="b">The blue.</param>
    /// <param name="sub">The subpalette, or null for the first.</param>
    /// <returns>The allowed index with the smallest squared RGB distance. It is 0 when index 0 is closer than the best allowed one; on a tie index 0 does not win.</returns>
    public int NearestColor(int r, int g, int b, SubPalette? sub = null)
    {
        sub ??= SubPalettes[0];
        long Dist(int i)
        {
            sub.Rgb(i, out var pr, out var pg, out var pb);
            long dr = pr - r, dg = pg - g, db = pb - b;
            return (dr * dr) + (dg * dg) + (db * db);
        }
        var best = -1;
        var bestD = long.MaxValue;
        for (var i = 0; i < 256; ++i)
        {
            if (!IsAllowed(i))
                continue;
            var d = Dist(i);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        if (best < 0 || Dist(0) < bestD)
            return 0;
        return best;
    }

    /// <summary>Creates a single-palette bank from RGBA entries, for a RIFF .pal or a Windows BMP palette.</summary>
    /// <param name="rgba">The RGBA entries.</param>
    /// <param name="count">The number of entries.</param>
    /// <returns>The bank.</returns>
    public static PaletteBank FromColors(ReadOnlySpan<byte> rgba, int count)
    {
        var sp = new SubPalette { Kind = 0, Id = SubPalette.NoId };
        rgba[..Math.Min(rgba.Length, Math.Min(count, 256) * 4)].CopyTo(sp.Colors);
        // 2.5: identity map and float maps from the colors (color / 255, alpha 0 for index 0).
        sp.IdentityMap = new byte[256];
        sp.IndexToRed = new float[256];
        sp.IndexToGreen = new float[256];
        sp.IndexToBlue = new float[256];
        sp.IndexToAlpha = new float[256];
        for (var i = 0; i < 256; ++i)
        {
            sp.IdentityMap[i] = (byte)i;
            sp.IndexToRed[i] = sp.Colors[i * 4] / 255f;
            sp.IndexToGreen[i] = sp.Colors[(i * 4) + 1] / 255f;
            sp.IndexToBlue[i] = sp.Colors[(i * 4) + 2] / 255f;
            sp.IndexToAlpha[i] = i == 0 ? 0f : 1f;
        }
        var bank = new PaletteBank { HazeSteps = 1 };
        for (var i = 0; i < 8; ++i)
            bank.AllowedColors[i] = 0xFFFFFFFF;
        bank.SubPalettes.Add(sp);
        return bank;
    }

    /// <summary>Parses a Windows RIFF PAL form (docs/renderer.md 2.5). The stream may start at <c>RIFF</c> or at <c>PAL </c>.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="bank">Receives the bank.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>True on success.</returns>
    public static bool TryParsePal(ReadOnlySpan<byte> data, out PaletteBank? bank, out string error)
    {
        bank = null;
        error = "";
        try
        {
            var c = new Cursor(data);
            bank = ReadPal(ref c);
            return true;
        }
        catch (GfxFormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    internal static PaletteBank ReadPal(ref Cursor c)
    {
        if (c.PeekTag() == "RIFF")
        {
            c.U32();
            c.U32(); // RIFF size
        }
        if (c.Tag() != "PAL ")
            throw new GfxFormatException("not a RIFF PAL form");
        // Chunks until `data`; the extra chunks (offl, tran, unde) come after it and are ignored.
        while (true)
        {
            var tag = c.Tag();
            var size = c.U32();
            if (tag != "data")
            {
                c.Bytes(size);
                continue;
            }
            // The reader divides the chunk size by 4 and subtracts 1 for the count (2.5).
            long count = (size / 4) - 1;
            if (count is < 0 or > 256)
                throw new GfxFormatException("bad palette entry count");
            var body = c.Bytes(size);
            c.Need(0);
            return FromColors(body[4..], (int)count);
        }
    }

    /// <summary>Parses a PL98 palette set (docs/renderer.md 2.4).</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="bank">Receives the bank.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>True on success. The trailer is read when present. A stream that ends after the color-weight flag is accepted, as only the weights follow.</returns>
    public static bool TryParsePpl(ReadOnlySpan<byte> data, out PaletteBank? bank, out string error)
    {
        bank = null;
        error = "";
        try
        {
            var c = new Cursor(data);
            bank = ReadPpl(ref c);
            return true;
        }
        catch (GfxFormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    internal static PaletteBank ReadPpl(ref Cursor c)
    {
        c.U32(); // "PL98", not checked by the reader
        var n = c.U32();
        if (n is < 1 or > 16)
            throw new GfxFormatException("subpalette count out of range");
        var bank = new PaletteBank { ShadeBits = c.U32(), HazeSteps = c.U32(), HazeColor = c.U32() };
        if (bank.ShadeBits > 8)
            throw new GfxFormatException("shadeBits out of range");
        for (var i = 0; i < 8; ++i)
            bank.AllowedColors[i] = c.U32();
        for (uint i = 0; i < n; ++i)
        {
            var sp = new SubPalette();
            c.Bytes(1024).CopyTo(sp.Colors);
            sp.Id = c.U32();
            sp.Kind = c.U32();
            bank.SubPalettes.Add(sp);
        }

        // Map blob. Layout used here (checked on the stock files): first every palette's table in order, then
        // every palette's 0x1100 block (identity bytes + four float maps) for kinds 1, 2, 6, 8.
        var shadeTable = (long)bank.ShadeSteps * 256 * (bank.HazeSteps + 1);
        foreach (var sp in bank.SubPalettes)
        {
            if (sp.Kind == 1)
                sp.ShadeHazeTable = c.Bytes(shadeTable).ToArray();
            else if (sp.Kind is 2 or 6 or 8)
                sp.BlendTable = c.Bytes(65536).ToArray();
        }
        foreach (var sp in bank.SubPalettes)
        {
            if (sp.Kind is not (1 or 2 or 6 or 8))
                continue;
            sp.IdentityMap = c.Bytes(256).ToArray();
            sp.IndexToRed = ReadFloats(ref c);
            sp.IndexToGreen = ReadFloats(ref c);
            sp.IndexToBlue = ReadFloats(ref c);
            sp.IndexToAlpha = ReadFloats(ref c);
        }

        if (c.Remaining >= 1 && c.U8() != 0)
        {
            bank.ColorWeights = ReadFloats(ref c, 256);
            bank.UsedStart = c.U32();
            bank.UsedEnd = c.U32();
        }
        // The kind-0 palettes of a bank carry no maps: derive the float maps used for flat colors.
        foreach (var sp in bank.SubPalettes)
            if (sp.IndexToRed == null)
                FillFloatMaps(sp);
        return bank;
    }

    private static void FillFloatMaps(SubPalette sp)
    {
        sp.IndexToRed = new float[256];
        sp.IndexToGreen = new float[256];
        sp.IndexToBlue = new float[256];
        sp.IndexToAlpha = new float[256];
        for (var i = 0; i < 256; ++i)
        {
            sp.IndexToRed[i] = sp.Colors[i * 4] / 255f;
            sp.IndexToGreen[i] = sp.Colors[(i * 4) + 1] / 255f;
            sp.IndexToBlue[i] = sp.Colors[(i * 4) + 2] / 255f;
            sp.IndexToAlpha[i] = i == 0 ? 0f : 1f;
        }
    }

    private static float[] ReadFloats(ref Cursor c, int count = 256)
    {
        var f = new float[count];
        var b = c.Bytes(count * 4);
        for (var i = 0; i < count; ++i)
            f[i] = BitConverter.ToSingle(b.Slice(i * 4, 4));
        return f;
    }
}
