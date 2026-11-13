namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

/// <summary>One column of a column list.</summary>
/// <param name="HeaderTag">The header tag, -1 draws no heading.</param>
/// <param name="DataIndex">The data index the cell text query gets (the field at +0x10 of the column record, 004b426c).</param>
/// <param name="Width">The width in pixels (+0x14 of the record).</param>
/// <param name="Visible">True when the column shows (+0x18 of the record).</param>
public sealed record ListColumn(int HeaderTag, int DataIndex, int Width, bool Visible = true);

/// <summary>The lists that draw a header row and a grid (docs/gui.md 3.3 "Column lists").</summary>
/// <remarks>Follows header 004b3e1c, row 004b426c and list 004b4584. The list is one cell column of rows, and each row draws the columns itself. The fonts of the wake (004b36e4): the header uses tag 150120 (150130 disabled) with the shadow font of tag 150171, the rows sf_orange_7, sf_white_7 (selected), sf_grey_7 (disabled) and sf_yellow_7 (under the cursor).</remarks>
public abstract class GuiColumnList : GuiList
{
    // Palette indices of the grid: header fill 0xf7 (0xf4 disabled), header and list lines 0xf0 (0xea disabled).
    private const int HeaderFill = 0xf7, HeaderFillOff = 0xf4, LineOn = 0xf0, LineOff = 0xea;

    /// <summary>Gets the columns.</summary>
    protected abstract IReadOnlyList<ListColumn> Columns { get; }
    /// <summary>Gets the columns.</summary>
    public IReadOnlyList<ListColumn> ColumnSet => Columns;
    /// <summary>Gets the number of rows.</summary>
    protected abstract int RowCount { get; }
    /// <summary>Returns the text of a cell.</summary>
    /// <param name="dataIndex">The data index of the column.</param>
    /// <param name="row">The row index.</param>
    /// <returns>The text, or null for none.</returns>
    protected abstract string? Cell(int dataIndex, int row);

    /// <summary>Draws a cell as bitmaps instead of text (004c8278 columns 0 to 2 of the server list).</summary>
    /// <param name="p">The painter.</param>
    /// <param name="dataIndex">The data index of the column.</param>
    /// <param name="row">The row index.</param>
    /// <param name="x">The cell left edge.</param>
    /// <param name="y">The row top edge.</param>
    /// <param name="rh">The row height.</param>
    /// <returns>True when the column drew itself.</returns>
    protected virtual bool DrawIcons(GuiPaint p, int dataIndex, int row, int x, int y, int rh) => false;

    /// <summary>Returns the grid color of the row lines: 0xf8 in the option columns list, 0xf0 or 0xea otherwise.</summary>
    /// <param name="disabled">True when the list is disabled.</param>
    /// <returns>The palette index.</returns>
    protected virtual int RowLine(bool disabled) => disabled ? LineOff : LineOn;
    /// <summary>Gets the vertical offset of the header text.</summary>
    protected virtual int HeaderTextOffset => 0;

    /// <summary>Gets the height of the header: a list in a scroll that asks for a header (headerY) reserves that much at its top.</summary>
    public int HeaderH => Parent is GuiScrollContent { Parent: GuiScroll s } ? (int)s.U("headerY") : 0;

    /// <summary>Gets the font of a normal row.</summary>
    protected PftFont? RowFontNormal => Host?.FontByName("sf_orange_7.pft");
    /// <summary>Gets the font of the selected row.</summary>
    protected PftFont? RowFontSelected => Host?.FontByName("sf_white_7.pft");
    /// <summary>Gets the font of a disabled row.</summary>
    protected PftFont? RowFontOff => Host?.FontByName("sf_grey_7.pft");
    /// <summary>Gets the font of the row under the cursor.</summary>
    protected PftFont? RowFontHover => Host?.FontByName("sf_yellow_7.pft");
    /// <summary>Gets the header font.</summary>
    /// <param name="disabled">True when the list is disabled.</param>
    /// <returns>The font.</returns>
    protected PftFont? HeadFont(bool disabled) => Host?.FontByTag(disabled ? 150130u : 150120u);
    /// <summary>Gets the shadow font of the header.</summary>
    protected PftFont? ShadowFont => Host?.FontByTag(150171);

    /// <inheritdoc/>
    protected override PftFont? RowFont => RowFontNormal;
    /// <summary>Gets the row height: the sf_orange_7 height plus six (004b36e4).</summary>
    public new int RowHeight => GuiPaint.FontHeight(RowFontNormal) + 6;

    /// <summary>Gets the sum of the visible column widths.</summary>
    protected int ColumnsWidth => Columns.Where(c => c.Visible).Sum(c => c.Width);
    /// <summary>Gets a value that is true when the list is inactive or dimmed.</summary>
    protected bool Disabled => !Active || Dimmed;

    /// <summary>The row under the cursor, -1 for none.</summary>
    public int HoverRow { get; set; } = -1;

    /// <inheritdoc/>
    /// <remarks>Follows 004b36e4, which gives the scroll around the list a one pixel border in 0xf0 (0xec disabled).</remarks>
    public override void Wake()
    {
        base.Wake();
        // 004b36e4 gives the scroll around the list a one pixel border in 0xf0 (0xec disabled).
        if (Parent is GuiScrollContent { Parent: GuiScroll s })
        {
            s.Border = 1;
            s.BorderColor = 0xf0;
            s.SelectedBorder = 0xf0;
            s.DisabledBorder = 0xec;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d5488 and 004bcefc: the list is as wide as its scroll (at least as wide as its columns) and exactly as high as the header and rows.</remarks>
    public override void PreRender()
    {
        if (Parent is not GuiScrollContent pc)
            return;
        if (pc.Parent is GuiScroll sc)
            sc.Disabled = Disabled;
        W = Math.Max(ColumnsWidth, pc.W);
        H = HeaderH + (RowCount * RowHeight);
    }

    /// <summary>Returns the row at a local y.</summary>
    /// <param name="ly">The local y.</param>
    /// <returns>The row index, -1 for none.</returns>
    protected int RowAt(int ly)
    {
        var h = HeaderH;
        if (ly < h)
            return -1;
        var row = (ly - h) / RowHeight;
        return row < RowCount ? row : -1;
    }

    /// <inheritdoc/>
    public override bool MsMove(GuiEvent e)
    {
        var r = base.MsMove(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        HoverRow = Active && lx >= 0 && lx < W ? RowAt(ly) : -1;
        return r;
    }

    /// <inheritdoc/>
    public override bool MsLeave(GuiEvent e)
    {
        HoverRow = -1;
        return base.MsLeave(e);
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        var r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        var row = RowAt(ly);
        if (row >= 0)
            Select(row);
        return r;
    }

    // ---- drawing ----

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var off = Disabled;
        DrawHeader(p, ox, oy, gx, off);
        int rh = RowHeight, top = gy + HeaderH, count = RowCount;
        for (var row = 0; row < count; ++row)
        {
            var y = top + (row * rh);
            if (y > p.Clip.Bottom)
                break;
            if (y + rh <= p.Clip.Top)
                continue;
            var saved = p.Clip;
            // 0053ccd8: a row is drawn clipped to its own rectangle, so its bottom grid line belongs to the next row.
            p.SetClip(saved.Intersect(new GfxRect(gx, y, gx + Math.Max(W, ColumnsWidth) - 1, y + rh - 1)));
            DrawRow(p, ox, gx, y, row, rh, off);
            p.SetClip(saved);
        }
        // 004b4584: a line along the bottom edge of the list.
        if (count > 0)
            p.Line(gx, gy + H - 1, gx + W - 1, gy + H - 1, off ? LineOff : LineOn);
    }

    /// <summary>Draws the header, which stays at the top of the scroll's view: x and y are the parent's origin, its columns follow the list (004b3e1c).</summary>
    private void DrawHeader(GuiPaint p, int ox, int oy, int gx, bool off)
    {
        var hh = HeaderH;
        if (hh <= 0)
            return;
        var pw = Parent?.W ?? W;
        p.Fill(ox, oy, pw, hh, off ? HeaderFillOff : HeaderFill);
        var x = gx;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            x += c.Width;
            p.Line(x, oy, x, oy + hh, off ? LineOff : LineOn);
        }
        var font = HeadFont(off);
        x = gx + 1;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            if (c.HeaderTag >= 0 && Host?.TagText((uint)c.HeaderTag) is string t)
                DrawCellText(p, font, true, true, c.Width - 4, hh, x + 2, oy, t);
            x += c.Width;
        }
        p.Outline(ox, oy, pw, hh, off ? LineOff : LineOn);
    }

    /// <summary>Draws one row (004b426c). Column cells are centred in the column less four pixels, with the text two pixels in and two up.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="ox">The parent origin x.</param>
    /// <param name="gx">The list left edge.</param>
    /// <param name="y">The row top edge.</param>
    /// <param name="row">The row index.</param>
    /// <param name="rh">The row height.</param>
    /// <param name="off">True when the list is disabled.</param>
    protected virtual void DrawRow(GuiPaint p, int ox, int gx, int y, int row, int rh, bool off)
    {
        var selected = row == Selected;
        var font = off ? RowFontOff : selected ? RowFontSelected : HoverRow == row ? RowFontHover : RowFontNormal;
        var pw = Parent?.W ?? W;
        var line = RowLine(off);
        var x = gx;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            p.Line(x, y, x, y + rh - 1, line);
            x += c.Width;
        }
        p.Line(x, y, x, y + rh - 1, line);
        x = gx;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            if (!DrawIcons(p, c.DataIndex, row, x, y, rh) && Cell(c.DataIndex, row) is string t)
                DrawCellText(p, font, true, false, c.Width - 4, rh, x + 2, y - 2, t);
            x += c.Width;
        }
        // The outline of the row (ox, y) to (ox + pw - 1, y + rh): its top and the right edge show, the bottom is the next row's top.
        p.Line(ox, y, ox + pw - 1, y, line);
        p.Line(ox + pw - 1, y, ox + pw - 1, y + rh - 1, line);
    }

    /// <summary>Draws the text of a cell (004b3b54).</summary>
    /// <param name="p">The painter.</param>
    /// <param name="font">The font.</param>
    /// <param name="center">True to centre the text.</param>
    /// <param name="shadow">True to draw the shadow font under it.</param>
    /// <param name="w">The extent width.</param>
    /// <param name="h">The extent height.</param>
    /// <param name="x">The origin x.</param>
    /// <param name="y">The origin y.</param>
    /// <param name="text">The text.</param>
    /// <remarks>The exe code is the rule for every offset. A text narrower than the extent is drawn at x = origin (+ (w - text width) / 2 when centred) and y = origin + (h - font height) / 2 - 1. The shadow font, when the flag is set and the control has one, draws the same text at (x - 1, y + 1). A text that does not fit is cut to leave room for "..." (nothing is drawn when "..." alone is wider than the extent), left aligned at y = origin + (h - font height) / 2, its shadow at (x - 1, y + 1). Halving rounds toward zero. The one pixel offsets are the exe's own arguments of the two draw calls.</remarks>
    protected void DrawCellText(GuiPaint p, PftFont? font, bool center, bool shadow, int w, int h, int x, int y, string text)
    {
        if (font == null)
            return;
        var sh = shadow ? ShadowFont : null;
        var fh = GuiPaint.FontHeight(font);
        var cy = Div2(h - fh) + y;
        var tw = GuiPaint.TextWidth(font, text);
        if (tw < w)
        {
            var tx = center ? Div2(w - tw) + x : x;
            if (sh != null)
                p.Text(sh, tx - 1, cy, text);
            p.Text(font, tx, cy - 1, text);
            return;
        }
        var ell = GuiPaint.TextWidth(font, "...");
        if (ell > w)
            return;
        var s = text;
        while (s.Length > 0 && GuiPaint.TextWidth(font, s) > w - ell)
            s = s[..^1];
        s += "...";
        if (sh != null)
            p.Text(sh, x - 1, cy + 1, s);
        p.Text(font, x, cy, s);
    }

    /// <summary>Halves a value toward zero: the original halves with an arithmetic shift and adds one back for odd negatives.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The half.</returns>
    protected static int Div2(int v) => v / 2;
}

/// <summary>The master servers of the Network options (FearGui::MasterServerList): two columns of 122 pixels (Master Server, Address), rows sorted by name without regard to case, the selected row in white.</summary>
/// <remarks>Follows ctor 004d5d08, wake 004d5368, add 004d55e0, delete 004d5708, select by id 004d5840 and select row 004d54d0.</remarks>
public sealed class GuiMasterList : GuiColumnList
{
    private static readonly ListColumn[] sColumns =
    [
        new(131310, 0, 122), // IDSTR_SI_MASTER_SERVER
        new(131306, 1, 122), // IDSTR_SI_ADDRESS
    ];

    private readonly List<string> _addresses = [];

    /// <inheritdoc/>
    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    /// <inheritdoc/>
    protected override int RowCount => Entries.Count;

    /// <inheritdoc/>
    protected override string? Cell(int dataIndex, int row) =>
        row >= 0 && row < Entries.Count ? dataIndex == 0 ? Entries[row].Text : dataIndex == 1 ? _addresses[row] : null : null;

    /// <summary>Returns the address of a row.</summary>
    /// <param name="row">The row index.</param>
    /// <returns>The address.</returns>
    public string AddressOf(int row) => _addresses[row];

    /// <summary>Adds a master server.</summary>
    /// <param name="name">The name, cut to 255 characters.</param>
    /// <param name="address">The address, cut to 255 characters.</param>
    /// <param name="id">The id.</param>
    /// <remarks>Follows 004d55e0: the list is sorted again, and the selection stays at its row.</remarks>
    public void AddMaster(string name, string address, int id)
    {
        if (name.Length > 255)
            name = name[..255];
        if (address.Length > 255)
            address = address[..255];
        var selId = Selected >= 0 ? Entries[Selected].Id : int.MinValue;
        var all = new List<(string Name, string Addr, int Id)>();
        for (var i = 0; i < Entries.Count; ++i)
            all.Add((Entries[i].Text, _addresses[i], Entries[i].Id));
        all.Add((name, address, id));
        var sorted = all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Entries.Clear();
        _addresses.Clear();
        foreach (var e in sorted)
        {
            Entries.Add((e.Name, e.Id));
            _addresses.Add(e.Addr);
        }
        Selected = selId == int.MinValue ? -1 : Entries.FindIndex(e => e.Id == selId);
    }

    /// <inheritdoc/>
    public override void DeleteById(int id)
    {
        for (var i = Entries.Count - 1; i >= 0; --i)
        {
            if (Entries[i].Id != id)
                continue;
            Entries.RemoveAt(i);
            _addresses.RemoveAt(i);
            if (Selected == i)
                Selected = -1;
            else if (Selected > i)
                --Selected;
        }
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        Entries.Clear();
        _addresses.Clear();
        Selected = -1;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d54d0: a selected row sets $Server::CurrentMaster to its id and $Server::Master1 to its address.</remarks>
    protected override void Selecting(int row)
    {
        if (row < 0 || row >= Entries.Count || Host == null)
            return;
        Host.SetVar("$Server::CurrentMaster", Entries[row].Id.ToString(CultureInfo.InvariantCulture));
        Host.SetVar("$Server::Master1", _addresses[row]);
    }
}

/// <summary>The filter builder of the Filters options (FearGui::FilterCtrl): its header has four columns (Variable 70, Condition 87, Value 67 and one of 32 without a heading) and no rows until a filter is chosen.</summary>
/// <remarks>Follows wake 004be06c.</remarks>
public sealed class GuiFilterCtrl : GuiColumnList
{
    private static readonly ListColumn[] sColumns =
    [
        new(131600, 0, 70), new(131601, 1, 87), new(131602, 2, 67), new(-1, 3, 32),
    ];

    /// <summary>The chosen filter, 0 or less for none (DAT_00628370 of the original).</summary>
    public int ChosenFilter { get; set; }

    /// <inheritdoc/>
    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    /// <inheritdoc/>
    protected override int RowCount => 0;
    /// <inheritdoc/>
    protected override string? Cell(int dataIndex, int row) => null;

    /// <inheritdoc/>
    /// <remarks>Follows 004d8fe4: with no filter chosen the box around the filter list is drawn dimmed.</remarks>
    public override void Wake()
    {
        base.Wake();
        for (var c = Parent; c != null; c = c.Parent)
        {
            if (c is GuiBox box)
            {
                box.ForceDim = ChosenFilter < 1;
                break;
            }
        }
    }
}

/// <summary>The twelve columns of the server browser and their display state.</summary>
/// <remarks>Follows the table 00627300, load 004bcc58 and save 004bcdac. Each column has a name tag, a description tag, the position in the browser (order), whether it shows, and a width. The state lives in the console variables $pref::ServerColumnDisplay0 to 11, each "order, YES|NO, width".</remarks>
public sealed class ServerColumns
{
    /// <summary>The state of one column.</summary>
    public sealed class Entry
    {
        /// <summary>The tag of the column name.</summary>
        public int NameTag { get; set; }
        /// <summary>The tag of the column description.</summary>
        public int DescTag { get; set; }
        /// <summary>The position in the browser.</summary>
        public int Order { get; set; }
        /// <summary>The width in pixels.</summary>
        public int Width { get; set; }
        /// <summary>True when the column shows.</summary>
        public bool Display { get; set; } = true;
    }

    // Name tag, description tag, width: the default order is the table order and every column shows.
    private static readonly (int Name, int Desc, int Width)[] Table =
    [
        (131309, 131519, 52), (131308, 131517, 52), (131307, 131516, 54), (131301, 131510, 105), (131303, 131512, 43),
        (131311, 131520, 100), (131302, 131511, 90), (132054, 131513, 59), (131305, 131514, 33), (131306, 131515, 102),
        (131803, 131518, 100), (131312, 131521, 100),
    ];

    /// <summary>The number of columns.</summary>
    public const int Count = 12;
    /// <summary>The columns in table order.</summary>
    public Entry[] Entries { get; } = new Entry[Count];

    /// <summary>Creates the default state: the table order and every column shows.</summary>
    public ServerColumns()
    {
        for (var i = 0; i < Count; ++i)
            Entries[i] = new Entry { NameTag = Table[i].Name, DescTag = Table[i].Desc, Order = i, Width = Table[i].Width };
    }

    /// <summary>Loads the state from the console variables.</summary>
    /// <param name="host">The GUI host.</param>
    /// <remarks>Follows 004bcc58: a variable that is set overrides the entry. When the orders are no permutation of 0 to 11 they reset and are written back.</remarks>
    public void Load(GuiHost host)
    {
        for (var i = 0; i < Count; ++i)
        {
            var v = host.GetVar("$pref::ServerColumnDisplay" + i.ToString(CultureInfo.InvariantCulture));
            if (string.IsNullOrEmpty(v))
                continue;
            var parts = v.Split(',');
            if (parts.Length < 2)
                continue;
            if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order))
                Entries[i].Order = order;
            if (parts.Length >= 3)
            {
                Entries[i].Display = string.Equals(parts[1].Trim(), "YES", StringComparison.OrdinalIgnoreCase);
                if (int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                    Entries[i].Width = w;
            }
        }
        if (!IsPermutation())
        {
            for (var i = 0; i < Count; ++i)
                Entries[i].Order = i;
            Save(host);
        }
    }

    /// <summary>Returns whether the orders are a permutation of 0 to 11.</summary>
    /// <returns>True when they are.</returns>
    public bool IsPermutation()
    {
        var seen = new bool[Count];
        foreach (var e in Entries)
        {
            if (e.Order < 0 || e.Order >= Count || seen[e.Order])
                return false;
            seen[e.Order] = true;
        }
        return true;
    }

    /// <summary>Writes the state to the console variables.</summary>
    /// <param name="host">The GUI host.</param>
    public void Save(GuiHost host)
    {
        for (var i = 0; i < Count; ++i)
            host.SetVar("$pref::ServerColumnDisplay" + i.ToString(CultureInfo.InvariantCulture),
                $"{Entries[i].Order}, {(Entries[i].Display ? "YES" : "NO")}, {Entries[i].Width}");
    }

    /// <summary>Returns the entry shown at a position of the options list.</summary>
    /// <param name="order">The position.</param>
    /// <returns>The entry, or null.</returns>
    public Entry? AtOrder(int order) => Array.Find(Entries, e => e.Order == order);

    /// <summary>Moves an entry to a row; the entries between shift one place the other way (004bcad4).</summary>
    /// <param name="e">The entry.</param>
    /// <param name="target">The row.</param>
    public void MoveTo(Entry e, int target)
    {
        target = Math.Clamp(target, 0, Count - 1);
        var step = Math.Sign(target - e.Order);
        if (step == 0)
            return;
        var cur = e.Order;
        // The lookup by order is taken before any order changes (the original fills a table first).
        var byOrder = new Entry?[Count];
        foreach (var x in Entries)
            if (x.Order is >= 0 and < Count)
                byOrder[x.Order] = x;
        for (var o = target; o != cur; o -= step)
            if (byOrder[o] is Entry shifted)
                shifted.Order -= step;
        e.Order = target;
    }
}

/// <summary>The Columns options list (FearGui::ColumnListCtrl): three columns (NAME 70, DESCRIPTION 161, DISP 36), one row per browser column in browser order.</summary>
/// <remarks>Follows ctor 004bd678, wake 004bce4c, row 004bcf44, down 004bc9a4, drag 004bcad4 and up 004bcc28. A press on a name picks the row up (white) and dragging moves it, a press in DISP toggles YES and NO. The release writes the preferences.</remarks>
public sealed class GuiOptionColumns : GuiColumnList
{
    private static readonly ListColumn[] sColumns = [new(131501, 0, 70), new(131502, 1, 161), new(131503, 2, 36)];

    /// <summary>The state of the columns.</summary>
    public ServerColumns Data { get; } = new();
    private ServerColumns.Entry? _drag, _toggle;
    private int _dragRow = -1;

    /// <inheritdoc/>
    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    /// <inheritdoc/>
    protected override int RowCount => ServerColumns.Count;
    /// <inheritdoc/>
    protected override int RowLine(bool disabled) => 0xf8;

    /// <inheritdoc/>
    protected override string? Cell(int dataIndex, int row)
    {
        var e = Data.AtOrder(row);
        if (e == null)
            return null;
        return dataIndex switch
        {
            0 => Host?.TagText((uint)e.NameTag),
            1 => Host?.TagText((uint)e.DescTag),
            _ => e.Display ? "YES" : "NO",
        };
    }

    /// <inheritdoc/>
    public override void Wake()
    {
        base.Wake();
        if (Host != null)
            Data.Load(Host);
    }

    /// <inheritdoc/>
    protected override void DrawRow(GuiPaint p, int ox, int gx, int y, int row, int rh, bool off)
    {
        var e = Data.AtOrder(row);
        var held = !off && _drag != null && e == _drag;
        var font = off ? RowFontOff : held ? RowFontSelected : _drag == null && HoverRow == row ? RowFontHover : RowFontNormal;
        var dispFont = !off && _toggle != null && e == _toggle ? RowFontSelected : font;
        var pw = Parent?.W ?? W;
        var line = RowLine(off);
        var x = gx;
        foreach (var c in Columns)
        {
            p.Line(x, y, x, y + rh - 1, line);
            x += c.Width;
        }
        p.Line(x, y, x, y + rh - 1, line);
        x = gx;
        foreach (var c in Columns)
        {
            if (Cell(c.DataIndex, row) is string t)
                DrawCellText(p, c.DataIndex == 2 ? dispFont : font, true, false, c.Width - 4, rh, x + 2, y - 2, t);
            x += c.Width;
        }
        // The outline of the row (ox, y) to (ox + pw - 1, y + rh): its top and the right edge show, the bottom is the next row's top.
        p.Line(ox, y, ox + pw - 1, y, line);
        p.Line(ox + pw - 1, y, ox + pw - 1, y + rh - 1, line);
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        var r = base.MsDown(e);
        _drag = _toggle = null;
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        if (ly < HeaderH || Selected < 0)
            return r;
        var hit = Data.AtOrder(Selected);
        if (hit == null)
            return r;
        if (lx >= 0 && lx <= sColumns[0].Width)
        {
            _drag = hit;
            _dragRow = Selected;
        }
        else if (lx >= sColumns[0].Width + sColumns[1].Width && lx <= sColumns[0].Width + sColumns[1].Width + sColumns[2].Width)
        {
            _toggle = hit;
            hit.Display = !hit.Display;
        }
        return r;
    }

    /// <inheritdoc/>
    public override bool MsDrag(GuiEvent e)
    {
        if (_drag != null)
        {
            var (_, ly) = GlobalToLocal(e.X, e.Y);
            var target = ly < 0 || (HeaderH * 2) + (RowHeight * ServerColumns.Count) < ly ? _dragRow : Math.Clamp((ly - HeaderH) / RowHeight, 0, ServerColumns.Count - 1);
            if (target != _drag.Order)
            {
                Data.MoveTo(_drag, target);
                _dragRow = target;
                Selected = target;
            }
        }
        return base.MsDrag(e);
    }

    /// <inheritdoc/>
    public override bool MsUp(GuiEvent e)
    {
        if (Host != null)
            Data.Save(Host);
        _drag = _toggle = null;
        _dragRow = -1;
        return base.MsUp(e);
    }
}

/// <summary>The server browser of the join screen (FearGui::ServerListCtrl).</summary>
/// <remarks>Follows ctor 004c4b84, wake 004c7b98 and open 004c7080. Its columns are the server column options (<see cref="ServerColumns"/>): the entries with Display set, in order. The rows are the servers the client knows (config\GameServerList.cs and the answers of the query, ServerQuery). The cells follow the cell query 004c8278.</remarks>
public sealed class GuiServerList : GuiColumnList
{
    /// <summary>One record of the browser.</summary>
    /// <remarks>The exe record is 0x458 bytes, and the offsets in the member docs are of that record. State +0x240: 0 info received, 1 pinged, 2 being queried, 3 unknown, 4 timed out.</remarks>
    public sealed class Server
    {
        /// <summary>The row id.</summary>
        public int Id { get; set; }
        /// <summary>The server name.</summary>
        public string Name { get; set; }
        /// <summary>The server address.</summary>
        public string Address { get; set; }
        /// <summary>The game version.</summary>
        public string Version { get; set; }
        /// <summary>The ping in milliseconds.</summary>
        public int Ping { get; set; }
        /// <summary>True for a favorite (+0x241): the star, kept through failed queries and saved.</summary>
        public bool Favorite { get; set; }
        /// <summary>True when the row is never pinged (+0x244): the broadcast and master replies for it are ignored (004c525c) and the row shows the I_Timeout icon (004c8278 column 0).</summary>
        public bool NeverPing { get; set; }
        /// <summary>The query state, one of the State constants.</summary>
        public int State { get; set; } = 3;
        /// <summary>The player count.</summary>
        public int Players { get; set; }
        /// <summary>The player limit.</summary>
        public int MaxPlayers { get; set; }
        /// <summary>The cpu speed.</summary>
        public int Cpu { get; set; }
        /// <summary>True when the server has a password.</summary>
        public bool Password { get; set; }
        /// <summary>True when the server is dedicated.</summary>
        public bool Dedicated { get; set; }
        /// <summary>The mission name.</summary>
        public string Mission { get; set; } = "";
        /// <summary>The mission type.</summary>
        public string MissionType { get; set; } = "";
        /// <summary>The mod list.</summary>
        public string ModList { get; set; } = "";
        /// <summary>The info text.</summary>
        public string Info { get; set; } = "";
        /// <summary>The team heading of the score table of the info reply (0042c4cc).</summary>
        public string TeamHeading { get; set; } = "";
        /// <summary>The player heading of the score table.</summary>
        public string ClientHeading { get; set; } = "";
        /// <summary>The rows of the score table (ServerInfoCtrl).</summary>
        public IReadOnlyList<string> Rows { get; set; } = Array.Empty<string>();

        /// <summary>Creates a record.</summary>
        /// <param name="id">The row id.</param>
        /// <param name="name">The name.</param>
        /// <param name="address">The address.</param>
        /// <param name="version">The version.</param>
        /// <param name="ping">The ping.</param>
        /// <param name="favorite">True for a favorite.</param>
        public Server(int id, string name, string address, string version, int ping, bool favorite)
        {
            Id = id;
            Name = name;
            Address = address;
            Version = version;
            Ping = ping;
            Favorite = favorite;
        }
    }

    /// <summary>State: info received.</summary>
    public const int StateInfo = 0;
    /// <summary>State: pinged.</summary>
    public const int StatePinged = 1;
    /// <summary>State: being queried.</summary>
    public const int StateQuerying = 2;
    /// <summary>State: unknown.</summary>
    public const int StateUnknown = 3;
    /// <summary>State: timed out.</summary>
    public const int StateTimeout = 4;

    // The widths of the browser's column records [table at 00628cf4]; the third number of the preference is not the width.
    private static readonly int[] Widths = [52, 52, 54, 105, 43, 90, 90, 59, 33, 102, 100, 90];

    /// <summary>The state of the browser columns.</summary>
    public ServerColumns Data { get; } = new();
    private readonly List<Server> _servers = [];
    private List<ListColumn> _columns = [];

    /// <summary>True when rows that failed stay in the list (+0x3b0). The stock refresh of visible servers and the cancel set it.</summary>
    public bool KeepFailed { get; set; }
    /// <summary>True when a REFRESH ALL has run since the list woke (+0x3b1).</summary>
    public bool RefreshedAll { get; set; }
    /// <summary>True when the favorites that did not answer are asked directly when the search ends (+0x3b2).</summary>
    public bool Requery { get; set; }
    /// <summary>The ping below which the ping icon is green (pref::ConnectionGoodPing; the exe default is 200).</summary>
    public int GoodPing { get; set; } = 200;
    /// <summary>The ping below which the ping icon is yellow (pref::ConnectionPoorPing; the exe default is 350).</summary>
    public int PoorPing { get; set; } = 350;

    /// <inheritdoc/>
    protected override IReadOnlyList<ListColumn> Columns => _columns;
    /// <inheritdoc/>
    protected override int RowCount => _servers.Count;
    /// <summary>Gets the known servers.</summary>
    public IReadOnlyList<Server> Servers => _servers;

    /// <summary>Marks every row as not answered yet and makes failures drop rows again (REFRESH ALL, 004c774c).</summary>
    public void MarkAllUnknown()
    {
        foreach (var s in _servers)
        {
            s.Ping = 9999;
            s.State = StateUnknown;
        }
        KeepFailed = false;
        RefreshedAll = true;
        Requery = true;
    }

    /// <summary>Resets the search flags: rows that fail stay, nothing is re-asked and no refresh has run (the last statements of the list's wake, 004c6f..4c702e).</summary>
    public void ResetSearchFlags()
    {
        KeepFailed = true;
        Requery = false;
        RefreshedAll = false;
    }

    /// <summary>Ends a search (004c7820).</summary>
    /// <param name="masterError">True after a master error (the delegate's +0x60).</param>
    /// <returns>The rows to ask directly.</returns>
    /// <remarks>The rows that did not answer (unknown or being queried) are dropped unless they are favorites, never-ping rows or the failures are kept. A favorite (or any row after a master error) is asked directly when REFRESH ALL asked for it. The selection follows its row by address.</remarks>
    public List<Server> EndSearch(bool masterError)
    {
        KeepFailed = masterError;
        var ask = new List<Server>();
        var drop = new List<Server>();
        foreach (var s in _servers)
        {
            if (s.State is not StateUnknown and not StateQuerying)
                continue;
            if ((!s.Favorite && !KeepFailed) || !Requery)
            {
                if (!s.Favorite && !s.NeverPing && !KeepFailed && RefreshedAll)
                    drop.Add(s);
                else if (s.State == StateQuerying)
                    s.State = StateUnknown;
            }
            else
            {
                s.State = StateQuerying;
                ask.Add(s);
            }
        }
        foreach (var s in drop)
            RemoveServer(s);
        Requery = false;
        return ask;
    }

    /// <summary>Parses the arguments of the console command addGameServer (004c5320) into a row.</summary>
    /// <param name="id">The row id.</param>
    /// <param name="a">The arguments, with the command name first.</param>
    /// <returns>The row, null when the first five are not all given.</returns>
    /// <remarks>The short form has address, name, version, ping and favorite. A 7 argument form adds never ping, an 11 argument form adds the mod, mission, mission type, cpu and never ping (the file 004c4e2c writes).</remarks>
    public static Server? ParseScriptRow(int id, IReadOnlyList<string> a)
    {
        if (a.Count < 6 || a[1].Length == 0 || a[2].Length == 0 || a[3].Length == 0 || a[4].Length == 0 || a[5].Length == 0)
            return null;
        static bool Flag(string v) => v.Length != 0 && !string.Equals(v, "FALSE", StringComparison.OrdinalIgnoreCase);
        var s = new Server(id, a[2], a[1], a[3], Numbers.BorlandAtoi(a[4]), Flag(a[5])) { Cpu = -1 };
        if (a.Count == 7)
            s.NeverPing = Flag(a[6]);
        else if (a.Count == 11)
        {
            s.ModList = a[6];
            s.Mission = a[7];
            s.MissionType = a[8];
            s.Cpu = Numbers.BorlandAtoi(a[9]);
            s.NeverPing = Flag(a[10]);
        }
        return s;
    }

    /// <summary>Builds the text of config\GameServerList.cs, one addGameServer line per row (004c4e2c).</summary>
    /// <returns>The file text.</returns>
    public string BuildServerFile()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("//-----------------------------------\n//\n// Database of GAME servers\n//\n//-----------------------------------\n\n");
        foreach (var s in _servers)
            sb.Append("addGameServer(\"").Append(s.Address).Append("\", \"").Append(s.Name).Append("\", \"").Append(s.Version)
              .Append("\", \"").Append(s.Ping.ToString(CultureInfo.InvariantCulture)).Append("\", \"")
              .Append(s.Favorite ? "TRUE" : "FALSE").Append("\", \"").Append(s.ModList).Append("\", \"").Append(s.Mission)
              .Append("\", \"").Append(s.MissionType).Append("\", \"").Append(s.Cpu.ToString(CultureInfo.InvariantCulture))
              .Append("\", \"").Append(s.NeverPing ? "TRUE" : "FALSE").Append("\");\n");
        return sb.ToString();
    }

    /// <summary>Sets the favorite flag of the row with an address, ignoring case (004c51f0).</summary>
    /// <param name="address">The address.</param>
    /// <param name="on">The new state.</param>
    public void SetFavorite(string address, bool on)
    {
        if (Find(address) is { } s)
            s.Favorite = on;
    }

    /// <summary>Sets the never ping flag of the row with an address, ignoring case (004c52b4).</summary>
    /// <param name="address">The address.</param>
    /// <param name="on">The new state.</param>
    public void SetNeverPing(string address, bool on)
    {
        if (Find(address) is { } s)
            s.NeverPing = on;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004c6888: the hand over the rows and the header, the horizontal adjust cursor on a column border of the header.</remarks>
    public override uint CursorTagAt(int lx, int ly)
    {
        var y = Y + ly;
        if (y < 0 || y >= HeaderH)
            return CursorHand;
        var edge = 0;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            edge += c.Width;
            if (lx >= edge - 2 && lx <= edge + 2)
                return CursorHAdjust;
            if (lx < edge)
                return CursorHand;
        }
        return CursorHAdjust;
    }

    /// <summary>The help tags of the columns, the IDHELP_SERVER_* entries: 220603 favorite, 220604 connection, 220605 status, 220606 name, 220608 ping, 220614 mission type, 220607 mission, 220609 players, 220610 cpu, 220611 address, 220612 version, 220613 mod.</summary>
    private static readonly uint[] Help = [220603, 220604, 220605, 220606, 220608, 220614, 220607, 220609, 220610, 220611, 220612, 220613];

    /// <inheritdoc/>
    /// <remarks>Follows 004c6940: after 500 ms the header shows the description tag of the column under the cursor (a column border shows none).</remarks>
    public override uint HelpTagAt(int lx, int ly, long ms)
    {
        var y = Y + ly;
        if (ms < 500 || y < 0 || y >= HeaderH)
            return 0;
        var edge = 0;
        foreach (var c in Columns)
        {
            if (!c.Visible)
                continue;
            edge += c.Width;
            if (lx >= edge - 2 && lx <= edge + 2)
                return 0;
            if (lx < edge)
                return Help[c.DataIndex];
        }
        return 0;
    }

    /// <summary>Finds a row by address, ignoring case.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The row, or null.</returns>
    public Server? Find(string address) => _servers.Find(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a server row.</summary>
    /// <param name="s">The row.</param>
    public void AddServer(Server s)
    {
        _servers.Add(s);
        AddEntry(s.Name, s.Id);
    }

    /// <summary>Replaces a row for an address already listed: it takes the new fields in place (004c5320).</summary>
    /// <param name="old">The listed row.</param>
    /// <param name="row">The new row.</param>
    public void ReplaceServer(Server old, Server row)
    {
        var i = _servers.IndexOf(old);
        if (i < 0)
            return;
        _servers[i] = row;
        if (i < Entries.Count)
            Entries[i] = (row.Name, row.Id);
    }

    /// <summary>Removes a server row.</summary>
    /// <param name="s">The row.</param>
    public void RemoveServer(Server s)
    {
        var i = _servers.IndexOf(s);
        if (i < 0)
            return;
        _servers.RemoveAt(i);
        if (i < Entries.Count)
            Entries.RemoveAt(i);
        if (Selected == i)
            Selected = -1;
        else if (Selected > i)
            --Selected;
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        base.Clear();
        _servers.Clear();
    }

    /// <inheritdoc/>
    public override void Wake()
    {
        base.Wake();
        if (Host != null)
        {
            Data.Load(Host);
            GoodPing = ParsePref(Host.GetVar("$pref::ConnectionGoodPing"), 200);
            PoorPing = ParsePref(Host.GetVar("$pref::ConnectionPoorPing"), 350);
        }
        Rebuild();
    }

    private static int ParsePref(string v, int d) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : d;

    /// <summary>Builds the visible columns in browser order. The data index is the entry index (0 favorites to 11 server type).</summary>
    private void Rebuild() => _columns = [.. Data.Entries.Select((e, i) => (e, i)).OrderBy(t => t.e.Order).Select(t => new ListColumn(t.e.NameTag, t.i, Widths[t.i], t.e.Display))];

    /// <inheritdoc/>
    /// <remarks>Follows 004c8278: the numbers use "%d", the players "%d / %d", an empty mission or a cpu speed of zero shows N/A. Columns 0 to 2 are icons (<see cref="DrawIcons"/>).</remarks>
    protected override string? Cell(int dataIndex, int row)
    {
        if (row < 0 || row >= _servers.Count)
            return null;
        var s = _servers[row];
        var inv = CultureInfo.InvariantCulture;
        return dataIndex switch
        {
            3 => s.Name,
            4 => s.Ping.ToString(inv),
            5 => s.MissionType,
            6 => s.Mission.Length == 0 ? "N/A" : s.Mission,
            7 => s.Players.ToString(inv) + " / " + s.MaxPlayers.ToString(inv),
            8 => s.Cpu > 0 ? s.Cpu.ToString(inv) : "N/A",
            9 => s.Address,
            10 => s.Version,
            11 => s.ModList,
            _ => null,
        };
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004c8278 columns 0 to 2: bitmaps (I_Favorite, I_PingGreen/Yellow/Red, I_Locked, I_Dedicated, I_Unknown, I_Pinging, I_Querying, I_Timeout) five pixels into the cell and centred on the row height (plus one).</remarks>
    protected override bool DrawIcons(GuiPaint p, int dataIndex, int row, int x, int y, int rh)
    {
        if (dataIndex > 2 || Host == null)
            return false;
        // A disabled or dimmed list (a modal dialog above it) shows no icons.
        if (Disabled)
            return true;
        var s = _servers[row];
        void Put(string name, ref int px)
        {
            var b = Host.BitmapByName(name);
            if (b == null)
                return;
            p.Bitmap(b, px, y + Div2(rh - b.Height) + 1);
            px += b.Width + 3;
        }
        var ix = x + 5;
        switch (dataIndex)
        {
            case 0:
                // 004c8278: a never-ping row shows the I_Timeout picture (and nothing else), else a favorite its star.
                if (s.NeverPing)
                    Put("I_Timeout.bmp", ref ix);
                else if (s.Favorite)
                    Put("I_Favorite.bmp", ref ix);
                break;
            case 1:
                Put(s.Ping < GoodPing ? "I_PingGreen.bmp" : s.Ping < PoorPing ? "I_PingYellow.bmp" : "I_PingRed.bmp", ref ix);
                break;
            case 2:
                if (s.State == StateInfo)
                {
                    // The lock's slot is skipped whether it is drawn or not.
                    if (s.Password)
                        Put("I_Locked.bmp", ref ix);
                    else if (Host.BitmapByName("I_Locked.bmp") is { } lockBmp)
                        ix += lockBmp.Width + 3;
                    if (s.Dedicated)
                        Put("I_Dedicated.bmp", ref ix);
                }
                else
                    Put(s.State switch { StateUnknown => "I_Unknown.bmp", StateTimeout => "I_Timeout.bmp", StateQuerying => "I_Pinging.bmp", _ => "I_Querying.bmp" }, ref ix);
                break;
        }
        return true;
    }
}
