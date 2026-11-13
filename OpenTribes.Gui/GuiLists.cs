namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;

/// <summary>SimGui::ArrayCtrl and every list class derived from it: one column of (text, id) rows (docs/gui.md 3.3 "Lists and array controls").</summary>
/// <remarks>The exe has a cell routine per list class (slot 0x110). SimGui::TextList and METextList draw as the exact rule below (0054b65c, cell size 0054b845). The other classes that map to this one (the StandardListCtrl family 004b7948, FGTextList 00489f2c, the command lists and the rest) still draw the common look of this class, which is not one of those routines.</remarks>
public class GuiList : GuiActive
{
    /// <summary>The rows as (text, id).</summary>
    public readonly List<(string Text, int Id)> Entries = [];
    /// <summary>The selected row index, -1 for none.</summary>
    public int Selected = -1;

    /// <summary>Gets a value that is true for FGTextList.</summary>
    /// <remarks>Its add-to-manager routine (00489d00) loads the three fonts of the list itself: sf_orange214_10.pft for a row, sf_white_10.pft for the selected row and sf_grey100_10b.pft (the dimmed case, not modelled). It runs after the file is read, so the font tags of the file (all 0 in the stock screens) do not matter. The cell is 640 wide and the orange font height + 4 high.</remarks>
    private bool FgTextList => ClassName == "FearGui::FGTextList";
    /// <summary>Gets the font of the rows.</summary>
    protected virtual PftFont? RowFont => FgTextList ? Host?.FontByName("sf_orange214_10.pft") : Host?.FontByTag(U("fontTag")) ?? Host?.FontByName("sf_white_10.pft");

    private bool ExactTextList => ClassName is "SimGui::TextList" or "METextList";
    /// <summary>Gets the height of a row.</summary>
    /// <remarks>For TextList (0054b845) the cell is the control's width wide and the font height + the vertical offset of the file + 4 high.</remarks>
    public int RowHeight => FgTextList ? GuiPaint.FontHeight(RowFont) + 4
        : ExactTextList ? GuiPaint.FontHeight(RowFont) + (int)U("textVOffset") + 4 : Math.Max(8, GuiPaint.FontHeight(RowFont) + 2);
    private bool IsTextList => ClassName.EndsWith("TextList", StringComparison.Ordinal);

    /// <summary>Adds a row.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id.</param>
    public void AddEntry(string text, int id) => Entries.Add((text, id));
    /// <summary>Removes every row and the selection.</summary>
    public virtual void Clear() { Entries.Clear(); Selected = -1; }

    /// <summary>Finds a row by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The row index, -1 when not found.</returns>
    public int FindId(int id) => Entries.FindIndex(e => e.Id == id);
    /// <summary>Finds a row by text, case insensitive.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The row index, -1 when not found.</returns>
    public int FindText(string text) => Entries.FindIndex(e => string.Equals(e.Text, text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sorts the rows by text, case insensitive.</summary>
    public void SortText() => Entries.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the row with an id.</summary>
    /// <param name="id">The id.</param>
    public virtual void DeleteById(int id)
    {
        var i = FindId(id);
        if (i < 0)
            return;
        Entries.RemoveAt(i);
        if (Selected == i)
            Selected = -1;
        else if (Selected > i)
            --Selected;
    }

    /// <summary>Moves the list so that a row shows.</summary>
    /// <param name="row">The row index.</param>
    /// <remarks>Follows the cell select (0053c9dc) which ends in 0053caa0: when the selected cell is not inside the parent content control (its top at or above 0, or its bottom below the content height) the list moves by the missing distance. Only the vertical case of the single column is modelled (the column is as wide as the content).</remarks>
    private void ScrollIntoView(int row)
    {
        if (Parent is not GuiScrollContent pc || pc.Parent is not GuiScroll sc)
            return;
        var top = (row * RowHeight) + Y;
        var dy = top < 1 ? -top : pc.H < top + RowHeight ? pc.H - (top + RowHeight) : 0;
        if (dy != 0)
            sc.ScrollBy(0, -dy);
    }

    /// <summary>Runs the class's own reaction to a selected row (the master list sets its console variables).</summary>
    /// <param name="row">The row index.</param>
    protected virtual void Selecting(int row) { }

    /// <summary>Returns whether the class takes the selection of a row (the command lists refuse it while the command map is missing).</summary>
    /// <param name="row">The row index.</param>
    /// <returns>True when the row can be selected.</returns>
    protected virtual bool Accepts(int row) => true;

    /// <summary>Selects a row.</summary>
    /// <param name="row">The row index, -1 for none.</param>
    /// <param name="notify">True to write the console variable, run the command, call `onSelect` and post the message.</param>
    /// <remarks>The control's console command runs on every selection (the recordings list enables its buttons this way).</remarks>
    public void Select(int row, bool notify = true)
    {
        if (row < -1 || row >= Entries.Count || !Accepts(row))
            return;
        Selected = row;
        Selecting(row);
        if (IsTextList)
            ScrollIntoView(row);
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

    /// <inheritdoc/>
    /// <remarks>A text list answers with the text of the selected line (the stock scripts build file names from it: `recordings\\ @ Control::getValue(RecordingsTextList) @ .rec`). The other array controls answer the row index.</remarks>
    public override string GetValue() => ClassName.EndsWith("TextList", StringComparison.Ordinal)
        ? (Selected >= 0 && Selected < Entries.Count ? Entries[Selected].Text : "")
        : Selected.ToString(CultureInfo.InvariantCulture);
    /// <inheritdoc/>
    /// <remarks>A text list takes a value by its text: the set-value slot (0054bf54) walks the lines, compares the text byte for byte and selects the first line that is equal through the cell select slot (0x100, 00489cb8 for FGTextList), which evaluates the control's command and nothing else. The variable-changed slot of the family is a bare return (00539dcc), so the bound variable (`$pref::LastMission` of the mission list) does not select anything.</remarks>
    protected override void ApplyValue(string v)
    {
        if (IsTextList)
        {
            var row = Entries.FindIndex(e => string.Equals(e.Text, v, StringComparison.Ordinal));
            if (row >= 0)
            {
                Selected = row;
                ScrollIntoView(row);
                if (Command.Length > 0)
                    Host?.Eval(Command);
            }
            return;
        }
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            Select(Math.Clamp(i, -1, Entries.Count - 1), notify: false);
    }

    /// <inheritdoc/>
    public override void VariableChanged(string text)
    {
        if (!IsTextList)
            base.VariableChanged(text);
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Parent is GuiScrollContent pc)
            H = Math.Max(pc.H, Entries.Count * RowHeight);
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        var r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        var row = ly / RowHeight;
        if (Active && row >= 0 && row < Entries.Count)
        {
            Select(row);
            if (e.ClickCount >= 2 && Name != null && Host != null && Host.HasFunction(Name + "::onDoubleClick"))
                Host.Call(Name + "::onDoubleClick", row.ToString(CultureInfo.InvariantCulture));
        }
        return r;
    }

    /// <summary>Does nothing: the row action comes with the mouse up in an ActiveCtrl, but a list selects on press, so the release only unlocks.</summary>
    protected override void Action() { }

    /// <inheritdoc/>
    /// <remarks>Follows ArrayCtrl key down (0053d4dc): an inactive control passes every key on. Up (0xc8), left (0xcb), right (0xcd) and down (0xd0) step the selected cell by one in that direction and the first cell that accepts the selection takes it (every row of this single column list does). With no selected cell any of the four keys selects the first row. A step that leaves the grid does nothing. The other keys go to the ActiveCtrl handler.</remarks>
    public override bool KeyDown(GuiEvent e)
    {
        if (!Active)
            return base.KeyDown(e);
        int dx, dy;
        switch (e.Scan)
        {
            case 0xc8:
                dx = 0;
                dy = -1;
                break;
            case 0xcb:
                dx = -1;
                dy = 0;
                break;
            case 0xcd:
                dx = 1;
                dy = 0;
                break;
            case 0xd0:
                dx = 0;
                dy = 1;
                break;
            default:
                return base.KeyDown(e);
        }
        if (Entries.Count < 1)
            return true;
        if (Selected < 0)
        {
            Select(0);
            return true;
        }
        int col = dx, row = Selected + dy;
        if (col == 0 && row >= 0 && row < Entries.Count)
            Select(row);
        return true;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        PftFont? f = RowFont;
        var rh = RowHeight;
        for (var i = 0; i < Entries.Count; ++i)
        {
            var y = gy + (i * rh);
            if (y > p.Clip.Bottom)
                break;
            if (y + rh < p.Clip.Top)
                continue;
            if (FgTextList)
            {
                // Cell draw [code 00489f2c]: the selected cell is filled with palette 0xfe (opaque), the text sits at (x + 10, y + vertical offset)
                // in the white font when selected and the orange font otherwise.
                var sel = i == Selected;
                if (sel)
                    p.Fill(gx, y, W, rh, 0xfe);
                p.Text(sel ? Host?.FontByName("sf_white_10.pft") : f, gx + 10, y + (int)U("textVOffset"), Entries[i].Text);
                continue;
            }
            if (ExactTextList)
            {
                // [code 0054b65c]: the text at (x + 10, y + vertical offset); the selected cell is filled with palette 0xf9 and its text
                // uses the highlight font.
                var sel = i == Selected;
                if (sel)
                    p.Fill(gx, y, W, rh, 0xf9);
                PftFont? tf = sel ? Host?.FontByTag(U("highlightFontTag")) ?? f : f;
                p.Text(tf, gx + 10, y + (int)U("textVOffset"), Entries[i].Text);
                continue;
            }
            if (i == Selected)
                p.Fill(gx, y, W, rh, 0xf8);
            p.Text(f, gx + 2, y + 1, Entries[i].Text);
        }
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The content control inside a scroll control.</summary>
public sealed class GuiScrollContent : GuiControl { }

/// <summary>The scroll control (SimGui::ScrollCtrl and the classes derived from it, 0054a0e0, 005491d4): a viewport with optional bars drawn with palette colors.</summary>
/// <remarks>The bar pieces follow the draw routines named at <c>DrawVBar</c> and <c>DrawHBar</c>.</remarks>
public class GuiScroll : GuiControl
{
    // TODO: compare the arrow, page and key handlers (005499dc to 00549b00, 00549acc) with the exe.
    /// <summary>The width of a bar when the piece array is missing, and the minimum thumb length.</summary>
    public const int BarWidth = 15;

    // The piece array of the bars (docs/gui.md "Scroll controls": the bar sizes are the sizes of its pieces).
    private PbmaArray? Pieces
    {
        get
        {
            var t = Host?.TagText(U("pbaTag"));
            PbmaArray? a = t == null ? null : Host?.ArrayByName(t.Split(',')[0].Trim());
            return a != null && a.Bitmaps.Count >= 25 ? a : null;
        }
    }

    private static int Piece(PbmaArray? a, int i, bool width, int def) => a == null ? def : width ? a.Bitmaps[i].Width : a.Bitmaps[i].Height;
    private int VBarW => Piece(Pieces, 0, true, BarWidth);       // vertical bar width = width of its arrow pieces
    private int HBarH => Piece(Pieces, 6, false, BarWidth);      // horizontal bar height
    private int VArrowTop => Piece(Pieces, 0, false, BarWidth);
    private int VArrowBottom => Piece(Pieces, 3, false, BarWidth);
    private int HArrowLeft => Piece(Pieces, 6, true, BarWidth);
    private int HArrowRight => Piece(Pieces, 9, true, BarWidth);
    /// <summary>The horizontal scroll offset.</summary>
    public int ScrollX;
    /// <summary>The vertical scroll offset.</summary>
    public int ScrollY;
    /// <summary>True when the list inside is disabled, so the border takes its disabled color (+0x270 of the original, 00548944, 004b4584).</summary>
    public bool Disabled;
    private int ViewW => W - (2 * mInsetX);
    private int ViewH => H - (2 * mInsetY);
    private bool mInsetKnown;
    private int mInsetX, mInsetY;
    private int mDragPart;      // 8 vertical thumb, 9 horizontal thumb
    private int mGrabOffset;
    private int mRepeatPart;

    /// <summary>Gets a value that is true unless the class is FearGuiScrollCtrl.</summary>
    /// <remarks>FearGuiScrollCtrl overrides the tab stop slot with a 4 byte stub (004b8bc4), taken as `return false`: the scroll frames of the game screens never hold the keyboard, so Tab and the movement keys reach the action maps while the score list is up. The plain SimGui::ScrollCtrl keeps the base rule.</remarks>
    private bool KeyboardStop => ClassName != "FearGui::FearGuiScrollCtrl";
    /// <inheritdoc/>
    public override bool WantsTab => KeyboardStop;
    // Gain-focus query (responder slot 0x40, 00549934): the answer is the "handle arrow keys" byte (+0x281). A scroll frame without
    // it never becomes the first responder, so every key reaches the action maps (the inventory screen closes with its key).
    /// <inheritdoc/>
    /// <remarks>Follows the gain-focus query (responder slot 0x40, 00549934): the answer is the "handle arrow keys" byte (+0x281). A scroll frame without it never becomes the first responder, so every key reaches the action maps (the inventory screen closes with its key).</remarks>
    public override bool CanTake() => U("handleArrowKeys") != 0;

    private GuiControl? ContentChild => Children.FirstOrDefault(c => c is GuiScrollContent);
    private GuiControl? Inner => ContentChild?.Children.FirstOrDefault();

    private int ContentW => Inner == null ? 0 : Inner.X + ScrollX + Inner.W;
    private int ContentH => Inner == null ? 0 : Inner.Y + ScrollY + Inner.H;

    /// <summary>Gets a value that is true when the vertical bar shows. Policy values: 0 always, 1 never, 2 when needed.</summary>
    /// <remarks>The stock files show the order: CmdInventory.gui (horizontal 1, vertical 0) sizes its list 19 pixels narrower than the frame for a vertical bar and has no use for a horizontal one. command.gui (1, 2) scrolls vertically only.</remarks>
    private bool VBar => U("verticalPolicy") == 0 || U("verticalPolicy") == 2 && ContentH > ViewH;
    /// <summary>Gets a value that is true when the horizontal bar shows. The policy values are those of <see cref="VBar"/>.</summary>
    private bool HBar => U("horizontalPolicy") == 0 || U("horizontalPolicy") == 2 && ContentW > ViewW;

    /// <inheritdoc/>
    /// <remarks>The content keeps the offset its file gives (the Columns options scroll has it at 1, 1 inside its border).</remarks>
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
        int vw = W - (VBar ? VBarW : 0) - (2 * mInsetX), vh = H - (HBar ? HBarH : 0) - (2 * mInsetY);
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

    /// <summary>Scrolls by a distance and lays the content out again.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void ScrollBy(int dx, int dy)
    {
        ScrollX = Math.Max(0, ScrollX + dx);
        ScrollY = Math.Max(0, ScrollY + dy);
        PreRender();
    }

    /// <summary>Returns the thumb of a bar.</summary>
    /// <param name="vert">True for the vertical bar.</param>
    /// <returns>The thumb start and length.</returns>
    private (int top, int len) Thumb(bool vert)
    {
        var view = vert ? ViewH - (HBar ? HBarH : 0) : ViewW - (VBar ? VBarW : 0);
        var content = Math.Max(1, vert ? ContentH : ContentW);
        int a0 = vert ? VArrowTop : HArrowLeft, a1 = vert ? VArrowBottom : HArrowRight;
        var track = Math.Max(1, view - a0 - a1);
        var len = U("constantThumb") != 0 ? BarWidth : Math.Clamp(track * view / content, Math.Min(BarWidth, track), track);
        var range = Math.Max(1, content - view);
        var pos = (vert ? ScrollY : ScrollX) * (track - len) / range;
        return (a0 + pos, len);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004c2c70 (FearGuiScrollCtrl): the grab hand while a thumb is dragged, the open hand over a thumb, the hand elsewhere on the frame.</remarks>
    public override uint CursorTagAt(int lx, int ly)
    {
        if (ClassName != "FearGui::FearGuiScrollCtrl")
            return 0;
        if (mDragPart != 0)
            return mDragPart is 8 or 9 ? CursorGrab : CursorHand;
        return Part(lx, ly) is 8 or 9 ? CursorOpenHand : CursorHand;
    }

    private int Part(int lx, int ly)
    {
        lx -= mInsetX;
        ly -= mInsetY;
        if (VBar && lx >= ViewW - VBarW)
        {
            if (ly < VArrowTop)
                return 1;
            if (ly >= ViewH - (HBar ? HBarH : 0) - VArrowBottom)
                return 2;
            var (t, l) = Thumb(true);
            return ly < t ? 3 : ly >= t + l ? 4 : 8;
        }
        if (HBar && ly >= ViewH - HBarH)
        {
            if (lx < HArrowLeft)
                return 5;
            if (lx >= ViewW - (VBar ? VBarW : 0) - HArrowRight)
                return 6;
            var (t, l) = Thumb(false);
            return lx < t ? 7 : lx >= t + l ? 10 : 9;
        }
        return 0;
    }

    private void StepPart(int part)
    {
        int vh = ViewH - (HBar ? HBarH : 0), vw = ViewW - (VBar ? VBarW : 0);
        switch (part)
        {
            case 1:
                ScrollBy(0, -ScrollLineY);
                break;
            case 2:
                ScrollBy(0, ScrollLineY);
                break;
            case 3:
                ScrollBy(0, -vh);
                break;
            case 4:
                ScrollBy(0, vh);
                break;
            case 5:
                ScrollBy(-ScrollLineX, 0);
                break;
            case 6:
                ScrollBy(ScrollLineX, 0);
                break;
            case 7:
                ScrollBy(-vw, 0);
                break;
            case 10:
                ScrollBy(vw, 0);
                break;
        }
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        var part = Part(lx, ly);
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

    /// <inheritdoc/>
    public override bool MsRepeat(GuiEvent e)
    {
        if (mRepeatPart != 0 && mDragPart == 0)
            StepPart(mRepeatPart);
        return true;
    }

    /// <inheritdoc/>
    public override bool MsDrag(GuiEvent e)
    {
        if (mDragPart == 0)
            return base.MsDrag(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        var vert = mDragPart == 8;
        var view = vert ? ViewH - (HBar ? HBarH : 0) : ViewW - (VBar ? VBarW : 0);
        var content = Math.Max(1, vert ? ContentH : ContentW);
        var (start, len) = Thumb(vert);
        int a0 = vert ? VArrowTop : HArrowLeft, a1 = vert ? VArrowBottom : HArrowRight;
        var track = Math.Max(1, view - a0 - a1) - len;
        var pos = Math.Clamp((vert ? ly : lx) - mGrabOffset - a0, 0, Math.Max(0, track));
        var v = track > 0 ? pos * Math.Max(0, content - view) / track : 0;
        if (vert)
            ScrollY = v;
        else
            ScrollX = v;
        PreRender();
        return true;
    }

    /// <inheritdoc/>
    public override bool MsUp(GuiEvent e)
    {
        mDragPart = 0;
        mRepeatPart = 0;
        Canvas?.UnlockMouse(this);
        return true;
    }

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e)
    {
        if (U("handleArrowKeys") != 0)
        {
            switch (e.Scan)
            {
                case 0xc8:
                    ScrollBy(0, -ScrollLineY);
                    return true;
                case 0xd0:
                    ScrollBy(0, ScrollLineY);
                    return true;
                case 0xcb:
                    ScrollBy(-ScrollLineX, 0);
                    return true;
                case 0xcd:
                    ScrollBy(ScrollLineX, 0);
                    return true;
            }
        }
        return base.KeyDown(e);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        // A scroll with a header row (headerY) frames only the part below the header: the header has its own lines.
        var hy = (int)U("headerY");
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, FillColor);
        // The frame slot of FearGuiScrollCtrl [code 004c2df4]: a style below 2 is the base frame (the outline here), a style of 2 or
        // more replaces it with the shell frame below.
        if (U("style") >= 2)
            DrawListFrame(p, gx, gy, Disabled);
        else if (Border != 0)
            p.Outline(gx, gy + hy, W, H - hy, Disabled ? DisabledBorder : BorderColor);
        RenderChildren(p, gx, gy);
        PbmaArray? pc = Pieces;
        if (pc == null)
        {
            DrawPlainBars(p, gx, gy);
            return;
        }
        if (VBar)
            DrawVBar(p, pc, gx + W - mInsetX - VBarW, gy + mInsetY, ViewH - (HBar ? HBarH : 0));
        if (HBar)
            DrawHBar(p, pc, gx + mInsetX, gy + H - mInsetY - HBarH, ViewW - (VBar ? VBarW : 0));
        if (VBar && HBar)
            p.Bitmap(pc.Bitmaps[24], gx + W - mInsetX - VBarW, gy + H - mInsetY - HBarH);
    }

    /// <summary>Draws the frame of a FearGuiScrollCtrl with a style of 2 or more.</summary>
    /// <remarks>Follows 004c2df4 (palette table 00628c04: 0xf0 0xef 0xee 0xf1, dimmed 0xec 0xeb 0xea 0xec). On each side three rules in the order outside to inside (the table colors 0, 1 and the constant 0xf4; the top and bottom from x + 4 to x1 - 4, the sides from y + 4 to y1 - 4), a notch pixel in color 0 next to the second rule of each corner, then three outlines 3, 4 and 5 pixels in (table colors 2 and 3, then 0xf4). A disabled or dimmed list (a modal dialog above it) takes the dimmed half of the table, the constant 0xf4 stays. The opaque fill of the file is drawn before it.</remarks>
    private void DrawListFrame(GuiPaint p, int x, int y, bool dim)
    {
        int d = dim ? 4 : 0, c0 = 0xf0 - d, c1 = 0xef - d, c2 = 0xee - d, c3 = dim ? 0xec : 0xf1;
        const int dark = 0xf4;
        int x1 = x + W - 1, y1 = y + H - 1;
        p.Line(x + 4, y, x1 - 4, y, c0);
        p.Line(x + 4, y + 1, x1 - 4, y + 1, c1);
        p.Line(x + 4, y + 2, x1 - 4, y + 2, dark);
        p.Fill(x + 3, y + 1, 1, 1, c0);
        p.Fill(x1 - 3, y + 1, 1, 1, c0);
        p.Line(x + 4, y1, x1 - 4, y1, c0);
        p.Line(x + 4, y1 - 1, x1 - 4, y1 - 1, c1);
        p.Line(x + 4, y1 - 2, x1 - 4, y1 - 2, dark);
        p.Fill(x + 3, y1 - 1, 1, 1, c0);
        p.Fill(x1 - 3, y1 - 1, 1, 1, c0);
        p.Line(x, y + 4, x, y1 - 4, c0);
        p.Line(x + 1, y + 4, x + 1, y1 - 4, c1);
        p.Line(x + 2, y + 4, x + 2, y1 - 4, dark);
        p.Fill(x + 1, y + 3, 1, 1, c0);
        p.Fill(x + 1, y1 - 3, 1, 1, c0);
        p.Line(x1, y + 4, x1, y1 - 4, c0);
        p.Line(x1 - 1, y + 4, x1 - 1, y1 - 4, c1);
        p.Line(x1 - 2, y + 4, x1 - 2, y1 - 4, dark);
        p.Fill(x1 - 1, y + 3, 1, 1, c0);
        p.Fill(x1 - 1, y1 - 3, 1, 1, c0);
        p.Outline(x + 3, y + 3, W - 6, H - 6, c2);
        p.Outline(x + 4, y + 4, W - 8, H - 8, c3);
        p.Outline(x + 5, y + 5, W - 10, H - 10, dark);
    }

    private void DrawPlainBars(GuiPaint p, int gx, int gy)
    {
        if (VBar)
        {
            var bh = H - (HBar ? BarWidth : 0);
            p.Fill(gx + W - BarWidth, gy, BarWidth, bh, 0xe9);
            var (t, l) = Thumb(true);
            p.Fill(gx + W - BarWidth + 1, gy + t, BarWidth - 2, l, 0xf0);
        }
        if (HBar)
        {
            var bw = W - (VBar ? BarWidth : 0);
            p.Fill(gx, gy + H - BarWidth, bw, BarWidth, 0xe9);
            var (t, l) = Thumb(false);
            p.Fill(gx + t, gy + H - BarWidth + 1, l, BarWidth - 2, 0xf0);
        }
    }

    /// <summary>Draws the vertical bar pieces.</summary>
    /// <remarks>Follows 0054a218 (vertical) and 0054a690 (horizontal). Vertical: arrows 0 to 2 (disabled, pressed, normal) and 3 to 5, track 21 to 23 (disabled, pressed, normal), thumb caps 17 and 16 with the middle 15 (30, 29, 28 while dragged when the array has more than 28 pieces). Horizontal: arrows 6 to 8 and 9 to 11, track 18 to 20, thumb 13, 14 and 12 (26, 27, 25 dragged). A bar whose content fits shows the disabled pieces without a thumb.</remarks>
    private void DrawVBar(GuiPaint p, PbmaArray pc, int bx, int by, int length)
    {
        var active = !Disabled && ContentH > ViewH - (HBar ? HBarH : 0);
        var bm = pc.Bitmaps;
        p.Bitmap(bm[!active ? 0 : mRepeatPart == 1 ? 1 : 2], bx, by);
        int top = by + VArrowTop, end = by + length - VArrowBottom;
        if (!active)
            Fill(p, bm[21], bx, top, bm[21].Width, end - top, true);
        else
        {
            var (t, l) = Thumb(true);
            var drag = mDragPart == 8 && bm.Count > 28;
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
        var active = !Disabled && ContentW > ViewW - (VBar ? VBarW : 0);
        var bm = pc.Bitmaps;
        p.Bitmap(bm[!active ? 6 : mRepeatPart == 5 ? 7 : 8], bx, by);
        int left = bx + HArrowLeft, end = bx + length - HArrowRight;
        if (!active)
            Fill(p, bm[18], left, by, end - left, bm[18].Height, false);
        else
        {
            var (t, l) = Thumb(false);
            var drag = mDragPart == 9 && bm.Count > 25;
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

/// <summary>The combo box (FearGui combo boxes, 004bba00, 0054d8bc): a text line with an entry list. The popup is pushed as an overlay.</summary>
public sealed class GuiCombo : GuiSimpleText
{
    /// <inheritdoc/>
    /// <remarks>Follows 004b65e0: the hand.</remarks>
    public override uint CursorTagAt(int lx, int ly) => CursorHand;

    /// <summary>The entries as (text, id).</summary>
    public readonly List<(string Text, int Id)> Entries = [];
    /// <summary>The selected entry index, -1 for none.</summary>
    public int Selected = -1;

    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;

    /// <summary>Selects an entry and shows its text.</summary>
    /// <param name="i">The entry index, -1 for none.</param>
    /// <param name="notify">True to write the console variable, run the command, call `onAction` and post the message.</param>
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

    /// <summary>Finds an entry by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The entry index, -1 when not found.</returns>
    public int IndexOfId(int id) => Entries.FindIndex(e => e.Id == id);

    /// <summary>Finds an entry by a saved name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The index of the entry that has it as its text or as its first word ("OpenGL" finds "OpenGL [renderer]", 004d9228), -1 when not found.</returns>
    public int FindEntryByName(string name)
    {
        var i = Entries.FindIndex(e => string.Equals(e.Text, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : Entries.FindIndex(e => string.Equals(GuiHost.FirstWord(e.Text), name, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Gets the id of the selected entry, -1 for none.</summary>
    public int SelectedId => Selected >= 0 ? Entries[Selected].Id : -1;

    /// <inheritdoc/>
    /// <remarks>A combo is a SimpleText: its value is the shown text (the scripts take getWord(Control::getValue(combo), 0), Options.cs).</remarks>
    public override string GetValue() => Selected >= 0 ? Entries[Selected].Text : Text;
    /// <inheritdoc/>
    protected override void ApplyValue(string v)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            SetSelectedIndex(IndexOfId(id));
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (!Active || Canvas == null)
            return base.MsDown(e);
        Host?.Audio.Play("Button5.wav");
        var (gx, gy) = GlobalOrigin();
        Canvas.PushDialog(new ComboPopup(this, gx, gy + H));
        return true;
    }

    /// <summary>Returns the arrow picture of a state: POP_Arrow + _DF, _HI, _MD, _MH, _ON or _NA (004b65e8).</summary>
    /// <param name="suffix">The state suffix.</param>
    /// <returns>The bitmap, or null.</returns>
    private PbmpBitmap? Arrow(string suffix) => Host?.BitmapByName("POP_Arrow" + suffix + ".BMP");

    /// <inheritdoc/>
    /// <remarks>Follows 004b6784: the height is the arrow picture plus four. The filter combos always list "&lt;None&gt;" first (filter 0 or less: none) (004d8fe4). The join screen's file text is a leftover.</remarks>
    public override void Wake()
    {
        base.Wake();
        PbmpBitmap? df = Arrow("_DF");
        if (df != null)
            H = df.Height + 4;
        // The filter combos always list "<None>" first (filter 0 or less: none) [code 004d8fe4]; the join screen's file text is a leftover.
        if (ClassName == "FearGui::FGServerFilterComboBox" && Entries.Count == 0)
        {
            Entries.Add(("<None>", 0));
            SetSelectedIndex(0);
        }
    }

    private PftFont? Tag(uint t) => Host?.FontByTag(t);

    /// <inheritdoc/>
    /// <remarks>Follows 004b6f80: two bars with rounded ends (palette 0xf0, 0xef, 0xee, and 0xec, 0xeb, 0xea when dimmed), the label (the string the file stores before the text), the arrow and the current entry, cut with "..." to the room that is left (004b6dfc).</remarks>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var dimmed = Dimmed;
        if (ClassName == "FearGui::FGBuddyComboBox")
        {
            // 004b5288: the buddy combo draws only its arrow (no bars, label or entry).
            PbmpBitmap? ba = dimmed ? Arrow("_NA") : Pressed ? Arrow("_ON") : Over ? Arrow("_MD") : Arrow("_DF");
            ba ??= Arrow("_DF");
            if (ba != null)
                p.Bitmap(ba, gx + W - 7 - ba.Width, gy + 2);
            return;
        }
        var k = dimmed ? 4 : 0;
        int[] pal = [0xf0, 0xef, 0xee, 0xed, 0xec, 0xeb, 0xea, 0xe9];
        var bottom = gy + H - 1;
        p.Fill(gx + 4, gy, W - 8, 1, pal[k + 1]);
        p.Fill(gx + 3, gy + 1, W - 6, 1, pal[k]);
        p.Fill(gx + 3, gy + 1, 1, 1, pal[k + 2]);
        p.Fill(gx + W - 4, gy + 1, 1, 1, pal[k + 2]);
        p.Fill(gx + 3, bottom - 1, W - 6, 1, pal[k]);
        p.Fill(gx + 4, bottom, W - 8, 1, pal[k + 1]);
        p.Fill(gx + 3, bottom - 1, 1, 1, pal[k + 2]);
        p.Fill(gx + W - 4, bottom - 1, 1, 1, pal[k + 2]);
        var left = gx + 10;
        var label = S("popupName");
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
        var room = gx + W - arrow.Width - 14 - left;
        PftFont? f = dimmed ? Tag(150134) : Tag(150114);
        PftFont? sh = Tag(150172);
        var t = Text;
        if (f == null)
            return;
        if (GuiPaint.TextWidth(f, t) >= room)
        {
            var dots = GuiPaint.TextWidth(f, "...");
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

/// <summary>The popup list of a combo, pushed over the screen: a click on an entry selects it, any other click closes it.</summary>
public sealed class ComboPopup : GuiControl
{
    private readonly GuiCombo mCombo;
    private readonly int mGx, mGy;
    private const int RowH = 14;

    /// <summary>Creates a popup under a combo.</summary>
    /// <param name="combo">The combo.</param>
    /// <param name="gx">The popup left edge in canvas pixels.</param>
    /// <param name="gy">The popup top edge in canvas pixels.</param>
    public ComboPopup(GuiCombo combo, int gx, int gy)
    {
        ClassName = "FearGui::ComboPopup";
        mCombo = combo;
        mGx = gx;
        mGy = gy;
        Flags = 3 | 4;
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        var row = (e.Y - mGy) / RowH;
        var inside = e.X >= mGx && e.X < mGx + mCombo.W && e.Y >= mGy && row >= 0 && row < mCombo.Entries.Count;
        Canvas?.PopDialog(0, force: true);
        if (inside)
            mCombo.SetSelectedIndex(row, notify: true);
        return true;
    }

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e)
    {
        if (e.Scan == 1)
            Canvas?.PopDialog(0, force: true);
        return true;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        PftFont? f = Host?.FontByName("sf_white_10.pft");
        var h = mCombo.Entries.Count * RowH;
        p.Fill(mGx, mGy, mCombo.W, h, 0xf4);
        GuiBox.Bevel(p, mGx, mGy, mCombo.W, h, false);
        for (var i = 0; i < mCombo.Entries.Count; ++i)
        {
            if (i == mCombo.Selected)
                p.Fill(mGx + 1, mGy + (i * RowH), mCombo.W - 2, RowH, 0xf8);
            p.Text(f, mGx + 4, mGy + (i * RowH) + 1, mCombo.Entries[i].Text);
        }
    }
}
