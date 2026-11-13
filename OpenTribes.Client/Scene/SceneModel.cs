namespace OpenTribes.Client.Scene;

using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

/// <summary>The visible state of a demo or a live connection: the visuals of the ghosts, the transient effects, the smoke pool and the decal ring.</summary>
/// <remarks>docs/shapes-render.md 2.1, 4 and 5. It is fed by the decoded packets of OpenTribes.Game.Client and steps in 32 ms frames like the client loop.</remarks>
public sealed class SceneModel
{
    /// <summary>The client world the packets feed.</summary>
    public readonly ClientWorld World;
    /// <summary>The datablock reader.</summary>
    public readonly Db Db;
    /// <summary>The shape loader.</summary>
    public readonly ShapeAssets Assets;
    /// <summary>The shape image builder.</summary>
    public readonly ShapeRenderer Renderer;
    /// <summary>The terrain for ground queries, or null.</summary>
    public readonly Terrain? Terrain;
    /// <summary>The visual of each ghost by ghost index.</summary>
    public readonly Dictionary<int, Visual> Visuals = [];
    /// <summary>The transient effects.</summary>
    public readonly List<IEffect> Effects = [];
    /// <summary>The smoke pool.</summary>
    public SmokePool Smoke;
    /// <summary>The decal ring.</summary>
    public DecalRing Decals;
    /// <summary>The rain or snow effect.</summary>
    public readonly WeatherEffect Weather = new();
    /// <summary>True when the mission has weather.</summary>
    public bool HasWeather;
    /// <summary>The <c>$pref::weatherPrecipitation</c> switch, default 1 (shapes-render.md 4.8).</summary>
    public bool WeatherPrecipitation = true;
    /// <summary>The random source of the effects.</summary>
    public Random Rng = new(0x8377);
    /// <summary>The scene time in seconds.</summary>
    public double Time;
    /// <summary>The sound starts, or null without audio.</summary>
    public Audio.SceneAudio? Audio;
    /// <summary>The <c>$pref::animateWithTransitions</c> switch, default off (3.3).</summary>
    public bool AnimateWithTransitions;
    /// <summary>The <c>$pref::playerShapeDetail</c> value.</summary>
    public double PlayerShapeDetail = 1;
    /// <summary>The scene light.</summary>
    public LightSet BaseLight = LightSet.Default();
    /// <summary>The shape light probe. Null means shapes keep the scene light as it is. The host that has the interiors and the terrain renderer sets it.</summary>
    public LightProbe? Probe;
    /// <summary>The number of shape triggers fired.</summary>
    public int TriggerCount;
    /// <summary>The number of contact triggers fired.</summary>
    public int ContactTriggers;
    /// <summary>The number of explosions spawned.</summary>
    public int ExplosionsSpawned;
    /// <summary>The number of footprints added.</summary>
    public int FootprintsAdded;
    /// <summary>The number of bullet holes added.</summary>
    public int BulletHolesAdded;
    private bool mDecalsLoaded;

    /// <summary>Creates the model.</summary>
    /// <param name="world">The client world.</param>
    /// <param name="assets">The shape loader.</param>
    /// <param name="renderer">The shape image builder.</param>
    /// <param name="terrain">The terrain for ground queries, or null.</param>
    /// <param name="smokePuffs">The size of the smoke pool.</param>
    /// <param name="decals">The size of the decal ring.</param>
    public SceneModel(ClientWorld world, ShapeAssets assets, ShapeRenderer renderer, Terrain? terrain, int smokePuffs = 100, int decals = 50)
    {
        World = world;
        Db = new Db(world.DataBlocks);
        Assets = assets;
        Renderer = renderer;
        Terrain = terrain;
        Smoke = new SmokePool(smokePuffs);
        Decals = new DecalRing(decals);
    }

    private static Visual? Create(string cls) => cls switch
    {
        "Player" => new PlayerVisual(),
        "StaticShape" or "Turret" or "Sensor" or "Item" or "Mine" or "Moveable" or "Vehicle" or "Flier" => new ShapeVisual(),
        "Bullet" or "Grenade" or "RocketDumb" or "SeekingMissile" => new ProjectileVisual(),
        "LaserProjectile" or "TargetLaser" or "Lightning" or "RepairEffect" => new BeamVisual(),
        _ => null,
    };

    /// <summary>Gets the skin name of a team.</summary>
    /// <param name="team">The team index.</param>
    /// <returns>The skin, or empty for an unknown team.</returns>
    public string TeamSkin(int team) => World.Players.Teams.TryGetValue(team, out ClientTeam? t) ? t.Skin : "";

    /// <summary>Applies a decoded packet: events, then the ghost updates.</summary>
    /// <param name="pkt">The packet.</param>
    public void OnPacket(DecodedPacket pkt)
    {
        Audio?.OnPacket(pkt);
        if (!mDecalsLoaded && World.DataBlocks.Blocks.ContainsKey((2, 0)))
        {
            mDecalsLoaded = true;
            ClientDataBlock? ds = Db.Block(2, 0);
            Assets.SetDamageDecals(Enumerable.Range(0, 10).Select(i => Db.Str(ds, $"bmpName[{i}]")).Where(s => s.Length > 0)
                .Select(s => s.Contains('.') ? s : s + ".bmp"));
        }
        if (pkt.Ghosts == null)
            return;
        foreach (DecodedGhost u in pkt.Ghosts.Updates)
        {
            if (u.Op == "delete")
            {
                Audio?.OnDelete(u.Index);
                if (Visuals.Remove(u.Index, out Visual? gone))
                    gone.OnDelete();
                continue;
            }
            ClientGhost? g = World.Ghosts.Get(u.Index);
            if (g == null)
                continue;
            if (g.Class == "Snowfall")
            {
                HasWeather = true;
                if (g.State.TryGetValue("intensity", out var it))
                    Weather.Intensity = Math.Clamp(Db.ToDouble(it), 0, 1);
                if (g.State.TryGetValue("wind", out var w))
                { var wv = Db.Vec(w); Weather.WindX = wv[0]; Weather.WindY = wv[1]; }
                Weather.Suspended = g.State.TryGetValue("flagf0", out var s) && Db.ToDouble(s) != 0;
                Weather.Rain = g.State.TryGetValue("flagf1", out var r) && Db.ToDouble(r) != 0;
                continue;
            }
            if (!Visuals.TryGetValue(u.Index, out Visual? v) || v.Ghost != g)
            {
                Visual? nv = Create(g.Class);
                if (nv == null)
                    continue;
                nv.Init(this, g);
                Visuals[u.Index] = v = nv;
            }
            v.OnUpdate(u, u.Op == "new");
            Audio?.OnGhost(v, u, u.Op == "new");
        }
    }

    /// <summary>Runs one client frame.</summary>
    /// <param name="dt">The frame length in seconds.</param>
    public void Tick(double dt)
    {
        Time += dt;
        foreach (Visual v in Visuals.Values)
            v.Tick(dt);
        for (var i = Effects.Count - 1; i >= 0; --i)
        {
            Effects[i].Tick(dt);
            if (!Effects[i].Alive)
                Effects.RemoveAt(i);
        }
        Smoke.Tick(dt);
        Audio?.Frame(dt);
    }

    /// <summary>Finds the nearest interior surface along a ray, with its normal (<c>InteriorRenderer.Probe</c>). The live game sets it.</summary>
    public DebrisRay? DebrisWorld;

    /// <summary>Casts the container ray of the prediction world for Player +0xfd0 (Player_updateWeaponPullback 004198d4). Takes the ghost index of the player, which the cast leaves out, the start and the end, and returns the hit point or null. The live game sets it.</summary>
    public Func<int, (double, double, double), (double, double, double), (double, double, double)?>? PullbackRay;

    /// <summary>Creates the DebrisEffect of a dying shape (PartDebrisCloud 00476434).</summary>
    /// <param name="inst">The shape instance of the dying shape.</param>
    /// <param name="pose">The pose of the shape.</param>
    /// <param name="vx">The velocity x.</param>
    /// <param name="vy">The velocity y.</param>
    /// <param name="vz">The velocity z.</param>
    /// <param name="debrisId">The DebrisData index (group 22).</param>
    /// <returns>The effect. It uses the interiors and the terrain as the world of the pieces, and spawns the explosion of a piece that ends and its secondary DebrisCloud.</returns>
    public DebrisEffect NewDebris(RenderInstance inst, Xf pose, double vx, double vy, double vz, int debrisId) =>
        new(inst, pose, vx, vy, vz, Terrain, Rng, DebrisParams.From(Db.Block(22, debrisId)), DebrisWorld,
            (e, x, y, z) => SpawnExplosion(e, x, y, z, 0, 0, 1), SpawnDebrisCloud);

    /// <summary>Spawns the DebrisCloud of a piece that ended (00474478). It uses the DebrisData rows whose type shares a bit with the mask. The stock rows have type 0, so no stock cloud has a piece.</summary>
    /// <param name="spec">The cloud.</param>
    public void SpawnDebrisCloud(DebrisCloudSpec spec)
    {
        var rows = Db.Group(22).Select(g => new DebrisRow(g.Index, (uint)Db.Num(g.Block, "type", 0), Db.Num(g.Block, "mass", 1), Db.Num(g.Block, "p", 1),
            Db.Str(g.Block, "pShapeName"), DebrisParams.From(g.Block))).ToList();
        if (!rows.Exists(r => ((uint)spec.Mask & r.Type) != 0))
            return;
        var cloud = new DebrisCloudEffect(rows, ShapeInstance, Terrain, Rng, spec, ray: DebrisWorld,
            explode: (e, x, y, z) => SpawnExplosion(e, x, y, z, 0, 0, 1), cloud: SpawnDebrisCloud);
        if (cloud.PieceCount > 0)
            Effects.Add(cloud);
    }

    private RenderInstance? ShapeInstance(string name)
    {
        if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return Assets.Shape(name) is { } sh ? new RenderInstance(sh) : null;
    }

    /// <summary>Spawns the explosion of a datablock and plays its sound.</summary>
    /// <param name="index">The explosion datablock index.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="ax">The surface axis x.</param>
    /// <param name="ay">The surface axis y.</param>
    /// <param name="az">The surface axis z.</param>
    public void SpawnExplosion(int index, double x, double y, double z, double ax, double ay, double az)
    {
        ClientDataBlock? db = Db.Block(20, index);
        if (db == null)
            return;
        Audio?.OnExplosion(db, x, y, z);
        var name = Db.Str(db, "shapeName");
        if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        RenderShape? sh = Assets.Shape(name);
        if (sh == null)
            return;
        Effects.Add(new ExplosionEffect(sh, db, x, y, z, ax, ay, az, Rng));
        ++ExplosionsSpawned;
    }

    /// <summary>Adds a footprint (4.6): the foot position offset +-0.25 along the x axis of the yaw frame of the player, a ray against the terrain, a quad aligned to the heading and the terrain normal. Only terrain gets footprints.</summary>
    /// <param name="p">The player visual.</param>
    /// <param name="side">1 for the right foot, -1 for the left foot.</param>
    public void AddFootprint(PlayerVisual p, int side)
    {
        if (Terrain == null)
            return;
        var (fx, fy, fz) = p.Foot(side);
        float h = 0;
        var n = new Vec3(0, 0, 1);
        if (!Terrain.Height((float)fx, (float)fy, ref h, ref n) || Math.Abs(fz - h) > 1.5)
            return;
        var hex = Db.Str(null, "") + (Db.Get(Db.Block(3, p.Ghost.State.TryGetValue("datablock", out var d) ? (int)Db.ToDouble(d) : -1), "footPrints") as string ?? "");
        int left = 0, right = 1;
        if (hex.Length >= 16)
        {
            left = BitConverter.ToInt32(Convert.FromHexString(hex[..8]));
            right = BitConverter.ToInt32(Convert.FromHexString(hex.AsSpan(8, 8)));
        }
        var bmp = side > 0 ? right : left;
        if (bmp < 0 || bmp >= DecalRing.Table.Length)
            return;
        var yaw = p.YawAngle;
        Decals.Add(DecalRing.Quad(bmp, fx, fy, h, n.X, n.Y, n.Z, -Math.Sin(yaw), Math.Cos(yaw), 0));
        ++FootprintsAdded;
    }

    /// <summary>Adds a bullet hole (4.6).</summary>
    /// <param name="x">The impact x.</param>
    /// <param name="y">The impact y.</param>
    /// <param name="z">The impact z.</param>
    /// <param name="nx">The impact normal x of the explode record.</param>
    /// <param name="ny">The impact normal y.</param>
    /// <param name="nz">The impact normal z.</param>
    /// <remarks>Follows Bullet_getHoleIndex (0045d048): only when the hit object is an interior (type mask bit 4, <c>$SimInteriorObjectType</c>) and the BulletData <c>bulletHoleIndex</c> is set. The quad lies on the surface plane at the impact point with a random rotation about the normal (FUN_004335d0). The terrain (type 2), shapes and players never get one.</remarks>
    public void AddBulletHole(double x, double y, double z, double nx = 0, double ny = 0, double nz = 1)
    {
        if (Probe?.Interior is not { } probe)
            return;
        var nl = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (nl < 1e-6)
            return;
        (nx, ny, nz) = (nx / nl, ny / nl, nz / nl);
        // The impact point is on the surface: look for that interior surface just in front of it.
        var (hit, t, _) = probe(x + (nx * 0.25), y + (ny * 0.25), z + (nz * 0.25), -nx, -ny, -nz, 0.5);
        if (!hit)
            return;
        double px = x + (nx * 0.25) - (nx * t), py = y + (ny * 0.25) - (ny * t), pz = z + (nz * 0.25) - (nz * t);
        var a = Rng.NextDouble() * Math.PI * 2;
        // forward = cos a * u + sin a * v, u and v an orthonormal pair in the surface plane
        (var rx, var ry, var rz) = Math.Abs(nz) > 0.99 ? (1.0, 0.0, 0.0) : (0.0, 0.0, 1.0);
        double ux = (ry * nz) - (rz * ny), uy = (rz * nx) - (rx * nz), uz = (rx * ny) - (ry * nx), ul = Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));
        (ux, uy, uz) = (ux / ul, uy / ul, uz / ul);
        double vx = (ny * uz) - (nz * uy), vy = (nz * ux) - (nx * uz), vz = (nx * uy) - (ny * ux);
        double ca = Math.Cos(a), sa = Math.Sin(a);
        Decals.Add(DecalRing.Quad(6, px, py, pz, nx, ny, nz, (ca * ux) + (sa * vx), (ca * uy) + (sa * vy), (ca * uz) + (sa * vz)));
        ++BulletHolesAdded;
    }

    /// <summary>Collects the light set of a frame (2.5, 4.9): the scene light plus every dynamic light.</summary>
    /// <returns>The light set.</returns>
    public LightSet CollectLights()
    {
        var set = new LightSet
        {
            AmbR = BaseLight.AmbR,
            AmbG = BaseLight.AmbG,
            AmbB = BaseLight.AmbB,
            SunX = BaseLight.SunX,
            SunY = BaseLight.SunY,
            SunZ = BaseLight.SunZ,
            SunR = BaseLight.SunR,
            SunG = BaseLight.SunG,
            SunB = BaseLight.SunB,
        };
        foreach (IEffect e in Effects)
            if (e.Light is PointLight l)
                set.Add(l);
        foreach (Visual v in Visuals.Values)
        {
            switch (v)
            {
                case ProjectileVisual pv when pv.Light is PointLight pl:
                    set.Add(pl);
                    break;
                case BeamVisual bv when bv.Light is PointLight bl:
                    set.Add(bl);
                    break;
                case PlayerVisual plv:
                    plv.CollectLights(set);
                    break;
                case ShapeVisual sv when sv.Class is "Item" or "Mine" && sv.Position is var (x, y, z):
                    ClientDataBlock? db = Db.Block(sv.Class == "Item" ? 5 : 23, (long)Db.ToDouble(sv.Ghost.State.GetValueOrDefault("datablock"), -1));
                    var type = (int)Db.Num(db, "lightType");
                    // Item::updateLight (00451bb0): type 2 pulses as 0.15 + 0.85 * (0.5 + 0.5 sin(pi * (now - added) / lightTime)),
                    // the phase counted from the ghost's creation; the light sits at the item's position.
                    var lightTime = Db.Num(db, "lightTime", 1.5);
                    var k = type == 1 ? 1 : type == 2 ? 0.15 + (0.85 * (0.5 + (0.5 * Math.Sin(Math.PI * sv.LightAge / lightTime)))) : 0;
                    k *= sv.LightFade;
                    if (k > 0)
                        set.Add(new PointLight(x, y, z, (float)(Db.Num(db, "lightColor.x") * k), (float)(Db.Num(db, "lightColor.y") * k), (float)(Db.Num(db, "lightColor.z") * k), Db.Num(db, "lightRadius", 3)));
                    break;
            }
        }
        return set;
    }

    /// <summary>Adds the render images of the visuals, the effects, the smoke, the decals and the weather to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    public void Build(FrameContext ctx)
    {
        ctx.Assets = Assets;
        ctx.Renderer = Renderer;
        ctx.Time = Time;
        ctx.Light = CollectLights();
        ctx.Lists.Clear();
        foreach (Visual v in Visuals.Values)
            (v as PlayerVisual)?.PrepareFrame(ctx);
        using (FramePerf.Scope("build-shapes"))
            foreach (Visual v in Visuals.Values)
            {
                ctx.Lists.CurrentContainer = ctx.ContainerOf != null && v.WorldBox is { } wb ? ctx.ContainerOf(wb) : -1;
                if (ctx.Lists.CurrentContainer == Client.World.InteriorRenderer.Culled)
                    continue;
                v.Build(ctx);
            }
        ctx.Lists.CurrentContainer = -1;
        using (FramePerf.Scope("build-effects"))
        {
            foreach (IEffect e in Effects)
                e.Build(ctx);
            Smoke.Build(ctx);
            Decals.Build(ctx);
            if (HasWeather && WeatherPrecipitation)
                Weather.Build(ctx, Time);
        }
    }
}
