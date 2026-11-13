namespace OpenTribes.Sim;

using OpenTribes.Core;
using OpenTribes.World;

/// <summary>The game-layer services that GameBase objects use while they register (docs/mission.md 3.2).</summary>
/// <remarks>Port of src/sim/sim_classes.hpp GameServices. GameBase and SimPathObject are classes of src/game (project OpenTribes.Game), so the members that take them take their <see cref="SimObject"/> base here.</remarks>
public abstract class GameServices
{
    /// <summary>Resolves the datablock name for the class of an object (GB3).</summary>
    /// <param name="obj">The object.</param>
    /// <returns>False to fail <see cref="SimObject.OnAdd"/>.</returns>
    public abstract bool ResolveDataBlock(SimObject obj);
    /// <summary>Prints a console line at level 1, for example the usage lines of <c>newObject</c>.</summary>
    /// <param name="line">The text.</param>
    public virtual void PrintLine(string line) { }
    /// <summary>Calls a script method through the GameBase virtual lookup (GB5). Does nothing when the method is undefined.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="method">The method name.</param>
    /// <param name="extra">The arguments after the object.</param>
    public abstract void ScriptCallback(SimObject obj, string method, IReadOnlyList<string> extra);
    /// <summary>Creates the next team (TM6).</summary>
    /// <returns>The team id.</returns>
    public abstract int AddTeamFromGroup();
    /// <summary>Adds an object to a set the game layer keeps by fixed id (718, 719, ...), called from <see cref="SimObject.OnAdd"/>.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="setId">The set id.</param>
    public abstract void JoinSet(SimObject obj, int setId);
    /// <summary>Removes an object from a set the game layer keeps by fixed id.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="setId">The set id.</param>
    public abstract void LeaveSet(SimObject obj, int setId);
    /// <summary>Sends the delete notification to the triggers a removed object occupies (004872e0).</summary>
    /// <param name="obj">The removed object.</param>
    public virtual void DropTriggerOccupant(SimObject obj) { }
    /// <summary>Tells whether a resource exists.</summary>
    /// <param name="name">The resource name.</param>
    /// <returns>True when it exists.</returns>
    public abstract bool ResourceExists(string name);
    /// <summary>Gets the resource manager.</summary>
    /// <returns>The manager, or null.</returns>
    public abstract ResourceManager? GetResourceManager();
    /// <summary>Gets the world container.</summary>
    /// <returns>The container, or null.</returns>
    public abstract Container? GetContainer();
    /// <summary>Renames a sensor target (GB4, GB7): the map name of a visible object, changed with <c>setMapName</c>.</summary>
    /// <param name="index">The target index.</param>
    /// <param name="name">The new name.</param>
    public abstract void RenameTarget(int index, string name);
    /// <summary>Reads a console variable for <c>&lt;vNAME&gt;</c> in map names (GB7).</summary>
    /// <param name="name">The variable name without the <c>$</c>.</param>
    /// <returns>The value.</returns>
    public abstract string Variable(string name);
    /// <summary>ORs bits into the ghost entries of an object (NetObject::setMaskBits, 0050a3e8).</summary>
    /// <param name="obj">The object.</param>
    /// <param name="bits">The mask bits.</param>
    public virtual void SetMaskBits(SimObject obj, uint bits) { }
    /// <summary>Refreshes the container entry of an object whose transform changed.</summary>
    /// <param name="obj">The object.</param>
    public virtual void ObjectMoved(SimObject obj) { }
    /// <summary>Gets the number of datablocks in a group, which sets the ghost index width (00402b60).</summary>
    /// <param name="group">The group.</param>
    /// <returns>The count.</returns>
    public virtual int DataBlockCount(int group) => 0;
    /// <summary>Gets the index of a SimPath in SimPathManager order, which is registration order.</summary>
    /// <param name="path">The path object.</param>
    /// <returns>The index, or -1.</returns>
    public virtual int PathIndex(SimObject path) => -1;
    /// <summary>Gets the grid of the terrain object (docs/collision.md 5.1).</summary>
    /// <returns>The terrain, or null without a SimTerrain.</returns>
    public virtual Terrain? GetTerrain() => null;
    /// <summary>Gets the material of a terrain square.</summary>
    /// <param name="index">The index into the terrain's material list.</param>
    /// <returns>The material, or null when unknown.</returns>
    public virtual Material? TerrainMaterial(int index) => null;
    /// <summary>Gets the gravity and drag the terrain container applies to an object it holds (docs/player-sim.md 2.6).</summary>
    /// <param name="obj">The object.</param>
    /// <param name="gravity">Receives the gravity vector.</param>
    /// <param name="drag">The drag, updated when the container applies one.</param>
    /// <returns>True when the container applies forces.</returns>
    public virtual bool TerrainForces(SimObject obj, Span<float> gravity, ref float drag) => false;
    /// <summary>Gets the same values as a SimMovement holds them (docs/player-sim.md FC2): taken when the object enters a container inside the terrain and kept while it stays. Call it after every container refile of the object.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="gravity">Receives the gravity vector.</param>
    /// <param name="drag">The drag, updated when the object is inside the terrain.</param>
    /// <returns>False outside the terrain, where the drag stays as taken.</returns>
    public virtual bool EnteredForces(SimObject obj, Span<float> gravity, ref float drag) => false;
    /// <summary>Gets the material table.</summary>
    /// <returns>The table, or null.</returns>
    public virtual MaterialTable? GetMaterialTable() => null;
    /// <summary>Collects the candidate list of a stepped move (docs/collision.md 2.1): the images of the objects of <paramref name="mask"/> whose world box meets <paramref name="query"/>, in container order.</summary>
    /// <param name="mover">The moving object, excluded.</param>
    /// <param name="mask">The container type mask.</param>
    /// <param name="query">The query box.</param>
    /// <param name="output">Receives the images.</param>
    public virtual void CollisionCandidates(SimObject mover, uint mask, ReadOnlySpan<float> query, List<CollisionImage> output) { }
    /// <summary>Posts event 0x41f to the target (docs/collision.md 8 E1). It is handled at the start of the next tick and dropped when the sender is gone by then.</summary>
    /// <param name="target">The destination.</param>
    /// <param name="sender">The sender.</param>
    public virtual void PostCollisionEvent(SimObject? target, SimObject? sender) { }
    /// <summary>Fills the ShapeCollisionImage of a ShapeBase object: transform, shape instance, collision detail and mode (docs/collision.md T1.2).</summary>
    /// <param name="obj">The object.</param>
    /// <param name="image">The image to fill.</param>
    /// <returns>False when the object has no shape.</returns>
    public virtual bool ShapeImage(SimObject obj, CollisionImage image) => false;
    /// <summary>Displaces what an elevator carries or overlaps after it moved from <paramref name="oldPos"/> (docs/static-sim.md 2.5, SimMovement_displace 00505ed4).</summary>
    /// <param name="mover">The moved object.</param>
    /// <param name="oldPos">The position before the move.</param>
    /// <returns>True when that worked.</returns>
    public virtual bool MoveableDisplace(SimObject mover, ReadOnlySpan<float> oldPos) => true;
    /// <summary>Tests whether the shape of a door fits at a new transform (00505790).</summary>
    /// <param name="mover">The door.</param>
    /// <param name="rotation">The new rotation.</param>
    /// <param name="position">The new position.</param>
    /// <returns>True when it fits.</returns>
    public virtual bool MoveableTest(SimObject mover, ReadOnlySpan<float> rotation, ReadOnlySpan<float> position) => true;
    /// <summary>Adds a sensor to the sensor network (docs/static-sim.md 3.2, 3.3).</summary>
    /// <param name="sensor">The sensor.</param>
    public virtual void SensorInsert(SimObject sensor) { }
    /// <summary>Removes a sensor from the sensor network.</summary>
    /// <param name="sensor">The sensor.</param>
    public virtual void SensorRemove(SimObject sensor) { }
    /// <summary>Tells the sensor network that a sensor changed team.</summary>
    /// <param name="sensor">The sensor.</param>
    public virtual void SensorTeam(SimObject sensor) { }
    /// <summary>Moves a client to a team when <c>GameBase::setTeam</c> changes the team of an object it owns (0040b394, player-manager.md 5).</summary>
    /// <param name="client">The client id.</param>
    /// <param name="team">The team.</param>
    public virtual void ClientTeam(int client, int team) { }
    /// <summary>Tells the game layer that the target of an object was removed.</summary>
    /// <param name="obj">The object.</param>
    public virtual void TargetRemoved(SimObject obj) { }
    /// <summary>Applies radius damage at the box centre of a Mine that entered the Destroyed state on the server (00458be4).</summary>
    /// <param name="mine">The mine.</param>
    public virtual void MineDestroyed(SimObject mine) { }
    /// <summary>Gets the SimPath behind a path id (SimPathManager slot).</summary>
    /// <param name="id">The path id.</param>
    /// <returns>The path, or null.</returns>
    public virtual SimObject? PathById(int id) => null;
}

/// <summary>The services some classes need when they are created or added.</summary>
/// <remarks>Port of src/sim/sim_classes.hpp ClassContext.</remarks>
public sealed class ClassContext
{
    /// <summary>The resource manager.</summary>
    public ResourceManager? Resources { get; set; }
    /// <summary>The game-layer services.</summary>
    public GameServices? Game { get; set; }
    /// <summary>Starts networking from the arguments of FearCSDelegate processArguments: the server flag and the transports.</summary>
    public Func<IReadOnlyList<string>, bool>? StartNetwork { get; set; }
    /// <summary>Runs when <c>newServer</c> has created the fixed objects. The game layer posts its periodic events here (docs/sim.md TK7).</summary>
    public Action<SimManager>? ServerCreated { get; set; }
}

/// <summary>Creates a script object of a registered class.</summary>
/// <param name="ctx">The creation context.</param>
/// <returns>The new object.</returns>
public delegate SimObject ClassFactory(ClassContext ctx);

/// <summary>A generic object of a fixed class name with an optional constructor-fixed id.</summary>
public class NamedClassObject : SimObject
{
    private readonly string _class;
    private readonly int _fixed;

    /// <summary>Creates the object.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="id">The fixed id, or 0 for a pool id.</param>
    public NamedClassObject(string cls, int id = 0)
    {
        _class = cls;
        _fixed = id;
    }

    /// <inheritdoc/>
    public override string ClassName() => _class;
    /// <inheritdoc/>
    public override int FixedId() => _fixed;
    /// <summary>Accepts any arguments.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>Always true.</returns>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
}

/// <summary>A set of a fixed class name with an optional constructor-fixed id.</summary>
public class NamedClassSet : SimSet
{
    private readonly string _class;
    private readonly int _fixed;

    /// <summary>Creates the set.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="id">The fixed id, or 0 for a pool id.</param>
    public NamedClassSet(string cls, int id = 0)
    {
        _class = cls;
        _fixed = id;
    }

    /// <inheritdoc/>
    public override string ClassName() => _class;
    /// <inheritdoc/>
    public override int FixedId() => _fixed;
}

/// <summary>A set that posts itself an event every <c>interval</c> seconds of sim time and notifies its members when it runs.</summary>
/// <remarks>Follows the SimTimerSet constructor (00507778), docs/sim.md TK6.</remarks>
public sealed class SimTimerSet : SimSet
{
    private readonly int _fixed;
    private readonly float _interval;

    /// <summary>Creates the set.</summary>
    /// <param name="id">The fixed id.</param>
    /// <param name="interval">The notification interval in seconds.</param>
    public SimTimerSet(int id, float interval)
    {
        _fixed = id;
        _interval = interval;
    }

    /// <inheritdoc/>
    public override string ClassName() => "SimTimerSet";
    /// <inheritdoc/>
    public override int FixedId() => _fixed;
    /// <summary>Gets the notification interval.</summary>
    /// <returns>The interval in seconds.</returns>
    public float Interval() => _interval;

    /// <summary>Starts the timer when the set is registered (005077a4).</summary>
    /// <returns>Always true.</returns>
    public override bool OnAdd()
    {
        Post();
        return base.OnAdd();
    }

    // src/sim/sim_classes.cpp SimTimerSet::post.
    private void Post()
    {
        var m = Manager()!;
        m.PostEvent(this, Fpu.AddF(m.Time(), _interval), Fire);
    }

    // src/sim/sim_classes.cpp SimTimerSet::fire: 0050782c. Index loop over the live member list: members added by a
    // callback are notified in the same pass; a removal moves the last member into the freed slot (GS4).
    private void Fire()
    {
        var now = Manager()!.Time();
        for (var i = 0; i < Members().Count; ++i)
            Members()[i].OnTimerEvent(now, _fixed, _interval);
        Post();
    }
}

/// <summary>A group of a fixed class name with an optional constructor-fixed id.</summary>
public class NamedClassGroup : SimGroup
{
    private readonly string _class;
    private readonly int _fixed;

    /// <summary>Creates the group.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="id">The fixed id, or 0 for a pool id.</param>
    public NamedClassGroup(string cls, int id = 0)
    {
        _class = cls;
        _fixed = id;
    }

    /// <inheritdoc/>
    public override string ClassName() => _class;
    /// <inheritdoc/>
    public override int FixedId() => _fixed;
}

/// <summary>The class registry (docs/sim.md 6) and the object classes known to the sim layer.</summary>
/// <remarks>Port of src/sim/sim_classes.cpp.</remarks>
public static class SimClasses
{
    // strcasecmp == 0 (ASCII).
    internal static bool EqualsNoCase(string a, string b)
    {
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

    // src/sim/sim_classes.cpp class SimVolume: mounts a volume through the resource manager (SimVolume,
    // docs/resources.md 8.3).
    private sealed class SimVolume : SimObject
    {
        private readonly ResourceManager? _res;
        private string _file = "", _pending = "";
        private bool _scopeAlways = true; // ctor 00508148 sets 0x140; mission volumes keep it

        public SimVolume(ResourceManager? res) => _res = res;
        /// <inheritdoc/>
        public override string ClassName() => "SimVolume";

        // SimVolume_processArguments: net flags 0x140 only with a true second argument.
        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            _scopeAlways = args.Count == 2 && args[1].Length != 0 && (args[1][0] == 't' || args[1][0] == 'T' || args[1][0] == '1');
            if (args.Count == 0 || args.Count > 2 || _res == null)
                return false;
            if (!_res.AddVolume(args[0]))
                return false;
            _file = StringTable.Global().Intern(args[0]);
            return true;
        }

        /// <inheritdoc/>
        public override uint NetFlags(uint constructed) => _scopeAlways ? constructed | 0x140u : constructed & ~0x140u;

        // Missions set the file through the fileName persist slot and mount in onAdd.
        /// <inheritdoc/>
        public override bool SetPersistField(string field, IReadOnlyList<string> values)
        {
            if (!EqualsNoCase(field, "fileName"))
                return false;
            // Interned (0x00508200): case variants share the first spelling.
            _pending = values.Count == 0 ? "" : StringTable.Global().Intern(values[0]);
            return true;
        }

        /// <inheritdoc/>
        public override bool OnAdd()
        {
            if (_pending.Length == 0)
                return true;
            if (_res == null || !_res.AddVolume(_pending))
                return false;
            _file = _pending;
            return true;
        }

        /// <inheritdoc/>
        public override void OnRemove()
        {
            if (_res != null && _file.Length != 0)
                _res.RemoveVolume(_file);
            _file = "";
        }

        public string File() => _file.Length == 0 ? _pending : _file;
        public bool GhostAlways() => _scopeAlways;
    }

    /// <summary>Reads the SimVolume state the ghost code needs.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="file">Receives the file name (+0x68).</param>
    /// <param name="ghostAlways">Receives whether the volume is ghost-always.</param>
    /// <returns>False when <paramref name="obj"/> is not a SimVolume.</returns>
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

    /// <summary>The ChatMenu class. It is a member of the input consumer set (object 25) until it is deleted.</summary>
    public sealed class ChatMenu : SimObject
    {
        /// <summary>The first argument of <c>newObject(name, ChatMenu, title)</c>, the heading the menu display shows.</summary>
        public string Title { get; set; } = "";
        /// <inheritdoc/>
        public override string ClassName() => "ChatMenu";

        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
                return false;
            Title = args[0];
            return true;
        }

        // a created ChatMenu is a member of the input consumer set (object 25) until it is deleted.
        /// <inheritdoc/>
        public override bool OnAdd()
        {
            Manager()?.FindById(25)?.AsSet()?.AddObject(this);
            return true;
        }
    }

    // src/sim/sim_classes.cpp class FailingClass: classes the original creates but whose constructor-side checks fail
    // on a dedicated server (docs/sim.md 6.5): processArguments fails for every argument list (ShapeBase,
    // Net::CSDelegate), registration fails (FlyingCamera, EditCamera), or only arguments make it fail (IRCClient).
    private sealed class FailingClass : SimObject
    {
        public enum Mode { Args, Register, AnyArgs }
        private readonly string _class;
        private readonly Mode _mode;
        private readonly int _fixed;

        public FailingClass(string cls, Mode mode, int id = 0)
        {
            _class = cls;
            _mode = mode;
            _fixed = id;
        }

        /// <inheritdoc/>
        public override string ClassName() => _class;
        /// <inheritdoc/>
        public override int FixedId() => _fixed;
        /// <inheritdoc/>
        public override bool OnAdd() => _mode != Mode.Register;
        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args) =>
            _mode == Mode.AnyArgs ? args.Count == 0 : _mode == Mode.Register;
    }

    // src/sim/sim_classes.cpp class GuiGroup: GUI and editor controls, SimGroup based, any arguments (docs/sim.md Q5).
    private class GuiGroup : SimGroup
    {
        private readonly string _class;

        public GuiGroup(string cls) => _class = cls;
        /// <inheritdoc/>
        public override string ClassName() => _class;
    }

    // src/sim/sim_classes.cpp class GuiScrollGroup: creates its content control when registered.
    private sealed class GuiScrollGroup : GuiGroup
    {
        public GuiScrollGroup(string cls) : base(cls) { }

        /// <inheritdoc/>
        public override bool OnAdd()
        {
            AddObject(new GuiGroup("SimGui::ScrollContentCtrl"));
            return base.OnAdd();
        }
    }

    // src/sim/sim_classes.cpp class GuiServerList: FearGui::ServerListCtrl::onAdd prints one line.
    private sealed class GuiServerList : GuiGroup
    {
        public GuiServerList(string cls) : base(cls) { }

        /// <inheritdoc/>
        public override bool OnAdd()
        {
            Manager()?.Print?.Invoke("ServerListCtrl::onAdd() mbQueryMoreServers: FALSE");
            return base.OnAdd();
        }
    }

    // src/sim/sim_classes.cpp class GuiDelegate: plain object, no arguments (D28).
    private sealed class GuiDelegate : SimObject
    {
        private readonly string _class;

        public GuiDelegate(string cls) => _class = cls;
        /// <inheritdoc/>
        public override string ClassName() => _class;
        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args) => args.Count == 0;
    }

    // src/sim/sim_classes.cpp class GuiCanvas: no argument prints the usage and fails.
    private sealed class GuiCanvas : SimObject
    {
        /// <inheritdoc/>
        public override string ClassName() => "SimGui::Canvas";

        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            if (args.Count != 0)
                return true;
            Manager()?.Print?.Invoke("SimCanvas: title width height quit(t/f)");
            return false;
        }
    }

    // src/sim/sim_classes.cpp class FearCSDelegate.
    private sealed class FearCSDelegate : SimObject
    {
        private readonly ClassContext _ctx;

        public FearCSDelegate(ClassContext ctx) => _ctx = ctx;
        /// <inheritdoc/>
        public override string ClassName() => "FearCSDelegate";
        /// <inheritdoc/>
        public override int FixedId() => 35; // Net::CSDelegate ctor (0054e6b8)

        /// <inheritdoc/>
        public override bool ProcessArguments(IReadOnlyList<string> args)
        {
            // The reference fails an empty list and lists of one or two arguments (docs/sim.md 6.5).
            if (args.Count < 3)
                return false;
            // Activating the server SimNet gives the manager the first id of net block 2 (MG6).
            if (Manager() is SimManager m && m.ManagerId() == 0)
                m.SetManagerId(2048);
            return _ctx.StartNetwork == null || _ctx.StartNetwork(args);
        }
    }

    // src/sim/sim_classes.cpp struct ClassEntry.
    private sealed class ClassEntry
    {
        public string Name { get; set; } = "";
        public ClassFactory Make { get; set; } = _ => throw new InvalidOperationException();
    }

    private static ClassFactory Plain(Func<SimObject> make) => _ => make();

    // src/sim/sim_classes.cpp registry: script-creatable classes implemented so far. Mission object classes are
    // registered by the game layer (MissionClasses.Register in OpenTribes.Game).
    private static readonly List<ClassEntry> Registry =
    [
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
        new() { Name = "SimGui::Canvas", Make = Plain(() => new GuiCanvas()) },
        // MEObjectList kills the original when it is created (D28): registration fails here.
        new() { Name = "MEObjectList", Make = Plain(() => new FailingClass("MEObjectList", FailingClass.Mode.Register)) },
        new() { Name = "FearGui::ServerListCtrl", Make = Plain(() => new GuiServerList("FearGui::ServerListCtrl")) },
    ];

    // src/sim/gui_classes.inc: the GUI and editor classes a dedicated server can create (docs/sim.md 6.2, Q5).
    private static readonly string[] GuiControls =
    [
        "CenterPrint", "FearGui::CFGButton", "FearGui::CMDObserve",
        "FearGui::ClockHud", "FearGui::ColumnListCtrl", "FearGui::CommandItemCtrl",
        "FearGui::CommandTeamCtrl", "FearGui::CommandTurretCtrl", "FearGui::CompassHud",
        "FearGui::ControlsListCtrl", "FearGui::Crosshair", "FearGui::CurrentWeaponHud",
        "FearGui::FGBitmapCtrl", "FearGui::FGBuddyComboBox", "FearGui::FGChatMenu",
        "FearGui::FGCommandBox", "FearGui::FGCommandPanel", "FearGui::FGCommandView",
        "FearGui::FGControl", "FearGui::FGControlsComboBox", "FearGui::FGCurrentCommand",
        "FearGui::FGDlgBox", "FearGui::FGFilterCBListCtrl", "FearGui::FGFilterComboBox",
        "FearGui::FGHBuySell", "FearGui::FGHCommandObjectivePage", "FearGui::FGHInventory",
        "FearGui::FGHSensorPing", "FearGui::FGHelpCtrl", "FearGui::FGIRCActiveTextEdit",
        "FearGui::FGIRCBanControl", "FearGui::FGIRCChannelCtrl", "FearGui::FGIRCChannelList",
        "FearGui::FGIRCChannelPropertiesControl", "FearGui::FGIRCJoinControl", "FearGui::FGIRCNicknames",
        "FearGui::FGIRCScrollChannel", "FearGui::FGIRCTextEdit", "FearGui::FGIRCTopicCtrl",
        "FearGui::FGIRCView", "FearGui::FGLagIndicator", "FearGui::FGMenuCtrl",
        "FearGui::FGMenuItem", "FearGui::FGMissionComboBox", "FearGui::FGPaletteCtrl",
        "FearGui::FGPlayerComboBox", "FearGui::FGPopUpMenu", "FearGui::FGRecordingListCtrl",
        "FearGui::FGServerFilterComboBox", "FearGui::FGShellBorder", "FearGui::FGSimpleText",
        "FearGui::FGSlider", "FearGui::FGStandardComboBox", "FearGui::FGTab",
        "FearGui::FGTabMenu", "FearGui::FGTextFormat", "FearGui::FGTextList",
        "FearGui::FGUniversalButton", "FearGui::FearGuiAbortDlg", "FearGui::FearGuiBox",
        "FearGui::FearGuiChat", "FearGui::FearGuiChatDisplay", "FearGui::FearGuiDialog",
        "FearGui::FearGuiMenu", "FearGui::FearGuiPage", "FearGui::FearGuiRadio",
        "FearGui::FearGuiSwitch", "FearGui::FilterCtrl", "FearGui::FilterListCtrl",
        "FearGui::FilterVarCtrl", "FearGui::HealthHud", "FearGui::HudCtrl",
        "FearGui::HudListCtrl", "FearGui::InventoryListCtrl", "FearGui::JetPackHud",
        "FearGui::MMShellBorder", "FearGui::MasterServerList", "FearGui::MenuDisplayCtrl",
        "FearGui::MissionListCtrl", "FearGui::PlayerListCtrl", "FearGui::ScoreListCtrl",
        "FearGui::ServerInfoCtrl", "FearGui::ShapeView", "FearGui::SkinView",
        "FearGui::SmackerCtrl", "FearGui::StandardListCtrl", "FearGui::TSCommander",
        "FearGui::TestEdit", "FearGuiFormattedText", "FearGuiInventory",
        "FearGuiPurchase", "MEButton", "MECheck",
        "MEInspectTagList", "MEPopup", "MEPopupButton",
        "MERadio", "METextEdit", "METextList",
        "SimGui::ActiveCtrl", "SimGui::AnimateBMA", "SimGui::ArrayCtrl",
        "SimGui::BitmapBox", "SimGui::BitmapCtrl", "SimGui::ComboBox",
        "SimGui::Control", "SimGui::EditControl", "SimGui::HelpCtrl",
        "SimGui::PaletteCtrl", "SimGui::ProgressCtrl", "SimGui::ScrollContentCtrl",
        "SimGui::SimpleText", "SimGui::Slider", "SimGui::TSControl",
        "SimGui::TestButton", "SimGui::TestCheck", "SimGui::TestControl",
        "SimGui::TestRadial", "SimGui::TextEdit", "SimGui::TextFormat",
        "SimGui::TextList", "SimGui::TextWrap", "SimGui::TimerCtrl",
    ];

    private static readonly string[] GuiScrollControls =
    [
        "FearGui::FearGuiScrollCtrl", "FearGui::FGHudMatrix", "SimGui::MatrixCtrl",
        "SimGui::ScrollCtrl",
    ];

    private static readonly string[] GuiDelegates =
    [
        "SimGui::ConnectDelegate", "SimGui::CreateServerDelegate", "SimGui::IRCChatDelegate",
        "SimGui::JoinGameDelegate", "SimGui::MainMenuDelegate", "SimGui::OptionsDelegate",
        "SimGui::playDelegate", "SimGui::PlayerSetupDelegate", "SimGui::quitDelegate",
        "SimGui::RecordingsDelegate",
    ];

    static SimClasses()
    {
        foreach (var name in GuiControls)
            Registry.Add(new ClassEntry { Name = name, Make = Plain(() => new GuiGroup(name)) });
        foreach (var name in GuiScrollControls)
            Registry.Add(new ClassEntry { Name = name, Make = Plain(() => new GuiScrollGroup(name)) });
        foreach (var name in GuiDelegates)
            Registry.Add(new ClassEntry { Name = name, Make = Plain(() => new GuiDelegate(name)) });
    }

    /// <summary>Adds or replaces a class. The game layer registers the mission classes this way.</summary>
    /// <param name="name">The class name.</param>
    /// <param name="factory">The factory.</param>
    public static void RegisterClass(string name, ClassFactory factory)
    {
        foreach (var c in Registry)
            if (EqualsNoCase(c.Name, name))
            {
                c.Make = factory;
                return;
            }
        Registry.Add(new ClassEntry { Name = name, Make = factory });
    }

    /// <summary>Creates an object by class name, matching ASCII case-insensitively (CL2).</summary>
    /// <param name="className">The class name.</param>
    /// <param name="ctx">The creation context.</param>
    /// <returns>The object, or null when the class is unknown.</returns>
    public static SimObject? CreateObject(string className, ClassContext ctx)
    {
        foreach (var c in Registry)
            if (EqualsNoCase(c.Name, className))
                return c.Make(ctx);
        return null;
    }
}
