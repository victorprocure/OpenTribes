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

    protected virtual PftFont? RowFont => Host?.FontByTag(U("fontTag")) ?? Host?.FontByName("sf_white_10.pft");
    public int RowHeight => Math.Max(8, GuiPaint.FontHeight(RowFont) + 2);

    public void AddEntry(string text, int id) => Entries.Add((text, id));
    public void Clear() { Entries.Clear(); Selected = -1; }

    public int FindId(int id) => Entries.FindIndex(e => e.Id == id);
    public int FindText(string text) => Entries.FindIndex(e => string.Equals(e.Text, text, StringComparison.OrdinalIgnoreCase));

    public void SortText() => Entries.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));

    public void DeleteById(int id)
    {
        int i = FindId(id);
        if (i < 0)
            return;
        Entries.RemoveAt(i);
        if (Selected == i) Selected = -1; else if (Selected > i) --Selected;
    }

    public void Select(int row, bool notify = true)
    {
        if (row < -1 || row >= Entries.Count)
            return;
        Selected = row;
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
    public int ScrollX, ScrollY;
    private int mDragPart;      // 8 vertical thumb, 9 horizontal thumb
    private int mGrabOffset;
    private int mRepeatPart;

    public override bool WantsTab => true;
    public override bool CanTake() => Visible;

    private GuiControl? ContentChild => Children.FirstOrDefault(c => c is GuiScrollContent);
    private GuiControl? Inner => ContentChild?.Children.FirstOrDefault();

    private int ContentW => Inner == null ? 0 : Inner.X + ScrollX + Inner.W;
    private int ContentH => Inner == null ? 0 : Inner.Y + ScrollY + Inner.H;

    // Policies: 0 never, 1 always, 2 when needed (the inspector order).
    private bool VBar => U("verticalPolicy") == 1 || U("verticalPolicy") == 2 && ContentH > H;
    private bool HBar => U("horizontalPolicy") == 1 || U("horizontalPolicy") == 2 && ContentW > W;

    public override void PreRender()
    {
        GuiControl? c = ContentChild;
        if (c == null)
            return;
        int vw = W - (VBar ? BarWidth : 0), vh = H - (HBar ? BarWidth : 0);
        if (c.X != 0 || c.Y != 0 || c.W != vw || c.H != vh)
        {
            c.X = c.Y = 0;
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
        int view = vert ? H - (HBar ? BarWidth : 0) : W - (VBar ? BarWidth : 0);
        int content = Math.Max(1, vert ? ContentH : ContentW);
        int track = Math.Max(1, view - 2 * BarWidth);
        int len = U("constantThumb") != 0 ? BarWidth : Math.Clamp(track * view / content, BarWidth, track);
        int range = Math.Max(1, content - view);
        int pos = (vert ? ScrollY : ScrollX) * (track - len) / range;
        return (BarWidth + pos, len);
    }

    private int Part(int lx, int ly)
    {
        if (VBar && lx >= W - BarWidth)
        {
            if (ly < BarWidth) return 1;
            if (ly >= H - (HBar ? BarWidth : 0) - BarWidth) return 2;
            var (t, l) = Thumb(true);
            return ly < t ? 3 : ly >= t + l ? 4 : 8;
        }
        if (HBar && ly >= H - BarWidth)
        {
            if (lx < BarWidth) return 5;
            if (lx >= W - (VBar ? BarWidth : 0) - BarWidth) return 6;
            var (t, l) = Thumb(false);
            return lx < t ? 7 : lx >= t + l ? 10 : 9;
        }
        return 0;
    }

    private void StepPart(int part)
    {
        int vh = H - (HBar ? BarWidth : 0), vw = W - (VBar ? BarWidth : 0);
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
        int view = vert ? H - (HBar ? BarWidth : 0) : W - (VBar ? BarWidth : 0);
        int content = Math.Max(1, vert ? ContentH : ContentW);
        var (_, len) = Thumb(vert);
        int track = Math.Max(1, view - 2 * BarWidth) - len;
        int pos = Math.Clamp((vert ? ly : lx) - mGrabOffset - BarWidth, 0, Math.Max(0, track));
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
        DrawBase(p, gx, gy);
        RenderChildren(p, gx, gy);
        if (VBar)
        {
            int bh = H - (HBar ? BarWidth : 0);
            p.Fill(gx + W - BarWidth, gy, BarWidth, bh, 0xe9);
            var (t, l) = Thumb(true);
            p.Fill(gx + W - BarWidth + 1, gy + t, BarWidth - 2, l, 0xf0);
            p.Outline(gx + W - BarWidth, gy, BarWidth, BarWidth, 0xf0);
            p.Outline(gx + W - BarWidth, gy + bh - BarWidth, BarWidth, BarWidth, 0xf0);
        }
        if (HBar)
        {
            int bw = W - (VBar ? BarWidth : 0);
            p.Fill(gx, gy + H - BarWidth, bw, BarWidth, 0xe9);
            var (t, l) = Thumb(false);
            p.Fill(gx + t, gy + H - BarWidth + 1, l, BarWidth - 2, 0xf0);
        }
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
    public int SelectedId => Selected >= 0 ? Entries[Selected].Id : -1;

    public override string GetValue() => SelectedId.ToString(CultureInfo.InvariantCulture);
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

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        GuiBox.Bevel(p, gx, gy, W, H, Dimmed);
        PftFont? f = Host?.FontByName(Dimmed ? "sf_grey100_10b.pft" : "sf_white_10.pft");
        p.Text(f, gx + 4, gy + Math.Max(0, (H - GuiPaint.FontHeight(f)) / 2), Text);
        RenderChildren(p, gx, gy);
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
