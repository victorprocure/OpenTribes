using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Server;
using OpenTribes.Sim;

namespace OpenTribes.ClientNet;

/// <summary>The prediction world of the headless client (docs/client-net.md 4.4): the mission's terrain, interiors and shapes that the client copy of the control object collides with.</summary>
/// <remarks>tribes-netclient builds it the way the server does: the stock start-up (<c>console.cs</c> with <c>-dedicated MISSION</c>) runs in-process with the network off, on a virtual clock, until the mission is loaded. The world is never advanced after that, and only the copy of the control object is stepped. The datablock indices match the server's when both run the same scripts (stock base, our server or the reference).</remarks>
public sealed class PredictionWorld
{
    /// <summary>The script interpreter of the world.</summary>
    public readonly Interpreter Console = new();
    /// <summary>The sim console of the world.</summary>
    public readonly SimConsole Sim;
    /// <summary>The game state of the world.</summary>
    public readonly GameState Game = new();
    /// <summary>The console output, at most 2000 lines.</summary>
    public readonly List<string> Output = new();

    /// <summary>Builds the world and runs the stock start-up until the mission is loaded. The mission load runs from scheduled script, so frames run on the virtual clock until the terrain exists, then two more seconds for objects the mission scripts add after the load.</summary>
    /// <param name="root">The game directory.</param>
    /// <param name="args">The command line arguments for <c>$cargv</c>.</param>
    /// <param name="log">Receives a summary line, or null.</param>
    public PredictionWorld(string root, IReadOnlyList<string> args, Action<string>? log = null)
    {
        var resources = new ResourceManager(root);
        var ctx = new ClassContext { Resources = resources, StartNetwork = _ => true };
        Console.SetOutput(line =>
        {
            if (Output.Count < 2000)
                Output.Add(line);
        });
        Console.SetVariable("$Console::Prompt", "% ");
        Console.SetResources(resources);
        Builtins.RegisterBuiltins(Console, resources, root);
        Sim = new SimConsole(Console, ctx);
        Sim.RegisterCommands();
        Console.SetObjects(Sim);
        Game.Sim = Sim;
        Game.Console = Console;
        ctx.Game = Game;
        ctx.ServerCreated = m => Game.Players.StartPingTimer(m);
        ServerClasses.EnsureRegistered();
        Game.Resources = resources;
        GameCommands.Register(Console, Game);
        var net = new NetServer(new ServerConfig { Port = 0 });
        SimConsole simOps = Sim;
        ServerCommands.RegisterServerCommands(Console, new ServerCommandContext
        {
            Net = net,
            RootDir = root,
            IsChatMenu = name => simOps.Server()?.Find(name)?.ClassName() == "ChatMenu",
            Quit = () => { },
        });
        new MasterHeartbeat(Console, net, false).RegisterCommands();
        Console.BindVariable("$ConsoleWorld::DefaultSearchPath", path => resources.SetSearchPath(path));
        Console.SetVariable("$CPU::estimatedSpeed", "0");
        Console.SetVariable("$cargv0", "Tribes.exe");
        for (int i = 0; i < args.Count; ++i)
            Console.SetVariable("$cargv" + (i + 1).ToString(CultureInfo.InvariantCulture), ByteString.FromHost(args[i]));
        Console.Evaluate(ByteString.FromBytes(File.ReadAllBytes(Path.Combine(root, "console.cs"))), "console.cs");
        var gameServer = new GameServer(Game, Console, net, claimGlobals: false);
        gameServer.RegisterCommands();
        // The mission load runs from scheduled script: frames on the virtual clock until the terrain exists.
        int frames = 0;
        for (int ms = 0; ms < 20000 && !Loaded(); ms += 32, ++frames)
            Sim.AdvanceRealTime(32);
        for (int k = 0; k < 64; ++k)   // two more seconds: objects the mission scripts add after the load
            Sim.AdvanceRealTime(32);
        log?.Invoke("prediction world: " + (Loaded() ? "mission " + Console.PeekVariable("$missionName") + " loaded after " + frames + " frames" : "no mission") + ", " +
                    Output.Count + " console lines");
    }

    /// <summary>Tells whether the mission group and the terrain exist.</summary>
    /// <returns>True when the mission is loaded.</returns>
    public bool Loaded() => Sim.Server()?.Find("MissionGroup") != null && Game.GetTerrain() != null;

    /// <summary>Creates the copy of a Player (PlayerData group 3) or Flier (FlierData group 10) control ghost, made by the same commands the server's scripts use (<c>ClientPrediction.CreateObject</c>).</summary>
    /// <param name="g">The control ghost.</param>
    /// <param name="psc">The player state the position comes from.</param>
    /// <returns>The object, or null when the datablock or the creation fails.</returns>
    public GameBase? Create(ClientGhost g, ClientPsc psc)
    {
        if (!g.State.TryGetValue("datablock", out object? dbv) || dbv is not long db)
            return null;
        int group = g.Class == "Player" ? 3 : 10;
        DataBlockManager.Block? b = Sim.DataBlocks().At(group, (int)db);
        if (b == null)
            return null;
        string pos = "0 0 0";
        if (psc.ControlState.TryGetValue("pos", out object? pv) && pv is double[] p)
            pos = string.Join(" ", p.Select(v => Numbers.FormatFloat((float)v)));
        string id = g.Class == "Player"
            ? Console.CallFunction("spawnPlayer", new[] { b.Name, pos, "0 0 0" })
            : Console.CallFunction("newObject", new[] { "", "Flier", b.Name, "true" });
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return null;
        GameBase? o = Sim.Server()?.FindById(n) as GameBase;
        if (o is Flier f)
        {
            float[] q = pos.Split(' ').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            f.SetPosition(q);
        }
        return o;
    }

    /// <summary>Creates the copy of a remote Player, Flier or Vehicle ghost (<c>RemoteGhosts.CreateObject</c>).</summary>
    /// <param name="g">The ghost.</param>
    /// <param name="position">The world position.</param>
    /// <returns>The object, or null when the datablock or the creation fails.</returns>
    public GameBase? CreateRemote(ClientGhost g, float[] position)
    {
        if (!g.State.TryGetValue("datablock", out object? dbv) || dbv is not long db)
            return null;
        int group = g.Class switch { "Player" => 3, "Flier" => 10, "Vehicle" => 9, _ => -1 };
        DataBlockManager.Block? b = group < 0 ? null : Sim.DataBlocks().At(group, (int)db);
        if (b == null)
            return null;
        string pos = string.Join(" ", position.Select(v => Numbers.FormatFloat(v)));
        string id = g.Class switch
        {
            "Player" => Console.CallFunction("spawnPlayer", new[] { b.Name, pos, "0 0 0" }),
            "Flier" => Console.CallFunction("newObject", new[] { "", "Flier", b.Name, "true" }),
            _ => Console.CallFunction("newObject", new[] { "", "Vehicle", b.Name, "true" }),
        };
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return null;
        GameBase? o = Sim.Server()?.FindById(n) as GameBase;
        if (o is Vehicle v)
            v.SetPosition(position);
        return o;
    }

    /// <summary>Deletes an object of the world.</summary>
    /// <param name="o">The object.</param>
    public void Delete(GameBase o) => Sim.Server()?.DeleteObject(o);
}
