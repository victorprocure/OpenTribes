// The side panels of the command screen (command.gui): docs/gui.md 3.3 "The command screen panels" "The command screen panels".
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// One row of the command team list: a player of the local player's team in the command tree (PlayerManager_buildCommandTree
// [code 0040b46c]). Ancestors are the nodes above it, nearest first, with the depth of each and whether it has a next sibling.
// Damage is the status of its command map slot (0 low, 1 medium, 2 high), Order the command status of the slot (the record's command
// status + 1: 2 pending, 3 acknowledged, else none).
public sealed record CommandTeamRow(int Id, string Name, int Depth, bool HasChildren, bool Expanded, bool HasNext, int Damage, int Order,
    IReadOnlyList<(int Depth, bool HasNext)> Ancestors, bool Selected = false);

// One row of the Objects or Controllables list: the command map slot (the entry's first int) and the name shown (the slot's label, else the
// datablock description) [code 004a98b8, 004aa6b8].
public sealed record CommandListRow(int Slot, string Name);

// A chat menu object on display in a FGChatMenu control: Active is false for a menu that is off (or has no items), Path is the headings
// above the shown level joined with ':' (empty at the top level), Lines the items as "key: text".
public sealed record ChatMenuState(bool Active, string Path, IReadOnlyList<string> Lines);

// FearGui::FGCommandPanel [code 004a159c]: no base render. A polygon of five points (the top left, the top right, the bottom right, the
// bottom edge `corner` pixels from the left and the left edge `corner` pixels above the bottom: the bottom left corner is cut) is filled
// with palette 0xf7 and its edges are drawn with 0xf8; then the children draw. The corner is 9 pixels (4 when the byte at +0x1ac is set,
// which nothing in the exe reads or sets: kept 0).
public sealed class GuiCommandPanel : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        const int corner = 9;
        int x0 = gx, y0 = gy, x1 = gx + W - 1, y1 = gy + H - 1;
        for (int y = y0; y <= y1; ++y)
        {
            int left = y <= y1 - corner ? x0 : x0 + (y - (y1 - corner));
            p.Fill(left, y, x1 - left + 1, 1, 0xf7);
        }
        p.Line(x0, y0, x1, y0, 0xf8);
        p.Line(x1, y0, x1, y1, 0xf8);
        p.Line(x1, y1, x0 + corner, y1, 0xf8);
        p.Line(x0 + corner, y1, x0, y1 - corner, 0xf8);
        p.Line(x0, y1 - corner, x0, y0, 0xf8);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FGChatMenu [code 004a7934]: shows the chat menu object named in the file (`CommandChatMenu`) in orange (sf_Orange255_10, white
// for the item under the mouse, sf_white_10 [code 004a774c]) from (3, 1): "Menu inactive" while the menu is off, else its heading (the path
// of headings, "Root Menu:" at the top level) and one "key: text" line per item. An object that is not a chat menu draws nothing.
public sealed class GuiChatMenu : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Host?.Hud?.NamedMenu(S("menuName")) is not ChatMenuState menu)
            return;
        PftFont? orange = Host.FontByName("sf_Orange255_10.pft");
        PftFont? white = Host.FontByName("sf_white_10.pft");
        int x = gx + 3, y = gy + 1;
        if (!menu.Active)
        {
            p.Text(orange, x, y, "Menu inactive");
            return;
        }
        p.Text(orange, x, y, menu.Path.Length == 0 ? "Root Menu:" : menu.Path);
        y += GuiPaint.FontHeight(orange);
        int mx = (int)(Canvas?.CursorX ?? -1) - gx, my = (int)(Canvas?.CursorY ?? -1) - gy;
        foreach (string line in menu.Lines)
        {
            // The line is white while the mouse is inside the control's width and the line's rows [code 004a7934].
            int top = y - gy;
            bool hover = mx >= 0 && mx < W && my >= top && my < top + GuiPaint.FontHeight(orange);
            p.Text(hover ? white : orange, x, y, line);
            y += GuiPaint.FontHeight(orange);
        }
    }
}

// FearGui::CommandTeamCtrl [code 004a8638 cell draw, 004a85c0 prepare, 004a8064 open]: the command tree of the local player's team, one
// row per player, rows of the font height + 2. A row (x from the cell's left): the damage picture (CMD_DamageLow, Med, High) at x + 2,
// one pixel down, then the order picture (CMD_OrderPending or CMD_OrderAck) after it, then at depth * (width of piece 11 of
// CmdListCtrl.pba) from there the tree piece (3 or 7 for a node with children, expanded or not, plus 1 when it has a next sibling and
// the row is the first, else plus 2 or 3 when it is not the first row), the vertical piece 11 at the depth of every ancestor that has
// a next sibling, and the name in if_w_10b (if_w_6 below 512 x 384) at one more depth, 2 pixels above the row top.
public sealed class GuiCommandTeam : GuiList
{
    private IReadOnlyList<CommandTeamRow> mRows = Array.Empty<CommandTeamRow>();
    private int mLastRow = -1;
    private const int CellWidth = 640;
    public IReadOnlyList<CommandTeamRow> Rows => mRows;
    private const uint TagWaypointArrow = 160558;

    private GuiCommandMap? Map
    {
        get
        {
            GuiControl top = this;
            while (top.Parent != null)
                top = top.Parent;
            return (top.FindById(GuiCommandMap.MapId) ?? Canvas?.Content?.FindById(GuiCommandMap.MapId)) as GuiCommandMap;
        }
    }

    // 004a8ad8: the waypoint arrow while the map waits for the order's target.
    public override uint CursorTagAt(int lx, int ly) => Map is { OrderMode: 1 } ? TagWaypointArrow : base.CursorTagAt(lx, ly);

    // 004a8a6c: the first selected row that is not the local client and does not already have the local client above it in the chain, -1 for none.
    public int FirstReportable()
    {
        if (Host?.Hud?.CommandLocal is not CommandLocalClient me)
            return -1;
        foreach (CommandTeamRow r in mRows)
            if ((Host.Hud.CommandLinks(r.Id)?.Selected ?? false) && r.Id != me.Id && !Host.Hud.CommandInChain(r.Id, me.Id))
                return r.Id;
        return -1;
    }

    // 004a850c: select a record (the first of a level), then, for it and each later sibling, the first record under it and so on down.
    private void SelectSubtree(GuiCommandMap map, int id, bool on)
    {
        if (id < 0)
            return;
        map.SelectClient(id, on);
        for (int p = id; p >= 0; p = Host?.Hud?.CommandLinks(p)?.NextSibling ?? -1)
            SelectSubtree(map, Host?.Hud?.CommandLinks(p)?.FirstChild ?? -1, on);
    }

    // 004a8b00: a click on a row. On the tree glyph of a node with children it flips the node open or closed. On the name: with the order cursor on, it
    // gives the order to that player; else a plain click makes it the only selected player, ctrl flips it, shift selects the rows from the last plain click,
    // and a double click does the same for the node and the records under it (004a850c). Any list selection first drops the turret and static selection.
    public override bool MsDown(GuiEvent e)
    {
        GuiCommandMap? map = Map;
        if (!Active || map == null)
            return base.MsDown(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        bool dbl = e.ClickCount > 1;
        int col = lx < 0 ? -1 : lx / CellWidth, row = ly < 0 ? -1 : ly / RowHeight;
        if (col < 0 || col >= 1 || row < 0 || row >= mRows.Count)
            return true;
        CommandTeamRow rec = mRows[row];
        PbmaArray? pieces = Host?.ArrayByName(Low ? "LRCmdListCtrl.pba" : "CmdListCtrl.pba");
        int step = (pieces != null && pieces.Bitmaps.Count > 11 ? pieces.Bitmaps[11].Width : 0) + 2;
        int glyph = (Host?.BitmapByName("CMD_DamageHigh.bmp")?.Width ?? 0) + (Host?.BitmapByName("CMD_OrderAck.bmp")?.Width ?? 0) + step * rec.Depth + 4;
        if (lx >= glyph && lx <= glyph + step && rec.HasChildren)
        {
            Host?.Hud?.CommandToggleExpanded(rec.Id);
            return true;
        }
        if ((rec.Depth + 1) * step + 2 > lx)
            return true;
        if (map.OrderMode == 1)
        {
            Host?.SetVar("$CommandTargetName", rec.Name);
            Host?.SetVar("$CommandTarget", "friendly player");
            int slot = map.SlotOfClient(rec.Id);
            if (slot >= 0)
            {
                map.IssueTargetOrder(slot);
                map.SetOrderMode(0);
            }
            return true;
        }
        if (row != mLastRow)
            dbl = false;
        map.SelectTurret(-1);
        map.SelectStatic(-1);
        bool Live(int id) => Host?.Hud?.CommandLinks(id)?.Selected ?? false;
        if (!e.Shift)
        {
            if (!e.Ctrl)
            {
                Selected = row;
                map.SelectClient(-1, false);
                if (!dbl)
                    map.SelectClient(rec.Id, true);
                else
                    SelectSubtree(map, rec.Id, !Live(rec.Id));
            }
            else if (!dbl)
                map.SelectClient(rec.Id, !Live(rec.Id));
            else
                SelectSubtree(map, rec.Id, !Live(rec.Id));
        }
        else
        {
            if (Selected < 0)
                Selected = row;
            map.SelectClient(-1, false);
            for (int i = Math.Min(Selected, row); i <= Math.Max(Selected, row) && i < mRows.Count; ++i)
                map.SelectClient(mRows[i].Id, true);
        }
        mLastRow = row;
        return true;
    }

    private bool Low => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    protected override PftFont? RowFont => Host?.FontByName("if_w_10b.pft") ?? base.RowFont;

    public override void PreRender()
    {
        mRows = Host?.Hud?.CommandTeam ?? Array.Empty<CommandTeamRow>();
        Entries.Clear();
        foreach (CommandTeamRow r in mRows)
            Entries.Add((r.Name, r.Id));
        base.PreRender();
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        PbmaArray? pieces = Host?.ArrayByName(Low ? "LRCmdListCtrl.pba" : "CmdListCtrl.pba");
        if (Host == null || pieces == null || pieces.Bitmaps.Count < 12)
            return;
        PbmpBitmap? low = Host.BitmapByName("CMD_DamageLow.bmp"), med = Host.BitmapByName("CMD_DamageMed.bmp"), high = Host.BitmapByName("CMD_DamageHigh.bmp");
        PbmpBitmap? pending = Host.BitmapByName("CMD_OrderPending.bmp"), ack = Host.BitmapByName("CMD_OrderAck.bmp");
        PbmpBitmap? ackPic = ack;
        PftFont? font = Host.FontByName(Low ? "if_w_6.pft" : "if_w_10b.pft");
        int step = pieces.Bitmaps[11].Width, rowH = RowHeight;
        for (int i = 0; i < mRows.Count; ++i)
        {
            CommandTeamRow r = mRows[i];
            int py = gy + i * rowH;
            // A selected node first gets a 0xfc fill from the picture area to the cell's right edge [code 004a8638].
            if (r.Selected)
                p.Fill(gx + (high?.Width ?? 0) + (ackPic?.Width ?? 0) + 3, py, CellWidth - ((high?.Width ?? 0) + (ackPic?.Width ?? 0) + 3), rowH, 0xfc);
            PbmpBitmap? dmg = r.Damage == 2 ? high : r.Damage == 1 ? med : low;
            if (dmg == null)
                continue;
            int x = gx + 2;
            p.Bitmap(dmg, x, py + 1);
            x += dmg.Width + 1;
            PbmpBitmap? order = r.Order == 2 ? pending : r.Order == 3 ? ack : null;
            if (order != null)
                p.Bitmap(order, x, py + 1);
            x += (pending?.Width ?? 0) + 2;
            int piece = !r.HasChildren ? -1 : r.Expanded ? 3 : 7;
            if (i == 0)
            {
                if (r.HasNext)
                    ++piece;
            }
            else
                piece += r.HasNext ? 3 : 2;
            if (piece >= 0)
                p.Bitmap(pieces.Bitmaps[piece], r.Depth * step + x, py);
            foreach (var (depth, hasNext) in r.Ancestors)
                if (hasNext)
                    p.Bitmap(pieces.Bitmaps[11], depth * step + x, py);
            p.Text(font, (r.Depth + 1) * step + x, py - 2, r.Name);
        }
    }
}

// FearGui::CMDObserve [code 004b4718 ctor, 004b4818 observe, 004b48bc toggle]: the observer panel of the command screen (the TV button of the
// Players tab shows it). Only the console side is ported: the panel is a plain control that holds the id of the player it follows (+0x1ac, 0 from
// the constructor) and tells the server which player to follow. The small 3D view it carries (child TSControl 140208) is not drawn.
public sealed class GuiCmdObserve : GuiControl
{
    public const uint ViewId = 140208;
    public int Observed;

    private void RemoteObserve(int id) =>
        Host?.Call("remoteEval", "2048", "SCOM", id.ToString(CultureInfo.InvariantCulture));

    // 004b4818 (Client::cmdObservePlayer 0043a6e8): another player than the followed one, and only while the panel is shown, is sent to the server as
    // remoteEval(2048, SCOM, id) and becomes the followed one. Returns whether it did.
    public bool Observe(int id)
    {
        if (id == Observed || !Visible)
            return false;
        RemoteObserve(id);
        Observed = id;
        return true;
    }

    // 004b48bc (Client::toggleCMDObserver 0043a670): a change of the shown state only. On: the first selected record of the local player's records
    // is followed (none: the panel stays off); with the 3D view present the server is told (SCOM, id) and the followed id is set, then the panel is
    // shown. Off: the server is told SCOM, -1 and the panel is hidden (the followed id stays).
    public void Toggle(bool on)
    {
        if (on == Visible)
            return;
        if (!on)
        {
            RemoteObserve(-1);
            SetVisible(false);
            return;
        }
        int id = Host?.Hud?.CommandFirstSelectedClient() ?? -1;
        if (id == -1)
            return;
        if (FindById(ViewId) != null)
        {
            RemoteObserve(id);
            Observed = id;
        }
        SetVisible(true);
    }
}
