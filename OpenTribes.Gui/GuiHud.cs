// docs/gui.md 4: HudCtrl and the HUD classes, the chat display, the chat line and CenterPrint.
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// FearGui::HudCtrl [code 004afa40, 004afc9c, 004afb8c]: position stored as two fractions of the free parent area.
public class GuiHud : GuiControl
{
    public float FracX => BitConverter.UInt32BitsToSingle(U("hudX"));
    public float FracY => BitConverter.UInt32BitsToSingle(U("hudY"));
    protected IHudSource? Source => Host?.Hud;

    public override void Wake() => Layout();

    public override void ParentResized(int oldW, int oldH, int newW, int newH) => Layout(newW, newH);

    protected void Layout(int pw = -1, int ph = -1)
    {
        if (Parent == null)
            return;
        if (this is GuiCrosshair)
        {
            X = (Parent.W - W) / 2;
            Y = (Parent.H - H) / 2;
            return;
        }
        if (pw < 0) { pw = Parent.W; ph = Parent.H; }
        X = (int)(FracX * (pw - W));
        Y = (int)(FracY * (ph - H));
    }

    // After a drag: keep the control at least 8 pixels inside the parent and recompute the fractions [code 004afb8c].
    public void Clamp()
    {
        if (Parent == null)
            return;
        int ex = Parent.W - W, ey = Parent.H - H;
        if (X < 8) X = 0; else if (X > ex - 8) X = ex;
        if (Y < 8) Y = 0; else if (Y > ey - 8) Y = ey;
        F["hudX"] = BitConverter.SingleToUInt32Bits(ex > 0 ? (float)X / ex : 0f);
        F["hudY"] = BitConverter.SingleToUInt32Bits(ey > 0 ? (float)Y / ey : 0f);
    }

    protected bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    // The hit test of a HUD is not a stock behaviour that matters in play: HUDs do not take the mouse.
    protected override bool PickInside(int lx, int ly) => false;
}

// FearGui::HealthHud and JetPackHud [code 004e6ab4, constants 004e70a4..004e70b0]: the bar HUD.
public class GuiBarHud : GuiHud
{
    public static readonly string FrameName = "hud_health.bmp";
    private long mBlinkClock;

    // The traced geometry: bar length in pixels, the colour, the icon choice.
    public static int BarLength(double value, bool lowRes)
    {
        int full = lowRes ? 51 : 85;
        int l = (int)(value * full);
        if (l < 1 && value > 0)
            l = 1;
        return l;
    }

    public static int BarColor(int l, bool lowRes)
    {
        int third = lowRes ? 17 : 28;
        return l >= 2 * third + (lowRes ? 0 : 1) ? 0xfc : l >= third ? 0xf2 : 0xf3;
    }

    protected virtual double Value => Source == null ? 0 : 1.0 - Source.DamageLevel;
    protected virtual string[] Names => new[] { "hudtrans", "HH_Icon_Green", "HH_Icon_Red", "HH_End_Green", "HH_End_Yellow", "HH_End_Red" };

    private PbmpBitmap? Bmp(int i, bool lr)
    {
        string[] n = Names;
        return n[i].Length == 0 ? null : Host?.BitmapByName((lr ? "lr_" : "") + n[i] + ".bmp") ?? Host?.BitmapByName(n[i] + ".bmp");
    }

    // Resolution switch: extent from the frame bitmap.
    public override void PreRender()
    {
        PbmpBitmap? frame = Bmp(0, LowRes);
        if (frame == null)
            return;
        int w = LowRes ? frame.Width * 6 / 10 + 2 : frame.Width + 3, h = LowRes ? frame.Height * 6 / 10 + 2 : frame.Height + 3;
        if (w != W || h != H)
        {
            W = w; H = h;
            Layout();
        }
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Canvas == null || W > Canvas.Width || H > Canvas.Height)
            return;
        int x = ox + X, y = oy + Y;
        bool lr = LowRes;
        double value = Value;
        int l = BarLength(value, lr);
        p.Bitmap(Bmp(0, lr), x + 1, y + 1);
        int goodMin = lr ? 17 : 28;
        PbmpBitmap? icon;
        if (l >= goodMin)
            icon = Bmp(1, lr);
        else if (l == 0)
            icon = Bmp(2, lr);
        else
        {
            if (mBlinkClock == 0)
                mBlinkClock = Host!.TickMs;
            icon = (((Host!.TickMs - mBlinkClock) >> 9) & 1) == 0 ? Bmp(2, lr) : Bmp(1, lr);
        }
        if (lr) p.Bitmap(icon, x + 4, y + 4); else p.Bitmap(icon, x + 5, y + 3);
        if (value > 0)
        {
            int ox2 = lr ? x + 23 : x + 37, oy2 = lr ? y + 7 : y + 10, full = lr ? 52 : 84;
            int color = BarColor(l, lr);
            PbmpBitmap? cap = Bmp(color == 0xfc ? 3 : color == 0xf2 ? 4 : 5, lr);
            int ch = cap?.Height ?? 8;
            GfxRect saved = p.Clip;
            p.SetClip(saved.Intersect(new GfxRect(ox2, oy2, ox2 + l - 1, oy2 + ch - 1)));
            int cw = cap?.Width ?? 0;
            p.Bitmap(cap, ox2, oy2);
            p.Fill(ox2 + cw, oy2, full - 2 * cw, ch, color);
            if (cap != null)
                p.Dev.Blit(cap, ox2 + full - cw, oy2, BlitFlip.X);
            p.SetClip(saved);
        }
    }
}

public sealed class GuiHealthHud : GuiBarHud { }

public sealed class GuiJetPackHud : GuiBarHud
{
    protected override double Value => Source == null ? 0 : Source.EnergyLevel;
    protected override string[] Names => new[] { "hudtrans", "HH_Icon_Green", "HH_Icon_Red", "HH_End_Green", "HH_End_Yellow", "HH_End_Red" };
}

// FearGui::ClockHud [code 004e7744]: |timer| as %02d:%02d:%02d.%1d.
public sealed class GuiClockHud : GuiHud
{
    public static string Format(double seconds)
    {
        double t = Math.Abs(seconds);
        long whole = (long)t;
        int tenths = (int)((t - whole) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}.{tenths}");
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        PftFont? f = Host?.FontByName("sf_white_10.pft");
        p.Text(f, ox + X + 2, oy + Y + 2, Format(Source.ClockSeconds));
    }
}

// FearGui::CompassHud [code 004e7e5c]: Compass.bmp and the needles of the control object's yaw. The polygon tables
// are not read; a north needle and an east marker are drawn with lines.
public sealed class GuiCompassHud : GuiHud
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        int x = ox + X, y = oy + Y;
        p.Bitmap(Host?.BitmapByName(LowRes ? "lr_Compass.bmp" : "Compass.bmp"), x, y);
        double yaw = Source.Yaw;
        int cx = x + W / 2, cy = y + H / 2, r = Math.Min(W, H) / 2 - 6;
        double s = Math.Sin(yaw), c = Math.Cos(yaw);
        p.Line(cx, cy, cx + (int)(-s * r), cy - (int)(c * r), 0xfc);
        p.Line(cx, cy, cx + (int)(c * r / 2), cy + (int)(s * r / 2), 0xf3);
    }
}

// FearGui::CurrentWeaponHud [code 004e8fe8]: the weapons the player carries, the mounted one marked, and its ammo.
public sealed class GuiWeaponHud : GuiHud
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        PftFont? f = Host?.FontByName("sf_white_10.pft");
        int x = ox + X, y = oy + Y;
        foreach (var (name, mounted) in Source.Weapons)
        {
            if (mounted)
                p.Fill(x, y, W, GuiPaint.FontHeight(f) + 2, 0xf8);
            p.Text(f, x + 2, y + 1, name);
            y += GuiPaint.FontHeight(f) + 2;
        }
        string ammo = Source.Ammo < 0 ? "inf" : Source.Ammo.ToString(CultureInfo.InvariantCulture);
        p.Text(f, x + 2, y + 2, ammo);
    }
}

// FearGui::FGHSensorPing [code 004ea720]: value 1 ping (blinking), value 2 locked.
public sealed class GuiSensorHud : GuiHud
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || !Source.SensorPing && !Source.SensorLocked)
            return;
        bool blink = ((Host?.TickMs ?? 0) >> 8 & 1) == 0;
        if (Source.SensorLocked)
            p.Bitmap(Host?.BitmapByName("I_Locked.bmp"), ox + X, oy + Y);
        else if (blink)
            p.Bitmap(Host?.BitmapByName(LowRes ? "lr_ping.bmp" : "I_PingRed.bmp"), ox + X, oy + Y);
    }
}

// FearGui::Crosshair [code 004ab500]: a cross at the centre of the view.
public sealed class GuiCrosshair : GuiHud
{
    public override void Wake()
    {
        W = H = 13;
        Layout();
    }

    public override void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        X = (newW - W) / 2;
        Y = (newH - H) / 2;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Canvas == null)
            return;
        int cx = ox + X + W / 2, cy = oy + Y + H / 2;
        p.Line(cx - 6, cy, cx - 2, cy, 0xfc);
        p.Line(cx + 2, cy, cx + 6, cy, 0xfc);
        p.Line(cx, cy - 6, cx, cy - 2, 0xfc);
        p.Line(cx, cy + 2, cx, cy + 6, 0xfc);
    }
}

// FearGui::FearGuiChatDisplay [code 004a633c]: the last N chat lines of the host's ring, wrapped to the extent.
public sealed class GuiChatDisplay : GuiHud
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null)
            return;
        int n = Math.Max(1, (int)U("lines", 3));
        var lines = new List<(string, PftFont)>();
        foreach (var (text, tag) in Host.Chat)
        {
            PftFont? f = Host.FontByTag(tag) ?? Host.FontByName("sf_white_10.pft");
            if (f == null)
                continue;
            foreach (string l in GuiTextWrap.Wrap(f, text, W))
                lines.Add((l, f));
        }
        int start = Math.Max(0, lines.Count - n);
        int y = oy + Y;
        for (int i = start; i < lines.Count; ++i)
        {
            p.Text(lines[i].Item2, ox + X, y, lines[i].Item1);
            y += GuiPaint.FontHeight(lines[i].Item2) + 1;
        }
    }
}

// CenterPrint: Client::centerPrint(text, time) messages, centred, expiring after their time.
public sealed class GuiCenterPrint : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null)
            return;
        Host.CenterPrints.RemoveAll(c => c.Until <= Host.TickMs);
        PftFont? f = Host.FontByName("sf_white_10.pft");
        int y = oy + Y;
        foreach (var (text, _) in Host.CenterPrints)
        {
            p.Text(f, ox + X + (W - GuiPaint.TextWidth(f, text)) / 2, y, text);
            y += GuiPaint.FontHeight(f) + 1;
        }
    }
}

// FearGui::FearGuiChat: the chat line, hidden until opened; Enter sends the text through its command, Escape cancels.
public sealed class GuiChat : GuiTextEdit
{
    public override bool WantsTab => Visible;

    public void Open()
    {
        SetVisible(true);
        Canvas?.RebuildTabList();
        Canvas?.SetFirstResponder(this);
    }

    private void Close2()
    {
        Text = "";
        Cursor = 0;
        SetVisible(false);
        Canvas?.SetFirstResponder(Canvas.Root);
        Canvas?.RebuildTabList();
    }

    public override bool KeyDown(GuiEvent e)
    {
        if (e.Scan == 0x01)
        {
            Close2();
            return true;
        }
        if (e.Scan == 0x1c)
        {
            string text = Text;
            WriteVariable(text);
            if (Command.Length > 0)
                Host?.Eval(Command);
            Close2();
            return true;
        }
        return base.KeyDown(e);
    }

    public override bool KeyUp(GuiEvent e) => true;
}

// SimGui::TSControl: the 3D view. The world is drawn by the world renderer before the GUI; this control draws its
// children (the HUD) over it. docs/gui.md 3.3 "The 3D play view".
public sealed class GuiPlayView : GuiControl
{
    public bool Produce3dEvents => U("produce3dEvents") != 0;
}
