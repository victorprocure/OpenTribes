// Shape image builder (docs/shapes-render.md 1.3, 2.3 to 2.7): detail choice, frame vertices, face culling, material
// states (shading, keyed and translucent blends, fade), vertex lighting. The output is a RenderImage of world-space
// triangle batches that RenderLists sorts and draws through the 3D device calls of OpenTribes.Platform.
using OpenTribes.Client.World;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

public sealed class ShapeDraw
{
    public Xf ToWorld = Xf.Identity;
    public LightSet Light = LightSet.Default();
    public double Fade;                // f of 2.2 step 3: 0 fully visible, 1 gone; alpha = 1 - f
    public string Skin = "";
    public int DamageLevel;
    public double DetailScale = 1;     // instance +0x90: the shape detail preference
    public int ForceDetail = -2;       // -2 chooses by size
    public bool ForceTranslucent;      // category 1 whatever the materials say
    public double? FixedSortKey;
    public double HazeStart = 1e9, Visible = 1e9;
    public double SortBias;            // added to the key (the flame draws just in front of the body)
    public string Label = "";
    public HashSet<int>? ObjectFilter;  // draw only these objects (debris parts)
}

public sealed class ShapeRenderer
{
    private sealed class FrameData
    {
        public float[] P = Array.Empty<float>();
        public float[] N = Array.Empty<float>();
    }

    private readonly ShapeAssets mAssets;
    private readonly Dictionary<(RsMesh, int), FrameData> mFrames = new();
    public bool FlipWinding;           // set by tests that check the cull rule
    public int LastDetail = -1, LastFaces, LastCulled;
    public readonly HashSet<string> Logged = new();
    public int SkippedMissingTexture;

    public ShapeRenderer(ShapeAssets assets) => mAssets = assets;

    // 1.2: vertex k of a frame is packed[firstVert + k].xyz * scale + origin, in mesh space; the normals are the area
    // weighted average of the adjacent faces of the frame (the exe normal table of 1.4 is not available to the port).
    private FrameData Frame(RsMesh mesh, int frame)
    {
        if (mFrames.TryGetValue((mesh, frame), out FrameData? fd))
            return fd;
        RsFrame f = mesh.Frames[frame];
        int count = mesh.VertsPerFrame > 0 ? mesh.VertsPerFrame : (frame + 1 < mesh.Frames.Length ? mesh.Frames[frame + 1].FirstVert - f.FirstVert : mesh.Verts.Length / 4 - f.FirstVert);
        count = Math.Clamp(count, 0, Math.Max(0, mesh.Verts.Length / 4 - f.FirstVert));
        fd = new FrameData { P = new float[count * 3], N = new float[count * 3] };
        for (int k = 0; k < count; ++k)
        {
            int o = (f.FirstVert + k) * 4;
            fd.P[k * 3] = mesh.Verts[o] * f.Sx + f.Ox;
            fd.P[k * 3 + 1] = mesh.Verts[o + 1] * f.Sy + f.Oy;
            fd.P[k * 3 + 2] = mesh.Verts[o + 2] * f.Sz + f.Oz;
        }
        foreach (RsFace fc in mesh.Faces)
        {
            if (fc.V0 < 0 || fc.V1 < 0 || fc.V2 < 0 || fc.V0 >= count || fc.V1 >= count || fc.V2 >= count)
                continue;
            float ax = fd.P[fc.V1 * 3] - fd.P[fc.V0 * 3], ay = fd.P[fc.V1 * 3 + 1] - fd.P[fc.V0 * 3 + 1], az = fd.P[fc.V1 * 3 + 2] - fd.P[fc.V0 * 3 + 2];
            float bx = fd.P[fc.V2 * 3] - fd.P[fc.V0 * 3], by = fd.P[fc.V2 * 3 + 1] - fd.P[fc.V0 * 3 + 1], bz = fd.P[fc.V2 * 3 + 2] - fd.P[fc.V0 * 3 + 2];
            float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            foreach (int v in new[] { fc.V0, fc.V1, fc.V2 })
            {
                fd.N[v * 3] += nx; fd.N[v * 3 + 1] += ny; fd.N[v * 3 + 2] += nz;
            }
        }
        for (int k = 0; k < count; ++k)
        {
            double l = Math.Sqrt(fd.N[k * 3] * fd.N[k * 3] + fd.N[k * 3 + 1] * fd.N[k * 3 + 1] + fd.N[k * 3 + 2] * fd.N[k * 3 + 2]);
            if (l > 1e-12)
            {
                fd.N[k * 3] = (float)(fd.N[k * 3] / l); fd.N[k * 3 + 1] = (float)(fd.N[k * 3 + 1] / l); fd.N[k * 3 + 2] = (float)(fd.N[k * 3 + 2] / l);
            }
            else
                fd.N[k * 3 + 2] = 1;
        }
        mFrames[(mesh, frame)] = fd;
        return fd;
    }

    // 2.3: size = cameraScale * projected radius * instance scale, then the detail by size.
    public int DetailFor(RenderShape shape, in Camera3D cam, in Xf toWorld, double scale)
    {
        var (cx, cy, cz) = toWorld.Apply(shape.Cx, shape.Cy, shape.Cz);
        var (_, depth, _) = cam.ToCamera(cx, cy, cz);
        double dx = cx - cam.X, dy = cy - cam.Y, dz = cz - cam.Z;
        double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-3);
        double size = cam.ProjectRadius(dist, shape.Radius) * scale;
        _ = depth;
        return shape.ChooseDetail(size);
    }

    public RenderImage? Build(RenderInstance inst, in Camera3D cam, ShapeDraw p)
    {
        RenderShape sh = inst.Shape;
        int detail = p.ForceDetail != -2 ? p.ForceDetail : DetailFor(sh, cam, p.ToWorld, p.DetailScale);
        LastDetail = detail;
        if (detail < 0)
            return null;
        inst.Animate(detail);
        List<int> nodes = sh.Subtree(sh.DetailRoot(detail));
        if (sh.AlwaysNode >= 0)
            nodes.AddRange(sh.Subtree(sh.AlwaysNode));
        var nodeSet = new HashSet<int>(nodes);
        var (ccx, ccy, ccz) = p.ToWorld.Apply(sh.Cx, sh.Cy, sh.Cz);
        var (_, depth, _) = cam.ToCamera(ccx, ccy, ccz);
        double dist = Math.Sqrt((ccx - cam.X) * (ccx - cam.X) + (ccy - cam.Y) * (ccy - cam.Y) + (ccz - cam.Z) * (ccz - cam.Z));
        float haze = (float)TerrainLod.ObjectHaze(dist, p.HazeStart, p.Visible);
        var img = new RenderImage { Label = p.Label.Length > 0 ? p.Label : sh.Name };
        var batches = new Dictionary<int, List<Vertex3D>>();
        var states = new Dictionary<int, DrawState3D>();
        bool translucent = p.ForceTranslucent || p.Fade > 0;
        int matBase = sh.MaterialDetails > 1 ? Math.Min(detail, sh.MaterialDetails - 1) * sh.MaterialCount : 0;
        double alphaFade = 1 - p.Fade;
        LastFaces = LastCulled = 0;
        for (int oi = 0; oi < sh.Objects.Length; ++oi)
        {
            RsObject ob = sh.Objects[oi];
            RsObjectState st = inst.Objects[oi];
            if ((p.ObjectFilter != null && !p.ObjectFilter.Contains(oi)) || !nodeSet.Contains(ob.Node) || !st.Visible || ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length)
                continue;
            RsMesh mesh = sh.Meshes[ob.Mesh];
            if (mesh.Faces.Length == 0 || mesh.Frames.Length == 0)
                continue;
            Xf ox = inst.ObjectTransform(oi).Then(p.ToWorld);
            FrameData fd = Frame(mesh, Math.Clamp(st.Frame, 0, mesh.Frames.Length - 1));
            int nv = fd.P.Length / 3;
            var wp = new (double x, double y, double z)[nv];
            var wn = new (double x, double y, double z)[nv];
            var col = new (float r, float g, float b)?[nv];
            for (int k = 0; k < nv; ++k)
            {
                wp[k] = ox.Apply(fd.P[k * 3], fd.P[k * 3 + 1], fd.P[k * 3 + 2]);
                wn[k] = ox.Rotate(fd.N[k * 3], fd.N[k * 3 + 1], fd.N[k * 3 + 2]);
            }
            int tvOffset = mesh.TexVertsPerFrame > 0 && mesh.TexVerts.Length / 2 >= (st.TextureFrame + 1) * mesh.TexVertsPerFrame ? st.TextureFrame * mesh.TexVertsPerFrame : 0;
            foreach (RsFace f in mesh.Faces)
            {
                ++LastFaces;
                if (f.V0 < 0 || f.V1 < 0 || f.V2 < 0 || f.V0 >= nv || f.V1 >= nv || f.V2 >= nv)
                    continue;
                var a = wp[f.V0]; var b = wp[f.V1]; var c = wp[f.V2];
                double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double facing = nx * (cam.X - a.x) + ny * (cam.Y - a.y) + nz * (cam.Z - a.z);
                if (FlipWinding ? facing > 0 : facing < 0)
                {
                    ++LastCulled;
                    continue;
                }
                int mi = f.Material >= 0 && f.Material < inst.Remap.Length ? inst.Remap[f.Material] : f.Material;
                mi += matBase;
                if (mi < 0 || mi >= sh.Materials.Length)
                    continue;
                RsMaterial m = sh.Materials[mi];
                if (!states.TryGetValue(mi, out DrawState3D ds))
                {
                    if (!MakeState(sh, m, p, ref ds, out bool blended))
                    {
                        ++SkippedMissingTexture;
                        states[mi] = default;
                        batches[mi] = new List<Vertex3D>();
                        continue;
                    }
                    states[mi] = ds;
                    batches[mi] = new List<Vertex3D>();
                    if (blended)
                        translucent = true;
                }
                if (ds.Texture == null && m.Type == 3)
                    continue;
                float alpha = 1;
                bool group = (m.Flags & 0x2000) != 0;
                bool paletteAlpha = false;
                if (m.Type == 3 && mAssets.Texture(m.Name, p.Skin, p.DamageLevel, m.Keyed) is TexEntry te)
                    paletteAlpha = te.PaletteAlpha;
                if (group && !paletteAlpha && ds.BlendKind == BlendMode.Translucent)
                    alpha = 0.5f;
                if (p.Fade > 0)
                    alpha *= (float)alphaFade;
                float fr = 1, fg = 1, fb = 1;
                if (m.Type != 3)
                {
                    if (m.Type == 1)
                    {
                        mAssets.World.Palette.SubPalettes[0].Rgb(Math.Clamp(m.PaletteIndex, 0, 255), out byte pr, out byte pg, out byte pb);
                        (fr, fg, fb) = (pr / 255f, pg / 255f, pb / 255f);
                    }
                    else
                        (fr, fg, fb) = (m.R / 255f, m.G / 255f, m.B / 255f);
                }
                int shading = m.Shading;
                float hz = (ds.BlendKind == BlendMode.Additive || ds.BlendKind == BlendMode.Subtractive) && ds.Blend ? 0 : haze;
                (float r, float g, float b)? faceCol = null;
                if (!group && shading == 2)
                {
                    double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    faceCol = p.Light.At((a.x + b.x + c.x) / 3, (a.y + b.y + c.y) / 3, (a.z + b.z + c.z) / 3, l > 0 ? nx / l : 0, l > 0 ? ny / l : 0, l > 0 ? nz / l : 1);
                }
                List<Vertex3D> list = batches[mi];
                for (int k = 0; k < 3; ++k)
                {
                    int vi = k == 0 ? f.V0 : k == 1 ? f.V1 : f.V2;
                    int ti = (k == 0 ? f.T0 : k == 1 ? f.T1 : f.T2) + tvOffset;
                    float u = 0, v = 0;
                    if (ti >= 0 && ti * 2 + 1 < mesh.TexVerts.Length)
                    {
                        u = mesh.TexVerts[ti * 2];
                        v = mesh.TexVerts[ti * 2 + 1];
                    }
                    (float r, float g, float b) lc;
                    if (group || shading == 1 || shading == 0)
                        lc = (1, 1, 1);
                    else if (shading == 2)
                        lc = faceCol!.Value;
                    else
                        lc = col[vi] ??= p.Light.At(wp[vi].x, wp[vi].y, wp[vi].z, wn[vi].x, wn[vi].y, wn[vi].z);
                    list.Add(new Vertex3D((float)wp[vi].x, (float)wp[vi].y, (float)wp[vi].z, u, v, 0, 0, hz, lc.r * fr, lc.g * fg, lc.b * fb, alpha));
                }
            }
        }
        foreach (var kv in batches)
            if (kv.Value.Count > 0)
                img.Batches.Add(new DrawBatch { Vertices = kv.Value.ToArray(), State = states[kv.Key] });
        img.Category = translucent ? 1 : 0;
        img.SortKey = (p.FixedSortKey ?? (translucent ? RenderLists.SortKey(depth, cam.Near) : 0)) + p.SortBias;
        return img.Batches.Count == 0 ? null : img;
    }

    // 1.3.2: the draw state of a material. False when the material needs a bitmap that is missing.
    private bool MakeState(RenderShape sh, in RsMaterial m, ShapeDraw p, ref DrawState3D ds, out bool blended)
    {
        blended = false;
        IGfxTexture? tex = null;
        BlendMode kind = BlendMode.Translucent;
        bool group = (m.Flags & 0x2000) != 0;
        if (m.Type == 3)
        {
            TexEntry? te = mAssets.Texture(m.Name, p.Skin, p.DamageLevel, m.Keyed);
            if (te?.Texture == null)
            {
                if (Logged.Add(sh.Name + ":" + m.Name))
                    Console.Error.WriteLine($"shape {sh.Name}: missing texture {m.Name}");
                return false;
            }
            tex = te.Texture;
            if (te.Blend == BlendMode.Additive || te.Blend == BlendMode.Subtractive)
                kind = te.Blend;
        }
        bool fade = p.Fade > 0;
        if (group)
        {
            // Translucent group: depth test only, blend by the bitmap (additive, subtractive, else texture alpha).
            blended = true;
            ds = new DrawState3D(tex, null, true, 0, true, false, kind);
        }
        else if (fade)
        {
            blended = true;
            ds = new DrawState3D(tex, null, true, 0, true, true, BlendMode.Translucent);
        }
        else
            ds = new DrawState3D(tex, null, false, m.Keyed ? 0.5f : 0f, true, true, BlendMode.Translucent);
        return true;
    }
}
