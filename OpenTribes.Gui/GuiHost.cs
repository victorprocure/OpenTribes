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
    // Called when a PaletteCtrl wakes with the palette file name.
    public Action<string>? OnPalette;

    private readonly Dictionary<string, PftFont?> mFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmpBitmap?> mBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PbmaArray?> mArrays = new(StringComparer.OrdinalIgnoreCase);

    public GuiHost(Interpreter interp, ResourceManager? res = null)
    {
        Interp = interp;
        Res = res;
    }

    public long TickMs => Now();

    // Chat lines (text, font tag) and Client::centerPrint messages (text, expiry tick) the HUD classes draw.
    public readonly List<(string Text, uint FontTag)> Chat = new();
    public readonly List<(string Text, long Until)> CenterPrints = new();
    public void AddChat(string text, uint fontTag = 0) => Chat.Add((text, fontTag));
    public void CenterPrint(string text, double seconds) => CenterPrints.Add((text, TickMs + (long)(seconds * 1000)));

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

    // The font of `<fN>` (GuiMarkup): the tag of esf.strings.cs by name, then its file. N outside 0..6 is the control's own font.
    public PftFont? MarkupFont(int n)
    {
        string name = GuiMarkup.FontTagName(n);
        if (name.Length == 0)
            return null;
        int id = Interp.TagId(name);
        return id > 0 ? FontByTag((uint)id) : null;
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
    // Weapon HUD: items the player has, the mounted one and its ammo (-1 for unlimited).
    IReadOnlyList<(string Name, bool Mounted)> Weapons { get; }
    int Ammo { get; }
    bool SensorPing { get; }
    bool SensorLocked { get; }
}

public sealed class StaticHudSource : IHudSource
{
    public bool HasPlayerState { get; set; } = true;
    public double DamageLevel { get; set; }
    public double EnergyLevel { get; set; } = 1;
    public double Yaw { get; set; }
    public double ClockSeconds { get; set; }
    public List<(string Name, bool Mounted)> WeaponList { get; } = new();
    public IReadOnlyList<(string Name, bool Mounted)> Weapons => WeaponList;
    public int Ammo { get; set; } = -1;
    public bool SensorPing { get; set; }
    public bool SensorLocked { get; set; }
}
