// Port of src/game/projectile.hpp, src/game/projectile_internal.hpp and src/game/projectile.cpp: the Projectile base,
// RocketDumb, Bullet, Grenade, the line queries and the creation (docs/projectile-sim.md 0-3). The beam classes are
// ProjectileBeam.cs, the SeekingMissile ProjectileSeeker.cs, the ghost frame ProjectileGhost.cs. The Chop class of the
// files is X87.Chop; world::x87::f32 is X87.F32.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/projectile.hpp RayHit: ray hit with the data a projectile impact needs (the "info" of 0x00505f6c).
public struct RayHit
{
    public GameBase? Object;
    public uint Type;
    public Float3 Point, Normal;
    public bool Terrain;
    public float Distance;
    public Material? Material; // surface record +0x20 of terrain and interior hits

    public RayHit()
    {
        Object = null;
        Type = 0;
        Point = default;
        Normal = Float3.Of(0, 0, 1);
        Terrain = false;
        Distance = 0;
        Material = null;
    }
}

// src/game/projectile.hpp and projectile_internal.hpp namespace pj: helpers shared by the projectile source files.
public static class Pj
{
    // src/game/projectile.cpp pj::field.
    public static DataBlockManager.Field? Field(DataBlockManager.Block b, string name)
    {
        DataBlockManager.Field? f = b.Find(name);
        return f != null && f.Elems.Count != 0 ? f : null;
    }

    // src/game/projectile.cpp pj::real.
    public static float Real(DataBlockManager.Block b, string name, float def)
    {
        DataBlockManager.Field? f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].F[0];
        if (f.Def.DefaultText.Length != 0)
            return DataBlockManager.CtorFloat(f.Def.DefaultText);
        return def;
    }

    // src/game/projectile.cpp pj::integer.
    public static int Integer(DataBlockManager.Block b, string name, int def)
    {
        DataBlockManager.Field? f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].I;
        if (f.Def.DefaultText.Length != 0 && f.Def.DefaultText[0] >= '0' && f.Def.DefaultText[0] <= '9')
            return Numbers.BorlandAtoi(f.Def.DefaultText);
        return def;
    }

    // src/game/projectile.cpp pj::boolean.
    public static bool Boolean(DataBlockManager.Block b, string name, bool def)
    {
        DataBlockManager.Field? f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].I != 0;
        if (f.Def.DefaultText.Length != 0)
            return GameUtil.EqualsNoCase(f.Def.DefaultText, "true");
        return def;
    }

    // src/game/projectile.cpp pj::ftol: crt_ftol.
    public static uint Ftol(double x) =>
        unchecked((uint)(int)(double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0));

    // src/game/projectile_beam.cpp pj::worldBox: world box of a container 7 entry, false when the object has none.
    public static bool WorldBox(GameState game, GameBase? obj, Span<float> mn, Span<float> mx)
    {
        Container.Entry? e = obj != null ? game.World.Find(obj) : null;
        if (e == null)
            return false;
        for (int i = 0; i < 3; ++i)
        {
            mn[i] = e.Box[i];
            mx[i] = e.Box[i + 3];
        }
        return true;
    }

    // src/game/projectile_beam.cpp pj::findMethod: GameBase_findScriptMethod (0x0044864c): mapName, data name,
    // className, C++ class, plain. Projectile blocks have no className field.
    public static string FindMethod(GameState game, GameBase obj, string method)
    {
        if (game.Console == null)
            return "";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(obj.MapName))
            candidates.Add(obj.MapName + "::" + method);
        if (obj.DataBlockName != null)
            candidates.Add(obj.DataBlockName + "::" + method);
        candidates.Add(obj.ScriptClass() + "::" + method);
        candidates.Add(method);
        foreach (string fn in candidates)
            if (game.Console.HasFunction(fn))
                return fn;
        return "";
    }

    // src/game/projectile_beam.cpp pj::callMethod: the original builds the call as text with sprintf and evaluates it,
    // so numbers pass through the %g formatting and the script's number parser. `args` are the formatted tokens.
    public static string CallMethod(GameState game, string fn, IReadOnlyList<string> args)
    {
        if (game.Console == null)
            return "";
        string line = fn + "(";
        for (int i = 0; i < args.Count; ++i)
            line += (i != 0 ? ", " : "") + args[i];
        return game.Console.Evaluate(line + ");");
    }

    // src/game/projectile_beam.cpp pj::fmtG: %g.
    public static string FmtG(double v) => Numbers.FormatFloat(v);

    // src/game/projectile_ghost.cpp ftolBits.
    private static uint FtolBits(double x, uint bits)
    {
        long i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        return (uint)i & ((1u << (int)bits) - 1);
    }

    // src/game/projectile_ghost.cpp pj::writeVec.
    public static void WriteVec(BitStream bs, ReadOnlySpan<float> v) => bs.WriteF32s(v[..3]);

    // src/game/projectile_ghost.cpp pj::writeUnit: BitStream::writeFloat (0x00582fb0).
    public static void WriteUnit(BitStream bs, float v, uint bits) =>
        bs.WriteInt(FtolBits(Mul((double)(int)((1u << (int)bits) - 1), v), bits), bits);

    // src/game/projectile_ghost.cpp pj::writeNormal: writeSignedFloat (0x00583018) of x and y, then the sign of z.
    public static void WriteNormal(BitStream bs, ReadOnlySpan<float> n, uint bits)
    {
        for (int i = 0; i < 2; ++i)
        {
            double x = Mul(Mul(Add(n[i], 1.0f), 0.5f), (double)(int)((1u << (int)bits) - 1));
            bs.WriteInt(FtolBits(x, bits), bits);
        }
        bs.WriteFlag(n[2] < 0.0f);
    }

    // src/game/projectile_ghost.cpp pj::writeIndex: datablock index width (0x00402b60): the bit length of the group
    // count + 1.
    public static void WriteIndex(BitStream bs, int index, int groupCount)
    {
        uint n = (uint)groupCount + 1, bits = 1;
        while (bits < 32 && (n >> (int)bits) != 0)
            ++bits;
        bs.WriteInt((uint)index & (bits >= 32 ? 0xffffffffu : ((1u << (int)bits) - 1)), bits);
    }

    // src/game/projectile_ghost.cpp pj::explosionCount.
    public static int ExplosionCount(GameState game) => game.DataBlockCount(20);
}

// src/game/projectile.hpp class Projectile.
public abstract class Projectile : GameBase
{
    // src/game/projectile.hpp ProjectileGroup: group of ProjectileData blocks per class (projectile-sim.md 0).
    public const int kBulletGroup = 14, kGrenadeGroup = 15, kRocketGroup = 16, kLaserGroup = 17, kTargetLaserGroup = 24,
                     kSeekerGroup = 25, kLightningGroup = 28, kRepairGroup = 29;
    // src/game/projectile.hpp kProjectileType: object type of every projectile in container 7 (+0x48).
    public const uint kProjectileType = 0x100;

    protected const float kSlice = 0.0333333351f; // 0x3d088889

    // src/game/projectile.cpp deflectionRandom (0x00685ef8) and explosionRandom (0x006862f4).
    private static readonly R250 sDeflection = new(), sExplosion = new();

    // src/game/projectile.cpp resetProjectileRandoms: the two generators as a freshly started server has them (seed
    // 0x8377).
    public static void ResetProjectileRandoms()
    {
        sDeflection.Reseed(0x8377);
        sExplosion.Reseed(0x8377);
    }

    // src/game/projectile.hpp Projectile::Data: ProjectileData values (docs/datablocks.md 5.19 - 5.27).
    public sealed class PData
    {
        public int ExplosionTag, ExpRandCycle, DamageClass, DamageType, OwnerGraceMS = 1000;
        public bool CollideWithOwner;
        public float CollisionRadius, Mass = 1, DamageValue, ExplosionRadius, Kick, AimDeflection;
        public float MuzzleVelocity, TerminalVelocity, Acceleration, TotalTime = 10000, LiveTime = 10000;
        public float InheritedScale, Elasticity = 0.5f;
    }

    protected readonly GameState mGame;
    protected readonly string mCls;
    protected readonly PData mData = new();
    protected readonly int mGroup;
    protected DataBlockManager.Block? mBlock;
    protected int mShooter;                  // +0x210
    protected int mInitShooter = -1;         // +0x328 until onAdd
    protected int mShooterObject;            // +0x32c: id of the shooter found in onAdd
    protected int mOwner, mOwnerJoin = -1;   // +0x320, +0x324
    protected readonly float[] mInherited = new float[3]; // +0x330
    protected readonly float[] mLaunch = new float[3];    // +0x33c
    protected int mExclude;                  // +0x1f8
    protected int mGrace;                    // +0x2dc
    protected uint mLast;                    // +0x304
    protected uint mLastSend;                // +0x308
    protected bool mDying, mLive = true;     // +0x30c, +0x314
    protected uint mDeleteTime, mLiveEnd, mTotalEnd; // +0x310, +0x318, +0x31c
    protected bool mExploded;                // +0x439
    protected readonly float[] mExpPos = new float[3], mExpNormal = { 0, 0, 1 }; // +0x43c, +0x448
    protected int mExpIndex;                 // +0x454
    protected readonly float[] mShapeBox = new float[6];
    protected bool mInContainer;
    protected bool mGravity;                 // +0x16c bit 0: the gravity force is integrated

    private static ClassContext OwnContext(GameState game) => new() { Game = game, Resources = game.Resources };

    // src/game/projectile.cpp Projectile::Projectile.
    protected Projectile(GameState game, string cls, int group) : base(cls, Kind.Marker, group, Array.Empty<PersistDef>(), OwnContext(game))
    {
        mGame = game;
        mCls = cls;
        mGroup = group;
    }

    private static readonly string[] kNames =
    {
        "Bullet", "Grenade", "RocketDumb", "LaserProjectile", "TargetLaser", "SeekingMissile", "Lightning", "RepairEffect",
    };

    // src/game/projectile.cpp Projectile::from.
    public static Projectile? From(SimObject? obj)
    {
        if (obj == null)
            return null;
        foreach (string n in kNames)
            if (obj.ClassName() == n)
                return obj as Projectile;
        return null;
    }

    public PData Data() => mData;
    public bool Exploded() => mExploded;

    // src/game/projectile.cpp Projectile::shooter: the shooter object (delete-notified in the original), or null.
    public GameBase? Shooter()
    {
        SimManager? m = Manager();
        SimObject? o = m != null && mShooterObject != 0 ? m.FindById(mShooterObject) : null;
        return o as GameBase;
    }

    // src/game/projectile.cpp Projectile::ownerClientId: owner client id (0x0045af24): the `source` of every damage
    // call, 0 when unknown.
    public int OwnerClientId()
    {
        if (mOwner == 0)
            return 0;
        ClientRecord? r = mGame.Players.FindHuman(mOwner);
        return r != null && r.JoinOrder == mOwnerJoin ? mOwner : 0;
    }

    // src/game/projectile.cpp Projectile::init: vtable +0x1b4: transform, inherited velocity, shooter id.
    public virtual void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        Transform = t;
        mShooter = mInitShooter = shooterId;
        mInherited[0] = velocity[0];
        mInherited[1] = velocity[1];
        mInherited[2] = velocity[2];
    }

    // src/game/projectile.hpp Projectile::setEnergy: vtable +0x1b8: energy used and fraction (lasers).
    public virtual void SetShotEnergy(float used, float fraction) { }
    // src/game/projectile.hpp Projectile::shutOff / updateTransform: vtable +0x1c0 / +0x1c4 (sustained weapons).
    public virtual void ShutOff() { }
    public virtual void UpdateTransform(in Transform t) { }
    // src/game/projectile.hpp: vtable +0x1c8 / +0x1cc / +0x1d0 (seekers).
    public virtual bool WantsTarget() => false;
    public virtual float TargetRange() => 0.0f;
    public virtual void SetTarget(GameBase? t) { }
    public virtual int SeekerTarget() => 0; // the object id a seeker steers to, 0 none
    public virtual bool IsSustained() => false; // vtable +0x1bc
    // vtable +0x118 of TargetLaser: the end point and team of a hit beam (set 725 users).
    public virtual bool BeamTargetPoint(Span<float> output, out int team)
    {
        team = 0;
        return false;
    }

    // Ghost packers (docs/projectile-sim.md 5), called by ProjectileGhost.
    public abstract void PackInitial(BitStream bs, GhostContext ctx);
    public virtual void PackExplode(BitStream bs) { }
    public virtual void PackUpdate(BitStream bs, uint mask, GhostContext ctx) { }

    // Class hooks of onAdd and the time limits (vtable +0x188, +0x18c).
    protected virtual void ClassOnAdd() { }
    protected virtual void ReadData(DataBlockManager.Block b) { }
    protected virtual void OnLiveTimePassed() => mLive = false;
    protected virtual void OnTotalTimePassed() { }

    // src/game/projectile.cpp slab: T7.1 slab test (0x005ebe80): t of the entry, or 0 when the start is inside.
    private static bool Slab(ReadOnlySpan<float> s, ReadOnlySpan<float> e, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx, out float t,
                             out int axisOut, out bool neg)
    {
        for (int a = 0; a < 3; ++a)
        {
            int b = (a + 1) % 3, c = (a + 2) % 3;
            float num, den;
            bool below;
            if (s[a] < mn[a])
            {
                if (e[a] < mn[a])
                    continue;
                num = X87.F32(Sub(mn[a], s[a]));
                den = X87.F32(Sub(e[a], s[a]));
                below = true;
            }
            else if (s[a] > mx[a])
            {
                if (e[a] > mx[a])
                    continue;
                num = X87.F32(Sub(s[a], mx[a]));
                den = X87.F32(Sub(s[a], e[a]));
                below = false;
            }
            else
            {
                continue;
            }
            if (Mul(Sub(mx[b], s[b]), den) > Mul(Sub(e[b], s[b]), num) && Mul(Sub(mn[b], s[b]), den) < Mul(Sub(e[b], s[b]), num) &&
                Mul(Sub(mx[c], s[c]), den) > Mul(Sub(e[c], s[c]), num) && Mul(Sub(mn[c], s[c]), den) < Mul(Sub(e[c], s[c]), num))
            {
                t = X87.F32(Div(num, den));
                axisOut = a;
                neg = below;
                return true;
            }
        }
        for (int a = 0; a < 3; ++a)
            if (s[a] < mn[a] || s[a] > mx[a])
            {
                t = 0;
                axisOut = -1;
                neg = false;
                return false;
            }
        t = 0.0f;
        axisOut = -1;
        neg = false;
        return true;
    }

    // src/game/projectile.cpp distanceOf.
    private static float DistanceOf(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double d0 = Sub(b[0], a[0]), d1 = Sub(b[1], a[1]), d2 = Sub(b[2], a[2]);
        return X87.F32(Sqrt(Add(Add(Mul(d0, d0), Mul(d1, d1)), Mul(d2, d2))));
    }

    // src/game/projectile.cpp shapeNameOf.
    private static string? ShapeNameOf(GameState game, GameBase gb)
    {
        if (game.Sim == null || gb.DataBlockName == null)
            return null;
        DataBlockManager.Block? b = game.Sim.DataBlocks().Find(gb.DataBlockName);
        DataBlockManager.Field? f = b != null ? Pj.Field(b, "shapeFile") : null;
        return f != null && !string.IsNullOrEmpty(f.Elems[0].Str) ? f.Elems[0].Str : null;
    }

    // src/game/projectile.cpp integrate: SimMovement_integrate (0x00505460). `drag` is the container drag (+0x124) of a
    // class with movement flag 0x10 (Grenade): v -= v * ((mediumDrag 1.0 * drag) * dt) after the forces (0x00505144).
    protected static void Integrate(Projectile p, float dt, float[]? acc, float[] target, float drag = 0)
    {
        float[] old = { p.Velocity[0], p.Velocity[1], p.Velocity[2] };
        if (acc != null)
            for (int i = 0; i < 3; ++i)
                p.Velocity[i] = X87.F32(Add(Mul(acc[i], dt), p.Velocity[i]));
        if (drag != 0)
        {
            float s = X87.F32(Mul(X87.F32(Mul(1.0f, drag)), dt));
            for (int i = 0; i < 3; ++i)
                p.Velocity[i] = X87.F32(Sub(p.Velocity[i], Mul(p.Velocity[i], s)));
        }
        float h = X87.F32(Mul(dt, 0.5f));
        for (int i = 0; i < 3; ++i)
            target[i] = X87.F32(Add(Mul(Add(old[i], p.Velocity[i]), h), p.Transform.P[i]));
    }

    // src/game/projectile.cpp castWorld: nearest line hit of container 7 (docs/collision.md 9): terrain, interiors with
    // their own line tests and, for shapes and players, the collision boxes (the face test of T7 for boxes). `exclude`
    // is skipped.
    public static bool CastWorld(GameState game, ReadOnlySpan<float> s, ReadOnlySpan<float> e, uint mask, SimObject? exclude,
                                 ref RayHit hit)
    {
        using var chop = X87.Chop();
        object? obj = null;
        Vec3 hp = default, n = default;
        bool found = false;
        float best = 3.0e38f;
        var before = new Vec3(e[0], e[1], e[2]);
        if (game.World.CastRay(new Vec3(s[0], s[1], s[2]), new Vec3(e[0], e[1], e[2]), mask, exclude, ref obj, ref hp, ref n,
                               ref before) && obj != null)
        {
            hit.Object = obj as GameBase;
            hit.Point = Float3.Of(hp.X, hp.Y, hp.Z);
            hit.Normal = Float3.Of(n.X, n.Y, n.Z);
            Container.Entry? en = game.World.Find(obj);
            hit.Type = en != null ? en.Type : 0;
            hit.Terrain = (hit.Type & ObjectType.kTerrainType) != 0;
            hit.Material = en?.HitMaterial?.Value;
            best = hit.Distance = DistanceOf(s, hit.Point);
            found = true;
        }
        if ((mask & ~0x6u) != 0)
        {
            float[] box =
            {
                Math.Min(s[0], e[0]), Math.Min(s[1], e[1]), Math.Min(s[2], e[2]), Math.Max(s[0], e[0]), Math.Max(s[1], e[1]),
                Math.Max(s[2], e[2]),
            };
            // SimContainer_castRay (0x004fa030, docs/interiors.md 6.3 step 4): a hit shortens the segment, and every later
            // candidate is tested against (s, best hit) and computes its hit point from that segment. The terrain and the
            // shapes of the world cast above come first in the candidate order, so the Player and box tests below start
            // from their hit.
            float[] cur = { e[0], e[1], e[2] };
            // A Player the world cast returned is tested again below; it keeps the segment it had in the cast (what the
            // candidates ahead of it left), not the one ending at its own hit.
            if (found)
            {
                if (Player.From(hit.Object) != null)
                {
                    cur[0] = before.X;
                    cur[1] = before.Y;
                    cur[2] = before.Z;
                }
                else
                {
                    cur[0] = hit.Point[0];
                    cur[1] = hit.Point[1];
                    cur[2] = hit.Point[2];
                }
            }
            foreach (object? o in game.World.FindObjects(box, mask & ~0x6u, exclude))
            {
                var gb = o as GameBase;
                Container.Entry? en = gb != null ? game.World.Find(o) : null;
                // A Player entry carries a cast function too (line test against its own box).
                if (gb == null || en == null || (en.Cast != null && Player.From(gb) == null))
                    continue;
                float t = 0;
                float[] hpos = new float[3], hn = { 0, 0, 0 };
                bool ok = false;
                bool direct = false;
                if (Player.From(gb) != null)
                {
                    // The Player's own line test (its container entry), record normal (0, 0, 1) (0x0043599c).
                    Vec3 ph = default, pn = default;
                    if (en.Cast != null && en.Cast(new Vec3(s[0], s[1], s[2]), new Vec3(cur[0], cur[1], cur[2]), ref ph, ref pn))
                    {
                        ok = direct = true;
                        hpos[0] = ph.X;
                        hpos[1] = ph.Y;
                        hpos[2] = ph.Z;
                        hn[2] = 1.0f;
                    }
                }
                else if (game.Resources != null)
                {
                    string? shape = ShapeNameOf(game, gb);
                    if (shape == null)
                        continue;
                    var local = new List<Float6>();
                    if (Shape.ShapeCollisionBoxes(game.Resources, shape) is List<Float6> boxes && boxes.Count != 0)
                    {
                        local.AddRange(boxes);
                    }
                    else
                    {
                        float[] sb = new float[6];
                        if (!Shape.ShapeBox(game.Resources, shape, sb))
                            continue;
                        local.Add(Float6.Of(sb));
                    }
                    Mat m = gb.WorldMat();
                    Vec3 ls = m.ToLocal(new Vec3(s[0], s[1], s[2])), le = m.ToLocal(new Vec3(cur[0], cur[1], cur[2]));
                    float[] sl = { ls.X, ls.Y, ls.Z }, el = { le.X, le.Y, le.Z };
                    float bestT = 1.1f;
                    foreach (Float6 b in local)
                    {
                        float[] mn = { b[0], b[1], b[2] }, mx = { b[3], b[4], b[5] };
                        if (Slab(sl, el, mn, mx, out float bt, out int axis, out bool neg) && bt < bestT)
                        {
                            bestT = bt;
                            ok = true;
                            float[] ln = { 0, 0, 0 };
                            if (axis >= 0)
                                ln[axis] = neg ? -1.0f : 1.0f;
                            Vec3 wn = m.ToWorldVector(new Vec3(ln[0], ln[1], ln[2]));
                            hn[0] = wn.X;
                            hn[1] = wn.Y;
                            hn[2] = wn.Z;
                        }
                    }
                    t = bestT;
                }
                if (!ok)
                    continue;
                for (int i = 0; i < 3 && !direct; ++i)
                {
                    float d = X87.F32(Sub(cur[i], s[i]));
                    d = X87.F32(Mul(t, d));
                    hpos[i] = X87.F32(Add(s[i], d));
                }
                float dist = DistanceOf(s, hpos);
                if (!found || dist < best)
                {
                    found = true;
                    best = dist;
                    cur[0] = hpos[0];
                    cur[1] = hpos[1];
                    cur[2] = hpos[2];
                    hit.Object = gb;
                    hit.Type = en.Type;
                    hit.Terrain = false;
                    hit.Distance = dist;
                    hit.Point = Float3.Of(hpos);
                    hit.Normal = Float3.Of(hn);
                }
            }
        }
        return found;
    }

    // src/game/projectile.cpp Projectile::placeInContainer: container 7 entry of the projectile at its current transform
    // (shape box).
    protected void PlaceInContainer()
    {
        if (mInContainer)
            return;
        string shape = "";
        if (mBlock != null)
            foreach (string f in new[] { "bulletShapeName", "shapeName" })
            {
                DataBlockManager.Field? fl = Pj.Field(mBlock, f);
                if (fl != null && !string.IsNullOrEmpty(fl.Elems[0].Str))
                {
                    shape = fl.Elems[0].Str!;
                    break;
                }
            }
        float[] local = { 0, 0, 0, 0, 0, 0 };
        if (shape.Length != 0 && mGame.Resources != null)
            Shape.ShapeBox(mGame.Resources, shape, local);
        Array.Copy(local, mShapeBox, 6);
        Span<float> box = stackalloc float[6];
        Shape.RotatedWorldBox(local, WorldMat(), box);
        var en = new Container.Entry(this, kProjectileType, Float6.Of(box), false, null);
        mGame.World.Add(en);
        mInContainer = true;
        Span<float> g = stackalloc float[3];
        float d = 0;
        mGame.EnteredForces(this, g, ref d); // the gravity force of the terrain container (FC2)
    }

    // src/game/projectile.cpp Projectile::removeFromContainer.
    protected void RemoveFromContainer()
    {
        if (mInContainer)
            mGame.World.Remove(this);
        mInContainer = false;
    }

    // src/game/projectile.cpp Projectile::moveTo: SimObject_setTransform with a new position.
    protected void MoveTo(ReadOnlySpan<float> p)
    {
        Transform.P[0] = p[0];
        Transform.P[1] = p[1];
        Transform.P[2] = p[2];
        Transform.Flags |= 2;
        if (mInContainer)
        {
            Span<float> box = stackalloc float[6];
            Shape.RotatedWorldBox(mShapeBox, WorldMat(), box);
            mGame.World.SetBox(this, box);
            Span<float> g = stackalloc float[3];
            float d = 0;
            mGame.EnteredForces(this, g, ref d);
        }
    }

    // src/game/projectile.cpp Projectile::graceTick: 0x0045a718: the owner grace counter.
    protected void GraceTick(int ms)
    {
        if (mGrace != 0)
        {
            mGrace -= ms;
            if (mGrace <= 0)
                mExclude = 0;
        }
    }

    // src/game/projectile.cpp Projectile::excludeObject: query exclusion by the owner rule (2.1 step 3).
    protected SimObject? ExcludeObject()
    {
        SimManager? m = Manager();
        if (m == null || mShooter == 0)
            return null;
        if (!mData.CollideWithOwner || mGrace > 0)
            return m.FindById(mShooter);
        return null;
    }

    // src/game/projectile.cpp Projectile::onAdd.
    public override bool OnAdd()
    {
        // GameBase datablock bind without the ShapeBase part (resolveDataBlock reads shape fields).
        mBlock = DataBlockName != null && mGame.Sim != null ? mGame.Sim.DataBlocks().Find(DataBlockName) : null;
        if (mBlock == null || mBlock.Group != mGroup)
            return false;
        DataBlockIndex = mBlock.Index;
        using var chop = X87.Chop();
        DataBlockManager.Block b = mBlock;
        PData d = mData;
        d.ExplosionTag = Pj.Integer(b, "explosionTag", 0);
        d.ExpRandCycle = Pj.Integer(b, "expRandCycle", 0);
        d.DamageClass = Pj.Integer(b, "damageClass", 0);
        d.DamageType = Pj.Integer(b, "damageType", 0);
        d.OwnerGraceMS = Pj.Integer(b, "ownerGraceMS", 1000);
        d.CollideWithOwner = Pj.Boolean(b, "collideWithOwner", false);
        d.CollisionRadius = Pj.Real(b, "collisionRadius", 0);
        d.Mass = Pj.Real(b, "mass", 1);
        d.DamageValue = Pj.Real(b, "damageValue", 0);
        d.ExplosionRadius = Pj.Real(b, "explosionRadius", 0);
        d.Kick = Pj.Real(b, "kickBackStrength", 0);
        d.AimDeflection = Pj.Real(b, "aimDeflection", 0);
        d.MuzzleVelocity = Pj.Real(b, "muzzleVelocity", Pj.Real(b, "maxLevelFlightDist", 0));
        d.TerminalVelocity = Pj.Real(b, "terminalVelocity", 0);
        d.Acceleration = Pj.Real(b, "acceleration", 0);
        d.TotalTime = Pj.Real(b, "totalTime", 10000);
        d.LiveTime = Pj.Real(b, "liveTime", 10000);
        d.InheritedScale = Pj.Real(b, "inheritedVelocityScale", 0);
        d.Elasticity = Pj.Real(b, "elasticity", 0.5f);
        ReadData(b);
        mGame.JoinSet(this, 719);
        uint now = mGame.Sim != null ? mGame.Sim.TickMs() : 0;
        // Shooter and owner client (1.4 step 2).
        SimManager? m = Manager();
        SimObject? so = m != null && mInitShooter > 0 ? m.FindById(mInitShooter) : null;
        if (so is GameBase sg && sg.IsShapeBase())
        {
            mShooterObject = sg.Id();
            mOwner = sg.ControlClient != 0 ? sg.ControlClient : sg.LastOwnerClient;
            ClientRecord? r = mOwner != 0 ? mGame.Players.FindHuman(mOwner) : null;
            mOwnerJoin = r != null ? r.JoinOrder : -1;
        }
        mInitShooter = -1;
        // Muzzle velocity along the forward axis plus the inherited part, then the deflection.
        float[] fwd = { Transform.M[3], Transform.M[4], Transform.M[5] };
        for (int i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Add(X87.F32(Mul(fwd[i], d.MuzzleVelocity)), X87.F32(Mul(mInherited[i], d.InheritedScale))));
        if (d.AimDeflection != 0.0f)
        {
            // Projectile_applyDeflection (0x00459fa4): three draws, each angle one extended chain stored once:
            // (((n * 2^-32 (f32) - 0.5f) * 2.0f) * pi (f64)) * aimDeflection with n the 32-bit draw loaded as an integer
            // (fild qword). Then Mat3_fromEulerWrap (0x005ed000, which is m_mat3_setEuler) and m_mul_vector on the velocity.
            float[] a = new float[3];
            for (int k = 0; k < 3; ++k)
            {
                double n = sDeflection.Next();
                a[k] = X87.F32(Mul(Mul(Mul(Sub(Mul(n, (double)2.3283064365386963e-10f), 0.5f), 2.0f), 3.141592653589793), d.AimDeflection));
            }
            var rot = new Transform();
            MissionTransform.EulerToMatrix(a, ref rot);
            float[] output = new float[3];
            X87.MulVector(Velocity, rot.M, output);
            output.CopyTo(Velocity, 0);
        }
        for (int i = 0; i < 3; ++i)
            mLaunch[i] = X87.F32(Mul(fwd[i], d.TerminalVelocity));
        mExclude = (!d.CollideWithOwner || d.OwnerGraceMS > 0) ? mShooter : 0;
        mLast = now;
        mGrace = unchecked((int)((uint)d.OwnerGraceMS + now));
        mTotalEnd = unchecked(Pj.Ftol(Mul(d.TotalTime, 1000.0f)) + now);
        mLiveEnd = unchecked(Pj.Ftol(Mul(d.LiveTime, 1000.0f)) + now);
        PlaceInContainer();
        ClassOnAdd();
        return true;
    }

    // src/game/projectile.cpp Projectile::onRemove.
    public override void OnRemove()
    {
        if (mInContainer)
            mGame.World.Remove(this);
        mInContainer = false;
    }

    // src/game/projectile.cpp Projectile::registerWith: registers with the manager and the MissionCleanup group. The
    // object is dropped on failure.
    public bool RegisterWith(SimManager m)
    {
        if (!m.RegisterObject(this))
            return false;
        if (m.Find("MissionCleanup")?.AsSet() is SimSet set)
            set.AddObject(this);
        return true;
    }

    // src/game/projectile.cpp Projectile::explosionIndex.
    protected int ExplosionIndex()
    {
        if (mData.ExpRandCycle <= 1)
            return mData.ExplosionTag;
        float u = X87.F32(Mul(Store(sExplosion.Next()), 2.3283064365386963e-10));
        return mData.ExplosionTag + unchecked((int)Pj.Ftol(X87.F32(Mul(u, mData.ExpRandCycle))));
    }

    // src/game/projectile.cpp Projectile::setExplosion.
    protected void SetExplosion(ReadOnlySpan<float> pos, ReadOnlySpan<float> normal)
    {
        pos[..3].CopyTo(mExpPos);
        normal[..3].CopyTo(mExpNormal);
        mExpIndex = ExplosionIndex();
        mExploded = true;
    }

    // src/game/projectile.cpp Projectile::explodeAtImpact: 0x00459eb4: the hit object's type decides whether the client
    // is told.
    protected void ExplodeAtImpact(in RayHit hit)
    {
        if ((hit.Type & 0x44a00026u) != 0)
            return;
        SetExplosion(hit.Point, hit.Normal);
        SetMaskBits(0x04);
    }

    private static readonly float[] kUp = { 0, 0, 1 };

    // src/game/projectile.cpp Projectile::explodeAtPosition.
    protected void ExplodeAtPosition(ReadOnlySpan<float> pos)
    {
        SetExplosion(pos, kUp);
        SetMaskBits(0x04);
    }

    // src/game/projectile.cpp Projectile::directDamageValue: 0x00459d90.
    protected virtual float DirectDamageValue(uint now) => mData.DamageValue;

    // src/game/projectile.cpp Projectile::directDamage.
    protected void DirectDamage(in RayHit hit, uint now)
    {
        if (hit.Object == null || (hit.Type & 0x24c00080u) == 0)
            return;
        float[] zero = { 0, 0, 0 };
        hit.Object.ApplyDamage(mData.DamageType, DirectDamageValue(now), hit.Point, Velocity, zero, OwnerClientId());
    }

    // src/game/projectile.cpp Projectile::radiusDamageAt: 0x00459e58 radius damage.
    protected void RadiusDamageAt(ReadOnlySpan<float> pos)
    {
        if (!(mData.ExplosionRadius > 0.0f))
            return;
        DamageCommands.ApplyRadiusDamage(mGame, mData.DamageType, pos, Velocity, mData.ExplosionRadius, mData.DamageValue,
                                         mData.Kick, OwnerClientId());
    }

    // src/game/projectile.cpp Projectile::impact: impact handling shared by the classes (damageClass switch of
    // 0x0045aa38).
    protected void Impact(in RayHit hit, uint now)
    {
        if (mData.DamageClass == 0)
        {
            DirectDamage(hit, now);
            ExplodeAtImpact(hit);
        }
        else if (mData.DamageClass == 1)
        {
            float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
            RadiusDamageAt(pos);
            ExplodeAtPosition(pos);
        }
        mDying = true;
        mDeleteTime = unchecked(now + 500);
    }

    // src/game/projectile.cpp Projectile::advance: the shared step (2.1, 0x0045a73c).
    protected bool Advance(uint now, bool variableSlice, ref RayHit hit)
    {
        using var chop = X87.Chop();
        int ms;
        float dt = kSlice;
        if (variableSlice)
        {
            int d = unchecked((int)(now - mLast));
            if (d <= 0)
                return false;
            dt = X87.F32(Mul(d, 0.001f));
        }
        ms = unchecked((int)Pj.Ftol(Mul(dt, 1000.0f)));
        if (variableSlice)
            ms = unchecked((int)(now - mLast));
        while (unchecked(mLast + (uint)ms) <= now)
        {
            float[] target = new float[3];
            float[] grav = { 0, 0, 0 };
            float drag = 0;
            bool inAir = mGravity && mGame.EnteredForces(this, grav, ref drag);
            Integrate(this, dt, inAir ? grav : null, target);
            GraceTick(ms);
            if (CastWorld(mGame, Transform.P, target, 0x24e000a6u, ExcludeObject(), ref hit))
                return true;
            MoveTo(target);
            mLast = unchecked(mLast + (uint)ms);
            if (variableSlice)
                break;
        }
        return false;
    }

    // src/game/projectile.cpp Projectile::baseTick: 0x0045aa38.
    protected void BaseTick(uint now)
    {
        if (mDying)
        {
            if (now >= mDeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        if (mLive && now > mLiveEnd)
        {
            OnLiveTimePassed();
        }
        else if (now > mTotalEnd)
        {
            OnTotalTimePassed();
            mDying = true;
            mDeleteTime = unchecked(now + 500);
            return;
        }
        var hit = new RayHit();
        if (Advance(now, false, ref hit))
            Impact(hit, now);
    }

    // src/game/projectile.cpp Projectile::create: Projectile_createFromType (0x0046f46c): null for a group without a
    // class or a missing block.
    public static Projectile? Create(GameState game, int group, int index)
    {
        if (game.Sim == null)
            return null;
        DataBlockManager.Block? b = game.Sim.DataBlocks().At(group, index);
        if (b == null)
            return null;
        Projectile? p = group switch
        {
            kBulletGroup => new Bullet(game),
            kGrenadeGroup => new Grenade(game),
            kRocketGroup => new RocketDumb(game),
            _ => ProjectileBeam.CreateBeamProjectile(game, group),
        };
        if (p != null)
            p.DataBlockName = b.Name;
        return p;
    }
}

// ---- RocketDumb (2.3) ----

// src/game/projectile.cpp class RocketDumb.
internal sealed class RocketDumb : Projectile
{
    private bool mForce, mExpired;

    public RocketDumb(GameState g) : base(g, "RocketDumb", kRocketGroup) { }

    protected override void ClassOnAdd()
    {
        mLastSend = mLast;
        mForce = true;
    }

    public override void ServerTick(uint now)
    {
        if (mForce && Math.Abs((double)mLaunch[0]) <= Math.Abs((double)Velocity[0]) &&
            Math.Abs((double)mLaunch[1]) <= Math.Abs((double)Velocity[1]) && Math.Abs((double)mLaunch[2]) <= Math.Abs((double)Velocity[2]))
        {
            mForce = false;
            mLaunch.CopyTo(Velocity, 0);
        }
        BaseTick(now);
    }

    protected override void OnTotalTimePassed()
    {
        // 0x00462134: expired, mask 0x04 (no explosion position).
        mExploded = true;
        mExpired = true;
        SetMaskBits(0x04);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, Transform.P);
        Pj.WriteVec(bs, Velocity);
    }

    public override void PackExplode(BitStream bs)
    {
        if (!bs.WriteFlag(mExpired))
        {
            Pj.WriteVec(bs, mExpPos);
            Pj.WriteNormal(bs, mExpNormal, 5);
            Pj.WriteIndex(bs, mExpIndex, Pj.ExplosionCount(mGame));
        }
    }

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x01) != 0))
            Pj.WriteVec(bs, Transform.P);
        bs.WriteFlag(mLive);
    }
}

// ---- Bullet (2.4) ----

// src/game/projectile.cpp class Bullet.
internal sealed class Bullet : Projectile
{
    private readonly float[] mStart = new float[3], mVel = new float[3];
    private float mSpeed = 1;
    private uint mEndTime;
    private bool mHasStatic;
    private RayHit mStatic = new();

    public Bullet(GameState g) : base(g, "Bullet", kBulletGroup) { }

    private const double kInv = 1.0 / 1048575.0;

    protected override void ClassOnAdd()
    {
        // The velocity is replaced by what the ghost decodes from the initial data.
        float s = X87.F32(X87.Len(Velocity));
        float[] d = new float[3];
        for (int i = 0; i < 3; ++i)
            d[i] = X87.F32(Div(Velocity[i], s));
        uint qx = Pj.Ftol(Mul(Mul(Add(d[0], 1.0f), 0.5f), 1048575.0f));
        uint qy = Pj.Ftol(Mul(Mul(Add(d[1], 1.0f), 0.5f), 1048575.0f));
        bool neg = d[2] < 0.0f;
        float x = X87.F32(Sub(Mul(kInv, Mul(2.0, qx)), 1.0f)), y = X87.F32(Sub(Mul(kInv, Mul(2.0, qy)), 1.0f));
        float z2 = X87.F32(Sub(Sub(1.0, Mul(x, x)), Mul(y, y)));
        float z = z2 < 0.0f ? 0.0f : X87.F32(Sqrt(z2));
        if (neg)
            z = -z;
        float s2 = X87.F32(Mul(Pj.Ftol(Mul(s, 16.0f)), 0.0625f));
        float[] dir = { x, y, z };
        for (int i = 0; i < 3; ++i)
            mVel[i] = Velocity[i] = X87.F32(Mul(s2, dir[i]));
        mStart[0] = Transform.P[0];
        mStart[1] = Transform.P[1];
        mStart[2] = Transform.P[2];
        mSpeed = X87.F32(X87.Len(mVel));
        // The static world is tested once (0x0045dfa0).
        uint now = mGame.Sim!.TickMs();
        float[] end = new float[3];
        for (int i = 0; i < 3; ++i)
            end[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(mVel[i], mData.TotalTime))));
        SimManager? m = Manager();
        SimObject? ex = m != null && mShooter > 0 ? m.FindById(mShooter) : null;
        var h = new RayHit();
        if (CastWorld(mGame, Transform.P, end, 0x6u, ex, ref h))
        {
            for (int i = 0; i < 3; ++i)
                h.Point[i] = X87.F32(Add(h.Point[i], X87.F32(Mul(h.Normal[i], 0.1f))));
            mStatic = h;
            mHasStatic = true;
            float[] dd = { X87.F32(Sub(h.Point[0], Transform.P[0])), X87.F32(Sub(h.Point[1], Transform.P[1])), X87.F32(Sub(h.Point[2], Transform.P[2])) };
            mEndTime = unchecked(Pj.Ftol(Mul(X87.F32(Div(X87.F32(X87.Len(dd)), X87.Len(mVel))), 1000.0f)) + now);
        }
        else
        {
            mHasStatic = false;
            mEndTime = unchecked(Pj.Ftol(Mul(mData.TotalTime, 1000.0f)) + now + 10000);
        }
    }

    protected override float DirectDamageValue(uint now)
    {
        // 0x0045bbe0: the damage fades between liveTime and totalTime.
        if (now > mLiveEnd)
            return X87.F32(Mul(X87.F32(Sub(1.0, Div(unchecked((int)(now - mLiveEnd)), unchecked((int)(mTotalEnd - mLiveEnd))))),
                               mData.DamageValue));
        return mData.DamageValue;
    }

    public override void ServerTick(uint now)
    {
        using var chop = X87.Chop();
        if (mDying)
        {
            if (now >= mDeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        if (mLive && now > mLiveEnd)
        {
            mLive = false;
        }
        else if (now > mTotalEnd)
        {
            mDying = true;
            mDeleteTime = unchecked(now + 500);
            return;
        }
        int ms = unchecked((int)(now - mLast));
        if (ms == 0)
            return;
        mLast = now;
        if (now > mEndTime)
            ms -= unchecked((int)(now - mEndTime));
        float[] end = new float[3];
        float dt = X87.F32(Mul(ms, 0.001f));
        for (int i = 0; i < 3; ++i)
            end[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(mVel[i], dt))));
        GraceTick(ms);
        var hit = new RayHit();
        if (CastWorld(mGame, Transform.P, end, 0x24e000a6u & ~0x6u, ExcludeObject(), ref hit))
        {
            MoveTo(hit.Point);
            HitImpact(hit, now, false);
            return;
        }
        if (now >= mEndTime)
        {
            if (mHasStatic)
            {
                HitImpact(mStatic, now, true);
            }
            else
            {
                mDying = true;
                mDeleteTime = unchecked(now + 500);
            }
            return;
        }
        MoveTo(end);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, mStart);
        float[] d = { X87.F32(Sub(Transform.P[0], mStart[0])), X87.F32(Sub(Transform.P[1], mStart[1])), X87.F32(Sub(Transform.P[2], mStart[2])) };
        bs.WriteInt(Pj.Ftol(Mul(X87.F32(Div(X87.F32(X87.Len(d)), mSpeed)), 1000.0f)) & 0x7fff, 15);
        float[] u = new float[3];
        for (int i = 0; i < 3; ++i)
            u[i] = X87.F32(Div(mVel[i], mSpeed));
        Pj.WriteNormal(bs, u, 20);
        bs.WriteInt(Pj.Ftol(Mul(mSpeed, 16.0f)) & 0x3fff, 14);
    }

    public override void PackExplode(BitStream bs)
    {
        float[] d = { X87.F32(Sub(mExpPos[0], mStart[0])), X87.F32(Sub(mExpPos[1], mStart[1])), X87.F32(Sub(mExpPos[2], mStart[2])) };
        bs.WriteInt(Pj.Ftol(Mul(Div(X87.F32(X87.Len(d)), mSpeed), 1000.0f)) & 0x7fff, 15);
        Pj.WriteNormal(bs, mExpNormal, 6);
        Pj.WriteIndex(bs, mExpIndex, Pj.ExplosionCount(mGame));
    }

    private void HitImpact(in RayHit hit, uint now, bool fromStatic)
    {
        if (mData.DamageClass == 0)
        {
            DirectDamage(hit, now);
            // 0x0045d090: the fields and +0x439 are always set, the mask only conditionally.
            SetExplosion(hit.Point, hit.Normal);
            if ((hit.Type & 0x44a00026u) == 0)
                SetMaskBits(0x04);
        }
        else if (mData.DamageClass == 1)
        {
            float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
            if (fromStatic)
            {
                pos[0] = mStatic.Point[0];
                pos[1] = mStatic.Point[1];
                pos[2] = mStatic.Point[2];
            }
            RadiusDamageAt(pos);
            ExplodeAtPosition(pos);
        }
        mDying = true;
        mDeleteTime = unchecked(now + 500);
    }
}

// ---- Grenade (2.5) ----

// src/game/projectile.cpp class Grenade.
internal sealed class Grenade : Projectile
{
    private uint mArmTime;
    private bool mArmed, mContact;
    private int mBounceType;

    public Grenade(GameState g) : base(g, "Grenade", kGrenadeGroup) { }

    protected override void ClassOnAdd()
    {
        mArmTime = unchecked(Pj.Ftol(Mul(mData.LiveTime, 1000.0f)) + mGame.Sim!.TickMs());
        mLastSend = mGame.Sim.TickMs();
    }

    public override void ServerTick(uint now)
    {
        using var chop = X87.Chop();
        if (mDying)
        {
            if (now >= mDeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        int ms = unchecked((int)Pj.Ftol(Mul(kSlice, 1000.0f)));
        if (unchecked((int)(now - mLastSend)) > 500)
        {
            mLastSend = now;
            SetMaskBits(0x01);
        }
        if (now > mTotalEnd)
        {
            Explode(now);
            return;
        }
        if (!mArmed && now > mArmTime)
            mArmed = true;
        while (unchecked(mLast + (uint)ms) < now)
        {
            float[] grav = { 0, 0, 0 };
            float drag = 0;
            bool inAir = mGame.EnteredForces(this, grav, ref drag);
            float[] target = new float[3];
            Integrate(this, kSlice, inAir ? grav : null, target, drag);
            int rem = ms;
            bool bounced = false;
            mContact = false;
            bool r = Step(target, ref rem, now);
            while (r && rem != 0)
            {
                float dtr = X87.F32(Mul(rem, 0.001f));
                for (int i = 0; i < 3; ++i)
                    target[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(Velocity[i], dtr))));
                if (mContact)
                    bounced = true;
                r = Step(target, ref rem, now);
            }
            GraceTick(ms);
            if (bounced)
            {
                mLastSend = now;
                SetMaskBits(0x03);
                if (mArmed)
                {
                    Explode(now);
                    return;
                }
            }
            mLast = unchecked(mLast + (uint)ms);
        }
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, Transform.P);
        Pj.WriteVec(bs, Velocity);
    }

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x01) != 0))
            Pj.WriteVec(bs, Transform.P);
        if (bs.WriteFlag((mask & 0x02) != 0))
        {
            Pj.WriteVec(bs, Velocity);
        }
        else
        {
            float l = X87.F32(X87.Len(Velocity));
            float k = X87.F32(Div(1.0, l));
            float[] u = { X87.F32(Mul(Velocity[0], k)), X87.F32(Mul(Velocity[1], k)), X87.F32(Mul(Velocity[2], k)) };
            Pj.WriteNormal(bs, u, 9);
        }
        if (bs.WriteFlag((mask & 0x08) != 0))
            bs.WriteInt((uint)mBounceType & 15, 4);
    }

    // 0x0046028c: one ray from the position to the target.
    private bool Step(float[] target, ref int rem, uint now)
    {
        var hit = new RayHit();
        float[] start = { Transform.P[0], Transform.P[1], Transform.P[2] };
        if (!CastWorld(mGame, start, target, 0x24e000a6u, ExcludeObject(), ref hit))
        {
            rem = 0;
            MoveTo(target);
            return false;
        }
        float s0 = X87.F32(X87.Len(Velocity));
        if (CollideSurface(hit, now))
        {
            MoveTo(hit.Point);
            float[] rel = { X87.F32(Sub(hit.Point[0], start[0])), X87.F32(Sub(hit.Point[1], start[1])), X87.F32(Sub(hit.Point[2], start[2])) };
            rem -= unchecked((int)Pj.Ftol(Mul(X87.F32(Div(X87.F32(X87.Len(rel)), s0)), 1000.0f)));
            if (((uint)rem & 0x08000000u) != 0)
                rem = 0;
            return true;
        }
        rem = 0;
        MoveTo(target);
        return false;
    }

    // 0x00460530.
    private bool CollideSurface(in RayHit hit, uint now)
    {
        ReadOnlySpan<float> n = hit.Normal;
        float vn = X87.F32(X87.Dot(n, Velocity));
        if (!(vn < 0.0f))
            return false;
        mContact = true;
        float k = X87.F32(Sub(0.01f, vn));
        float[] kn = new float[3];
        for (int i = 0; i < 3; ++i)
            kn[i] = X87.F32(Mul(k, n[i]));
        for (int i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Add(Velocity[i], X87.F32(Mul(kn[i], 2.0f))));
        float e = mData.Elasticity;
        if (hit.Material != null)
            e = X87.F32(Mul(mGame.Materials.ElasticityOf(hit.Material), e));
        for (int i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Mul(e, Velocity[i]));
        if (Mul(mData.MuzzleVelocity, 0.15) < (double)X87.F32(X87.Len(Velocity)))
        {
            mLastSend = now;
            SetMaskBits(0x08);
            mBounceType = hit.Material != null ? hit.Material.Type : 0;
        }
        return true;
    }

    private void Explode(uint now)
    {
        float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
        RadiusDamageAt(pos);
        mExploded = true;
        mLastSend = now;
        SetMaskBits(0x04);
        mDying = true;
        mDeleteTime = unchecked(now + 500);
    }
}
