// Client EventManager (docs/client-net.md 2.3, docs/game-stream.md 2): read of the event block (00551ef8), unpack of
// every event class and the effect of each handler on the client state. Layouts follow docs/game-stream.md 2.4, 2.5.
namespace OpenTribes.Game;

using System.Globalization;
using System.Text;

/// <summary>One event as read from the stream: the class, its delivery mode, the decoded fields and a one line text.</summary>
public sealed class DecodedEvent
{
    /// <summary>The class tag.</summary>
    public int Tag { get; set; }
    /// <summary>The class name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The delivery mode.</summary>
    public string Mode { get; set; } = "";
    /// <summary>The one line text.</summary>
    public string Text { get; set; } = "";
    /// <summary>The number of bits the event took.</summary>
    public int Bits { get; set; }
    /// <summary>The ordered sequence, -2 when dispatched at once.</summary>
    public int Seq { get; set; } = -2;
    /// <summary>The decoded fields.</summary>
    public List<DecodedField> Fields { get; set; } = [];
    /// <summary>The handler effect (docs/client-net.md 2.3), run on dispatch.</summary>
    public Action? Apply { get; set; }
}

/// <summary>The client EventManager: reads the event block, unpacks every event class and applies the effect of each handler to the client state.</summary>
/// <remarks>See docs/client-net.md 2.3 and docs/game-stream.md 2. Layouts follow docs/game-stream.md 2.4 and 2.5.</remarks>
public sealed class ClientEvents
{
    private readonly ClientWorld _world;
    private int _expectedSeq;                                   // ordered receive (+0x64)
    private readonly SortedDictionary<int, DecodedEvent> _pending = [];
    /// <summary>The events dispatched so far.</summary>
    public List<DecodedEvent> Dispatched { get; } = [];

    /// <summary>Creates an event manager for a client world.</summary>
    /// <param name="world">The client world.</param>
    public ClientEvents(ClientWorld world) => _world = world;

    /// <summary>Clears the pending events and the sequence state.</summary>
    public void Reset()
    {
        _expectedSeq = 0;
        _pending.Clear();
        Dispatched.Clear();
    }

    /// <summary>Formats a string as an escaped one line text.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The text.</returns>
    public static string Repr(string s)
    {
        var q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (var c in s)
        {
            if (c == q || c == '\\')
                sb.Append('\\').Append(c);
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\t')
                sb.Append("\\t");
            else if (c is < (char)0x20 or (char)0x7f or (>= (char)0x80 and < (char)0xa0))
                sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        sb.Append(q);
        return sb.ToString();
    }

    private static string G(double v) => ClientReader.G(v);

    // SimEvent base unpack 004fdcc4: destination address.
    private static string Dest(ClientReader r)
    {
        if (r.Flag())
            return r.Add("dest", "ghost#" + r.Bits(10));
        if (r.Flag())
            return r.Add("dest", "ghost(local)#" + r.Bits(10));
        return r.Add("dest", "obj " + r.Bits(32));
    }

    /// <summary>Reads the event block of a packet.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="into">Receives the events in stream order.</param>
    /// <remarks>Follows EventManager readPacket (00551ef8). Ordered events wait for their turn before the handler runs (docs/client-net.md 2.3). Throws Unsupported or Overrun like every reader.</remarks>
    public void Read(ClientReader r, List<DecodedEvent> into)
    {
        var prev = -4;
        while (r.Flag())
        {
            var start = r.Pos;
            string mode;
            var seq = -2;
            if (r.Flag())
            {
                if (r.Flag())
                {
                    seq = (prev + 1) & 0x7f;
                    prev = seq;
                    mode = "ordered seq=" + seq + " (implicit)";
                }
                else if (r.Flag())
                {
                    seq = (int)r.Bits(7);
                    prev = seq;
                    mode = "ordered seq=" + seq;
                }
                else
                    mode = "guaranteed unordered";
            }
            else
                mode = "unguaranteed";
            var tag = (int)r.Bits(7) + 0x400;
            var e = new DecodedEvent { Tag = tag, Mode = mode, Seq = seq };
            var save = r.Out;
            r.Out = e.Fields;
            try
            {
                Unpack(r, e);
            }
            finally
            {
                r.Out = save;
            }
            e.Bits = r.Pos - start;
            _world.Events++;
            _world.EventCounts[e.Name] = _world.EventCounts.GetValueOrDefault(e.Name) + 1;
            into.Add(e);
            Deliver(e);
        }
    }

    // Ordered events run in sequence: a sequence below the expected one is a repeat (the VC layer already dropped it)
    // and a later one waits.
    private void Deliver(DecodedEvent e)
    {
        if (e.Seq == -2)
        {
            Dispatch(e);
            return;
        }
        if (((e.Seq - _expectedSeq) & 0x7f) >= 64)
            return; // behind the window
        _pending[e.Seq] = e;
        while (_pending.TryGetValue(_expectedSeq, out var next))
        {
            _pending.Remove(_expectedSeq);
            _expectedSeq = (_expectedSeq + 1) & 0x7f;
            Dispatch(next);
        }
    }

    private void Dispatch(DecodedEvent e)
    {
        e.Apply?.Invoke();
        Dispatched.Add(e);
    }

    private void Unpack(ClientReader r, DecodedEvent e)
    {
        var pm = _world.Players;
        switch (e.Tag)
        {
            case 0x401: // SimMessageEvent
            {
                e.Name = "SimMessageEvent";
                var d = Dest(r);
                int[] widths = [1, 4, 8, 16];
                long msg = 0;
                var got = false;
                foreach (var w in widths)
                    if (r.Flag())
                    {
                        msg = r.Bits((uint)w);
                        got = true;
                        break;
                    }
                if (!got)
                    msg = r.Bits(32);
                r.Add("msg", msg);
                e.Text = d + " msg=" + msg;
                break;
            }
            case 0x408: // SimConsoleEvent
            {
                e.Name = "SimConsoleEvent";
                var n = (int)r.Bits(5);
                var args = new string[n];
                for (var i = 0; i < n; ++i)
                    args[i] = r.Str();
                r.Add("argc", (long)n);
                r.Add("args", args.Cast<object>().ToList());
                e.Text = "argc=" + n + " " + string.Join(" ", args.Select(Repr));
                e.Apply = () =>
                {
                    if (args.Length == 0)
                        return;
                    var rest = args.Skip(1).ToArray();
                    _world.RemoteCalls.Add((args[0], rest));
                    _world.OnRemoteCall?.Invoke(args[0], rest);
                };
                break;
            }
            case 0x40b: // SimActionEvent
                e.Name = "SimActionEvent";
                e.Text = Dest(r);
                break;
            case 0x40f: // Sfx::SoundEvent
            {
                e.Name = "Sfx::SoundEvent";
                var d = Dest(r);
                long kind = r.Bits(2);
                var name = r.Str();
                long id = r.Bits(32);
                r.Add("kind", kind);
                r.Add("name", name);
                r.Add("id", id);
                var s = d + " kind=" + kind + " name=" + Repr(name) + " id=" + id;
                if (r.Flag())
                {
                    double[] p = [r.SUnit(24), r.SUnit(24), r.SUnit(24)];
                    r.Add("pos", p);
                    s += " pos=(" + G(p[0]) + "," + G(p[1]) + "," + G(p[2]) + ")";
                }
                e.Text = s;
                break;
            }
            case 0x421: // Net::RemoteCreateEvent
                e.Name = "Net::RemoteCreateEvent";
                throw new UnsupportedException("RemoteCreateEvent class tag " + r.Bits(10) + ": per-class packUpdate not decoded");
            case 0x423: // SimSoundSequenceEvent
            {
                e.Name = "SimSoundSequenceEvent";
                var d = Dest(r);
                long a = r.Bits(32);
                long b = r.Bits(6);
                var s = d + " field20=" + a + " field5c=" + (b == 63 ? -1 : b);
                if (r.Flag())
                {
                    r.Bytes(0x34);
                    s += " record=52 bytes";
                }
                e.Text = s;
                break;
            }
            case 0x425: // SimPathEvent
            {
                e.Name = "SimPathEvent";
                var d = Dest(r);
                long a = r.Bits(32);
                var s = d + " field24=" + a;
                if (r.Flag())
                {
                    var f = r.Flag();
                    long n = r.Bits(32);
                    for (long i = 0; i < n; ++i)
                        r.Bytes(48);
                    s += " path flag=" + (f ? 1 : 0) + " points=" + n;
                }
                e.Text = s;
                break;
            }
            case 0x44b: // PlayerAddEvent: a copy of the client record (player-manager.md 11)
            {
                e.Name = "PlayerAddEvent";
                var rec = new ClientPlayerRecord
                {
                    Id = (int)r.Bits(32),
                    Ai = r.Flag(),
                    Name = r.Str(),
                    Skin = r.Str(),
                    Voice = r.Str(),
                    Male = r.Bits(1) != 0,
                    Commander = (int)r.Bits(32),
                    Team = r.Flag() ? (int)r.Bits(3) : -1
                };
                e.Text = "id=" + rec.Id + " ai=" + (rec.Ai ? 1 : 0) + " name=" + Repr(rec.Name) + " skin=" + Repr(rec.Skin) +
                         " voice=" + Repr(rec.Voice) + " male=" + (rec.Male ? 1 : 0) + " commander=" + rec.Commander + " team=" + rec.Team;
                r.Add("id", (long)rec.Id);
                r.Add("ai", rec.Ai ? 1L : 0L);
                r.Add("name", rec.Name);
                r.Add("skin", rec.Skin);
                r.Add("voice", rec.Voice);
                r.Add("male", rec.Male ? 1L : 0L);
                r.Add("commander", (long)rec.Commander);
                r.Add("team", (long)rec.Team);
                e.Apply = () =>
                {
                    pm.Clients[rec.Id] = rec;
                    if (!rec.Ai)
                    {
                        ++pm.HumanCount;
                        pm.Script("onClientJoin(" + rec.Id + ");");
                        pm.Message(rec.Name + " connected to the game.");
                    }
                    ++pm.PlayerCount;
                };
                break;
            }
            case 0x44c: // PlayerRemoveEvent
            {
                e.Name = "PlayerRemoveEvent";
                var id = (int)r.Bits(7) + 0x800;
                r.Add("client", (long)id);
                e.Text = "client=" + id;
                e.Apply = () =>
                {
                    var rec = pm.Get(id);
                    if (rec == null)
                        return;
                    if (!rec.Ai)
                    {
                        --pm.HumanCount;
                        pm.Script("onClientDrop(" + id + ");");
                        pm.Message(rec.Name + " dropped.");
                    }
                    --pm.PlayerCount;
                    pm.Clients.Remove(id);
                };
                break;
            }
            case 0x44d: // DeltaScoreEvent
            {
                e.Name = "DeltaScoreEvent";
                var team = r.Flag();
                var who = team ? (int)r.Bits(4) - 1 : (int)r.Bits(7) + 0x800;
                long score = r.Bits(32);
                var text = r.Str();
                r.Add("team", team ? 1L : 0L);
                r.Add("who", (long)who);
                r.Add("score", score);
                r.Add("text", text);
                e.Text = (team ? "team " : "client ") + who + " score=" + score + " text=" + Repr(text);
                e.Apply = () =>
                {
                    if (!team)
                    {
                        if (who == 2048)
                            pm.ClientHeading = text;
                        else if (pm.Get(who) is ClientPlayerRecord rec)
                        {
                            rec.ScoreText = text;
                            rec.SortValue = (int)score;
                        }
                    }
                    else if (who == -1)
                        pm.TeamHeading = text;
                    else if (pm.Teams.TryGetValue(who, out var t))
                    {
                        t.ScoreText = text;
                        t.SortValue = (int)score;
                    }
                };
                break;
            }
            case 0x44f: // TeamAddEvent
            {
                e.Name = "TeamAddEvent";
                long a = r.Bits(32);
                var tid = (int)r.Bits(32);
                string name = r.Str(), skin = r.Str();
                r.Add("team", (long)tid);
                r.Add("field24", a);
                r.Add("name", name);
                r.Add("skin", skin);
                e.Text = "team=" + tid + " field24=" + a + " name=" + Repr(name) + " skin=" + Repr(skin);
                e.Apply = () =>
                {
                    // The record goes to the slot of the old count (0040de18: slots run -1..count-1, the observer team
                    // -1 comes first), so a record's index is its team id.
                    var slot = pm.TeamCount++;
                    pm.Teams[slot] = new ClientTeam { Index = slot, Name = name, Skin = skin };
                    pm.Script("onTeamAdd(" + slot + ");");
                };
                break;
            }
            case 0x450: // MissionResetEvent
                e.Name = "MissionResetEvent";
                e.Apply = () =>
                {
                    pm.TeamCount = -1;
                    pm.WaypointTarget = -1;      // 006a83e4
                    pm.Teams.Clear();
                    foreach (var rec in pm.Clients.Values)
                    {
                        rec.Commander = -1;
                        rec.CommandStatus = 0;
                    }
                };
                break;
            case 0x452: // PlayerTeamChangeEvent
            {
                e.Name = "PlayerTeamChangeEvent";
                var id = (int)r.Bits(7) + 0x800;
                var team = (int)r.Bits(4) - 1;
                r.Add("client", (long)id);
                r.Add("team", (long)team);
                e.Text = "client=" + id + " team=" + team;
                e.Apply = () =>
                {
                    if (pm.Get(id) is not ClientPlayerRecord rec)
                        return;
                    rec.Team = team;
                    // The record takes the skin of the new team [code 00406bc4 case 7: record +0xa0 = team +0xc], so a player whose
                    // client never got a PlayerSkinEvent (the server sets no skin on a team change) shows the team colours.
                    if (team >= 0 && pm.Teams.TryGetValue(team, out var skinTeam))
                        rec.Skin = skinTeam.Skin;
                    pm.Script("onClientChangeTeam(" + id + ", " + team + ");");
                    if (team >= 0 && pm.Teams.TryGetValue(team, out var t))
                        pm.Message(rec.Name + " joined team " + t.Name + ".");
                    else
                        pm.Message(rec.Name + " is no longer on a team.");
                };
                break;
            }
            case 0x453: // PlayerSayEvent
            {
                e.Name = "PlayerSayEvent";
                long from = r.Bits(12), type = r.Bits(5);
                var msg = r.Str();
                r.Add("from", from);
                r.Add("type", type);
                r.Add("msg", msg);
                e.Text = "from=" + from + " type=" + type + " msg=" + Repr(msg);
                e.Apply = () =>
                {
                    pm.Script("onClientMessage(" + from + ", \"" + msg + "\");");
                    // no sender record (server message): the text alone, else "<name>: <text>"
                    var sender = pm.Get((int)from);
                    var shown = pm.StripVoice(msg);
                    pm.Message(sender == null ? shown : sender.Name + ": " + shown, (int)type);
                };
                break;
            }
            case 0x455: // PlayerCommandEvent
            {
                e.Name = "PlayerCommandEvent";
                var id = (int)r.Bits(10) + 0x800;
                long status = r.Bits(2);
                r.Add("client", (long)id);
                r.Add("status", status);
                var s = "client=" + id + " status=" + status;
                var issuer = -1;
                long icon = 0, obj = -1, wx = 0, wy = 0;
                var ack = r.Flag();
                if (ack)
                    s += " ack";
                else if (r.Flag())
                {
                    issuer = (int)r.Bits(10) + 0x800;
                    icon = r.Bits(6);
                    obj = r.Flag() ? r.Bits(8) : -1;
                    long x = r.Bits(10), y = r.Bits(10);
                    wx = x;
                    wy = y;
                    r.Add("issuer", (long)issuer);
                    r.Add("icon", icon);
                    r.Add("target", obj);
                    r.Add("x", x);
                    r.Add("y", y);
                    s += " issuer=" + issuer + " icon=" + icon + " target=" + obj + " x=" + x + " y=" + y;
                }
                // +0x3d: a message follows; +0x40: its text when the next flag is set (else the last text is shown again).
                var hasMsg = r.Flag();
                var text = hasMsg && r.Flag() ? r.Str() : null;
                if (text != null)
                {
                    r.Add("text", text);
                    s += " text=" + Repr(text);
                }
                e.Text = s;
                e.Apply = () =>
                {
                    // 0040d3.. case 10: the voice plays and the shown text loses its ~w tag (0040940c), the line type is 3 (green)
                    // or 4 (red) with the name of the commander or of the commanded client.
                    var local = _world.PlayerId;
                    var raw = "";
                    if (hasMsg)
                    {
                        if (text != null)
                            pm.LastCommandText = text;
                        raw = pm.LastCommandText;
                    }
                    if (pm.Get(id) is not ClientPlayerRecord rec)
                        return;
                    if (!ack)
                    {
                        var who = pm.Get(issuer);
                        if (who == null)
                            return;
                        rec.CommandStatus = (int)status;
                        rec.Commander = (int)icon;     // record +0x40
                        rec.CmdIssuer = issuer;
                        rec.CmdTarget = (int)obj;
                        (rec.CmdWaypointX, rec.CmdWaypointY) = ((int)wx, (int)wy);
                        if (id == local)
                        {
                            pm.WaypointTarget = (int)icon;       // 006a83e4, 006a83e8, 006a83ec
                            pm.SetWaypointXY(wx, wy);
                        }
                        if (hasMsg && (id == local || issuer == local))
                        {
                            pm.ChatVoice?.Invoke(who.Id, raw);
                            var shown = pm.StripVoice(raw);
                            if (id == local)
                                pm.Message(who.Name + ": " + shown, 4);
                            else
                                pm.Message("Cmd. to " + rec.Name + ": " + shown, 3);
                        }
                    }
                    else
                    {
                        var old = rec.CmdIssuer;
                        if (status == 0)
                        {
                            rec.Commander = -1;
                            rec.CmdIssuer = -1;
                            if (id == local)
                                pm.WaypointTarget = -1;
                        }
                        rec.CommandStatus = (int)status;
                        if (hasMsg && (local == old || local == id))
                        {
                            pm.ChatVoice?.Invoke(rec.Id, raw);
                            pm.Message(rec.Name + ": " + pm.StripVoice(raw), 3);
                        }
                    }
                };
                break;
            }
            case 0x456: // PlayerSelectCmdrEvent
            {
                e.Name = "PlayerSelectCmdrEvent";
                var a = (int)r.Bits(7) + 0x800;
                var b = r.Flag() ? (int)r.Bits(7) + 0x800 : 0;
                r.Add("client", (long)a);
                r.Add("commander", (long)b);
                e.Text = "client=" + a + " commander=" + b;
                e.Apply = () =>
                {
                    if (pm.Get(a) is ClientPlayerRecord rec)
                        rec.Commander = b;
                };
                break;
            }
            case 0x458: // DataBlockEvent: registered at read time, the index widths of the ghosts that follow need it
                e.Name = "DataBlockEvent";
                _world.DataBlocks.ReadEvent(r, out var summary);
                e.Text = summary;
                if (_world.DataBlocks.PreloadError is string preloadError)
                    throw new PacketErrorException(preloadError);
                break;
            case 0x459: // TeamObjectiveEvent
            {
                e.Name = "TeamObjectiveEvent";
                long index = r.Bits(32);
                var text = r.Str();
                r.Add("index", index);
                r.Add("text", text);
                e.Text = "index=" + index + " text=" + Repr(text);
                e.Apply = () =>
                {
                    pm.Objectives.Add(text);
                    if (index >= 0 && index < pm.ObjectiveLines.Length)
                        pm.ObjectiveLines[index] = text;
                };
                break;
            }
            case 0x45c: // LocSoundEvent
            {
                e.Name = "LocSoundEvent";
                var snd = (long)r.Bits(8) - 1;
                r.Add("sound", snd);
                var s = "sound=" + snd;
                if (r.Flag())
                {
                    var dir = r.Normal(8);
                    var dist = r.Unit(9);
                    r.Add("dir", dir);
                    r.Add("dist", dist);
                    s += " dir=(" + G(dir[0]) + ", " + G(dir[1]) + ", " + G(dir[2]) + ") dist=" + G(dist);
                }
                e.Text = s;
                break;
            }
            case 0x45d: // TargetNameEvent
            {
                e.Name = "TargetNameEvent";
                var index = (int)r.Bits(7);
                var name = r.Str();
                r.Add("index", (long)index);
                r.Add("name", name);
                e.Text = "index=" + index + " name=" + Repr(name);
                e.Apply = () => pm.TargetNames[index] = name;
                break;
            }
            case 0x45f: // VoiceEvent
            {
                e.Name = "VoiceEvent";
                var id = (int)r.Bits(7) + 0x800;
                var wav = r.Str();
                r.Add("client", (long)id);
                r.Add("wav", wav);
                e.Text = "client=" + id + " wav=" + Repr(wav);
                break;
            }
            case 0x460: // SoundEvent
            {
                e.Name = "SoundEvent";
                long ghost = r.Bits(10), channel = r.Bits(2), snd = (long)r.Bits(8) - 1;
                r.Add("ghost", ghost);
                r.Add("channel", channel);
                r.Add("sound", snd);
                e.Text = "ghost=" + ghost + " channel=" + channel + " sound=" + snd;
                break;
            }
            case 0x461: // PingPLEvent: the unpack writes the table (2.3)
            {
                e.Name = "PingPLEvent";
                var n = (int)r.Bits(7);
                var rows = new List<string>();
                for (var i = 0; i < n; ++i)
                {
                    var id = (int)r.Bits(7) + 0x800;
                    var ping = (int)r.Bits(8) * 4;
                    var loss = r.Unit(6);
                    rows.Add(id + ":ping=" + ping + "ms loss=" + loss.ToString("0.000", CultureInfo.InvariantCulture));
                    r.Add("row", new object[] { (long)id, (long)ping, loss });
                    if (pm.Get(id) is ClientPlayerRecord rec)
                    {
                        rec.PingMs = ping;
                        rec.Loss = loss;
                    }
                }
                r.Add("count", (long)n);
                e.Text = "count=" + n + " " + string.Join(" ", rows);
                break;
            }
            case 0x462: // PlayerSkinEvent
            {
                e.Name = "PlayerSkinEvent";
                var id = (int)r.Bits(7) + 0x800;
                var skin = r.Str();
                r.Add("client", (long)id);
                r.Add("skin", skin);
                e.Text = "client=" + id + " skin=" + Repr(skin);
                e.Apply = () =>
                {
                    if (pm.Get(id) is ClientPlayerRecord rec)
                        rec.Skin = skin;
                };
                break;
            }
            default:
                throw new UnsupportedException("event class tag 0x" + e.Tag.ToString("x", CultureInfo.InvariantCulture));
        }
    }
}
