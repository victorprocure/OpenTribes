using OpenTribes.Client.World;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

/// <summary>The parameters of one shape draw.</summary>
public sealed class ShapeDraw
{
    /// <summary>The transform of the shape to the world.</summary>
    public Xf ToWorld = Xf.Identity;
    /// <summary>True for the Player armor only: ShapeBase_resetRootTransform clears the instance root transform (3.6 step 4).</summary>
    public bool NoRootTransform;
    /// <summary>The lights.</summary>
    public LightSet Light = LightSet.Default();
    /// <summary>The fade f of 2.2 step 3: 0 fully visible, 1 gone. The alpha is 1 - f.</summary>
    public double Fade;
    /// <summary>The skin name.</summary>
    public string Skin = "";
    /// <summary>The damage level of the skin.</summary>
    public int DamageLevel;
    /// <summary>The scale of the detail choice, the shape detail preference (instance +0x90).</summary>
    public double DetailScale = 1;
    /// <summary>The detail to draw, or -2 to choose by size.</summary>
    public int ForceDetail = -2;
    /// <summary>True to draw in category 1 whatever the materials say.</summary>
    public bool ForceTranslucent;
    /// <summary>True to cull a face that crosses a clip plane by its winding too, set for the explosion shapes (a camera inside a cone sees only back faces).</summary>
    /// <remarks>The device culls the back faces of every polygon (beginScene3d 005daa30: cull on, front face CW), so a face that goes through the clipper 005b1f68 is culled by its winding too.</remarks>
    public bool CullClipped;
    /// <summary>A sort key that replaces the computed one, or null.</summary>
    public double? FixedSortKey;
    /// <summary>The distance where the haze starts.</summary>
    public double HazeStart = 1e9;
    /// <summary>The visible distance.</summary>
    public double Visible = 1e9;
    /// <summary>True to keep the fog amount of the image at 0 (projectile images never set it): no haze.</summary>
    public bool NoHaze;
    /// <summary>A bias added to the sort key (the flame draws just in front of the body).</summary>
    public double SortBias;
    /// <summary>The label of the render image.</summary>
    public string Label = "";
    /// <summary>The objects to draw, or null for all (debris parts).</summary>
    public HashSet<int>? ObjectFilter;
}

/// <summary>The shape image builder: detail choice, frame vertices, face culling, material states (shading, keyed and translucent blends, fade) and vertex lighting.</summary>
/// <remarks>docs/shapes-render.md 1.3 and 2.3 to 2.7. The output is a <see cref="RenderImage"/> of world space triangle batches that <see cref="RenderLists"/> sorts and draws through the 3D device calls of OpenTribes.Platform.</remarks>
public sealed class ShapeRenderer
{
    private sealed class FrameData
    {
        public float[] P = Array.Empty<float>();
        public float[] N = Array.Empty<float>();
    }

    private struct MatState
    {
        public DrawState3D Ds;
        public bool PaletteAlpha;
    }

    // Scratch kept between builds (the renderer is used by one thread): per-material vertex lists, material states of the
    // current build in order of first use, and the world-space vertex, normal and colour arrays of one object.
    private readonly Dictionary<int, MatState> mStates = new();
    private readonly Dictionary<int, List<Vertex3D>> mBatches = new();
    private readonly List<int> mUsed = new();
    private (double x, double y, double z)[] mWp = Array.Empty<(double, double, double)>(), mWn = Array.Empty<(double, double, double)>();
    private (float r, float g, float b)?[] mCol = Array.Empty<(float, float, float)?>();
    private byte[] mOut = Array.Empty<byte>();

    private readonly ShapeAssets mAssets;
    private readonly Dictionary<(RsMesh, int), FrameData> mFrames = new();
    /// <summary>True to flip the winding, set by tests that check the cull rule.</summary>
    public bool FlipWinding;
    /// <summary>The detail of the last build.</summary>
    public int LastDetail = -1;
    /// <summary>The faces of the last build.</summary>
    public int LastFaces;
    /// <summary>The culled faces of the last build.</summary>
    public int LastCulled;
    /// <summary>The names of the files already logged.</summary>
    public readonly HashSet<string> Logged = new();
    /// <summary>The number of draws skipped for a missing texture.</summary>
    public int SkippedMissingTexture;

    /// <summary>Creates the builder.</summary>
    /// <param name="assets">The shape assets.</param>
    public ShapeRenderer(ShapeAssets assets) => mAssets = assets;

    // 1.2: vertex k of a frame is packed[firstVert + k].xyz * scale + origin, in mesh space; the normals are the area
    // weighted average of the adjacent faces of the frame, pointing out of the clockwise faces (the exe normal table of 1.4 is
    // not available to the port; its vectors point out of the faces, as checked on the disc launcher).
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
            float nx = az * by - ay * bz, ny = ax * bz - az * bx, nz = ay * bx - ax * by;   // clockwise faces: outward is the reverse of a x b
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

    /// <summary>Chooses the detail of a shape (2.3): size = cameraScale * projected radius * instance scale, then the detail by size.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="cam">The camera.</param>
    /// <param name="toWorld">The transform to the world.</param>
    /// <param name="scale">The instance scale.</param>
    /// <returns>The detail index.</returns>
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

    /// <summary>Builds the render image of an instance.</summary>
    /// <param name="inst">The shape instance.</param>
    /// <param name="cam">The camera.</param>
    /// <param name="p">The draw parameters.</param>
    /// <returns>The image, or null when nothing draws.</returns>
    public RenderImage? Build(RenderInstance inst, in Camera3D cam, ShapeDraw p)
    {
        RenderShape sh = inst.Shape;
        // Every instance draws with the default transform of node 0 in front of its nodes (shapes-render.md 3.6 step 4).
        Xf toWorld = p.NoRootTransform ? p.ToWorld : sh.RootTransform().Then(p.ToWorld);
        int detail = p.ForceDetail != -2 ? p.ForceDetail : DetailFor(sh, cam, toWorld, p.DetailScale);
        LastDetail = detail;
        if (detail < 0)
            return null;
        inst.Animate(detail);
        bool[] nodeMask = sh.NodesOfDetail(detail).Mask;
        var (ccx, ccy, ccz) = toWorld.Apply(sh.Cx, sh.Cy, sh.Cz);
        LightSet.ShapeFrame lights = p.Light.ForShape(ccx, ccy, ccz, sh.Radius);
        var (_, depth, _) = cam.ToCamera(ccx, ccy, ccz);
        double dist = Math.Sqrt((ccx - cam.X) * (ccx - cam.X) + (ccy - cam.Y) * (ccy - cam.Y) + (ccz - cam.Z) * (ccz - cam.Z));
        // A shape whose bounding sphere (padded for moving parts) is wholly outside the view makes no image: nothing of it can
        // reach the screen. Forced details (debris, tests) always build.
        if (p.ForceDetail == -2 && sh.Radius > 0)
        {
            var (vx, vy, vz) = cam.ToCamera(ccx, ccy, ccz);
            if (!World.InteriorRenderer.SphereVisible(cam, vx, vy, vz, sh.Radius * 2 + 2))
            {
                LastFaces = LastCulled = 0;
                return null;
            }
        }
        float haze = p.NoHaze ? 0 : (float)TerrainLod.ObjectHaze(dist, p.HazeStart, p.Visible);
        var img = new RenderImage { Label = p.Label.Length > 0 ? p.Label : sh.Name };
        Dictionary<int, MatState> states = mStates;
        states.Clear();
        mUsed.Clear();
        bool translucent = p.ForceTranslucent || p.Fade > 0;
        int matBase = sh.MaterialDetails > 1 ? Math.Min(detail, sh.MaterialDetails - 1) * sh.MaterialCount : 0;
        double alphaFade = 1 - p.Fade;
        LastFaces = LastCulled = 0;
        for (int oi = 0; oi < sh.Objects.Length; ++oi)
        {
            RsObject ob = sh.Objects[oi];
            RsObjectState st = inst.Objects[oi];
            if ((p.ObjectFilter != null && !p.ObjectFilter.Contains(oi)) || !(ob.Node >= 0 && ob.Node < nodeMask.Length && nodeMask[ob.Node]) || !st.Visible || ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length)
                continue;
            RsMesh mesh = sh.Meshes[ob.Mesh];
            if (mesh.Faces.Length == 0 || mesh.Frames.Length == 0)
                continue;
            Xf ox = inst.ObjectTransform(oi).Then(toWorld);
            FrameData fd = Frame(mesh, Math.Clamp(st.Frame, 0, mesh.Frames.Length - 1));
            int nv = fd.P.Length / 3;
            if (mWp.Length < nv)
            {
                mWp = new (double x, double y, double z)[nv * 2];
                mWn = new (double x, double y, double z)[nv * 2];
                mCol = new (float r, float g, float b)?[nv * 2];
                mOut = new byte[nv * 2];
            }
            (double x, double y, double z)[] wp = mWp, wn = mWn;
            (float r, float g, float b)?[] col = mCol;
            Array.Clear(col, 0, nv);
            for (int k = 0; k < nv; ++k)
            {
                wp[k] = ox.Apply(fd.P[k * 3], fd.P[k * 3 + 1], fd.P[k * 3 + 2]);
                wn[k] = ox.Rotate(fd.N[k * 3], fd.N[k * 3 + 1], fd.N[k * 3 + 2]);
                mOut[k] = ClipCode(cam, wp[k]);
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
                // 2.4 step 6: the stock faces are clockwise seen from outside and the cull drops the counter-clockwise ones, but
                // only for a face wholly inside every clip plane. One that crosses a plane goes to the clipper, which draws it
                // whatever its winding. A face outside one shared plane is dropped.
                int oc = mOut[f.V0] | mOut[f.V1] | mOut[f.V2];
                if (oc != 0 && (mOut[f.V0] & mOut[f.V1] & mOut[f.V2]) != 0)
                    continue;
                if ((oc == 0 || p.CullClipped) && (FlipWinding ? facing < 0 : facing > 0))
                {
                    ++LastCulled;
                    continue;
                }
                int mi = f.Material >= 0 && f.Material < inst.Remap.Length ? inst.Remap[f.Material] : f.Material;
                mi += matBase;
                if (mi < 0 || mi >= sh.Materials.Length)
                    continue;
                RsMaterial m = sh.Materials[mi];
                if (!states.TryGetValue(mi, out MatState ms))
                {
                    DrawState3D made = default;
                    mUsed.Add(mi);
                    if (!MakeState(sh, m, p, ref made, out bool blended))
                    {
                        ++SkippedMissingTexture;
                        states[mi] = default;
                        continue;
                    }
                    bool pa = m.Type == 3 && mAssets.Texture(m.Name, p.Skin, p.DamageLevel, m.Keyed) is TexEntry te && te.PaletteAlpha;
                    ms = new MatState { Ds = made, PaletteAlpha = pa };
                    states[mi] = ms;
                    if (!mBatches.ContainsKey(mi))
                        mBatches[mi] = new List<Vertex3D>();
                    if (blended)
                        translucent = true;
                }
                DrawState3D ds = ms.Ds;
                if (ds.Texture == null && m.Type == 3)
                    continue;
                float alpha = 1;
                bool group = (m.Flags & 0x2000) != 0;
                bool paletteAlpha = ms.PaletteAlpha;
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
                    faceCol = lights.At((a.x + b.x + c.x) / 3, (a.y + b.y + c.y) / 3, (a.z + b.z + c.z) / 3, l > 0 ? -nx / l : 0, l > 0 ? -ny / l : 0, l > 0 ? -nz / l : 1);
                }
                List<Vertex3D> list = mBatches[mi];
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
                        lc = col[vi] ??= lights.At(wp[vi].x, wp[vi].y, wp[vi].z, wn[vi].x, wn[vi].y, wn[vi].z);
                    list.Add(new Vertex3D((float)wp[vi].x, (float)wp[vi].y, (float)wp[vi].z, u, v, 0, 0, hz, lc.r * fr, lc.g * fg, lc.b * fb, alpha));
                }
            }
        }
        foreach (int mi in mUsed)
        {
            if (!mBatches.TryGetValue(mi, out List<Vertex3D>? list))
                continue;
            if (list.Count > 0)
                img.Batches.Add(new DrawBatch { Vertices = list.ToArray(), State = states[mi].Ds });
            list.Clear();
        }
        img.Category = translucent ? 1 : 0;
        img.SortKey = (p.FixedSortKey ?? (translucent ? RenderLists.SortKey(depth, cam.Near) : 0)) + p.SortBias;
        return img.Batches.Count == 0 ? null : img;
    }

    // The clip bits of the exe's vertex test: 0x01 behind the near plane, 0x02 beyond the far plane, 0x08 and 0x10 left and right
    // of the window, 0x20 and 0x40 above and below it (`005aab18` reads them as `& 0x7f`).
    private static byte ClipCode(in Camera3D cam, (double x, double y, double z) w)
    {
        var (cx, cy, cz) = cam.ToCamera(w.x, w.y, w.z);
        int c = 0;
        if (cy < cam.Near) c |= 1;
        else if (cy > cam.Far) c |= 2;
        if (cy > 1e-9)
        {
            double sx = cx * cam.FocalLength / cy, sy = cz * cam.FocalLength / cy;
            if (sx < -cam.Width / 2.0) c |= 8; else if (sx > cam.Width / 2.0) c |= 16;
            if (sy > cam.Height / 2.0) c |= 32; else if (sy < -cam.Height / 2.0) c |= 64;
        }
        return (byte)c;
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
