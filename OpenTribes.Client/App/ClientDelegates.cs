namespace OpenTribes.Client.App;

using System.Globalization;
using OpenTribes.Gui;

/// <summary>The delegates of the GUI screens (<c>SimGui::PlayerSetupDelegate</c>, <c>JoinGameDelegate</c>, ...) that receive the control messages of their screen (vtable slot 0x74).</summary>
/// <remarks>
/// <para>docs/gui.md 2.2. The handlers are the ones the join flow needs, derived from the stock scripts and the message tags of the <c>.gui</c> files.</para>
/// <list type="bullet">
/// <item><description>PlayerSetup: the name field enables DONE when it holds text (140318). DONE adds the player and closes the dialog (140060).</description></item>
/// <item><description>JoinGame: a list row enables JOIN (140016). JOIN joins the selected server (140006). The refresh buttons, CANCEL and the query behind them are <see cref="ServerBrowser"/>.</description></item>
/// </list>
/// <para>The server list holds the servers the script gave with <c>addGameServer()</c> and the ones the query found (R6: loopback only by default).</para>
/// </remarks>
public sealed class ClientDelegates
{
    /// <summary>The control id of the player name field.</summary>
    public const int NameEdit = 140318;
    /// <summary>The control id of the DONE button.</summary>
    public const int DoneButton = 140060;
    /// <summary>The control id of the server list.</summary>
    public const int ServerList = 140016;
    /// <summary>The control id of the JOIN button.</summary>
    public const int JoinButton = 140006;
    /// <summary>The control id of the refresh all button.</summary>
    public const int RefreshAll = 140017;
    /// <summary>The control id of the refresh visible button.</summary>
    public const int RefreshVisible = 140042;
    /// <summary>The control id of the cancel query button.</summary>
    public const int CancelQuery = 140026;

    private readonly ClientApp _app;
    /// <summary>Maps a row id to the address and name of the server the row joins.</summary>
    public Dictionary<int, (string Address, string Name)> Servers { get; } = [];
    private int _nextRow = 1;
    /// <summary>Hands out the next list row id.</summary>
    /// <returns>The row id.</returns>
    public int NextRow() => _nextRow++;
    /// <summary>The server query behind the join screen.</summary>
    public ServerBrowser Browser { get; }

    /// <summary>The game version the client reports.</summary>
    public const string GameVersion = "1.11";

    /// <summary>Creates the delegates.</summary>
    /// <param name="app">The client.</param>
    public ClientDelegates(ClientApp app)
    {
        _app = app;
        Browser = new ServerBrowser(app, this);
    }

    private static readonly string[] Names =
        ["MainMenu", "Connect", "PlayerSetup", "JoinGame", "CreateServer", "Recordings", "Options", "IRCChat", "play", "command", "quit"];

    /// <summary>Installs a delegate on the canvas for every screen.</summary>
    public void Install()
    {
        foreach (var n in Names)
        {
            // 004d74f5, 004da97e, 004dbc35, 004dbedd, 004dc324: these five install the help pop-up (FGHelpCtrl) on their content.
            var d = new GuiDelegate { Name = n, ClassName = "SimGui::" + n + "Delegate", HelpPopups = n is "MainMenu" or "Connect" or "JoinGame" or "Options" or "IRCChat" };
            switch (n)
            {
                case "PlayerSetup":
                    d.OnMessage = PlayerSetupMessage;
                    break;
                // The play delegate is the delegate of play.gui and of command.gui (client-scripts.md 3.8): its message slot 004dd280 takes the buttons of the
                // command screen.
                case "play" or "command":
                    d.OnMessage = _app.Play.Message;
                    break;
                case "JoinGame":
                    d.OnMessage = JoinGameMessage;
                    d.OnRemove = Browser.SaveList;
                    break;
                // The exit dialog loads quit.gui; the quit delegate ends the process when it is installed (client-app.md 6.1).
                // 004dbc00: the main menu formats "Version %s" with the game version into its text control 140503.
                case "MainMenu":
                    d.OnOpenContent = c => c.FindById(140503)?.SetText("Version " + GameVersion);
                    break;
                // 004dbea8: the Connect screen reads $pref::PlayGameMode (stricmp, 0062c475 "HOST", 0062c47a "TRAIN") and activates the radio of
                // that mode: id 100105 for HOST, 100998 for TRAIN, 100103 for anything else.
                case "Connect":
                    d.OnContentReady = ActivateGameMode;
                    break;
                case "quit":
                    d.OnOpen = _app.RequestQuit;
                    break;
                // 004d9228: the Video options lists are filled when the options screen opens, before its script runs.
                case "Options":
                    d.OnOpenContent = FillVideoOptions;
                    break;
            }
            _app.Canvas.Delegates[n] = d;
        }
        // addGameServer(address, name, version, ping, favorite [, neverPing]) and the 11 argument form of the saved file: adds a row to the
        // join screen's list (client-scripts.md 6.3, GameServerList.cs; command 0041e470, row 004c5320). A row with this address is replaced.
        _app.Interp.AddCommand("addGameServer", (i, a) =>
        {
            if (a.Length < 6)
            {
                i.Print(1, "addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);");
                return "FALSE";
            }
            if (_app.GuiRt.FindControl(ServerList.ToString(CultureInfo.InvariantCulture)) is not GuiList list)
            {
                i.Print(1, "Unable to locate the GAME server list control");
                return "FALSE";
            }
            if (list is GuiServerList browser)
            {
                if (GuiServerList.ParseScriptRow(_nextRow, a) is { } row)
                {
                    if (browser.Find(row.Address) is { } old)
                    {
                        row.Id = old.Id;
                        browser.ReplaceServer(old, row);
                    }
                    else
                    {
                        ++_nextRow;
                        browser.AddServer(row);
                    }
                    Servers[row.Id] = (a[1], a[2]);
                }
            }
            else
            {
                var id = _nextRow++;
                Servers[id] = (a[1], a[2]);
                list.AddEntry($"{a[2]}   {a[1]}   {a[3]}   {a[4]} ms", id);
            }
            return "TRUE";
        });
        // rebuildServerList() is TribesNet_queryServers (0041e4cc, 0042bb50): the LAN broadcast and the first master.
        _app.Interp.AddCommand("rebuildServerList", (i, a) =>
        {
            Browser.QueryServers();
            return "";
        });
    }

    private void ActivateGameMode(GuiControl root)
    {
        var mode = _app.Interp.PeekVariable("$pref::PlayGameMode");
        var id = mode.Equals("HOST", StringComparison.OrdinalIgnoreCase) ? 100105
            : mode.Equals("TRAIN", StringComparison.OrdinalIgnoreCase) ? 100998 : 100103;
        (root.FindById((uint)id) as GuiUniversalButton)?.Activate();
    }

    private void FillVideoOptions(GuiControl root)
    {
        var modes = _app.Win?.FullscreenModes() ?? [];
        if (modes.Count == 0)
            modes.AddRange(ClientCommandHandlers.Modes);
        VideoOptions.Fill(root, _app.Host, _app.Win?.Renderer, modes, _app.Handlers.WindowedDevice, _app.Handlers.FullscreenDevice);
        VideoOptions.AddFullscreenWindowed(root, _app.Host, ClientApp.FullscreenWindowedPref);
    }

    private GuiControl? _seeded;

    /// <summary>Runs <c>config\GameServerList.cs</c> when the join screen opens.</summary>
    /// <remarks>client-scripts.md 6.3, 004c712d.</remarks>
    public void Poll()
    {
        var content = _app.Canvas.Content;
        if (content == null || content.Id != 100103 || content.FindById(ServerList) == null || ReferenceEquals(content, _seeded))
            return;
        _seeded = content;
        Servers.Clear();
        _app.Interp.ExecFile("GameServerList.cs");
        Browser.OnOpen();
    }

    /// <summary>Handles the replies and timers of the server query. Runs every frame.</summary>
    public void Tick()
    {
        Browser.Tick();
        if (_app.Canvas.Content is { } content)
            VideoOptions.SyncFullscreenWindowed(content);
    }

    private GuiControl? Find(int id) => _app.GuiRt.FindControl(id.ToString(CultureInfo.InvariantCulture));

    private bool PlayerSetupMessage(GuiControl? sender, int tag)
    {
        switch (tag)
        {
            case NameEdit:
                if (Find(DoneButton) is GuiActive done && Find(NameEdit) is GuiControl edit)
                    done.Active = edit.GetText().Length > 0;
                return true;
            case DoneButton:
            {
                var name = Find(NameEdit)?.GetText() ?? "";
                if (name.Length == 0)
                    return true;
                _app.Canvas.PopDialog(tag);
                _app.Interp.CallFunction("AddPlayerConfig", new[] { name });
                return true;
            }
        }
        return false;
    }

    private bool JoinGameMessage(GuiControl? sender, int tag)
    {
        switch (tag)
        {
            case ServerList:
                Browser.UpdateButtons();
                return true;
            case JoinButton:
            {
                if (Find(ServerList) is not GuiList list || list.Selected < 0 || !Servers.TryGetValue(list.Entries[list.Selected].Id, out var s))
                    return true;
                _app.Interp.SetVariable("$Server::Address", s.Address);
                _app.Interp.CallFunction("JoinGame", Array.Empty<string>());
                return true;
            }
        }
        return Browser.Message(tag);
    }
}
