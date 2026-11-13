namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>The mission objective page of CmdObjectives.gui (FearGui::FGHCommandObjectivePage, docs/gui.md 3.3.1).</summary>
/// <remarks>The server sets up to 32 lines (Team::setObjective, TeamObjectiveEvent). Each line is its own formatted text laid out at the page width with the table of ten fonts, and the lines stack from the top. The page takes the total height plus 4 so that the scroll control around it follows.</remarks>
public sealed class GuiObjectivePage : GuiActive
{
    /// <summary>The number of objective lines.</summary>
    public const int LineCount = 32;

    private readonly GuiTextFormat?[] mLayouts = new GuiTextFormat?[LineCount];
    private readonly string?[] mTexts = new string?[LineCount];
    private int mLaidWidth = -1;

    /// <inheritdoc/>
    public override bool WantsTab => false;

    private PftFont?[] Fonts()
    {
        var fonts = new PftFont?[GuiTextFormat.FontCount];
        for (var i = 0; i < fonts.Length; ++i)
            fonts[i] = Host?.FontByName(GuiTextFormat.ObjectiveFonts[i]);
        return fonts;
    }

    /// <summary>Lays out the lines whose text or the page width changed.</summary>
    /// <returns>The total height of the page.</returns>
    private int Update()
    {
        IReadOnlyList<string?> lines = Host?.Hud?.ObjectiveLines ?? Array.Empty<string?>();
        PftFont?[]? fonts = null;
        var total = 0;
        for (var i = 0; i < LineCount; ++i)
        {
            var text = i < lines.Count ? lines[i] : null;
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

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Host == null)
            return;
        var h = Update() + 4;
        if (h != H)
            H = h;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (Host != null)
        {
            Update();
            var y = gy;
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
