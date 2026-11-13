using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

/// <summary>A device event as the input manager delivers it (docs/gui.md 2.7): the device byte +0x28 and the fields behind it.</summary>
public struct RawInput
{
    /// <summary>The device byte of the mouse.</summary>
    public const byte DevMouse = 2;
    /// <summary>The device byte of the keyboard.</summary>
    public const byte DevKeyboard = 3;
    /// <summary>The device byte of the windows mouse.</summary>
    public const byte DevWinMouse = 0x10;
    /// <summary>The object type of a button.</summary>
    public const byte ObjButton = 2;
    /// <summary>The object type of the x axis.</summary>
    public const byte ObjXAxis = 0x0b;
    /// <summary>The object type of the y axis.</summary>
    public const byte ObjYAxis = 0x0c;
    /// <summary>The device byte.</summary>
    public byte Device;
    /// <summary>The object type.</summary>
    public byte ObjType;
    /// <summary>The object index: the button or key scan code.</summary>
    public byte Index;
    /// <summary>The action: 1 make, 0 break.</summary>
    public byte Action;
    /// <summary>The character of a key press.</summary>
    public char Ascii;
    /// <summary>The modifier bits.</summary>
    public byte Mod;
    /// <summary>The axis movement.</summary>
    public float Value;
}

/// <summary>The responder a canvas puts below the content control of a screen: it forwards to the canvas root and calls the screen's hooks.</summary>
public sealed class GuiDelegate : IGuiResponder
{
    /// <summary>The delegate name, the file name of its screen.</summary>
    public string Name = "";
    /// <summary>The class name.</summary>
    public string ClassName = "";
    /// <summary>The canvas the delegate is installed on.</summary>
    public GuiCanvas? Canvas;
    /// <summary>Called when a dialog is about to pop with a message (slot 0x70). Returns -1 to keep it open.</summary>
    public Func<GuiControl, int, int>? OnPop;
    /// <summary>Called once per frame.</summary>
    public Action? OnFrame;
    /// <summary>True when the screen's delegate installs a help pop-up (FGHelpCtrl) on its content (004d749c and its siblings).</summary>
    public bool HelpPopups;
    /// <summary>Called when the canvas installed this delegate for a new content control (slot 0x64).</summary>
    public Action? OnOpen;
    /// <summary>Called with the new content control when it is installed (slot 0x64). The main menu sets its version text there (004dbc00).</summary>
    public Action<GuiControl>? OnOpenContent;
    /// <summary>Called once the new content has been woken. The Connect screen activates the radio of the game mode there (004dbea8).</summary>
    public Action<GuiControl>? OnContentReady;
    /// <summary>Called when the canvas is removing the delegate's content (slot 0x60). JoinGame saves its server list there (004d7630).</summary>
    public Action<GuiControl>? OnRemove;
    private IGuiResponder? Up => Canvas?.Root;
    /// <inheritdoc/>
    public bool MsDown(GuiEvent e) => Up?.MsDown(e) ?? false;
    /// <inheritdoc/>
    public bool MsRepeat(GuiEvent e) => Up?.MsRepeat(e) ?? false;
    /// <inheritdoc/>
    public bool MsUp(GuiEvent e) => Up?.MsUp(e) ?? false;
    /// <inheritdoc/>
    public bool MsMove(GuiEvent e) => Up?.MsMove(e) ?? false;
    /// <inheritdoc/>
    public bool MsEnter(GuiEvent e) => Up?.MsEnter(e) ?? false;
    /// <inheritdoc/>
    public bool MsLeave(GuiEvent e) => Up?.MsLeave(e) ?? false;
    /// <inheritdoc/>
    public bool MsDrag(GuiEvent e) => Up?.MsDrag(e) ?? false;
    /// <inheritdoc/>
    public bool MsRightDown(GuiEvent e) => Up?.MsRightDown(e) ?? false;
    /// <inheritdoc/>
    public bool MsRightUp(GuiEvent e) => Up?.MsRightUp(e) ?? false;
    /// <inheritdoc/>
    public bool MsRightDrag(GuiEvent e) => Up?.MsRightDrag(e) ?? false;
    /// <inheritdoc/>
    public bool KeyUp(GuiEvent e) => Up?.KeyUp(e) ?? false;
    /// <inheritdoc/>
    public bool KeyRepeat(GuiEvent e) => Up?.KeyRepeat(e) ?? false;
    /// <inheritdoc/>
    public bool KeyDown(GuiEvent e) => Up?.KeyDown(e) ?? false;
    /// <summary>Handles the delegate's own messages (docs/gui.md 2.2 slot 0x74). The client installs it.</summary>
    public Func<GuiControl?, int, bool>? OnMessage;
    /// <inheritdoc/>
    public bool Message(GuiControl? sender, int tag) => OnMessage?.Invoke(sender, tag) == true || (Up?.Message(sender, tag) ?? false);
    /// <inheritdoc/>
    public bool CanRelease() => true;
    /// <inheritdoc/>
    public bool CanTake() => false;
}

/// <summary>The canvas root responder (+0x1a0): tab and arrow movement and the message table of docs/gui.md 2.8.</summary>
public sealed class CanvasRoot : IGuiResponder
{
    private readonly GuiCanvas mCanvas;
    /// <summary>Creates the root responder.</summary>
    /// <param name="c">The canvas.</param>
    public CanvasRoot(GuiCanvas c) => mCanvas = c;
    /// <inheritdoc/>
    public bool MsDown(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsRepeat(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsUp(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsMove(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsEnter(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsLeave(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsDrag(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsRightDown(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsRightUp(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool MsRightDrag(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool KeyUp(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool KeyRepeat(GuiEvent e) => false;
    /// <inheritdoc/>
    public bool CanRelease() => true;
    /// <inheritdoc/>
    public bool CanTake() => true;

    /// <inheritdoc/>
    /// <remarks>Follows 00535620: Tab moves the focus, and the arrow keys do too unless the top control has flag bit 2.</remarks>
    public bool KeyDown(GuiEvent e)
    {
        switch (e.Scan)
        {
            case 0x0f:
                if (e.Shift) mCanvas.FocusPrevious(); else mCanvas.FocusNext();
                return true;
            case 0xcd:
            case 0xd0:
            case 0xcb:
            case 0xc8:
                if (mCanvas.Top != null && (mCanvas.Top.Flags & 4) == 0)
                {
                    if (e.Scan is 0xcd or 0xd0) mCanvas.FocusNext(); else mCanvas.FocusPrevious();
                    return true;
                }
                return false;
        }
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 00535b7c (docs/gui.md 2.8): tags 100002 to 109998 load a content file, 110001 and 110002 pop the dialog, 110003 to 119998 push a dialog, 120001 to 129998 run a command. The text of the tag is the file or command.</remarks>
    public bool Message(GuiControl? sender, int tag)
    {
        GuiHost host = mCanvas.Host;
        string? text = host.TagText((uint)tag);
        if (tag > 100001 && tag < 109999)
        {
            if (text == null)
                return false;
            mCanvas.LoadContent(text);
            return true;
        }
        if (tag is 110001 or 110002)
        {
            mCanvas.PopDialog(tag);
            return true;
        }
        if (tag > 110000 && tag < 119999)
        {
            if (text == null)
                return false;
            mCanvas.PushDialog(text);
            return true;
        }
        if (tag > 120000 && tag < 129999)
        {
            if (text == null)
                return false;
            host.Eval(text);
            return true;
        }
        return false;
    }
}

/// <summary>The GUI canvas (docs/gui.md 2.1, 2.2, 2.6 to 2.9).</summary>
/// <remarks>It holds the content control and dialog stack, the delegates, the first responder, the tab list, the mouse lock and hover, the cursor, the key and mouse repeat, the input routing and the per-frame steps.</remarks>
public sealed class GuiCanvas
{
    /// <summary>The GUI host.</summary>
    public readonly GuiHost Host;
    /// <summary>The canvas name scripts use.</summary>
    public string Name = "MainWindow";
    /// <summary>The canvas width in pixels.</summary>
    public int Width = 640;
    /// <summary>The canvas height in pixels.</summary>
    public int Height = 480;
    /// <summary>The content control at index 0 and the dialogs above it.</summary>
    public readonly List<GuiControl> Stack = new();
    /// <summary>The replaced content controls that are not deleted on replacement, by name.</summary>
    public readonly Dictionary<string, GuiControl> Retained = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The delegates by screen name.</summary>
    public readonly Dictionary<string, GuiDelegate> Delegates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GuiControl, GuiDelegate> mDelegateOf = new();
    /// <summary>The canvas root responder.</summary>
    public readonly CanvasRoot Root;
    /// <summary>The responder that gets the keys.</summary>
    public IGuiResponder FirstResponder;
    /// <summary>The control that has locked the mouse, or null.</summary>
    public GuiControl? Lock;
    /// <summary>The control under the cursor, or null.</summary>
    public GuiControl? Hover;
    /// <summary>True while the cursor is shown and the mouse is read.</summary>
    public bool CursorOn = true;
    /// <summary>True while the window is active. Input is ignored otherwise.</summary>
    public bool WindowActive = true;
    /// <summary>True when the canvas consumes every input event, also the ones no control took.</summary>
    public bool CaptureAll;
    /// <summary>The cursor x in canvas pixels.</summary>
    public float CursorX;
    /// <summary>The cursor y in canvas pixels.</summary>
    public float CursorY;
    /// <summary>The mouse speed factor.</summary>
    public float MouseSpeed = 1f;
    /// <summary>True while the left button is down.</summary>
    public bool LeftDown;
    /// <summary>True while the right button is down.</summary>
    public bool RightDown;
    /// <summary>The key repeat delay in milliseconds.</summary>
    public int KeyRepeatDelay = 300;
    /// <summary>The mouse repeat delay in milliseconds.</summary>
    public int MouseRepeatDelay = 300;
    /// <summary>The key repeat interval in milliseconds.</summary>
    public int KeyRepeatInterval = 50;
    /// <summary>The maximum time between the clicks of a double click in milliseconds.</summary>
    public int DoubleClickMs = 500;
    /// <summary>The cursor string last set.</summary>
    public string CursorName = "";
    /// <summary>The default cursor bitmap.</summary>
    public PbmpBitmap? CursorBitmap;
    /// <summary>The hot spot of the default cursor.</summary>
    public (int x, int y) CursorHot;
    /// <summary>The tag of the cursor the locked or hovered control asked for (+0x22c), shown instead of the default (+0x210). 0 for none.</summary>
    public uint CursorOverrideTag;
    /// <summary>The bitmap of the override cursor.</summary>
    public PbmpBitmap? OverrideBitmap;
    /// <summary>The hot spot of the override cursor.</summary>
    public (int x, int y) OverrideHot;
    /// <summary>Gets the cursor bitmap that is shown: the override, else the default.</summary>
    public PbmpBitmap? ShownCursor => OverrideBitmap ?? CursorBitmap;

    // The help pop-up (FGHelpCtrl 004af248 shows it, 004af6d8 draws it). The tick of the last hover change is +0x26c.
    private long mHoverTick;
    // +0x268: a button went down while this control was hovered; the pop-up stays away until the hover changes.
    private bool mPressedOnHover;
    private uint mHelpTag;
    private (int X, int Y, int W, int H) mHelpBox;
    private GuiTextFormat? mHelpLayout;
    /// <summary>The time the cursor rests on a control before its help pop-up shows, in milliseconds.</summary>
    public const int HelpDelayMs = 1069;
    /// <summary>True once a screen's delegate installed the help pop-up control. It stays on the canvas for every later screen.</summary>
    /// <remarks>The first screen of the main menu, Connect, JoinGame, Options and IRCChat delegates creates it (00534308).</remarks>
    public bool HelpInstalled;
    /// <summary>Gets a value that is true while the help pop-up is shown.</summary>
    public bool HelpVisible => mHelpTag != 0 && mHelpLayout != null;
    /// <summary>Gets the rectangle of the help pop-up.</summary>
    public (int X, int Y, int W, int H) HelpBox => mHelpBox;
    /// <summary>Gets the tag of the help text shown.</summary>
    public uint HelpShownTag => mHelpTag;
    /// <summary>The controls the Tab key moves between, in tree order.</summary>
    public readonly List<GuiControl> TabList = new();
    /// <summary>The load and stack events, for tests and tools.</summary>
    public readonly List<string> Trace = new();
    /// <summary>True when the canvas needs a redraw.</summary>
    public bool Dirty = true;

    private long mKeyRepeatAt = -1, mMouseRepeatAt = -1;
    private GuiEvent mLastKey;
    private int mClickCount;
    private long mLastClickAt;
    private bool mLastClickLeft;
    private readonly Dictionary<GuiControl, string> mVarSeen = new();

    /// <summary>Creates a canvas.</summary>
    /// <param name="host">The GUI host.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public GuiCanvas(GuiHost host, int width = 640, int height = 480)
    {
        Host = host;
        Width = width;
        Height = height;
        Root = new CanvasRoot(this);
        FirstResponder = Root;
        CursorX = width / 2f;
        CursorY = height / 2f;
    }

    /// <summary>Gets the top control of the stack, or null.</summary>
    public GuiControl? Top => Stack.Count > 0 ? Stack[^1] : null;
    /// <summary>Gets the content control, or null.</summary>
    public GuiControl? Content => Stack.Count > 0 ? Stack[0] : null;
    /// <summary>Gets the number of dialogs above the content control.</summary>
    public int DialogCount => Math.Max(0, Stack.Count - 1);
    /// <summary>Gets the host time in milliseconds.</summary>
    public long Now => Host.TickMs;
    /// <summary>Marks the canvas for a redraw.</summary>
    public void Invalidate() => Dirty = true;

    // ---- responder links (2.2): a stack element's next responder is the one below it, the first one's its delegate ----

    /// <summary>Returns the responder below a control (docs/gui.md 2.2): a stack element's next responder is the one below it, the first one's is its delegate.</summary>
    /// <param name="c">The control.</param>
    /// <returns>The next responder.</returns>
    public IGuiResponder ResponderAbove(GuiControl c)
    {
        GuiControl top = c;
        while (top.Parent != null)
            top = top.Parent;
        int i = Stack.IndexOf(top);
        if (i > 0)
            return Stack[i - 1];
        return mDelegateOf.TryGetValue(top, out GuiDelegate? d) ? d : Root;
    }

    /// <summary>Returns whether a dialog dims a control (docs/gui.md 2.5).</summary>
    /// <param name="c">The control.</param>
    /// <returns>True when stack element 1 holds control id 141000 and is not the control's own root.</returns>
    public bool IsDimmed(GuiControl c)
    {
        if (Stack.Count < 2)
            return false;
        GuiControl dlg = Stack[1];
        GuiControl r = c;
        while (r.Parent != null)
            r = r.Parent;
        return r != dlg && dlg.FindById(141000) != null;
    }

    // ---- loading ----

    /// <summary>Loads a `.gui` file.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The root control, null when the file is missing or invalid.</returns>
    public GuiControl? LoadFile(string name)
    {
        if (!Host.ReadGuiFile(name, out byte[] data))
        {
            Host.Print("Could not load gui: " + name);
            return null;
        }
        GuiControl c = GuiLoader.Load(data, out string error);
        if (c == null)
        {
            Host.Print("Could not load gui: " + name);
            Trace.Add("load failed " + name + ": " + error);
            return null;
        }
        return c;
    }

    private GuiDelegate? DelegateFor(string file)
    {
        string baseName = Path.GetFileNameWithoutExtension(file.Replace('\\', '/'));
        return Delegates.GetValueOrDefault(baseName);
    }

    /// <summary>Loads a `.gui` file as the content control (GuiLoadContentCtrl, docs/gui.md 2.2).</summary>
    /// <param name="file">The file name.</param>
    /// <returns>True on success.</returns>
    public bool LoadContent(string file)
    {
        Trace.Add("load content " + file);
        GuiControl? c = LoadFile(file);
        if (c == null)
            return false;
        // The content control is named after its file: JoinGame.gui is `JoinGameGui`, play.gui `playGui` (the names of the
        // `<Content>::onOpen` / `onClose` callbacks of client-scripts.md 4.3).
        if (string.IsNullOrEmpty(c.Name))
            c.Name = Path.GetFileNameWithoutExtension(file.Replace('\\', '/')) + "Gui";
        SetContent(c, DelegateFor(file));
        return true;
    }

    /// <summary>Calls the `Content::onClose()` of the control that leaves or the `Content::onOpen()` of the one that comes (client-scripts.md 4.3).</summary>
    /// <param name="c">The content control.</param>
    /// <param name="hook">The hook name.</param>
    private void CallContentHook(GuiControl? c, string hook)
    {
        if (c == null || string.IsNullOrEmpty(c.Name))
            return;
        string fn = c.Name + "::" + hook;
        if (Host.HasFunction(fn))
            Host.Call(fn);
    }

    /// <summary>Replaces the content control.</summary>
    /// <param name="c">The new content control.</param>
    /// <param name="del">The screen's delegate, or null.</param>
    /// <remarks>It pops every dialog and calls the old content's onClose and the delegate's remove hook. The old content is kept by name when its flag bit 1 is clear: play.gui and CmdInventory.gui have flags 1 and a lookup by name still finds them (00533fdc, 0040dbfc).</remarks>
    public void SetContent(GuiControl c, GuiDelegate? del = null)
    {
        Lock = null;
        Hover = null;
        Invalidate();
        while (Stack.Count > 1)
            PopDialog(0, force: true);
        if (Stack.Count == 1)
            CallContentHook(Stack[0], "onClose");
        if (Stack.Count == 1)
        {
            GuiControl old = Stack[0];
            Stack.RemoveAt(0);
            if (mDelegateOf.TryGetValue(old, out GuiDelegate? oldDel))
                oldDel.OnRemove?.Invoke(old);
            old.CloseTree();
            old.SetCanvas(null);
            mDelegateOf.Remove(old);
            // A control whose flag bit 1 is clear is not deleted when replaced (play.gui and CmdInventory.gui have flags 1): it
            // stays in the object manager and a lookup by name still finds it [code 00533fdc, 0040dbfc].
            if (!old.DeleteOnLose && !string.IsNullOrEmpty(old.Name))
                Retained[old.Name] = old;
        }
        if (del != null)
        {
            del.Canvas = this;
            mDelegateOf[c] = del;
            if (del.HelpPopups)
                HelpInstalled = true;
            del.OnOpen?.Invoke();
            del.OnOpenContent?.Invoke(c);
        }
        Stack.Add(c);
        c.SetCanvas(this);
        c.WakeTree();
        if (del != null)
            del.OnContentReady?.Invoke(c);
        AfterStackChange();
        CallContentHook(c, "onOpen");
        Trace.Add($"content control {c.ClassName} {c.Name} ({c.Children.Count} children)");
    }

    /// <summary>Loads a `.gui` file and pushes it as a dialog.</summary>
    /// <param name="file">The file name.</param>
    /// <returns>True on success.</returns>
    public bool PushDialog(string file)
    {
        Trace.Add("push dialog " + file);
        GuiControl? c = LoadFile(file);
        if (c == null)
            return false;
        PushDialog(c);
        return true;
    }

    /// <summary>Pushes a control as a dialog.</summary>
    /// <param name="c">The dialog control.</param>
    public void PushDialog(GuiControl c)
    {
        Stack.Add(c);
        c.SetCanvas(this);
        c.WakeTree();
        AfterStackChange();
    }

    /// <summary>Pops the top dialog (docs/gui.md 2.2). The delegate sees the message first and can keep the dialog by answering -1.</summary>
    /// <param name="message">The pop message.</param>
    /// <param name="force">True to pop without asking the delegate.</param>
    public void PopDialog(int message, bool force = false)
    {
        if (Stack.Count < 2)
            return;
        GuiControl dlg = Stack[^1];
        if (!force)
        {
            GuiDelegate? d = mDelegateOf.GetValueOrDefault(Stack[0]);
            if (d?.OnPop != null && d.OnPop(dlg, message) == -1)
                return;
        }
        Stack.RemoveAt(Stack.Count - 1);
        dlg.CloseTree();
        dlg.SetCanvas(null);
        if (!force)
            AfterStackChange();
    }

    private void AfterStackChange()
    {
        RebuildTabList();
        ResetMouse();
        FocusNext(first: true);
        Invalidate();
    }

    /// <summary>Resets the mouse state (00534190).</summary>
    public void ResetMouse()
    {
        if (FirstResponder.CanRelease())
            FirstResponder = Root;
        Hover = null;
        mClickCount = 0;
        LeftDown = RightDown = false;
        mPressedOnHover = false;
        SendMove();
    }

    // ---- focus (2.6) ----

    /// <summary>Moves the keyboard focus to a responder.</summary>
    /// <param name="r">The responder.</param>
    /// <returns>True when it took the focus.</returns>
    public bool SetFirstResponder(IGuiResponder r)
    {
        IGuiResponder old = FirstResponder;
        if (!old.CanRelease())
            return false;
        IGuiResponder next = r.CanTake() ? r : Root;
        FirstResponder = next;
        if (next != old && (next == Root || old == Root))
            mKeyRepeatAt = -1;
        return next == r;
    }

    /// <summary>Rebuilds the tab list from the top control.</summary>
    public void RebuildTabList()
    {
        TabList.Clear();
        if (Top != null)
            Collect(Top);
        void Collect(GuiControl c)
        {
            if (!c.Visible)
                return;
            if (c.WantsTab)
                TabList.Add(c);
            foreach (GuiControl k in c.Children)
                Collect(k);
        }
    }

    /// <summary>Moves the focus to the next control in the tab list.</summary>
    /// <param name="first">True to start from the first control.</param>
    public void FocusNext(bool first = false) => MoveFocus(+1, first);
    /// <summary>Moves the focus to the previous control in the tab list.</summary>
    public void FocusPrevious() => MoveFocus(-1, false);

    private void MoveFocus(int dir, bool first)
    {
        int n = TabList.Count;
        if (n == 0)
            return;
        int cur = first ? -1 : TabList.FindIndex(c => ReferenceEquals(c, FirstResponder));
        int idx = cur < 0 ? (dir > 0 ? -1 : 0) : cur;
        for (int step = 0; step < n; ++step)
        {
            idx = (idx + dir + n) % n;
            GuiControl cand = TabList[idx];
            if (cand.CanTake())
            {
                SetFirstResponder(cand);
                Invalidate();
                return;
            }
            if (cur >= 0 && idx == cur)
                break;
        }
    }

    // ---- mouse state (2.6) ----

    /// <summary>Locks the mouse to a control.</summary>
    /// <param name="c">The control.</param>
    public void LockMouse(GuiControl c) => Lock = c;

    /// <summary>Unlocks the mouse when the control holds the lock.</summary>
    /// <param name="c">The control.</param>
    public void UnlockMouse(GuiControl c)
    {
        if (Lock == c)
        {
            Lock = null;
            SendMove();
        }
    }

    // The modifier byte of the raw mouse event reaches the GUI event (+0x2e) [code 0053349c].
    private byte mMods;

    private GuiEvent MouseEvent() => new() { X = (int)CursorX, Y = (int)CursorY, ClickCount = mClickCount, Mod = mMods };

    private GuiControl? Target()
    {
        if (Lock != null)
            return Lock;
        GuiControl? top = Top;
        return top?.Pick((int)CursorX - top.X, (int)CursorY - top.Y);
    }

    private void SendMove()
    {
        if (Top == null || !CursorOn)
            return;
        Dispatch(MouseEvent(), drag: false);
    }

    private void Dispatch(GuiEvent e, bool drag)
    {
        GuiControl? target = Target();
        if (Lock == null && target != Hover)
        {
            GuiControl? old = Hover;
            old?.MsLeave(e);
            Hover = target;
            mHoverTick = Now;
            mPressedOnHover = false;
            target?.MsEnter(e);
        }
        if (drag)
            target?.MsDrag(e);
        else
            target?.MsMove(e);
    }

    // ---- input (2.7) ----

    /// <summary>Returns the DirectInput scan code of a key name.</summary>
    /// <param name="name">The key name.</param>
    /// <returns>The scan code, 0 for an unknown name.</returns>
    public static int ScanForName(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "escape": case "esc": return 1;
            case "backspace": return 0x0e;
            case "tab": return 0x0f;
            case "enter": case "return": return 0x1c;
            case "space": return 0x39;
            case "up": return 0xc8;
            case "down": return 0xd0;
            case "left": return 0xcb;
            case "right": return 0xcd;
            case "home": return 0xc7;
            case "end": return 0xcf;
            case "insert": return 0xd2;
            case "delete": return 0xd3;
        }
        if (name.Length == 1)
        {
            char ch = char.ToLowerInvariant(name[0]);
            const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm";
            int i;
            if ((i = row1.IndexOf(ch)) >= 0) return 0x10 + i;
            if ((i = row2.IndexOf(ch)) >= 0) return 0x1e + i;
            if ((i = row3.IndexOf(ch)) >= 0) return 0x2c + i;
            if (ch >= '1' && ch <= '9') return 2 + (ch - '1');
            if (ch == '0') return 11;
            if (ch == '-') return 12;
            if (ch == '.') return 0x34;
        }
        return 0;
    }

    /// <summary>Handles a raw input event.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True when the canvas consumed the event, false to pass it to the action maps.</returns>
    public bool HandleInput(in RawInput ev)
    {
        if (!WindowActive)
            return false;
        long now = Now;
        if (ev.Device == RawInput.DevKeyboard)
            return HandleKey(ev, now);
        if (ev.Device is RawInput.DevMouse or RawInput.DevWinMouse)
            return HandleMouse(ev, now);
        return CaptureAll;
    }

    private bool HandleKey(in RawInput ev, long now)
    {
        bool make = ev.Action == 1;
        var ge = new GuiEvent { Scan = ev.Index, Ascii = ev.Ascii, Mod = ev.Mod };
        if (ev.Index == 0x1c && (ev.Mod & 0x30) != 0)
            return false;
        if (make && ev.Index == 1 && Host.HasFunction("Canvas::handleEscapeKey") && Stack.Count < 2)
        {
            string r = Host.Call("Canvas::handleEscapeKey");
            if (string.Equals(r, "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        IGuiResponder entry = FirstResponder;
        if (make)
        {
            mLastKey = ge;
            if (ev.Index != 0x0f)
                mKeyRepeatAt = now + KeyRepeatDelay;
            entry.KeyDown(ge);
        }
        else
        {
            mKeyRepeatAt = -1;
            entry.KeyUp(ge);
        }
        if (entry == Root && FirstResponder == Root)
            return CaptureAll;
        return true;
    }

    private bool HandleMouse(in RawInput ev, long now)
    {
        if (!CursorOn)
            return false;
        mMods = ev.Mod;
        if (ev.ObjType is RawInput.ObjXAxis or RawInput.ObjYAxis)
        {
            GuiControl? top = Top;
            int mx = top?.W ?? Width, my = top?.H ?? Height;
            int ox = (int)CursorX, oy = (int)CursorY;
            if (ev.ObjType == RawInput.ObjXAxis)
                CursorX = Math.Clamp(CursorX + ev.Value * MouseSpeed, 0, mx - 1);
            else
                CursorY = Math.Clamp(CursorY + ev.Value * MouseSpeed, 0, my - 1);
            if ((int)CursorX != ox || (int)CursorY != oy)
            {
                var e = MouseEvent();
                if (!LeftDown && !RightDown) Dispatch(e, false);
                else if (LeftDown) Dispatch(e, true);
                else Target()?.MsRightDrag(e);
            }
            return true;
        }
        if (ev.ObjType != RawInput.ObjButton)
            return true;   // wheel and extra axes: consumed and dropped
        bool make = ev.Action == 1;
        if (ev.Index == 0)
        {
            if (make)
            {
                mClickCount = mLastClickLeft && now - mLastClickAt <= DoubleClickMs ? mClickCount + 1 : 1;
                mLastClickAt = now;
                mLastClickLeft = true;
                mMouseRepeatAt = now + MouseRepeatDelay;
                LeftDown = true;
                mPressedOnHover = true;
                Target()?.MsDown(MouseEvent());
            }
            else
            {
                mMouseRepeatAt = -1;
                LeftDown = false;
                Target()?.MsUp(MouseEvent());
            }
        }
        else if (ev.Index == 1)
        {
            if (make)
            {
                mLastClickLeft = false;
                RightDown = true;
                mPressedOnHover = true;
                Target()?.MsRightDown(MouseEvent());
            }
            else
            {
                RightDown = false;
                Target()?.MsRightUp(MouseEvent());
            }
        }
        Invalidate();
        return true;
    }

    // ---- per frame (2.9) ----

    /// <summary>Runs the per-frame steps (docs/gui.md 2.9): the delegate frame hooks, the key and mouse repeat, the resize and pre-render of the stack, the cursor, the help pop-up and the console variable changes.</summary>
    public void Frame()
    {
        long now = Now;
        foreach (GuiDelegate d in mDelegateOf.Values.Distinct())
            d.OnFrame?.Invoke();
        if (mKeyRepeatAt >= 0 && now > mKeyRepeatAt && FirstResponder != Root)
        {
            FirstResponder.KeyRepeat(mLastKey);
            mKeyRepeatAt = mLastKey.Scan == 0x0f ? -1 : now + KeyRepeatInterval;
        }
        if (mMouseRepeatAt >= 0 && now > mMouseRepeatAt && LeftDown && Lock != null)
            Lock.MsRepeat(MouseEvent());
        // A pre-render may replace the content (the movie control runs its done command there), so walk a copy.
        foreach (GuiControl el in Stack.ToArray())
        {
            if (el.X != 0 || el.Y != 0 || el.W != Width || el.H != Height)
            {
                el.Resize(0, 0, Width, Height);
                Invalidate();
            }
            el.PreRenderTree();
        }
        ResolveCursor();
        UpdateHelp(now);
        foreach (GuiControl el in Stack.ToArray())
            foreach (GuiControl c in el.Descendants())
                if (c.ConsoleVar.Length > 0)
                {
                    string v = Host.GetVar(c.ConsoleVar);
                    if (!mVarSeen.TryGetValue(c, out string? seen) || seen != v)
                    {
                        mVarSeen[c] = v;
                        c.VariableChanged(v);
                    }
                }
    }

    /// <summary>Gets the tick of the key repeat timer, -1 when unarmed. For tests.</summary>
    public long KeyRepeatAt => mKeyRepeatAt;
    /// <summary>Gets the tick of the mouse repeat timer, -1 when unarmed. For tests.</summary>
    public long MouseRepeatAt => mMouseRepeatAt;

    /// <summary>False while the play view has no camera (no control object): its TSControl draws no HUD (<see cref="GuiPlayView"/>).</summary>
    public bool ViewHasCamera = true;

    /// <summary>Draws the 3D world into one rectangle of the frame, in the middle of the GUI draw: the host's world renderer, which answers false when it has no camera. Null in tests and without a game.</summary>
    /// <remarks>A TSControl other than the play view's that has a camera object (<c>GuiPlayView.CameraAttached</c>) draws its view through it. The stock scene render runs inside the control's own draw (00546dc4).</remarks>
    public Func<GfxRect, bool>? ViewRenderer;

    /// <summary>Draws the stack, the help pop-up and the cursor.</summary>
    /// <param name="dev">The graphics device.</param>
    public void Render(IGfxDevice dev)
    {
        var p = new GuiPaint(dev, Host);
        dev.SetClip(null);
        foreach (GuiControl el in Stack)
        {
            p.SetClip(new GfxRect(0, 0, dev.Width - 1, dev.Height - 1));
            el.Render(p, 0, 0);
        }
        dev.SetClip(null);
        if (HelpVisible)
        {
            p.SetClip(new GfxRect(0, 0, dev.Width - 1, dev.Height - 1));
            DrawHelp(p);
        }
        if (CursorOn && ShownCursor is { } cur)
        {
            (int hx, int hy) = OverrideBitmap != null ? OverrideHot : CursorHot;
            // palette index 0 is clear in every GUI picture, the cursor's too
            dev.Blit(GuiPaint.KeyedBitmap(cur), (int)CursorX - hx, (int)CursorY - hy);
        }
    }

    private GuiDelegate? DelegateOfTop => Top != null && mDelegateOf.TryGetValue(Top, out GuiDelegate? d) ? d : null;

    private PftFont? HelpFont(int n) => Host.FontByTag(n == 1 ? 150114u : 150104u);

    /// <summary>Updates the help pop-up (step 6 of the frame, docs/gui.md 2.6).</summary>
    /// <remarks>With a hover control, the cursor on, no lock and no button down, the control's help tag is shown once the cursor has rested 1069 ms on it (004af248). Anything else hides the pop-up. `$pref::HelpPopups FALSE` turns the pop-ups off.</remarks>
    private void UpdateHelp(long now)
    {
        uint tag = 0;
        if (HelpInstalled && Hover != null && CursorOn && Lock == null && !LeftDown && !RightDown && !mPressedOnHover
            && !string.Equals(Host.GetVar("$pref::HelpPopups"), "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            long ms = now - mHoverTick;
            var (lx, ly) = Hover.GlobalToLocal((int)CursorX, (int)CursorY);
            tag = ms >= HelpDelayMs ? Hover.HelpTagAt(lx, ly, ms) : 0;
        }
        if (tag == mHelpTag)
            return;
        mHelpTag = 0;
        mHelpLayout = null;
        if (tag == 0 || Host.TagText(tag) is not string text)
            return;
        // "width,text": the width is atoi of what stands before the first comma (at most 7 characters), -1 or less is 200, at most 256.
        int comma = text.IndexOf(',');
        if (comma < 0 || comma + 1 >= text.Length)
            return;
        int.TryParse(text[..Math.Min(comma, 7)].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int req);
        req = req < 0 ? 200 : Math.Min(req, 256);
        // The box [code 004af248]: the text is laid out by the TextFormat child [005370b8] for the width asked less 6 with the fonts of
        // slots 0 and 1 (tags 150104 and 150114, the constructor 004af164); the box is min(asked width, widest line + 6) wide and
        // min(0xc0, layout height + 6) high [vtable +0x100 and 005389e0 (+0x210)].
        var fonts = new PftFont?[GuiTextFormat.FontCount];
        fonts[0] = HelpFont(0);
        fonts[1] = HelpFont(1);
        GuiTextFormat layout = GuiTextFormat.Layout(text[(comma + 1)..], req - 6, fonts, n => Host.BitmapByName(n), n => Host.GetVar("$" + n));
        int w = Math.Min(req, layout.MaxWidth + 6), h = Math.Min(0xc0, layout.Height + 6);
        // Below the cursor (the height of the default cursor bitmap), pushed back inside the window by three pixels.
        int x = (int)CursorX, y = (int)CursorY + (CursorBitmap?.Height ?? 0);
        if (x + w > Width - 3)
            x -= x + w - (Width - 3);
        if (y + h > Height - 3)
            y -= y + h - (Height - 3);
        mHelpBox = (x, y, w, h);
        mHelpLayout = layout;
        mHelpTag = tag;
    }

    /// <summary>Draws the help pop-up: the box in color 0xf7 with an outline of 0xf1, the text three pixels in (004af6d8).</summary>
    private void DrawHelp(GuiPaint p)
    {
        var (x, y, w, h) = mHelpBox;
        p.Fill(x, y, w, h, 0xf7);
        p.Outline(x, y, w, h, 0xf1);
        mHelpLayout?.Draw(p, x + 3, y + 3);
    }

    /// <summary>Splits a cursor string "name, x, y" into the bitmap name and the hot spot, which default to 0, 0 (00533138).</summary>
    private static (string Name, int X, int Y) ParseCursor(string text)
    {
        string[] parts = text.Split(',');
        int Num(int i) => i < parts.Length && int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0;
        return (parts[0].Trim(), Num(1), Num(2));
    }

    /// <summary>Sets the default cursor.</summary>
    /// <param name="name">The cursor string "name, x, y".</param>
    public void SetCursorBitmap(string name)
    {
        CursorName = name;
        var (n, x, y) = ParseCursor(name);
        CursorBitmap = Host.BitmapByName(n);
        CursorHot = (x, y);
    }

    /// <summary>Resolves the override cursor (step 5 of the frame, docs/gui.md 2.6).</summary>
    /// <remarks>It takes the cursor tag of the lock control, else of the hover control, and picks the override slot when the tag changed. A tag of 0 falls back to the default cursor.</remarks>
    private void ResolveCursor()
    {
        GuiControl? src = Lock ?? Hover;
        uint tag = 0;
        if (CursorOn && src != null)
        {
            var (lx, ly) = src.GlobalToLocal((int)CursorX, (int)CursorY);
            tag = src.CursorTagAt(lx, ly);
        }
        if (tag == CursorOverrideTag)
            return;
        CursorOverrideTag = tag;
        OverrideBitmap = null;
        if (tag != 0 && Host.TagText(tag) is string text)
        {
            var (n, x, y) = ParseCursor(text);
            OverrideBitmap = Host.BitmapByName(n);
            OverrideHot = (x, y);
        }
    }
}
