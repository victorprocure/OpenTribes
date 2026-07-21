// FearGuiInventory (the items the player carries) and FearGuiPurchase (the items a station sells) of CmdInventory.gui
// [code 0048afcc and 0048b05c rebuild the rows, 0048a700 inserts a row, 0048ac04 draws one, 0048aaa4 selects].
using System.Globalization;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public sealed class GuiInventoryList : GuiList
{
    // The two lists differ in the filter and in the number at the right: the carried count or the price.
    public bool Purchase;

    // Both classes answer no to the tab stop query (0048a6c4) and to the gain-focus query (responder slot 0x40, 0048a6c0): the list never
    // holds the keyboard, so the inventory key reaches the action maps while the screen is up.
    public override bool WantsTab => false;
    public override bool CanTake() => false;

    // The font tags of the engine: normal 150114, selected 150164, disabled 150134, highlight 150124 [code 0048aea0].
    public const uint NormalFontTag = 150114, SelectedFontTag = 150164, DisabledFontTag = 150134;
    public const int Padding = 6, CellWidth = 300, TextY = 1, TextX = 15, ValueX = 0x9e, StarX = 3, HeaderColor = 0xf0;

    // A row: a header (no item text) or an item. The first character of the heading is the sort key and is not shown.
    public readonly record struct Row(string Heading, string Text, int Value, int Item, bool Enabled)
    {
        public bool IsHeader => Text.Length == 0;
    }

    public readonly List<Row> Rows = new();
    public int SelectedItem = -1;

    private PftFont? NormalFont => Host?.FontByTag(NormalFontTag) ?? Host?.FontByName("sf_white_10.pft");
    public int CellHeight => GuiPaint.FontHeight(NormalFont) + Padding;
    private static bool IsActiveRow(Row r) => !r.IsHeader && r.Enabled;

    // Rows from the entries: the inventory list takes the carried items, the purchase list the ones in the shopping mask (enabled
    // when also in the buying mask). A heading that is new inserts a header row in front of its first item.
    public static List<Row> Build(IEnumerable<InventoryEntry> items, bool purchase)
    {
        var rows = new List<Row>();
        foreach (InventoryEntry e in items)
        {
            if (purchase ? !e.Shopping : e.Count == 0)
                continue;
            string heading = e.Heading.Length > 0 ? e.Heading : " ";
            var item = new Row(heading, e.Description.Length > 0 ? e.Description : "item " + e.Index.ToString(CultureInfo.InvariantCulture),
                purchase ? e.Price : e.Count, e.Index, purchase ? e.Buying : true);
            int at = 0;
            while (at < rows.Count && (sbyte)rows[at].Heading[0] < (sbyte)heading[0])
                ++at;
            if (at == rows.Count || rows[at].Heading[0] != heading[0])
                rows.Insert(at, new Row(heading, "", 0, -1, false));
            while (at < rows.Count && rows[at].Heading[0] == heading[0])
                ++at;
            rows.Insert(at, item);
        }
        return rows;
    }

    public override void PreRender()
    {
        Rows.Clear();
        if (Host?.Hud is IHudSource src && src.HasPlayerState)
            Rows.AddRange(Build(src.InventoryItems, Purchase));
        int sel = Rows.FindIndex(r => IsActiveRow(r) && r.Item == SelectedItem);
        if (sel < 0)
            SelectedItem = -1;
        Selected = sel;
        int h = Rows.Count * CellHeight;
        if (h != H)
            H = h;
    }

    // Control::getValue answers the item index of the selected row, -1 without one.
    public override string GetValue() => SelectedItem.ToString(CultureInfo.InvariantCulture);

    protected override void ApplyValue(string v)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            SelectedItem = i;
    }

    public string VisibleSet() => string.Join(" ", Rows.Where(r => !r.IsHeader).Select(r => r.Item.ToString(CultureInfo.InvariantCulture)));

    public void SelectRow(int row, bool doubleClick)
    {
        if (row < 0 || row >= Rows.Count || !IsActiveRow(Rows[row]))
        {
            SelectedItem = -1;
            return;
        }
        SelectedItem = Rows[row].Item;
        Selected = row;
        string arg = SelectedItem.ToString(CultureInfo.InvariantCulture);
        if (Name != null && Host != null && Host.HasFunction(Name + "::onSelect"))
            Host.Call(Name + "::onSelect", arg);
        if (doubleClick && Name != null && Host != null && Host.HasFunction(Name + "::onDoubleClick"))
            Host.Call(Name + "::onDoubleClick", arg);
        PostMessage();
    }

    public override bool MsDown(GuiEvent e)
    {
        bool r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        if (Active && CellHeight > 0)
            SelectRow(ly / CellHeight, e.ClickCount >= 2);
        return r;
    }

    public override bool KeyDown(GuiEvent e) => base.KeyDown(e);

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        int rh = CellHeight;
        PftFont? normal = NormalFont;
        PftFont? selected = Host?.FontByTag(SelectedFontTag) ?? normal;
        PftFont? disabled = Host?.FontByTag(DisabledFontTag) ?? normal;
        PbmpBitmap? star = Host?.BitmapByName("I_Favorite.bmp");
        for (int i = 0; i < Rows.Count; ++i)
        {
            int y = gy + i * rh;
            if (y > p.Clip.Bottom)
                break;
            if (y + rh < p.Clip.Top)
                continue;
            Row row = Rows[i];
            if (row.IsHeader)
            {
                // The outline of the cell (300 wide, one pixel left of the control, so its left and right edges lie outside the extent):
                // only the top and bottom edges show. The routine names the rectangle (x - 1, y) to (x + 299, y + cell height - 1)
                // in colour 0xf0 and draws a row's text at (x + 15, y) [code 0048ac04]; the cell origin is the list origin plus
                // row x cell height [code 0053ccd8]. Stock frames show the edges and the text one pixel lower than that
                // rectangle, which no instruction of the routine accounts for: the difference sits in the origin of the list inside
                // its scroll, so it stays here as the one row offset (TextY, +1 on the edges) until that origin is traced.
                p.Fill(gx, y + 1, CellWidth, 1, HeaderColor);
                p.Fill(gx, y + rh, CellWidth, 1, HeaderColor);
                p.Text(selected, gx + 2, y + TextY, row.Heading.Length > 1 ? row.Heading[1..] : "");
                continue;
            }
            p.Fill(gx, y, W, rh, 0);
            PftFont? f = !row.Enabled ? disabled : row.Item == SelectedItem ? selected : normal;
            p.Text(f, gx + TextX, y + TextY, row.Text);
            p.Text(f, gx + ValueX, y + TextY, row.Value.ToString(CultureInfo.InvariantCulture));
            if (star != null && Host != null && Host.Favorites.Contains(row.Item))
                p.Bitmap(star, gx + StarX, y + (rh - star.Height) / 2);
        }
        RenderChildren(p, gx, gy);
    }
}
