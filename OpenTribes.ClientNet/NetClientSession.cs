namespace OpenTribes.ClientNet;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;

/// <summary>One line of a move script: from <see cref="At"/> seconds on, the held move state changes by the given keys.</summary>
public sealed class ScriptStep
{
    /// <summary>The time in seconds after the accept.</summary>
    public double At;
    /// <summary>The key values that change at this step.</summary>
    public Dictionary<string, double> Keys = [];
}

/// <summary>Parser of the move script of the headless client.</summary>
public static class MoveScript
{
    /// <summary>Parses a move script. Lines are <c>&lt;seconds&gt; key=value ...</c>. The keys are fwd, back, left, right (0 to 15), jet, jump, crouch, fire (0 or 1), pitch and turn (radians per move), useItem (item type) and spawn (1 toggles fire until the control object is a Player). A <c>#</c> starts a comment.</summary>
    /// <param name="text">The script text.</param>
    /// <returns>The steps in time order.</returns>
    /// <exception cref="FormatException">A word is not <c>key=value</c>.</exception>
    public static List<ScriptStep> Parse(string text)
    {
        var steps = new List<ScriptStep>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var s = new ScriptStep { At = double.Parse(words[0], CultureInfo.InvariantCulture) };
            foreach (var w in words.Skip(1))
            {
                var kv = w.Split('=');
                if (kv.Length != 2)
                    throw new FormatException("bad script word: " + w);
                s.Keys[kv[0]] = double.Parse(kv[1], CultureInfo.InvariantCulture);
            }
            steps.Add(s);
        }
        return [.. steps.OrderBy(s => s.At)];
    }
}

/// <summary>The outcome of a headless session.</summary>
public sealed class SessionResult
{
    /// <summary>True when the server accepted the connection.</summary>
    public bool Connected;
    /// <summary>True when the server rejected the connection.</summary>
    public bool Rejected;
    /// <summary>True when the connection closed without a reject, for example on a drop or timeout.</summary>
    public bool Dropped;
    /// <summary>True when the session ended with our own disconnect.</summary>
    public bool Disconnected;
    /// <summary>The reject, drop or timeout reason.</summary>
    public string Reason = "";
    /// <summary>The session length in seconds.</summary>
    public double Seconds;
    /// <summary>The packets the world decoded.</summary>
    public long Packets;
    /// <summary>The events received.</summary>
    public long Events;
    /// <summary>The ghost updates received.</summary>
    public long GhostUpdates;
    /// <summary>The ghost deletes received.</summary>
    public long GhostDeletes;
    /// <summary>The bits the decoder could not interpret.</summary>
    public long UnknownBits;
    /// <summary>The decode errors of the world.</summary>
    public long Errors;
    /// <summary>The moves sent.</summary>
    public long MovesSent;
    /// <summary>The ghosts alive at the end.</summary>
    public int Ghosts;
    /// <summary>The class of the control object.</summary>
    public string ControlClass = "";
    /// <summary>True when the datablock phase finished.</summary>
    public bool DataFinished;
    /// <summary>True when the ghost-always phase finished.</summary>
    public bool AlwaysDone;
    /// <summary>The event counts by name.</summary>
    public SortedDictionary<string, long> EventCounts = [];
    /// <summary>The ghost update counts by class.</summary>
    public SortedDictionary<string, long> GhostCounts = [];
    /// <summary>The reasons packets could not be fully decoded.</summary>
    public List<string> UnknownReasons = [];
}

/// <summary>A headless client session (docs/client-net.md 1, 2.5, 2.7, 4.3): connect, challenge and accept as a client, the game stream receive path, the mission-load handshake (SetCLInfo, dataFinished, CGADone), moves at the 32 ms tick from a script, and disconnect. The <c>tribes</c> client reuses this class for its network side.</summary>
public sealed class NetClientSession : ConnectionEvents, IDisposable
{
    private readonly UdpSocket mSock = new();
    private readonly Stopwatch mClock = Stopwatch.StartNew();
    private Connection? mVc;
    private readonly string mName;
    private readonly PacketDump mDump;
    private readonly List<ScriptStep> mScript;
    private int mScriptPos;
    private readonly Dictionary<string, double> mHeld = [];
    private int mCgaCount;
    private bool mDataSent, mCgaSent, mClosed, mRejected, mSpawn;
    private string mReason = "";
    private long mLastTick;
    private long mMovesSent;
    private CorrectionRecord? mPendingCorrection;
    private uint mFrameNow;
    /// <summary>The version string sent in the connect request.</summary>
    public readonly string Version;
    /// <summary>The join password (<c>$Server::JoinPassword</c>, client-net.md 1.2).</summary>
    public string Password = "";
    /// <summary>The gender (<c>$PCFG::Gender</c>).</summary>
    public string Gender = "Male";
    /// <summary>The voice (<c>$PCFG::Voice</c>).</summary>
    public string Voice = "Male1";
    /// <summary>The skin base (<c>$PCFG::SkinBase</c>).</summary>
    public string SkinBase = "base";

    /// <summary>The client world built from the stream.</summary>
    public ClientWorld World { get; private set; } = new();
    /// <summary>Receives log lines.</summary>
    public Action<string>? Log;

    /// <summary>The client prediction of the control object (docs/client-net.md 4.4). It runs when a prediction world is set.</summary>
    public readonly ClientPrediction Prediction = new();
    /// <summary>The smoothing and look-ahead of the remote Player and Vehicle ghosts (client-net.md 4.4.2). It needs the prediction world.</summary>
    public readonly RemoteGhosts Remote = new();
    /// <summary>The prediction world, or null.</summary>
    public PredictionWorld? PredictWorld;
    /// <summary>Builds the prediction world of a mission. The mission comes from the server's <c>remoteEval</c> MInfo (client.cs).</summary>
    public Func<string, PredictionWorld?>? PredictFactory;
    /// <summary>The mission the server reported.</summary>
    public string Mission = "";

    /// <summary>Creates a session.</summary>
    /// <param name="name">The player name.</param>
    /// <param name="dump">Receives each decoded packet.</param>
    /// <param name="script">The move script, or null for none.</param>
    /// <param name="version">The version string to send.</param>
    public NetClientSession(string name, PacketDump dump, List<ScriptStep>? script = null, string version = "1.11")
    {
        mName = name;
        mDump = dump;
        mScript = script ?? [];
        Version = version;
        Game = new ClientGameConnection(World);
    }

    private uint Now => ClockMs != null ? ClockMs() : (uint)mClock.ElapsedMilliseconds;


    /// <summary>True when the client scripts drive the join and the moves, as in the <c>tribes</c> frame loop. <c>onConnection</c> sends SetCLInfo, <c>dataFinished()</c> and <c>onClientGhostAlwaysDone()</c> answer the server, and the moves come from the PSC buffer (client-net.md 4.2). The session does neither itself.</summary>
    public bool External;
    /// <summary>Returns the clock in milliseconds. Defaults to a stopwatch.</summary>
    public Func<uint>? ClockMs;
    /// <summary>Raised like the script <c>onConnection(msg)</c> (client-net.md 1.6) with Accepted, Rejected, TimedOut or Dropped, and the reason text.</summary>
    public Action<string, string>? OnConnection;
    /// <summary>Raised when a member reader failed, with its text. The script gets <c>onConnectionError(...)</c> (TribesNet_onPacketError, 0042b7f8).</summary>
    public Action<string>? OnPacketError;
    /// <summary>Raised when the datablock phase finished.</summary>
    public Action? OnDataFinished;
    /// <summary>Raised when the ghost-always phase finished.</summary>
    public Action? OnAlwaysDone;
    /// <summary>Called with every decoded packet.</summary>
    public Action<DecodedPacket>? PacketHook;
    /// <summary>Reads a console variable. <c>$pref::PacketRate</c> and <c>$pref::PacketSize</c> feed the own rate request. Null means defaults.</summary>
    public Func<string, string>? PrefReader;
    private void ApplyPacketPrefs()
    {
        if (PrefReader != null)
            Game.LoadPacketPrefs(PrefReader("$pref::PacketRate"), PrefReader("$pref::PacketSize"));
    }
    /// <summary>Tells whether the connection is open.</summary>
    public bool IsOpen => mVc != null && !mClosed;
    /// <summary>Tells whether the server accepted the connection.</summary>
    public bool IsAccepted { get; private set; }
    /// <summary>The game stream connection.</summary>
    public ClientGameConnection Game { get; private set; }
    private bool mOpened;

    /// <summary>Opens the socket and sends the connect request (client-net.md 1.2). The replies arrive in <see cref="Frame"/>.</summary>
    /// <param name="hostPort">The server as <c>host:port</c>.</param>
    /// <returns>False when the socket cannot open.</returns>
    public bool Connect(string hostPort)
    {
        ApplyPacketPrefs();
        Address server = Resolve(hostPort);
        mServerAddress = server;
        if (!mSock.OpenToward(server))
            return false;
        var rng = new Random();
        var id = (uint)rng.NextInt64(1, 0xffffffffL);
        mVc = new Connection(server, id, this, (to, data) => mSock.Send(to, data), Now);
        mVc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        mOpened = true;
        mClosed = false;
        return true;
    }

    // A console variable of type int (atoi of its text), the default when the host has no console.
    private int ReadIntPref(string name, int def) => PrefReader == null ? def : unchecked(Numbers.BorlandAtoi(PrefReader(name) ?? ""));

    private Address mServerAddress;
    private readonly byte[] mRx = new byte[0x10000];

    /// <summary>Runs the net steps of one client frame: F10 (receive), F14 (prediction) and F12 (send) of client-app.md 2.2.</summary>
    /// <param name="now">The clock in milliseconds.</param>
    public void Frame(uint now)
    {
        if (!mOpened || mVc == null)
            return;
        mFrameNow = now;
        int n;
        while (!mClosed && (n = mSock.Receive(out Address from, mRx)) > 0)
            if (from == mServerAddress)
                mVc.Receive(mRx.AsSpan(0, n), Now);
        if (!mClosed)
            mVc.Tick(Now);
        if (mClosed)
            return;
        if (IsAccepted)
        {
            Prediction.AdvanceTurret(World.Psc, now);
            if (PredictWorld != null)
            {
                Prediction.BindControlMount(World, Remote);
                Prediction.Advance(World.Psc, now);
                Remote.Advance(now);
                Prediction.PlaceControlRider();
                mPendingCorrection = null;
            }
            if (mVc.State() == ConnectionState.Connected && Game.Due(now))
            {
                Connection vc = mVc;
                Game.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
            }
        }
    }

    /// <summary>Sends a Disconnect packet when connected, then closes the socket (<c>disconnect()</c>).</summary>
    public void Disconnect()
    {
        if (mVc != null && !mClosed && mVc.State() == ConnectionState.Connected)
            mVc.Close(Connection.Disconnect, "", Now);
        mClosed = true;
        mOpened = false;
        mSock.Close();
    }


    /// <summary>Ignores connect requests. A client does not accept them.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The request payload.</param>
    public override void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload) { }

    /// <summary>Handles the accept: reads the player id and the server manager id (net-protocol.md 5.4, client-net.md 1.5 step 2), then answers with <c>SetCLInfo</c> through <c>onConnection("Accepted")</c> (1.6).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The accept payload.</param>
    public override void OnAccepted(Connection c, ReadOnlySpan<byte> payload)
    {
        // net-protocol.md 5.4: the player id and the server manager id (1.5 step 2).
        var b = payload.ToArray();
        if (b.Length >= 8 && External)
        {
            World.PlayerId = BitConverter.ToInt32(b, 0);
            World.ServerId = BitConverter.ToInt32(b, 4);
        }
        else if (b.Length >= 8)
        {
            World = new ClientWorld { PlayerId = BitConverter.ToInt32(b, 0), ServerId = BitConverter.ToInt32(b, 4) };
            Game = new ClientGameConnection(World);
            ApplyPacketPrefs();
        }
        IsAccepted = true;
        Log?.Invoke("accepted: player " + World.PlayerId + ", server " + World.ServerId);
        if (External)
        {
            OnConnection?.Invoke("Accepted", "");
            return;
        }
        // onConnection("Accepted") (1.6): remoteEval(2048, "SetCLInfo", skin, real name, email, tribe, url, info,
        // autoWaypoint, noEnterInvStation, messageMask).
        Game.PostRemoteEval("SetCLInfo", "base", mName, "", "", "", "", "true", "false", "-1");
    }

    /// <summary>Records the reject reason and reports <c>Rejected</c> to the script.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The reason bytes.</param>
    public override void OnRejected(Connection c, ReadOnlySpan<byte> payload)
    {
        mRejected = true;
        mClosed = true;
        mReason = System.Text.Encoding.Latin1.GetString(payload).TrimEnd('\0');
        Log?.Invoke("rejected: " + mReason);
        OnConnection?.Invoke("Rejected", mReason);
    }

    /// <summary>Records the drop or timeout reason and reports <c>TimedOut</c> or <c>Dropped</c> to the script.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="reason">The disconnect reason bytes.</param>
    /// <param name="timedOut">True on a timeout.</param>
    public override void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut)
    {
        mClosed = true;
        mReason = (timedOut ? "timed out" : "dropped") + (reason.Length > 0 ? ": " + System.Text.Encoding.Latin1.GetString(reason).TrimEnd('\0') : "");
        Log?.Invoke(mReason);
        OnConnection?.Invoke(timedOut ? "TimedOut" : "Dropped", mReason);
    }

    /// <summary>Handles a packet read error (client-net.md 1.4): the connection is detached with a Disconnect packet that has an empty reason, then the script is told. Nothing else of the packet is used.</summary>
    private void PacketError(string text)
    {
        mReason = "packet error: " + text;
        Log?.Invoke(mReason);
        if (mVc != null && mVc.State() == ConnectionState.Connected)
            mVc.Close(Connection.Disconnect, "", Now);
        mClosed = true;
        OnPacketError?.Invoke(text);
    }

    /// <summary>Passes a delivery notification to the game stream.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="cookie">The cookie of the sent packet.</param>
    /// <param name="delivered">True when the server acknowledged it.</param>
    public override void OnNotify(Connection c, uint cookie, bool delivered) => Game.Notify(cookie, delivered);

    /// <summary>Decodes a data packet, feeds the prediction and answers the mission-load handshake: <c>dataFinished()</c> and <c>onClientGhostAlwaysDone()</c> answer with remote evals (client-net.md 2.6, 2.7).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The packet payload.</param>
    public override void OnData(Connection c, ReadOnlySpan<byte> payload)
    {
        DecodedPacket p = Game.Receive(payload.ToArray());
        mDump.Write(p, mClock.Elapsed.TotalSeconds);
        if (p.Error != null)
        {
            PacketError(p.Error);
            return;
        }
        PacketHook?.Invoke(p);
        if (PredictFactory != null)
            FollowMission();
        if (PredictWorld != null && p.Ghosts != null)
            Remote.OnBlock(p.Ghosts, World, mFrameNow, World.Psc.HasControl ? World.Psc.ControlGhost : -1);
        if (p.Psc != null && p.Psc.Direction == "s2c")
            Prediction.OnTurretBlock(World, mFrameNow);
        if (PredictWorld != null && p.Psc != null && p.Psc.Direction == "s2c")
        {
            CorrectionRecord? rec = Prediction.OnServerBlock(World, mFrameNow);
            if (rec != null)
                mPendingCorrection = rec;
        }
        if (p.UnknownReason != null)
            Log?.Invoke("UNKNOWN " + p.UnknownReason);
        // dataFinished() and onClientGhostAlwaysDone() answer with remote evals (2.6, 2.7).
        if (!mDataSent && World.DataBlocks.Finished)
        {
            mDataSent = true;
            if (External)
                OnDataFinished?.Invoke();
            else
                Game.PostRemoteEval("dataFinished");
        }
        // a mission reload ghosts the connected client again and the server waits for the next CGADone
        if (mCgaSent && World.AlwaysDoneCount > mCgaCount)
            mCgaSent = false;
        if (!mCgaSent && World.AlwaysDone)
        {
            mCgaSent = true;
            mCgaCount = World.AlwaysDoneCount;
            if (External)
                OnAlwaysDone?.Invoke();
            else
                Game.PostRemoteEval("CGADone");
        }
    }

    // The prediction world follows the server's mission: the last MInfo names it.
    private void FollowMission()
    {
        for (var i = World.RemoteCalls.Count - 1; i >= 0; --i)
        {
            if (!World.RemoteCalls[i].Name.Equals("MInfo", StringComparison.OrdinalIgnoreCase) || World.RemoteCalls[i].Args.Length == 0)
                continue;
            var mission = World.RemoteCalls[i].Args[0];
            if (mission.Equals(Mission, StringComparison.OrdinalIgnoreCase))
                return;
            Prediction.Reset();
            Remote.Reset();
            Mission = mission;
            Log?.Invoke("mission " + mission + ": building the prediction world");
            PredictWorld = PredictFactory!(mission);
            if (PredictWorld != null)
            {
                Prediction.CreateObject = PredictWorld.Create;
                Prediction.DeleteObject = PredictWorld.Delete;
                Remote.CreateObject = PredictWorld.CreateRemote;
                Remote.DeleteObject = PredictWorld.Delete;
                Remote.ControlObject = () => Prediction.Object;
                Remote.Interpolate = () => ReadIntPref("$net::interpolateTime", SmoothWindow.DefaultInterpolate);
                Remote.Predict = () => ReadIntPref("$net::predictForwardTime", SmoothWindow.DefaultPredict);
            }
            return;
        }
    }


    private static Address Resolve(string hostPort)
    {
        var colon = hostPort.LastIndexOf(':');
        var host = colon < 0 ? hostPort : hostPort[..colon];
        var port = colon < 0 ? (ushort)28001 : ushort.Parse(hostPort[(colon + 1)..], CultureInfo.InvariantCulture);
        IPAddress ip = host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
        var b = ip.GetAddressBytes();
        return new Address(((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3], port);
    }

    // net-protocol.md 5.1: version, "Tribes", version string, password, name, gender, voice, skin base.
    private byte[] ConnectPayload()
    {
        var buf = new byte[1500];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteInt(1, 16);
        foreach (var s in new[] { "Tribes", Version, Password, mName, Gender, Voice, SkinBase })
            bs.WriteString(s);
        return buf.AsSpan(0, bs.BytePosition()).ToArray();
    }

    private void ApplyScript(double seconds)
    {
        while (mScriptPos < mScript.Count && mScript[mScriptPos].At <= seconds)
        {
            foreach (var kv in mScript[mScriptPos].Keys)
            {
                if (kv.Key == "spawn")
                    mSpawn = kv.Value != 0;
                else
                    mHeld[kv.Key] = kv.Value;
            }
            ++mScriptPos;
        }
    }

    private Move MakeMove(long tick)
    {
        double D(string k) => mHeld.GetValueOrDefault(k);
        var m = new Move
        {
            Forward = (float)(Math.Clamp((int)D("fwd"), 0, 15) / 15.0),
            Backward = (float)(Math.Clamp((int)D("back"), 0, 15) / 15.0),
            Left = (float)(Math.Clamp((int)D("left"), 0, 15) / 15.0),
            Right = (float)(Math.Clamp((int)D("right"), 0, 15) / 15.0),
            Jet = D("jet") != 0,
            Crouch = D("crouch") != 0,
            Jump = D("jump") != 0,
            Fire = D("fire") != 0,
            Turn = (float)D("turn"),
            Pitch = (float)D("pitch"),
        };
        if (mHeld.TryGetValue("useItem", out var u) && u >= 0)
            m.UseItem = (int)u;
        // Jump and useItem fire once per script step (4.2 step 5 resets them after the next move).
        mHeld.Remove("jump");
        mHeld.Remove("useItem");
        if (mSpawn && World.Psc.ControlClass != "Player")
            m.Fire = tick / 8 % 2 == 0;           // press and release every 256 ms until a Player is the control object
        // 4.2 step 4: the buffered move is the quantised one the server reads.
        return ClientPrediction.Quantize(m);
    }

    /// <summary>Runs the session: connects, plays for <paramref name="seconds"/> after the accept and disconnects.</summary>
    /// <param name="hostPort">The server as <c>host:port</c>.</param>
    /// <param name="seconds">The play time after the accept.</param>
    /// <returns>The result.</returns>
    /// <exception cref="IOException">The UDP socket cannot open.</exception>
    public SessionResult Run(string hostPort, double seconds)
    {
        var res = new SessionResult();
        Address server = Resolve(hostPort);
        if (!mSock.OpenToward(server))
            throw new IOException("cannot open a UDP socket");
        var rng = new Random();
        var id = (uint)rng.NextInt64(1, 0xffffffffL);
        mVc = new Connection(server, id, this, (to, data) => mSock.Send(to, data), Now);
        mVc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        var rx = new byte[0x10000];
        double acceptedAt = -1;
        double timeout = 60;
        while (!mClosed)
        {
            var now = Now;
            mFrameNow = now;
            // client-net.md 2.1: the moves of the ticks crossed are made (step 4) before the receive (step 5), so a
            // correction in this frame sees them and +0x218 is computed from the frame's net time.
            if (acceptedAt >= 0)
            {
                ApplyScript((now / 1000.0) - acceptedAt);
                var tickNow = ((long)now - 1) >> 5;
                for (; mLastTick < tickNow; ++mLastTick)
                {
                    World.Psc.AddMove(MakeMove(mLastTick));
                    ++mMovesSent;
                }
            }
            int n;
            while ((n = mSock.Receive(out Address from, rx)) > 0)
            {
                if (from == server)
                    mVc.Receive(rx.AsSpan(0, n), Now);
                if (mClosed)
                    break;
            }
            mVc.Tick(Now);
            if (mClosed)
                break;
            var t = now / 1000.0;
            if (IsAccepted && acceptedAt < 0)
            {
                acceptedAt = t;
                mLastTick = (now - 1) >> 5;
            }
            if (acceptedAt < 0 && t > timeout)
            {
                mReason = "no accept within " + timeout + " s";
                break;
            }
            if (acceptedAt >= 0)
            {
                Prediction.AdvanceTurret(World.Psc, now);
                if (PredictWorld != null)
                {
                    // 4.4: the replay after a correction and the prediction of the new moves, once per frame.
                    Prediction.BindControlMount(World, Remote);
                    Prediction.Advance(World.Psc, now);
                    Remote.Advance(now);
                    Prediction.PlaceControlRider();
                    if (mPendingCorrection != null)
                    {
                        mDump.WriteCorrection(mPendingCorrection, mClock.Elapsed.TotalSeconds);
                        mPendingCorrection = null;
                    }
                }
                if (mVc.State() == ConnectionState.Connected && Game.Due(now))
                {
                    Connection vc = mVc;
                    Game.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
                }
                if (t - acceptedAt >= seconds)
                    break;
            }
            Thread.Sleep(1);
        }
        if (!mClosed && mVc.State() == ConnectionState.Connected)
        {
            mVc.Close(Connection.Disconnect, "", Now);
            res.Disconnected = true;
        }
        mSock.Close();
        res.Connected = IsAccepted;
        res.Rejected = mRejected;
        res.Dropped = mClosed && !mRejected;
        res.Reason = mReason;
        res.Seconds = acceptedAt < 0 ? 0 : (Now / 1000.0) - acceptedAt;
        res.Packets = World.Packets;
        res.Events = World.Events;
        res.GhostUpdates = World.GhostUpdates;
        res.GhostDeletes = World.GhostDeletes;
        res.UnknownBits = World.UnknownBits;
        res.Errors = World.Errors;
        res.MovesSent = mMovesSent;
        res.Ghosts = World.Ghosts.Count;
        res.ControlClass = World.Psc.ControlClass;
        res.DataFinished = World.DataBlocks.Finished;
        res.AlwaysDone = World.AlwaysDone;
        foreach (var kv in World.EventCounts)
            res.EventCounts[kv.Key] = kv.Value;
        foreach (var kv in World.GhostCounts)
            res.GhostCounts[kv.Key] = kv.Value;
        res.UnknownReasons = [.. World.UnknownReasons.Take(20)];
        return res;
    }

    /// <inheritdoc/>
    public void Dispose() => mSock.Dispose();
}
