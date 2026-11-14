namespace OpenTribes.Gui;

using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

/// <summary>The base picture the map control keeps: the terrain seen from above (`rebuildCommandMap`).</summary>
/// <param name="X">The mission area lower left x (MissionCenterPos, +0x298).</param>
/// <param name="Y">The mission area lower left y.</param>
/// <param name="W">The mission area width.</param>
/// <param name="H">The mission area height (+0x2a4).</param>
/// <param name="Tiles">The 4 x 4 bitmaps of 256 x 256 pixels in row major order (+0x2f0). The 1024 x 1024 picture shows the area with the north edge on top.</param>
public sealed record CommandMapPicture(float X, float Y, float W, float H, PbmpBitmap[] Tiles);

/// <summary>One slot of the command map as the draw reads it.</summary>
/// <param name="Slot">The slot index in the slot table (DAT_006a83d8 + 0x340).</param>
/// <param name="X">The x position in 1/1024 of the area.</param>
/// <param name="Y">The y position in 1/1024 of the area.</param>
/// <param name="Team">The team.</param>
/// <param name="Group">The datablock group: 3 player, 4 static shape, 8 sensor, 10 vehicle, 19 turret.</param>
/// <param name="ClientId">The client id of a client slot.</param>
/// <param name="Icon">The map icon of the datablock (mapIcon, +0x1c).</param>
/// <param name="Description">The description of the datablock (+0x20).</param>
/// <param name="TargetName">The label of a target slot (TargetNameEvent), or null.</param>
/// <param name="Filter">The map filter of the datablock (mapFilter, +0x18).</param>
/// <param name="Range">The sensor range (+0x104).</param>
/// <param name="RangeFlag">The flag at +0x10c.</param>
/// <param name="Active">The active flag.</param>
/// <param name="ClientName">The name of the client record.</param>
/// <param name="OrderIcon">The current command of the record.</param>
/// <param name="OrderTarget">The target of the current command.</param>
/// <param name="OrderStatus">The status of the current command.</param>
/// <param name="OrderX">The waypoint x of the current command.</param>
/// <param name="OrderY">The waypoint y of the current command.</param>
public sealed record CommandMapSlotMark(int Slot, int X, int Y, int Team, int Group, int ClientId, string Icon, string Description, string? TargetName,
    int Filter, int Range, bool RangeFlag, bool Active, string ClientName, int OrderIcon, int OrderTarget, int OrderStatus, int OrderX, int OrderY);

/// <summary>What the map draws over the picture.</summary>
/// <param name="LocalId">The record id of the local client.</param>
/// <param name="LocalTeam">The team of the local client.</param>
/// <param name="Heading">The control object's yaw as 0 to 256.</param>
/// <param name="Slots">The slots.</param>
public sealed record CommandMapMarks(int LocalId, int LocalTeam, int Heading, IReadOnlyList<CommandMapSlotMark> Slots);

/// <summary>The commander map (FearGui::TSCommander, command.gui, docs/gui.md 3.3 "The command map").</summary>
/// <remarks>
/// <para>Follows ctor 004ceb18, dtor 004ced10, rebuild 004cef14, prepare 004cf784 and draw 004d04e8. Constructed it is the 408 x 408 control of the file. The rebuild (`onClientGhostAlwaysDone`) renders the picture and then fits the control into its parent: the control is scaled by min(parent width, parent height) / max(width, height) (both sides truncated) and centred in the parent on any axis where the parent is larger (004cf3ed to 004cf4f1).</para>
/// <para>Draw: the clip becomes the parent's rectangle, then the 16 tiles are stretched over the control, each a quarter of its width and height (rounded up when four of them fall short), row by row from the control's top left corner.</para>
/// <para>The zoom (004d3424 in, 004d3710 out), the pan with the right button (004d2d78, 004d2e5c, 004d2fe0), the zoom animation (004cf784), the selection and the order cursor (left button 004d1bf0, 004d2754, 004d2b1c, 004d2a98, 004d291c, 004d2404, 004d2614, 004d23bc) and the centring on the selected slots (004d3018, 004d3220, 004d32c4) are ported.</para>
/// </remarks>
public sealed class GuiCommandMap : GuiControl
{
    private CommandMapPicture? _fitted;

    /// <summary>True in zoom mode (+0x28c): a left click zooms in, a right click out.</summary>
    public bool ZoomMode { get; set; }
    /// <summary>The number of zoom steps (+0x290), 0 to 4.</summary>
    public int ZoomSteps { get; set; }
    private bool _panning, _buttonTaken;
    private long _animStart;
    private (int X, int Y, int W, int H) _from, _to;
    private int _panMouseX, _panMouseY, _panX, _panY;

    /// <summary>Gets a value that is true while the zoom animation runs.</summary>
    public bool Animating { get; private set; }
    /// <summary>The maximum number of zoom steps.</summary>
    public const int MaxZoomSteps = 4;

    /// <summary>The id of the map control in command.gui.</summary>
    public const uint MapId = 140201;
    /// <summary>The id of the current command line (0x223b2) in command.gui. The zoom (0x223b3) and centre (0x223b8) buttons are 140211 and 140216 in the file.</summary>
    public const uint CurrentCommandId = 140210;
    /// <summary>The id of the centre button in command.gui.</summary>
    public const uint CenterButtonId = 140216;
    /// <summary>The name of the chat menu object the map drives.</summary>
    public const string CommandMenuName = "CommandChatMenu";
    // The default cursors [code 004d23bc]: the arrow (0x27326) and the waypoint wait cursor (0x2732f) while the order cursor is on; the map's own
    // cursors [code 004d1b54]: 0x2732d zoom, 0x27328 open hand, 0x27329 grab, 0x2732c waypoint.
    private const uint TagArrow = 160550, TagWayWait = 160559, TagZoom = 160557, TagOpenHand = 160552, TagGrab = 160553, TagWaypoint = 160556;

    /// <summary>Gets the order mode (+0x280): 0 select, 1 the next click gives the order.</summary>
    public int OrderMode { get; private set; }
    /// <summary>The kind the map selects (+0x28f): 3 players, 4 statics and sensors, 0x13 turrets. The three list tabs of the screen set it.</summary>
    public int Group { get; set; } = 3;
    /// <summary>The selected byte of each of the 256 slots (+0x360).</summary>
    public bool[] Selected { get; } = new bool[256];
    /// <summary>The order the menu chose (+0x284).</summary>
    public int OrderAction { get; set; }
    /// <summary>The text of the order the menu chose (+0x288).</summary>
    public string? OrderText { get; set; }
    private bool _clearOnAdd, _scriptGuard, _dragging;
    private int _dragX0, _dragY0, _dragX1, _dragY1;

    // The centre mode (+0x28d) and its animation (+0x2dc start position, +0x2e4 target, +0x2ec start tick).
    private bool _centerMode;
    private long _centerTick;
    private (int X, int Y) _centerFrom, _centerTo;

    private int ZoomTime => int.TryParse(Host?.GetVar("$pref::CommanderZoomTime"), out var t) ? Math.Clamp(t, 1, 2000) : 250;

    // 004d3424 and 004d3710 both end by taking the control's place for the animation: the clamp keeps the picture over the parent.
    private (int X, int Y, int W, int H) Clamp(int x, int y, int w, int h)
    {
        if (Parent == null)
            return (x, y, w, h);
        int pw = Parent.W, ph = Parent.H;
        x = Math.Min(Math.Max(x, -(pw >> 1) - Math.Abs(pw - w)), pw >> 1);
        y = Math.Min(Math.Max(y, -(ph >> 1) - Math.Abs(ph - h)), ph >> 1);
        return (x, y, w, h);
    }

    // The cursor in the parent's coordinates when the control is under it, else the middle of the parent.
    private (int X, int Y) ZoomCentre(int cursorX, int cursorY, bool over)
    {
        if (Parent == null)
            return (0, 0);
        if (over)
            return Parent.GlobalToLocal(cursorX, cursorY);
        return (Parent.W >> 1, Parent.H >> 1);
    }

    private void StartAnimation((int X, int Y, int W, int H) to)
    {
        Animating = true;
        _animStart = Host?.TickMs ?? 0;
        _from = (X, Y, W, H);
        _to = to;
    }

    /// <summary>Zooms in one step.</summary>
    /// <param name="cursorX">The cursor x in canvas pixels.</param>
    /// <param name="cursorY">The cursor y in canvas pixels.</param>
    /// <param name="overControl">True when the cursor is over the control.</param>
    /// <remarks>Follows 004d3424: the control doubles in size around the cursor point and the change runs over `$pref::CommanderZoomTime`. In centre mode the new place is the one that centres the selected slots (004d3018) and the centre animation restarts from the current place (004d3284).</remarks>
    public void ZoomIn(int cursorX, int cursorY, bool overControl)
    {
        if (ZoomSteps >= MaxZoomSteps || Parent == null)
            return;
        var cur = Animating ? _to : (X, Y, W, H);
        ++ZoomSteps;
        int nw = cur.W << 1, nh = cur.H << 1;
        int px, py;
        if (!_centerMode)
        {
            var (cx, cy) = ZoomCentre(cursorX, cursorY, overControl);
            int dx = cx - cur.X, dy = cy - cur.Y;
            (px, py) = (cx - (dx * 2), cy - (dy * 2));
        }
        else
        {
            (px, py) = CenterPos(nw, nh);
            RestartCenter(px, py);
        }
        StartAnimation(Clamp(px, py, nw, nh));
    }

    /// <summary>Zooms out one step. At no zoom the control is fitted again at once.</summary>
    /// <param name="cursorX">The cursor x in canvas pixels.</param>
    /// <param name="cursorY">The cursor y in canvas pixels.</param>
    /// <param name="overControl">True when the cursor is over the control.</param>
    /// <remarks>Follows 004d3710.</remarks>
    public void ZoomOut(int cursorX, int cursorY, bool overControl)
    {
        if (Parent == null)
            return;
        if (ZoomSteps < 1)
        {
            (X, Y, W, H) = Fit(X, Y, W, H, Parent.W, Parent.H);
            return;
        }
        --ZoomSteps;
        var cur = Animating ? _to : (X, Y, W, H);
        int nw = cur.W >> 1, nh = cur.H >> 1;
        int px, py;
        if (!_centerMode)
        {
            var (cx, cy) = ZoomCentre(cursorX, cursorY, overControl);
            int dx = cx - cur.X, dy = cy - cur.Y;
            (px, py) = (cx - (dx >> 1), cy - (dy >> 1));
        }
        else
        {
            (px, py) = CenterPos(nw, nh);
            RestartCenter(px, py);
        }
        StartAnimation(Clamp(px, py, nw, nh));
    }

    // The keyboard forms (the play delegate actions ZOOM_IN and ZOOM_OUT [code 004dd280 cases 0x35c2f, 0x35c30]): the cursor counts as over the control when it is inside it.
    private bool CursorOver(out int cx, out int cy)
    {
        cx = (int)(Canvas?.CursorX ?? 0);
        cy = (int)(Canvas?.CursorY ?? 0);
        var (lx, ly) = GlobalToLocal(cx, cy);
        return Contains(lx, ly);
    }

    /// <summary>Zooms in at the cursor: the keyboard form (the play delegate action ZOOM_IN, 004dd280 case 0x35c2f).</summary>
    public void ZoomInAtCursor()
    {
        var over = CursorOver(out var cx, out var cy);
        ZoomIn(cx, cy, over);
    }

    /// <summary>Zooms out at the cursor: the keyboard form (the play delegate action ZOOM_OUT, 004dd280 case 0x35c30).</summary>
    public void ZoomOutAtCursor()
    {
        var over = CursorOver(out var cx, out var cy);
        ZoomOut(cx, cy, over);
    }

    // ---- centre mode [code 004d3018, 004d3220, 004d3284, 004d32c4] ----

    /// <summary>Gets or sets the centre mode (+0x28d). Turning it on starts the centring from the current place, turning it off only clears the flag (004d3220).</summary>
    public bool CenterMode
    {
        get => _centerMode;
        set
        {
            if (!value)
            {
                _centerMode = false;
                return;
            }
            _centerMode = true;
            _centerTick = Host?.TickMs ?? 0;
            _centerFrom = (X, Y);
            _centerTo = CenterPos(W, H);
        }
    }

    // 004d3284
    private void RestartCenter(int x, int y)
    {
        _centerTick = Host?.TickMs ?? 0;
        _centerFrom = (X, Y);
        _centerTo = (x, y);
    }

    private int PrefFilter => int.TryParse(Host?.GetVar("$pref::mapFilter"), out var f) ? f : 0;

    // 004d3018: the place that puts the middle of the selected slots of the local team (the ones the map filter shows) in the middle of the parent, for a
    // control of that size, kept over the parent; the current place when nothing counts.
    private (int X, int Y) CenterPos(int w, int h)
    {
        var hud = Host?.Hud;
        var me = hud?.CommandLocal;
        var slots = Slots;
        long sumX = 0, sumY = 0;
        var n = 0;
        var pref = PrefFilter;
        for (var i = 0; i < slots.Length; ++i)
        {
            var s = slots[i];
            if (!Selected[i] || me == null || me.Value.Team != s.Team || !s.Present || hud!.CommandBlockFilter(s.Group, s.Index) is not int filter)
                continue;
            if (filter != -1 && (filter & 0xff & pref) == 0)
                continue;
            sumX += s.X;
            sumY += s.Y;
            ++n;
        }
        if (n == 0 || Parent == null)
            return (X, Y);
        var px = (Parent.W >> 1) - ((w * (int)(sumX / n)) >> 10);
        var py = (Parent.H >> 1) - (h - ((h * (int)(sumY / n)) >> 10));
        var (cx, cy, _, _) = Clamp(px, py, w, h);
        return (cx, cy);
    }

    // 004d32c4: the centring step of every frame while the centre mode is on.
    private void CenterStep()
    {
        var target = CenterPos(W, H);
        if (target != _centerTo)
            RestartCenter(target.X, target.Y);
        var now = Host?.TickMs ?? 0;
        var time = ZoomTime;
        if (now < _centerTick + time)
        {
            var f = (float)(now - _centerTick) / time;
            X = (int)(f * (_centerTo.X - _centerFrom.X)) + _centerFrom.X;
            Y = (int)(f * (_centerTo.Y - _centerFrom.Y)) + _centerFrom.Y;
        }
        else
            (X, Y) = _centerTo;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004cf784: the animation step of every frame. The pan and the drag end when the canvas no longer holds the mouse for the control.</remarks>
    public override void PreRender()
    {
        if (Canvas?.Lock != this)
            _panning = _dragging = false;
        if (Animating)
        {
            var now = Host?.TickMs ?? 0;
            var time = ZoomTime;
            if (now < _animStart + time)
            {
                var f = (float)(now - _animStart) / time;
                (X, Y, W, H) = ((int)(f * (_to.X - _from.X)) + _from.X, (int)(f * (_to.Y - _from.Y)) + _from.Y, (int)(f * (_to.W - _from.W)) + _from.W,
                    (int)(f * (_to.H - _from.H)) + _from.H);
            }
            else
            {
                (X, Y, W, H) = _to;
                Animating = false;
            }
        }
        if (_centerMode)
            CenterStep();
    }

    // ---- the slots ----

    private ClientCommandSlot[] Slots => Host?.Hud?.CommandSlotTable ?? [];

    // The place of a slot in the control: x = slot x * width >> 10, y = height - (slot y * height >> 10) [code 004cf94c].
    private int SlotX(in ClientCommandSlot s) => (s.X * W) >> 10;
    private int SlotY(in ClientCommandSlot s) => H - ((s.Y * H) >> 10);

    // The slots of the same kind as the shown one: the statics list also selects sensors (group 8) [code 004d1bf0, 004d2b1c].
    private bool InGroup(int slotGroup) => slotGroup == Group || (Group == 4 && slotGroup == 8);

    private GuiControl? FindInView(uint id)
    {
        GuiControl top = this;
        while (top.Parent != null)
            top = top.Parent;
        return top.FindById(id) ?? Canvas?.Content?.FindById(id);
    }

    // The two buttons the map unlights when it takes the mode over [code 004d1bf0, 004d2d78].
    private void UnlightCenterButton()
    {
        if (FindInView(CenterButtonId) is GuiUniversalButton b)
            b.On = false;
    }

    /// <summary>Sets the order mode and the default cursor that goes with it (004d23bc).</summary>
    /// <param name="mode">0 select, 1 the next click gives the order.</param>
    public void SetOrderMode(int mode)
    {
        OrderMode = mode;
        if (Canvas == null || Host == null)
            return;
        if (mode == 0 && Host.TagText(TagArrow) is string arrow)
            Canvas.SetCursorBitmap(arrow);
        else if (mode == 1 && Host.TagText(TagWayWait) is string wait)
            Canvas.SetCursorBitmap(wait);
    }

    /// <summary>Selects or deselects a slot (004d2a98).</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="on">The new state.</param>
    /// <remarks>Deselecting switches the command menu off, selecting a player switches it on and marks the player's record.</remarks>
    public void SelectSlot(int slot, bool on)
    {
        var host = Host;
        var menu = host?.ChatMenuExists?.Invoke(CommandMenuName) == true;
        if (menu && !on)
            host!.SetChatMenuMode?.Invoke(CommandMenuName, 0);
        Selected[slot] = on;
        if (Slots[slot].Group == 3)
        {
            if (on && menu)
                host!.SetChatMenuMode?.Invoke(CommandMenuName, 1);
            host?.Hud?.CommandSetSelected(0x800 + slot, on);
        }
    }

    /// <summary>Selects the player with a client id (004d291c), only while the map shows players.</summary>
    /// <param name="clientId">The client id. A negative id deselects every player.</param>
    /// <param name="on">The new state.</param>
    public void SelectClient(int clientId, bool on)
    {
        var slots = Slots;
        if (clientId < 0)
        {
            for (var i = 0; i < slots.Length; ++i)
                if (slots[i].Group == 3)
                    SelectSlot(i, false);
            return;
        }
        if (Group != 3)
            return;
        var found = -1;
        for (var i = 0; i < slots.Length; ++i)
            if (slots[i].Group == 3 && 0x800 + i == clientId)
            {
                found = i;
                break;
            }
        if (found < 0)
            return;
        // Selecting a player also points the observer view at it [code 004d291c: Client::cmdObservePlayer(id)].
        if (on)
            Host?.Call("Client::cmdObservePlayer", clientId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectSlot(found, on);
    }

    /// <summary>Deselects the turrets, then selects the one at a slot if the map shows that kind (004d27d0).</summary>
    /// <param name="slot">The slot, -1 for none.</param>
    public void SelectTurret(int slot)
    {
        var slots = Slots;
        for (var i = 0; i < slots.Length; ++i)
            if (slots[i].Group == 0x13)
                SelectSlot(i, false);
        if (slot >= 0 && Group == 0x13 && slot < slots.Length && slots[slot].Group == 0x13)
            SelectSlot(slot, true);
    }

    /// <summary>Deselects the statics (and sensors), then selects the one at a slot if the map shows that kind (004d28a8).</summary>
    /// <param name="slot">The slot, -1 for none.</param>
    public void SelectStatic(int slot)
    {
        var slots = Slots;
        for (var i = 0; i < slots.Length; ++i)
            if (slots[i].Group is 4 or 8)
                SelectSlot(i, false);
        if (slot >= 0 && Group == 4 && slot < slots.Length && slots[slot].Group is 4 or 8)
            SelectSlot(slot, true);
    }

    /// <summary>Returns the first selected turret as the target index (004d2798).</summary>
    /// <returns>The slot - 128, -1 for none.</returns>
    public int SelectedTurretTarget() => Group != 0x13 ? -1 : FirstSelected(s => s.Group == 0x13) is int i and >= 0 ? i - 0x80 : -1;
    /// <summary>Returns the first selected static or sensor as the target index (004d2830).</summary>
    /// <returns>The slot - 128, -1 for none.</returns>
    public int SelectedStaticTarget() => Group != 4 ? -1 : FirstSelected(s => s.Group is 4 or 8) is int i and >= 0 ? i - 0x80 : -1;
    /// <summary>Returns the first selected player slot (004d2870).</summary>
    /// <returns>The slot, -1 for none.</returns>
    public int SelectedPlayerSlot() => Group != 3 ? -1 : FirstSelected(s => s.Group == 3);

    private int FirstSelected(Func<ClientCommandSlot, bool> kind)
    {
        var slots = Slots;
        for (var i = 0; i < slots.Length; ++i)
            if (kind(slots[i]) && Selected[i])
                return i;
        return -1;
    }

    /// <summary>Returns the slot of the player with a client id (004d2a44).</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>The slot, -1 for none.</returns>
    public int SlotOfClient(int clientId)
    {
        var slots = Slots;
        for (var i = 0; i < slots.Length; ++i)
            if (slots[i].Group == 3 && 0x800 + i == clientId)
                return i;
        return -1;
    }

    // ---- the order [code 004d2614, 004d2404, 004d25ec] ----

    /// <summary>Handles the order the menu chose (the console function setIssueCommand, 00421e14; 004d2614).</summary>
    /// <param name="action">The order action, -1 to end the order cursor.</param>
    /// <param name="text">The order text.</param>
    /// <remarks>While an order runs its script this only stores it. Else the current command line shows the text up to its voice tag. An action of -1 or no selected player of the team ends the order cursor, anything else starts it and keeps the order for the next click.</remarks>
    public void SetIssueCommand(int action, string? text)
    {
        if (_scriptGuard)
        {
            OrderAction = action;
            OrderText = text;
            return;
        }
        var hud = Host?.Hud;
        if (hud?.CommandLocal is not CommandLocalClient me)
            return;
        var shown = text ?? "";
        if (shown.Length > 255)
            shown = shown[..255];
        var tilde = shown.IndexOf('~');
        if (tilde >= 0)
            shown = shown[..tilde];
        FindInView(CurrentCommandId)?.SetText(shown);
        if (action == -1)
        {
            SetOrderMode(0);
            return;
        }
        if (CountSelectedPlayers(me) == 0)
        {
            SetOrderMode(0);
            return;
        }
        SetOrderMode(1);
        OrderAction = action;
        OrderText = text;
    }

    private int CountSelectedPlayers(CommandLocalClient me)
    {
        var slots = Slots;
        var n = 0;
        for (var i = 0; i < slots.Length; ++i)
            if (Selected[i] && slots[i].Group == 3 && me.Team == slots[i].Team)
                ++n;
        return n;
    }

    /// <summary>Sends the order to the selected players of the team (004d2404).</summary>
    /// <param name="wayX">The waypoint x in 1/1024 of the mission area.</param>
    /// <param name="wayY">The waypoint y in 1/1024 of the mission area.</param>
    /// <param name="targetSlot">The target slot, or 0 for a waypoint order.</param>
    /// <remarks>A text that starts with `*` is first resolved by `Commander::StarCommand` (which calls setIssueCommand back and so replaces the order). Afterwards the chosen order is put back. With no target the order is the waypoint, else it names the slot: remoteEval(2048, IssueCommand, action, text, x, y, clients...) or remoteEval(2048, IssueTargCommand, action, text, slot, clients...).</remarks>
    private void IssueOrder(int wayX, int wayY, int targetSlot)
    {
        if (Host?.Hud?.CommandLocal is not CommandLocalClient me)
            return;
        var savedAction = OrderAction;
        var savedText = OrderText;
        if (!string.IsNullOrEmpty(OrderText) && OrderText[0] == '*')
        {
            _scriptGuard = true;
            Host.Call("Commander::StarCommand", OrderText);
            _scriptGuard = false;
        }
        var args = new List<string> { "2048", targetSlot == -1 ? "IssueCommand" : "IssueTargCommand", Inv(OrderAction), OrderText ?? "" };
        if (targetSlot == -1)
        {
            args.Add(Inv(wayX));
            args.Add(Inv(wayY));
        }
        else
            args.Add(Inv(targetSlot));
        var slots = Slots;
        for (var i = 0; i < slots.Length; ++i)
            if (Selected[i] && slots[i].Group == 3 && me.Team == slots[i].Team)
                args.Add(Inv(0x800 + i));
        Host.Call("remoteEval", [.. args]);
        OrderText = savedText;
        OrderAction = savedAction;
    }

    private static string Inv(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Gives the order to the object at a slot (004d25ec).</summary>
    /// <param name="slot">The slot.</param>
    public void IssueTargetOrder(int slot) => IssueOrder(0, 0, slot);

    // ---- the mouse ----

    /// <inheritdoc/>
    /// <remarks>Follows 004d1b54: the zoom cursor in zoom mode, the hand while the picture is dragged, the waypoint cursor while the order waits.</remarks>
    public override uint CursorTagAt(int lx, int ly)
    {
        if (ZoomMode)
            return TagZoom;
        if (_panning)
            return X == _panX && Y == _panY ? TagOpenHand : TagGrab;
        return OrderMode == 1 ? TagWaypoint : 0;
    }

    /// <inheritdoc/>
    /// <remarks>Follows the left button 004d1bf0. Centre mode ends. In zoom mode a click zooms in. Else, unless the picture is animating, the click picks slots (order mode 0) or gives the order (order mode 1), and the mouse stays with the control until the button comes up.</remarks>
    public override bool MsDown(GuiEvent e)
    {
        if (_panning)
            return true;
        if (_centerMode)
        {
            _centerMode = false;
            UnlightCenterButton();
        }
        if (ZoomMode)
        {
            ZoomIn(e.X, e.Y, true);
            return true;
        }
        if (Animating)
            return true;
        var (lx, ly) = GlobalToLocal(e.X, e.Y);
        if (Host?.Hud?.CommandLocal is not CommandLocalClient me)
            return true;
        if (OrderMode == 0)
        {
            PickSlots(e, lx, ly, me);
            _dragX0 = _dragX1 = lx;
            _dragY0 = _dragY1 = ly;
            _dragging = true;
            FindInView(CurrentCommandId)?.SetText("");
        }
        else
            ClickOrder(lx, ly, me);
        Canvas?.LockMouse(this);
        return true;
    }

    /// <summary>Picks slots at a click in order mode 0 (004d1bf0).</summary>
    /// <param name="e">The mouse event.</param>
    /// <param name="lx">The local x.</param>
    /// <param name="ly">The local y.</param>
    /// <param name="me">The local client.</param>
    /// <remarks>It takes the first slot of the shown kind and of the team within 3 pixels of the click that the map filter shows. Without shift it becomes the only selected slot of its kind. With shift it flips, but a kind that holds one slot (statics, turrets) takes no more once one is selected. The slots of the other kinds are always deselected.</remarks>
    private void PickSlots(GuiEvent e, int lx, int ly, CommandLocalClient me)
    {
        var shift = (e.Mod & 3) != 0;
        if (!shift)
            _clearOnAdd = true;
        var single = Group != 3;
        var singleHeld = Group == 4 ? SelectedStaticTarget() >= 0 : Group == 0x13 && SelectedTurretTarget() >= 0;
        var hud = Host!.Hud!;
        var slots = Slots;
        var pref = PrefFilter;
        for (var i = 0; i < slots.Length; ++i)
        {
            var s = slots[i];
            if (!s.Present || s.Team != me.Team || hud.CommandBlockFilter(s.Group, s.Index) is not int filter)
                continue;
            if (filter != -1 && (filter & 0xff & pref) == 0)
                continue;
            if (!InGroup(s.Group))
                continue;
            if (Math.Abs(lx - SlotX(s)) > 3 || Math.Abs(ly - SlotY(s)) > 3)
                continue;
            for (var j = 0; j < slots.Length; ++j)
                if (Group == 4 ? slots[j].Group is not (4 or 8) : slots[j].Group != Group)
                    SelectSlot(j, false);
            if (shift)
            {
                if (!single || !singleHeld)
                    SelectSlot(i, !Selected[i]);
                continue;
            }
            for (var j = 0; j < slots.Length; ++j)
                if (Group == 4 ? slots[j].Group is 4 or 8 : slots[j].Group == Group)
                    SelectSlot(j, false);
            _clearOnAdd = false;
            SelectSlot(i, true);
            return;
        }
    }

    /// <summary>Handles a click in order mode 1 (004d1bf0).</summary>
    /// <param name="lx">The local x.</param>
    /// <param name="ly">The local y.</param>
    /// <param name="me">The local client.</param>
    /// <remarks>The slot nearest to the click under 4 pixels (any slot) is the target, else the click is a waypoint. The kind of the target and its team set $CommandTarget, its name $CommandTargetName (the scripts of Commander::StarCommand read them).</remarks>
    private void ClickOrder(int lx, int ly, CommandLocalClient me)
    {
        var hud = Host!.Hud!;
        var slots = Slots;
        var best = -1;
        var bestDist = 4.0f;
        for (var i = 0; i < slots.Length; ++i)
        {
            if (!slots[i].Present)
                continue;
            float dx = SlotX(slots[i]) - lx, dy = SlotY(slots[i]) - ly;
            var d = (float)Math.Sqrt((dy * dy) + (dx * dx));
            if (d < bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        if (best < 0)
        {
            Host.SetVar("$CommandTarget", "waypoint");
            IssueOrder(lx * 0x3ff / W, (H - ly) * 0x3ff / H, -1);
            return;
        }
        var t = slots[best];
        var name = best < 0x80 ? hud.CommandClientName(0x800 + best) ?? "" : hud.CommandTargetName(best) ?? "";
        Host.SetVar("$CommandTargetName", name);
        var friendly = t.Team == me.Team;
        var kind = t.Group switch
        {
            3 => "player",
            4 => "static",
            8 => "sensor",
            10 => "vehicle",
            0x13 => "turret",
            _ => "",
        };
        Host.SetVar("$CommandTarget", kind.Length == 0 ? "waypoint" : (friendly ? "friendly " : "enemy ") + kind);
        if (kind.Length == 0)
            IssueOrder(t.X, t.Y, -1);
        else
            IssueTargetOrder(best);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d2754: the drag moves the far corner of the rectangle.</remarks>
    public override bool MsDrag(GuiEvent e)
    {
        if (!ZoomMode && !Animating)
            (_dragX1, _dragY1) = GlobalToLocal(e.X, e.Y);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d2b1c: the button comes up. The mouse is let go. In order mode 0 the slots of the shown kind and team inside the rectangle are selected (the first selection after a plain click clears the old one, a kind of one slot takes the first), then the order cursor ends.</remarks>
    public override bool MsUp(GuiEvent e)
    {
        _dragging = false;
        Canvas?.UnlockMouse(this);
        if (ZoomMode || Animating)
            return true;
        if (OrderMode == 0)
        {
            if (Host?.Hud?.CommandLocal is not CommandLocalClient me)
                return true;
            int left = Math.Min(_dragX0, _dragX1), top = Math.Min(_dragY0, _dragY1);
            int right = Math.Max(_dragX1, _dragX0), bottom = Math.Max(_dragY1, _dragY0);
            var single = Group != 3;
            var singleHeld = Group == 4 ? SelectedStaticTarget() >= 0 : Group == 0x13 && SelectedTurretTarget() >= 0;
            var slots = Slots;
            for (var i = 0; i < slots.Length; ++i)
            {
                var s = slots[i];
                if (!s.Present || me.Team != s.Team || !InGroup(s.Group))
                    continue;
                int sx = SlotX(s), sy = SlotY(s);
                if (sx < left || right <= sx || sy < top || bottom <= sy)
                    continue;
                if (_clearOnAdd)
                {
                    for (var j = 0; j < slots.Length; ++j)
                        SelectSlot(j, false);
                    _clearOnAdd = false;
                    singleHeld = false;
                }
                if (!single || !singleHeld)
                    SelectSlot(i, true);
                if (single)
                    break;
            }
        }
        SetOrderMode(0);
        _clearOnAdd = false;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows the right button 004d2d78: in zoom mode a click zooms out, else it starts the pan unless an animation runs. It does nothing while the left button drags.</remarks>
    public override bool MsRightDown(GuiEvent e)
    {
        if (_dragging)
            return true;
        if (ZoomMode || Animating)
            _buttonTaken = true;
        if (_centerMode)
        {
            _centerMode = false;
            UnlightCenterButton();
        }
        if (ZoomMode)
            ZoomOut(e.X, e.Y, true);
        else if (!Animating)
        {
            _panning = true;
            (_panMouseX, _panMouseY, _panX, _panY) = (e.X, e.Y, X, Y);
            Canvas?.LockMouse(this);
        }
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d2e5c: the pan follows the mouse and keeps the picture over the parent.</remarks>
    public override bool MsRightDrag(GuiEvent e)
    {
        if (ZoomMode || _centerMode || _buttonTaken || Parent == null)
            return true;
        (X, Y, _, _) = Clamp(e.X - _panMouseX + _panX, e.Y - _panMouseY + _panY, W, H);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004d2fe0.</remarks>
    public override bool MsRightUp(GuiEvent e)
    {
        _buttonTaken = false;
        Canvas?.UnlockMouse(this);
        _panning = false;
        return true;
    }

    /// <summary>Returns the control rectangle after the fit of 004cef14 for a parent of a size.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <param name="parentW">The parent width.</param>
    /// <param name="parentH">The parent height.</param>
    /// <returns>The fitted rectangle.</returns>
    public static (int X, int Y, int W, int H) Fit(int x, int y, int w, int h, int parentW, int parentH)
    {
        var scale = (float)Math.Min(parentW, parentH) / Math.Max(w, h);
        int nw = (int)(w * (double)scale), nh = (int)(h * (double)scale);
        if (parentW > nw)
            x = Half(parentW - nw);
        if (parentH > nh)
            y = Half(parentH - nh);
        return (x, y, nw, nh);
    }

    // sar by one with the carry added back for a negative value: the exe halves toward zero.
    private static int Half(int v) => v / 2;

    /// <summary>Returns the tile size on screen: a quarter of the side, plus one when four tiles are narrower than the control (004d04e8).</summary>
    /// <param name="side">The control side.</param>
    /// <returns>The tile size in pixels.</returns>
    public static int TileSpan(int side)
    {
        var t = (int)(side * 0.25f);
        return t * 4 < side ? t + 1 : t;
    }

    private static bool Truthy(string v) => v.Length > 0 && !v.Equals("0", StringComparison.Ordinal) && !v.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        var pic = Host?.Hud?.CommandMap;
        if (pic == null)
            return;
        if (!ReferenceEquals(pic, _fitted) && Parent != null)
        {
            _fitted = pic;
            (X, Y, W, H) = Fit(X, Y, W, H, Parent.W, Parent.H);
        }
        if (Parent != null)
            p.SetClip(new GfxRect(ox, oy, ox + Parent.W - 1, oy + Parent.H - 1));
        int gx = ox + X, gy = oy + Y;
        int tw = TileSpan(W), th = TileSpan(H);
        for (var j = 0; j < CommandMapPictureTiles; ++j)
            for (var i = 0; i < CommandMapPictureTiles; ++i)
                p.BitmapStretch(pic.Tiles[(j * CommandMapPictureTiles) + i], gx + (i * tw), gy + (j * th), tw, th);
        if (Host?.Hud?.CommandMapMarks is { } marks)
            RenderMarks(p, marks, pic, gx, gy);
        // 004d04e8: the drag rectangle while the button is down, outline 0xff, from the corners as dragged.
        if (_dragging)
            p.Dev.DrawRectOutline(new GfxRect(Math.Min(_dragX0, _dragX1) + gx, Math.Min(_dragY0, _dragY1) + gy, Math.Max(_dragX1, _dragX0) + gx,
                Math.Max(_dragY1, _dragY0) + gy), 0xff);
    }

    /// <summary>Draws the sensor circles and the marks over the picture.</summary>
    /// <remarks>The sensor circles of 004d04e8 and the five passes of 004cf94c (0 and 1 the objects: icon, label. 2 to 4 the players: icon, order line and mark, name. 4 also the where-am-I mark). Positions are the slot's 1/1024 coordinates scaled to the control, y up.</remarks>
    private void RenderMarks(GuiPaint p, CommandMapMarks marks, CommandMapPicture pic, int gx, int gy)
    {
        var host = Host!;
        var filter = int.TryParse(host.GetVar("$pref::mapFilter"), out var f) ? f : 0;
        var showNames = Truthy(host.GetVar("$pref::mapNames"));
        int SX(CommandMapSlotMark m) => (m.X * W) >> 10;
        int SY(CommandMapSlotMark m) => H - ((m.Y * H) >> 10);
        if (Truthy(host.GetVar("$pref::mapSensorRange")))
        {
            var scale = W / pic.W;
            foreach (var m in marks.Slots)
            {
                if ((m.Group != 8 && m.Group != 19) || m.Team != marks.LocalTeam || !m.Active)
                    continue;
                int r = (int)(m.Range * (double)scale), cx = gx + SX(m), cy = gy + SY(m);
                var pt = new (int X, int Y)[32];
                for (var k = 0; k < 32; ++k)
                {
                    var a = k * 0.19634955;
                    pt[k] = ((int)(cx + (r * Math.Cos(a))), (int)(cy + (r * Math.Sin(a))));
                }
                var color = m.RangeFlag ? 0xf2 : 0xfb;
                for (var k = 0; k < 32; ++k)
                    p.Line(pt[k].X, pt[k].Y, pt[(k + 1) & 31].X, pt[(k + 1) & 31].Y, color);
            }
        }
        var bySlot = new Dictionary<int, CommandMapSlotMark>();
        foreach (var m in marks.Slots)
            bySlot[m.Slot] = m;
        var font = host.FontByName("if_small.pft");
        PbmaArray? icons = host.ArrayByName("CmdIconsCtrl.pba"), whereAmI = host.ArrayByName("CmdWhereAmI.pba");
        for (var pass = 0; pass < 5; ++pass)
            foreach (var m in marks.Slots)
            {
                if ((filter & 0x80) == 0 && (m.Filter & filter) == 0)
                    continue;
                var player = m.Group == 3;
                if (pass < 2 ? player : !player)
                    continue;
                int px = gx + SX(m), py = gy + SY(m);
                var icon = m.Icon.Length > 0 ? host.BitmapByName(m.Icon + (m.Team == marks.LocalTeam ? "_green.bmp" : "_red.bmp")) : null;
                if (player && m.ClientId == marks.LocalId && pass == 4)
                {
                    int piece = 0, flip = 0;
                    var heading = marks.Heading;
                    if (heading < 0)
                        heading += 256;
                    if (heading > 256)
                        heading -= 256;
                    foreach (var (limit, pc, fl) in WhereAmITable)
                        if (heading <= limit)
                        {
                            (piece, flip) = (pc, fl);
                            break;
                        }
                    if (Selected[m.Slot])
                        piece += 4;
                    var b = whereAmI?.Bitmaps.Count > piece ? whereAmI.Bitmaps[piece] : null;
                    if (b != null)
                        p.Dev.Blit(GuiPaint.KeyedBitmap(b), px - (b.Width >> 1), py - (b.Height >> 1), (BlitFlip)flip);
                }
                else if (icon != null && (pass == 0 || (pass == 2 && m.ClientId != marks.LocalId)))
                    p.Bitmap(icon, px - (icon.Width >> 1), py - (icon.Height >> 1));
                // The outline of a selected slot, 0xff, 6 pixels around its place: a player other than the local client in pass 2 or 4, a static, sensor or
                // turret in pass 0 [code 004cf94c].
                if (Selected[m.Slot] && ((player && m.ClientId != marks.LocalId && pass is 2 or 4) || (m.Group is 4 or 8 or 0x13 && pass == 0)))
                    p.Dev.DrawRectOutline(new GfxRect(px - 6, py - 6, px + 6, py + 6), 0xff);
                if (player)
                {
                    if (m.OrderIcon != -1 && (pass == 2 || pass == 4))
                    {
                        (var tx, var ty) = m.OrderTarget != -1 && bySlot.TryGetValue(m.OrderTarget, out var t) ? (t.X, t.Y) : (m.OrderX, m.OrderY);
                        int sx = gx + ((tx * W) >> 10), sy = gy + (H - ((ty * H) >> 10));
                        p.Line(px, py, sx, sy, m.OrderStatus == 1 ? 0xf2 : 0xff);
                        if (m.OrderIcon is > 0 and < 9 && icons?.Bitmaps.Count > m.OrderIcon - 1)
                        {
                            var b = icons.Bitmaps[m.OrderIcon - 1];
                            p.Bitmap(b, sx - (b.Width >> 1), sy - (b.Height >> 1));
                        }
                    }
                    if (pass >= 3 && font != null)
                        p.Text(font, px + 4, py - 4, m.ClientName);
                }
                else if (pass == 1 && font != null && m.TargetName != null && (Selected[m.Slot] || showNames || CursorNear(gx, gy, px, py)))
                    p.Text(font, px + 4, py - 4, m.TargetName.Length > 0 ? m.TargetName : m.Description);
            }
    }

    // The label also shows while the cursor is within 4 pixels of the slot [code 004cf94c: the cursor minus the control place minus 4, tested 0 to 8].
    private bool CursorNear(int gx, int gy, int px, int py)
    {
        if (Canvas == null)
            return false;
        int dx = (int)Canvas.CursorX - gx - 4, dy = (int)Canvas.CursorY - gy - 4;
        int a = px - gx - dx, b = py - gy - dy;
        return a is >= 0 and < 9 && b is >= 0 and < 9;
    }

    // The where-am-I table at 00629fe8: the heading (0 to 256) up to which a piece of CmdWhereAmI.pba is used, with the flip of the blit.
    private static readonly (int Limit, int Piece, int Flip)[] WhereAmITable =
    [
        (12, 0, 0), (32, 1, 0), (52, 2, 0), (76, 3, 0), (96, 2, 2), (116, 1, 2), (140, 0, 2), (160, 1, 3), (180, 2, 3), (204, 3, 1), (224, 2, 1), (244, 1, 1),
    ];

    private const int CommandMapPictureTiles = 4;
}
