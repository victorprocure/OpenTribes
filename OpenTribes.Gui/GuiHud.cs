namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;

/// <summary>The base HUD control (FearGui::HudCtrl, 004afa40, 004afc9c, 004afb8c): the position is stored as two fractions of the free parent area (docs/gui.md 4).</summary>
public class GuiHud : GuiControl
{
    /// <summary>Gets the horizontal position as a fraction of the free parent width.</summary>
    public float FracX => BitConverter.UInt32BitsToSingle(U("hudX"));
    /// <summary>Gets the vertical position as a fraction of the free parent height.</summary>
    public float FracY => BitConverter.UInt32BitsToSingle(U("hudY"));
    /// <summary>Gets the HUD data source of the host, or null.</summary>
    protected IHudSource? Source => Host?.Hud;

    /// <inheritdoc/>
    /// <remarks>The stock client shows a HUD at the position stored in the file: the fractions only apply when a parent that already has a size is resized.</remarks>
    public override void Wake() { if (this is GuiCrosshair) Layout(); }

    /// <inheritdoc/>
    public override void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        if (oldW > 0 && oldH > 0)
        {
            mFractions = true;
            Layout(newW, newH);
        }
    }

    private bool mFractions;

    /// <summary>Places the HUD from its fractions, or centres the crosshair.</summary>
    /// <param name="pw">The parent width, -1 for the current one.</param>
    /// <param name="ph">The parent height, -1 for the current one.</param>
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
        if (pw < 0)
        { pw = Parent.W; ph = Parent.H; }
        X = (int)(FracX * (pw - W));
        Y = (int)(FracY * (ph - FractionH));
    }

    /// <summary>Gets the height the fractions see: the extent at the time of the parent resize (004afc9c). The weapon HUD has none before its rows exist.</summary>
    protected virtual int FractionH => H;

    /// <summary>Applies the size switch of a HUD (004b0b64, 004e6ab4): after a resize of the parent the stock draw keeps the position the fractions gave (they are not applied again) and only pulls it back inside the parent.</summary>
    protected void KeepInside()
    {
        if (mFractions)
            Clamp();
    }

    /// <summary>Keeps the control at least 8 pixels inside the parent and recomputes the fractions, as after a drag (004afb8c).</summary>
    public void Clamp()
    {
        if (Parent == null)
            return;
        int ex = Parent.W - W, ey = Parent.H - H;
        if (X < 8)
            X = 0;
        else if (X > ex - 8)
            X = ex;
        if (Y < 8)
            Y = 0;
        else if (Y > ey - 8)
            Y = ey;
        F["hudX"] = BitConverter.SingleToUInt32Bits(ex > 0 ? (float)X / ex : 0f);
        F["hudY"] = BitConverter.SingleToUInt32Bits(ey > 0 ? (float)Y / ey : 0f);
    }

    /// <summary>Gets a value that is true when the canvas is below 512 x 384.</summary>
    protected bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    /// <inheritdoc/>
    /// <remarks>The hit test of a HUD is not a stock behaviour that matters in play: HUDs do not take the mouse.</remarks>
    protected override bool PickInside(int lx, int ly) => false;
}

/// <summary>The bar HUD (FearGui::HealthHud and JetPackHud, 004e6ab4, constants 004e70a4 to 004e70b0).</summary>
public class GuiBarHud : GuiHud
{
    /// <summary>The file name of the frame bitmap.</summary>
    public static readonly string FrameName = "hud_health.bmp";
    private long mBlinkClock;

    /// <summary>Returns the bar length in pixels.</summary>
    /// <param name="value">The value, 0 to 1.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <returns>The length, at least 1 for a value above 0.</returns>
    public static int BarLength(double value, bool lowRes)
    {
        var full = lowRes ? 51 : 85;
        var l = (int)(value * full);
        if (l < 1 && value > 0)
            l = 1;
        return l;
    }

    /// <summary>Returns the bar color.</summary>
    /// <param name="l">The bar length in pixels.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <returns>The palette index: green, yellow or red by the third of the bar.</returns>
    public static int BarColor(int l, bool lowRes)
    {
        var third = lowRes ? 17 : 28;
        return l >= (2 * third) + (lowRes ? 0 : 1) ? 0xfc : l >= third ? 0xf2 : 0xf3;
    }

    /// <summary>Gets the value the bar shows, 0 to 1.</summary>
    protected virtual double Value => Source == null ? 0 : 1.0 - Source.DamageLevel;
    /// <summary>Gets the names of the bitmaps: frame, icons and end caps.</summary>
    protected virtual string[] Names => ["hudtrans", "HH_Icon_Green", "HH_Icon_Red", "HH_End_Green", "HH_End_Yellow", "HH_End_Red"];

    /// <summary>Returns a bitmap of <see cref="Names"/>.</summary>
    /// <param name="i">The index.</param>
    /// <param name="lr">True for the low resolution version.</param>
    /// <returns>The bitmap, or null.</returns>
    protected PbmpBitmap? Bmp(int i, bool lr)
    {
        var n = Names;
        return n[i].Length == 0 ? null : Host?.BitmapByName((lr ? "lr_" : "") + n[i] + ".bmp") ?? Host?.BitmapByName(n[i] + ".bmp");
    }

    /// <inheritdoc/>
    /// <remarks>The resolution switch takes the extent from the frame bitmap.</remarks>
    public override void PreRender()
    {
        var frame = Bmp(0, LowRes);
        if (frame == null)
            return;
        int w = LowRes ? (frame.Width * 6 / 10) + 2 : frame.Width + 3, h = LowRes ? (frame.Height * 6 / 10) + 2 : frame.Height + 3;
        if (w != W || h != H)
        {
            W = w;
            H = h;
            KeepInside();
        }
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Canvas == null || W > Canvas.Width || H > Canvas.Height)
            return;
        int x = ox + X, y = oy + Y;
        var lr = LowRes;
        var value = Value;
        var l = BarLength(value, lr);
        p.Bitmap(Bmp(0, lr), x + 1, y + 1);
        var goodMin = lr ? 17 : 28;
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
        if (lr)
            p.Bitmap(icon, x + 4, y + 4);
        else
            p.Bitmap(icon, x + 5, y + 3);
        if (value > 0)
        {
            int ox2 = lr ? x + 23 : x + 37, oy2 = lr ? y + 7 : y + 10, full = lr ? 52 : 84;
            var color = BarColor(l, lr);
            var cap = Bmp(color == 0xfc ? 3 : color == 0xf2 ? 4 : 5, lr);
            var ch = cap?.Height ?? 8;
            var saved = p.Clip;
            p.SetClip(saved.Intersect(new GfxRect(ox2, oy2, ox2 + l - 1, oy2 + ch - 1)));
            var cw = cap?.Width ?? 0;
            p.Bitmap(cap, ox2, oy2);
            p.Fill(ox2 + cw, oy2, full - (2 * cw), ch, color);
            if (cap != null)
                p.Dev.Blit(cap, ox2 + full - cw, oy2, BlitFlip.X);
            p.SetClip(saved);
        }
    }
}

/// <summary>The health bar HUD (FearGui::HealthHud).</summary>
public sealed class GuiHealthHud : GuiBarHud { }

/// <summary>The energy bar (FearGui::JetPackHud, 004b0b64, loader 004b11d0).</summary>
/// <remarks>The same frame as the health bar, but a fixed icon (HJ_Icon_Green), one end cap (HJ_End_Green) and the fixed palette color 0xe7. Draw order: the frame at (x + 1, y + 1), the icon at (x + 5, y + 8), then, when the energy is above zero, the bar from (x + 37, y + 11), 84 pixels wide when full (low resolution x + 23, y + 7, 52 wide): the cap, the fill between and the mirrored cap, clipped to the energy length. The length per unit of energy is not read from the code: it is taken as the full width.</remarks>
public sealed class GuiJetPackHud : GuiBarHud
{
    /// <summary>The palette index of the bar.</summary>
    public const int BarColorIndex = 0xe7;

    /// <inheritdoc/>
    protected override double Value => Source == null ? 0 : Source.EnergyLevel;
    /// <inheritdoc/>
    protected override string[] Names => ["hudtrans", "HJ_Icon_Green", "HJ_End_Green"];

    /// <summary>Returns the energy bar length in pixels.</summary>
    /// <param name="value">The energy, 0 to 1.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <returns>The length, at least 1 for a value above 0.</returns>
    public static int EnergyBarLength(double value, bool lowRes)
    {
        var full = lowRes ? 52 : 84;
        var l = (int)(Math.Clamp(value, 0, 1) * full);
        if (l < 1 && value > 0)
            l = 1;
        return l;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState || Canvas == null || W > Canvas.Width || H > Canvas.Height)
            return;
        int x = ox + X, y = oy + Y;
        var lr = LowRes;
        var value = Value;
        var frame = Bmp(0, lr);
        if (frame != null)
            p.Dev.Blit(frame, x + 1, lr ? y - 11 : y + 1, BlitFlip.Y);   // low resolution: 12 higher [code 004b0b64]
        if (lr)
            p.Bitmap(Bmp(1, lr), x + 3, y + 4);
        else
            p.Bitmap(Bmp(1, lr), x + 5, y + 8);
        if (value <= 0)
            return;
        int l = EnergyBarLength(value, lr), full = lr ? 52 : 84;
        int bx = lr ? x + 23 : x + 37, by = lr ? y + 7 : y + 11;
        var cap = Bmp(2, lr);
        int ch = cap?.Height ?? 8, cw = cap?.Width ?? 0;
        var saved = p.Clip;
        p.SetClip(saved.Intersect(new GfxRect(bx, by, bx + l - 1, by + ch - 1)));
        p.Bitmap(cap, bx, by);
        p.Fill(bx + cw, by, full - (2 * cw), ch, BarColorIndex);
        if (cap != null)
            p.Dev.Blit(cap, bx + full - cw, by, BlitFlip.X);
        p.SetClip(saved);
    }
}

/// <summary>The clock (FearGui::ClockHud, 004e7744): |timer| as %02d:%02d:%02d.%1d in a shaded box with a bracket on each side (stub length 2).</summary>
public sealed class GuiClockHud : GuiHud
{
    /// <summary>Formats the timer.</summary>
    /// <param name="seconds">The timer value.</param>
    /// <returns>The absolute value as hours:minutes:seconds.tenths.</returns>
    public static string Format(double seconds)
    {
        var t = Math.Abs(seconds);
        var whole = (long)t;
        var tenths = (int)((t - whole) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}.{tenths}");
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        int x = ox + X, y = oy + Y;
        p.Fill(x, y, W, H, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        // 004e7744: 004a5f98(x, y, 2, bottom - 1) and 004a5f98(right - 1, y, -2, bottom - 1).
        HudFrame.Bracket(p, x, y, 2, y + H - 1);
        HudFrame.Bracket(p, x + W - 1, y, -2, y + H - 1);
        var f = Host?.FontByName(LowRes ? "sf_white_6.pft" : "if_w_10.pft");
        p.Text(f, x + 4, y + (LowRes ? 1 : 0), Format(Source.ClockSeconds));
    }
}

/// <summary>The compass (FearGui::CompassHud, 004e7e5c).</summary>
/// <remarks>It draws Compass.bmp, then the letters N, E, S and W and four ticks as polylines rotated by the control object's yaw (palette 0xfc, drawn around the bitmap centre), a fixed red needle at the top (0xf3) and the distance readout in the middle: "000", or with a waypoint the distance and a filled arrow (palette 0xfc) toward it. Each row of the tables is a polyline. The points are in 1/1 pixels of the 64 pixel bitmap (low resolution: the 36 pixel bitmap with its own table).</remarks>
public sealed class GuiCompassHud : GuiHud
{
    private static readonly float[][] Glyphs =
    [
        [-2, -18, -2, -24, 2, -18, 2, -24],
        [18, 2, 18, -2, 24, -2, 24, 2],
        [21, -2, 21, 2],
        [-24, 4, -18, 2, -24, 0, -18, -2, -24, -4],
        [2, 20, 1, 19, -1, 19, -2, 20, -2, 21, -1, 22, 1, 22, 2, 23, 2, 24, 1, 25, -1, 25, -2, 24],
        [15, -14, 19, -18],
        [15, 14, 19, 18],
        [-14, 14, -18, 18],
        [-14, -14, -18, -18],
    ];

    private static readonly float[][] GlyphsLow =
    [
        [-1.5f, -10.5f, -1.5f, -13.5f, 1.5f, -10.5f, 1.5f, -13.5f],
        [9.5f, 0.5f, 9.5f, -1.5f, 13.5f, -1.5f, 13.5f, 0.5f],
        [11.5f, -1.5f, 11.5f, -0.5f],
        [-13.5f, 2.5f, -11.5f, 2.5f, -10.5f, 1.5f, -11.5f, 1.5f, -10.5f, -0.5f, -11.5f, -1.5f, -13.5f, -1.5f],
        [1.5f, 9.5f, 0.5f, 9.5f, -0.5f, 10.5f, 1.5f, 12.5f, 0.5f, 13.5f, -0.5f, 13.5f],
        [7.5f, -7.5f, 9.5f, -9.5f],
        [7.5f, 7.5f, 9.5f, 9.5f],
        [-7.5f, 7.5f, -9.5f, 9.5f],
        [-7.5f, -7.5f, -9.5f, -9.5f],
    ];

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.HasPlayerState)
            return;
        var lr = LowRes;
        int x = ox + X, y = oy + Y;
        p.Bitmap(Host?.BitmapByName(lr ? "lr_Compass.bmp" : "Compass.bmp"), x, y);
        var yaw = Source.Yaw;
        float sin = (float)Math.Sin(yaw), cos = (float)Math.Cos(yaw);
        float cx = x + (lr ? 18.5f : 31.5f), cy = y + (lr ? 18.5f : 31.5f);
        foreach (var line in lr ? GlyphsLow : Glyphs)
        {
            var n = line.Length / 2;
            for (var i = 0; i + 1 < n; ++i)
            {
                p.Line((int)(cx + (line[2 * i] * cos) - (line[(2 * i) + 1] * sin)), (int)(cy + (line[(2 * i) + 1] * cos) + (line[2 * i] * sin)),
                    (int)(cx + (line[(2 * i) + 2] * cos) - (line[(2 * i) + 3] * sin)), (int)(cy + (line[(2 * i) + 3] * cos) + (line[(2 * i) + 2] * sin)), 0xfc);
            }
        }
        int top = lr ? 17 : 28, bottom = lr ? 8 : 13;
        p.Line((int)cx, (int)cy - top, (int)cx, (int)cy - bottom, 0xf3);
        var f = Host?.FontByName(lr ? "sf_green_5.pft" : "if_g_8.pft");
        var wp = Source.Waypoint;
        var text = Readout(wp);
        if (wp is HudWaypoint w && text != "000")
        {
            // 004e7e5c: the arrow is a filled triangle in palette 0xfc, rotated like the glyphs (+0x58 sets 0xfc).
            var pts = Arrow(yaw, w, lr, cx, cy);
            HudFrame.FillPolygon(p, new[] { ((int)pts[0].X, (int)pts[0].Y), ((int)pts[1].X, (int)pts[1].Y), ((int)pts[2].X, (int)pts[2].Y) }, 0xfc, 1f);
        }
        // The readout origin [code 004e7e5c, 004e86d9 and 004e87b5 for "000"]: x = trunc(cx + 1.0 - (textWidth >> 1)), y = trunc(cy - height + 3.0)
        // with (cx, cy) the compass centre (31.5 or 18.5 inside the bitmap) and height = (font cell height * font scale y) >> 16, the
        // cell height itself for every stock font (scale 1.0). The truncation is the exe's control word 0x0f7f (round toward zero).
        var tx = (int)(cx + 1f - (GuiPaint.TextWidth(f, text) >> 1));
        var ty = (int)(cy - GuiPaint.FontHeight(f) + 3f);
        p.Text(f, tx, ty, text);
    }

    /// <summary>Returns the readout text.</summary>
    /// <param name="wp">The waypoint, or null.</param>
    /// <returns>"000" without a waypoint or when both truncated coordinate differences are within 2 metres, else the truncated 2D distance, at most 999.</returns>
    /// <remarks>Follows 004e7e5c: the format "%d" at 0062ec1a and "000" at 0062ec1d.</remarks>
    public static string Readout(HudWaypoint? wp)
    {
        if (wp is not HudWaypoint w)
            return "000";
        if (Math.Abs((int)(w.PlayerX - w.X)) < 3 && Math.Abs((int)(w.PlayerY - w.Y)) < 3)
            return "000";
        var dist = (int)Math.Sqrt((float)(((w.PlayerX - w.X) * (w.PlayerX - w.X)) + ((w.PlayerY - w.Y) * (w.PlayerY - w.Y))));
        return Math.Min(999, dist).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the arrow corners around the compass centre.</summary>
    /// <param name="yaw">The control object's yaw.</param>
    /// <param name="w">The waypoint.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <param name="cx">The centre x.</param>
    /// <param name="cy">The centre y.</param>
    /// <returns>The three corners.</returns>
    /// <remarks>Follows 004e7e5c: the bearing atan2(wy - py, wx - px) (+-pi/2 when the truncated x difference is 0) minus the yaw, turned by pi/2 - angle. The corners (0, -24), (6, -16), (-6, -16) are rotated by that angle (low resolution: 0, -14.4 / 3.6, -9.6 / -3.6, -9.6).</remarks>
    public static (float X, float Y)[] Arrow(double yaw, HudWaypoint w, bool lowRes, float cx, float cy)
    {
        double bearing;
        if ((int)(w.X - w.PlayerX) == 0)
            bearing = w.PlayerY <= w.Y ? 1.5707964 : -1.5707964;
        else
            bearing = Math.Atan2(w.Y - w.PlayerY, w.X - w.PlayerX);
        var a = 1.5707964 - ((float)bearing - (float)yaw);
        float sin = (float)Math.Sin(a), cos = (float)Math.Cos(a);
        float e4 = lowRes ? 3.6000001f : 6f, e8 = lowRes ? -14.400001f : -24f, ec = lowRes ? -9.6f : -16f;
        return
        [
            (cx + ((0f * cos) - (e8 * sin)), cy + ((e8 * cos) + (sin * 0f))),
            (cx + ((e4 * cos) - (ec * sin)), cy + ((ec * cos) + (e4 * sin))),
            (cx + ((-e4 * cos) - (ec * sin)), cy + ((ec * cos) + (-e4 * sin))),
        ];
    }
}

/// <summary>The weapon list (FearGui::CurrentWeaponHud, 004e8fe8): a framed column with one icon per weapon the player carries and, right of it, the ammo count in a small dark box (the infinity icon when the weapon has no ammo item).</summary>
/// <remarks>The control resizes to the row count: width = the weapon icon width + 38, height = (icon height + 4) x rows + 10 (low resolution + 27, + 2).</remarks>
public sealed class GuiWeaponHud : GuiHud
{
    private static readonly int[] Poly = [2, 8, 8, 2, 24, 2, 30, 8];
    private static readonly int[] PolyLow = [1, 6, 6, 1, 16, 1, 18, 4];
    private int mRows = -1;

    /// <inheritdoc/>
    /// <remarks>Follows 004e8b8c and 004e8fb9 to 004e8fd4: after loading the icons it sets the extent to 1 x 1, the low resolution flag (+0x1f8) to 0 and the last row count (+0x204) to -1, then calls the clamp. A parent resize before the first draw therefore places the window with a height of 1 (the file's extent 62 x 76 is replaced), and the later row changes do not apply the fractions again.</remarks>
    public override void Wake()
    {
        base.Wake();
        W = 1;
        H = 1;
        mRows = -1;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.ControlIsPlayer || Host == null)
            return;
        var lr = LowRes;
        var fallback = Host.BitmapByName(lr ? "lr_weapon_off.bmp" : "I_weapon_off.bmp");
        int iconH = fallback?.Height ?? 24, iconW = fallback?.Width ?? 24;
        var rows = Source.Weapons;
        if (rows.Count < 1)
        {
            // [code 004e90fc to 004e9112]: no weapon: count -1, height = the icon height, width and position untouched.
            mRows = -1;
            H = iconH;
            return;
        }
        if (rows.Count != mRows)
        {
            mRows = rows.Count;
            W = iconW + (lr ? 27 : 38);
            H = ((iconH + (lr ? 2 : 4)) * rows.Count) + 10;
            KeepInside();
            return;
        }
        int x = ox + X, y = oy + Y, bottom = y + H - 1;
        var poly = lr ? PolyLow : Poly;
        var pts = new List<(int, int)>();
        for (var i = 0; i < 4; ++i)
            pts.Add((poly[2 * i] + x + (lr ? 1 : 0), poly[(2 * i) + 1] + y));
        for (var i = 3; i >= 0; --i)
            pts.Add((poly[2 * i] + x + (lr ? 1 : 0), y + H - poly[(2 * i) + 1]));
        HudFrame.FillPolygon(p, pts, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        var stub = lr ? 11 : 17;
        for (var k = 7; k <= 9; ++k)
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
        var box = Host.BitmapByName(lr ? "lr_ammo.bmp" : "ammoSh.bmp");
        var iy = y + (lr ? 6 : 7);
        foreach (var w in rows)
        {
            var stem = (lr ? "lr_" : "I_") + w.Icon + (w.Active ? "_on" : "_off");
            var icon = Host.BitmapByName(stem + ".bmp") ?? fallback;
            p.BitmapKeyed(icon, x + 4, iy);
            var bx = x + 4 + (lr ? 3 : 4) + (icon?.Width ?? 0);
            p.Bitmap(box, bx, iy);
            bx += lr ? 3 : 2;
            var by = iy + (lr ? 0 : -1);
            var f = w.Active ? lit : dim;
            if (w.Ammo < 0)
                p.BitmapKeyed(Host.BitmapByName((lr ? "lr_" : "") + (w.Active ? "I_Infinity_on.bmp" : "I_Infinity_off.bmp")), bx + (lr ? 1 : 2), by + (lr ? 3 : 6));
            else
            {
                var t = Math.Min(w.Ammo, 999).ToString(CultureInfo.InvariantCulture);
                p.Text(f, bx + ((GuiPaint.TextWidth(f, "888") - GuiPaint.TextWidth(f, t)) / 2), by, t);
            }
            iy += iconH + (lr ? 2 : 4);
        }
    }
}

/// <summary>The sensor ping (FearGui::FGHSensorPing, 004ea720): the frame bitmap `ping` is always drawn for the control player.</summary>
/// <remarks>The flags byte of the control object adds an overlay centred in the control: 2 the suppressed icon, 1 a cycle of the three ping icons. The cycle index is ftol((now - t0) * 10) % 6 (0x4ea8b8 to 0x4ea8d2, constant 10.0 at 0x4ea9b8): 0 Lo, 1 Med, else Hi, 100 ms each. t0 (+0x1f0) is latched on the first pinged frame and reset to -1 by any other state.</remarks>
public sealed class GuiSensorHud : GuiHud
{
    private double mPingStart = -1;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Source == null || !Source.SensorHudShown || Host == null)
            return;
        var lr = LowRes;
        int x = ox + X, y = oy + Y;
        p.Bitmap(Host.BitmapByName(lr ? "lr_ping.bmp" : "ping.bmp"), x, y);
        string? overlay = null;
        var now = Host.TickMs / 1000.0;
        if (Source.SensorLocked)
        {
            overlay = "HP_Supressed";
            mPingStart = -1;
        }
        else if (Source.SensorPing)
        {
            if (mPingStart <= 0)
                mPingStart = now;
            overlay = ((int)((now - mPingStart) * 10.0) % 6) switch { 0 => "HP_PingedLo", 1 => "HP_PingedMed", _ => "HP_PingedHi" };
        }
        else
            mPingStart = -1;
        if (overlay == null)
            return;
        var b = Host.BitmapByName((lr ? "LR_" : "") + overlay + ".bmp") ?? Host.BitmapByName(overlay + ".bmp");
        if (b != null)
            p.Bitmap(b, x + ((W - b.Width) / 2), y + ((H - b.Height) / 2));
    }
}

/// <summary>The crosshair (FearGui::Crosshair, 004ab500): a cross at the centre of the view.</summary>
public sealed class GuiCrosshair : GuiHud
{
    /// <inheritdoc/>
    public override void Wake()
    {
        W = H = 13;
        Layout();
    }

    /// <inheritdoc/>
    public override void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        X = (newW - W) / 2;
        Y = (newH - H) / 2;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        // 004ab500 draws nothing (reticle, zoom text, marks, label) unless PSC +0xa0 is on and +0x9c is 0 (third person off).
        if (Canvas == null || (Source != null && (!Source.InPlayerView || !Source.FirstPersonView)))
            return;
        int cx = ox + ((Parent?.W ?? W) / 2), cy = oy + ((Parent?.H ?? H) / 2);
        // the control is 13 pixels wide: its clip would cut the sniper cross, the zoom box, its text, the marks and the label
        var clip = p.Clip;
        p.SetClip(new GfxRect(0, 0, Canvas.Width - 1, Canvas.Height - 1));
        var reticleOnly = true;
        if (Source?.Zoom is HudZoom z)
            reticleOnly = DrawZoom(p, z, cx, cy);
        if (reticleOnly)
            DrawReticle(p, cx, cy);
        DrawTarget(p, cx, cy);
        DrawMarkers(p, cx, cy);
        p.SetClip(clip);
    }

    /// <summary>Draws the aim triangles of the placed targets and their distances.</summary>
    /// <remarks>Follows 004ada90, called at the end of 004ab500 unless 00623ed0 is set: a filled triangle pointing down onto the aim point (palette 0xfb when the weapon reaches it, 0xf1 when not), the label centred under the tip in the target label font. The coordinates are canvas pixels.</remarks>
    private void DrawMarkers(GuiPaint p, int cx, int cy)
    {
        if (Host == null || Source == null)
            return;
        var marks = Source.TargetMarkers(Canvas!.Width, Canvas.Height);
        if (marks.Count == 0)
            return;
        var lr = LowRes;
        int ox = cx - ((Parent?.W ?? W) / 2), oy = cy - ((Parent?.H ?? H) / 2);
        var f = Host.FontByName(lr ? "sf_white_5.pft" : "sf_white_7.pft");
        foreach (var m in marks)
            foreach (var mp in new[] { m.Low, m.High })
            {
                if (mp is not HudMarkPoint pt)
                    continue;
                int x = ox + pt.X, y = oy + pt.Y;
                HudFrame.FillPolygon(p, HudMarkers.Triangle(x, y, lr), pt.Reaches ? HudMarkers.ReachColor : HudMarkers.ShortColor, 1f);
                var (lx, ly) = HudMarkers.LabelOrigin(x, y, GuiPaint.TextWidth(f, m.Label), lr);
                p.Text(f, lx, ly, m.Label);
            }
    }

    /// <summary>Draws the reticle picture (`H_Reticle.bmp`, `LR_H_Reticle.bmp` below 512 x 384) centred on the view (004ab500).</summary>
    /// <remarks>Without the picture it draws four arms (3 pixels, 2 from the centre).</remarks>
    private void DrawReticle(GuiPaint p, int cx, int cy)
    {
        var reticle = Host?.BitmapByName(LowRes ? "LR_H_Reticle.bmp" : "H_Reticle.bmp");
        if (reticle != null)
            p.BitmapKeyed(reticle, cx - (reticle.Width >> 1), cy - (reticle.Height >> 1));
        else
        {
            p.Fill(cx - 4, cy, 3, 1, 0xfc);
            p.Fill(cx + 2, cy, 3, 1, 0xfc);
            p.Fill(cx, cy - 4, 1, 3, 0xfc);
            p.Fill(cx, cy + 2, 1, 3, 0xfc);
        }
    }

    /// <summary>The sniper state of the control (+0x1f8): on while the view is zoomed to 30 degrees or less (and while the zoom-in animation heads there). The cross grows or shrinks with the animation.</summary>
    private bool mSniper;

    /// <summary>Draws the zoom branch of 004ab500 (004ab66d to 004ac48f).</summary>
    /// <returns>True when the reticle picture follows, false when the sniper cross was drawn instead.</returns>
    private bool DrawZoom(GuiPaint p, HudZoom z, int cx, int cy)
    {
        int pw = Parent?.W ?? W, ph = Parent?.H ?? H;
        int halfW = pw >> 1, halfH = ph >> 1;
        int sw = 0, sh = 0;
        if (z.Now < z.End)
        {
            var zoomingIn = z.Target < z.Cur;
            if (zoomingIn)
                mSniper = mSniper || z.Target <= 30;
            if (mSniper)
            {
                double span = z.End - z.Start;
                var num = zoomingIn ? z.Now - z.Start : z.End - z.Now;
                sw = (int)((uint)(halfW * (int)num) / span);
                sh = (int)((uint)(halfH * (int)num) / span);
            }
        }
        else if (z.Cur > 30)
            mSniper = false;
        else
        {
            mSniper = true;
            sw = halfW;
            sh = halfH;
        }
        var lr = LowRes;
        if (!mSniper)
        {
            // the box of the next zoom level with its text, for 2 s after a zoom step while the zoom is off
            var until = z.Target != z.Cur ? 0u : unchecked(z.StepStamp + 2000);
            if (z.Now < until)
                DrawZoomBox(p, z, cx, cy, halfW, halfH, pw, ph, lr);
            return true;
        }
        if (string.Equals(Host?.GetVar("$pref::SniperCrosshair"), "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            mSniper = false;
            return true;
        }
        // the cross over the whole view: two lines wide, with a gap in the middle of each arm
        p.Line(cx, cy - sh, cx, cy + sh, 0xfc);
        p.Line(cx - 1, cy - sh, cx - 1, cy - (sh / 3), 0xfc);
        p.Line(cx - 1, cy + (sh / 3), cx - 1, cy + sh, 0xfc);
        p.Line(cx + 1, cy - sh, cx + 1, cy - (sh / 3), 0xfc);
        p.Line(cx + 1, cy + (sh / 3), cx + 1, cy + sh, 0xfc);
        p.Line(cx - sw, cy, cx + sw, cy, 0xfc);
        p.Line(cx - sw, cy - 1, cx - (sw / 3), cy - 1, 0xfc);
        p.Line(cx + (sw / 3), cy - 1, cx + sw, cy - 1, 0xfc);
        p.Line(cx - sw, cy + 1, cx - (sw / 3), cy + 1, 0xfc);
        p.Line(cx + (sw / 3), cy + 1, cx + sw, cy + 1, 0xfc);
        return false;
    }

    /// <summary>Returns the corners of the zoom box in a view.</summary>
    /// <param name="zoomFov">The zoom field of view.</param>
    /// <param name="pw">The view width.</param>
    /// <param name="ph">The view height.</param>
    /// <returns>The corners: half size = view size x tan(zoom FOV / 2) x 0.5 around the view centre (004ab8c3).</returns>
    public static (int X0, int Y0, int X1, int Y1) ZoomBox(double zoomFov, int pw, int ph)
    {
        var t = Math.Tan((double)(float)zoomFov * Math.PI / 360.0);
        float hw = (float)(t * pw * 0.5f), hh = (float)(t * ph * 0.5f);
        int cx = pw >> 1, cy = ph >> 1;
        return ((int)(cx - hw), (int)(cy - hh), (int)(cx + hw), (int)(cy + hh));
    }

    /// <summary>Draws the corner marks around the area the next zoom level would show and "ZOOM: %dx" below it, x being the view width over the box width (004ab8c3 to 004abe2c).</summary>
    private void DrawZoomBox(GuiPaint p, HudZoom z, int cx, int cy, int halfW, int halfH, int pw, int ph, bool lr)
    {
        (var x0, var y0, var x1, var y1) = ZoomBox(z.ZoomFov, pw, ph);
        int ox = cx - halfW, oy = cy - halfH;
        var arm = lr ? 4 : 7;
        void Mark(int x, int y, int dx, int dy)
        {
            p.Line(ox + x, oy + y, ox + x + dx, oy + y, 0xfc);
            p.Line(ox + x, oy + y, ox + x, oy + y + dy, 0xfc);
        }
        Mark(x0, y0, arm, arm);
        Mark(x1, y0, -arm, arm);
        Mark(x0, y1, arm, -arm);
        Mark(x1, y1, -arm, -arm);
        if (Host == null || x1 == x0)
            return;
        var text = "ZOOM: " + (pw / (x1 - x0)).ToString(CultureInfo.InvariantCulture) + "x";
        var f = Host.FontByName(lr ? "sf_white_5.pft" : "sf_white_7.pft");
        p.Text(f, cx - (GuiPaint.TextWidth(f, text) >> 1), cy + (lr ? 18 : 30), text);
    }

    /// <summary>Draws the target marks and the label (004ab500, 004aca84).</summary>
    /// <remarks>A friend or foe picture is centred above the head of every player in sight. For the picked one its name is drawn at (16, 16) from the view centre (10 at low resolution) in sf_white_7 (sf_white_5), with the health box of 27 x 6 (20 x 4) below it: a frame in palette 0xfb and a bar of (1 - damage) x 25 (18) pixels in 0xfc, 0xf3 below 8 (5) pixels.</remarks>
    private void DrawTarget(GuiPaint p, int cx, int cy)
    {
        if (Host == null || Source?.CrosshairTarget(Canvas!.Width, Canvas.Height) is not HudTargetResult t)
            return;
        var lr = LowRes;
        int ox = cx - ((Parent?.W ?? W) / 2), oy = cy - ((Parent?.H ?? H) / 2);
        foreach (var m in t.Marks)
        {
            var b = Host.BitmapByName((lr ? "LR_" : "") + (m.Friend ? "H_Friend.bmp" : "H_Foe.bmp"));
            if (b != null)
                p.BitmapKeyed(b, ox + m.X - (b.Width >> 1), oy + m.Y - b.Height);
        }
        if (t.Name == null)
            return;
        if (t.Icon != null && Host.BitmapByName(t.Icon) is PbmpBitmap icon)
            p.BitmapKeyed(icon, ox + t.IconX - (icon.Width >> 1), oy + t.IconY - icon.Height);
        int x0 = cx + (lr ? 10 : 16), y0 = cy + (lr ? 10 : 16);
        var f = Host.FontByName(lr ? "sf_white_5.pft" : "sf_white_7.pft");
        if (t.Name.Length > 0)
        {
            p.Text(f, x0, y0, t.Name);
            y0 += GuiPaint.FontHeight(f) + (lr ? 3 : 4);
        }
        int bw = lr ? 20 : 27, bh = lr ? 4 : 6;
        int x1 = x0 + bw, y1 = y0 + bh;
        if (t.Damage is < 0 or > 1)
            return;
        p.Line(x0, y0, x1, y0, 0xfb);
        p.Line(x1, y0, x1, y1, 0xfb);
        p.Line(x1, y1, x0, y1, 0xfb);
        p.Line(x0, y1, x0, y0, 0xfb);
        var len = HudTargetPicker.BarLength(t.Damage, bw - 2);
        if (len > 0)
            p.Fill(x0 + 1, y0 + 1, len + 1, y1 - y0 - 1, len < (lr ? 5 : 8) ? 0xf3 : 0xfc);
    }
}

/// <summary>The brackets of the message window frames (004a5f98, 004a38f0).</summary>
public static class HudFrame
{
    /// <summary>The palette index of the translucent shade.</summary>
    public const int ShadeColor = 0;
    /// <summary>The palette index of the bracket face.</summary>
    public const int FaceColor = 0xfc;
    /// <summary>The palette index of the bracket edge.</summary>
    public const int EdgeColor = 0xf8;
    /// <summary>The opacity of the shade.</summary>
    public const float ShadeAlpha = 0.5f;

    /// <summary>Draws a bracket: a vertical rule at x in palette 0xf8, a second one beside it in 0xfc and a short horizontal stub at each end.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="x">The rule x.</param>
    /// <param name="top">The top y.</param>
    /// <param name="dir">The stub length with sign: +4 for the left bracket and -4 for the right one.</param>
    /// <param name="bottom">The bottom y.</param>
    public static void Bracket(GuiPaint p, int x, int top, int dir, int bottom)
    {
        var step = dir < 0 ? -1 : 1;
        p.Line(x, top, x, bottom, EdgeColor);
        p.Line(x + step, top, x + step, bottom, FaceColor);
        p.Line(x + step, top, x + step + dir, top, FaceColor);
        p.Line(x + step, bottom, x + step + dir, bottom, FaceColor);
    }

    /// <summary>Fills a convex polygon with one scan line per row (the translucent frames of the weapon HUD).</summary>
    /// <param name="p">The painter.</param>
    /// <param name="pts">The corners.</param>
    /// <param name="color">The palette index.</param>
    /// <param name="alpha">The opacity.</param>
    public static void FillPolygon(GuiPaint p, IReadOnlyList<(int X, int Y)> pts, int color, float alpha)
    {
        int minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        for (var row = minY; row <= maxY; ++row)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (var i = 0; i < pts.Count; ++i)
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
                var xs = ax + ((double)(row - ay) * (bx - ax) / (by - ay));
                lo = Math.Min(lo, xs);
                hi = Math.Max(hi, xs);
            }
            if (hi >= lo)
                p.Fill((int)Math.Ceiling(lo), row, (int)Math.Floor(hi) - (int)Math.Ceiling(lo) + 1, 1, color, alpha);
        }
    }

    /// <summary>Draws the translucent shade and both brackets over a rectangle.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    public static void Panel(GuiPaint p, int x, int y, int w, int h)
    {
        p.Fill(x, y, w, h, ShadeColor, ShadeAlpha);
        Bracket(p, x, y, 4, y + h - 1);
        Bracket(p, x + w - 1, y, -4, y + h - 1);
    }
}

/// <summary>The chat display line count per content control id.</summary>
/// <remarks>The stock GuiLoadContentCtrl (00535c7c) looks the content up by name before it loads the file, so a size set with IDACTION_CHAT_DISP_SIZE survives the trip through the command and inventory screens. The port loads the file each time, so the count is kept here and applied when the content comes back.</remarks>
public sealed class ChatSizeMemory
{
    private readonly Dictionary<uint, int> mLines = [];

    /// <summary>Stores the line count of a display.</summary>
    /// <param name="contentId">The content control id.</param>
    /// <param name="disp">The display.</param>
    public void Remember(uint contentId, GuiChatDisplay disp) => mLines[contentId] = disp.Lines;

    /// <summary>Applies the stored line count to a display.</summary>
    /// <param name="contentId">The content control id.</param>
    /// <param name="disp">The display.</param>
    /// <returns>True when a size was stored for the content and the display took it.</returns>
    public bool Apply(uint contentId, GuiChatDisplay disp)
    {
        if (!mLines.TryGetValue(contentId, out var n))
            return false;
        disp.SetLines(n);
        return true;
    }
}

/// <summary>The message window (FearGui::FearGuiChatDisplay, 004a633c).</summary>
/// <remarks>With lines > 0 it draws a translucent shade over its extent and the two frame brackets (the `chatE` flag of the file is 0 in the stock play.gui), then the last N chat lines of the host's ring, wrapped to the extent minus the 4 and 2 pixel inset the draw routine applies.</remarks>
public sealed class GuiChatDisplay : GuiHud
{
    /// <summary>The inset of the text from the left edge.</summary>
    public const int InsetX = 4;
    /// <summary>The indent of the text: half of the stock indent value 12 (00623478).</summary>
    public const int IndentX = 6;
    /// <summary>The distance of the last line above the bottom edge.</summary>
    public const int BottomInset = 6;
    /// <summary>The height added to the lines (DAT_00623474), see <see cref="SetLines"/>.</summary>
    public const int HeightPad = 6;

    /// <summary>Returns the font of a message type.</summary>
    /// <param name="type">The message type.</param>
    /// <param name="low">True for the low resolution set (canvas below 512 x 384), which has the 6 point versions.</param>
    /// <returns>The font file name: 0 white, 1 and 4 red, 3 green, any other yellow.</returns>
    /// <remarks>Follows 004a5124, loaded by 004a5b54.</remarks>
    public static string FontName(uint type, bool low) => type switch
    {
        0 => low ? "sf_white_6.pft" : "if_w_10b.pft",
        1 or 4 => low ? "sf_red_6.pft" : "sf_red_10b.pft",
        3 => low ? "sf_green_6.pft" : "if_g_10b.pft",
        _ => low ? "sf_yellow_6.pft" : "sf_yellow_10b.pft",
    };

    /// <summary>Returns the next line count of the size action (004a7030).</summary>
    /// <param name="current">The current line count.</param>
    /// <param name="value">The action value: a negative one cycles 3, 5, 20, 3, a value from 0 sets it.</param>
    /// <returns>The line count.</returns>
    public static int NextLines(int current, int value) => value >= 0 ? value : current switch { 3 => 5, 5 => 20, _ => 3 };

    /// <summary>Gets the number of message lines.</summary>
    public int Lines => (int)U("lines", 3);

    private bool mLowMode;

    /// <inheritdoc/>
    /// <remarks>The loader (004a5b54) sets the lower-case-as-capitals flag (0x400) of the four low resolution message fonts, so the 6 point chat lines show capitals only.</remarks>
    public override void Wake()
    {
        base.Wake();
        if (Host == null)
            return;
        foreach (var n in new[] { "sf_white_6.pft", "sf_red_6.pft", "sf_yellow_6.pft", "sf_green_6.pft" })
            if (Host.FontByName(n) is PftFont f)
                f.Flags |= PftFont.FlagLowerCaps;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004a56e8: the message window does not use the fractions of the other HUDs. A surface below 512 x 384 is the low resolution mode (width 240 and a height of lines x line height + 6 on entering it, back to width 370 on leaving it). The width is never below that mode's width and stays under the surface width, the height under the surface height and a whole number of lines, and the position is pulled back inside the surface.</remarks>
    public override void ParentResized(int oldW, int oldH, int newW, int newH)
    {
        if (newW <= 0 || newH <= 0)
            return;
        var low = newW < 0x200 || newH < 0x180;
        var changed = low != mLowMode;
        mLowMode = low;
        var f = Host?.FontByName(FontName(0, low)) ?? Host?.FontByName("sf_white_10.pft");
        int lh = f == null ? 13 : GuiPaint.FontHeight(f), lines = (int)U("lines", 3), min = low ? 0xf0 : 0x172;
        if (changed)
        {
            W = min;
            H = (lines * lh) + HeightPad;
        }
        W = Math.Max(Math.Min(Math.Max(W, min), newW - 1), 1);
        H = Math.Max(Math.Min(H, newH - 1), 1);
        lines = (H - HeightPad) / lh;
        F["lines"] = (uint)Math.Max(lines, 0);
        H = (lines * lh) + HeightPad;
        X = Math.Min(X, newW - W);
        Y = Math.Min(Y, newH - H);
    }

    /// <summary>Sets the line count (IDACTION_CHAT_DISP_SIZE) and resizes the control to lines * line height + 6, the top stays.</summary>
    /// <param name="value">The action value, see <see cref="NextLines"/>.</param>
    public void SetLines(int value)
    {
        var n = NextLines((int)U("lines", 3), value);
        F["lines"] = (uint)n;
        var f = Host?.FontByName(FontName(0, Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384))) ?? Host?.FontByName("sf_white_10.pft");
        H = (n * (f == null ? 13 : GuiPaint.FontHeight(f))) + HeightPad;
    }

    /// <summary>Draws the player chat menu under the message window (004a633c, after the frame).</summary>
    /// <remarks>It is drawn when `chatE` is 0 and the menu is on, with the heading and the items in if_g_10b one font height apart, starting right below the control (its top plus its height). When the list would reach the bottom of the screen it sits above the control instead, 8 pixels clear of it.</remarks>
    private void RenderMenu(GuiPaint p, int x, int y)
    {
        if (Host?.Hud?.PlayMenu is not PlayMenuView menu || U("chatE") != 0)
            return;
        var f = Host?.FontByName("if_g_10b.pft");
        if (f == null)
            return;
        var h = GuiPaint.FontHeight(f);
        var total = h * (menu.Lines.Count + 1);
        var ty = y + H;
        var screenH = Canvas?.Height ?? p.Dev.Height;
        if (screenH <= ty + total)
            ty = y - total - 8;
        // The original sets its clip to the whole surface first [code 004a633c]: the list lies outside the control's own rectangle.
        var saved = p.Clip;
        p.SetClip(new GfxRect(0, 0, p.Dev.Width - 1, p.Dev.Height - 1));
        p.Text(f, x, ty, menu.Heading);
        ty += h;
        foreach (var line in menu.Lines)
        {
            p.Text(f, x, ty, line);
            ty += h;
        }
        p.SetClip(saved);
    }

    /// <inheritdoc/>
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
        var n = (int)U("lines", 3);
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
        var lines = new List<(string Text, PftFont Font)>();
        var low = Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
        foreach (var (text, tag) in Host.Chat)
        {
            var f = Host.FontByName(FontName(tag, low)) ?? Host.FontByName("sf_white_10.pft");
            if (f == null)
                continue;
            // The chat display draws the message as plain text: the wrap routine [004a55c0] and the draw [004a633c] read no markup.
            foreach (var l in GuiTextWrap.Wrap(f, text, W - (2 * InsetX)))
                lines.Add((l, f));
        }
        // The command line takes the bottom of the display (one line of the count) while the local player holds a command.
        var cmdLines = new List<string>();
        PftFont? cmdFont = null;
        // [code 004a633c] The slot is kept for as long as the record holds a command (+0x40 >= 0), whatever the text: one line less
        // for the ring, the bottom of the ring one command font height higher, and only the first wrapped line of the text is drawn.
        if (Host.CommandActive)
        {
            cmdFont = Host.FontByName(FontName(Host.CommandAccepted ? 3u : 4u, low)) ?? Host.FontByName("sf_white_10.pft");
            if (cmdFont != null && Host.CommandText.Length > 0)
                cmdLines.AddRange(GuiTextWrap.Wrap(cmdFont, Host.CommandText, W - (2 * InsetX)).Take(1));
            n = Math.Max(0, n - 1);
        }
        var start = Math.Max(0, lines.Count - n);
        var ty = y + H - BottomInset;
        if (cmdFont != null)
            ty -= GuiPaint.FontHeight(cmdFont);
        for (var i = start; i < lines.Count; ++i)
            ty -= GuiPaint.FontHeight(lines[i].Font);
        for (var i = start; i < lines.Count; ++i)
        {
            p.Text(lines[i].Font, x + InsetX + IndentX, ty, lines[i].Text);
            ty += GuiPaint.FontHeight(lines[i].Font);
        }
        if (cmdFont != null)
            foreach (var l in cmdLines)
            {
                p.Text(cmdFont, x + InsetX + IndentX, ty, l);
                ty += GuiPaint.FontHeight(cmdFont);
            }
    }
}

/// <summary>The center print message (CenterPrint, 00488f80 prepare, 00489108 draw): the message of Client::centerPrint in a shaded box with a bracket on each side (stub length 4).</summary>
/// <remarks>The box is the parent's width less 70 (low resolution 30) wide at x 35 (15), as high as the wrapped text plus 8, and sits by the message type: 0 vertically centred, 1 35 (15) above the bottom, 2 35 (15) below the top. The text is wrapped to the box less 4 and drawn 2 pixels inside with the fonts f0, f1 and f2 of the control (sf_orange214_10, sf_orange255_10, sf_white_9b, low resolution sf_orange_6, sf_yellow_6, sf_white_6) by the TextFormat layout (docs/gui.md 3.3.1).</remarks>
public sealed class GuiCenterPrint : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Parent == null || Host.CenterText.Length == 0 || Host.CenterType is < 0 or > 2)
            return;
        int pw = Parent.W, ph = Parent.H;
        var low = pw < 512 || ph < 384;
        var names = low ? new[] { "sf_orange_6.pft", "sf_yellow_6.pft", "sf_white_6.pft" }
            : ["sf_orange214_10.pft", "sf_orange255_10.pft", "sf_white_9b.pft"];
        var fonts = new PftFont?[GuiTextFormat.FontCount];
        for (var i = 0; i < names.Length; ++i)
            fonts[i] = Host.FontByName(names[i]);
        fonts[0] ??= Host.FontByName("sf_white_10.pft");
        if (fonts[0] == null)
            return;
        var w = pw - (low ? 30 : 70);
        // The prepare handler [code 00488f80] lays the message out at the box width less 4 with the TextFormat routine [005370b8];
        // the box is the layout height + 8 high and the layout is drawn 2 pixels inside it [draw 00489108].
        GuiTextFormat layout = GuiTextFormat.Layout(Host.CenterText, w - 4, fonts, Host.BitmapByName, n => Host.GetVar("$" + n));
        var h = layout.Height + 8;
        var bx = low ? 15 : 35;
        var by = Host.CenterType == 0 ? (ph - h) / 2 : Host.CenterType == 1 ? ph - h - (low ? 15 : 35) : (low ? 15 : 35);
        X = bx;
        Y = by;
        W = w;
        H = h;
        int x = ox + bx, y = oy + by;
        p.Fill(x, y, w, h, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        HudFrame.Bracket(p, x, y, 4, y + h - 1);
        HudFrame.Bracket(p, x + w - 1, y, -4, y + h - 1);
        layout.Draw(p, x + 2, y + 2);
    }
}

/// <summary>The chat line (FearGui::FearGuiChat), hidden until opened: Enter sends the text through its command, Escape cancels.</summary>
public sealed class GuiChat : GuiTextEdit
{
    /// <inheritdoc/>
    public override bool WantsTab => Visible;

    /// <inheritdoc/>
    /// <remarks>The stock chat line has font tag 0 and the highlight tag 150007 (play.gui). A tag that resolves to nothing falls back to the highlight font, then to the HUD font, so the typed text is always drawn (docs/gui.md 3.3 TextEdit draw: the class text draw).</remarks>
    protected override PftFont? Font =>
        Host?.FontByTag(U("fontTag")) ?? Host?.FontByTag(U("highlightFontTag")) ?? Host?.FontByName("sf_white_10.pft");

    /// <summary>The distance of the line above the bottom edge of its parent.</summary>
    public const int Margin = 40;
    /// <summary>The horizontal padding.</summary>
    public const int PadX = 12;
    /// <summary>The vertical padding.</summary>
    public const int PadY = 4;
    /// <summary>The number of text lines the height is counted in.</summary>
    public const int Lines = 2;
    /// <summary>The height added to the lines (DAT_00623204).</summary>
    public const int ExtraHeight = 10;

    private bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    /// <summary>Returns the prompt of a channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>"SAY: " for channel 0, "SAY TEAM: " for any other.</returns>
    public static string Prompt(int channel) => channel < 1 ? "SAY: " : "SAY TEAM: ";

    private int Channel => Math.Clamp((int)Math.Truncate(Host == null ? 0 : ParseInt(Host.GetVar("$pref::msgChannel"))), 0, 31);

    private static double ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private PftFont? PromptFont(bool lowRes, int channel)
    {
        var name = lowRes ? (channel < 1 ? "sf_yellow_6.pft" : "sf_green_6.pft") : (channel < 1 ? "sf_yellow_10b.pft" : "if_g_10b.pft");
        return Host?.FontByName(name) ?? Host?.FontByName("sf_white_10.pft");
    }

    /// <summary>Wraps the prompt plus text into lines.</summary>
    /// <param name="f">The font.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="text">The typed text.</param>
    /// <param name="width">The control width.</param>
    /// <returns>The first character index and length of each line. The first line starts after the prompt, later ones at the left pad.</returns>
    public static List<(int Start, int Length)> WrapLines(PftFont f, string prompt, string text, int width)
    {
        var lines = new List<(int, int)>();
        var avail = Math.Max(1, width - (2 * PadX));
        int used = GuiPaint.TextWidth(f, prompt), start = 0;
        for (var i = 0; i < text.Length; ++i)
        {
            var cw = f.CharWidth(text[i]) + 1;
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

    /// <inheritdoc/>
    /// <remarks>Follows FearGui::FearGuiChat draw (004a3ebc, layout 004a3e68): the line sits at the bottom centre of its parent, 40 pixels above the bottom edge less one text line per wrapped line. A translucent shade with the two frame brackets covers the control, the prompt is "SAY: " (channel 0) or "SAY TEAM: " (any other) in the channel's font, the typed text follows it and wraps below it, and the cursor is a vertical bar in palette 0xfc that blinks.</remarks>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Parent == null || Canvas == null)
            return;
        var low = LowRes;
        var channel = Channel;
        var f = PromptFont(low, channel);
        if (f == null)
            return;
        var prompt = Prompt(channel);
        var lines = WrapLines(f, prompt, Text, W);
        var fh = GuiPaint.FontHeight(f);
        // The control is two lines of sf_yellow_10b tall plus 10 (onWake: font height * [+0x464] = 2 + `DAT_00623204`), whatever the text, and
        // the layout lifts it by the same two lines (004a3e68); the metrics never follow the wrapped line count or the low resolution font.
        var mh = GuiPaint.FontHeight(Host.FontByName("sf_yellow_10b.pft") ?? f);
        H = (mh * Lines) + ExtraHeight;
        X = (Parent.W - W) / 2;
        Y = Parent.H - Margin - (mh * Lines);
        int x = ox + X, y = oy + Y;
        HudFrame.Panel(p, x, y, W, H);
        var ty = y + PadY - (low ? 0 : 2);
        var tx = x + PadX;
        var focus = ReferenceEquals(Canvas.FirstResponder, this);
        var blink = ((Host.TickMs / 500) & 1) == 0;
        for (var i = 0; i < lines.Count; ++i)
        {
            var lx = tx;
            if (i == 0)
            {
                p.Text(f, lx, ty, prompt);
                lx += GuiPaint.TextWidth(f, prompt);
            }
            var (start, len) = lines[i];
            var seg = Text.Substring(start, len);
            p.Text(f, lx, ty, seg);
            var last = i == lines.Count - 1;
            if (focus && blink && Cursor >= start && (Cursor < start + len || (last && Cursor == start + len)))
            {
                var cx = lx + GuiPaint.TextWidth(f, seg[..(Cursor - start)]);
                // 004a3ebc: the cursor starts 3 below the text line (normal resolution) and is one font height long
                var cy = ty + (low ? 0 : 3);
                p.Line(cx, cy, cx, cy + fh - 1, HudFrame.FaceColor);
            }
            ty += fh;
        }
    }

    /// <summary>Shows the line and gives it the keyboard.</summary>
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

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e)
    {
        if (e.Scan == 0x01)
        {
            Close2();
            return true;
        }
        if (e.Scan == 0x1c)
        {
            var text = Text;
            WriteVariable(text);
            if (Command.Length > 0)
                Host?.Eval(Command);
            else if (text.Length > 0 && Host != null)
            {
                // The stock `.gui` files give the chat line no command: the engine sends the line to the channel the chat key
                // chose (client.cs `say(channel, message)`, `$pref::msgChannel`, client-scripts.md 3.10 CHAT).
                var channel = Host.GetVar("$pref::msgChannel");
                Host.Eval("say(" + (channel.Length > 0 ? channel : "0") + ", \"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\");");
            }
            Close2();
            return true;
        }
        return base.KeyDown(e);
    }

    /// <inheritdoc/>
    public override bool KeyUp(GuiEvent e) => true;
}

/// <summary>The 3D view (SimGui::TSControl, docs/gui.md 3.3 "The 3D play view").</summary>
/// <remarks>The world is drawn by the world renderer before the GUI. This control draws its children (the HUD) over it.</remarks>
public sealed class GuiPlayView : GuiControl
{
    /// <summary>Gets a value that is true when the control produces 3D events.</summary>
    public bool Produce3dEvents => U("produce3dEvents") != 0;

    /// <summary>True when the control has a camera object (+0x1ec, set by 005473ec).</summary>
    /// <remarks>The play view of play.gui is drawn by the host before the GUI and never sets it here. The observer panel's view (<see cref="GuiCmdObserve"/>) sets it to the client's control object, which makes the control draw the world itself, inside its rectangle.</remarks>
    public bool CameraAttached;

    /// <inheritdoc/>
    /// <remarks>Follows the TSControl draw 00546dc4: when the camera set-up (00545f7c, the control object's camera query, vtable +0x24) fails, the control fills its rectangle with color 0 (surface vtable +0x10, alpha 1.0) and returns without drawing its children (the HUD). The view fills the control rectangle: the frustum comes from that rectangle, the field of view across its width.</remarks>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (CameraAttached && Canvas?.ViewRenderer is { } draw)
        {
            // 00546dc4 / 00545f7c: the view fills the control rectangle (the frustum comes from that rectangle, the field of view across
            // its width); no camera: colour 0 over the rectangle and no children.
            int gx = ox + X, gy = oy + Y;
            if (W <= 0 || H <= 0)
                return;
            var drew = draw(new GfxRect(gx, gy, gx + W - 1, gy + H - 1));
            p.SetClip(p.Clip);
            if (!drew)
            {
                p.Fill(gx, gy, W, H, 0);
                return;
            }
            base.Render(p, ox, oy);
            return;
        }
        if (Canvas is { ViewHasCamera: false })
        {
            p.Fill(ox + X, oy + Y, W, H, 0);
            return;
        }
        base.Render(p, ox, oy);
    }
}
