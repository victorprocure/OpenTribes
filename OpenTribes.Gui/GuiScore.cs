namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>The list of the score dialog of the Tab key (Score.gui, lr_score.gui): the headings, the teams and the players.</summary>
/// <remarks>Follows FearGui::ScoreListCtrl (draw 004c2198). FearGui::MenuDisplayCtrl, which lists the server menu, is <see cref="GuiMenuDisplay"/>.</remarks>
public sealed class GuiScoreList : GuiList
{
    /// <summary>The palette index that fills the selected row.</summary>
    public const int LocalRowColor = 0xf1;
    /// <summary>The width of the selected row's fill.</summary>
    public const int HighlightWidth = 0x280;

    // One line: the text with its tab separated cells, the column x positions it uses and whether it is a heading.
    /// <summary>One line of the list.</summary>
    /// <param name="Text">The text with its tab separated cells.</param>
    /// <param name="Columns">The column x positions the line uses.</param>
    /// <param name="Heading">True for a heading line.</param>
    /// <param name="Local">True for the selected row.</param>
    public readonly record struct Line(string Text, int[] Columns, bool Heading, bool Local);

    /// <summary>The lines shown, rebuilt before each render.</summary>
    public readonly List<Line> Lines = [];

    private bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
    private PftFont? HeadingFont => Host?.FontByName(LowRes ? "sf_white_6.pft" : "sf_white_10b.pft");
    /// <summary>Gets the row font: yellow `sf_yellow_10b` (low resolution `sf_yellow_6`).</summary>
    /// <remarks>The headings use the white pair. Follows the prepare routine of the control (004c1f60).</remarks>
    protected override PftFont? RowFont => Host?.FontByName(LowRes ? "sf_yellow_6.pft" : "sf_yellow_10b.pft");
    /// <summary>Gets the pixel height of one line.</summary>
    public int LineHeight => GuiPaint.FontHeight(RowFont) + 2;

    /// <summary>Reads the column x positions a heading defines.</summary>
    /// <param name="heading">The heading text.</param>
    /// <returns>The positions: the first cell sits at 0, every later cell starts with its position as one character.</returns>
    public static int[] Columns(string heading)
    {
        var cells = heading.Split('\t');
        var cols = new int[Math.Max(1, cells.Length)];
        for (var i = 1; i < cells.Length; ++i)
            cols[i] = cells[i].Length > 0 ? cells[i][0] & 0xff : cols[i - 1] + 60;
        return cols;
    }

    /// <summary>Removes the position characters from a heading line.</summary>
    /// <param name="heading">The heading text.</param>
    /// <returns>The heading cells without their position characters.</returns>
    public static string HeadingText(string heading)
    {
        var cells = heading.Split('\t');
        for (var i = 1; i < cells.Length; ++i)
            cells[i] = cells[i].Length > 0 ? cells[i][1..] : "";
        return string.Join('\t', cells);
    }

    /// <summary>Builds the lines in the order shown.</summary>
    /// <param name="board">The score board.</param>
    /// <param name="selected">The id of the selected client, or -1 for none.</param>
    /// <returns>The team heading and the teams, then the player heading and the players.</returns>
    /// <remarks>The filled row is the selected client (a click sets it, 004c1d0c), not the local player: none until a click.</remarks>
    public static List<Line> Build(ScoreBoard board, int selected = -1)
    {
        var lines = new List<Line>();
        if (board.Teams.Count > 0 && board.TeamHeading.Length > 0)
        {
            var tc = Columns(board.TeamHeading);
            lines.Add(new Line(HeadingText(board.TeamHeading), tc, true, false));
            foreach (ScoreRow t in board.Teams)
                lines.Add(new Line(t.Text, tc, false, false));
        }
        var cc = Columns(board.ClientHeading);
        if (board.ClientHeading.Length > 0)
            lines.Add(new Line(HeadingText(board.ClientHeading), cc, true, false));
        foreach (ScoreRow c in board.Clients)
            lines.Add(new Line(c.Text, cc, false, selected >= 0 && c.Id == selected));
        return lines;
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        Lines.Clear();
        if (Host?.Hud?.Scores is ScoreBoard board)
            Lines.AddRange(Build(board));
        var h = Lines.Count * LineHeight;
        if (Parent != null)
            h = Math.Max(h, Parent.H);
        if (h != H)
            H = h;
    }

    /// <summary>Gets a value that is always false: the scoreboard is display only, so the Tab key reaches the action map and closes the dialog again.</summary>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;
    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e) => false;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        var lh = LineHeight;
        var low = LowRes;
        for (var i = 0; i < Lines.Count; ++i)
        {
            var y = gy + (i * lh);
            if (y > p.Clip.Bottom)
                break;
            if (y + lh < p.Clip.Top)
                continue;
            Line line = Lines[i];
            PftFont? f = line.Heading ? HeadingFont : RowFont;
            if (line.Local)
                p.Fill(gx, y, Math.Min(W, HighlightWidth), lh, LocalRowColor);
            var cells = line.Text.Split('\t');
            for (var k = 0; k < cells.Length; ++k)
            {
                if (cells[k].Length == 0)
                    continue;
                var col = line.Columns[Math.Min(k, line.Columns.Length - 1)] + (k >= line.Columns.Length ? 60 * (k - line.Columns.Length + 1) : 0);
                if (low)
                    col = col * 70 / 100;
                // The draw clips each cell at the next column less 2 pixels [code 004c2198].
                GfxRect saved = p.Clip;
                if (k + 1 < line.Columns.Length)
                {
                    var next = line.Columns[k + 1];
                    if (low)
                        next = next * 70 / 100;
                    p.SetClip(saved.Intersect(new GfxRect(gx + col, y - 2, gx + next - 3, y + lh - 1)));
                }
                p.Text(f, gx + col + 2, y - (low ? 1 : 2), cells[k]);   // 004c2198: the text sits 2 pixels (low resolution 1) above its row
                p.SetClip(saved);
            }
        }
    }
}

/// <summary>The title and the items of the menu a script has on display (FearGui::MenuDisplayCtrl).</summary>
public sealed class GuiMenuDisplay : GuiControl
{
    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Host?.Hud?.ServerMenu is not ServerMenuView menu)
            return;
        var low = Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
        PftFont? row = Host.FontByName(low ? "sf_yellow_6.pft" : "sf_yellow_10b.pft");
        PftFont? head = row;
        var y = gy;
        if (menu.Title.Length > 0)
        {
            p.Text(head, gx, y, menu.Title);
            y += GuiPaint.FontHeight(head);
        }
        foreach (var (key, text) in menu.Items)
        {
            p.Text(row, gx, y, key + ": " + text);
            y += GuiPaint.FontHeight(row);
        }
    }
}
