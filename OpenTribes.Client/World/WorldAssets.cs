namespace OpenTribes.Client.World;

using System.Buffers.Binary;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;

/// <summary>Reads <c>.dml</c> material lists for the renderer.</summary>
/// <remarks>docs/collision.md 4.2 layout, world-render.md 2.8. One entry per (detail, index). Entries whose flags &amp; 0xf == 3 name a bitmap at +0x10. The world renderer indexes by entry number (terrain material byte, interior surface material, sky panel), so a non-texture entry stays in place with a null name.</remarks>
public static class RenderMaterialList
{
    /// <summary>Parses a material list.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>The bitmap name of each entry, null for an entry without a bitmap, or null when the file is not a material list.</returns>
    public static List<string?>? Parse(byte[] d)
    {
        if (d.Length < 12 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return null;
        long pos = 8;
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
        pos += 2 + nameLen + (nameLen & 1);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4));
        if (version is < 1 or > 4)
            return null;
        var details = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 4, 4));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 8, 4));
        pos += 12;
        long entry = version <= 2 ? 0x30 : version == 3 ? 0x3c : 0x40;
        if (details > 4096 || count > 65536 || pos + ((long)details * count * entry) > d.Length)
            return null;
        var r = new List<string?>();
        for (long i = 0; i < (long)details * count; ++i)
        {
            var e = pos + (i * entry);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)e, 4)) & 0xf) == 3)
            {
                var n = 0;
                while (n < 0x20 && d[e + 0x10 + n] != 0)
                    ++n;
                r.Add(System.Text.Encoding.Latin1.GetString(d, (int)e + 0x10, n));
            }
            else
            {
                r.Add(null);
            }
        }
        return r;
    }
}

/// <summary>Loads the files of one world through the resource manager and converts bitmaps to RGBA through the world palette.</summary>
public sealed class WorldAssets
{
    /// <summary>The resources the files load from.</summary>
    public ResourceManager Res { get; }
    /// <summary>The world palette.</summary>
    public PaletteBank Palette { get; set; }
    private byte[] mGammaTable = PaletteTables.GammaTable(1f);

    /// <summary>The display gamma (<c>$pref::Display::gammaValue</c>).</summary>
    /// <remarks>The table 005d4880 builds serves every texture upload of the surface (005d3ecc), world and GUI alike. The upload routines 005d5164, 005d5784, 005d5a3c, 005d5e34, 005d7148 and 005d7674 read the same per palette tables.</remarks>
    public float Gamma
    {
        get;
        set
        {
            if (value == field)
                return;
            field = value;
            mGammaTable = PaletteTables.GammaTable(value);
        }
    } = 1f;
    private readonly Dictionary<string, PbmpBitmap?> mBitmaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the loader.</summary>
    /// <param name="res">The resources to load from.</param>
    /// <param name="palette">The world palette.</param>
    public WorldAssets(ResourceManager res, PaletteBank palette)
    {
        Res = res;
        Palette = palette;
    }

    /// <summary>Loads a bitmap once and caches it.</summary>
    /// <param name="name">The bitmap name.</param>
    /// <returns>The bitmap, or null when it is missing.</returns>
    public PbmpBitmap? Bitmap(string name)
    {
        if (!mBitmaps.TryGetValue(name, out PbmpBitmap? b))
            mBitmaps[name] = b = GfxAssets.LoadBitmap(Res, name);
        return b;
    }

    /// <summary>Loads and parses a material list.</summary>
    /// <param name="name">The <c>.dml</c> file name.</param>
    /// <returns>The bitmap names, or null when the file is missing or invalid.</returns>
    public List<string?>? MaterialList(string name) =>
        GfxAssets.Read(Res, name, out var data) ? RenderMaterialList.Parse(data) : null;

    /// <summary>Converts a bitmap to RGBA8 through the subpalette of its PiDX.</summary>
    /// <param name="bmp">The bitmap.</param>
    /// <param name="opaque">True to force alpha 255. Terrain and interior polygons draw with keyed transparency off.</param>
    /// <returns>The RGBA pixels.</returns>
    /// <remarks>renderer.md 3.4. For opaque, world-render.md 2.4 and 3.3.</remarks>
    public byte[] Rgba(PbmpBitmap bmp, bool opaque)
    {
        var rgba = PaletteTables.ToRgba(bmp, Palette.FindSubPalette(bmp.PaletteId), mGammaTable);
        if (opaque)
            for (var i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
        return rgba;
    }

    /// <summary>Converts a bitmap to RGBA8 through its subpalette, packed the way the stock upload packs it. 24 bit bitmaps are not palette indexed.</summary>
    /// <param name="bmp">The bitmap.</param>
    /// <param name="opaque">True to force alpha 255.</param>
    /// <returns>The RGBA pixels.</returns>
    public byte[] PackedRgba(PbmpBitmap bmp, bool opaque)
    {
        var rgba = Rgba(bmp, opaque);
        return bmp.Depth == 8 ? PaletteTables.Pack(rgba, Palette.FindSubPalette(bmp.PaletteId)) : rgba;
    }

    /// <summary>Converts levels 1 to MipCount - 1 of the own mip chain of a bitmap to RGBA8.</summary>
    /// <param name="bmp">The bitmap.</param>
    /// <param name="opaque">True to force alpha 255.</param>
    /// <param name="quantize">True to pack the colours the way the stock upload does.</param>
    /// <param name="keyed">True to keep the transparent key.</param>
    /// <returns>The levels, or an empty list when the file has no chain.</returns>
    /// <remarks>The stock client uploads each file level (005d7148, loop over bitmap +0x5c). The levels are not row padded.</remarks>
    public List<byte[]> MipRgba(PbmpBitmap bmp, bool opaque, bool quantize = true, bool keyed = false)
    {
        var levels = new List<byte[]>();
        if (bmp.Depth != 8)
            return levels;
        SubPalette sub = Palette.FindSubPalette(bmp.PaletteId);
        for (var k = 1; k <= bmp.MipLevels.Count; ++k)
        {
            int w = Math.Max(1, bmp.Width >> k), h = Math.Max(1, bmp.Height >> k);
            var level = new PbmpBitmap { Width = w, Height = h, Depth = 8, Attributes = bmp.Attributes };
            var stride = level.Stride;
            level.Pixels = new byte[stride * h];
            for (var y = 0; y < h; ++y)
                Array.Copy(bmp.MipLevels[k - 1], y * w, level.Pixels, y * stride, w);
            var rgba = PaletteTables.ToRgba(level, sub, mGammaTable);
            if (opaque)
                for (var i = 3; i < rgba.Length; i += 4)
                    rgba[i] = 255;
            if (keyed)
                for (var i = 0; i < w * h; ++i)
                    if (bmp.MipLevels[k - 1][i] == 0)
                        rgba[(i * 4) + 3] = 0;
            levels.Add(quantize ? PaletteTables.Pack(rgba, sub) : rgba);
        }
        return levels;
    }

    /// <summary>Creates a texture the way the stock upload makes it.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="bmp">The bitmap.</param>
    /// <param name="level0">The converted base level.</param>
    /// <param name="repeat">True to repeat, false to clamp to edge: the wrap mode the caller of the upload asks for.</param>
    /// <param name="opaque">True when the bitmap is opaque.</param>
    /// <param name="quantize">True to pack the colours the way the stock upload does.</param>
    /// <param name="keyed">True to keep the transparent key.</param>
    /// <returns>The texture.</returns>
    /// <remarks>Follows 005d3ecc: the own mip chain of the file with LINEAR_MIPMAP_NEAREST when the bitmap has more than one level, else LINEAR. Magnification is nearest for a transparent bitmap.</remarks>
    public IGfxTexture CreateTexture(IGfxDevice dev, PbmpBitmap bmp, byte[] level0, bool repeat, bool opaque, bool quantize, bool keyed = false)
    {
        List<byte[]> levels = MipRgba(bmp, opaque, quantize, keyed);
        var options = new TextureOptions(levels.Count > 0, repeat, true, (bmp.Attributes & BitmapAttributes.Transparent) != 0);
        return dev.CreateTexture3D(level0, bmp.Width, bmp.Height, options, levels);
    }

    /// <summary>Reads the haze colour (D34, 6.3): entry <c>hazeColor</c> of the first subpalette of the world palette.</summary>
    /// <returns>The red, green and blue values.</returns>
    public (float r, float g, float b) HazeColor()
    {
        var index = (int)Math.Min(Palette.HazeColor, 255u);
        SubPalette sp = Palette.SubPalettes[0];
        return (sp.Colors[index * 4] / 255f, sp.Colors[(index * 4) + 1] / 255f, sp.Colors[(index * 4) + 2] / 255f);
    }
}
