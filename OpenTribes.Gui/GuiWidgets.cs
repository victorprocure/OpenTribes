// docs/gui.md 3.3: containers, dialog frames, bitmaps, the shell buttons, switches, menus, sliders, progress, timers.
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// FearGui::FGControl [code 00488a8c]: the base fill and border with the dimmed colours of 2.5.
public sealed class GuiFGControl : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy, Dimmed);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FearGuiBox [code 004966c4]: fill and a 3D frame.
public sealed class GuiBox : GuiControl
{
    // The eight palette indices 0xf0 down to 0xe9; the second half is used when dimmed. The frame geometry (top, left,
    // bottom, right lines, an inner outline in the fourth colour, softened corners) is inferred.
    public static void Bevel(GuiPaint p, int x, int y, int w, int h, bool dimmed)
    {
        if (w < 4 || h < 4)
            return;
        int b = dimmed ? 4 : 0;
        int C(int i) => 0xf0 - (b + i);
        p.Line(x + 1, y, x + w - 2, y, C(0));
        p.Line(x, y + 1, x, y + h - 2, C(1));
        p.Line(x + 1, y + h - 1, x + w - 2, y + h - 1, C(2));
        p.Line(x + w - 1, y + 1, x + w - 1, y + h - 2, C(3));
        p.Outline(x + 1, y + 1, w - 2, h - 2, C(3));
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Opaque != 0)
            p.Fill(gx, gy, W, H, FillColor);
        Bevel(p, gx, gy, W, H, Dimmed);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FGDlgBox [code 004a31c8, 004a2f2c, 004a30fc]: the modal dialog frame.
public sealed class GuiDlgBox : GuiActive
{
    private static readonly string[] kGrab =
    {
        "ExitGameDialog", "RemoveDemoDialog", "RemovePlayerDialog", "MOTDDialog", "MessageDialog", "ServerInfoDialog",
        "HostWarnDialog", "DNSWarningDialog",
    };

    public override bool WantsTab => false;
    public override bool CanTake() => true;

    public override void PreRender()
    {
        if (Name == null || Canvas == null || !kGrab.Contains(Name, StringComparer.OrdinalIgnoreCase))
            return;
        if (Canvas.FirstResponder is GuiControl fr && IsAncestorOf(fr))
            return;
        Canvas.SetFirstResponder(this);
    }

    private bool IsAncestorOf(GuiControl c)
    {
        for (GuiControl? p = c; p != null; p = p.Parent)
            if (p == this)
                return true;
        return false;
    }

    public override bool KeyDown(GuiEvent e)
    {
        string[]? names = e.Scan switch
        {
            0x1c => new[] { "DialogReturnButton", "DialogCloseButton" },
            0x01 => new[] { "DialogEscapeButton", "DialogCloseButton" },
            _ => null,
        };
        if (names != null)
        {
            GuiControl root = this;
            while (root.Parent != null)
                root = root.Parent;
            foreach (string n in names)
                if (root.FindByName(n) is GuiActive b)
                {
                    b.DoAction();
                    return true;
                }
        }
        return base.KeyDown(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, 0xf4);
        GuiBox.Bevel(p, gx, gy, W, H, false);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FGShellBorder / MMShellBorder: the stored bitmap name is a full-window background picture (Background0.bmp,
// Background1.bmp, DarkLogo.bmp in the stock files), drawn stretched over the extent. A name ending in `.pba` is read as
// a frame set drawn in nine parts (the part order is inferred; no stock file uses it).
public sealed class GuiShellBorder : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        string name = S("bitmapName");
        if (name.EndsWith(".pba", StringComparison.OrdinalIgnoreCase))
            DrawFrame(p, gx, gy, Host?.ArrayByName(name));
        else
            p.BitmapStretch(Host?.BitmapByName(name), gx, gy, W, H);
        RenderChildren(p, gx, gy);
    }

    private void DrawFrame(GuiPaint p, int gx, int gy, PbmaArray? arr)
    {
        if (arr == null || arr.Bitmaps.Count < 9)
            return;
        PbmpBitmap tl = arr.Bitmaps[0], t = arr.Bitmaps[1], tr = arr.Bitmaps[2], l = arr.Bitmaps[3], r = arr.Bitmaps[5],
            bl = arr.Bitmaps[6], b = arr.Bitmaps[7], br = arr.Bitmaps[8];
        for (int x = gx + tl.Width; x < gx + W - tr.Width; x += Math.Max(1, t.Width))
            p.Bitmap(t, x, gy);
        for (int x = gx + bl.Width; x < gx + W - br.Width; x += Math.Max(1, b.Width))
            p.Bitmap(b, x, gy + H - b.Height);
        for (int y = gy + tl.Height; y < gy + H - bl.Height; y += Math.Max(1, l.Height))
        {
            p.Bitmap(l, gx, y);
            p.Bitmap(r, gx + W - r.Width, y);
        }
        p.Bitmap(tl, gx, gy);
        p.Bitmap(tr, gx + W - tr.Width, gy);
        p.Bitmap(bl, gx, gy + H - bl.Height);
        p.Bitmap(br, gx + W - br.Width, gy + H - br.Height);
    }
}

// SimGui::BitmapCtrl / FGBitmapCtrl [code 0053fa50]: the bitmap stretched to the extent.
public sealed class GuiBitmap : GuiActive
{
    public override bool WantsTab => false;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        uint tag = U("bitmapTag", 169998);
        p.BitmapStretch(Host?.BitmapByTag(tag), gx, gy, W, H);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FGUniversalButton [code 0049f1e4 .. 0049fa94]: the shell picture button.
public sealed class GuiUniversalButton : GuiActive
{
    private static readonly string[] kSuffixes = { "_DF", "_HI", "_MD", "_MH", "_ON", "_NA", "_BD", "_BO" };
    private long mFlashClock;
    public bool On, Flash;

    public override bool WantsTab => false;
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

    public override void Wake()
    {
        On = U("on") != 0;
        bool lr = LowRes;
        PbmpBitmap? df = Pic("_DF", lr) ?? Pic("_DF", false);
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

    public override void PreRender()
    {
        if (U("mirrorVar") != 0 && ConsoleVar.Length > 0 && Host != null)
            On = Host.GetVar(ConsoleVar) == "TRUE";
    }

    public override string GetValue() => RadioId != 0 || IsSwitch ? (On ? "TRUE" : "FALSE") : "";

    protected override void ApplyValue(string v)
    {
        if (RadioId != 0 || IsSwitch)
            On = v == "TRUE" || v == "1";
    }

    public override bool MsDown(GuiEvent e)
    {
        if (Active)
            Host?.Audio.Play(IsSwitch && !On ? "Button2.wav" : "Button1.wav");
        return base.MsDown(e);
    }

    protected override void Action()
    {
        if (!Active)
            return;
        GuiControl? before = Canvas?.Content;
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
        GuiControl? page = root.FindById(MessageTag) ?? Canvas.Content?.FindById(MessageTag);
        page?.SetVisible(show);
    }

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

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        bool lr = LowRes;
        bool dimmed = Dimmed;
        PbmpBitmap? df = Pic("_DF", lr), on = Pic("_ON", lr), na = Pic("_NA", lr), bd = Pic("_BD", lr), bo = Pic("_BO", lr);
        PbmpBitmap? pic;
        bool radioMark = RadioId != 0 && On && !Pressed && BtnVersion >= 2;
        if (dimmed)
            pic = !On ? bd ?? na : bo ?? bd ?? na;
        else if (!Active)
            pic = na ?? bd;
        else if (Pressed || IsSwitch && On)
            pic = on;
        else if (Flash)
        {
            if (mFlashClock == 0)
                mFlashClock = Host?.TickMs ?? 0;
            pic = (((Host?.TickMs ?? 0) - mFlashClock) >> 9) % 2 == 1 ? on : df;
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

// FearGui::CFGButton [code 0049d74c, 0049d6e4]: a row of the controls screen. Action: onAction then the message tag.
public sealed class GuiCFGButton : GuiSimpleText
{
    public override bool WantsTab => false;
    public override bool CanTake() => false;
    public bool WaitingForKey;

    protected override void Action()
    {
        CallOnAction();
        PostMessage();
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        p.Fill(gx, gy, W, H, Pressed || WaitingForKey ? 0xf6 : Dimmed ? 0xf3 : 0xf4);
        p.Outline(gx, gy, W, H, 0xfa);
        PftFont? f = Host?.FontByName("sf_white_10.pft");
        string s = ResolvedText();
        if (f != null && s.Length > 0)
            p.Text(f, gx + (W - GuiPaint.TextWidth(f, s)) / 2, gy + (H - GuiPaint.FontHeight(f)) / 2, s);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::FearGuiSwitch: a picture switch for a target control id.
public sealed class GuiSwitch : GuiActive
{
    public bool On = true;
    public override bool WantsTab => false;

    protected override void Action()
    {
        On = !On;
        GuiControl? t = Canvas?.Content?.FindById(U("targetId"));
        t?.SetVisible(On);
        base.Action();
    }
}

// FearGui::FearGuiRadio: one of a set mirroring a console variable.
public sealed class GuiRadio : GuiActive
{
    public bool Selected => U("selected") != 0;
    protected override void Action()
    {
        F["selected"] = (byte)1;
        BroadcastSibling(3);
        base.Action();
    }

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

// FearGui::FGMenuItem / FGTab: select the tab page and hide the pages of the other tabs of the set.
public sealed class GuiMenuItem : GuiSimpleText
{
    protected override void Action()
    {
        if (U("tabSet") != 0 && Parent != null)
        {
            foreach (GuiControl s in Parent.Children)
                if (s is GuiMenuItem m && m != this && m.U("tabSet") != 0)
                    Canvas?.Content?.FindById(m.U("tabPage"))?.SetVisible(false);
            Canvas?.Content?.FindById(U("tabPage"))?.SetVisible(true);
        }
        F["expanded"] = (byte)(U("expanded") != 0 ? 0 : 1);
        base.Action();
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        DrawText(p, gx, gy);
        if (U("expanded") != 0 || Children.Count == 0)
            RenderChildren(p, gx, gy);
    }
}

// SimGui::Slider / FGSlider [code 004ca3a0 .. 004cad0c].
public sealed class GuiSlider : GuiActive
{
    private int mPos;      // pixels
    private bool mDragging;
    private int mGrab;
    public int Discrete;   // step count, 0 or 1 = continuous

    private int Thumb => 11;
    private int RightEnd => Math.Max(1, W - Thumb - 2);
    private float MaxValue => BitConverter.UInt32BitsToSingle(U("maxValue"));

    public override void Wake()
    {
        Discrete = U("sliderVersion") >= 2 ? (int)U("minValue") : 0;
        float frac = BitConverter.UInt32BitsToSingle(U("value"));
        float max = U("sliderVersion") >= 2 ? MaxValue : 1f;
        if (max != 0)
            mPos = (int)(Math.Clamp(frac / max, 0, 1) * RightEnd);
        if (ConsoleVar.Length > 0 && Host != null)
            VariableChanged(Host.GetVar(ConsoleVar));
    }

    public void SetDiscrete(int count)
    {
        Discrete = count <= 1 ? 0 : count;
        if (Discrete > 0)
            mPos = Snap(mPos);
    }

    private int Snap(int pos)
    {
        if (Discrete <= 1)
            return pos;
        double step = (double)RightEnd / (Discrete - 1);
        return (int)Math.Round(Math.Round(pos / step) * step);
    }

    public override string GetValue()
    {
        if (Discrete > 1)
            return ((int)Math.Round((double)mPos / RightEnd * (Discrete - 1))).ToString(CultureInfo.InvariantCulture);
        float max = U("sliderVersion") >= 2 ? MaxValue : 1f;
        return ((double)mPos / RightEnd * max).ToString("F6", CultureInfo.InvariantCulture);
    }

    protected override void ApplyValue(string v)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return;
        if (Discrete > 1)
            mPos = (int)Math.Round(d / (Discrete - 1) * RightEnd);
        else
        {
            float max = U("sliderVersion") >= 2 ? MaxValue : 1f;
            mPos = max != 0 ? (int)(d / max * RightEnd) : 0;
        }
        mPos = Math.Clamp(mPos, 0, RightEnd);
    }

    private void Changed()
    {
        WriteVariable(GetValue());
        RunCommand();
    }

    public override bool MsDown(GuiEvent e)
    {
        if (!Active)
            return base.MsDown(e);
        var (lx, _) = GlobalToLocal(e.X, 0);
        Canvas?.LockMouse(this);
        mDragging = true;
        int unit = Discrete > 1 ? Math.Max(1, RightEnd / (Discrete - 1)) : 1;
        if (lx < 8)
            mPos = Snap(Math.Max(0, mPos - unit));
        else if (lx > W - 8)
            mPos = Snap(Math.Min(RightEnd, mPos + unit));
        else if (lx < mPos)
            mPos = Snap(Math.Max(0, mPos - (Discrete > 1 ? unit : RightEnd / 8)));
        else if (lx >= mPos + Thumb)
            mPos = Snap(Math.Min(RightEnd, mPos + (Discrete > 1 ? unit : RightEnd / 8)));
        else
            mGrab = lx - mPos;
        return base.MsDown(e);
    }

    public override bool MsDrag(GuiEvent e)
    {
        if (mDragging)
        {
            var (lx, _) = GlobalToLocal(e.X, 0);
            mPos = Math.Clamp(lx - mGrab, 0, RightEnd);
            Changed();
        }
        return base.MsDrag(e);
    }

    public override bool MsUp(GuiEvent e)
    {
        if (mDragging)
        {
            mDragging = false;
            mPos = Snap(mPos);
            Changed();
        }
        return base.MsUp(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        int cy = gy + H / 2;
        p.Fill(gx + 2, cy - 1, W - 4, 3, 0xe9);
        p.Fill(gx + 1 + mPos, gy + 1, Thumb, Math.Max(1, H - 2), Active ? 0xf0 : 0xe9);
        RenderChildren(p, gx, gy);
    }
}

// SimGui::ProgressCtrl: fills a rectangle in proportion to its value (0..1).
public sealed class GuiProgress : GuiActive
{
    public double Value;
    public override bool WantsTab => false;
    protected override void ApplyValue(string v) => Value = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? Math.Clamp(d, 0, 1) : 0;
    public override string GetValue() => Value.ToString("F6", CultureInfo.InvariantCulture);

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        p.Fill(gx, gy, (int)(W * Value), H, 0xfc);
        RenderChildren(p, gx, gy);
    }
}

// SimGui::TimerCtrl: after the initial alarm, then every repeat interval, posts its message tag.
public sealed class GuiTimer : GuiControl
{
    private long mNext = -1;
    public int Fired;

    public override void Wake()
    {
        float init = BitConverter.UInt32BitsToSingle(U("initialAlarm"));
        mNext = (Host?.TickMs ?? 0) + (long)(init * 1000);
    }

    public override void PreRender()
    {
        if (mNext < 0 || Host == null || Host.TickMs < mNext)
            return;
        ++Fired;
        float rep = BitConverter.UInt32BitsToSingle(U("repeatAlarm"));
        mNext = rep > 0 ? Host.TickMs + (long)(rep * 1000) : -1;
        if (U("messageTag") != 0)
            Next?.Message(this, (int)U("messageTag"));
    }
}

// SimGui::PaletteCtrl / FGPaletteCtrl: selects the active palette on wake.
public sealed class GuiPalette : GuiControl
{
    public override void Wake()
    {
        if (Host?.TagText(U("paletteTag")) is string t && Host.OnPalette != null)
            Host.OnPalette(GuiHost.FirstItem(t));
    }
}

// SimGui::TextWrap / TextFormat: wrapped text in the extent.
public sealed class GuiTextWrap : GuiActive
{
    public string Text = "";
    public override bool WantsTab => false;
    protected override void ApplyValue(string v) => Text = v;
    public override string GetValue() => Text;

    public override void Wake()
    {
        if (Host?.TagText(U("textTag")) is string t)
            Text = t;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        PftFont? f = Host?.FontByTag(U("fontTag")) ?? Host?.FontByName("sf_white_10.pft");
        if (f != null)
        {
            int lh = GuiPaint.FontHeight(f) + (int)U("lineSpacing");
            int y = gy;
            foreach (string line in Wrap(f, Text, W))
            {
                p.Text(f, gx, y, line);
                y += lh;
            }
        }
        RenderChildren(p, gx, gy);
    }

    public static List<string> Wrap(PftFont f, string text, int width)
    {
        var lines = new List<string>();
        foreach (string para in text.Replace("\r", "").Split('\n'))
        {
            string cur = "";
            foreach (string w in para.Split(' '))
            {
                string t = cur.Length == 0 ? w : cur + " " + w;
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
