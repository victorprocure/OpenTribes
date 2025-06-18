// docs/gui.md 2.3 to 2.6: the control tree, sizing, visibility, hit testing, the responder chain defaults and the
// base render. Class SimGui::Control.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// docs/gui.md 2.7: the GUI event (type 0x412) the canvas builds from a raw device event.
public struct GuiEvent
{
    public int X, Y;       // canvas pixels
    public int Scan;       // DirectInput scan code
    public char Ascii;
    public byte Mod;       // 0x03 shift, 0x0c ctrl, 0x30 alt
    public int ClickCount;
    public bool Shift => (Mod & 0x03) != 0;
    public bool Ctrl => (Mod & 0x0c) != 0;
}

// docs/gui.md 2.7: the 17 responder slots. A control forwards what it does not handle to the next responder.
public interface IGuiResponder
{
    bool MsDown(GuiEvent e);
    bool MsRepeat(GuiEvent e);
    bool MsUp(GuiEvent e);
    bool MsMove(GuiEvent e);
    bool MsEnter(GuiEvent e);
    bool MsLeave(GuiEvent e);
    bool MsDrag(GuiEvent e);
    bool KeyUp(GuiEvent e);
    bool KeyRepeat(GuiEvent e);
    bool KeyDown(GuiEvent e);
    bool Message(GuiControl? sender, int tag);
    bool CanRelease();
    bool CanTake();
}

public class GuiControl : IGuiResponder
{
    public string ClassName = "SimGui::Control";
    public string? Name;
    public uint PersistVersion;
    public byte BodyVersion = 4;
    public GuiControl? Parent;
    public GuiCanvas? Canvas;
    public readonly List<GuiControl> Children = new();
    // The persistent fields of 1.4 by the names of GuiLoader.Routines.
    public readonly Dictionary<string, object> F = new(StringComparer.Ordinal);

    public int X, Y, W = 50, H = 50;
    public uint Flags = 3, Id, HSizing, VSizing, HelpTag;
    public byte Opaque, FillColor, SelectedFill, DisabledFill, Border, BorderColor, SelectedBorder, DisabledBorder;
    public string Command = "", AltCommand = "", ConsoleVar = "";
    public int ScrollLineX = 5, ScrollLineY = 5;

    public bool Visible
    {
        get => (Flags & 1) != 0;
        set => Flags = value ? Flags | 1 : Flags & ~1u;
    }
    public bool DeleteOnLose => (Flags & 2) != 0;

    public uint U(string name, uint def = 0) => F.TryGetValue(name, out object? v) ? Convert.ToUInt32(v) : def;
    public string S(string name) => F.TryGetValue(name, out object? v) && v is string s ? s : "";
    public GuiHost? Host => Canvas?.Host;

    // ---- tree (2.3) ----

    public virtual void AddChild(GuiControl c)
    {
        c.Parent = this;
        c.Canvas = Canvas;
        Children.Add(c);
        c.OnAdd();
    }

    public virtual void RemoveChild(GuiControl c)
    {
        Children.Remove(c);
        c.Parent = null;
    }

    // Records the canvas in the control and all descendants [code 0053a144].
    public void SetCanvas(GuiCanvas? canvas)
    {
        Canvas = canvas;
        foreach (GuiControl k in Children)
            k.SetCanvas(canvas);
    }

    protected virtual void OnAdd() { }

    public (int x, int y) GlobalOrigin()
    {
        int gx = 0, gy = 0;
        for (GuiControl? c = this; c != null; c = c.Parent)
        {
            gx += c.X;
            gy += c.Y;
        }
        return (gx, gy);
    }

    public (int x, int y) GlobalToLocal(int x, int y)
    {
        var (gx, gy) = GlobalOrigin();
        return (x - gx, y - gy);
    }

    public bool Contains(int lx, int ly) => lx >= 0 && ly >= 0 && lx < W && ly < H;

    // Hit test [code 00539f9c]: the point is in this control's frame.
    public virtual GuiControl Pick(int px, int py)
    {
        for (int i = Children.Count - 1; i >= 0; --i)
        {
            GuiControl k = Children[i];
            if (!k.Visible)
                continue;
            if (px >= k.X && py >= k.Y && px < k.X + k.W && py < k.Y + k.H && k.PickInside(px - k.X, py - k.Y))
                return k.Pick(px - k.X, py - k.Y);
        }
        return this;
    }

    protected virtual bool PickInside(int lx, int ly) => true;

    public GuiControl? FindById(uint id)
    {
        if (Id == id && id != 0)
            return this;
        foreach (GuiControl k in Children)
            if (k.FindById(id) is GuiControl r)
                return r;
        return null;
    }

    public GuiControl? FindByName(string name)
    {
        if (Name != null && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
            return this;
        foreach (GuiControl k in Children)
            if (k.FindByName(name) is GuiControl r)
                return r;
        return null;
    }

    public IEnumerable<GuiControl> Descendants()
    {
        yield return this;
        foreach (GuiControl k in Children)
            foreach (GuiControl d in k.Descendants())
                yield return d;
    }

    // ---- sizing (2.4) ----

    // resize(position, extent) [code 0053a350]: tells the children the old and new extent, stores, then tells the parent.
    public virtual void Resize(int x, int y, int w, int h)
    {
        int ow = W, oh = H;
        foreach (GuiControl k in Children.ToArray())
            k.ParentResized(ow, oh, w, h);
        X = x; Y = y; W = w; H = h;
        Parent?.ChildResized(this);
    }

    public virtual void ChildResized(GuiControl child) { }

    // [code 0053a2a0]
    public virtual void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        int dx = newW - oldW, dy = newH - oldH;
        int nx = X, ny = Y, nw = W, nh = H;
        switch (HSizing)
        {
            case 1: nw += dx; break;
            case 2: nx += dx; break;
            case 3: nx = (newW - W) >> 1; break;
        }
        switch (VSizing)
        {
            case 1: nh += dy; break;
            case 2: ny += dy; break;
            case 3: ny = (newH - H) >> 1; break;
        }
        if (nx != X || ny != Y || nw != W || nh != H)
            Resize(nx, ny, nw, nh);
    }

    // ---- visibility, value, state ----

    // Control::setVisible: toggles the branch [code 00538dc4].
    public virtual void SetVisible(bool v)
    {
        Visible = v;
        Canvas?.Invalidate();
    }

    public virtual string GetValue() => "";
    // Control::setValue (slot 0x98) mirrors the console variable (2.3); a change coming from the variable only applies.
    public void SetValue(string v)
    {
        ApplyValue(v);
        if (ConsoleVar.Length > 0)
            WriteVariable(GetValue());
    }
    protected virtual void ApplyValue(string v) { }
    public virtual bool Active { get => true; set { } }
    public virtual bool IsActiveCtrl => false;
    public virtual string GetText() => GetValue();
    public virtual void SetText(string t) => SetValue(t);

    // Whether the control is dimmed by a modal dialog (2.5).
    public bool Dimmed => Canvas?.IsDimmed(this) ?? false;

    // ---- life cycle ----

    // wake [slot 0xb0]: children first [code 0053a6e8].
    public void WakeTree()
    {
        foreach (GuiControl k in Children.ToArray())
            k.WakeTree();
        Wake();
    }

    public void CloseTree()
    {
        foreach (GuiControl k in Children.ToArray())
            k.CloseTree();
        Close();
    }

    public void PreRenderTree()
    {
        foreach (GuiControl k in Children.ToArray())
            k.PreRenderTree();
        PreRender();
    }

    public virtual void Wake() { }
    public virtual void Close() { }
    public virtual void PreRender() { }

    // Variable-changed slot (0x70): the console variable text was pushed in [code 00539dcc].
    public virtual void VariableChanged(string text) => ApplyValue(text);

    public void PushVariable()
    {
        if (ConsoleVar.Length > 0 && Host != null)
            VariableChanged(Host.GetVar(ConsoleVar));
    }

    public void WriteVariable(string text)
    {
        if (ConsoleVar.Length > 0 && Host != null)
            Host.SetVar(ConsoleVar, text);
    }

    // True when the control is in the tab list (2.6): plain Control answers no.
    public virtual bool WantsTab => false;

    // Cursor tag (slot 0xa0), help items (0xa4, 0xa8).
    public virtual string? CursorTag => null;

    // ---- render (2.5) ----

    public virtual void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        RenderChildren(p, gx, gy);
    }

    // Base fill and border [code 0053a59c].
    protected void DrawBase(GuiPaint p, int gx, int gy, bool dimmed = false)
    {
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, dimmed ? DisabledFill : FillColor);
        if (Border != 0)
            p.Outline(gx, gy, W, H, dimmed ? DisabledBorder : BorderColor);
    }

    // Render children [code 0053a3f4]: each visible child in member order, clipped to the parent's clip.
    protected void RenderChildren(GuiPaint p, int gx, int gy)
    {
        foreach (GuiControl k in Children.ToArray())
        {
            if (!k.Visible)
                continue;
            GfxRect saved = p.Clip;
            GfxRect r = new(gx + k.X, gy + k.Y, gx + k.X + k.W - 1, gy + k.Y + k.H - 1);
            GfxRect c = saved.Intersect(r);
            if (c.IsEmpty)
                continue;
            p.SetClip(c);
            k.Render(p, gx, gy);
            p.SetClip(saved);
        }
    }

    // ---- responder defaults (2.7): hand the event to the next responder ----

    public IGuiResponder? Next => Parent != null ? Parent : Canvas?.ResponderAbove(this);

    public virtual bool MsDown(GuiEvent e) => Next?.MsDown(e) ?? false;
    public virtual bool MsRepeat(GuiEvent e) => Next?.MsRepeat(e) ?? false;
    public virtual bool MsUp(GuiEvent e) => Next?.MsUp(e) ?? false;
    public virtual bool MsMove(GuiEvent e) => Next?.MsMove(e) ?? false;
    public virtual bool MsEnter(GuiEvent e) => Next?.MsEnter(e) ?? false;
    public virtual bool MsLeave(GuiEvent e) => Next?.MsLeave(e) ?? false;
    public virtual bool MsDrag(GuiEvent e) => Next?.MsDrag(e) ?? false;
    public virtual bool KeyUp(GuiEvent e) => Next?.KeyUp(e) ?? false;
    public virtual bool KeyRepeat(GuiEvent e) => Next?.KeyRepeat(e) ?? false;
    public virtual bool KeyDown(GuiEvent e) => Next?.KeyDown(e) ?? false;
    public virtual bool Message(GuiControl? sender, int tag) => Next?.Message(sender, tag) ?? false;
    public virtual bool CanRelease() => true;
    public virtual bool CanTake() => false;

    // Broadcast of sibling message 3 [code 0053a684].
    public void BroadcastSibling(int tag)
    {
        if (Parent == null)
            return;
        foreach (GuiControl s in Parent.Children.ToArray())
            if (s != this)
                s.Message(this, tag);
    }

    public override string ToString() => $"{ClassName}{(Name != null ? " " + Name : "")} ({X},{Y} {W}x{H})";
}

// The clip and the primitives a control draws with; global canvas pixels.
public sealed class GuiPaint
{
    public readonly IGfxDevice Dev;
    public readonly GuiHost Host;
    public GfxRect Clip;

    public GuiPaint(IGfxDevice dev, GuiHost host)
    {
        Dev = dev;
        Host = host;
        Clip = new GfxRect(0, 0, dev.Width - 1, dev.Height - 1);
    }

    public void SetClip(GfxRect r)
    {
        Clip = r;
        Dev.SetClip(r);
    }

    public void Fill(int x, int y, int w, int h, int color, float alpha = 1f)
    {
        if (w > 0 && h > 0)
            Dev.FillRect(new GfxRect(x, y, x + w - 1, y + h - 1), color, alpha);
    }

    public void Outline(int x, int y, int w, int h, int color)
    {
        if (w > 0 && h > 0)
            Dev.DrawRectOutline(new GfxRect(x, y, x + w - 1, y + h - 1), color);
    }

    public void Line(int x0, int y0, int x1, int y1, int color) => Dev.DrawLine(x0, y0, x1, y1, color);

    public void Bitmap(PbmpBitmap? b, int x, int y)
    {
        if (b != null)
            Dev.Blit(b, x, y);
    }

    public void BitmapStretch(PbmpBitmap? b, int x, int y, int w, int h)
    {
        if (b != null && w > 0 && h > 0)
            Dev.BlitStretch(b, x, y, w, h);
    }

    public int Text(PftFont? f, int x, int y, string s)
    {
        if (f == null || s.Length == 0)
            return y;
        return Dev.DrawText(f, x, y, s);
    }

    public static int TextWidth(PftFont? f, string s) => f == null ? 0 : f.MeasureTextWidth(s);
    public static int FontHeight(PftFont? f) => f == null ? 0 : f.CellHeight;
}
