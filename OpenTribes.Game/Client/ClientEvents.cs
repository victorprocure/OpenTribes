// Client EventManager (docs/client-net.md 2.3, docs/game-stream.md 2): read of the event block (00551ef8), unpack of
// every event class and the effect of each handler on the client state. Layouts follow docs/game-stream.md 2.4, 2.5.
using System.Globalization;
using System.Text;

namespace OpenTribes.Game;

/// <summary>One event as read from the stream: the class, its delivery mode, the decoded fields and a one line text.</summary>
public sealed class DecodedEvent
{
    /// <summary>The class tag.</summary>
    public int Tag;
    /// <summary>The class name.</summary>
    public string Name = "";
    /// <summary>The delivery mode.</summary>
    public string Mode = "";
    /// <summary>The one line text.</summary>
    public string Text = "";
    /// <summary>The number of bits the event took.</summary>
    public int Bits;
    /// <summary>The ordered sequence, -2 when dispatched at once.</summary>
    public int Seq = -2;
    /// <summary>The decoded fields.</summary>
    public List<DecodedField> Fields = new();
    /// <summary>The handler effect (docs/client-net.md 2.3), run on dispatch.</summary>
    public Action? Apply;
}

/// <summary>The client EventManager: reads the event block, unpacks every event class and applies the effect of each handler to the client state.</summary>
/// <remarks>See docs/client-net.md 2.3 and docs/game-stream.md 2. Layouts follow docs/game-stream.md 2.4 and 2.5.</remarks>
public sealed class ClientEvents
{
    private readonly ClientWorld mWorld;
    private int mExpectedSeq;                                   // ordered receive (+0x64)
    private readonly SortedDictionary<int, DecodedEvent> mPending = new();
    /// <summary>The events dispatched so far.</summary>
    public readonly List<DecodedEvent> Dispatched = new();

    /// <summary>Creates an event manager for a client world.</summary>
    /// <param name="world">The client world.</param>
    public ClientEvents(ClientWorld world)
    {
        mWorld = world;
    }

    /// <summary>Clears the pending events and the sequence state.</summary>
    public void Reset()
    {
        mExpectedSeq = 0;
        mPending.Clear();
        Dispatched.Clear();
    }

    /// <summary>Formats a string as an escaped one line text.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The text.</returns>
    public static string Repr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (char c in s)
        {
            if (c == q || c == '\\')
                sb.Append('\\').Append(c);
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\t')
                sb.Append("\\t");
            else if (c < 0x20 || c == 0x7f || (c >= 0x80 && c < 0xa0))
                sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        sb.Append(q);
        return sb.ToString();
    }

    private static string G(double v) => ClientReader.G(v);
    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    // SimEvent base unpack 004fdcc4: destination address.
    private static string Dest(ClientReader r)
    {
        if (r.Flag())
            return r.Add("dest", "ghost#" + r.Int(10));
        if (r.Flag())
            return r.Add("dest", "ghost(local)#" + r.Int(10));
        return r.Add("dest", "obj " + r.Int(32));
    }

    /// <summary>Reads the event block of a packet.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="into">Receives the events in stream order.</param>
    /// <remarks>Follows EventManager readPacket (00551ef8). Ordered events wait for their turn before the handler runs (docs/client-net.md 2.3). Throws Unsupported or Overrun like every reader.</remarks>
    public void Read(ClientReader r, List<DecodedEvent> into)
    {
        int prev = -4;
        while (r.Flag())
        {
            int start = r.Pos;
            string mode;
            int seq = -2;
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
                    seq = (int)r.Int(7);
                    prev = seq;
                    mode = "ordered seq=" + seq;
                }
                else
                    mode = "guaranteed unordered";
            }
            else
                mode = "unguaranteed";
            int tag = (int)r.Int(7) + 0x400;
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
            mWorld.Events++;
            mWorld.EventCounts[e.Name] = mWorld.EventCounts.GetValueOrDefault(e.Name) + 1;
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
        if (((e.Seq - mExpectedSeq) & 0x7f) >= 64)
            return; // behind the window
        mPending[e.Seq] = e;
        while (mPending.TryGetValue(mExpectedSeq, out DecodedEvent? next))
        {
            mPending.Remove(mExpectedSeq);
            mExpectedSeq = (mExpectedSeq + 1) & 0x7f;
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
        ClientPlayerManager pm = mWorld.Players;
        switch (e.Tag)
        {
            case 0x401: // SimMessageEvent
                {
                    e.Name = "SimMessageEvent";
                    string d = Dest(r);
                    int[] widths = { 1, 4, 8, 16 };
                    long msg = 0;
                    bool got = false;
                    foreach (int w in widths)
                        if (r.Flag())
                        {
                            msg = r.Int((uint)w);
                            got = true;
                            break;
                        }
                    if (!got)
                        msg = r.Int(32);
                    r.Add("msg", msg);
                    e.Text = d + " msg=" + msg;
                    break;
                }
            case 0x408: // SimConsoleEvent
                {
                    e.Name = "SimConsoleEvent";
                    int n = (int)r.Int(5);
                    var args = new string[n];
                    for (int i = 0; i < n; ++i)
                        args[i] = r.Str();
                    r.Add("argc", (long)n);
                    r.Add("args", args.Cast<object>().ToList());
                    e.Text = "argc=" + n + " " + string.Join(" ", args.Select(Repr));
                    e.Apply = () =>
                    {
                        if (args.Length == 0)
                            return;
                        string[] rest = args.Skip(1).ToArray();
                        mWorld.RemoteCalls.Add((args[0], rest));
                        mWorld.OnRemoteCall?.Invoke(args[0], rest);
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
                    string d = Dest(r);
                    long kind = r.Int(2);
                    string name = r.Str();
                    long id = r.Int(32);
                    r.Add("kind", kind);
                    r.Add("name", name);
                    r.Add("id", id);
                    string s = d + " kind=" + kind + " name=" + Repr(name) + " id=" + id;
                    if (r.Flag())
                    {
                        double[] p = { r.SUnit(24), r.SUnit(24), r.SUnit(24) };
                        r.Add("pos", p);
                        s += " pos=(" + G(p[0]) + "," + G(p[1]) + "," + G(p[2]) + ")";
                    }
                    e.Text = s;
                    break;
                }
            case 0x421: // Net::RemoteCreateEvent
                e.Name = "Net::RemoteCreateEvent";
                throw new UnsupportedException("RemoteCreateEvent class tag " + r.Int(10) + ": per-class packUpdate not decoded");
            case 0x423: // SimSoundSequenceEvent
                {
                    e.Name = "SimSoundSequenceEvent";
                    string d = Dest(r);
                    long a = r.Int(32);
                    long b = r.Int(6);
                    string s = d + " field20=" + a + " field5c=" + (b == 63 ? -1 : b);
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
                    string d = Dest(r);
                    long a = r.Int(32);
                    string s = d + " field24=" + a;
                    if (r.Flag())
                    {
                        bool f = r.Flag();
                        long n = r.Int(32);
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
                    var rec = new ClientPlayerRecord { Id = (int)r.Int(32) };
                    rec.Ai = r.Flag();
                    rec.Name = r.Str();
                    rec.Skin = r.Str();
                    rec.Voice = r.Str();
                    rec.Male = r.Int(1) != 0;
                    rec.Commander = (int)r.Int(32);
                    rec.Team = r.Flag() ? (int)r.Int(3) : -1;
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
                    int id = (int)r.Int(7) + 0x800;
                    r.Add("client", (long)id);
                    e.Text = "client=" + id;
                    e.Apply = () =>
                    {
                        ClientPlayerRecord? rec = pm.Get(id);
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
                    bool team = r.Flag();
                    int who = team ? (int)r.Int(4) - 1 : (int)r.Int(7) + 0x800;
                    long score = r.Int(32);
                    string text = r.Str();
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
                        else if (pm.Teams.TryGetValue(who, out ClientTeam? t))
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
                    long a = r.Int(32);
                    int tid = (int)r.Int(32);
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
                        int slot = pm.TeamCount++;
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
                    foreach (ClientPlayerRecord rec in pm.Clients.Values)
                    {
                        rec.Commander = -1;
                        rec.CommandStatus = 0;
                    }
                };
                break;
            case 0x452: // PlayerTeamChangeEvent
                {
                    e.Name = "PlayerTeamChangeEvent";
                    int id = (int)r.Int(7) + 0x800;
                    int team = (int)r.Int(4) - 1;
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
                        if (team >= 0 && pm.Teams.TryGetValue(team, out ClientTeam? skinTeam))
                            rec.Skin = skinTeam.Skin;
                        pm.Script("onClientChangeTeam(" + id + ", " + team + ");");
                        if (team >= 0 && pm.Teams.TryGetValue(team, out ClientTeam? t))
                            pm.Message(rec.Name + " joined team " + t.Name + ".");
                        else
                            pm.Message(rec.Name + " is no longer on a team.");
                    };
                    break;
                }
            case 0x453: // PlayerSayEvent
                {
                    e.Name = "PlayerSayEvent";
                    long from = r.Int(12), type = r.Int(5);
                    string msg = r.Str();
                    r.Add("from", from);
                    r.Add("type", type);
                    r.Add("msg", msg);
                    e.Text = "from=" + from + " type=" + type + " msg=" + Repr(msg);
                    e.Apply = () =>
                    {
                        pm.Script("onClientMessage(" + from + ", \"" + msg + "\");");
                        // no sender record (server message): the text alone, else "<name>: <text>"
                        var sender = pm.Get((int)from);
                        string shown = pm.StripVoice(msg);
                        pm.Message(sender == null ? shown : sender.Name + ": " + shown, (int)type);
                    };
                    break;
                }
            case 0x455: // PlayerCommandEvent
                {
                    e.Name = "PlayerCommandEvent";
                    int id = (int)r.Int(10) + 0x800;
                    long status = r.Int(2);
                    r.Add("client", (long)id);
                    r.Add("status", status);
                    string s = "client=" + id + " status=" + status;
                    int issuer = -1;
                    long icon = 0, obj = -1, wx = 0, wy = 0;
                    bool ack = r.Flag();
                    if (ack)
                        s += " ack";
                    else if (r.Flag())
                    {
                        issuer = (int)r.Int(10) + 0x800;
                        icon = r.Int(6);
                        obj = r.Flag() ? r.Int(8) : -1;
                        long x = r.Int(10), y = r.Int(10);
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
                    bool hasMsg = r.Flag();
                    string? text = hasMsg && r.Flag() ? r.Str() : null;
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
                        int local = mWorld.PlayerId;
                        string raw = "";
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
                            ClientPlayerRecord? who = pm.Get(issuer);
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
                                string shown = pm.StripVoice(raw);
                                if (id == local)
                                    pm.Message(who.Name + ": " + shown, 4);
                                else
                                    pm.Message("Cmd. to " + rec.Name + ": " + shown, 3);
                            }
                        }
                        else
                        {
                            int old = rec.CmdIssuer;
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
                    int a = (int)r.Int(7) + 0x800;
                    int b = r.Flag() ? (int)r.Int(7) + 0x800 : 0;
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
                mWorld.DataBlocks.ReadEvent(r, out string summary);
                e.Text = summary;
                if (mWorld.DataBlocks.PreloadError is string preloadError)
                    throw new PacketErrorException(preloadError);
                break;
            case 0x459: // TeamObjectiveEvent
                {
                    e.Name = "TeamObjectiveEvent";
                    long index = r.Int(32);
                    string text = r.Str();
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
                    long snd = (long)r.Int(8) - 1;
                    r.Add("sound", snd);
                    string s = "sound=" + snd;
                    if (r.Flag())
                    {
                        double[] dir = r.Normal(8);
                        double dist = r.Unit(9);
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
                    int index = (int)r.Int(7);
                    string name = r.Str();
                    r.Add("index", (long)index);
                    r.Add("name", name);
                    e.Text = "index=" + index + " name=" + Repr(name);
                    e.Apply = () => pm.TargetNames[index] = name;
                    break;
                }
            case 0x45f: // VoiceEvent
                {
                    e.Name = "VoiceEvent";
                    int id = (int)r.Int(7) + 0x800;
                    string wav = r.Str();
                    r.Add("client", (long)id);
                    r.Add("wav", wav);
                    e.Text = "client=" + id + " wav=" + Repr(wav);
                    break;
                }
            case 0x460: // SoundEvent
                {
                    e.Name = "SoundEvent";
                    long ghost = r.Int(10), channel = r.Int(2), snd = (long)r.Int(8) - 1;
                    r.Add("ghost", ghost);
                    r.Add("channel", channel);
                    r.Add("sound", snd);
                    e.Text = "ghost=" + ghost + " channel=" + channel + " sound=" + snd;
                    break;
                }
            case 0x461: // PingPLEvent: the unpack writes the table (2.3)
                {
                    e.Name = "PingPLEvent";
                    int n = (int)r.Int(7);
                    var rows = new List<string>();
                    for (int i = 0; i < n; ++i)
                    {
                        int id = (int)r.Int(7) + 0x800;
                        int ping = (int)r.Int(8) * 4;
                        double loss = r.Unit(6);
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
                    int id = (int)r.Int(7) + 0x800;
                    string skin = r.Str();
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
