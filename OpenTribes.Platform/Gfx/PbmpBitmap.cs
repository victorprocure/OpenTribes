namespace OpenTribes.Platform.Gfx;

using System.Buffers.Binary;

// Windows BMP fallback of 2.2.4. Pixels are top-down rows of palette indices (8-bit), or
// B, G, R triples for the 24-bit Windows variant.
/// <summary>A <c>.bmp</c> PBMP bitmap, also <c>.pbm</c>, read by the same reader, with the Windows BMP fallback (docs/renderer.md 2.2, 2.2.4).</summary>
/// <remarks>Pixels are top-down rows of 8-bit palette indices, or B, G, R triples for the 24-bit Windows variant.</remarks>
public sealed class PbmpBitmap
{
    /// <summary>The width in pixels.</summary>
    public int Width;
    /// <summary>The height in pixels.</summary>
    public int Height;
    /// <summary>The bits per pixel.</summary>
    public int Depth = 8;
    /// <summary>The attribute bits.</summary>
    public BitmapAttributes Attributes;
    /// <summary>The number of chunks after the head chunk (low 24 bits of the first head dword).</summary>
    public uint ChunkCount;
    /// <summary>Level 0, Stride * Height bytes. The guard rows of the original are not kept.</summary>
    public byte[] Pixels = [];
    /// <summary>The number of levels from DETL, 1 when the chunk is absent.</summary>
    public int MipCount = 1;
    /// <summary>The levels 1 to MipCount - 1 that fit in the data chunk, (w &gt;&gt; k) * (h &gt;&gt; k) bytes each with unpadded rows (docs/renderer.md 2.2.2).</summary>
    public List<byte[]> MipLevels = [];
    /// <summary>The PiDX palette id. 0xffffffff selects the first subpalette (docs/renderer.md 2.2.3).</summary>
    public uint PaletteId = SubPalette.NoId;
    /// <summary>The embedded RIFF palette. It is read only when the caller asked for it (docs/renderer.md 2.2.1).</summary>
    public PaletteBank? Palette;

    /// <summary>Gets the row stride in bytes, padded to 4 (docs/renderer.md 2.2.2).</summary>
    public int Stride => ((Width * Depth / 8) + 3) & ~3;

    /// <summary>Gets the blend mode of the attributes.</summary>
    public BlendMode Blend => Attributes.Blend();

    /// <summary>Parses a bitmap file.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="includePalette">Whether to read the embedded palette.</param>
    /// <param name="bitmap">Receives the bitmap.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>True on success.</returns>
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
        var headSize = c.U32();
        if (headSize < 20)
            throw new GfxFormatException("head chunk too small");
        var head = c.Bytes(headSize);
        var bmp = new PbmpBitmap();
        var first = BinaryPrimitives.ReadUInt32LittleEndian(head);
        bmp.ChunkCount = first & 0xFFFFFF;
        var w = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        var h = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        var depth = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        var attrs = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
        if (w > 16384 || h > 16384)
            throw new GfxFormatException("bitmap too large");
        if (depth is not 8 and not 24)
            throw new GfxFormatException("unsupported depth");
        bmp.Width = (int)w;
        bmp.Height = (int)h;
        bmp.Depth = (int)depth;
        bmp.Attributes = (BitmapAttributes)(attrs | (uint)BitmapAttributes.OwnsMemory);

        byte[]? data = null;
        for (uint i = 0; i < bmp.ChunkCount; ++i)
        {
            var tag = c.Tag();
            var size = c.U32();
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

        var level0 = (long)bmp.Stride * bmp.Height;
        if (data.Length < level0)
            throw new GfxFormatException("data chunk shorter than the bitmap");
        bmp.Pixels = new byte[level0];
        Array.Copy(data, bmp.Pixels, level0);

        // Mip chain (2.2.2): level k has (w >> k) * (h >> k) bytes after level 0.
        var offset = level0;
        for (var k = 1; k < bmp.MipCount; ++k)
        {
            var size = (long)Math.Max(1, bmp.Width >> k) * Math.Max(1, bmp.Height >> k) * (bmp.Depth / 8);
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
        var start = c.Pos;
        c.U16();
        c.U32(); // file size
        var res1 = c.U16();
        var res2 = c.U16();
        var offBits = c.U32();
        var infoSize = c.U32();
        if (infoSize < 40)
            throw new GfxFormatException("unsupported BMP info header");
        var width = (int)c.U32();
        var height = (int)c.U32();
        c.U16(); // planes
        int bits = c.U16();
        c.U32(); // compression
        c.U32(); // image size
        c.U32();
        c.U32();
        var colorsUsed = c.U32();
        c.U32();
        if (bits is not 8 and not 24)
            throw new GfxFormatException("only 8 and 24 bit BMP files are read");
        if (width <= 0 || height == 0 || width > 16384 || Math.Abs((long)height) > 16384)
            throw new GfxFormatException("bad BMP size");
        var bottomUp = height > 0;
        var h = Math.Abs(height);

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
                for (var i = 0; i < colors; ++i)
                {
                    rgba[i * 4] = table[(i * 4) + 2];
                    rgba[(i * 4) + 1] = table[(i * 4) + 1];
                    rgba[(i * 4) + 2] = table[i * 4];
                    rgba[(i * 4) + 3] = 4;
                }
                bmp.Palette = PaletteBank.FromColors(rgba, 256);
            }
        }

        var stride = bmp.Stride;
        c.Pos = start + (int)offBits;
        var src = c.Bytes((long)stride * h);
        bmp.Pixels = new byte[(long)stride * h];
        for (var y = 0; y < h; ++y)
        {
            var sy = bottomUp ? h - 1 - y : y;
            src.Slice(sy * stride, stride).CopyTo(bmp.Pixels.AsSpan(y * stride, stride));
        }
        return bmp;
    }
}
