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
    // The action maps (the controls screen rows look their keys up in them).
    public OpenTribes.Script.ClientState? Actions;
    // The item indices marked as favorites (CmdInventory::setFavorites); the inventory lists draw a star beside them.
    public readonly HashSet<int> Favorites = new();
    // Called when a PaletteCtrl wakes with the palette file name.
    public Action<string>? OnPalette;
    // Movie control hooks: a movie palette (768 RGB bytes) to merge into the active palette at palStart for palColors entries,
    // the end of a movie (the active palette comes back), and the start of the movie sound (returns the stop call).
    public Action<byte[], int, int>? OnMoviePalette;
    public Action? OnMovieEnd;
    public Func<OpenTribes.Platform.Audio.WavData, Action?>? StartMovieAudio;
    // The model views (FearGui::ShapeView, SkinView) draw through this: the client owns the shape renderer.
    public IModelViewRenderer? ModelViews;

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

    // FearGuiChatDisplay add [code 004a525c]: a line that holds no text once the tags are cut (the alert sound of
    // `Client::sendMessage(%c, 1, "~wLeftMissionArea.wav")` arrives as "") copies no characters, so the ring slot is given back
    // (DAT_00623488 is decremented) and the display does not scroll. The console still prints it.
    public static bool ChatLineShown(string text) => text.Length > 0;

    // The current command line of the chat display (message type 4) and the state that decides whether and how it is drawn.
    public string CommandText = "";
    public bool CommandActive, CommandAccepted;

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

    public string? TagText(uint tag) => tag == 0 ? null : Interp.TagTextRaw((int)tag) ?? Interp.TagText((int)tag);

    // The file name before the first comma of a tag text.
    public static string FirstWord(string text)
    {
        int i = text.IndexOf(' ');
        return i < 0 ? text : text[..i];
    }

    public static string FirstItem(string text)
    {
        int c = text.IndexOf(',');
        return (c < 0 ? text : text[..c]).Trim();
    }

    // ---- resources ----

    // Puts a font into the cache under a file name (the loaders of Res are not needed; tests and tools).
    public void SetFont(string name, PftFont font) => mFonts[name] = font;
    public void SetArray(string name, PbmaArray array) => mArrays[name] = array;
    public void SetBitmap(string name, PbmpBitmap bitmap) => mBitmaps[name] = bitmap;

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

// The inputs of the zoom branch of Crosshair 004ab500 (docs/gui.md 4.2): Cur = 0060f0a0, ZoomFov = 0060f0a4, Target = PSC +0x40c,
// Start = +0x414, End = +0x418, Now = 006a83a0 (ms), StepStamp = 0060f0b4 (time of the last INC_SNIPER_FOV with the zoom off).
public readonly record struct HudZoom(double Cur, double ZoomFov, double Target, uint Start, uint End, uint Now, uint StepStamp);

// docs/gui.md 4.1: what the HUD classes read from the client player state and the control object.
public interface IHudSource
{
    bool HasPlayerState { get; }
    // The sensor ping and the crosshair draw only while the client's own player or a vehicle it rides is the control object
    // [code 004ea720, 004ab500]: not in the observer camera.
    bool InPlayerView => HasPlayerState;
    // The weapon list draws only while the control object is the client's own player (PSC +0xa4 == the controlled player 006a83cc), not in a
    // vehicle it rides [code 004e8fe8]. The ping and the crosshair also take an object flagged 0x20000000 (Vehicle, Flier) [code 004ea720].
    bool ControlIsPlayer => HasPlayerState;
    // PSC +0xa0 (first person on) and view distance +0x9c at 0: the crosshair draws only then [code 004ab500].
    bool FirstPersonView => true;
    // The zoom state the crosshair reads (00404578 getter: current FOV 0060f0a0, zoom FOV 0060f0a4, PSC +0x40c/+0x414/+0x418, time
    // 006a83a0, last zoom step stamp 0060f0b4), null without one.
    HudZoom? Zoom => null;
    // The control object's damage level, 0.0 healthy to 1.0 dead (vtable +0xf8).
    double DamageLevel { get; }
    // Energy level 0.0 to 1.0 (vtable +0xfc).
    double EnergyLevel { get; }
    // Yaw of the control object in radians (compass).
    double Yaw { get; }
    // The waypoint of the local client (006a83e4 set) with its world position and the control object's, null without one.
    HudWaypoint? Waypoint => null;
    // The HUD timer (setHudTimer) in seconds.
    double ClockSeconds { get; }
    // Weapon HUD: the weapon rows in datablock order (docs/gui.md 4.2).
    IReadOnlyList<WeaponRow> Weapons { get; }
    // The marks and the label of the crosshair target for a view of this size (docs/gui.md 4.2 Crosshair), null without a view.
    HudTargetResult? CrosshairTarget(int width, int height) => null;
    // The aim triangles with their distance labels over the placed targets for a view of this size (docs/gui.md 4.2, `004ada90`).
    IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height) => Array.Empty<HudTargetMarker>();
    bool SensorPing { get; }
    bool SensorLocked { get; }
    // The item datablocks with showInventory set, with the counts and the station masks of the client player state (the lists of
    // CmdInventory.gui). Empty without a client.
    IReadOnlyList<InventoryEntry> InventoryItems => Array.Empty<InventoryEntry>();
    // The scoreboard of the client player manager (docs/client-net.md 2.3 DeltaScoreEvent) and the menu a script put on display.
    ScoreBoard? Scores => null;
    ServerMenuView? ServerMenu => null;
    // The player chat menu (`v`) as the chat display draws it: heading line and item lines, null when it is closed.
    PlayMenuView? PlayMenu => null;
    // The state of the chat menu object of that name for a FGChatMenu control, null when there is no such chat menu (docs/gui.md 3.3 "The command screen panels").
    ChatMenuState? NamedMenu(string name) => null;
    // The rows of the command team list of the local player's team (CommandTeamCtrl, docs/gui.md 3.3 "The command screen panels").
    IReadOnlyList<CommandTeamRow> CommandTeam => Array.Empty<CommandTeamRow>();
    // The base picture of the commander map, null until the world is rendered (docs/gui.md 3.3 "The command map").
    CommandMapPicture? CommandMap => null;
    // The slots and the local client for the marks over it (null without a client).
    CommandMapMarks? CommandMapMarks => null;
    // The mission objective lines (TeamObjectiveEvent), 32 slots; a slot nobody set is null.
    IReadOnlyList<string?> ObjectiveLines => Array.Empty<string?>();
}

// The compass input: the waypoint (006a83e8, 006a83ec) and the control object's x and y.
public readonly record struct HudWaypoint(double X, double Y, double PlayerX, double PlayerY);

// One row of the weapon HUD: the item's hudIcon, whether its "on" icon shows (the mounted weapon, or a firing one when the item
// highlights on active), and the ammo count (negative for unlimited).
public readonly record struct WeaponRow(string Icon, bool Active, int Ammo);

// The scoreboard: the two heading strings (tab separated columns, each column after the first starts with its x position as one
// character) and the rows with their placeholders (%n name, %t team, %p ping, %l loss) already replaced.
public sealed record ScoreBoard(string TeamHeading, string ClientHeading, IReadOnlyList<ScoreRow> Teams, IReadOnlyList<ScoreRow> Clients, int LocalClient);
public readonly record struct ScoreRow(int Id, string Text, int Sort);

// A chat menu on display: its title and the items of its top level with the key that selects each.
public sealed record ServerMenuView(string Title, IReadOnlyList<(char Key, string Text)> Items);

// The player chat menu on display: the heading (the menu's title at the top level, else the headings above joined with ':') and the
// item lines ("key: text").
public sealed record PlayMenuView(string Heading, IReadOnlyList<string> Lines);

// One item datablock for the inventory lists: heading (the first character is the sort key), description, price, the count the
// player carries, and the shopping and buying bits of the station masks.
public readonly record struct InventoryEntry(int Index, string Heading, string Description, int Price, int Count, bool Shopping, bool Buying);

public sealed class StaticHudSource : IHudSource
{
    public bool HasPlayerState { get; set; } = true;
    public double DamageLevel { get; set; }
    public double EnergyLevel { get; set; } = 1;
    public double Yaw { get; set; }
    public HudWaypoint? Waypoint { get; set; }
    public double ClockSeconds { get; set; }
    public List<WeaponRow> WeaponList { get; } = new();
    public IReadOnlyList<WeaponRow> Weapons => WeaponList;
    public bool SensorPing { get; set; }
    public bool SensorLocked { get; set; }
    public HudZoom? Zoom { get; set; }
    public List<HudTargetMarker> Markers { get; } = new();
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height) => Markers;
    public List<InventoryEntry> Inventory { get; } = new();
    public IReadOnlyList<InventoryEntry> InventoryItems => Inventory;
    public ScoreBoard? Board { get; set; }
    public ScoreBoard? Scores => Board;
    public ServerMenuView? Menu { get; set; }
    public ServerMenuView? ServerMenu => Menu;
    public PlayMenuView? PlayMenuValue { get; set; }
    public PlayMenuView? PlayMenu => PlayMenuValue;
    public CommandMapPicture? CommandMapValue { get; set; }
    public CommandMapPicture? CommandMap => CommandMapValue;
}
