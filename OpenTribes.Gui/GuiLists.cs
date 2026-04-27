// docs/gui.md 3.3 "Lists and array controls", "Scroll controls", "Combo boxes and popups".
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// SimGui::ArrayCtrl and every list class derived from it: one column of (text, id) rows. The row and selection colours,
// the row height and the keyboard rules beyond up and down are inferred (Q2, Q4 of the spec).
public class GuiList : GuiActive
{
    public readonly List<(string Text, int Id)> Entries = new();
    public int Selected = -1;

    // The command screen lists override the focus query with a stub [code 004a93d0 CommandTeamCtrl, 004aa0fc CommandItemCtrl,
    // 004aafb4 CommandTurretCtrl, taken as `return false` (I), like the buttons' 004a05d0]: nothing on the commander screen holds
    // the keyboard, so every key reaches the action maps (chat menu, command keys). Clicks still select rows.
    private bool CommandList => ClassName is "FearGui::CommandTeamCtrl" or "FearGui::CommandItemCtrl" or "FearGui::CommandTurretCtrl";
    public override bool CanTake() => !CommandList && base.CanTake();

    protected virtual PftFont? RowFont => Host?.FontByTag(U("fontTag")) ?? Host?.FontByName("sf_white_10.pft");
    public int RowHeight => Math.Max(8, GuiPaint.FontHeight(RowFont) + 2);

    public void AddEntry(string text, int id) => Entries.Add((text, id));
    public virtual void Clear() { Entries.Clear(); Selected = -1; }

    public int FindId(int id) => Entries.FindIndex(e => e.Id == id);
    public int FindText(string text) => Entries.FindIndex(e => string.Equals(e.Text, text, StringComparison.OrdinalIgnoreCase));

    public void SortText() => Entries.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));

    public virtual void DeleteById(int id)
    {
        int i = FindId(id);
        if (i < 0)
            return;
        Entries.RemoveAt(i);
        if (Selected == i) Selected = -1; else if (Selected > i) --Selected;
    }

    // The class's own reaction to a selected row (the master list sets its console variables).
    protected virtual void Selecting(int row) { }

    public void Select(int row, bool notify = true)
    {
        if (row < -1 || row >= Entries.Count)
            return;
        Selected = row;
        Selecting(row);
        if (!notify)
            return;
        WriteVariable(row.ToString(CultureInfo.InvariantCulture));
        // The control's console command runs on every selection (the recordings list enables its buttons this way).
        if (Command.Length > 0)
            Host?.Eval(Command);
        if (Name != null && Host != null && Host.HasFunction(Name + "::onSelect"))
            Host.Call(Name + "::onSelect", row.ToString(CultureInfo.InvariantCulture));
        PostMessage();
    }

    // A text list answers Control::getValue with the text of the selected line (the stock scripts build file names from it:
    // `recordings\\ @ Control::getValue(RecordingsTextList) @ .rec`, C8); the other array controls answer the row index.
    public override string GetValue() => ClassName.EndsWith("TextList", StringComparison.Ordinal)
        ? (Selected >= 0 && Selected < Entries.Count ? Entries[Selected].Text : "")
        : Selected.ToString(CultureInfo.InvariantCulture);
    protected override void ApplyValue(string v)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            Select(Math.Clamp(i, -1, Entries.Count - 1), notify: false);
    }

    public override void PreRender()
    {
        if (Parent is GuiScrollContent pc)
            H = Math.Max(pc.H, Entries.Count * RowHeight);
    }

    public override bool MsDown(GuiEvent e)
    {
        bool r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        int row = ly / RowHeight;
        if (Active && row >= 0 && row < Entries.Count)
        {
            Select(row);
            if (e.ClickCount >= 2 && Name != null && Host != null && Host.HasFunction(Name + "::onDoubleClick"))
                Host.Call(Name + "::onDoubleClick", row.ToString(CultureInfo.InvariantCulture));
        }
        return r;
    }

    // The row action comes with the mouse up in an ActiveCtrl; a list selects on press, so release only unlocks.
    protected override void Action() { }

    public override bool KeyDown(GuiEvent e)
    {
        if (e.Scan == 0xc8 && Selected > 0) { Select(Selected - 1); return true; }
        if (e.Scan == 0xd0 && Selected < Entries.Count - 1) { Select(Selected + 1); return true; }
        return base.KeyDown(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        PftFont? f = RowFont;
        int rh = RowHeight;
        for (int i = 0; i < Entries.Count; ++i)
        {
            int y = gy + i * rh;
            if (y > p.Clip.Bottom)
                break;
            if (y + rh < p.Clip.Top)
                continue;
            if (i == Selected)
                p.Fill(gx, y, W, rh, 0xf8);
            p.Text(f, gx + 2, y + 1, Entries[i].Text);
        }
        RenderChildren(p, gx, gy);
    }
}

public sealed class GuiScrollContent : GuiControl { }

// SimGui::ScrollCtrl and derived [code 0054a0e0, 005491d4]: a viewport with optional bars. Bars are drawn with palette
// colours; the arrow, page and key handlers are inferred.
public class GuiScroll : GuiControl
{
    public const int BarWidth = 15;

    // The piece array of the bars (docs/gui.md "Scroll controls": the bar sizes are the sizes of its pieces).
    private PbmaArray? Pieces
    {
        get
        {
            string? t = Host?.TagText(U("pbaTag"));
            PbmaArray? a = t == null ? null : Host?.ArrayByName(t.Split(',')[0].Trim());
            return a != null && a.Bitmaps.Count >= 25 ? a : null;
        }
    }

    private int Piece(PbmaArray? a, int i, bool width, int def) => a == null ? def : width ? a.Bitmaps[i].Width : a.Bitmaps[i].Height;
    private int VBarW => Piece(Pieces, 0, true, BarWidth);       // vertical bar width = width of its arrow pieces
    private int HBarH => Piece(Pieces, 6, false, BarWidth);      // horizontal bar height
    private int VArrowTop => Piece(Pieces, 0, false, BarWidth);
    private int VArrowBottom => Piece(Pieces, 3, false, BarWidth);
    private int HArrowLeft => Piece(Pieces, 6, true, BarWidth);
    private int HArrowRight => Piece(Pieces, 9, true, BarWidth);
    public int ScrollX, ScrollY;
    // +0x270 of the original: the list inside is disabled, so the border takes its disabled colour [code 00548944, 004b4584].
    public bool Disabled;
    private bool mInsetKnown;
    private int mInsetX, mInsetY;
    private int mDragPart;      // 8 vertical thumb, 9 horizontal thumb
    private int mGrabOffset;
    private int mRepeatPart;

    // FearGuiScrollCtrl overrides the tab stop slot with a stub [code 004b8bc4, 4 bytes, taken as `return false` (I)]: the scroll
    // frames of the game screens never hold the keyboard, so Tab and the movement keys reach the action maps while the score list
    // is up. The plain SimGui::ScrollCtrl keeps the base rule.
    private bool KeyboardStop => ClassName != "FearGui::FearGuiScrollCtrl";
    public override bool WantsTab => KeyboardStop;
    public override bool CanTake() => Visible && KeyboardStop;

    private GuiControl? ContentChild => Children.FirstOrDefault(c => c is GuiScrollContent);
    private GuiControl? Inner => ContentChild?.Children.FirstOrDefault();

    private int ContentW => Inner == null ? 0 : Inner.X + ScrollX + Inner.W;
    private int ContentH => Inner == null ? 0 : Inner.Y + ScrollY + Inner.H;

    // Policies: 0 never, 1 always, 2 when needed (the inspector order).
    // Policy values: 0 always, 1 never, 2 when needed. The stock files show it: CmdInventory.gui (horizontal 1, vertical 0) sizes its
    // list 19 pixels narrower than the frame for a vertical bar and has no use for a horizontal one; command.gui (1, 2) scrolls
    // vertically only.
    private bool VBar => U("verticalPolicy") == 0 || U("verticalPolicy") == 2 && ContentH > H;
    private bool HBar => U("horizontalPolicy") == 0 || U("horizontalPolicy") == 2 && ContentW > W;

    public override void PreRender()
    {
        GuiControl? c = ContentChild;
        if (c == null)
            return;
        // The content keeps the offset its file gives (the Columns options scroll has it at 1, 1 inside its border).
        if (!mInsetKnown)
        {
            mInsetKnown = true;
            mInsetX = c.X;
            mInsetY = c.Y;
        }
        int vw = W - (VBar ? VBarW : 0) - 2 * mInsetX, vh = H - (HBar ? HBarH : 0) - 2 * mInsetY;
        if (c.X != mInsetX || c.Y != mInsetY || c.W != vw || c.H != vh)
        {
            c.X = mInsetX;
            c.Y = mInsetY;
            c.W = vw;
            c.H = vh;
        }
        ScrollX = Math.Clamp(ScrollX, 0, Math.Max(0, ContentW - vw));
        ScrollY = Math.Clamp(ScrollY, 0, Math.Max(0, ContentH - vh));
        if (Inner != null)
        {
            Inner.X = -ScrollX;
            Inner.Y = -ScrollY;
        }
    }

    public void ScrollBy(int dx, int dy)
    {
        ScrollX = Math.Max(0, ScrollX + dx);
        ScrollY = Math.Max(0, ScrollY + dy);
        PreRender();
    }

    // Part codes: 1 up arrow, 2 down arrow, 3 page up, 4 page down, 8 vertical thumb, 0 none (the horizontal bar mirrors).
    private (int top, int len) Thumb(bool vert)
    {
        int view = vert ? H - (HBar ? HBarH : 0) : W - (VBar ? VBarW : 0);
        int content = Math.Max(1, vert ? ContentH : ContentW);
        int a0 = vert ? VArrowTop : HArrowLeft, a1 = vert ? VArrowBottom : HArrowRight;
        int track = Math.Max(1, view - a0 - a1);
        int len = U("constantThumb") != 0 ? BarWidth : Math.Clamp(track * view / content, Math.Min(BarWidth, track), track);
        int range = Math.Max(1, content - view);
        int pos = (vert ? ScrollY : ScrollX) * (track - len) / range;
        return (a0 + pos, len);
    }

    private int Part(int lx, int ly)
    {
        if (VBar && lx >= W - VBarW)
        {
            if (ly < VArrowTop) return 1;
            if (ly >= H - (HBar ? HBarH : 0) - VArrowBottom) return 2;
            var (t, l) = Thumb(true);
            return ly < t ? 3 : ly >= t + l ? 4 : 8;
        }
        if (HBar && ly >= H - HBarH)
        {
            if (lx < HArrowLeft) return 5;
            if (lx >= W - (VBar ? VBarW : 0) - HArrowRight) return 6;
            var (t, l) = Thumb(false);
            return lx < t ? 7 : lx >= t + l ? 10 : 9;
        }
        return 0;
    }

    private void StepPart(int part)
    {
        int vh = H - (HBar ? HBarH : 0), vw = W - (VBar ? VBarW : 0);
        switch (part)
        {
            case 1: ScrollBy(0, -ScrollLineY); break;
            case 2: ScrollBy(0, ScrollLineY); break;
            case 3: ScrollBy(0, -vh); break;
            case 4: ScrollBy(0, vh); break;
            case 5: ScrollBy(-ScrollLineX, 0); break;
            case 6: ScrollBy(ScrollLineX, 0); break;
            case 7: ScrollBy(-vw, 0); break;
            case 10: ScrollBy(vw, 0); break;
        }
    }

    public override bool MsDown(GuiEvent e)
    {
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        int part = Part(lx, ly);
        if (part == 0)
            return base.MsDown(e);
        Canvas?.LockMouse(this);
        Canvas?.SetFirstResponder(this);
        mRepeatPart = part;
        if (part is 8 or 9)
        {
            mDragPart = part;
            var (t, _) = Thumb(part == 8);
            mGrabOffset = (part == 8 ? ly : lx) - t;
        }
        else
            StepPart(part);
        return true;
    }

    public override bool MsRepeat(GuiEvent e)
    {
        if (mRepeatPart != 0 && mDragPart == 0)
            StepPart(mRepeatPart);
        return true;
    }

    public override bool MsDrag(GuiEvent e)
    {
        if (mDragPart == 0)
            return base.MsDrag(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        bool vert = mDragPart == 8;
        int view = vert ? H - (HBar ? HBarH : 0) : W - (VBar ? VBarW : 0);
        int content = Math.Max(1, vert ? ContentH : ContentW);
        var (start, len) = Thumb(vert);
        int a0 = vert ? VArrowTop : HArrowLeft, a1 = vert ? VArrowBottom : HArrowRight;
        int track = Math.Max(1, view - a0 - a1) - len;
        int pos = Math.Clamp((vert ? ly : lx) - mGrabOffset - a0, 0, Math.Max(0, track));
        int v = track > 0 ? pos * Math.Max(0, content - view) / track : 0;
        if (vert) ScrollY = v; else ScrollX = v;
        PreRender();
        return true;
    }

    public override bool MsUp(GuiEvent e)
    {
        mDragPart = 0;
        mRepeatPart = 0;
        Canvas?.UnlockMouse(this);
        return true;
    }

    public override bool KeyDown(GuiEvent e)
    {
        if (U("handleArrowKeys") != 0)
        {
            switch (e.Scan)
            {
                case 0xc8: ScrollBy(0, -ScrollLineY); return true;
                case 0xd0: ScrollBy(0, ScrollLineY); return true;
                case 0xcb: ScrollBy(-ScrollLineX, 0); return true;
                case 0xcd: ScrollBy(ScrollLineX, 0); return true;
            }
        }
        return base.KeyDown(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        // A scroll with a header row (headerY) frames only the part below the header: the header has its own lines.
        int hy = (int)U("headerY");
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, FillColor);
        if (Border != 0)
            p.Outline(gx, gy + hy, W, H - hy, Disabled ? DisabledBorder : BorderColor);
        RenderChildren(p, gx, gy);
        PbmaArray? pc = Pieces;
        if (pc == null)
        {
            DrawPlainBars(p, gx, gy);
            return;
        }
        if (VBar)
            DrawVBar(p, pc, gx + W - VBarW, gy, H - (HBar ? HBarH : 0));
        if (HBar)
            DrawHBar(p, pc, gx, gy + H - HBarH, W - (VBar ? VBarW : 0));
        if (VBar && HBar)
            p.Bitmap(pc.Bitmaps[24], gx + W - VBarW, gy + H - HBarH);
    }

    private void DrawPlainBars(GuiPaint p, int gx, int gy)
    {
        if (VBar)
        {
            int bh = H - (HBar ? BarWidth : 0);
            p.Fill(gx + W - BarWidth, gy, BarWidth, bh, 0xe9);
            var (t, l) = Thumb(true);
            p.Fill(gx + W - BarWidth + 1, gy + t, BarWidth - 2, l, 0xf0);
        }
        if (HBar)
        {
            int bw = W - (VBar ? BarWidth : 0);
            p.Fill(gx, gy + H - BarWidth, bw, BarWidth, 0xe9);
            var (t, l) = Thumb(false);
            p.Fill(gx + t, gy + H - BarWidth + 1, l, BarWidth - 2, 0xf0);
        }
    }

    // The bar pieces of the scroll draw [code 0054a218 vertical, 0054a690 horizontal]. Vertical: arrows 0 to 2 (disabled, pressed,
    // normal) and 3 to 5, track 21 to 23 (disabled, pressed, normal), thumb caps 17 and 16 with the middle 15 (30, 29, 28 while
    // dragged when the array has more than 28 pieces). Horizontal: arrows 6 to 8 and 9 to 11, track 18 to 20, thumb 13, 14 and 12
    // (26, 27, 25 dragged). A bar whose content fits shows the disabled pieces without a thumb.
    private void DrawVBar(GuiPaint p, PbmaArray pc, int bx, int by, int length)
    {
        bool active = ContentH > H - (HBar ? HBarH : 0);
        var bm = pc.Bitmaps;
        p.Bitmap(bm[!active ? 0 : mRepeatPart == 1 ? 1 : 2], bx, by);
        int top = by + VArrowTop, end = by + length - VArrowBottom;
        if (!active)
            Fill(p, bm[21], bx, top, bm[21].Width, end - top, true);
        else
        {
            var (t, l) = Thumb(true);
            bool drag = mDragPart == 8 && bm.Count > 28;
            PbmpBitmap cap0 = bm[drag ? 30 : 17], mid = bm[drag ? 28 : 15], cap1 = bm[drag ? 29 : 16];
            int ty = by + t, te = ty + l;
            Fill(p, bm[mRepeatPart == 3 ? 22 : 23], bx, top, bm[23].Width, ty - top, true);
            p.Bitmap(cap0, bx, ty);
            Fill(p, mid, bx, ty + cap0.Height, mid.Width, te - cap1.Height - (ty + cap0.Height), true);
            p.Bitmap(cap1, bx, te - cap1.Height);
            Fill(p, bm[mRepeatPart == 4 ? 22 : 23], bx, te, bm[23].Width, end - te, true);
        }
        p.Bitmap(bm[!active ? 3 : mRepeatPart == 2 ? 4 : 5], bx, end);
    }

    private void DrawHBar(GuiPaint p, PbmaArray pc, int bx, int by, int length)
    {
        bool active = ContentW > W - (VBar ? VBarW : 0);
        var bm = pc.Bitmaps;
        p.Bitmap(bm[!active ? 6 : mRepeatPart == 5 ? 7 : 8], bx, by);
        int left = bx + HArrowLeft, end = bx + length - HArrowRight;
        if (!active)
            Fill(p, bm[18], left, by, end - left, bm[18].Height, false);
        else
        {
            var (t, l) = Thumb(false);
            bool drag = mDragPart == 9 && bm.Count > 25;
            PbmpBitmap cap0 = bm[drag ? 26 : 13], mid = bm[drag ? 25 : 12], cap1 = bm[drag ? 27 : 14];
            int tx = bx + t, te = tx + l;
            Fill(p, bm[mRepeatPart == 7 ? 19 : 20], left, by, tx - left, bm[20].Height, false);
            p.Bitmap(cap0, tx, by);
            Fill(p, mid, tx + cap0.Width, by, te - cap1.Width - (tx + cap0.Width), mid.Height, false);
            p.Bitmap(cap1, te - cap1.Width, by);
            Fill(p, bm[mRepeatPart == 10 ? 19 : 20], te, by, end - te, bm[20].Height, false);
        }
        p.Bitmap(bm[!active ? 9 : mRepeatPart == 6 ? 10 : 11], end, by);
    }

    private static void Fill(GuiPaint p, PbmpBitmap b, int x, int y, int w, int h, bool vertical)
    {
        if (w > 0 && h > 0)
            p.BitmapStretch(b, x, y, w, h);
    }
}

// FearGui combo boxes [code 004bba00, 0054d8bc]: a text line with an entry list; the popup is pushed as an overlay.
public sealed class GuiCombo : GuiSimpleText
{
    public readonly List<(string Text, int Id)> Entries = new();
    public int Selected = -1;

    public override bool WantsTab => false;
    public override bool CanTake() => false;

    public void SetSelectedIndex(int i, bool notify = false)
    {
        if (i < -1 || i >= Entries.Count)
            return;
        Selected = i;
        Text = i >= 0 ? Entries[i].Text : "";
        if (notify)
        {
            WriteVariable(i >= 0 ? Entries[i].Id.ToString(CultureInfo.InvariantCulture) : "");
            RunCommand();
            CallOnAction();
            PostMessage();
        }
    }

    public int IndexOfId(int id) => Entries.FindIndex(e => e.Id == id);

    // A saved name matches an entry that has it as its text or as its first word ("OpenGL" finds "OpenGL [renderer]", [code 004d9228]).
    public int FindEntryByName(string name)
    {
        int i = Entries.FindIndex(e => string.Equals(e.Text, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : Entries.FindIndex(e => string.Equals(GuiHost.FirstWord(e.Text), name, StringComparison.OrdinalIgnoreCase));
    }
    public int SelectedId => Selected >= 0 ? Entries[Selected].Id : -1;

    // A combo is a SimpleText: its value is the shown text (the scripts take getWord(Control::getValue(combo), 0), Options.cs).
    public override string GetValue() => Selected >= 0 ? Entries[Selected].Text : Text;
    protected override void ApplyValue(string v)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            SetSelectedIndex(IndexOfId(id));
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active || Canvas == null)
            return base.MsDown(e);
        Host?.Audio.Play("Button5.wav");
        var (gx, gy) = GlobalOrigin();
        Canvas.PushDialog(new ComboPopup(this, gx, gy + H));
        return true;
    }

    // The picture of each state: POP_Arrow + _DF, _HI, _MD, _MH, _ON, _NA (004b65e8).
    private PbmpBitmap? Arrow(string suffix) => Host?.BitmapByName("POP_Arrow" + suffix + ".BMP");

    // 004b6784: the height is the arrow picture plus four.
    public override void Wake()
    {
        base.Wake();
        PbmpBitmap? df = Arrow("_DF");
        if (df != null)
            H = df.Height + 4;
    }

    private PftFont? Tag(uint t) => Host?.FontByTag(t);

    // 004b6f80: two bars with rounded ends (palette 0xf0, 0xef, 0xee; 0xec, 0xeb, 0xea when dimmed), the label (the string the
    // file stores before the text), the arrow and the current entry, cut with "..." to the room that is left (004b6dfc).
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        bool dimmed = Dimmed;
        int k = dimmed ? 4 : 0;
        int[] pal = { 0xf0, 0xef, 0xee, 0xed, 0xec, 0xeb, 0xea, 0xe9 };
        int bottom = gy + H - 1;
        p.Fill(gx + 4, gy, W - 8, 1, pal[k + 1]);
        p.Fill(gx + 3, gy + 1, W - 6, 1, pal[k]);
        p.Fill(gx + 3, gy + 1, 1, 1, pal[k + 2]);
        p.Fill(gx + W - 4, gy + 1, 1, 1, pal[k + 2]);
        p.Fill(gx + 3, bottom - 1, W - 6, 1, pal[k]);
        p.Fill(gx + 4, bottom, W - 8, 1, pal[k + 1]);
        p.Fill(gx + 3, bottom - 1, 1, 1, pal[k + 2]);
        p.Fill(gx + W - 4, bottom - 1, 1, 1, pal[k + 2]);
        int left = gx + 10;
        string label = S("popupName");
        if (label.Length > 0)
        {
            PftFont? lf = Tag(150121);
            p.Text(Tag(150173), left, gy + 1, label);
            p.Text(dimmed ? Tag(150131) : lf, left + 1, gy, label);
            left += GuiPaint.TextWidth(lf, label) + 6;
        }
        PbmpBitmap? arrow = dimmed ? Arrow("_NA") : Pressed ? Arrow("_ON") : Over ? Arrow("_MD") : Arrow("_DF");
        arrow ??= Arrow("_DF");
        if (arrow == null)
            return;
        p.Bitmap(arrow, gx + W - 7 - arrow.Width, gy + 2);
        int room = gx + W - arrow.Width - 14 - left;
        PftFont? f = dimmed ? Tag(150134) : Tag(150114);
        PftFont? sh = Tag(150172);
        string t = Text;
        if (f == null)
            return;
        if (GuiPaint.TextWidth(f, t) >= room)
        {
            int dots = GuiPaint.TextWidth(f, "...");
            if (dots > room)
                return;
            while (t.Length > 0 && GuiPaint.TextWidth(f, t) > room - dots)
                t = t[..^1];
            t += "...";
        }
        p.Text(sh, left - 1, gy + 2, t);
        p.Text(f, left, gy + 1, t);
    }
}

// The popup list of a combo, pushed over the screen: a click on an entry selects it, any other click closes it.
public sealed class ComboPopup : GuiControl
{
    private readonly GuiCombo mCombo;
    private readonly int mGx, mGy;
    private const int RowH = 14;

    public ComboPopup(GuiCombo combo, int gx, int gy)
    {
        ClassName = "FearGui::ComboPopup";
        mCombo = combo;
        mGx = gx;
        mGy = gy;
        Flags = 3 | 4;
    }

    public override bool MsDown(GuiEvent e)
    {
        int row = (e.Y - mGy) / RowH;
        bool inside = e.X >= mGx && e.X < mGx + mCombo.W && e.Y >= mGy && row >= 0 && row < mCombo.Entries.Count;
        Canvas?.PopDialog(0, force: true);
        if (inside)
            mCombo.SetSelectedIndex(row, notify: true);
        return true;
    }

    public override bool KeyDown(GuiEvent e)
    {
        if (e.Scan == 1)
            Canvas?.PopDialog(0, force: true);
        return true;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        PftFont? f = Host?.FontByName("sf_white_10.pft");
        int h = mCombo.Entries.Count * RowH;
        p.Fill(mGx, mGy, mCombo.W, h, 0xf4);
        GuiBox.Bevel(p, mGx, mGy, mCombo.W, h, false);
        for (int i = 0; i < mCombo.Entries.Count; ++i)
        {
            if (i == mCombo.Selected)
                p.Fill(mGx + 1, mGy + i * RowH, mCombo.W - 2, RowH, 0xf8);
            p.Text(f, mGx + 4, mGy + i * RowH + 1, mCombo.Entries[i].Text);
        }
    }
}
