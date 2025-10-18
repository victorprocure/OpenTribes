// What the GUI needs from the rest of the client: the console, the resources (fonts, bitmaps, tags), the audio stub and
// the clock. Tests substitute each part.
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

namespace OpenTribes.Gui;

// Stub audio interface (C7 replaces it): the GUI only asks for a named wave to play.
public interface IGuiAudio
{
    void Play(string name);
}

public sealed class RecordingAudio : IGuiAudio
{
    public readonly List<string> Played = new();
    public void Play(string name) => Played.Add(name);
}

public sealed class GuiHost
{
    public readonly Interpreter Interp;
    public ResourceManager? Res;
    public string Root = "";
    public IGuiAudio Audio = new RecordingAudio();
    public Func<long> Now = () => Environment.TickCount64;
    public Action<string> Log = s => Console.WriteLine(s);
    public string Clipboard = "";
    // The HUD data source (docs/gui.md 4.1); null means no client player state.
    public IHudSource? Hud;
    // The item indices marked as favorites (CmdInventory::setFavorites); the inventory lists draw a star beside them.
    public readonly HashSet<int> Favorites = new();
    // Called when a PaletteCtrl wakes with the palette file name.
    public Action<string>? OnPalette;
    // Movie control hooks: a movie palette (768 RGB bytes) to merge into the active palette at palStart for palColors entries,
    // the end of a movie (the active palette comes back), and the start of the movie sound (returns the stop call).
    public Action<byte[], int, int>? OnMoviePalette;
    public Action? OnMovieEnd;
    public Func<OpenTribes.Platform.Audio.WavData, Action?>? StartMovieAudio;

    private readonly Dictionary<string, PftFont?> mFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmpBitmap?> mBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmaArray?> mArrays = new(StringComparer.OrdinalIgnoreCase);

    public GuiHost(Interpreter interp, ResourceManager? res = null)
    {
        Interp = interp;
        Res = res;
    }

    public long TickMs => Now();

    // Chat lines (text, font tag) the HUD classes draw.
    public readonly List<(string Text, uint FontTag)> Chat = new();
    public void AddChat(string text, uint fontTag = 0) => Chat.Add((text, fontTag));

    // Client::centerPrint(text, type) [code 0042276c]: one message buffer and its type (0 centred, 1 near the bottom, 2 near the
    // top). The scripts clear it with an empty text after their own timeout.
    public string CenterText = "";
    public int CenterType;
    public void CenterPrint(string text, int type)
    {
        CenterText = text;
        CenterType = type;
    }

    // ---- console ----

    public string Eval(string text) => Interp.Evaluate(text);
    public string GetVar(string name) => Interp.PeekVariable(name);
    public void SetVar(string name, string value) => Interp.SetVariable(name, value);
    public bool HasFunction(string name) => Interp.HasFunction(name);
    public string Call(string name, params string[] args) => Interp.CallFunction(name, args);
    public void Print(string line) => Interp.Print(1, line);

    // ---- tags (1.5) ----

    public string? TagText(uint tag) => tag == 0 ? null : Interp.TagText((int)tag);

    // The file name before the first comma of a tag text.
    public static string FirstItem(string text)
    {
        int c = text.IndexOf(',');
        return (c < 0 ? text : text[..c]).Trim();
    }

    // ---- resources ----

    // Puts a font into the cache under a file name (the loaders of Res are not needed; tests and tools).
    public void SetFont(string name, PftFont font) => mFonts[name] = font;

    public PftFont? FontByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!mFonts.TryGetValue(name, out PftFont? f))
        {
            f = Res == null ? null : GfxAssets.LoadFont(Res, name);
            mFonts[name] = f;
        }
        return f;
    }

    // The font of `<fN>` (GuiMarkup): the tag of esf.strings.cs by name when a script defines it, else the table of the formatted
    // text control [code 00489738]: f0 tag 150111 (10 point standard), f1 tag 150121 (10 point hilite), f2 tag 150161 (10 point
    // selected). N outside the table is the control's own font.
    public PftFont? MarkupFont(int n)
    {
        string name = GuiMarkup.FontTagName(n);
        if (name.Length == 0)
            return null;
        int id = Interp.TagId(name);
        if (id > 0)
            return FontByTag((uint)id);
        return n switch { 0 => FontByTag(150111), 1 => FontByTag(150121), 2 => FontByTag(150161), _ => null };
    }

    public PftFont? FontByTag(uint tag) => TagText(tag) is string t ? FontByName(FirstItem(t)) : null;

    public PbmpBitmap? BitmapByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!mBitmaps.TryGetValue(name, out PbmpBitmap? b))
        {
            b = Res == null ? null : GfxAssets.LoadBitmap(Res, name);
            mBitmaps[name] = b;
        }
        return b;
    }

    public PbmpBitmap? BitmapByTag(uint tag) => TagText(tag) is string t ? BitmapByName(FirstItem(t)) : null;

    public PbmaArray? ArrayByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!mArrays.TryGetValue(name, out PbmaArray? a))
        {
            a = Res == null ? null : GfxAssets.LoadBitmapArray(Res, name);
            mArrays[name] = a;
        }
        return a;
    }

    // Reads a `.gui` file: a direct path first, then the resource manager (docs/gui.md 1.1).
    public bool ReadGuiFile(string name, out byte[] data)
    {
        data = Array.Empty<byte>();
        string host = name.Replace('\\', '/');
        foreach (string p in new[] { host, Path.Combine(Root, host) })
        {
            try
            {
                if (File.Exists(p))
                {
                    data = File.ReadAllBytes(p);
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (Res == null)
            return false;
        if (GfxAssets.Read(Res, name, out data))
            return true;
        return !name.Contains('\\') && GfxAssets.Read(Res, "gui\\" + name, out data);
    }
}

// docs/gui.md 4.1: what the HUD classes read from the client player state and the control object.
public interface IHudSource
{
    bool HasPlayerState { get; }
    // The control object's damage level, 0.0 healthy to 1.0 dead (vtable +0xf8).
    double DamageLevel { get; }
    // Energy level 0.0 to 1.0 (vtable +0xfc).
    double EnergyLevel { get; }
    // Yaw of the control object in radians (compass).
    double Yaw { get; }
    // The HUD timer (setHudTimer) in seconds.
    double ClockSeconds { get; }
    // Weapon HUD: the weapon rows in datablock order (docs/gui.md 4.2).
    IReadOnlyList<WeaponRow> Weapons { get; }
    bool SensorPing { get; }
    bool SensorLocked { get; }
    // The item datablocks with showInventory set, with the counts and the station masks of the client player state (the lists of
    // CmdInventory.gui). Empty without a client.
    IReadOnlyList<InventoryEntry> InventoryItems => Array.Empty<InventoryEntry>();
    // The scoreboard of the client player manager (docs/client-net.md 2.3 DeltaScoreEvent) and the menu a script put on display.
    ScoreBoard? Scores => null;
    ServerMenuView? ServerMenu => null;
}

// One row of the weapon HUD: the item's hudIcon, whether its "on" icon shows (the mounted weapon, or a firing one when the item
// highlights on active), and the ammo count (negative for unlimited).
public readonly record struct WeaponRow(string Icon, bool Active, int Ammo);

// The scoreboard: the two heading strings (tab separated columns, each column after the first starts with its x position as one
// character) and the rows with their placeholders (%n name, %t team, %p ping, %l loss) already replaced.
public sealed record ScoreBoard(string TeamHeading, string ClientHeading, IReadOnlyList<ScoreRow> Teams, IReadOnlyList<ScoreRow> Clients, int LocalClient);
public readonly record struct ScoreRow(int Id, string Text, int Sort);

// A chat menu on display: its title and the items of its top level with the key that selects each.
public sealed record ServerMenuView(string Title, IReadOnlyList<(char Key, string Text)> Items);

// One item datablock for the inventory lists: heading (the first character is the sort key), description, price, the count the
// player carries, and the shopping and buying bits of the station masks.
public readonly record struct InventoryEntry(int Index, string Heading, string Description, int Price, int Count, bool Shopping, bool Buying);

public sealed class StaticHudSource : IHudSource
{
    public bool HasPlayerState { get; set; } = true;
    public double DamageLevel { get; set; }
    public double EnergyLevel { get; set; } = 1;
    public double Yaw { get; set; }
    public double ClockSeconds { get; set; }
    public List<WeaponRow> WeaponList { get; } = new();
    public IReadOnlyList<WeaponRow> Weapons => WeaponList;
    public bool SensorPing { get; set; }
    public bool SensorLocked { get; set; }
    public List<InventoryEntry> Inventory { get; } = new();
    public IReadOnlyList<InventoryEntry> InventoryItems => Inventory;
    public ScoreBoard? Board { get; set; }
    public ScoreBoard? Scores => Board;
    public ServerMenuView? Menu { get; set; }
    public ServerMenuView? ServerMenu => Menu;
}
