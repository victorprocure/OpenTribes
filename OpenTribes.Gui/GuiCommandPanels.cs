namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;

/// <summary>One row of the command team list: a player of the local player's team in the command tree.</summary>
/// <param name="Id">The client id.</param>
/// <param name="Name">The player name.</param>
/// <param name="Depth">The depth in the tree.</param>
/// <param name="HasChildren">True when the node has subordinates.</param>
/// <param name="Expanded">True when the node is open.</param>
/// <param name="HasNext">True when the node has a next sibling.</param>
/// <param name="Damage">The status of its command map slot: 0 low, 1 medium, 2 high.</param>
/// <param name="Order">The command status of the slot (the record's command status + 1): 2 pending, 3 acknowledged, else none.</param>
/// <param name="Ancestors">The nodes above it, nearest first, with the depth of each and whether it has a next sibling.</param>
/// <param name="Selected">True when the record is selected.</param>
/// <remarks>Follows PlayerManager_buildCommandTree (0040b46c).</remarks>
public sealed record CommandTeamRow(int Id, string Name, int Depth, bool HasChildren, bool Expanded, bool HasNext, int Damage, int Order,
    IReadOnlyList<(int Depth, bool HasNext)> Ancestors, bool Selected = false);

/// <summary>One row of the Objects or Controllables list.</summary>
/// <param name="Slot">The command map slot (the entry's first int).</param>
/// <param name="Name">The name shown: the slot's label, else the datablock description (004a98b8, 004aa6b8).</param>
public sealed record CommandListRow(int Slot, string Name);

/// <summary>A chat menu object on display in a FGChatMenu control.</summary>
/// <param name="Active">False for a menu that is off or has no items.</param>
/// <param name="Path">The headings above the shown level joined with ':', empty at the top level.</param>
/// <param name="Lines">The items as "key: text".</param>
public sealed record ChatMenuState(bool Active, string Path, IReadOnlyList<string> Lines);

/// <summary>The side panel frame of the command screen (FearGui::FGCommandPanel, command.gui, docs/gui.md 3.3 "The command screen panels").</summary>
/// <remarks>Follows 004a159c: no base render. A polygon of five points (the top left, the top right, the bottom right, the bottom edge `corner` pixels from the left and the left edge `corner` pixels above the bottom, so the bottom left corner is cut) is filled with palette 0xf7 and its edges are drawn with 0xf8, then the children draw. The corner is 9 pixels (4 when the byte at +0x1ac is set, which nothing in the exe reads or sets: kept 0).</remarks>
public sealed class GuiCommandPanel : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        const int corner = 9;
        int x0 = gx, y0 = gy, x1 = gx + W - 1, y1 = gy + H - 1;
        for (var y = y0; y <= y1; ++y)
        {
            var left = y <= y1 - corner ? x0 : x0 + (y - (y1 - corner));
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

/// <summary>The chat menu display of the command screen (FearGui::FGChatMenu).</summary>
/// <remarks>Follows 004a7934: it shows the chat menu object named in the file (`CommandChatMenu`) in orange (sf_Orange255_10, white for the item under the mouse, sf_white_10, 004a774c) from (3, 1): "Menu inactive" while the menu is off, else its heading (the path of headings, "Root Menu:" at the top level) and one "key: text" line per item. An object that is not a chat menu draws nothing.</remarks>
public sealed class GuiChatMenu : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Host?.Hud?.NamedMenu(S("menuName")) is not ChatMenuState menu)
            return;
        var orange = Host.FontByName("sf_Orange255_10.pft");
        var white = Host.FontByName("sf_white_10.pft");
        int x = gx + 3, y = gy + 1;
        if (!menu.Active)
        {
            p.Text(orange, x, y, "Menu inactive");
            return;
        }
        p.Text(orange, x, y, menu.Path.Length == 0 ? "Root Menu:" : menu.Path);
        y += GuiPaint.FontHeight(orange);
        int mx = (int)(Canvas?.CursorX ?? -1) - gx, my = (int)(Canvas?.CursorY ?? -1) - gy;
        foreach (var line in menu.Lines)
        {
            // The line is white while the mouse is inside the control's width and the line's rows [code 004a7934].
            var top = y - gy;
            var hover = mx >= 0 && mx < W && my >= top && my < top + GuiPaint.FontHeight(orange);
            p.Text(hover ? white : orange, x, y, line);
            y += GuiPaint.FontHeight(orange);
        }
    }
}

/// <summary>The command tree list of the local player's team (FearGui::CommandTeamCtrl): one row per player, rows of the font height + 2.</summary>
/// <remarks>
/// <para>Follows cell draw 004a8638, prepare 004a85c0 and open 004a8064.</para>
/// <para>A row (x from the cell's left): the damage picture (CMD_DamageLow, Med, High) at x + 2, one pixel down, then the order picture (CMD_OrderPending or CMD_OrderAck) after it, then at depth * (width of piece 11 of CmdListCtrl.pba) from there the tree piece (3 or 7 for a node with children, expanded or not, plus 1 when it has a next sibling and the row is the first, else plus 2 or 3 when it is not the first row), the vertical piece 11 at the depth of every ancestor that has a next sibling, and the name in if_w_10b (if_w_6 below 512 x 384) at one more depth, 2 pixels above the row top.</para>
/// </remarks>
public sealed class GuiCommandTeam : GuiList
{
    private int _lastRow = -1;
    private const int CellWidth = 640;
    /// <summary>Gets the rows, rebuilt before each render.</summary>
    public IReadOnlyList<CommandTeamRow> Rows { get; private set; } = [];
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

    /// <inheritdoc/>
    /// <remarks>Follows 004a8ad8: the waypoint arrow while the map waits for the order's target.</remarks>
    public override uint CursorTagAt(int lx, int ly) => Map is { OrderMode: 1 } ? TagWaypointArrow : base.CursorTagAt(lx, ly);

    /// <summary>Returns the first selected row that is not the local client and does not already have the local client above it in the chain (004a8a6c).</summary>
    /// <returns>The client id, -1 for none.</returns>
    public int FirstReportable()
    {
        if (Host?.Hud?.CommandLocal is not CommandLocalClient me)
            return -1;
        foreach (var r in Rows)
            if ((Host.Hud.CommandLinks(r.Id)?.Selected ?? false) && r.Id != me.Id && !Host.Hud.CommandInChain(r.Id, me.Id))
                return r.Id;
        return -1;
    }

    /// <summary>Selects a record (the first of a level), then, for it and each later sibling, the first record under it and so on down (004a850c).</summary>
    private void SelectSubtree(GuiCommandMap map, int id, bool on)
    {
        if (id < 0)
            return;
        map.SelectClient(id, on);
        for (var p = id; p >= 0; p = Host?.Hud?.CommandLinks(p)?.NextSibling ?? -1)
            SelectSubtree(map, Host?.Hud?.CommandLinks(p)?.FirstChild ?? -1, on);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004a8b00. On the tree glyph of a node with children it flips the node open or closed. On the name: with the order cursor on, it gives the order to that player. Else a plain click makes it the only selected player, ctrl flips it, shift selects the rows from the last plain click, and a double click does the same for the node and the records under it (004a850c). Any list selection first drops the turret and static selection.</remarks>
    public override bool MsDown(GuiEvent e)
    {
        var map = Map;
        if (!Active || map == null)
            return base.MsDown(e);
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        var dbl = e.ClickCount > 1;
        int col = lx < 0 ? -1 : lx / CellWidth, row = ly < 0 ? -1 : ly / RowHeight;
        if (col < 0 || col >= 1 || row < 0 || row >= Rows.Count)
            return true;
        var rec = Rows[row];
        var pieces = Host?.ArrayByName(Low ? "LRCmdListCtrl.pba" : "CmdListCtrl.pba");
        var step = (pieces != null && pieces.Bitmaps.Count > 11 ? pieces.Bitmaps[11].Width : 0) + 2;
        var glyph = (Host?.BitmapByName("CMD_DamageHigh.bmp")?.Width ?? 0) + (Host?.BitmapByName("CMD_OrderAck.bmp")?.Width ?? 0) + (step * rec.Depth) + 4;
        if (lx >= glyph && lx <= glyph + step && rec.HasChildren)
        {
            Host?.Hud?.CommandToggleExpanded(rec.Id);
            return true;
        }
        if (((rec.Depth + 1) * step) + 2 > lx)
            return true;
        if (map.OrderMode == 1)
        {
            Host?.SetVar("$CommandTargetName", rec.Name);
            Host?.SetVar("$CommandTarget", "friendly player");
            var slot = map.SlotOfClient(rec.Id);
            if (slot >= 0)
            {
                map.IssueTargetOrder(slot);
                map.SetOrderMode(0);
            }
            return true;
        }
        if (row != _lastRow)
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
            for (var i = Math.Min(Selected, row); i <= Math.Max(Selected, row) && i < Rows.Count; ++i)
                map.SelectClient(Rows[i].Id, true);
        }
        _lastRow = row;
        return true;
    }

    private bool Low => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    /// <inheritdoc/>
    protected override PftFont? RowFont => Host?.FontByName("if_w_10b.pft") ?? base.RowFont;

    /// <inheritdoc/>
    public override void PreRender()
    {
        Rows = Host?.Hud?.CommandTeam ?? [];
        Entries.Clear();
        foreach (var r in Rows)
            Entries.Add((r.Name, r.Id));
        base.PreRender();
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var pieces = Host?.ArrayByName(Low ? "LRCmdListCtrl.pba" : "CmdListCtrl.pba");
        if (Host == null || pieces == null || pieces.Bitmaps.Count < 12)
            return;
        PbmpBitmap? low = Host.BitmapByName("CMD_DamageLow.bmp"), med = Host.BitmapByName("CMD_DamageMed.bmp"), high = Host.BitmapByName("CMD_DamageHigh.bmp");
        PbmpBitmap? pending = Host.BitmapByName("CMD_OrderPending.bmp"), ack = Host.BitmapByName("CMD_OrderAck.bmp");
        var ackPic = ack;
        var font = Host.FontByName(Low ? "if_w_6.pft" : "if_w_10b.pft");
        int step = pieces.Bitmaps[11].Width, rowH = RowHeight;
        for (var i = 0; i < Rows.Count; ++i)
        {
            var r = Rows[i];
            var py = gy + (i * rowH);
            // A selected node first gets a 0xfc fill from the picture area to the cell's right edge [code 004a8638].
            if (r.Selected)
                p.Fill(gx + (high?.Width ?? 0) + (ackPic?.Width ?? 0) + 3, py, CellWidth - ((high?.Width ?? 0) + (ackPic?.Width ?? 0) + 3), rowH, 0xfc);
            var dmg = r.Damage == 2 ? high : r.Damage == 1 ? med : low;
            if (dmg == null)
                continue;
            var x = gx + 2;
            p.Bitmap(dmg, x, py + 1);
            x += dmg.Width + 1;
            var order = r.Order == 2 ? pending : r.Order == 3 ? ack : null;
            if (order != null)
                p.Bitmap(order, x, py + 1);
            x += (pending?.Width ?? 0) + 2;
            var piece = !r.HasChildren ? -1 : r.Expanded ? 3 : 7;
            if (i == 0)
            {
                if (r.HasNext)
                    ++piece;
            }
            else
                piece += r.HasNext ? 3 : 2;
            if (piece >= 0)
                p.Bitmap(pieces.Bitmaps[piece], (r.Depth * step) + x, py);
            foreach (var (depth, hasNext) in r.Ancestors)
                if (hasNext)
                    p.Bitmap(pieces.Bitmaps[11], (depth * step) + x, py);
            p.Text(font, ((r.Depth + 1) * step) + x, py - 2, r.Name);
        }
    }
}

/// <summary>The observer panel of the command screen (FearGui::CMDObserve): the TV button of the Players tab shows it.</summary>
/// <remarks>
/// <para>It tells the server which player to follow (remoteEval SCOM: the server makes the client's control object an observer camera orbiting that player) and shows what that camera sees in its child TSControl 140208. The child's camera object is the client's control object (005473ec with DAT_006a83b8), the same one the play view uses, so the view is the world from the observer camera, drawn into the child's rectangle by the TSControl draw 00546dc4.</para>
/// <para>Follows ctor 004b4718, wake 004b47ac, observe 004b4818, toggle 004b48bc, draw 004b4de4, grow 004b4ad8, bars 004b4d60 and noise 004b4c9c and 004b4bdc.</para>
/// </remarks>
public sealed class GuiCmdObserve : GuiControl
{
    /// <summary>The id of the child view control.</summary>
    public const uint ViewId = 140208;
    /// <summary>The client id of the followed player, 0 for none.</summary>
    public int Observed { get; set; }

    // The child view and its rectangle as the file has it (+0x620, +0x610 to +0x61c), read at wake (004b47ac).
    private GuiControl? _view;
    private int _extX, _extY, _extW, _extH;
    // The rectangle the view has now (+0x600 to +0x60c): it opens from a thin line at the middle (004b48bc, 004b4ad8).
    private int _curX, _curY, _curW, _curH;
    private bool _barsDone;     // +0x5fc
    private double _noiseInterval; // +0x5ec seconds
    private int _damageBar, _energyBar; // +0x5f4, +0x5f0: the drawn bar heights
    private long _noiseTimerMs;
    private long? _barTimerMs;      // the constructor starts it; the first step is long overdue
    // R250 seeded 0x8377 (004b4718).
    private readonly Script.RandomR250 _random = new(0x8377);

    // 004b4c9c: the noise grows by this each interval until it reaches the limit. 004b48bc: values 4, 0x50, 6.
    private const int NoiseStart = 4, NoiseLimit = 0x50, NoiseDecay = 6;

    /// <summary>Gets a value that is true when a player was chosen.</summary>
    public bool IsActive { get; private set; }
    /// <summary>Gets a value that is true when the view has its full rectangle.</summary>
    public bool IsOpen { get; private set; }
    /// <summary>Gets a value that is true when the noise has settled.</summary>
    public bool NoiseDone { get; private set; }
    /// <summary>Gets the rectangle the view has now.</summary>
    public (int X, int Y, int W, int H) ViewRect => (_curX, _curY, _curW, _curH);
    /// <summary>Gets the heights of the damage and energy bars as drawn.</summary>
    public (int Damage, int Energy) BarHeights => (_damageBar, _energyBar);
    /// <summary>Gets the noise value: 1 in this many pixels (the middle value) is drawn white while the picture settles.</summary>
    public int NoiseValue { get; private set; } = 4;

    private void RemoteObserve(int id) =>
        Host?.Call("remoteEval", "2048", "SCOM", id.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    /// <remarks>Follows 004b47ac (slot 0xb0): it finds the child view (the control with the id, anywhere below), keeps its rectangle, hides the panel and forgets the followed player.</remarks>
    public override void Wake()
    {
        _view = FindById(ViewId);
        if (_view != null)
            (_extX, _extY, _extW, _extH) = (_view.X, _view.Y, _view.W, _view.H);
        SetVisible(false);
        Observed = 0;
    }

    /// <summary>Follows another player.</summary>
    /// <param name="id">The client id.</param>
    /// <returns>True when the player was followed: a player other than the followed one, and only while the panel is shown, is sent to the server as remoteEval(2048, SCOM, id). The noise and the bars start again, the view keeps its size.</returns>
    /// <remarks>Follows 004b4818 (Client::cmdObservePlayer, 0043a6e8).</remarks>
    public bool Observe(int id)
    {
        if (id == Observed || !Visible)
            return false;
        RemoteObserve(id);
        NoiseValue = NoiseStart;
        NoiseDone = false;
        _damageBar = _energyBar = 0;
        _barsDone = false;
        IsActive = true;
        Observed = id;
        return true;
    }

    /// <summary>Shows or hides the panel.</summary>
    /// <param name="on">True to show.</param>
    /// <remarks>Follows 004b48bc (Client::toggleCMDObserver, 0043a670): a change of the shown state only. On: the first selected record of the local player's records is followed (none: the panel stays off). With the 3D view present the server is told (SCOM, id), the followed id is set, the view starts as a line at the middle of its rectangle, the noise and the bars restart and the view gets the control object as camera. Then the panel is shown. Off: the server is told SCOM, -1 and the panel is hidden (the followed id stays).</remarks>
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
        var id = Host?.Hud?.CommandFirstSelectedClient() ?? -1;
        if (id == -1)
            return;
        if (_view != null)
        {
            RemoteObserve(id);
            IsActive = true;
            Observed = id;
            _curX = (_extW >> 1) + _extX;
            _curY = (_extH >> 1) + _extY;
            _curW = 0;
            _curH = 2;
            NoiseValue = NoiseStart;
            _noiseInterval = NextNoiseInterval();
            _damageBar = _energyBar = 0;
            IsOpen = NoiseDone = _barsDone = false;
            if (_view is GuiPlayView tv)
                tv.CameraAttached = true;
            _view.Resize(_curX, _curY, _curW, _curH);
        }
        SetVisible(true);
    }

    /// <summary>Returns the seconds until the next noise step: random * 2^-32 * 0.01 + 0.025 (floats) (004b4c9c, 004b48bc).</summary>
    private double NextNoiseInterval() => (float)((_random.Next() * 2.3283064365386963e-10 * 0.009999999776482582) + 0.02500000037252903);

    private long Now => Host?.TickMs ?? 0;

    /// <summary>Runs one step of the opening per draw (004b4ad8).</summary>
    /// <remarks>First the width (the rectangle moves 8 left and widens by 16 until it is the full width), then the height (10 up, 20 taller). Then it is open and the noise timer starts. The view gets the rectangle every step.</remarks>
    private void GrowStep()
    {
        if (_extX < _curX || _curW < _extW)
        {
            _curX = Math.Max(_curX - 8, _extX);
            _curW = Math.Min(_extW, _curW + 16);
        }
        else if (_extY < _curY || _curH < _extH)
        {
            _curY = Math.Max(_curY - 10, _extY);
            _curH = Math.Min(_extH, _curH + 20);
        }
        else
        {
            IsOpen = true;
            _noiseTimerMs = Now;
        }
        _view?.Resize(_curX, _curY, _curW, _curH);
    }

    /// <summary>Grows both bars by 4 pixels towards their targets every 0.03 s, done when both have got there (004b4d60).</summary>
    private void BarStep(int damageTarget, int energyTarget)
    {
        if (_barTimerMs is long t && Now - t <= 30)
            return;
        _barTimerMs = Now;
        _energyBar += 4;
        _damageBar += 4;
        if (damageTarget < _damageBar && energyTarget < _energyBar)
            _barsDone = true;
        if (damageTarget < _damageBar)
            _damageBar = damageTarget;
        if (energyTarget < _energyBar)
            _energyBar = energyTarget;
    }

    /// <summary>Thins the noise by a step every interval once open, and stops it at the limit (004b4c9c).</summary>
    private void NoiseStep()
    {
        if (!IsActive || !IsOpen || Now - _noiseTimerMs <= _noiseInterval * 1000.0)
            return;
        _noiseTimerMs = Now;
        _noiseInterval = NextNoiseInterval();
        NoiseValue += NoiseDecay;
        if (NoiseLimit <= NoiseValue)
            NoiseDone = true;
    }

    /// <summary>Returns the bar targets: the view height times (1 - damage level of the control object, slot 0xf8) and times the energy fraction (1 - slot 0xfc). A bar with a value above 0 is at least 1 pixel (004b4de4).</summary>
    /// <returns>The damage and energy bar heights.</returns>
    public (int Damage, int Energy) BarTargets()
    {
        var health = 1f - (float)(Host?.Hud?.DamageLevel ?? 0);
        var energy = 1f - (1f - (float)(Host?.Hud?.EnergyLevel ?? 1));
        int Bar(float v)
        {
            var n = (int)((double)_extH * v);
            return v > 0f ? Math.Max(n, 1) : n;
        }
        return (Bar(health), Bar(energy));
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004b4de4 (slot 0x78): the panel fill (palette 0xf4). With the view: the opening, the two bars right of the view (palette 0xfc, 0xf2 or 0xf3 by the damage bar's target height, 0xe7 for the energy bar), the child view, and the noise until it has settled.</remarks>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, 0xf4);
        if (_view == null)
            return;
        if (!IsOpen)
            GrowStep();
        if (IsActive)
        {
            if (IsOpen)
            {
                (var hd, var he) = BarTargets();
                if (!_barsDone)
                    BarStep(hd, he);
                else
                    (_damageBar, _energyBar) = (hd, he);
                var right = gx + _extX + _extW;
                var bottom = gy + _extY + _extH;
                if (_damageBar != 0)
                    p.Fill(right + 4, bottom - _damageBar, 2, _damageBar, hd >= 0x39 ? 0xfc : hd >= 0x1c ? 0xf2 : 0xf3);
                if (_energyBar != 0)
                    p.Fill(right + 8, bottom - _energyBar, 2, _energyBar, 0xe7);
            }
            RenderChildren(p, gx, gy);
        }
        if (!NoiseDone)
        {
            NoiseStep();
            DrawNoise(p, gx, gy);
        }
    }

    /// <summary>Draws white noise over the view rectangle: a pixel is white (palette 0xff) when a random number scaled to the noise value is its middle (004b4bdc).</summary>
    private void DrawNoise(GuiPaint p, int gx, int gy)
    {
        for (var c = 0; c < _curW; ++c)
            for (var r = 0; r < _curH; ++r)
            {
                var v = (uint)((ulong)_random.Next() * (uint)NoiseValue / 0xffffffffUL);
                if (v == (uint)(NoiseValue >> 1))
                    p.Fill(gx + _curX + c, gy + _curY + r, 1, 1, 0xff);
            }
    }
}
