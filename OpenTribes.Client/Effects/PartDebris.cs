// PartDebris (docs/shapes-render.md 4.4): the collision radius of a debris piece, from the box of its part. Stock functions followed:
//   loadShape  0047603c  the collision radius of a piece (the bounds of its part)
//   part box   005c3b18 / 005ad14c  the part of one object: the object's frame box, moved so that it is centred on the part origin
// ShapeBase_explode (0044cdec) makes parts of one object each (005c463c, 005c3b18 counts it), so the piece end 004762e0 always takes the plain
// Debris_explode for stock shapes and the re-split path (004764f8, 00475edc, 005c4060) is never reached. It is not ported.
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.World;

namespace OpenTribes.Client.Effects;

// The box of the part of one object, in the object's own space, as the part keeps it after 005c3b18.
// Min/Max: the frame box before the move (005ad14c). Centre: its middle (the part origin moves there). Radius: loadShape 0047603c of the moved box.
public readonly record struct PartBox(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ, float Cx, float Cy, float Cz, float Radius, float SphereX, float SphereY, float SphereZ);

public static class PartDebris
{
    // 005ad14c TSCelAnimMesh_getFrameBox, then 005c3b18 (the single object part) and 0047603c (loadShape).
    //   005ad14c: min = decoded vertex FirstVert of the frame, max = decoded vertex FirstVert + 1 (byte * frame scale + frame origin), in that order.
    //   005c3b18: centre c = (min + max) * 0.5 and the part's bounds are min - c and max - c.
    //   0047603c: size = max - min of those bounds, radius (+0x318) = m_Point3F_lenf(size) * 0.5, sphere centre (+0x30c) = (min + max) * 0.5 of the bounds.
    // m_Point3F_lenf 005ed031 is the bit trick sqrt (bits >> 1) + 0x1fc00000, not an exact root, so a piece is up to 6% wider than its half diagonal.
    // The frame is the object's current frame (+4 of the object instance, copied by 005c463c); an index outside the mesh is clamped as the renderer does.
    public static PartBox Measure(RsMesh mesh, int frame)
    {
        if (mesh.Frames.Length == 0 || mesh.Verts.Length < (mesh.Frames[Math.Clamp(frame, 0, mesh.Frames.Length - 1)].FirstVert + 2) * 4)
            return default;   // no box to read (the stock mesh always has one)
        RsFrame f = mesh.Frames[Math.Clamp(frame, 0, mesh.Frames.Length - 1)];
        int a = f.FirstVert * 4, b = a + 4;
        float x0 = Dec(mesh.Verts[a], f.Sx, f.Ox), y0 = Dec(mesh.Verts[a + 1], f.Sy, f.Oy), z0 = Dec(mesh.Verts[a + 2], f.Sz, f.Oz);
        float x1 = Dec(mesh.Verts[b], f.Sx, f.Ox), y1 = Dec(mesh.Verts[b + 1], f.Sy, f.Oy), z1 = Dec(mesh.Verts[b + 2], f.Sz, f.Oz);
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f, cz = (z0 + z1) * 0.5f;
        float nx0 = x0 - cx, ny0 = y0 - cy, nz0 = z0 - cz, nx1 = x1 - cx, ny1 = y1 - cy, nz1 = z1 - cz;
        Span<float> size = stackalloc float[3] { nx1 - nx0, ny1 - ny0, nz1 - nz0 };
        float r = X87.Lenf(size) * 0.5f;
        return new PartBox(x0, y0, z0, x1, y1, z1, cx, cy, cz, r, (nx0 + nx1) * 0.5f, (ny0 + ny1) * 0.5f, (nz0 + nz1) * 0.5f);
    }

    private static float Dec(byte v, float scale, float origin) => (float)(v * (double)scale + origin);
}
