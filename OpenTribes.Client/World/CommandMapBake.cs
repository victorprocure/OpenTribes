using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// The base picture of the commander map (docs/world-render.md 2.11): FearGui::TSCommander `rebuildCommandMap` [code 0041e3d4,
// 004cef14] renders the world from above with an orthographic camera into 8 x 8 tiles of 128 x 128 pixels, 8 bit bitmaps of the
// mission palette, and keeps the result as 4 x 4 bitmaps of 256 x 256. The camera stands over the middle of the mission area
// (MissionCenterPos x y w h, the lower left corner and the extent) and its view volume is exactly the area, so one pixel of the
// 1024 x 1024 picture is area / 1024 world units and the top row is the north edge [code 004d0ef8: left = -w / 2, top = +h / 2,
// camera at area corner + extent / 2, z 500, looking down]. The script runs it with `$pref::terrainTextureDetail` 1 (the finest
// textures [code client.cs onClientGhostAlwaysDone]).
//
// What is drawn here is the terrain: the per cell material bitmap in the orientation of the cell flags, times the light map
// (the same two inputs as the perspective renderer, world-render.md 2.8, 2.9), taken per pixel instead of by triangles. One pixel is
// about one world unit and a cell is eight, so each texel of the cell picture (the material box filtered to 16 x 16, 2.8) is about
// two pixels; four samples per pixel stand in for the area average of the software renderer. The picture is 8 bit like the stock
// tile bitmaps: every colour becomes the nearest entry of the palette (Internal in mechanism: the software renderer reaches the
// index through the palette shade tables). Squares flagged empty stay index 0.
public static class CommandMapBake
{
    public const int Size = 1024;
    public const int TileSize = 256;
    public const int TilesPerSide = 4;
    public const int CellPixels = TerrainRenderer.AtlasCell;

    // The 8 bit picture, rows top to bottom, `Size` x `Size` palette indices.
    public static byte[] Render(TerrainData data, double originX, double originY, double areaX, double areaY, double areaW, double areaH,
                                Func<int, byte[]?> materialRgba, Func<int, (int w, int h)> materialSize, Func<int, int, int, int> nearest)
    {
        var small = new Dictionary<int, byte[]>();
        byte[] Small(int m)
        {
            if (small.TryGetValue(m, out byte[]? s))
                return s;
            s = SmallCell(materialRgba(m), materialSize(m));
            small[m] = s;
            return s;
        }
        var picture = new byte[Size * Size];
        var cache = new Dictionary<int, int>();
        double cs = TerrainData.CellSize;
        var light = new (float r, float g, float b)[2, 2];
        for (int py = 0; py < Size; ++py)
        {
            for (int px = 0; px < Size; ++px)
            {
                double wxc = areaX + (px + 0.5) * areaW / Size, wyc = areaY + areaH - (py + 0.5) * areaH / Size;
                double fxc = (wxc - originX) / cs, fyc = (wyc - originY) / cs;
                int cxc = (int)Math.Floor(fxc), cyc = (int)Math.Floor(fyc);
                if (data.IsEmptySquare(cxc, cyc))
                    continue;
                // The light map is one value per corner, linear between them (a linear texture whose texels sit on the corners).
                float lx = (float)(fxc - cxc), ly = (float)(fyc - cyc);
                for (int j = 0; j < 2; ++j)
                    for (int i = 0; i < 2; ++i)
                    {
                        var (lr, lg, lb) = TerrainData.ExpandLightPacked(data.LightWord(cxc + i, cyc + j));
                        light[i, j] = (lr / 255f, lg / 255f, lb / 255f);
                    }
                float w00 = (1 - lx) * (1 - ly), w10 = lx * (1 - ly), w01 = (1 - lx) * ly, w11 = lx * ly;
                float lightR = light[0, 0].r * w00 + light[1, 0].r * w10 + light[0, 1].r * w01 + light[1, 1].r * w11;
                float lightG = light[0, 0].g * w00 + light[1, 0].g * w10 + light[0, 1].g * w01 + light[1, 1].g * w11;
                float lightB = light[0, 0].b * w00 + light[1, 0].b * w10 + light[0, 1].b * w01 + light[1, 1].b * w11;
                int sr = 0, sg = 0, sb = 0;
                for (int k = 0; k < 4; ++k)
                {
                    double wx = areaX + (px + 0.25 + 0.5 * (k & 1)) * areaW / Size, wy = areaY + areaH - (py + 0.25 + 0.5 * (k >> 1)) * areaH / Size;
                    double fx = (wx - originX) / cs, fy = (wy - originY) / cs;
                    int cx = (int)Math.Floor(fx), cy = (int)Math.Floor(fy);
                    byte[] cell = Small(data.Material(cx, cy));
                    int e = TerrainMesher.FlagOrientation[data.Flags(cx, cy) & 7];
                    int u = Math.Min(CellPixels - 1, (int)((fx - cx) * CellPixels)), v = Math.Min(CellPixels - 1, (int)((fy - cy) * CellPixels));
                    if ((e & 4) != 0)
                        (u, v) = (v, u);
                    if ((e & 1) != 0)
                        u = CellPixels - 1 - u;
                    if ((e & 2) != 0)
                        v = CellPixels - 1 - v;
                    int o = (v * CellPixels + u) * 4;
                    sr += cell[o];
                    sg += cell[o + 1];
                    sb += cell[o + 2];
                }
                int r = Math.Min(255, (int)(sr / 4 * lightR + 0.5f)), g = Math.Min(255, (int)(sg / 4 * lightG + 0.5f)),
                    b = Math.Min(255, (int)(sb / 4 * lightB + 0.5f));
                int key = r << 16 | g << 8 | b;
                if (!cache.TryGetValue(key, out int idx))
                    cache[key] = idx = nearest(r, g, b);
                picture[py * Size + px] = (byte)idx;
            }
        }
        return picture;
    }

    // The material bitmap box filtered down to CellPixels x CellPixels (RGBA), like TerrainRenderer.BuildAtlas.
    private static byte[] SmallCell(byte[]? src, (int w, int h) size)
    {
        var s = new byte[CellPixels * CellPixels * 4];
        for (int y = 0; y < CellPixels; ++y)
            for (int x = 0; x < CellPixels; ++x)
            {
                int a = 0, b = 0, c = 0, n = 0;
                var (w, h) = size;
                if (src != null && w > 0 && h > 0)
                {
                    int x0 = x * w / CellPixels, x1 = Math.Max(x0 + 1, (x + 1) * w / CellPixels);
                    int y0 = y * h / CellPixels, y1 = Math.Max(y0 + 1, (y + 1) * h / CellPixels);
                    for (int yy = y0; yy < y1; ++yy)
                        for (int xx = x0; xx < x1; ++xx)
                        {
                            int i = (yy * w + xx) * 4;
                            a += src[i]; b += src[i + 1]; c += src[i + 2];
                            ++n;
                        }
                }
                int o = (y * CellPixels + x) * 4;
                s[o] = (byte)(n > 0 ? a / n : 128);
                s[o + 1] = (byte)(n > 0 ? b / n : 128);
                s[o + 2] = (byte)(n > 0 ? c / n : 128);
                s[o + 3] = 255;
            }
        return s;
    }

    // Cuts the picture into the 4 x 4 bitmaps of 256 x 256 the control keeps (row major, [row * 4 + column]).
    public static PbmpBitmap[] Tiles(byte[] picture)
    {
        var tiles = new PbmpBitmap[TilesPerSide * TilesPerSide];
        for (int ty = 0; ty < TilesPerSide; ++ty)
            for (int tx = 0; tx < TilesPerSide; ++tx)
            {
                var b = new PbmpBitmap { Width = TileSize, Height = TileSize, Depth = 8 };
                b.Pixels = new byte[b.Stride * TileSize];
                for (int y = 0; y < TileSize; ++y)
                    Array.Copy(picture, (ty * TileSize + y) * Size + tx * TileSize, b.Pixels, y * b.Stride, TileSize);
                tiles[ty * TilesPerSide + tx] = b;
            }
        return tiles;
    }
}
