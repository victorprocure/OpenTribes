namespace OpenTribes.Client.App;

using OpenTribes.Gui;
using OpenTribes.Script;

/// <summary>The play delegate: it owns <c>actionMap.sae</c>, <c>playMap.sae</c> and <c>pdaMap.sae</c>, pushes and pops them when the canvas content control changes (PlayDelegate_setContentControl 004dcc4c), receives their actions and handles the ones that are not move input (chat, score page, escape), and switches the GUI with the PSC GUI mode (PlayDelegate_setGuiMode 004dc84c).</summary>
/// <remarks>docs/client-scripts.md 3.8 and 3.10.</remarks>
public sealed class PlayDelegate : IActionConsumer
{
    /// <summary>The control id of the play screen.</summary>
    public const uint PlayGuiId = 140114;
    /// <summary>The control id of the command screen.</summary>
    public const uint CommandGuiId = 140200;
    /// <summary>The control id of the chat entry.</summary>
    public const uint ChatEntryId = 140112;
    /// <summary>The control id of the chat display.</summary>
    public const uint ChatDisplayId = 140113;
    /// <summary>The control id of the HUD list.</summary>
    public const uint HudListId = 140220;
    /// <summary>The action id of the chat key.</summary>
    public const int Chat = 220200;
    /// <summary>The action id that sizes the chat display.</summary>
    public const int ChatDispSize = 220201;
    /// <summary>The action id that pages the chat display.</summary>
    public const int ChatDispPage = 220202;
    /// <summary>The action id that pages the menu.</summary>
    public const int MenuPage = 220203;
    /// <summary>The action id of the escape key.</summary>
    public const int EscapePressed = 220204;
    /// <summary>The action id of zoom in on the command map.</summary>
    public const int ZoomIn = 220207;
    /// <summary>The action id of zoom out on the command map.</summary>
    public const int ZoomOut = 220208;
    /// <summary>The action id that turns the zoom mode on.</summary>
    public const int ZoomModeOn = 220210;
    /// <summary>The action id that turns the zoom mode off.</summary>
    public const int ZoomModeOff = 220211;
    /// <summary>The action id that turns the center mode on.</summary>
    public const int CenterModeOn = 220219;
    /// <summary>The action id that turns the center mode off.</summary>
    public const int CenterModeOff = 220220;
    /// <summary>The control id of the command map.</summary>
    public const uint CommanderMapId = 140201;
    /// <summary>The control id of the zoom button.</summary>
    public const uint ZoomButtonId = 140211;
    /// <summary>The control id of the center button.</summary>
    public const uint CenterButtonId = 140216;

    private readonly ClientApp _app;
    /// <summary>The move builder that turns the actions of the play maps into moves.</summary>
    public MoveBuilder Moves { get; } = new();
    private ActionMap? _action, _play, _pda;
    private uint _lastContentId;
    private bool _scoresOn;
    private readonly ChatSizeMemory _chatSizes = new();
    /// <inheritdoc/>
    public bool Removing => false;
    /// <summary>The number of action map pushes.</summary>
    public int MapPushes { get; set; }
    /// <summary>The number of action map pops.</summary>
    public int MapPops { get; set; }

    /// <summary>Creates the delegate.</summary>
    /// <param name="app">The client.</param>
    public PlayDelegate(ClientApp app) => _app = app;

    // The maps of the three stock names, found on use (config.cs replaces them with NewActionMap).
    private ActionMap? Map(string n) => _app.Maps.FindMap(n);

    /// <summary>Pushes or pops the maps for the content control of the canvas (3.8 PlayDelegate_setContentControl). Called every frame.</summary>
    /// <param name="id">The id of the content control.</param>
    public void UpdateContent(uint id)
    {
        if (id == _lastContentId)
            return;
        var old = _lastContentId;
        _lastContentId = id;
        if (_app.GuiRt.FindControl(ChatDisplayId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiChatDisplay kept)
            _chatSizes.Apply(id, kept);
        bool newPc = id is PlayGuiId or CommandGuiId, oldPc = old is PlayGuiId or CommandGuiId;
        _action ??= Map("actionMap.sae");
        _play ??= Map("playMap.sae");
        _pda ??= Map("pdaMap.sae");
        if (newPc && !oldPc)
        {
            Push("playMap.sae");
            Push("actionMap.sae");
            MapsActive = true;
        }
        else if (!newPc && oldPc)
        {
            Pop("pdaMap.sae");
            Pop("playMap.sae");
            Pop("actionMap.sae");
            MapsActive = false;
        }
        // The mouse belongs to the game while the play view is up (client-app.md 3.8: play is `cursorOff`) and to the GUI
        // on every other screen; the command map shows the cursor.
        if (id == PlayGuiId)
        {
            _app.Canvas.CursorOn = false;
            _app.Canvas.Lock = null;
        }
        else if (!_app.Canvas.CursorOn && (oldPc || id == CommandGuiId))
            _app.Canvas.CursorOn = true;
        if (id == PlayGuiId)
        {
            Pop("pdaMap.sae");
            _app.Interp.SetVariable("$ConnectedToServer", "True");
        }
        else if (id == CommandGuiId)
            Push("pdaMap.sae");
    }

    /// <summary>Pushes an action map.</summary>
    /// <param name="name">The map name.</param>
    public void Push(string name)
    {
        var m = Map(name);
        if (m == null)
            return;
        _app.Input.Push(m, this);
        ++MapPushes;
    }

    /// <summary>Pops an action map.</summary>
    /// <param name="name">The map name.</param>
    public void Pop(string name)
    {
        var m = Map(name);
        if (m == null)
            return;
        _app.Input.Pop(m, this);
        ++MapPops;
    }

    /// <summary>True while the maps are pushed.</summary>
    public bool MapsActive { get; private set; }

    /// <inheritdoc/>
    /// <remarks>It handles the actions that are not move input (3.10).</remarks>
    public bool OnAction(int action, int kind, float value)
    {
        if (_app.Game == null || (_app.Game.IsDemo && action != MoveBuilder.ObserverToggle))
        {
            // No connection: only the script and GUI actions have an effect.
            if (action is Chat or MenuPage or EscapePressed)
                return Dispatch(action, value);
            return false;
        }
        if (Moves.OnAction(action, value))
            return true;
        return Dispatch(action, value);
    }

    private void LightButton(uint id, bool on)
    {
        if (_app.GuiRt.FindControl(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiUniversalButton b)
            b.On = on;
    }

    private bool Dispatch(int action, float value)
    {
        switch (action)
        {
            case Chat:
            {
                _app.Interp.SetVariable("$pref::msgChannel", ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (_app.GuiRt.FindControl(ChatEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiChat chat)
                    chat.Open();
                return true;
            }
            case ChatDispSize:
                // 0040cf74 case 0x35c29: the chat display (140113) cycles 3, 5 and 20 lines [code 004a7030].
                if (_app.GuiRt.FindControl(ChatDisplayId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiChatDisplay disp)
                {
                    disp.SetLines((int)value);
                    _chatSizes.Remember(_lastContentId, disp);
                }
                return true;
            case ChatDispPage:
                return true;
            case MenuPage:
                if ((int)value == 1)
                {
                    _scoresOn = !_scoresOn;
                    _app.Game?.PostRemoteEval([_scoresOn ? "scoresOn" : "scoresOff"]);
                }
                else if ((int)value == 2 && _app.GuiRt.FindControl(HudListId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiControl list)
                {
                    list.SetVisible(!list.Visible);
                    _app.Canvas.CursorOn = list.Visible;
                }
                return true;
            // The commander map (docs/gui.md 3.3 "The command map") [code 004dd280 cases 0x35c2f .. 0x35c3c]: the zoom steps, and the zoom and centre modes
            // with the lit state (+0x275) of their buttons.
            case ZoomIn or ZoomOut or ZoomModeOn or ZoomModeOff or CenterModeOn or CenterModeOff:
            {
                if (_app.GuiRt.FindControl(CommanderMapId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is not GuiCommandMap map)
                    return false;
                switch (action)
                {
                    case ZoomIn:
                        map.ZoomInAtCursor();
                        break;
                    case ZoomOut:
                        map.ZoomOutAtCursor();
                        break;
                    case ZoomModeOn or ZoomModeOff:
                        map.ZoomMode = action == ZoomModeOn;
                        LightButton(ZoomButtonId, map.ZoomMode);
                        break;
                    default:
                        map.CenterMode = action == CenterModeOn;
                        LightButton(CenterButtonId, map.CenterMode);
                        break;
                }
                return true;
            }
            case EscapePressed:
            {
                if (_app.GuiRt.FindControl(HudListId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiControl { Visible: true } hl)
                {
                    hl.SetVisible(false);
                    _app.Canvas.CursorOn = false;
                    return true;
                }
                _app.Interp.CallFunction("EnterLobbyMode", Array.Empty<string>());
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>The control id of the report button: it asks the server to make the first selected player of the list the commander.</summary>
    public const int ReportButton = 140213;
    /// <summary>The control id of the unreport button: it clears the commander.</summary>
    public const int UnreportButton = 140214;
    /// <summary>The control id of take control: it mounts the selected turret.</summary>
    public const int TakeControlButton = 160243;
    /// <summary>The control id of the players tab.</summary>
    public const int PlayersTab = 140115;
    /// <summary>The control id of the controllables tab.</summary>
    public const int TurretsTab = 140116;
    /// <summary>The control id of the objects tab.</summary>
    public const int ObjectsTab = 140117;
    /// <summary>The control id of the team list.</summary>
    public const int TeamListId = 140202;

    private GuiCommandMap? Map() => _app.GuiRt.FindControl(CommanderMapId.ToString(System.Globalization.CultureInfo.InvariantCulture)) as GuiCommandMap;

    /// <summary>Handles the messages of the buttons of the command screen (004dd280 cases 0x223b3 to 0x223b8, 0x271f3, 0x22353 to 0x22355).</summary>
    /// <param name="sender">The control that sent the message, or null.</param>
    /// <param name="tag">The message tag.</param>
    /// <returns>True when the message was handled.</returns>
    /// <remarks>The zoom (140211) and center (140216) buttons hand their lit state to the map. The three tabs (140115 players, 140116 controllables, 140117 objects) tell it which kind it selects.</remarks>
    public bool Message(GuiControl? sender, int tag)
    {
        var map = Map();
        switch (tag)
        {
            case (int)ZoomButtonId when sender is GuiUniversalButton zoom:
                map?.ZoomMode = zoom.On;
                return true;
            case (int)CenterButtonId when sender is GuiUniversalButton center:
                map?.CenterMode = center.On;
                return true;
            case ReportButton:
            {
                if (_app.GuiRt.FindControl(TeamListId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is not GuiCommandTeam team || _app.Host.Hud?.CommandLocal == null)
                    return false;
                var id = team.FirstReportable();
                if (id < 0)
                    return false;
                _app.Interp.CallFunction("remoteEval", new[] { "2048", "SelectCommander", id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                return true;
            }
            case UnreportButton:
                if (_app.Host.Hud?.CommandLocal == null)
                    return false;
                _app.Interp.CallFunction("remoteEval", new[] { "2048", "SelectCommander", "-1" });
                return true;
            case TakeControlButton:
            {
                var target = map?.SelectedTurretTarget() ?? -1;
                if (target >= 0)
                    _app.Interp.CallFunction("remoteEval", new[] { "2048", "CmdrMountObject", target.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                return true;
            }
            case PlayersTab or TurretsTab or ObjectsTab:
                map?.Group = tag == PlayersTab ? 3 : tag == TurretsTab ? 0x13 : 4;
                return true;
        }
        return false;
    }

    /// <summary>Switches the GUI to a PSC GUI mode (4.4, client-scripts 4.3, PlayDelegate_setGuiMode).</summary>
    /// <param name="mode">The GUI mode.</param>
    public void SetGuiMode(int mode)
    {
        var i = _app.Interp;
        _scoreDialog = null;
        i.Evaluate("setCMMode(PlayChatMenu, 0); setCMMode(CommandChatMenu, 0);");
        switch (mode)
        {
            case 0:
                i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\loading.gui\");");
                break;
            case 1:
                i.Evaluate("loadPlayGui();");
                break;
            case 2:
                i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\command.gui\");");
                break;
            case 3:
                i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdObjectives.gui\");");
                break;
            case 4:
                i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdInventory.gui\");");
                break;
            case 5:
                i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdObjectives.gui\");");
                break;
        }
        if (mode != 0 && i.HasFunction("loadPlayGui") && mode != 1)
        {
            // Unless the mode is 0 the commander object is loaded again (setContentControl then pushes pdaMap).
            if (!i.Evaluate("isObject(commandGui);").Equals("True", StringComparison.OrdinalIgnoreCase))
                i.Evaluate("loadObject(\"commandGui\", \"gui\\\\command.gui\");");
        }
    }

    // The score flag of the PSC [code 004dc7a0]: the score dialog (the Tab key's score list and server menu) is pushed over the
    // content while it is set, lr_score.gui on a canvas below 512 x 384, else score.gui. A content switch drops dialogs, so the
    // mode switch pushes it again.
    private GuiControl? _scoreDialog;

    /// <summary>Shows or hides the score page.</summary>
    /// <param name="on">True to show.</param>
    public void SetScoreVisible(bool on)
    {
        var shown = _scoreDialog != null && _app.Canvas.Stack.Contains(_scoreDialog);
        if (on == shown)
            return;
        if (!on)
        {
            if (ReferenceEquals(_app.Canvas.Stack[^1], _scoreDialog))
                _app.Canvas.PopDialog(0);
            _scoreDialog = null;
            return;
        }
        var low = _app.Canvas.Width < 512 || _app.Canvas.Height < 384;
        _app.Interp.Evaluate(low ? "GuiPushDialog(MainWindow, \"gui\\\\lr_score.gui\");" : "GuiPushDialog(MainWindow, \"gui\\\\score.gui\");");
        _scoreDialog = _app.Canvas.Stack.Count > 1 ? _app.Canvas.Stack[^1] : null;
    }

    /// <summary>Resets the delegate to its start state.</summary>
    public void Reset()
    {
        Moves.Reset();
        _scoresOn = false;
        _scoreDialog = null;
    }
}
