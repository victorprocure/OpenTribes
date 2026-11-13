// Port of src/game/mission_objects.hpp and src/game/mission_objects.cpp (namespace ot::sim): the mission object classes
// (docs/mission.md 3): the GameBase family, TeamGroup and the non-GameBase classes with persist fields. Persist fields
// are parsed but not readable as obj.field; every other instant slot becomes a dynamic field. The Transform struct and
// the Euler conversions are OpenTribes.Sim (MissionTransform.cs). GameBase is one partial class over the files of its
// C++ methods: MissionObjects.cs, StaticSim.cs, ItemSim.cs, TurretSim.cs and Damage.cs. Arithmetic follows the mode of
// Fpu; x87::f32 is X87.F32 (toward zero) and x87::Chop is Fpu.ChopScope.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The definition of a persist field.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Type">The type number (docs/datablocks.md 3).</param>
/// <param name="Count">The number of array elements.</param>
public readonly record struct PersistDef(string Name, int Type, int Count);

/// <summary>The shared persist field storage: raw values plus parsed point3F fields. Array fields keep one value per element.</summary>
public sealed class PersistFields
{
    private readonly List<PersistDef> _defs;
    private readonly Dictionary<string, List<string>> _values = []; // lower-case field name
    private readonly Dictionary<string, float[]> _points = [];

    /// <summary>Creates the storage for a list of field definitions.</summary>
    /// <param name="defs">The field definitions.</param>
    public PersistFields(IEnumerable<PersistDef> defs) => _defs = [.. defs];

    // src/game/mission_objects.cpp lower.
    internal static string Lower(string s)
    {
        var c = s.ToCharArray();
        for (var i = 0; i < c.Length; ++i)
            if (c[i] is >= 'A' and <= 'Z')
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
            for (var i = 0; i < 3; ++i)
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
        if (!_points.TryGetValue(key, out var p))
            _points[key] = p = new float[3];
        return p;
    }

    /// <summary>Sets a field.</summary>
    /// <param name="field">The field name, compared without case.</param>
    /// <param name="values">The values.</param>
    /// <returns>False when the field is not defined or the values are not accepted.</returns>
    public bool Set(string field, IReadOnlyList<string> values)
    {
        foreach (var d in _defs)
        {
            if (!SimClassesNoCase(d.Name, field))
                continue;
            var key = Lower(d.Name);
            if (d.Type == 5)
                ParsePoint(values, PointSlot(key));
            else if (d.Type == 0)
                // String table entry: case variants share the first spelling interned.
                _values[key] = [values.Count == 0 ? "" : StringTable.Global().Intern(values[0])];
            else if (d.Type != 22) // type 22 (lightParams) setter does nothing
                _values[key] = [.. values];
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
        foreach (var d in _defs)
        {
            if (!SimClassesNoCase(d.Name, field) || d.Count < 2 || index < 0 || index >= d.Count)
                continue;
            var key = ElementKey(Lower(d.Name), index);
            if (d.Type == 5)
                ParsePoint(values, PointSlot(key));
            else
                _values[key] = [.. values];
            return true;
        }
        return false;
    }

    /// <summary>Replaces a point3F field. Engine code uses it, not the persist setter.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="v">The point.</param>
    public void SetPoint(string field, ReadOnlySpan<float> v) => _points[Lower(field)] = [v[0], v[1], v[2]];

    /// <summary>Returns the values of a field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="index">The element index, or -1 for the whole field.</param>
    /// <returns>The values, or null when the field has none.</returns>
    public IReadOnlyList<string>? Get(string field, int index = -1) =>
        _values.TryGetValue(ElementKey(Lower(field), index), out var v) ? v : null;

    /// <summary>Reads a point3F field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="output">Receives the point.</param>
    /// <param name="index">The element index, or -1.</param>
    /// <returns>False when the field has no point.</returns>
    public bool Point(string field, Span<float> output, int index = -1)
    {
        if (!_points.TryGetValue(ElementKey(Lower(field), index), out var p))
            return false;
        for (var i = 0; i < 3; ++i)
            output[i] = p[i];
        return true;
    }

    /// <summary>Reads a string field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public string Str(string field, string def = "")
    {
        var v = Get(field);
        return v != null && v.Count != 0 ? v[0] : def;
    }

    /// <summary>Reads a real field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public float Real(string field, float def)
    {
        var v = Get(field);
        return v != null && v.Count != 0 ? AtofRZ(v[0]) : def;
    }

    /// <summary>Reads an integer field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <param name="index">The element index, or -1.</param>
    /// <returns>The value.</returns>
    public int IntegerValue(string field, int def, int index = -1)
    {
        var v = Get(field, index);
        return v != null && v.Count != 0 ? Numbers.BorlandAtoi(v[0]) : def;
    }

    /// <summary>Reads a boolean field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public bool Boolean(string field, bool def)
    {
        var v = Get(field);
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
        for (var i = 0; i < a.Length; ++i)
        {
            char x = a[i], y = b[i];
            if (x is >= 'A' and <= 'Z')
                x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z')
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
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; ++i)
        {
            int x = a[i], y = b[i];
            if (x is >= 'A' and <= 'Z')
                x += 32;
            if (y is >= 'A' and <= 'Z')
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
        if (v is not (> (-2147483649.0) and < 2147483648.0))
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
    public const int None = 0;
    /// <summary>The damage state: enabled.</summary>
    public const int Enabled = 1;
    /// <summary>The damage state: disabled.</summary>
    public const int Disabled = 2;
    /// <summary>The damage state: destroyed.</summary>
    public const int Destroyed = 3;
    /// <summary>The thread flag mask of the state bits.</summary>
    public const byte ThreadState = 3;
    /// <summary>The thread flag: at end.</summary>
    public const byte ThreadAtEnd = 4;
    /// <summary>The thread flag: forward.</summary>
    public const byte ThreadForward = 8;
    /// <summary>The thread state: play.</summary>
    public const int Play = 0;
    /// <summary>The thread state: stop.</summary>
    public const int Stop = 1;
    /// <summary>The thread state: pause.</summary>
    public const int Pause = 2;
    /// <summary>The move state: none.</summary>
    public const int MoveNone = 0;
    /// <summary>The move state: forward.</summary>
    public const int MoveForwardType = 1;
    /// <summary>The move state: backward.</summary>
    public const int MoveBackward = 2;
    /// <summary>The move state: stopped.</summary>
    public const int MoveStopped = 3;

    /// <summary>An animation thread slot of a ShapeBase.</summary>
    public sealed class Thread
    {
        /// <summary>The sequence index, -1 for none (+0x750 + 0x10 * slot).</summary>
        public int Sequence { get; set; } = -1;
        /// <summary>The flags (+0x754): bits 0 and 1 state, 4 at end, 8 forward.</summary>
        public byte Flags { get; set; }
        /// <summary>True when the shape instance thread exists (+0x748).</summary>
        public bool Created { get; set; }
        /// <summary>The position, 0 to 1 (thread +0x1c).</summary>
        public float Position { get; set; }
        /// <summary>The speed (thread +0x18).</summary>
        public float Speed { get; set; }
    }

    /// <summary>A collision callback record: per other object, the manager time before which no further onCollision runs.</summary>
    public struct CollisionRecord
    {
        /// <summary>The id of the other object.</summary>
        public int Other { get; set; }
        /// <summary>The manager time at which the record expires.</summary>
        public float Expiry { get; set; }
    }

    /// <summary>The Item datablock values.</summary>
    public sealed class ItemBlock
    {
        /// <summary>The mass.</summary>
        public float Mass { get; set; } = 1;
        /// <summary>The drag.</summary>
        public float Drag { get; set; } = 1;
        /// <summary>The density.</summary>
        public float Density { get; set; } = 1;
        /// <summary>The friction.</summary>
        public float Friction { get; set; } = 1;
        /// <summary>The elasticity.</summary>
        public float Elasticity { get; set; } = 0.2f;
    }

    /// <summary>The MineData values (vtable block at 0x004588f8).</summary>
    public sealed class MineBlock
    {
        /// <summary>The damage type.</summary>
        public int DamageType { get; set; }
        /// <summary>The damage value.</summary>
        public float DamageValue { get; set; } = 1;
        /// <summary>The explosion radius.</summary>
        public float ExplosionRadius { get; set; } = 1;
        /// <summary>The kick.</summary>
        public float Kick { get; set; } = 100;
    }

    /// <summary>The gravity force (type 1) and container drag (+0x124) taken on terrain container entry, for Items and projectiles.</summary>
    /// <remarks>Follows SimTerrain_enterContainer (0x005195b4, docs/player-sim.md FC2).</remarks>
    public sealed class EntryForcesRec
    {
        /// <summary>The terrain container entry at the last refile, or null outside.</summary>
        public object? Terrain { get; set; }
        /// <summary>The parent container entry.</summary>
        public object? Parent { get; set; }
        /// <summary>The gravity force.</summary>
        public float[] Gravity { get; } = new float[3];
        /// <summary>The container drag.</summary>
        public float Drag { get; set; }
    }

    /// <summary>The SensorData fields, also read from a TurretData.</summary>
    public sealed class SensorBlock
    {
        /// <summary>True when the sensor casts a line of sight.</summary>
        public bool CastLOS { get; set; }
        /// <summary>The detection range.</summary>
        public int Range { get; set; } = 100;
        /// <summary>The doppler value.</summary>
        public int Doppler { get; set; }
        /// <summary>True when the sensor suppresses others.</summary>
        public bool Supression { get; set; }
        /// <summary>True when the sensor can be suppressed.</summary>
        public bool Supressable { get; set; } = true;
        /// <summary>True when the sensor pings.</summary>
        public bool Pinger { get; set; } = true;
    }

    /// <summary>The TurretData values.</summary>
    public sealed class TurretBlock
    {
        /// <summary>The gun range, -1 for unset.</summary>
        public float GunRange { get; set; } = -1;
        /// <summary>The speed.</summary>
        public float Speed { get; set; } = 1;
        /// <summary>The speed modifier.</summary>
        public float SpeedModifier { get; set; }
        /// <summary>The reload delay.</summary>
        public float ReloadDelay { get; set; }
        /// <summary>The minimum gun energy.</summary>
        public float MinGunEnergy { get; set; }
        /// <summary>The maximum gun energy.</summary>
        public float MaxGunEnergy { get; set; }
        /// <summary>The field of view, -1 for unset (+0x14c).</summary>
        public float Fov { get; set; } = -1;
        /// <summary>The deflection.</summary>
        public float Deflection { get; set; }
        /// <summary>The energy rate.</summary>
        public float EnergyRate { get; set; }
        /// <summary>The targetable field of view ratio.</summary>
        public float TargetableFovRatio { get; set; } = 0.5f;
        /// <summary>True for a sustained weapon.</summary>
        public bool Sustained { get; set; }
        /// <summary>True when the turret has a projectile.</summary>
        public bool HasProjectile { get; set; }
        /// <summary>The firing time in milliseconds.</summary>
        public int FiringTimeMS { get; set; }
    }

    /// <summary>The fields of the last move the turret reads (+0x8d8).</summary>
    public struct TurretMove
    {
        /// <summary>The turn input.</summary>
        public float Turn { get; set; }
        /// <summary>The pitch input.</summary>
        public float Pitch { get; set; }
        /// <summary>The jump input.</summary>
        public bool Jump { get; set; }
        /// <summary>The fire input.</summary>
        public bool Fire { get; set; }
    }

    /// <summary>The MoveableData values (datablock group 7) read at bind.</summary>
    public sealed class MoveableBlock
    {
        /// <summary>True when the Moveable displaces objects (+0x100).</summary>
        public bool Displace { get; set; } = true;
        /// <summary>True when the Moveable blocks forward (+0x101).</summary>
        public bool BlockForward { get; set; } = true;
        /// <summary>True when the Moveable blocks backward (+0x102).</summary>
        public bool BlockBackward { get; set; } = true;
        /// <summary>The speed (+0x114).</summary>
        public float Speed { get; set; } = 1;
    }

    /// <summary>An occupant of a Trigger.</summary>
    public struct Occupant
    {
        /// <summary>The id of the occupant object.</summary>
        public int ObjectId { get; set; }
        /// <summary>The time at which the occupant expires.</summary>
        public float Expiry { get; set; }
        /// <summary>The time of the next contact event.</summary>
        public float NextContact { get; set; }
    }

    /// <summary>The team id, -1 for none.</summary>
    public int Team { get; set; } = -1;
    /// <summary>The datablock name, interned.</summary>
    public string? DataBlockName { get; set; }
    /// <summary>The map name. A ShapeBase starts with "", and null prints False.</summary>
    public string? MapName { get; set; }            // ShapeBase starts with ""; null prints False
    /// <summary>The sensor target index, when the datablock is visibleToSensor.</summary>
    public int TargetIndex { get; set; } = -1;
    /// <summary>The object transform.</summary>
    public ref Transform Transform => ref _transform;
    private Transform _transform = new();
    /// <summary>The collision geometry of an InteriorShape.</summary>
    public InteriorGeometry? InteriorGeo { get; set; }
    /// <summary>The InteriorShape isContainer value (+0x244), -1 until a shape loads. Part of the InteriorShape ghost data (docs/game-stream-ghosts.md 3.4), with the state words and light records.</summary>
    public int IsContainer { get; set; } = -1;
    /// <summary>The InteriorShape state words and light records.</summary>
    public InteriorNetInfo InteriorNet { get; set; } = new();
    /// <summary>The resources of the ITRInstance (Interior.LoadInteriorResources).</summary>
    public ResourceLocks ResourceLocks { get; } = new();
    /// <summary>True when collision is disabled (+0x41c).</summary>
    public bool CollisionDisabled { get; set; }

    /// <summary>The id of the controlling client (+0x230). Client links are in docs/player-manager.md 12.5, the control object interface in docs/game-stream.md 6.5.</summary>
    public int ControlClient { get; set; }
    /// <summary>The id of the last non-zero owner client (+0x234).</summary>
    public int LastOwnerClient { get; set; }
    /// <summary>The id of the owner client (+0x238).</summary>
    public int OwnerClient { get; set; }
    /// <summary>The field of view, stored by the PlayerPSC reader (+0x22c).</summary>
    public float Fov { get; set; } = 1.57079637f;
    /// <summary>True when the object is a client ghost (+0x4c bit 0x02). The client prediction sets it on its copy of the control object, and the step then skips the server-only parts (Player S7 crouch, scripts, Flier fire and jump). Never set on the server (docs/client-net.md 4.4).</summary>
    public bool ClientSide { get; set; }

    /// <summary>The index in the datablock group (+0x220), -1 for none (docs/game-stream-ghosts.md 4.1).</summary>
    public int DataBlockIndex { get; set; } = -1;
    /// <summary>True when the object is a power generator (+0x204).</summary>
    public bool PowerGenerator { get; set; }
    /// <summary>The number of generators in the groups above (+0x208).</summary>
    public int PowerCount { get; set; }

    /// <summary>The animation threads (ShapeBase net state, docs/game-stream-ghosts.md 4.2).</summary>
    public Thread[] Threads { get; } = [new(), new(), new()];
    /// <summary>The damage flash direction (+0x738).</summary>
    public float[] FlashNormal { get; } = new float[3];
    /// <summary>The damage flash amount (+0x744).</summary>
    public float FlashAmount { get; set; }
    /// <summary>The fade state (+0x280): 0 none, 1 fade out, 2 fade in (2.5 s).</summary>
    public int FadeState { get; set; }
    /// <summary>The tick time in ms at startFadeOut or startFadeIn (+0x284).</summary>
    public uint FadeStart { get; set; }
    /// <summary>The Euler angles as given by the persist field (+0x244).</summary>
    public float[] Rotation { get; } = new float[3];
    /// <summary>The velocity (+0x194).</summary>
    public float[] Velocity { get; } = new float[3];
    /// <summary>The damage (+0x72c).</summary>
    public float Damage { get; set; }
    /// <summary>The maximum damage (datablock +0x5c).</summary>
    public float MaxDamage { get; set; } = 1.0f;
    /// <summary>The ratio of maximum damage at which the object is disabled (datablock +0xec).</summary>
    public float DisabledLevel { get; set; } = 0.5f;
    /// <summary>The ratio of maximum damage at which the object is destroyed (datablock +0xf0).</summary>
    public float DestroyedLevel { get; set; } = 0.75f;
    /// <summary>The damage state (+0x7d4). The StaticBase constructor sets 1 (0x0044f430).</summary>
    public int DamageState { get; set; } = Enabled;
    /// <summary>True when the object is active (+0x7d0, GameBase::setActive).</summary>
    public bool Active { get; set; }
    /// <summary>True when the object is a target (+0x7d1, GameBase::setIsTarget, set 725).</summary>
    public bool IsTarget { get; set; }
    /// <summary>The energy recharge rate (+0x728).</summary>
    public float RechargeRate { get; set; }
    /// <summary>The energy (+0x724).</summary>
    public float Energy { get; set; }
    /// <summary>The maximum energy (datablock +0x58).</summary>
    public float MaxEnergy { get; set; }
    /// <summary>The repair rate (datablock +0x60).</summary>
    public float RepairRate { get; set; } = 0.1f;
    /// <summary>The pending repair (+0x730).</summary>
    public float RepairPending { get; set; }
    /// <summary>The automatic repair rate (+0x734).</summary>
    public float AutoRepairRate { get; set; }
    /// <summary>True when the object is deleted when destroyed (+0x7e4). A persist field. A Mine and a collideable Item force it.</summary>
    public bool DeleteOnDestroy { get; set; }
    /// <summary>True once the datablock is resolved (+0x7cc).</summary>
    public bool ShapeBound { get; set; }
    /// <summary>The shape animation.</summary>
    public ShapeAnim? Anim { get; set; }

    /// <summary>The collision callback records (+0x214, docs/static-sim.md 1.8).</summary>
    public List<CollisionRecord> CollisionRecords { get; } = [];
    /// <summary>The latest expiry of the collision records.</summary>
    public float CollisionLatest { get; set; }

    /// <summary>True when the Item rotates (+0x888). Item and Mine state is in docs/static-sim.md 5.</summary>
    public bool ItemRotates { get; set; }
    /// <summary>True when the Item is collideable (+0x889).</summary>
    public bool ItemCollideable { get; set; }
    /// <summary>The object that threw the Item (+0x878).</summary>
    public GameBase? ItemThrower { get; set; }
    /// <summary>True when the Item is at rest (+0x164 bit 0x80).</summary>
    public ref bool AtRest => ref _atRest;
    private bool _atRest;
    /// <summary>The item count (+0x868).</summary>
    public int ItemCount { get; set; }
    /// <summary>True when the Item is visible (+0x864 bit 0). Hidden items leave their container.</summary>
    public bool ItemVisible { get; set; } = true;
    /// <summary>The pop flag (+0x864 bit 1). Stored and read by nothing registered.</summary>
    public bool ItemPop { get; set; }
    /// <summary>The previous at-rest state (+0x86c).</summary>
    public bool ItemPrevAtRest { get; set; } = true;
    /// <summary>The time of the throw (+0x870).</summary>
    public float ItemThrowTime { get; set; }
    /// <summary>The time of the next update (+0x874).</summary>
    public float ItemNextUpdate { get; set; }
    /// <summary>The client id of the thrower (+0x880).</summary>
    public int ItemThrowerClient { get; set; } = -1;
    /// <summary>The join counter of the thrower (+0x884).</summary>
    public int ItemThrowerJoin { get; set; } = -1;
    /// <summary>True when a contact surface was hit this step (+0x1f4).</summary>
    public bool ItemContact { get; set; }
    /// <summary>The notify list of the running move (info +0x3c).</summary>
    public List<SimObject> ItemNotify { get; set; } = [];
    /// <summary>The sub step length (+0x250): 0.45 times the shape radius.</summary>
    public float ItemSubStep { get; set; } = 0.3f;
    /// <summary>The centre of the shape bounding sphere (shape +0x08).</summary>
    public float[] ShapeCenter { get; } = new float[3];
    /// <summary>The radius of the shape bounding sphere (shape +0x04).</summary>
    public float ShapeRadius { get; set; }
    /// <summary>The shape box in object space (image +0x4c).</summary>
    public float[] ShapeBox { get; } = new float[6];
    /// <summary>The shape box enlarged by the trigger radius (the object box).</summary>
    public float[] ShapeQueryBox { get; } = new float[6];
    /// <summary>The datablock triggerRadius. The sphere image +0x778 is used when it is not 0.</summary>
    public float ShapeTriggerRadius { get; set; }
    /// <summary>The Item datablock values.</summary>
    public ItemBlock ItemData { get; } = new();
    /// <summary>The MineData values.</summary>
    public MineBlock MineData { get; } = new();
    /// <summary>The mine surface normal (+0x88c).</summary>
    public float[] MineNormal { get; } = new float[3];
    /// <summary>True when the mine is aligned to the surface (+0x898).</summary>
    public bool MineAligned { get; set; }
    /// <summary>The movement flags (+0x164): 0x181 plus 0x1d. A displacement clears 0x05.</summary>
    public int ItemFlags { get; set; } = 0x19d;
    /// <summary>The ids of the SimMovement objects in contact (+0x154). The Moveable carry list (docs/collision.md E3.3).</summary>
    public List<int> Carried { get; } = [];
    /// <summary>True when the shape has a <c>visibility</c> sequence (+0x7e0).</summary>
    public bool VisibilityThread { get; set; }
    /// <summary>The magnitude of the container gravity (+0x14c).</summary>
    public float ItemGravity { get; set; }
    /// <summary>The terrain entry forces.</summary>
    public EntryForcesRec EntryForces { get; } = new();

    /// <summary>The SensorData values (docs/static-sim.md 3).</summary>
    public SensorBlock SensorData { get; } = new();
    /// <summary>The sensor word (+0x200): detected bits 0 to 7, always visible 8 to 15, state 24 to 31.</summary>
    public uint SensorWord { get; set; }
    /// <summary>True when the object is a member of a SensorManager list.</summary>
    public bool SensorListed { get; set; }
    /// <summary>The next object id of the jamming player list (Player +0x7d4, docs/static-sim.md 3.8).</summary>
    public int JamNext { get; set; }
    /// <summary>The previous object id of the jamming player list (Player +0x7d8).</summary>
    public int JamPrev { get; set; }
    /// <summary>The turret rotation (+0x8b4). The constructor sets pi, and it is 0 once the shape has a turn thread (docs/static-sim.md 4.5).</summary>
    public ref float TurretRotation => ref _turretRotation;
    private float _turretRotation = 3.14159274f;
    /// <summary>The turret elevation (+0x8b8).</summary>
    public ref float TurretElevation => ref _turretElevation;
    private float _turretElevation;
    /// <summary>The TurretData values.</summary>
    public TurretBlock TurretData { get; } = new();
    /// <summary>The sim time until which an enemy keeps the turret awake (+0x8ac).</summary>
    public float TurretSleep { get; set; }
    /// <summary>The time of the next shot (+0x8b0).</summary>
    public float TurretNextFire { get; set; }
    /// <summary>The position, 0 to 1, of the power and fire thread (+0x8a4).</summary>
    public ref float TurretPowerPos => ref _turretPowerPos;
    private float _turretPowerPos;
    /// <summary>True when the shape has the sequences <c>fire</c> and <c>power</c>.</summary>
    public bool TurretHasPower { get; set; }
    /// <summary>The duration of the <c>power</c> sequence.</summary>
    public float TurretPowerDuration { get; set; }
    /// <summary>The duration of the <c>fire</c> sequence.</summary>
    public float TurretFireDuration { get; set; }
    /// <summary>True when the power and fire thread runs <c>fire</c> (priority 0), else <c>power</c> (-1).</summary>
    public bool TurretPowerOnFire { get; set; }
    /// <summary>The maximum rotation (+0x8c4). Equal maximum and minimum mean unlimited.</summary>
    public float TurretRotationMax { get; set; }
    /// <summary>The minimum rotation (+0x8c8).</summary>
    public float TurretRotationMin { get; set; }
    /// <summary>The maximum elevation (+0x8bc).</summary>
    public float TurretMaxElevation { get; set; } = 0.785398185f;
    /// <summary>The minimum elevation (+0x8c0).</summary>
    public float TurretMinElevation { get; set; } = -0.785398185f;
    /// <summary>The fire state (+0x908).</summary>
    public int TurretFireState { get; set; }
    /// <summary>The mode (+0x8cc): 0 off, 1 deactivating, 2 activating, 3 on.</summary>
    public int TurretMode { get; set; }
    /// <summary>The barrel state (+0x918): 1 from onAdd on a server.</summary>
    public int TurretBarrelState { get; set; }
    /// <summary>The engine time in ms of the current tick (DAT_006a8494 + 4).</summary>
    public uint TurretTickMs { get; set; }
    /// <summary>The projectile speed (+0x8d4), resolved on first use (docs/static-sim.md 4.2 step 2). Negative means unresolved.</summary>
    public float TurretProjectileSpeed { get; set; } = -1;
    /// <summary>The in-range counter (+0x8a8).</summary>
    public int TurretInRange { get; set; }
    /// <summary>The id of the preferred target (+0x900). The original keeps a raw pointer.</summary>
    public int TurretPreferred { get; set; }
    /// <summary>The id of the beam target (+0x920).</summary>
    public int TurretBeamTarget { get; set; }
    /// <summary>The id of the beam projectile (+0x924).</summary>
    public int TurretBeamProjectile { get; set; }
    /// <summary>The stamp in ms (+0x91c).</summary>
    public uint TurretStamp { get; set; }
    /// <summary>The move credit (+0x8f8).</summary>
    public int TurretCredit { get; set; }
    /// <summary>The position of the turn thread (+0x8a0), negative when there is no thread.</summary>
    public ref float TurretTurnPos => ref _turretTurnPos;
    private float _turretTurnPos = -1;
    /// <summary>The position of the elevate thread (+0x89c), negative when there is no thread.</summary>
    public ref float TurretElevatePos => ref _turretElevatePos;
    private float _turretElevatePos = -1;
    /// <summary>The last move (+0x8d8).</summary>
    public ref TurretMove TurretLastMove => ref _turretLastMove;
    private TurretMove _turretLastMove;

    /// <summary>The move state (+0x868). Moveable state is in docs/static-sim.md 2.</summary>
    public int MoveState { get; set; } = MoveNone;
    /// <summary>True when the move is blocked (+0x86c).</summary>
    public bool MoveBlocked { get; set; }
    /// <summary>The target waypoint (+0x870).</summary>
    public int MoveTarget { get; set; } = 15;
    /// <summary>The path id (+0x87c).</summary>
    public int PathId { get; set; } = 0xff;
    /// <summary>The current waypoint (+0x884).</summary>
    public int Waypoint { get; set; }
    /// <summary>The time on the current segment (+0x888).</summary>
    public float WaypointTime { get; set; }
    /// <summary>The number of waypoints (+0x89c).</summary>
    public int WaypointCount { get; set; }
    /// <summary>The MoveableData values.</summary>
    public MoveableBlock MoveData { get; } = new();
    /// <summary>The waypoint of the segment cache (+0x88c to +0x8cc).</summary>
    public int AppliedWaypoint { get; set; } = -1;
    /// <summary>The time of the segment cache.</summary>
    public float AppliedTime { get; set; }
    /// <summary>The cached segment.</summary>
    public int CachedSegment { get; set; } = -1;
    /// <summary>The segment time.</summary>
    public float SegmentTime { get; set; } = 1.0f;
    /// <summary>The segment start position.</summary>
    public float[] SegPos { get; } = new float[3];
    /// <summary>The segment start rotation.</summary>
    public float[] SegRot { get; } = new float[3];
    /// <summary>The position delta of the segment.</summary>
    public float[] SegDPos { get; } = new float[3];
    /// <summary>The rotation delta of the segment.</summary>
    public float[] SegDRot { get; } = new float[3];
    /// <summary>A test override of the answer of <c>GameServices.MoveableDisplace</c>.</summary>
    public Func<GameBase, bool>? MoveableDisplaceOverride { get; set; }
    /// <summary>A test override of the answer of <c>GameServices.MoveableTest</c>.</summary>
    public Func<GameBase, bool>? MoveableTestOverride { get; set; }

    /// <summary>True when the Trigger is active (+0x250). Trigger state is in docs/static-sim.md 6.</summary>
    public bool TriggerActive { get; set; } = true;
    /// <summary>The boundingBox in object space (+0x1d4).</summary>
    public float[] TriggerBox { get; } = [-1, -1, -1, 1, 1, 1];
    /// <summary>True when the Trigger is a sphere (+0x251, isSphere).</summary>
    public bool TriggerSphere { get; set; } = true;
    /// <summary>The occupants (+0x240).</summary>
    public List<Occupant> Occupants { get; } = [];
    /// <summary>The contact interval (datablock +0x34).</summary>
    public float TriggerInterval { get; set; } = 0.25f;

    private readonly ShapeKind _shapeClass = ShapeKind.NoShape;
    private readonly string _class;
    private readonly Kind _kind;
    private readonly int _dbGroup;
    private readonly PersistFields _fields;
    private readonly ClassContext _ctx;
    private readonly int _setId, _leaveSetId;

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
        _class = cls;
        _kind = kind;
        _dbGroup = dataBlockGroup;
        _fields = new PersistFields(fields);
        _ctx = ctx;
        _setId = setId;
        _leaveSetId = leaveSetId;
        if (kind == Kind.ShapeBase)
        {
            MapName = "";
            (string, ShapeKind)[] kShapes =
            [
                ("StaticShape", ShapeKind.StaticShape), ("Sensor", ShapeKind.Sensor), ("Turret", ShapeKind.Turret),
                ("Item", ShapeKind.Item), ("Mine", ShapeKind.Mine), ("Moveable", ShapeKind.Moveable),
                ("Flier", ShapeKind.Vehicle), ("Vehicle", ShapeKind.Vehicle),
            ];
            foreach ((var c, var s) in kShapes)
                if (GameUtil.EqualsNoCase(c, cls))
                    _shapeClass = s;
        }
        else
        {
            DamageState = None; // not a StaticBase: no damage state
        }
    }

    /// <inheritdoc/>
    public override string ClassName() => _class;
    /// <summary>Returns the script class name.</summary>
    /// <returns>The class name.</returns>
    public string ScriptClass() => _class;
    /// <summary>Returns the C++ class behind the ShapeBase kind.</summary>
    /// <returns>The shape kind.</returns>
    public ShapeKind ShapeClass() => _shapeClass;
    /// <summary>Tells whether the object is built on the StaticBase constructor (0x0044f430), as a Vehicle is (0x00444fe4).</summary>
    /// <returns>True for the StaticBase family.</returns>
    public bool IsStaticBase() => _shapeClass != ShapeKind.NoShape;
    /// <summary>Tells whether the object is a ShapeBase.</summary>
    /// <returns>True for the ShapeBase kind.</returns>
    public bool IsShapeBase() => _kind == Kind.ShapeBase;
    /// <summary>Tells whether the object is an interior.</summary>
    /// <returns>True for the Interior kind.</returns>
    public bool IsInterior() => _kind == Kind.Interior;
    /// <summary>Returns the persist fields.</summary>
    /// <returns>The fields.</returns>
    public PersistFields Fields() => _fields;
    /// <summary>Returns the datablock group.</summary>
    /// <returns>The group.</returns>
    public int DataBlockGroup() => _dbGroup;
    /// <summary>Returns the number of datablocks in the group.</summary>
    /// <returns>The count, 0 without a game.</returns>
    public int DataBlockCount() => _ctx.Game != null ? _ctx.Game.DataBlockCount(_dbGroup) : 0;
    /// <summary>Returns the kind of the object.</summary>
    /// <returns>The kind.</returns>
    public Kind GetKind() => _kind;
    /// <summary>Returns the game services.</summary>
    /// <returns>The services, or null.</returns>
    public GameServices? Services() => _ctx.Game;
    /// <summary>Returns the class context.</summary>
    /// <returns>The context.</returns>
    public ClassContext Context() => _ctx;
    /// <summary>Reads a point3F persist field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="output">Receives the point.</param>
    /// <returns>False when the field has no point.</returns>
    public bool PersistPoint(string field, Span<float> output) => _fields.Point(field, output);
    /// <inheritdoc/>
    public override IReadOnlyList<string>? PersistValue(string field) => _fields.Get(field);

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
        if (!_fields.Set(field, values))
            return false;
        var v = values.Count == 0 ? "" : values[0];
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
        _fields.Point("position", pos);
        _fields.Point("rotation", rot);
        MissionTransform.EulerToMatrix(rot, ref Transform);
        for (var i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
        if (pos[0] != 0 || pos[1] != 0 || pos[2] != 0)
            Transform.Flags |= 2;
    }

    // src/game/mission_objects.cpp GameBase::initTeam: GB2: the first MissionGroup or TeamGroup above the object decides
    // the team.
    private void InitTeam()
    {
        for (var g = Group(); g != null; g = g.Group())
        {
            if (g.Name() != null && GameUtil.EqualsNoCase(g.Name(), "MissionGroup"))
            {
                SetTeam(-1);
                return;
            }
            var t = 0;
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
        var old = Team;
        Team = t;
        if (_kind == Kind.Marker) // Marker_setTeam 0x00449558: always visible to its own team (all for -1)
            SensorWord = (SensorWord & ~0xff00u) | (t == -1 ? 0xff00u : (1u << (int)(((uint)t + 8u) & 31)));
        if (SensorListed && _ctx.Game != null)
            _ctx.Game.SensorTeam(this);
        if (old != t)
        {
            SetMaskBits(0x04);
            _ctx.Game?.ScriptCallback(this, "onSetTeam", [Numbers.FormatInt(old)]);
            // 0x004483cd: the owning client (+0x238) follows the object; its PlayerTeamChangeEvent goes to every human.
            if (OwnerClient != 0 && _ctx.Game != null)
                _ctx.Game.ClientTeam(OwnerClient, Team);
        }
    }

    /// <summary>Adds bits to the dirty mask of the object's ghosts.</summary>
    /// <param name="bits">The mask bits.</param>
    public void SetMaskBits(uint bits) => _ctx.Game?.SetMaskBits(this, bits);

    // ---- power (docs/game-stream-ghosts.md 4.11) ----

    // src/game/mission_objects.cpp propagatePower: 0x00448524: every GameBase in the group and its sub-groups gains or
    // loses one generator.
    private static void PropagatePower(GameBase generator, SimGroup? group, bool on)
    {
        if (group == null)
            return;
        var members = new List<SimObject>(group.Members()); // scripts may change the group
        foreach (var m in members)
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
        for (var g = Group(); g != null; g = g.Group())
        {
            var members = new List<SimObject>(g.Members());
            foreach (var m in members)
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
        if (!IsStaticBase() || !ShapeBound || DamageState != Enabled || _ctx.Game == null)
            return;
        _ctx.Game.ScriptCallback(this, "onPower", [PowerCount != 0 ? "true" : "false", Numbers.FormatInt(generator.Id())]);
    }

    // ---- threads (4.2) ----

    // src/game/mission_objects.cpp GameBase::threadChanged: ShapeBase_updateThreadSpeed 0x0044c2b4. One draw of the
    // thread generator on every apply, whatever the state: s = 0.9 + u * 2^-32 * 0.2, evaluated as
    // ((u * 2^-32f) * 2 * 0.1f + 1) - 0.1f and stored as f32 toward zero.
    private void ThreadChanged(int slot)
    {
        var t = Threads[slot];
        if (!t.Created)
            return;
        double u = StaticRandom.Thread().Next();
        var scale = Mul(u, 2.3283064365386963e-10);
        scale = Mul(scale, 2.0);
        scale = Mul(scale, (double)0.1f);
        scale = Add(scale, 1.0);
        scale = Sub(scale, (double)0.1f);
        var s = X87.F32(scale); // control word: round toward zero
        var state = t.Flags & ThreadState;
        if (state == Play)
        {
            if ((t.Flags & ThreadAtEnd) == 0)
            {
                t.Speed = (t.Flags & ThreadForward) != 0 ? s : -s;
            }
            else
            {
                t.Position = (t.Flags & ThreadForward) != 0 ? 1.0f : 0.0f;
                t.Speed = 0;
            }
        }
        else
        {
            if (state == Stop)
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
        var t = Threads[slot];
        if (t.Created && sequence == t.Sequence && (t.Flags & ThreadState) == Play)
            return true;
        if (sequence >= 16)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Sequence = sequence;
        if (reset)
            t.Flags = (byte)((t.Flags & ~(ThreadState | ThreadAtEnd)) | ThreadForward);
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
        var t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & ThreadState) == Stop)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)((t.Flags & ~ThreadState) | Stop);
        ThreadChanged(slot);
        return true;
    }

    /// <summary>Pauses a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c428.</remarks>
    public bool PauseThread(int slot)
    {
        var t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & ThreadState) == Pause)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)((t.Flags & ~ThreadState) | Pause);
        ThreadChanged(slot);
        return true;
    }

    /// <summary>Resumes a thread.</summary>
    /// <param name="slot">The thread slot.</param>
    /// <returns>True when the thread existed.</returns>
    /// <remarks>Follows 0x0044c480.</remarks>
    public bool ResumeThread(int slot)
    {
        var t = Threads[slot];
        if (t.Sequence == -1 || (t.Flags & ThreadState) == Play)
            return false;
        SetMaskBits((0x800u << slot) | 0x400u);
        t.Flags = (byte)(t.Flags & ~ThreadState);
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
        var t = Threads[slot];
        if (t.Sequence == -1)
            return false;
        if ((t.Flags & ThreadForward) != 0 != forward)
        {
            SetMaskBits((0x800u << slot) | 0x400u);
            t.Flags = (byte)((t.Flags & ~(ThreadForward | ThreadAtEnd)) | (forward ? ThreadForward : 0));
            ThreadChanged(slot);
        }
        return true;
    }

    // ---- StaticBase state (4.2, 4.3) ----

    // src/game/mission_objects.cpp GameBase::stateCallback: vtable +0x1ac: StaticBase 0x0044fec0 calls the script named
    // after the damage state. Moveable (0x00444098) stops unless enabled; Turret (0x004570f4) adds mask 0x10000.
    private void StateCallback()
    {
        string[] kNames = ["onNone", "onEnabled", "onDisabled", "onDestroyed"];
        if (_shapeClass == ShapeKind.Moveable && DamageState != Enabled)
            SetMoveState(MoveStopped);
        if (_ctx.Game != null && DamageState >= 0 && DamageState <= 3)
            _ctx.Game.ScriptCallback(this, kNames[DamageState], []);
        if (_shapeClass == ShapeKind.Turret)
            SetMaskBits(0x10000);
        // Mine (0x00458be4): after the StaticBase part, entering Destroyed on the server explodes.
        if (_shapeClass == ShapeKind.Mine && DamageState == Destroyed && _ctx.Game != null)
            _ctx.Game.MineDestroyed(this);
    }

    // src/game/mission_objects.cpp GameBase::scriptOnAdd: vtable +0x134: GameBase 0x004487a4 calls onAdd; StaticBase
    // 0x0044fe8c then reports the damage state (onEnabled for a new object).
    private void ScriptOnAdd()
    {
        if (_ctx.Game == null)
            return;
        _ctx.Game.ScriptCallback(this, "onAdd", []);
        if (IsStaticBase())
            StateCallback();
    }

    /// <summary>Sets the active state.</summary>
    /// <param name="on">The requested state.</param>
    /// <returns>The resulting state.</returns>
    /// <remarks>Follows vtable +0x130 (0x0044fbc4). A GameBase that is not a StaticBase is always active and returns the argument (0x00448628, 0x0044862c). A Trigger has its own flag (docs/static-sim.md 6.4).</remarks>
    public bool SetActive(bool on)
    {
        if (_kind == Kind.Trigger)
            return TriggerSetActive(on);
        if (!IsStaticBase())
            return on;
        if (on != Active && DamageState == Enabled)
        {
            Active = on;
            if (!on && _shapeClass == ShapeKind.Moveable)
                SetMoveState(MoveStopped); // 0x00444080
            if (ShapeBound && DamageState == Enabled && _ctx.Game != null)
                _ctx.Game.ScriptCallback(this, on ? "onActivate" : "onDeactivate", []);
        }
        return Active;
    }

    /// <summary>Returns the active flag.</summary>
    /// <returns>The flag.</returns>
    /// <remarks>Follows GameBase::isActive (vtable +0x12c).</remarks>
    public bool IsActiveFlag()
    {
        if (_kind == Kind.Trigger)
            return TriggerActive;
        return !IsStaticBase() || Active;
    }

    /// <summary>Sets whether the object is a target.</summary>
    /// <param name="on">True to make it a target.</param>
    /// <remarks>Follows 0x0045041c.</remarks>
    public void SetIsTarget(bool on)
    {
        if (Manager() != null && _ctx.Game != null && on != IsTarget)
        {
            if (on)
                _ctx.Game.JoinSet(this, 725);
            else
                _ctx.Game.LeaveSet(this, 725);
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
        if (state is Disabled or Destroyed)
            Active = false;
        if (state == Destroyed)
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
            SetDamageState(Destroyed, true);
        }
        else if (!(Mul(MaxDamage, DisabledLevel) > Damage))
        {
            SetDamageState(Disabled, true);
        }
        else
        {
            SetDamageState(Enabled, true);
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
        for (var i = 0; i < 3; ++i)
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
        _fields.Point("rotation", ang, 0, 0, 0);
        var m = new Transform();
        MissionTransform.EulerToMatrix(ang, ref m);
        MissionTransform.MatrixToEuler(m, e);
        _fields.SetPoint("position", p);
        _fields.SetPoint("rotation", e);
        // Mat3_fromEulerWrap of the read-back angles, then SimObject_setTransform (InteriorShape_setTransform).
        MissionTransform.EulerToMatrix(e, ref Transform);
        for (var i = 0; i < 3; ++i)
            Transform.P[i] = p[i];
        Transform.Flags |= 2;
        SetMaskBits(0x01);
        if (InteriorGeo != null && _ctx.Game?.GetContainer() is Container c)
        {
            // World box through the new transform, padded (InteriorShape_worldBox 0047e5a0), then the container refile.
            Span<float> box = stackalloc float[6];
            var wm = WorldMat();
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
        for (var i = 0; i < 3; ++i)
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
        Span<float> q = [p[0], p[1], p[2]];
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
            var t = CString.CStr(text);
            var p = 0;
            while (p < t.Length && output.Length < 255)
            {
                if (t[p] == '<' && p + 1 < t.Length && t[p + 1] == 'v')
                {
                    var gt = t.IndexOf('>', p);
                    if (gt >= 0)
                    {
                        var value = _ctx.Game != null ? _ctx.Game.Variable(t[(p + 2)..gt]) : "";
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
        var s = output.ToString();
        StoreMapName(s);
        if (TargetIndex >= 0 && _ctx.Game != null)
            _ctx.Game.RenameTarget(TargetIndex, s);
    }

    /// <summary>Returns the world matrix of the object.</summary>
    /// <returns>The matrix.</returns>
    public Mat WorldMat()
    {
        var m = new Mat();
        for (var i = 0; i < 9; ++i)
            m.M[i] = Transform.M[i];
        m.P = new Vec3(Transform.P[0], Transform.P[1], Transform.P[2]);
        return m;
    }

    // src/game/mission_objects.cpp transformBox: world AABB of a local box under a transform: the 8 corners
    // (docs/interiors.md 5.2).
    private static void TransformBox(ReadOnlySpan<float> local, in Mat m, Span<float> output)
    {
        for (var i = 0; i < 3; ++i)
        {
            output[i] = 1e30f;
            output[i + 3] = -1e30f;
        }
        for (var c = 0; c < 8; ++c)
        {
            var p = new Vec3(local[(c & 1) != 0 ? 3 : 0], local[(c & 2) != 0 ? 4 : 1], local[(c & 4) != 0 ? 5 : 2]);
            var w = m.ToWorld(p);
            for (var i = 0; i < 3; ++i)
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
        if (_kind != Kind.Marker)
            BuildTransform();
        if (_kind == Kind.Interior && _ctx.Game?.GetResourceManager() is ResourceManager res)
        {
            // Load the state-0 LOD-0 geometry; a missing file is silent (docs/interiors.md L5).
            var f = _fields.Get("filename");
            if (f != null && f.Count != 0)
            {
                InteriorGeo = Interior.LoadInterior(res, f[0]);
                Interior.LoadInteriorNetInfo(res, f[0], InteriorNet);
                Interior.LoadInteriorResources(res, f[0], ResourceLocks);
            }
            // isContainer (0x0047ec6c): -1 becomes 1 when a collision box extent is at least 6.0, else 0; it stays -1
            // without a shape.
            IsContainer = _fields.IntegerValue("isContainer", -1);
            if (InteriorGeo != null && IsContainer == -1)
            {
                var b = InteriorGeo.Box;
                IsContainer = (Sub(b[3], b[0]) >= 6.0 || Sub(b[4], b[1]) >= 6.0 || Sub(b[5], b[2]) >= 6.0) ? 1 : 0;
            }
            if (InteriorGeo != null && _ctx.Game.GetContainer() is Container cont)
            {
                var e = new Container.Entry(this, ObjectType.InteriorType, default, false, null);
                var m = WorldMat();
                Span<float> box = stackalloc float[6];
                TransformBox(InteriorGeo.Box, m, box);
                Container.PadInteriorBox(box);
                e.Box = Float6.Of(box);
                // isContainer (docs/interiors.md 5.1 step 5): -1 means "any extent >= 6".
                var ic = _fields.Get("isContainer");
                var isContainer = ic != null && ic.Count != 0 ? Numbers.BorlandAtoi(ic[0]) : -1;
                if (isContainer == -1)
                {
                    isContainer = 0;
                    for (var i = 0; i < 3; ++i)
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
                var geo = InteriorGeo;
                // Material list of the shape (.dis +0x4c) for the hit surface (collision.md 5.2 part 4.1).
                List<Material>? mats = null;
                var ml = Interior.InteriorMaterialList(res, f![0]);
                if (ml.Length != 0)
                    mats = MaterialList.Get(res, ml);
                var slot = new System.Runtime.CompilerServices.StrongBox<Material?>(null);
                e.HitMaterial = slot;
                // Line into geometry space with the inverse image transform, the hit back with the transform itself
                // (0x00503408, m_mul_point, m_mul_vector). InteriorShape_getCollisionImages (0x0047f814) copies the
                // current object transform into the image on every query, so the cast reads it at call time (G38).
                e.Cast = (s, en, ref hit, ref n) =>
                {
                    using var chop = X87.Chop();
                    var toWorld = new TMat3
                    {
                        Flags = Transform.Flags,
                        M = Transform.M,
                        P = Transform.P
                    };
                    Shape.TmatInverse(toWorld, out var toLocal);
                    Span<float> ws = [s.X, s.Y, s.Z], we = [en.X, en.Y, en.Z];
                    Span<float> ls = stackalloc float[3], le = stackalloc float[3];
                    X87.MulPoint(ws, toLocal.M, toLocal.P, ls);
                    X87.MulPoint(we, toLocal.M, toLocal.P, le);
                    var h = new InteriorGeometry.Hit();
                    if (!geo.CastLine(new Vec3(ls[0], ls[1], ls[2]), new Vec3(le[0], le[1], le[2]), ref h))
                        return false;
                    var mb = geo.SurfaceMaterial(geo.SurfaceAt(h.Leaf, h.PlaneIndex, h.Point));
                    slot.Value = mats != null && mb != 0xff && mb < mats.Count ? mats[mb] : null;
                    Span<float> lp = [h.Point.X, h.Point.Y, h.Point.Z];
                    Span<float> ln = [h.Normal.X, h.Normal.Y, h.Normal.Z];
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
        if (_kind == Kind.ShapeBase)
        {
            _fields.Point("rotation", Rotation);
            ItemRotates = _fields.Boolean("rotates", false);
            ItemCollideable = _fields.Boolean("collideable", _shapeClass == ShapeKind.Mine); // Mine_ctor 0x004588f8 sets +0x889
            ItemCount = _fields.IntegerValue("count", 0);
            DeleteOnDestroy = _fields.Boolean("deleteOnDestroy", false) || _shapeClass == ShapeKind.Mine;
            if (_shapeClass == ShapeKind.Item && ItemCollideable)
                DeleteOnDestroy = true; // a collideable Item is deleted when destroyed
            ItemThrowTime = Manager() != null ? Manager()!.Time() : 0;
            // A server Item starts not at rest and settles in its first tick (docs/static-sim.md 5.2).
            AtRest = false;
        }
        InitTeam();
        if (DataBlockName != null && !BindDataBlock(true))
            return false;
        if (_ctx.Game != null)
        {
            if (_leaveSetId != 0)
                _ctx.Game.LeaveSet(this, _leaveSetId);
            if (_setId != 0)
                _ctx.Game.JoinSet(this, _setId);
            if (IsTarget)
                _ctx.Game.JoinSet(this, 725); // StaticBase_onAdd 0x0044fe1c
            if (DataBlockName != null)
                ScriptOnAdd();
            if (_kind == Kind.Trigger && DataBlockName != null)
                _ctx.Game.ScriptCallback(this, "onActivate", []); // Trigger::onAdd 0x00487198 ends with it
        }
        if (_shapeClass == ShapeKind.Turret)
            TurretBarrelState = 1; // Turret_onAdd 0x00452d68
        return true;
    }

    // src/game/mission_objects.cpp GameBase::bindDataBlock: GB3: resolve the datablock and run what its bind does
    // (docs/static-sim.md 1.1).
    private bool BindDataBlock(bool registering)
    {
        _ = registering;
        if (_ctx.Game != null && !_ctx.Game.ResolveDataBlock(this))
            return false;
        OnNewDataBlock();
        if (_kind == Kind.Marker)
        {
            BuildTransform(); // Marker::onNewDataBlock (0x00485f3c)
            SetTeam(-1);
        }
        if (_shapeClass == ShapeKind.Turret)
        {
            // Turret onNewDataBlock (0x00452970) ends with vtable +0x134 on a server.
            BindTurretShape();
            ScriptOnAdd();
        }
        if (_shapeClass is ShapeKind.Sensor or ShapeKind.Turret)
            _ctx.Game?.SensorInsert(this); // 0x00438fbc: the list of the team, or the suppressors
        _ctx.Game?.ScriptCallback(this, "onInit", []);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>The newObject(name, class, args...) arguments are processed after registration (docs/sim.md 6.4). StaticBase 0x0044f508 takes dataBlock [, deleteOnDestroy]. Item 0x004510e8 takes dataBlock, count, pop [, rotate [, collide]]. Mine 0x00458970 takes dataBlock. The datablock binds first, so its onAdd scripts see count 0, then the other arguments are stored.</remarks>
    public override bool ProcessArguments(IReadOnlyList<string> args)
    {
        if (_kind != Kind.ShapeBase)
            return true;
        var n = args.Count;
        string? usage = null;
        if (_shapeClass == ShapeKind.Item)
        {
            if (n is < 3 or > 5)
                usage = "Item: <dataFile> <count> <bool pop> [bool rotate] [bool collide]";
        }
        else if (_shapeClass == ShapeKind.Mine)
        {
            if (n != 1)
                usage = "Mine: <dataFile>";
        }
        else if (n is not 1 and not 2)
        {
            usage = "StaticBase: <dataFile> [deleteOnDestroy]";
        }
        if (usage != null)
        {
            _ctx.Game?.PrintLine(usage);
            return false;
        }
        SetPersistField("dataBlock", [args[0]]);
        if (!BindDataBlock(false))
            return false;
        ScriptOnAdd();
        if (_shapeClass == ShapeKind.Item)
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
        else if (_shapeClass != ShapeKind.Mine && n == 2)
        {
            DeleteOnDestroy = Numbers.ParseBool(args[1]);
        }
        return true;
    }

    /// <inheritdoc/>
    public override void OnRemove()
    {
        DetachAll(); // 0x0050471e: the carry lists never keep a deleted object
        if (_ctx.Game != null && _kind != Kind.Trigger)
            _ctx.Game.DropTriggerOccupant(this);
        if (_kind == Kind.Trigger && TriggerActive && DataBlockName != null)
            TriggerSetActive(false); // 0x004872a4 before the removal
        if (_ctx.Game != null && DataBlockName != null)
            _ctx.Game.ScriptCallback(this, "onRemove", []);
        if (_ctx.Game != null)
        {
            if (SensorListed)
                _ctx.Game.SensorRemove(this);
            _ctx.Game.TargetRemoved(this);
        }
        _ctx.Game?.GetContainer()?.Remove(this);
    }
}

// src/game/mission_objects.cpp class PersistGroup: SimGroup subclass with persist fields (SimPath, DropPointGroup).
internal sealed class PersistGroup : SimGroup
{
    private readonly string _class;
    private readonly PersistFields _fields;

    public PersistGroup(string cls, IEnumerable<PersistDef> fields)
    {
        _class = cls;
        _fields = new PersistFields(fields);
    }

    public override string ClassName() => _class;
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => _fields.Set(field, values);
    public override IReadOnlyList<string>? PersistValue(string field) => _fields.Get(field);
}

/// <summary>A non-GameBase mission object with persist fields and optional load checks.</summary>
/// <remarks>Used for Sky, Planet, StarField, MissionCenterPos, SimPalette, SimLight and Snowfall. Port of class PersistObject in src/game/mission_objects.hpp.</remarks>
public class PersistObject : SimObject
{
    private readonly string _class;
    private readonly PersistFields _fields;
    private readonly ClassContext _ctx;
    private readonly int _fixed;
    private readonly string? _required;
    private readonly ResourceLocks _locks = new(); // the SimPalette's .ppl

    /// <summary>Creates a persist object.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="fields">The persist field definitions.</param>
    /// <param name="ctx">The class context.</param>
    /// <param name="fixedId">The fixed object id, or 0.</param>
    /// <param name="requiredFile">The persist field that names a file the object needs, or null.</param>
    public PersistObject(string cls, IEnumerable<PersistDef> fields, ClassContext ctx, int fixedId = 0, string? requiredFile = null)
    {
        _class = cls;
        _fields = new PersistFields(fields);
        _ctx = ctx;
        _fixed = fixedId;
        _required = requiredFile;
    }

    /// <inheritdoc/>
    public override string ClassName() => _class;
    /// <inheritdoc/>
    public override int FixedId() => _fixed;
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    /// <inheritdoc/>
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => _fields.Set(field, values);
    /// <inheritdoc/>
    public override bool SetPersistElement(string field, int index, IReadOnlyList<string> values) =>
        _fields.SetElement(field, index, values);
    /// <inheritdoc/>
    public override IReadOnlyList<string>? PersistValue(string field) => _fields.Get(field);
    /// <summary>Returns the persist fields.</summary>
    /// <returns>The fields.</returns>
    public PersistFields Fields() => _fields;
    /// <summary>Returns the class context.</summary>
    /// <returns>The context.</returns>
    protected ClassContext Context() => _ctx;
    /// <summary>Returns the persist fields for a subclass to change.</summary>
    /// <returns>The fields.</returns>
    protected PersistFields MutableFields() => _fields;

    /// <inheritdoc/>
    /// <remarks>SimPalette (.ppl) and SimTerrain (.ted) fail onAdd when their file is missing.</remarks>
    public override bool OnAdd()
    {
        if (_required != null && _ctx.Game != null)
        {
            var v = _fields.Get(_required);
            if (v == null || v.Count == 0 || !_ctx.Game.ResourceExists(v[0]))
                return false;
            // SimPalette_onAdd (004fd9ec) loads the palette and holds it; after the object the instance stays until
            // purge (docs/resources.md 8.7).
            if (_class == "SimPalette" && _ctx.Game.GetResourceManager() is ResourceManager res)
                _locks.Load(res, v[0]);
        }
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Releases the resource locks, as the C++ destructor does.</remarks>
    public override void Destroy()
    {
        _locks.Release();
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
        var argc = args.Count;
        var f = MutableFields();
        void SetInt(string field, string v) => f.Set(field, [v]);
        if (args[0].StartsWith("Directional", StringComparison.Ordinal))
        {
            if (argc is 7 or 10)
            {
                SetInt("type", args[0] == "DirectionalWrap" ? "6" : "1");
                float[] color = [Num(1), Num(2), Num(3)];
                float[] dir = [Num(4), Num(5), Num(6)];
                using (ChopScope())
                {
                    var inv = X87.F32(Div(1.0, X87.Lenf(dir)));
                    for (var i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(inv, dir[i]));
                    var inv2 = X87.F32(Div(1.0, X87.F32(X87.Len(dir))));
                    for (var i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(inv2, dir[i]));
                }
                float[] ambient = [0, 0, 0];
                if (argc == 10)
                    for (var i = 0; i < 3; ++i)
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
            if (argc is 8 or 11)
            {
                SetInt("type", "2");
                var range = Num(1);
                float[] color = [Num(2), Num(3), Num(4)], pos = [Num(5), Num(6), Num(7)];
                float[] ambient = [0, 0, 0];
                if (argc == 11)
                    for (var i = 0; i < 3; ++i)
                        ambient[i] = Num(8 + i);
                SetVec("position", pos);
                SetVec("color", color);
                SetVec("ambient", ambient);
                f.Set("range", [TerrainCommands.PersistFloatText(range)]);
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
        float[] color = [value, value, value];
        MutableFields().SetPoint("color", color);
        Context().Game?.SetMaskBits(this, 0x04);
    }
}

/// <summary>The SimTerrain: loads the .dtf, joins container 7 and computes the verification CRC sent to clients (docs/terrain.md 2.1).</summary>
/// <remarks>Port of class TerrainObject in src/game/mission_objects.hpp.</remarks>
public sealed class TerrainObject : PersistObject
{
    private string _ted = "";
    private Vec3 _pos;
    private float _visibleDistance = 3000; // docs/terrain.md 2.2 default
    private Terrain? _terrain;
    private uint _crc = 0xffffffffu;       // ctor value -1

    /// <summary>Creates a SimTerrain.</summary>
    /// <param name="ctx">The class context.</param>
    /// <param name="fields">The persist field definitions.</param>
    public TerrainObject(ClassContext ctx, IEnumerable<PersistDef> fields) : base("SimTerrain", fields, ctx, 8, "tedFileName") { }

    /// <summary>Returns the verification CRC sent to clients.</summary>
    /// <returns>The CRC.</returns>
    public uint Crc() => _crc;
    /// <summary>Returns the loaded terrain.</summary>
    /// <returns>The terrain, or null.</returns>
    public Terrain? GetTerrain() => _terrain;
    /// <summary>Returns the visible distance.</summary>
    /// <returns>The distance.</returns>
    public float VisibleDistance() => _visibleDistance;

    /// <inheritdoc/>
    public override bool SetPersistField(string field, IReadOnlyList<string> values)
    {
        if (GameUtil.EqualsNoCase(field, "tedFileName"))
            _ted = values.Count == 0 ? "" : values[0];
        if (GameUtil.EqualsNoCase(field, "position") && values.Count != 0)
        {
            Span<float> p = [_pos.X, _pos.Y, _pos.Z];
            CScan.Floats(values[0], p);
            _pos = new Vec3(p[0], p[1], p[2]);
        }
        if (GameUtil.EqualsNoCase(field, "visibleDistance") && values.Count != 0)
            _visibleDistance = PersistFields.AtofRZ(values[0]);
        return base.SetPersistField(field, values);
    }

    /// <inheritdoc/>
    /// <remarks>The terrain goes with its resource locks.</remarks>
    public override void Destroy()
    {
        _terrain?.ReleaseLocks();
        base.Destroy();
    }

    /// <inheritdoc/>
    public override bool OnAdd()
    {
        var ctx = Context();
        if (!base.OnAdd() || ctx.Game == null || ctx.Game.GetResourceManager() == null)
            return false;
        var res = ctx.Game.GetResourceManager()!;
        _terrain = Terrain.Load(res, _ted);
        if (_terrain == null || !_terrain.Crc(res, _ted, ref _crc))
            return false; // the original exits: "Could not verify Terrain" (D11)
        _terrain.SetPosition(_pos);
        if (ctx.Game.GetContainer() is Container c)
        {
            var e = new Container.Entry(this, ObjectType.TerrainType, default, false, null);
            Span<float> box = stackalloc float[6];
            _terrain.WorldBox(box);
            // Own database (ctor 005171ac) taking every type; +0x2e0 (00517444).
            e.DatabaseKind = Container.Database.Terrain;
            e.ContainerMask = 0xffffffffu;
            e.VisibleDistance = Math.Min(_visibleDistance, 10000.0f);
            // Container properties for moving objects (docs/player-sim.md FC2, FC3): gravity and drag, and the box
            // extended by contHeight below and above (0x005193cc).
            e.TerrainData = _terrain;
            Span<float> g = stackalloc float[3];
            Fields().Point("contGravity", g, 0.0f, 0.0f, -9.8f);
            e.Gravity = Float3.Of(g);
            e.Drag = Fields().Real("contDrag", 0.0f);
            // SimTerrain_updateBox (0x005193cc): the local z range is -H..H with H = contHeight, or hmax when contHeight
            // is 0; hmin is not used (docs/static-sim.md TD8, `staticdiff` ch_item_hmax).
            var h = Fields().Real("contHeight", 10000.0f);
            var top = h != 0.0f ? h : _terrain.Hmax();
            box[2] = Store(Add(-top, _pos.Z));
            box[5] = Store(Add(top, _pos.Z));
            e.Box = Float6.Of(box);
            var t = _terrain;
            var slot = new System.Runtime.CompilerServices.StrongBox<Material?>(null);
            e.HitMaterial = slot;
            var services = ctx.Game;
            e.Cast = (s, en, ref hit, ref n) =>
            {
                var material = -1;
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
        var m = Manager()!;
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
    private readonly ClassContext _ctx;
    /// <summary>The team id, -1 until the group registers.</summary>
    public int Team { get; set; } = -1;

    /// <summary>Creates a team group.</summary>
    /// <param name="ctx">The class context.</param>
    public TeamGroup(ClassContext ctx) => _ctx = ctx;
    /// <inheritdoc/>
    public override string ClassName() => "TeamGroup";

    /// <inheritdoc/>
    public override bool TeamOf(ref int team)
    {
        team = Team;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>The team id is the current team count, and the group becomes <c>team&lt;digit&gt;</c> (TM6).</remarks>
    public override bool OnAdd()
    {
        Team = _ctx.Game != null ? _ctx.Game.AddTeamFromGroup() : 0;
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
    private static readonly PersistDef[] GameBaseFields = [new("dataBlock", 0, 1), new("name", 1, 1)];

    private static PersistDef[] Plus(PersistDef[] a, params PersistDef[] b) => [.. a, .. b];

    /// <summary>Registers every mission class with the sim class registry.</summary>
    public static void Register()
    {
        SensorManager.Register();
        var shape = Plus(GameBaseFields, new("position", 5, 1), new("rotation", 5, 1), new("destroyable", 4, 1));
        var staticBase = Plus(shape, new PersistDef("deleteOnDestroy", 4, 1));
        var item = Plus(staticBase, new("rotates", 4, 1), new("collideable", 4, 1), new("count", 2, 1));
        var marker = Plus(GameBaseFields, new("position", 5, 1), new("rotation", 5, 1));
        var trigger = Plus(marker, new("boundingBox", 8, 1), new("isSphere", 4, 1));
        PersistDef[] interior =
        [
            new("filename", 0, 1), new("isContainer", 2, 1), new("position", 5, 1), new("rotation", 5, 1), new("lightParams", 22, 1),
        ];
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
        SimClasses.RegisterClass("DropPointGroup", _ => new PersistGroup("DropPointGroup", []));
        SimClasses.RegisterClass("SimPath", _ => new SimPathObject());

        void Obj(string cls, PersistDef[] f, int fixedId = 0, string? required = null) =>
            SimClasses.RegisterClass(cls, c => new PersistObject(cls, f, c, fixedId, required));
        Obj("Sky",
        [
            new("dmlName", 0, 1), new("skyColor", 5, 1), new("hazeColor", 5, 1), new("featurePosition", 3, 1), new("size", 3, 1),
            new("textures", 2, 16),
        ], 729);
        Obj("StarField", [new("inFrontOfSky", 4, 1), new("colors", 5, 3)]);
        Obj("Planet",
        [
            new("fileName", 0, 1), new("azimuth", 3, 1), new("incidence", 3, 1), new("castShadows", 4, 1), new("useLensFlare", 4, 1),
            new("intensity", 5, 1), new("ambient", 5, 1), new("size", 3, 1), new("distance", 3, 1),
        ]);
        Obj("MissionCenterPos", [new("x", 3, 1), new("y", 3, 1), new("w", 3, 1), new("h", 3, 1)], 716);
        Obj("SimPalette", [new("fileName", 0, 1)], 0, "fileName");
        PersistDef[] terrainFields =
        [
            new("tedFileName", 0, 1), new("visibleDistance", 3, 1), new("hazeDistance", 3, 1), new("hazeVerticalMin", 3, 1),
            new("hazeVerticalMax", 3, 1), new("perspectiveDistance", 3, 1), new("screenSize", 3, 1), new("contGravity", 5, 1),
            new("contDrag", 3, 1), new("contHeight", 3, 1), new("position", 5, 1), new("rotation", 5, 1),
        ];
        SimClasses.RegisterClass("SimTerrain", c => new TerrainObject(c, terrainFields));
        SimClasses.RegisterClass("SimLight", c => new SimLightObject(
        [
            new("type", 2, 1), new("range", 3, 1), new("color", 5, 1), new("ambient", 5, 1), new("position", 5, 1),
            new("aim", 5, 1), new("isStatic", 4, 1),
        ], c));
        Obj("Snowfall", [new("intensity", 3, 1), new("wind", 5, 1), new("suspendRendering", 4, 1), new("rain", 4, 1)], 714);
    }
}
