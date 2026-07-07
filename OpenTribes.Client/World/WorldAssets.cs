using System.Buffers.Binary;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// Render-side reading of `.dml` material lists (docs/collision.md 4.2 layout, world-render.md 2.8): one entry per
// (detail, index); entries whose flags & 0xf == 3 name a bitmap at +0x10. The world renderer indexes by entry number
// (terrain material byte, interior surface material, sky panel), so a non-texture entry stays in place with a null name.
public static class RenderMaterialList
{
    public static List<string?>? Parse(byte[] d)
    {
        if (d.Length < 12 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return null;
        long pos = 8;
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
        pos += 2 + nameLen + (nameLen & 1);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4));
        if (version < 1 || version > 4)
            return null;
        uint details = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 4, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 8, 4));
        pos += 12;
        long entry = version <= 2 ? 0x30 : version == 3 ? 0x3c : 0x40;
        if (details > 4096 || count > 65536 || pos + (long)details * count * entry > d.Length)
            return null;
        var r = new List<string?>();
        for (long i = 0; i < (long)details * count; ++i)
        {
            long e = pos + i * entry;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)e, 4)) & 0xf) == 3)
            {
                int n = 0;
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

// Loads the files of one world through the resource manager and converts bitmaps to RGBA through the world palette.
public sealed class WorldAssets
{
    public ResourceManager Res { get; }
    public PaletteBank Palette { get; set; }
    private readonly byte[] mIdentityGamma = PaletteTables.GammaTable(1f);
    private readonly Dictionary<string, PbmpBitmap?> mBitmaps = new(StringComparer.OrdinalIgnoreCase);

    public WorldAssets(ResourceManager res, PaletteBank palette)
    {
        Res = res;
        Palette = palette;
    }

    public PbmpBitmap? Bitmap(string name)
    {
        if (!mBitmaps.TryGetValue(name, out PbmpBitmap? b))
            mBitmaps[name] = b = GfxAssets.LoadBitmap(Res, name);
        return b;
    }

    public List<string?>? MaterialList(string name) =>
        GfxAssets.Read(Res, name, out byte[] data) ? RenderMaterialList.Parse(data) : null;

    // RGBA8 through the subpalette of the bitmap's PiDX (renderer.md 3.4). `opaque` forces alpha 255 (terrain and
    // interior polygons draw with keyed transparency off, world-render.md 2.4 and 3.3).
    public byte[] Rgba(PbmpBitmap bmp, bool opaque)
    {
        byte[] rgba = PaletteTables.ToRgba(bmp, Palette.FindSubPalette(bmp.PaletteId), mIdentityGamma);
        if (opaque)
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
        return rgba;
    }

    // RGBA8 of the bitmap through its subpalette, packed the way the stock upload packs it (`Pack`); 24 bit bitmaps are not palette indexed.
    public byte[] PackedRgba(PbmpBitmap bmp, bool opaque)
    {
        byte[] rgba = Rgba(bmp, opaque);
        return bmp.Depth == 8 ? PaletteTables.Pack(rgba, Palette.FindSubPalette(bmp.PaletteId)) : rgba;
    }

    // Levels 1 .. MipCount-1 of the bitmap's own mip chain as RGBA8 (the stock client uploads each file level, 005d7148 loop over
    // bitmap +0x5c; the levels are not row padded). Empty when the file has no chain.
    public List<byte[]> MipRgba(PbmpBitmap bmp, bool opaque, bool quantize = true, bool keyed = false)
    {
        var levels = new List<byte[]>();
        if (bmp.Depth != 8)
            return levels;
        SubPalette sub = Palette.FindSubPalette(bmp.PaletteId);
        for (int k = 1; k <= bmp.MipLevels.Count; ++k)
        {
            int w = Math.Max(1, bmp.Width >> k), h = Math.Max(1, bmp.Height >> k);
            var level = new PbmpBitmap { Width = w, Height = h, Depth = 8, Attributes = bmp.Attributes };
            int stride = level.Stride;
            level.Pixels = new byte[stride * h];
            for (int y = 0; y < h; ++y)
                Array.Copy(bmp.MipLevels[k - 1], y * w, level.Pixels, y * stride, w);
            byte[] rgba = PaletteTables.ToRgba(level, sub, mIdentityGamma);
            if (opaque)
                for (int i = 3; i < rgba.Length; i += 4)
                    rgba[i] = 255;
            if (keyed)
                for (int i = 0; i < w * h; ++i)
                    if (bmp.MipLevels[k - 1][i] == 0)
                        rgba[i * 4 + 3] = 0;
            levels.Add(quantize ? PaletteTables.Pack(rgba, sub) : rgba);
        }
        return levels;
    }

    // A texture the way the stock upload makes it (005d3ecc): the file's own mip chain with LINEAR_MIPMAP_NEAREST when the bitmap has more
    // than one level, else LINEAR, magnification nearest for a transparent bitmap. `level0` is the converted base level, `repeat` the wrap
    // mode the caller of the upload asks for (clamp to edge or repeat).
    public IGfxTexture CreateTexture(IGfxDevice dev, PbmpBitmap bmp, byte[] level0, bool repeat, bool opaque, bool quantize, bool keyed = false)
    {
        List<byte[]> levels = MipRgba(bmp, opaque, quantize, keyed);
        var options = new TextureOptions(levels.Count > 0, repeat, true, (bmp.Attributes & BitmapAttributes.Transparent) != 0);
        return dev.CreateTexture3D(level0, bmp.Width, bmp.Height, options, levels);
    }

    // The haze colour (D34, 6.3): entry `hazeColor` of the first subpalette of the world palette.
    public (float r, float g, float b) HazeColor()
    {
        int index = (int)Math.Min(Palette.HazeColor, 255u);
        SubPalette sp = Palette.SubPalettes[0];
        return (sp.Colors[index * 4] / 255f, sp.Colors[index * 4 + 1] / 255f, sp.Colors[index * 4 + 2] / 255f);
    }
}
