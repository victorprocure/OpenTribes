namespace OpenTribes.Server;

using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;

/// <summary>Registers the script classes a server world needs.</summary>
public static class ServerClasses
{
    private static bool sRegistered;
    private static readonly Lock sLock = new();

    /// <summary>Registers the mission and ghost classes once.</summary>
    public static void EnsureRegistered()
    {
        lock (sLock)
        {
            if (sRegistered)
                return;
            MissionClasses.Register();
            GhostClasses.Register();
            sRegistered = true;
        }
    }
}

/// <summary>A server world inside a client process (client-app.md 1.7).</summary>
/// <remarks>The stock exe keeps a client world and a server world in one process, with one console, one resource manager and one scaled clock (FearWorld_create 004eb470, FearGame_consoleCallback cases 0x79-0x7e, FearApp_onIdle 004ebc34). Here the console variables, the script functions and the resources are shared, and the console commands of each world answer while that world has the focus (<c>Interpreter.World</c>). The stock loopback transport (LOOPTransport 0058d744, 0058d7d4) is an in-process memory copy of the datagrams. A game with only LOOPBACK uses the same here (<see cref="Net.UdpSocket.OpenMemory"/>), with no socket.</remarks>
public sealed class HostedServer : SimConsole.IWorldHost, IDisposable
{
    private readonly Interpreter mIn;
    private readonly ResourceManager mRes;
    private readonly string mRoot;
    private readonly SimConsole mClientSim;
    private readonly bool mAllowMasters;
    private bool mServing;

    /// <summary>Creates the host. No world exists until <see cref="NewServer"/>.</summary>
    /// <param name="interp">The shared interpreter.</param>
    /// <param name="resources">The shared resource manager.</param>
    /// <param name="root">The game directory.</param>
    /// <param name="clientSim">The console of the client world, whose clock the server world takes.</param>
    /// <param name="allowMasters">True when the operator allows masters outside loopback (R6).</param>
    public HostedServer(Interpreter interp, ResourceManager resources, string root, SimConsole clientSim, bool allowMasters = false)
    {
        mAllowMasters = allowMasters;
        mIn = interp;
        mRes = resources;
        mRoot = root;
        mClientSim = clientSim;
    }

    /// <summary>Tells whether a server world exists.</summary>
    public bool Exists => Sim != null;
    /// <summary>The console of the server world, or null.</summary>
    public SimConsole? Sim { get; private set; }
    /// <summary>The game state of the server world, or null.</summary>
    public GameState? Game { get; private set; }
    /// <summary>The network of the server world, or null.</summary>
    public NetServer? Net { get; private set; }
    /// <summary>The interpreter world of the server world, or null.</summary>
    public Interpreter.World? World { get; private set; }

    /// <summary>Creates the server world, replacing any existing one.</summary>
    /// <returns>Always true.</returns>
    /// <remarks>Follows case 0x7c of FearGame_consoleCallback. The server slot is refilled, which destroys the world in it (MG1), and the new world starts its tick clock at the scaled clock of this frame.</remarks>
    public bool NewServer()
    {
        DeleteServer();
        ServerClasses.EnsureRegistered();
        Interpreter.World world = mIn.NewWorld();
        var net = new NetServer(new ServerConfig { Port = 0 });
        var game = new GameState();
        var ctx = new ClassContext { Resources = mRes };
        var sim = new SimConsole(mIn, ctx, null, mClientSim);
        world.Objects = sim;
        game.Sim = sim;
        game.Console = mIn;
        game.Resources = mRes;
        ctx.Game = game;
        World = world;
        Sim = sim;
        Game = game;
        Net = net;
        net.SetLeveledLog(mIn.Print);
        net.SetGameInfo(GameInfoProvider.Create(mIn, game));
        game.AllocateRecordId = () => (int)net.AllocatePlayerId();
        game.ReleaseRecordId = id => net.ReleasePlayerId((uint)id);
        // serverDelegate (FearCSDelegate, isServer): the IP transport opens the socket; a game with only LOOPBACK (the
        // $SinglePlayer form of createServer) is reachable from this process only.
        var master = new MasterHeartbeat(mIn, net, mAllowMasters);
        ctx.StartNetwork = args => ActivateTransports(args, net, sim, master);
        ctx.ServerCreated = m =>
        {
            game.Players.StartPingTimer(m);
            var gs = new GameServer(game, mIn, net);
            mIn.Register(world, gs.RegisterCommands);
        };
        mIn.Register(world, () =>
        {
            sim.RegisterCommands();
            GameCommands.Register(mIn, game);
            ServerCommands.RegisterServerCommands(mIn, new ServerCommandContext
            {
                Net = net,
                RootDir = mRoot,
                IsChatMenu = name => sim.Server()?.Find(name) is { } o && o.ClassName() == "ChatMenu",
                Quit = () => { },
            });
            master.RegisterCommands();
        });
        // The world commands are the process's, not a world's.
        mIn.Unregister(world, "newServer", "focusServer", "deleteServer", "newClient", "focusClient", "deleteClient");
        Interpreter.World? prev = mIn.FocusedWorld();
        mIn.Focus(world);
        try
        {
            sim.UpdateClock();
            sim.CreateWorld();
        }
        finally
        {
            mIn.Focus(prev);
        }
        return true;
    }

    /// <summary>Activates every (transport, port) pair in order, as the server delegate does, and logs one line per pair at print level 2: <c>SimNET: Session (server|client): transport NAME on port N: Ok|ERROR</c>.</summary>
    /// <returns>False when no transport started. Nothing falls back to another port.</returns>
    /// <remarks>Follows SimNet_activate (0054e86c). The word is Ok once any transport has activated, so an IP bind that fails while nothing else has started prints ERROR (format 0063ead8, words 0063eb18 and 0063eb1b). The IP transport is UDPTransport_activate (0058bfac): bind of the exact port, and a failure closes the socket. The LOOPBACK transport always activates (0058d7c4). Transport names match exactly (DNetSession_activateTransport 0058ba60). A hosted game with an IP transport that is not <c>$SinglePlayer</c> announces itself like a dedicated server to the masters (R6: loopback ones unless remote masters are allowed).</remarks>
    private bool ActivateTransports(IReadOnlyList<string> args, NetServer net, SimConsole sim, MasterHeartbeat master)
    {
        var isServer = args.Count > 0 && args[0].Length > 0 && args[0][0] is 't' or 'T' or '1';
        var anyOk = false;
        var ipActivated = false;
        var loopback = false;
        ushort loopbackPort = 0;
        for (var i = 1; i + 1 < args.Count; i += 2)
        {
            var name = args[i];
            var port = NetServer.CAtoi(args[i + 1]);
            var ok = false;
            if (name == "IP")
            {
                ok = net.IsOpen() && !net.LoopbackOnly || net.Open(unchecked((ushort)port), false);
                ipActivated |= ok;
            }
            else if (name == "LOOPBACK")
            {
                ok = true;
                loopback = true;
                loopbackPort = unchecked((ushort)port);
            }
            anyOk |= ok;
            mIn.Print(2, "SimNET: Session (" + (isServer ? "server" : "client") + "): transport " + name + " on port "
                + Numbers.FormatInt(port) + ": " + (anyOk ? "Ok" : "ERROR"));
        }
        if (!anyOk)
            return false;
        // The LOOPBACK transport is the in-process datagram queue, opened when no IP transport is.
        if (!ipActivated && !net.IsOpen() && !(loopback && net.Open(loopbackPort, true)))
            return false;
        mServing = net.IsOpen();
        // SimNet_activate tail: a hosted game with an IP transport that is not $SinglePlayer announces itself like a
        // dedicated server, to the masters $inet::Master1.. (R6: loopback ones unless --allow-remote).
        if (mServing && ipActivated && sim.Server() is { } m)
            master.OnNetworkStart(args, true, (delay, action) => m.PostEvent(m, Fpu.AddF(m.Time(), delay), action));
        return mServing;
    }

    /// <summary>Focuses the server world.</summary>
    /// <returns>False when the slot is empty (case 0x7a).</returns>
    public bool FocusServer()
    {
        if (World == null || Sim?.Server() == null)
            return false;
        mIn.Focus(World);
        return true;
    }

    /// <summary>Focuses the client world (case 0x79).</summary>
    public void FocusClient() => mIn.Focus(null);

    /// <summary>Ends the world and its connections (cases 0x7d and 0x7e).</summary>
    public void DeleteServer()
    {
        if (World == null)
            return;
        mIn.DropWorld(World);
        Net?.Close();
        Sim?.DestroyWorld();
        World = null;
        Sim = null;
        Game = null;
        Net = null;
        mServing = false;
    }

    /// <summary>Runs the server half of a frame: the transports, the 32 ms tick loop, then one packet write per due connection (docs/sim.md 8.3 TK1 to TK5). It runs with the server focus and the client focus is back afterwards.</summary>
    /// <param name="nowMs">The unscaled clock in milliseconds.</param>
    /// <remarks>Follows FearWorld_advance (004eba40), client-app.md 2.2 F3. FearWorld_clientAdvance (004eb7ac) sets the client focus again at its start.</remarks>
    public void Frame(uint nowMs)
    {
        if (World == null || Sim == null || Net == null || Sim.Server() == null)
            return;
        mIn.Focus(World);
        try
        {
            Sim.UpdateClock();
            if (mServing)
                Net.ReceivePackets(nowMs);
            Sim.AdvanceServer();
            if (mServing)
                Net.SendPackets(nowMs);
        }
        finally
        {
            mIn.Focus(null);
        }
    }

    /// <summary>Deletes the server world.</summary>
    public void Dispose() => DeleteServer();
}
