namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Platform.Gfx;

/// <summary>The inventory and purchase lists of CmdInventory.gui: FearGuiInventory (the items the player carries) and FearGuiPurchase (the items a station sells).</summary>
/// <remarks>Follows the stock row rebuild (0048afcc, 0048b05c), row insert (0048a700), row draw (0048ac04) and select (0048aaa4).</remarks>
public sealed class GuiInventoryList : GuiList
{
    /// <summary>The list kind: false lists the carried items with their count, true lists the items for sale with their price.</summary>
    public bool Purchase { get; set; }

    /// <summary>Gets a value that is always false: both classes answer no to the tab stop query (0048a6c4), so the inventory key reaches the action maps while the screen is up.</summary>
    public override bool WantsTab => false;
    /// <summary>Gets a value that is always false: both classes answer no to the gain-focus query (responder slot 0x40, 0048a6c0), so the list never holds the keyboard.</summary>
    public override bool CanTake() => false;

    /// <summary>The font tag of a normal row (150114, 0048aea0).</summary>
    public const uint NormalFontTag = 150114;
    /// <summary>The font tag of the selected row (150164).</summary>
    public const uint SelectedFontTag = 150164;
    /// <summary>The font tag of a disabled row (150134).</summary>
    public const uint DisabledFontTag = 150134;
    /// <summary>The padding added to the font height for the cell height.</summary>
    public const int Padding = 6;
    /// <summary>The width of a header outline.</summary>
    public const int CellWidth = 300;
    /// <summary>The y offset of a row's text.</summary>
    public const int TextY = 1;
    /// <summary>The x offset of an item's text.</summary>
    public const int TextX = 15;
    /// <summary>The x offset of the count or price.</summary>
    public const int ValueX = 0x9e;
    /// <summary>The x offset of the favorite star.</summary>
    public const int StarX = 3;
    /// <summary>The palette index of a header outline.</summary>
    public const int HeaderColor = 0xf0;

    /// <summary>A row: a header (no item text) or an item.</summary>
    /// <param name="Heading">The heading. Its first character is the sort key and is not shown.</param>
    /// <param name="Text">The item text, empty for a header.</param>
    /// <param name="Value">The carried count or the price.</param>
    /// <param name="Item">The item index, -1 for a header.</param>
    /// <param name="Enabled">True when the row can be selected.</param>
    public readonly record struct Row(string Heading, string Text, int Value, int Item, bool Enabled)
    {
        /// <summary>Gets a value that is true for a header row.</summary>
        public bool IsHeader => Text.Length == 0;
    }

    /// <summary>The rows shown, rebuilt before each render.</summary>
    public List<Row> Rows { get; } = [];
    /// <summary>The item index of the selected row, -1 for none.</summary>
    public int SelectedItem { get; set; } = -1;

    private PftFont? NormalFont => Host?.FontByTag(NormalFontTag) ?? Host?.FontByName("sf_white_10.pft");
    /// <summary>Gets the pixel height of one row.</summary>
    public int CellHeight => GuiPaint.FontHeight(NormalFont) + Padding;
    private static bool IsActiveRow(Row r) => !r.IsHeader && r.Enabled;

    /// <summary>Builds the rows from the entries.</summary>
    /// <param name="items">The inventory entries.</param>
    /// <param name="purchase">True to list the items for sale, false for the carried items.</param>
    /// <returns>The rows. The purchase list takes the items in the shopping mask, enabled when also in the buying mask. A new heading inserts a header row in front of its first item.</returns>
    public static List<Row> Build(IEnumerable<InventoryEntry> items, bool purchase)
    {
        var rows = new List<Row>();
        foreach (var e in items)
        {
            if (purchase ? !e.Shopping : e.Count == 0)
                continue;
            var heading = e.Heading.Length > 0 ? e.Heading : " ";
            var item = new Row(heading, e.Description.Length > 0 ? e.Description : "item " + e.Index.ToString(CultureInfo.InvariantCulture),
                purchase ? e.Price : e.Count, e.Index, !purchase || e.Buying);
            var at = 0;
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

    /// <inheritdoc/>
    public override void PreRender()
    {
        Rows.Clear();
        if (Host?.Hud is IHudSource src && src.HasPlayerState)
            Rows.AddRange(Build(src.InventoryItems, Purchase));
        var sel = Rows.FindIndex(r => IsActiveRow(r) && r.Item == SelectedItem);
        if (sel < 0)
            SelectedItem = -1;
        Selected = sel;
        var h = Rows.Count * CellHeight;
        if (h != H)
            H = h;
    }

    /// <summary>Returns the item index of the selected row (Control::getValue), -1 without one.</summary>
    /// <returns>The item index as text.</returns>
    public override string GetValue() => SelectedItem.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    protected override void ApplyValue(string v)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            SelectedItem = i;
    }

    /// <summary>Lists the item indices of the item rows.</summary>
    /// <returns>The indices separated by spaces.</returns>
    public string VisibleSet() => string.Join(" ", Rows.Where(r => !r.IsHeader).Select(r => r.Item.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Selects a row and calls the `onSelect` handler of the control, and `onDoubleClick` for a double click.</summary>
    /// <param name="row">The row index. A row that does not exist, or a header or disabled row, clears the selection.</param>
    /// <param name="doubleClick">True for a double click.</param>
    public void SelectRow(int row, bool doubleClick)
    {
        if (row < 0 || row >= Rows.Count || !IsActiveRow(Rows[row]))
        {
            SelectedItem = -1;
            return;
        }
        SelectedItem = Rows[row].Item;
        Selected = row;
        var arg = SelectedItem.ToString(CultureInfo.InvariantCulture);
        if (Name != null && Host != null && Host.HasFunction(Name + "::onSelect"))
            Host.Call(Name + "::onSelect", arg);
        if (doubleClick && Name != null && Host != null && Host.HasFunction(Name + "::onDoubleClick"))
            Host.Call(Name + "::onDoubleClick", arg);
        PostMessage();
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        var r = base.MsDown(e);
        var (_, ly) = GlobalToLocal(e.X, e.Y);
        if (Active && CellHeight > 0)
            SelectRow(ly / CellHeight, e.ClickCount >= 2);
        return r;
    }

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e) => base.KeyDown(e);

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        var rh = CellHeight;
        var normal = NormalFont;
        var selected = Host?.FontByTag(SelectedFontTag) ?? normal;
        var disabled = Host?.FontByTag(DisabledFontTag) ?? normal;
        var star = Host?.BitmapByName("I_Favorite.bmp");
        for (var i = 0; i < Rows.Count; ++i)
        {
            var y = gy + (i * rh);
            if (y > p.Clip.Bottom)
                break;
            if (y + rh < p.Clip.Top)
                continue;
            var row = Rows[i];
            if (row.IsHeader)
            {
                // The cell is 300 wide and starts one pixel left of the control, so only its top and bottom edges show (0048ac04, cell origin 0053ccd8).
                // TextY is the one row offset the stock frames need: no instruction of the routine accounts for it.
                p.Fill(gx, y + 1, CellWidth, 1, HeaderColor);
                p.Fill(gx, y + rh, CellWidth, 1, HeaderColor);
                p.Text(selected, gx + 2, y + TextY, row.Heading.Length > 1 ? row.Heading[1..] : "");
                continue;
            }
            p.Fill(gx, y, W, rh, 0);
            var f = !row.Enabled ? disabled : row.Item == SelectedItem ? selected : normal;
            p.Text(f, gx + TextX, y + TextY, row.Text);
            p.Text(f, gx + ValueX, y + TextY, row.Value.ToString(CultureInfo.InvariantCulture));
            if (star != null && Host != null && Host.Favorites.Contains(row.Item))
                p.Bitmap(star, gx + StarX, y + ((rh - star.Height) / 2));
        }
        RenderChildren(p, gx, gy);
    }
}
