// docs/gui.md 3.3 "Column lists": the array controls with a header row and several columns per row.
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// One column of a column list: the header tag (-1 draws no heading), the data index the cell text query gets [code 004b426c
// reads the field at +0x10 of the column record], the width and the visible flag (+0x14, +0x18 of the record).
public sealed record ListColumn(int HeaderTag, int DataIndex, int Width, bool Visible = true);

// FearGui lists that draw a header row and a grid [code 004b3e1c header, 004b426c row, 004b4584 list]. The list is one cell
// column of rows; each row draws the columns itself. Fonts of the wake [code 004b36e4]: the header uses tag 150120 (150130
// disabled) with the shadow font of tag 150171, the rows sf_orange_7, sf_white_7 (selected), sf_grey_7 (disabled) and
// sf_yellow_7 (under the cursor).
public abstract class GuiColumnList : GuiList
{
    // Palette indices of the grid: header fill 0xf7 (0xf4 disabled), header and list lines 0xf0 (0xea disabled).
    private const int HeaderFill = 0xf7, HeaderFillOff = 0xf4, LineOn = 0xf0, LineOff = 0xea;

    protected abstract IReadOnlyList<ListColumn> Columns { get; }
    public IReadOnlyList<ListColumn> ColumnSet => Columns;
    protected abstract int RowCount { get; }
    protected abstract string? Cell(int dataIndex, int row);

    // A cell drawn as bitmaps instead of text (004c8278 columns 0 to 2 of the server list): true when the column drew itself.
    protected virtual bool DrawIcons(GuiPaint p, int dataIndex, int row, int x, int y, int rh) => false;

    // The grid colour of the row lines (0xf8 in the option columns list, 0xf0 or 0xea otherwise).
    protected virtual int RowLine(bool disabled) => disabled ? LineOff : LineOn;
    protected virtual int HeaderTextOffset => 0;

    // A list in a scroll that asks for a header (headerY) reserves that many rows at its top.
    public int HeaderH => Parent is GuiScrollContent { Parent: GuiScroll s } ? (int)s.U("headerY") : 0;

    protected PftFont? RowFontNormal => Host?.FontByName("sf_orange_7.pft");
    protected PftFont? RowFontSelected => Host?.FontByName("sf_white_7.pft");
    protected PftFont? RowFontOff => Host?.FontByName("sf_grey_7.pft");
    protected PftFont? RowFontHover => Host?.FontByName("sf_yellow_7.pft");
    protected PftFont? HeadFont(bool disabled) => Host?.FontByTag(disabled ? 150130u : 150120u);
    protected PftFont? ShadowFont => Host?.FontByTag(150171);

    protected override PftFont? RowFont => RowFontNormal;
    // 004b36e4: the row is the sf_orange_7 height plus six.
    public new int RowHeight => GuiPaint.FontHeight(RowFontNormal) + 6;

    protected int ColumnsWidth => Columns.Where(c => c.Visible).Sum(c => c.Width);
    protected bool Disabled => !Active || Dimmed;

    public int HoverRow = -1;

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

    // 004d5488, 004bcefc: the list is as wide as its scroll (at least as wide as its columns) and exactly as high as the header and rows.
    public override void PreRender()
    {
        if (Parent is not GuiScrollContent pc)
            return;
        if (pc.Parent is GuiScroll sc)
            sc.Disabled = Disabled;
        W = Math.Max(ColumnsWidth, pc.W);
        H = HeaderH + RowCount * RowHeight;
    }

    protected int RowAt(int ly)
    {
        int h = HeaderH;
        if (ly < h)
            return -1;
        int row = (ly - h) / RowHeight;
        return row < RowCount ? row : -1;
    }

    public override bool MsMove(GuiEvent e)
    {
        bool r = base.MsMove(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        HoverRow = Active && lx >= 0 && lx < W ? RowAt(ly) : -1;
        return r;
    }

    public override bool MsLeave(GuiEvent e)
    {
        HoverRow = -1;
        return base.MsLeave(e);
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        bool r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        int row = RowAt(ly);
        if (row >= 0)
            Select(row);
        return r;
    }

    // ---- drawing ----

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        bool off = Disabled;
        DrawHeader(p, ox, oy, gx, off);
        int rh = RowHeight, top = gy + HeaderH, count = RowCount;
        for (int row = 0; row < count; ++row)
        {
            int y = top + row * rh;
            if (y > p.Clip.Bottom)
                break;
            if (y + rh <= p.Clip.Top)
                continue;
            GfxRect saved = p.Clip;
            // 0053ccd8: a row is drawn clipped to its own rectangle, so its bottom grid line belongs to the next row.
            p.SetClip(saved.Intersect(new GfxRect(gx, y, gx + Math.Max(W, ColumnsWidth) - 1, y + rh - 1)));
            DrawRow(p, ox, gx, y, row, rh, off);
            p.SetClip(saved);
        }
        // 004b4584: a line along the bottom edge of the list.
        if (count > 0)
            p.Line(gx, gy + H - 1, gx + W - 1, gy + H - 1, off ? LineOff : LineOn);
    }

    // 004b3e1c. The header stays at the top of the scroll's view (x and y are the parent's origin), its columns follow the list.
    private void DrawHeader(GuiPaint p, int ox, int oy, int gx, bool off)
    {
        int hh = HeaderH;
        if (hh <= 0)
            return;
        int pw = Parent?.W ?? W;
        p.Fill(ox, oy, pw, hh, off ? HeaderFillOff : HeaderFill);
        int x = gx;
        foreach (ListColumn c in Columns)
        {
            if (!c.Visible)
                continue;
            x += c.Width;
            p.Line(x, oy, x, oy + hh, off ? LineOff : LineOn);
        }
        PftFont? font = HeadFont(off);
        x = gx + 1;
        foreach (ListColumn c in Columns)
        {
            if (!c.Visible)
                continue;
            if (c.HeaderTag >= 0 && Host?.TagText((uint)c.HeaderTag) is string t)
                DrawCellText(p, font, true, true, c.Width - 4, hh, x + 2, oy, t);
            x += c.Width;
        }
        p.Outline(ox, oy, pw, hh, off ? LineOff : LineOn);
    }

    // 004b426c. Column cells are centred in the column less four pixels, with the text two pixels in and two up.
    protected virtual void DrawRow(GuiPaint p, int ox, int gx, int y, int row, int rh, bool off)
    {
        bool selected = row == Selected;
        PftFont? font = off ? RowFontOff : selected ? RowFontSelected : HoverRow == row ? RowFontHover : RowFontNormal;
        int pw = Parent?.W ?? W;
        int line = RowLine(off);
        int x = gx;
        foreach (ListColumn c in Columns)
        {
            if (!c.Visible)
                continue;
            p.Line(x, y, x, y + rh - 1, line);
            x += c.Width;
        }
        p.Line(x, y, x, y + rh - 1, line);
        x = gx;
        foreach (ListColumn c in Columns)
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

    // 004b3b54: the text of a cell. A text narrower than the rectangle is centred (or left aligned), centre line from the
    // font height; a text that does not fit is cut to leave room for "..." and left aligned. With the shadow flag the shadow
    // font draws the same text one pixel left and, for the fitted case, one down.
    protected void DrawCellText(GuiPaint p, PftFont? font, bool center, bool shadow, int w, int h, int x, int y, string text)
    {
        if (font == null)
            return;
        PftFont? sh = shadow ? ShadowFont : null;
        int fh = GuiPaint.FontHeight(font);
        int cy = Div2(h - fh) + y;
        int tw = GuiPaint.TextWidth(font, text);
        if (tw < w)
        {
            int tx = center ? Div2(w - tw) + x : x;
            if (sh != null)
                p.Text(sh, tx - 1, cy, text);
            p.Text(font, tx, cy - 1, text);
            return;
        }
        int ell = GuiPaint.TextWidth(font, "...");
        if (ell > w)
            return;
        string s = text;
        while (s.Length > 0 && GuiPaint.TextWidth(font, s) > w - ell)
            s = s[..^1];
        s += "...";
        if (sh != null)
            p.Text(sh, x - 1, cy + 1, s);
        p.Text(font, x, cy, s);
    }

    // The original halves with an arithmetic shift and adds one back for odd negatives: division toward zero.
    protected static int Div2(int v) => v / 2;
}

// FearGui::MasterServerList [code 004d5d08 ctor, 004d5368 wake, 004d55e0 add, 004d5708 delete, 004d5840 select by id,
// 004d54d0 select row]: the master servers of the Network options. Two columns of 122 pixels (Master Server, Address), rows
// sorted by name without regard to case, the selected row in white.
public sealed class GuiMasterList : GuiColumnList
{
    private static readonly ListColumn[] sColumns =
    {
        new(131310, 0, 122), // IDSTR_SI_MASTER_SERVER
        new(131306, 1, 122), // IDSTR_SI_ADDRESS
    };

    private readonly List<string> mAddresses = new();

    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    protected override int RowCount => Entries.Count;

    protected override string? Cell(int dataIndex, int row) =>
        row >= 0 && row < Entries.Count ? dataIndex == 0 ? Entries[row].Text : dataIndex == 1 ? mAddresses[row] : null : null;

    public string AddressOf(int row) => mAddresses[row];

    // 004d55e0: the name and address are cut to 255 characters, the list is sorted again; the selection stays at its row.
    public void AddMaster(string name, string address, int id)
    {
        if (name.Length > 255) name = name[..255];
        if (address.Length > 255) address = address[..255];
        int selId = Selected >= 0 ? Entries[Selected].Id : int.MinValue;
        var all = new List<(string Name, string Addr, int Id)>();
        for (int i = 0; i < Entries.Count; ++i)
            all.Add((Entries[i].Text, mAddresses[i], Entries[i].Id));
        all.Add((name, address, id));
        var sorted = all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Entries.Clear();
        mAddresses.Clear();
        foreach (var e in sorted)
        {
            Entries.Add((e.Name, e.Id));
            mAddresses.Add(e.Addr);
        }
        Selected = selId == int.MinValue ? -1 : Entries.FindIndex(e => e.Id == selId);
    }

    public override void DeleteById(int id)
    {
        for (int i = Entries.Count - 1; i >= 0; --i)
        {
            if (Entries[i].Id != id)
                continue;
            Entries.RemoveAt(i);
            mAddresses.RemoveAt(i);
            if (Selected == i) Selected = -1; else if (Selected > i) --Selected;
        }
    }

    public override void Clear()
    {
        Entries.Clear();
        mAddresses.Clear();
        Selected = -1;
    }

    // 004d54d0: a selected row sets $Server::CurrentMaster to its id and $Server::Master1 to its address.
    protected override void Selecting(int row)
    {
        if (row < 0 || row >= Entries.Count || Host == null)
            return;
        Host.SetVar("$Server::CurrentMaster", Entries[row].Id.ToString(CultureInfo.InvariantCulture));
        Host.SetVar("$Server::Master1", mAddresses[row]);
    }
}

// FearGui::FilterCtrl [code 004be06c wake]: the filter builder of the Filters options. Its header has four columns (Variable
// 70, Condition 87, Value 67 and one of 32 without a heading) and no rows until a filter is chosen.
public sealed class GuiFilterCtrl : GuiColumnList
{
    private static readonly ListColumn[] sColumns =
    {
        new(131600, 0, 70), new(131601, 1, 87), new(131602, 2, 67), new(-1, 3, 32),
    };

    // DAT_00628370 of the original: the chosen filter (0 or less: none).
    public int ChosenFilter;

    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    protected override int RowCount => 0;
    protected override string? Cell(int dataIndex, int row) => null;

    // 004d8fe4: with no filter chosen the box around the filter list is drawn dimmed.
    public override void Wake()
    {
        base.Wake();
        for (GuiControl? c = Parent; c != null; c = c.Parent)
        {
            if (c is GuiBox box)
            {
                box.ForceDim = ChosenFilter < 1;
                break;
            }
        }
    }
}

// The twelve columns of the server browser and their display state [code 00627300 table, 004bcc58 load, 004bcdac save]. Each has a
// name tag, a description tag, the position in the browser (order), whether it shows, and a width. The state lives in the
// console variables $pref::ServerColumnDisplay0 to 11, each "order, YES|NO, width".
public sealed class ServerColumns
{
    public sealed class Entry
    {
        public int NameTag, DescTag, Order, Width;
        public bool Display = true;
    }

    // Name tag, description tag, width: the default order is the table order and every column shows.
    private static readonly (int Name, int Desc, int Width)[] sTable =
    {
        (131309, 131519, 52), (131308, 131517, 52), (131307, 131516, 54), (131301, 131510, 105), (131303, 131512, 43),
        (131311, 131520, 100), (131302, 131511, 90), (132054, 131513, 59), (131305, 131514, 33), (131306, 131515, 102),
        (131803, 131518, 100), (131312, 131521, 100),
    };

    public const int Count = 12;
    public readonly Entry[] Entries = new Entry[Count];

    public ServerColumns()
    {
        for (int i = 0; i < Count; ++i)
            Entries[i] = new Entry { NameTag = sTable[i].Name, DescTag = sTable[i].Desc, Order = i, Width = sTable[i].Width };
    }

    // 004bcc58: a variable that is set overrides the entry; when the orders are no permutation of 0 to 11 they reset and are written back.
    public void Load(GuiHost host)
    {
        for (int i = 0; i < Count; ++i)
        {
            string v = host.GetVar("$pref::ServerColumnDisplay" + i.ToString(CultureInfo.InvariantCulture));
            if (string.IsNullOrEmpty(v))
                continue;
            string[] parts = v.Split(',');
            if (parts.Length < 2)
                continue;
            if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
                Entries[i].Order = order;
            if (parts.Length >= 3)
            {
                Entries[i].Display = string.Equals(parts[1].Trim(), "YES", StringComparison.OrdinalIgnoreCase);
                if (int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
                    Entries[i].Width = w;
            }
        }
        if (!IsPermutation())
        {
            for (int i = 0; i < Count; ++i)
                Entries[i].Order = i;
            Save(host);
        }
    }

    public bool IsPermutation()
    {
        var seen = new bool[Count];
        foreach (Entry e in Entries)
        {
            if (e.Order < 0 || e.Order >= Count || seen[e.Order])
                return false;
            seen[e.Order] = true;
        }
        return true;
    }

    public void Save(GuiHost host)
    {
        for (int i = 0; i < Count; ++i)
            host.SetVar("$pref::ServerColumnDisplay" + i.ToString(CultureInfo.InvariantCulture),
                $"{Entries[i].Order}, {(Entries[i].Display ? "YES" : "NO")}, {Entries[i].Width}");
    }

    // The entry shown at a position of the options list.
    public Entry? AtOrder(int order) => Array.Find(Entries, e => e.Order == order);

    // 004bcad4: the entry moves to the row `target`, the entries between shift one place the other way.
    public void MoveTo(Entry e, int target)
    {
        target = Math.Clamp(target, 0, Count - 1);
        int step = Math.Sign(target - e.Order);
        if (step == 0)
            return;
        int cur = e.Order;
        // The lookup by order is taken before any order changes (the original fills a table first).
        var byOrder = new Entry?[Count];
        foreach (Entry x in Entries)
            if (x.Order >= 0 && x.Order < Count)
                byOrder[x.Order] = x;
        for (int o = target; o != cur; o -= step)
            if (byOrder[o] is Entry shifted)
                shifted.Order -= step;
        e.Order = target;
    }
}

// FearGui::ColumnListCtrl [code 004bd678 ctor, 004bce4c wake, 004bcf44 row, 004bc9a4 down, 004bcad4 drag, 004bcc28 up]: the Columns
// options. Three columns (NAME 70, DESCRIPTION 161, DISP 36), one row per browser column in browser order. A press on a name
// picks the row up (white) and dragging moves it, a press in DISP toggles YES and NO; the release writes the preferences.
public sealed class GuiOptionColumns : GuiColumnList
{
    private static readonly ListColumn[] sColumns = { new(131501, 0, 70), new(131502, 1, 161), new(131503, 2, 36) };

    public readonly ServerColumns Data = new();
    private ServerColumns.Entry? mDrag, mToggle;
    private int mDragRow = -1;

    protected override IReadOnlyList<ListColumn> Columns => sColumns;
    protected override int RowCount => ServerColumns.Count;
    protected override int RowLine(bool disabled) => 0xf8;

    protected override string? Cell(int dataIndex, int row)
    {
        ServerColumns.Entry? e = Data.AtOrder(row);
        if (e == null)
            return null;
        return dataIndex switch
        {
            0 => Host?.TagText((uint)e.NameTag),
            1 => Host?.TagText((uint)e.DescTag),
            _ => e.Display ? "YES" : "NO",
        };
    }

    public override void Wake()
    {
        base.Wake();
        if (Host != null)
            Data.Load(Host);
    }

    protected override void DrawRow(GuiPaint p, int ox, int gx, int y, int row, int rh, bool off)
    {
        ServerColumns.Entry? e = Data.AtOrder(row);
        bool held = !off && mDrag != null && e == mDrag;
        PftFont? font = off ? RowFontOff : held ? RowFontSelected : mDrag == null && HoverRow == row ? RowFontHover : RowFontNormal;
        PftFont? dispFont = !off && mToggle != null && e == mToggle ? RowFontSelected : font;
        int pw = Parent?.W ?? W;
        int line = RowLine(off);
        int x = gx;
        foreach (ListColumn c in Columns)
        {
            p.Line(x, y, x, y + rh - 1, line);
            x += c.Width;
        }
        p.Line(x, y, x, y + rh - 1, line);
        x = gx;
        foreach (ListColumn c in Columns)
        {
            if (Cell(c.DataIndex, row) is string t)
                DrawCellText(p, c.DataIndex == 2 ? dispFont : font, true, false, c.Width - 4, rh, x + 2, y - 2, t);
            x += c.Width;
        }
        // The outline of the row (ox, y) to (ox + pw - 1, y + rh): its top and the right edge show, the bottom is the next row's top.
        p.Line(ox, y, ox + pw - 1, y, line);
        p.Line(ox + pw - 1, y, ox + pw - 1, y + rh - 1, line);
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        bool r = base.MsDown(e);
        mDrag = mToggle = null;
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        if (ly < HeaderH || Selected < 0)
            return r;
        ServerColumns.Entry? hit = Data.AtOrder(Selected);
        if (hit == null)
            return r;
        if (lx >= 0 && lx <= sColumns[0].Width)
        {
            mDrag = hit;
            mDragRow = Selected;
        }
        else if (lx >= sColumns[0].Width + sColumns[1].Width && lx <= sColumns[0].Width + sColumns[1].Width + sColumns[2].Width)
        {
            mToggle = hit;
            hit.Display = !hit.Display;
        }
        return r;
    }

    public override bool MsDrag(GuiEvent e)
    {
        if (mDrag != null)
        {
            var (_, ly) = GlobalToLocal(e.X, e.Y);
            int target = ly < 0 || HeaderH * 2 + RowHeight * ServerColumns.Count < ly ? mDragRow : Math.Clamp((ly - HeaderH) / RowHeight, 0, ServerColumns.Count - 1);
            if (target != mDrag.Order)
            {
                Data.MoveTo(mDrag, target);
                mDragRow = target;
                Selected = target;
            }
        }
        return base.MsDrag(e);
    }

    public override bool MsUp(GuiEvent e)
    {
        if (Host != null)
            Data.Save(Host);
        mDrag = mToggle = null;
        mDragRow = -1;
        return base.MsUp(e);
    }
}

// FearGui::ServerListCtrl [code 004c4b84 ctor, 004c7b98 wake, 004c7080 open]: the server browser of the join screen. Its columns are the
// server column options (ServerColumns): the entries with Display set, in order. The rows are the servers the client knows
// (config\GameServerList.cs and the answers of the query, ServerQuery); the cells follow the cell query 004c8278.
public sealed class GuiServerList : GuiColumnList
{
    // One record of the browser [0x458 bytes in the exe; the offsets are of that record]. State +0x240: 0 info received, 1 pinged,
    // 2 being queried, 3 unknown, 4 timed out.
    public sealed class Server
    {
        public int Id;
        public string Name, Address, Version;
        public int Ping;
        public bool Favorite;
        public int State = 3;
        public int Players, MaxPlayers, Cpu;
        public bool Password, Dedicated;
        public string Mission = "", MissionType = "", ModList = "", Info = "";

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

    public const int StateInfo = 0, StatePinged = 1, StateQuerying = 2, StateUnknown = 3, StateTimeout = 4;

    // The widths of the browser's column records [table at 00628cf4]; the third number of the preference is not the width.
    private static readonly int[] sWidths = { 52, 52, 54, 105, 43, 90, 90, 59, 33, 102, 100, 90 };

    public readonly ServerColumns Data = new();
    private readonly List<Server> mServers = new();
    private List<ListColumn> mColumns = new();

    // +0x3b0: rows that failed stay in the list (the stock refresh of visible servers and the cancel set it).
    public bool KeepFailed;
    // pref::ConnectionGoodPing and PoorPing: the ping icon (004c8278 column 1; the exe defaults are 200 and 350).
    public int GoodPing = 200, PoorPing = 350;

    protected override IReadOnlyList<ListColumn> Columns => mColumns;
    protected override int RowCount => mServers.Count;
    public IReadOnlyList<Server> Servers => mServers;

    // 004c774c (REFRESH ALL): every known row has no answer yet and failures drop rows again.
    public void MarkAllUnknown()
    {
        foreach (Server s in mServers)
        {
            s.Ping = 9999;
            s.State = StateUnknown;
        }
        KeepFailed = false;
    }

    // 004c6888: the hand over the rows and the header; the horizontal adjust cursor on a column border of the header.
    public override uint CursorTagAt(int lx, int ly)
    {
        int y = Y + ly;
        if (y < 0 || y >= HeaderH)
            return CursorHand;
        int edge = 0;
        foreach (ListColumn c in Columns)
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

    // 004c6940: after 500 ms the header shows the description tag of the column under the cursor (a column border shows none).
    // The tags are the IDHELP_SERVER_* entries (220603 favorite, 220604 connection, 220605 status, 220606 name, 220608 ping, 220614 mission type, 220607 mission,
    // 220609 players, 220610 cpu, 220611 address, 220612 version, 220613 mod).
    private static readonly uint[] sHelp = { 220603, 220604, 220605, 220606, 220608, 220614, 220607, 220609, 220610, 220611, 220612, 220613 };

    public override uint HelpTagAt(int lx, int ly, long ms)
    {
        int y = Y + ly;
        if (ms < 500 || y < 0 || y >= HeaderH)
            return 0;
        int edge = 0;
        foreach (ListColumn c in Columns)
        {
            if (!c.Visible)
                continue;
            edge += c.Width;
            if (lx >= edge - 2 && lx <= edge + 2)
                return 0;
            if (lx < edge)
                return sHelp[c.DataIndex];
        }
        return 0;
    }

    public Server? Find(string address) => mServers.Find(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

    public void AddServer(Server s)
    {
        mServers.Add(s);
        AddEntry(s.Name, s.Id);
    }

    public void RemoveServer(Server s)
    {
        int i = mServers.IndexOf(s);
        if (i < 0)
            return;
        mServers.RemoveAt(i);
        if (i < Entries.Count)
            Entries.RemoveAt(i);
        if (Selected == i)
            Selected = -1;
        else if (Selected > i)
            --Selected;
    }

    public override void Clear()
    {
        base.Clear();
        mServers.Clear();
    }

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

    private static int ParsePref(string v, int d) => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : d;

    // The visible columns in browser order; the data index is the entry index (0 favorites .. 11 server type).
    private void Rebuild()
    {
        mColumns = Data.Entries.Select((e, i) => (e, i)).OrderBy(t => t.e.Order)
            .Select(t => new ListColumn(t.e.NameTag, t.i, sWidths[t.i], t.e.Display)).ToList();
    }

    // 004c8278: the cell text of a column. The numbers use "%d", the players "%d / %d", an empty mission or a cpu speed of zero
    // shows N/A. Columns 0 to 2 are icons (DrawIcons).
    protected override string? Cell(int dataIndex, int row)
    {
        if (row < 0 || row >= mServers.Count)
            return null;
        Server s = mServers[row];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
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

    // 004c8278 columns 0 to 2: bitmaps (I_Favorite, I_PingGreen/Yellow/Red, I_Locked, I_Dedicated, I_Unknown, I_Pinging, I_Querying,
    // I_Timeout) five pixels into the cell and centred on the row height (plus one).
    protected override bool DrawIcons(GuiPaint p, int dataIndex, int row, int x, int y, int rh)
    {
        if (dataIndex > 2 || Host == null)
            return false;
        // A disabled or dimmed list (a modal dialog above it) shows no icons.
        if (Disabled)
            return true;
        Server s = mServers[row];
        void Put(string name, ref int px)
        {
            PbmpBitmap? b = Host.BitmapByName(name);
            if (b == null)
                return;
            p.Bitmap(b, px, y + Div2(rh - b.Height) + 1);
            px += b.Width + 3;
        }
        int ix = x + 5;
        switch (dataIndex)
        {
            case 0:
                if (s.Favorite)
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
