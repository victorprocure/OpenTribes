namespace OpenTribes.Client.Effects;

using OpenTribes.Client.Shapes;
using OpenTribes.World;

/// <summary>The box of the part of one object, in the object's own space, as the part keeps it after 005c3b18.</summary>
/// <param name="MinX">The frame box minimum x before the move (005ad14c).</param>
/// <param name="MinY">The frame box minimum y.</param>
/// <param name="MinZ">The frame box minimum z.</param>
/// <param name="MaxX">The frame box maximum x.</param>
/// <param name="MaxY">The frame box maximum y.</param>
/// <param name="MaxZ">The frame box maximum z.</param>
/// <param name="Cx">The box centre x: the part origin moves there.</param>
/// <param name="Cy">The box centre y.</param>
/// <param name="Cz">The box centre z.</param>
/// <param name="Radius">The collision radius from loadShape (0047603c) of the moved box.</param>
/// <param name="SphereX">The sphere centre x of the moved box (+0x30c).</param>
/// <param name="SphereY">The sphere centre y.</param>
/// <param name="SphereZ">The sphere centre z.</param>
public readonly record struct PartBox(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ, float Cx, float Cy, float Cz, float Radius, float SphereX, float SphereY, float SphereZ);

/// <summary>The collision radius of a debris piece, from the box of its part.</summary>
/// <remarks>docs/shapes-render.md 4.4. Follows loadShape (0047603c, the bounds of a piece) and the part box (005c3b18 and 005ad14c: the object's frame box, moved so that it is centred on the part origin). ShapeBase_explode (0044cdec) makes parts of one object each (005c463c, 005c3b18 counts it), so the piece end 004762e0 always takes the plain Debris_explode for stock shapes. The re-split path (004764f8, 00475edc, 005c4060) is never reached and is not ported.</remarks>
public static class PartDebris
{
    /// <summary>Measures the part box of one frame of a mesh.</summary>
    /// <param name="mesh">The mesh of the object.</param>
    /// <param name="frame">The frame index, clamped to the mesh as the renderer does.</param>
    /// <returns>The box, or the default value when the mesh has no frame box (the stock mesh always has one).</returns>
    /// <remarks>Follows 005ad14c (TSCelAnimMesh_getFrameBox: min is the decoded vertex FirstVert of the frame, max is FirstVert + 1, byte * frame scale + frame origin), 005c3b18 (centre c = (min + max) * 0.5, the part bounds are min - c and max - c) and 0047603c (size = max - min of those bounds, radius +0x318 = m_Point3F_lenf(size) * 0.5, sphere centre +0x30c = (min + max) * 0.5 of the bounds). m_Point3F_lenf 005ed031 is a bit trick square root (bits >> 1) + 0x1fc00000, not an exact root, so a piece is up to 6% wider than its half diagonal. The frame is the current frame of the object (+4 of the object instance, copied by 005c463c).</remarks>
    public static PartBox Measure(RsMesh mesh, int frame)
    {
        if (mesh.Frames.Length == 0 || mesh.Verts.Length < (mesh.Frames[Math.Clamp(frame, 0, mesh.Frames.Length - 1)].FirstVert + 2) * 4)
            return default;   // no box to read (the stock mesh always has one)
        var f = mesh.Frames[Math.Clamp(frame, 0, mesh.Frames.Length - 1)];
        int a = f.FirstVert * 4, b = a + 4;
        float x0 = Dec(mesh.Verts[a], f.Sx, f.Ox), y0 = Dec(mesh.Verts[a + 1], f.Sy, f.Oy), z0 = Dec(mesh.Verts[a + 2], f.Sz, f.Oz);
        float x1 = Dec(mesh.Verts[b], f.Sx, f.Ox), y1 = Dec(mesh.Verts[b + 1], f.Sy, f.Oy), z1 = Dec(mesh.Verts[b + 2], f.Sz, f.Oz);
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f, cz = (z0 + z1) * 0.5f;
        float nx0 = x0 - cx, ny0 = y0 - cy, nz0 = z0 - cz, nx1 = x1 - cx, ny1 = y1 - cy, nz1 = z1 - cz;
        Span<float> size = [nx1 - nx0, ny1 - ny0, nz1 - nz0];
        var r = X87.Lenf(size) * 0.5f;
        return new PartBox(x0, y0, z0, x1, y1, z1, cx, cy, cz, r, (nx0 + nx1) * 0.5f, (ny0 + ny1) * 0.5f, (nz0 + nz1) * 0.5f);
    }

    private static float Dec(byte v, float scale, float origin) => (float)((v * (double)scale) + origin);
}
