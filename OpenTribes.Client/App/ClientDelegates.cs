// docs/gui.md 2.2: the delegates of the GUI screens (`SimGui::PlayerSetupDelegate`, `JoinGameDelegate`, ...) receive the control
// messages of their screen (vtable slot 0x74). C6 left them without handlers (gui.md Q6); this class gives the ones the join
// flow needs, derived from the stock scripts and the message tags of the `.gui` files:
//   PlayerSetup: the name field enables DONE when it holds text (140318); DONE adds the player and closes the dialog (140060).
//   JoinGame: a list row enables JOIN (140016); JOIN joins the selected server (140006); the refresh buttons do nothing (R6).
// The server list holds only the servers the script gave with addGameServer(): the master list stays local (R6).
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

    public const string GameVersion = "1.11";

    public ClientDelegates(ClientApp app) => mApp = app;

    private static readonly string[] Names =
        { "MainMenu", "Connect", "PlayerSetup", "JoinGame", "CreateServer", "Recordings", "Options", "IRCChat", "play", "quit" };

    public void Install()
    {
        foreach (string n in Names)
        {
            var d = new GuiDelegate { Name = n, ClassName = "SimGui::" + n + "Delegate" };
            switch (n)
            {
                case "PlayerSetup": d.OnMessage = PlayerSetupMessage; break;
                case "JoinGame": d.OnMessage = JoinGameMessage; break;
                // The exit dialog loads quit.gui; the quit delegate ends the process when it is installed (client-app.md 6.1).
                // 004dbc00: the main menu formats "Version %s" with the game version into its text control 140503.
                case "MainMenu": d.OnOpenContent = c => c.FindById(140503)?.SetText("Version " + GameVersion); break;
                case "quit": d.OnOpen = mApp.RequestQuit; break;
                // 004d9228: the Video options lists are filled when the options screen opens, before its script runs.
                case "Options": d.OnOpenContent = FillVideoOptions; break;
            }
            mApp.Canvas.Delegates[n] = d;
        }
        // addGameServer(address, name, version, ping, favorite): adds a row to the join screen's list (client-scripts.md 6.3,
        // GameServerList.cs).
        mApp.Interp.AddCommand("addGameServer", (i, a) =>
        {
            if (a.Length != 6)
            {
                i.Print(1, "addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);");
                return "FALSE";
            }
            if (mApp.GuiRt.FindControl(ServerList.ToString(CultureInfo.InvariantCulture)) is not GuiList list)
            {
                i.Print(1, "Unable to locate the GAME server list control");
                return "FALSE";
            }
            int id = mNextRow++;
            Servers[id] = (a[1], a[2]);
            if (list is GuiServerList browser)
                browser.AddServer(new GuiServerList.Server(id, a[2], a[1], a[3], Numbers.BorlandAtoi(a[4]), ClientCommandHandlers.IsTrue(a[5])));
            else
                list.AddEntry($"{a[2]}   {a[1]}   {a[3]}   {a[4]} ms", id);
            return "TRUE";
        });
        mApp.Interp.AddCommand("rebuildServerList", (i, a) =>
        {
            if (mApp.GuiRt.FindControl(ServerList.ToString(CultureInfo.InvariantCulture)) is GuiList list)
            {
                list.Clear();
                Servers.Clear();
            }
            return "";
        });
    }

    private void FillVideoOptions(GuiControl root)
    {
        List<(int W, int H)> modes = mApp.Win?.FullscreenModes() ?? new();
        if (modes.Count == 0)
            modes.AddRange(ClientCommandHandlers.Modes);
        VideoOptions.Fill(root, mApp.Host, mApp.Win?.Renderer, modes, mApp.Handlers.WindowedDevice, mApp.Handlers.FullscreenDevice);
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
                if (Find(JoinButton) is GuiActive join && Find(ServerList) is GuiList l)
                    join.Active = l.Selected >= 0;
                return true;
            case JoinButton:
                {
                    if (Find(ServerList) is not GuiList list || list.Selected < 0 || !Servers.TryGetValue(list.Entries[list.Selected].Id, out var s))
                        return true;
                    mApp.Interp.SetVariable("$Server::Address", s.Address);
                    mApp.Interp.CallFunction("JoinGame", Array.Empty<string>());
                    return true;
                }
            case RefreshAll:
            case RefreshVisible:
            case CancelQuery:
                return true;
        }
        return false;
    }
}
