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

    // The haze colour (D34, 6.3): entry `hazeColor` of the first subpalette of the world palette.
    public (float r, float g, float b) HazeColor()
    {
        int index = (int)Math.Min(Palette.HazeColor, 255u);
        SubPalette sp = Palette.SubPalettes[0];
        return (sp.Colors[index * 4] / 255f, sp.Colors[index * 4 + 1] / 255f, sp.Colors[index * 4 + 2] / 255f);
    }
}
