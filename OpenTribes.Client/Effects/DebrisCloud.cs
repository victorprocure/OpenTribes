// DebrisCloud (docs/shapes-render.md 4.4): the secondary debris of a piece that ends (Debris_explode 00472954 makes one through DebrisCloud_ctorParams 00474268)
// and the ghost object 0x20c, which no stock server ever sends. The spawn is DebrisCloud_spawn 00474478: the rows of DebrisData whose `type` shares a bit with the
// cloud mask (00475484), picked by weight until the budget is spent, each thrown out of the cloud position by 00473e58. A PartDebrisCloud (00476434) is the
// DebrisEffect of Debris.cs. The stock DebrisData all have type 0 and the mask 1, so the stock cloud finds no row and makes nothing.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.World;

namespace OpenTribes.Client.Effects;

// A DebrisData row as the cloud sees it: index in the group (the order of the data block manager), `type` (0x34), mass (0x44), weight `p` (0x90).
public readonly record struct DebrisRow(int Index, uint Type, double Mass, double Weight, string Shape, DebrisParams Params);

public static class DebrisCloud
{
    // 00475484: walk the rows in index order and stop at the first row heavier than the budget; keep the rows whose type shares a bit with the mask as
    // {index, int(mass), weight}. The weight sum goes with them.
    public static (List<DebrisRow> Rows, double Total) Find(IEnumerable<DebrisRow> all, uint mask, double budget)
    {
        var rows = new List<DebrisRow>();
        double total = 0;
        foreach (DebrisRow r in all)
        {
            if (r.Mass > budget)
                break;
            if ((r.Type & mask) != 0)
            {
                rows.Add(r with { Mass = Math.Truncate(r.Mass) });
                total += r.Weight;
            }
        }
        return (rows, total);
    }

    // 00474478: pick rows by weight, scanning from the last row down (a random value in [0, total) is reduced by the weight of each row it passes), spend the row's
    // mass from the budget, drop the last rows that no longer fit with their weight, until no row is left. Rows with weight 0 never end a scan early, so the
    // loop count is bounded by the budget; `maxPieces` guards a budget that no row can use up.
    public static List<DebrisRow> Pick(List<DebrisRow> rows, double total, double budget, Random rng, int maxPieces = 256)
    {
        var picked = new List<DebrisRow>();
        int count = rows.Count;
        while (count != 0 && picked.Count < maxPieces)
        {
            double r = rng.NextDouble() * total;
            int i = count - 1;
            while (i > 0 && rows[i].Weight < r)
            {
                r -= rows[i].Weight;
                --i;
            }
            picked.Add(rows[i]);
            budget -= rows[i].Mass;
            while (count != 0 && budget < rows[count - 1].Mass)
            {
                --count;
                total -= rows[count].Weight;
            }
        }
        return picked;
    }

    // 00473c20: the orthonormal basis (x, y, z) of the cone: z the cloud direction. A zero direction gives the identity basis and the half angle pi (the cloud
    // then throws in every direction).
    public static (double[] X, double[] Y, double[] Z, double Angle) Basis(double dx, double dy, double dz, double angle)
    {
        double len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 1e-12)
            return (new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, Math.PI);
        double l = Math.Sqrt(len2);
        double[] z = { dx / l, dy / l, dz / l };
        double[] x = { z[1], -z[0], 0 };
        double xl = Math.Sqrt(x[0] * x[0] + x[1] * x[1]);
        x = xl < 1e-6 ? new double[] { 1, 0, 0 } : new[] { x[0] / xl, x[1] / xl, 0 };
        double[] y = { z[1] * x[2] - z[2] * x[1], z[2] * x[0] - z[0] * x[2], z[0] * x[1] - z[1] * x[0] };
        return (x, y, z, angle);
    }

    // 00473e58: the launch of one piece. A direction in the cone (azimuth in [0, 2 pi), polar angle in [-angle, angle]), the position `radius` along it from the
    // cloud, the cloud velocity, a spin of +-min(strength * 0.015, 6) / 2 per axis, and the impulse strength along the direction plus the cloud's extra impulse, both
    // divided by the piece mass.
    public static void Launch(DebrisBody body, double px, double py, double pz, double vx, double vy, double vz, double ix, double iy, double iz, double angle,
                              (double[] X, double[] Y, double[] Z, double Angle) basis, double strength, double radius, double mass, Random rng)
    {
        double a = rng.NextDouble() * Math.PI * 2;
        double polar = rng.NextDouble() * (2 * angle) - angle;
        double sp = Math.Sin(polar), cx = Math.Cos(a) * sp, cy = Math.Sin(a) * sp, cz = Math.Cos(polar);
        double[] d = new double[3];
        for (int k = 0; k < 3; ++k)
            d[k] = basis.Z[k] * cz + basis.Y[k] * cy + basis.X[k] * cx;
        body.X = px + radius * d[0]; body.Y = py + radius * d[1]; body.Z = pz + radius * d[2];
        body.Mass = mass;
        double inv = mass > 0 ? 1 / mass : 0;
        body.Vx = vx + (d[0] * strength + ix) * inv;
        body.Vy = vy + (d[1] * strength + iy) * inv;
        body.Vz = vz + (d[2] * strength + iz) * inv;
        double s = Math.Min(strength * 0.015, 6);
        body.Wx = rng.NextDouble() * s - s * 0.5;
        body.Wy = rng.NextDouble() * s - s * 0.5;
        body.Wz = rng.NextDouble() * s - s * 0.5;
        // the stock piece starts at a random entry of a table of 16 orientations; the table is filled at run time and not decoded, so the start spin is random
        body.Ax = rng.NextDouble() * Math.PI * 2; body.Ay = rng.NextDouble() * Math.PI * 2; body.Az = rng.NextDouble() * Math.PI * 2;
    }
}

// The pieces of one DebrisCloud: whole shapes (DebrisData pShapeName) flying like the parts of a dying shape. `shape(name)` gives the shape of a row.
public sealed class DebrisCloudEffect : IEffect
{
    private sealed class Piece
    {
        public DebrisBody Body = new();
        public RenderInstance Inst = null!;
        public DebrisParams Params;
        public double Remaining;     // the piece budget +0x344: the cloud's budget argument
    }

    private readonly List<Piece> mPieces = new();
    private readonly Terrain? mTerrain;
    private readonly Random mRng;
    private readonly DebrisRay? mRay;
    private readonly Action<int, double, double, double>? mExplode;
    private readonly Action<DebrisCloudSpec>? mCloud;
    private double mAcc;

    public bool Alive => mPieces.Exists(p => !p.Body.Done);
    public PointLight? Light => null;
    public int PieceCount => mPieces.Count;
    public IEnumerable<DebrisBody> Bodies => mPieces.Select(p => p.Body);

    // `rows` are all DebrisData rows in index order. `strength` and `radius` are the cloud's +0xb8 and +0xa8, `(dx, dy, dz)` its direction +0xbc with the half angle
    // `angle` (pi for none), `(ix, iy, iz)` the extra impulse +0xf0.
    public DebrisCloudEffect(IReadOnlyList<DebrisRow> rows, Func<string, RenderInstance?> shape, Terrain? terrain, Random rng, DebrisCloudSpec spec,
                             double dx = 0, double dy = 0, double dz = 0, double angle = Math.PI, double ix = 0, double iy = 0, double iz = 0,
                             DebrisRay? ray = null, Action<int, double, double, double>? explode = null, Action<DebrisCloudSpec>? cloud = null)
    {
        mTerrain = terrain;
        mRng = rng;
        mRay = ray;
        mExplode = explode;
        mCloud = cloud;
        var (found, total) = DebrisCloud.Find(rows, (uint)spec.Mask, spec.Budget);
        var basis = DebrisCloud.Basis(dx, dy, dz, angle);
        foreach (DebrisRow row in DebrisCloud.Pick(found, total, spec.Budget, rng))
        {
            RenderInstance? inst = shape(row.Shape);
            if (inst == null)
                continue;
            var body = new DebrisBody { Radius = Math.Max(0.1, inst.Shape.Radius) };
            DebrisCloud.Launch(body, spec.X, spec.Y, spec.Z, spec.Vx, spec.Vy, spec.Vz, ix, iy, iz, basis.Angle, basis, spec.Strength, spec.Radius, row.Params.Mass, rng);
            body.Life = row.Params.MinTimeout + (row.Params.MaxTimeout - row.Params.MinTimeout) * rng.NextDouble();
            body.Damage = row.Params.Damage;
            mPieces.Add(new Piece { Body = body, Inst = inst, Params = row.Params, Remaining = spec.Remaining });
        }
    }

    public void Tick(double dt)
    {
        mAcc += dt;
        while (mAcc >= DebrisBody.TimeSlice)
        {
            mAcc -= DebrisBody.TimeSlice;
            foreach (Piece p in mPieces)
            {
                DebrisBody b = p.Body;
                if (b.Done)
                    continue;
                RenderShape sh = p.Inst.Shape;
                bool explode = b.WantsExplosion(p.Params, mRng) || b.Integrate(p.Params, mTerrain, mRay, mRng, sh.Cx, sh.Cy, sh.Cz);
                if (!explode)
                    continue;
                b.Done = true;
                if (b.SecondaryCloud(p.Params, p.Remaining - b.Age) is { } spec)
                    mCloud?.Invoke(spec);
                if (p.Params.Explosion >= 0)
                    mExplode?.Invoke(p.Params.Explosion, b.X, b.Y, b.Z);
            }
        }
    }

    public void Build(FrameContext ctx)
    {
        foreach (Piece p in mPieces)
        {
            DebrisBody b = p.Body;
            if (b.Done)
                continue;
            Xf xf = Xf.Euler(b.Ax, b.Ay, b.Az).Then(Xf.Translate(b.X, b.Y, b.Z));
            RenderImage? img = ctx.Renderer.Build(p.Inst, ctx.Camera, ctx.Draw(xf, p.Inst.Shape.Name + " debris"));
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
