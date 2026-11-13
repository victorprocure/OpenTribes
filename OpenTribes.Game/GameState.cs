// Port of the GameState part of src/game/game_commands.hpp and src/game/game_commands.cpp: the game plugin state
// (FearPlugin, registered at 0x00423040): teams (PlayerManager 713) and the GameBase services for mission objects. The
// stepped-move candidates (src/game/move_candidates.cpp), the displacement (src/game/displace.cpp) and the collision
// event (GameState::postCollisionEvent and dispatchCollisionEvent of src/game/player.cpp) are Displace.cs; the AI
// manager is AIManager.cs.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The record of one team of the PlayerManager.</summary>
/// <remarks>Port of struct Team in src/game/game_commands.hpp.</remarks>
public sealed class TeamRecord
{
    /// <summary>The team name.</summary>
    public string Name { get; set; }
    /// <summary>The team skin.</summary>
    public string Skin { get; set; }
    /// <summary>The score text.</summary>
    public string Score { get; set; } = "";
    /// <summary>The sort value of the team.</summary>
    public int SortValue { get; set; }
    /// <summary>The stamp of the last score change.</summary>
    public ref uint ScoreStamp => ref _scoreStamp;
    private uint _scoreStamp;
    /// <summary>The stamp of each objective. Stamps start at 1 (docs/player-manager.md 2.2).</summary>
    public uint[] ObjectiveStamp { get; } = new uint[32];
    /// <summary>The text of each objective.</summary>
    public string[] Objectives { get; } = new string[32];

    /// <summary>Creates a team record with empty objectives.</summary>
    /// <param name="n">The team name.</param>
    /// <param name="s">The team skin.</param>
    public TeamRecord(string n, string s)
    {
        Name = n;
        Skin = s;
        for (var i = 0; i < 32; ++i)
        {
            ObjectiveStamp[i] = 1;
            Objectives[i] = "";
        }
    }
}

/// <summary>The game plugin state (FearPlugin, registered at 0x00423040): teams (PlayerManager 713) and the GameBase services for mission objects.</summary>
/// <remarks>Port of class GameState in src/game/game_commands.hpp. The stepped-move candidates and the displacement are in Displace.cs, the AI manager in AIManager.cs.</remarks>
public sealed partial class GameState : GameServices
{
    /// <summary>The script interpreter.</summary>
    public Interpreter? Console { get; set; }
    /// <summary>The simulation console, or null.</summary>
    public SimConsole? Sim { get; set; }
    /// <summary>The resource manager, or null.</summary>
    public ResourceManager? Resources { get; set; }
    /// <summary>The player manager.</summary>
    public PlayerManager Players { get; }
    /// <summary>Allocates a record id for an AI player from the connection layer's pool. Set by the server.</summary>
    public Func<int>? AllocateRecordId { get; set; }
    /// <summary>Releases a record id of an AI player.</summary>
    public Action<int>? ReleaseRecordId { get; set; }
    /// <summary>The AI manager (object 726) while the mission has one (docs/ai-sim.md 1.2).</summary>
    public AIManager? Ai { get; set; }

    /// <summary>The team count, or -1 until the first resetPlayerManager (docs/mission.md 2.2).</summary>
    public int TeamCount { get; set; } = -1;
    /// <summary>The team records. The record of team t is at index t + 1.</summary>
    public List<TeamRecord> Teams { get; } = [];
    /// <summary>The team score heading.</summary>
    public string TeamScoreHeading { get; set; } = "";
    /// <summary>The client score heading.</summary>
    public string ClientScoreHeading { get; set; } = "";
    /// <summary>The sensor target names in registration order (GB4).</summary>
    public List<string> TargetNames { get; } = [];
    /// <summary>The target objects, at the same index as the names. An entry is null once removed.</summary>
    public List<GameBase?> Targets { get; } = [];
    /// <summary>The material table (setMaterialProperty, 0x006c1ff0).</summary>
    public MaterialTable Materials { get; } = new();
    /// <summary>ORs bits into the object's entry of every ghost manager. Set by the game server.</summary>
    public Action<SimObject, uint>? MaskBits { get; set; }
    /// <summary>Container 7, which holds the objects of the world.</summary>
    public Container World { get; } = new();
    /// <summary>The movers of the running displaces (object type 0).</summary>
    public List<GameBase> DisplaceHidden { get; } = [];

    /// <summary>Creates the game state and its player manager.</summary>
    public GameState() => Players = new PlayerManager(this);

    /// <summary>Adds a team (TM1).</summary>
    /// <param name="id">The team id.</param>
    public void AddTeam(int id)
    {
        if (TeamCount == 8)
            return;
        // $Server::teamName<c> with c = '0' + count ('/' for the observer team).
        var c = ((char)('0' + TeamCount)).ToString();
        var name = Console != null ? Console.GetVariable("$Server::teamName" + c, out _) : "";
        var skin = Console != null ? Console.GetVariable("$Server::teamSkin" + c, out _) : "";
        Console?.Print(1, "MSG: \"" + Builtins.ExportEscape("Server: Team \"" + name + "\" added - id = " + Numbers.FormatInt(id)) + "\"");
        ++TeamCount;
        Teams.Add(new TeamRecord(name.Length == 0 ? "unnamed" : name, skin.Length == 0 ? "base" : skin));
        Players.TeamAdded(id);
    }

    /// <summary>Removes every team (TM5).</summary>
    public void ResetTeams()
    {
        Players.Reset();
        TeamCount = -1;
        Teams.Clear();
        AddTeam(-1);
    }

    /// <inheritdoc/>
    public override int AddTeamFromGroup()
    {
        var id = TeamCount;
        AddTeam(id);
        return id;
    }

    /// <inheritdoc/>
    public override bool ResolveDataBlock(SimObject obj)
    {
        if (obj is not GameBase gameObj || Sim == null || gameObj.DataBlockGroup() < 0)
            return false;
        var db = Sim.DataBlocks();
        var b = db.Find(gameObj.DataBlockName);
        if (b == null || b.Group != gameObj.DataBlockGroup())
            return false;
        // GB4: a block visible to sensors expands the map name (GB7).
        var hasVisible = db.GetField(b, "visibleToSensor", "", out var visible);
        if (hasVisible && visible == "True")
        {
            var raw = gameObj.MapName ?? "";
            gameObj.SetMapName(gameObj.MapName != null ? raw : null);
        }
        if (visible == "True" && gameObj.TargetIndex < 0 && gameObj.DataBlockGroup() != 3)
        {
            // 0x00447e6c: the lowest free slot 0..127; a full table gives -1 with no message. SensorManager_addTarget
            // refuses datablock group 3: a Player has no target slot.
            var slot = 0;
            while (slot < Targets.Count && Targets[slot] != null)
                ++slot;
            if (slot < 128)
            {
                if (slot == Targets.Count)
                {
                    Targets.Add(null);
                    TargetNames.Add("");
                }
                gameObj.TargetIndex = slot;
                Targets[slot] = gameObj;
                SetTargetName(slot, gameObj.MapName ?? "");
            }
        }
        gameObj.DataBlockIndex = b.Index;
        if (gameObj.IsShapeBase())
        {
            // ShapeBase onNewDataBlock (0x0044b274): maxDamage (+0x5c), the damageLevel ratios (+0xec, +0xf0) and the
            // shape instance the threads run on.
            float Value(string field, int comp, float def)
            {
                var f = b.Find(StringTable.Global().Intern(field));
                if (f == null || f.Elems.Count == 0)
                    return def;
                if (f.Elems[0].Set)
                    return f.Elems[0].F[comp];
                // Unset: the constructor constant (0x00449ffc stores 0x3dcccccd for repairRate).
                float[] v = [def, def];
                var p = f.Def.DefaultText;
                var at = 0;
                for (var k = 0; k < 2 && at < p.Length; ++k)
                {
                    var x = DataBlockManager.CtorFloat(p, at, out var e);
                    if (e == at)
                        break;
                    v[k] = x;
                    at = e;
                }
                return v[comp];
            }
            bool Boolean(string field, bool def)
            {
                var f = b.Find(StringTable.Global().Intern(field));
                if (f == null || f.Elems.Count == 0)
                    return def;
                if (f.Elems[0].Set)
                    return f.Elems[0].I != 0;
                return def;
            }
            int Integer(string field, int def)
            {
                var f = b.Find(StringTable.Global().Intern(field));
                if (f == null || f.Elems.Count == 0)
                    return def;
                if (f.Elems[0].Set)
                    return f.Elems[0].I;
                return Strtol(f.Def.DefaultText);
            }
            if (gameObj.ShapeClass() == GameBase.ShapeKind.Sensor || gameObj.ShapeClass() == GameBase.ShapeKind.Turret)
            {
                gameObj.SensorData.CastLOS = Boolean("castLOS", false);
                gameObj.SensorData.Range = Integer("range", 100);
                gameObj.SensorData.Doppler = Integer("dopplerVelocity", 0);
                gameObj.SensorData.Supression = Boolean("supression", false);
                gameObj.SensorData.Supressable = Boolean("supressable", true);
                gameObj.SensorData.Pinger = Boolean("pinger", true);
            }
            if (gameObj.ShapeClass() == GameBase.ShapeKind.Turret)
            {
                var t = gameObj.TurretData;
                t.GunRange = Value("gunRange", 0, -1.0f);
                t.Speed = Value("speed", 0, 1.0f);
                t.SpeedModifier = Value("speedModifier", 0, 0.0f);
                t.ReloadDelay = Value("reloadDelay", 0, 0.0f);
                t.MinGunEnergy = Value("minGunEnergy", 0, 0.0f);
                t.MaxGunEnergy = Value("maxGunEnergy", 0, 0.0f);
                t.Fov = Value("FOV", 0, -1.0f);
                t.Deflection = Value("deflection", 0, 0.0f);
                t.TargetableFovRatio = Value("targetableFovRatio", 0, 0.5f);
                t.EnergyRate = Value("energyRate", 0, 0.0f);
                t.Sustained = Boolean("isSustained", false);
                t.FiringTimeMS = Integer("firingTimeMS", 0);
                t.HasProjectile = db.GetField(b, "projectileType", "", out var proj) && proj.Length != 0;
            }
            gameObj.MaxDamage = Value("maxDamage", 0, 1.0f);
            gameObj.DisabledLevel = Value("damageLevel", 0, 0.5f);
            gameObj.DestroyedLevel = Value("damageLevel", 1, 0.75f);
            gameObj.MaxEnergy = Value("maxEnergy", 0, 0.0f);
            gameObj.RepairRate = Value("repairRate", 0, 0.1f);
            gameObj.Energy = gameObj.MaxEnergy; // 0x0044b274
            if (gameObj.MaxDamage <= gameObj.Damage)
                gameObj.Damage = Store(Mul(gameObj.MaxDamage, 0.9f));
            if (gameObj.ShapeClass() == GameBase.ShapeKind.Item || gameObj.ShapeClass() == GameBase.ShapeKind.Mine)
            {
                gameObj.ItemData.Mass = Value("mass", 0, 1.0f);
                gameObj.ItemData.Drag = Value("drag", 0, 1.0f);
                gameObj.ItemData.Density = Value("density", 0, 1.0f);
                gameObj.ItemData.Friction = Value("friction", 0, 1.0f);
                gameObj.ItemData.Elasticity = Value("elasticity", 0, 0.2f);
            }
            if (gameObj.ShapeClass() == GameBase.ShapeKind.Mine)
            {
                gameObj.MineData.DamageType = Integer("damageType", 0);
                gameObj.MineData.DamageValue = Value("damageValue", 0, 1.0f);
                gameObj.MineData.ExplosionRadius = Value("explosionRadius", 0, 1.0f);
                gameObj.MineData.Kick = Value("kickBackStrength", 0, 100.0f);
            }
            if (gameObj.ShapeClass() == GameBase.ShapeKind.Moveable)
            {
                gameObj.MoveData.Speed = Value("speed", 0, 1.0f);
                gameObj.MoveData.Displace = Boolean("displace", true);
                gameObj.MoveData.BlockForward = Boolean("blockForward", true);
                gameObj.MoveData.BlockBackward = Boolean("blockBackward", true);
            }
            var shape = "";
            if (Resources != null && db.GetField(b, "shapeFile", "", out shape) && shape.Length != 0)
                gameObj.Anim = Shape.GetShapeAnim(Resources, shape);
            if (shape.Length != 0 && Resources != null)
            {
                float radius = 0;
                Span<float> center = stackalloc float[3];
                if (Shape.ShapeSphere(Resources, shape, ref radius, center))
                {
                    gameObj.ShapeRadius = radius;
                    for (var i = 0; i < 3; ++i)
                        gameObj.ShapeCenter[i] = center[i];
                    gameObj.ItemSubStep = Store(Mul(radius, 0.45)); // 0x0044b4c5
                }
                Span<float> sb = stackalloc float[6];
                if (Shape.ShapeBox(Resources, shape, sb))
                {
                    for (var i = 0; i < 6; ++i)
                        gameObj.ShapeBox[i] = gameObj.ShapeQueryBox[i] = sb[i];
                    // StaticBase only: other classes have no such field and the lookup would print an error.
                    var r = gameObj.IsStaticBase() && db.GetField(b, "triggerRadius", "", out var tr) ? Store(Numbers.BorlandAtof(tr)) : 0.0f;
                    gameObj.ShapeTriggerRadius = r;
                    if (r != 0.0f)
                        for (var i = 0; i < 3; ++i)
                        {
                            gameObj.ShapeQueryBox[i] = Math.Min(sb[i], -r);
                            gameObj.ShapeQueryBox[i + 3] = Math.Max(sb[i + 3], r);
                        }
                }
            }
            gameObj.VisibilityThread = gameObj.Anim != null && gameObj.Anim.FindSequence("visibility") != -1;
            gameObj.ShapeBound = true;
        }
        PlaceInContainer(gameObj, b);
        return true;
    }

    // std::strtol(text, nullptr, 10).
    private static int Strtol(string s)
    {
        var v = 0;
        CScan.ScanInt(s, ref v);
        return v;
    }

    // src/game/game_commands.cpp collectPathObjects.
    private static void CollectPathObjects(SimObject obj, List<SimObject> output)
    {
        if (GameUtil.EqualsNoCase(obj.ClassName(), "SimPath"))
            output.Add(obj);
        if (obj.AsSet() is SimSet set)
            foreach (var m in set.Members())
                CollectPathObjects(m, output);
    }

    /// <inheritdoc/>
    /// <remarks>Refreshes the container entry of the object whose transform changed.</remarks>
    public override void ObjectMoved(SimObject obj)
    {
        if (obj is not GameBase gameObj)
            return;
        if (Player.From(gameObj) is Player p)
        {
            p.RefileBox(); // setTransform updates the container at once (0x005054a4 -> 0x004f9a38)
            return;
        }
        if (Sim != null && gameObj.DataBlockName != null && Sim.DataBlocks().Find(gameObj.DataBlockName) is DataBlockManager.Block b)
            PlaceInContainer(gameObj, b);
    }

    /// <inheritdoc/>
    public override int DataBlockCount(int group) => Sim != null && group >= 0 && group <= 30 ? Sim.DataBlocks().GroupSize(group) : 0;

    /// <inheritdoc/>
    /// <remarks>SimPathManager ids follow SimPath registration order (docs/game-stream-ghosts.md 6.5).</remarks>
    public override int PathIndex(SimObject path)
    {
        var m = Sim?.Server();
        if (m == null || path.Manager() == null)
            return -1;
        var paths = new List<SimObject>();
        CollectPathObjects(m, paths);
        var index = 0;
        foreach (var p in paths)
            if (p.Sequence() < path.Sequence())
                ++index;
        return index;
    }

    private SensorManager? SensorManagerObject() =>
        Sim?.Server()?.FindById(720) is SensorManager sm && GameUtil.EqualsNoCase(sm.ClassName(), "SensorManager") ? sm : null;

    /// <inheritdoc/>
    public override void SensorInsert(SimObject sensor)
    {
        if (sensor is GameBase gb)
            SensorManagerObject()?.Insert(gb);
    }

    /// <inheritdoc/>
    public override void SensorRemove(SimObject sensor)
    {
        if (sensor is GameBase gb)
            SensorManagerObject()?.Remove(gb);
    }

    /// <summary>Links or unlinks a player in the jamming player list (docs/static-sim.md 3.8).</summary>
    /// <param name="p">The player.</param>
    /// <param name="link">True to link after the list head, false to unlink.</param>
    /// <remarks>Follows 0x0040fc58.</remarks>
    public void SensorJam(GameBase p, bool link)
    {
        var sm = SensorManagerObject();
        if (sm == null)
            return;
        if (link)
            sm.JamLink(p);
        else
            sm.JamUnlink(p);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x00458be4: radius damage (0x0046edd0) with the mine's damage type, radius, value and kick, no velocity, centred on the world box ((max + min) stored as f32, times 0.5). The source is the thrower's client while its record still carries the join counter stored at the throw (0x004524f8), else 0.</remarks>
    public override void MineDestroyed(SimObject mine)
    {
        if (mine is not GameBase gameMine || World.Find(gameMine) is not Container.Entry e)
            return;
        var c = new float[3];
        for (var i = 0; i < 3; ++i)
        {
            var sum = X87.F32(Add(e.Box[i + 3], e.Box[i]));
            c[i] = X87.F32(Mul(0.5, sum));
        }
        var source = 0;
        if (gameMine.ItemThrowerClient > 0 && Players.Find(gameMine.ItemThrowerClient) is ClientRecord r && r.JoinOrder == gameMine.ItemThrowerJoin)
            source = gameMine.ItemThrowerClient;
        float[] zero = [0, 0, 0];
        DamageCommands.ApplyRadiusDamage(this, gameMine.MineData.DamageType, c, zero, gameMine.MineData.ExplosionRadius,
                                         gameMine.MineData.DamageValue, gameMine.MineData.Kick, source);
    }

    /// <inheritdoc/>
    public override void SensorTeam(SimObject sensor)
    {
        if (sensor is GameBase gb)
            SensorManagerObject()?.TeamChanged(gb);
    }

    /// <inheritdoc/>
    public override void ClientTeam(int client, int team) => Players.SetTeam(client, team);

    /// <summary>Sets the name of a sensor target. A changed name goes to every human client.</summary>
    /// <param name="index">The target index.</param>
    /// <param name="name">The new name.</param>
    /// <remarks>Follows SensorManager_setTargetName (0x00439b68).</remarks>
    public void SetTargetName(int index, string name)
    {
        if (index < 0 || index >= TargetNames.Count || TargetNames[index] == name)
            return;
        TargetNames[index] = name;
        // One TargetNameEvent per human, newest first.
        foreach (var h in Players.Humans())
            Players.SendEvent(h, GameEvents.MakeTargetName((uint)index, name));
    }

    /// <inheritdoc/>
    public override void RenameTarget(int index, string name) => SetTargetName(index, name);

    /// <inheritdoc/>
    /// <remarks>Follows 0x00439b54: the slot is freed (count - 1, entry 0, name NULL).</remarks>
    public override void TargetRemoved(SimObject obj)
    {
        if (obj is not GameBase gameObj)
            return;
        if (gameObj.TargetIndex >= 0 && gameObj.TargetIndex < Targets.Count && Targets[gameObj.TargetIndex] == gameObj)
        {
            Targets[gameObj.TargetIndex] = null;
            // The name goes to NULL through the same setter, so a named target tells every human.
            SetTargetName(gameObj.TargetIndex, "");
        }
        gameObj.TargetIndex = -1;
    }

    /// <inheritdoc/>
    public override void PrintLine(string line) => Console?.Print(1, line);

    /// <inheritdoc/>
    public override SimObject? PathById(int id)
    {
        var m = Sim?.Server();
        if (m == null || id < 0)
            return null;
        var paths = new List<SimObject>();
        CollectPathObjects(m, paths);
        paths = [.. paths.OrderBy(p => p.Sequence())]; // stable
        return id < paths.Count ? paths[id] : null;
    }

    // src/game/game_commands.cpp findTerrain.
    private TerrainObject? FindTerrain() =>
        Sim?.Server()?.FindById(8) is TerrainObject t && GameUtil.EqualsNoCase(t.ClassName(), "SimTerrain") ? t : null;

    /// <inheritdoc/>
    public override Terrain? GetTerrain() => FindTerrain()?.GetTerrain();

    /// <inheritdoc/>
    public override Material? TerrainMaterial(int index)
    {
        var terr = GetTerrain();
        if (terr == null || Resources == null)
            return null;
        var list = MaterialList.Get(Resources, terr.MaterialListName());
        return list != null && index >= 0 && index < list.Count ? list[index] : null;
    }

    /// <inheritdoc/>
    /// <remarks>An object whose container chain reaches the SimTerrain is pulled by its contGravity (default 0 0 -9.8) and slowed by contDrag (default 0) (FC2, FC3).</remarks>
    public override bool TerrainForces(SimObject obj, Span<float> gravity, ref float drag)
    {
        var t = FindTerrain();
        if (t == null)
            return false;
        var c = World.ParentOf(obj);
        var inside = false;
        for (var depth = 0; c != null && depth < 8; ++depth)
        {
            if (c == t)
            {
                inside = true;
                break;
            }
            c = World.ParentOf(c);
        }
        if (!inside)
            return false;
        t.Fields().Point("contGravity", gravity, 0.0f, 0.0f, -9.8f);
        drag = t.Fields().Real("contDrag", 0.0f);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows SimTerrain_enterContainer (0x005195b4): contGravity is copied into the force list and contDrag into +0x124 when the object enters a container inside the terrain. A move between nested containers leaves and enters again. Leaving (0x0051963c) removes the force only (FC2, FC3).</remarks>
    public override bool EnteredForces(SimObject obj, Span<float> gravity, ref float drag)
    {
        if (obj is not GameBase gameObj)
            return false;
        var terr = World.TerrainOf(gameObj);
        var parent = World.ParentOf(gameObj);
        var f = gameObj.EntryForces;
        if (terr != f.Terrain || parent != f.Parent)
        {
            if (terr != null)
            {
                for (var i = 0; i < 3; ++i)
                    f.Gravity[i] = terr.Gravity[i];
                f.Drag = terr.Drag;
            }
            f.Terrain = terr;
            f.Parent = parent;
        }
        for (var i = 0; i < 3; ++i)
            gravity[i] = terr != null ? f.Gravity[i] : 0.0f;
        drag = f.Drag;
        return terr != null;
    }

    /// <inheritdoc/>
    public override MaterialTable? GetMaterialTable() => Materials;

    /// <summary>Returns the object type (+0x48) of the GameBase classes that join container 7.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The type bits.</returns>
    public static uint ContainerType(GameBase obj)
    {
        (string, uint)[] kTypes =
        [
            ("StaticShape", ObjectType.StaticType), ("Sensor", ObjectType.StaticType), ("Turret", ObjectType.StaticType),
            ("Moveable", ObjectType.MoveableType), ("Item", ObjectType.ItemType), ("Mine", ObjectType.MineType),
            ("Flier", ObjectType.VehicleType), ("Trigger", ObjectType.TriggerType),
        ];
        foreach ((var cls, var type) in kTypes)
            if (GameUtil.EqualsNoCase(cls, obj.ClassName()))
                return type;
        return 0;
    }

    /// <summary>Binds the object's box and adds it to container 7.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="block">The datablock of the object.</param>
    /// <remarks>Follows ShapeBase::onNewDataBlock (0x0044b274), which binds the shape box, and Trigger::onAdd (0x00487178), which adds its boundingBox. World boxes: ShapeBase rotates the box (0x005edbb4), a Trigger only translates it (0x005069fc).</remarks>
    public void PlaceInContainer(GameBase obj, DataBlockManager.Block block)
    {
        var type = ContainerType(obj);
        if (type == 0 || Resources == null)
            return;
        float[] local = [-1, -1, -1, 1, 1, 1];
        Span<float> box = stackalloc float[6];
        var m = obj.WorldMat();
        if (obj.GetKind() == GameBase.Kind.Trigger)
        {
            if (obj.PersistValue("boundingBox") is IReadOnlyList<string> v && v.Count != 0)
                CScan.Floats(v[0], local);
            for (var i = 0; i < 6; ++i)
                obj.TriggerBox[i] = local[i];
            if (obj.PersistValue("isSphere") is IReadOnlyList<string> s && s.Count != 0)
                obj.TriggerSphere = Numbers.ParseBool(s[0]);
            for (var i = 0; i < 3; ++i)
            {
                box[i] = Store(Add(local[i], m.P[i]));
                box[i + 3] = Store(Add(local[i + 3], m.P[i]));
            }
        }
        else
        {
            if (Sim == null || !Sim.DataBlocks().GetField(block, "shapeFile", "", out var shape) || shape.Length == 0 ||
                !Shape.ShapeBox(Resources, shape, local))
                return;
            // StaticBase_bindShape (0x0044f68c): a non-zero triggerRadius widens the box.
            if (Sim.DataBlocks().GetField(block, "triggerRadius", "", out var radius))
            {
                var r = Store(Numbers.BorlandAtof(radius));
                if (r != 0.0f)
                    for (var i = 0; i < 3; ++i)
                    {
                        local[i] = Math.Min(local[i], -r);
                        local[i + 3] = Math.Max(local[i + 3], r);
                    }
            }
            Shape.RotatedWorldBox(local, m, box);
        }
        Span<float> g = stackalloc float[3];
        float d = 0;
        if (World.Find(obj) is Container.Entry e)
        {
            World.SetBox(obj, box);
            e.Type = type;
            EnteredForces(obj, g, ref d);
            return;
        }
        var ne = new Container.Entry(obj, type, Float6.Of(box), false, null);
        if (obj.GetKind() != GameBase.Kind.Trigger)
        {
            // ShapeCollisionImage against a line (docs/collision.md 6 T7).
            var gb = obj;
            ne.Cast = (s, en, ref hit, ref n) => ShapeState.ShapeObjectLine(this, gb, s, en, ref hit, ref n);
        }
        World.Add(ne);
        EnteredForces(obj, g, ref d);
    }

    /// <summary>Looks up a virtual script function of a GameBase (docs/datablocks.md 8).</summary>
    /// <param name="gb">The object.</param>
    /// <param name="method">The method name.</param>
    /// <returns>The first defined function, or an empty string.</returns>
    public string VirtualFunction(GameBase gb, string method)
    {
        if (Console == null)
            return "";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(gb.MapName))
            candidates.Add(gb.MapName + "::" + method);
        if (gb.DataBlockName != null)
        {
            candidates.Add(gb.DataBlockName + "::" + method);
            if (Sim?.DataBlocks().Find(gb.DataBlockName) is DataBlockManager.Block b &&
                Sim.DataBlocks().GetField(b, "className", "", out var cls) && cls.Length != 0)
                candidates.Add(cls + "::" + method);
        }
        candidates.Add(gb.ScriptClass() + "::" + method);
        candidates.Add(method);
        foreach (var fn in candidates)
            if (Console.HasFunction(fn))
                return fn;
        return "";
    }

    /// <inheritdoc/>
    /// <remarks>Calls the script method through the GameBase virtual lookup, and does nothing when it is undefined (GB5).</remarks>
    public override void ScriptCallback(SimObject obj, string method, IReadOnlyList<string> extra)
    {
        if (obj is not GameBase gameObj)
            return;
        var fn = VirtualFunction(gameObj, method);
        if (fn.Length == 0)
            return;
        // D11 / docs/static-sim.md K1: the original passes argc 4 with three strings to onWaypoint, so a method declared
        // with four parameters reads a stray word and the process dies. Such a method is not called.
        if (method == "onWaypoint" && Console!.FunctionParamCount(fn) >= 4)
        {
            Console.Print(1, "onWaypoint: " + fn + " declares " + Numbers.FormatInt(Console.FunctionParamCount(fn)) +
                             " parameters, the engine passes 2: not called");
            return;
        }
        var args = new List<string> { Numbers.FormatInt(gameObj.Id()) };
        args.AddRange(extra);
        Console!.CallFunction(fn, args);
    }

    /// <inheritdoc/>
    public override void JoinSet(SimObject obj, int setId)
    {
        if (Sim?.Server()?.FindById(setId)?.AsSet() is SimSet set)
            set.AddObject(obj);
    }

    /// <inheritdoc/>
    public override void LeaveSet(SimObject obj, int setId)
    {
        if (Sim?.Server()?.FindById(setId)?.AsSet() is SimSet set)
            set.RemoveObject(obj);
    }

    /// <inheritdoc/>
    /// <remarks>Follows Trigger_onDeleteNotify (0x004872e0): the record goes without a callback, and a Trigger whose list becomes empty leaves SimTimerSet 12. Every Trigger with occupants is in that set.</remarks>
    public override void DropTriggerOccupant(SimObject obj)
    {
        if (Sim?.Server()?.FindById(12)?.AsSet() is not SimSet set)
            return;
        var members = new List<SimObject>(set.Members());
        foreach (var m in members)
        {
            if (m is not GameBase t || t.GetKind() != GameBase.Kind.Trigger || t == obj)
                continue;
            var before = t.Occupants.Count;
            t.Occupants.RemoveAll(o => o.Object == obj.Id());
            if (t.Occupants.Count != before && t.Occupants.Count == 0)
                set.RemoveObject(t);
        }
    }

    /// <inheritdoc/>
    public override bool ResourceExists(string name) => Resources != null && Resources.Find(name) != null;

    /// <inheritdoc/>
    public override ResourceManager? GetResourceManager() => Resources;
    /// <inheritdoc/>
    public override Container? GetContainer() => World;

    /// <inheritdoc/>
    public override string Variable(string name) => Console != null ? Console.GetVariable("$" + name) : "";

    /// <inheritdoc/>
    public override void SetMaskBits(SimObject obj, uint bits) => MaskBits?.Invoke(obj, bits);
}
