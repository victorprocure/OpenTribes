// The visible state of a demo or a live connection: the visuals of the ghosts, the transient effects, the smoke pool and
// the decal ring (docs/shapes-render.md 2.1, 4, 5). Fed by the decoded packets of OpenTribes.Game.Client; steps in
// 32 ms frames like the client loop.
using OpenTribes.Platform.Gfx;
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.World;

namespace OpenTribes.Client.Scene;

public sealed class SceneModel
{
    public readonly ClientWorld World;
    public readonly Db Db;
    public readonly ShapeAssets Assets;
    public readonly ShapeRenderer Renderer;
    public readonly Terrain? Terrain;
    public readonly Dictionary<int, Visual> Visuals = new();
    public readonly List<IEffect> Effects = new();
    public SmokePool Smoke;
    public DecalRing Decals;
    public readonly WeatherEffect Weather = new();
    public bool HasWeather;
    public Random Rng = new(0x8377);
    public double Time;
    public Audio.SceneAudio? Audio;           // C7: sound starts, null without audio
    public bool AnimateWithTransitions;       // $pref::animateWithTransitions, default off (3.3)
    public double PlayerShapeDetail = 1;      // $pref::playerShapeDetail
    public LightSet BaseLight = LightSet.Default();
    public int TriggerCount, ContactTriggers, ExplosionsSpawned, FootprintsAdded, BulletHolesAdded;
    private bool mDecalsLoaded;

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

    public string TeamSkin(int team) => World.Players.Teams.TryGetValue(team, out ClientTeam? t) ? t.Skin : "";

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
                if (g.State.TryGetValue("intensity", out object? it)) Weather.Intensity = Math.Clamp(Db.ToDouble(it), 0, 1);
                if (g.State.TryGetValue("wind", out object? w)) { double[] wv = Db.Vec(w); Weather.WindX = wv[0]; Weather.WindY = wv[1]; }
                Weather.Suspended = g.State.TryGetValue("flagf0", out object? s) && Db.ToDouble(s) != 0;
                Weather.Rain = g.State.TryGetValue("flagf1", out object? r) && Db.ToDouble(r) != 0;
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

    // One client frame of dt seconds.
    public void Tick(double dt)
    {
        Time += dt;
        foreach (Visual v in Visuals.Values)
            v.Tick(dt);
        for (int i = Effects.Count - 1; i >= 0; --i)
        {
            Effects[i].Tick(dt);
            if (!Effects[i].Alive)
                Effects.RemoveAt(i);
        }
        Smoke.Tick(dt);
        Audio?.Frame(dt);
    }

    public void SpawnExplosion(int index, double x, double y, double z, double ax, double ay, double az)
    {
        ClientDataBlock? db = Db.Block(20, index);
        if (db == null)
            return;
        Audio?.OnExplosion(db, x, y, z);
        string name = Db.Str(db, "shapeName");
        if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        RenderShape? sh = Assets.Shape(name);
        if (sh == null)
            return;
        Effects.Add(new ExplosionEffect(sh, db, x, y, z, ax, ay, az, Rng));
        ++ExplosionsSpawned;
    }

    // 4.6 Footprint: the foot position offset +-0.25 along the player's yaw frame x axis, a ray against the terrain, a quad
    // aligned to the heading and the terrain normal. Only terrain gets footprints.
    public void AddFootprint(PlayerVisual p, int side)
    {
        if (Terrain == null)
            return;
        var (fx, fy, fz) = p.Foot(side);
        float h = 0;
        var n = new Vec3(0, 0, 1);
        if (!Terrain.Height((float)fx, (float)fy, ref h, ref n) || Math.Abs(fz - h) > 1.5)
            return;
        string hex = Db.Str(null, "") + (Db.Get(Db.Block(3, p.Ghost.State.TryGetValue("datablock", out object? d) ? (int)Db.ToDouble(d) : -1), "footPrints") as string ?? "");
        int left = 0, right = 1;
        if (hex.Length >= 16)
        {
            left = BitConverter.ToInt32(Convert.FromHexString(hex[..8]));
            right = BitConverter.ToInt32(Convert.FromHexString(hex.Substring(8, 8)));
        }
        int bmp = side > 0 ? right : left;
        if (bmp < 0 || bmp >= DecalRing.Table.Length)
            return;
        double yaw = p.YawAngle;
        Decals.Add(DecalRing.Quad(bmp, fx, fy, h, n.X, n.Y, n.Z, -Math.Sin(yaw), Math.Cos(yaw), 0));
        ++FootprintsAdded;
    }

    // 4.6 Bullet hole: the same quad at an impact point with a random rotation about the normal; only on the terrain.
    public void AddBulletHole(double x, double y, double z)
    {
        if (Terrain == null)
            return;
        float h = 0;
        var n = new Vec3(0, 0, 1);
        if (!Terrain.Height((float)x, (float)y, ref h, ref n) || Math.Abs(z - h) > 0.6)
            return;
        double a = Rng.NextDouble() * Math.PI * 2;
        Decals.Add(DecalRing.Quad(6, x, y, h, n.X, n.Y, n.Z, Math.Cos(a), Math.Sin(a), 0));
        ++BulletHolesAdded;
    }

    // The light set of a frame (2.5, 4.9): the scene light plus every dynamic light.
    public LightSet CollectLights()
    {
        var set = new LightSet
        {
            AmbR = BaseLight.AmbR, AmbG = BaseLight.AmbG, AmbB = BaseLight.AmbB, SunX = BaseLight.SunX, SunY = BaseLight.SunY, SunZ = BaseLight.SunZ,
            SunR = BaseLight.SunR, SunG = BaseLight.SunG, SunB = BaseLight.SunB,
        };
        foreach (IEffect e in Effects)
            if (e.Light is PointLight l)
                set.Add(l);
        foreach (Visual v in Visuals.Values)
        {
            switch (v)
            {
                case ProjectileVisual pv when pv.Light is PointLight pl: set.Add(pl); break;
                case BeamVisual bv when bv.Light is PointLight bl: set.Add(bl); break;
                case PlayerVisual plv: plv.CollectLights(set); break;
                case ShapeVisual sv when sv.Class is "Item" or "Mine" && sv.Position is var (x, y, z):
                    ClientDataBlock? db = Db.Block(sv.Class == "Item" ? 5 : 23, (long)Db.ToDouble(sv.Ghost.State.GetValueOrDefault("datablock"), -1));
                    int type = (int)Db.Num(db, "lightType");
                    double k = type == 1 ? 1 : type == 2 ? 0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(Time * 3)) : 0;
                    if (k > 0)
                        set.Add(new PointLight(x, y, z + 0.5, (float)(Db.Num(db, "lightColor.x") * k), (float)(Db.Num(db, "lightColor.y") * k), (float)(Db.Num(db, "lightColor.z") * k), Db.Num(db, "lightRadius", 3)));
                    break;
            }
        }
        return set;
    }

    public void Build(FrameContext ctx)
    {
        ctx.Assets = Assets;
        ctx.Renderer = Renderer;
        ctx.Time = Time;
        ctx.Light = CollectLights();
        ctx.Lists.Clear();
        using (FramePerf.Scope("build-shapes"))
            foreach (Visual v in Visuals.Values)
                v.Build(ctx);
        using (FramePerf.Scope("build-effects"))
        {
            foreach (IEffect e in Effects)
                e.Build(ctx);
            Smoke.Build(ctx);
            Decals.Build(ctx);
            if (HasWeather)
                Weather.Build(ctx, Time);
        }
    }
}
