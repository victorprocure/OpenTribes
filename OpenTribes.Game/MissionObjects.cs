// Port of src/game/mission_objects.hpp and src/game/mission_objects.cpp (namespace ot::sim): the mission object classes
// (docs/mission.md 3): the GameBase family, TeamGroup and the non-GameBase classes with persist fields. Persist fields
// are parsed but not readable as obj.field; every other instant slot becomes a dynamic field. The Transform struct and
// the Euler conversions are OpenTribes.Sim (MissionTransform.cs). GameBase is one partial class over the files of its
// C++ methods: MissionObjects.cs, StaticSim.cs, ItemSim.cs, TurretSim.cs and Damage.cs. Arithmetic follows the mode of
// Fpu; x87::f32 is X87.F32 (toward zero) and x87::Chop is Fpu.ChopScope.
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/mission_objects.hpp struct PersistDef.
public readonly record struct PersistDef(string Name, int Type, int Count); // type: docs/datablocks.md 3 type numbers

// src/game/mission_objects.hpp class PersistFields: shared persist-field storage: raw values plus parsed point3F fields.
// Array fields keep one value per element.
public sealed class PersistFields
{
    private readonly List<PersistDef> mDefs;
    private readonly Dictionary<string, List<string>> mValues = new(); // lower-case field name
    private readonly Dictionary<string, float[]> mPoints = new();

    public PersistFields(IEnumerable<PersistDef> defs) => mDefs = new List<PersistDef>(defs);

    // src/game/mission_objects.cpp lower.
    internal static string Lower(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; ++i)
            if (c[i] >= 'A' && c[i] <= 'Z')
                c[i] = (char)(c[i] + 32);
        return new string(c);
    }

    // src/game/mission_objects.cpp atofRZ.
    internal static float AtofRZ(string s) => Store(Numbers.BorlandAtof(s));

    // src/game/mission_objects.cpp parsePoint: point3F setter (docs/datablocks.md 3 type 5): one "x y z" string keeps
    // unmatched components, three values are parsed separately.
    private static void ParsePoint(IReadOnlyList<string> values, float[] p)
    {
        if (values.Count == 1)
            CScan.Floats(values[0], p);
        else if (values.Count == 3)
            for (int i = 0; i < 3; ++i)
                p[i] = AtofRZ(values[i]);
    }

    // src/game/mission_objects.cpp elementKey.
    private static string ElementKey(string name, int index) => index < 0 ? name : name + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    // src/game/mission_objects.cpp parseBool.
    internal static bool ParseBool(string v)
    {
        if (v.Length != 0 && (v[0] == 'T' || v[0] == 't'))
            return true;
        if (v.Length != 0 && (v[0] == 'F' || v[0] == 'f'))
            return false;
        return Numbers.BorlandAtof(v) != 0;
    }

    private float[] PointSlot(string key)
    {
        if (!mPoints.TryGetValue(key, out float[]? p))
            mPoints[key] = p = new float[3];
        return p;
    }

    // src/game/mission_objects.cpp PersistFields::set.
    public bool Set(string field, IReadOnlyList<string> values)
    {
        foreach (PersistDef d in mDefs)
        {
            if (!SimClassesNoCase(d.Name, field))
                continue;
            string key = Lower(d.Name);
            if (d.Type == 5)
                ParsePoint(values, PointSlot(key));
            else if (d.Type == 0)
                // String table entry: case variants share the first spelling interned.
                mValues[key] = new List<string> { values.Count == 0 ? "" : StringTable.Global().Intern(values[0]) };
            else if (d.Type != 22) // type 22 (lightParams) setter does nothing
                mValues[key] = new List<string>(values);
            return true;
        }
        return false;
    }

    internal static bool SimClassesNoCase(string a, string b) => GameUtil.EqualsNoCase(a, b);

    // src/game/mission_objects.cpp PersistFields::setElement.
    public bool SetElement(string field, int index, IReadOnlyList<string> values)
    {
        foreach (PersistDef d in mDefs)
        {
            if (!SimClassesNoCase(d.Name, field) || d.Count < 2 || index < 0 || index >= d.Count)
                continue;
            string key = ElementKey(Lower(d.Name), index);
            if (d.Type == 5)
                ParsePoint(values, PointSlot(key));
            else
                mValues[key] = new List<string>(values);
            return true;
        }
        return false;
    }

    // src/game/mission_objects.cpp PersistFields::setPoint: replaces a point3F field (engine code, not the persist setter).
    public void SetPoint(string field, ReadOnlySpan<float> v) => mPoints[Lower(field)] = new[] { v[0], v[1], v[2] };

    // src/game/mission_objects.cpp PersistFields::get.
    public IReadOnlyList<string>? Get(string field, int index = -1) =>
        mValues.TryGetValue(ElementKey(Lower(field), index), out List<string>? v) ? v : null;

    // src/game/mission_objects.cpp PersistFields::point.
    public bool Point(string field, Span<float> output, int index = -1)
    {
        if (!mPoints.TryGetValue(ElementKey(Lower(field), index), out float[]? p))
            return false;
        for (int i = 0; i < 3; ++i)
            output[i] = p[i];
        return true;
    }

    // src/game/mission_objects.cpp PersistFields::str.
    public string Str(string field, string def = "")
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? v[0] : def;
    }

    // src/game/mission_objects.cpp PersistFields::real.
    public float Real(string field, float def)
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? AtofRZ(v[0]) : def;
    }

    // src/game/mission_objects.cpp PersistFields::integer.
    public int Integer(string field, int def, int index = -1)
    {
        IReadOnlyList<string>? v = Get(field, index);
        return v != null && v.Count != 0 ? Numbers.BorlandAtoi(v[0]) : def;
    }

    // src/game/mission_objects.cpp PersistFields::boolean.
    public bool Boolean(string field, bool def)
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? ParseBool(v[0]) : def;
    }

    // src/game/mission_objects.cpp PersistFields::point with a default.
    public void Point(string field, Span<float> output, float x, float y, float z, int index = -1)
    {
        if (Point(field, output, index))
            return;
        output[0] = x;
        output[1] = y;
        output[2] = z;
    }
}

// Small helpers shared by the game files (C library string compare, number text).
public static class GameUtil
{
    // strcasecmp == 0 (ASCII).
    public static bool EqualsNoCase(string? a, string? b)
    {
        if (a == null || b == null)
            return a == b;
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; ++i)
        {
            char x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z')
                x = (char)(x + 32);
            if (y >= 'A' && y <= 'Z')
                y = (char)(y + 32);
            if (x != y)
                return false;
        }
        return true;
    }

    // strcasecmp < 0 order (ASCII).
    public static int CompareNoCase(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; ++i)
        {
            int x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z')
                x += 32;
            if (y >= 'A' && y <= 'Z')
                y += 32;
            if (x != y)
                return x - y;
        }
        return a.Length - b.Length;
    }

    // "%g" of a float as the script prints it.
    public static string G(float v) => Numbers.FormatFloat(v);

    // Three floats as "x y z".
    public static string Fmt3(ReadOnlySpan<float> v) => G(v[0]) + " " + G(v[1]) + " " + G(v[2]);

    // sscanf(s, "%f %f %f") into a zeroed vector (the scan3 helpers of the command files).
    public static void Scan3(string s, Span<float> v)
    {
        v[0] = v[1] = v[2] = 0;
        CScan.Floats(s, v);
    }

    // crt_ftol: truncation toward zero, 0x80000000 when out of range.
    public static uint Ftol(double v)
    {
        if (!(v > -2147483649.0 && v < 2147483648.0))
            return 0x80000000u;
        return unchecked((uint)(int)v);
    }

    public static string Itoa(int v) => Numbers.FormatInt(v);
}

// src/game/mission_objects.hpp class GameBase: the GameBase family.
public partial class GameBase : SimObject
{
    // src/game/mission_objects.hpp GameBase::Kind.
    public enum Kind { ShapeBase, Marker, Trigger, Interior }

    // src/game/mission_objects.hpp GameBase::ShapeClass: C++ class behind the ShapeBase kind (packUpdate layout, script
    // state hooks).
    public enum ShapeKind { NoShape, StaticShape, Sensor, Turret, Item, Mine, Moveable, Vehicle }

    // DamageState
    public const int kNone = 0, kEnabled = 1, kDisabled = 2, kDestroyed = 3;
    // ThreadFlag
    public const byte kThreadState = 3, kThreadAtEnd = 4, kThreadForward = 8;
    // ThreadState
    public const int kPlay = 0, kStop = 1, kPause = 2;
    // MoveState
    public const int kMoveNone = 0, kMoveForward = 1, kMoveBackward = 2, kMoveStopped = 3;

    // src/game/mission_objects.hpp GameBase::Thread.
    public sealed class Thread
    {
        public int Sequence = -1;  // +0x750 + 0x10 * slot
        public byte Flags;         // +0x754: bits 0-1 state, 4 at end, 8 forward
        public bool Created;       // +0x748: the shape instance thread exists
        public float Position;     // thread +0x1c, 0..1
        public float Speed;        // thread +0x18
    }

    // src/game/mission_objects.hpp GameBase::CollisionRecord.
    public struct CollisionRecord
    {
        public int Other;
        public float Expiry;
    }

    // src/game/mission_objects.hpp GameBase::ItemBlock.
    public sealed class ItemBlock
    {
        public float Mass = 1, Drag = 1, Density = 1, Friction = 1, Elasticity = 0.2f;
    }

    // src/game/mission_objects.hpp GameBase::MineBlock: MineData (0x004588f8 vtable block).
    public sealed class MineBlock
    {
        public int DamageType;
        public float DamageValue = 1, ExplosionRadius = 1, Kick = 100;
    }

    // src/game/mission_objects.hpp GameBase::EntryForces: gravity force (type 1) and container drag (+0x124) taken on
    // terrain container entry (SimTerrain_enterContainer 0x005195b4, docs/player-sim.md FC2). Items and projectiles.
    public sealed class EntryForcesRec
    {
        public object? Terrain;       // terrain container entry at the last refile, null outside
        public object? Parent;
        public readonly float[] Gravity = new float[3];
        public float Drag;
    }

    // src/game/mission_objects.hpp GameBase::SensorBlock: SensorData fields (also read from a TurretData).
    public sealed class SensorBlock
    {
        public bool CastLOS;
        public int Range = 100, Doppler;
        public bool Supression, Supressable = true, Pinger = true;
    }

    // src/game/mission_objects.hpp GameBase::TurretBlock.
    public sealed class TurretBlock
    {
        public float GunRange = -1, Speed = 1, SpeedModifier, ReloadDelay, MinGunEnergy, MaxGunEnergy;
        public float Fov = -1, Deflection, EnergyRate, TargetableFovRatio = 0.5f; // +0x14c
        public bool Sustained, HasProjectile;
        public int FiringTimeMS;
    }

    // src/game/mission_objects.hpp GameBase::TurretMove: the fields of the last move the turret reads (+0x8d8).
    public struct TurretMove
    {
        public float Turn, Pitch;
        public bool Jump, Fire;
    }

    // src/game/mission_objects.hpp GameBase::MoveableBlock: MoveableData (datablock group 7) values read at bind.
    public sealed class MoveableBlock
    {
        public bool Displace = true, BlockForward = true, BlockBackward = true; // +0x100..+0x102
        public float Speed = 1;                                                 // +0x114
    }

    // src/game/mission_objects.hpp GameBase::Occupant.
    public struct Occupant
    {
        public int Object;
        public float Expiry, NextContact;
    }

    public int Team = -1;
    public string? DataBlockName;      // interned
    public string? MapName;            // ShapeBase starts with ""; null prints False
    public int TargetIndex = -1;       // sensor target, when the datablock is visibleToSensor
    public Transform Transform = new();
    public InteriorGeometry? InteriorGeo; // InteriorShape collision geometry
    // InteriorShape ghost data (docs/game-stream-ghosts.md 3.4): isContainer (+0x244, -1 until a shape loads), state
    // words and light records.
    public int IsContainer = -1;
    public InteriorNetInfo InteriorNet = new();
    public bool CollisionDisabled;     // +0x41c

    // Client links (docs/player-manager.md 12.5) and the control object interface (docs/game-stream.md 6.5).
    public int ControlClient;          // +0x230
    public int LastOwnerClient;        // +0x234: the last non-zero owner
    public int OwnerClient;            // +0x238
    public float Fov = 1.57079637f;    // +0x22c, stored by the PlayerPSC reader

    // ---- GameBase net state (docs/game-stream-ghosts.md 4.1) ----
    public int DataBlockIndex = -1;    // +0x220: index in the datablock group
    public bool PowerGenerator;        // +0x204
    public int PowerCount;             // +0x208: generators in the groups above

    // ---- ShapeBase net state (4.2) ----
    public readonly Thread[] Threads = { new(), new(), new() };
    public readonly float[] FlashNormal = new float[3]; // +0x738 damage flash direction
    public float FlashAmount;          // +0x744
    public int FadeState;              // +0x280: 0 none, 1 fade out, 2 fade in (2.5 s)
    public uint FadeStart;             // +0x284: tick time in ms at startFadeOut / startFadeIn
    public readonly float[] Rotation = new float[3]; // +0x244 Euler angles as given (persist field)
    public readonly float[] Velocity = new float[3]; // +0x194
    public float Damage;               // +0x72c
    public float MaxDamage = 1.0f;     // datablock +0x5c
    public float DisabledLevel = 0.5f, DestroyedLevel = 0.75f; // datablock +0xec, +0xf0 (ratios)
    public int DamageState = kEnabled; // +0x7d4 (StaticBase ctor 0x0044f430: 1)
    public bool Active;                // +0x7d0, GameBase::setActive
    public bool IsTarget;              // +0x7d1, GameBase::setIsTarget (set 725)
    public float RechargeRate;         // +0x728
    public float Energy, MaxEnergy;    // +0x724, datablock +0x58
    public float RepairRate = 0.1f;    // datablock +0x60
    public float RepairPending;        // +0x730
    public float AutoRepairRate;       // +0x734
    public bool DeleteOnDestroy;       // +0x7e4 (persist field, Mine and collideable Item force it)
    public bool ShapeBound;            // datablock resolved (+0x7cc)
    public ShapeAnim? Anim;

    // Collision callback records (+0x214, docs/static-sim.md 1.8): per other object the manager time before which no
    // further onCollision runs.
    public readonly List<CollisionRecord> CollisionRecords = new();
    public float CollisionLatest;

    // ---- Item and Mine (docs/static-sim.md 5) ----
    public bool ItemRotates;           // +0x888
    public bool ItemCollideable;       // +0x889
    public GameBase? ItemThrower;      // +0x878
    public bool AtRest;                // +0x164 bit 0x80
    public int ItemCount;              // +0x868
    public bool ItemVisible = true;    // +0x864 bit 0 (hidden items leave their container)
    public bool ItemPop;               // +0x864 bit 1, stored and read by nothing registered
    public bool ItemPrevAtRest = true; // +0x86c
    public float ItemThrowTime;        // +0x870
    public float ItemNextUpdate;       // +0x874
    public int ItemThrowerClient = -1, ItemThrowerJoin = -1; // +0x880, +0x884
    public bool ItemContact;           // +0x1f4: a contact surface was hit this step
    public List<SimObject> ItemNotify = new(); // notify list of the running move (info +0x3c)
    public float ItemSubStep = 0.3f;   // +0x250: 0.45 * shape radius
    public readonly float[] ShapeCenter = new float[3];   // shape bounding sphere centre (shape +0x08)
    public float ShapeRadius;          // shape bounding sphere radius (shape +0x04)
    public readonly float[] ShapeBox = new float[6];      // shape box (image +0x4c), object space
    public readonly float[] ShapeQueryBox = new float[6]; // the same enlarged by triggerRadius (object box)
    public float ShapeTriggerRadius;   // datablock triggerRadius: the sphere image +0x778 when not 0
    public readonly ItemBlock ItemData = new();
    public readonly MineBlock MineData = new();
    public readonly float[] MineNormal = new float[3]; // +0x88c
    public bool MineAligned;           // +0x898
    public int ItemFlags = 0x19d;      // +0x164 movement flags (0x181 | 0x1d); a displacement clears 0x05
    // ---- Moveable carry list (docs/collision.md E3.3) ----
    public readonly List<int> Carried = new(); // +0x154: ids of the SimMovement objects in contact
    public bool VisibilityThread;      // the shape has a `visibility` sequence (+0x7e0)
    public float ItemGravity;          // +0x14c: magnitude of the container gravity
    public readonly EntryForcesRec EntryForces = new();

    // ---- Sensor (docs/static-sim.md 3) ----
    public readonly SensorBlock SensorData = new();
    public uint SensorWord;            // +0x200: detected bits 0-7, always visible 8-15, state 24-31
    public bool SensorListed;          // member of a SensorManager list
    public int JamNext, JamPrev;       // jamming-player list links as object ids (Player +0x7d4, +0x7d8; 3.8)
    // ---- Turret (4.5) ----
    public float TurretRotation = 3.14159274f; // +0x8b4 (ctor pi; 0 once the shape has a turn thread)
    public float TurretElevation;      // +0x8b8
    public readonly TurretBlock TurretData = new();
    public float TurretSleep;          // +0x8ac: sim time until which an enemy keeps it awake
    public float TurretNextFire;       // +0x8b0
    public float TurretPowerPos;       // position (0..1) of the power / fire thread (+0x8a4)
    public bool TurretHasPower;        // the shape has the sequences `fire` and `power`
    public float TurretPowerDuration;  // duration of the `power` sequence
    public float TurretFireDuration;   // duration of the `fire` sequence
    public bool TurretPowerOnFire;     // the power / fire thread runs `fire` (priority 0), else `power` (-1)
    public float TurretRotationMax, TurretRotationMin; // +0x8c4, +0x8c8 (equal: unlimited)
    public float TurretMaxElevation = 0.785398185f, TurretMinElevation = -0.785398185f; // +0x8bc, +0x8c0
    public int TurretFireState;        // +0x908
    public int TurretMode;             // +0x8cc: 0 off, 1 deactivating, 2 activating, 3 on
    public int TurretBarrelState;      // +0x918: 1 from onAdd on a server
    public uint TurretTickMs;          // engine time in ms of the current tick (DAT_006a8494 + 4)
    public float TurretProjectileSpeed = -1; // +0x8d4, resolved on first use (4.2 step 2); < 0 unresolved
    public int TurretInRange;          // +0x8a8
    public int TurretPreferred;        // +0x900 (object id; the original keeps a raw pointer)
    public int TurretBeamTarget;       // +0x920
    public int TurretBeamProjectile;   // +0x924
    public uint TurretStamp;           // +0x91c (ms)
    public int TurretCredit;           // +0x8f8
    // Positions of the turn (+0x8a0) and elevate (+0x89c) threads (< 0: no thread).
    public float TurretTurnPos = -1, TurretElevatePos = -1;
    public TurretMove TurretLastMove;  // +0x8d8

    // ---- Moveable (docs/static-sim.md 2) ----
    public int MoveState = kMoveNone;  // +0x868
    public bool MoveBlocked;           // +0x86c
    public int MoveTarget = 15;        // +0x870 target waypoint
    public int PathId = 0xff;          // +0x87c
    public int Waypoint;               // +0x884
    public float WaypointTime;         // +0x888
    public int WaypointCount;          // +0x89c
    public readonly MoveableBlock MoveData = new();
    // Segment cache (+0x88c..+0x8cc).
    public int AppliedWaypoint = -1;
    public float AppliedTime;
    public int CachedSegment = -1;
    public float SegmentTime = 1.0f;
    public readonly float[] SegPos = new float[3], SegRot = new float[3], SegDPos = new float[3], SegDRot = new float[3];
    // Test overrides of the world's answers (GameServices::moveableDisplace, moveableTest).
    public Func<GameBase, bool>? MoveableDisplaceOverride;
    public Func<GameBase, bool>? MoveableTestOverride;

    // ---- Trigger (docs/static-sim.md 6) ----
    public bool TriggerActive = true;  // +0x250
    public readonly float[] TriggerBox = { -1, -1, -1, 1, 1, 1 }; // +0x1d4 boundingBox, object space
    public bool TriggerSphere = true;  // +0x251 isSphere
    public readonly List<Occupant> Occupants = new(); // +0x240
    public float TriggerInterval = 0.25f; // contact interval (datablock +0x34)

    private ShapeKind mShapeClass = ShapeKind.NoShape;
    private readonly string mClass;
    private readonly Kind mKind;
    private readonly int mDbGroup;
    private readonly PersistFields mFields;
    private readonly ClassContext mCtx;
    private readonly int mSetId, mLeaveSetId;

    // src/game/mission_objects.cpp GameBase::GameBase.
    public GameBase(string cls, Kind kind, int dataBlockGroup, IEnumerable<PersistDef> fields, ClassContext ctx,
                    int setId = 0, int leaveSetId = 0)
    {
        mClass = cls;
        mKind = kind;
        mDbGroup = dataBlockGroup;
        mFields = new PersistFields(fields);
        mCtx = ctx;
        mSetId = setId;
        mLeaveSetId = leaveSetId;
        if (kind == Kind.ShapeBase)
        {
            MapName = "";
            (string, ShapeKind)[] kShapes =
            {
                ("StaticShape", ShapeKind.StaticShape), ("Sensor", ShapeKind.Sensor), ("Turret", ShapeKind.Turret),
                ("Item", ShapeKind.Item), ("Mine", ShapeKind.Mine), ("Moveable", ShapeKind.Moveable),
                ("Flier", ShapeKind.Vehicle), ("Vehicle", ShapeKind.Vehicle),
            };
            foreach ((string c, ShapeKind s) in kShapes)
                if (GameUtil.EqualsNoCase(c, cls))
                    mShapeClass = s;
        }
        else
        {
            DamageState = kNone; // not a StaticBase: no damage state
        }
    }

    public override string ClassName() => mClass;
    public string ScriptClass() => mClass;
    public ShapeKind ShapeClass() => mShapeClass;
    // StaticBase family: everything built on the 0x0044f430 constructor (Vehicle : StaticBase, 0x00444fe4).
    public bool IsStaticBase() => mShapeClass != ShapeKind.NoShape;
    public bool IsShapeBase() => mKind == Kind.ShapeBase;
    public bool IsInterior() => mKind == Kind.Interior;
    public PersistFields Fields() => mFields;
    public int DataBlockGroup() => mDbGroup;
    public int DataBlockCount() => mCtx.Game != null ? mCtx.Game.DataBlockCount(mDbGroup) : 0;
    public Kind GetKind() => mKind;
    public GameServices? Services() => mCtx.Game;
    public ClassContext Context() => mCtx;
    public bool PersistPoint(string field, Span<float> output) => mFields.Point(field, output);
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);

    // vtable +0x100: the damage flash of a control object.
    public virtual float DamageFlash() => 0;
    // vtable +0x104 (Player 0x004113e8): throw direction of an object that throws along its own aim; false: the
    // GameBase rule.
    public virtual bool ThrowDirection(Span<float> output) => false;
    // vtable +0x130 chain: called after the datablock binds (Vehicle_onNewDataBlock 0x00445230).
    public virtual void OnNewDataBlock() { }
    // vtable +0x16c: the world transform of a mount point (Vehicle 0x00445c38); the object transform here.
    public virtual void GetMountTransform(int point, out Transform output) => output = Transform;
    // vtable +0x110: the eye transform in world space (Player 0x0041248c, Turret 0x004566ac); the object transform
    // otherwise (turret_fire.cpp GameBase::eyeTransform).
    public virtual Transform EyeTransform()
    {
        // P4b: Turret: the `dummy eye` node of the turret shape (turret_fire.cpp GameBase::eyeTransform).
        return Transform;
    }
    // vtable +0x150: the moves of a control object (Turret: turret_control.cpp, P4b).
    public virtual void ApplyMoves(Move[] moves, int count)
    {
        // P4b: Turret: turretApplyMoves (turret_control.cpp GameBase::applyMoves).
    }
    // vtable +0x15c: the control state of a control object (Turret: turret_control.cpp, P4b).
    public virtual void WriteControlState(BitStream bs, ControlContext ctx)
    {
        // P4b: Turret: turretWriteControlState (turret_control.cpp GameBase::writeControlState).
    }

    // src/game/mission_objects.cpp GameBase::setPersistField.
    public override bool SetPersistField(string field, IReadOnlyList<string> values)
    {
        if (!mFields.Set(field, values))
            return false;
        string v = values.Count == 0 ? "" : values[0];
        if (GameUtil.EqualsNoCase(field, "dataBlock"))
            DataBlockName = v.Length == 0 ? null : StringTable.Global().Intern(v);
        else if (GameUtil.EqualsNoCase(field, "name"))
            StoreMapName(v); // stringCS: raw text, exact spelling, until GB4 expands it
        return true;
    }

    // src/game/mission_objects.cpp GameBase::buildTransform.
    private void BuildTransform()
    {
        Span<float> pos = stackalloc float[3], rot = stackalloc float[3];
        pos.Clear();
        rot.Clear();
        mFields.Point("position", pos);
        mFields.Point("rotation", rot);
        MissionTransform.EulerToMatrix(rot, ref Transform);
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
        if (pos[0] != 0 || pos[1] != 0 || pos[2] != 0)
            Transform.Flags |= 2;
    }

    // src/game/mission_objects.cpp GameBase::initTeam: GB2: the first MissionGroup or TeamGroup above the object decides
    // the team.
    private void InitTeam()
    {
        for (SimGroup? g = Group(); g != null; g = g.Group())
        {
            if (g.Name() != null && GameUtil.EqualsNoCase(g.Name(), "MissionGroup"))
            {
                SetTeam(-1);
                return;
            }
            int t = 0;
            if (g.TeamOf(ref t))
            {
                SetTeam(t);
                return;
            }
            if (g.Group() == null)
                return;
        }
    }

    // src/game/mission_objects.cpp GameBase::setTeam: 0x00448368: mask 4, then the script.
    public void SetTeam(int t)
    {
        int old = Team;
        Team = t;
        if (mKind == Kind.Marker) // Marker_setTeam 0x00449558: always visible to its own team (all for -1)
            SensorWord = (SensorWord & ~0xff00u) | (t == -1 ? 0xff00u : (1u << (int)(((uint)t + 8u) & 31)));
        if (SensorListed && mCtx.Game != null)
            mCtx.Game.SensorTeam(this);
        if (old != t)
        {
            SetMaskBits(0x04);
            mCtx.Game?.ScriptCallback(this, "onSetTeam", new[] { Numbers.FormatInt(old) });
            // 0x004483cd: the owning client (+0x238) follows the object; its PlayerTeamChangeEvent goes to every human.
            if (OwnerClient != 0 && mCtx.Game != null)
                mCtx.Game.ClientTeam(OwnerClient, Team);
        }
    }

    // src/game/mission_objects.cpp GameBase::setMaskBits.
    public void SetMaskBits(uint bits) => mCtx.Game?.SetMaskBits(this, bits);

    // ---- power (docs/game-stream-ghosts.md 4.11) ----

    // src/game/mission_objects.cpp propagatePower: 0x00448524: every GameBase in the group and its sub-groups gains or
    // loses one generator.
    private static void PropagatePower(GameBase generator, SimGroup? group, bool on)
    {
        if (group == null)
            return;
        var members = new List<SimObject>(group.Members()); // scripts may change the group
        foreach (SimObject m in members)
        {
            if (m is GameBase gb)
            {
                if (on)
                {
                    ++gb.PowerCount;
                    gb.OnPower(generator);
                }
                else if (gb.PowerCount != 0)
                {
                    --gb.PowerCount;
                    gb.OnPower(generator);
                }
            }
            else if (m.AsGroup() is SimGroup sub)
            {
                PropagatePower(generator, sub, on);
            }
        }
    }

    // src/game/mission_objects.cpp GameBase::initPower: 0x00448478: own generator flag, then every generating GameBase
    // that is a direct member of a group above this object.
    private void InitPower()
    {
        PowerCount = 0;
        if (PowerGenerator)
        {
            PowerCount = 1;
            OnPower(this);
        }
        for (SimGroup? g = Group(); g != null; g = g.Group())
        {
            var members = new List<SimObject>(g.Members());
            foreach (SimObject m in members)
                if (m is GameBase gb && gb.PowerGenerator)
                {
                    ++PowerCount;
                    OnPower(gb);
                }
        }
    }

    // src/game/mission_objects.cpp GameBase::onGroupAdd: 0x004482d0: power count from the groups above.
    public override void OnGroupAdd()
    {
        InitPower();
        if (PowerGenerator)
            PropagatePower(this, Group(), true);
    }

    // src/game/mission_objects.cpp GameBase::onGroupRemove: 0x0044830c.
    public override void OnGroupRemove()
    {
        if (PowerGenerator && Manager() != null)
            PropagatePower(this, Group(), false);
    }

    // src/game/mission_objects.cpp GameBase::generatePower: 0x004485f4.
    public void GeneratePower(bool on)
    {
        if (on == PowerGenerator || Manager() == null)
            return;
        PowerGenerator = on;
        PropagatePower(this, Group(), on);
    }

    // src/game/mission_objects.cpp GameBase::onPower: StaticBase 0x0044ff0c: script onPower(id, powered, generator)
    // while enabled. The Turret override (0x004586ac) first stops a firing barrel, which is not modelled.
    public void OnPower(GameBase generator)
    {
        if (!IsStaticBase() || !ShapeBound || DamageState != kEnabled || mCtx.Game == null)
            return;
        mCtx.Game.ScriptCallback(this, "onPower", new[] { PowerCount != 0 ? "true" : "false", Numbers.FormatInt(generator.Id()) });
    }

    // ---- threads (4.2) ----

    // src/game/mission_objects.cpp GameBase::threadChanged: ShapeBase_updateThreadSpeed 0x0044c2b4. One draw of the
    // thread generator on every apply, whatever the state: s = 0.9 + u * 2^-32 * 0.2, evaluated as
    // ((u * 2^-32f) * 2 * 0.1f + 1) - 0.1f and stored as f32 toward zero.
    private void ThreadChanged(int slot)
    {
        Thread t = Threads[slot];
        if (!t.Created)
            return;
        double u = StaticRandom.Thread().Next();
        double scale = Mul(u, 2.3283064365386963e-10);
        scale = Mul(scale, 2.0);
        scale = Mul(scale, (double)0.1f);
        scale = Add(scale, 1.0);
        scale = Sub(scale, (double)0.1f);
        float s = X87.F32(scale); // control word: round toward zero
        int state = t.Flags & kThreadState;
        if (state == kPlay)
        {
            if ((t.Flags & kThreadAtEnd) == 0)
            {
                t.Speed = (t.Flags & kThreadForward) != 0 ? s : -s;
            }
            else
            {
                t.Position = (t.Flags & kThreadForward) != 0 ? 1.0f : 0.0f;
                t.Speed = 0;
            }
        }
        else
        {
            if (state == kStop)
                t.Position = 0;
            t.Speed = 0;
        }
    }

    // src/game/mission_objects.cpp GameBase::playThread: 0x0044c20c.
    public bool PlayThread(int slot, int sequence, bool reset)
    {
        Thread t = Threads[slot];
        if (t.Created && sequence == t.Sequence && (t.Flags & kThreadState) == kPlay)
            return true;
        if (sequence >= 16)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Sequence = sequence;
        if (reset)
            t.Flags = (byte)((t.Flags & ~(kThreadState | kThreadAtEnd)) | kThreadForward);
        if (ShapeBound)
        {
            t.Created = true;
            t.Position = 0;
            ThreadChanged(slot);
        }
        return true;
    }

    // src/game/mission_objects.cpp GameBase::stopThread: 0x0044c3d0.
    public bool StopThread(int slot)
    {
        Thread t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & kThreadState) == kStop)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)((t.Flags & ~kThreadState) | kStop);
        ThreadChanged(slot);
        return true;
    }

    // src/game/mission_objects.cpp GameBase::pauseThread: 0x0044c428.
    public bool PauseThread(int slot)
    {
        Thread t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & kThreadState) == kPause)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)((t.Flags & ~kThreadState) | kPause);
        ThreadChanged(slot);
        return true;
    }

    // src/game/mission_objects.cpp GameBase::resumeThread: 0x0044c480.
    public bool ResumeThread(int slot)
    {
        Thread t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & kThreadState) == kPlay)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)(t.Flags & ~kThreadState);
        ThreadChanged(slot);
        return true;
    }

    // src/game/mission_objects.cpp GameBase::setThreadDirection: 0x0044c4cc.
    public bool SetThreadDirection(int slot, bool forward)
    {
        Thread t = Threads[slot];
        if (t.Sequence == -1)
            return false;
        if (((t.Flags & kThreadForward) != 0) != forward)
        {
            SetMaskBits((0x800u << slot) | 0x400u);
            t.Flags = (byte)((t.Flags & ~(kThreadForward | kThreadAtEnd)) | (forward ? kThreadForward : 0));
            ThreadChanged(slot);
        }
        return true;
    }

    // ---- StaticBase state (4.2, 4.3) ----

    // src/game/mission_objects.cpp GameBase::stateCallback: vtable +0x1ac: StaticBase 0x0044fec0 calls the script named
    // after the damage state. Moveable (0x00444098) stops unless enabled; Turret (0x004570f4) adds mask 0x10000.
    private void StateCallback()
    {
        string[] kNames = { "onNone", "onEnabled", "onDisabled", "onDestroyed" };
        if (mShapeClass == ShapeKind.Moveable && DamageState != kEnabled)
            SetMoveState(kMoveStopped);
        if (mCtx.Game != null && DamageState >= 0 && DamageState <= 3)
            mCtx.Game.ScriptCallback(this, kNames[DamageState], Array.Empty<string>());
        if (mShapeClass == ShapeKind.Turret)
            SetMaskBits(0x10000);
        // Mine (0x00458be4): after the StaticBase part, entering Destroyed on the server explodes.
        if (mShapeClass == ShapeKind.Mine && DamageState == kDestroyed && mCtx.Game != null)
            mCtx.Game.MineDestroyed(this);
    }

    // src/game/mission_objects.cpp GameBase::scriptOnAdd: vtable +0x134: GameBase 0x004487a4 calls onAdd; StaticBase
    // 0x0044fe8c then reports the damage state (onEnabled for a new object).
    private void ScriptOnAdd()
    {
        if (mCtx.Game == null)
            return;
        mCtx.Game.ScriptCallback(this, "onAdd", Array.Empty<string>());
        if (IsStaticBase())
            StateCallback();
    }

    // src/game/mission_objects.cpp GameBase::setActive: vtable +0x130 (0x0044fbc4). A GameBase that is not a StaticBase
    // is always active and returns the argument (0x00448628, 0x0044862c). A Trigger has its own flag (section 6.4).
    public bool SetActive(bool on)
    {
        if (mKind == Kind.Trigger)
            return TriggerSetActive(on);
        if (!IsStaticBase())
            return on;
        if (on != Active && DamageState == kEnabled)
        {
            Active = on;
            if (!on && mShapeClass == ShapeKind.Moveable)
                SetMoveState(kMoveStopped); // 0x00444080
            if (ShapeBound && DamageState == kEnabled && mCtx.Game != null)
                mCtx.Game.ScriptCallback(this, on ? "onActivate" : "onDeactivate", Array.Empty<string>());
        }
        return Active;
    }

    // src/game/mission_objects.cpp GameBase::isActiveFlag: GameBase::isActive (vtable +0x12c).
    public bool IsActiveFlag()
    {
        if (mKind == Kind.Trigger)
            return TriggerActive;
        return IsStaticBase() ? Active : true;
    }

    // src/game/mission_objects.cpp GameBase::setIsTarget: 0x0045041c.
    public void SetIsTarget(bool on)
    {
        if (Manager() != null && mCtx.Game != null && on != IsTarget)
        {
            if (on)
                mCtx.Game.JoinSet(this, 725);
            else
                mCtx.Game.LeaveSet(this, 725);
        }
        SetMaskBits(0x02);
        IsTarget = on;
    }

    // src/game/mission_objects.cpp GameBase::setDamageState: 0x0044f9a0. Before registration only the field is stored.
    public void SetDamageState(int state, bool callScript)
    {
        if (Manager() == null)
        {
            DamageState = state;
            return;
        }
        if (state == DamageState)
            return;
        DamageState = state;
        if (state == kDisabled || state == kDestroyed)
            Active = false;
        if (state == kDestroyed)
        {
            Damage = MaxDamage;
            PostDelayedDelete();
        }
        SetMaskBits(0x100);
        if (callScript)
            StateCallback();
    }

    // src/game/mission_objects.cpp GameBase::updateDamageState: 0x0044f918: thresholds are products of maxDamage and the
    // datablock ratios, compared unrounded (fmul result against the stored damage).
    public void UpdateDamageState()
    {
        if (!(Mul(MaxDamage, DestroyedLevel) > Damage))
        {
            if (!(Damage < MaxDamage))
                Damage = MaxDamage;
            SetDamageState(kDestroyed, true);
        }
        else if (!(Mul(MaxDamage, DisabledLevel) > Damage))
        {
            SetDamageState(kDisabled, true);
        }
        else
        {
            SetDamageState(kEnabled, true);
        }
    }

    // src/game/mission_objects.cpp GameBase::setDamageLevel: StaticBase vtable +0x190 (0x0044fea4): clamped to
    // [0, maxDamage]; a change sets mask 0x100; the state is evaluated either way.
    public virtual void SetDamageLevel(float level)
    {
        if (level < 0)
            level = 0;
        if (level > MaxDamage)
            level = MaxDamage;
        if (level != Damage)
        {
            Damage = level;
            SetMaskBits(0x100);
        }
        UpdateDamageState();
    }

    // src/game/mission_objects.cpp GameBase::setPosition: vtable +0x19c, 0x0044c81c: rebuild the transform from +0x244
    // and the position; mask 8; not at rest.
    public void SetPosition(ReadOnlySpan<float> p)
    {
        MissionTransform.EulerToMatrix(Rotation, ref Transform);
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = p[i];
        Transform.Flags |= 2;
        SetMaskBits(0x08);
        AtRest = false;
    }

    // src/game/mission_objects.cpp GameBase::setInteriorPosition: InteriorShape::setTransform
    // (game-stream-ghosts.md 3.4): persist position, rotation read back from its matrix, mask 0x01.
    public void SetInteriorPosition(ReadOnlySpan<float> p)
    {
        Span<float> ang = stackalloc float[3], e = stackalloc float[3];
        mFields.Point("rotation", ang, 0, 0, 0);
        var m = new Transform();
        MissionTransform.EulerToMatrix(ang, ref m);
        MissionTransform.MatrixToEuler(m, e);
        mFields.SetPoint("position", p);
        mFields.SetPoint("rotation", e);
        // Mat3_fromEulerWrap of the read-back angles, then SimObject_setTransform (InteriorShape_setTransform).
        MissionTransform.EulerToMatrix(e, ref Transform);
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = p[i];
        Transform.Flags |= 2;
        SetMaskBits(0x01);
        if (InteriorGeo != null && mCtx.Game?.GetContainer() is Container c)
        {
            // World box through the new transform, padded (InteriorShape_worldBox 0047e5a0), then the container refile.
            Span<float> box = stackalloc float[6];
            Mat wm = WorldMat();
            TransformBox(InteriorGeo.Box, wm, box);
            Container.PadInteriorBox(box);
            c.SetTransform(this, wm, box);
        }
    }

    // src/game/mission_objects.cpp GameBase::setRotation: vtable +0x1a0: mask 0x10.
    public void SetRotation(ReadOnlySpan<float> e)
    {
        for (int i = 0; i < 3; ++i)
            Rotation[i] = e[i];
        SetMaskBits(0x10);
    }

    // src/game/mission_objects.cpp GameBase::transformEvent: 0x0044c978: `GameBase::setPosition` sends the object's own
    // matrix and `GameBase::setRotation` the matrix of the new angles, so the stored angles are those of a matrix round
    // trip (sine and cosine truncated to f32, then atan2), not the input.
    public void TransformEvent(in Transform m, ReadOnlySpan<float> p)
    {
        Span<float> e = stackalloc float[3];
        MissionTransform.MatrixToEuler(m, e);
        Span<float> q = stackalloc float[3] { p[0], p[1], p[2] };
        SetRotation(e);
        SetPosition(q);
    }

    // src/game/mission_objects.cpp GameBase::storeMapName: the raw `name` slot (no expansion), case-sensitive (GB7).
    public void StoreMapName(string text) => MapName = text;

    // src/game/mission_objects.cpp GameBase::setMapName: GB7 (0x00448fd8): copy text, replacing <vNAME> with $NAME; a
    // '<v' without '>' is copied literally; a value that would pass 254 characters ends the copy; at most 255.
    public void SetMapName(string? text)
    {
        var output = new System.Text.StringBuilder();
        if (text != null)
        {
            string t = CString.CStr(text);
            int p = 0;
            while (p < t.Length && output.Length < 255)
            {
                if (t[p] == '<' && p + 1 < t.Length && t[p + 1] == 'v')
                {
                    int gt = t.IndexOf('>', p);
                    if (gt >= 0)
                    {
                        string value = mCtx.Game != null ? mCtx.Game.Variable(t[(p + 2)..gt]) : "";
                        if (value.Length + output.Length > 0xfe)
                            break;
                        output.Append(value);
                        p = gt + 1;
                        continue;
                    }
                }
                output.Append(t[p++]);
            }
        }
        string s = output.ToString();
        StoreMapName(s);
        if (TargetIndex >= 0 && mCtx.Game != null)
            mCtx.Game.RenameTarget(TargetIndex, s);
    }

    // src/game/mission_objects.cpp GameBase::worldMat.
    public Mat WorldMat()
    {
        var m = new Mat();
        for (int i = 0; i < 9; ++i)
            m.M[i] = Transform.M[i];
        m.P = new Vec3(Transform.P[0], Transform.P[1], Transform.P[2]);
        return m;
    }

    // src/game/mission_objects.cpp transformBox: world AABB of a local box under a transform: the 8 corners
    // (docs/interiors.md 5.2).
    private static void TransformBox(ReadOnlySpan<float> local, in Mat m, Span<float> output)
    {
        for (int i = 0; i < 3; ++i)
        {
            output[i] = 1e30f;
            output[i + 3] = -1e30f;
        }
        for (int c = 0; c < 8; ++c)
        {
            var p = new Vec3(local[(c & 1) != 0 ? 3 : 0], local[(c & 2) != 0 ? 4 : 1], local[(c & 4) != 0 ? 5 : 2]);
            Vec3 w = m.ToWorld(p);
            for (int i = 0; i < 3; ++i)
            {
                output[i] = Math.Min(output[i], w[i]);
                output[i + 3] = Math.Max(output[i + 3], w[i]);
            }
        }
    }

    // src/game/mission_objects.cpp GameBase::onAdd.
    public override bool OnAdd()
    {
        if (mKind != Kind.Marker)
            BuildTransform();
        if (mKind == Kind.Interior && mCtx.Game?.GetResourceManager() is ResourceManager res)
        {
            // Load the state-0 LOD-0 geometry; a missing file is silent (docs/interiors.md L5).
            IReadOnlyList<string>? f = mFields.Get("filename");
            if (f != null && f.Count != 0)
            {
                InteriorGeo = OpenTribes.World.Interior.LoadInterior(res, f[0]);
                OpenTribes.World.Interior.LoadInteriorNetInfo(res, f[0], InteriorNet);
            }
            // isContainer (0x0047ec6c): -1 becomes 1 when a collision box extent is at least 6.0, else 0; it stays -1
            // without a shape.
            IsContainer = mFields.Integer("isContainer", -1);
            if (InteriorGeo != null && IsContainer == -1)
            {
                Float6 b = InteriorGeo.Box;
                IsContainer = (Sub(b[3], b[0]) >= 6.0 || Sub(b[4], b[1]) >= 6.0 || Sub(b[5], b[2]) >= 6.0) ? 1 : 0;
            }
            if (InteriorGeo != null && mCtx.Game.GetContainer() is Container cont)
            {
                var e = new Container.Entry(this, ObjectType.kInteriorType, default, false, null);
                Mat m = WorldMat();
                Span<float> box = stackalloc float[6];
                TransformBox(InteriorGeo.Box, m, box);
                Container.PadInteriorBox(box);
                e.Box = Float6.Of(box);
                // isContainer (docs/interiors.md 5.1 step 5): -1 means "any extent >= 6".
                IReadOnlyList<string>? ic = mFields.Get("isContainer");
                int isContainer = ic != null && ic.Count != 0 ? Numbers.BorlandAtoi(ic[0]) : -1;
                if (isContainer == -1)
                {
                    isContainer = 0;
                    for (int i = 0; i < 3; ++i)
                        if (Sub(InteriorGeo.Box[i + 3], InteriorGeo.Box[i]) >= 6.0)
                            isContainer = 1;
                }
                if (isContainer == 1)
                {
                    e.DatabaseKind = Container.Database.Interior;
                    e.ContainerMask = 0xffffffffu;
                    e.Geometry = InteriorGeo;
                    e.Transform = m;
                }
                InteriorGeometry geo = InteriorGeo;
                // Material list of the shape (.dis +0x4c) for the hit surface (collision.md 5.2 part 4.1).
                List<Material>? mats = null;
                string ml = OpenTribes.World.Interior.InteriorMaterialList(res, f![0]);
                if (ml.Length != 0)
                    mats = MaterialList.Get(res, ml);
                var slot = new System.Runtime.CompilerServices.StrongBox<Material?>(null);
                e.HitMaterial = slot;
                // Line into geometry space with the inverse image transform, the hit back with the transform itself
                // (0x00503408, m_mul_point, m_mul_vector). InteriorShape_getCollisionImages (0x0047f814) copies the
                // current object transform into the image on every query, so the cast reads it at call time (G38).
                e.Cast = (Vec3 s, Vec3 en, ref Vec3 hit, ref Vec3 n) =>
                {
                    using var chop = X87.Chop();
                    var toWorld = new TMat3();
                    toWorld.Flags = Transform.Flags;
                    toWorld.M = Transform.M;
                    toWorld.P = Transform.P;
                    Shape.TmatInverse(toWorld, out TMat3 toLocal);
                    Span<float> ws = stackalloc float[3] { s.X, s.Y, s.Z }, we = stackalloc float[3] { en.X, en.Y, en.Z };
                    Span<float> ls = stackalloc float[3], le = stackalloc float[3];
                    X87.MulPoint(ws, toLocal.M, toLocal.P, ls);
                    X87.MulPoint(we, toLocal.M, toLocal.P, le);
                    var h = new InteriorGeometry.Hit();
                    if (!geo.CastLine(new Vec3(ls[0], ls[1], ls[2]), new Vec3(le[0], le[1], le[2]), ref h))
                        return false;
                    byte mb = geo.SurfaceMaterial(geo.SurfaceAt(h.Leaf, h.PlaneIndex, h.Point));
                    slot.Value = mats != null && mb != 0xff && mb < mats.Count ? mats[mb] : null;
                    Span<float> lp = stackalloc float[3] { h.Point.X, h.Point.Y, h.Point.Z };
                    Span<float> ln = stackalloc float[3] { h.Normal.X, h.Normal.Y, h.Normal.Z };
                    Span<float> wp = stackalloc float[3], wn = stackalloc float[3];
                    X87.MulPoint(lp, toWorld.M, toWorld.P, wp);
                    X87.MulVector(ln, toWorld.M, wn);
                    hit = new Vec3(wp[0], wp[1], wp[2]);
                    n = new Vec3(wn[0], wn[1], wn[2]);
                    return true;
                };
                cont.Add(e);
            }
        }
        if (mKind == Kind.ShapeBase)
        {
            mFields.Point("rotation", Rotation);
            ItemRotates = mFields.Boolean("rotates", false);
            ItemCollideable = mFields.Boolean("collideable", mShapeClass == ShapeKind.Mine); // Mine_ctor 0x004588f8 sets +0x889
            ItemCount = mFields.Integer("count", 0);
            DeleteOnDestroy = mFields.Boolean("deleteOnDestroy", false) || mShapeClass == ShapeKind.Mine;
            if (mShapeClass == ShapeKind.Item && ItemCollideable)
                DeleteOnDestroy = true; // a collideable Item is deleted when destroyed
            ItemThrowTime = Manager() != null ? Manager()!.Time() : 0;
            // A server Item starts not at rest and settles in its first tick (docs/static-sim.md 5.2).
            AtRest = false;
        }
        InitTeam();
        if (DataBlockName != null && !BindDataBlock(true))
            return false;
        if (mCtx.Game != null)
        {
            if (mLeaveSetId != 0)
                mCtx.Game.LeaveSet(this, mLeaveSetId);
            if (mSetId != 0)
                mCtx.Game.JoinSet(this, mSetId);
            if (IsTarget)
                mCtx.Game.JoinSet(this, 725); // StaticBase_onAdd 0x0044fe1c
            if (DataBlockName != null)
                ScriptOnAdd();
            if (mKind == Kind.Trigger && DataBlockName != null)
                mCtx.Game.ScriptCallback(this, "onActivate", Array.Empty<string>()); // Trigger::onAdd 0x00487198 ends with it
        }
        if (mShapeClass == ShapeKind.Turret)
            TurretBarrelState = 1; // Turret_onAdd 0x00452d68
        return true;
    }

    // src/game/mission_objects.cpp GameBase::bindDataBlock: GB3: resolve the datablock and run what its bind does
    // (docs/static-sim.md 1.1).
    private bool BindDataBlock(bool registering)
    {
        _ = registering;
        if (mCtx.Game != null && !mCtx.Game.ResolveDataBlock(this))
            return false;
        OnNewDataBlock();
        if (mKind == Kind.Marker)
        {
            BuildTransform(); // Marker::onNewDataBlock (0x00485f3c)
            SetTeam(-1);
        }
        if (mShapeClass == ShapeKind.Turret)
        {
            // Turret onNewDataBlock (0x00452970) ends with vtable +0x134 on a server.
            BindTurretShape();
            ScriptOnAdd();
        }
        if (mShapeClass == ShapeKind.Sensor || mShapeClass == ShapeKind.Turret)
            mCtx.Game?.SensorInsert(this); // 0x00438fbc: the list of the team, or the suppressors
        mCtx.Game?.ScriptCallback(this, "onInit", Array.Empty<string>());
        return true;
    }

    // src/game/mission_objects.cpp GameBase::processArguments: newObject(name, class, args...) runs after registration
    // (docs/sim.md 6.4). StaticBase 0x0044f508: dataBlock [, deleteOnDestroy]; Item 0x004510e8: dataBlock, count, pop
    // [, rotate [, collide]]; Mine 0x00458970: dataBlock. The datablock binds first (its onAdd scripts see count 0), then
    // the other arguments are stored.
    public override bool ProcessArguments(IReadOnlyList<string> args)
    {
        if (mKind != Kind.ShapeBase)
            return true;
        int n = args.Count;
        string? usage = null;
        if (mShapeClass == ShapeKind.Item)
        {
            if (n < 3 || n > 5)
                usage = "Item: <dataFile> <count> <bool pop> [bool rotate] [bool collide]";
        }
        else if (mShapeClass == ShapeKind.Mine)
        {
            if (n != 1)
                usage = "Mine: <dataFile>";
        }
        else if (n != 1 && n != 2)
        {
            usage = "StaticBase: <dataFile> [deleteOnDestroy]";
        }
        if (usage != null)
        {
            mCtx.Game?.PrintLine(usage);
            return false;
        }
        SetPersistField("dataBlock", new[] { args[0] });
        if (!BindDataBlock(false))
            return false;
        ScriptOnAdd();
        if (mShapeClass == ShapeKind.Item)
        {
            ItemCount = Numbers.BorlandAtoi(args[1]);
            if (Numbers.ParseBool(args[2]))
                ItemPop = true;
            if (n > 3 && Numbers.ParseBool(args[3]))
                ItemRotates = true;
            if (n > 4 && Numbers.ParseBool(args[4]))
            {
                ItemCollideable = true;
                DeleteOnDestroy = true;
            }
        }
        else if (mShapeClass != ShapeKind.Mine && n == 2)
        {
            DeleteOnDestroy = Numbers.ParseBool(args[1]);
        }
        return true;
    }

    // src/game/mission_objects.cpp GameBase::onRemove.
    public override void OnRemove()
    {
        DetachAll(); // 0x0050471e: the carry lists never keep a deleted object
        if (mCtx.Game != null && mKind != Kind.Trigger)
            mCtx.Game.DropTriggerOccupant(this);
        if (mKind == Kind.Trigger && TriggerActive && DataBlockName != null)
            TriggerSetActive(false); // 0x004872a4 before the removal
        if (mCtx.Game != null && DataBlockName != null)
            mCtx.Game.ScriptCallback(this, "onRemove", Array.Empty<string>());
        if (mCtx.Game != null)
        {
            if (SensorListed)
                mCtx.Game.SensorRemove(this);
            mCtx.Game.TargetRemoved(this);
        }
        mCtx.Game?.GetContainer()?.Remove(this);
    }
}

// src/game/mission_objects.cpp class PersistGroup: SimGroup subclass with persist fields (SimPath, DropPointGroup).
internal sealed class PersistGroup : SimGroup
{
    private readonly string mClass;
    private readonly PersistFields mFields;

    public PersistGroup(string cls, IEnumerable<PersistDef> fields)
    {
        mClass = cls;
        mFields = new PersistFields(fields);
    }

    public override string ClassName() => mClass;
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => mFields.Set(field, values);
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);
}

// src/game/mission_objects.hpp class PersistObject: non-GameBase mission object with persist fields and optional load
// checks (Sky, Planet, StarField, MissionCenterPos, SimPalette, SimLight, Snowfall).
public class PersistObject : SimObject
{
    private readonly string mClass;
    private readonly PersistFields mFields;
    private readonly ClassContext mCtx;
    private readonly int mFixed;
    private readonly string? mRequired;

    public PersistObject(string cls, IEnumerable<PersistDef> fields, ClassContext ctx, int fixedId = 0, string? requiredFile = null)
    {
        mClass = cls;
        mFields = new PersistFields(fields);
        mCtx = ctx;
        mFixed = fixedId;
        mRequired = requiredFile;
    }

    public override string ClassName() => mClass;
    public override int FixedId() => mFixed;
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => mFields.Set(field, values);
    public override bool SetPersistElement(string field, int index, IReadOnlyList<string> values) =>
        mFields.SetElement(field, index, values);
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);
    public PersistFields Fields() => mFields;
    protected ClassContext Context() => mCtx;
    protected PersistFields MutableFields() => mFields;

    // src/game/mission_objects.cpp PersistObject::onAdd: SimPalette (.ppl) and SimTerrain (.ted) fail onAdd when their
    // file is missing.
    public override bool OnAdd()
    {
        if (mRequired != null && mCtx.Game != null)
        {
            IReadOnlyList<string>? v = mFields.Get(mRequired);
            if (v == null || v.Count == 0 || !mCtx.Game.ResourceExists(v[0]))
                return false;
        }
        return true;
    }
}

// src/game/mission_objects.hpp class SimLightObject: SimLight: event 0x420 of `activateGroup` sets the colour to
// (v, v, v) and sends mask 0x04 (docs/game-stream-ghosts.md 3.3, 0x0052d290).
public sealed class SimLightObject : PersistObject
{
    public SimLightObject(IEnumerable<PersistDef> fields, ClassContext ctx) : base("SimLight", fields, ctx) { }

    // src/game/mission_objects.cpp SimLightObject::processArguments: SimLight::processArguments (0x0052cac0):
    // `Directional|DirectionalWrap intR intG intB dirX dirY dirZ [ambR ambG ambB]` or `Point range intR intG intB posX
    // posY posZ [ambR ambG ambB]`. The direction is normalized twice, first with the approximate length
    // (m_Point3F_lenf), then with the exact one. Every number goes through atof and an f32 store toward zero.
    public override bool ProcessArguments(IReadOnlyList<string> args)
    {
        void Say(string text) => Context().Game?.PrintLine(text);
        const string kUsage = "SimLight: type(Directional/Point) ...";
        if (args.Count == 0)
        {
            Say(kUsage);
            return false;
        }
        float Num(int i) => X87.F32(Numbers.BorlandAtof(args[i]));
        void SetVec(string field, ReadOnlySpan<float> v) => MutableFields().SetPoint(field, v);
        int argc = args.Count;
        PersistFields f = MutableFields();
        void SetInt(string field, string v) => f.Set(field, new[] { v });
        if (args[0].StartsWith("Directional", StringComparison.Ordinal))
        {
            if (argc == 7 || argc == 10)
            {
                SetInt("type", args[0] == "DirectionalWrap" ? "6" : "1");
                float[] color = { Num(1), Num(2), Num(3) };
                float[] dir = { Num(4), Num(5), Num(6) };
                using (ChopScope())
                {
                    float inv = X87.F32(Div(1.0, X87.Lenf(dir)));
                    for (int i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(inv, dir[i]));
                    float inv2 = X87.F32(Div(1.0, X87.F32(X87.Len(dir))));
                    for (int i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(inv2, dir[i]));
                }
                float[] ambient = { 0, 0, 0 };
                if (argc == 10)
                    for (int i = 0; i < 3; ++i)
                        ambient[i] = Num(7 + i);
                SetVec("aim", dir);
                SetVec("color", color);
                SetVec("ambient", ambient);
                return true;
            }
            Say("SimLight: Directional intR intG intB dirX dirY dirz [ambR ambG ambB]");
        }
        else if (args[0] == "Point")
        {
            if (argc == 8 || argc == 11)
            {
                SetInt("type", "2");
                float range = Num(1);
                float[] color = { Num(2), Num(3), Num(4) }, pos = { Num(5), Num(6), Num(7) };
                float[] ambient = { 0, 0, 0 };
                if (argc == 11)
                    for (int i = 0; i < 3; ++i)
                        ambient[i] = Num(8 + i);
                SetVec("position", pos);
                SetVec("color", color);
                SetVec("ambient", ambient);
                f.Set("range", new[] { TerrainCommands.PersistFloatText(range) });
                return true;
            }
            Say("SimLight: Point range intR intG intB posX posY posZ [ambR ambG ambB]");
        }
        Say(kUsage);
        return false;
    }

    // src/game/mission_objects.cpp SimLightObject::onActivateEvent.
    public override void OnActivateEvent(float value)
    {
        float[] color = { value, value, value };
        MutableFields().SetPoint("color", color);
        Context().Game?.SetMaskBits(this, 0x04);
    }
}

// src/game/mission_objects.hpp class TerrainObject: SimTerrain (docs/terrain.md 2.1): loads the .dtf, joins container 7
// and computes the verification CRC sent to clients.
public sealed class TerrainObject : PersistObject
{
    private string mTed = "";
    private Vec3 mPos;
    private float mVisibleDistance = 3000; // docs/terrain.md 2.2 default
    private Terrain? mTerrain;
    private uint mCrc = 0xffffffffu;       // ctor value -1

    public TerrainObject(ClassContext ctx, IEnumerable<PersistDef> fields) : base("SimTerrain", fields, ctx, 8, "tedFileName") { }

    public uint Crc() => mCrc;
    public Terrain? GetTerrain() => mTerrain;
    public float VisibleDistance() => mVisibleDistance;

    // src/game/mission_objects.cpp TerrainObject::setPersistField.
    public override bool SetPersistField(string field, IReadOnlyList<string> values)
    {
        if (GameUtil.EqualsNoCase(field, "tedFileName"))
            mTed = values.Count == 0 ? "" : values[0];
        if (GameUtil.EqualsNoCase(field, "position") && values.Count != 0)
        {
            Span<float> p = stackalloc float[3] { mPos.X, mPos.Y, mPos.Z };
            CScan.Floats(values[0], p);
            mPos = new Vec3(p[0], p[1], p[2]);
        }
        if (GameUtil.EqualsNoCase(field, "visibleDistance") && values.Count != 0)
            mVisibleDistance = PersistFields.AtofRZ(values[0]);
        return base.SetPersistField(field, values);
    }

    // src/game/mission_objects.cpp TerrainObject::onAdd.
    public override bool OnAdd()
    {
        ClassContext ctx = Context();
        if (!base.OnAdd() || ctx.Game == null || ctx.Game.GetResourceManager() == null)
            return false;
        ResourceManager res = ctx.Game.GetResourceManager()!;
        mTerrain = Terrain.Load(res, mTed);
        if (mTerrain == null || !mTerrain.Crc(res, mTed, ref mCrc))
            return false; // the original exits: "Could not verify Terrain" (D11)
        mTerrain.SetPosition(mPos);
        if (ctx.Game.GetContainer() is Container c)
        {
            var e = new Container.Entry(this, ObjectType.kTerrainType, default, false, null);
            Span<float> box = stackalloc float[6];
            mTerrain.WorldBox(box);
            // Own database (ctor 005171ac) taking every type; +0x2e0 (00517444).
            e.DatabaseKind = Container.Database.Terrain;
            e.ContainerMask = 0xffffffffu;
            e.VisibleDistance = Math.Min(mVisibleDistance, 10000.0f);
            // Container properties for moving objects (docs/player-sim.md FC2, FC3): gravity and drag, and the box
            // extended by contHeight below and above (0x005193cc).
            e.TerrainData = mTerrain;
            Span<float> g = stackalloc float[3];
            Fields().Point("contGravity", g, 0.0f, 0.0f, -9.8f);
            e.Gravity = Float3.Of(g);
            e.Drag = Fields().Real("contDrag", 0.0f);
            // SimTerrain_updateBox (0x005193cc): the local z range is -H..H with H = contHeight, or hmax when contHeight
            // is 0; hmin is not used (docs/static-sim.md TD8, `staticdiff` ch_item_hmax).
            float h = Fields().Real("contHeight", 10000.0f);
            float top = h != 0.0f ? h : mTerrain.Hmax();
            box[2] = Store(Add(-top, mPos.Z));
            box[5] = Store(Add(top, mPos.Z));
            e.Box = Float6.Of(box);
            Terrain t = mTerrain;
            var slot = new System.Runtime.CompilerServices.StrongBox<Material?>(null);
            e.HitMaterial = slot;
            GameServices services = ctx.Game;
            e.Cast = (Vec3 s, Vec3 en, ref Vec3 hit, ref Vec3 n) =>
            {
                int material = -1;
                if (!t.CastLine(s, en, ref hit, ref n, ref material))
                    return false;
                slot.Value = services.TerrainMaterial(material);
                return true;
            };
            c.Add(e);
        }
        PostVisibilityEvent(15.0f);
        return true;
    }

    // src/game/mission_objects.cpp TerrainObject::postVisibilityEvent: 00518670 (docs/terrain.md 2.1 step 4,
    // docs/sim.md TK7): event 0x401 subtype 2 at onAdd + 15 s, reposted every 30 s. It resends the visibility block
    // (net mask 0x02).
    private void PostVisibilityEvent(float delay)
    {
        SimManager m = Manager()!;
        m.PostEvent(this, AddF(m.Time(), delay), () =>
        {
            Context().Game?.SetMaskBits(this, 0x02);
            PostVisibilityEvent(30.0f);
        });
    }

    // src/game/mission_objects.cpp TerrainObject::onRemove.
    public override void OnRemove() => Context().Game?.GetContainer()?.Remove(this);
}

// src/game/mission_objects.hpp class TeamGroup: a SimGroup that creates a team when it registers (TM6).
public sealed class TeamGroup : SimGroup
{
    private readonly ClassContext mCtx;
    public int Team = -1;

    public TeamGroup(ClassContext ctx) => mCtx = ctx;
    public override string ClassName() => "TeamGroup";

    public override bool TeamOf(ref int t)
    {
        t = Team;
        return true;
    }

    // src/game/mission_objects.cpp TeamGroup::onAdd: TM6: the team id is the current team count; the group becomes
    // team<digit>.
    public override bool OnAdd()
    {
        Team = mCtx.Game != null ? mCtx.Game.AddTeamFromGroup() : 0;
        Group()?.AssignName(this, StringTable.Global().Intern("team" + (char)('0' + Team)));
        return base.OnAdd();
    }
}

// src/game/mission_objects.cpp DebrisCloudObject: DebrisCloud (0x004741d0), an object that is never ghosted
// (docs/game-stream-ghosts.md 6.9); it takes no arguments.
internal sealed class DebrisCloudObject : SimObject
{
    public override string ClassName() => "DebrisCloud";
}

public static class MissionClasses
{
    private static readonly PersistDef[] kGameBase = { new("dataBlock", 0, 1), new("name", 1, 1) };

    private static PersistDef[] Plus(PersistDef[] a, params PersistDef[] b) => a.Concat(b).ToArray();

    // src/game/mission_objects.cpp registerMissionClasses: registers every mission class with the sim class registry.
    public static void Register()
    {
        SensorManager.Register();
        PersistDef[] shape = Plus(kGameBase, new("position", 5, 1), new("rotation", 5, 1), new("destroyable", 4, 1));
        PersistDef[] staticBase = Plus(shape, new PersistDef("deleteOnDestroy", 4, 1));
        PersistDef[] item = Plus(staticBase, new("rotates", 4, 1), new("collideable", 4, 1), new("count", 2, 1));
        PersistDef[] marker = Plus(kGameBase, new("position", 5, 1), new("rotation", 5, 1));
        PersistDef[] trigger = Plus(marker, new("boundingBox", 8, 1), new("isSphere", 4, 1));
        PersistDef[] interior =
        {
            new("filename", 0, 1), new("isContainer", 2, 1), new("position", 5, 1), new("rotation", 5, 1), new("lightParams", 22, 1),
        };
        void Gb(string cls, GameBase.Kind kind, int dbGroup, PersistDef[] f, int set = 0, int leave = 0) =>
            SimClasses.RegisterClass(cls, c => new GameBase(cls, kind, dbGroup, f, c, set, leave));
        Gb("StaticShape", GameBase.Kind.ShapeBase, 4, staticBase, 719);
        Gb("Item", GameBase.Kind.ShapeBase, 5, item, 719);
        Gb("Mine", GameBase.Kind.ShapeBase, 23, item, 719);
        Gb("Sensor", GameBase.Kind.ShapeBase, 8, staticBase, 719);
        Gb("Turret", GameBase.Kind.ShapeBase, 19, staticBase, 719);
        Gb("Moveable", GameBase.Kind.ShapeBase, 7, staticBase, 718);
        // P4b: Vehicle: registerClass("Flier", makeFlier(c, staticBase)) and registerClass("Vehicle", makeVehicle(c,
        // staticBase)) (src/game/vehicle.cpp).
        Gb("Marker", GameBase.Kind.Marker, 21, marker);
        Gb("Trigger", GameBase.Kind.Trigger, 26, trigger);
        Gb("InteriorShape", GameBase.Kind.Interior, -1, interior, 718);

        SimClasses.RegisterClass("DebrisCloud", _ => new DebrisCloudObject());
        SimClasses.RegisterClass("TeamGroup", c => new TeamGroup(c));
        SimClasses.RegisterClass("DropPointGroup", _ => new PersistGroup("DropPointGroup", Array.Empty<PersistDef>()));
        SimClasses.RegisterClass("SimPath", _ => new SimPathObject());

        void Obj(string cls, PersistDef[] f, int fixedId = 0, string? required = null) =>
            SimClasses.RegisterClass(cls, c => new PersistObject(cls, f, c, fixedId, required));
        Obj("Sky", new PersistDef[]
        {
            new("dmlName", 0, 1), new("skyColor", 5, 1), new("hazeColor", 5, 1), new("featurePosition", 3, 1), new("size", 3, 1),
            new("textures", 2, 16),
        }, 729);
        Obj("StarField", new PersistDef[] { new("inFrontOfSky", 4, 1), new("colors", 5, 3) });
        Obj("Planet", new PersistDef[]
        {
            new("fileName", 0, 1), new("azimuth", 3, 1), new("incidence", 3, 1), new("castShadows", 4, 1), new("useLensFlare", 4, 1),
            new("intensity", 5, 1), new("ambient", 5, 1), new("size", 3, 1), new("distance", 3, 1),
        });
        Obj("MissionCenterPos", new PersistDef[] { new("x", 3, 1), new("y", 3, 1), new("w", 3, 1), new("h", 3, 1) }, 716);
        Obj("SimPalette", new PersistDef[] { new("fileName", 0, 1) }, 0, "fileName");
        PersistDef[] terrainFields =
        {
            new("tedFileName", 0, 1), new("visibleDistance", 3, 1), new("hazeDistance", 3, 1), new("hazeVerticalMin", 3, 1),
            new("hazeVerticalMax", 3, 1), new("perspectiveDistance", 3, 1), new("screenSize", 3, 1), new("contGravity", 5, 1),
            new("contDrag", 3, 1), new("contHeight", 3, 1), new("position", 5, 1), new("rotation", 5, 1),
        };
        SimClasses.RegisterClass("SimTerrain", c => new TerrainObject(c, terrainFields));
        SimClasses.RegisterClass("SimLight", c => new SimLightObject(new PersistDef[]
        {
            new("type", 2, 1), new("range", 3, 1), new("color", 5, 1), new("ambient", 5, 1), new("position", 5, 1),
            new("aim", 5, 1), new("isStatic", 4, 1),
        }, c));
        Obj("Snowfall", new PersistDef[] { new("intensity", 3, 1), new("wind", 5, 1), new("suspendRendering", 4, 1), new("rain", 4, 1) }, 714);
    }
}
