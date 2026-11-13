// Port of src/game/projectile.hpp, src/game/projectile_internal.hpp and src/game/projectile.cpp: the Projectile base,
// RocketDumb, Bullet, Grenade, the line queries and the creation (docs/projectile-sim.md 0-3). The beam classes are
// ProjectileBeam.cs, the SeekingMissile ProjectileSeeker.cs, the ghost frame ProjectileGhost.cs. The Chop class of the
// files is X87.Chop; world::x87::f32 is X87.F32.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>A ray hit with the data a projectile impact needs (the "info" of 0x00505f6c).</summary>
public struct RayHit
{
    /// <summary>The object that was hit, or null.</summary>
    public GameBase? Object { get; set; }
    /// <summary>The type bits of the hit object.</summary>
    public uint Type { get; set; }
    /// <summary>The hit point.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref Float3 Point => ref _point;
    private Float3 _point;
    /// <summary>The surface normal at the hit point.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref Float3 Normal => ref _normal;
    private Float3 _normal;
    /// <summary>True when the hit is on the terrain.</summary>
    public bool Terrain { get; set; }
    /// <summary>The distance from the ray start to the hit.</summary>
    public float Distance { get; set; }
    /// <summary>The surface record (+0x20) of terrain and interior hits.</summary>
    public Material? Material { get; set; }

    /// <summary>Creates an empty hit with the normal (0, 0, 1).</summary>
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

/// <summary>The helpers shared by the projectile source files (namespace pj).</summary>
public static class Pj
{
    /// <summary>Finds a field of a datablock.</summary>
    /// <param name="b">The datablock.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The field, or null.</returns>
    public static DataBlockManager.Field? Field(DataBlockManager.Block b, string name)
    {
        var f = b.Find(name);
        return f != null && f.Elems.Count != 0 ? f : null;
    }

    /// <summary>Reads a real field of a datablock.</summary>
    /// <param name="b">The datablock.</param>
    /// <param name="name">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public static float Real(DataBlockManager.Block b, string name, float def)
    {
        var f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].F[0];
        if (f.Def.DefaultText.Length != 0)
            return DataBlockManager.CtorFloat(f.Def.DefaultText);
        return def;
    }

    /// <summary>Reads an integer field of a datablock.</summary>
    /// <param name="b">The datablock.</param>
    /// <param name="name">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public static int Integer(DataBlockManager.Block b, string name, int def)
    {
        var f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].I;
        if (f.Def.DefaultText.Length != 0 && f.Def.DefaultText[0] >= '0' && f.Def.DefaultText[0] <= '9')
            return Numbers.BorlandAtoi(f.Def.DefaultText);
        return def;
    }

    /// <summary>Reads a boolean field of a datablock.</summary>
    /// <param name="b">The datablock.</param>
    /// <param name="name">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public static bool Boolean(DataBlockManager.Block b, string name, bool def)
    {
        var f = Field(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].I != 0;
        if (f.Def.DefaultText.Length != 0)
            return GameUtil.EqualsNoCase(f.Def.DefaultText, "true");
        return def;
    }

    /// <summary>Converts a double to an unsigned integer as crt_ftol does.</summary>
    /// <param name="x">The value.</param>
    /// <returns>The converted value.</returns>
    public static uint Ftol(double x) =>
        unchecked((uint)(int)(double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0));

    /// <summary>Reads the world box of a container 7 entry.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="obj">The object.</param>
    /// <param name="mn">Receives the box minimum.</param>
    /// <param name="mx">Receives the box maximum.</param>
    /// <returns>False when the object has no entry.</returns>
    public static bool WorldBox(GameState game, GameBase? obj, Span<float> mn, Span<float> mx)
    {
        var e = obj != null ? game.World.Find(obj) : null;
        if (e == null)
            return false;
        for (var i = 0; i < 3; ++i)
        {
            mn[i] = e.Box[i];
            mx[i] = e.Box[i + 3];
        }
        return true;
    }

    /// <summary>Finds the script function of a method on an object.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="obj">The object.</param>
    /// <param name="method">The method name.</param>
    /// <returns>The function name, or an empty string.</returns>
    /// <remarks>Follows GameBase_findScriptMethod (0x0044864c). The lookup order is mapName, data name, className, C++ class, plain. Projectile blocks have no className field.</remarks>
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
        foreach (var fn in candidates)
            if (game.Console.HasFunction(fn))
                return fn;
        return "";
    }

    /// <summary>Calls a script function with formatted arguments.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="fn">The function name.</param>
    /// <param name="args">The formatted argument tokens.</param>
    /// <returns>The call result.</returns>
    /// <remarks>The original builds the call as text with sprintf and evaluates it, so numbers pass through the <c>%g</c> formatting and the script's number parser.</remarks>
    public static string CallMethod(GameState game, string fn, IReadOnlyList<string> args)
    {
        if (game.Console == null)
            return "";
        var line = fn + "(";
        for (var i = 0; i < args.Count; ++i)
            line += (i != 0 ? ", " : "") + args[i];
        return game.Console.Evaluate(line + ");");
    }

    /// <summary>Formats a number as <c>%g</c>.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string FmtG(double v) => Numbers.FormatFloat(v);

    // src/game/projectile_ghost.cpp ftolBits.
    private static uint FtolBits(double x, uint bits)
    {
        var i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        return (uint)i & ((1u << (int)bits) - 1);
    }

    /// <summary>Writes a vector as three raw floats.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="v">The vector.</param>
    public static void WriteVec(BitStream bs, ReadOnlySpan<float> v) => bs.WriteF32s(v[..3]);

    /// <summary>Writes a unit float in a number of bits.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="v">The value.</param>
    /// <param name="bits">The number of bits.</param>
    /// <remarks>Follows BitStream::writeFloat (0x00582fb0).</remarks>
    public static void WriteUnit(BitStream bs, float v, uint bits) =>
        bs.WriteInt(FtolBits(Mul((int)((1u << (int)bits) - 1), v), bits), bits);

    /// <summary>Writes a unit normal: x and y as signed floats, then the sign of z.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="n">The normal.</param>
    /// <param name="bits">The number of bits of x and y.</param>
    /// <remarks>Follows writeSignedFloat (0x00583018).</remarks>
    public static void WriteNormal(BitStream bs, ReadOnlySpan<float> n, uint bits)
    {
        for (var i = 0; i < 2; ++i)
        {
            var x = Mul(Mul(Add(n[i], 1.0f), 0.5f), (int)((1u << (int)bits) - 1));
            bs.WriteInt(FtolBits(x, bits), bits);
        }
        bs.WriteFlag(n[2] < 0.0f);
    }

    /// <summary>Writes a datablock index, whose width is the bit length of the group count + 1.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="index">The datablock index.</param>
    /// <param name="groupCount">The number of blocks in the group.</param>
    /// <remarks>Follows 0x00402b60.</remarks>
    public static void WriteIndex(BitStream bs, int index, int groupCount)
    {
        uint n = (uint)groupCount + 1, bits = 1;
        while (bits < 32 && (n >> (int)bits) != 0)
            ++bits;
        bs.WriteInt((uint)index & (bits >= 32 ? 0xffffffffu : ((1u << (int)bits) - 1)), bits);
    }

    /// <summary>Returns the number of explosion datablocks (group 20).</summary>
    /// <param name="game">The game state.</param>
    /// <returns>The count.</returns>
    public static int ExplosionCount(GameState game) => game.DataBlockCount(20);
}

/// <summary>The base class of the projectiles.</summary>
/// <remarks>Port of class Projectile in src/game/projectile.hpp. See docs/projectile-sim.md.</remarks>
public abstract class Projectile : GameBase
{
    /// <summary>The datablock group of Bullet projectiles (docs/projectile-sim.md 0).</summary>
    public const int BulletGroup = 14;
    /// <summary>The datablock group of Grenade projectiles.</summary>
    public const int GrenadeGroup = 15;
    /// <summary>The datablock group of Rocket projectiles.</summary>
    public const int RocketGroup = 16;
    /// <summary>The datablock group of Laser projectiles.</summary>
    public const int LaserGroup = 17;
    /// <summary>The datablock group of TargetLaser projectiles.</summary>
    public const int TargetLaserGroup = 24;
    /// <summary>The datablock group of Seeker projectiles.</summary>
    public const int SeekerGroup = 25;
    /// <summary>The datablock group of Lightning projectiles.</summary>
    public const int LightningGroup = 28;
    /// <summary>The datablock group of RepairEffect projectiles.</summary>
    public const int RepairGroup = 29;
    /// <summary>The object type of every projectile in container 7 (+0x48).</summary>
    public const uint ProjectileType = 0x100;

    /// <summary>The time slice of a projectile step, 1/30 s (0x3d088889).</summary>
    protected const float Slice = 0.0333333351f;

    // src/game/projectile.cpp deflectionRandom (0x00685ef8) and explosionRandom (0x006862f4).
    private static readonly R250 Deflection = new(), Explosion = new();

    /// <summary>Resets the two projectile random generators to the state of a freshly started server (seed 0x8377).</summary>
    public static void ResetProjectileRandoms()
    {
        Deflection.Reseed(0x8377);
        Explosion.Reseed(0x8377);
    }

    /// <summary>The ProjectileData values (docs/datablocks.md 5.19 to 5.27).</summary>
    public sealed class PData
    {
        /// <summary>The explosion datablock tag.</summary>
        public int ExplosionTag { get; set; }
        /// <summary>The explosion random cycle.</summary>
        public int ExpRandCycle { get; set; }
        /// <summary>The damage class.</summary>
        public int DamageClass { get; set; }
        /// <summary>The damage type.</summary>
        public int DamageType { get; set; }
        /// <summary>The time in milliseconds during which the owner is not hit.</summary>
        public int OwnerGraceMS { get; set; } = 1000;
        /// <summary>True when the projectile can hit its owner.</summary>
        public bool CollideWithOwner { get; set; }
        /// <summary>The collision radius.</summary>
        public float CollisionRadius { get; set; }
        /// <summary>The mass.</summary>
        public float Mass { get; set; } = 1;
        /// <summary>The direct damage value.</summary>
        public float DamageValue { get; set; }
        /// <summary>The explosion radius.</summary>
        public float ExplosionRadius { get; set; }
        /// <summary>The explosion kick.</summary>
        public float Kick { get; set; }
        /// <summary>The aim deflection.</summary>
        public float AimDeflection { get; set; }
        /// <summary>The muzzle velocity.</summary>
        public float MuzzleVelocity { get; set; }
        /// <summary>The terminal velocity.</summary>
        public float TerminalVelocity { get; set; }
        /// <summary>The acceleration.</summary>
        public float Acceleration { get; set; }
        /// <summary>The total life in milliseconds.</summary>
        public float TotalTime { get; set; } = 10000;
        /// <summary>The live time in milliseconds.</summary>
        public float LiveTime { get; set; } = 10000;
        /// <summary>The fraction of the shooter velocity the projectile inherits.</summary>
        public float InheritedScale { get; set; }
        /// <summary>The elasticity of bounces.</summary>
        public float Elasticity { get; set; } = 0.5f;
    }

    /// <summary>The game state.</summary>
    protected GameState Game { get; }
    /// <summary>The class name.</summary>
    protected string Cls { get; }
    /// <summary>The datablock values.</summary>
    protected PData mData { get; } = new();
    /// <summary>The datablock group.</summary>
    protected int Group { get; }
    /// <summary>The datablock, or null.</summary>
    protected DataBlockManager.Block? Block { get; set; }
    /// <summary>The id of the shooter (+0x210).</summary>
    protected int mShooter { get; set; }
    /// <summary>The id of the shooter passed to init, until onAdd (+0x328).</summary>
    protected int InitShooter { get; set; } = -1;
    /// <summary>The id of the shooter object found in onAdd (+0x32c).</summary>
    protected int ShooterObject { get; set; }
    /// <summary>The id of the owner client (+0x320).</summary>
    protected int Owner { get; set; }
    /// <summary>The join counter of the owner client at the shot (+0x324).</summary>
    protected int OwnerJoin { get; set; } = -1;
    /// <summary>The inherited velocity (+0x330).</summary>
    protected float[] Inherited { get; } = new float[3];
    /// <summary>The launch velocity (+0x33c).</summary>
    protected float[] Launch { get; } = new float[3];
    /// <summary>The id of the object excluded from queries (+0x1f8).</summary>
    protected int Exclude { get; set; }
    /// <summary>The owner grace counter (+0x2dc).</summary>
    protected int Grace { get; set; }
    /// <summary>The time of the last step (+0x304).</summary>
    protected uint Last { get; set; }
    /// <summary>The time of the last ghost update (+0x308).</summary>
    protected uint LastSend { get; set; }
    /// <summary>True while the projectile is dying (+0x30c).</summary>
    protected bool Dying { get; set; }
    /// <summary>True while the projectile is live (+0x314).</summary>
    protected bool Live { get; set; } = true;
    /// <summary>The time of deletion (+0x310).</summary>
    protected uint DeleteTime { get; set; }
    /// <summary>The end of the live time (+0x318).</summary>
    protected uint LiveEnd { get; set; }
    /// <summary>The end of the total time (+0x31c).</summary>
    protected uint TotalEnd { get; set; }
    /// <summary>True once the projectile exploded (+0x439).</summary>
    protected bool mExploded { get; set; }
    /// <summary>The explosion position (+0x43c).</summary>
    protected float[] ExpPos { get; } = new float[3];
    /// <summary>The explosion normal (+0x448).</summary>
    protected float[] ExpNormal { get; } = [0, 0, 1];
    /// <summary>The explosion datablock index (+0x454).</summary>
    protected int ExpIndex { get; set; }
    /// <summary>The box of the shape.</summary>
    protected float[] ShapeBox { get; } = new float[6];
    /// <summary>True while the projectile is in container 7.</summary>
    protected bool InContainer { get; set; }
    /// <summary>True when the gravity force is integrated (+0x16c bit 0).</summary>
    protected bool Gravity { get; set; }

    private static ClassContext OwnContext(GameState game) => new() { Game = game, Resources = game.Resources };

    /// <summary>Creates a projectile.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="cls">The class name.</param>
    /// <param name="group">The datablock group.</param>
    protected Projectile(GameState game, string cls, int group) : base(cls, Kind.Marker, group, Array.Empty<PersistDef>(), OwnContext(game))
    {
        Game = game;
        Cls = cls;
        Group = group;
    }

    private static readonly string[] Names =
    [
        "Bullet", "Grenade", "RocketDumb", "LaserProjectile", "TargetLaser", "SeekingMissile", "Lightning", "RepairEffect",
    ];

    /// <summary>Casts an object to a projectile.</summary>
    /// <param name="obj">The object, or null.</param>
    /// <returns>The projectile, or null when the object is not one.</returns>
    public static Projectile? From(SimObject? obj)
    {
        if (obj == null)
            return null;
        foreach (var n in Names)
            if (obj.ClassName() == n)
                return obj as Projectile;
        return null;
    }

    /// <summary>Returns the datablock values.</summary>
    /// <returns>The values.</returns>
    public PData Data() => mData;
    /// <summary>Tells whether the projectile exploded.</summary>
    /// <returns>True after the explosion.</returns>
    public bool Exploded() => mExploded;

    /// <summary>Returns the shooter object. The original delete-notifies it.</summary>
    /// <returns>The shooter, or null.</returns>
    public GameBase? Shooter()
    {
        var m = Manager();
        var o = m != null && ShooterObject != 0 ? m.FindById(ShooterObject) : null;
        return o as GameBase;
    }

    /// <summary>Returns the owner client id, the <c>source</c> of every damage call.</summary>
    /// <returns>The client id, 0 when unknown.</returns>
    /// <remarks>Follows 0x0045af24.</remarks>
    public int OwnerClientId()
    {
        if (Owner == 0)
            return 0;
        var r = Game.Players.FindHuman(Owner);
        return r != null && r.JoinOrder == OwnerJoin ? Owner : 0;
    }

    /// <summary>Initialises the projectile: transform, inherited velocity and shooter id.</summary>
    /// <param name="t">The transform.</param>
    /// <param name="velocity">The velocity the projectile inherits.</param>
    /// <param name="shooterId">The shooter object id.</param>
    /// <remarks>Follows vtable +0x1b4.</remarks>
    public virtual void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        Transform = t;
        mShooter = InitShooter = shooterId;
        Inherited[0] = velocity[0];
        Inherited[1] = velocity[1];
        Inherited[2] = velocity[2];
    }

    /// <summary>Sets the energy used by a shot and its fraction (lasers).</summary>
    /// <param name="used">The energy used.</param>
    /// <param name="fraction">The fraction of the full charge.</param>
    /// <remarks>Follows vtable +0x1b8.</remarks>
    public virtual void SetShotEnergy(float used, float fraction) { }
    /// <summary>Shuts a sustained projectile off (vtable +0x1c0).</summary>
    public virtual void ShutOff() { }
    /// <summary>Updates the transform of a sustained projectile (vtable +0x1c4).</summary>
    /// <param name="t">The new transform.</param>
    public virtual void UpdateTransform(in Transform t) { }
    /// <summary>Tells whether the projectile wants a target, as seekers do (vtable +0x1c8).</summary>
    /// <returns>True for a seeker.</returns>
    public virtual bool WantsTarget() => false;
    /// <summary>Returns the range in which a target is acquired (vtable +0x1cc).</summary>
    /// <returns>The range.</returns>
    public virtual float TargetRange() => 0.0f;
    /// <summary>Sets the target of a seeker (vtable +0x1d0).</summary>
    /// <param name="t">The target, or null.</param>
    public virtual void SetTarget(GameBase? t) { }
    /// <summary>Returns the object id a seeker steers to.</summary>
    /// <returns>The id, or 0 for none.</returns>
    public virtual int SeekerTarget() => 0;
    /// <summary>Tells whether the projectile is a sustained weapon (vtable +0x1bc).</summary>
    /// <returns>True for a sustained projectile.</returns>
    public virtual bool IsSustained() => false;
    /// <summary>Returns the end point and team of the beam of a TargetLaser (vtable +0x118, used by set 725).</summary>
    /// <param name="output">Receives the end point.</param>
    /// <param name="team">Receives the team.</param>
    /// <returns>True when the beam hit something.</returns>
    public virtual bool BeamTargetPoint(Span<float> output, out int team)
    {
        team = 0;
        return false;
    }

    /// <summary>Writes the initial ghost data (docs/projectile-sim.md 5). Called by <see cref="ProjectileGhost"/>.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="ctx">The connection context.</param>
    public abstract void PackInitial(BitStream bs, GhostContext ctx);
    /// <summary>Writes the explosion ghost data.</summary>
    /// <param name="bs">The stream.</param>
    public virtual void PackExplode(BitStream bs) { }
    /// <summary>Writes a ghost update.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <param name="ctx">The connection context.</param>
    public virtual void PackUpdate(BitStream bs, uint mask, GhostContext ctx) { }

    /// <summary>The class hook of onAdd (vtable +0x188).</summary>
    protected virtual void ClassOnAdd() { }
    /// <summary>Reads the class values of the datablock.</summary>
    /// <param name="b">The datablock.</param>
    protected virtual void ReadData(DataBlockManager.Block b) { }
    /// <summary>Called when the live time passed. The default ends the live state (vtable +0x18c).</summary>
    protected virtual void OnLiveTimePassed() => Live = false;
    /// <summary>Called when the total time passed.</summary>
    protected virtual void OnTotalTimePassed() { }

    // src/game/projectile.cpp slab: T7.1 slab test (0x005ebe80): t of the entry, or 0 when the start is inside.
    private static bool Slab(ReadOnlySpan<float> s, ReadOnlySpan<float> e, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx, out float t,
                             out int axisOut, out bool neg)
    {
        for (var a = 0; a < 3; ++a)
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
        for (var a = 0; a < 3; ++a)
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
        var b = game.Sim.DataBlocks().Find(gb.DataBlockName);
        var f = b != null ? Pj.Field(b, "shapeFile") : null;
        return f != null && !string.IsNullOrEmpty(f.Elems[0].Str) ? f.Elems[0].Str : null;
    }

    /// <summary>Integrates the projectile motion one step.</summary>
    /// <param name="p">The projectile.</param>
    /// <param name="dt">The step length in seconds.</param>
    /// <param name="acc">The acceleration, or null.</param>
    /// <param name="target">Receives the target position.</param>
    /// <param name="drag">The container drag (+0x124) of a class with movement flag 0x10 (Grenade).</param>
    /// <remarks>Follows SimMovement_integrate (0x00505460). The drag applies as <c>v -= v * ((mediumDrag 1.0 * drag) * dt)</c> after the forces (0x00505144).</remarks>
    protected static void Integrate(Projectile p, float dt, float[]? acc, float[] target, float drag = 0)
    {
        float[] old = [p.Velocity[0], p.Velocity[1], p.Velocity[2]];
        if (acc != null)
            for (var i = 0; i < 3; ++i)
                p.Velocity[i] = X87.F32(Add(Mul(acc[i], dt), p.Velocity[i]));
        if (drag != 0)
        {
            var s = X87.F32(Mul(X87.F32(Mul(1.0f, drag)), dt));
            for (var i = 0; i < 3; ++i)
                p.Velocity[i] = X87.F32(Sub(p.Velocity[i], Mul(p.Velocity[i], s)));
        }
        var h = X87.F32(Mul(dt, 0.5f));
        for (var i = 0; i < 3; ++i)
            target[i] = X87.F32(Add(Mul(Add(old[i], p.Velocity[i]), h), p.Transform.P[i]));
    }

    /// <summary>Finds the nearest line hit in container 7 (docs/collision.md 9).</summary>
    /// <param name="game">The game state.</param>
    /// <param name="s">The segment start.</param>
    /// <param name="e">The segment end.</param>
    /// <param name="mask">The object type mask.</param>
    /// <param name="exclude">The object that is skipped.</param>
    /// <param name="hit">Receives the hit.</param>
    /// <returns>True on a hit.</returns>
    /// <remarks>Tests terrain, interiors with their own line tests and, for shapes and players, the collision boxes (the face test of T7 for boxes).</remarks>
    public static bool CastWorld(GameState game, ReadOnlySpan<float> s, ReadOnlySpan<float> e, uint mask, SimObject? exclude,
                                 ref RayHit hit)
    {
        using var chop = X87.Chop();
        object? obj = null;
        Vec3 hp = default, n = default;
        var found = false;
        var best = 3.0e38f;
        var before = new Vec3(e[0], e[1], e[2]);
        if (game.World.CastRay(new Vec3(s[0], s[1], s[2]), new Vec3(e[0], e[1], e[2]), mask, exclude, ref obj, ref hp, ref n,
                               ref before) && obj != null)
        {
            hit.Object = obj as GameBase;
            hit.Point = Float3.Of(hp.X, hp.Y, hp.Z);
            hit.Normal = Float3.Of(n.X, n.Y, n.Z);
            var en = game.World.Find(obj);
            hit.Type = en != null ? en.Type : 0;
            hit.Terrain = (hit.Type & ObjectType.TerrainType) != 0;
            hit.Material = en?.HitMaterial?.Value;
            best = hit.Distance = DistanceOf(s, hit.Point);
            found = true;
        }
        if ((mask & ~0x6u) != 0)
        {
            float[] box =
            [
                Math.Min(s[0], e[0]), Math.Min(s[1], e[1]), Math.Min(s[2], e[2]), Math.Max(s[0], e[0]), Math.Max(s[1], e[1]),
                Math.Max(s[2], e[2]),
            ];
            // SimContainer_castRay (0x004fa030, docs/interiors.md 6.3 step 4): a hit shortens the segment, and every later
            // candidate is tested against (s, best hit) and computes its hit point from that segment. The terrain and the
            // shapes of the world cast above come first in the candidate order, so the Player and box tests below start
            // from their hit.
            float[] cur = [e[0], e[1], e[2]];
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
            foreach (var o in game.World.FindObjects(box, mask & ~0x6u, exclude))
            {
                var gb = o as GameBase;
                var en = gb != null ? game.World.Find(o) : null;
                // A Player entry carries a cast function too (line test against its own box).
                if (gb == null || en == null || (en.Cast != null && Player.From(gb) == null))
                    continue;
                float t = 0;
                float[] hpos = new float[3], hn = [0, 0, 0];
                var ok = false;
                var direct = false;
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
                    var shape = ShapeNameOf(game, gb);
                    if (shape == null)
                        continue;
                    var local = new List<Float6>();
                    if (Shape.ShapeCollisionBoxes(game.Resources, shape) is List<Float6> boxes && boxes.Count != 0)
                    {
                        local.AddRange(boxes);
                    }
                    else
                    {
                        var sb = new float[6];
                        if (!Shape.ShapeBox(game.Resources, shape, sb))
                            continue;
                        local.Add(Float6.Of(sb));
                    }
                    var m = gb.WorldMat();
                    Vec3 ls = m.ToLocal(new Vec3(s[0], s[1], s[2])), le = m.ToLocal(new Vec3(cur[0], cur[1], cur[2]));
                    float[] sl = [ls.X, ls.Y, ls.Z], el = [le.X, le.Y, le.Z];
                    var bestT = 1.1f;
                    foreach (var b in local)
                    {
                        float[] mn = [b[0], b[1], b[2]], mx = [b[3], b[4], b[5]];
                        if (Slab(sl, el, mn, mx, out var bt, out var axis, out var neg) && bt < bestT)
                        {
                            bestT = bt;
                            ok = true;
                            float[] ln = [0, 0, 0];
                            if (axis >= 0)
                                ln[axis] = neg ? -1.0f : 1.0f;
                            var wn = m.ToWorldVector(new Vec3(ln[0], ln[1], ln[2]));
                            hn[0] = wn.X;
                            hn[1] = wn.Y;
                            hn[2] = wn.Z;
                        }
                    }
                    t = bestT;
                }
                if (!ok)
                    continue;
                for (var i = 0; i < 3 && !direct; ++i)
                {
                    var d = X87.F32(Sub(cur[i], s[i]));
                    d = X87.F32(Mul(t, d));
                    hpos[i] = X87.F32(Add(s[i], d));
                }
                var dist = DistanceOf(s, hpos);
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

    /// <summary>Puts the projectile in container 7 at its current transform, using the shape box.</summary>
    protected void PlaceInContainer()
    {
        if (InContainer)
            return;
        var shape = "";
        if (Block != null)
            foreach (var f in new[] { "bulletShapeName", "shapeName" })
            {
                var fl = Pj.Field(Block, f);
                if (fl != null && !string.IsNullOrEmpty(fl.Elems[0].Str))
                {
                    shape = fl.Elems[0].Str!;
                    break;
                }
            }
        float[] local = [0, 0, 0, 0, 0, 0];
        if (shape.Length != 0 && Game.Resources != null)
            Shape.ShapeBox(Game.Resources, shape, local);
        Array.Copy(local, ShapeBox, 6);
        Span<float> box = stackalloc float[6];
        Shape.RotatedWorldBox(local, WorldMat(), box);
        var en = new Container.Entry(this, ProjectileType, Float6.Of(box), false, null);
        Game.World.Add(en);
        InContainer = true;
        Span<float> g = stackalloc float[3];
        float d = 0;
        Game.EnteredForces(this, g, ref d); // the gravity force of the terrain container (FC2)
    }

    /// <summary>Removes the projectile from container 7.</summary>
    protected void RemoveFromContainer()
    {
        if (InContainer)
            Game.World.Remove(this);
        InContainer = false;
    }

    /// <summary>Moves the projectile to a new position (SimObject_setTransform).</summary>
    /// <param name="p">The position.</param>
    protected void MoveTo(ReadOnlySpan<float> p)
    {
        Transform.P[0] = p[0];
        Transform.P[1] = p[1];
        Transform.P[2] = p[2];
        Transform.Flags |= 2;
        if (InContainer)
        {
            Span<float> box = stackalloc float[6];
            Shape.RotatedWorldBox(ShapeBox, WorldMat(), box);
            Game.World.SetBox(this, box);
            Span<float> g = stackalloc float[3];
            float d = 0;
            Game.EnteredForces(this, g, ref d);
        }
    }

    /// <summary>Counts down the owner grace time.</summary>
    /// <param name="ms">The elapsed time in milliseconds.</param>
    /// <remarks>Follows 0x0045a718.</remarks>
    protected void GraceTick(int ms)
    {
        if (Grace != 0)
        {
            Grace -= ms;
            if (Grace <= 0)
                Exclude = 0;
        }
    }

    /// <summary>Returns the object excluded from queries by the owner rule (docs/projectile-sim.md 2.1 step 3).</summary>
    /// <returns>The object, or null.</returns>
    protected SimObject? ExcludeObject()
    {
        var m = Manager();
        if (m == null || mShooter == 0)
            return null;
        if (!mData.CollideWithOwner || Grace > 0)
            return m.FindById(mShooter);
        return null;
    }

    /// <inheritdoc/>
    public override bool OnAdd()
    {
        // GameBase datablock bind without the ShapeBase part (resolveDataBlock reads shape fields).
        Block = DataBlockName != null && Game.Sim != null ? Game.Sim.DataBlocks().Find(DataBlockName) : null;
        if (Block == null || Block.Group != Group)
            return false;
        DataBlockIndex = Block.Index;
        using var chop = X87.Chop();
        var b = Block;
        var d = mData;
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
        Game.JoinSet(this, 719);
        var now = Game.Sim != null ? Game.Sim.TickMs() : 0;
        // Shooter and owner client (1.4 step 2).
        var m = Manager();
        var so = m != null && InitShooter > 0 ? m.FindById(InitShooter) : null;
        if (so is GameBase sg && sg.IsShapeBase())
        {
            ShooterObject = sg.Id();
            Owner = sg.ControlClient != 0 ? sg.ControlClient : sg.LastOwnerClient;
            var r = Owner != 0 ? Game.Players.FindHuman(Owner) : null;
            OwnerJoin = r != null ? r.JoinOrder : -1;
        }
        InitShooter = -1;
        // Muzzle velocity along the forward axis plus the inherited part, then the deflection.
        float[] fwd = [Transform.M[3], Transform.M[4], Transform.M[5]];
        for (var i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Add(X87.F32(Mul(fwd[i], d.MuzzleVelocity)), X87.F32(Mul(Inherited[i], d.InheritedScale))));
        if (d.AimDeflection != 0.0f)
        {
            // Projectile_applyDeflection (0x00459fa4): three draws, each angle one extended chain stored once:
            // (((n * 2^-32 (f32) - 0.5f) * 2.0f) * pi (f64)) * aimDeflection with n the 32-bit draw loaded as an integer
            // (fild qword). Then Mat3_fromEulerWrap (0x005ed000, which is m_mat3_setEuler) and m_mul_vector on the velocity.
            var a = new float[3];
            for (var k = 0; k < 3; ++k)
            {
                double n = Deflection.Next();
                a[k] = X87.F32(Mul(Mul(Mul(Sub(Mul(n, (double)2.3283064365386963e-10f), 0.5f), 2.0f), 3.141592653589793), d.AimDeflection));
            }
            var rot = new Transform();
            MissionTransform.EulerToMatrix(a, ref rot);
            var output = new float[3];
            X87.MulVector(Velocity, rot.M, output);
            output.CopyTo(Velocity, 0);
        }
        for (var i = 0; i < 3; ++i)
            Launch[i] = X87.F32(Mul(fwd[i], d.TerminalVelocity));
        Exclude = (!d.CollideWithOwner || d.OwnerGraceMS > 0) ? mShooter : 0;
        Last = now;
        Grace = unchecked((int)((uint)d.OwnerGraceMS + now));
        TotalEnd = unchecked(Pj.Ftol(Mul(d.TotalTime, 1000.0f)) + now);
        LiveEnd = unchecked(Pj.Ftol(Mul(d.LiveTime, 1000.0f)) + now);
        PlaceInContainer();
        ClassOnAdd();
        return true;
    }

    /// <inheritdoc/>
    public override void OnRemove()
    {
        if (InContainer)
            Game.World.Remove(this);
        InContainer = false;
    }

    /// <summary>Registers the projectile with the manager and the MissionCleanup group. The object is dropped on failure.</summary>
    /// <param name="m">The server manager.</param>
    /// <returns>True when registered.</returns>
    public bool RegisterWith(SimManager m)
    {
        if (!m.RegisterObject(this))
            return false;
        if (m.Find("MissionCleanup")?.AsSet() is SimSet set)
            set.AddObject(this);
        return true;
    }

    /// <summary>Returns the explosion datablock index.</summary>
    /// <returns>The index.</returns>
    protected int ExplosionIndex()
    {
        if (mData.ExpRandCycle <= 1)
            return mData.ExplosionTag;
        var u = X87.F32(Mul(Store(Explosion.Next()), 2.3283064365386963e-10));
        return mData.ExplosionTag + unchecked((int)Pj.Ftol(X87.F32(Mul(u, mData.ExpRandCycle))));
    }

    /// <summary>Records the explosion position and normal.</summary>
    /// <param name="pos">The position.</param>
    /// <param name="normal">The normal.</param>
    protected void SetExplosion(ReadOnlySpan<float> pos, ReadOnlySpan<float> normal)
    {
        pos[..3].CopyTo(ExpPos);
        normal[..3].CopyTo(ExpNormal);
        ExpIndex = ExplosionIndex();
        mExploded = true;
    }

    /// <summary>Explodes at an impact. The hit object's type decides whether the client is told.</summary>
    /// <param name="hit">The hit.</param>
    /// <remarks>Follows 0x00459eb4.</remarks>
    protected void ExplodeAtImpact(in RayHit hit)
    {
        if ((hit.Type & 0x44a00026u) != 0)
            return;
        SetExplosion(hit.Point, hit.Normal);
        SetMaskBits(0x04);
    }

    private static readonly float[] Up = [0, 0, 1];

    /// <summary>Explodes at a position.</summary>
    /// <param name="pos">The position.</param>
    protected void ExplodeAtPosition(ReadOnlySpan<float> pos)
    {
        SetExplosion(pos, Up);
        SetMaskBits(0x04);
    }

    /// <summary>Returns the direct damage value.</summary>
    /// <param name="now">The time in milliseconds.</param>
    /// <returns>The damage value.</returns>
    /// <remarks>Follows 0x00459d90.</remarks>
    protected virtual float DirectDamageValue(uint now) => mData.DamageValue;

    /// <summary>Applies direct damage to the hit object.</summary>
    /// <param name="hit">The hit.</param>
    /// <param name="now">The time in milliseconds.</param>
    protected void DirectDamage(in RayHit hit, uint now)
    {
        if (hit.Object == null || (hit.Type & 0x24c00080u) == 0)
            return;
        float[] zero = [0, 0, 0];
        hit.Object.ApplyDamage(mData.DamageType, DirectDamageValue(now), hit.Point, Velocity, zero, OwnerClientId());
    }

    /// <summary>Applies radius damage around a position.</summary>
    /// <param name="pos">The blast centre.</param>
    /// <remarks>Follows 0x00459e58.</remarks>
    protected void RadiusDamageAt(ReadOnlySpan<float> pos)
    {
        if (!(mData.ExplosionRadius > 0.0f))
            return;
        DamageCommands.ApplyRadiusDamage(Game, mData.DamageType, pos, Velocity, mData.ExplosionRadius, mData.DamageValue,
                                         mData.Kick, OwnerClientId());
    }

    /// <summary>Handles an impact, shared by the classes: the damage class switch.</summary>
    /// <param name="hit">The hit.</param>
    /// <param name="now">The time in milliseconds.</param>
    /// <remarks>Follows 0x0045aa38.</remarks>
    protected void Impact(in RayHit hit, uint now)
    {
        if (mData.DamageClass == 0)
        {
            DirectDamage(hit, now);
            ExplodeAtImpact(hit);
        }
        else if (mData.DamageClass == 1)
        {
            float[] pos = [Transform.P[0], Transform.P[1], Transform.P[2]];
            RadiusDamageAt(pos);
            ExplodeAtPosition(pos);
        }
        Dying = true;
        DeleteTime = unchecked(now + 500);
    }

    /// <summary>Runs the shared projectile step (docs/projectile-sim.md 2.1).</summary>
    /// <param name="now">The time in milliseconds.</param>
    /// <param name="variableSlice">True to use a variable time slice.</param>
    /// <param name="hit">Receives the hit when the step ends in one.</param>
    /// <returns>True when the step hit something.</returns>
    /// <remarks>Follows 0x0045a73c.</remarks>
    protected bool Advance(uint now, bool variableSlice, ref RayHit hit)
    {
        using var chop = X87.Chop();
        int ms;
        var dt = Slice;
        if (variableSlice)
        {
            var d = unchecked((int)(now - Last));
            if (d <= 0)
                return false;
            dt = X87.F32(Mul(d, 0.001f));
        }
        ms = unchecked((int)Pj.Ftol(Mul(dt, 1000.0f)));
        if (variableSlice)
            ms = unchecked((int)(now - Last));
        while (unchecked(Last + (uint)ms) <= now)
        {
            var target = new float[3];
            float[] grav = [0, 0, 0];
            float drag = 0;
            var inAir = Gravity && Game.EnteredForces(this, grav, ref drag);
            Integrate(this, dt, inAir ? grav : null, target);
            GraceTick(ms);
            if (CastWorld(Game, Transform.P, target, 0x24e000a6u, ExcludeObject(), ref hit))
                return true;
            MoveTo(target);
            Last = unchecked(Last + (uint)ms);
            if (variableSlice)
                break;
        }
        return false;
    }

    /// <summary>Runs the base tick.</summary>
    /// <param name="now">The time in milliseconds.</param>
    /// <remarks>Follows 0x0045aa38.</remarks>
    protected void BaseTick(uint now)
    {
        if (Dying)
        {
            if (now >= DeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        if (Live && now > LiveEnd)
        {
            OnLiveTimePassed();
        }
        else if (now > TotalEnd)
        {
            OnTotalTimePassed();
            Dying = true;
            DeleteTime = unchecked(now + 500);
            return;
        }
        var hit = new RayHit();
        if (Advance(now, false, ref hit))
            Impact(hit, now);
    }

    /// <summary>Creates a projectile from a datablock.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="group">The datablock group.</param>
    /// <param name="index">The datablock index.</param>
    /// <returns>The projectile, or null for a group without a class or a missing block.</returns>
    /// <remarks>Follows Projectile_createFromType (0x0046f46c).</remarks>
    public static Projectile? Create(GameState game, int group, int index)
    {
        if (game.Sim == null)
            return null;
        var b = game.Sim.DataBlocks().At(group, index);
        if (b == null)
            return null;
        var p = group switch
        {
            BulletGroup => new Bullet(game),
            GrenadeGroup => new Grenade(game),
            RocketGroup => new RocketDumb(game),
            _ => ProjectileBeam.CreateBeamProjectile(game, group),
        };
        p?.DataBlockName = b.Name;
        return p;
    }
}

// ---- RocketDumb (2.3) ----

// src/game/projectile.cpp class RocketDumb.
internal sealed class RocketDumb : Projectile
{
    private bool _force, _expired;

    public RocketDumb(GameState g) : base(g, "RocketDumb", RocketGroup) { }

    protected override void ClassOnAdd()
    {
        LastSend = Last;
        _force = true;
    }

    public override void ServerTick(uint now)
    {
        if (_force && Math.Abs((double)Launch[0]) <= Math.Abs((double)Velocity[0]) &&
            Math.Abs((double)Launch[1]) <= Math.Abs((double)Velocity[1]) && Math.Abs((double)Launch[2]) <= Math.Abs((double)Velocity[2]))
        {
            _force = false;
            Launch.CopyTo(Velocity, 0);
        }
        BaseTick(now);
    }

    protected override void OnTotalTimePassed()
    {
        // 0x00462134: expired, mask 0x04 (no explosion position).
        mExploded = true;
        _expired = true;
        SetMaskBits(0x04);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, Transform.P);
        Pj.WriteVec(bs, Velocity);
    }

    public override void PackExplode(BitStream bs)
    {
        if (!bs.WriteFlag(_expired))
        {
            Pj.WriteVec(bs, ExpPos);
            Pj.WriteNormal(bs, ExpNormal, 5);
            Pj.WriteIndex(bs, ExpIndex, Pj.ExplosionCount(Game));
        }
    }

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x01) != 0))
            Pj.WriteVec(bs, Transform.P);
        bs.WriteFlag(Live);
    }
}

// ---- Bullet (2.4) ----

// src/game/projectile.cpp class Bullet.
internal sealed class Bullet : Projectile
{
    private readonly float[] _start = new float[3], _vel = new float[3];
    private float _speed = 1;
    private uint _endTime;
    private bool _hasStatic;
    private RayHit _static = new();

    public Bullet(GameState g) : base(g, "Bullet", BulletGroup) { }

    private const double Inv = 1.0 / 1048575.0;

    protected override void ClassOnAdd()
    {
        // The velocity is replaced by what the ghost decodes from the initial data.
        var s = X87.F32(X87.Len(Velocity));
        var d = new float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Div(Velocity[i], s));
        var qx = Pj.Ftol(Mul(Mul(Add(d[0], 1.0f), 0.5f), 1048575.0f));
        var qy = Pj.Ftol(Mul(Mul(Add(d[1], 1.0f), 0.5f), 1048575.0f));
        var neg = d[2] < 0.0f;
        float x = X87.F32(Sub(Mul(Inv, Mul(2.0, qx)), 1.0f)), y = X87.F32(Sub(Mul(Inv, Mul(2.0, qy)), 1.0f));
        var z2 = X87.F32(Sub(Sub(1.0, Mul(x, x)), Mul(y, y)));
        var z = z2 < 0.0f ? 0.0f : X87.F32(Sqrt(z2));
        if (neg)
            z = -z;
        var s2 = X87.F32(Mul(Pj.Ftol(Mul(s, 16.0f)), 0.0625f));
        float[] dir = [x, y, z];
        for (var i = 0; i < 3; ++i)
            _vel[i] = Velocity[i] = X87.F32(Mul(s2, dir[i]));
        _start[0] = Transform.P[0];
        _start[1] = Transform.P[1];
        _start[2] = Transform.P[2];
        _speed = X87.F32(X87.Len(_vel));
        // The static world is tested once (0x0045dfa0).
        var now = Game.Sim!.TickMs();
        var end = new float[3];
        for (var i = 0; i < 3; ++i)
            end[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(_vel[i], mData.TotalTime))));
        var m = Manager();
        var ex = m != null && mShooter > 0 ? m.FindById(mShooter) : null;
        var h = new RayHit();
        if (CastWorld(Game, Transform.P, end, 0x6u, ex, ref h))
        {
            for (var i = 0; i < 3; ++i)
                h.Point[i] = X87.F32(Add(h.Point[i], X87.F32(Mul(h.Normal[i], 0.1f))));
            _static = h;
            _hasStatic = true;
            float[] dd = [X87.F32(Sub(h.Point[0], Transform.P[0])), X87.F32(Sub(h.Point[1], Transform.P[1])), X87.F32(Sub(h.Point[2], Transform.P[2]))];
            _endTime = unchecked(Pj.Ftol(Mul(X87.F32(Div(X87.F32(X87.Len(dd)), X87.Len(_vel))), 1000.0f)) + now);
        }
        else
        {
            _hasStatic = false;
            _endTime = unchecked(Pj.Ftol(Mul(mData.TotalTime, 1000.0f)) + now + 10000);
        }
    }

    protected override float DirectDamageValue(uint now)
    {
        // 0x0045bbe0: the damage fades between liveTime and totalTime.
        if (now > LiveEnd)
            return X87.F32(Mul(X87.F32(Sub(1.0, Div(unchecked((int)(now - LiveEnd)), unchecked((int)(TotalEnd - LiveEnd))))),
                               mData.DamageValue));
        return mData.DamageValue;
    }

    public override void ServerTick(uint now)
    {
        using var chop = X87.Chop();
        if (Dying)
        {
            if (now >= DeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        if (Live && now > LiveEnd)
        {
            Live = false;
        }
        else if (now > TotalEnd)
        {
            Dying = true;
            DeleteTime = unchecked(now + 500);
            return;
        }
        var ms = unchecked((int)(now - Last));
        if (ms == 0)
            return;
        Last = now;
        if (now > _endTime)
            ms -= unchecked((int)(now - _endTime));
        var end = new float[3];
        var dt = X87.F32(Mul(ms, 0.001f));
        for (var i = 0; i < 3; ++i)
            end[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(_vel[i], dt))));
        GraceTick(ms);
        var hit = new RayHit();
        if (CastWorld(Game, Transform.P, end, 0x24e000a6u & ~0x6u, ExcludeObject(), ref hit))
        {
            MoveTo(hit.Point);
            HitImpact(hit, now, false);
            return;
        }
        if (now >= _endTime)
        {
            if (_hasStatic)
            {
                HitImpact(_static, now, true);
            }
            else
            {
                Dying = true;
                DeleteTime = unchecked(now + 500);
            }
            return;
        }
        MoveTo(end);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, _start);
        float[] d = [X87.F32(Sub(Transform.P[0], _start[0])), X87.F32(Sub(Transform.P[1], _start[1])), X87.F32(Sub(Transform.P[2], _start[2]))];
        bs.WriteInt(Pj.Ftol(Mul(X87.F32(Div(X87.F32(X87.Len(d)), _speed)), 1000.0f)) & 0x7fff, 15);
        var u = new float[3];
        for (var i = 0; i < 3; ++i)
            u[i] = X87.F32(Div(_vel[i], _speed));
        Pj.WriteNormal(bs, u, 20);
        bs.WriteInt(Pj.Ftol(Mul(_speed, 16.0f)) & 0x3fff, 14);
    }

    public override void PackExplode(BitStream bs)
    {
        float[] d = [X87.F32(Sub(ExpPos[0], _start[0])), X87.F32(Sub(ExpPos[1], _start[1])), X87.F32(Sub(ExpPos[2], _start[2]))];
        bs.WriteInt(Pj.Ftol(Mul(Div(X87.F32(X87.Len(d)), _speed), 1000.0f)) & 0x7fff, 15);
        Pj.WriteNormal(bs, ExpNormal, 6);
        Pj.WriteIndex(bs, ExpIndex, Pj.ExplosionCount(Game));
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
            float[] pos = [Transform.P[0], Transform.P[1], Transform.P[2]];
            if (fromStatic)
            {
                pos[0] = _static.Point[0];
                pos[1] = _static.Point[1];
                pos[2] = _static.Point[2];
            }
            RadiusDamageAt(pos);
            ExplodeAtPosition(pos);
        }
        Dying = true;
        DeleteTime = unchecked(now + 500);
    }
}

// ---- Grenade (2.5) ----

// src/game/projectile.cpp class Grenade.
internal sealed class Grenade : Projectile
{
    private uint _armTime;
    private bool _armed, _contact;
    private int _bounceType;

    public Grenade(GameState g) : base(g, "Grenade", GrenadeGroup) { }

    protected override void ClassOnAdd()
    {
        _armTime = unchecked(Pj.Ftol(Mul(mData.LiveTime, 1000.0f)) + Game.Sim!.TickMs());
        LastSend = Game.Sim.TickMs();
    }

    public override void ServerTick(uint now)
    {
        using var chop = X87.Chop();
        if (Dying)
        {
            if (now >= DeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        var ms = unchecked((int)Pj.Ftol(Mul(Slice, 1000.0f)));
        if (unchecked((int)(now - LastSend)) > 500)
        {
            LastSend = now;
            SetMaskBits(0x01);
        }
        if (now > TotalEnd)
        {
            Explode(now);
            return;
        }
        if (!_armed && now > _armTime)
            _armed = true;
        while (unchecked(Last + (uint)ms) < now)
        {
            float[] grav = [0, 0, 0];
            float drag = 0;
            var inAir = Game.EnteredForces(this, grav, ref drag);
            var target = new float[3];
            Integrate(this, Slice, inAir ? grav : null, target, drag);
            var rem = ms;
            var bounced = false;
            _contact = false;
            var r = Step(target, ref rem, now);
            while (r && rem != 0)
            {
                var dtr = X87.F32(Mul(rem, 0.001f));
                for (var i = 0; i < 3; ++i)
                    target[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(Velocity[i], dtr))));
                if (_contact)
                    bounced = true;
                r = Step(target, ref rem, now);
            }
            GraceTick(ms);
            if (bounced)
            {
                LastSend = now;
                SetMaskBits(0x03);
                if (_armed)
                {
                    Explode(now);
                    return;
                }
            }
            Last = unchecked(Last + (uint)ms);
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
            var l = X87.F32(X87.Len(Velocity));
            var k = X87.F32(Div(1.0, l));
            float[] u = [X87.F32(Mul(Velocity[0], k)), X87.F32(Mul(Velocity[1], k)), X87.F32(Mul(Velocity[2], k))];
            Pj.WriteNormal(bs, u, 9);
        }
        if (bs.WriteFlag((mask & 0x08) != 0))
            bs.WriteInt((uint)_bounceType & 15, 4);
    }

    // 0x0046028c: one ray from the position to the target.
    private bool Step(float[] target, ref int rem, uint now)
    {
        var hit = new RayHit();
        float[] start = [Transform.P[0], Transform.P[1], Transform.P[2]];
        if (!CastWorld(Game, start, target, 0x24e000a6u, ExcludeObject(), ref hit))
        {
            rem = 0;
            MoveTo(target);
            return false;
        }
        var s0 = X87.F32(X87.Len(Velocity));
        if (CollideSurface(hit, now))
        {
            MoveTo(hit.Point);
            float[] rel = [X87.F32(Sub(hit.Point[0], start[0])), X87.F32(Sub(hit.Point[1], start[1])), X87.F32(Sub(hit.Point[2], start[2]))];
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
        var vn = X87.F32(X87.Dot(n, Velocity));
        if (!(vn < 0.0f))
            return false;
        _contact = true;
        var k = X87.F32(Sub(0.01f, vn));
        var kn = new float[3];
        for (var i = 0; i < 3; ++i)
            kn[i] = X87.F32(Mul(k, n[i]));
        for (var i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Add(Velocity[i], X87.F32(Mul(kn[i], 2.0f))));
        var e = mData.Elasticity;
        if (hit.Material != null)
            e = X87.F32(Mul(Game.Materials.ElasticityOf(hit.Material), e));
        for (var i = 0; i < 3; ++i)
            Velocity[i] = X87.F32(Mul(e, Velocity[i]));
        if (Mul(mData.MuzzleVelocity, 0.15) < (double)X87.F32(X87.Len(Velocity)))
        {
            LastSend = now;
            SetMaskBits(0x08);
            _bounceType = hit.Material != null ? hit.Material.Type : 0;
        }
        return true;
    }

    private void Explode(uint now)
    {
        float[] pos = [Transform.P[0], Transform.P[1], Transform.P[2]];
        RadiusDamageAt(pos);
        mExploded = true;
        LastSend = now;
        SetMaskBits(0x04);
        Dying = true;
        DeleteTime = unchecked(now + 500);
    }
}
