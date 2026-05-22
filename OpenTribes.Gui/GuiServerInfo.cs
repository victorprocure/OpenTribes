// FearGui::ServerInfoCtrl [code 004c91f8 setServer, 004c92c8 cell draw, 004c952c draw]: the key/value table of the server info dialog
// (ServerInfo.gui): the team and player score rows of the server's info reply. The dialog's own texts are set by ServerBrowser.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public sealed class GuiServerInfo : GuiList
{
    // 004c91f8: a heading string carries its column starts as a tab followed by one byte; the first column starts at 0, the list ends at 0x280.
    private int[] mTeamColumns = { 0, 0x280 }, mPlayerColumns = { 0, 0x280 };
    private IReadOnlyList<string> mRows = Array.Empty<string>();

    public int RowCount => mRows.Count;

    // 004c91f8: the record's two headings and its rows. A row starts with two characters: '0' for a team row (else a player row)
    // and '1' for a heading line, then the cells split at tabs.
    public void SetServer(string teamHeading, string playerHeading, IReadOnlyList<string> rows)
    {
        mTeamColumns = Columns(teamHeading);
        mPlayerColumns = Columns(playerHeading);
        mRows = rows;
        Selected = -1;
    }

    public void ClearServer() => SetServer("", "", Array.Empty<string>());

    private static int[] Columns(string heading)
    {
        var cols = new List<int> { 0 };
        for (int i = 0; i < heading.Length && cols.Count < 16;)
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
        return cols.ToArray();
    }

    private PftFont? HeadingFont => Host?.FontByName("sf_orange_7.pft");
    private PftFont? RowTextFont => Host?.FontByName("sf_white_7.pft");
    protected override PftFont? RowFont => HeadingFont;

    public override void PreRender()
    {
        if (Parent is not GuiScrollContent pc)
            return;
        W = pc.W;
        H = Math.Max(pc.H, mRows.Count * RowHeight);
    }

    public override bool MsDown(GuiEvent e) => false;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y, rh = RowHeight;
        for (int r = 0; r < mRows.Count; ++r)
        {
            int y = gy + r * rh;
            if (y > p.Clip.Bottom)
                break;
            if (y + rh <= p.Clip.Top)
                continue;
            DrawRow(p, gx, y, rh, mRows[r]);
        }
    }

    // 004c92c8: every cell is clipped to its column (the next column's start less two) and drawn four pixels in, two up.
    private void DrawRow(GuiPaint p, int gx, int y, int rh, string row)
    {
        if (row.Length < 2)
            return;
        int[] cols = row[0] == '0' ? mTeamColumns : mPlayerColumns;
        bool heading = row[1] != '0';
        PftFont? font = heading ? HeadingFont : RowTextFont;
        int i = 2, col = 0;
        GfxRect saved = p.Clip;
        while (col + 1 < cols.Length)
        {
            int start = i;
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
