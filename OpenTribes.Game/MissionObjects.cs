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

/// <summary>The definition of a persist field.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Type">The type number (docs/datablocks.md 3).</param>
/// <param name="Count">The number of array elements.</param>
public readonly record struct PersistDef(string Name, int Type, int Count);

/// <summary>The shared persist field storage: raw values plus parsed point3F fields. Array fields keep one value per element.</summary>
public sealed class PersistFields
{
    private readonly List<PersistDef> mDefs;
    private readonly Dictionary<string, List<string>> mValues = new(); // lower-case field name
    private readonly Dictionary<string, float[]> mPoints = new();

    /// <summary>Creates the storage for a list of field definitions.</summary>
    /// <param name="defs">The field definitions.</param>
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

    /// <summary>Sets a field.</summary>
    /// <param name="field">The field name, compared without case.</param>
    /// <param name="values">The values.</param>
    /// <returns>False when the field is not defined or the values are not accepted.</returns>
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

    /// <summary>Sets one element of an array field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="index">The element index.</param>
    /// <param name="values">The values.</param>
    /// <returns>False when the field or index is not accepted.</returns>
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

    /// <summary>Replaces a point3F field. Engine code uses it, not the persist setter.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="v">The point.</param>
    public void SetPoint(string field, ReadOnlySpan<float> v) => mPoints[Lower(field)] = new[] { v[0], v[1], v[2] };

    /// <summary>Returns the values of a field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="index">The element index, or -1 for the whole field.</param>
    /// <returns>The values, or null when the field has none.</returns>
    public IReadOnlyList<string>? Get(string field, int index = -1) =>
        mValues.TryGetValue(ElementKey(Lower(field), index), out List<string>? v) ? v : null;

    /// <summary>Reads a point3F field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="output">Receives the point.</param>
    /// <param name="index">The element index, or -1.</param>
    /// <returns>False when the field has no point.</returns>
    public bool Point(string field, Span<float> output, int index = -1)
    {
        if (!mPoints.TryGetValue(ElementKey(Lower(field), index), out float[]? p))
            return false;
        for (int i = 0; i < 3; ++i)
            output[i] = p[i];
        return true;
    }

    /// <summary>Reads a string field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public string Str(string field, string def = "")
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? v[0] : def;
    }

    /// <summary>Reads a real field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public float Real(string field, float def)
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? AtofRZ(v[0]) : def;
    }

    /// <summary>Reads an integer field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <param name="index">The element index, or -1.</param>
    /// <returns>The value.</returns>
    public int Integer(string field, int def, int index = -1)
    {
        IReadOnlyList<string>? v = Get(field, index);
        return v != null && v.Count != 0 ? Numbers.BorlandAtoi(v[0]) : def;
    }

    /// <summary>Reads a boolean field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public bool Boolean(string field, bool def)
    {
        IReadOnlyList<string>? v = Get(field);
        return v != null && v.Count != 0 ? ParseBool(v[0]) : def;
    }

    /// <summary>Reads a point3F field with a default.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="output">Receives the point.</param>
    /// <param name="x">The default x.</param>
    /// <param name="y">The default y.</param>
    /// <param name="z">The default z.</param>
    /// <param name="index">The element index, or -1.</param>
    public void Point(string field, Span<float> output, float x, float y, float z, int index = -1)
    {
        if (Point(field, output, index))
            return;
        output[0] = x;
        output[1] = y;
        output[2] = z;
    }
}

/// <summary>Small helpers shared by the game files: C library string compare and number text.</summary>
public static class GameUtil
{
    /// <summary>Compares two strings for equality without case (<c>strcasecmp == 0</c>, ASCII).</summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>True when equal.</returns>
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

    /// <summary>Orders two strings without case (<c>strcasecmp</c>, ASCII).</summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>Less than 0, 0 or greater than 0.</returns>
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

    /// <summary>Formats a float as <c>%g</c>, as the script prints it.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string G(float v) => Numbers.FormatFloat(v);

    /// <summary>Formats three floats as "x y z".</summary>
    /// <param name="v">The vector.</param>
    /// <returns>The text.</returns>
    public static string Fmt3(ReadOnlySpan<float> v) => G(v[0]) + " " + G(v[1]) + " " + G(v[2]);

    /// <summary>Parses "x y z" like <c>sscanf(s, "%f %f %f")</c> into a zeroed vector. The scan3 helpers of the command files use it.</summary>
    /// <param name="s">The text.</param>
    /// <param name="v">Receives the vector.</param>
    public static void Scan3(string s, Span<float> v)
    {
        v[0] = v[1] = v[2] = 0;
        CScan.Floats(s, v);
    }

    /// <summary>Converts a double as <c>crt_ftol</c> does: truncation toward zero, 0x80000000 when out of range.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The converted value.</returns>
    public static uint Ftol(double v)
    {
        if (!(v > -2147483649.0 && v < 2147483648.0))
            return 0x80000000u;
        return unchecked((uint)(int)v);
    }

    /// <summary>Formats an integer.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string Itoa(int v) => Numbers.FormatInt(v);
}

/// <summary>The base class of the mission objects: the GameBase family.</summary>
public partial class GameBase : SimObject
{
    /// <summary>The kind of a GameBase.</summary>
    public enum Kind
    {
        /// <summary>A ShapeBase object.</summary>
        ShapeBase,
        /// <summary>A Marker.</summary>
        Marker,
        /// <summary>A Trigger.</summary>
        Trigger,
        /// <summary>An interior.</summary>
        Interior,
    }

    /// <summary>The C++ class behind the ShapeBase kind. It decides the packUpdate layout and the script state hooks.</summary>
    public enum ShapeKind
    {
        /// <summary>Not a shape class.</summary>
        NoShape,
        /// <summary>A StaticShape.</summary>
        StaticShape,
        /// <summary>A Sensor.</summary>
        Sensor,
        /// <summary>A Turret.</summary>
        Turret,
        /// <summary>An Item.</summary>
        Item,
        /// <summary>A Mine.</summary>
        Mine,
        /// <summary>A Moveable.</summary>
        Moveable,
        /// <summary>A Vehicle or Flier.</summary>
        Vehicle,
    }

    /// <summary>The damage state: none.</summary>
    public const int kNone = 0;
    /// <summary>The damage state: enabled.</summary>
    public const int kEnabled = 1;
    /// <summary>The damage state: disabled.</summary>
    public const int kDisabled = 2;
    /// <summary>The damage state: destroyed.</summary>
    public const int kDestroyed = 3;
    /// <summary>The thread flag mask of the state bits.</summary>
    public const byte kThreadState = 3;
    /// <summary>The thread flag: at end.</summary>
    public const byte kThreadAtEnd = 4;
    /// <summary>The thread flag: forward.</summary>
    public const byte kThreadForward = 8;
    /// <summary>The thread state: play.</summary>
    public const int kPlay = 0;
    /// <summary>The thread state: stop.</summary>
    public const int kStop = 1;
    /// <summary>The thread state: pause.</summary>
    public const int kPause = 2;
    /// <summary>The move state: none.</summary>
    public const int kMoveNone = 0;
    /// <summary>The move state: forward.</summary>
    public const int kMoveForward = 1;
    /// <summary>The move state: backward.</summary>
    public const int kMoveBackward = 2;
    /// <summary>The move state: stopped.</summary>
    public const int kMoveStopped = 3;

    /// <summary>An animation thread slot of a ShapeBase.</summary>
    public sealed class Thread
    {
        /// <summary>The sequence index, -1 for none (+0x750 + 0x10 * slot).</summary>
        public int Sequence = -1;
        /// <summary>The flags (+0x754): bits 0 and 1 state, 4 at end, 8 forward.</summary>
        public byte Flags;
        /// <summary>True when the shape instance thread exists (+0x748).</summary>
        public bool Created;
        /// <summary>The position, 0 to 1 (thread +0x1c).</summary>
        public float Position;
        /// <summary>The speed (thread +0x18).</summary>
        public float Speed;
    }

    /// <summary>A collision callback record: per other object, the manager time before which no further onCollision runs.</summary>
    public struct CollisionRecord
    {
        /// <summary>The id of the other object.</summary>
        public int Other;
        /// <summary>The manager time at which the record expires.</summary>
        public float Expiry;
    }

    /// <summary>The Item datablock values.</summary>
    public sealed class ItemBlock
    {
        /// <summary>The mass.</summary>
        public float Mass = 1;
        /// <summary>The drag.</summary>
        public float Drag = 1;
        /// <summary>The density.</summary>
        public float Density = 1;
        /// <summary>The friction.</summary>
        public float Friction = 1;
        /// <summary>The elasticity.</summary>
        public float Elasticity = 0.2f;
    }

    /// <summary>The MineData values (vtable block at 0x004588f8).</summary>
    public sealed class MineBlock
    {
        /// <summary>The damage type.</summary>
        public int DamageType;
        /// <summary>The damage value.</summary>
        public float DamageValue = 1;
        /// <summary>The explosion radius.</summary>
        public float ExplosionRadius = 1;
        /// <summary>The kick.</summary>
        public float Kick = 100;
    }

    /// <summary>The gravity force (type 1) and container drag (+0x124) taken on terrain container entry, for Items and projectiles.</summary>
    /// <remarks>Follows SimTerrain_enterContainer (0x005195b4, docs/player-sim.md FC2).</remarks>
    public sealed class EntryForcesRec
    {
        /// <summary>The terrain container entry at the last refile, or null outside.</summary>
        public object? Terrain;
        /// <summary>The parent container entry.</summary>
        public object? Parent;
        /// <summary>The gravity force.</summary>
        public readonly float[] Gravity = new float[3];
        /// <summary>The container drag.</summary>
        public float Drag;
    }

    /// <summary>The SensorData fields, also read from a TurretData.</summary>
    public sealed class SensorBlock
    {
        /// <summary>True when the sensor casts a line of sight.</summary>
        public bool CastLOS;
        /// <summary>The detection range.</summary>
        public int Range = 100;
        /// <summary>The doppler value.</summary>
        public int Doppler;
        /// <summary>True when the sensor suppresses others.</summary>
        public bool Supression;
        /// <summary>True when the sensor can be suppressed.</summary>
        public bool Supressable = true;
        /// <summary>True when the sensor pings.</summary>
        public bool Pinger = true;
    }

    /// <summary>The TurretData values.</summary>
    public sealed class TurretBlock
    {
        /// <summary>The gun range, -1 for unset.</summary>
        public float GunRange = -1;
        /// <summary>The speed.</summary>
        public float Speed = 1;
        /// <summary>The speed modifier.</summary>
        public float SpeedModifier;
        /// <summary>The reload delay.</summary>
        public float ReloadDelay;
        /// <summary>The minimum gun energy.</summary>
        public float MinGunEnergy;
        /// <summary>The maximum gun energy.</summary>
        public float MaxGunEnergy;
        /// <summary>The field of view, -1 for unset (+0x14c).</summary>
        public float Fov = -1;
        /// <summary>The deflection.</summary>
        public float Deflection;
        /// <summary>The energy rate.</summary>
        public float EnergyRate;
        /// <summary>The targetable field of view ratio.</summary>
        public float TargetableFovRatio = 0.5f;
        /// <summary>True for a sustained weapon.</summary>
        public bool Sustained;
        /// <summary>True when the turret has a projectile.</summary>
        public bool HasProjectile;
        /// <summary>The firing time in milliseconds.</summary>
        public int FiringTimeMS;
    }

    /// <summary>The fields of the last move the turret reads (+0x8d8).</summary>
    public struct TurretMove
    {
        /// <summary>The turn input.</summary>
        public float Turn;
        /// <summary>The pitch input.</summary>
        public float Pitch;
        /// <summary>The jump input.</summary>
        public bool Jump;
        /// <summary>The fire input.</summary>
        public bool Fire;
    }

    /// <summary>The MoveableData values (datablock group 7) read at bind.</summary>
    public sealed class MoveableBlock
    {
        /// <summary>True when the Moveable displaces objects (+0x100).</summary>
        public bool Displace = true;
        /// <summary>True when the Moveable blocks forward (+0x101).</summary>
        public bool BlockForward = true;
        /// <summary>True when the Moveable blocks backward (+0x102).</summary>
        public bool BlockBackward = true;
        /// <summary>The speed (+0x114).</summary>
        public float Speed = 1;
    }

    /// <summary>An occupant of a Trigger.</summary>
    public struct Occupant
    {
        /// <summary>The id of the occupant object.</summary>
        public int Object;
        /// <summary>The time at which the occupant expires.</summary>
        public float Expiry;
        /// <summary>The time of the next contact event.</summary>
        public float NextContact;
    }

    /// <summary>The team id, -1 for none.</summary>
    public int Team = -1;
    /// <summary>The datablock name, interned.</summary>
    public string? DataBlockName;
    /// <summary>The map name. A ShapeBase starts with "", and null prints False.</summary>
    public string? MapName;            // ShapeBase starts with ""; null prints False
    /// <summary>The sensor target index, when the datablock is visibleToSensor.</summary>
    public int TargetIndex = -1;
    /// <summary>The object transform.</summary>
    public Transform Transform = new();
    /// <summary>The collision geometry of an InteriorShape.</summary>
    public InteriorGeometry? InteriorGeo;
    /// <summary>The InteriorShape isContainer value (+0x244), -1 until a shape loads. Part of the InteriorShape ghost data (docs/game-stream-ghosts.md 3.4), with the state words and light records.</summary>
    public int IsContainer = -1;
    /// <summary>The InteriorShape state words and light records.</summary>
    public InteriorNetInfo InteriorNet = new();
    /// <summary>The resources of the ITRInstance (Interior.LoadInteriorResources).</summary>
    public readonly ResourceLocks ResourceLocks = new();
    /// <summary>True when collision is disabled (+0x41c).</summary>
    public bool CollisionDisabled;

    /// <summary>The id of the controlling client (+0x230). Client links are in docs/player-manager.md 12.5, the control object interface in docs/game-stream.md 6.5.</summary>
    public int ControlClient;
    /// <summary>The id of the last non-zero owner client (+0x234).</summary>
    public int LastOwnerClient;
    /// <summary>The id of the owner client (+0x238).</summary>
    public int OwnerClient;
    /// <summary>The field of view, stored by the PlayerPSC reader (+0x22c).</summary>
    public float Fov = 1.57079637f;
    /// <summary>True when the object is a client ghost (+0x4c bit 0x02). The client prediction sets it on its copy of the control object, and the step then skips the server-only parts (Player S7 crouch, scripts, Flier fire and jump). Never set on the server (docs/client-net.md 4.4).</summary>
    public bool ClientSide;

    /// <summary>The index in the datablock group (+0x220), -1 for none (docs/game-stream-ghosts.md 4.1).</summary>
    public int DataBlockIndex = -1;
    /// <summary>True when the object is a power generator (+0x204).</summary>
    public bool PowerGenerator;
    /// <summary>The number of generators in the groups above (+0x208).</summary>
    public int PowerCount;

    /// <summary>The animation threads (ShapeBase net state, docs/game-stream-ghosts.md 4.2).</summary>
    public readonly Thread[] Threads = { new(), new(), new() };
    /// <summary>The damage flash direction (+0x738).</summary>
    public readonly float[] FlashNormal = new float[3];
    /// <summary>The damage flash amount (+0x744).</summary>
    public float FlashAmount;
    /// <summary>The fade state (+0x280): 0 none, 1 fade out, 2 fade in (2.5 s).</summary>
    public int FadeState;
    /// <summary>The tick time in ms at startFadeOut or startFadeIn (+0x284).</summary>
    public uint FadeStart;
    /// <summary>The Euler angles as given by the persist field (+0x244).</summary>
    public readonly float[] Rotation = new float[3];
    /// <summary>The velocity (+0x194).</summary>
    public readonly float[] Velocity = new float[3];
    /// <summary>The damage (+0x72c).</summary>
    public float Damage;
    /// <summary>The maximum damage (datablock +0x5c).</summary>
    public float MaxDamage = 1.0f;
    /// <summary>The ratio of maximum damage at which the object is disabled (datablock +0xec).</summary>
    public float DisabledLevel = 0.5f;
    /// <summary>The ratio of maximum damage at which the object is destroyed (datablock +0xf0).</summary>
    public float DestroyedLevel = 0.75f;
    /// <summary>The damage state (+0x7d4). The StaticBase constructor sets 1 (0x0044f430).</summary>
    public int DamageState = kEnabled;
    /// <summary>True when the object is active (+0x7d0, GameBase::setActive).</summary>
    public bool Active;
    /// <summary>True when the object is a target (+0x7d1, GameBase::setIsTarget, set 725).</summary>
    public bool IsTarget;
    /// <summary>The energy recharge rate (+0x728).</summary>
    public float RechargeRate;
    /// <summary>The energy (+0x724).</summary>
    public float Energy;
    /// <summary>The maximum energy (datablock +0x58).</summary>
    public float MaxEnergy;
    /// <summary>The repair rate (datablock +0x60).</summary>
    public float RepairRate = 0.1f;
    /// <summary>The pending repair (+0x730).</summary>
    public float RepairPending;
    /// <summary>The automatic repair rate (+0x734).</summary>
    public float AutoRepairRate;
    /// <summary>True when the object is deleted when destroyed (+0x7e4). A persist field. A Mine and a collideable Item force it.</summary>
    public bool DeleteOnDestroy;
    /// <summary>True once the datablock is resolved (+0x7cc).</summary>
    public bool ShapeBound;
    /// <summary>The shape animation.</summary>
    public ShapeAnim? Anim;

    /// <summary>The collision callback records (+0x214, docs/static-sim.md 1.8).</summary>
    public readonly List<CollisionRecord> CollisionRecords = new();
    /// <summary>The latest expiry of the collision records.</summary>
    public float CollisionLatest;

    /// <summary>True when the Item rotates (+0x888). Item and Mine state is in docs/static-sim.md 5.</summary>
    public bool ItemRotates;
    /// <summary>True when the Item is collideable (+0x889).</summary>
    public bool ItemCollideable;
    /// <summary>The object that threw the Item (+0x878).</summary>
    public GameBase? ItemThrower;
    /// <summary>True when the Item is at rest (+0x164 bit 0x80).</summary>
    public bool AtRest;
    /// <summary>The item count (+0x868).</summary>
    public int ItemCount;
    /// <summary>True when the Item is visible (+0x864 bit 0). Hidden items leave their container.</summary>
    public bool ItemVisible = true;
    /// <summary>The pop flag (+0x864 bit 1). Stored and read by nothing registered.</summary>
    public bool ItemPop;
    /// <summary>The previous at-rest state (+0x86c).</summary>
    public bool ItemPrevAtRest = true;
    /// <summary>The time of the throw (+0x870).</summary>
    public float ItemThrowTime;
    /// <summary>The time of the next update (+0x874).</summary>
    public float ItemNextUpdate;
    /// <summary>The client id of the thrower (+0x880).</summary>
    public int ItemThrowerClient = -1;
    /// <summary>The join counter of the thrower (+0x884).</summary>
    public int ItemThrowerJoin = -1;
    /// <summary>True when a contact surface was hit this step (+0x1f4).</summary>
    public bool ItemContact;
    /// <summary>The notify list of the running move (info +0x3c).</summary>
    public List<SimObject> ItemNotify = new();
    /// <summary>The sub step length (+0x250): 0.45 times the shape radius.</summary>
    public float ItemSubStep = 0.3f;
    /// <summary>The centre of the shape bounding sphere (shape +0x08).</summary>
    public readonly float[] ShapeCenter = new float[3];
    /// <summary>The radius of the shape bounding sphere (shape +0x04).</summary>
    public float ShapeRadius;
    /// <summary>The shape box in object space (image +0x4c).</summary>
    public readonly float[] ShapeBox = new float[6];
    /// <summary>The shape box enlarged by the trigger radius (the object box).</summary>
    public readonly float[] ShapeQueryBox = new float[6];
    /// <summary>The datablock triggerRadius. The sphere image +0x778 is used when it is not 0.</summary>
    public float ShapeTriggerRadius;
    /// <summary>The Item datablock values.</summary>
    public readonly ItemBlock ItemData = new();
    /// <summary>The MineData values.</summary>
    public readonly MineBlock MineData = new();
    /// <summary>The mine surface normal (+0x88c).</summary>
    public readonly float[] MineNormal = new float[3];
    /// <summary>True when the mine is aligned to the surface (+0x898).</summary>
    public bool MineAligned;
    /// <summary>The movement flags (+0x164): 0x181 plus 0x1d. A displacement clears 0x05.</summary>
    public int ItemFlags = 0x19d;
    /// <summary>The ids of the SimMovement objects in contact (+0x154). The Moveable carry list (docs/collision.md E3.3).</summary>
    public readonly List<int> Carried = new();
    /// <summary>True when the shape has a <c>visibility</c> sequence (+0x7e0).</summary>
    public bool VisibilityThread;
    /// <summary>The magnitude of the container gravity (+0x14c).</summary>
    public float ItemGravity;
    /// <summary>The terrain entry forces.</summary>
    public readonly EntryForcesRec EntryForces = new();

    /// <summary>The SensorData values (docs/static-sim.md 3).</summary>
    public readonly SensorBlock SensorData = new();
    /// <summary>The sensor word (+0x200): detected bits 0 to 7, always visible 8 to 15, state 24 to 31.</summary>
    public uint SensorWord;
    /// <summary>True when the object is a member of a SensorManager list.</summary>
    public bool SensorListed;
    /// <summary>The next object id of the jamming player list (Player +0x7d4, docs/static-sim.md 3.8).</summary>
    public int JamNext;
    /// <summary>The previous object id of the jamming player list (Player +0x7d8).</summary>
    public int JamPrev;
    /// <summary>The turret rotation (+0x8b4). The constructor sets pi, and it is 0 once the shape has a turn thread (docs/static-sim.md 4.5).</summary>
    public float TurretRotation = 3.14159274f;
    /// <summary>The turret elevation (+0x8b8).</summary>
    public float TurretElevation;
    /// <summary>The TurretData values.</summary>
    public readonly TurretBlock TurretData = new();
    /// <summary>The sim time until which an enemy keeps the turret awake (+0x8ac).</summary>
    public float TurretSleep;
    /// <summary>The time of the next shot (+0x8b0).</summary>
    public float TurretNextFire;
    /// <summary>The position, 0 to 1, of the power and fire thread (+0x8a4).</summary>
    public float TurretPowerPos;
    /// <summary>True when the shape has the sequences <c>fire</c> and <c>power</c>.</summary>
    public bool TurretHasPower;
    /// <summary>The duration of the <c>power</c> sequence.</summary>
    public float TurretPowerDuration;
    /// <summary>The duration of the <c>fire</c> sequence.</summary>
    public float TurretFireDuration;
    /// <summary>True when the power and fire thread runs <c>fire</c> (priority 0), else <c>power</c> (-1).</summary>
    public bool TurretPowerOnFire;
    /// <summary>The maximum rotation (+0x8c4). Equal maximum and minimum mean unlimited.</summary>
    public float TurretRotationMax;
    /// <summary>The minimum rotation (+0x8c8).</summary>
    public float TurretRotationMin;
    /// <summary>The maximum elevation (+0x8bc).</summary>
    public float TurretMaxElevation = 0.785398185f;
    /// <summary>The minimum elevation (+0x8c0).</summary>
    public float TurretMinElevation = -0.785398185f;
    /// <summary>The fire state (+0x908).</summary>
    public int TurretFireState;
    /// <summary>The mode (+0x8cc): 0 off, 1 deactivating, 2 activating, 3 on.</summary>
    public int TurretMode;
    /// <summary>The barrel state (+0x918): 1 from onAdd on a server.</summary>
    public int TurretBarrelState;
    /// <summary>The engine time in ms of the current tick (DAT_006a8494 + 4).</summary>
    public uint TurretTickMs;
    /// <summary>The projectile speed (+0x8d4), resolved on first use (docs/static-sim.md 4.2 step 2). Negative means unresolved.</summary>
    public float TurretProjectileSpeed = -1;
    /// <summary>The in-range counter (+0x8a8).</summary>
    public int TurretInRange;
    /// <summary>The id of the preferred target (+0x900). The original keeps a raw pointer.</summary>
    public int TurretPreferred;
    /// <summary>The id of the beam target (+0x920).</summary>
    public int TurretBeamTarget;
    /// <summary>The id of the beam projectile (+0x924).</summary>
    public int TurretBeamProjectile;
    /// <summary>The stamp in ms (+0x91c).</summary>
    public uint TurretStamp;
    /// <summary>The move credit (+0x8f8).</summary>
    public int TurretCredit;
    /// <summary>The position of the turn thread (+0x8a0), negative when there is no thread.</summary>
    public float TurretTurnPos = -1;
    /// <summary>The position of the elevate thread (+0x89c), negative when there is no thread.</summary>
    public float TurretElevatePos = -1;
    /// <summary>The last move (+0x8d8).</summary>
    public TurretMove TurretLastMove;

    /// <summary>The move state (+0x868). Moveable state is in docs/static-sim.md 2.</summary>
    public int MoveState = kMoveNone;
    /// <summary>True when the move is blocked (+0x86c).</summary>
    public bool MoveBlocked;
    /// <summary>The target waypoint (+0x870).</summary>
    public int MoveTarget = 15;
    /// <summary>The path id (+0x87c).</summary>
    public int PathId = 0xff;
    /// <summary>The current waypoint (+0x884).</summary>
    public int Waypoint;
    /// <summary>The time on the current segment (+0x888).</summary>
    public float WaypointTime;
    /// <summary>The number of waypoints (+0x89c).</summary>
    public int WaypointCount;
    /// <summary>The MoveableData values.</summary>
    public readonly MoveableBlock MoveData = new();
    /// <summary>The waypoint of the segment cache (+0x88c to +0x8cc).</summary>
    public int AppliedWaypoint = -1;
    /// <summary>The time of the segment cache.</summary>
    public float AppliedTime;
    /// <summary>The cached segment.</summary>
    public int CachedSegment = -1;
    /// <summary>The segment time.</summary>
    public float SegmentTime = 1.0f;
    /// <summary>The segment start position.</summary>
    public readonly float[] SegPos = new float[3];
    /// <summary>The segment start rotation.</summary>
    public readonly float[] SegRot = new float[3];
    /// <summary>The position delta of the segment.</summary>
    public readonly float[] SegDPos = new float[3];
    /// <summary>The rotation delta of the segment.</summary>
    public readonly float[] SegDRot = new float[3];
    /// <summary>A test override of the answer of <c>GameServices.MoveableDisplace</c>.</summary>
    public Func<GameBase, bool>? MoveableDisplaceOverride;
    /// <summary>A test override of the answer of <c>GameServices.MoveableTest</c>.</summary>
    public Func<GameBase, bool>? MoveableTestOverride;

    /// <summary>True when the Trigger is active (+0x250). Trigger state is in docs/static-sim.md 6.</summary>
    public bool TriggerActive = true;
    /// <summary>The boundingBox in object space (+0x1d4).</summary>
    public readonly float[] TriggerBox = { -1, -1, -1, 1, 1, 1 };
    /// <summary>True when the Trigger is a sphere (+0x251, isSphere).</summary>
    public bool TriggerSphere = true;
    /// <summary>The occupants (+0x240).</summary>
    public readonly List<Occupant> Occupants = new();
    /// <summary>The contact interval (datablock +0x34).</summary>
    public float TriggerInterval = 0.25f;

    private ShapeKind mShapeClass = ShapeKind.NoShape;
    private readonly string mClass;
    private readonly Kind mKind;
    private readonly int mDbGroup;
    private readonly PersistFields mFields;
    private readonly ClassContext mCtx;
    private readonly int mSetId, mLeaveSetId;

    /// <summary>Creates a GameBase.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="kind">The kind of object.</param>
    /// <param name="dataBlockGroup">The datablock group.</param>
    /// <param name="fields">The persist fields.</param>
    /// <param name="ctx">The class context.</param>
    /// <param name="setId">The id of the set the object joins.</param>
    /// <param name="leaveSetId">The id of the set the object leaves.</param>
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

    /// <inheritdoc/>
    public override string ClassName() => mClass;
    /// <summary>Returns the script class name.</summary>
    /// <returns>The class name.</returns>
    public string ScriptClass() => mClass;
    /// <summary>Returns the C++ class behind the ShapeBase kind.</summary>
    /// <returns>The shape kind.</returns>
    public ShapeKind ShapeClass() => mShapeClass;
    /// <summary>Tells whether the object is built on the StaticBase constructor (0x0044f430), as a Vehicle is (0x00444fe4).</summary>
    /// <returns>True for the StaticBase family.</returns>
    public bool IsStaticBase() => mShapeClass != ShapeKind.NoShape;
    /// <summary>Tells whether the object is a ShapeBase.</summary>
    /// <returns>True for the ShapeBase kind.</returns>
    public bool IsShapeBase() => mKind == Kind.ShapeBase;
    /// <summary>Tells whether the object is an interior.</summary>
    /// <returns>True for the Interior kind.</returns>
    public bool IsInterior() => mKind == Kind.Interior;
    /// <summary>Returns the persist fields.</summary>
    /// <returns>The fields.</returns>
    public PersistFields Fields() => mFields;
    /// <summary>Returns the datablock group.</summary>
    /// <returns>The group.</returns>
    public int DataBlockGroup() => mDbGroup;
    /// <summary>Returns the number of datablocks in the group.</summary>
    /// <returns>The count, 0 without a game.</returns>
    public int DataBlockCount() => mCtx.Game != null ? mCtx.Game.DataBlockCount(mDbGroup) : 0;
    /// <summary>Returns the kind of the object.</summary>
    /// <returns>The kind.</returns>
    public Kind GetKind() => mKind;
    /// <summary>Returns the game services.</summary>
    /// <returns>The services, or null.</returns>
    public GameServices? Services() => mCtx.Game;
    /// <summary>Returns the class context.</summary>
    /// <returns>The context.</returns>
    public ClassContext Context() => mCtx;
    /// <summary>Reads a point3F persist field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="output">Receives the point.</param>
    /// <returns>False when the field has no point.</returns>
    public bool PersistPoint(string field, Span<float> output) => mFields.Point(field, output);
    /// <inheritdoc/>
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);

    /// <summary>Returns the damage flash of a control object (vtable +0x100).</summary>
    /// <returns>The flash amount.</returns>
    public virtual float DamageFlash() => 0;
    /// <summary>Computes the throw direction of an object that throws along its own aim (vtable +0x104, Player 0x004113e8).</summary>
    /// <param name="output">Receives the direction.</param>
    /// <returns>False for the GameBase rule.</returns>
    public virtual bool ThrowDirection(Span<float> output) => false;
    /// <summary>Called after the datablock binds (vtable +0x130 chain, Vehicle_onNewDataBlock 0x00445230).</summary>
    public virtual void OnNewDataBlock() { }
    /// <summary>Returns the world transform of a mount point (vtable +0x16c, Vehicle 0x00445c38). Here it is the object transform.</summary>
    /// <param name="point">The mount point.</param>
    /// <param name="output">Receives the transform.</param>
    public virtual void GetMountTransform(int point, out Transform output) => output = Transform;
    // vtable +0x110 (eye transform), +0x150 (moves) and +0x15c (control state): TurretFire.cs (turret_fire.cpp,
    // turret_control.cpp).

    /// <inheritdoc/>
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

    /// <summary>Sets the team: sets mask 4, then calls the script.</summary>
    /// <param name="t">The team id.</param>
    /// <remarks>Follows 0x00448368.</remarks>
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

    /// <summary>Adds bits to the dirty mask of the object's ghosts.</summary>
    /// <param name="bits">The mask bits.</param>
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

    /// <inheritdoc/>
    /// <remarks>Follows 0x004482d0: the power count comes from the groups above.</remarks>
    public override void OnGroupAdd()
    {
        InitPower();
        if (PowerGenerator)
            PropagatePower(this, Group(), true);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x0044830c.</remarks>
    public override void OnGroupRemove()
    {
        if (PowerGenerator && Manager() != null)
            PropagatePower(this, Group(), false);
    }

    /// <summary>Switches the power generated by this object.</summary>
    /// <param name="on">True to generate power.</param>
    /// <remarks>Follows 0x004485f4.</remarks>
    public void GeneratePower(bool on)
    {
        if (on == PowerGenerator || Manager() == null)
            return;
        PowerGenerator = on;
        PropagatePower(this, Group(), on);
    }

    /// <summary>Handles a power change from a generator.</summary>
    /// <param name="generator">The generator.</param>
    /// <remarks>Follows StaticBase 0x0044ff0c: calls the script onPower(id, powered, generator) while enabled. The Turret override (0x004586ac) first stops a firing barrel, which is not modelled.</remarks>
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

    /// <summary>Plays a sequence on a thread slot.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <param name="sequence">The sequence index.</param>
    /// <param name="reset">True to restart the sequence.</param>
    /// <returns>True when the thread plays.</returns>
    /// <remarks>Follows 0x0044c20c.</remarks>
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

    /// <summary>Stops a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c3d0.</remarks>
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

    /// <summary>Pauses a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c428.</remarks>
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

    /// <summary>Resumes a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c480.</remarks>
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

    /// <summary>Sets the direction of a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <param name="forward">True to play forward.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c4cc.</remarks>
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

    /// <summary>Sets the active state.</summary>
    /// <param name="on">The requested state.</param>
    /// <returns>The resulting state.</returns>
    /// <remarks>Follows vtable +0x130 (0x0044fbc4). A GameBase that is not a StaticBase is always active and returns the argument (0x00448628, 0x0044862c). A Trigger has its own flag (docs/static-sim.md 6.4).</remarks>
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

    /// <summary>Returns the active flag.</summary>
    /// <returns>The flag.</returns>
    /// <remarks>Follows GameBase::isActive (vtable +0x12c).</remarks>
    public bool IsActiveFlag()
    {
        if (mKind == Kind.Trigger)
            return TriggerActive;
        return IsStaticBase() ? Active : true;
    }

    /// <summary>Sets whether the object is a target.</summary>
    /// <param name="on">True to make it a target.</param>
    /// <remarks>Follows 0x0045041c.</remarks>
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

    /// <summary>Sets the damage state. Before registration only the field is stored.</summary>
    /// <param name="state">The damage state.</param>
    /// <param name="callScript">True to call the script.</param>
    /// <remarks>Follows 0x0044f9a0.</remarks>
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

    /// <summary>Updates the damage state from the damage level.</summary>
    /// <remarks>Follows 0x0044f918. The thresholds are products of maxDamage and the datablock ratios, compared unrounded (the fmul result against the stored damage).</remarks>
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

    /// <summary>Sets the damage level, clamped to [0, maxDamage].</summary>
    /// <param name="level">The new level.</param>
    /// <remarks>Follows StaticBase vtable +0x190 (0x0044fea4). A change sets mask 0x100. The state is evaluated either way.</remarks>
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

    /// <summary>Sets the position: rebuilds the transform from +0x244 and the position.</summary>
    /// <param name="p">The position.</param>
    /// <remarks>Follows vtable +0x19c (0x0044c81c). Sets mask 8 and clears at rest.</remarks>
    public void SetPosition(ReadOnlySpan<float> p)
    {
        MissionTransform.EulerToMatrix(Rotation, ref Transform);
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = p[i];
        Transform.Flags |= 2;
        SetMaskBits(0x08);
        AtRest = false;
    }

    /// <summary>Sets the position of an InteriorShape: the persist position, the rotation read back from its matrix.</summary>
    /// <param name="p">The position.</param>
    /// <remarks>Follows InteriorShape::setTransform (docs/game-stream-ghosts.md 3.4). Sets mask 0x01.</remarks>
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

    /// <summary>Sets the rotation.</summary>
    /// <param name="e">The Euler angles.</param>
    /// <remarks>Follows vtable +0x1a0. Sets mask 0x10.</remarks>
    public void SetRotation(ReadOnlySpan<float> e)
    {
        for (int i = 0; i < 3; ++i)
            Rotation[i] = e[i];
        SetMaskBits(0x10);
    }

    /// <summary>Sends the transform event of a position or rotation change.</summary>
    /// <param name="m">The new matrix.</param>
    /// <param name="p">The position.</param>
    /// <remarks>Follows 0x0044c978. <c>GameBase::setPosition</c> sends the object's own matrix and <c>GameBase::setRotation</c> the matrix of the new angles, so the stored angles are those of a matrix round trip (sine and cosine truncated to f32, then atan2), not the input.</remarks>
    public void TransformEvent(in Transform m, ReadOnlySpan<float> p)
    {
        Span<float> e = stackalloc float[3];
        MissionTransform.MatrixToEuler(m, e);
        Span<float> q = stackalloc float[3] { p[0], p[1], p[2] };
        SetRotation(e);
        SetPosition(q);
    }

    /// <summary>Stores the raw <c>name</c> slot without expansion, case-sensitive (GB7).</summary>
    /// <param name="text">The name text.</param>
    public void StoreMapName(string text) => MapName = text;

    /// <summary>Sets the map name.</summary>
    /// <param name="text">The text, or null.</param>
    /// <remarks>Follows GB7 (0x00448fd8): copies the text and replaces <c>&lt;vNAME&gt;</c> with <c>$NAME</c>. A <c>&lt;v</c> without <c>&gt;</c> is copied literally. A value that would pass 254 characters ends the copy, and at most 255 are kept.</remarks>
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

    /// <summary>Returns the world matrix of the object.</summary>
    /// <returns>The matrix.</returns>
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

    /// <inheritdoc/>
    /// <remarks>Releases the resource locks, as the C++ destructor does.</remarks>
    public override void Destroy()
    {
        ResourceLocks.Release();
        base.Destroy();
    }

    /// <inheritdoc/>
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
                OpenTribes.World.Interior.LoadInteriorResources(res, f[0], ResourceLocks);
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

    /// <inheritdoc/>
    /// <remarks>The newObject(name, class, args...) arguments are processed after registration (docs/sim.md 6.4). StaticBase 0x0044f508 takes dataBlock [, deleteOnDestroy]. Item 0x004510e8 takes dataBlock, count, pop [, rotate [, collide]]. Mine 0x00458970 takes dataBlock. The datablock binds first, so its onAdd scripts see count 0, then the other arguments are stored.</remarks>
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

    /// <inheritdoc/>
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

/// <summary>A non-GameBase mission object with persist fields and optional load checks.</summary>
/// <remarks>Used for Sky, Planet, StarField, MissionCenterPos, SimPalette, SimLight and Snowfall. Port of class PersistObject in src/game/mission_objects.hpp.</remarks>
public class PersistObject : SimObject
{
    private readonly string mClass;
    private readonly PersistFields mFields;
    private readonly ClassContext mCtx;
    private readonly int mFixed;
    private readonly string? mRequired;
    private readonly ResourceLocks mLocks = new(); // the SimPalette's .ppl

    /// <summary>Creates a persist object.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="fields">The persist field definitions.</param>
    /// <param name="ctx">The class context.</param>
    /// <param name="fixedId">The fixed object id, or 0.</param>
    /// <param name="requiredFile">The persist field that names a file the object needs, or null.</param>
    public PersistObject(string cls, IEnumerable<PersistDef> fields, ClassContext ctx, int fixedId = 0, string? requiredFile = null)
    {
        mClass = cls;
        mFields = new PersistFields(fields);
        mCtx = ctx;
        mFixed = fixedId;
        mRequired = requiredFile;
    }

    /// <inheritdoc/>
    public override string ClassName() => mClass;
    /// <inheritdoc/>
    public override int FixedId() => mFixed;
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    /// <inheritdoc/>
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => mFields.Set(field, values);
    /// <inheritdoc/>
    public override bool SetPersistElement(string field, int index, IReadOnlyList<string> values) =>
        mFields.SetElement(field, index, values);
    /// <inheritdoc/>
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);
    /// <summary>Returns the persist fields.</summary>
    /// <returns>The fields.</returns>
    public PersistFields Fields() => mFields;
    /// <summary>Returns the class context.</summary>
    /// <returns>The context.</returns>
    protected ClassContext Context() => mCtx;
    /// <summary>Returns the persist fields for a subclass to change.</summary>
    /// <returns>The fields.</returns>
    protected PersistFields MutableFields() => mFields;

    /// <inheritdoc/>
    /// <remarks>SimPalette (.ppl) and SimTerrain (.ted) fail onAdd when their file is missing.</remarks>
    public override bool OnAdd()
    {
        if (mRequired != null && mCtx.Game != null)
        {
            IReadOnlyList<string>? v = mFields.Get(mRequired);
            if (v == null || v.Count == 0 || !mCtx.Game.ResourceExists(v[0]))
                return false;
            // SimPalette_onAdd (004fd9ec) loads the palette and holds it; after the object the instance stays until
            // purge (docs/resources.md 8.7).
            if (mClass == "SimPalette" && mCtx.Game.GetResourceManager() is ResourceManager res)
                mLocks.Load(res, v[0]);
        }
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Releases the resource locks, as the C++ destructor does.</remarks>
    public override void Destroy()
    {
        mLocks.Release();
        base.Destroy();
    }
}

/// <summary>A SimLight: event 0x420 of <c>activateGroup</c> sets the colour to (v, v, v) and sends mask 0x04.</summary>
/// <remarks>See docs/game-stream-ghosts.md 3.3 and 0x0052d290. Port of class SimLightObject in src/game/mission_objects.hpp.</remarks>
public sealed class SimLightObject : PersistObject
{
    /// <summary>Creates a SimLight.</summary>
    /// <param name="fields">The persist field definitions.</param>
    /// <param name="ctx">The class context.</param>
    public SimLightObject(IEnumerable<PersistDef> fields, ClassContext ctx) : base("SimLight", fields, ctx) { }

    /// <inheritdoc/>
    /// <remarks>Follows SimLight::processArguments (0x0052cac0): <c>Directional|DirectionalWrap intR intG intB dirX dirY dirZ [ambR ambG ambB]</c> or <c>Point range intR intG intB posX posY posZ [ambR ambG ambB]</c>. The direction is normalised twice, first with the approximate length (m_Point3F_lenf), then with the exact one. Every number goes through atof and an f32 store toward zero.</remarks>
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

    /// <summary>Handles the activate event: sets the colour to (value, value, value).</summary>
    /// <param name="value">The event value.</param>
    public override void OnActivateEvent(float value)
    {
        float[] color = { value, value, value };
        MutableFields().SetPoint("color", color);
        Context().Game?.SetMaskBits(this, 0x04);
    }
}

/// <summary>The SimTerrain: loads the .dtf, joins container 7 and computes the verification CRC sent to clients (docs/terrain.md 2.1).</summary>
/// <remarks>Port of class TerrainObject in src/game/mission_objects.hpp.</remarks>
public sealed class TerrainObject : PersistObject
{
    private string mTed = "";
    private Vec3 mPos;
    private float mVisibleDistance = 3000; // docs/terrain.md 2.2 default
    private Terrain? mTerrain;
    private uint mCrc = 0xffffffffu;       // ctor value -1

    /// <summary>Creates a SimTerrain.</summary>
    /// <param name="ctx">The class context.</param>
    /// <param name="fields">The persist field definitions.</param>
    public TerrainObject(ClassContext ctx, IEnumerable<PersistDef> fields) : base("SimTerrain", fields, ctx, 8, "tedFileName") { }

    /// <summary>Returns the verification CRC sent to clients.</summary>
    /// <returns>The CRC.</returns>
    public uint Crc() => mCrc;
    /// <summary>Returns the loaded terrain.</summary>
    /// <returns>The terrain, or null.</returns>
    public Terrain? GetTerrain() => mTerrain;
    /// <summary>Returns the visible distance.</summary>
    /// <returns>The distance.</returns>
    public float VisibleDistance() => mVisibleDistance;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    /// <remarks>The terrain goes with its resource locks.</remarks>
    public override void Destroy()
    {
        mTerrain?.ReleaseLocks();
        base.Destroy();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override void OnRemove() => Context().Game?.GetContainer()?.Remove(this);
}

/// <summary>A SimGroup that creates a team when it registers (TM6).</summary>
/// <remarks>Port of class TeamGroup in src/game/mission_objects.hpp.</remarks>
public sealed class TeamGroup : SimGroup
{
    private readonly ClassContext mCtx;
    /// <summary>The team id, -1 until the group registers.</summary>
    public int Team = -1;

    /// <summary>Creates a team group.</summary>
    /// <param name="ctx">The class context.</param>
    public TeamGroup(ClassContext ctx) => mCtx = ctx;
    /// <inheritdoc/>
    public override string ClassName() => "TeamGroup";

    /// <inheritdoc/>
    public override bool TeamOf(ref int t)
    {
        t = Team;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>The team id is the current team count, and the group becomes <c>team&lt;digit&gt;</c> (TM6).</remarks>
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

/// <summary>The mission class registry setup.</summary>
public static class MissionClasses
{
    private static readonly PersistDef[] kGameBase = { new("dataBlock", 0, 1), new("name", 1, 1) };

    private static PersistDef[] Plus(PersistDef[] a, params PersistDef[] b) => a.Concat(b).ToArray();

    /// <summary>Registers every mission class with the sim class registry.</summary>
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
        SimClasses.RegisterClass("Flier", c => Vehicle.MakeFlier(c, staticBase));
        SimClasses.RegisterClass("Vehicle", c => Vehicle.MakeVehicle(c, staticBase));
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
