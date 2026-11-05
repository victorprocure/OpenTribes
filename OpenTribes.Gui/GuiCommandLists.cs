// The Objects and Controllables lists of the command screen (command.gui): docs/gui.md 3.3 "The command screen panels".
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// The part CommandItemCtrl [code 004a94d4 open, 004a98b8 prepare, 004a9a08 cell draw, 004a973c cell select, 004a97b0 mouse down, 004a9714 cursor] and
// CommandTurretCtrl [code 004aa21c open, 004aa6b8 prepare, 004aa814 cell draw, 004aa644 cell select, 004aa4b4 mouse down, 004aa48c cursor] share. A row is a
// command map slot of the local team (the entry's first int is the slot); the cell is 640 wide and the height of the font of tag 150151 (if_w_10b) + 2.
// Draw (from the cell origin): a 0xfc fill from the damage and active picture widths + 3 to the cell's right edge while the slot is selected; the
// damage picture of the slot (status 2 high, 1 medium, else low) at (x + 2, y + 1); the active picture (CMD_ItemActive when the slot's second flag is
// set, else CMD_ItemInactive) after it at y + 1; then the name 2 pixels above the row top, 2 pixels after the active picture. Nothing is drawn without
// the command map (`DAT_006a83d8`). The byte (+0x210 / +0x214) that would pick the small font is cleared by open and no code in the exe sets it, so the
// 10 pixel fonts are always used.
public abstract class GuiCommandObjectList : GuiList
{
    protected const int CellWidth = 640;
    private const uint TagWaypointArrow = 160558;
    private IReadOnlyList<CommandListRow> mRows = Array.Empty<CommandListRow>();
    public IReadOnlyList<CommandListRow> Rows => mRows;

    protected abstract IReadOnlyList<CommandListRow> Source(IHudSource hud);
    // The font of a slot's name [code 004a9a08: always white; 004aa814: white for a controllable slot, grey else].
    protected abstract PftFont? NameFont(in OpenTribes.Game.ClientCommandSlot slot);
    // 004a973c / 004aa644: the selection a row makes.
    protected abstract void SelectSlot(GuiCommandMap map, int slot);

    protected GuiCommandMap? Map
    {
        get
        {
            GuiControl top = this;
            while (top.Parent != null)
                top = top.Parent;
            return (top.FindById(GuiCommandMap.MapId) ?? Canvas?.Content?.FindById(GuiCommandMap.MapId)) as GuiCommandMap;
        }
    }

    protected override PftFont? RowFont => Host?.FontByName("if_w_10b.pft") ?? base.RowFont;

    // 004a9714 / 004aa48c: the waypoint arrow while the map waits for the order's target, else the control's own cursor.
    public override uint CursorTagAt(int lx, int ly) => Map is { OrderMode: 1 } ? TagWaypointArrow : base.CursorTagAt(lx, ly);

    // 004a98b8 / 004aa6b8: the rows are rebuilt from the slot table; the count is zero without a local client.
    public override void PreRender()
    {
        mRows = Host?.Hud is IHudSource hud ? Source(hud) : Array.Empty<CommandListRow>();
        Entries.Clear();
        foreach (CommandListRow r in mRows)
            Entries.Add((r.Name, r.Slot));
        base.PreRender();
    }

    // 004a973c / 004aa644: a row below the count and the map present; the map's selection first loses the players and the other kinds.
    protected override bool Accepts(int row) => row < 0 || (row < mRows.Count && Map != null);

    protected override void Selecting(int row)
    {
        if (row < 0 || row >= mRows.Count || Map is not GuiCommandMap map)
            return;
        map.SelectClient(-1, false);
        SelectSlot(map, mRows[row].Slot);
    }

    // The cell under the mouse as the stock divides it: -1 outside the control.
    protected (int Col, int Row) CellAt(GuiEvent e)
    {
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        return (lx < 0 ? -1 : lx / CellWidth, ly < 0 ? -1 : ly / RowHeight);
    }

    // 004a97b0 / 004aa4b4 with the order cursor on: the click is the target of the order. The label of the row goes to $CommandTargetName (not
    // $CommandTarget: only the team list sets that), the order goes to the slot and the cursor returns to select. A click outside the rows does nothing.
    // Returns true when the order branch ran.
    protected bool OrderClick(GuiEvent e)
    {
        GuiCommandMap? map = Map;
        if (!Active || map is not { OrderMode: 1 })
            return false;
        var (col, row) = CellAt(e);
        if (col >= 0 && col < 1 && row >= 0 && row < mRows.Count)
        {
            Host?.SetVar("$CommandTargetName", mRows[row].Name);
            map.IssueTargetOrder(mRows[row].Slot);
            map.SetOrderMode(0);
        }
        return true;
    }

    public override bool MsDown(GuiEvent e)
    {
        if (OrderClick(e))
            return true;
        return base.MsDown(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        GuiCommandMap? map = Map;
        var slots = Host?.Hud?.CommandSlotTable;
        if (Host == null || map == null || slots == null)
            return;
        PbmpBitmap? low = Host.BitmapByName("CMD_DamageLow.bmp"), med = Host.BitmapByName("CMD_DamageMed.bmp"), high = Host.BitmapByName("CMD_DamageHigh.bmp");
        PbmpBitmap? active = Host.BitmapByName("CMD_ItemActive.bmp"), inactive = Host.BitmapByName("CMD_ItemInactive.bmp");
        int rowH = RowHeight;
        for (int i = 0; i < mRows.Count; ++i)
        {
            CommandListRow r = mRows[i];
            if (r.Slot < 0 || r.Slot >= slots.Length)
                continue;
            var slot = slots[r.Slot];
            int py = gy + i * rowH;
            if (map.Selected[r.Slot])
            {
                int left = (high?.Width ?? 0) + (active?.Width ?? 0) + 3;
                p.Fill(gx + left, py, CellWidth - left, rowH, 0xfc);
            }
            PbmpBitmap? dmg = slot.Status == 2 ? high : slot.Status == 1 ? med : low;
            if (dmg == null)
                continue;
            int x = gx + 2;
            p.Bitmap(dmg, x, py + 1);
            x += dmg.Width + 1;
            PbmpBitmap? flag = slot.Active ? active : inactive;
            if (flag != null)
                p.Bitmap(flag, x, py + 1);
            x += (flag?.Width ?? 0) + 2;
            p.Text(NameFont(slot), x, py - 2, r.Name);
        }
    }
}

// FearGui::CommandItemCtrl: the statics and sensors of the local team (datablock groups 4 and 8) in the file's tab 140117 (the Objects tab). A click
// selects that static (004d28a8) after dropping the selected players and turrets.
public sealed class GuiCommandItems : GuiCommandObjectList
{
    protected override IReadOnlyList<CommandListRow> Source(IHudSource hud) => hud.CommandObjects;
    protected override PftFont? NameFont(in OpenTribes.Game.ClientCommandSlot slot) => Host?.FontByName("if_w_10b.pft");
    protected override void SelectSlot(GuiCommandMap map, int slot)
    {
        map.SelectTurret(-1);
        map.SelectStatic(slot);
    }
}

// FearGui::CommandTurretCtrl: the turrets and vehicles of the local team (groups 0x13, 10, 0xb, 9) in the file's tab 140116 (the Controllables tab). A
// click selects that turret (004d27d0) after dropping the players and statics. A second click on the same row (click count above 1) while a turret is
// selected mounts it: `remoteEval(2048, CmdrMountObject, <target>);` [004aa4b4].
public sealed class GuiCommandTurrets : GuiCommandObjectList
{
    private int mPrevRow = -1;

    protected override IReadOnlyList<CommandListRow> Source(IHudSource hud) => hud.CommandControllables;

    // 004aa814: the white font for a controllable slot (+0x361), the grey one (if_gr_10b, tag 150154) else.
    protected override PftFont? NameFont(in OpenTribes.Game.ClientCommandSlot slot) =>
        Host?.FontByName(slot.Controllable ? "if_w_10b.pft" : "if_gr_10b.pft");

    protected override void SelectSlot(GuiCommandMap map, int slot)
    {
        map.SelectStatic(-1);
        map.SelectTurret(slot);
    }

    public override bool MsDown(GuiEvent e)
    {
        if (OrderClick(e))
            return true;
        bool handled = base.MsDown(e);
        // 004aa5d0: after the base press, a click count above 1 on the row the previous press left selected, with a turret selected on the map, mounts it.
        if (e.ClickCount > 1 && Selected == mPrevRow && Map?.SelectedTurretTarget() is int target and >= 0)
            Host?.Eval("remoteEval(2048, CmdrMountObject, " + target.ToString(CultureInfo.InvariantCulture) + ");");
        mPrevRow = Selected;
        return handled;
    }
}
