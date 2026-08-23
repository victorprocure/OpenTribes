using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// world-render.md 2.2: the mission fields as the renderer uses them.
// ScreenError is $screenError (1.11 registers it as a console variable bound to the T2 renderer, default 9).
public readonly record struct TerrainParams(double Visible, double HazeStart, double ScreenSize, double ScreenError = 9.0)
{
    public const double PrefVisibleDistance = 10000; // $pref::TerrainVisibleDistance default (2.2)

    // 2.2: visible = min(visibleDistance, pref); hazeStart = hazeDistance * (hazeDistance / visibleDistance), at most
    // 0.75 * visible. Broadside (600, 400) gives 266.7.
    public static TerrainParams FromMission(double visibleDistance, double hazeDistance, double screenSize)
    {
        double visible = Math.Min(visibleDistance, PrefVisibleDistance);
        double hazeStart = visibleDistance > 0 ? hazeDistance * (hazeDistance / visibleDistance) : 0;
        hazeStart = Math.Min(hazeStart, 0.75 * visible);
        return new TerrainParams(visible, hazeStart, screenSize);
    }
}

// One selected square of the render domain: level k has side 2^k cells; (X, Y) is its corner cell.
// D is the distance to its box, the stock mip rank of the sort needs it.
public readonly record struct TerrainSquare(int Level, int X, int Y, double D = 0);

// world-render.md 2.4 to 2.7: the T2 square selection, the vertex morph and the two haze curves.
public static class TerrainLod
{
    public const int MaxSquares = 16384; // 2.5: further squares are dropped

    // 2.6 vertex haze of the T2 terrain: 0 below hazeStart, else 1 - ((dist - hazeStart) * scale - 1)^2 with
    // scale = 1 / (visible - hazeStart). No floor, no 1.02 factor.
    public static double TerrainHaze(double dist, double hazeStart, double visible)
    {
        if (dist < hazeStart || visible <= hazeStart || dist >= visible)
            return dist < hazeStart ? 0 : 1;
        double scale = 1.0 / (visible - hazeStart);
        double t = (dist - hazeStart) * scale - 1;
        return Math.Clamp(1 - t * t, 0, 1);
    }

    // 2.7 haze of interiors, shapes and effects (the software curve): 0.01 below hazeStart, else
    // 1 - ((dist - hazeStart) * scale - 1)^2 with scale = 1 / (1.02 * (visible - hazeStart)), dist clamped to visible.
    public static double ObjectHaze(double dist, double hazeStart, double visible)
    {
        dist = Math.Min(dist, visible);
        if (dist < hazeStart || visible <= hazeStart)
            return 0.01;
        double scale = 1.0 / (1.02 * (visible - hazeStart));
        double t = (dist - hazeStart) * scale - 1;
        return Math.Clamp(1 - t * t, 0, 1);
    }

    // 2.5: detailDistance = distanceScale * size - size, size = 8 * 2^level, distanceScale = projectionDistance(
    // screenSize / pixelScale, 1). pixelScale is 1 (Q1: the camera slots are not transcribed).
    public static double DetailDistance(in Camera3D cam, double screenSize, int level)
    {
        double size = TerrainData.CellSize * (1 << level);
        return cam.ProjectionDistance(screenSize, 1) * size - size;
    }

    // 2.5 grow factor: 0 below 0.75 * detailDistance, 1 at the detail distance.
    public static double Grow(double dist, double detailDistance)
    {
        if (detailDistance <= 0)
            return 1;
        return Math.Clamp((dist - 0.75 * detailDistance) / (0.25 * detailDistance), 0, 1);
    }

    private static double BoxDistance(double cx, double cy, double cz, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        double dx = Math.Max(Math.Max(x0 - cx, 0), cx - x1);
        double dy = Math.Max(Math.Max(y0 - cy, 0), cy - y1);
        double dz = Math.Max(Math.Max(z0 - cz, 0), cz - z1);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // Six clip planes of the view as inward normals through the camera (near is checked by distance).
    private static (double nx, double ny, double nz)[] SidePlanes(in Camera3D cam)
    {
        double ha = cam.HorizontalFov / 2;
        double va = Math.Atan(Math.Tan(ha) * cam.Height / cam.Width);
        var r = cam.Right;
        var f = cam.Forward;
        var u = cam.Up;
        (double, double, double) Mix((double x, double y, double z) a, double ka, (double x, double y, double z) b, double kb) =>
            (a.x * ka + b.x * kb, a.y * ka + b.y * kb, a.z * ka + b.z * kb);
        return new[]
        {
            Mix(r, Math.Cos(ha), f, Math.Sin(ha)),
            Mix(r, -Math.Cos(ha), f, Math.Sin(ha)),
            Mix(u, Math.Cos(va), f, Math.Sin(va)),
            Mix(u, -Math.Cos(va), f, Math.Sin(va)),
        };
    }

    // 2.5: the squares to draw for this camera. `origin` is the terrain position (terrain space = world - origin).
    // Squares at distance >= visible or empty are dropped; a square outside the view is dropped; a square subdivides
    // when d < 1 or d < detailDistance. The traversal order is level 8 blocks (i, j), children in raster order.
    public static List<TerrainSquare> Select(TerrainData t, in Camera3D cam, double ox, double oy, double oz, TerrainParams p,
                                             Dictionary<long, float>? growOut = null)
    {
        growOut?.Clear();
        var result = new List<TerrainSquare>();
        var planes = SidePlanes(cam);
        double px = cam.X - ox, py = cam.Y - oy, pz = cam.Z - oz;
        // 2.4 step 3: pixelError = 1 / projectRadius(screenError, 1), 0.000001 when screenError < 0.001 [code 0057c08d]
        double pixelError = p.ScreenError < 0.001 ? (double)1.0e-6f : 1.0 / cam.ProjectRadius(1.0, p.ScreenError);
        var stack = new Stack<TerrainSquare>();
        // The stock client walks the blocks with x outer and y inner, and a square's children from (0,1) to (1,1), (1,0), (0,0).
        for (int i = TerrainData.BlockCount - 1; i >= 0; --i)
            for (int j = TerrainData.BlockCount - 1; j >= 0; --j)
                stack.Push(new TerrainSquare(8, i * TerrainData.Cells, j * TerrainData.Cells));
        while (stack.Count > 0 && result.Count < MaxSquares)
        {
            var sq = stack.Pop();
            int s = 1 << sq.Level;
            if (t.IsEmpty(sq.Level, sq.X, sq.Y))
                continue;
            double x0 = sq.X * TerrainData.CellSize, y0 = sq.Y * TerrainData.CellSize;
            double x1 = x0 + s * TerrainData.CellSize, y1 = y0 + s * TerrainData.CellSize;
            double z0 = t.MinHeight(sq.Level, sq.X, sq.Y), z1 = t.MaxHeight(sq.Level, sq.X, sq.Y);
            double d = BoxDistance(px, py, pz, x0, y0, z0, x1, y1, z1);
            if (d >= p.Visible)
                continue;
            // frustum: widen the box in z by d * pixelError both ways (2.5)
            double w = d * pixelError;
            bool outside = false;
            foreach (var pl in planes)
            {
                bool any = false;
                for (int c = 0; c < 8 && !any; ++c)
                {
                    double cx = (c & 1) == 0 ? x0 : x1, cy = (c & 2) == 0 ? y0 : y1, cz = (c & 4) == 0 ? z0 - w : z1 + w;
                    any = (cx - px) * pl.nx + (cy - py) * pl.ny + (cz - pz) * pl.nz >= 0;
                }
                if (!any)
                {
                    outside = true;
                    break;
                }
            }
            if (outside)
                continue;
            if (sq.Level == 0)
            {
                result.Add(sq with { D = d });
                continue;
            }
            bool subdivide = d < 1 || d < DetailDistance(cam, p.ScreenSize, sq.Level);
            if (!subdivide)
            {
                result.Add(sq with { D = d });
                continue;
            }
            int h = s / 2;
            if (growOut != null)
            {
                // 2.5: the points a subdivision creates (centre and the four edge mid points) take the grow factor of the
                // square's own distance; a point that exists already keeps the value of the square that made it.
                float g = (float)Grow(d, DetailDistance(cam, p.ScreenSize, sq.Level));
                foreach (long key in new[] { GrowKey(sq.X + h, sq.Y + h), GrowKey(sq.X + h, sq.Y), GrowKey(sq.X + s, sq.Y + h),
                                             GrowKey(sq.X + h, sq.Y + s), GrowKey(sq.X, sq.Y + h) })
                    growOut.TryAdd(key, g);
            }
            foreach (int k in new[] { 0, 1, 3, 2 })
                stack.Push(new TerrainSquare(sq.Level - 1, sq.X + (k & 1) * h, sq.Y + (k >> 1) * h));
        }
        return result;
    }

    public static long GrowKey(int x, int y) => ((long)x << 20) | (uint)y;

    private static int CreationLevel(int gx, int gy)
    {
        int v = gx | gy;
        return v == 0 ? 99 : System.Numerics.BitOperations.TrailingZeroCount(v);
    }

    // True when the cell (x, y) at `level` splits along the diagonal from its (0,0) to (1,1) corner. Level 0 follows
    // the checkerboard ((x ^ y) & 1) == 0 of the server's height query (terrain.md 4.2); higher levels use the same
    // pattern on the square grid of that level (the deviation rule of 2.3 is not transcribed, Q4 of this stage).
    public static bool SplitMainDiagonal(int level, int x, int y) => (((x >> level) ^ (y >> level)) & 1) == 0;

    // 2.5 vertex morph: the drawn height is h + grow * (average - h), average = mean of the two parent points. Returns the
    // unmorphed and morphed heights of a corner of the render domain for this camera.
    public static float MorphedHeight(TerrainData t, int gx, int gy, in Camera3D cam, double ox, double oy, double oz, TerrainParams p,
                                           Dictionary<long, float>? growMap = null)
    {
        float h = t.Height(gx, gy);
        int pl = CreationLevel(gx, gy);
        if (pl >= 8)
            return h;
        int half = 1 << pl, full = half << 1;
        bool ax = (gx & (full - 1)) == 0, ay = (gy & (full - 1)) == 0;
        float avg;
        if (ax && !ay)
            avg = (t.Height(gx, gy - half) + t.Height(gx, gy + half)) * 0.5f;
        else if (!ax && ay)
            avg = (t.Height(gx - half, gy) + t.Height(gx + half, gy)) * 0.5f;
        else
        {
            int x0 = gx - half, y0 = gy - half;
            avg = t.SplitMain(pl + 1, x0, y0)
                ? (t.Height(x0, y0) + t.Height(x0 + full, y0 + full)) * 0.5f
                : (t.Height(x0 + full, y0) + t.Height(x0, y0 + full)) * 0.5f;
        }
        double grow;
        if (growMap != null && growMap.TryGetValue(GrowKey(gx, gy), out float stored))
            grow = stored;
        else
        {
            double dx = gx * TerrainData.CellSize + ox - cam.X, dy = gy * TerrainData.CellSize + oy - cam.Y, dz = h + oz - cam.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            grow = Grow(dist, DetailDistance(cam, p.ScreenSize, pl + 1));
        }
        return (float)(h + grow * (avg - h));
    }
}

// The vertices of one frame, grouped by texture: level 0 squares by material (the material's own bitmap) and the
// larger squares sharing the atlas of per-cell colours (2.8 "cover" and "composite", merged into one texture).
public sealed class TerrainMesh
{
    public readonly List<Vertex3D> Atlas = new();
    public readonly Dictionary<int, List<Vertex3D>> ByMaterial = new();
    // The squares a dynamic light touches (TerrainLightSet): their own light map, one 9 x 9 tile of nibbles each in `Tiles`;
    // the vertices carry the light coordinate in texels of the tile atlas (TileAtlasWidth wide) until the renderer normalises it.
    public readonly List<Vertex3D> DynAtlas = new();
    public readonly Dictionary<int, List<Vertex3D>> DynByMaterial = new();
    public readonly List<byte[]> Tiles = new();
    public const int TileSize = 9, TilesPerRow = 112, TileAtlasWidth = TileSize * TilesPerRow;
    public int TriangleCount => Atlas.Count / 3 + ByMaterial.Values.Sum(v => v.Count / 3) + DynAtlas.Count / 3 + DynByMaterial.Values.Sum(v => v.Count / 3);

    // Scratch the mesher refills every frame when the same mesh object is passed back in.
    internal readonly HashSet<long> Points = new();
    internal readonly Dictionary<long, float> Heights = new();
    internal readonly List<(int, int)> Ring = new();

    public void Reset()
    {
        Atlas.Clear();
        foreach (List<Vertex3D> l in ByMaterial.Values)
            l.Clear();
        DynAtlas.Clear();
        foreach (List<Vertex3D> l in DynByMaterial.Values)
            l.Clear();
        Tiles.Clear();
        Points.Clear();
        Heights.Clear();
        Ring.Clear();
    }
}

public static class TerrainMesher
{
    // 2.8: the corner coordinates rotated or flipped by the low three bits of the flags byte. The exe builds one table of four
    // (u, v) pairs per flag value when the terrain renderer is created [code 00576784 to 005768d2]: flag 0 is (0,0) (1,0) (1,1)
    // (0,1) for the corners at (0,1) (1,1) (1,0) (0,0); the table of flag f is that one with, in this order, bit 2 swapping
    // entries 0 and 1 and entries 2 and 3, bit 4 swapping entries 0 and 3 and entries 1 and 2, and bit 1 rotating the
    // entries (entry 0 becomes the old entry 3, entry 1 the old 0, entry 2 the old 1, entry 3 the old 2). The draw picks the
    // table by `flags & 7` [code 0057a79c, 0057afc8]. The values below are the same tables in the form of the element bits
    // (4 swap u and v first, then 1 mirrors u, 2 mirrors v). The C4 seam cost search found the same table; the exe
    // construction is checked in WorldRenderTests.FlagOrientationIsTheExeUvTableConstruction.
    public static readonly int[] FlagOrientation = { 2, 7, 3, 6, 0, 5, 1, 4 };

    public static (float u, float v) CornerUv(int cornerX, int cornerY, int flags)
    {
        int e = FlagOrientation[flags & 7];
        float u = cornerX, v = cornerY;
        if ((e & 4) != 0)
            (u, v) = (v, u);
        if ((e & 1) != 0)
            u = 1 - u;
        if ((e & 2) != 0)
            v = 1 - v;
        return (u, v);
    }

    // The stock back face cull of the emitted squares (`0057813c`, run before the sort): a square is dropped when
    // the camera is behind both triangles of its split. A dropped record is replaced by the last one, which reorders the list
    // and so the order the sort leaves equal keys in.
    internal static void StockCull(TerrainData t, List<TerrainSquare> squares, Func<int, int, float> z, double px, double py, double pz)
    {
        const double cs = TerrainData.CellSize;
        int n = squares.Count, i = 0;
        while (i < n)
        {
            TerrainSquare sq = squares[i];
            int s = 1 << sq.Level;
            double size = s * cs;
            double h08 = z(sq.X, sq.Y + s), h0c = z(sq.X + s, sq.Y + s), h10 = z(sq.X, sq.Y), h14 = z(sq.X + s, sq.Y);
            double vx, vy, vz, ax, ay, bx, by;
            if (t.SplitMain(sq.Level, sq.X, sq.Y))
            {
                vx = sq.X * cs; vy = sq.Y * cs; vz = h10;
                ax = h10 - h14; ay = h14 - h0c;
                bx = h08 - h0c; by = h10 - h08;
            }
            else
            {
                vx = (sq.X + s) * cs; vy = sq.Y * cs; vz = h14;
                ax = h08 - h0c; ay = h14 - h0c;
                bx = h10 - h14; by = h10 - h08;
            }
            double dx = px - vx, dy = py - vy, dz = pz - vz;
            if (dx * ax + dy * ay + dz * size >= 0 || dx * bx + dy * by + dz * size >= 0)
            {
                ++i;
                continue;
            }
            squares[i] = squares[--n];
        }
        squares.RemoveRange(n, squares.Count - n);
    }

    // The stock sort of the emitted squares (world-render.md 2.8): the compare function 00578590 -> 0057642c over records of
    // (level, mip rank, position) sorted by the exe's quicksort. Level 0 squares come first by material (the mip field holds
    // it). The others follow by mip rank descending (cover squares, 0xff, first), then level ascending, then position (y
    // major). Cover squares tie among themselves and the exe's quicksort decides their order. The pin and dynamic light bits
    // of the records' flags (which the compare function ranks first) are not modelled.
    private readonly record struct SortKey(int Level, int Mip, int Pos, int Index);

    internal static List<TerrainSquare> StockOrder(TerrainData t, List<TerrainSquare> squares, in Camera3D cam)
    {
        var keys = new SortKey[squares.Count];
        int stale = 0; // the exe keeps the mip of the previous square when the distance is 0
        for (int i = 0; i < keys.Length; ++i)
        {
            TerrainSquare sq = squares[i];
            int pos = (sq.Y << 16) | (sq.X & 0xffff);
            if (sq.Level == 0)
            {
                keys[i] = new SortKey(0, t.Material(sq.X, sq.Y), pos, i);
                continue;
            }
            int mip = stale;
            if (sq.D > 0.001)
            {
                double size = TerrainData.CellSize * (1 << sq.Level);
                int px = (int)cam.ProjectRadius(sq.D + size / 2, size);
                mip = (px == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)px)) - 1;
            }
            mip = Math.Min(mip, 6);
            stale = mip;
            if (mip <= sq.Level + 1)
                mip = 0xff;
            keys[i] = new SortKey(sq.Level, mip & 0xff, pos, i);
        }
        static int Compare(SortKey a, SortKey b)
        {
            if (a.Level == 0 && b.Level != 0)
                return -1;
            if (a.Level != 0 && b.Level == 0)
                return 1;
            if (a.Level == 0)
                return a.Mip - b.Mip;
            if (a.Mip != b.Mip)
                return b.Mip - a.Mip;
            if (a.Mip == 0xff)
                return 0;
            if (a.Level != b.Level)
                return a.Level - b.Level;
            return a.Pos - b.Pos;
        }
        StockSort.Sort(keys, keys.Length, Compare);
        var result = new List<TerrainSquare>(keys.Length);
        foreach (SortKey k in keys)
            result.Add(squares[k.Index]);
        return result;
    }

    public static TerrainMesh Build(TerrainData t, List<TerrainSquare> squares, Camera3D cam, double ox, double oy, double oz, TerrainParams p, TerrainMesh? reuse = null,
                                   Dictionary<long, float>? growMap = null, TerrainLightSet? lights = null)
    {
        TerrainMesh mesh = reuse ?? new TerrainMesh();
        mesh.Reset();
        HashSet<long> points = mesh.Points;
        static long Key(int x, int y) => ((long)x << 20) | (uint)y;
        foreach (var sq in squares)
        {
            int s = 1 << sq.Level;
            points.Add(Key(sq.X, sq.Y));
            points.Add(Key(sq.X + s, sq.Y));
            points.Add(Key(sq.X, sq.Y + s));
            points.Add(Key(sq.X + s, sq.Y + s));
        }
        Dictionary<long, float> heightCache = mesh.Heights;
        float Z(int gx, int gy)
        {
            long k = Key(gx, gy);
            if (!heightCache.TryGetValue(k, out float z))
                heightCache[k] = z = TerrainLod.MorphedHeight(t, gx, gy, cam, ox, oy, oz, p, growMap);
            return z;
        }
        Vertex3D V(int gx, int gy, float z, float u, float v)
        {
            double wx = ox + gx * TerrainData.CellSize, wy = oy + gy * TerrainData.CellSize, wz = oz + z;
            double dx = wx - cam.X, dy = wy - cam.Y, dz = wz - cam.Z;
            float haze = (float)TerrainLod.TerrainHaze(Math.Sqrt(dx * dx + dy * dy + dz * dz), p.HazeStart, p.Visible);
            return new Vertex3D((float)wx, (float)wy, (float)wz, u, v, (gx + 0.5f) / TerrainData.Cells,
                                (gy + 0.5f) / TerrainData.Cells, haze);
        }
        void Border(int ax, int ay, int bx, int by, List<(int, int)> ring)
        {
            int mx = (ax + bx) / 2, my = (ay + by) / 2;
            if (Math.Abs(bx - ax) + Math.Abs(by - ay) > 1 && points.Contains(Key(mx, my)))
            {
                Border(ax, ay, mx, my, ring);
                Border(mx, my, bx, by, ring);
            }
            else
            {
                ring.Add((ax, ay));
            }
        }
        List<(int, int)> ring = mesh.Ring;
        Vertex3D prev = default;
        var culled = new List<TerrainSquare>(squares);
        StockCull(t, culled, Z, cam.X - ox, cam.Y - oy, cam.Z - oz);
        List<TerrainSquare> ordered = StockOrder(t, culled, cam);
        foreach (var sq in ordered)
        {
            int s = 1 << sq.Level, x = sq.X, y = sq.Y;
            // A square a light touches (2.9) has a tile of its own: light coordinates in texels of the tile, fraction of the side times 8 plus a half.
            int tile = -1;
            if (lights != null && lights.Count > 0)
            {
                uint mask = lights.Mask(t, sq.Level, x, y);
                if (mask != 0)
                {
                    var nib = new byte[TerrainMesh.TileSize * TerrainMesh.TileSize * 3];
                    lights.Nodes(t, sq.Level, x, y, mask, nib);
                    tile = mesh.Tiles.Count;
                    mesh.Tiles.Add(nib);
                }
            }
            Vertex3D Lit(Vertex3D v, int gx, int gy)
            {
                if (tile < 0)
                    return v;
                v.Lu = tile % TerrainMesh.TilesPerRow * TerrainMesh.TileSize + 0.5f + (gx - x) * 8f / s;
                v.Lv = tile / TerrainMesh.TilesPerRow * TerrainMesh.TileSize + 0.5f + (gy - y) * 8f / s;
                return v;
            }
            if (sq.Level == 0)
            {
                int flags = t.Flags(x, y);
                int mat = t.Material(x, y);
                Dictionary<int, List<Vertex3D>> target = tile < 0 ? mesh.ByMaterial : mesh.DynByMaterial;
                if (!target.TryGetValue(mat, out List<Vertex3D>? list))
                    target[mat] = list = new List<Vertex3D>();
                Vertex3D C(int cx, int cy)
                {
                    var (u, v) = CornerUv(cx, cy, flags);
                    return Lit(V(x + cx, y + cy, Z(x + cx, y + cy), u, v), x + cx, y + cy);
                }
                Vertex3D a = C(0, 0), b = C(1, 0), c = C(1, 1), d = C(0, 1);
                if (TerrainLod.SplitMainDiagonal(0, x, y))
                {
                    list.Add(a); list.Add(b); list.Add(c);
                    list.Add(a); list.Add(c); list.Add(d);
                }
                else
                {
                    list.Add(a); list.Add(b); list.Add(d);
                    list.Add(b); list.Add(c); list.Add(d);
                }
                continue;
            }
            ring.Clear();
            Border(x, y, x + s, y, ring);
            Border(x + s, y, x + s, y + s, ring);
            Border(x + s, y + s, x, y + s, ring);
            Border(x, y + s, x, y, ring);
            Vertex3D At(int gx, int gy) => Lit(V(gx, gy, Z(gx, gy), gx / (float)TerrainData.Cells, gy / (float)TerrainData.Cells), gx, gy);
            List<Vertex3D> atlas = tile < 0 ? mesh.Atlas : mesh.DynAtlas;
            // 2.8: one polygon of the border points; the fan starts at the corner the split diagonal runs from: the first
            // corner (top left) for the (0,0)-(1,1) diagonal, else the second (top right).
            int start = 0;
            if (!t.SplitMain(sq.Level, x, y))
                for (int i = 0; i < ring.Count; ++i)
                    if (ring[i].Item1 == x + s && ring[i].Item2 == y)
                    {
                        start = i;
                        break;
                    }
            Vertex3D first = default;
            for (int i = 0; i < ring.Count; ++i)
            {
                var (ax, ay) = ring[(start + i) % ring.Count];
                Vertex3D v = At(ax, ay);
                if (i == 0)
                {
                    first = v;
                    continue;
                }
                if (i >= 2)
                {
                    atlas.Add(first);
                    atlas.Add(prev);
                    atlas.Add(v);
                }
                prev = v;
            }
        }
        DropFarTriangles(mesh.Atlas, cam, p.Visible);
        foreach (List<Vertex3D> l in mesh.ByMaterial.Values)
            DropFarTriangles(l, cam, p.Visible);
        DropFarTriangles(mesh.DynAtlas, cam, p.Visible);
        foreach (List<Vertex3D> l in mesh.DynByMaterial.Values)
            DropFarTriangles(l, cam, p.Visible);
        return mesh;
    }

    // 2.6 visible distance cut: a point beyond the visible distance carries the far clip bit, and a triangle whose three
    // points all carry it is dropped.
    private static void DropFarTriangles(List<Vertex3D> tris, in Camera3D cam, double visible)
    {
        double v2 = visible * visible, cx = cam.X, cy = cam.Y, cz = cam.Z;
        bool Far(in Vertex3D v)
        {
            double dx = v.X - cx, dy = v.Y - cy, dz = v.Z - cz;
            return dx * dx + dy * dy + dz * dz > v2;
        }
        int n = 0;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            if (Far(tris[i]) && Far(tris[i + 1]) && Far(tris[i + 2]))
                continue;
            if (n != i)
            {
                tris[n] = tris[i]; tris[n + 1] = tris[i + 1]; tris[n + 2] = tris[i + 2];
            }
            n += 3;
        }
        tris.RemoveRange(n, tris.Count - n);
    }
}
