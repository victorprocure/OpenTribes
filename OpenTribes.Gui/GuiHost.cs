namespace OpenTribes.Gui;

using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

/// <summary>The audio the GUI needs: it only asks for a named wave to play.</summary>
/// <remarks>A stub until the client audio (C7) replaces it.</remarks>
public interface IGuiAudio
{
    /// <summary>Plays a named wave.</summary>
    /// <param name="name">The wave file name.</param>
    public void Play(string name);
}

/// <summary>An audio stub that records the names it was asked to play.</summary>
public sealed class RecordingAudio : IGuiAudio
{
    /// <summary>The names played, in order.</summary>
    public List<string> Played { get; } = [];
    /// <inheritdoc/>
    public void Play(string name) => Played.Add(name);
}

/// <summary>What the GUI needs from the rest of the client: the console, the resources (fonts, bitmaps, tags), the audio and the clock.</summary>
/// <remarks>Tests substitute each part.</remarks>
public sealed class GuiHost
{
    /// <summary>The script interpreter that runs the console.</summary>
    public Interpreter Interp { get; }
    /// <summary>The resource manager that loads fonts, bitmaps and `.gui` files, or null.</summary>
    public ResourceManager? Res { get; set; }
    /// <summary>The directory searched first for `.gui` files.</summary>
    public string Root { get; set; } = "";
    /// <summary>The audio the controls play through.</summary>
    public IGuiAudio Audio { get; set; } = new RecordingAudio();
    /// <summary>The clock in milliseconds.</summary>
    public Func<long> Now { get; set; } = () => Environment.TickCount64;
    /// <summary>Receives log lines.</summary>
    public Action<string> Log { get; set; } = Console.WriteLine;
    /// <summary>The clipboard text.</summary>
    public string Clipboard { get; set; } = "";
    /// <summary>The HUD data source (docs/gui.md 4.1), null for no client player state.</summary>
    public IHudSource? Hud { get; set; }
    /// <summary>The action maps the controls screen rows look their keys up in.</summary>
    public ClientState? Actions { get; set; }
    /// <summary>The item indices marked as favorites (CmdInventory::setFavorites). The inventory lists draw a star beside them.</summary>
    public HashSet<int> Favorites { get; } = [];
    /// <summary>Called when a PaletteCtrl wakes, with the palette file name.</summary>
    public Action<string>? OnPalette { get; set; }
    /// <summary>Called with a movie palette (768 RGB bytes) to merge into the active palette at palStart for palColors entries.</summary>
    public Action<byte[], int, int>? OnMoviePalette { get; set; }
    /// <summary>Called at the end of a movie: the active palette comes back.</summary>
    public Action? OnMovieEnd { get; set; }
    /// <summary>Called at the start of the movie sound. Returns the call that stops it.</summary>
    public Func<Platform.Audio.WavData, Action?>? StartMovieAudio { get; set; }
    /// <summary>Returns whether a chat menu object of that name exists (`CommandChatMenu`, TSCommander 004d2a98).</summary>
    public Func<string, bool>? ChatMenuExists { get; set; }
    /// <summary>Calls setCMMode on the chat menu object of that name (TSCommander 004d2a98).</summary>
    public Action<string, int>? SetChatMenuMode { get; set; }
    /// <summary>The renderer the model views (FearGui::ShapeView, SkinView) draw through: the client owns the shape renderer.</summary>
    public IModelViewRenderer? ModelViews { get; set; }

    private readonly Dictionary<string, PftFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmpBitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmaArray?> _arrays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a host.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="res">The resource manager, or null.</param>
    public GuiHost(Interpreter interp, ResourceManager? res = null)
    {
        Interp = interp;
        Res = res;
    }

    /// <summary>Gets the current time in milliseconds.</summary>
    public long TickMs => Now();

    /// <summary>The chat lines (text, font tag) the HUD classes draw.</summary>
    public List<(string Text, uint FontTag)> Chat { get; } = [];
    /// <summary>Adds a chat line.</summary>
    /// <param name="text">The line text.</param>
    /// <param name="fontTag">The font tag.</param>
    public void AddChat(string text, uint fontTag = 0) => Chat.Add((text, fontTag));

    /// <summary>Returns whether a chat line is shown.</summary>
    /// <param name="text">The line text with the tags cut.</param>
    /// <returns>False for a line that holds no text.</returns>
    /// <remarks>Follows FearGuiChatDisplay add (004a525c): a line that holds no text once the tags are cut (the alert sound of `Client::sendMessage(%c, 1, "~wLeftMissionArea.wav")` arrives as "") copies no characters, so the ring slot is given back (DAT_00623488 is decremented) and the display does not scroll. The console still prints it.</remarks>
    public static bool ChatLineShown(string text) => text.Length > 0;

    /// <summary>The current command line of the chat display (message type 4).</summary>
    public string CommandText { get; set; } = "";
    /// <summary>True while the command line is active, which decides whether it is drawn.</summary>
    public bool CommandActive { get; set; }
    /// <summary>True when the command line was accepted, which decides how it is drawn.</summary>
    public bool CommandAccepted { get; set; }

    /// <summary>The message buffer of Client::centerPrint (0042276c). The scripts clear it with an empty text after their own timeout.</summary>
    public string CenterText { get; set; } = "";
    /// <summary>The type of the center print: 0 centred, 1 near the bottom, 2 near the top.</summary>
    public int CenterType { get; set; }
    /// <summary>Sets the center print message.</summary>
    /// <param name="text">The message.</param>
    /// <param name="type">The type: 0 centred, 1 near the bottom, 2 near the top.</param>
    public void CenterPrint(string text, int type)
    {
        CenterText = text;
        CenterType = type;
    }

    // ---- console ----

    /// <summary>Evaluates a console expression.</summary>
    /// <param name="text">The script text.</param>
    /// <returns>The result text.</returns>
    public string Eval(string text) => Interp.Evaluate(text);
    /// <summary>Adds the `$` the stock variable accessors put in front of a name that has none (005f3324).</summary>
    /// <remarks>Console_getVariableText and Console_setVariableText bind controls through this: CreateServer.gui binds `Server::HostName`.</remarks>
    private static string VarName(string name) => name.Length > 0 && name[0] != '$' ? "$" + name : name;
    /// <summary>Reads a console variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value text.</returns>
    public string GetVar(string name) => Interp.PeekVariable(VarName(name));
    /// <summary>Writes a console variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value text.</param>
    public void SetVar(string name, string value) => Interp.SetVariable(VarName(name), value);
    /// <summary>Returns whether a script function exists.</summary>
    /// <param name="name">The function name.</param>
    /// <returns>True when it exists.</returns>
    public bool HasFunction(string name) => Interp.HasFunction(name);
    /// <summary>Calls a script function.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The result text.</returns>
    public string Call(string name, params string[] args) => Interp.CallFunction(name, args);
    /// <summary>Prints a line to the console.</summary>
    /// <param name="line">The line.</param>
    public void Print(string line) => Interp.Print(1, line);

    // ---- tags (1.5) ----

    /// <summary>Looks up the text of a tag (docs/gui.md 1.5).</summary>
    /// <param name="tag">The tag, 0 for none.</param>
    /// <returns>The text, or null.</returns>
    public string? TagText(uint tag) => tag == 0 ? null : Interp.TagTextRaw((int)tag) ?? Interp.TagText((int)tag);

    /// <summary>Returns the text before the first space.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The first word.</returns>
    public static string FirstWord(string text)
    {
        var i = text.IndexOf(' ');
        return i < 0 ? text : text[..i];
    }

    /// <summary>Returns the trimmed text before the first comma, the file name of a tag text.</summary>
    /// <param name="text">The tag text.</param>
    /// <returns>The first item.</returns>
    public static string FirstItem(string text)
    {
        var c = text.IndexOf(',');
        return (c < 0 ? text : text[..c]).Trim();
    }

    // ---- resources ----

    /// <summary>Puts a font into the cache under a file name, so that the loaders of <see cref="Res"/> are not needed (tests and tools).</summary>
    /// <param name="name">The file name.</param>
    /// <param name="font">The font.</param>
    public void SetFont(string name, PftFont font) => _fonts[name] = font;
    /// <summary>Puts a bitmap array into the cache under a file name.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="array">The bitmap array.</param>
    public void SetArray(string name, PbmaArray array) => _arrays[name] = array;
    /// <summary>Puts a bitmap into the cache under a file name.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="bitmap">The bitmap.</param>
    public void SetBitmap(string name, PbmpBitmap bitmap) => _bitmaps[name] = bitmap;

    /// <summary>Returns a font, loading it on first use.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The font, or null when it is empty or cannot be loaded.</returns>
    public PftFont? FontByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!_fonts.TryGetValue(name, out var f))
        {
            f = Res == null ? null : GfxAssets.LoadFont(Res, name);
            _fonts[name] = f;
        }
        return f;
    }

    /// <summary>Returns the font named by a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The font, or null.</returns>
    public PftFont? FontByTag(uint tag) => TagText(tag) is string t ? FontByName(FirstItem(t)) : null;

    /// <summary>Returns a bitmap, loading it on first use.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The bitmap, or null when it is empty or cannot be loaded.</returns>
    public PbmpBitmap? BitmapByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!_bitmaps.TryGetValue(name, out var b))
        {
            b = Res == null ? null : GfxAssets.LoadBitmap(Res, name);
            _bitmaps[name] = b;
        }
        return b;
    }

    /// <summary>Returns the bitmap named by a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The bitmap, or null.</returns>
    public PbmpBitmap? BitmapByTag(uint tag) => TagText(tag) is string t ? BitmapByName(FirstItem(t)) : null;

    /// <summary>Returns a bitmap array, loading it on first use.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The array, or null when it is empty or cannot be loaded.</returns>
    public PbmaArray? ArrayByName(string name)
    {
        if (name.Length == 0)
            return null;
        if (!_arrays.TryGetValue(name, out var a))
        {
            a = Res == null ? null : GfxAssets.LoadBitmapArray(Res, name);
            _arrays[name] = a;
        }
        return a;
    }

    /// <summary>Reads a `.gui` file: a direct path first, then the resource manager (docs/gui.md 1.1).</summary>
    /// <param name="name">The file name.</param>
    /// <param name="data">Receives the file bytes.</param>
    /// <returns>True when the file was found.</returns>
    public bool ReadGuiFile(string name, out byte[] data)
    {
        data = [];
        var host = name.Replace('\\', '/');
        foreach (var p in new[] { host, Path.Combine(Root, host) })
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

/// <summary>The inputs of the zoom branch of the crosshair draw (004ab500, docs/gui.md 4.2).</summary>
/// <param name="Cur">The current field of view (0060f0a0).</param>
/// <param name="ZoomFov">The zoom field of view (0060f0a4).</param>
/// <param name="Target">The target field of view (PSC +0x40c).</param>
/// <param name="Start">The zoom start time (PSC +0x414).</param>
/// <param name="End">The zoom end time (PSC +0x418).</param>
/// <param name="Now">The time in milliseconds (006a83a0).</param>
/// <param name="StepStamp">The time of the last INC_SNIPER_FOV with the zoom off (0060f0b4).</param>
public readonly record struct HudZoom(double Cur, double ZoomFov, double Target, uint Start, uint End, uint Now, uint StepStamp);

/// <summary>What the HUD classes read from the client player state and the control object (docs/gui.md 4.1).</summary>
public interface IHudSource
{
    /// <summary>Gets a value that is true when there is a client player state.</summary>
    public bool HasPlayerState { get; }
    /// <summary>Gets a value that is true when the client's own player or a vehicle it rides is the control object: the sensor ping and the crosshair draw only then, not in the observer camera (004ea720, 004ab500).</summary>
    public bool InPlayerView => HasPlayerState;
    /// <summary>Gets a value that is true when the sensor ping shows: the control object is the client's own player or has object flag 0x20000000 (Vehicle, Flier). A controlled Turret shows none, not even the frame (004ea720, test at 004ea7d3).</summary>
    public bool SensorHudShown => HasPlayerState;
    /// <summary>Gets a value that is true when the control object is the client's own player (PSC +0xa4 equals the controlled player 006a83cc), not a vehicle it rides: the weapon list draws only then (004e8fe8).</summary>
    /// <remarks>The ping and the crosshair also take an object flagged 0x20000000 (Vehicle, Flier) (004ea720).</remarks>
    public bool ControlIsPlayer => HasPlayerState;
    /// <summary>Gets a value that is true when the view is first person (PSC +0xa0 on, view distance +0x9c at 0): the crosshair draws only then (004ab500).</summary>
    public bool FirstPersonView => true;
    /// <summary>Gets the zoom state the crosshair reads, null without one.</summary>
    /// <remarks>Follows the getter at 00404578: current FOV 0060f0a0, zoom FOV 0060f0a4, PSC +0x40c, +0x414 and +0x418, time 006a83a0, last zoom step stamp 0060f0b4.</remarks>
    public HudZoom? Zoom => null;
    /// <summary>Gets the control object's damage level, 0.0 healthy to 1.0 dead (vtable +0xf8).</summary>
    public double DamageLevel { get; }
    /// <summary>Gets the energy level, 0.0 to 1.0 (vtable +0xfc).</summary>
    public double EnergyLevel { get; }
    /// <summary>Gets the yaw of the control object in radians (compass).</summary>
    public double Yaw { get; }
    /// <summary>Gets the waypoint of the local client (006a83e4 set) with its world position and the control object's, null without one.</summary>
    public HudWaypoint? Waypoint => null;
    /// <summary>Gets the HUD timer (setHudTimer) in seconds.</summary>
    public double ClockSeconds { get; }
    /// <summary>Gets the weapon rows of the weapon HUD in datablock order (docs/gui.md 4.2).</summary>
    public IReadOnlyList<WeaponRow> Weapons { get; }
    /// <summary>Returns the marks and the label of the crosshair target (docs/gui.md 4.2 Crosshair).</summary>
    /// <param name="width">The view width.</param>
    /// <param name="height">The view height.</param>
    /// <returns>The target, null without a view.</returns>
    public HudTargetResult? CrosshairTarget(int width, int height) => null;
    /// <summary>Returns the aim triangles with their distance labels over the placed targets (docs/gui.md 4.2, 004ada90).</summary>
    /// <param name="width">The view width.</param>
    /// <param name="height">The view height.</param>
    /// <returns>The markers.</returns>
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height) => [];
    /// <summary>Gets a value that is true while the sensor pings.</summary>
    public bool SensorPing { get; }
    /// <summary>Gets a value that is true while the sensor has a lock.</summary>
    public bool SensorLocked { get; }
    /// <summary>Gets the item datablocks with showInventory set, with the counts and the station masks of the client player state (the lists of CmdInventory.gui). Empty without a client.</summary>
    public IReadOnlyList<InventoryEntry> InventoryItems => [];
    /// <summary>Gets the scoreboard of the client player manager (docs/client-net.md 2.3 DeltaScoreEvent), null without one.</summary>
    public ScoreBoard? Scores => null;
    /// <summary>Gets the menu a script put on display, null without one.</summary>
    public ServerMenuView? ServerMenu => null;
    /// <summary>Gets the player chat menu (`v`) as the chat display draws it, null when it is closed.</summary>
    public PlayMenuView? PlayMenu => null;
    /// <summary>Returns the state of the chat menu object of that name for a FGChatMenu control (docs/gui.md 3.3 "The command screen panels").</summary>
    /// <param name="name">The menu object name.</param>
    /// <returns>The state, null when there is no such chat menu.</returns>
    public ChatMenuState? NamedMenu(string name) => null;
    /// <summary>Gets the rows of the command team list of the local player's team (CommandTeamCtrl, docs/gui.md 3.3 "The command screen panels").</summary>
    public IReadOnlyList<CommandTeamRow> CommandTeam => [];
    /// <summary>Gets the rows of the Objects list (CommandItemCtrl) of the command screen.</summary>
    public IReadOnlyList<CommandListRow> CommandObjects => [];
    /// <summary>Gets the rows of the Controllables list (CommandTurretCtrl) of the command screen.</summary>
    public IReadOnlyList<CommandListRow> CommandControllables => [];
    /// <summary>Returns the client id of the first selected record of the local player's records (the CMDObserve toggle takes it).</summary>
    /// <returns>The client id, -1 for none.</returns>
    public int CommandFirstSelectedClient() => -1;
    /// <summary>Gets the base picture of the commander map, null until the world is rendered (docs/gui.md 3.3 "The command map").</summary>
    public CommandMapPicture? CommandMap => null;
    /// <summary>Gets the slots and the local client for the marks over the map, null without a client.</summary>
    public CommandMapMarks? CommandMapMarks => null;
    /// <summary>Gets the mission objective lines (TeamObjectiveEvent), 32 slots. A slot nobody set is null.</summary>
    public IReadOnlyList<string?> ObjectiveLines => [];

    // ---- what the command map and the team list read and write (TSCommander 004d1bf0 .. 004d3018, CommandTeamCtrl 004a8b00) ----
    /// <summary>Gets the local human client of the command map (PlayerManager_findHumanClient of the manager id), null without one.</summary>
    public CommandLocalClient? CommandLocal => null;
    /// <summary>Gets the 256 slots of the command map block as the map control reads them (+0x340 to +0x364), null without a client.</summary>
    public Game.ClientCommandSlot[]? CommandSlotTable => null;
    /// <summary>Returns the mapFilter field (+0x18) of the datablock of a group and index (DataBlockManager_getByIndex).</summary>
    /// <param name="group">The datablock group.</param>
    /// <param name="index">The datablock index.</param>
    /// <returns>The filter, null when the datablock does not exist.</returns>
    public int? CommandBlockFilter(int group, int index) => null;
    /// <summary>Returns the name of the human client with an id (PlayerManager_findHumanClient).</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>The name, null when there is none.</returns>
    public string? CommandClientName(int clientId) => null;
    /// <summary>Returns the label a target slot (128 and up) was given (TargetNameEvent).</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The label, null without one.</returns>
    public string? CommandTargetName(int slot) => null;
    /// <summary>Sets the selected byte (+0x68) of the client record with an id (PlayerManager_findClient).</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="selected">The new state.</param>
    public void CommandSetSelected(int clientId, bool selected) { }
    /// <summary>Flips the expanded byte (+0x69) of a record, as a click on the tree glyph does.</summary>
    /// <param name="clientId">The client id.</param>
    public void CommandToggleExpanded(int clientId) { }
    /// <summary>Returns whether a client stands above another in the command chain (PlayerManager_isInCommandChain, 0040cad4).</summary>
    /// <param name="clientId">The client below.</param>
    /// <param name="commander">The client above.</param>
    /// <returns>True when <paramref name="commander"/> stands above <paramref name="clientId"/>.</returns>
    public bool CommandInChain(int clientId, int commander) => false;
    /// <summary>Returns the command tree links of a record (+0x74 first subordinate, +0x78 next sibling) as client ids, -1 for none, and its selected flag.</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>The links, null for an unknown id.</returns>
    public (int FirstChild, int NextSibling, bool Selected)? CommandLinks(int clientId) => null;
}

/// <summary>The id and team of the local human client record.</summary>
/// <param name="Id">The client id.</param>
/// <param name="Team">The team index.</param>
public readonly record struct CommandLocalClient(int Id, int Team);

/// <summary>The compass input: the waypoint (006a83e8, 006a83ec) and the control object's position.</summary>
/// <param name="X">The waypoint x.</param>
/// <param name="Y">The waypoint y.</param>
/// <param name="PlayerX">The control object x.</param>
/// <param name="PlayerY">The control object y.</param>
public readonly record struct HudWaypoint(double X, double Y, double PlayerX, double PlayerY);

/// <summary>One row of the weapon HUD.</summary>
/// <param name="Icon">The hudIcon of the item.</param>
/// <param name="Active">True when its "on" icon shows: the mounted weapon, or a firing one when the item highlights on active.</param>
/// <param name="Ammo">The ammo count, negative for unlimited.</param>
public readonly record struct WeaponRow(string Icon, bool Active, int Ammo);

/// <summary>The scoreboard.</summary>
/// <param name="TeamHeading">The team heading: tab separated columns, each column after the first starts with its x position as one character.</param>
/// <param name="ClientHeading">The player heading, in the same format.</param>
/// <param name="Teams">The team rows.</param>
/// <param name="Clients">The player rows.</param>
/// <param name="LocalClient">The id of the local client.</param>
public sealed record ScoreBoard(string TeamHeading, string ClientHeading, IReadOnlyList<ScoreRow> Teams, IReadOnlyList<ScoreRow> Clients, int LocalClient);
/// <summary>One scoreboard row.</summary>
/// <param name="Id">The client or team id.</param>
/// <param name="Text">The row text with its placeholders (%n name, %t team, %p ping, %l loss) already replaced.</param>
/// <param name="Sort">The sort key.</param>
public readonly record struct ScoreRow(int Id, string Text, int Sort);

/// <summary>A chat menu on display.</summary>
/// <param name="Title">The menu title.</param>
/// <param name="Items">The items of its top level with the key that selects each.</param>
public sealed record ServerMenuView(string Title, IReadOnlyList<(char Key, string Text)> Items);

/// <summary>The player chat menu on display.</summary>
/// <param name="Heading">The menu's title at the top level, else the headings above joined with ':'.</param>
/// <param name="Lines">The item lines ("key: text").</param>
public sealed record PlayMenuView(string Heading, IReadOnlyList<string> Lines);

/// <summary>One item datablock for the inventory lists.</summary>
/// <param name="Index">The item index.</param>
/// <param name="Heading">The heading. Its first character is the sort key.</param>
/// <param name="Description">The description.</param>
/// <param name="Price">The price.</param>
/// <param name="Count">The count the player carries.</param>
/// <param name="Shopping">The shopping bit of the station mask.</param>
/// <param name="Buying">The buying bit of the station mask.</param>
public readonly record struct InventoryEntry(int Index, string Heading, string Description, int Price, int Count, bool Shopping, bool Buying);

/// <summary>A fixed HUD source for tests and tools: each value is a settable property.</summary>
public sealed class StaticHudSource : IHudSource
{
    /// <inheritdoc cref="IHudSource.HasPlayerState"/>
    public bool HasPlayerState { get; set; } = true;
    /// <inheritdoc/>
    public double DamageLevel { get; set; }
    /// <inheritdoc/>
    public double EnergyLevel { get; set; } = 1;
    /// <inheritdoc/>
    public double Yaw { get; set; }
    /// <inheritdoc/>
    public HudWaypoint? Waypoint { get; set; }
    /// <inheritdoc/>
    public double ClockSeconds { get; set; }
    /// <summary>The weapon rows.</summary>
    public List<WeaponRow> WeaponList { get; } = [];
    /// <inheritdoc/>
    public IReadOnlyList<WeaponRow> Weapons => WeaponList;
    /// <inheritdoc/>
    public bool SensorPing { get; set; }
    /// <inheritdoc/>
    public bool SensorLocked { get; set; }
    /// <inheritdoc/>
    public HudZoom? Zoom { get; set; }
    /// <summary>The target markers.</summary>
    public List<HudTargetMarker> Markers { get; } = [];
    /// <inheritdoc/>
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height) => Markers;
    /// <summary>The inventory entries.</summary>
    public List<InventoryEntry> Inventory { get; } = [];
    /// <inheritdoc/>
    public IReadOnlyList<InventoryEntry> InventoryItems => Inventory;
    /// <summary>The scoreboard value.</summary>
    public ScoreBoard? Board { get; set; }
    /// <inheritdoc/>
    public ScoreBoard? Scores => Board;
    /// <summary>The server menu value.</summary>
    public ServerMenuView? Menu { get; set; }
    /// <inheritdoc/>
    public ServerMenuView? ServerMenu => Menu;
    /// <summary>The player chat menu value.</summary>
    public PlayMenuView? PlayMenuValue { get; set; }
    /// <inheritdoc/>
    public PlayMenuView? PlayMenu => PlayMenuValue;
    /// <summary>The command map picture value.</summary>
    public CommandMapPicture? CommandMapValue { get; set; }
    /// <inheritdoc/>
    public CommandMapPicture? CommandMap => CommandMapValue;
}
