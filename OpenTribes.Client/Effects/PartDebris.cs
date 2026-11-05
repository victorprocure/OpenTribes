// PartDebris (docs/shapes-render.md 4.4): the pieces of a dying shape and how they size and split. The stock functions followed here:
//   loadShape        0047603c  the collision radius of a piece (the bounds of its part)
//   part box         005c3b18 / 005ad14c  the part of one object: the object's frame box, moved so that it is centred on the part origin
//   end of a piece   004762e0  explode virtual: Debris_explode when the part holds one object, else a second PartDebrisCloud
//   split cloud      004764f8 / 004765c0  the cloud ctor of that second cloud and the setter of its split chance
//   row choice       00475edc  PartDebris_ctorFromPart, the piece for a part of a split cloud
//   part split       005c4060 / 005c41ac / 005c44c0 / 005c48dc  the split of a node tree into parts, with its own random draw per node
// ShapeBase_explode (0044cdec) only makes the first cloud, whose parts are one object each (005c463c, 005c3b18 counts it), so 004762e0 always ends in
// Debris_explode for stock shapes. The split path (a part with any other object count) is kept here for the day a shape needs it.
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

    // ---- 004762e0: the end of a piece ----

    // What 004762e0 does when a PartDebris ends (its lifetime is over, or Debris_tick raised the explode flag).
    //   ExplodeLikeDebris: the part holds exactly one object (+0x154 == 1): the plain Debris_explode 00472954 and nothing else.
    //   Split: otherwise, when the budget left (+0x344) after taking off the seconds lived is above zero, a second PartDebrisCloud (004764f8) is made at the
    //   piece, carrying the table of rows (+0x378), the DebrisData spawnedDebrisStrength (+0x84) and the split chance of 004765c0.
    //   Explosion: with a split, the Explosion of spawnedExplosionID (+0x254) at the piece position when that id is not 0 (the test is for 0, not for -1 as in
    //   Debris_explode).
    //   The piece is deleted in every case, a budget of zero or less makes nothing.
    public readonly record struct End(bool ExplodeLikeDebris, bool Split, bool Explosion, double Budget, double Strength, double SplitChance, bool CopyOwner);

    // The split chance 004765c0 stores in the cloud (+0x184): the float 0x3f266666.
    public const double SplitChance = 0.6499999761581421;

    // `age` is the simulation time now (manager +0x80) less the birth time (+0x334). `copyOwner` is the byte +0x34d: the new cloud gets the ghost field +0x108
    // from the piece's +0x68 (047441c).
    public static End Ending(int objectCount, double budget, double age, int explosionId, double spawnStrength, bool copyOwner)
    {
        if (objectCount == 1)
            return new End(true, false, false, budget, 0, 0, false);
        double left = (float)(budget - (float)age);
        if (left <= 0)
            return new End(false, false, false, left, 0, 0, false);
        return new End(false, true, explosionId != 0, left, spawnStrength, SplitChance, copyOwner);
    }

    // ---- 00475edc: the row of a piece of a split cloud ----

    // One row of the table the piece and its split cloud share (stride 0x18): the DebrisData id given to Debris_ctor and stored at +0x220, the mass (+0x244),
    // a size factor, the chance to take the row, the chance not to end at once, and the value stored at +0x26c.
    public readonly record struct Row(int DebrisId, float Mass, float SizeFactor, float Chance, float KeepChance, int Extra);

    // The result: the row taken, and whether the piece is flagged to explode at once (+0x34c = 1 and the budget +0x344 negated, so Debris_explode makes nothing).
    public readonly record struct RowPick(int Index, bool EndsAtOnce);

    // 00475edc: size = the part box's max - min; quarter = dot(size, size) * 0.25. The rows are walked from the first. A row is skipped while
    // row.SizeFactor * rows[0].SizeFactor < quarter (no random drawn). Otherwise one draw u * 2^-32 * 0.99 is made (R250 00688444) and the row is skipped when
    // the draw is above row.Chance. The row taken then makes one more draw, and the piece ends at once when (1 - KeepChance) * 1.0 + KeepChance is below
    // that draw * 0.99 (the 1.0 is the constant at 0061d1ac). Returns -1 when no row fits (the stock loop would read past the table).
    public static RowPick PickRow(IReadOnlyList<Row> rows, float sizeX, float sizeY, float sizeZ, R250 rng)
    {
        float quarter = (float)((sizeX * (double)sizeX + sizeY * (double)sizeY + sizeZ * (double)sizeZ) * 0.25);
        for (int i = 0; i < rows.Count; ++i)
        {
            if (rows[i].SizeFactor * (double)rows[0].SizeFactor < quarter)
                continue;
            if (Draw(rng) * 0.9900000095367432 > rows[i].Chance)
                continue;
            double keep = (1.0 - rows[i].KeepChance) * 1.0 + rows[i].KeepChance;
            bool atOnce = keep < Draw(rng) * 0.9900000095367432;
            return new RowPick(i, atOnce);
        }
        return new RowPick(-1, false);
    }

    // Random_next 005eb660 as the stock code scales it: the unsigned draw times the float 2.3283064e-10.
    public static double Draw(R250 rng) => rng.Next() * 2.3283064365386963e-10;
}

// ---- 005c4060 / 005c41ac: the split of a node tree into parts ----

// One node of the detail's tree in the shape instance: Child (+0x14, first child), Sibling (+0x10, next child of the parent), Lod (+0x18, the same node of the
// next lower detail). Objects are the visible objects of the node (the ones 005c48dc takes); Active is the byte +0x18 that a taking clears.
public sealed class SplitObject
{
    public int Index;
    public bool Active = true;
}

public sealed class SplitNode
{
    public readonly List<SplitObject> Objects = new();
    public SplitNode? Child, Sibling, Lod;
}

// A part that came out of the split: the objects of its nodes of the detail (Objects, whose number is the count +0x154) and of its lower detail nodes (LodObjects).
public sealed class SplitPart
{
    public readonly List<int> Objects = new();
    public readonly List<int> LodObjects = new();
    public int Count;
}

public static class PartSplitter
{
    // 005c4060: a first part, then 005c41ac over the tree from the detail root, then the parts without objects (Count +0x154 == 0) are dropped by moving the
    // last part into their slot. `splitChance` is the float of 004765c0 (PartDebris.SplitChance); `rng` is the generator at 006c22ec (not the debris generator).
    public static List<SplitPart> Split(SplitNode root, double splitChance, R250 rng)
    {
        var parts = new List<SplitPart>();
        var first = new SplitPart();
        parts.Add(first);
        Build(parts, first, root, true, 0, splitChance, rng);
        for (int i = 0; i < parts.Count; ++i)
        {
            if (parts[i].Count != 0)
                continue;
            parts[i] = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            --i;
        }
        return parts;
    }

    // 005c41ac. `isRoot` is the char argument being 0 (a node that starts a part), `parentLod` the length of the lower detail chain the parent got (005c44c0 stops
    // a child's chain at the parent's). The order of the stock calls: the lower detail nodes (005c44c0), ONE draw, the objects of the node (005c48dc) and of its
    // chain, the children, the next sibling. Returns false when 005c41ac returns 0 (nothing in the node, below it or after it).
    private static bool Build(List<SplitPart> parts, SplitPart part, SplitNode src, bool isRoot, int parentLod, double splitChance, R250 rng)
    {
        int lodLen = 0;
        for (SplitNode? s = src.Lod; s != null && (isRoot || lodLen < parentLod); s = s.Lod)
            ++lodLen;
        bool splitHere = PartDebris.Draw(rng) < splitChance;   // setb: draw below the chance
        int moved = Take(src, part.Objects);
        part.Count += moved;
        SplitNode? l = src.Lod;
        for (int k = 0; k < lodLen && l != null; ++k, l = l.Lod)
            Take(l, part.LodObjects);
        bool child = false;
        if (src.Child != null)
        {
            if (!splitHere)
                child = Build(parts, part, src.Child, false, lodLen, splitChance, rng);
            else
            {
                for (SplitNode? c = src.Child; c != null; c = c.Sibling)
                {
                    var np = new SplitPart();
                    parts.Add(np);
                    bool res = Build(parts, np, c, true, 0, splitChance, rng);
                    if (!res && parts[^1] == np)
                        parts.RemoveAt(parts.Count - 1);
                }
            }
        }
        bool sibling = !isRoot && src.Sibling != null && Build(parts, part, src.Sibling, false, parentLod, splitChance, rng);
        // 005c44c0 links the first lower detail node into the sibling field of a part's root, so a root with a chain is never empty
        return child || sibling || (isRoot && lodLen > 0) || moved > 0;
    }

    // 005c48dc: take the active objects of a node, clearing their flags.
    private static int Take(SplitNode n, List<int> into)
    {
        int c = 0;
        foreach (SplitObject o in n.Objects)
        {
            if (!o.Active)
                continue;
            into.Add(o.Index);
            o.Active = false;
            ++c;
        }
        return c;
    }
}
