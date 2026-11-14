namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

/// <summary>The base fill and border with the dimmed colors of docs/gui.md 2.5 (FearGui::FGControl, 00488a8c).</summary>
public sealed class GuiFGControl : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy, Dimmed);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The fill and a double line frame with notched corners (FearGui::FearGuiBox, 004966c4).</summary>
public sealed class GuiBox : GuiControl
{
    /// <summary>True to draw the box dimmed: the byte at +0x1b0 of the original (004966c4), set by the screen's own code (the filter list is dimmed with no filter).</summary>
    public bool ForceDim { get; set; }

    /// <summary>Draws the double line frame.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <param name="dimmed">True to use the dimmed half of the palette.</param>
    /// <remarks>The palette indices are 0xf0 down to 0xe9 (table at 0062164c), the second half is used when dimmed. Of the four colors c0 to c3 of the set, the outer rule c1 and the inner rule c0 run on every side (each cut 4 pixels short of the corners), c2 colors the corner pixels and c3 a one pixel outline inside, 3 pixels from the edge.</remarks>
    public static void Bevel(GuiPaint p, int x, int y, int w, int h, bool dimmed)
    {
        if (w < 8 || h < 8)
            return;
        var b = dimmed ? 4 : 0;
        int c0 = 0xf0 - b, c1 = 0xef - b, c2 = 0xee - b, c3 = 0xed - b;
        int x1 = x + w - 1, y1 = y + h - 1;
        p.Line(x + 4, y, x1 - 4, y, c1);
        p.Line(x + 4, y + 1, x1 - 4, y + 1, c0);
        p.Line(x + 4, y1 - 1, x1 - 4, y1 - 1, c0);
        p.Line(x + 4, y1, x1 - 4, y1, c1);
        p.Line(x, y + 4, x, y1 - 4, c1);
        p.Line(x + 1, y + 4, x + 1, y1 - 4, c0);
        p.Line(x1, y + 4, x1, y1 - 4, c1);
        p.Line(x1 - 1, y + 4, x1 - 1, y1 - 4, c0);
        foreach (var (px, py) in new[] { (x + 3, y + 1), (x1 - 3, y + 1), (x + 3, y1 - 1), (x1 - 3, y1 - 1), (x + 1, y + 3), (x + 1, y1 - 3),
                     (x1 - 1, y + 3), (x1 - 1, y1 - 3) })
            p.Fill(px, py, 1, 1, c2);
        p.Outline(x + 3, y + 3, w - 6, h - 6, c3);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, FillColor);
        Bevel(p, gx, gy, W, H, Dimmed || ForceDim);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The root of the command screens: command, inventory and objectives (FearGui::FGCommandView, 004a1c78).</summary>
/// <remarks>It clears its extent to black, draws the children, then the metal frame from CmdViewCtrl.pba (LRCmdViewCtrl.pba at low resolution), 15 pieces. The children draw first and the frame pieces over them (the children call 0053a3f4 comes before the piece draws).
/// <list type="bullet">
/// <item><description>Left column: 0 at the top left, 1 below it, the stretched 12, and 2 at the bottom (the stretch takes the remaining height).</description></item>
/// <item><description>Bottom strip: 3 and 4 after the left column, the stretched 13, 5, and 6 at the bottom right (the stretch takes the width left).</description></item>
/// <item><description>Top bar: 10 at x 224, the stretched 11 (the width left less pieces 9 and 8 and 44 pixels), 9, then 8 after a gap of 44 pixels at the right edge, over which the cancel button (570 to 615) draws its own picture.</description></item>
/// <item><description>Right column: under 8 the stretched 14 and 7 above piece 6, aligned to the right edge (the stretch takes the height left).</description></item>
/// </list>
/// </remarks>
public sealed class GuiCommandView : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, 0);
        // The children draw first and the frame pieces over them [code 004a1c78: the children call 0053a3f4 comes before the piece draws].
        RenderChildren(p, gx, gy);
        DrawFrame(p, gx, gy);
    }

    private void DrawFrame(GuiPaint p, int gx, int gy)
    {
        var low = Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
        var a = Host?.ArrayByName(low ? "LRCmdViewCtrl.pba" : "CmdViewCtrl.pba");
        if (a == null || a.Bitmaps.Count < 15)
            return;
        PbmpBitmap B(int i) => a.Bitmaps[i];
        // Left column.
        var y = gy;
        p.Bitmap(B(0), gx, y);
        y += B(0).Height;
        p.Bitmap(B(1), gx, y);
        y += B(1).Height;
        var fill = H - (y - gy) - B(2).Height;
        p.BitmapStretch(B(12), gx, y, B(12).Width, fill);
        y += fill;
        p.Bitmap(B(2), gx, y);
        // Bottom strip.
        var x = gx + B(2).Width;
        p.Bitmap(B(3), x, gy + H - B(3).Height);
        x += B(3).Width;
        p.Bitmap(B(4), x, gy + H - B(4).Height);
        x += B(4).Width;
        var wfill = W - (x - gx) - B(5).Width - B(6).Width;
        p.BitmapStretch(B(13), x, gy + H - B(13).Height, wfill, B(13).Height);
        x += wfill;
        p.Bitmap(B(5), x, gy + H - B(5).Height);
        x += B(5).Width;
        p.Bitmap(B(6), x, gy + H - B(6).Height);
        // Top bar.
        x = gx + 224;
        p.Bitmap(B(10), x, gy);
        x += B(10).Width;
        var tfill = W - (x - gx) - B(9).Width - 44 - B(8).Width;
        p.BitmapStretch(B(11), x, gy, tfill, B(11).Height);
        x += tfill;
        p.Bitmap(B(9), x, gy);
        // Piece 8 follows 44 pixels after piece 9 (the stretch of piece 11 left room for it), at the right edge [code 004a2322 to 004a24c9].
        x += B(9).Width + 44;
        p.Bitmap(B(8), x, gy);
        // Right column.
        var ry = gy;
        ry += B(8).Height;
        var rfill = H - (ry - gy) - B(7).Height - B(6).Height;
        p.BitmapStretch(B(14), gx + W - B(14).Width, ry, B(14).Width, rfill);
        ry += rfill;
        p.Bitmap(B(7), gx + W - B(7).Width, ry);
    }
}

/// <summary>A translucent shade over the extent with a bracket on each side (stub length 6), then the children (FearGui::FearGuiMenu, 004b13ec). The score window and the HUD lists are menus.</summary>
public sealed class GuiMenuPanel : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, HudFrame.ShadeColor, HudFrame.ShadeAlpha);
        HudFrame.Bracket(p, gx, gy, 6, gy + H - 1);
        HudFrame.Bracket(p, gx + W - 1, gy, -6, gy + H - 1);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The modal dialog frame (FearGui::FGDlgBox, 004a31c8, 004a2f2c, 004a30fc).</summary>
public sealed class GuiDlgBox : GuiActive
{
    private static readonly string[] Grab =
    [
        "ExitGameDialog", "RemoveDemoDialog", "RemovePlayerDialog", "MOTDDialog", "MessageDialog", "ServerInfoDialog",
        "HostWarnDialog", "DNSWarningDialog",
    ];

    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => true;

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Name == null || Canvas == null || !Grab.Contains(Name, StringComparer.OrdinalIgnoreCase))
            return;
        if (Canvas.FirstResponder is GuiControl fr && IsAncestorOf(fr))
            return;
        Canvas.SetFirstResponder(this);
    }

    private bool IsAncestorOf(GuiControl c)
    {
        for (var p = c; p != null; p = p.Parent)
            if (p == this)
                return true;
        return false;
    }

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e)
    {
        var names = e.Scan switch
        {
            0x1c => new[] { "DialogReturnButton", "DialogCloseButton" },
            0x01 => ["DialogEscapeButton", "DialogCloseButton"],
            _ => null,
        };
        if (names != null)
        {
            GuiControl root = this;
            while (root.Parent != null)
                root = root.Parent;
            foreach (var n in names)
                if (root.FindByName(n) is GuiActive b)
                {
                    b.DoAction();
                    return true;
                }
        }
        return base.KeyDown(e);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, 0xf4);
        // 004a31c8: the frame is five pictures (DLG_TopLeft, TopEdge, LeftEdge, BottomLeft, BottomEdge), the right side mirrored.
        PbmpBitmap? tl = Host?.BitmapByName("DLG_TopLeft.BMP"), te = Host?.BitmapByName("DLG_TopEdge.BMP"),
            le = Host?.BitmapByName("DLG_LeftEdge.BMP"), bl = Host?.BitmapByName("DLG_BottomLeft.BMP"),
            be = Host?.BitmapByName("DLG_BottomEdge.BMP");
        if (tl != null && te != null && le != null && bl != null && be != null)
        {
            var right = gx + W;
            p.Bitmap(tl, gx, gy);
            var x = gx + tl.Width;
            p.Bitmap(tl, x, gy);
            for (; x < right - tl.Width; x += te.Width)
                p.Bitmap(te, x, gy);
            p.BitmapFlipX(tl, right - tl.Width, gy);
            var bottom = gy + H - bl.Height;
            for (var y = gy + tl.Height; y < bottom; y += le.Height)
            {
                p.Bitmap(le, gx, y);
                p.BitmapFlipX(le, right - le.Width, y);
            }
            p.Bitmap(bl, gx, bottom);
            for (x = gx + bl.Width; x < right - bl.Width; x += be.Width)
                p.Bitmap(be, x, gy + H - be.Height);
            p.BitmapFlipX(bl, right - bl.Width, bottom);
        }
        else
        {
            GuiBox.Bevel(p, gx, gy, W, H, false);
        }
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The shell window background and frame (FearGui::FGShellBorder and MMShellBorder, 0049bd0c).</summary>
/// <remarks>The stored bitmap name is a full-window background picture (Background0.bmp, Background1.bmp, DarkLogo.bmp in the stock files), drawn stretched over the extent. The children draw next, then, when the extent is at least 512 x 384, the metal frame of ShellBorderCtrl.pba (tag 170018, 15 pieces) on top.
/// <list type="bullet">
/// <item><description>Title bar: 9 at x 224, the stretched 8, 10, then 44 pixels, then 13 (the stretch takes the width left).</description></item>
/// <item><description>Right column: 14 below 13, the stretched 7, and 3 at the bottom right (the stretch takes the height left).</description></item>
/// <item><description>Left column: 11 and 12 at the top left, the stretched 6, and 0 at the bottom.</description></item>
/// <item><description>Bottom strip: 1 after 0, the stretched 5, and 4 (the stretch takes the width left, 5 sits at the y of 1).</description></item>
/// </list>
/// </remarks>
public sealed class GuiShellBorder : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        p.BitmapStretch(Host?.BitmapByName(S("bitmapName")), gx, gy, W, H);
        RenderChildren(p, gx, gy);
        if (W >= 512 && H >= 384)
            DrawFrame(p, gx, gy, Host?.ArrayByName("ShellBorderCtrl.pba"));
    }

    private void DrawFrame(GuiPaint p, int gx, int gy, PbmaArray? a)
    {
        if (a == null || a.Bitmaps.Count < 15)
            return;
        PbmpBitmap B(int i) => a.Bitmaps[i];
        // Title bar.
        var x = gx + 224 + B(9).Width;
        p.Bitmap(B(9), gx + 224, gy);
        var tfill = W - (x - gx) - B(10).Width - 44 - B(13).Width;
        p.BitmapStretch(B(8), x, gy, tfill, B(8).Height);
        x += tfill;
        p.Bitmap(B(10), x, gy);
        x += B(10).Width + 44;
        p.Bitmap(B(13), x, gy);
        // Right column.
        var y = gy + B(13).Height;
        p.Bitmap(B(14), gx + W - B(14).Width, y);
        y += B(14).Height;
        var rfill = H - (y - gy) - B(3).Height;
        p.BitmapStretch(B(7), gx + W - B(7).Width, y, B(7).Width, rfill);
        y += rfill;
        p.Bitmap(B(3), gx + W - B(3).Width, y);
        // Left column.
        y = gy;
        p.Bitmap(B(11), gx, y);
        y += B(11).Height;
        p.Bitmap(B(12), gx, y);
        y += B(12).Height;
        var lfill = H - (y - gy) - B(0).Height;
        p.BitmapStretch(B(6), gx, y, B(6).Width, lfill);
        y += lfill;
        p.Bitmap(B(0), gx, y);
        // Bottom strip.
        x = gx + B(0).Width;
        var by = gy + H - B(1).Height;
        p.Bitmap(B(1), x, by);
        x += B(1).Width;
        var bfill = W - (x - gx) - B(4).Width - B(3).Width;
        p.BitmapStretch(B(5), x, by, bfill, B(5).Height);
        x += bfill;
        p.Bitmap(B(4), x, gy + H - B(4).Height);
    }
}

/// <summary>The bitmap stretched to the extent (SimGui::BitmapCtrl and FGBitmapCtrl, 0053fa50).</summary>
public sealed class GuiBitmap : GuiActive
{
    /// <inheritdoc/>
    public override bool WantsTab => false;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var tag = U("bitmapTag", 169998);
        p.BitmapStretch(Host?.BitmapByTag(tag), gx, gy, W, H);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The shell picture button (FearGui::FGUniversalButton, 0049f1e4 to 0049fa94).</summary>
public sealed class GuiUniversalButton : GuiActive
{
    /// <inheritdoc/>
    /// <remarks>Follows 0049f1cc: the hand while the button is active.</remarks>
    public override uint CursorTagAt(int lx, int ly) => Active ? CursorHand : 0;

    private long _flashClock;
    /// <summary>True while the button is on.</summary>
    public bool On { get; set; }
    /// <summary>True while the button flashes.</summary>
    public bool Flash { get; set; }

    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;
    private uint RadioId => U("radioId");
    private bool IsSwitch => U("isSwitch") != 0;
    private uint BtnVersion => U("bodyVersion");
    private string Root => BtnVersion < 2 ? (Host?.TagText(U("bitmapTag")) ?? "") : S("bitmapRoot");

    private bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);

    private PbmpBitmap? Pic(string suffix, bool lowRes)
    {
        if (Host == null || Root.Length == 0)
            return null;
        return Host.BitmapByName((lowRes ? "LR_" : "") + Root + suffix + ".bmp");
    }

    /// <summary>Does the same work as a click: the radio is set and the command runs (slot 0xd8, 0049f81c).</summary>
    public void Activate() => Action();

    /// <inheritdoc/>
    public override void Wake()
    {
        On = U("on") != 0;
        var lr = LowRes;
        var df = Pic("_DF", lr) ?? Pic("_DF", false);
        if (df != null)
        {
            W = df.Width;
            H = df.Height;
        }
        if (lr && Pic("_DF", true) != null)
        {
            X = (int)U("lowResX");
            Y = (int)U("lowResY");
        }
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (U("mirrorVar") != 0 && ConsoleVar.Length > 0 && Host != null)
            // 0049fa3c compares with stricmpAscii (00488150): a bool cell reads back "True", which must count as on.
            On = string.Equals(Host.GetVar(ConsoleVar), "TRUE", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override string GetValue() => RadioId != 0 || IsSwitch ? (On ? "TRUE" : "FALSE") : "";

    /// <inheritdoc/>
    protected override void ApplyValue(string v)
    {
        if (RadioId != 0 || IsSwitch)
            On = string.Equals(v, "TRUE", StringComparison.OrdinalIgnoreCase);   // 0049fa00: stricmp against TRUE, "1" is off
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (Active)
            Host?.Audio.Play(IsSwitch && !On ? "Button2.wav" : "Button1.wav");
        return base.MsDown(e);
    }

    /// <inheritdoc/>
    protected override void Action()
    {
        if (!Active)
            return;
        var before = Canvas?.Content;
        if (RadioId != 0)
        {
            On = true;
            BroadcastSibling(3);
            ShowPage(true);
        }
        else if (IsSwitch)
        {
            On = !On;
            WriteVariable(On ? "TRUE" : "FALSE");
            if (!On)
                Host?.Audio.Play("Button1.wav");
        }
        if (IsSwitch && !On && AltCommand.Length > 0)
            Host?.Eval(AltCommand);
        else if (Command.Length > 0)
            Host?.Eval(Command);
        CallOnAction();
        if (Canvas?.Content == before)
            PostMessage();
    }

    private void ShowPage(bool show)
    {
        if (MessageTag == 0 || Canvas == null)
            return;
        GuiControl root = this;
        while (root.Parent != null)
            root = root.Parent;
        var page = root.FindById(MessageTag) ?? Canvas.Content?.FindById(MessageTag);
        page?.SetVisible(show);
    }

    /// <inheritdoc/>
    public override bool Message(GuiControl? sender, int tag)
    {
        if (tag == 3 && sender is GuiUniversalButton s && s.RadioId == RadioId && RadioId != 0 && s != this)
        {
            On = false;
            ShowPage(false);
            return true;
        }
        return base.Message(sender, tag);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var lr = LowRes;
        var dimmed = Dimmed;
        PbmpBitmap? df = Pic("_DF", lr), on = Pic("_ON", lr), na = Pic("_NA", lr), bd = Pic("_BD", lr), bo = Pic("_BO", lr);
        PbmpBitmap? pic;
        var radioMark = RadioId != 0 && IsSwitch && On && !Pressed && BtnVersion >= 2;
        if (dimmed)
            pic = !On ? bd ?? na : bo ?? bd ?? na;
        else if (!Active)
            pic = na ?? bd;
        else if (Pressed || On)   // the on flag alone selects _ON, a switch or not (CmdInventory favourite 1 starts on, not a switch)
            pic = on;
        else if (Flash)
        {
            if (_flashClock == 0)
                _flashClock = Host?.TickMs ?? 0;
            pic = (((Host?.TickMs ?? 0) - _flashClock) >> 9) % 2 == 1 ? on : df;
        }
        else
            pic = df;
        if (RadioId != 0 && IsSwitch && On && !Pressed && BtnVersion >= 2 && !dimmed && Active)
            pic = df;
        pic ??= na ?? df;
        p.Bitmap(pic ?? df, gx, gy);
        if (radioMark && Host != null)
            p.Bitmap(Host.BitmapByName(dimmed || !Active ? "RAD_CheckX_NA.BMP" : "RAD_CheckX_ON.BMP"), gx + 1, gy);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>A row of the controls screen (FearGui::CFGButton).</summary>
/// <remarks>Follows draw 0049d74c, action 0049d6e4 and add 0049e460. The action is onAction, then the message tag. The row shows the key of its binding in the action map the row names (0049df98).</remarks>
public sealed class GuiCFGButton : GuiSimpleText
{
    /// <inheritdoc/>
    /// <remarks>Follows 0049d400: the hand while the button is active.</remarks>
    public override uint CursorTagAt(int lx, int ly) => Active ? CursorHand : 0;

    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;
    /// <summary>True while the row waits for a key to bind.</summary>
    public bool WaitingForKey { get; set; }
    /// <summary>The binding text (+0x367): modifier codes 1 to 3 then the key name, empty when the map has no such binding.</summary>
    public string Binding { get; set; } = "";

    // The fonts of 0049e460: +0x1cc, +0x1d0, +0x1d4 and the shadow +0x2e0 (tags 150114, 150124, 150134, 150172).
    private PftFont? FontA => Host?.FontByTag(150114);
    private PftFont? FontB => Host?.FontByTag(150124);
    private PftFont? FontC => Host?.FontByTag(150134);
    private PftFont? FontShadow => Host?.FontByTag(150172);

    private PbmpBitmap? Pic(string name) => Host?.BitmapByName(name);

    /// <inheritdoc/>
    protected override void Action()
    {
        CallOnAction();
        PostMessage();
    }

    /// <inheritdoc/>
    public override void Wake()
    {
        base.Wake();
        // 0049e460: the extent is the idle row picture, widened to the label (+2), and the label height above it.
        var df = Pic("CFG_Controls_DF.BMP");
        var fb = FontB;
        if (df != null && fb != null)
        {
            W = Math.Max(df.Width, GuiPaint.TextWidth(fb, ResolvedText()) + 2);
            H = GuiPaint.FontHeight(fb) + df.Height;
        }
        RefreshBinding();
    }

    /// <inheritdoc/>
    public override void PreRender() => RefreshBinding();

    // 0049e7dc: the text of the binding the row describes.
    private void RefreshBinding()
    {
        var map = S("actionMap");
        var m = Host?.Actions?.FindMap(ClientCommands.ActionMapName(map));
        Binding = map.Length == 0 ? "" : BindingText.For(m, Command, (int)U("makeAction"), BitConverter.UInt32BitsToSingle(U("makeValue")),
            AltCommand, (int)U("breakAction"), BitConverter.UInt32BitsToSingle(U("breakValue")), U("actionFlags"));
    }

    private static int Centered(int space) => space / 2;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var dimmed = Dimmed;
        PftFont? fa = FontA, fb = FontB, fc = FontC, shadow = FontShadow;
        var name = ResolvedText();
        var pic = dimmed ? Pic("CFG_Controls_NA.BMP") : Pressed || WaitingForKey ? Pic("CFG_Controls_ON.BMP") : Pic("CFG_Controls_DF.BMP");
        var fh = GuiPaint.FontHeight(fb);
        if (pic != null)
            p.Bitmap(pic, gx + Centered(W - pic.Width), gy + fh - 1);
        var tx = gx + Centered(W - (GuiPaint.TextWidth(fa, name) + 1));
        p.Text(shadow, tx - 1, gy + 1, name);
        p.Text(dimmed ? fc : Binding.Length > 0 ? fb : fc, tx, gy, name);
        var by = gy + fh + 1;
        var b = Binding;
        if (b.Length == 0)
            return;
        int codes = 0, iconsW = 0;
        PbmpBitmap? shiftPic = Pic("I_Shift.BMP"), altPic = Pic("I_Alt.BMP"), ctrlPic = Pic("I_Ctrl.BMP");
        while (codes < b.Length && b[codes] >= 1 && b[codes] < 4)
        {
            iconsW += (shiftPic?.Width ?? 0) + 2;
            ++codes;
        }
        var key = b[codes..];
        if (codes == 3 && key.Length > 3)
            key = key[..3];                       // 0049d74c writes a terminator at the fourth character
        var x = gx + Centered(W - (iconsW + GuiPaint.TextWidth(fa, key) + 1));
        for (var i = 0; i < codes; ++i)
        {
            var ic = b[i] == 1 ? shiftPic : b[i] == 2 ? ctrlPic : altPic;
            if (!dimmed && ic != null)
                p.Bitmap(ic, x, by + 4);
            x += (ic?.Width ?? 0) + 2;
        }
        p.Text(shadow, x - 1, by + 1, key);
        p.Text(dimmed ? fc : fa, x, by, key);
    }
}

/// <summary>A picture switch for a target control id (FearGui::FearGuiSwitch).</summary>
public sealed class GuiSwitch : GuiActive
{
    /// <summary>True while the target is shown.</summary>
    public bool On { get; set; } = true;
    /// <inheritdoc/>
    public override bool WantsTab => false;

    /// <inheritdoc/>
    protected override void Action()
    {
        On = !On;
        var t = Canvas?.Content?.FindById(U("targetId"));
        t?.SetVisible(On);
        base.Action();
    }
}

/// <summary>One of a set of radio buttons mirroring a console variable (FearGui::FearGuiRadio).</summary>
public sealed class GuiRadio : GuiActive
{
    /// <summary>Gets a value that is true when the button is selected.</summary>
    public bool Selected => U("selected") != 0;
    /// <inheritdoc/>
    protected override void Action()
    {
        F["selected"] = (byte)1;
        BroadcastSibling(3);
        base.Action();
    }

    /// <inheritdoc/>
    public override bool Message(GuiControl? sender, int tag)
    {
        if (tag == 3 && sender is GuiRadio)
        {
            F["selected"] = (byte)0;
            return true;
        }
        return base.Message(sender, tag);
    }
}

/// <summary>A menu or tab item (FearGui::FGMenuItem and FGTab, 004959c8, 00495bd0): selecting an item shows its tab page and hides the pages of the other items of the same menu. An item without a page expands or collapses its children.</summary>
/// <remarks>The page selector itself was not traced (Q10), this follows the stock Options screen. The tab flag marks the shown item (white text). Follows draw 00495d44 and 00495bec: the header text starts after the width of MNU_Expand_DF.BMP (the picture itself is not drawn), centred on that picture's height, in sf_orange214_10 (sf_white_10 when the tab flag is set, sf_orange255_10 for an item with children, sf_grey100_10b while a dialog dims the screen), with a black sf_black_10 shadow one pixel left and one down. The expanded children are drawn below, indented by the picture width, each below the one before. Fonts are loaded by the control's open (00495ef0).</remarks>
public sealed class GuiMenuItem : GuiSimpleText
{
    /// <inheritdoc/>
    /// <remarks>Follows 004958bc (menu item) and 004ccda8 (tab): always the hand.</remarks>
    public override uint CursorTagAt(int lx, int ly) => CursorHand;

    /// <inheritdoc/>
    public override bool WantsTab => false;

    /// <inheritdoc/>
    protected override void Action()
    {
        var page = U("tabPage");
        GuiControl menu = this;
        while (menu.Parent != null && !(menu.Parent is GuiActive && menu.Parent.ClassName.EndsWith("MenuCtrl", StringComparison.Ordinal)))
            menu = menu.Parent;
        var scope = menu.Parent ?? menu;
        if (page != 0)
        {
            var top = Canvas?.Top;
            foreach (GuiControl item in scope.Descendants().OfType<GuiMenuItem>())
                if (item != this)
                {
                    item.F["tabSet"] = (byte)0;
                    if (item.U("tabPage") != 0)
                        top?.FindById(item.U("tabPage"))?.SetVisible(false);
                }
            F["tabSet"] = (byte)1;
            top?.FindById(page)?.SetVisible(true);
            Canvas?.RebuildTabList();
        }
        else
            F["expanded"] = (byte)(U("expanded") != 0 ? 0 : 1);
        base.Action();
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var pic = Host?.BitmapByName("MNU_Expand_DF.BMP");
        int indent = pic?.Width ?? 19, rowH = pic?.Height ?? 19;
        var s = ResolvedText();
        if (s.Length > 0 && Host != null)
        {
            var main = Dimmed ? "sf_grey100_10b.pft" : Children.Count > 0 ? "sf_orange255_10.pft" : U("tabSet") != 0 ? "sf_white_10.pft" : "sf_orange214_10.pft";
            var f = Host.FontByName(main);
            var ty = gy + ((rowH - GuiPaint.FontHeight(f)) / 2);
            p.Text(Host.FontByName("sf_black_10.pft"), gx + indent - 1, ty - 1, s);
            p.Text(f, gx + indent, ty - 2, s);
        }
        if (Children.Count > 0 && U("expanded") != 0)
        {
            var y = gy + rowH;
            foreach (var c in Children)
            {
                c.Render(p, gx + indent - c.X, y - c.Y);
                y += c.H;
            }
        }
    }
}

/// <summary>The slider (SimGui::Slider and FGSlider, 004ca3a0 to 004cad0c).</summary>
public sealed class GuiSlider : GuiActive
{
    /// <inheritdoc/>
    /// <remarks>Follows 004cb150 (FGSlider): the hand while active.</remarks>
    public override uint CursorTagAt(int lx, int ly) => ClassName == "FearGui::FGSlider" && Active ? CursorHand : 0;

    private int _pos;      // pixels
    private bool _dragging;
    private int _grab;
    /// <summary>The step count, 0 or 1 for a continuous slider.</summary>
    public int Discrete { get; set; }   // step count, 0 or 1 = continuous

    /// <summary>Gets the thumb width: 30 pixels (the disassembly at 004ca1ee stores 0x1e at +0x1d0).</summary>
    /// <remarks>The right end of its travel is the extent less the width of piece 5 of ShellSliderCtrl.pba less the thumb (004ca760).</remarks>
    private static int Thumb => 30;
    // The thumb starts after the left cap.
    private int LeftCap => Pieces?.Bitmaps.Count > 2 ? Pieces.Bitmaps[2].Width : 0;
    private int RightEnd => Math.Max(1, W - (Pieces?.Bitmaps.Count > 5 ? Pieces.Bitmaps[5].Width : 11) - Thumb - LeftCap);
    private PbmaArray? Pieces => Host?.ArrayByName("ShellSliderCtrl.pba");
    private float MaxValue => BitConverter.UInt32BitsToSingle(U("maxValue"));

    /// <inheritdoc/>
    public override void Wake()
    {
        Discrete = U("sliderVersion") >= 2 ? (int)U("minValue") : 0;
        var frac = BitConverter.UInt32BitsToSingle(U("value"));
        var max = U("sliderVersion") >= 2 ? MaxValue : 1f;
        if (max != 0)
            _pos = (int)(Math.Clamp(frac / max, 0, 1) * RightEnd);
        if (ConsoleVar.Length > 0 && Host != null)
            VariableChanged(Host.GetVar(ConsoleVar));
    }

    /// <summary>Sets the step count and snaps the thumb to a step.</summary>
    /// <param name="count">The step count, 1 or less for a continuous slider.</param>
    public void SetDiscrete(int count)
    {
        Discrete = count <= 1 ? 0 : count;
        if (Discrete > 0)
            _pos = Snap(_pos);
    }

    private int Snap(int pos)
    {
        if (Discrete <= 1)
            return pos;
        var step = (double)RightEnd / (Discrete - 1);
        return (int)Math.Round(Math.Round(pos / step) * step);
    }

    /// <inheritdoc/>
    public override string GetValue()
    {
        if (Discrete > 1)
            return ((int)Math.Round((double)_pos / RightEnd * (Discrete - 1))).ToString(CultureInfo.InvariantCulture);
        var max = U("sliderVersion") >= 2 ? MaxValue : 1f;
        return ((double)_pos / RightEnd * max).ToString("F6", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    protected override void ApplyValue(string v)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return;
        if (Discrete > 1)
            _pos = (int)Math.Round(d / (Discrete - 1) * RightEnd);
        else
        {
            var max = U("sliderVersion") >= 2 ? MaxValue : 1f;
            _pos = max != 0 ? (int)(d / max * RightEnd) : 0;
        }
        _pos = Math.Clamp(_pos, 0, RightEnd);
    }

    private void Changed()
    {
        WriteVariable(GetValue());
        RunCommand();
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        var (lx, _) = GlobalToLocal(e.X, 0);
        Canvas?.LockMouse(this);
        _dragging = true;
        var unit = Discrete > 1 ? Math.Max(1, RightEnd / (Discrete - 1)) : 1;
        if (lx < 8)
            _pos = Snap(Math.Max(0, _pos - unit));
        else if (lx > W - 8)
            _pos = Snap(Math.Min(RightEnd, _pos + unit));
        else if (lx < LeftCap + _pos)
            _pos = Snap(Math.Max(0, _pos - (Discrete > 1 ? unit : RightEnd / 8)));
        else if (lx >= LeftCap + _pos + Thumb)
            _pos = Snap(Math.Min(RightEnd, _pos + (Discrete > 1 ? unit : RightEnd / 8)));
        else
            _grab = lx - LeftCap - _pos;
        return base.MsDown(e);
    }

    /// <inheritdoc/>
    public override bool MsDrag(GuiEvent e)
    {
        if (_dragging)
        {
            var (lx, _) = GlobalToLocal(e.X, 0);
            _pos = Math.Clamp(lx - _grab - LeftCap, 0, RightEnd);
            Changed();
        }
        return base.MsDrag(e);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 004ca5ec: the release unlocks the mouse, snaps a discrete slider and evaluates the command. It runs no action.</remarks>
    public override bool MsUp(GuiEvent e)
    {
        if (_dragging)
        {
            // [disassembly 004ca5ec] up releases the lock, snaps a discrete slider and evaluates the command; no action.
            _dragging = false;
            Pressed = false;
            Canvas?.UnlockMouse(this);
            _pos = Snap(_pos);
            Changed();
            return true;
        }
        return base.MsUp(e);
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var a = Pieces;
        if (a != null && a.Bitmaps.Count >= 15)
        {
            // Draw [code 004ca7b8], pieces of ShellSliderCtrl.pba: left cap 2 (0 dimmed), the stretched bar 11 (9 dimmed), the right
            // cap 5 (3 dimmed); then, not dimmed, the 30 pixel thumb at its position: 7 + the stretched 6 + 8 (13, 12, 14 while dragged).
            var dim = Dimmed;
            PbmpBitmap B(int i) => a.Bitmaps[i];
            PbmpBitmap lc = B(dim ? 0 : 2), rc = B(dim ? 3 : 5), bar = B(dim ? 9 : 11);
            p.Bitmap(lc, gx, gy);
            var fill = W - lc.Width - B(5).Width;
            p.BitmapStretch(bar, gx + lc.Width, gy, fill, bar.Height);
            p.Bitmap(rc, gx + lc.Width + fill, gy);
            if (!dim)
            {
                PbmpBitmap tl = B(_dragging ? 13 : 7), tm = B(_dragging ? 12 : 6), tr = B(_dragging ? 14 : 8);
                var x = gx + LeftCap + _pos;
                p.Bitmap(tl, x, gy);
                x += tl.Width;
                var mid = Thumb - tl.Width - B(8).Width;
                p.BitmapStretch(tm, x, gy, mid, tm.Height);
                p.Bitmap(tr, x + mid, gy);
            }
        }
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The progress bar (SimGui::ProgressCtrl, 005448d8): fills a rectangle in proportion to its value, 0 to 1.</summary>
public sealed class GuiProgress : GuiActive
{
    /// <summary>The value, 0 to 1.</summary>
    public double Value { get; set; }
    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    protected override void ApplyValue(string v) => Value = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 0, 1) : 0;
    /// <inheritdoc/>
    public override string GetValue() => Value.ToString("F6", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        // The draw [code 005448d8]: the finished part in the control's fill colour (+0x76), then the whole extent outlined in the
        // border colour (+0x79); no background fill.
        // the value is a 32 bit float (0.9 of 240 pixels is 215, not 216)
        p.Fill(gx, gy, (int)(W * (double)(float)Value), H, FillColor);
        p.Outline(gx, gy, W, H, BorderColor);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The timer (SimGui::TimerCtrl): after the initial alarm, then every repeat interval, it posts its message tag.</summary>
public sealed class GuiTimer : GuiControl
{
    private long _next = -1;
    /// <summary>The number of times the timer fired.</summary>
    public int Fired { get; set; }

    /// <inheritdoc/>
    public override void Wake()
    {
        var init = BitConverter.UInt32BitsToSingle(U("initialAlarm"));
        _next = (Host?.TickMs ?? 0) + (long)(init * 1000);
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (_next < 0 || Host == null || Host.TickMs < _next)
            return;
        ++Fired;
        var rep = BitConverter.UInt32BitsToSingle(U("repeatAlarm"));
        _next = rep > 0 ? Host.TickMs + (long)(rep * 1000) : -1;
        if (U("messageTag") != 0)
            Next?.Message(this, (int)U("messageTag"));
    }
}

/// <summary>The palette selector (SimGui::PaletteCtrl and FGPaletteCtrl): it selects the active palette on wake.</summary>
public sealed class GuiPalette : GuiControl
{
    /// <inheritdoc/>
    public override void Wake()
    {
        if (Host?.TagText(U("paletteTag")) is string t && Host.OnPalette != null)
            Host.OnPalette(GuiHost.FirstItem(t));
    }
}

/// <summary>The wrapped text in the extent (SimGui::TextWrap and TextFormat).</summary>
public sealed class GuiTextWrap : GuiActive
{
    /// <summary>The text.</summary>
    public string Text { get; set; } = "";
    /// <summary>True for TextFormat, FGTextFormat and FearGuiFormattedText, which lay their text out with <see cref="GuiTextFormat"/>. TextWrap draws its text as is.</summary>
    public bool Formatted { get; set; }
    /// <inheritdoc/>
    public override bool WantsTab => false;

    /// <summary>Gets the font of text without a font tag (slot 0).</summary>
    /// <remarks>FearGuiFormattedText has no font field: its prepare handler (00489738) fills the font slots 0 to 2 with tags 150111, 150121 and 150161, or all three with 150131 when the control sits under another dialog's dimming rule (<see cref="Dim"/>).</remarks>
    private PftFont? BaseFont => Formatted
        ? Host?.FontByTag(Dim ? 150131u : 150111u) ?? Host?.FontByName("sf_white_10.pft")
        : Host?.FontByTag(U("fontTag")) ?? Host?.FontByName("sf_white_10.pft");

    /// <inheritdoc/>
    protected override void ApplyValue(string v)
    {
        Text = v;
        FitHeight();
    }

    /// <inheritdoc/>
    public override string GetValue() => Text;

    /// <inheritdoc/>
    public override void Wake()
    {
        if (Host?.TagText(U("textTag")) is string t)
            Text = t;
        FitHeight();
    }

    /// <summary>Gets a value that is true when the text is laid out with the TextFormat routine (005370b8) and three fonts (slots 0 to 2, the rest empty).</summary>
    /// <remarks>FearGuiFormattedText sets them from its prepare handler (00489738), with the dimmed table under another dialog. FGTextFormat sets them from its set value handler (004ce3f8): tags 150111, 150121, 150161, never dimmed. FGTextFormat sets its height to the layout height + 4, the FearGuiFormattedText parent size handler (0048983c) to the layout height.</remarks>
    private bool UsesLayout => Formatted;
    private bool Dim => Dimmed && ClassName == "FearGuiFormattedText";
    private GuiTextFormat? _layout;
    private string? _laidText;
    private int _laidWidth = -1;
    private bool _laidDimmed;

    private GuiTextFormat? Layout()
    {
        if (Host == null)
            return null;
        var text = Text;
        if (_layout == null || _laidText != text || _laidWidth != W || _laidDimmed != Dim)
        {
            var fonts = new PftFont?[GuiTextFormat.FontCount];
            fonts[0] = BaseFont;
            fonts[1] = Host.FontByTag(Dim ? 150131u : 150121u);
            fonts[2] = Host.FontByTag(Dim ? 150131u : 150161u);
            _layout = GuiTextFormat.Layout(text, W, fonts, Host.BitmapByName, n => Host.GetVar("$" + n));
            _laidText = text;
            _laidWidth = W;
            _laidDimmed = Dim;
        }
        return _layout;
    }

    /// <summary>Fits the height to the wrapped text at the control's width.</summary>
    /// <remarks>A formatted text control takes the height of its wrapped text at its width (parent size handler 0048983c), so a control stored with height 0 (the progress text of Loading.gui) still shows what a script puts into it. The parent clips each child to its extent.</remarks>
    private void FitHeight()
    {
        if (!Formatted || Host == null || Text.Length == 0)
            return;
        if (Layout() is GuiTextFormat l)
        {
            if (ClassName == "FearGui::FGTextFormat")
                H = l.Height + 4;
            else if (l.Height > H)
                H = l.Height;
        }
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var f = BaseFont;
        if (UsesLayout && Host != null)
            Layout()?.Draw(p, gx, gy);
        else if (f != null)
        {
            var lh = GuiPaint.FontHeight(f) + (int)U("lineSpacing");
            var y = gy;
            foreach (var line in Wrap(f, Text, W))
            {
                p.Text(f, gx, y, line);
                y += lh;
            }
        }
        RenderChildren(p, gx, gy);
    }

    /// <summary>Wraps text at spaces to a width.</summary>
    /// <param name="f">The font.</param>
    /// <param name="text">The text. A line break starts a new line.</param>
    /// <param name="width">The width in pixels.</param>
    /// <returns>The lines.</returns>
    public static List<string> Wrap(PftFont f, string text, int width)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var cur = "";
            foreach (var w in para.Split(' '))
            {
                var t = cur.Length == 0 ? w : cur + " " + w;
                if (cur.Length > 0 && GuiPaint.TextWidth(f, t) > width)
                {
                    lines.Add(cur);
                    cur = w;
                }
                else
                    cur = t;
            }
            lines.Add(cur);
        }
        return lines;
    }
}
