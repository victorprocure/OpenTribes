// Port of src/sim/sim_classes.hpp and src/sim/sim_classes.cpp: the class registry (docs/sim.md 6) and the object
// classes known to the sim layer. GameBase and SimPathObject are classes of src/game (stage P4); the GameServices
// members that take them take their SimObject base here.
using OpenTribes.Core;
using OpenTribes.World;

namespace OpenTribes.Sim;

// src/sim/sim_classes.hpp struct GameServices: game-layer services used by GameBase objects while they register
// (docs/mission.md 3.2).
public abstract class GameServices
{
    // GB3: resolve the datablock name for the object's class; false fails onAdd.
    public abstract bool ResolveDataBlock(SimObject obj);
    // Console print at level 1 (usage lines of newObject).
    public virtual void PrintLine(string line) { }
    // GB5: script method through the GameBase::virtual lookup (no-op when undefined).
    public abstract void ScriptCallback(SimObject obj, string method, IReadOnlyList<string> extra);
    // TM6: create the next team; returns its id.
    public abstract int AddTeamFromGroup();
    // Sets the game layer keeps by fixed id (718, 719, ...), joined in onAdd.
    public abstract void JoinSet(SimObject obj, int setId);
    public abstract void LeaveSet(SimObject obj, int setId);
    // Delete notify of the Triggers a removed object occupies (0x004872e0).
    public virtual void DropTriggerOccupant(SimObject obj) { }
    public abstract bool ResourceExists(string name);
    public abstract ResourceManager? GetResourceManager();
    public abstract Container? GetContainer();
    // Sensor targets (GB4/GB7): the map name of a visible object; renamed with setMapName.
    public abstract void RenameTarget(int index, string name);
    // Console variable value (name without the '$'), for <vNAME> in map names (GB7).
    public abstract string Variable(string name);
    // NetObject::setMaskBits (0x0050a3e8): ORs bits into the object's ghost entries.
    public virtual void SetMaskBits(SimObject obj, uint bits) { }
    // The object's transform changed: refresh its container entry.
    public virtual void ObjectMoved(SimObject obj) { }
    // Number of datablocks in a group (ghost index width, 0x00402b60).
    public virtual int DataBlockCount(int group) => 0;
    // Index of the SimPath in SimPathManager order (registration order), or -1.
    public virtual int PathIndex(SimObject path) => -1;
    // The terrain object's grid (docs/collision.md 5.1), null without a SimTerrain, and the material of a square (index
    // into the terrain's material list), null when unknown.
    public virtual Terrain? GetTerrain() => null;
    public virtual Material? TerrainMaterial(int index) => null;
    // Gravity and drag the terrain container applies to an object it holds (docs/player-sim.md 2.6).
    public virtual bool TerrainForces(SimObject obj, Span<float> gravity, ref float drag) => false;
    // The same values as a SimMovement holds them (docs/player-sim.md FC2): taken when the object enters a container
    // inside the terrain, kept while it stays. Call after every container refile of the object; false outside the
    // terrain (the drag stays as taken).
    public virtual bool EnteredForces(SimObject obj, Span<float> gravity, ref float drag) => false;
    public virtual MaterialTable? GetMaterialTable() => null;
    // Candidate list of a stepped move (docs/collision.md 2.1): the images of the objects of `mask` whose world box
    // meets `query`, in container order, `mover` excluded.
    public virtual void CollisionCandidates(SimObject mover, uint mask, ReadOnlySpan<float> query, List<CollisionImage> output) { }
    // Event 0x41f to `target` with `sender` (docs/collision.md 8 E1): handled at the start of the next tick, dropped
    // when the sender is gone by then.
    public virtual void PostCollisionEvent(SimObject? target, SimObject? sender) { }
    // The ShapeCollisionImage of a ShapeBase object (docs/collision.md T1.2): transform, shape instance, collision
    // detail and mode. False when the object has no shape.
    public virtual bool ShapeImage(SimObject obj, CollisionImage image) => false;
    // Moveable motion that touches other objects (docs/static-sim.md 2.5): an elevator has already moved from `oldPos`;
    // displace what it carries or overlaps, true when that worked (SimMovement_displace 0x00505ed4). A door tests
    // whether its shape fits at the new transform (0x00505790).
    public virtual bool MoveableDisplace(SimObject mover, ReadOnlySpan<float> oldPos) => true;
    public virtual bool MoveableTest(SimObject mover, ReadOnlySpan<float> rotation, ReadOnlySpan<float> position) => true;
    // Sensor network (docs/static-sim.md 3.2, 3.3).
    public virtual void SensorInsert(SimObject sensor) { }
    public virtual void SensorRemove(SimObject sensor) { }
    public virtual void SensorTeam(SimObject sensor) { }
    // GameBase::setTeam of an object with an owning client moves that client (0040b394, player-manager.md 5).
    public virtual void ClientTeam(int client, int team) { }
    public virtual void TargetRemoved(SimObject obj) { }
    // A Mine entered the Destroyed state on the server: radius damage at its box centre (0x00458be4).
    public virtual void MineDestroyed(SimObject mine) { }
    // The SimPath behind a path id (SimPathManager slot), or null.
    public virtual SimObject? PathById(int id) => null;
}

// src/sim/sim_classes.hpp struct ClassContext: services some classes need when they are created or added.
public sealed class ClassContext
{
    public ResourceManager? Resources;
    public GameServices? Game;
    // FearCSDelegate processArguments: (isServer, transports...) starts networking.
    public Func<IReadOnlyList<string>, bool>? StartNetwork;
    // Runs when newServer has created the fixed objects (the game layer posts its periodic events here, docs/sim.md
    // TK7).
    public Action<SimManager>? ServerCreated;
}

// src/sim/sim_classes.hpp ClassFactory.
public delegate SimObject ClassFactory(ClassContext ctx);

// src/sim/sim_classes.hpp class NamedClassObject: generic object of a fixed class name, optionally with a
// constructor-fixed id.
public class NamedClassObject : SimObject
{
    private readonly string mClass;
    private readonly int mFixed;

    public NamedClassObject(string cls, int id = 0)
    {
        mClass = cls;
        mFixed = id;
    }

    public override string ClassName() => mClass;
    public override int FixedId() => mFixed;
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
}

// src/sim/sim_classes.hpp class NamedClassSet.
public class NamedClassSet : SimSet
{
    private readonly string mClass;
    private readonly int mFixed;

    public NamedClassSet(string cls, int id = 0)
    {
        mClass = cls;
        mFixed = id;
    }

    public override string ClassName() => mClass;
    public override int FixedId() => mFixed;
}

// src/sim/sim_classes.hpp class SimTimerSet: SimTimerSet (ctor 00507778, docs/sim.md TK6), posts itself an event every
// `interval` seconds of sim time and notifies its members when it runs.
public sealed class SimTimerSet : SimSet
{
    private readonly int mFixed;
    private readonly float mInterval;

    public SimTimerSet(int id, float interval)
    {
        mFixed = id;
        mInterval = interval;
    }

    public override string ClassName() => "SimTimerSet";
    public override int FixedId() => mFixed;
    public float Interval() => mInterval;

    // src/sim/sim_classes.cpp SimTimerSet::onAdd: 005077a4.
    public override bool OnAdd()
    {
        Post();
        return base.OnAdd();
    }

    // src/sim/sim_classes.cpp SimTimerSet::post.
    private void Post()
    {
        SimManager m = Manager()!;
        m.PostEvent(this, Fpu.AddF(m.Time(), mInterval), Fire);
    }

    // src/sim/sim_classes.cpp SimTimerSet::fire: 0050782c. Index loop over the live member list: members added by a
    // callback are notified in the same pass; a removal moves the last member into the freed slot (GS4).
    private void Fire()
    {
        float now = Manager()!.Time();
        for (int i = 0; i < Members().Count; ++i)
            Members()[i].OnTimerEvent(now, mFixed, mInterval);
        Post();
    }
}

// src/sim/sim_classes.hpp class NamedClassGroup.
public class NamedClassGroup : SimGroup
{
    private readonly string mClass;
    private readonly int mFixed;

    public NamedClassGroup(string cls, int id = 0)
    {
        mClass = cls;
        mFixed = id;
    }

    public override string ClassName() => mClass;
    public override int FixedId() => mFixed;
}

// The registry and the classes of src/sim/sim_classes.cpp.
public static class SimClasses
{
    // strcasecmp == 0 (ASCII).
    internal static bool EqualsNoCase(string a, string b)
    {
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

    // src/sim/sim_classes.cpp class SimVolume: mounts a volume through the resource manager (SimVolume,
    // docs/resources.md 8.3).
    private sealed class SimVolume : SimObject
    {
        private readonly ResourceManager? mRes;
        private string mFile = "", mPending = "";
        private bool mScopeAlways = true; // ctor 00508148 sets 0x140; mission volumes keep it

        public SimVolume(ResourceManager? res) => mRes = res;
        public override string ClassName() => "SimVolume";

        // SimVolume_processArguments: net flags 0x140 only with a true second argument.
        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            mScopeAlways = args.Count == 2 && args[1].Length != 0 && (args[1][0] == 't' || args[1][0] == 'T' || args[1][0] == '1');
            if (args.Count == 0 || args.Count > 2 || mRes == null)
                return false;
            if (!mRes.AddVolume(args[0]))
                return false;
            mFile = StringTable.Global().Intern(args[0]);
            return true;
        }

        public override uint NetFlags(uint constructed) => mScopeAlways ? constructed | 0x140u : constructed & ~0x140u;

        // Missions set the file through the fileName persist slot and mount in onAdd.
        public override bool SetPersistField(string field, IReadOnlyList<string> values)
        {
            if (!EqualsNoCase(field, "fileName"))
                return false;
            // Interned (0x00508200): case variants share the first spelling.
            mPending = values.Count == 0 ? "" : StringTable.Global().Intern(values[0]);
            return true;
        }

        public override bool OnAdd()
        {
            if (mPending.Length == 0)
                return true;
            if (mRes == null || !mRes.AddVolume(mPending))
                return false;
            mFile = mPending;
            return true;
        }

        public override void OnRemove()
        {
            if (mRes != null && mFile.Length != 0)
                mRes.RemoveVolume(mFile);
            mFile = "";
        }

        public string File() => mFile.Length == 0 ? mPending : mFile;
        public bool GhostAlways() => mScopeAlways;
    }

    // src/sim/sim_classes.cpp simVolumeInfo: SimVolume state the ghost code needs: file name (+0x68) and whether the
    // volume is ghost-always. False when obj is not a SimVolume.
    public static bool SimVolumeInfo(SimObject obj, out string file, out bool ghostAlways)
    {
        file = "";
        ghostAlways = false;
        if (!EqualsNoCase(obj.ClassName(), "SimVolume") || obj is not SimVolume v)
            return false;
        file = v.File();
        ghostAlways = v.GhostAlways();
        return true;
    }

    // src/sim/sim_classes.cpp class ChatMenu.
    private sealed class ChatMenu : SimObject
    {
        public override string ClassName() => "ChatMenu";
        public override bool ProcessArguments(IReadOnlyList<string> args) => args.Count != 0;
    }

    // src/sim/sim_classes.cpp class FailingClass: classes the original creates but whose constructor-side checks fail
    // on a dedicated server (docs/sim.md 6.5): processArguments fails for every argument list (ShapeBase,
    // Net::CSDelegate), registration fails (FlyingCamera, EditCamera), or only arguments make it fail (IRCClient).
    private sealed class FailingClass : SimObject
    {
        public enum Mode { Args, Register, AnyArgs }
        private readonly string mClass;
        private readonly Mode mMode;
        private readonly int mFixed;

        public FailingClass(string cls, Mode mode, int id = 0)
        {
            mClass = cls;
            mMode = mode;
            mFixed = id;
        }

        public override string ClassName() => mClass;
        public override int FixedId() => mFixed;
        public override bool OnAdd() => mMode != Mode.Register;
        public override bool ProcessArguments(IReadOnlyList<string> args) =>
            mMode == Mode.AnyArgs ? args.Count == 0 : mMode == Mode.Register;
    }

    // src/sim/sim_classes.cpp class FearCSDelegate.
    private sealed class FearCSDelegate : SimObject
    {
        private readonly ClassContext mCtx;

        public FearCSDelegate(ClassContext ctx) => mCtx = ctx;
        public override string ClassName() => "FearCSDelegate";
        public override int FixedId() => 35; // Net::CSDelegate ctor (0054e6b8)

        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            // The reference fails an empty list and lists of one or two arguments (docs/sim.md 6.5).
            if (args.Count < 3)
                return false;
            // Activating the server SimNet gives the manager the first id of net block 2 (MG6).
            if (Manager() is SimManager m && m.ManagerId() == 0)
                m.SetManagerId(2048);
            return mCtx.StartNetwork == null || mCtx.StartNetwork(args);
        }
    }

    // src/sim/sim_classes.cpp struct ClassEntry.
    private sealed class ClassEntry
    {
        public string Name = "";
        public ClassFactory Make = _ => throw new InvalidOperationException();
    }

    private static ClassFactory Plain(Func<SimObject> make) => _ => make();

    // src/sim/sim_classes.cpp registry: script-creatable classes implemented so far. Mission object classes are
    // registered by the game layer (registerMissionClasses, stage P4).
    private static readonly List<ClassEntry> sRegistry = new()
    {
        new() { Name = "SimSet", Make = Plain(() => new SimSet()) },
        new() { Name = "SimGroup", Make = Plain(() => new SimGroup()) },
        new() { Name = "SimVolume", Make = c => new SimVolume(c.Resources) },
        new() { Name = "SimConsoleScheduler", Make = Plain(() => new NamedClassObject("SimConsoleScheduler", 43)) },
        new() { Name = "FearCSDelegate", Make = c => new FearCSDelegate(c) },
        new() { Name = "IRCClient", Make = Plain(() => new FailingClass("IRCClient", FailingClass.Mode.AnyArgs, 46)) },
        new() { Name = "ChatMenu", Make = Plain(() => new ChatMenu()) },
        new() { Name = "EditCamera", Make = Plain(() => new FailingClass("EditCamera", FailingClass.Mode.Register)) },
        new() { Name = "FlyingCamera", Make = Plain(() => new FailingClass("FlyingCamera", FailingClass.Mode.Register)) },
        new() { Name = "ShapeBase", Make = Plain(() => new FailingClass("ShapeBase", FailingClass.Mode.Args)) },
        new() { Name = "Net::CSDelegate", Make = Plain(() => new FailingClass("Net::CSDelegate", FailingClass.Mode.Args)) },
        new() { Name = "SimMoveObject", Make = Plain(() => new NamedClassObject("SimMoveObject")) },
        new() { Name = "CenterPrint", Make = Plain(() => new NamedClassObject("CenterPrint")) },
        new() { Name = "FearGuiFormattedText", Make = Plain(() => new NamedClassObject("FearGuiFormattedText")) },
        new() { Name = "FearGuiInventory", Make = Plain(() => new NamedClassObject("FearGuiInventory")) },
        new() { Name = "FearGuiPurchase", Make = Plain(() => new NamedClassObject("FearGuiPurchase")) },
    };

    // src/sim/sim_classes.cpp registerClass: adds or replaces a class (the game layer registers mission classes this
    // way).
    public static void RegisterClass(string name, ClassFactory factory)
    {
        foreach (ClassEntry c in sRegistry)
            if (EqualsNoCase(c.Name, name))
            {
                c.Make = factory;
                return;
            }
        sRegistry.Add(new ClassEntry { Name = name, Make = factory });
    }

    // src/sim/sim_classes.cpp createObject: creates an object by class name, ASCII case-insensitive (CL2). Null when
    // unknown.
    public static SimObject? CreateObject(string className, ClassContext ctx)
    {
        foreach (ClassEntry c in sRegistry)
            if (EqualsNoCase(c.Name, className))
                return c.Make(ctx);
        return null;
    }
}
