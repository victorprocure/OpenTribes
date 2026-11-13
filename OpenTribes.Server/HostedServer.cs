// A server world inside a client process (client-app.md 1.7): the stock exe keeps a client world and a server world in
// one process, with one console, one resource manager and one scaled clock [code FearWorld_create 004eb470,
// FearGame_consoleCallback cases 0x79-0x7e, FearApp_onIdle 004ebc34]. Here the console variables, the script functions and
// the resources are shared and the console commands of each world answer while that world has the focus
// (Interpreter.World). The loopback transport of the stock exe (LOOPTransport 0058d744, 0058d7d4) is an in-process
// memory copy of the datagrams; a game with only LOOPBACK uses the same here (UdpSocket.OpenMemory), no socket.
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Server;

public static class ServerClasses
{
    private static bool sRegistered;
    private static readonly object sLock = new();

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

public sealed class HostedServer : SimConsole.IWorldHost, IDisposable
{
    private readonly Interpreter mIn;
    private readonly ResourceManager mRes;
    private readonly string mRoot;
    private readonly SimConsole mClientSim;
    private readonly bool mAllowMasters;
    private Interpreter.World? mWorld;
    private SimConsole? mSim;
    private GameState? mGame;
    private NetServer? mNet;
    private bool mServing;

    public HostedServer(Interpreter interp, ResourceManager resources, string root, SimConsole clientSim, bool allowMasters = false)
    {
        mAllowMasters = allowMasters;
        mIn = interp;
        mRes = resources;
        mRoot = root;
        mClientSim = clientSim;
    }

    public bool Exists => mSim != null;
    public SimConsole? Sim => mSim;
    public GameState? Game => mGame;
    public NetServer? Net => mNet;
    public Interpreter.World? World => mWorld;

    // Case 0x7c of FearGame_consoleCallback: the server slot is refilled, which destroys the world in it (MG1), and the new
    // world starts its tick clock at the scaled clock of this frame.
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
        mWorld = world;
        mSim = sim;
        mGame = game;
        mNet = net;
        net.SetLeveledLog((level, line) => mIn.Print(level, line));
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

    // SimNet_activate 0x0054e86c: every (transport, port) pair is activated in order and logs one line at print level 2,
    // "SimNET: Session (server|client): transport NAME on port N: Ok|ERROR". The word is Ok once any transport has
    // activated, so an IP bind that fails while nothing else has started prints ERROR [format 0x0063ead8, words 0x0063eb18
    // and 0x0063eb1b]. The delegate fails when no transport started, and nothing falls back to another port. The IP
    // transport is UDPTransport_activate 0058bfac (bind of the exact port, a failure closes the socket); the LOOPBACK
    // transport always activates (0058d7c4). Transport names match exactly (DNetSession_activateTransport 0058ba60).
    private bool ActivateTransports(IReadOnlyList<string> args, NetServer net, SimConsole sim, MasterHeartbeat master)
    {
        bool isServer = args.Count > 0 && args[0].Length > 0 && args[0][0] is 't' or 'T' or '1';
        bool anyOk = false;
        bool ipActivated = false;
        bool loopback = false;
        ushort loopbackPort = 0;
        for (int i = 1; i + 1 < args.Count; i += 2)
        {
            string name = args[i];
            int port = NetServer.CAtoi(args[i + 1]);
            bool ok = false;
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

    // Case 0x7a: focus the server world; False when the slot is empty.
    public bool FocusServer()
    {
        if (mWorld == null || mSim?.Server() == null)
            return false;
        mIn.Focus(mWorld);
        return true;
    }

    // Case 0x79.
    public void FocusClient() => mIn.Focus(null);

    // Cases 0x7d, 0x7e: the world and its connections end.
    public void DeleteServer()
    {
        if (mWorld == null)
            return;
        mIn.DropWorld(mWorld);
        mNet?.Close();
        mSim?.DestroyWorld();
        mWorld = null;
        mSim = null;
        mGame = null;
        mNet = null;
        mServing = false;
    }

    // The server half of a frame (FearWorld_advance 004eba40, client-app.md 2.2 F3): the transports, the 32 ms tick loop,
    // then one packet write per due connection (sim.md 8.3 TK1 to TK5). Runs with the server focus; the client focus is
    // back afterwards (FearWorld_clientAdvance 004eb7ac sets it again at its start).
    public void Frame(uint nowMs)
    {
        if (mWorld == null || mSim == null || mNet == null || mSim.Server() == null)
            return;
        mIn.Focus(mWorld);
        try
        {
            mSim.UpdateClock();
            if (mServing)
                mNet.ReceivePackets(nowMs);
            mSim.AdvanceServer();
            if (mServing)
                mNet.SendPackets(nowMs);
        }
        finally
        {
            mIn.Focus(null);
        }
    }

    public void Dispose() => DeleteServer();
}
