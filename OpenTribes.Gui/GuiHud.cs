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

    // The stock client shows a HUD at the position stored in the file: the fractions only apply when a parent that
    // already has a size is resized (shot diff: weapon (0,213), clock (505,34) at 640 x 480).
    public override void Wake() { if (this is GuiCrosshair) Layout(); }

    public override void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        if (oldW > 0 && oldH > 0)
        {
            mFractions = true;
            Layout(newW, newH);
        }
    }

    private bool mFractions;

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
        if (!mFractions)
            return;
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

// FearGui::ClockHud [code 004e7744]: |timer| as %02d:%02d:%02d.%1d in a shaded box with a bracket on each side (stub length 2).
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
        int x = ox + X, y = oy + Y;
        p.Fill(x, y, W, H, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        HudFrame.Bracket(p, x, y, 2, y + H - 2);
        HudFrame.Bracket(p, x + W - 2, y, -2, y + H - 2);
        PftFont? f = Host?.FontByName(LowRes ? "sf_white_6.pft" : "if_w_10.pft");
        p.Text(f, x + 4, y + (LowRes ? 1 : 0), Format(Source.ClockSeconds));
    }
}

// FearGui::CompassHud [code 004e7e5c]: Compass.bmp, then the letters N, E, S and W and four ticks as polylines rotated by the
// control object's yaw (palette 0xfc, drawn around the bitmap centre), a fixed red needle at the top (0xf3) and the distance
// readout "000" in the middle. Each row of the tables is a polyline; the points are in 1/1 pixels of the 64 pixel bitmap (low
// resolution: the 36 pixel bitmap with its own table).
public sealed class GuiCompassHud : GuiHud
{
    private static readonly float[][] Glyphs =
    {
        new float[] { -2, -18, -2, -24, 2, -18, 2, -24 },
        new float[] { 18, 2, 18, -2, 24, -2, 24, 2 },
        new float[] { 21, -2, 21, 2 },
        new float[] { -24, 4, -18, 2, -24, 0, -18, -2, -24, -4 },
        new float[] { 2, 20, 1, 19, -1, 19, -2, 20, -2, 21, -1, 22, 1, 22, 2, 23, 2, 24, 1, 25, -1, 25, -2, 24 },
        new float[] { 15, -14, 19, -18 },
        new float[] { 15, 14, 19, 18 },
        new float[] { -14, 14, -18, 18 },
        new float[] { -14, -14, -18, -18 },
    };

    private static readonly float[][] GlyphsLow =
    {
        new float[] { -1.5f, -10.5f, -1.5f, -13.5f, 1.5f, -10.5f, 1.5f, -13.5f },
        new float[] { 9.5f, 0.5f, 9.5f, -1.5f, 13.5f, -1.5f, 13.5f, 0.5f },
        new float[] { 11.5f, -1.5f, 11.5f, -0.5f },
        new float[] { -13.5f, 2.5f, -11.5f, 2.5f, -10.5f, 1.5f, -11.5f, 1.5f, -10.5f, -0.5f, -11.5f, -1.5f, -13.5f, -1.5f },
        new float[] { 1.5f, 9.5f, 0.5f, 9.5f, -0.5f, 10.5f, 1.5f, 12.5f, 0.5f, 13.5f, -0.5f, 13.5f },
        new float[] { 7.5f, -7.5f, 9.5f, -9.5f },
        new float[] { 7.5f, 7.5f, 9.5f, 9.5f },
        new float[] { -7.5f, 7.5f, -9.5f, 9.5f },
        new float[] { -7.5f, -7.5f, -9.5f, -9.5f },
    };

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        bool lr = LowRes;
        int x = ox + X, y = oy + Y;
        p.Bitmap(Host?.BitmapByName(lr ? "lr_Compass.bmp" : "Compass.bmp"), x, y);
        double yaw = Source.Yaw;
        float sin = (float)Math.Sin(yaw), cos = (float)Math.Cos(yaw);
        float cx = x + (lr ? 18.5f : 31.5f), cy = y + (lr ? 18.5f : 31.5f);
        foreach (float[] line in lr ? GlyphsLow : Glyphs)
        {
            int n = line.Length / 2;
            for (int i = 0; i + 1 < n; ++i)
            {
                p.Line((int)(cx + line[2 * i] * cos - line[2 * i + 1] * sin), (int)(cy + line[2 * i + 1] * cos + line[2 * i] * sin),
                    (int)(cx + line[2 * i + 2] * cos - line[2 * i + 3] * sin), (int)(cy + line[2 * i + 3] * cos + line[2 * i + 2] * sin), 0xfc);
            }
        }
        int top = lr ? 17 : 28, bottom = lr ? 8 : 13;
        p.Line((int)cx, (int)cy - top, (int)cx, (int)cy - bottom, 0xf3);
        PftFont? f = Host?.FontByName(lr ? "sf_green_5.pft" : "if_g_8.pft");
        const string text = "000";
        p.Text(f, (int)cx - GuiPaint.TextWidth(f, text) / 2, (int)cy - GuiPaint.FontHeight(f) / 2, text);
    }
}

// FearGui::CurrentWeaponHud [code 004e8fe8]: a framed column with one icon per weapon the player carries and, right of it, the
// ammo count in a small dark box (the infinity icon when the weapon has no ammo item). The control resizes to the row count:
// width = the weapon icon width + 38, height = (icon height + 4) x rows + 10 (low resolution + 27, + 2).
public sealed class GuiWeaponHud : GuiHud
{
    private static readonly int[] Poly = { 2, 8, 8, 2, 24, 2, 30, 8 };
    private static readonly int[] PolyLow = { 1, 6, 6, 1, 16, 1, 18, 4 };
    private int mRows = -1;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Host == null)
            return;
        bool lr = LowRes;
        PbmpBitmap? fallback = Host.BitmapByName(lr ? "lr_weapon_off.bmp" : "I_weapon_off.bmp");
        int iconH = fallback?.Height ?? 24, iconW = fallback?.Width ?? 24;
        var rows = Source.Weapons;
        if (rows.Count != mRows)
        {
            mRows = rows.Count;
            W = iconW + (lr ? 27 : 38);
            H = rows.Count < 1 ? iconH : (iconH + (lr ? 2 : 4)) * rows.Count + 10;
            Layout();
            return;
        }
        if (rows.Count < 1)
            return;
        int x = ox + X, y = oy + Y, bottom = y + H - 1;
        int[] poly = lr ? PolyLow : Poly;
        var pts = new List<(int, int)>();
        for (int i = 0; i < 4; ++i)
            pts.Add((poly[2 * i] + x + (lr ? 1 : 0), poly[2 * i + 1] + y));
        for (int i = 3; i >= 0; --i)
            pts.Add((poly[2 * i] + x + (lr ? 1 : 0), y + H - poly[2 * i + 1]));
        HudFrame.FillPolygon(p, pts, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        int stub = lr ? 11 : 17;
        for (int k = 7; k <= 9; ++k)
        {
            p.Line(x, y + k, x + k, y, HudFrame.FaceColor);
            p.Line(x, bottom - k, x + k, bottom, HudFrame.FaceColor);
        }
        p.Line(x + 7, y, x + stub, y, HudFrame.FaceColor);
        p.Line(x + 7, y + 1, x + stub, y + 1, HudFrame.FaceColor);
        p.Line(x + 7, bottom, x + stub, bottom, HudFrame.FaceColor);
        p.Line(x + 7, bottom - 1, x + stub, bottom - 1, HudFrame.FaceColor);
        p.Line(x, y + 7, x, bottom - 7, HudFrame.FaceColor);
        p.Line(x + 1, y + 7, x + 1, bottom - 7, HudFrame.FaceColor);
        PftFont? dim = Host.FontByName(lr ? "sf_dk_green_5.pft" : "if_dg_8.pft"), lit = Host.FontByName(lr ? "sf_green_5.pft" : "if_g_8.pft");
        PbmpBitmap? box = Host.BitmapByName(lr ? "lr_ammo.bmp" : "ammoSh.bmp");
        int iy = y + (lr ? 6 : 7);
        foreach (WeaponRow w in rows)
        {
            string stem = (lr ? "lr_" : "I_") + w.Icon + (w.Active ? "_on" : "_off");
            PbmpBitmap? icon = Host.BitmapByName(stem + ".bmp") ?? fallback;
            p.BitmapKeyed(icon, x + 4, iy);
            int bx = x + 4 + (lr ? 3 : 4) + (icon?.Width ?? 0);
            p.Bitmap(box, bx, iy);
            bx += lr ? 3 : 2;
            int by = iy + (lr ? 0 : -1);
            PftFont? f = w.Active ? lit : dim;
            if (w.Ammo < 0)
                p.BitmapKeyed(Host.BitmapByName((lr ? "lr_" : "") + (w.Active ? "I_Infinity_on.bmp" : "I_Infinity_off.bmp")), bx + (lr ? 1 : 2), by + (lr ? 3 : 6));
            else
            {
                string t = Math.Min(w.Ammo, 999).ToString(CultureInfo.InvariantCulture);
                p.Text(f, bx + (GuiPaint.TextWidth(f, "888") - GuiPaint.TextWidth(f, t)) / 2, by, t);
            }
            iy += iconH + (lr ? 2 : 4);
        }
    }
}

// FearGui::FGHSensorPing [code 004ea720]: the frame bitmap `ping` is always drawn for the control player; the flags byte of the
// control object adds an overlay centred in the control: 2 the suppressed icon, 1 a cycle of the three ping icons.
public sealed class GuiSensorHud : GuiHud
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Host == null)
            return;
        bool lr = LowRes;
        int x = ox + X, y = oy + Y;
        p.Bitmap(Host.BitmapByName(lr ? "lr_ping.bmp" : "ping.bmp"), x, y);
        string? overlay = null;
        if (Source.SensorLocked)
            overlay = "HP_Supressed";
        else if (Source.SensorPing)
            overlay = ((Host.TickMs / 250) % 6) switch { 0 => "HP_PingedLo", 1 => "HP_PingedMed", _ => "HP_PingedHi" };
        if (overlay == null)
            return;
        PbmpBitmap? b = Host.BitmapByName((lr ? "LR_" : "") + overlay + ".bmp") ?? Host.BitmapByName(overlay + ".bmp");
        if (b != null)
            p.Bitmap(b, x + (W - b.Width) / 2, y + (H - b.Height) / 2);
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
        // centre of the view; four arms of 3 pixels, 2 pixels from the centre (stock capture at 640 x 480)
        int cx = ox + (Parent?.W ?? W) / 2, cy = oy + (Parent?.H ?? H) / 2;
        p.Fill(cx - 4, cy, 3, 1, 0xfc);
        p.Fill(cx + 2, cy, 3, 1, 0xfc);
        p.Fill(cx, cy - 4, 1, 3, 0xfc);
        p.Fill(cx, cy + 2, 1, 3, 0xfc);
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

    // Fills a convex polygon with one scan line per row (the translucent frames of the weapon HUD).
    public static void FillPolygon(GuiPaint p, IReadOnlyList<(int X, int Y)> pts, int color, float alpha)
    {
        int minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        for (int row = minY; row <= maxY; ++row)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < pts.Count; ++i)
            {
                var (ax, ay) = pts[i];
                var (bx, by) = pts[(i + 1) % pts.Count];
                if (ay == by)
                {
                    if (ay == row)
                    {
                        lo = Math.Min(lo, Math.Min(ax, bx));
                        hi = Math.Max(hi, Math.Max(ax, bx));
                    }
                    continue;
                }
                if (row < Math.Min(ay, by) || row > Math.Max(ay, by))
                    continue;
                double xs = ax + (double)(row - ay) * (bx - ax) / (by - ay);
                lo = Math.Min(lo, xs);
                hi = Math.Max(hi, xs);
            }
            if (hi >= lo)
                p.Fill((int)Math.Ceiling(lo), row, (int)Math.Floor(hi) - (int)Math.Ceiling(lo) + 1, 1, color, alpha);
        }
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
    // Text starts 4 + 6 pixels from the left (the 6 is half of the stock indent value 12, `00623478`) and the lines stack upward from
    // 6 pixels above the bottom, one font height apart [code 004a633c]. Measured on stock frames: play screen and inventory screen.
    public const int InsetX = 4, IndentX = 6, BottomInset = 6;
    // Height = lines * line height + 6 (DAT_00623474), see SetLines.
    public const int HeightPad = 6;

    // The font of a message type [code 004a5124, loaded by 004a5b54]: 0 white, 1 and 4 red, 3 green, any other yellow; the
    // low resolution set (canvas below 512 x 384) has the 6 point versions.
    public static string FontName(uint type, bool low) => type switch
    {
        0 => low ? "sf_white_6.pft" : "if_w_10b.pft",
        1 or 4 => low ? "sf_red_6.pft" : "sf_red_10b.pft",
        3 => low ? "sf_green_6.pft" : "if_g_10b.pft",
        _ => low ? "sf_yellow_6.pft" : "sf_yellow_10b.pft",
    };

    // Next line count of the size action with a negative value [code 004a7030]: 3, 5, 20, 3; a value from 0 sets it.
    public static int NextLines(int current, int value) => value >= 0 ? value : current switch { 3 => 5, 5 => 20, _ => 3 };

    // IDACTION_CHAT_DISP_SIZE: sets the line count and resizes the control to lines * line height + 6 (the top stays).
    public void SetLines(int value)
    {
        int n = NextLines((int)U("lines", 3), value);
        F["lines"] = (uint)n;
        PftFont? f = Host?.FontByName(FontName(0, Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384))) ?? Host?.FontByName("sf_white_10.pft");
        H = n * (f == null ? 13 : GuiPaint.FontHeight(f)) + HeightPad;
    }

    // The player chat menu under the message window [code 004a633c, after the frame]: drawn when `chatE` is 0 and the menu is on, with
    // the heading and the items in if_g_10b one font height apart, starting right below the control (its top plus its height). When the list would
    // reach the bottom of the screen it sits above the control instead, 8 pixels clear of it.
    private void RenderMenu(GuiPaint p, int x, int y)
    {
        if (Host?.Hud?.PlayMenu is not PlayMenuView menu || U("chatE") != 0)
            return;
        PftFont? f = Host?.FontByName("if_g_10b.pft");
        if (f == null)
            return;
        int h = GuiPaint.FontHeight(f);
        int total = h * (menu.Lines.Count + 1);
        int ty = y + H;
        int screenH = Canvas?.Height ?? p.Dev.Height;
        if (screenH <= ty + total)
            ty = y - total - 8;
        // The original sets its clip to the whole surface first [code 004a633c]: the list lies outside the control's own rectangle.
        GfxRect saved = p.Clip;
        p.SetClip(new GfxRect(0, 0, p.Dev.Width - 1, p.Dev.Height - 1));
        p.Text(f, x, ty, menu.Heading);
        ty += h;
        foreach (string line in menu.Lines)
        {
            p.Text(f, x, ty, line);
            ty += h;
        }
        p.SetClip(saved);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null)
            return;
        RenderMessages(p, ox, oy);
        RenderMenu(p, ox + X, oy + Y);
    }

    private void RenderMessages(GuiPaint p, int ox, int oy)
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
        bool low = Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
        foreach (var (text, tag) in Host.Chat)
        {
            PftFont? f = Host.FontByName(FontName(tag, low)) ?? Host.FontByName("sf_white_10.pft");
            if (f == null)
                continue;
            foreach (var l in GuiMarkup.WrapText(Host, f, text, W - 2 * InsetX))
                lines.Add((l, f));
        }
        // The command line takes the bottom of the display (one line of the count) while the local player holds a command.
        var cmdLines = new List<List<GuiMarkup.Run>>();
        PftFont? cmdFont = null;
        if (Host.CommandActive && Host.CommandText.Length > 0)
        {
            cmdFont = Host.FontByName(FontName(Host.CommandAccepted ? 3u : 4u, low)) ?? Host.FontByName("sf_white_10.pft");
            if (cmdFont != null)
                cmdLines.AddRange(GuiMarkup.WrapText(Host, cmdFont, Host.CommandText, W - 2 * InsetX));
            n = Math.Max(0, n - 1);
        }
        int start = Math.Max(0, lines.Count - n);
        int ty = y + H - BottomInset;
        if (cmdFont != null)
            ty -= cmdLines.Count * GuiPaint.FontHeight(cmdFont);
        for (int i = start; i < lines.Count; ++i)
            ty -= GuiPaint.FontHeight(lines[i].Font);
        for (int i = start; i < lines.Count; ++i)
        {
            GuiMarkup.Draw(p, Host, lines[i].Font, x + InsetX + IndentX, ty, lines[i].Runs);
            ty += GuiPaint.FontHeight(lines[i].Font);
        }
        if (cmdFont != null)
            foreach (var l in cmdLines)
            {
                GuiMarkup.Draw(p, Host, cmdFont, x + InsetX + IndentX, ty, l);
                ty += GuiPaint.FontHeight(cmdFont);
            }
    }
}

// CenterPrint [code 00488f80 prepare, 00489108 draw]: the message of Client::centerPrint in a shaded box with a bracket on each
// side (stub length 4). The box is the parent's width less 70 (low resolution 30) wide at x 35 (15), as high as the wrapped text plus
// 8, and sits by the message type: 0 vertically centred, 1 35 (15) above the bottom, 2 35 (15) below the top. The text is wrapped to
// the box less 4 and drawn 2 pixels inside with the fonts f0, f1 and f2 of the control (sf_orange214_10, sf_orange255_10,
// sf_white_9b; low resolution sf_orange_6, sf_yellow_6, sf_white_6); `<jc>` centres the lines.
public sealed class GuiCenterPrint : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Parent == null || Host.CenterText.Length == 0 || Host.CenterType is < 0 or > 2)
            return;
        int pw = Parent.W, ph = Parent.H;
        bool low = pw < 512 || ph < 384;
        string[] names = low ? new[] { "sf_orange_6.pft", "sf_yellow_6.pft", "sf_white_6.pft" }
            : new[] { "sf_orange214_10.pft", "sf_orange255_10.pft", "sf_white_9b.pft" };
        var fonts = names.Select(n => Host.FontByName(n)).ToArray();
        PftFont? def = fonts[0] ?? Host.FontByName("sf_white_10.pft");
        if (def == null)
            return;
        PftFont? FontOf(int n) => n is >= 0 and <= 2 ? fonts[n] : null;
        int w = pw - (low ? 30 : 70);
        string text = Host.CenterText;
        var lines = GuiMarkup.WrapText(Host, def, text, w - 4, FontOf);
        int lh = GuiPaint.FontHeight(def);
        int h = lines.Count * lh + 8;
        int bx = low ? 15 : 35;
        int by = Host.CenterType == 0 ? (ph - h) / 2 : Host.CenterType == 1 ? ph - h - (low ? 15 : 35) : (low ? 15 : 35);
        X = bx; Y = by; W = w; H = h;
        int x = ox + bx, y = oy + by;
        p.Fill(x, y, w, h, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        HudFrame.Bracket(p, x, y, 4, y + h - 1);
        HudFrame.Bracket(p, x + w - 1, y, -4, y + h - 1);
        bool center = text.Contains("<jc>", StringComparison.Ordinal);
        int ty = y + 2;
        foreach (var line in lines)
        {
            int lw = GuiMarkup.Width(Host, def, line, FontOf);
            GuiMarkup.Draw(p, Host, def, x + 2 + (center ? (w - 4 - lw) / 2 : 0), ty, line, FontOf);
            ty += lh;
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
