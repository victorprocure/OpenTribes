// Headless client session (docs/client-net.md 1, 2.5, 2.7, 4.3): connect/challenge/accept as client, the game stream
// receive path, the mission-load handshake (SetCLInfo, dataFinished, CGADone), moves at the 32 ms tick from a script,
// disconnect. C2's client reuses this class for its network side.
using OpenTribes.Script;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using OpenTribes.Game;
using OpenTribes.Net;

namespace OpenTribes.ClientNet;

// One line of a move script: from `At` seconds on, the held move state changes by the given keys.
public sealed class ScriptStep
{
    public double At;
    public Dictionary<string, double> Keys = new();
}

public static class MoveScript
{
    // Lines: `<seconds> key=value ...` with fwd back left right (0..15), jet jump crouch fire (0/1), pitch turn (radians
    // per move), useItem (item type), spawn (1: toggle fire until the control object is a Player). `#` starts a comment.
    public static List<ScriptStep> Parse(string text)
    {
        var steps = new List<ScriptStep>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var s = new ScriptStep { At = double.Parse(words[0], CultureInfo.InvariantCulture) };
            foreach (string w in words.Skip(1))
            {
                string[] kv = w.Split('=');
                if (kv.Length != 2)
                    throw new FormatException("bad script word: " + w);
                s.Keys[kv[0]] = double.Parse(kv[1], CultureInfo.InvariantCulture);
            }
            steps.Add(s);
        }
        return steps.OrderBy(s => s.At).ToList();
    }
}

public sealed class SessionResult
{
    public bool Connected, Rejected, Dropped, Disconnected;
    public string Reason = "";
    public double Seconds;
    public long Packets, Events, GhostUpdates, GhostDeletes, UnknownBits, Errors, MovesSent;
    public int Ghosts;
    public string ControlClass = "";
    public bool DataFinished, AlwaysDone;
    public SortedDictionary<string, long> EventCounts = new(), GhostCounts = new();
    public List<string> UnknownReasons = new();
}

public sealed class NetClientSession : ConnectionEvents
{
    private readonly UdpSocket mSock = new();
    private readonly Stopwatch mClock = Stopwatch.StartNew();
    private Connection? mVc;
    private ClientWorld mWorld = new();
    private ClientGameConnection mGame;
    private readonly string mName;
    private readonly PacketDump mDump;
    private readonly List<ScriptStep> mScript;
    private int mScriptPos;
    private readonly Dictionary<string, double> mHeld = new();
    private int mCgaCount;
    private bool mAccepted, mDataSent, mCgaSent, mClosed, mRejected, mSpawn;
    private string mReason = "";
    private long mLastTick;
    private long mMovesSent;
    private CorrectionRecord? mPendingCorrection;
    private uint mFrameNow;
    public readonly string Version;
    // client-net.md 1.2: $Server::JoinPassword and $PCFG::Gender, Voice, SkinBase.
    public string Password = "", Gender = "Male", Voice = "Male1", SkinBase = "base";

    public ClientWorld World => mWorld;
    public Action<string>? Log;

    // Client prediction of the control object (docs/client-net.md 4.4); runs when a prediction world is set.
    public readonly ClientPrediction Prediction = new();
    // Smoothing and look-ahead of the remote Player and Vehicle ghosts (client-net.md 4.4.2), needs the prediction world.
    public readonly RemoteGhosts Remote = new();
    public PredictionWorld? PredictWorld;
    // Builds the prediction world of a mission; the mission comes from the server's remoteEval MInfo (client.cs).
    public Func<string, PredictionWorld?>? PredictFactory;
    public string Mission = "";

    public NetClientSession(string name, PacketDump dump, List<ScriptStep>? script = null, string version = "1.11")
    {
        mName = name;
        mDump = dump;
        mScript = script ?? new List<ScriptStep>();
        Version = version;
        mGame = new ClientGameConnection(mWorld);
    }

    private uint Now => ClockMs != null ? ClockMs() : (uint)mClock.ElapsedMilliseconds;

    // ---- M9 C8: the session as a part of the `tribes` frame loop ----

    // True: the scripts of the client drive the join (onConnection sends SetCLInfo, dataFinished() and
    // onClientGhostAlwaysDone() answer the server) and the moves come from the PSC buffer (client-net.md 4.2); the
    // session does neither itself.
    public bool External;
    public Func<uint>? ClockMs;
    // onConnection(msg) of the script (client-net.md 1.6): Accepted, Rejected, TimedOut, Dropped, and the reason text.
    public Action<string, string>? OnConnection;
    // TribesNet_onPacketError 0x0042b7f8: a member reader failed with this text; the script gets onConnectionError(...).
    public Action<string>? OnPacketError;
    public Action? OnDataFinished, OnAlwaysDone;
    public Action<DecodedPacket>? PacketHook;
    // Reads a console variable ($pref::PacketRate and $pref::PacketSize feed the own rate request); null: defaults.
    public Func<string, string>? PrefReader;
    private void ApplyPacketPrefs()
    {
        if (PrefReader != null)
            mGame.LoadPacketPrefs(PrefReader("$pref::PacketRate"), PrefReader("$pref::PacketSize"));
    }
    public bool IsOpen => mVc != null && !mClosed;
    public bool IsAccepted => mAccepted;
    public ClientGameConnection Game => mGame;
    private bool mOpened;

    // Opens the socket and sends the connect request (client-net.md 1.2); the replies arrive in Frame.
    public bool Connect(string hostPort)
    {
        ApplyPacketPrefs();
        Address server = Resolve(hostPort);
        mServerAddress = server;
        if (!mSock.OpenToward(server))
            return false;
        var rng = new Random();
        uint id = (uint)rng.NextInt64(1, 0xffffffffL);
        mVc = new Connection(server, id, this, (to, data) => mSock.Send(to, data), Now);
        mVc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        mOpened = true;
        mClosed = false;
        return true;
    }

    // A console variable of type int (atoi of its text), the default when the host has no console.
    private int ReadIntPref(string name, int def) => PrefReader == null ? def : unchecked((int)Numbers.BorlandAtoi(PrefReader(name) ?? ""));

    private Address mServerAddress;
    private readonly byte[] mRx = new byte[0x10000];

    // One client frame of the net steps F10 (receive), F14 (prediction) and F12 (send) of client-app.md 2.2.
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
        if (mAccepted)
        {
            Prediction.AdvanceTurret(mWorld.Psc, now);
            if (PredictWorld != null)
            {
                Prediction.BindControlMount(mWorld, Remote);
                Prediction.Advance(mWorld.Psc, now);
                Remote.Advance(now);
                Prediction.PlaceControlRider();
                mPendingCorrection = null;
            }
            if (mVc.State() == ConnectionState.Connected && mGame.Due(now))
            {
                Connection vc = mVc;
                mGame.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
            }
        }
    }

    // disconnect(): a Disconnect packet when connected, then the socket closes.
    public void Disconnect()
    {
        if (mVc != null && !mClosed && mVc.State() == ConnectionState.Connected)
            mVc.Close(Connection.Disconnect, "", Now);
        mClosed = true;
        mOpened = false;
        mSock.Close();
    }

    // ---- ConnectionEvents ----

    public override void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload) { }

    public override void OnAccepted(Connection c, ReadOnlySpan<byte> payload)
    {
        // net-protocol.md 5.4: the player id and the server manager id (1.5 step 2).
        byte[] b = payload.ToArray();
        if (b.Length >= 8 && External)
        {
            mWorld.PlayerId = BitConverter.ToInt32(b, 0);
            mWorld.ServerId = BitConverter.ToInt32(b, 4);
        }
        else if (b.Length >= 8)
        {
            mWorld = new ClientWorld { PlayerId = BitConverter.ToInt32(b, 0), ServerId = BitConverter.ToInt32(b, 4) };
            mGame = new ClientGameConnection(mWorld);
            ApplyPacketPrefs();
        }
        mAccepted = true;
        Log?.Invoke("accepted: player " + mWorld.PlayerId + ", server " + mWorld.ServerId);
        if (External)
        {
            OnConnection?.Invoke("Accepted", "");
            return;
        }
        // onConnection("Accepted") (1.6): remoteEval(2048, "SetCLInfo", skin, real name, email, tribe, url, info,
        // autoWaypoint, noEnterInvStation, messageMask).
        mGame.PostRemoteEval("SetCLInfo", "base", mName, "", "", "", "", "true", "false", "-1");
    }

    public override void OnRejected(Connection c, ReadOnlySpan<byte> payload)
    {
        mRejected = true;
        mClosed = true;
        mReason = System.Text.Encoding.Latin1.GetString(payload).TrimEnd('\0');
        Log?.Invoke("rejected: " + mReason);
        OnConnection?.Invoke("Rejected", mReason);
    }

    public override void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut)
    {
        mClosed = true;
        mReason = (timedOut ? "timed out" : "dropped") + (reason.Length > 0 ? ": " + System.Text.Encoding.Latin1.GetString(reason).TrimEnd('\0') : "");
        Log?.Invoke(mReason);
        OnConnection?.Invoke(timedOut ? "TimedOut" : "Dropped", mReason);
    }

    // client-net.md 1.4 packet read error: the connection is detached with a Disconnect packet that has an empty reason, then the
    // script is told. Nothing else of the packet is used.
    private void PacketError(string text)
    {
        mReason = "packet error: " + text;
        Log?.Invoke(mReason);
        if (mVc != null && mVc.State() == ConnectionState.Connected)
            mVc.Close(Connection.Disconnect, "", Now);
        mClosed = true;
        OnPacketError?.Invoke(text);
    }

    public override void OnNotify(Connection c, uint cookie, bool delivered) => mGame.Notify(cookie, delivered);

    public override void OnData(Connection c, ReadOnlySpan<byte> payload)
    {
        DecodedPacket p = mGame.Receive(payload.ToArray());
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
            Remote.OnBlock(p.Ghosts, mWorld, mFrameNow, mWorld.Psc.HasControl ? mWorld.Psc.ControlGhost : -1);
        if (p.Psc != null && p.Psc.Direction == "s2c")
            Prediction.OnTurretBlock(mWorld, mFrameNow);
        if (PredictWorld != null && p.Psc != null && p.Psc.Direction == "s2c")
        {
            CorrectionRecord? rec = Prediction.OnServerBlock(mWorld, mFrameNow);
            if (rec != null)
                mPendingCorrection = rec;
        }
        if (p.UnknownReason != null)
            Log?.Invoke("UNKNOWN " + p.UnknownReason);
        // dataFinished() and onClientGhostAlwaysDone() answer with remote evals (2.6, 2.7).
        if (!mDataSent && mWorld.DataBlocks.Finished)
        {
            mDataSent = true;
            if (External)
                OnDataFinished?.Invoke();
            else
                mGame.PostRemoteEval("dataFinished");
        }
        // a mission reload ghosts the connected client again and the server waits for the next CGADone
        if (mCgaSent && mWorld.AlwaysDoneCount > mCgaCount)
            mCgaSent = false;
        if (!mCgaSent && mWorld.AlwaysDone)
        {
            mCgaSent = true;
            mCgaCount = mWorld.AlwaysDoneCount;
            if (External)
                OnAlwaysDone?.Invoke();
            else
                mGame.PostRemoteEval("CGADone");
        }
    }

    // The prediction world follows the server's mission: the last MInfo names it.
    private void FollowMission()
    {
        for (int i = mWorld.RemoteCalls.Count - 1; i >= 0; --i)
        {
            if (!mWorld.RemoteCalls[i].Name.Equals("MInfo", StringComparison.OrdinalIgnoreCase) || mWorld.RemoteCalls[i].Args.Length == 0)
                continue;
            string mission = mWorld.RemoteCalls[i].Args[0];
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

    // ---- run ----

    private static Address Resolve(string hostPort)
    {
        int colon = hostPort.LastIndexOf(':');
        string host = colon < 0 ? hostPort : hostPort[..colon];
        ushort port = colon < 0 ? (ushort)28001 : ushort.Parse(hostPort[(colon + 1)..], CultureInfo.InvariantCulture);
        IPAddress ip = host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
        byte[] b = ip.GetAddressBytes();
        return new Address((uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3], port);
    }

    // net-protocol.md 5.1: version, "Tribes", version string, password, name, gender, voice, skin base.
    private byte[] ConnectPayload()
    {
        byte[] buf = new byte[1500];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteInt(1, 16);
        foreach (string s in new[] { "Tribes", Version, Password, mName, Gender, Voice, SkinBase })
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
        if (mHeld.TryGetValue("useItem", out double u) && u >= 0)
            m.UseItem = (int)u;
        // Jump and useItem fire once per script step (4.2 step 5 resets them after the next move).
        mHeld.Remove("jump");
        mHeld.Remove("useItem");
        if (mSpawn && mWorld.Psc.ControlClass != "Player")
            m.Fire = (tick / 8) % 2 == 0;           // press and release every 256 ms until a Player is the control object
        // 4.2 step 4: the buffered move is the quantised one the server reads.
        return ClientPrediction.Quantize(m);
    }

    // Runs the session: connect to host:port, play for `seconds` after the accept, disconnect.
    public SessionResult Run(string hostPort, double seconds)
    {
        var res = new SessionResult();
        Address server = Resolve(hostPort);
        if (!mSock.OpenToward(server))
            throw new IOException("cannot open a UDP socket");
        var rng = new Random();
        uint id = (uint)rng.NextInt64(1, 0xffffffffL);
        mVc = new Connection(server, id, this, (to, data) => mSock.Send(to, data), Now);
        mVc.Send(Connection.ConnectRequest, ConnectPayload(), Now);
        byte[] rx = new byte[0x10000];
        double acceptedAt = -1;
        double timeout = 60;
        while (!mClosed)
        {
            uint now = Now;
            mFrameNow = now;
            // client-net.md 2.1: the moves of the ticks crossed are made (step 4) before the receive (step 5), so a
            // correction in this frame sees them and +0x218 is computed from the frame's net time.
            if (acceptedAt >= 0)
            {
                ApplyScript(now / 1000.0 - acceptedAt);
                long tickNow = ((long)now - 1) >> 5;
                for (; mLastTick < tickNow; ++mLastTick)
                {
                    mWorld.Psc.AddMove(MakeMove(mLastTick));
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
            double t = now / 1000.0;
            if (mAccepted && acceptedAt < 0)
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
                Prediction.AdvanceTurret(mWorld.Psc, now);
                if (PredictWorld != null)
                {
                    // 4.4: the replay after a correction and the prediction of the new moves, once per frame.
                    Prediction.BindControlMount(mWorld, Remote);
                    Prediction.Advance(mWorld.Psc, now);
                    Remote.Advance(now);
                    Prediction.PlaceControlRider();
                    if (mPendingCorrection != null)
                    {
                        mDump.WriteCorrection(mPendingCorrection, mClock.Elapsed.TotalSeconds);
                        mPendingCorrection = null;
                    }
                }
                if (mVc.State() == ConnectionState.Connected && mGame.Due(now))
                {
                    Connection vc = mVc;
                    mGame.WritePacket(now, (buf, len, cookie) => vc.Send(Connection.Data, buf.AsSpan(0, len), now, cookie));
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
        res.Connected = mAccepted;
        res.Rejected = mRejected;
        res.Dropped = mClosed && !mRejected;
        res.Reason = mReason;
        res.Seconds = acceptedAt < 0 ? 0 : Now / 1000.0 - acceptedAt;
        res.Packets = mWorld.Packets;
        res.Events = mWorld.Events;
        res.GhostUpdates = mWorld.GhostUpdates;
        res.GhostDeletes = mWorld.GhostDeletes;
        res.UnknownBits = mWorld.UnknownBits;
        res.Errors = mWorld.Errors;
        res.MovesSent = mMovesSent;
        res.Ghosts = mWorld.Ghosts.Count;
        res.ControlClass = mWorld.Psc.ControlClass;
        res.DataFinished = mWorld.DataBlocks.Finished;
        res.AlwaysDone = mWorld.AlwaysDone;
        foreach (var kv in mWorld.EventCounts)
            res.EventCounts[kv.Key] = kv.Value;
        foreach (var kv in mWorld.GhostCounts)
            res.GhostCounts[kv.Key] = kv.Value;
        res.UnknownReasons = mWorld.UnknownReasons.Take(20).ToList();
        return res;
    }
}
