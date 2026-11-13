namespace OpenTribes.Client.App;

using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Net;
using OpenTribes.Script;

/// <summary>The query of the Join Server screen: what the stock exe does when the screen opens and when REFRESH ALL, REFRESH VIS or CANCEL is pressed.</summary>
/// <remarks>docs/gui.md 3.3 (server browser), client-net.md 1.7 and net-protocol.md 6.2. Follows the message handler of the delegate (004d7bb4), its screen updates 004d793c (progress), 004d7ac4 (search ended), 004d7648 (JOIN and INFO), TribesNet_queryServers (0042bb50) and the reply handlers 0042caf0 (ping), 0042c4cc (info) and 0042cb88 (failure). R6: only 127.0.0.0/8 is contacted unless <c>--allow-remote</c> is given. The broadcast then is the loopback broadcast (127.255.255.255), never the one of the LAN.</remarks>
public sealed class ServerBrowser : IDisposable
{
    /// <summary>The message tag of the M.O.T.D. button (IDDLG_MASTER_MOTD).</summary>
    public const int MotdDialogTag = 110015;
    /// <summary>The message tag of the INFO button (IDDLG_SERVER_INFO).</summary>
    public const int InfoDialogTag = 110014;
    /// <summary>The control id of the server name in ServerInfo.gui.</summary>
    public const int InfoNameId = 140500;
    /// <summary>The control id of the address.</summary>
    public const int InfoAddressId = 140501;
    /// <summary>The control id of the ping.</summary>
    public const int InfoPingId = 140502;
    /// <summary>The control id of the version.</summary>
    public const int InfoVersionId = 140503;
    /// <summary>The control id of the mod.</summary>
    public const int InfoModId = 140504;
    /// <summary>The control id of the mission.</summary>
    public const int InfoMissionId = 140505;
    /// <summary>The control id of the password flag.</summary>
    public const int InfoPasswordId = 140506;
    /// <summary>The control id of the dedicated flag.</summary>
    public const int InfoDedicatedId = 140509;
    /// <summary>The control id of the player table.</summary>
    public const int InfoTableId = 140510;
    /// <summary>The control id of the REFRESH button of the info dialog (004d8754).</summary>
    public const int InfoRefreshId = 0x2230b;
    /// <summary>The message tag of the Favorite check box of the info dialog (004d862c).</summary>
    public const int InfoFavoriteId = 0x2230c;
    /// <summary>The message tag of the Never Ping check box of the info dialog (004d86bf).</summary>
    public const int InfoNeverPingId = 0x2230d;
    /// <summary>The control id of the Favorite check box.</summary>
    public const int InfoFavoriteBoxId = 0x2230c;
    /// <summary>The control id of the Never Ping check box.</summary>
    public const int InfoNeverPingBoxId = 0x2230d;
    /// <summary>The control id of the server list.</summary>
    public const int ListId = 140016;
    /// <summary>The control id of the JOIN button.</summary>
    public const int JoinId = 140006;
    /// <summary>The control id of the INFO button.</summary>
    public const int InfoId = 140018;
    /// <summary>The control id of the REFRESH ALL button.</summary>
    public const int RefreshAllId = 140017;
    /// <summary>The control id of the REFRESH VIS button.</summary>
    public const int RefreshVisId = 140042;
    /// <summary>The control id of the CANCEL button.</summary>
    public const int CancelId = 140026;
    /// <summary>The control id of the progress box.</summary>
    public const int BoxId = 140027;
    /// <summary>The control id of the progress bar.</summary>
    public const int ProgressId = 140024;
    /// <summary>The control id of the progress text.</summary>
    public const int TextId = 140025;
    /// <summary>The control id of the text of the M.O.T.D. dialog.</summary>
    public const int MotdId = 140900;

    private readonly ClientApp _app;
    private readonly ClientDelegates _owner;
    private UdpQueryTransport? _net;
    private ServerQuery? _query;
    // 0062b210: the automatic refresh runs once per process.
    private bool _autoRefreshed;
    // net +0xe0: the servers whose info arrived in this query.
    private int _infoCount;
    // DAT_0061682c / DAT_00616828: the master being asked and how many there are.
    private int _masterIndex, _masterCount;
    // Delegate +0x60: no master answered (004d7864); REFRESH ALL clears it (004d7c1e).
    private bool _masterError;

    /// <summary>Creates the browser.</summary>
    /// <param name="app">The client.</param>
    /// <param name="owner">The delegates that own the server list.</param>
    public ServerBrowser(ClientApp app, ClientDelegates owner)
    {
        _app = app;
        _owner = owner;
    }

    /// <inheritdoc/>
    public void Dispose() => _net?.Dispose();

    private GuiServerList? List => _app.GuiRt.FindControl(ListId.ToString(CultureInfo.InvariantCulture)) as GuiServerList;
    private GuiControl? Find(int id) => _app.GuiRt.FindControl(id.ToString(CultureInfo.InvariantCulture));
    private string Var(string name) => _app.Interp.PeekVariable(name);
    private int IntVar(string name, int fallback)
    {
        var v = Var(name);
        return v.Length > 0 && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }
    private static bool IsTrue(string v) => v.Length > 0 && (v[0] is 't' or 'T' or '1');

    // The queue and its socket, made on first use with the preferences of that moment (SimNet_loadQueryPrefs reads them once).
    private ServerQuery Query()
    {
        if (_query != null)
            return _query;
        _net = new UdpQueryTransport(() => _app.Host.TickMs);
        _query = new ServerQuery(_net, () => _app.Host.TickMs)
        {
            BroadcastIp = _app.Opt.AllowRemote ? 0xffffffffu : 0x7fffffffu,
            PacketVersion = (ushort)Numbers.BorlandAtoi(ClientDelegates.GameVersion),
            MayContact = a => _app.Opt.AllowRemote || (a.Ip >> 24) == 127,
            Prefs = new ServerQueryPrefs
            {
                MaxPings = IntVar("$pref::maxConcurrentPings", 20),
                PingTimeoutMs = IntVar("$pref::pingTimeoutTime", 1500),
                PingRetries = IntVar("$pref::pingRetryCount", 2),
                MaxRequests = IntVar("$pref::maxConcurrentRequests", 10),
                RequestTimeoutMs = IntVar("$pref::requestTimeoutTime", 2000),
                RequestRetries = IntVar("$pref::requestRetryCount", 2),
            },
            OnPing = OnPing,
            OnInfo = OnInfo,
            OnFail = OnFail,
            OnMaster = OnMaster
        };
        return _query;
    }

    /// <summary>Handles the replies and the timers. Runs once per frame.</summary>
    public void Tick() => _query?.Tick();

    /// <summary>Starts the screen state after the screen opened (004d749c, after the list ran <c>config\GameServerList.cs</c>).</summary>
    public void OnOpen()
    {
        // 004c702e (the list's wake): failures stay, nothing is re-asked, no REFRESH ALL has run.
        List?.ResetSearchFlags();
        UpdateButtons();
        UpdateMotd();
        if (Var("$pref::AutoRefresh").Length == 0)
            _app.Interp.SetVariable("$pref::AutoRefresh", "TRUE");
        if (string.Equals(Var("$pref::AutoRefresh"), "FALSE", StringComparison.OrdinalIgnoreCase))
            _autoRefreshed = true;
        else if (!_autoRefreshed)
        {
            RefreshAll();
            _autoRefreshed = true;
        }
    }

    /// <summary>Handles the messages of the three buttons (004d7bb4).</summary>
    /// <param name="tag">The message tag.</param>
    /// <returns>True when the message was handled.</returns>
    public bool Message(int tag)
    {
        switch (tag)
        {
            case InfoDialogTag:
                return OpenInfo();
            case MotdDialogTag:
                return OpenMotd();
            case RefreshAllId:
                RefreshAll();
                return true;
            case RefreshVisId:
                RefreshVisible();
                return true;
            case CancelId:
                Cancel();
                return true;
            case InfoRefreshId:
                RefreshSelected();
                return true;
            case InfoFavoriteId:
                SetSelectedFlag(favorite: true);
                return true;
            case InfoNeverPingId:
                SetSelectedFlag(favorite: false);
                return true;
            default:
                break;
        }
        return false;
    }

    /// <summary>Runs REFRESH ALL (0x222f1): the console function <c>rebuildServerList()</c> (queryServers), every row marked unknown, the buttons off, the box on.</summary>
    public void RefreshAll()
    {
        _masterError = false;
        QueryServers();
        List?.MarkAllUnknown();
        StartUi(IsTrue(Var("$pref::lanOnly")) || IntVar("$Server::numMasters", 0) == 0 ? "Searching for LAN servers" : "Querying master servers");
    }

    // 0x2230a: every row is pinged again (004c77a8); nothing happens without rows.
    private void RefreshVisible()
    {
        var list = List;
        if (list == null || list.Servers.Count == 0)
            return;
        var q = Query();
        foreach (var s in list.Servers.ToList())
        {
            q.Queue(s.Address);
            s.State = GuiServerList.StateQuerying;
        }
        list.KeepFailed = true;
        list.Requery = false;
        StartUi("Pinging visible servers");
    }

    /// <summary>Builds the text of the M.O.T.D. dialog (004d7fbf, IDDLG_MASTER_MOTD).</summary>
    /// <param name="name">The name of the master, <c>$pref::MSName</c>.</param>
    /// <param name="motd">The message, <c>$pref::MSMOTD</c>.</param>
    /// <returns>The text of the dialog <c>gui\\MasterMOTD.gui</c> control 140900: <c>&lt;f1&gt;Message of the day from: name&lt;f0&gt;</c> and two line feeds, then the message, the whole text cut to 1023 characters.</returns>
    /// <remarks>$pref::MSprevMOTD becomes the message that was shown and the button state is worked out again (004d7770).</remarks>
    public static string MotdText(string name, string motd)
    {
        var text = "<f1>Message of the day from: " + name + "<f0>\n\n";
        return text + motd[..Math.Min(motd.Length, Math.Max(0, 1023 - text.Length))];
    }

    private bool OpenMotd()
    {
        var name = Var("$pref::MSName");
        var motd = Var("$pref::MSMOTD");
        _app.Interp.Evaluate("GuiPushDialog(MainWindow, \"gui\\\\MasterMOTD.gui\");");
        _app.GuiRt.Canvas.Top?.FindById(MotdId)?.SetText(MotdText(name, motd));
        _app.Interp.SetVariable("$pref::MSprevMOTD", motd);
        UpdateMotd();
        return true;
    }

    // INFO: the dialog opens over the selected server and shows what its info reply gave.
    private bool OpenInfo()
    {
        var list = List;
        if (list == null || list.Selected < 0 || list.Selected >= list.Servers.Count || list.Servers[list.Selected].State != GuiServerList.StateInfo)
            return true;
        var row = list.Servers[list.Selected];
        _app.Interp.Evaluate("GuiPushDialog(MainWindow, \"gui\\\\ServerInfo.gui\");");
        FillInfoDialog(row);
        ShowFlags(row);
        return true;
    }

    /// <summary>Shows the flags of a row in the two check boxes (004d8532 and 004d856f). Only when the dialog opens: an info reply does not touch them.</summary>
    /// <param name="row">The server row.</param>
    public void ShowFlags(GuiServerList.Server row)
    {
        if (_app.GuiRt.FindControl(InfoFavoriteBoxId.ToString(CultureInfo.InvariantCulture)) is GuiUniversalButton fav)
            fav.On = row.Favorite;
        if (_app.GuiRt.FindControl(InfoNeverPingBoxId.ToString(CultureInfo.InvariantCulture)) is GuiUniversalButton never)
            never.On = row.NeverPing;
    }

    /// <summary>The number of pings not answered yet.</summary>
    public int PendingPings => _query?.PendingPings ?? 0;
    /// <summary>The addresses seen by the query.</summary>
    public IReadOnlyList<string> SeenAddresses => _query?.Seen ?? [];

    // 004d8754: the dialog's REFRESH asks the selected server again (SimNet_queueServerQuery 0054fff0); it needs a selected row that has its
    // info and a query queue. The row keeps its state and flags until the answers come.
    private void RefreshSelected()
    {
        var list = List;
        if (list == null || list.Selected < 0 || list.Selected >= list.Servers.Count || list.Servers[list.Selected].State != GuiServerList.StateInfo)
            return;
        Query().Queue(list.Servers[list.Selected].Address);
    }

    // 004d862c and 004d86bf: the Favorite and Never Ping buttons write their state into the selected row (004c52b4 sets +0x241, 004c51f0 sets
    // +0x244, found again by address). Both need a selected row that has its info. The file is written when the search ends or the screen closes.
    private void SetSelectedFlag(bool favorite)
    {
        var list = List;
        if (list == null || list.Selected < 0 || list.Selected >= list.Servers.Count || list.Servers[list.Selected].State != GuiServerList.StateInfo)
            return;
        if (Find(favorite ? InfoFavoriteBoxId : InfoNeverPingBoxId) is not GuiUniversalButton box)
            return;
        var address = list.Servers[list.Selected].Address;
        if (favorite)
            list.SetFavorite(address, box.On);
        else
            list.SetNeverPing(address, box.On);
    }

    /// <summary>Saves <c>config\GameServerList.cs</c> from the rows (004c4e2c). Called when the screen closes (004d7630) and when a search ends (004c7820).</summary>
    /// <param name="content">The control that holds the list, or null for the current content.</param>
    public void SaveList(GuiControl? content = null)
    {
        var list = content?.FindById(ListId) as GuiServerList ?? List;
        if (list == null)
            return;
        var path = Builtins.ResolveGamePath(_app.Opt.Root, "config\\GameServerList.cs");
        if (path.Length == 0)
            return;
        try
        {
            File.WriteAllBytes(path, ByteString.ToBytes(list.BuildServerFile()));
        }
        catch (Exception)
        {
            // A stream that cannot open writes nothing.
        }
    }

    // 004c5f24 and 004c91f8: the dialog's texts and the score table (ServerInfoCtrl) from a server record.
    private void FillInfoDialog(GuiServerList.Server row)
    {
        var inv = CultureInfo.InvariantCulture;
        Find(InfoNameId)?.SetText(row.Name);
        Find(InfoAddressId)?.SetText(row.Address);
        Find(InfoVersionId)?.SetText(row.Version);
        Find(InfoPingId)?.SetText(row.Ping.ToString(inv));
        Find(InfoModId)?.SetText(row.ModList);
        Find(InfoMissionId)?.SetText(row.Mission);
        Find(InfoPasswordId)?.SetText(row.Password ? "YES" : "NO");
        Find(InfoDedicatedId)?.SetText(row.Dedicated ? "YES" : "NO");
        _app.GuiRt.FindControl("ServerInfoText")?.SetValue(row.Info);
        (Find(InfoTableId) as GuiServerInfo)?.SetServer(row.TeamHeading, row.ClientHeading, row.Rows);
    }

    // 0x222fa: both queues emptied, the rows still being queried become unknown, the search is over.
    private void Cancel()
    {
        _query?.CancelAll();
        var list = List;
        if (list != null)
        {
            list.KeepFailed = true;
            list.Requery = false;
            foreach (var s in list.Servers)
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

    /// <summary>Runs queryServers (0042bb50, TribesNet_queryServers): the LAN broadcast on the game port, then the first master unless lanOnly is set.</summary>
    public void QueryServers()
    {
        var q = Query();
        _infoCount = 0;
        q.ClearSeen();
        // 004c525c: the never-ping rows count as answered, a broadcast or master reply for one is not queried.
        if (List is { } list)
            foreach (var s in list.Servers)
                if (s.NeverPing && QueryAddress.TryResolve(s.Address, 0, out var canon, out _, out _, out _))
                    q.AddSeen(canon);
        var port = IntVar("$Server::Port", 0).ToString(CultureInfo.InvariantCulture);
        q.Queue("IP:BROADCAST:" + port);
        q.Queue("IPX:BROADCAST:" + port);
        if (!IsTrue(Var("$pref::lanOnly")))
        {
            if (_app.Interp.HasFunction("checkMasterTranslation"))
                _app.Interp.CallFunction("checkMasterTranslation", []);
            _masterCount = IntVar("$Server::numMasters", 0);
            _masterIndex = 0;
            if (_masterCount != 0 && Var("$Server::XLMasterN0") is { Length: > 0 } m)
                q.Queue(m);
        }
    }

    // 0042caf0: a ping answered. The row is added or updated (004c5c0c); a server of this version gets an info request.
    private void OnPing(string address, string name, int ping, ushort packetVersion)
    {
        var list = List;
        if (list != null)
        {
            var row = list.Find(address);
            if (row == null)
            {
                row = new GuiServerList.Server(_owner.NextRow(), name, address, packetVersion.ToString(CultureInfo.InvariantCulture), ping, false);
                _owner.Servers[row.Id] = (ToJoinAddress(address), name);
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
        ++_infoCount;
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
            row.TeamHeading = r.TeamHeading;
            row.ClientHeading = r.ClientHeading;
            row.Rows = [.. r.Rows];
            row.Cpu = r.Cpu;
            row.Password = r.Password;
            row.Dedicated = r.Dedicated;
            row.State = GuiServerList.StateInfo;
            _owner.Servers[row.Id] = (ToJoinAddress(row.Address), r.Hostname);
            // 004c5f24: the open info dialog shows this server: its fields follow the reply.
            if (Find(InfoAddressId) is { } shown && string.Equals(shown.GetText(), row.Address, StringComparison.OrdinalIgnoreCase))
                FillInfoDialog(row);
        }
        var requests = Query().PendingRequests;
        if (requests > 0)
            UpdateProgress(requests, _infoCount, false);
        else
            End();
        UpdateButtons();
    }

    // 0042ca00: a master answered. Part 1 with a name and a message sets $pref::MSName and $pref::MSMOTD; the progress shows the pings left and
    // every row that is still being pinged shows as querying.
    private void OnMaster(string address, string name, bool first, string motd)
    {
        if (first)
        {
            if (name.Length == 0 || motd.Length == 0)
                return;
            _app.Interp.SetVariable("$pref::MSName", name);
            _app.Interp.SetVariable("$pref::MSMOTD", motd);
            UpdateMotd();
        }
        UpdateProgress(Query().PendingPings, Query().PendingRequests, true);
        if (List is { } list)
            foreach (var a in Query().PendingPingAddresses)
                if (list.Find(a) is { } row)
                    row.State = GuiServerList.StateQuerying;
    }

    // 0042cb88: a ping or an info request gave up, or the server speaks another version.
    private void OnFail(string address)
    {
        var list = List;
        if (list?.Find(address) is { } row)
        {
            row.State = GuiServerList.StateTimeout;
            // 004c5054: a row that failed goes unless it is a favorite, a never-ping row or the refresh keeps them.
            if (!list.KeepFailed && !row.Favorite && !row.NeverPing)
            {
                list.RemoveServer(row);
                _owner.Servers.Remove(row.Id);
            }
        }
        // 0042cb88: the current master failed, the next one is asked, with none left the error dialog opens.
        if (_masterIndex < _masterCount && string.Equals(NormalAddress(Var("$Server::XLMasterN" + _masterIndex.ToString(CultureInfo.InvariantCulture))), address, StringComparison.OrdinalIgnoreCase))
        {
            var queued = false;
            while (++_masterIndex < _masterCount)
            {
                var next = Var("$Server::XLMasterN" + _masterIndex.ToString(CultureInfo.InvariantCulture));
                if (next.Length == 0)
                    continue;
                Query().Queue(next);
                queued = true;
                break;
            }
            if (!queued)
                MasterError();
        }
        var q = Query();
        if (q.PendingPings < 1)
        {
            if (q.PendingRequests < 2)
                End();
            else
                UpdateProgress(q.PendingRequests - 1, _infoCount, false);
        }
        else
            UpdateProgress(q.PendingPings, q.PendingRequests, true);
    }

    // 004d7864: no master answered.
    private void MasterError()
    {
        _masterError = true;
        _app.Interp.Evaluate("GuiPushDialog(MainWindow, \"gui\\\\MessageDialog.gui\"); Control::setValue(MessageDialogTextFormat, \"Internet Connection Error\\nCould not contact any master server.  Please verify your internet connection.\");");
    }

    private static string NormalAddress(string text) =>
        QueryAddress.TryResolve(text, 0, out var c, out _, out _, out _) ? c : text;

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
        var f = pings + requests == 0 ? 1.0f : requests / ((pings + requests) * 2.0f);
        if (!pinging)
            f += 0.5f;
        Find(ProgressId)?.SetValue(f.ToString("R", CultureInfo.InvariantCulture));
        Find(TextId)?.SetText("Servers left to " + (pinging ? "ping" : "query") + ": " + pings.ToString(CultureInfo.InvariantCulture));
    }

    // 004d7ac4: the search is over: the box goes, REFRESH ALL and REFRESH VIS work again.
    private void End()
    {
        // 004d7ac4 -> 004c7820: rows that never answered go, favorites are asked directly, then the file is written.
        if (List is { } list)
        {
            foreach (var s in list.EndSearch(_masterError))
                Query().Queue(s.Address);
            SaveList();
        }
        Find(BoxId)?.SetVisible(false);
        if (Find(RefreshAllId) is GuiActive a)
            a.Active = true;
        if (Find(RefreshVisId) is GuiActive v)
            v.Active = true;
        UpdateButtons();
    }

    /// <summary>Sets the buttons of the screen (004d7648): JOIN needs a selected row, INFO one that has its info.</summary>
    public void UpdateButtons()
    {
        var list = List;
        var sel = list != null && list.Selected >= 0 && list.Selected < list.Servers.Count;
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
