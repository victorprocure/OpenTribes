// docs/gui.md 3.3 "Base SimGui::ActiveCtrl and states", SimpleText and the text edit.
using System.Text;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// SimGui::ActiveCtrl [code 0053c444 .. 0053c86c]: pressed, over, active, message tag.
public class GuiActive : GuiControl
{
    public bool Pressed, Over;

    public override bool IsActiveCtrl => true;
    public override bool WantsTab => true;

    public override bool Active
    {
        get => !F.TryGetValue("active", out object? v) || Convert.ToByte(v) != 0;
        set { F["active"] = (byte)(value ? 1 : 0); Canvas?.Invalidate(); }
    }

    public uint MessageTag => U("messageTag");

    // The hit area (slot 0xd4): the whole extent unless the class overrides it. Local coordinates.
    protected virtual bool HitArea(int lx, int ly) => Contains(lx, ly);


    public override bool MsMove(GuiEvent e)
    {
        if (!Active)
            return base.MsMove(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        Over = HitArea(lx, ly);
        return true;
    }

    public override bool MsLeave(GuiEvent e)
    {
        if (!Active)
            return base.MsLeave(e);
        Over = false;
        return true;
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        Over = HitArea(lx, ly);
        if (Over)
        {
            Canvas?.LockMouse(this);
            Canvas?.SetFirstResponder(this);
            Pressed = true;
        }
        return true;
    }

    public override bool MsDrag(GuiEvent e)
    {
        if (!Active)
            return base.MsDrag(e);
        if (Canvas?.Lock == this)
        {
            var (lx, ly) = GlobalToLocal(e.X, e.Y);
            Pressed = HitArea(lx, ly);
        }
        return true;
    }

    public override bool MsUp(GuiEvent e)
    {
        if (!Active)
            return base.MsUp(e);
        if (Canvas?.Lock == this)
            Canvas.UnlockMouse(this);
        if (Pressed)
        {
            Pressed = false;
            DoAction();
        }
        return true;
    }

    public override bool KeyDown(GuiEvent e)
    {
        if (Active && e.Scan == 0x1c)
        {
            if (Canvas?.Lock != this)
                Pressed = true;
            return true;
        }
        return base.KeyDown(e);
    }

    public override bool KeyUp(GuiEvent e)
    {
        if (Active && e.Scan == 0x1c)
        {
            if (Canvas?.Lock != this && Pressed)
            {
                Pressed = false;
                DoAction();
            }
            return true;
        }
        return base.KeyUp(e);
    }

    public override bool CanRelease() => !Pressed;
    // Classes whose gain-focus slot (control virtual 0xfc, responder slot 0x40) is a 3 byte stub that returns false [code 0048a1dc FGTextList,
    // 00489a04 FearGuiFormattedText, 0048fdcc FGIRCView, 00491b5c FGIRCNicknames, 00492ff4 FGIRCChannelCtrl, 004938e8 FGIRCTopicCtrl,
    // 00494104 FGIRCScrollChannel, 00496614 FGMenuCtrl, 004a93d0 CommandTeamCtrl, 004aa0fc CommandItemCtrl, 004aafb4 CommandTurretCtrl,
    // 004e5f0c FGHInventory]: they never hold the keyboard, so every key reaches the action maps while their screen is up. Clicks still work.
    private bool RefusesFocus => ClassName is "FearGui::FGTextList" or "FearGuiFormattedText" or "FearGui::FGIRCView" or "FearGui::FGIRCNicknames"
        or "FearGui::FGIRCChannelCtrl" or "FearGui::FGIRCTopicCtrl" or "FearGui::FGIRCScrollChannel" or "FearGui::FGMenuCtrl"
        or "FearGui::CommandTeamCtrl" or "FearGui::CommandItemCtrl" or "FearGui::CommandTurretCtrl" or "FearGui::FGHInventory";
    public override bool CanTake() => Active && Visible && !RefusesFocus;

    // Control::performClick and every release that fires: the action (slot 0xd8).
    public void DoAction() => Action();

    protected virtual void Action()
    {
        RunCommand();
        CallOnAction();
        PostMessage();
    }

    protected void RunCommand()
    {
        if (Command.Length > 0)
            Host?.Eval(Command);
    }

    protected void CallOnAction()
    {
        if (Name != null && Host != null)
        {
            string fn = Name + "::onAction";
            if (Host.HasFunction(fn))
                Host.Call(fn);
        }
    }

    protected void PostMessage()
    {
        if (MessageTag != 0)
            Next?.Message(this, (int)MessageTag);
    }
}

// SimGui::SimpleText [code 00540040 .. 00540178]: one line of text.
public class GuiSimpleText : GuiActive
{
    public string Text
    {
        get => S("text");
        set => F["text"] = value;
    }

    protected string ResolvedText()
    {
        uint tag = U("textTag");
        if (tag != 0 && Host?.TagText(tag) is string t)
            return t;
        return Text;
    }

    protected virtual PftFont? Font => Host?.FontByTag(U("fontTag"));

    public override string GetValue() => ResolvedText();

    protected override void ApplyValue(string v)
    {
        Text = v;
        F["textTag"] = 0u;
        Canvas?.Invalidate();
    }

    public override void SetText(string t) => ApplyValue(t);

    public override void Wake()
    {
        if (U("textTag") != 0 && Host?.TagText(U("textTag")) is string t)
            Text = t;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        DrawText(p, gx, gy);
        RenderChildren(p, gx, gy);
    }

    protected virtual void DrawText(GuiPaint p, int gx, int gy)
    {
        PftFont? f = Font;
        string s = ResolvedText();
        if (f == null || s.Length == 0)
            return;
        int w = GuiPaint.TextWidth(f, s);
        int x = U("alignment") switch { 1 => W - w, 2 => (W - w) / 2, _ => 0 };
        p.Text(f, gx + x, gy + (int)U("textVOffset"), s);
    }
}

// FearGui::FGSimpleText [code 004c9c70]: orange when inactive, white active, grey dimmed, with a black shadow.
public sealed class GuiFGSimpleText : GuiSimpleText
{
    private bool mLowRes;
    public override bool WantsTab => false;
    public override bool CanTake() => false;

    // The set-text slot (0x10c) of the class [code 004c99f8] runs the base routine and then sizes the control to the text: the width is the
    // width of the text in the normal font (tag 150121) plus 2, so a value set from a script is as wide as it is long. The base clips the
    // draw to the control, which is why a label of the file with width 1 stays empty until it is set.
    protected override void ApplyValue(string v)
    {
        base.ApplyValue(v);
        PftFont? big = Host?.FontByName("sf_orange255_10.pft");
        if (big != null)
            W = GuiPaint.TextWidth(big, Text) + 2;
    }

    public override void PreRender()
    {
        if (Canvas == null)
            return;
        bool small = Canvas.Width < 512 || Canvas.Height < 384;
        PftFont? big = Host?.FontByName("sf_orange255_10.pft"), sm = Host?.FontByName("sf_white_6.pft");
        if (small && !mLowRes)
        {
            mLowRes = true;
            H = GuiPaint.FontHeight(sm) + 2;
        }
        else if (!small && (mLowRes || H != GuiPaint.FontHeight(big) + 4) && big != null)
        {
            mLowRes = false;
            H = GuiPaint.FontHeight(big) + 4;
        }
    }

    protected override void DrawText(GuiPaint p, int gx, int gy)
    {
        string s = ResolvedText();
        if (s.Length == 0 || Host == null)
            return;
        string main = Dimmed ? "sf_grey100_10b.pft" : Active ? "sf_white_10.pft" : "sf_orange255_10.pft";
        string shadow = "sf_black_10.pft";
        if (mLowRes)
        {
            main = Dimmed ? "sf_grey_6.pft" : Active ? "sf_white_6.pft" : "sf_yellow_6.pft";
            shadow = "sf_black_6.pft";
        }
        p.Text(Host.FontByName(shadow), gx, gy + 1, s);
        p.Text(Host.FontByName(main), gx + 1, gy, s);
    }
}

// SimGui::TextEdit and derived [code 0053e0d0 .. 0053e3dc].
public class GuiTextEdit : GuiSimpleText
{
    // 004cd838: the text cursor of the IRC edits and TestEdit (the other text edits keep the default).
    public override uint CursorTagAt(int lx, int ly) =>
        ClassName is "FearGui::FGIRCActiveTextEdit" or "FearGui::FGIRCTextEdit" or "FearGui::TestEdit" ? CursorIBeam : 0;

    public int Cursor, SelStart, SelEnd, Anchor;
    public bool Overwrite, Dragging;
    private string mUndoText = "";
    private int mUndoCursor, mUndoSelStart, mUndoSelEnd;
    // The blink state [code 0053f028, 0053e0d0]: +0x2f4 shown, +0x2ec the sim time of the last change, +0x2e8 the frames since.
    private long mBlinkBase;
    private int mBlinkFrames;
    private bool mCursorShown;
    private int mTextLeft = 3;

    public int MaxLength => (int)U("maxLength", 255) is var m && m > 0 ? m : 255;
    public bool NumbersOnly => U("numbersOnly") != 0;

    protected override PftFont? Font => Host?.FontByTag(U("fontTag"));

    // The commit rules of 3.3 are observable through these counters in tests.
    public override string GetValue() => Text;

    protected override void ApplyValue(string v)
    {
        Text = v.Length > MaxLength ? v[..MaxLength] : v;
        Cursor = Math.Min(Cursor, Text.Length);
        SelStart = SelEnd = 0;
        Canvas?.Invalidate();
    }

    public override void Wake()
    {
        base.Wake();
        Cursor = Math.Min(Cursor, Text.Length);
    }

    public override bool CanTake()
    {
        if (!base.CanTake())
            return false;
        Cursor = 0;
        mBlinkBase = Host?.TickMs ?? 0;
        mBlinkFrames = 0;
        mCursorShown = true;
        return true;
    }

    // Prepare [code 0053f028], only while this control is the first responder: count the frame, and when more than 0.5 s of sim time
    // (a float of seconds, `fcomp` against 0x3f000000, strictly greater) have passed since the last change and more than 3 frames
    // were counted, flip the shown flag, restart both and mark the control for redraw.
    public override void PreRender()
    {
        if (!ReferenceEquals(Canvas?.FirstResponder, this))
            return;
        long now = Host?.TickMs ?? 0;
        ++mBlinkFrames;
        if (now - mBlinkBase > 500 && mBlinkFrames > 3)
        {
            mCursorShown = !mCursorShown;
            mBlinkBase = now;
            mBlinkFrames = 0;
            Canvas?.Invalidate();
        }
    }

    private int IndexAt(int gx)
    {
        PftFont? f = Font;
        var (lx, _) = GlobalToLocal(gx, 0);
        int x = lx - mTextLeft;
        if (x <= 0 || f == null)
            return 0;
        int sum = 0;
        for (int i = 0; i < Text.Length; ++i)
        {
            sum += f.CharWidth(Text[i]) + 1;
            if (sum > x)
                return i;
        }
        return Text.Length;
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        SelStart = SelEnd = 0;
        Cursor = Anchor = IndexAt(e.X);
        Canvas?.LockMouse(this);
        Dragging = true;
        return base.MsDown(e);
    }

    public override bool MsDrag(GuiEvent e)
    {
        if (!Active)
            return base.MsDrag(e);
        Cursor = IndexAt(e.X);
        if (Cursor != Anchor)
        {
            SelStart = Math.Min(Cursor, Anchor);
            SelEnd = Math.Max(Cursor, Anchor);
        }
        else
            SelStart = SelEnd = 0;
        return base.MsDrag(e);
    }

    public override bool MsUp(GuiEvent e)
    {
        if (!Active)
            return base.MsUp(e);
        Dragging = false;
        Canvas?.UnlockMouse(this);
        return base.MsUp(e);
    }

    // Key up [0053e3ac]: every release writes the buffer to the variable and runs the action.
    public override bool KeyUp(GuiEvent e)
    {
        WriteVariable(Text);
        DoAction();
        return true;
    }

    public override bool KeyRepeat(GuiEvent e) => HandleKey(e);
    public override bool KeyDown(GuiEvent e) => HandleKey(e);

    private void SaveUndo()
    {
        mUndoText = Text;
        mUndoCursor = Cursor;
        mUndoSelStart = SelStart;
        mUndoSelEnd = SelEnd;
    }

    private void DeleteSelection()
    {
        if (SelEnd > SelStart)
        {
            Text = Text.Remove(SelStart, Math.Min(SelEnd, Text.Length) - SelStart);
            Cursor = SelStart;
        }
        SelStart = SelEnd = 0;
    }

    private bool HandleKey(GuiEvent e)
    {
        if (!Active)
            return false;
        int len = Text.Length;
        if (e.Ctrl && !e.Shift && !Dragging)
        {
            switch (e.Scan)
            {
                case 0x0e:
                case 0x16:
                    (Text, mUndoText) = (mUndoText, Text);
                    (Cursor, mUndoCursor) = (mUndoCursor, Cursor);
                    (SelStart, mUndoSelStart) = (mUndoSelStart, SelStart);
                    (SelEnd, mUndoSelEnd) = (mUndoSelEnd, SelEnd);
                    return true;
                case 0x2e:
                case 0x2d:
                    if (SelEnd > SelStart && Host != null)
                    {
                        Host.Clipboard = Text.Substring(SelStart, Math.Min(SelEnd, Text.Length) - SelStart);
                        if (e.Scan == 0x2d)
                        {
                            SaveUndo();
                            DeleteSelection();
                        }
                    }
                    return true;
                case 0x2f:
                    if (Host != null)
                    {
                        SaveUndo();
                        DeleteSelection();
                        string clip = Host.Clipboard;
                        int room = MaxLength - Text.Length;
                        if (room > 0 && clip.Length > 0)
                        {
                            clip = clip.Length > room ? clip[..room] : clip;
                            Text = Text.Insert(Cursor, clip);
                            Cursor += clip.Length;
                        }
                    }
                    return true;
            }
        }
        if (e.Scan is 0x01 or 0x0f or 0xc8 or 0xd0)
            return base.KeyDown(e);
        if (e.Scan == 0x1c)
            return true;
        bool shift = e.Shift;
        switch (e.Scan)
        {
            case 0xcd:
            case 0xcb:
            case 0xc7:
            case 0xcf:
                {
                    int target = e.Scan switch { 0xcd => Math.Min(Cursor + 1, len), 0xcb => Math.Max(Cursor - 1, 0), 0xc7 => 0, _ => len };
                    if (shift)
                    {
                        if (SelEnd <= SelStart)
                            Anchor = Cursor;
                        Cursor = target;
                        SelStart = Math.Min(Anchor, Cursor);
                        SelEnd = Math.Max(Anchor, Cursor);
                    }
                    else
                    {
                        Cursor = target;
                        SelStart = SelEnd = 0;
                    }
                    return true;
                }
            case 0xd2:
                Overwrite = !Overwrite;
                return true;
            case 0xd3:
                SaveUndo();
                if (SelEnd > SelStart)
                    DeleteSelection();
                else if (Cursor < len)
                    Text = Text.Remove(Cursor, 1);
                return true;
            case 0x0e:
                SaveUndo();
                if (SelEnd > SelStart)
                    DeleteSelection();
                else if (Cursor > 0)
                {
                    Text = Text.Remove(Cursor - 1, 1);
                    --Cursor;
                }
                return true;
        }
        char ch = e.Ascii;
        if (ch >= 0x20 && ch < 0x7f || ch >= 0xa0)
        {
            if (NumbersOnly && !(ch >= '0' && ch <= '9' || ch == '-' && Cursor == 0 && !Text.StartsWith('-')))
                return true;
            SaveUndo();
            DeleteSelection();
            if (Overwrite && Cursor < Text.Length)
                Text = Text.Remove(Cursor, 1).Insert(Cursor, ch.ToString());
            else if (Text.Length < MaxLength)
                Text = Text.Insert(Cursor, ch.ToString());
            else
                return true;
            ++Cursor;
        }
        return true;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        DrawField(p, gx, gy);
        RenderChildren(p, gx, gy);
    }

    // Draw [code 0053efbc]: the text rectangle is the extent, the field is filled with palette 0xff, then the text draw.
    protected virtual void DrawField(GuiPaint p, int gx, int gy)
    {
        p.Fill(gx, gy, W, H, 0xff);
        DrawLine(p, gx + mTextLeft - 3, gy, gy + H - 1);
    }

    // The colours of the selection box (+0x2e4) and of the cursor line (+0x2f0): both are 0 from the constructor [code 0053e006,
    // 0053e02d]; a selection box of colour 0 is not drawn [code 0053f42f]. TestEdit writes 0xf0 to the cursor colour on every draw.
    protected virtual int SelectionColor => 0;
    protected virtual int CursorColor => 0;

    // The text draw [code 0053f0b0, TestEdit copy 004cdb1c], for a text rectangle whose left edge is `left` and whose top and bottom
    // (inclusive) are `top` and `bottom`: the text starts at left + 3 and its top is top + ((bottom - top - font height) >> 1) + the
    // vertical offset of the file (+0x2d8). The disabled font is used when the control is inactive and has one. A selection is only
    // kept while the control has the focus and a highlight font exists. The text before the selection is drawn in the normal font,
    // each part follows the last one by its width + 1; the selected part is drawn in the highlight font over a box (when its colour is
    // not 0) as high as the rectangle and one pixel wider than the part. The cursor is a vertical line from the top to the bottom of the
    // rectangle at the start of the text plus the width of the characters before the cursor, shown while the blink flag is on.
    protected void DrawLine(GuiPaint p, int left, int top, int bottom)
    {
        if (Host == null)
            return;
        PftFont? dis = Host.FontByTag(U("disabledFontTag")), hi = Host.FontByTag(U("highlightFontTag"));
        PftFont? f = !Active && dis != null ? dis : Font;
        if (f == null)
            return;
        bool focus = ReferenceEquals(Canvas?.FirstResponder, this);
        if (hi == null || !focus || SelStart > Text.Length || SelEnd > Text.Length)
            SelStart = SelEnd = 0;
        int y = top + ((bottom - top - GuiPaint.FontHeight(f)) >> 1) + (int)U("textVOffset");
        int x0 = left + 3, x = x0;
        int end = 0;
        if (SelEnd > 0)
        {
            if (SelStart > 0)
            {
                string pre = Text[..SelStart];
                p.Text(f, x, y, pre);
                x += GuiPaint.TextWidth(f, pre) + 1;
            }
            string sel = Text[SelStart..SelEnd];
            int w = GuiPaint.TextWidth(hi, sel);
            if (SelectionColor != 0)
                p.Fill(x, top, w + 1, bottom - top + 1, SelectionColor);
            p.Text(hi, x, y, sel);
            x += w + 1;
            end = SelEnd;
        }
        p.Text(f, x, y, Text[end..]);
        if (focus && mCursorShown)
        {
            int cx = x0 + GuiPaint.TextWidth(f, Text[..Math.Min(Cursor, Text.Length)]);
            p.Line(cx, top, cx, bottom, CursorColor);
        }
    }

    protected void SetTextLeft(int left) => mTextLeft = left;
}

// FearGui::TestEdit [code 004cd840, 004cd974, 004cd814]: the shell text field.
public sealed class GuiTestEdit : GuiTextEdit
{
    public GuiTestEdit() => SetTextLeft(9);

    public override bool KeyDown(GuiEvent e) => HandleShell(e, base.KeyDown(e));
    public override bool KeyRepeat(GuiEvent e) => HandleShell(e, base.KeyRepeat(e));

    private bool HandleShell(GuiEvent e, bool r)
    {
        if (Host == null)
            return r;
        if (Command.Length > 0)
            Host.Eval(Command);
        WriteVariable(Text);
        if (e.Scan == 0x1c && AltCommand.Length > 0)
            Host.Eval(AltCommand);
        if (e.Scan is 0x1c or 0x01 && Canvas != null)
        {
            // A dialog box (control 141000) under the canvas: Enter presses its OK button, Escape its cancel button.
            GuiControl? root = Canvas.Stack.Count > 0 ? Canvas.Top : null;
            if (root != null && root.FindById(141000) != null)
            {
                string[] names = e.Scan == 0x1c ? new[] { "DialogReturnButton", "DialogCloseButton" } : new[] { "DialogEscapeButton", "DialogCloseButton" };
                foreach (string n in names)
                    if (root.FindByName(n) is GuiActive b)
                    {
                        b.DoAction();
                        break;
                    }
            }
        }
        return r;
    }

    // Focus-out runs the alternate command and always allows the change.
    public override bool CanRelease()
    {
        if (Host != null && AltCommand.Length > 0 && ReferenceEquals(Canvas?.FirstResponder, this))
            Host.Eval(AltCommand);
        return true;
    }

    // Draw [code 004cd974]: an underline of two rules (the table at 00629f68, 0xf0 0xef 0xee 0xed, the second four when dimmed), the
    // upper from x + 3 to x + W - 4 at y + H - 2 in the first colour, the lower from x + 4 to x + W - 5 at y + H - 1 in the second,
    // and the third colour on both ends of the upper rule. It sets the cursor colour to 0xf0, then the text rectangle to
    // (x + 6, y, x + W - 1, y + H - 3) and runs the text draw [code 004cdb1c], so the text starts 9 pixels in.
    protected override int CursorColor => 0xf0;

    protected override void DrawField(GuiPaint p, int gx, int gy)
    {
        int b = Dimmed ? 4 : 0;
        p.Line(gx + 3, gy + H - 2, gx + W - 4, gy + H - 2, 0xf0 - b);
        p.Line(gx + 4, gy + H - 1, gx + W - 5, gy + H - 1, 0xef - b);
        p.Fill(gx + 3, gy + H - 2, 1, 1, 0xee - b);
        p.Fill(gx + W - 4, gy + H - 2, 1, 1, 0xee - b);
        DrawLine(p, gx + 6, gy, gy + H - 3);
    }
}
