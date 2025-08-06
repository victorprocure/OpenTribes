// docs/client-scripts.md 3.8 and 3.10: the play delegate. It owns actionMap.sae, playMap.sae and pdaMap.sae, pushes and pops
// them when the canvas content control changes (PlayDelegate_setContentControl 004dcc4c), receives their actions and
// handles the ones that are not move input (chat, score page, escape), and switches the GUI with the PSC GUI mode
// (PlayDelegate_setGuiMode 004dc84c).
using OpenTribes.Gui;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class PlayDelegate : IActionConsumer
{
    public const uint PlayGuiId = 140114, CommandGuiId = 140200, ChatEntryId = 140112, HudListId = 140220;
    public const int Chat = 220200, ChatDispSize = 220201, ChatDispPage = 220202, MenuPage = 220203, EscapePressed = 220204;

    private readonly ClientApp mApp;
    public readonly MoveBuilder Moves = new();
    private ActionMap? mAction, mPlay, mPda;
    private uint mLastContentId;
    private bool mPlayActive;
    private bool mScoresOn;
    public bool Removing => false;
    public int MapPushes, MapPops;

    public PlayDelegate(ClientApp app) => mApp = app;

    // The maps of the three stock names, found on use (config.cs replaces them with NewActionMap).
    private ActionMap? Map(string n) => mApp.Maps.FindMap(n);

    // 3.8 PlayDelegate_setContentControl: called every frame with the canvas content control's id.
    public void UpdateContent(uint id)
    {
        if (id == mLastContentId)
            return;
        uint old = mLastContentId;
        mLastContentId = id;
        bool newPc = id is PlayGuiId or CommandGuiId, oldPc = old is PlayGuiId or CommandGuiId;
        mAction ??= Map("actionMap.sae");
        mPlay ??= Map("playMap.sae");
        mPda ??= Map("pdaMap.sae");
        if (newPc && !oldPc)
        {
            Push("playMap.sae");
            Push("actionMap.sae");
            mPlayActive = true;
        }
        else if (!newPc && oldPc)
        {
            Pop("pdaMap.sae");
            Pop("playMap.sae");
            Pop("actionMap.sae");
            mPlayActive = false;
        }
        // The mouse belongs to the game while the play view is up (client-app.md 3.8: play is `cursorOff`) and to the GUI
        // on every other screen; the command map shows the cursor.
        if (id == PlayGuiId)
        {
            mApp.Canvas.CursorOn = false;
            mApp.Canvas.Lock = null;
        }
        else if (!mApp.Canvas.CursorOn && (oldPc || id == CommandGuiId))
            mApp.Canvas.CursorOn = true;
        if (id == PlayGuiId)
        {
            Pop("pdaMap.sae");
            mApp.Interp.SetVariable("$ConnectedToServer", "True");
        }
        else if (id == CommandGuiId)
            Push("pdaMap.sae");
    }

    public void Push(string name)
    {
        ActionMap? m = Map(name);
        if (m == null)
            return;
        mApp.Input.Push(m, this);
        ++MapPushes;
    }

    public void Pop(string name)
    {
        ActionMap? m = Map(name);
        if (m == null)
            return;
        mApp.Input.Pop(m, this);
        ++MapPops;
    }

    public bool MapsActive => mPlayActive;

    // 3.10: the actions that are not move input.
    public bool OnAction(int action, int kind, float value)
    {
        if (mApp.Game == null || mApp.Game.IsDemo && action != MoveBuilder.ObserverToggle)
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

    private bool Dispatch(int action, float value)
    {
        switch (action)
        {
            case Chat:
                {
                    mApp.Interp.SetVariable("$pref::msgChannel", ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (mApp.GuiRt.FindControl(ChatEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiChat chat)
                        chat.Open();
                    return true;
                }
            case ChatDispSize:
            case ChatDispPage:
                return true;
            case MenuPage:
                if ((int)value == 1)
                {
                    mScoresOn = !mScoresOn;
                    mApp.Game?.PostRemoteEval(new[] { mScoresOn ? "scoresOn" : "scoresOff" });
                }
                else if ((int)value == 2 && mApp.GuiRt.FindControl(HudListId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiControl list)
                {
                    list.SetVisible(!list.Visible);
                    mApp.Canvas.CursorOn = list.Visible;
                }
                return true;
            case EscapePressed:
                {
                    if (mApp.GuiRt.FindControl(HudListId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is GuiControl { Visible: true } hl)
                    {
                        hl.SetVisible(false);
                        mApp.Canvas.CursorOn = false;
                        return true;
                    }
                    mApp.Interp.CallFunction("EnterLobbyMode", Array.Empty<string>());
                    return false;
                }
            default:
                return false;
        }
    }

    // 4.4 / client-scripts 4.3: PlayDelegate_setGuiMode.
    public void SetGuiMode(int mode)
    {
        Interpreter i = mApp.Interp;
        i.Evaluate("setCMMode(PlayChatMenu, 0); setCMMode(CommandChatMenu, 0);");
        switch (mode)
        {
            case 0: i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\loading.gui\");"); break;
            case 1: i.Evaluate("loadPlayGui();"); break;
            case 2: i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\command.gui\");"); break;
            case 3: i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdObjectives.gui\");"); break;
            case 4: i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdInventory.gui\");"); break;
            case 5: i.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CmdObjectives.gui\");"); break;
        }
        if (mode != 0 && i.HasFunction("loadPlayGui") && mode != 1)
        {
            // Unless the mode is 0 the commander object is loaded again (setContentControl then pushes pdaMap).
            if (!i.Evaluate("isObject(commandGui);").Equals("True", StringComparison.OrdinalIgnoreCase))
                i.Evaluate("loadObject(\"commandGui\", \"gui\\\\command.gui\");");
        }
    }

    public void Reset()
    {
        Moves.Reset();
        mScoresOn = false;
    }
}
