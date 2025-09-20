// The score dialog of the Tab key (Score.gui, lr_score.gui): FearGui::ScoreListCtrl [draw 004c2198] lists the headings, the teams
// and the players, FearGui::MenuDisplayCtrl lists the server menu.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public sealed class GuiScoreList : GuiList
{
    // Fonts of the headings and of the rows; the file of the control has no font tags, so these are chosen [I].
    public const uint HeadingFontTag = 150114, RowFontTag = 150124;
    public const int LocalRowColor = 0xf1, HighlightWidth = 0x280;

    // One line: the text with its tab separated cells, the column x positions it uses and whether it is a heading.
    public readonly record struct Line(string Text, int[] Columns, bool Heading, bool Local);

    public readonly List<Line> Lines = new();

    private PftFont? HeadingFont => Host?.FontByTag(HeadingFontTag) ?? Host?.FontByName("sf_white_10.pft");
    protected override PftFont? RowFont => Host?.FontByTag(RowFontTag) ?? HeadingFont;
    private bool LowRes => Canvas != null && (Canvas.Width < 512 || Canvas.Height < 384);
    public int LineHeight => GuiPaint.FontHeight(RowFont) + 2;

    // The x positions a heading defines: the first cell sits at 0, every later cell starts with its position as one character.
    public static int[] Columns(string heading)
    {
        string[] cells = heading.Split('\t');
        var cols = new int[Math.Max(1, cells.Length)];
        for (int i = 1; i < cells.Length; ++i)
            cols[i] = cells[i].Length > 0 ? cells[i][0] & 0xff : cols[i - 1] + 60;
        return cols;
    }

    // A heading line without the position characters.
    public static string HeadingText(string heading)
    {
        string[] cells = heading.Split('\t');
        for (int i = 1; i < cells.Length; ++i)
            cells[i] = cells[i].Length > 0 ? cells[i][1..] : "";
        return string.Join('\t', cells);
    }

    // The lines in the order shown: the team heading and the teams, then the player heading and the players.
    public static List<Line> Build(ScoreBoard board)
    {
        var lines = new List<Line>();
        if (board.Teams.Count > 0 && board.TeamHeading.Length > 0)
        {
            int[] tc = Columns(board.TeamHeading);
            lines.Add(new Line(HeadingText(board.TeamHeading), tc, true, false));
            foreach (ScoreRow t in board.Teams)
                lines.Add(new Line(t.Text, tc, false, false));
        }
        int[] cc = Columns(board.ClientHeading);
        if (board.ClientHeading.Length > 0)
            lines.Add(new Line(HeadingText(board.ClientHeading), cc, true, false));
        foreach (ScoreRow c in board.Clients)
            lines.Add(new Line(c.Text, cc, false, c.Id == board.LocalClient));
        return lines;
    }

    public override void PreRender()
    {
        Lines.Clear();
        if (Host?.Hud?.Scores is ScoreBoard board)
            Lines.AddRange(Build(board));
        int h = Lines.Count * LineHeight;
        if (Parent != null)
            h = Math.Max(h, Parent.H);
        if (h != H)
            H = h;
        if (Parent != null && W != Parent.W)
            W = Parent.W;
    }

    // The scoreboard is display only: no tab stop, so the Tab key reaches the action map and closes the dialog again.
    public override bool WantsTab => false;
    public override bool CanTake() => false;
    public override bool MsDown(GuiEvent e) => false;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        int lh = LineHeight;
        bool low = LowRes;
        for (int i = 0; i < Lines.Count; ++i)
        {
            int y = gy + i * lh;
            if (y > p.Clip.Bottom)
                break;
            if (y + lh < p.Clip.Top)
                continue;
            Line line = Lines[i];
            PftFont? f = line.Heading ? HeadingFont : RowFont;
            if (line.Local)
                p.Fill(gx, y, Math.Min(W, HighlightWidth), lh, LocalRowColor);
            string[] cells = line.Text.Split('\t');
            for (int k = 0; k < cells.Length; ++k)
            {
                if (cells[k].Length == 0)
                    continue;
                int col = line.Columns[Math.Min(k, line.Columns.Length - 1)] + (k >= line.Columns.Length ? 60 * (k - line.Columns.Length + 1) : 0);
                if (low)
                    col = col * 70 / 100;
                p.Text(f, gx + col + 2, y, cells[k]);
            }
        }
    }
}

// FearGui::MenuDisplayCtrl: the title and the items of the menu a script has on display.
public sealed class GuiMenuDisplay : GuiControl
{
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        if (Host?.Hud?.ServerMenu is not ServerMenuView menu)
            return;
        PftFont? head = Host.FontByTag(GuiScoreList.HeadingFontTag) ?? Host.FontByName("sf_white_10.pft");
        PftFont? row = Host.FontByTag(GuiScoreList.RowFontTag) ?? head;
        int y = gy;
        if (menu.Title.Length > 0)
        {
            p.Text(head, gx + 2, y, menu.Title);
            y += GuiPaint.FontHeight(head) + 4;
        }
        foreach (var (key, text) in menu.Items)
        {
            p.Text(row, gx + 2, y, key + " " + text);
            y += GuiPaint.FontHeight(row) + 2;
        }
    }
}
