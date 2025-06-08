namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.4: one subpalette of a PL98 set (or the single palette of a RIFF .pal).
public sealed class SubPalette
{
    public const uint NoId = 0xFFFFFFFF;

    // 256 entries of 4 bytes: red, green, blue, flags (the flags byte is the alpha of kind 2 palettes).
    public byte[] Colors = new byte[1024];
    public uint Id = NoId;
    // 0 no remap, 1 shade+haze, 2 translucent, 3 color quant, 4 alpha quant, 5 additive quant,
    // 6 additive, 7 subtractive quant, 8 subtractive.
    public uint Kind;
    // Kind 1: (hazeSteps + 1) * shadeSteps * 256 bytes, index (haze * shadeSteps + shade) * 256 + color.
    public byte[]? ShadeHazeTable;
    // Kinds 2, 6, 8: 256 x 256 bytes.
    public byte[]? BlendTable;
    // Kinds 1, 2, 6, 8: 256 identity-map bytes and the red, green, blue, alpha float maps.
    public byte[]? IdentityMap;
    public float[]? IndexToRed, IndexToGreen, IndexToBlue, IndexToAlpha;

    public void Rgb(int index, out byte r, out byte g, out byte b)
    {
        r = Colors[index * 4];
        g = Colors[index * 4 + 1];
        b = Colors[index * 4 + 2];
    }

    public byte Flags(int index) => Colors[index * 4 + 3];
}

// docs/renderer.md 2.4 (`.ppl`) and 2.5 (`.pal`).
public sealed class PaletteBank
{
    public uint ShadeBits;
    public uint HazeSteps;
    public uint HazeColor;
    public uint[] AllowedColors = new uint[8];
    public List<SubPalette> SubPalettes = new();
    public float[]? ColorWeights;
    public uint UsedStart, UsedEnd;

    public uint ShadeSteps => 1u << (int)ShadeBits;

    // 2.4 "Palette selection by id": 0xffffffff or no match returns palette 0.
    public SubPalette FindSubPalette(uint id)
    {
        if (id != SubPalette.NoId)
            foreach (SubPalette p in SubPalettes)
                if (p.Id == id)
                    return p;
        return SubPalettes[0];
    }

    public bool IsAllowed(int index) => (AllowedColors[index >> 5] & (0x80000000u >> (index & 31))) != 0;

    // 2.4 getNearestColor: the allowed index with the smallest squared RGB distance; 0 when index 0 is
    // closer than the best allowed one (on a tie index 0 does not win).
    public int NearestColor(int r, int g, int b, SubPalette? sub = null)
    {
        sub ??= SubPalettes[0];
        long Dist(int i)
        {
            sub.Rgb(i, out byte pr, out byte pg, out byte pb);
            long dr = pr - r, dg = pg - g, db = pb - b;
            return dr * dr + dg * dg + db * db;
        }
        int best = -1;
        long bestD = long.MaxValue;
        for (int i = 0; i < 256; ++i)
        {
            if (!IsAllowed(i))
                continue;
            long d = Dist(i);
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

    // A single-palette bank from 256 RGBA entries (RIFF .pal, Windows BMP palette).
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
        for (int i = 0; i < 256; ++i)
        {
            sp.IdentityMap[i] = (byte)i;
            sp.IndexToRed[i] = sp.Colors[i * 4] / 255f;
            sp.IndexToGreen[i] = sp.Colors[i * 4 + 1] / 255f;
            sp.IndexToBlue[i] = sp.Colors[i * 4 + 2] / 255f;
            sp.IndexToAlpha[i] = i == 0 ? 0f : 1f;
        }
        var bank = new PaletteBank { HazeSteps = 1 };
        for (int i = 0; i < 8; ++i)
            bank.AllowedColors[i] = 0xFFFFFFFF;
        bank.SubPalettes.Add(sp);
        return bank;
    }

    // 2.5: Windows RIFF PAL form. The stream may start at `RIFF` or at `PAL `.
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
            string tag = c.Tag();
            uint size = c.U32();
            if (tag != "data")
            {
                c.Bytes(size);
                continue;
            }
            // The reader divides the chunk size by 4 and subtracts 1 for the count (2.5).
            long count = size / 4 - 1;
            if (count < 0 || count > 256)
                throw new GfxFormatException("bad palette entry count");
            var body = c.Bytes(size);
            c.Need(0);
            return FromColors(body[4..], (int)count);
        }
    }

    // 2.4: PL98 palette set. The trailer is read when present; a stream that ends after the
    // color-weight flag is accepted, as only the weights follow.
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
        uint n = c.U32();
        if (n < 1 || n > 16)
            throw new GfxFormatException("subpalette count out of range");
        var bank = new PaletteBank { ShadeBits = c.U32(), HazeSteps = c.U32(), HazeColor = c.U32() };
        if (bank.ShadeBits > 8)
            throw new GfxFormatException("shadeBits out of range");
        for (int i = 0; i < 8; ++i)
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
        long shadeTable = (long)bank.ShadeSteps * 256 * (bank.HazeSteps + 1);
        foreach (SubPalette sp in bank.SubPalettes)
        {
            if (sp.Kind == 1)
                sp.ShadeHazeTable = c.Bytes(shadeTable).ToArray();
            else if (sp.Kind is 2 or 6 or 8)
                sp.BlendTable = c.Bytes(65536).ToArray();
        }
        foreach (SubPalette sp in bank.SubPalettes)
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
        foreach (SubPalette sp in bank.SubPalettes)
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
        for (int i = 0; i < 256; ++i)
        {
            sp.IndexToRed[i] = sp.Colors[i * 4] / 255f;
            sp.IndexToGreen[i] = sp.Colors[i * 4 + 1] / 255f;
            sp.IndexToBlue[i] = sp.Colors[i * 4 + 2] / 255f;
            sp.IndexToAlpha[i] = i == 0 ? 0f : 1f;
        }
    }

    private static float[] ReadFloats(ref Cursor c, int count = 256)
    {
        var f = new float[count];
        var b = c.Bytes(count * 4);
        for (int i = 0; i < count; ++i)
            f[i] = BitConverter.ToSingle(b.Slice(i * 4, 4));
        return f;
    }
}
