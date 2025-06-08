using System.Buffers.Binary;

namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.2: `.bmp` PBMP bitmap (also `.pbm`, read by the same reader), with the
// Windows BMP fallback of 2.2.4. Pixels are top-down rows of palette indices (8-bit), or
// B, G, R triples for the 24-bit Windows variant.
public sealed class PbmpBitmap
{
    public int Width;
    public int Height;
    public int Depth = 8;
    public BitmapAttributes Attributes;
    // Number of chunks after the head chunk (low 24 bits of the first head dword).
    public uint ChunkCount;
    // Level 0, Stride * Height bytes. The guard rows of the original are not kept.
    public byte[] Pixels = Array.Empty<byte>();
    // DETL: number of levels, 1 when the chunk is absent.
    public int MipCount = 1;
    // Levels 1 .. MipCount-1 that fit in the data chunk; (w >> k) * (h >> k) bytes each, rows unpadded (2.2.2).
    public List<byte[]> MipLevels = new();
    // PiDX: 0xffffffff selects the first subpalette (2.2.3).
    public uint PaletteId = SubPalette.NoId;
    // Embedded RIFF palette; read only when the caller asked for it (2.2.1).
    public PaletteBank? Palette;

    public int Stride => ((Width * Depth / 8) + 3) & ~3; // 2.2.2

    public BlendMode Blend => Attributes.Blend();

    public static bool TryParse(ReadOnlySpan<byte> data, bool includePalette, out PbmpBitmap? bitmap, out string error)
    {
        bitmap = null;
        error = "";
        try
        {
            var c = new Cursor(data);
            bitmap = Read(ref c, includePalette);
            return true;
        }
        catch (GfxFormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    // Reads one bitmap at the cursor. The cursor may be at `PBMP` (a no-op tag, its size is ignored) or at
    // `head`; it ends after the last chunk of the bitmap, which is what the array reader needs (2.3).
    internal static PbmpBitmap Read(ref Cursor c, bool includePalette)
    {
        if (c.Remaining >= 2 && c.Data[c.Pos] == (byte)'B' && c.Data[c.Pos + 1] == (byte)'M')
            return ReadWindows(ref c, includePalette);

        if (c.PeekTag() == "PBMP")
        {
            c.Tag();
            c.U32(); // size field: not the length of the rest (2.2.5), ignored
        }
        if (c.Tag() != "head")
            throw new GfxFormatException("first chunk is not head");
        uint headSize = c.U32();
        if (headSize < 20)
            throw new GfxFormatException("head chunk too small");
        var head = c.Bytes(headSize);
        var bmp = new PbmpBitmap();
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(head);
        bmp.ChunkCount = first & 0xFFFFFF;
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        uint depth = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
        if (w > 16384 || h > 16384)
            throw new GfxFormatException("bitmap too large");
        if (depth != 8 && depth != 24)
            throw new GfxFormatException("unsupported depth");
        bmp.Width = (int)w;
        bmp.Height = (int)h;
        bmp.Depth = (int)depth;
        bmp.Attributes = (BitmapAttributes)(attrs | (uint)BitmapAttributes.OwnsMemory);

        byte[]? data = null;
        for (uint i = 0; i < bmp.ChunkCount; ++i)
        {
            string tag = c.Tag();
            uint size = c.U32();
            var body = c.Bytes(size);
            switch (tag)
            {
                case "data":
                    data = body.ToArray();
                    break;
                case "DETL":
                    if (size >= 4)
                        bmp.MipCount = (int)Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(body), 1u, 16u);
                    break;
                case "PiDX":
                    if (size >= 4)
                        bmp.PaletteId = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    break;
                case "RIFF":
                    if (includePalette)
                    {
                        // The payload starts at `PAL `; the palette reader accepts that (2.5).
                        var rc = new Cursor(body.ToArray());
                        bmp.Palette = PaletteBank.ReadPal(ref rc);
                    }
                    break;
                default:
                    break; // unknown chunks are skipped by size (2.2.1)
            }
        }
        if (data == null)
            throw new GfxFormatException("no data chunk");

        long level0 = (long)bmp.Stride * bmp.Height;
        if (data.Length < level0)
            throw new GfxFormatException("data chunk shorter than the bitmap");
        bmp.Pixels = new byte[level0];
        Array.Copy(data, bmp.Pixels, level0);

        // Mip chain (2.2.2): level k has (w >> k) * (h >> k) bytes after level 0.
        long offset = level0;
        for (int k = 1; k < bmp.MipCount; ++k)
        {
            long size = (long)Math.Max(1, bmp.Width >> k) * Math.Max(1, bmp.Height >> k) * (bmp.Depth / 8);
            if (offset + size > data.Length)
                break;
            var level = new byte[size];
            Array.Copy(data, offset, level, 0, size);
            bmp.MipLevels.Add(level);
            offset += size;
        }
        return bmp;
    }

    // 2.2.4: classic Windows bitmap, 8 or 24 bit. The cursor must be at `BM`.
    private static PbmpBitmap ReadWindows(ref Cursor c, bool includePalette)
    {
        int start = c.Pos;
        c.U16();
        c.U32(); // file size
        ushort res1 = c.U16();
        ushort res2 = c.U16();
        uint offBits = c.U32();
        uint infoSize = c.U32();
        if (infoSize < 40)
            throw new GfxFormatException("unsupported BMP info header");
        int width = (int)c.U32();
        int height = (int)c.U32();
        c.U16(); // planes
        int bits = c.U16();
        c.U32(); // compression
        c.U32(); // image size
        c.U32();
        c.U32();
        uint colorsUsed = c.U32();
        c.U32();
        if (bits != 8 && bits != 24)
            throw new GfxFormatException("only 8 and 24 bit BMP files are read");
        if (width <= 0 || height == 0 || width > 16384 || Math.Abs((long)height) > 16384)
            throw new GfxFormatException("bad BMP size");
        bool bottomUp = height > 0;
        int h = Math.Abs(height);

        var bmp = new PbmpBitmap
        {
            Width = width,
            Height = h,
            Depth = bits,
            Attributes = BitmapAttributes.OwnsMemory,
        };
        // Palette index in the reserved words (2.2.4).
        if (res1 == 0xF5F7 && res2 != 0xFFFF)
            bmp.PaletteId = res2;

        if (bits == 8)
        {
            long colors = colorsUsed == 0 ? 256 : colorsUsed;
            if (colors > 256)
                throw new GfxFormatException("bad BMP palette size");
            c.Pos = start + 14 + (int)infoSize;
            var table = c.Bytes(colors * 4); // B, G, R, reserved
            if (includePalette)
            {
                var rgba = new byte[1024];
                for (int i = 0; i < colors; ++i)
                {
                    rgba[i * 4] = table[i * 4 + 2];
                    rgba[i * 4 + 1] = table[i * 4 + 1];
                    rgba[i * 4 + 2] = table[i * 4];
                    rgba[i * 4 + 3] = 4;
                }
                bmp.Palette = PaletteBank.FromColors(rgba, 256);
            }
        }

        int stride = bmp.Stride;
        c.Pos = start + (int)offBits;
        var src = c.Bytes((long)stride * h);
        bmp.Pixels = new byte[(long)stride * h];
        for (int y = 0; y < h; ++y)
        {
            int sy = bottomUp ? h - 1 - y : y;
            src.Slice(sy * stride, stride).CopyTo(bmp.Pixels.AsSpan(y * stride, stride));
        }
        return bmp;
    }
}
