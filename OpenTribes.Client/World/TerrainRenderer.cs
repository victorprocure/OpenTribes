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
    // The point lights of the frame (nearest first); the squares they touch get their own light map (TerrainLightSet).
    public IReadOnlyList<Shapes.PointLight>? DynamicLights;
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
            t = mAssets.CreateTexture(mDev, bmp, mAssets.PackedRgba(bmp, true), false, true, true);
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
                    rgbaCache[m] = r = b != null ? mAssets.PackedRgba(b, true) : null;
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

    // The commander map picture of an area (CommandMapBake): the materials as the perspective view sees them (packed colours).
    public byte[] RenderCommandMap(double areaX, double areaY, double areaW, double areaH, Func<int, int, int, int> nearest)
    {
        var rgbaCache = new Dictionary<int, (byte[]? Rgba, int W, int H)>();
        (byte[]? Rgba, int W, int H) Material(int m)
        {
            if (!rgbaCache.TryGetValue(m, out var r))
            {
                string? name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                PbmpBitmap? b = name != null ? mAssets.Bitmap(name) : null;
                rgbaCache[m] = r = b != null ? (mAssets.PackedRgba(b, true), b.Width, b.Height) : (null, 0, 0);
            }
            return r;
        }
        return CommandMapBake.Render(mData, OriginX, OriginY, areaX, areaY, areaW, areaH, m => Material(m).Rgba, m => (Material(m).W, Material(m).H), nearest);
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
            mesh = TerrainMesher.Build(mData, squares, cam, OriginX, OriginY, OriginZ, Params, mMesh, mGrow,
                                       DynamicLights is { Count: > 0 } ? new TerrainLightSet(DynamicLights, OriginX, OriginY, OriginZ) : null);
        LastSquares = squares.Count;
        LastTriangles = mesh.TriangleCount;
        // The stock sort of the emitted squares (compare function 0057642c): level 0 first, by material, then the coarser
        // squares (the atlas here). With 16 bit depth the later draw wins a tie.
        IGfxTexture? tiles = mesh.Tiles.Count > 0 ? CreateTileAtlas(mesh) : null;
        foreach (int mat in mesh.ByMaterial.Keys.Union(mesh.DynByMaterial.Keys).OrderBy(k => k))
        {
            if (mesh.ByMaterial.TryGetValue(mat, out List<Vertex3D>? list) && list.Count > 0)
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(MaterialTexture(mat), mLight));
            if (tiles != null && mesh.DynByMaterial.TryGetValue(mat, out List<Vertex3D>? lit) && lit.Count > 0)
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lit), new DrawState3D(MaterialTexture(mat), tiles));
        }
        if (mesh.Atlas.Count > 0)
            mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Atlas), new DrawState3D(mAtlas, mLight));
        if (tiles != null && mesh.DynAtlas.Count > 0)
            mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.DynAtlas), new DrawState3D(mAtlas, tiles));
        tiles?.Dispose();
    }

    // The private light maps of the frame in one texture: the nibbles read back as n * 17 like the static light map (2.9). The
    // vertices of the lit squares hold texel coordinates until here.
    private IGfxTexture CreateTileAtlas(TerrainMesh mesh)
    {
        const int ts = TerrainMesh.TileSize, per = TerrainMesh.TilesPerRow, w = TerrainMesh.TileAtlasWidth;
        int h = (mesh.Tiles.Count + per - 1) / per * ts;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < mesh.Tiles.Count; ++i)
            for (int j = 0; j < ts; ++j)
                for (int k = 0; k < ts; ++k)
                {
                    int o = ((i / per * ts + j) * w + i % per * ts + k) * 4, n = (j * ts + k) * 3;
                    rgba[o] = (byte)(mesh.Tiles[i][n] * 17);
                    rgba[o + 1] = (byte)(mesh.Tiles[i][n + 1] * 17);
                    rgba[o + 2] = (byte)(mesh.Tiles[i][n + 2] * 17);
                    rgba[o + 3] = 255;
                }
        void Normalise(List<Vertex3D> l)
        {
            for (int i = 0; i < l.Count; ++i)
            {
                Vertex3D v = l[i];
                v.Lu /= w;
                v.Lv /= h;
                l[i] = v;
            }
        }
        Normalise(mesh.DynAtlas);
        foreach (List<Vertex3D> l in mesh.DynByMaterial.Values)
            Normalise(l);
        return mDev.CreateTexture3D(rgba, w, h, new TextureOptions(false, false, true));
    }

    // The static light map value under a world point as 0..1 colours (the nibbles over 15, ShapeBase_lightProbe 0044e6f4).
    public (float r, float g, float b) LightAt(double x, double y)
    {
        ushort w = mData.LightWord((int)Math.Floor((x - OriginX) / TerrainData.CellSize), (int)Math.Floor((y - OriginY) / TerrainData.CellSize));
        return ((w >> 8 & 0xf) / 15f, (w >> 4 & 0xf) / 15f, (w & 0xf) / 15f);
    }

    public void Dispose()
    {
        foreach (IGfxTexture? t in mMaterialTextures.Values)
            t?.Dispose();
        mAtlas?.Dispose();
        mLight?.Dispose();
    }
}
