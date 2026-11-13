namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>The GUI event (type 0x412) the canvas builds from a raw device event (docs/gui.md 2.7).</summary>
public struct GuiEvent
{
    /// <summary>The x position in canvas pixels.</summary>
    public int X;
    /// <summary>The y position in canvas pixels.</summary>
    public int Y;
    /// <summary>The DirectInput scan code.</summary>
    public int Scan;       // DirectInput scan code
    /// <summary>The character of a key press.</summary>
    public char Ascii;
    /// <summary>The modifier bits: 0x03 shift, 0x0c ctrl, 0x30 alt.</summary>
    public byte Mod;       // 0x03 shift, 0x0c ctrl, 0x30 alt
    /// <summary>The click count of a mouse press: 2 or more for a double click.</summary>
    public int ClickCount;
    /// <summary>Gets a value that is true when a shift key is held.</summary>
    public readonly bool Shift => (Mod & 0x03) != 0;
    /// <summary>Gets a value that is true when a ctrl key is held.</summary>
    public readonly bool Ctrl => (Mod & 0x0c) != 0;
}

/// <summary>The 17 responder slots (docs/gui.md 2.7). A control forwards what it does not handle to the next responder.</summary>
public interface IGuiResponder
{
    /// <summary>Handles a left button press.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsDown(GuiEvent e);
    /// <summary>Handles a left button repeat while held.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsRepeat(GuiEvent e);
    /// <summary>Handles a left button release.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsUp(GuiEvent e);
    /// <summary>Handles a mouse move.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsMove(GuiEvent e);
    /// <summary>Handles the mouse entering the control.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsEnter(GuiEvent e);
    /// <summary>Handles the mouse leaving the control.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsLeave(GuiEvent e);
    /// <summary>Handles a mouse drag with the left button.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsDrag(GuiEvent e);
    /// <summary>Handles a right button press.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsRightDown(GuiEvent e);
    /// <summary>Handles a right button release.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsRightUp(GuiEvent e);
    /// <summary>Handles a mouse drag with the right button.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool MsRightDrag(GuiEvent e);
    /// <summary>Handles a key release.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool KeyUp(GuiEvent e);
    /// <summary>Handles a key repeat.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool KeyRepeat(GuiEvent e);
    /// <summary>Handles a key press.</summary>
    /// <param name="e">The event.</param>
    /// <returns>True when handled.</returns>
    public bool KeyDown(GuiEvent e);
    /// <summary>Handles a message from another control.</summary>
    /// <param name="sender">The sending control, or null.</param>
    /// <param name="tag">The message tag.</param>
    /// <returns>True when handled.</returns>
    public bool Message(GuiControl? sender, int tag);
    /// <summary>Returns whether the responder lets go of the keyboard focus.</summary>
    /// <returns>True when it can release.</returns>
    public bool CanRelease();
    /// <summary>Returns whether the responder takes the keyboard focus.</summary>
    /// <returns>True when it can take it.</returns>
    public bool CanTake();
}

/// <summary>The base control (SimGui::Control, docs/gui.md 2.3 to 2.6).</summary>
/// <remarks>It holds the control tree, sizing, visibility, hit testing, the responder chain defaults and the base render.</remarks>
public class GuiControl : IGuiResponder
{
    /// <summary>The class name of the table in <see cref="GuiClassTable"/>.</summary>
    public string ClassName = "SimGui::Control";
    /// <summary>The name the parent gave the control, or null.</summary>
    public string? Name;
    /// <summary>The version of the persisted block.</summary>
    public uint PersistVersion;
    /// <summary>The version of the SimGui::Control body.</summary>
    public byte BodyVersion = 4;
    /// <summary>The parent control, or null for a root.</summary>
    public GuiControl? Parent;
    /// <summary>The canvas the control belongs to, or null.</summary>
    public GuiCanvas? Canvas;
    /// <summary>The child controls in member order.</summary>
    public readonly List<GuiControl> Children = [];
    /// <summary>The persistent fields of docs/gui.md 1.4 by the names of <see cref="GuiLoader.Routines"/>.</summary>
    public readonly Dictionary<string, object> F = new(StringComparer.Ordinal);

    /// <summary>The x position in the parent.</summary>
    public int X;
    /// <summary>The y position in the parent.</summary>
    public int Y;
    /// <summary>The width.</summary>
    public int W = 50;
    /// <summary>The height.</summary>
    public int H = 50;
    /// <summary>The flag bits: bit 0 visible, bit 1 delete on lose.</summary>
    public uint Flags = 3;
    /// <summary>The control id.</summary>
    public uint Id;
    /// <summary>The horizontal sizing mode: 0 fixed, 1 grows with the parent, 2 follows the right edge, 3 centred.</summary>
    public uint HSizing;
    /// <summary>The vertical sizing mode, as for <see cref="HSizing"/>.</summary>
    public uint VSizing;
    /// <summary>The tag of the help text.</summary>
    public uint HelpTag;
    /// <summary>The fill mode: not 0 fills the control.</summary>
    public byte Opaque;
    /// <summary>The palette index of the fill.</summary>
    public byte FillColor;
    /// <summary>The palette index of the fill while selected.</summary>
    public byte SelectedFill;
    /// <summary>The palette index of the fill while dimmed or disabled.</summary>
    public byte DisabledFill;
    /// <summary>The border mode: not 0 draws the border.</summary>
    public byte Border;
    /// <summary>The palette index of the border.</summary>
    public byte BorderColor;
    /// <summary>The palette index of the border while selected.</summary>
    public byte SelectedBorder;
    /// <summary>The palette index of the border while dimmed or disabled.</summary>
    public byte DisabledBorder;
    /// <summary>The console command the control runs.</summary>
    public string Command = "";
    /// <summary>The alternate console command.</summary>
    public string AltCommand = "";
    /// <summary>The name of the console variable the control mirrors.</summary>
    public string ConsoleVar = "";
    /// <summary>The horizontal scroll step of a line.</summary>
    public int ScrollLineX = 5;
    /// <summary>The vertical scroll step of a line.</summary>
    public int ScrollLineY = 5;

    /// <summary>Gets or sets whether the control is visible (flag bit 0).</summary>
    public bool Visible
    {
        get => (Flags & 1) != 0;
        set => Flags = value ? Flags | 1 : Flags & ~1u;
    }
    /// <summary>Gets a value that is true when the control is deleted when it loses its place (flag bit 1).</summary>
    public bool DeleteOnLose => (Flags & 2) != 0;

    /// <summary>Reads a persistent field as an unsigned integer.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The value.</returns>
    public uint U(string name, uint def = 0) => F.TryGetValue(name, out var v) ? Convert.ToUInt32(v) : def;
    /// <summary>Reads a persistent field as a string.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, empty when the field is missing or not a string.</returns>
    public string S(string name) => F.TryGetValue(name, out var v) && v is string s ? s : "";
    /// <summary>Gets the host of the canvas, or null.</summary>
    public GuiHost? Host => Canvas?.Host;

    // ---- tree (2.3) ----

    /// <summary>Adds a child control.</summary>
    /// <param name="c">The child.</param>
    public virtual void AddChild(GuiControl c)
    {
        c.Parent = this;
        c.Canvas = Canvas;
        Children.Add(c);
        c.OnAdd();
    }

    /// <summary>Removes a child control.</summary>
    /// <param name="c">The child.</param>
    public virtual void RemoveChild(GuiControl c)
    {
        Children.Remove(c);
        c.Parent = null;
    }

    /// <summary>Records the canvas in the control and all descendants (0053a144).</summary>
    /// <param name="canvas">The canvas, or null.</param>
    public void SetCanvas(GuiCanvas? canvas)
    {
        Canvas = canvas;
        foreach (var k in Children)
            k.SetCanvas(canvas);
    }

    /// <summary>Runs after the control was added to a parent.</summary>
    protected virtual void OnAdd() { }

    /// <summary>Returns the position of the control in canvas pixels.</summary>
    /// <returns>The sum of the offsets up the parent chain.</returns>
    public (int x, int y) GlobalOrigin()
    {
        int gx = 0, gy = 0;
        for (var c = this; c != null; c = c.Parent)
        {
            gx += c.X;
            gy += c.Y;
        }
        return (gx, gy);
    }

    /// <summary>Converts a canvas point to a point local to the control.</summary>
    /// <param name="x">The canvas x.</param>
    /// <param name="y">The canvas y.</param>
    /// <returns>The local point.</returns>
    public (int x, int y) GlobalToLocal(int x, int y)
    {
        var (gx, gy) = GlobalOrigin();
        return (x - gx, y - gy);
    }

    /// <summary>Returns whether a local point is inside the control.</summary>
    /// <param name="lx">The local x.</param>
    /// <param name="ly">The local y.</param>
    /// <returns>True when inside.</returns>
    public bool Contains(int lx, int ly) => lx >= 0 && ly >= 0 && lx < W && ly < H;

    /// <summary>Finds the deepest visible control under a point (00539f9c).</summary>
    /// <param name="px">The x in this control's frame.</param>
    /// <param name="py">The y in this control's frame.</param>
    /// <returns>The control, or this control when no child is hit.</returns>
    public virtual GuiControl Pick(int px, int py)
    {
        for (var i = Children.Count - 1; i >= 0; --i)
        {
            var k = Children[i];
            if (!k.Visible)
                continue;
            if (px >= k.X && py >= k.Y && px < k.X + k.W && py < k.Y + k.H && k.PickInside(px - k.X, py - k.Y))
                return k.Pick(px - k.X, py - k.Y);
        }
        return this;
    }

    /// <summary>Returns whether a point inside the control's frame hits the control itself.</summary>
    /// <param name="lx">The local x.</param>
    /// <param name="ly">The local y.</param>
    /// <returns>True when hit.</returns>
    protected virtual bool PickInside(int lx, int ly) => true;

    /// <summary>Finds a control by id in this subtree.</summary>
    /// <param name="id">The id, not 0.</param>
    /// <returns>The control, or null.</returns>
    public GuiControl? FindById(uint id)
    {
        if (Id == id && id != 0)
            return this;
        foreach (var k in Children)
            if (k.FindById(id) is GuiControl r)
                return r;
        return null;
    }

    /// <summary>Finds a control by name in this subtree, case insensitive.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The control, or null.</returns>
    public GuiControl? FindByName(string name)
    {
        if (Name != null && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
            return this;
        foreach (var k in Children)
            if (k.FindByName(name) is GuiControl r)
                return r;
        return null;
    }

    /// <summary>Enumerates this control and all its descendants.</summary>
    /// <returns>The controls, each control before its children.</returns>
    public IEnumerable<GuiControl> Descendants()
    {
        yield return this;
        foreach (var k in Children)
            foreach (var d in k.Descendants())
                yield return d;
    }

    // ---- sizing (2.4) ----

    /// <summary>Moves and resizes the control.</summary>
    /// <param name="x">The new x.</param>
    /// <param name="y">The new y.</param>
    /// <param name="w">The new width.</param>
    /// <param name="h">The new height.</param>
    /// <remarks>Follows resize (0053a350): it tells the children the old and new extent, stores the new one, then tells the parent.</remarks>
    public virtual void Resize(int x, int y, int w, int h)
    {
        int ow = W, oh = H;
        foreach (var k in Children.ToArray())
            k.ParentResized(ow, oh, w, h);
        X = x;
        Y = y;
        W = w;
        H = h;
        Parent?.ChildResized(this);
    }

    /// <summary>Runs after a child was resized.</summary>
    /// <param name="child">The child.</param>
    public virtual void ChildResized(GuiControl child) { }

    /// <summary>Applies the sizing modes after the parent was resized (0053a2a0).</summary>
    /// <param name="oldW">The old parent width.</param>
    /// <param name="oldH">The old parent height.</param>
    /// <param name="newW">The new parent width.</param>
    /// <param name="newH">The new parent height.</param>
    public virtual void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        int dx = newW - oldW, dy = newH - oldH;
        int nx = X, ny = Y, nw = W, nh = H;
        switch (HSizing)
        {
            case 1:
                nw += dx;
                break;
            case 2:
                nx += dx;
                break;
            case 3:
                nx = (newW - W) >> 1;
                break;
        }
        switch (VSizing)
        {
            case 1:
                nh += dy;
                break;
            case 2:
                ny += dy;
                break;
            case 3:
                ny = (newH - H) >> 1;
                break;
        }
        if (nx != X || ny != Y || nw != W || nh != H)
            Resize(nx, ny, nw, nh);
    }

    // ---- visibility, value, state ----

    /// <summary>Sets the visibility and invalidates the canvas.</summary>
    /// <param name="v">True to show.</param>
    /// <remarks>Follows Control::setVisible, which toggles the branch (00538dc4).</remarks>
    public virtual void SetVisible(bool v)
    {
        Visible = v;
        Canvas?.Invalidate();
    }

    /// <summary>Gets the value of the control as text.</summary>
    /// <returns>The value, empty for a plain control.</returns>
    public virtual string GetValue() => "";
    /// <summary>Sets the value of the control and mirrors it to the console variable.</summary>
    /// <param name="v">The value text.</param>
    /// <remarks>Follows Control::setValue (slot 0x98, docs/gui.md 2.3). A change coming from the variable only applies the value.</remarks>
    public void SetValue(string v)
    {
        ApplyValue(v);
        if (ConsoleVar.Length > 0)
            WriteVariable(GetValue());
    }
    /// <summary>Applies a value text to the control without writing the console variable.</summary>
    /// <param name="v">The value text.</param>
    protected virtual void ApplyValue(string v) { }
    /// <summary>Gets or sets whether the control is active.</summary>
    public virtual bool Active { get => true; set { } }
    /// <summary>Gets a value that is true for an active control class.</summary>
    public virtual bool IsActiveCtrl => false;
    /// <summary>Gets the text of the control.</summary>
    /// <returns>The text.</returns>
    public virtual string GetText() => GetValue();
    /// <summary>Sets the text of the control.</summary>
    /// <param name="t">The text.</param>
    public virtual void SetText(string t) => SetValue(t);

    /// <summary>Gets a value that is true when a modal dialog dims the control (docs/gui.md 2.5).</summary>
    public bool Dimmed => Canvas?.IsDimmed(this) ?? false;

    // ---- life cycle ----

    /// <summary>Wakes the children first, then the control (slot 0xb0, 0053a6e8).</summary>
    public void WakeTree()
    {
        foreach (var k in Children.ToArray())
            k.WakeTree();
        Wake();
    }

    /// <summary>Closes the children first, then the control.</summary>
    public void CloseTree()
    {
        foreach (var k in Children.ToArray())
            k.CloseTree();
        Close();
    }

    /// <summary>Runs <see cref="PreRender"/> on the children first, then on the control.</summary>
    public void PreRenderTree()
    {
        foreach (var k in Children.ToArray())
            k.PreRenderTree();
        PreRender();
    }

    /// <summary>Runs when the control's content is shown.</summary>
    public virtual void Wake() { }
    /// <summary>Runs when the control's content is removed.</summary>
    public virtual void Close() { }
    /// <summary>Runs before each render.</summary>
    public virtual void PreRender() { }

    /// <summary>Applies the console variable text that was pushed in (slot 0x70, 00539dcc).</summary>
    /// <param name="text">The variable text.</param>
    public virtual void VariableChanged(string text) => ApplyValue(text);

    /// <summary>Reads the console variable and applies it.</summary>
    public void PushVariable()
    {
        if (ConsoleVar.Length > 0 && Host != null)
            VariableChanged(Host.GetVar(ConsoleVar));
    }

    /// <summary>Writes text to the console variable.</summary>
    /// <param name="text">The text.</param>
    public void WriteVariable(string text)
    {
        if (ConsoleVar.Length > 0 && Host != null)
            Host.SetVar(ConsoleVar, text);
    }

    /// <summary>Gets a value that is true when the control is in the tab list (docs/gui.md 2.6). A plain control answers no.</summary>
    public virtual bool WantsTab => false;

    /// <summary>The cursor tag of the hand.</summary>
    public const uint CursorHand = 160551;
    /// <summary>The cursor tag of the open hand.</summary>
    public const uint CursorOpenHand = 160552;
    /// <summary>The cursor tag of the grab hand.</summary>
    public const uint CursorGrab = 160553;
    /// <summary>The cursor tag of the text I beam.</summary>
    public const uint CursorIBeam = 160554;
    /// <summary>The cursor tag of the horizontal adjust arrows.</summary>
    public const uint CursorHAdjust = 160555;
    /// <summary>Returns the help tag at a point once the cursor has rested (slot 0xa4).</summary>
    /// <param name="lx">The control-local x.</param>
    /// <param name="ly">The control-local y.</param>
    /// <param name="ms">The milliseconds the cursor has rested.</param>
    /// <returns>The help tag (+0x188 unless the class says more).</returns>
    public virtual uint HelpTagAt(int lx, int ly, long ms) => HelpTag;
    /// <summary>Returns the cursor tag for the cursor at a point (slot 0xa0).</summary>
    /// <param name="lx">The control-local x.</param>
    /// <param name="ly">The control-local y.</param>
    /// <returns>The tag, 0 to keep the canvas default.</returns>
    /// <remarks>The tags are the IDBMP_CURSOR_* entries of fear.strings.cs ("CUR_Hand.bmp, 8, 2": bitmap and hot spot). The help items are slots 0xa4 and 0xa8.</remarks>
    public virtual uint CursorTagAt(int lx, int ly) => ClassName == "FearGui::FGTabMenu" ? TabMenuCursor(lx, ly) : 0;

    // 004cca88: the hand over the tab children of the menu.
    private uint TabMenuCursor(int lx, int ly)
    {
        foreach (var c in Children)
            if (c is GuiMenuItem && lx >= c.X && lx <= c.X + c.W && ly >= c.Y && ly <= c.Y + c.H)
                return CursorHand;
        return 0;
    }

    // ---- render (2.5) ----

    /// <summary>Draws the control and its children.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="ox">The x of the parent origin in canvas pixels.</param>
    /// <param name="oy">The y of the parent origin in canvas pixels.</param>
    public virtual void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        RenderChildren(p, gx, gy);
    }

    /// <summary>Draws the base fill and border (0053a59c).</summary>
    /// <param name="p">The painter.</param>
    /// <param name="gx">The control x in canvas pixels.</param>
    /// <param name="gy">The control y in canvas pixels.</param>
    /// <param name="dimmed">True to use the disabled colors.</param>
    protected void DrawBase(GuiPaint p, int gx, int gy, bool dimmed = false)
    {
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, dimmed ? DisabledFill : FillColor);
        if (Border != 0)
            p.Outline(gx, gy, W, H, dimmed ? DisabledBorder : BorderColor);
    }

    /// <summary>Draws each visible child in member order, clipped to the parent's clip (0053a3f4).</summary>
    /// <param name="p">The painter.</param>
    /// <param name="gx">The control x in canvas pixels.</param>
    /// <param name="gy">The control y in canvas pixels.</param>
    protected void RenderChildren(GuiPaint p, int gx, int gy)
    {
        foreach (var k in Children.ToArray())
        {
            if (!k.Visible)
                continue;
            var saved = p.Clip;
            GfxRect r = new(gx + k.X, gy + k.Y, gx + k.X + k.W - 1, gy + k.Y + k.H - 1);
            var c = saved.Intersect(r);
            if (c.IsEmpty)
                continue;
            p.SetClip(c);
            k.Render(p, gx, gy);
            p.SetClip(saved);
        }
    }

    // ---- responder defaults (2.7): hand the event to the next responder ----

    /// <summary>Gets the next responder: the parent, else the responder the canvas puts above.</summary>
    public IGuiResponder? Next => Parent ?? (Canvas?.ResponderAbove(this));

    /// <inheritdoc/>
    public virtual bool MsDown(GuiEvent e) => Next?.MsDown(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsRepeat(GuiEvent e) => Next?.MsRepeat(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsUp(GuiEvent e) => Next?.MsUp(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsMove(GuiEvent e) => Next?.MsMove(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsEnter(GuiEvent e) => Next?.MsEnter(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsLeave(GuiEvent e) => Next?.MsLeave(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsDrag(GuiEvent e) => Next?.MsDrag(e) ?? false;
    /// <inheritdoc/>
    /// <remarks>Slots 0x1c, 0x24 and 0x28 (docs/gui.md 2.7): a control that does not use the right button hands it to the next responder.</remarks>
    public virtual bool MsRightDown(GuiEvent e) => Next?.MsRightDown(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsRightUp(GuiEvent e) => Next?.MsRightUp(e) ?? false;
    /// <inheritdoc/>
    public virtual bool MsRightDrag(GuiEvent e) => Next?.MsRightDrag(e) ?? false;
    /// <inheritdoc/>
    public virtual bool KeyUp(GuiEvent e) => Next?.KeyUp(e) ?? false;
    /// <inheritdoc/>
    public virtual bool KeyRepeat(GuiEvent e) => Next?.KeyRepeat(e) ?? false;
    /// <inheritdoc/>
    public virtual bool KeyDown(GuiEvent e) => Next?.KeyDown(e) ?? false;
    /// <inheritdoc/>
    public virtual bool Message(GuiControl? sender, int tag) => Next?.Message(sender, tag) ?? false;
    /// <inheritdoc/>
    public virtual bool CanRelease() => true;
    /// <inheritdoc/>
    public virtual bool CanTake() => false;

    /// <summary>Sends message tag to every sibling (broadcast of sibling message 3, 0053a684).</summary>
    /// <param name="tag">The message tag.</param>
    public void BroadcastSibling(int tag)
    {
        if (Parent == null)
            return;
        foreach (var s in Parent.Children.ToArray())
            if (s != this)
                s.Message(this, tag);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{ClassName}{(Name != null ? " " + Name : "")} ({X},{Y} {W}x{H})";
}

/// <summary>The clip and the primitives a control draws with, in global canvas pixels.</summary>
public sealed class GuiPaint
{
    /// <summary>The graphics device.</summary>
    public readonly IGfxDevice Dev;
    /// <summary>The GUI host.</summary>
    public readonly GuiHost Host;
    /// <summary>The current clip rectangle.</summary>
    public GfxRect Clip;

    /// <summary>Creates a painter that clips to the whole device.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="host">The GUI host.</param>
    public GuiPaint(IGfxDevice dev, GuiHost host)
    {
        Dev = dev;
        Host = host;
        Clip = new GfxRect(0, 0, dev.Width - 1, dev.Height - 1);
    }

    /// <summary>Sets the clip rectangle.</summary>
    /// <param name="r">The rectangle.</param>
    public void SetClip(GfxRect r)
    {
        Clip = r;
        Dev.SetClip(r);
    }

    /// <summary>Fills a rectangle.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <param name="color">The palette index.</param>
    /// <param name="alpha">The opacity, 1 for opaque.</param>
    public void Fill(int x, int y, int w, int h, int color, float alpha = 1f)
    {
        if (w > 0 && h > 0)
            Dev.FillRect(new GfxRect(x, y, x + w - 1, y + h - 1), color, alpha);
    }

    /// <summary>Draws a rectangle outline.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <param name="color">The palette index.</param>
    public void Outline(int x, int y, int w, int h, int color)
    {
        if (w > 0 && h > 0)
            Dev.DrawRectOutline(new GfxRect(x, y, x + w - 1, y + h - 1), color);
    }

    /// <summary>Draws a line.</summary>
    /// <param name="x0">The start x.</param>
    /// <param name="y0">The start y.</param>
    /// <param name="x1">The end x.</param>
    /// <param name="y1">The end y.</param>
    /// <param name="color">The palette index.</param>
    public void Line(int x0, int y0, int x1, int y1, int color) => Dev.DrawLine(x0, y0, x1, y1, color);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PbmpBitmap, PbmpBitmap> sKeyed = [];

    /// <summary>Returns the copy of a bitmap with the transparent flag set.</summary>
    /// <param name="b">The bitmap.</param>
    /// <returns>The keyed copy, or the bitmap itself when it is transparent already or not 8 bit.</returns>
    /// <remarks>The stock client draws every GUI picture with palette index 0 clear (its palette entry has alpha 0): the weapon HUD icons show the shade behind them and the command screen's frame pieces leave the buttons under them visible. The bitmap attributes do not say so (stock files carry 8 only), so the draw uses a copy with the transparent flag.</remarks>
    public static PbmpBitmap KeyedBitmap(PbmpBitmap b) => Keyed(b);
    private static PbmpBitmap Keyed(PbmpBitmap b) => (b.Attributes & BitmapAttributes.Transparent) != 0 || b.Depth != 8 ? b :
        sKeyed.GetValue(b, o => new PbmpBitmap
        {
            Width = o.Width,
            Height = o.Height,
            Depth = o.Depth,
            Attributes = o.Attributes | BitmapAttributes.Transparent,
            ChunkCount = o.ChunkCount,
            Pixels = o.Pixels,
            MipCount = o.MipCount,
            MipLevels = o.MipLevels,
            PaletteId = o.PaletteId,
        });

    /// <summary>Draws a bitmap.</summary>
    /// <param name="b">The bitmap, or null for none.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void Bitmap(PbmpBitmap? b, int x, int y)
    {
        if (b != null)
            Dev.Blit(Keyed(b), x, y);
    }

    /// <summary>Draws a bitmap mirrored left to right.</summary>
    /// <param name="b">The bitmap, or null for none.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void BitmapFlipX(PbmpBitmap? b, int x, int y)
    {
        if (b != null)
            Dev.Blit(Keyed(b), x, y, BlitFlip.X);
    }

    /// <summary>Draws a bitmap. Every GUI bitmap is drawn keyed already, so this equals <see cref="Bitmap"/>.</summary>
    /// <param name="b">The bitmap, or null for none.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void BitmapKeyed(PbmpBitmap? b, int x, int y) => Bitmap(b, x, y);

    /// <summary>Draws a bitmap stretched to a size.</summary>
    /// <param name="b">The bitmap, or null for none.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    public void BitmapStretch(PbmpBitmap? b, int x, int y, int w, int h)
    {
        if (b != null && w > 0 && h > 0)
            Dev.BlitStretch(Keyed(b), x, y, w, h);
    }

    /// <summary>Draws text.</summary>
    /// <param name="f">The font, or null for none.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="s">The text.</param>
    /// <returns>The y after the text, or <paramref name="y"/> when nothing was drawn.</returns>
    public int Text(PftFont? f, int x, int y, string s)
    {
        if (f == null || s.Length == 0)
            return y;
        return Dev.DrawText(f, x, y, s);
    }

    /// <summary>Measures the width of a text.</summary>
    /// <param name="f">The font, or null for none.</param>
    /// <param name="s">The text.</param>
    /// <returns>The width in pixels.</returns>
    public static int TextWidth(PftFont? f, string s) => f == null ? 0 : f.MeasureTextWidth(s);
    /// <summary>Returns the line height of a font.</summary>
    /// <param name="f">The font, or null for none.</param>
    /// <returns>The cell height in pixels.</returns>
    public static int FontHeight(PftFont? f) => f == null ? 0 : f.CellHeight;
}
