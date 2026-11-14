namespace OpenTribes.Client.Effects;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.World;

/// <summary>A DebrisData row as the cloud sees it.</summary>
/// <param name="Index">The index in the group (the order of the data block manager).</param>
/// <param name="Type">The <c>type</c> mask (0x34).</param>
/// <param name="Mass">The mass (0x44).</param>
/// <param name="Weight">The weight <c>p</c> (0x90).</param>
/// <param name="Shape">The shape name.</param>
/// <param name="Params">The debris parameters.</param>
public readonly record struct DebrisRow(int Index, uint Type, double Mass, double Weight, string Shape, DebrisParams Params);

/// <summary>The secondary debris of a piece that ends, and the ghost object 0x20c, which no stock server ever sends.</summary>
/// <remarks>docs/shapes-render.md 4.4. Debris_explode (00472954) makes one through DebrisCloud_ctorParams (00474268). The spawn is DebrisCloud_spawn (00474478): the rows of DebrisData whose <c>type</c> shares a bit with the cloud mask (00475484), picked by weight until the budget is spent, each thrown out of the cloud position by 00473e58. A PartDebrisCloud (00476434) is the DebrisEffect of Debris.cs. The stock DebrisData all have type 0 and the mask 1, so the stock cloud finds no row and makes nothing.</remarks>
public static class DebrisCloud
{
    /// <summary>Finds the rows the cloud can use.</summary>
    /// <param name="all">All DebrisData rows in index order.</param>
    /// <param name="mask">The cloud mask.</param>
    /// <param name="budget">The mass budget.</param>
    /// <returns>The rows and the sum of their weights.</returns>
    /// <remarks>Follows 00475484: walk the rows in index order and stop at the first row heavier than the budget. Keep the rows whose type shares a bit with the mask as {index, int(mass), weight}.</remarks>
    public static (List<DebrisRow> Rows, double Total) Find(IEnumerable<DebrisRow> all, uint mask, double budget)
    {
        var rows = new List<DebrisRow>();
        double total = 0;
        foreach (var r in all)
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

    /// <summary>Picks the rows of the pieces by weight.</summary>
    /// <param name="rows">The rows from <see cref="Find"/>.</param>
    /// <param name="total">The weight sum of the rows.</param>
    /// <param name="budget">The mass budget.</param>
    /// <param name="rng">The random source.</param>
    /// <param name="maxPieces">The most pieces, a guard for a budget that no row can use up.</param>
    /// <returns>The picked rows.</returns>
    /// <remarks>Follows 00474478: scan from the last row down (a random value in [0, total) is reduced by the weight of each row it passes), spend the mass of the row from the budget, drop the last rows that no longer fit with their weight, until no row is left. Rows with weight 0 never end a scan early, so the loop count is bounded by the budget.</remarks>
    public static List<DebrisRow> Pick(List<DebrisRow> rows, double total, double budget, Random rng, int maxPieces = 256)
    {
        var picked = new List<DebrisRow>();
        var count = rows.Count;
        while (count != 0 && picked.Count < maxPieces)
        {
            var r = rng.NextDouble() * total;
            var i = count - 1;
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

    /// <summary>Builds the orthonormal basis of the cone, with z the cloud direction.</summary>
    /// <param name="dx">The direction x.</param>
    /// <param name="dy">The direction y.</param>
    /// <param name="dz">The direction z.</param>
    /// <param name="angle">The half angle of the cone.</param>
    /// <returns>The x, y and z axes and the half angle. A zero direction gives the identity basis and the half angle pi, so the cloud throws in every direction.</returns>
    /// <remarks>Follows 00473c20.</remarks>
    public static (double[] X, double[] Y, double[] Z, double Angle) Basis(double dx, double dy, double dz, double angle)
    {
        var len2 = (dx * dx) + (dy * dy) + (dz * dz);
        if (len2 < 1e-12)
            return (new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, Math.PI);
        var l = Math.Sqrt(len2);
        double[] z = [dx / l, dy / l, dz / l];
        double[] x = [z[1], -z[0], 0];
        var xl = Math.Sqrt((x[0] * x[0]) + (x[1] * x[1]));
        x = xl < 1e-6 ? [1, 0, 0] : [x[0] / xl, x[1] / xl, 0];
        double[] y = [(z[1] * x[2]) - (z[2] * x[1]), (z[2] * x[0]) - (z[0] * x[2]), (z[0] * x[1]) - (z[1] * x[0])];
        return (x, y, z, angle);
    }

    /// <summary>Launches one piece.</summary>
    /// <param name="body">The piece body.</param>
    /// <param name="px">The cloud position x.</param>
    /// <param name="py">The cloud position y.</param>
    /// <param name="pz">The cloud position z.</param>
    /// <param name="vx">The cloud velocity x.</param>
    /// <param name="vy">The cloud velocity y.</param>
    /// <param name="vz">The cloud velocity z.</param>
    /// <param name="ix">The extra impulse x of the cloud.</param>
    /// <param name="iy">The extra impulse y of the cloud.</param>
    /// <param name="iz">The extra impulse z of the cloud.</param>
    /// <param name="angle">The half angle of the cone.</param>
    /// <param name="basis">The cone basis from <see cref="Basis"/>.</param>
    /// <param name="strength">The impulse strength.</param>
    /// <param name="radius">The distance from the cloud at which the piece starts.</param>
    /// <param name="mass">The piece mass.</param>
    /// <param name="rng">The random source.</param>
    /// <remarks>Follows 00473e58: a direction in the cone (azimuth in [0, 2 pi), polar angle in [-angle, angle]), the position <paramref name="radius"/> along it from the cloud, the cloud velocity, a spin of +-min(strength * 0.015, 6) / 2 per axis, and the impulse strength along the direction plus the extra impulse of the cloud, both divided by the piece mass.</remarks>
    public static void Launch(DebrisBody body, double px, double py, double pz, double vx, double vy, double vz, double ix, double iy, double iz, double angle,
                              (double[] X, double[] Y, double[] Z, double Angle) basis, double strength, double radius, double mass, Random rng)
    {
        var a = rng.NextDouble() * Math.PI * 2;
        var polar = (rng.NextDouble() * (2 * angle)) - angle;
        double sp = Math.Sin(polar), cx = Math.Cos(a) * sp, cy = Math.Sin(a) * sp, cz = Math.Cos(polar);
        var d = new double[3];
        for (var k = 0; k < 3; ++k)
            d[k] = (basis.Z[k] * cz) + (basis.Y[k] * cy) + (basis.X[k] * cx);
        body.X = px + (radius * d[0]);
        body.Y = py + (radius * d[1]);
        body.Z = pz + (radius * d[2]);
        body.Mass = mass;
        var inv = mass > 0 ? 1 / mass : 0;
        body.Vx = vx + (((d[0] * strength) + ix) * inv);
        body.Vy = vy + (((d[1] * strength) + iy) * inv);
        body.Vz = vz + (((d[2] * strength) + iz) * inv);
        var s = Math.Min(strength * 0.015, 6);
        body.Wx = (rng.NextDouble() * s) - (s * 0.5);
        body.Wy = (rng.NextDouble() * s) - (s * 0.5);
        body.Wz = (rng.NextDouble() * s) - (s * 0.5);
        // the stock piece starts at a random entry of a table of 16 orientations; the table is filled at run time and not decoded, so the start spin is random
        body.Ax = rng.NextDouble() * Math.PI * 2;
        body.Ay = rng.NextDouble() * Math.PI * 2;
        body.Az = rng.NextDouble() * Math.PI * 2;
    }
}

/// <summary>The pieces of one DebrisCloud: whole shapes (DebrisData pShapeName) flying like the parts of a dying shape.</summary>
public sealed class DebrisCloudEffect : IEffect
{
    private sealed class Piece
    {
        public DebrisBody Body { get; set; } = new();
        public RenderInstance Inst { get; set; } = null!;
        public DebrisParams Params { get; set; }
        public double Remaining { get; set; }     // the piece budget +0x344: the cloud's budget argument
    }

    private readonly List<Piece> _pieces = [];
    private readonly Terrain? _terrain;
    private readonly Random _rng;
    private readonly DebrisRay? _ray;
    private readonly Action<int, double, double, double>? _explode;
    private readonly Action<DebrisCloudSpec>? _cloud;
    private double _acc;

    /// <inheritdoc/>
    public bool Alive => _pieces.Exists(p => !p.Body.Done);
    /// <inheritdoc/>
    public PointLight? Light => null;
    /// <summary>The number of pieces.</summary>
    public int PieceCount => _pieces.Count;
    /// <summary>The bodies of the pieces.</summary>
    public IEnumerable<DebrisBody> Bodies => _pieces.Select(p => p.Body);

    /// <summary>Creates the effect and spawns the pieces.</summary>
    /// <param name="rows">All DebrisData rows in index order.</param>
    /// <param name="shape">Gives the shape instance of a row shape name.</param>
    /// <param name="terrain">The terrain the pieces collide with, or null.</param>
    /// <param name="rng">The random source.</param>
    /// <param name="spec">The cloud: position, velocity, mask, budget, strength (+0xb8) and radius (+0xa8).</param>
    /// <param name="dx">The direction x of the cloud (+0xbc).</param>
    /// <param name="dy">The direction y.</param>
    /// <param name="dz">The direction z.</param>
    /// <param name="angle">The half angle of the cone, pi for none.</param>
    /// <param name="ix">The extra impulse x (+0xf0).</param>
    /// <param name="iy">The extra impulse y.</param>
    /// <param name="iz">The extra impulse z.</param>
    /// <param name="ray">The ray test of the pieces, or null.</param>
    /// <param name="explode">Called when a piece ends in an explosion, with the explosion datablock index and the position.</param>
    /// <param name="cloud">Called when a piece ends in a cloud of its own.</param>
    public DebrisCloudEffect(IReadOnlyList<DebrisRow> rows, Func<string, RenderInstance?> shape, Terrain? terrain, Random rng, DebrisCloudSpec spec,
                             double dx = 0, double dy = 0, double dz = 0, double angle = Math.PI, double ix = 0, double iy = 0, double iz = 0,
                             DebrisRay? ray = null, Action<int, double, double, double>? explode = null, Action<DebrisCloudSpec>? cloud = null)
    {
        _terrain = terrain;
        _rng = rng;
        _ray = ray;
        _explode = explode;
        _cloud = cloud;
        var (found, total) = DebrisCloud.Find(rows, (uint)spec.Mask, spec.Budget);
        var basis = DebrisCloud.Basis(dx, dy, dz, angle);
        foreach (var row in DebrisCloud.Pick(found, total, spec.Budget, rng))
        {
            var inst = shape(row.Shape);
            if (inst == null)
                continue;
            var body = new DebrisBody { Radius = Math.Max(0.1, inst.Shape.Radius) };
            DebrisCloud.Launch(body, spec.X, spec.Y, spec.Z, spec.Vx, spec.Vy, spec.Vz, ix, iy, iz, basis.Angle, basis, spec.Strength, spec.Radius, row.Params.Mass, rng);
            body.Life = row.Params.MinTimeout + ((row.Params.MaxTimeout - row.Params.MinTimeout) * rng.NextDouble());
            body.Damage = row.Params.Damage;
            _pieces.Add(new Piece { Body = body, Inst = inst, Params = row.Params, Remaining = spec.Remaining });
        }
    }

    /// <inheritdoc/>
    public void Tick(double dt)
    {
        _acc += dt;
        while (_acc >= DebrisBody.TimeSlice)
        {
            _acc -= DebrisBody.TimeSlice;
            foreach (var p in _pieces)
            {
                var b = p.Body;
                if (b.Done)
                    continue;
                var sh = p.Inst.Shape;
                var explode = b.WantsExplosion(p.Params, _rng) || b.Integrate(p.Params, _terrain, _ray, _rng, sh.Cx, sh.Cy, sh.Cz);
                if (!explode)
                    continue;
                b.Done = true;
                if (b.SecondaryCloud(p.Params, p.Remaining - b.Age) is { } spec)
                    _cloud?.Invoke(spec);
                if (p.Params.Explosion >= 0)
                    _explode?.Invoke(p.Params.Explosion, b.X, b.Y, b.Z);
            }
        }
    }

    /// <inheritdoc/>
    public void Build(FrameContext ctx)
    {
        foreach (var p in _pieces)
        {
            var b = p.Body;
            if (b.Done)
                continue;
            var xf = Xf.Euler(b.Ax, b.Ay, b.Az).Then(Xf.Translate(b.X, b.Y, b.Z));
            var img = ctx.Renderer.Build(p.Inst, ctx.Camera, ctx.Draw(xf, p.Inst.Shape.Name + " debris"));
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
