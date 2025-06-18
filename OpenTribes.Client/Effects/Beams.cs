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

    private static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
}
