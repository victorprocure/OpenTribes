namespace OpenTribes.Client.Effects;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

/// <summary>The decal ring: footprints and bullet holes as textured quads on the terrain, drawn with the subtractive blend of their bitmaps.</summary>
/// <remarks>docs/shapes-render.md 4.6. A fixed ring (<c>$pref::NumDecals</c>, default 50, 0 to 200) overwrites the oldest.</remarks>
public sealed class DecalRing
{
    /// <summary>The decal bitmaps and their half sizes (table at 006173ec). Index 5 names the left heavy foot again, because no heavyfootr.bmp exists.</summary>
    public static readonly (string File, double Half)[] Table =
    [
        ("lightfootl.bmp", 0.15), ("lightfootr.bmp", 0.15), ("medfootl.bmp", 0.20), ("medfootr.bmp", 0.20),
        ("heavyfootl.bmp", 0.32), ("heavyfootl.bmp", 0.32), ("bullethole.bmp", 0.15),
    ];

    /// <summary>One decal.</summary>
    public struct Entry
    {
        /// <summary>The index in <see cref="Table"/>.</summary>
        public int Bitmap { get; set; }
        /// <summary>The centre x.</summary>
        public double Cx { get; set; }
        /// <summary>The centre y.</summary>
        public double Cy { get; set; }
        /// <summary>The centre z.</summary>
        public double Cz { get; set; }
        /// <summary>The four corners as x, y, z each.</summary>
        public double[] Corners { get; set; }
    }

    private readonly Entry[] _ring;
    private int _cursor;
    /// <summary>The size of the ring.</summary>
    public int Capacity => _ring.Length;
    /// <summary>The number of decals in the ring.</summary>
    public int Count { get; private set; }

    /// <summary>Creates a ring.</summary>
    /// <param name="capacity">The size of the ring, clamped to 0 to 200.</param>
    public DecalRing(int capacity = 50) => _ring = new Entry[Math.Clamp(capacity, 0, 200)];

    /// <summary>Inserts a decal at the cursor and wraps (FUN_00433a00).</summary>
    /// <param name="e">The decal.</param>
    public void Add(in Entry e)
    {
        if (_ring.Length == 0)
            return;
        _ring[_cursor] = e;
        _cursor = (_cursor + 1) % _ring.Length;
        Count = Math.Min(Count + 1, _ring.Length);
    }

    /// <summary>Builds the quad of a decal: centre plus or minus right times half plus or minus forward times half, lifted 0.05 along the surface normal.</summary>
    /// <param name="bitmap">The index in <see cref="Table"/>.</param>
    /// <param name="cx">The centre x.</param>
    /// <param name="cy">The centre y.</param>
    /// <param name="cz">The centre z.</param>
    /// <param name="nx">The surface normal x.</param>
    /// <param name="ny">The surface normal y.</param>
    /// <param name="nz">The surface normal z.</param>
    /// <param name="fx">The forward x.</param>
    /// <param name="fy">The forward y.</param>
    /// <param name="fz">The forward z.</param>
    /// <returns>The decal.</returns>
    public static Entry Quad(int bitmap, double cx, double cy, double cz, double nx, double ny, double nz, double fx, double fy, double fz)
    {
        var half = Table[bitmap].Half;
        // right = forward x normal, made orthogonal to the normal.
        var d = (fx * nx) + (fy * ny) + (fz * nz);
        fx -= nx * d;
        fy -= ny * d;
        fz -= nz * d;
        var fl = Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
        if (fl < 1e-6)
            (fx, fy, fz, fl) = (0, 1, 0, 1);
        fx /= fl;
        fy /= fl;
        fz /= fl;
        double rx = (fy * nz) - (fz * ny), ry = (fz * nx) - (fx * nz), rz = (fx * ny) - (fy * nx);
        cx += nx * 0.05;
        cy += ny * 0.05;
        cz += nz * 0.05;
        var c = new double[12];
        double[] sr = [-1, 1, 1, -1], sf = [1, 1, -1, -1];
        for (var i = 0; i < 4; ++i)
        {
            c[i * 3] = cx + (rx * half * sr[i]) + (fx * half * sf[i]);
            c[(i * 3) + 1] = cy + (ry * half * sr[i]) + (fy * half * sf[i]);
            c[(i * 3) + 2] = cz + (rz * half * sr[i]) + (fz * half * sf[i]);
        }
        return new Entry { Bitmap = bitmap, Cx = cx, Cy = cy, Cz = cz, Corners = c };
    }

    /// <summary>Adds the render image of all decals to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    public void Build(FrameContext ctx)
    {
        var img = new RenderImage { Category = 1, SortKey = 0.0, Label = "decals" };
        for (var i = 0; i < Count; ++i)
        {
            var e = _ring[i];
            var te = ctx.Assets.Texture(Table[e.Bitmap].File);
            if (te?.Texture == null)
                continue;
            double dx = e.Cx - ctx.Camera.X, dy = e.Cy - ctx.Camera.Y, dz = e.Cz - ctx.Camera.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > ctx.Visible * ctx.Visible)
                continue;
            var v = new Vertex3D[6];
            int[] order = [0, 1, 2, 0, 2, 3];
            float[] us = [0, 1, 1, 0], vs = [0, 0, 1, 1];
            for (var k = 0; k < 6; ++k)
            {
                var c = order[k];
                v[k] = new Vertex3D((float)e.Corners[c * 3], (float)e.Corners[(c * 3) + 1], (float)e.Corners[(c * 3) + 2], us[c], vs[c], 0, 0, 0);
            }
            // Subtractive bitmaps (all stock decals) blend with the target times one minus the colour; depth test only.
            img.Batches.Add(new DrawBatch { Vertices = v, State = new DrawState3D(te.Texture, null, true, 0, true, false, te.Blend == BlendMode.Subtractive ? BlendMode.Subtractive : BlendMode.Translucent) });
        }
        ctx.Lists.Add(img);
    }
}
