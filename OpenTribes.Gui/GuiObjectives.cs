// FearGui::FGHCommandObjectivePage (docs/gui.md 3.3.1): the mission objective page of CmdObjectives.gui. The server sets up to 32
// lines (Team::setObjective, TeamObjectiveEvent); each line is its own formatted text laid out at the page width with the table of
// ten fonts, and the lines stack from the top. The page takes the total height plus 4 so that the scroll control around it follows.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public sealed class GuiObjectivePage : GuiActive
{
    public const int LineCount = 32;

    private readonly GuiTextFormat?[] mLayouts = new GuiTextFormat?[LineCount];
    private readonly string?[] mTexts = new string?[LineCount];
    private int mLaidWidth = -1;

    public override bool WantsTab => false;

    private PftFont?[] Fonts()
    {
        var fonts = new PftFont?[GuiTextFormat.FontCount];
        for (int i = 0; i < fonts.Length; ++i)
            fonts[i] = Host?.FontByName(GuiTextFormat.ObjectiveFonts[i]);
        return fonts;
    }

    // Lays out the lines whose text or the page width changed; returns the total height of the page.
    private int Update()
    {
        IReadOnlyList<string?> lines = Host?.Hud?.ObjectiveLines ?? Array.Empty<string?>();
        PftFont?[]? fonts = null;
        int total = 0;
        for (int i = 0; i < LineCount; ++i)
        {
            string? text = i < lines.Count ? lines[i] : null;
            if (text == null)
            {
                mLayouts[i] = null;
                mTexts[i] = null;
                continue;
            }
            if (mLayouts[i] == null || mTexts[i] != text || mLaidWidth != W)
            {
                fonts ??= Fonts();
                mLayouts[i] = GuiTextFormat.Layout(text, W, fonts, n => Host?.BitmapByName(n), n => Host?.GetVar("$" + n) ?? "");
                mTexts[i] = text;
            }
            total += mLayouts[i]!.Height;
        }
        mLaidWidth = W;
        return total;
    }

    public override void PreRender()
    {
        if (Host == null)
            return;
        int h = Update() + 4;
        if (h != H)
            H = h;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (Host != null)
        {
            Update();
            int y = gy;
            foreach (GuiTextFormat? l in mLayouts)
                if (l != null)
                {
                    l.Draw(p, gx, y);
                    y += l.Height;
                }
        }
        RenderChildren(p, gx, gy);
    }
}
