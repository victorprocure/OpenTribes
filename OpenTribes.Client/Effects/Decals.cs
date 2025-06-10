// Decal ring (docs/shapes-render.md 4.6): footprints and bullet holes as textured quads on the terrain, drawn with the
// subtractive blend of their bitmaps. A fixed ring ($pref::NumDecals, default 50, 0 to 200) overwrites the oldest.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Effects;

public sealed class DecalRing
{
    // The table at 006173ec: file and half size. Index 5 names the left heavy foot again (no heavyfootr.bmp exists).
    public static readonly (string File, double Half)[] Table =
    {
        ("lightfootl.bmp", 0.15), ("lightfootr.bmp", 0.15), ("medfootl.bmp", 0.20), ("medfootr.bmp", 0.20),
        ("heavyfootl.bmp", 0.32), ("heavyfootl.bmp", 0.32), ("bullethole.bmp", 0.15),
    };

    public struct Entry
    {
        public int Bitmap;
        public double Cx, Cy, Cz;
        public double[] Corners;      // 4 x (x, y, z)
    }

    private readonly Entry[] mRing;
    private int mCursor, mCount;
    public int Capacity => mRing.Length;
    public int Count => mCount;

    public DecalRing(int capacity = 50) => mRing = new Entry[Math.Clamp(capacity, 0, 200)];

    // FUN_00433a00: inserts at the cursor and wraps.
    public void Add(in Entry e)
    {
        if (mRing.Length == 0)
            return;
        mRing[mCursor] = e;
        mCursor = (mCursor + 1) % mRing.Length;
        mCount = Math.Min(mCount + 1, mRing.Length);
    }

    // The quad of a decal: center +- right * half +- forward * half, lifted 0.05 along the surface normal.
    public static Entry Quad(int bitmap, double cx, double cy, double cz, double nx, double ny, double nz, double fx, double fy, double fz)
    {
        double half = Table[bitmap].Half;
        // right = forward x normal, made orthogonal to the normal.
        double d = fx * nx + fy * ny + fz * nz;
        fx -= nx * d; fy -= ny * d; fz -= nz * d;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl < 1e-6)
            (fx, fy, fz, fl) = (0, 1, 0, 1);
        fx /= fl; fy /= fl; fz /= fl;
        double rx = fy * nz - fz * ny, ry = fz * nx - fx * nz, rz = fx * ny - fy * nx;
        cx += nx * 0.05; cy += ny * 0.05; cz += nz * 0.05;
        var c = new double[12];
        double[] sr = { -1, 1, 1, -1 }, sf = { 1, 1, -1, -1 };
        for (int i = 0; i < 4; ++i)
        {
            c[i * 3] = cx + rx * half * sr[i] + fx * half * sf[i];
            c[i * 3 + 1] = cy + ry * half * sr[i] + fy * half * sf[i];
            c[i * 3 + 2] = cz + rz * half * sr[i] + fz * half * sf[i];
        }
        return new Entry { Bitmap = bitmap, Cx = cx, Cy = cy, Cz = cz, Corners = c };
    }

    public void Build(FrameContext ctx)
    {
        var img = new RenderImage { Category = 1, SortKey = 0.0, Label = "decals" };
        for (int i = 0; i < mCount; ++i)
        {
            Entry e = mRing[i];
            TexEntry? te = ctx.Assets.Texture(Table[e.Bitmap].File);
            if (te?.Texture == null)
                continue;
            double dx = e.Cx - ctx.Camera.X, dy = e.Cy - ctx.Camera.Y, dz = e.Cz - ctx.Camera.Z;
            if (dx * dx + dy * dy + dz * dz > ctx.Visible * ctx.Visible)
                continue;
            var v = new Vertex3D[6];
            int[] order = { 0, 1, 2, 0, 2, 3 };
            float[] us = { 0, 1, 1, 0 }, vs = { 0, 0, 1, 1 };
            for (int k = 0; k < 6; ++k)
            {
                int c = order[k];
                v[k] = new Vertex3D((float)e.Corners[c * 3], (float)e.Corners[c * 3 + 1], (float)e.Corners[c * 3 + 2], us[c], vs[c], 0, 0, 0);
            }
            // Subtractive bitmaps (all stock decals) blend with the target times one minus the colour; depth test only.
            img.Batches.Add(new DrawBatch { Vertices = v, State = new DrawState3D(te.Texture, null, true, 0, true, false, te.Blend == BlendMode.Subtractive ? BlendMode.Subtractive : BlendMode.Translucent) });
        }
        ctx.Lists.Add(img);
    }
}
