// FearGui::TSCommander, the commander map (command.gui): docs/gui.md 3.3 "The command map".
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// The base picture the map control keeps (`rebuildCommandMap`, the terrain seen from above): the mission area it covers (MissionCenterPos
// x y w h, the lower left corner and the extent, +0x298 .. +0x2a4) and the 4 x 4 bitmaps of 256 x 256 pixels in row major order
// (+0x2f0). The 1024 x 1024 picture shows the area with the north edge on top.
public sealed record CommandMapPicture(float X, float Y, float W, float H, PbmpBitmap[] Tiles);

// One slot of the command map as the draw reads it (the slot table at `DAT_006a83d8` + 0x340, its datablock and, for a client slot, the record):
// the position in 1/1024 of the area, the team, the datablock group (3 player, 4 static shape, 8 sensor, 10 vehicle, 19 turret) and its map
// fields (mapFilter +0x18, mapIcon +0x1c, description +0x20, the sensor range +0x104 and the flag at +0x10c), the label of a target slot
// (TargetNameEvent), the active flag, and for a client slot the client id, the name and the current command of its record.
public sealed record CommandMapSlotMark(int Slot, int X, int Y, int Team, int Group, int ClientId, string Icon, string Description, string? TargetName,
    int Filter, int Range, bool RangeFlag, bool Active, string ClientName, int OrderIcon, int OrderTarget, int OrderStatus, int OrderX, int OrderY);

// What the map draws over the picture: the local client (record id and team), its heading (the control object's yaw as 0 to 256) and the slots.
public sealed record CommandMapMarks(int LocalId, int LocalTeam, int Heading, IReadOnlyList<CommandMapSlotMark> Slots);

// TSCommander [code 004ceb18 ctor, 004ced10 dtor, 004cef14 rebuild, 004cf784 prepare, 004d04e8 draw]. Constructed it is the 408 x 408 control of
// the file; the rebuild (`onClientGhostAlwaysDone`) renders the picture and then fits the control into its parent: the control is scaled
// by min(parent width, parent height) / max(width, height) (both sides truncated) and centred in the parent on any axis where the parent
// is larger [code 004cf3ed .. 004cf4f1]. Draw: the clip becomes the parent's rectangle, then the 16 tiles are stretched over the control,
// each a quarter of its width and height (rounded up when four of them fall short), row by row from the control's top left corner.
// Zoom (004d3424 in, 004d3710 out), the pan with the right button (004d2d78, 004d2e5c, 004d2fe0) and the zoom animation (004cf784) are ported; the
// selection, the order cursor and the centring on the selected slots (004d1bf0, 004d2b1c, 004d3018) are not.
public sealed class GuiCommandMap : GuiControl
{
    private CommandMapPicture? mFitted;

    // The zoom mode (+0x28c: a left click zooms in, a right click out), the centre mode (+0x28d), the number of zoom steps (+0x290, 0 to 4), the
    // animation (+0x2d0 with its start tick +0x2d4, the rectangle it started from +0x2b0 and the one it ends at +0x2c0) and the pan (+0x274 with
    // the mouse (+0x264, +0x268) and the position (+0x278, +0x27c) where it started, +0x2d1 once the zoom or animation took the button).
    public bool ZoomMode, CenterMode;
    public int ZoomSteps;
    private bool mAnimating, mPanning, mButtonTaken;
    private long mAnimStart;
    private (int X, int Y, int W, int H) mFrom, mTo;
    private int mPanMouseX, mPanMouseY, mPanX, mPanY;

    public bool Animating => mAnimating;
    public const int MaxZoomSteps = 4;

    private int ZoomTime => int.TryParse(Host?.GetVar("$pref::CommanderZoomTime"), out int t) ? Math.Clamp(t, 1, 2000) : 250;

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
        mAnimating = true;
        mAnimStart = Host?.TickMs ?? 0;
        mFrom = (X, Y, W, H);
        mTo = to;
    }

    // 004d3424: a step in. The control doubles in size around the cursor point and the change runs over `$pref::CommanderZoomTime`.
    public void ZoomIn(int cursorX, int cursorY, bool overControl)
    {
        if (ZoomSteps >= MaxZoomSteps || Parent == null)
            return;
        var cur = mAnimating ? mTo : (X, Y, W, H);
        ++ZoomSteps;
        var (cx, cy) = ZoomCentre(cursorX, cursorY, overControl);
        int dx = cx - cur.Item1, dy = cy - cur.Item2;
        StartAnimation(Clamp(cx - dx * 2, cy - dy * 2, cur.Item3 << 1, cur.Item4 << 1));
    }

    // 004d3710: a step out; at no zoom the control is fitted again at once.
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
        var cur = mAnimating ? mTo : (X, Y, W, H);
        var (cx, cy) = ZoomCentre(cursorX, cursorY, overControl);
        int dx = cx - cur.Item1, dy = cy - cur.Item2;
        StartAnimation(Clamp(cx - (dx >> 1), cy - (dy >> 1), cur.Item3 >> 1, cur.Item4 >> 1));
    }

    // The keyboard forms (the play delegate actions ZOOM_IN and ZOOM_OUT [code 004dd280 cases 0x35c2f, 0x35c30]): the cursor counts as over the control when it is inside it.
    private bool CursorOver(out int cx, out int cy)
    {
        cx = (int)(Canvas?.CursorX ?? 0);
        cy = (int)(Canvas?.CursorY ?? 0);
        var (lx, ly) = GlobalToLocal(cx, cy);
        return Contains(lx, ly);
    }

    public void ZoomInAtCursor()
    {
        bool over = CursorOver(out int cx, out int cy);
        ZoomIn(cx, cy, over);
    }

    public void ZoomOutAtCursor()
    {
        bool over = CursorOver(out int cx, out int cy);
        ZoomOut(cx, cy, over);
    }

    // 004cf784: the animation step of every frame.
    public override void PreRender()
    {
        if (!mAnimating)
            return;
        long now = Host?.TickMs ?? 0;
        int time = ZoomTime;
        if (now < mAnimStart + time)
        {
            float f = (float)(now - mAnimStart) / time;
            (X, Y, W, H) = ((int)(f * (mTo.X - mFrom.X)) + mFrom.X, (int)(f * (mTo.Y - mFrom.Y)) + mFrom.Y, (int)(f * (mTo.W - mFrom.W)) + mFrom.W,
                (int)(f * (mTo.H - mFrom.H)) + mFrom.H);
        }
        else
        {
            (X, Y, W, H) = mTo;
            mAnimating = false;
        }
    }

    // The left button (004d1bf0): in zoom mode a click zooms in.
    public override bool MsDown(GuiEvent e)
    {
        if (CenterMode)
            CenterMode = false;
        if (ZoomMode)
        {
            ZoomIn(e.X, e.Y, true);
            return true;
        }
        return base.MsDown(e);
    }

    // The right button (004d2d78): in zoom mode a click zooms out, else it starts the pan unless an animation runs.
    public override bool MsRightDown(GuiEvent e)
    {
        if (mPanning)
            return true;
        if (ZoomMode || mAnimating)
            mButtonTaken = true;
        if (CenterMode)
            CenterMode = false;
        if (ZoomMode)
            ZoomOut(e.X, e.Y, true);
        else if (!mAnimating)
        {
            mPanning = true;
            (mPanMouseX, mPanMouseY, mPanX, mPanY) = (e.X, e.Y, X, Y);
        }
        return true;
    }

    // 004d2e5c: the pan follows the mouse and keeps the picture over the parent.
    public override bool MsRightDrag(GuiEvent e)
    {
        if (ZoomMode || CenterMode || mButtonTaken || Parent == null)
            return true;
        (X, Y, _, _) = Clamp(e.X - mPanMouseX + mPanX, e.Y - mPanMouseY + mPanY, W, H);
        return true;
    }

    // 004d2fe0
    public override bool MsRightUp(GuiEvent e)
    {
        mButtonTaken = false;
        mPanning = false;
        return true;
    }

    // The control rectangle after the fit of 004cef14 for a parent of that size.
    public static (int X, int Y, int W, int H) Fit(int x, int y, int w, int h, int parentW, int parentH)
    {
        float scale = (float)Math.Min(parentW, parentH) / Math.Max(w, h);
        int nw = (int)(w * (double)scale), nh = (int)(h * (double)scale);
        if (parentW > nw)
            x = Half(parentW - nw);
        if (parentH > nh)
            y = Half(parentH - nh);
        return (x, y, nw, nh);
    }

    // sar by one with the carry added back for a negative value: the exe halves toward zero.
    private static int Half(int v) => v / 2;

    // Tile size on screen: a quarter of the side, plus one when four tiles are narrower than the control [code 004d04e8].
    public static int TileSpan(int side)
    {
        int t = (int)(side * 0.25f);
        return t * 4 < side ? t + 1 : t;
    }

    private static bool Truthy(string v) => v.Length > 0 && !v.Equals("0", StringComparison.Ordinal) && !v.Equals("false", StringComparison.OrdinalIgnoreCase);

    public override void Render(GuiPaint p, int ox, int oy)
    {
        CommandMapPicture? pic = Host?.Hud?.CommandMap;
        if (pic == null)
            return;
        if (!ReferenceEquals(pic, mFitted) && Parent != null)
        {
            mFitted = pic;
            (X, Y, W, H) = Fit(X, Y, W, H, Parent.W, Parent.H);
        }
        if (Parent != null)
            p.SetClip(new GfxRect(ox, oy, ox + Parent.W - 1, oy + Parent.H - 1));
        int gx = ox + X, gy = oy + Y;
        int tw = TileSpan(W), th = TileSpan(H);
        for (int j = 0; j < CommandMapPictureTiles; ++j)
            for (int i = 0; i < CommandMapPictureTiles; ++i)
                p.BitmapStretch(pic.Tiles[j * CommandMapPictureTiles + i], gx + i * tw, gy + j * th, tw, th);
        if (Host?.Hud?.CommandMapMarks is { } marks)
            RenderMarks(p, marks, pic, gx, gy);
    }

    // The sensor circles of 004d04e8 and the five passes of 004cf94c (0 and 1 the objects: icon, label; 2 to 4 the players: icon, order line and
    // mark, name; 4 also the where-am-I mark). Positions are the slot's 1/1024 coordinates scaled to the control, y up.
    private void RenderMarks(GuiPaint p, CommandMapMarks marks, CommandMapPicture pic, int gx, int gy)
    {
        GuiHost host = Host!;
        int filter = int.TryParse(host.GetVar("$pref::mapFilter"), out int f) ? f : 0;
        bool showNames = Truthy(host.GetVar("$pref::mapNames"));
        int SX(CommandMapSlotMark m) => m.X * W >> 10;
        int SY(CommandMapSlotMark m) => H - (m.Y * H >> 10);
        if (Truthy(host.GetVar("$pref::mapSensorRange")))
        {
            float scale = W / pic.W;
            foreach (CommandMapSlotMark m in marks.Slots)
            {
                if ((m.Group != 8 && m.Group != 19) || m.Team != marks.LocalTeam || !m.Active)
                    continue;
                int r = (int)(m.Range * (double)scale), cx = gx + SX(m), cy = gy + SY(m);
                var pt = new (int X, int Y)[32];
                for (int k = 0; k < 32; ++k)
                {
                    double a = k * 0.19634955;
                    pt[k] = ((int)(cx + r * Math.Cos(a)), (int)(cy + r * Math.Sin(a)));
                }
                int color = m.RangeFlag ? 0xf2 : 0xfb;
                for (int k = 0; k < 32; ++k)
                    p.Line(pt[k].X, pt[k].Y, pt[(k + 1) & 31].X, pt[(k + 1) & 31].Y, color);
            }
        }
        var bySlot = new Dictionary<int, CommandMapSlotMark>();
        foreach (CommandMapSlotMark m in marks.Slots)
            bySlot[m.Slot] = m;
        PftFont? font = host.FontByName("if_small.pft");
        PbmaArray? icons = host.ArrayByName("CmdIconsCtrl.pba"), whereAmI = host.ArrayByName("CmdWhereAmI.pba");
        for (int pass = 0; pass < 5; ++pass)
            foreach (CommandMapSlotMark m in marks.Slots)
            {
                if ((filter & 0x80) == 0 && (m.Filter & filter) == 0)
                    continue;
                bool player = m.Group == 3;
                if (pass < 2 ? player : !player)
                    continue;
                int px = gx + SX(m), py = gy + SY(m);
                PbmpBitmap? icon = m.Icon.Length > 0 ? host.BitmapByName(m.Icon + (m.Team == marks.LocalTeam ? "_green.bmp" : "_red.bmp")) : null;
                if (player && m.ClientId == marks.LocalId && pass == 4)
                {
                    int piece = 0, flip = 0;
                    int heading = marks.Heading;
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
                    PbmpBitmap? b = whereAmI?.Bitmaps.Count > piece ? whereAmI.Bitmaps[piece] : null;
                    if (b != null)
                        p.Dev.Blit(GuiPaint.KeyedBitmap(b), px - (b.Width >> 1), py - (b.Height >> 1), (BlitFlip)flip);
                }
                else if (icon != null && (pass == 0 || (pass == 2 && m.ClientId != marks.LocalId)))
                    p.Bitmap(icon, px - (icon.Width >> 1), py - (icon.Height >> 1));
                if (player)
                {
                    if (m.OrderIcon != -1 && (pass == 2 || pass == 4))
                    {
                        (int tx, int ty) = m.OrderTarget != -1 && bySlot.TryGetValue(m.OrderTarget, out CommandMapSlotMark? t) ? (t.X, t.Y) : (m.OrderX, m.OrderY);
                        int sx = gx + (tx * W >> 10), sy = gy + (H - (ty * H >> 10));
                        p.Line(px, py, sx, sy, m.OrderStatus == 1 ? 0xf2 : 0xff);
                        if (m.OrderIcon is > 0 and < 9 && icons?.Bitmaps.Count > m.OrderIcon - 1)
                        {
                            PbmpBitmap b = icons.Bitmaps[m.OrderIcon - 1];
                            p.Bitmap(b, sx - (b.Width >> 1), sy - (b.Height >> 1));
                        }
                    }
                    if (pass >= 3 && font != null)
                        p.Text(font, px + 4, py - 4, m.ClientName);
                }
                else if (pass == 1 && font != null && m.TargetName != null && showNames)
                    p.Text(font, px + 4, py - 4, m.TargetName.Length > 0 ? m.TargetName : m.Description);
            }
    }

    // The where-am-I table at 00629fe8: the heading (0 to 256) up to which a piece of CmdWhereAmI.pba is used, with the flip of the blit.
    private static readonly (int Limit, int Piece, int Flip)[] WhereAmITable =
    {
        (12, 0, 0), (32, 1, 0), (52, 2, 0), (76, 3, 0), (96, 2, 2), (116, 1, 2), (140, 0, 2), (160, 1, 3), (180, 2, 3), (204, 3, 1), (224, 2, 1), (244, 1, 1),
    };

    private const int CommandMapPictureTiles = 4;
}
