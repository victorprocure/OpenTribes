using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// world-render.md 2.2: the mission fields as the renderer uses them.
public readonly record struct TerrainParams(double Visible, double HazeStart, double ScreenSize)
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
public readonly record struct TerrainSquare(int Level, int X, int Y);

// world-render.md 2.4 to 2.7: the T2 square selection, the vertex morph and the two haze curves.
public static class TerrainLod
{
    public const int MaxSquares = 16384; // 2.5: further squares are dropped

    // 2.6 vertex haze of the T2 terrain: 0 below hazeStart, else 1 - ((dist - hazeStart) * scale - 1)^2 with
    // scale = 1 / (visible - hazeStart). No floor, no 1.02 factor.
    public static double TerrainHaze(double dist, double hazeStart, double visible)
    {
        if (dist < hazeStart || visible <= hazeStart)
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
    public static List<TerrainSquare> Select(TerrainData t, in Camera3D cam, double ox, double oy, double oz, TerrainParams p)
    {
        var result = new List<TerrainSquare>();
        var planes = SidePlanes(cam);
        double px = cam.X - ox, py = cam.Y - oy, pz = cam.Z - oz;
        // 2.4 step 3: pixelError = 1 / projectRadius(screenError = 9, 1)
        double pixelError = 1.0 / cam.ProjectRadius(1.0, 9.0) ;
        var stack = new Stack<TerrainSquare>();
        for (int j = TerrainData.BlockCount - 1; j >= 0; --j)
            for (int i = TerrainData.BlockCount - 1; i >= 0; --i)
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
                result.Add(sq);
                continue;
            }
            bool subdivide = d < 1 || d < DetailDistance(cam, p.ScreenSize, sq.Level);
            if (!subdivide)
            {
                result.Add(sq);
                continue;
            }
            int h = s / 2;
            for (int k = 3; k >= 0; --k)
                stack.Push(new TerrainSquare(sq.Level - 1, sq.X + (k & 1) * h, sq.Y + (k >> 1) * h));
        }
        return result;
    }

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
    public static float MorphedHeight(TerrainData t, int gx, int gy, in Camera3D cam, double ox, double oy, double oz, TerrainParams p)
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
            avg = SplitMainDiagonal(pl + 1, x0, y0)
                ? (t.Height(x0, y0) + t.Height(x0 + full, y0 + full)) * 0.5f
                : (t.Height(x0 + full, y0) + t.Height(x0, y0 + full)) * 0.5f;
        }
        double dx = gx * TerrainData.CellSize + ox - cam.X, dy = gy * TerrainData.CellSize + oy - cam.Y, dz = h + oz - cam.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double grow = Grow(dist, DetailDistance(cam, p.ScreenSize, pl + 1));
        return (float)(h + grow * (avg - h));
    }
}

// The vertices of one frame, grouped by texture: level 0 squares by material (the material's own bitmap) and the
// larger squares sharing the atlas of per-cell colours (2.8 "cover" and "composite", merged into one texture).
public sealed class TerrainMesh
{
    public readonly List<Vertex3D> Atlas = new();
    public readonly Dictionary<int, List<Vertex3D>> ByMaterial = new();
    public int TriangleCount => Atlas.Count / 3 + ByMaterial.Values.Sum(v => v.Count / 3);

    // Scratch the mesher refills every frame when the same mesh object is passed back in.
    internal readonly HashSet<long> Points = new();
    internal readonly Dictionary<long, float> Heights = new();
    internal readonly List<(int, int)> Ring = new();

    public void Reset()
    {
        Atlas.Clear();
        foreach (List<Vertex3D> l in ByMaterial.Values)
            l.Clear();
        Points.Clear();
        Heights.Clear();
        Ring.Clear();
    }
}

public static class TerrainMesher
{
    // 2.8: the corner coordinates rotated or flipped by the low three bits of the flags byte. The spec does not give the
    // meaning; it was measured (C4): for each of the 8! assignments of flag value to orientation the colour difference across
    // the borders of neighbouring level 0 squares was summed over IceRidge, Broadside and Raindance, and the same table is
    // the minimum on all three (about 20 times lower than any bit-wise reading). Element bits: 4 swap u and v first, then
    // 1 mirrors u, 2 mirrors v.
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

    public static TerrainMesh Build(TerrainData t, List<TerrainSquare> squares, Camera3D cam, double ox, double oy, double oz, TerrainParams p, TerrainMesh? reuse = null)
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
                heightCache[k] = z = TerrainLod.MorphedHeight(t, gx, gy, cam, ox, oy, oz, p);
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
        foreach (var sq in squares)
        {
            int s = 1 << sq.Level, x = sq.X, y = sq.Y;
            if (sq.Level == 0)
            {
                int flags = t.Flags(x, y);
                int mat = t.Material(x, y);
                if (!mesh.ByMaterial.TryGetValue(mat, out List<Vertex3D>? list))
                    mesh.ByMaterial[mat] = list = new List<Vertex3D>();
                Vertex3D C(int cx, int cy)
                {
                    var (u, v) = CornerUv(cx, cy, flags);
                    return V(x + cx, y + cy, Z(x + cx, y + cy), u, v);
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
            Vertex3D At(int gx, int gy) => V(gx, gy, Z(gx, gy), gx / (float)TerrainData.Cells, gy / (float)TerrainData.Cells);
            int cx = x + s / 2, cy = y + s / 2;
            float zc = TerrainLod.MorphedHeight(t, cx, cy, cam, ox, oy, oz, p);
            Vertex3D centre = V(cx, cy, zc, cx / (float)TerrainData.Cells, cy / (float)TerrainData.Cells);
            for (int i = 0; i < ring.Count; ++i)
            {
                var (ax, ay) = ring[i];
                var (bx, by) = ring[(i + 1) % ring.Count];
                mesh.Atlas.Add(centre);
                mesh.Atlas.Add(At(ax, ay));
                mesh.Atlas.Add(At(bx, by));
            }
        }
        return mesh;
    }
}
