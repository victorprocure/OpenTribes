// The Join Server screen's query (docs/gui.md 3.3 server browser, client-net.md 1.7, net-protocol.md 6.2): what the stock exe does when the
// screen opens and when REFRESH ALL, REFRESH VIS or CANCEL is pressed. Code: the delegate's message handler 004d7bb4, its screen
// updates 004d793c (progress), 004d7ac4 (search ended), 004d7648 (JOIN and INFO), TribesNet_queryServers 0042bb50 and the reply handlers
// 0042caf0 (ping), 0042c4cc (info), 0042cb88 (failure). R6: only 127.0.0.0/8 is contacted unless --allow-remote is given; the
// broadcast then is the loopback broadcast (127.255.255.255), never the LAN's.
using System.Globalization;
using OpenTribes.Gui;
using OpenTribes.Net;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class ServerBrowser : IDisposable
{
    public const int ListId = 140016, JoinId = 140006, InfoId = 140018, RefreshAllId = 140017, RefreshVisId = 140042, CancelId = 140026,
        BoxId = 140027, ProgressId = 140024, TextId = 140025, MotdId = 140900;

    private readonly ClientApp mApp;
    private readonly ClientDelegates mOwner;
    private UdpQueryTransport? mNet;
    private ServerQuery? mQuery;
    // 0062b210: the automatic refresh runs once per process.
    private bool mAutoRefreshed;
    // net +0xe0: the servers whose info arrived in this query.
    private int mInfoCount;
    // DAT_0061682c / DAT_00616828: the master being asked and how many there are.
    private int mMasterIndex, mMasterCount;

    public ServerBrowser(ClientApp app, ClientDelegates owner)
    {
        mApp = app;
        mOwner = owner;
    }

    public void Dispose() => mNet?.Dispose();

    private GuiServerList? List => mApp.GuiRt.FindControl(ListId.ToString(CultureInfo.InvariantCulture)) as GuiServerList;
    private GuiControl? Find(int id) => mApp.GuiRt.FindControl(id.ToString(CultureInfo.InvariantCulture));
    private string Var(string name) => mApp.Interp.PeekVariable(name);
    private int IntVar(string name, int fallback)
    {
        string v = Var(name);
        return v.Length > 0 && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
    }
    private static bool IsTrue(string v) => v.Length > 0 && (v[0] is 't' or 'T' or '1');

    // The queue and its socket, made on first use with the preferences of that moment (SimNet_loadQueryPrefs reads them once).
    private ServerQuery Query()
    {
        if (mQuery != null)
            return mQuery;
        mNet = new UdpQueryTransport(() => mApp.Host.TickMs);
        mQuery = new ServerQuery(mNet, () => mApp.Host.TickMs)
        {
            BroadcastIp = mApp.Opt.AllowRemote ? 0xffffffffu : 0x7fffffffu,
            PacketVersion = (ushort)Numbers.BorlandAtoi(ClientDelegates.GameVersion),
            MayContact = a => mApp.Opt.AllowRemote || (a.Ip >> 24) == 127,
            Prefs = new ServerQueryPrefs
            {
                MaxPings = IntVar("$pref::maxConcurrentPings", 20), PingTimeoutMs = IntVar("$pref::pingTimeoutTime", 1500),
                PingRetries = IntVar("$pref::pingRetryCount", 2), MaxRequests = IntVar("$pref::maxConcurrentRequests", 10),
                RequestTimeoutMs = IntVar("$pref::requestTimeoutTime", 2000), RequestRetries = IntVar("$pref::requestRetryCount", 2),
            },
        };
        mQuery.OnPing = OnPing;
        mQuery.OnInfo = OnInfo;
        mQuery.OnFail = OnFail;
        return mQuery;
    }

    // Once per frame: the replies and the timers.
    public void Tick() => mQuery?.Tick();

    // The screen has just opened (004d749c after the list's own open ran config\GameServerList.cs).
    public void OnOpen()
    {
        UpdateButtons();
        UpdateMotd();
        if (Var("$pref::AutoRefresh").Length == 0)
            mApp.Interp.SetVariable("$pref::AutoRefresh", "TRUE");
        if (string.Equals(Var("$pref::AutoRefresh"), "FALSE", StringComparison.OrdinalIgnoreCase))
            mAutoRefreshed = true;
        else if (!mAutoRefreshed)
        {
            RefreshAll();
            mAutoRefreshed = true;
        }
    }

    // The messages of the three buttons (004d7bb4). True when handled.
    public bool Message(int tag)
    {
        switch (tag)
        {
            case RefreshAllId:
                RefreshAll();
                return true;
            case RefreshVisId:
                RefreshVisible();
                return true;
            case CancelId:
                Cancel();
                return true;
        }
        return false;
    }

    // 0x222f1: the console function rebuildServerList() (queryServers), every row marked unknown, the buttons off, the box on.
    public void RefreshAll()
    {
        QueryServers();
        List?.MarkAllUnknown();
        StartUi(IsTrue(Var("$pref::lanOnly")) || IntVar("$Server::numMasters", 0) == 0 ? "Searching for LAN servers" : "Querying master servers");
    }

    // 0x2230a: every row is pinged again (004c77a8); nothing happens without rows.
    private void RefreshVisible()
    {
        GuiServerList? list = List;
        if (list == null || list.Servers.Count == 0)
            return;
        ServerQuery q = Query();
        foreach (GuiServerList.Server s in list.Servers.ToList())
        {
            q.Queue(s.Address);
            s.State = GuiServerList.StateQuerying;
        }
        list.KeepFailed = true;
        StartUi("Pinging visible servers");
    }

    // 0x222fa: both queues emptied, the rows still being queried become unknown, the search is over.
    private void Cancel()
    {
        mQuery?.CancelAll();
        GuiServerList? list = List;
        if (list != null)
        {
            list.KeepFailed = true;
            foreach (GuiServerList.Server s in list.Servers)
                if (s.State == GuiServerList.StateQuerying)
                    s.State = GuiServerList.StateUnknown;
        }
        End();
    }

    private void StartUi(string text)
    {
        if (Find(RefreshAllId) is GuiActive a)
            a.Active = false;
        if (Find(RefreshVisId) is GuiActive v)
            v.Active = false;
        Find(BoxId)?.SetVisible(true);
        Find(ProgressId)?.SetValue("0");
        Find(TextId)?.SetText(text);
    }

    // 0042bb50 (TribesNet_queryServers): the LAN broadcast on the game port, then the first master unless lanOnly is set.
    public void QueryServers()
    {
        ServerQuery q = Query();
        mInfoCount = 0;
        q.ClearSeen();
        // 004c525c: the favorites count as answered, a broadcast reply from one is not queried again.
        if (List is { } list)
            foreach (GuiServerList.Server s in list.Servers)
                if (s.Favorite && QueryAddress.TryResolve(s.Address, 0, out string canon, out _, out _, out _))
                    q.AddSeen(canon);
        string port = IntVar("$Server::Port", 0).ToString(CultureInfo.InvariantCulture);
        q.Queue("IP:BROADCAST:" + port);
        q.Queue("IPX:BROADCAST:" + port);
        if (!IsTrue(Var("$pref::lanOnly")))
        {
            if (mApp.Interp.HasFunction("checkMasterTranslation"))
                mApp.Interp.CallFunction("checkMasterTranslation", Array.Empty<string>());
            mMasterCount = IntVar("$Server::numMasters", 0);
            mMasterIndex = 0;
            if (mMasterCount != 0 && Var("$Server::XLMasterN0") is { Length: > 0 } m)
                q.Queue(m);
        }
    }

    // 0042caf0: a ping answered. The row is added or updated (004c5c0c); a server of this version gets an info request.
    private void OnPing(string address, string name, int ping, ushort packetVersion)
    {
        GuiServerList? list = List;
        if (list != null)
        {
            GuiServerList.Server? row = list.Find(address);
            if (row == null)
            {
                row = new GuiServerList.Server(mOwner.NextRow(), name, address, packetVersion.ToString(CultureInfo.InvariantCulture), ping, false);
                mOwner.Servers[row.Id] = (ToJoinAddress(address), name);
                list.AddServer(row);
            }
            else
            {
                row.Name = name;
                row.Ping = ping;
            }
            row.State = packetVersion == Query().PacketVersion ? GuiServerList.StatePinged : GuiServerList.StateInfo;
            if (row.State == GuiServerList.StateInfo)
                row.Password = row.Dedicated = false;
        }
        if (packetVersion == Query().PacketVersion)
            Query().Request(address, ping);
        UpdateProgress(Query().PendingPings, Query().PendingRequests, true);
    }

    // 0042c4cc: the info of a server. Its row takes the fields and shows the final state.
    private void OnInfo(ServerInfoRecord r)
    {
        ++mInfoCount;
        if (List?.Find(r.Address) is { } row)
        {
            row.Name = r.Hostname;
            row.Version = r.Version;
            row.Players = r.Players;
            row.MaxPlayers = r.MaxPlayers;
            row.Mission = r.Mission;
            row.MissionType = r.MissionType;
            row.ModList = r.ModList;
            row.Info = r.Info;
            row.Cpu = r.Cpu;
            row.Password = r.Password;
            row.Dedicated = r.Dedicated;
            row.State = GuiServerList.StateInfo;
            mOwner.Servers[row.Id] = (ToJoinAddress(row.Address), r.Hostname);
        }
        int requests = Query().PendingRequests;
        if (requests > 0)
            UpdateProgress(requests, mInfoCount, false);
        else
            End();
        UpdateButtons();
    }

    // 0042cb88: a ping or an info request gave up, or the server speaks another version.
    private void OnFail(string address)
    {
        GuiServerList? list = List;
        if (list?.Find(address) is { } row)
        {
            row.State = GuiServerList.StateTimeout;
            // 004c5054: a row that failed goes unless it is a favorite or the refresh keeps them.
            if (!list.KeepFailed && !row.Favorite)
            {
                list.RemoveServer(row);
                mOwner.Servers.Remove(row.Id);
            }
        }
        // 0042cb88: the current master failed, the next one is asked, with none left the error dialog opens.
        if (mMasterIndex < mMasterCount && string.Equals(NormalAddress(Var("$Server::XLMasterN" + mMasterIndex.ToString(CultureInfo.InvariantCulture))), address, StringComparison.OrdinalIgnoreCase))
        {
            bool queued = false;
            while (++mMasterIndex < mMasterCount)
            {
                string next = Var("$Server::XLMasterN" + mMasterIndex.ToString(CultureInfo.InvariantCulture));
                if (next.Length == 0)
                    continue;
                Query().Queue(next);
                queued = true;
                break;
            }
            if (!queued)
                MasterError();
        }
        ServerQuery q = Query();
        if (q.PendingPings < 1)
        {
            if (q.PendingRequests < 2)
                End();
            else
                UpdateProgress(q.PendingRequests - 1, mInfoCount, false);
        }
        else
            UpdateProgress(q.PendingPings, q.PendingRequests, true);
    }

    // 004d7864: no master answered.
    private void MasterError()
    {
        mApp.Interp.Evaluate("GuiPushDialog(MainWindow, \"gui\\\\MessageDialog.gui\"); Control::setValue(MessageDialogTextFormat, \"Internet Connection Error\\nCould not contact any master server.  Please verify your internet connection.\");");
    }

    private static string NormalAddress(string text) =>
        QueryAddress.TryResolve(text, 0, out string c, out _, out _, out _) ? c : text;

    // A row's join address: the text the JoinGame script takes ("IP:a.b.c.d:port").
    private static string ToJoinAddress(string canonical) => canonical;

    // 004d793c: nothing left ends the search, else the bar and the text.
    private void UpdateProgress(int pings, int requests, bool pinging)
    {
        if (pings == 0 && requests == 0)
        {
            End();
            return;
        }
        float f = pings + requests == 0 ? 1.0f : (float)requests / ((float)(pings + requests) * 2.0f);
        if (!pinging)
            f += 0.5f;
        Find(ProgressId)?.SetValue(f.ToString("R", CultureInfo.InvariantCulture));
        Find(TextId)?.SetText("Servers left to " + (pinging ? "ping" : "query") + ": " + pings.ToString(CultureInfo.InvariantCulture));
    }

    // 004d7ac4: the search is over: the box goes, REFRESH ALL and REFRESH VIS work again.
    private void End()
    {
        Find(BoxId)?.SetVisible(false);
        if (Find(RefreshAllId) is GuiActive a)
            a.Active = true;
        if (Find(RefreshVisId) is GuiActive v)
            v.Active = true;
        UpdateButtons();
    }

    // 004d7648: JOIN needs a selected row, INFO one that has its info.
    public void UpdateButtons()
    {
        GuiServerList? list = List;
        bool sel = list != null && list.Selected >= 0 && list.Selected < list.Servers.Count;
        if (Find(JoinId) is GuiActive join)
            join.Active = sel;
        if (Find(InfoId) is GuiActive info)
            info.Active = sel && list!.Servers[list.Selected].State == GuiServerList.StateInfo;
    }

    // 004d7770: M.O.T.D. works when a message of the day is known; the LAN game mode never has one.
    private void UpdateMotd()
    {
        if (Find(MotdId) is not GuiActive motd)
            return;
        motd.Active = Var("$pref::MSMOTD").Length > 0 && !string.Equals(Var("$Pref::JoinGameMode"), "LAN", StringComparison.OrdinalIgnoreCase);
    }
}
