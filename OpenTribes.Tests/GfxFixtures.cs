// Synthetic graphics files for the renderer tests (R5, R9): every byte is built here from docs/renderer.md section 2.
namespace OpenTribes.Tests;

using System.Text;
using OpenTribes.Platform.Gfx;

internal static class GfxFixtures
{
    public static void U32(List<byte> b, uint v) => Fixtures.PutU32(b, v);
    public static void U16(List<byte> b, ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
    public static void Tag(List<byte> b, string t) => b.AddRange(Encoding.Latin1.GetBytes(t));

    // tag(4) size(4) payload. No padding to even sizes (2 intro).
    public static void Chunk(List<byte> b, string tag, byte[] payload)
    {
        Tag(b, tag);
        U32(b, (uint)payload.Length);
        b.AddRange(payload);
    }

    public static byte[] Dwords(params uint[] v)
    {
        var b = new List<byte>();
        foreach (var x in v)
            U32(b, x);
        return [.. b];
    }

    // 2.2.1: PBMP with head and the chunks given. `count` defaults to the number of chunks after head.
    public static byte[] Pbmp(int w, int h, byte[] data, uint attrs = 0, uint? pidx = null, uint? detl = null,
                              IEnumerable<(string tag, byte[] body)>? extra = null, uint? count = null,
                              bool tag = true, uint topSize = 0x1234)
    {
        var chunks = new List<(string tag, byte[] body)> { ("data", data) };
        if (detl != null)
            chunks.Add(("DETL", Dwords(detl.Value)));
        if (pidx != null)
            chunks.Add(("PiDX", Dwords(pidx.Value)));
        if (extra != null)
            chunks.AddRange(extra);
        var b = new List<byte>();
        if (tag)
        {
            Tag(b, "PBMP");
            U32(b, topSize);
        }
        Chunk(b, "head", Dwords(count ?? (uint)chunks.Count, (uint)w, (uint)h, 8, attrs));
        foreach (var (t, body) in chunks)
            Chunk(b, t, body);
        return [.. b];
    }

    // 2.5: RIFF PAL form with `n` entries, color i = (i, 2 i, 3 i, flags).
    public static byte[] RiffPal(int n, bool withExtra, byte flags = 0)
    {
        var data = new List<byte>();
        U16(data, 0x0300);
        U16(data, (ushort)n);
        for (var i = 0; i < n; ++i)
            data.AddRange(new[] { (byte)i, (byte)(2 * i), (byte)(3 * i), flags });
        var form = new List<byte>();
        Tag(form, "PAL ");
        Chunk(form, "data", [.. data]);
        if (withExtra)
            foreach (var t in new[] { "offl", "tran", "unde" })
                Chunk(form, t, new byte[32]);
        var b = new List<byte>();
        Chunk(b, "RIFF", [.. form]);
        return [.. b];
    }

    // 2.3: PBMA of PBMP chunks, with optional user and rmap chunks.
    public static byte[] Pbma(IList<byte[]> bitmaps, byte[]? user = null, uint[]? rmap = null, uint? count = null)
    {
        var body = new List<byte>();
        uint n = 0;
        if (user != null)
        {
            Chunk(body, "user", user);
            ++n;
        }
        if (rmap != null)
        {
            Chunk(body, "rmap", Dwords(rmap));
            ++n;
        }
        foreach (var bmp in bitmaps)
        {
            body.AddRange(bmp);
            ++n;
        }
        var b = new List<byte>();
        Tag(b, "PBMA");
        U32(b, 0x77777);
        Chunk(b, "head", Dwords(count ?? n, (uint)bitmaps.Count));
        b.AddRange(body);
        return [.. b];
    }

    // 2.4: PL98 set. Subpalette k has color i = (i, k, seed, k) and the id and kind given. The map blob lists the
    // tables of every palette in order, then the 0x1100 block of kinds 1, 2, 6, 8.
    public static byte[] Ppl((uint id, uint kind)[] pals, uint shadeBits = 1, uint hazeSteps = 2, uint hazeColor = 254,
                             bool weights = false)
    {
        var b = new List<byte>();
        Tag(b, "PL98");
        U32(b, (uint)pals.Length);
        U32(b, shadeBits);
        U32(b, hazeSteps);
        U32(b, hazeColor);
        for (var i = 0; i < 8; ++i)
            U32(b, i == 0 ? 0xFFFFFFFF : 0);
        for (var k = 0; k < pals.Length; ++k)
        {
            for (var i = 0; i < 256; ++i)
                b.AddRange(new[] { (byte)i, (byte)k, (byte)0x55, (byte)k });
            U32(b, pals[k].id);
            U32(b, pals[k].kind);
        }
        var shadeSteps = 1 << (int)shadeBits;
        foreach (var (_, kind) in pals)
        {
            if (kind == 1)
                for (var i = 0; i < shadeSteps * 256 * (hazeSteps + 1); ++i)
                    b.Add((byte)(i * 7));
            else if (kind is 2 or 6 or 8)
                b.AddRange(new byte[65536]);
        }
        for (var k = 0; k < pals.Length; ++k)
        {
            if (pals[k].kind is not (1 or 2 or 6 or 8))
                continue;
            for (var i = 0; i < 256; ++i)
                b.Add((byte)i);
            for (var m = 0; m < 4; ++m)
                for (var i = 0; i < 256; ++i)
                    b.AddRange(BitConverter.GetBytes((((k * 4) + m) / 100f) + (i / 1000f)));
        }
        if (weights)
        {
            b.Add(1);
            for (var i = 0; i < 256; ++i)
                b.AddRange(BitConverter.GetBytes(i * 0.5f));
            U32(b, 12);
            U32(b, 34);
        }
        else
            b.Add(0);
        U32(b, 0);
        return [.. b];
    }

    // 2.6: PFON font. Glyph g (0..n-1) is the character `offset + g` unless `lookup` says otherwise.
    public static byte[] Pft(uint flags, uint cellH, uint cellW, uint baseline, int spacing, ushort lookupOffset,
                             short[] lookup, (byte sheet, byte x, byte y, byte w, byte h, sbyte bl)[] glyphs,
                             byte[] sheets, uint justification = 0, int scale = 65536)
    {
        var b = new List<byte>();
        Tag(b, "PFON");
        U32(b, 0);
        U32(b, 2);
        foreach (var v in new uint[]
                 { flags, justification, (uint)glyphs.Length, cellH, cellW, 255, 0, baseline, (uint)scale, (uint)scale, (uint)spacing })
            U32(b, v);
        U16(b, (ushort)lookup.Length);
        U16(b, lookupOffset);
        foreach (var l in lookup)
            U16(b, (ushort)l);
        foreach (var (sheet, x, y, w, h, bl) in glyphs)
            b.AddRange(new[] { sheet, x, y, w, h, (byte)bl, (byte)0, (byte)0 });
        b.AddRange(sheets);
        U32(b, 0);
        return [.. b];
    }

    // 2.2.4: classic Windows bitmap. Rows are given top-first and stored bottom-up when `bottomUp`.
    public static byte[] WindowsBmp(int w, int h, int bits, byte[] topDownRows, bool bottomUp, byte[]? bgra = null,
                                    ushort res1 = 0, ushort res2 = 0)
    {
        var stride = ((w * bits / 8) + 3) & ~3;
        var palBytes = bits == 8 ? 1024 : 0;
        var b = new List<byte>();
        Tag(b, "BM");
        U32(b, (uint)(14 + 40 + palBytes + (stride * h)));
        U16(b, res1);
        U16(b, res2);
        U32(b, (uint)(14 + 40 + palBytes));
        U32(b, 40);
        U32(b, (uint)w);
        U32(b, (uint)(bottomUp ? h : -h));
        U16(b, 1);
        U16(b, (ushort)bits);
        U32(b, 0);
        U32(b, (uint)(stride * h));
        U32(b, 0);
        U32(b, 0);
        U32(b, 0); // colors used: 0 means 256
        U32(b, 0);
        if (bits == 8)
            b.AddRange(bgra ?? new byte[1024]);
        for (var y = 0; y < h; ++y)
        {
            var sy = bottomUp ? h - 1 - y : y;
            b.AddRange(topDownRows.AsSpan(sy * stride, stride).ToArray());
        }
        return [.. b];
    }

    public static byte[] Pad4(byte[] d)
    {
        var l = new List<byte>(d);
        while (l.Count % 4 != 0)
            l.Add(0);
        return [.. l];
    }

    public static PbmpBitmap Bitmap(byte[] bytes, bool palette = false)
    {
        Assert.True(PbmpBitmap.TryParse(bytes, palette, out var bmp, out var err), err);
        return bmp!;
    }
}
