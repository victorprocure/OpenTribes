namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>The key/value table of the server info dialog (ServerInfo.gui).</summary>
/// <remarks>Shows the team and player score rows of the server's info reply. The dialog's own texts are set by ServerBrowser. Follows FearGui::ServerInfoCtrl: setServer (004c91f8), cell draw (004c92c8), draw (004c952c).</remarks>
public sealed class GuiServerInfo : GuiList
{
    /// <summary>The column starts of the team and player rows: the first column starts at 0 and the list ends at 0x280 (004c91f8).</summary>
    private int[] mTeamColumns = [0, 0x280], mPlayerColumns = [0, 0x280];
    private IReadOnlyList<string> mRows = Array.Empty<string>();

    /// <summary>Gets the number of rows.</summary>
    public int RowCount => mRows.Count;

    /// <summary>Sets the headings and rows of the server's reply.</summary>
    /// <param name="teamHeading">The team row heading. A heading carries its column starts as a tab followed by one byte (004c91f8).</param>
    /// <param name="playerHeading">The player row heading, in the same format.</param>
    /// <param name="rows">The rows. A row starts with two characters: '0' for a team row (else a player row) and '1' for a heading line, then the cells split at tabs.</param>
    public void SetServer(string teamHeading, string playerHeading, IReadOnlyList<string> rows)
    {
        mTeamColumns = Columns(teamHeading);
        mPlayerColumns = Columns(playerHeading);
        mRows = rows;
        Selected = -1;
    }

    /// <summary>Removes all rows.</summary>
    public void ClearServer() => SetServer("", "", Array.Empty<string>());

    private static int[] Columns(string heading)
    {
        var cols = new List<int> { 0 };
        for (var i = 0; i < heading.Length && cols.Count < 16;)
        {
            if (heading[i] == '\t' && i + 1 < heading.Length)
            {
                cols.Add(heading[i + 1] & 0xff);
                i += 2;
            }
            else
                ++i;
        }
        cols.Add(0x280);
        return [.. cols];
    }

    private PftFont? HeadingFont => Host?.FontByName("sf_orange_7.pft");
    private PftFont? RowTextFont => Host?.FontByName("sf_white_7.pft");
    /// <inheritdoc/>
    protected override PftFont? RowFont => HeadingFont;

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Parent is not GuiScrollContent pc)
            return;
        W = pc.W;
        H = Math.Max(pc.H, mRows.Count * RowHeight);
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e) => false;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y, rh = RowHeight;
        for (var r = 0; r < mRows.Count; ++r)
        {
            var y = gy + (r * rh);
            if (y > p.Clip.Bottom)
                break;
            if (y + rh <= p.Clip.Top)
                continue;
            DrawRow(p, gx, y, rh, mRows[r]);
        }
    }

    /// <summary>Draws one row, each cell clipped to its column.</summary>
    /// <remarks>Follows the stock cell draw (004c92c8): a cell is clipped to its column (the next column's start less two) and drawn four pixels in, two up.</remarks>
    private void DrawRow(GuiPaint p, int gx, int y, int rh, string row)
    {
        if (row.Length < 2)
            return;
        var cols = row[0] == '0' ? mTeamColumns : mPlayerColumns;
        var heading = row[1] != '0';
        PftFont? font = heading ? HeadingFont : RowTextFont;
        int i = 2, col = 0;
        GfxRect saved = p.Clip;
        while (col + 1 < cols.Length)
        {
            var start = i;
            while (i < row.Length && row[i] != '\t')
                ++i;
            if (i > start)
            {
                p.SetClip(saved.Intersect(new GfxRect(gx + cols[col], y, gx + cols[col + 1] - 3, y + rh - 1)));
                p.Text(font, gx + cols[col] + 4, y - 2, row[start..i]);
                p.SetClip(saved);
            }
            if (i + 1 < row.Length && row[i] == '\t' && heading)
                i += 2;
            else if (i < row.Length && row[i] == '\t')
                ++i;
            if (i >= row.Length)
                break;
            ++col;
        }
    }
}
