// docs/gui.md 2.1, 2.2, 2.6 to 2.9: the canvas. Content control and dialog stack, delegates, first responder, tab list,
// mouse lock and hover, cursor, key and mouse repeat, input routing and the per-frame steps.
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// docs/gui.md 2.7: a device event as the input manager delivers it (device byte +0x28 and the fields behind it).
public struct RawInput
{
    public const byte DevMouse = 2, DevKeyboard = 3, DevWinMouse = 0x10;
    public const byte ObjButton = 2, ObjXAxis = 0x0b, ObjYAxis = 0x0c;
    public byte Device, ObjType, Index, Action; // Action: 1 make, 0 break
    public char Ascii;
    public byte Mod;
    public float Value;
}

public sealed class GuiDelegate : IGuiResponder
{
    public string Name = "";
    public string ClassName = "";
    public GuiCanvas? Canvas;
    // Slot 0x70: a dialog is about to pop with this message; -1 keeps it open.
    public Func<GuiControl, int, int>? OnPop;
    public Action? OnFrame;
    // 004d749c and its siblings: the screen's delegate installs a help pop-up (FGHelpCtrl) on its content.
    public bool HelpPopups;
    // Slot 0x64: the canvas installed this delegate for a new content control.
    public Action? OnOpen;
    // Slot 0x64 with the new content control (the main menu sets its version text, 004dbc00).
    public Action<GuiControl>? OnOpenContent;
    private IGuiResponder? Up => Canvas?.Root;
    public bool MsDown(GuiEvent e) => Up?.MsDown(e) ?? false;
    public bool MsRepeat(GuiEvent e) => Up?.MsRepeat(e) ?? false;
    public bool MsUp(GuiEvent e) => Up?.MsUp(e) ?? false;
    public bool MsMove(GuiEvent e) => Up?.MsMove(e) ?? false;
    public bool MsEnter(GuiEvent e) => Up?.MsEnter(e) ?? false;
    public bool MsLeave(GuiEvent e) => Up?.MsLeave(e) ?? false;
    public bool MsDrag(GuiEvent e) => Up?.MsDrag(e) ?? false;
    public bool KeyUp(GuiEvent e) => Up?.KeyUp(e) ?? false;
    public bool KeyRepeat(GuiEvent e) => Up?.KeyRepeat(e) ?? false;
    public bool KeyDown(GuiEvent e) => Up?.KeyDown(e) ?? false;
    // C8: the handler of the delegate's own messages (docs/gui.md 2.2 slot 0x74); the client installs it.
    public Func<GuiControl?, int, bool>? OnMessage;
    public bool Message(GuiControl? sender, int tag) => OnMessage?.Invoke(sender, tag) == true || (Up?.Message(sender, tag) ?? false);
    public bool CanRelease() => true;
    public bool CanTake() => false;
}

// The canvas root responder (+0x1a0): tab and arrow movement and the message table of 2.8.
public sealed class CanvasRoot : IGuiResponder
{
    private readonly GuiCanvas mCanvas;
    public CanvasRoot(GuiCanvas c) => mCanvas = c;
    public bool MsDown(GuiEvent e) => false;
    public bool MsRepeat(GuiEvent e) => false;
    public bool MsUp(GuiEvent e) => false;
    public bool MsMove(GuiEvent e) => false;
    public bool MsEnter(GuiEvent e) => false;
    public bool MsLeave(GuiEvent e) => false;
    public bool MsDrag(GuiEvent e) => false;
    public bool KeyUp(GuiEvent e) => false;
    public bool KeyRepeat(GuiEvent e) => false;
    public bool CanRelease() => true;
    public bool CanTake() => true;

    // [code 00535620]
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

    // docs/gui.md 2.8 [code 00535b7c].
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

public sealed class GuiCanvas
{
    public readonly GuiHost Host;
    public string Name = "MainWindow";
    public int Width = 640, Height = 480;
    public readonly List<GuiControl> Stack = new();
    public readonly Dictionary<string, GuiDelegate> Delegates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GuiControl, GuiDelegate> mDelegateOf = new();
    public readonly CanvasRoot Root;
    public IGuiResponder FirstResponder;
    public GuiControl? Lock, Hover;
    public bool CursorOn = true, WindowActive = true, CaptureAll;
    public float CursorX, CursorY, MouseSpeed = 1f;
    public bool LeftDown, RightDown;
    public int KeyRepeatDelay = 300, MouseRepeatDelay = 300, KeyRepeatInterval = 50;
    public int DoubleClickMs = 500;
    public string CursorName = "";
    public PbmpBitmap? CursorBitmap;
    public (int x, int y) CursorHot;
    // The override slot (+0x22c): the cursor of the locked or hovered control's tag, shown instead of the default (+0x210).
    public uint CursorOverrideTag;
    public PbmpBitmap? OverrideBitmap;
    public (int x, int y) OverrideHot;
    public PbmpBitmap? ShownCursor => OverrideBitmap ?? CursorBitmap;

    // The help pop-up (FGHelpCtrl 004af248 shows it, 004af6d8 draws it). The tick of the last hover change is +0x26c.
    private long mHoverTick;
    // +0x268: a button went down while this control was hovered; the pop-up stays away until the hover changes.
    private bool mPressedOnHover;
    private uint mHelpTag;
    private (int X, int Y, int W, int H) mHelpBox;
    private List<List<GuiMarkup.Run>>? mHelpLines;
    public const int HelpDelayMs = 1069;
    public bool HelpVisible => mHelpTag != 0 && mHelpLines != null;
    public (int X, int Y, int W, int H) HelpBox => mHelpBox;
    public uint HelpShownTag => mHelpTag;
    public readonly List<GuiControl> TabList = new();
    public readonly List<string> Trace = new();
    public bool Dirty = true;

    private long mKeyRepeatAt = -1, mMouseRepeatAt = -1;
    private GuiEvent mLastKey;
    private int mClickCount;
    private long mLastClickAt;
    private bool mLastClickLeft;
    private readonly Dictionary<GuiControl, string> mVarSeen = new();

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

    public GuiControl? Top => Stack.Count > 0 ? Stack[^1] : null;
    public GuiControl? Content => Stack.Count > 0 ? Stack[0] : null;
    public int DialogCount => Math.Max(0, Stack.Count - 1);
    public long Now => Host.TickMs;
    public void Invalidate() => Dirty = true;

    // ---- responder links (2.2): a stack element's next responder is the one below it, the first one's its delegate ----

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

    // 2.5 dimmed rule: a dialog in stack element 1 that holds control id 141000 and is not the control's own root.
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

    // GuiLoadContentCtrl (2.2): true on success.
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

    // client-scripts.md 4.3: `<Content>::onClose()` of the control that leaves, then `<Content>::onOpen()` of the one that comes.
    private void CallContentHook(GuiControl? c, string hook)
    {
        if (c == null || string.IsNullOrEmpty(c.Name))
            return;
        string fn = c.Name + "::" + hook;
        if (Host.HasFunction(fn))
            Host.Call(fn);
    }

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
            old.CloseTree();
            old.SetCanvas(null);
            mDelegateOf.Remove(old);
        }
        if (del != null)
        {
            del.Canvas = this;
            mDelegateOf[c] = del;
            del.OnOpen?.Invoke();
            del.OnOpenContent?.Invoke(c);
        }
        Stack.Add(c);
        c.SetCanvas(this);
        c.WakeTree();
        AfterStackChange();
        CallContentHook(c, "onOpen");
        Trace.Add($"content control {c.ClassName} {c.Name} ({c.Children.Count} children)");
    }

    public bool PushDialog(string file)
    {
        Trace.Add("push dialog " + file);
        GuiControl? c = LoadFile(file);
        if (c == null)
            return false;
        PushDialog(c);
        return true;
    }

    public void PushDialog(GuiControl c)
    {
        Stack.Add(c);
        c.SetCanvas(this);
        c.WakeTree();
        AfterStackChange();
    }

    // Pop dialog (2.2): the delegate sees the message first; -1 keeps the dialog.
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

    // Reset the mouse [code 00534190].
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

    public void FocusNext(bool first = false) => MoveFocus(+1, first);
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

    public void LockMouse(GuiControl c) => Lock = c;

    public void UnlockMouse(GuiControl c)
    {
        if (Lock == c)
        {
            Lock = null;
            SendMove();
        }
    }

    private GuiEvent MouseEvent() => new() { X = (int)CursorX, Y = (int)CursorY, ClickCount = mClickCount };

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

    // Returns true when the canvas consumed the event; false passes it to the action maps.
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
            }
            else
                RightDown = false;
        }
        Invalidate();
        return true;
    }

    // ---- per frame (2.9) ----

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

    // The tick of the key repeat and mouse repeat timers, for tests: -1 when unarmed.
    public long KeyRepeatAt => mKeyRepeatAt;
    public long MouseRepeatAt => mMouseRepeatAt;

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

    // Step 6 of the frame (2.6): with a hover control, the cursor on, no lock and no button down, the control's help tag is shown once the
    // cursor has rested 1069 ms on it (004af248); anything else hides the pop-up. $pref::HelpPopups FALSE turns the pop-ups off.
    private void UpdateHelp(long now)
    {
        uint tag = 0;
        if (DelegateOfTop?.HelpPopups == true && Hover != null && CursorOn && Lock == null && !LeftDown && !RightDown && !mPressedOnHover
            && !string.Equals(Host.GetVar("$pref::HelpPopups"), "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            long ms = now - mHoverTick;
            var (lx, ly) = Hover.GlobalToLocal((int)CursorX, (int)CursorY);
            tag = ms >= HelpDelayMs ? Hover.HelpTagAt(lx, ly, ms) : 0;
        }
        if (tag == mHelpTag)
            return;
        mHelpTag = 0;
        mHelpLines = null;
        if (tag == 0 || Host.TagText(tag) is not string text)
            return;
        // "width,text": the width is atoi of what stands before the first comma (at most 7 characters), -1 or less is 200, at most 256.
        int comma = text.IndexOf(',');
        if (comma < 0 || comma + 1 >= text.Length)
            return;
        int.TryParse(text[..Math.Min(comma, 7)].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int req);
        req = req < 0 ? 200 : Math.Min(req, 256);
        PftFont? f0 = HelpFont(0);
        List<List<GuiMarkup.Run>> lines = GuiMarkup.WrapText(Host, f0, text[(comma + 1)..], req - 6, HelpFont);
        // measured on stock boxes: the width leaves out the spacing after the last character, a line is the font height plus one
        int textW = lines.Count == 0 ? 0 : lines.Max(l => GuiMarkup.Width(Host, f0, l, HelpFont)) - (f0?.Spacing ?? 0);
        int textH = lines.Count * (GuiPaint.FontHeight(f0) + 1);
        int w = Math.Min(req, textW + 6), h = Math.Min(0xc0, textH + 6);
        // Below the cursor (the height of the default cursor bitmap), pushed back inside the window by three pixels.
        int x = (int)CursorX, y = (int)CursorY + (CursorBitmap?.Height ?? 0);
        if (x + w > Width - 3)
            x -= x + w - (Width - 3);
        if (y + h > Height - 3)
            y -= y + h - (Height - 3);
        mHelpBox = (x, y, w, h);
        mHelpLines = lines;
        mHelpTag = tag;
    }

    // 004af6d8: the box in colour 0xf7 with an outline of 0xf1, the text three pixels in.
    private void DrawHelp(GuiPaint p)
    {
        var (x, y, w, h) = mHelpBox;
        p.Fill(x, y, w, h, 0xf7);
        p.Outline(x, y, w, h, 0xf1);
        PftFont? f0 = HelpFont(0);
        int ty = y + 3;
        foreach (List<GuiMarkup.Run> line in mHelpLines!)
        {
            GuiMarkup.Draw(p, Host, f0, x + 3, ty, line, HelpFont);
            ty += GuiPaint.FontHeight(f0) + 1;
        }
    }

    // "name, x, y": the bitmap name and the hot spot a cursor string may carry (00533138; the default is 0, 0).
    private static (string Name, int X, int Y) ParseCursor(string text)
    {
        string[] parts = text.Split(',');
        int Num(int i) => i < parts.Length && int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0;
        return (parts[0].Trim(), Num(1), Num(2));
    }

    public void SetCursorBitmap(string name)
    {
        CursorName = name;
        var (n, x, y) = ParseCursor(name);
        CursorBitmap = Host.BitmapByName(n);
        CursorHot = (x, y);
    }

    // Step 5 of the frame (2.6): the cursor tag of the lock control, else of the hover control; it picks the override slot
    // when it changed, a tag of 0 falls back to the default cursor.
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
