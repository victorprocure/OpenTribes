namespace OpenTribes.Gui;

/// <summary>A check box or radio button (SimGui::TestCheck, TestRadial, MECheck, MERadio): toggles on action and sets the console variable to 0 or 1.</summary>
public sealed class GuiCheck : GuiSimpleText
{
    /// <summary>Gets or sets the checked state.</summary>
    public bool Checked
    {
        get => U("checked") != 0;
        set => F["checked"] = (byte)(value ? 1 : 0);
    }

    /// <inheritdoc/>
    public override string GetValue() => Checked ? "1" : "0";
    /// <inheritdoc/>
    protected override void ApplyValue(string v) => Checked = v is "1" or "true" or "True" or "TRUE";

    /// <inheritdoc/>
    protected override void Action()
    {
        Checked = ClassName.EndsWith("Radio", StringComparison.Ordinal) || ClassName.EndsWith("Radial", StringComparison.Ordinal) || !Checked;
        WriteVariable(Checked ? "1" : "0");
        base.Action();
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var s = Math.Min(10, H);
        p.Outline(gx, gy + ((H - s) / 2), s, s, 0xfa);
        if (Checked)
            p.Fill(gx + 3, gy + ((H - s) / 2) + 3, s - 5, s - 5, 0xfc);
        var (tx, ty) = (gx + s + 3, gy);
        var t = ResolvedText();
        p.Text(Font, tx, ty, t);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The editor test button (TestButton, MEButton, MEPopupButton): text in a box.</summary>
public sealed class GuiTextButton : GuiSimpleText
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        p.Outline(gx, gy, W, H, Pressed ? 0xf8 : 0xfa);
        DrawText(p, gx, gy);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>The 3D side of a model view: draws the control's model into its rectangle of the surface (the shape renderer, C5).</summary>
public interface IModelViewRenderer
{
    /// <summary>Draws the model of a view.</summary>
    /// <param name="view">The view control.</param>
    /// <param name="p">The surface to draw on.</param>
    /// <param name="x">The left edge of the rectangle.</param>
    /// <param name="y">The top edge of the rectangle.</param>
    /// <param name="w">The rectangle width.</param>
    /// <param name="h">The rectangle height.</param>
    public void Draw(GuiShapeView view, GuiPaint p, int x, int y, int w, int h);
}

/// <summary>The model viewer of the player setup screen (FearGui::ShapeView, SkinView).</summary>
/// <remarks>
/// <para>It takes no focus. The skin view shows the armor of the gender (male or female) and armor index 0 light, 1 medium, 2 heavy, in the skin FGSkin::set gave. A drag with the left button turns it (0.01 radians per pixel, the angle stays in (0, 2 pi]).</para>
/// <para>The right button (0049adb8, 0049ae00, 0049adec) takes the mouse and a drag of dy pixels adds -dy * 10 / height to the camera distance, which stays between the fitted distance and <see cref="ZoomMax"/>.</para>
/// <para>Follows ctor 004997c0, FGSkin::set 0049a798, cycleArmor 0049a97c, mouse 0049ad58 to 0049ae84 and draw 0049af20.</para>
/// </remarks>
public sealed class GuiShapeView : GuiActive
{
    /// <summary>The angle of the view around the model (+0x418), pi at the start (the model faces the viewer).</summary>
    public double Yaw = Math.PI;
    private int mLastX, mLastY;
    /// <summary>The camera distance (+0x1e8), NaN until the renderer has fitted the model.</summary>
    public double Zoom = double.NaN;
    /// <summary>The fitted distance, the lowest (+0x1ec), NaN until the renderer has fitted the model.</summary>
    public double ZoomMin = double.NaN;
    /// <summary>The highest distance (+0x1f4), set with the fitted one by the renderer (SkinViewRenderer.MaxDistance, camera call 005aa65c).</summary>
    public double ZoomMax = double.NaN;
    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;

    /// <summary>Gets the skin base name.</summary>
    public string SkinBase => S("skin");
    /// <summary>Gets a value that is true for the male armor.</summary>
    public bool Male => !string.Equals(S("gender"), "FEMALE", StringComparison.OrdinalIgnoreCase);
    /// <summary>Gets the armor index: 0 light, 1 medium, 2 heavy.</summary>
    public int Armor => (int)(U("armor") % 3);
    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        mLastX = e.X;
        mLastY = e.Y;
        return base.MsDown(e);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0049ae84: a drag of dx pixels turns the model by dx / 100 radians and keeps the angle in (0, 2 pi].</remarks>
    public override bool MsDrag(GuiEvent e)
    {
        if (e.X != mLastX)
        {
            Yaw += (e.X - mLastX) * 0.01;
            if (Yaw >= 2 * Math.PI)
                Yaw -= 2 * Math.PI;
            else if (Yaw <= 0)
                Yaw = 2 * Math.PI;
        }
        mLastX = e.X;
        mLastY = e.Y;
        return base.MsDrag(e);
    }

    /// <inheritdoc/>
    public override bool MsRightDown(GuiEvent e)
    {
        Canvas?.LockMouse(this);
        mLastX = e.X;
        mLastY = e.Y;
        return true;
    }

    /// <inheritdoc/>
    public override bool MsRightUp(GuiEvent e)
    {
        Canvas?.UnlockMouse(this);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0049ae00: the distance grows by the pixels the mouse went up times 10 / height, then is clamped.</remarks>
    public override bool MsRightDrag(GuiEvent e)
    {
        if (!double.IsNaN(Zoom) && H > 0)
        {
            Zoom += (mLastY - e.Y) * (10.0 / H);
            Zoom = Zoom < ZoomMin ? ZoomMin : Zoom >= ZoomMax ? ZoomMax : Zoom;
        }
        mLastX = e.X;
        mLastY = e.Y;
        return true;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        Host?.ModelViews?.Draw(this, p, gx, gy, W, H);
        RenderChildren(p, gx, gy);
    }
}

/// <summary>Creates the behaviour class for a GUI class name.</summary>
/// <remarks>Maps the class names of docs/gui.md 3.1 to the C# behaviour classes. Classes without their own traced behaviour use the nearest family class: they load, hold their persistent fields and keep the children.</remarks>
public static class GuiFactory
{
    private static bool DerivesFrom(GuiClassRow row, string ancestor)
    {
        for (var r = row; r != null && r.Parent.Length > 0; r = GuiClassTable.ByName(r.Parent))
            if (r.Parent == ancestor)
                return true;
        return false;
    }

    /// <summary>Creates the control for a class row.</summary>
    /// <param name="row">The class row.</param>
    /// <returns>A new control with its class name set.</returns>
    public static GuiControl Create(GuiClassRow row)
    {
        var c = Make(row);
        c.ClassName = row.Name;
        return c;
    }

    private static GuiControl Make(GuiClassRow row)
    {
        switch (row.Name)
        {
            case "FearGui::FGControl":
                return new GuiFGControl();
            case "FearGui::FGCommandView":
                return new GuiCommandView();
            case "FearGui::FGCommandPanel":
                return new GuiCommandPanel();
            case "FearGui::FGChatMenu":
                return new GuiChatMenu();
            case "FearGui::CommandTeamCtrl":
                return new GuiCommandTeam();
            case "FearGui::CommandItemCtrl":
                return new GuiCommandItems();
            case "FearGui::CommandTurretCtrl":
                return new GuiCommandTurrets();
            case "FearGui::CMDObserve":
                return new GuiCmdObserve();
            case "FearGui::FearGuiBox":
            case "FearGui::FearGuiDialog":
            case "FearGui::FearGuiAbortDlg":
                return new GuiBox();
            case "FearGui::FearGuiMenu":
                return new GuiMenuPanel();
            case "FearGui::FGDlgBox":
                return new GuiDlgBox();
            case "FearGui::FGShellBorder":
            case "FearGui::MMShellBorder":
                return new GuiShellBorder();
            case "SimGui::BitmapCtrl":
            case "FearGui::FGBitmapCtrl":
                return new GuiBitmap();
            case "FearGui::FGUniversalButton":
                return new GuiUniversalButton();
            case "FearGui::CFGButton":
                return new GuiCFGButton();
            case "FearGui::ShapeView":
            case "FearGui::SkinView":
                return new GuiShapeView();
            case "FearGui::FearGuiSwitch":
                return new GuiSwitch();
            case "FearGui::FearGuiRadio":
                return new GuiRadio();
            case "FearGui::FGMenuItem":
            case "FearGui::FGTab":
                return new GuiMenuItem();
            case "SimGui::Slider":
            case "FearGui::FGSlider":
                return new GuiSlider();
            case "SimGui::ProgressCtrl":
                return new GuiProgress();
            case "FearGui::ScoreListCtrl":
                return new GuiScoreList();
            case "FearGui::MasterServerList":
                return new GuiMasterList();
            case "FearGui::FilterCtrl":
                return new GuiFilterCtrl();
            case "FearGui::ColumnListCtrl":
                return new GuiOptionColumns();
            case "FearGui::ServerListCtrl":
                return new GuiServerList();
            case "FearGui::ServerInfoCtrl":
                return new GuiServerInfo();
            case "FearGui::MenuDisplayCtrl":
                return new GuiMenuDisplay();
            case "FearGuiInventory":
                return new GuiInventoryList();
            case "FearGuiPurchase":
                return new GuiInventoryList { Purchase = true };
            case "SimGui::TimerCtrl":
                return new GuiTimer();
            case "FearGui::SmackerCtrl":
                return new GuiSmacker();
            case "SimGui::AnimateBMA":
                return new GuiAnimate();
            case "SimGui::PaletteCtrl":
            case "FearGui::FGPaletteCtrl":
                return new GuiPalette();
            case "SimGui::TextWrap":
                return new GuiTextWrap();
            case "SimGui::TextFormat":
            case "FearGui::FGTextFormat":
            case "FearGuiFormattedText":
                return new GuiTextWrap { Formatted = true };
            case "FearGui::FGHCommandObjectivePage":
                return new GuiObjectivePage();
            case "SimGui::ScrollContentCtrl":
                return new GuiScrollContent();
            case "SimGui::TSControl":
                return new GuiPlayView();
            case "FearGui::TSCommander":
                return new GuiCommandMap();
            case "FearGui::HealthHud":
                return new GuiHealthHud();
            case "FearGui::JetPackHud":
                return new GuiJetPackHud();
            case "FearGui::ClockHud":
                return new GuiClockHud();
            case "FearGui::CompassHud":
                return new GuiCompassHud();
            case "FearGui::CurrentWeaponHud":
                return new GuiWeaponHud();
            case "FearGui::FGHSensorPing":
                return new GuiSensorHud();
            case "FearGui::Crosshair":
                return new GuiCrosshair();
            case "FearGui::FearGuiChatDisplay":
                return new GuiChatDisplay();
            case "FearGui::HudCtrl":
                return new GuiHud();
            case "CenterPrint":
                return new GuiCenterPrint();
            case "FearGui::FearGuiChat":
                return new GuiChat();
            case "FearGui::FGSimpleText":
                return new GuiFGSimpleText();
            case "FearGui::TestEdit":
                return new GuiTestEdit();
            case "SimGui::TestCheck":
            case "SimGui::TestRadial":
            case "MECheck":
            case "MERadio":
                return new GuiCheck();
            case "SimGui::TestButton":
            case "MEButton":
            case "MEPopupButton":
                return new GuiTextButton();
        }
        if (DerivesFrom(row, "SimGui::ArrayCtrl") || row.Name == "SimGui::ArrayCtrl")
            return new GuiList();
        if (DerivesFrom(row, "SimGui::ScrollCtrl") || row.Name == "SimGui::ScrollCtrl")
            return new GuiScroll();
        if (row.Name.Contains("ComboBox", StringComparison.Ordinal))
            return new GuiCombo();
        if (DerivesFrom(row, "SimGui::TextEdit") || row.Name == "SimGui::TextEdit")
            return new GuiTextEdit();
        if (DerivesFrom(row, "FearGui::HudCtrl"))
            return new GuiHud();
        if (DerivesFrom(row, "SimGui::SimpleText") || row.Name is "SimGui::SimpleText" or "MEPopup")
            return new GuiSimpleText();
        if (DerivesFrom(row, "SimGui::ActiveCtrl") || row.Name == "SimGui::ActiveCtrl")
            return new GuiActive();
        return new GuiControl();
    }
}
