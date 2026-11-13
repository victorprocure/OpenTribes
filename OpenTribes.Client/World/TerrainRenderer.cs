namespace OpenTribes.Client.World;

using OpenTribes.Platform.Gfx;

/// <summary>The terrain image.</summary>
/// <remarks>world-render.md 2. Textures: one per material of the <c>.dml</c> (the level 0 squares, 2.8), one atlas of 8 x 8 pixels per cell (the larger squares: stands in for the cover bitmap and the on demand composites, 2.8) and the light map (2.9). Selection, morph and haze are <c>TerrainLod</c>.</remarks>
public sealed class TerrainRenderer : IDisposable
{
    /// <summary>The side of one cell of the atlas in pixels.</summary>
    public const int AtlasCell = 16;

    private readonly TerrainData mData;
    private readonly IGfxDevice mDev;
    private readonly WorldAssets mAssets;
    private readonly List<string?> mMaterials;
    private readonly Dictionary<int, IGfxTexture?> mMaterialTextures = [];
    private IGfxTexture? mAtlas, mLight;
    /// <summary>The x of the terrain origin.</summary>
    public double OriginX;
    /// <summary>The y of the terrain origin.</summary>
    public double OriginY;
    /// <summary>The z of the terrain origin.</summary>
    public double OriginZ;
    /// <summary>The terrain parameters.</summary>
    public TerrainParams Params;
    /// <summary>The number of squares the last draw rendered.</summary>
    public int LastSquares;
    /// <summary>The number of triangles the last draw rendered.</summary>
    public int LastTriangles;
    /// <summary>The point lights of the frame, nearest first. The squares they touch get their own light map (<see cref="TerrainLightSet"/>).</summary>
    public IReadOnlyList<Shapes.PointLight>? DynamicLights;
    private readonly TerrainMesh mMesh = new();

    /// <summary>Creates the renderer.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="assets">The world assets.</param>
    /// <param name="data">The terrain data.</param>
    /// <param name="materials">The bitmap name of each material.</param>
    /// <param name="ox">The x of the terrain origin.</param>
    /// <param name="oy">The y of the terrain origin.</param>
    /// <param name="oz">The z of the terrain origin.</param>
    /// <param name="p">The terrain parameters.</param>
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
        if (mMaterialTextures.TryGetValue(index, out var t))
            return t;
        t = null;
        var name = index >= 0 && index < mMaterials.Count ? mMaterials[index] : null;
        var bmp = name != null ? mAssets.Bitmap(name) : null;
        if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
            t = mAssets.CreateTexture(mDev, bmp, mAssets.PackedRgba(bmp, true), false, true, true);
        mMaterialTextures[index] = t;
        return t;
    }

    /// <summary>Builds the atlas of 8 x 8 colours per cell: each material bitmap box filtered down (the mip 4 of a 128 x 128 bitmap), laid out by the material of the cell.</summary>
    /// <param name="data">The terrain data.</param>
    /// <param name="materialRgba">Returns the RGBA pixels of a material, or null.</param>
    /// <param name="materialSize">Returns the width and height of a material bitmap.</param>
    /// <returns>The atlas pixels.</returns>
    /// <remarks>world-render.md 2.3 (cover bitmap) and 2.8 (composite).</remarks>
    public static byte[] BuildAtlas(TerrainData data, Func<int, byte[]?> materialRgba, Func<int, (int w, int h)> materialSize)
    {
        var dim = TerrainData.Cells * AtlasCell;
        var atlas = new byte[dim * dim * 4];
        var small = new Dictionary<int, byte[]>();
        byte[] Small(int m)
        {
            if (small.TryGetValue(m, out var s))
                return s;
            s = new byte[AtlasCell * AtlasCell * 4];
            var src = materialRgba(m);
            var (w, h) = materialSize(m);
            for (var y = 0; y < AtlasCell; ++y)
                for (var x = 0; x < AtlasCell; ++x)
                {
                    int sum0 = 0, sum1 = 0, sum2 = 0, n = 0;
                    if (src != null && w > 0 && h > 0)
                    {
                        int x0 = x * w / AtlasCell, x1 = Math.Max(x0 + 1, (x + 1) * w / AtlasCell);
                        int y0 = y * h / AtlasCell, y1 = Math.Max(y0 + 1, (y + 1) * h / AtlasCell);
                        for (var yy = y0; yy < y1; ++yy)
                            for (var xx = x0; xx < x1; ++xx)
                            {
                                var i = ((yy * w) + xx) * 4;
                                sum0 += src[i];
                                sum1 += src[i + 1];
                                sum2 += src[i + 2];
                                ++n;
                            }
                    }
                    var o = ((y * AtlasCell) + x) * 4;
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
        for (var cy = 0; cy < TerrainData.Cells; ++cy)
            for (var cx = 0; cx < TerrainData.Cells; ++cx)
            {
                var s = Small(data.Material(cx, cy));
                var e = TerrainMesher.FlagOrientation[data.Flags(cx, cy) & 7];
                for (var y = 0; y < AtlasCell; ++y)
                {
                    if (e == 0)
                    {
                        Buffer.BlockCopy(s, y * AtlasCell * 4, atlas, ((((cy * AtlasCell) + y) * dim) + (cx * AtlasCell)) * 4, AtlasCell * 4);
                        continue;
                    }
                    for (var x = 0; x < AtlasCell; ++x)
                    {
                        int u = x, v = y;
                        if ((e & 4) != 0)
                            (u, v) = (v, u);
                        if ((e & 1) != 0)
                            u = AtlasCell - 1 - u;
                        if ((e & 2) != 0)
                            v = AtlasCell - 1 - v;
                        Buffer.BlockCopy(s, ((v * AtlasCell) + u) * 4, atlas, ((((cy * AtlasCell) + y) * dim) + (cx * AtlasCell) + x) * 4, 4);
                    }
                }
            }
        return atlas;
    }

    private void EnsureTextures()
    {
        mLight ??= mDev.CreateTexture3D(mData.LightRgba(), TerrainData.Cells, TerrainData.Cells, new TextureOptions(false, true, true));
        if (mAtlas == null)
        {
            var rgbaCache = new Dictionary<int, byte[]?>();
            byte[]? Rgba(int m)
            {
                if (!rgbaCache.TryGetValue(m, out var r))
                {
                    var name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                    var b = name != null ? mAssets.Bitmap(name) : null;
                    rgbaCache[m] = r = b != null ? mAssets.PackedRgba(b, true) : null;
                }
                return r;
            }
            (int, int) Size(int m)
            {
                var name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                var b = name != null ? mAssets.Bitmap(name) : null;
                return b == null ? (0, 0) : (b.Width, b.Height);
            }
            var dim = TerrainData.Cells * AtlasCell;
            mAtlas = mDev.CreateTexture3D(BuildAtlas(mData, Rgba, Size), dim, dim, new TextureOptions(true, true, true));
        }
    }

    /// <summary>Builds the atlas, the light map and the material textures now instead of at the first draw.</summary>
    public void Warm()
    {
        EnsureTextures();
        for (var i = 0; i < mMaterials.Count; ++i)
            MaterialTexture(i);
    }

    /// <summary>Renders the commander map picture of an area: the materials as the perspective view sees them (packed colours).</summary>
    /// <param name="areaX">The x of the lower left corner of the area.</param>
    /// <param name="areaY">The y of the lower left corner of the area.</param>
    /// <param name="areaW">The width of the area.</param>
    /// <param name="areaH">The height of the area.</param>
    /// <param name="nearest">Returns the palette index nearest to a red, green and blue value.</param>
    /// <returns>The 8 bit picture, see <see cref="CommandMapBake.Render"/>.</returns>
    public byte[] RenderCommandMap(double areaX, double areaY, double areaW, double areaH, Func<int, int, int, int> nearest)
    {
        var rgbaCache = new Dictionary<int, (byte[]? Rgba, int W, int H)>();
        (byte[]? Rgba, int W, int H) Material(int m)
        {
            if (!rgbaCache.TryGetValue(m, out var r))
            {
                var name = m >= 0 && m < mMaterials.Count ? mMaterials[m] : null;
                var b = name != null ? mAssets.Bitmap(name) : null;
                rgbaCache[m] = r = b != null ? (mAssets.PackedRgba(b, true), b.Width, b.Height) : (null, 0, 0);
            }
            return r;
        }
        return CommandMapBake.Render(mData, OriginX, OriginY, areaX, areaY, areaW, areaH, m => Material(m).Rgba, m => (Material(m).W, Material(m).H), nearest);
    }

    private readonly Dictionary<long, float> mGrow = [];

    /// <summary>Draws the terrain for a camera.</summary>
    /// <param name="cam">The camera.</param>
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
        var tiles = mesh.Tiles.Count > 0 ? CreateTileAtlas(mesh) : null;
        foreach (var mat in mesh.ByMaterial.Keys.Union(mesh.DynByMaterial.Keys).OrderBy(k => k))
        {
            if (mesh.ByMaterial.TryGetValue(mat, out var list) && list.Count > 0)
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(MaterialTexture(mat), mLight));
            if (tiles != null && mesh.DynByMaterial.TryGetValue(mat, out var lit) && lit.Count > 0)
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
        var h = (mesh.Tiles.Count + per - 1) / per * ts;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < mesh.Tiles.Count; ++i)
            for (var j = 0; j < ts; ++j)
                for (var k = 0; k < ts; ++k)
                {
                    int o = ((((i / per * ts) + j) * w) + (i % per * ts) + k) * 4, n = ((j * ts) + k) * 3;
                    rgba[o] = (byte)(mesh.Tiles[i][n] * 17);
                    rgba[o + 1] = (byte)(mesh.Tiles[i][n + 1] * 17);
                    rgba[o + 2] = (byte)(mesh.Tiles[i][n + 2] * 17);
                    rgba[o + 3] = 255;
                }
        void Normalise(List<Vertex3D> l)
        {
            for (var i = 0; i < l.Count; ++i)
            {
                var v = l[i];
                v.Lu /= w;
                v.Lv /= h;
                l[i] = v;
            }
        }
        Normalise(mesh.DynAtlas);
        foreach (var l in mesh.DynByMaterial.Values)
            Normalise(l);
        return mDev.CreateTexture3D(rgba, w, h, new TextureOptions(false, false, true));
    }

    /// <summary>Reads the static light map value under a world point.</summary>
    /// <param name="x">The world x.</param>
    /// <param name="y">The world y.</param>
    /// <returns>The red, green and blue values, 0 to 1 (the nibbles over 15, ShapeBase_lightProbe 0044e6f4).</returns>
    public (float r, float g, float b) LightAt(double x, double y)
    {
        var w = mData.LightWord((int)Math.Floor((x - OriginX) / TerrainData.CellSize), (int)Math.Floor((y - OriginY) / TerrainData.CellSize));
        return (((w >> 8) & 0xf) / 15f, ((w >> 4) & 0xf) / 15f, (w & 0xf) / 15f);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var t in mMaterialTextures.Values)
            t?.Dispose();
        mAtlas?.Dispose();
        mLight?.Dispose();
    }
}
