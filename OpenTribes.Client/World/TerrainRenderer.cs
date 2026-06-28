using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// world-render.md 2: the terrain image. Textures: one per material of the `.dml` (the level 0 squares, 2.8), one atlas of
// 8 x 8 pixels per cell (the larger squares: stands in for the cover bitmap and the on-demand composites, Internal in
// mechanism, 2.8) and the light map (2.9). Selection, morph and haze are TerrainLod.
public sealed class TerrainRenderer : IDisposable
{
    public const int AtlasCell = 16;

    private readonly TerrainData mData;
    private readonly IGfxDevice mDev;
    private readonly WorldAssets mAssets;
    private readonly List<string?> mMaterials;
    private readonly Dictionary<int, IGfxTexture?> mMaterialTextures = new();
    private IGfxTexture? mAtlas, mLight;
    public double OriginX, OriginY, OriginZ;
    public TerrainParams Params;
    public int LastSquares, LastTriangles;
    private readonly TerrainMesh mMesh = new();

    public TerrainRenderer(IGfxDevice dev, WorldAssets assets, TerrainData data, List<string?> materials, double ox, double oy,
                           double oz, TerrainParams p)
    {
        mDev = dev;
        mAssets = assets;
        mData = data;
        mMaterials = materials;
        (OriginX, OriginY, OriginZ) = (ox, oy, oz);
        Params = p;
    }

    private IGfxTexture? MaterialTexture(int index)
    {
        if (mMaterialTextures.TryGetValue(index, out IGfxTexture? t))
            return t;
        t = null;
        string? name = index >= 0 && index < mMaterials.Count ? mMaterials[index] : null;
        PbmpBitmap? bmp = name != null ? mAssets.Bitmap(name) : null;
        if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
            t = mAssets.CreateTexture(mDev, bmp, TerrainData.QuantizeRgba5(mAssets.Rgba(bmp, true)), false, true, true);
        mMaterialTextures[index] = t;
        return t;
    }

    // 8 x 8 colours per cell: each material bitmap box-filtered down (the mip 4 of a 128 x 128 bitmap), laid out by the
    // cell's material (2.3 cover bitmap, 2.8 composite).
    public static byte[] BuildAtlas(TerrainData data, Func<int, byte[]?> materialRgba, Func<int, (int w, int h)> materialSize)
    {
        int dim = TerrainData.Cells * AtlasCell;
        var atlas = new byte[dim * dim * 4];
        var small = new Dictionary<int, byte[]>();
        byte[] Small(int m)
        {
            if (small.TryGetValue(m, out byte[]? s))
                return s;
            s = new byte[AtlasCell * AtlasCell * 4];
            byte[]? src = materialRgba(m);
            var (w, h) = materialSize(m);
            for (int y = 0; y < AtlasCell; ++y)
                for (int x = 0; x < AtlasCell; ++x)
                {
                    int sum0 = 0, sum1 = 0, sum2 = 0, n = 0;
                    if (src != null && w > 0 && h > 0)
                    {
                        int x0 = x * w / AtlasCell, x1 = Math.Max(x0 + 1, (x + 1) * w / AtlasCell);
                        int y0 = y * h / AtlasCell, y1 = Math.Max(y0 + 1, (y + 1) * h / AtlasCell);
                        for (int yy = y0; yy < y1; ++yy)
                            for (int xx = x0; xx < x1; ++xx)
                            {
                                int i = (yy * w + xx) * 4;
                                sum0 += src[i]; sum1 += src[i + 1]; sum2 += src[i + 2];
                                ++n;
                            }
                    }
                    int o = (y * AtlasCell + x) * 4;
                    s[o] = (byte)(n > 0 ? sum0 / n : 128);
                    s[o + 1] = (byte)(n > 0 ? sum1 / n : 128);
                    s[o + 2] = (byte)(n > 0 ? sum2 / n : 128);
                    s[o + 3] = 255;
                }
            small[m] = s;
            return s;
        }
        // The composite builder of 2.8 copies each cell's bitmap flipped or turned by the flags of the cell, like the level 0
        // squares do with their texture coordinates, so a transition tile keeps its edge toward the neighbour it blends with.
        for (int cy = 0; cy < TerrainData.Cells; ++cy)
            for (int cx = 0; cx < TerrainData.Cells; ++cx)
            {
                byte[] s = Small(data.Material(cx, cy));
                int e = TerrainMesher.FlagOrientation[data.Flags(cx, cy) & 7];
                for (int y = 0; y < AtlasCell; ++y)
                {
                    if (e == 0)
                    {
                        Buffer.BlockCopy(s, y * AtlasCell * 4, atlas, ((cy * AtlasCell + y) * dim + cx * AtlasCell) * 4, AtlasCell * 4);
                        continue;
                    }
                    for (int x = 0; x < AtlasCell; ++x)
                    {
                        int u = x, v = y;
                        if ((e & 4) != 0)
                            (u, v) = (v, u);
                        if ((e & 1) != 0)
                            u = AtlasCell - 1 - u;
                        if ((e & 2) != 0)
                            v = AtlasCell - 1 - v;
                        Buffer.BlockCopy(s, (v * AtlasCell + u) * 4, atlas, ((cy * AtlasCell + y) * dim + cx * AtlasCell + x) * 4, 4);
                    }
                }
            }
        return atlas;
    }

    private void EnsureTextures()
    {
        if (mLight == null)
            mLight = mDev.CreateTexture3D(mData.LightRgba(), TerrainData.Cells, TerrainData.Cells, new TextureOptions(false, true, true));
        if (mAtlas == null)
        {
            var rgbaCache = new Dictionary<int, byte[]?>();
            byte[]? Rgba(int m)
            {
                if (!rgbaCache.TryGetValue(m, out byte[]? r))
                {
                    string? name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                    PbmpBitmap? b = name != null ? mAssets.Bitmap(name) : null;
                    rgbaCache[m] = r = b != null ? TerrainData.QuantizeRgba5(mAssets.Rgba(b, true)) : null;
                }
                return r;
            }
            (int, int) Size(int m)
            {
                string? name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                PbmpBitmap? b = name != null ? mAssets.Bitmap(name) : null;
                return b == null ? (0, 0) : (b.Width, b.Height);
            }
            int dim = TerrainData.Cells * AtlasCell;
            mAtlas = mDev.CreateTexture3D(BuildAtlas(mData, Rgba, Size), dim, dim, new TextureOptions(true, true, true));
        }
    }

    // Builds the atlas and the light map now instead of at the first draw.
    public void Warm()
    {
        EnsureTextures();
        for (int i = 0; i < mMaterials.Count; ++i)
            MaterialTexture(i);
    }

    private readonly Dictionary<long, float> mGrow = new();

    public void Draw(in Camera3D cam)
    {
        EnsureTextures();
        List<TerrainSquare> squares;
        using (FramePerf.Scope("terrain-select"))
            squares = TerrainLod.Select(mData, cam, OriginX, OriginY, OriginZ, Params, mGrow);
        TerrainMesh mesh;
        using (FramePerf.Scope("terrain-mesh"))
            mesh = TerrainMesher.Build(mData, squares, cam, OriginX, OriginY, OriginZ, Params, mMesh, mGrow);
        LastSquares = squares.Count;
        LastTriangles = mesh.TriangleCount;
        // The stock sort of the emitted squares (compare function 0057642c): level 0 first, by material, then the coarser
        // squares (the atlas here). With 16 bit depth the later draw wins a tie.
        foreach (var (mat, list) in mesh.ByMaterial.OrderBy(kv => kv.Key))
            if (list.Count > 0)
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(MaterialTexture(mat), mLight));
        if (mesh.Atlas.Count > 0)
            mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Atlas), new DrawState3D(mAtlas, mLight));
    }

    public void Dispose()
    {
        foreach (IGfxTexture? t in mMaterialTextures.Values)
            t?.Dispose();
        mAtlas?.Dispose();
        mLight?.Dispose();
    }
}
