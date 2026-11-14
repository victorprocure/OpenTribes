namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;

/// <summary>The part the Objects and Controllables lists of the command screen share (command.gui, docs/gui.md 3.3 "The command screen panels").</summary>
/// <remarks>
/// <para>Shared by CommandItemCtrl (open 004a94d4, prepare 004a98b8, cell draw 004a9a08, cell select 004a973c, mouse down 004a97b0, cursor 004a9714) and CommandTurretCtrl (open 004aa21c, prepare 004aa6b8, cell draw 004aa814, cell select 004aa644, mouse down 004aa4b4, cursor 004aa48c).</para>
/// <para>A row is a command map slot of the local team (the entry's first int is the slot). The cell is 640 wide and as high as the font of tag 150151 (if_w_10b) plus 2.</para>
/// <para>Draw, from the cell origin: a 0xfc fill from the damage and active picture widths plus 3 to the cell's right edge while the slot is selected, the damage picture of the slot (status 2 high, 1 medium, else low) at (x + 2, y + 1), the active picture (CMD_ItemActive when the slot's second flag is set, else CMD_ItemInactive) after it at y + 1, then the name 2 pixels above the row top and 2 pixels after the active picture. Nothing is drawn without the command map (DAT_006a83d8). The byte (+0x210 / +0x214) that would pick the small font is cleared by open and no code in the exe sets it, so the 10 pixel fonts are always used.</para>
/// </remarks>
public abstract class GuiCommandObjectList : GuiList
{
    /// <summary>The cell width in pixels.</summary>
    protected const int CellWidth = 640;
    private const uint TagWaypointArrow = 160558;

    /// <summary>Gets the rows, rebuilt before each render.</summary>
    public IReadOnlyList<CommandListRow> Rows { get; private set; } = [];

    /// <summary>Returns the rows of the list from the HUD source.</summary>
    /// <param name="hud">The HUD source.</param>
    /// <returns>The rows.</returns>
    protected abstract IReadOnlyList<CommandListRow> Source(IHudSource hud);
    /// <summary>Returns the font of a slot's name.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The font.</returns>
    /// <remarks>Follows 004a9a08 (always white) and 004aa814 (white for a controllable slot, grey else).</remarks>
    protected abstract PftFont? NameFont(in Game.ClientCommandSlot slot);
    /// <summary>Applies the selection a row makes on the command map (004a973c, 004aa644).</summary>
    /// <param name="map">The command map.</param>
    /// <param name="slot">The slot of the row.</param>
    protected abstract void SelectSlot(GuiCommandMap map, int slot);

    /// <summary>Gets the command map control, searched from the root of the control tree and then the canvas content.</summary>
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

    /// <inheritdoc/>
    protected override PftFont? RowFont => Host?.FontByName("if_w_10b.pft") ?? base.RowFont;

    /// <summary>Returns the cursor tag: the waypoint arrow while the map waits for the order's target, else the control's own cursor (004a9714, 004aa48c).</summary>
    /// <param name="lx">The local x.</param>
    /// <param name="ly">The local y.</param>
    /// <returns>The cursor tag.</returns>
    public override uint CursorTagAt(int lx, int ly) => Map is { OrderMode: 1 } ? TagWaypointArrow : base.CursorTagAt(lx, ly);

    /// <summary>Rebuilds the rows from the slot table.</summary>
    /// <remarks>Follows 004a98b8 and 004aa6b8: the count is zero without a local client.</remarks>
    public override void PreRender()
    {
        Rows = Host?.Hud is IHudSource hud ? Source(hud) : [];
        Entries.Clear();
        foreach (var r in Rows)
            Entries.Add((r.Name, r.Slot));
        base.PreRender();
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004a973c and 004aa644: a row below the count with the map present. The map's selection first loses the players and the other kinds.</remarks>
    protected override bool Accepts(int row) => row < 0 || (row < Rows.Count && Map != null);

    /// <inheritdoc/>
    protected override void Selecting(int row)
    {
        if (row < 0 || row >= Rows.Count || Map is not GuiCommandMap map)
            return;
        map.SelectClient(-1, false);
        SelectSlot(map, Rows[row].Slot);
    }

    /// <summary>Returns the cell under the mouse as the stock divides it.</summary>
    /// <param name="e">The mouse event.</param>
    /// <returns>The column and row, -1 outside the control.</returns>
    protected (int Col, int Row) CellAt(GuiEvent e)
    {
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        return (lx < 0 ? -1 : lx / CellWidth, ly < 0 ? -1 : ly / RowHeight);
    }

    /// <summary>Handles a click while the order cursor is on: the click is the target of the order.</summary>
    /// <param name="e">The mouse event.</param>
    /// <returns>True when the order branch ran.</returns>
    /// <remarks>Follows 004a97b0 and 004aa4b4. The label of the row goes to $CommandTargetName (not $CommandTarget: only the team list sets that), the order goes to the slot and the cursor returns to select. A click outside the rows does nothing.</remarks>
    protected bool OrderClick(GuiEvent e)
    {
        var map = Map;
        if (!Active || map is not { OrderMode: 1 })
            return false;
        var (col, row) = CellAt(e);
        if (col >= 0 && col < 1 && row >= 0 && row < Rows.Count)
        {
            Host?.SetVar("$CommandTargetName", Rows[row].Name);
            map.IssueTargetOrder(Rows[row].Slot);
            map.SetOrderMode(0);
        }
        return true;
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (OrderClick(e))
            return true;
        return base.MsDown(e);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var map = Map;
        var slots = Host?.Hud?.CommandSlotTable;
        if (Host == null || map == null || slots == null)
            return;
        PbmpBitmap? low = Host.BitmapByName("CMD_DamageLow.bmp"), med = Host.BitmapByName("CMD_DamageMed.bmp"), high = Host.BitmapByName("CMD_DamageHigh.bmp");
        PbmpBitmap? active = Host.BitmapByName("CMD_ItemActive.bmp"), inactive = Host.BitmapByName("CMD_ItemInactive.bmp");
        var rowH = RowHeight;
        for (var i = 0; i < Rows.Count; ++i)
        {
            var r = Rows[i];
            if (r.Slot < 0 || r.Slot >= slots.Length)
                continue;
            var slot = slots[r.Slot];
            var py = gy + (i * rowH);
            if (map.Selected[r.Slot])
            {
                var left = (high?.Width ?? 0) + (active?.Width ?? 0) + 3;
                p.Fill(gx + left, py, CellWidth - left, rowH, 0xfc);
            }
            var dmg = slot.Status == 2 ? high : slot.Status == 1 ? med : low;
            if (dmg == null)
                continue;
            var x = gx + 2;
            p.Bitmap(dmg, x, py + 1);
            x += dmg.Width + 1;
            var flag = slot.Active ? active : inactive;
            if (flag != null)
                p.Bitmap(flag, x, py + 1);
            x += (flag?.Width ?? 0) + 2;
            p.Text(NameFont(slot), x, py - 2, r.Name);
        }
    }
}

/// <summary>The Objects tab list (FearGui::CommandItemCtrl): the statics and sensors of the local team (datablock groups 4 and 8) in the file's tab 140117.</summary>
/// <remarks>A click selects that static (004d28a8) after dropping the selected players and turrets.</remarks>
public sealed class GuiCommandItems : GuiCommandObjectList
{
    /// <inheritdoc/>
    protected override IReadOnlyList<CommandListRow> Source(IHudSource hud) => hud.CommandObjects;
    /// <inheritdoc/>
    protected override PftFont? NameFont(in Game.ClientCommandSlot slot) => Host?.FontByName("if_w_10b.pft");
    /// <inheritdoc/>
    protected override void SelectSlot(GuiCommandMap map, int slot)
    {
        map.SelectTurret(-1);
        map.SelectStatic(slot);
    }
}

/// <summary>The Controllables tab list (FearGui::CommandTurretCtrl): the turrets and vehicles of the local team (groups 0x13, 10, 0xb, 9) in the file's tab 140116.</summary>
/// <remarks>A click selects that turret (004d27d0) after dropping the players and statics. A second click on the same row (click count above 1) while a turret is selected mounts it: `remoteEval(2048, CmdrMountObject, &lt;target&gt;);` (004aa4b4).</remarks>
public sealed class GuiCommandTurrets : GuiCommandObjectList
{
    private int _prevRow = -1;

    /// <inheritdoc/>
    protected override IReadOnlyList<CommandListRow> Source(IHudSource hud) => hud.CommandControllables;

    /// <inheritdoc/>
    /// <remarks>Follows 004aa814: the white font for a controllable slot (+0x361), the grey one (if_gr_10b, tag 150154) else.</remarks>
    protected override PftFont? NameFont(in Game.ClientCommandSlot slot) =>
        Host?.FontByName(slot.Controllable ? "if_w_10b.pft" : "if_gr_10b.pft");

    /// <inheritdoc/>
    protected override void SelectSlot(GuiCommandMap map, int slot)
    {
        map.SelectStatic(-1);
        map.SelectTurret(slot);
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (OrderClick(e))
            return true;
        var handled = base.MsDown(e);
        // 004aa5d0: after the base press, a click count above 1 on the row the previous press left selected, with a turret selected on the map, mounts it.
        if (e.ClickCount > 1 && Selected == _prevRow && Map?.SelectedTurretTarget() is int target and >= 0)
            Host?.Eval("remoteEval(2048, CmdrMountObject, " + target.ToString(CultureInfo.InvariantCulture) + ");");
        _prevRow = Selected;
        return handled;
    }
}
