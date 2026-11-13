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
    public double At { get; set; }
    /// <summary>The key values that change at this step.</summary>
    public Dictionary<string, double> Keys { get; set; } = [];
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
    public bool Connected { get; set; }
    /// <summary>True when the server rejected the connection.</summary>
    public bool Rejected { get; set; }
    /// <summary>True when the connection closed without a reject, for example on a drop or timeout.</summary>
    public bool Dropped { get; set; }
    /// <summary>True when the session ended with our own disconnect.</summary>
    public bool Disconnected { get; set; }
    /// <summary>The reject, drop or timeout reason.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The session length in seconds.</summary>
    public double Seconds { get; set; }
    /// <summary>The packets the world decoded.</summary>
    public long Packets { get; set; }
    /// <summary>The events received.</summary>
    public long Events { get; set; }
    /// <summary>The ghost updates received.</summary>
    public long GhostUpdates { get; set; }
    /// <summary>The ghost deletes received.</summary>
    public long GhostDeletes { get; set; }
    /// <summary>The bits the decoder could not interpret.</summary>
    public long UnknownBits { get; set; }
    /// <summary>The decode errors of the world.</summary>
    public long Errors { get; set; }
    /// <summary>The moves sent.</summary>
    public long MovesSent { get; set; }
    /// <summary>The ghosts alive at the end.</summary>
    public int Ghosts { get; set; }
    /// <summary>The class of the control object.</summary>
    public string ControlClass { get; set; } = "";
    /// <summary>True when the datablock phase finished.</summary>
    public bool DataFinished { get; set; }
    /// <summary>True when the ghost-always phase finished.</summary>
    public bool AlwaysDone { get; set; }
    /// <summary>The event counts by name.</summary>
    public SortedDictionary<string, long> EventCounts { get; set; } = [];
    /// <summary>The ghost update counts by class.</summary>
    public SortedDictionary<string, long> GhostCounts { get; set; } = [];
    /// <summary>The reasons packets could not be fully decoded.</summary>
    public List<string> UnknownReasons { get; set; } = [];
}

/// <summary>A headless client session (docs/client-net.md 1, 2.5, 2.7, 4.3): connect, challenge and accept as a client, the game stream receive path, the mission-load handshake (SetCLInfo, dataFinished, CGADone), moves at the 32 ms tick from a script, and disconnect. The <c>tribes</c> client reuses this class for its network side.</summary>
public sealed class NetClientSession : ConnectionEvents, IDisposable
{
    private readonly UdpSocket _sock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Connection? _vc;
    private readonly string _name;
    private readonly PacketDump _dump;
    private readonly List<ScriptStep> _script;
    private int _scriptPos;
    private readonly Dictionary<string, double> _held = [];
    private int _cgaCount;
    private bool _dataSent, _cgaSent, _closed, _rejected, _spawn;
    private string _reason = "";
    private long _lastTick;
    private long _movesSent;
    private CorrectionRecord? _pendingCorrection;
    private uint _frameNow;
    /// <summary>The version string sent in the connect request.</summary>
    public string Version { get; }
    /// <summary>The join password (<c>$Server::JoinPassword</c>, client-net.md 1.2).</summary>
    public string Password { get; set; } = "";
    /// <summary>The gender (<c>$PCFG::Gender</c>).</summary>
    public string Gender { get; set; } = "Male";
    /// <summary>The voice (<c>$PCFG::Voice</c>).</summary>
    public string Voice { get; set; } = "Male1";
    /// <summary>The skin base (<c>$PCFG::SkinBase</c>).</summary>
    public string SkinBase { get; set; } = "base";

    /// <summary>The client world built from the stream.</summary>
    public ClientWorld World { get; private set; } = new();
    /// <summary>Receives log lines.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The client prediction of the control object (docs/client-net.md 4.4). It runs when a prediction world is set.</summary>
    public ClientPrediction Prediction { get; } = new();
    /// <summary>The smoothing and look-ahead of the remote Player and Vehicle ghosts (client-net.md 4.4.2). It needs the prediction world.</summary>
    public RemoteGhosts Remote { get; } = new();
    /// <summary>The prediction world, or null.</summary>
    public PredictionWorld? PredictWorld { get; set; }
    /// <summary>Builds the prediction world of a mission. The mission comes from the server's <c>remoteEval</c> MInfo (client.cs).</summary>
    public Func<string, PredictionWorld?>? PredictFactory { get; set; }
    /// <summary>The mission the server reported.</summary>
    public string Mission { get; set; } = "";

    /// <summary>Creates a session.</summary>
    /// <param name="name">The player name.</param>
    /// <param name="dump">Receives each decoded packet.</param>
    /// <param name="script">The move script, or null for none.</param>
    /// <param name="version">The version string to send.</param>
    public NetClientSession(string name, PacketDump dump, List<ScriptStep>? script = null, string version = "1.11")
    {
        _name = name;
        _dump = dump;
        _script = script ?? [];
        Version = version;
        Game = new ClientGameConnection(World);
    }

    private uint Now => ClockMs != null ? ClockMs() : (uint)_clock.ElapsedMilliseconds;


    /// <summary>True when the client scripts drive the join and the moves, as in the <c>tribes</c> frame loop. <c>onConnection</c> sends SetCLInfo, <c>dataFinished()</c> and <c>onClientGhostAlwaysDone()</c> answer the server, and the moves come from the PSC buffer (client-net.md 4.2). The session does neither itself.</summary>
    public bool External { get; set; }
    /// <summary>Returns the clock in milliseconds. Defaults to a stopwatch.</summary>
    public Func<uint>? ClockMs { get; set; }
    /// <summary>Raised like the script <c>onConnection(msg)</c> (client-net.md 1.6) with Accepted, Rejected, TimedOut or Dropped, and the reason text.</summary>
    public Action<string, string>? OnConnection { get; set; }
    /// <summary>Raised when a member reader failed, with its text. The script gets <c>onConnectionError(...)</c> (TribesNet_onPacketError, 0042b7f8).</summary>
    public Action<string>? OnPacketError { get; set; }
    /// <summary>Raised when the datablock phase finished.</summary>
    public Action? OnDataFinished { get; set; }
    /// <summary>Raised when the ghost-always phase finished.</summary>
    public Action? OnAlwaysDone { get; set; }
    /// <summary>Called with every decoded packet.</summary>
    public Action<DecodedPacket>? PacketHook { get; set; }
    /// <summary>Reads a console variable. <c>$pref::PacketRate</c> and <c>$pref::PacketSize</c> feed the own rate request. Null means defaults.</summary>
    public Func<string, string>? PrefReader { get; set; }
    private void ApplyPacketPrefs()
    {
        if (PrefReader != null)
            Game.LoadPacketPrefs(PrefReader("$pref::PacketRate"), PrefReader("$pref::PacketSize"));
    }
    /// <summary>Tells whether the connection is open.</summary>
    public bool IsOpen => _vc != null && !_closed;
    /// <summary>Tells whether the server accepted the connection.</summary>
    public bool IsAccepted { get; private set; }
    /// <summary>The game stream connection.</summary>
    public ClientGameConnection Game { get; private set; }
    private bool _opened;

    /// <summary>Opens the socket and sends the connect request (client-net.md 1.2). The replies arrive in <see cref="Frame"/>.</summary>
    /// <param name="hostPort">The server as <c>host:port</c>.</param>
    /// <returns>False when the socket cannot open.</returns>
    public bool Connect(string hostPort)
    {
        ApplyPacketPrefs();
        var server = Resolve(hostPort);
        _serverAddress = server;
        if (!_sock.OpenToward(server))
            return false;
        var rng = new Random();
        var id = (uint)rng.NextInt64(1, 0xffffffffL);
        _vc = new Connection(server, id, this, (to, data) => _sock.Send(to, data), Now);
        _vc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        _opened = true;
        _closed = false;
        return true;
    }

    // A console variable of type int (atoi of its text), the default when the host has no console.
    private int ReadIntPref(string name, int def) => PrefReader == null ? def : unchecked(Numbers.BorlandAtoi(PrefReader(name) ?? ""));

    private Address _serverAddress;
    private readonly byte[] _rx = new byte[0x10000];

    /// <summary>Runs the net steps of one client frame: F10 (receive), F14 (prediction) and F12 (send) of client-app.md 2.2.</summary>
    /// <param name="now">The clock in milliseconds.</param>
    public void Frame(uint now)
    {
        if (!_opened || _vc == null)
            return;
        _frameNow = now;
        int n;
        while (!_closed && (n = _sock.Receive(out var from, _rx)) > 0)
            if (from == _serverAddress)
                _vc.Receive(_rx.AsSpan(0, n), Now);
        if (!_closed)
            _vc.Tick(Now);
        if (_closed)
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
                _pendingCorrection = null;
            }
            if (_vc.State() == ConnectionState.Connected && Game.Due(now))
            {
                var vc = _vc;
                Game.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
            }
        }
    }

    /// <summary>Sends a Disconnect packet when connected, then closes the socket (<c>disconnect()</c>).</summary>
    public void Disconnect()
    {
        if (_vc != null && !_closed && _vc.State() == ConnectionState.Connected)
            _vc.Close(Connection.Disconnect, "", Now);
        _closed = true;
        _opened = false;
        _sock.Close();
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
        Game.PostRemoteEval("SetCLInfo", "base", _name, "", "", "", "", "true", "false", "-1");
    }

    /// <summary>Records the reject reason and reports <c>Rejected</c> to the script.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The reason bytes.</param>
    public override void OnRejected(Connection c, ReadOnlySpan<byte> payload)
    {
        _rejected = true;
        _closed = true;
        _reason = System.Text.Encoding.Latin1.GetString(payload).TrimEnd('\0');
        Log?.Invoke("rejected: " + _reason);
        OnConnection?.Invoke("Rejected", _reason);
    }

    /// <summary>Records the drop or timeout reason and reports <c>TimedOut</c> or <c>Dropped</c> to the script.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="reason">The disconnect reason bytes.</param>
    /// <param name="timedOut">True on a timeout.</param>
    public override void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut)
    {
        _closed = true;
        _reason = (timedOut ? "timed out" : "dropped") + (reason.Length > 0 ? ": " + System.Text.Encoding.Latin1.GetString(reason).TrimEnd('\0') : "");
        Log?.Invoke(_reason);
        OnConnection?.Invoke(timedOut ? "TimedOut" : "Dropped", _reason);
    }

    /// <summary>Handles a packet read error (client-net.md 1.4): the connection is detached with a Disconnect packet that has an empty reason, then the script is told. Nothing else of the packet is used.</summary>
    private void PacketError(string text)
    {
        _reason = "packet error: " + text;
        Log?.Invoke(_reason);
        if (_vc != null && _vc.State() == ConnectionState.Connected)
            _vc.Close(Connection.Disconnect, "", Now);
        _closed = true;
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
        var p = Game.Receive(payload.ToArray());
        _dump.Write(p, _clock.Elapsed.TotalSeconds);
        if (p.Error != null)
        {
            PacketError(p.Error);
            return;
        }
        PacketHook?.Invoke(p);
        if (PredictFactory != null)
            FollowMission();
        if (PredictWorld != null && p.Ghosts != null)
            Remote.OnBlock(p.Ghosts, World, _frameNow, World.Psc.HasControl ? World.Psc.ControlGhost : -1);
        if (p.Psc != null && p.Psc.Direction == "s2c")
            Prediction.OnTurretBlock(World, _frameNow);
        if (PredictWorld != null && p.Psc != null && p.Psc.Direction == "s2c")
        {
            var rec = Prediction.OnServerBlock(World, _frameNow);
            if (rec != null)
                _pendingCorrection = rec;
        }
        if (p.UnknownReason != null)
            Log?.Invoke("UNKNOWN " + p.UnknownReason);
        // dataFinished() and onClientGhostAlwaysDone() answer with remote evals (2.6, 2.7).
        if (!_dataSent && World.DataBlocks.Finished)
        {
            _dataSent = true;
            if (External)
                OnDataFinished?.Invoke();
            else
                Game.PostRemoteEval("dataFinished");
        }
        // a mission reload ghosts the connected client again and the server waits for the next CGADone
        if (_cgaSent && World.AlwaysDoneCount > _cgaCount)
            _cgaSent = false;
        if (!_cgaSent && World.AlwaysDone)
        {
            _cgaSent = true;
            _cgaCount = World.AlwaysDoneCount;
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
        var ip = host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
        var b = ip.GetAddressBytes();
        return new Address(((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3], port);
    }

    // net-protocol.md 5.1: version, "Tribes", version string, password, name, gender, voice, skin base.
    private byte[] ConnectPayload()
    {
        var buf = new byte[1500];
        var bs = new BitBuffer(buf, buf.Length);
        bs.WriteInt(1, 16);
        foreach (var s in new[] { "Tribes", Version, Password, _name, Gender, Voice, SkinBase })
            bs.WriteString(s);
        return buf.AsSpan(0, bs.BytePosition()).ToArray();
    }

    private void ApplyScript(double seconds)
    {
        while (_scriptPos < _script.Count && _script[_scriptPos].At <= seconds)
        {
            foreach (var kv in _script[_scriptPos].Keys)
            {
                if (kv.Key == "spawn")
                    _spawn = kv.Value != 0;
                else
                    _held[kv.Key] = kv.Value;
            }
            ++_scriptPos;
        }
    }

    private Move MakeMove(long tick)
    {
        double D(string k) => _held.GetValueOrDefault(k);
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
        if (_held.TryGetValue("useItem", out var u) && u >= 0)
            m.UseItem = (int)u;
        // Jump and useItem fire once per script step (4.2 step 5 resets them after the next move).
        _held.Remove("jump");
        _held.Remove("useItem");
        if (_spawn && World.Psc.ControlClass != "Player")
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
        var server = Resolve(hostPort);
        if (!_sock.OpenToward(server))
            throw new IOException("cannot open a UDP socket");
        var rng = new Random();
        var id = (uint)rng.NextInt64(1, 0xffffffffL);
        _vc = new Connection(server, id, this, (to, data) => _sock.Send(to, data), Now);
        _vc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        var rx = new byte[0x10000];
        double acceptedAt = -1;
        double timeout = 60;
        while (!_closed)
        {
            var now = Now;
            _frameNow = now;
            // client-net.md 2.1: the moves of the ticks crossed are made (step 4) before the receive (step 5), so a
            // correction in this frame sees them and +0x218 is computed from the frame's net time.
            if (acceptedAt >= 0)
            {
                ApplyScript((now / 1000.0) - acceptedAt);
                var tickNow = ((long)now - 1) >> 5;
                for (; _lastTick < tickNow; ++_lastTick)
                {
                    World.Psc.AddMove(MakeMove(_lastTick));
                    ++_movesSent;
                }
            }
            int n;
            while ((n = _sock.Receive(out var from, rx)) > 0)
            {
                if (from == server)
                    _vc.Receive(rx.AsSpan(0, n), Now);
                if (_closed)
                    break;
            }
            _vc.Tick(Now);
            if (_closed)
                break;
            var t = now / 1000.0;
            if (IsAccepted && acceptedAt < 0)
            {
                acceptedAt = t;
                _lastTick = (now - 1) >> 5;
            }
            if (acceptedAt < 0 && t > timeout)
            {
                _reason = "no accept within " + timeout + " s";
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
                    if (_pendingCorrection != null)
                    {
                        _dump.WriteCorrection(_pendingCorrection, _clock.Elapsed.TotalSeconds);
                        _pendingCorrection = null;
                    }
                }
                if (_vc.State() == ConnectionState.Connected && Game.Due(now))
                {
                    var vc = _vc;
                    Game.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
                }
                if (t - acceptedAt >= seconds)
                    break;
            }
            Thread.Sleep(1);
        }
        if (!_closed && _vc.State() == ConnectionState.Connected)
        {
            _vc.Close(Connection.Disconnect, "", Now);
            res.Disconnected = true;
        }
        _sock.Close();
        res.Connected = IsAccepted;
        res.Rejected = _rejected;
        res.Dropped = _closed && !_rejected;
        res.Reason = _reason;
        res.Seconds = acceptedAt < 0 ? 0 : (Now / 1000.0) - acceptedAt;
        res.Packets = World.Packets;
        res.Events = World.Events;
        res.GhostUpdates = World.GhostUpdates;
        res.GhostDeletes = World.GhostDeletes;
        res.UnknownBits = World.UnknownBits;
        res.Errors = World.Errors;
        res.MovesSent = _movesSent;
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
    public void Dispose() => _sock.Dispose();
}
