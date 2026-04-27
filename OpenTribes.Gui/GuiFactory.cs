// Maps the class names of docs/gui.md 3.1 to the C# behaviour classes. Classes without their own traced behaviour use the
// nearest family class: they load, hold their persistent fields and keep the children.
using System.Globalization;

namespace OpenTribes.Gui;

// SimGui::TestCheck, TestRadial, MECheck, MERadio: toggle on action and set the console variable to 0 or 1.
public sealed class GuiCheck : GuiSimpleText
{
    public bool Checked
    {
        get => U("checked") != 0;
        set => F["checked"] = (byte)(value ? 1 : 0);
    }

    public override string GetValue() => Checked ? "1" : "0";
    protected override void ApplyValue(string v) => Checked = v is "1" or "true" or "True" or "TRUE";

    protected override void Action()
    {
        Checked = ClassName.EndsWith("Radio", StringComparison.Ordinal) || ClassName.EndsWith("Radial", StringComparison.Ordinal) ? true : !Checked;
        WriteVariable(Checked ? "1" : "0");
        base.Action();
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        int s = Math.Min(10, H);
        p.Outline(gx, gy + (H - s) / 2, s, s, 0xfa);
        if (Checked)
            p.Fill(gx + 3, gy + (H - s) / 2 + 3, s - 5, s - 5, 0xfc);
        var (tx, ty) = (gx + s + 3, gy);
        string t = ResolvedText();
        p.Text(Font, tx, ty, t);
        RenderChildren(p, gx, gy);
    }
}

// The editor test buttons (TestButton, MEButton, MEPopupButton): text in a box.
public sealed class GuiTextButton : GuiSimpleText
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        p.Outline(gx, gy, W, H, Pressed ? 0xf8 : 0xfa);
        DrawText(p, gx, gy);
        RenderChildren(p, gx, gy);
    }
}

// FearGui::ShapeView / SkinView: the model viewer. Takes no focus; a drag turns the model (the 3D submission is the
// shape renderer's, C5; this control only keeps the angle and the skin).
public sealed class GuiShapeView : GuiActive
{
    public double Yaw;
    private int mLastX;
    public override bool WantsTab => false;
    public override bool CanTake() => false;

    public override bool MsDown(GuiEvent e)
    {
        mLastX = e.X;
        return base.MsDown(e);
    }

    public override bool MsDrag(GuiEvent e)
    {
        Yaw += (e.X - mLastX) * 0.02;
        mLastX = e.X;
        return base.MsDrag(e);
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        RenderChildren(p, gx, gy);
    }
}

public static class GuiFactory
{
    private static bool DerivesFrom(GuiClassRow row, string ancestor)
    {
        for (GuiClassRow? r = row; r != null && r.Parent.Length > 0; r = GuiClassTable.ByName(r.Parent))
            if (r.Parent == ancestor)
                return true;
        return false;
    }

    public static GuiControl Create(GuiClassRow row)
    {
        GuiControl c = Make(row);
        c.ClassName = row.Name;
        return c;
    }

    private static GuiControl Make(GuiClassRow row)
    {
        switch (row.Name)
        {
            case "FearGui::FGControl": return new GuiFGControl();
            case "FearGui::FGCommandView": return new GuiCommandView();
            case "FearGui::FearGuiBox":
            case "FearGui::FearGuiDialog":
            case "FearGui::FearGuiAbortDlg": return new GuiBox();
            case "FearGui::FearGuiMenu": return new GuiMenuPanel();
            case "FearGui::FGDlgBox": return new GuiDlgBox();
            case "FearGui::FGShellBorder":
            case "FearGui::MMShellBorder": return new GuiShellBorder();
            case "SimGui::BitmapCtrl":
            case "FearGui::FGBitmapCtrl": return new GuiBitmap();
            case "FearGui::FGUniversalButton": return new GuiUniversalButton();
            case "FearGui::CFGButton": return new GuiCFGButton();
            case "FearGui::ShapeView":
            case "FearGui::SkinView": return new GuiShapeView();
            case "FearGui::FearGuiSwitch": return new GuiSwitch();
            case "FearGui::FearGuiRadio": return new GuiRadio();
            case "FearGui::FGMenuItem":
            case "FearGui::FGTab": return new GuiMenuItem();
            case "SimGui::Slider":
            case "FearGui::FGSlider": return new GuiSlider();
            case "SimGui::ProgressCtrl": return new GuiProgress();
            case "FearGui::ScoreListCtrl": return new GuiScoreList();
            case "FearGui::MasterServerList": return new GuiMasterList();
            case "FearGui::FilterCtrl": return new GuiFilterCtrl();
            case "FearGui::ColumnListCtrl": return new GuiOptionColumns();
            case "FearGui::MenuDisplayCtrl": return new GuiMenuDisplay();
            case "FearGuiInventory": return new GuiInventoryList();
            case "FearGuiPurchase": return new GuiInventoryList { Purchase = true };
            case "SimGui::TimerCtrl": return new GuiTimer();
            case "FearGui::SmackerCtrl": return new GuiSmacker();
            case "SimGui::AnimateBMA": return new GuiAnimate();
            case "SimGui::PaletteCtrl":
            case "FearGui::FGPaletteCtrl": return new GuiPalette();
            case "SimGui::TextWrap": return new GuiTextWrap();
            case "SimGui::TextFormat":
            case "FearGui::FGTextFormat":
            case "FearGuiFormattedText": return new GuiTextWrap { Formatted = true };
            case "FearGui::FGHCommandObjectivePage": return new GuiObjectivePage();
            case "SimGui::ScrollContentCtrl": return new GuiScrollContent();
            case "SimGui::TSControl":
            case "FearGui::TSCommander": return new GuiPlayView();
            case "FearGui::HealthHud": return new GuiHealthHud();
            case "FearGui::JetPackHud": return new GuiJetPackHud();
            case "FearGui::ClockHud": return new GuiClockHud();
            case "FearGui::CompassHud": return new GuiCompassHud();
            case "FearGui::CurrentWeaponHud": return new GuiWeaponHud();
            case "FearGui::FGHSensorPing": return new GuiSensorHud();
            case "FearGui::Crosshair": return new GuiCrosshair();
            case "FearGui::FearGuiChatDisplay": return new GuiChatDisplay();
            case "FearGui::HudCtrl": return new GuiHud();
            case "CenterPrint": return new GuiCenterPrint();
            case "FearGui::FearGuiChat": return new GuiChat();
            case "FearGui::FGSimpleText": return new GuiFGSimpleText();
            case "FearGui::TestEdit": return new GuiTestEdit();
            case "SimGui::TestCheck":
            case "SimGui::TestRadial":
            case "MECheck":
            case "MERadio": return new GuiCheck();
            case "SimGui::TestButton":
            case "MEButton":
            case "MEPopupButton": return new GuiTextButton();
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
