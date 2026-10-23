// The side panels of the command screen (command.gui): docs/gui.md 3.3 "The command screen panels" "The command screen panels".
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// One row of the command team list: a player of the local player's team in the command tree (PlayerManager_buildCommandTree
// [code 0040b46c]). Ancestors are the nodes above it, nearest first, with the depth of each and whether it has a next sibling.
// Damage is the status of its command map slot (0 low, 1 medium, 2 high), Order the command status of the slot (the record's command
// status + 1: 2 pending, 3 acknowledged, else none).
public sealed record CommandTeamRow(int Id, string Name, int Depth, bool HasChildren, bool Expanded, bool HasNext, int Damage, int Order,
    IReadOnlyList<(int Depth, bool HasNext)> Ancestors);

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
        PftFont? font = Host.FontByName(Low ? "if_w_6.pft" : "if_w_10b.pft");
        int step = pieces.Bitmaps[11].Width, rowH = RowHeight;
        for (int i = 0; i < mRows.Count; ++i)
        {
            CommandTeamRow r = mRows[i];
            int py = gy + i * rowH;
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
