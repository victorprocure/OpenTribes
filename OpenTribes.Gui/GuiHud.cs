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

    protected PbmpBitmap? Bmp(int i, bool lr)
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

// FearGui::JetPackHud [code 004b0b64, loader 004b11d0]: the energy bar. Same frame as the health bar, but a fixed icon
// (HJ_Icon_Green), one end cap (HJ_End_Green) and the fixed palette colour 0xe7. Draw order: the frame at (x + 1, y + 1), the icon
// at (x + 5, y + 8), then, when the energy is above zero, the bar from (x + 37, y + 11), 84 pixels wide when full (low resolution
// x + 23, y + 7, 52 wide): the cap, the fill between and the mirrored cap, clipped to the energy length. The length per unit
// of energy is not read from the code; it is taken as the full width [I].
public sealed class GuiJetPackHud : GuiBarHud
{
    public const int BarColorIndex = 0xe7;

    protected override double Value => Source == null ? 0 : Source.EnergyLevel;
    protected override string[] Names => new[] { "hudtrans", "HJ_Icon_Green", "HJ_End_Green" };

    public static int EnergyBarLength(double value, bool lowRes)
    {
        int full = lowRes ? 52 : 84;
        int l = (int)(Math.Clamp(value, 0, 1) * full);
        if (l < 1 && value > 0)
            l = 1;
        return l;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Canvas == null || W > Canvas.Width || H > Canvas.Height)
            return;
        int x = ox + X, y = oy + Y;
        bool lr = LowRes;
        double value = Value;
        PbmpBitmap? frame = Bmp(0, lr);
        if (frame != null)
            p.Dev.Blit(frame, x + 1, y + 1, BlitFlip.Y);
        if (lr) p.Bitmap(Bmp(1, lr), x + 3, y + 4); else p.Bitmap(Bmp(1, lr), x + 5, y + 8);
        if (value <= 0)
            return;
        int l = EnergyBarLength(value, lr), full = lr ? 52 : 84;
        int bx = lr ? x + 23 : x + 37, by = lr ? y + 7 : y + 11;
        PbmpBitmap? cap = Bmp(2, lr);
        int ch = cap?.Height ?? 8, cw = cap?.Width ?? 0;
        GfxRect saved = p.Clip;
        p.SetClip(saved.Intersect(new GfxRect(bx, by, bx + l - 1, by + ch - 1)));
        p.Bitmap(cap, bx, by);
        p.Fill(bx + cw, by, full - 2 * cw, ch, BarColorIndex);
        if (cap != null)
            p.Dev.Blit(cap, bx + full - cw, by, BlitFlip.X);
        p.SetClip(saved);
    }
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
        p.Fill(cx - 6, cy, 5, 1, 0xfc);
        p.Fill(cx + 2, cy, 5, 1, 0xfc);
        p.Fill(cx, cy - 6, 1, 5, 0xfc);
        p.Fill(cx, cy + 2, 1, 5, 0xfc);
    }
}

// A bracket of the message window frames [code 004a5f98, 004a38f0]: a vertical rule at x in palette 0xf8, a second one beside it
// in 0xfc and a short horizontal stub at each end; `dir` is +4 for the left bracket and -4 for the right one.
public static class HudFrame
{
    public const int ShadeColor = 0, FaceColor = 0xfc, EdgeColor = 0xf8;
    public const float ShadeAlpha = 0.5f;

    public static void Bracket(GuiPaint p, int x, int top, int dir, int bottom)
    {
        int step = dir < 0 ? -1 : 1;
        p.Line(x, top, x, bottom, EdgeColor);
        p.Line(x + step, top, x + step, bottom, FaceColor);
        p.Line(x + step, top, x + step + dir, top, FaceColor);
        p.Line(x + step, bottom, x + step + dir, bottom, FaceColor);
    }

    // The translucent shade and both brackets over the rectangle (x, y, w, h).
    public static void Panel(GuiPaint p, int x, int y, int w, int h)
    {
        p.Fill(x, y, w, h, ShadeColor, ShadeAlpha);
        Bracket(p, x, y, 4, y + h - 1);
        Bracket(p, x + w - 1, y, -4, y + h - 1);
    }
}

// FearGui::FearGuiChatDisplay [code 004a633c]: the message window. With lines > 0 it draws a translucent shade over its extent
// and the two frame brackets (the `chatE` flag of the file is 0 in the stock play.gui), then the last N chat lines of the host's
// ring, wrapped to the extent minus the 4 and 2 pixel inset the draw routine applies.
public sealed class GuiChatDisplay : GuiHud
{
    public const int InsetX = 4, InsetY = 2;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null)
            return;
        int n = (int)U("lines", 3);
        if (n <= 0)
            return;
        int x = ox + X, y = oy + Y;
        if (U("chatE") == 0 || n > 3)
            p.Fill(x, y, W, H, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        if (U("chatE") == 0)
        {
            HudFrame.Bracket(p, x, y, 4, y + H - 1);
            HudFrame.Bracket(p, x + W - 1, y, -4, y + H - 1);
        }
        var lines = new List<(List<GuiMarkup.Run> Runs, PftFont Font)>();
        foreach (var (text, tag) in Host.Chat)
        {
            PftFont? f = Host.FontByTag(tag) ?? Host.FontByName("sf_white_10.pft");
            if (f == null)
                continue;
            foreach (var l in GuiMarkup.WrapText(Host, f, text, W - 2 * InsetX))
                lines.Add((l, f));
        }
        int start = Math.Max(0, lines.Count - n);
        int ty = y + InsetY;
        for (int i = start; i < lines.Count; ++i)
        {
            GuiMarkup.Draw(p, Host, lines[i].Font, x + InsetX, ty, lines[i].Runs);
            ty += GuiPaint.FontHeight(lines[i].Font) + 1;
        }
    }
}

// CenterPrint: Client::centerPrint(text, time) messages, centred, expiring after their time; `<fN>` markup as in the formatted text.
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
            foreach (var line in GuiMarkup.WrapText(Host, f, text, W))
            {
                GuiMarkup.Draw(p, Host, f, ox + X + (W - GuiMarkup.Width(Host, f, line)) / 2, y, line);
                y += GuiPaint.FontHeight(f) + 1;
            }
    }
}

// FearGui::FearGuiChat: the chat line, hidden until opened; Enter sends the text through its command, Escape cancels.
public sealed class GuiChat : GuiTextEdit
{
    public override bool WantsTab => Visible;

    // The stock chat line has font tag 0 and the highlight tag 150007 (play.gui); a tag that resolves to nothing falls back to the
    // highlight font, then to the HUD font, so the typed text is always drawn (docs/gui.md 3.3 TextEdit draw: the class text draw).
    protected override PftFont? Font =>
        Host?.FontByTag(U("fontTag")) ?? Host?.FontByTag(U("highlightFontTag")) ?? Host?.FontByName("sf_white_10.pft");

    // FearGui::FearGuiChat draw [code 004a3ebc, layout 004a3e68]: the line sits at the bottom centre of its parent, 40 pixels above the
    // bottom edge less one text line per wrapped line. A translucent shade with the two frame brackets covers the control, the
    // prompt is "SAY: " (channel 0) or "SAY TEAM: " (any other) in the channel's font, the typed text follows it and wraps below it,
    // and the cursor is a vertical bar in palette 0xfc that blinks.
    public const int Margin = 40, PadX = 12, PadY = 4;

    private bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    public static string Prompt(int channel) => channel < 1 ? "SAY: " : "SAY TEAM: ";

    private int Channel => Math.Clamp((int)Math.Truncate(Host == null ? 0 : ParseInt(Host.GetVar("$pref::msgChannel"))), 0, 31);

    private static double ParseInt(string s) =>
        int.TryParse(s.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;

    private PftFont? PromptFont(bool lowRes, int channel)
    {
        string name = lowRes ? (channel < 1 ? "sf_yellow_6.pft" : "sf_green_6.pft") : (channel < 1 ? "sf_yellow_10b.pft" : "if_g_10b.pft");
        return Host?.FontByName(name) ?? Host?.FontByName("sf_white_10.pft");
    }

    // The wrapped lines of the prompt plus text: the first line starts after the prompt, later ones at the left pad. Each entry is
    // (first character index, length).
    public static List<(int Start, int Length)> WrapLines(PftFont f, string prompt, string text, int width)
    {
        var lines = new List<(int, int)>();
        int avail = Math.Max(1, width - 2 * PadX);
        int used = GuiPaint.TextWidth(f, prompt), start = 0;
        for (int i = 0; i < text.Length; ++i)
        {
            int cw = f.CharWidth(text[i]) + 1;
            if (used + cw > avail && i > start)
            {
                lines.Add((start, i - start));
                start = i;
                used = 0;
            }
            used += cw;
        }
        lines.Add((start, text.Length - start));
        return lines;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Parent == null || Canvas == null)
            return;
        bool low = LowRes;
        int channel = Channel;
        PftFont? f = PromptFont(low, channel);
        if (f == null)
            return;
        string prompt = Prompt(channel);
        var lines = WrapLines(f, prompt, Text, W);
        int fh = GuiPaint.FontHeight(f);
        H = fh * lines.Count + 2 * PadY;
        X = (Parent.W - W) / 2;
        Y = Parent.H - Margin - fh * lines.Count;
        int x = ox + X, y = oy + Y;
        HudFrame.Panel(p, x, y, W, H);
        int ty = y + PadY - (low ? 0 : 2);
        int tx = x + PadX;
        bool focus = ReferenceEquals(Canvas.FirstResponder, this);
        bool blink = (Host.TickMs / 500 & 1) == 0;
        for (int i = 0; i < lines.Count; ++i)
        {
            int lx = tx;
            if (i == 0)
            {
                p.Text(f, lx, ty, prompt);
                lx += GuiPaint.TextWidth(f, prompt);
            }
            var (start, len) = lines[i];
            string seg = Text.Substring(start, len);
            p.Text(f, lx, ty, seg);
            bool last = i == lines.Count - 1;
            if (focus && blink && Cursor >= start && (Cursor < start + len || last && Cursor == start + len))
            {
                int cx = lx + GuiPaint.TextWidth(f, seg[..(Cursor - start)]);
                p.Line(cx, ty, cx, ty + fh - 1, HudFrame.FaceColor);
            }
            ty += fh;
        }
    }

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
            else if (text.Length > 0 && Host != null)
            {
                // The stock `.gui` files give the chat line no command: the engine sends the line to the channel the chat key
                // chose (client.cs `say(channel, message)`, `$pref::msgChannel`, client-scripts.md 3.10 CHAT).
                string channel = Host.GetVar("$pref::msgChannel");
                Host.Eval("say(" + (channel.Length > 0 ? channel : "0") + ", \"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\");");
            }
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
