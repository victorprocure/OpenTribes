// Beams and bolts (docs/shapes-render.md 4.7): camera facing quad strips with the beam bitmap, blend by the bitmap
// attributes (add, subtract or texture alpha), fullbright, depth test without depth write. The bolt jitter is random on
// the stock client too, so only the look matches (spec Q12).
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Effects;

public static class Beams
{
    // A polyline of 2^n + 1 points from `a` to `b`; each midpoint is displaced perpendicular to the beam by up to
    // `bias` times the length of the segment it splits (Lightning `segmentDivisions` and `displaceBias`).
    public static double[][] BoltPoints(double[] a, double[] b, int divisions, double bias, Random rng)
    {
        divisions = Math.Clamp(divisions, 0, 8);
        int n = (1 << divisions) + 1;
        var pts = new double[n][];
        pts[0] = (double[])a.Clone();
        pts[n - 1] = (double[])b.Clone();
        void Split(int lo, int hi)
        {
            if (hi - lo < 2)
                return;
            int mid = (lo + hi) / 2;
            double[] p = new double[3];
            double len = 0;
            for (int k = 0; k < 3; ++k)
            {
                p[k] = (pts[lo][k] + pts[hi][k]) / 2;
                len += (pts[hi][k] - pts[lo][k]) * (pts[hi][k] - pts[lo][k]);
            }
            len = Math.Sqrt(len);
            // a random direction in the plane perpendicular to the segment
            double dx = pts[hi][0] - pts[lo][0], dy = pts[hi][1] - pts[lo][1], dz = pts[hi][2] - pts[lo][2];
            double rx = rng.NextDouble() * 2 - 1, ry = rng.NextDouble() * 2 - 1, rz = rng.NextDouble() * 2 - 1;
            double dl2 = dx * dx + dy * dy + dz * dz;
            if (dl2 > 1e-12)
            {
                double dot = (rx * dx + ry * dy + rz * dz) / dl2;
                rx -= dx * dot; ry -= dy * dot; rz -= dz * dot;
            }
            double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rl > 1e-9)
            {
                double amp = Math.Min(bias * len, 1.2) * rng.NextDouble();   // capped: a bolt of 100 m must not sway by 15 m
                p[0] += rx / rl * amp; p[1] += ry / rl * amp; p[2] += rz / rl * amp;
            }
            pts[mid] = p;
            Split(lo, mid);
            Split(mid, hi);
        }
        Split(0, n - 1);
        return pts;
    }

    public static double[][] Lerp(double[][] a, double[][] b, double t)
    {
        int n = Math.Min(a.Length, b.Length);
        var r = new double[n][];
        for (int i = 0; i < n; ++i)
            r[i] = new[] { a[i][0] + (b[i][0] - a[i][0]) * t, a[i][1] + (b[i][1] - a[i][1]) * t, a[i][2] + (b[i][2] - a[i][2]) * t };
        return r;
    }

    // A strip of quads along the polyline, `width` wide, facing the camera; u runs along the beam `repeat` times.
    public static RenderImage Strip(FrameContext ctx, TexEntry? tex, IReadOnlyList<double[]> pts, double width, float r, float g, float b, float a, double repeat = 1,
                                    string label = "beam", BlendMode? forceBlend = null)
    {
        var img = new RenderImage { Category = 1, SortKey = 0, Label = label };
        if (pts.Count < 2)
            return img;
        double total = 0;
        for (int i = 1; i < pts.Count; ++i)
            total += Dist(pts[i - 1], pts[i]);
        if (total < 1e-9)
            return img;
        var v = new List<Vertex3D>();
        double acc = 0;
        double cx = ctx.Camera.X, cy = ctx.Camera.Y, cz = ctx.Camera.Z;
        for (int i = 1; i < pts.Count; ++i)
        {
            double[] p0 = pts[i - 1], p1 = pts[i];
            double dx = p1[0] - p0[0], dy = p1[1] - p0[1], dz = p1[2] - p0[2];
            double seg = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (seg < 1e-9)
                continue;
            double mx = (p0[0] + p1[0]) / 2 - cx, my = (p0[1] + p1[1]) / 2 - cy, mz = (p0[2] + p1[2]) / 2 - cz;
            // perpendicular = beam direction x view direction
            double px = dy * mz - dz * my, py = dz * mx - dx * mz, pz = dx * my - dy * mx;
            double pl = Math.Sqrt(px * px + py * py + pz * pz);
            if (pl < 1e-9)
                continue;
            double h = width / 2 / pl;
            px *= h; py *= h; pz *= h;
            float u0 = (float)(acc / total * repeat), u1 = (float)((acc + seg) / total * repeat);
            acc += seg;
            Vertex3D V(double[] p, double sx, float u, float vv) => new((float)(p[0] + px * sx), (float)(p[1] + py * sx), (float)(p[2] + pz * sx), u, vv, 0, 0, 0, r, g, b, a);
            v.Add(V(p0, -1, u0, 0)); v.Add(V(p1, -1, u1, 0)); v.Add(V(p1, 1, u1, 1));
            v.Add(V(p0, -1, u0, 0)); v.Add(V(p1, 1, u1, 1)); v.Add(V(p0, 1, u0, 1));
        }
        BlendMode kind = forceBlend ?? (tex != null && (tex.Blend == BlendMode.Additive || tex.Blend == BlendMode.Subtractive) ? tex.Blend : BlendMode.Additive);
        img.Batches.Add(new DrawBatch { Vertices = v.ToArray(), State = new DrawState3D(tex?.Texture, null, true, 0, true, false, kind) });
        return img;
    }

    // The laser beam of LaserProjectile_draw 00463fd0 / image setup 00463810: one quad from the start to the end point, facing the camera.
    // side = unit((camera - start) x unit(end - start)); the half width is 0.04 (DAT_0061bff0) times 1 / projectRadius(point, 0.04) where
    // that radius is under one pixel, else times 1, taken at each end, so the beam is at least one pixel wide at both ends. The texture
    // coordinate u is one value for the whole beam, `u`, and v runs 0 to 1 across it (the bitmap column picked by the beam's age).
    public const double LaserHalfWidth = 0.04;

    public static RenderImage LaserQuad(FrameContext ctx, TexEntry? tex, double[] a, double[] b, double u)
    {
        var img = new RenderImage { Category = 1, SortKey = 0, Label = "laser" };
        double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2], len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-9)
            return img;
        dx /= len; dy /= len; dz /= len;
        double cx = ctx.Camera.X - a[0], cy = ctx.Camera.Y - a[1], cz = ctx.Camera.Z - a[2];
        double sx = cy * dz - cz * dy, sy = cz * dx - cx * dz, sz = cx * dy - cy * dx, sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (sl < 1e-9)
            return img;
        sx /= sl; sy /= sl; sz /= sl;
        double Half(double[] p)
        {
            double depth = ctx.Camera.ToCamera(p[0], p[1], p[2]).y;
            double pr = depth > 1e-6 ? ctx.Camera.ProjectRadius(depth, LaserHalfWidth) : 1.0;
            return LaserHalfWidth * (pr < 1.0 ? 1.0 / pr : 1.0);
        }
        double ha = Half(a), hb = Half(b);
        Vertex3D V(double[] p, double h, double sgn, float vv) =>
            new((float)(p[0] + sx * h * sgn), (float)(p[1] + sy * h * sgn), (float)(p[2] + sz * h * sgn), (float)u, vv, 0, 0, 0, 1, 1, 1, 1);
        Vertex3D v0 = V(a, ha, -1, 0), v1 = V(a, ha, 1, 1), v2 = V(b, hb, 1, 1), v3 = V(b, hb, -1, 0);
        BlendMode kind = tex != null && (tex.Blend == BlendMode.Additive || tex.Blend == BlendMode.Subtractive) ? tex.Blend : BlendMode.Additive;
        img.Batches.Add(new DrawBatch { Vertices = new[] { v0, v1, v2, v0, v2, v3 }, State = new DrawState3D(tex?.Texture, null, true, 0, true, false, kind) });
        return img;
    }

    private static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
}
