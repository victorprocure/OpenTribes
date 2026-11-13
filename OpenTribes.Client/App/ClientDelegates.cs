// docs/gui.md 2.2: the delegates of the GUI screens (`SimGui::PlayerSetupDelegate`, `JoinGameDelegate`, ...) receive the control
// messages of their screen (vtable slot 0x74). C6 left them without handlers (gui.md Q6); this class gives the ones the join
// flow needs, derived from the stock scripts and the message tags of the `.gui` files:
//   PlayerSetup: the name field enables DONE when it holds text (140318); DONE adds the player and closes the dialog (140060).
//   JoinGame: a list row enables JOIN (140016); JOIN joins the selected server (140006); the refresh buttons, CANCEL and the query
//   behind them are ServerBrowser.
// The server list holds the servers the script gave with addGameServer() and the ones the query found (R6: loopback only by default).
using System.Globalization;
using OpenTribes.Script;
using OpenTribes.Gui;

namespace OpenTribes.Client.App;

public sealed class ClientDelegates
{
    public const int NameEdit = 140318, DoneButton = 140060, ServerList = 140016, JoinButton = 140006, RefreshAll = 140017,
        RefreshVisible = 140042, CancelQuery = 140026;

    private readonly ClientApp mApp;
    // Row id to the address the row joins.
    public readonly Dictionary<int, (string Address, string Name)> Servers = new();
    private int mNextRow = 1;
    public int NextRow() => mNextRow++;
    public ServerBrowser Browser { get; }

    public const string GameVersion = "1.11";

    public ClientDelegates(ClientApp app)
    {
        mApp = app;
        Browser = new ServerBrowser(app, this);
    }

    private static readonly string[] Names =
        { "MainMenu", "Connect", "PlayerSetup", "JoinGame", "CreateServer", "Recordings", "Options", "IRCChat", "play", "command", "quit" };

    public void Install()
    {
        foreach (string n in Names)
        {
            // 004d74f5, 004da97e, 004dbc35, 004dbedd, 004dc324: these five install the help pop-up (FGHelpCtrl) on their content.
            var d = new GuiDelegate { Name = n, ClassName = "SimGui::" + n + "Delegate", HelpPopups = n is "MainMenu" or "Connect" or "JoinGame" or "Options" or "IRCChat" };
            switch (n)
            {
                case "PlayerSetup": d.OnMessage = PlayerSetupMessage; break;
                // The play delegate is the delegate of play.gui and of command.gui (client-scripts.md 3.8): its message slot 004dd280 takes the buttons of the
                // command screen.
                case "play" or "command": d.OnMessage = (sender, tag) => mApp.Play.Message(sender, tag); break;
                case "JoinGame":
                    d.OnMessage = JoinGameMessage;
                    d.OnRemove = Browser.SaveList;
                    break;
                // The exit dialog loads quit.gui; the quit delegate ends the process when it is installed (client-app.md 6.1).
                // 004dbc00: the main menu formats "Version %s" with the game version into its text control 140503.
                case "MainMenu": d.OnOpenContent = c => c.FindById(140503)?.SetText("Version " + GameVersion); break;
                // 004dbea8: the Connect screen reads $pref::PlayGameMode (stricmp, 0062c475 "HOST", 0062c47a "TRAIN") and activates the radio of
                // that mode: id 100105 for HOST, 100998 for TRAIN, 100103 for anything else.
                case "Connect": d.OnContentReady = ActivateGameMode; break;
                case "quit": d.OnOpen = mApp.RequestQuit; break;
                // 004d9228: the Video options lists are filled when the options screen opens, before its script runs.
                case "Options": d.OnOpenContent = FillVideoOptions; break;
            }
            mApp.Canvas.Delegates[n] = d;
        }
        // addGameServer(address, name, version, ping, favorite [, neverPing]) and the 11 argument form of the saved file: adds a row to the
        // join screen's list (client-scripts.md 6.3, GameServerList.cs; command 0041e470, row 004c5320). A row with this address is replaced.
        mApp.Interp.AddCommand("addGameServer", (i, a) =>
        {
            if (a.Length < 6)
            {
                i.Print(1, "addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);");
                return "FALSE";
            }
            if (mApp.GuiRt.FindControl(ServerList.ToString(CultureInfo.InvariantCulture)) is not GuiList list)
            {
                i.Print(1, "Unable to locate the GAME server list control");
                return "FALSE";
            }
            if (list is GuiServerList browser)
            {
                if (GuiServerList.ParseScriptRow(mNextRow, a) is { } row)
                {
                    if (browser.Find(row.Address) is { } old)
                    {
                        row.Id = old.Id;
                        browser.ReplaceServer(old, row);
                    }
                    else
                    {
                        ++mNextRow;
                        browser.AddServer(row);
                    }
                    Servers[row.Id] = (a[1], a[2]);
                }
            }
            else
            {
                int id = mNextRow++;
                Servers[id] = (a[1], a[2]);
                list.AddEntry($"{a[2]}   {a[1]}   {a[3]}   {a[4]} ms", id);
            }
            return "TRUE";
        });
        // rebuildServerList() is TribesNet_queryServers (0041e4cc, 0042bb50): the LAN broadcast and the first master.
        mApp.Interp.AddCommand("rebuildServerList", (i, a) =>
        {
            Browser.QueryServers();
            return "";
        });
    }

    private void ActivateGameMode(GuiControl root)
    {
        string mode = mApp.Interp.PeekVariable("$pref::PlayGameMode");
        int id = mode.Equals("HOST", StringComparison.OrdinalIgnoreCase) ? 100105
            : mode.Equals("TRAIN", StringComparison.OrdinalIgnoreCase) ? 100998 : 100103;
        (root.FindById((uint)id) as GuiUniversalButton)?.Activate();
    }

    private void FillVideoOptions(GuiControl root)
    {
        List<(int W, int H)> modes = mApp.Win?.FullscreenModes() ?? new();
        if (modes.Count == 0)
            modes.AddRange(ClientCommandHandlers.Modes);
        VideoOptions.Fill(root, mApp.Host, mApp.Win?.Renderer, modes, mApp.Handlers.WindowedDevice, mApp.Handlers.FullscreenDevice);
        VideoOptions.AddFullscreenWindowed(root, mApp.Host, ClientApp.FullscreenWindowedPref);
    }

    private GuiControl? mSeeded;

    // The join screen runs config\GameServerList.cs when it opens (client-scripts.md 6.3, 004c712d).
    public void Poll()
    {
        GuiControl? content = mApp.Canvas.Content;
        if (content == null || content.Id != 100103 || content.FindById(ServerList) == null || ReferenceEquals(content, mSeeded))
            return;
        mSeeded = content;
        Servers.Clear();
        mApp.Interp.ExecFile("GameServerList.cs");
        Browser.OnOpen();
    }

    // Every frame: the replies and timers of the server query.
    public void Tick()
    {
        Browser.Tick();
        if (mApp.Canvas.Content is { } content)
            VideoOptions.SyncFullscreenWindowed(content);
    }

    private GuiControl? Find(int id) => mApp.GuiRt.FindControl(id.ToString(CultureInfo.InvariantCulture));

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
                    string name = Find(NameEdit)?.GetText() ?? "";
                    if (name.Length == 0)
                        return true;
                    mApp.Canvas.PopDialog(tag);
                    mApp.Interp.CallFunction("AddPlayerConfig", new[] { name });
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
                    mApp.Interp.SetVariable("$Server::Address", s.Address);
                    mApp.Interp.CallFunction("JoinGame", Array.Empty<string>());
                    return true;
                }
        }
        return Browser.Message(tag);
    }
}
