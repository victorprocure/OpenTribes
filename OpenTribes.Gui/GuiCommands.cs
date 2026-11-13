namespace OpenTribes.Gui;

using System.Globalization;
using OpenTribes.Script;

/// <summary>The console commands that act on the GUI, registered in the port's interpreter for the client (docs/gui.md 5, docs/client-scripts.md 2.7, 2.13, 2.14).</summary>
/// <remarks>Control commands take a name or a numeric id. The `Ded = N` list commands take a control.</remarks>
public sealed class GuiRuntime
{
    /// <summary>The script interpreter the commands are registered in.</summary>
    public readonly Interpreter Interp;
    /// <summary>The GUI host.</summary>
    public readonly GuiHost Host;
    /// <summary>The canvas the commands act on.</summary>
    public readonly GuiCanvas Canvas;
    /// <summary>The objects made by loadObject(name, file) (playGui, commandGui, ...).</summary>
    public readonly Dictionary<string, GuiControl> Named = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Called when a script asks for a render of the canvas.</summary>
    public Action? RenderRequested;

    /// <summary>Creates the runtime and registers the commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="host">The GUI host.</param>
    /// <param name="canvas">The canvas.</param>
    public GuiRuntime(Interpreter interp, GuiHost host, GuiCanvas canvas)
    {
        Interp = interp;
        Host = host;
        Canvas = canvas;
        Register();
    }

    /// <summary>The tag dictionary scripts (docs/gui.md 1.5).</summary>
    public static readonly string[] StringScripts =
    [
        "darkstar.strings.cs", "fear.strings.cs", "esf.strings.cs", "commonEditor.strings.cs", "editor.strings.cs", "help.strings.cs",
    ];

    /// <summary>Runs the tag dictionary scripts.</summary>
    /// <returns>The number of scripts that ran.</returns>
    public int LoadTagDictionary()
    {
        var n = 0;
        foreach (var s in StringScripts)
            if (Interp.ExecFile(s))
                ++n;
        return n;
    }

    /// <summary>Finds a control by name or id (docs/gui.md 5.2): a number is a control id, otherwise a name.</summary>
    /// <param name="key">The name or the id.</param>
    /// <returns>The control, or null.</returns>
    public GuiControl? FindControl(string key)
    {
        if (Named.TryGetValue(key, out GuiControl? n))
            return n;
        var numeric = uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        foreach (GuiControl root in Canvas.Stack.Concat(Named.Values))
        {
            GuiControl? c = numeric ? root.FindById(id) : root.FindByName(key);
            if (c != null)
                return c;
        }
        return null;
    }

    /// <summary>Finds an object by name, then its child by id.</summary>
    /// <param name="objectName">The object name.</param>
    /// <param name="id">The child id.</param>
    /// <returns>The control, or null.</returns>
    /// <remarks>Follows the stock lookup of the chat display `playGui` / 140113 (0040dbfc, 0053a048). A control of the same id inside another object (the chat display of `commandGui`) is not found.</remarks>
    public GuiControl? FindChildOf(string objectName, uint id)
    {
        GuiControl? obj = Named.TryGetValue(objectName, out GuiControl? n) ? n : null;
        if (obj == null)
            foreach (GuiControl root in Canvas.Stack)
                if ((obj = root.FindByName(objectName)) != null)
                    break;
        if (obj == null)
            Canvas.Retained.TryGetValue(objectName, out obj);
        return obj?.FindById(id);
    }

    private static string B(bool v) => v ? "True" : "False";
    private static bool ParseBool(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";

    private GuiCanvas? CanvasArg(string cmd, string name)
    {
        if (string.Equals(name, Canvas.Name, StringComparison.OrdinalIgnoreCase))
            return Canvas;
        Interp.Print(1, $"{cmd}: {name} is not a valid SimGui::Canvas");
        return null;
    }

    private void Cmd(string name, int argc, string usage, Func<string[], string?> body) => Interp.AddCommand(name, (inr, a) =>
                                                                                                {
                                                                                                    if (a.Length != argc)
                                                                                                    {
                                                                                                        inr.Print(1, usage);
                                                                                                        return "False";
                                                                                                    }
                                                                                                    return body(a);
                                                                                                });

    private void CtrlCmd<T>(string name, int argc, string usage, Func<T, string[], string?> body) where T : GuiControl => Cmd(name, argc, usage, a =>
                                                                                                                               {
                                                                                                                                   GuiControl? c = FindControl(a[1]);
                                                                                                                                   if (c is not T t)
                                                                                                                                   {
                                                                                                                                       Interp.Print(1, $"{name} - invalid control {a[1]}.");
                                                                                                                                       return "False";
                                                                                                                                   }
                                                                                                                                   return body(t, a);
                                                                                                                               });

    /// <summary>Finds the observer panel of the command screen in the loaded trees.</summary>
    /// <remarks>The stock exe keeps it in DAT_006a83dc, set at construction (004b4718).</remarks>
    private GuiCmdObserve? FindObserver()
    {
        static GuiCmdObserve? In(GuiControl c)
        {
            if (c is GuiCmdObserve o)
                return o;
            foreach (GuiControl k in c.Children)
                if (In(k) is GuiCmdObserve f)
                    return f;
            return null;
        }
        foreach (GuiControl root in Named.Values.Concat(Canvas.Stack))
            if (In(root) is GuiCmdObserve o)
                return o;
        return null;
    }

    /// <summary>Parses an integer like C atoi: blanks, a sign, digits.</summary>
    private static int Atoi(string s)
    {
        int i = 0, sign = 1;
        long v = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            ++i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            sign = s[i++] == '-' ? -1 : 1;
        for (; i < s.Length && s[i] is >= '0' and <= '9'; ++i)
            v = Math.Min((v * 10) + (s[i] - '0'), int.MaxValue);
        return (int)(sign * v);
    }

    private void Register()
    {
        // ---- the command screen observer [code 0043a670, 0043a6e8] ----
        // Client::toggleCMDObserver(on): "true"/"false" or a number; False without the panel or with another argument count.
        Interp.AddCommand("Client::toggleCMDObserver", (_, a) =>
        {
            if (FindObserver() is not GuiCmdObserve o || a.Length != 2)
                return "False";
            o.Toggle(a[1].Equals("true", StringComparison.OrdinalIgnoreCase) || (!a[1].Equals("false", StringComparison.OrdinalIgnoreCase) && Atoi(a[1]) != 0));
            return "True";
        });
        // Client::cmdObservePlayer(id): True when the panel took a new player to follow.
        Interp.AddCommand("Client::cmdObservePlayer", (_, a) =>
            FindObserver() is GuiCmdObserve o && a.Length == 2 && o.Observe(Atoi(a[1])) ? "True" : "False");
        // ---- 5.1 canvas and content ----
        Cmd("GuiLoadContentCtrl", 3, "GuiLoadContentCtrl: simCanvas filename", a => CanvasArg(a[0], a[1]) is GuiCanvas c ? (c.LoadContent(a[2]) ? "" : "False") : "False");
        Cmd("GuiNewContentCtrl", 3, "GuiNewContentCtrl: simCanvas classname", a =>
        {
            if (CanvasArg(a[0], a[1]) is not GuiCanvas c)
                return "False";
            GuiClassRow? row = GuiClassTable.ByName(a[2]);
            if (row == null)
            {
                Interp.Print(1, $"{a[0]}: {a[2]} is not a valid class name");
                return "False";
            }
            if (!GuiClassTable.IsControl(row))
            {
                Interp.Print(1, $"{a[0]}: {a[2]} is not a control class");
                return "False";
            }
            c.SetContent(GuiFactory.Create(row));
            return "";
        });
        Cmd("GuiPushDialog", 3, "GuiPushDialog: simCanvas filename", a => CanvasArg(a[0], a[1]) is GuiCanvas c ? (c.PushDialog(a[2]) ? "" : "False") : "False");
        Cmd("GuiPopDialog", 3, "GuiPopDialog: simCanvas message", a =>
        {
            if (CanvasArg(a[0], a[1]) is GuiCanvas c)
                c.PopDialog(int.TryParse(a[2], out var m) ? m : 0);
            return "";
        });
        Cmd("GuiSendRootMessage", 3, "GuiSendRootMessage: simCanvas tag", a =>
        {
            if (CanvasArg(a[0], a[1]) is GuiCanvas c)
                c.Root.Message(null, int.TryParse(a[2], out var m) ? m : 0);
            return "False";
        });
        Cmd("GuiSaveContentCtrl", 3, "GuiSaveContentCtrl: simCanvas filename", a =>
        {
            if (CanvasArg(a[0], a[1]) is not GuiCanvas c || c.Content == null)
                return "False";
            return StoreControl(c.Content, a[2]) ? "" : "False";
        });
        Cmd("renderCanvas", 2, "renderCanvas: could not find specified canvas", a =>
        {
            if (CanvasArg(a[0], a[1]) is not null)
                RenderRequested?.Invoke();
            return "";
        });
        Cmd("setCursor", 3, "setCursor: simCanvas bitmapName", a =>
        {
            if (CanvasArg(a[0], a[1]) is GuiCanvas c)
                c.SetCursorBitmap(a[2]);
            return "False";
        });
        Cmd("isCursorOn", 2, "isCursorOn: simCanvas", a => CanvasArg(a[0], a[1]) is GuiCanvas c ? B(c.CursorOn) : "False");
        Cmd("cursorOn", 2, "cursorOn: simCanvas", a => { if (CanvasArg(a[0], a[1]) is GuiCanvas c) c.CursorOn = true; return "False"; });
        Cmd("cursorOff", 2, "cursorOff: simCanvas", a =>
        {
            if (CanvasArg(a[0], a[1]) is GuiCanvas c)
            {
                c.CursorOn = false;
                c.Lock = null;
            }
            return "False";
        });
        foreach (var n in new[] { "windowsMouseEnable", "windowsMouseDisable", "windowsKeyboardEnable", "windowsKeyboardDisable" })
            Cmd(n, 2, n + ": simCanvas", a => { CanvasArg(a[0], a[1]); return ""; });

        Interp.AddCommand("loadObject", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "loadObject: objectName fileName");
                return "0";
            }
            GuiControl? c = Canvas.LoadFile(a[2]);
            if (c == null)
                return "0";
            c.Name = a[1];
            c.SetCanvas(Canvas);
            Named[a[1]] = c;
            return "1";
        });
        Interp.AddCommand("storeObject", (inr, a) =>
        {
            if (a.Length is not (3 or 4))
            {
                inr.Print(1, "storeObject: objectName fileName [userField]");
                return "False";
            }
            return FindControl(a[1]) is GuiControl c && StoreControl(c, a[2]) ? "True" : "False";
        });

        // ---- 5.2 control access ----
        // A missing control gives the empty string and prints nothing: the stock start-up runs OptionsVideo::apply before
        // any options screen exists and the install's console.log has no line for it (C8, docs/client-scripts.md 6.1 step 13).
        Cmd("Control::getValue", 2, "Control::getValue(control);", a => FindControl(a[1])?.GetValue() ?? "");
        Cmd("Control::setValue", 3, "Control::setValue(control, value);", a => { FindControl(a[1])?.SetValue(a[2]); return ""; });
        Cmd("Control::getActive", 2, "Control::getActive(control);", a => FindControl(a[1]) is GuiControl c ? B(c.Active) : "False");
        Cmd("Control::setActive", 3, "Control::setActive(control, bool);", a =>
        {
            if (FindControl(a[1]) is GuiControl { IsActiveCtrl: true } c)
            {
                c.Active = ParseBool(a[2]);
                return "True";
            }
            return "False";
        });
        Cmd("Control::getVisible", 2, "Control::getVisible(control);", a => FindControl(a[1]) is GuiControl c ? B(c.Visible) : "False");
        Cmd("Control::setVisible", 3, "Control::setVisible(control, bool);", a =>
        {
            if (FindControl(a[1]) is not GuiControl c)
                return "False";
            c.SetVisible(ParseBool(a[2]));
            Canvas.RebuildTabList();
            return "True";
        });
        Cmd("Control::getPosition", 2, "Control::getPosition(control);", a => FindControl(a[1]) is GuiControl c ? $"{c.X} {c.Y}" : "False");
        Cmd("Control::getExtent", 2, "Control::getExtent(control);", a => FindControl(a[1]) is GuiControl c ? $"{c.W} {c.H}" : "False");
        Cmd("Control::setPosition", 4, "Control::setPosition(control, x, y);", a =>
        {
            if (FindControl(a[1]) is GuiControl c && int.TryParse(a[2], out var x) && int.TryParse(a[3], out var y))
                c.Resize(x, y, c.W, c.H);
            return "";
        });
        Cmd("Control::setExtent", 4, "Control::setExtent(control, x, y);", a =>
        {
            if (FindControl(a[1]) is GuiControl c && int.TryParse(a[2], out var w) && int.TryParse(a[3], out var h))
                c.Resize(c.X, c.Y, w, h);
            return "";
        });
        Cmd("Control::performClick", 2, "Control::performClick(control);", a =>
        {
            if (FindControl(a[1]) is GuiActive c)
                c.DoAction();
            return "";
        });
        Cmd("Control::setText", 3, "Control::setText(control, text);", a => { FindControl(a[1])?.SetText(a[2]); return ""; });
        Cmd("Control::getText", 2, "Control::getText(control);", a => FindControl(a[1])?.GetText() ?? Missing(a));
        foreach (var n in new[] { "TextList::Clear", "TextList::clear" })
            CtrlCmd<GuiList>(n, 2, "TextList::Clear(list);", (l, a) => { l.Clear(); return ""; });
        foreach (var n in new[] { "TextList::AddLine", "TextList::addLine" })
            CtrlCmd<GuiList>(n, 3, "TextList::AddLine(list, text);", (l, a) => { l.AddEntry(a[2], l.Entries.Count); return ""; });
        CtrlCmd<GuiList>("FGTextList::sort", 2, "FGTextList::sort(control);", (l, a) => { l.SortText(); return ""; });

        // ---- 2.14 and 5.3 list, combo, slider commands ----
        RegisterList<GuiList>("FGArray", l => l.Entries, l => l.Selected, (l, i) => l.Select(i, false), (l, t, id) => l.AddEntry(t, id), l => l.Clear(), (l, id) => l.DeleteById(id), false);
        RegisterList<GuiCombo>("FGCombo", l => l.Entries, l => l.Selected, (l, i) => l.SetSelectedIndex(i), (l, t, id) => l.Entries.Add((t, id)),
            l => { l.Entries.Clear(); l.SetSelectedIndex(-1); }, (l, id) => { var i = l.IndexOfId(id); if (i >= 0) { l.Entries.RemoveAt(i); if (l.Selected == i) l.SetSelectedIndex(-1); else if (l.Selected > i) --l.Selected; } }, true);
        CtrlCmd<GuiCombo>("FGCombo::selectPrev", 2, "FGCombo::selectPrev(combo);", (c, a) => { c.SetSelectedIndex(Math.Max(0, c.Selected - 1), true); return ""; });
        CtrlCmd<GuiCombo>("FGCombo::selectNext", 2, "FGCombo::selectNext(combo);", (c, a) => { c.SetSelectedIndex(Math.Min(c.Entries.Count - 1, c.Selected + 1), true); return ""; });
        CtrlCmd<GuiSlider>("FGSlider::setDiscretePositions", 3, "FGSlider::setDiscretePositions(slider, count);", (s, a) => { s.SetDiscrete(int.TryParse(a[2], out var n) ? n : 0); return ""; });
        CtrlCmd<GuiControl>("FGSkin::set", 4, "FGSkin::set(control, skin, gender);", (c, a) => { c.F["skin"] = a[2]; c.F["gender"] = a[3]; return ""; });
        CtrlCmd<GuiControl>("FGSkin::cycleArmor", 2, "FGSkin::cycleArmor(control);", (c, a) => { c.F["armor"] = (U32(c, "armor") + 1) % 3; return ""; });
        CtrlCmd<GuiControl>("Server::ResortList", 2, "Server::ResortList(control);", (c, a) => { (c as GuiList)?.SortText(); return ""; });
        // The inventory lists: the visible set is the item indices of the rows, the favorites the indices that get a star [code 0048a700].
        CtrlCmd<GuiControl>("CmdInventory::getVisibleSet", 2, "CmdInventory::getVisibleSet(control);", (c, a) => (c as GuiInventoryList)?.VisibleSet() ?? "");
        CtrlCmd<GuiControl>("CmdInventory::setFavorites", 3, "CmdInventory::setFavorites(control, favorites);", (c, a) =>
        {
            Host.Favorites.Clear();
            foreach (var w in a[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    Host.Favorites.Add(idx);
            return "";
        });
        CtrlCmd<GuiShapeView>("ShapeView::setItem", 3, "ShapeView::setItem(control, item);", (c, a) => { c.F["item"] = int.TryParse(a[2], out var item) ? (uint)item : 0; return ""; });
        Cmd("FGMasterList::addEntry", 5, "FGMasterList::addEntry(control, name, address, id);", a =>
        {
            if (FindControl(a[1]) is GuiMasterList m && int.TryParse(a[4], out var id))
                m.AddMaster(a[2], a[3], id);
            else if (FindControl(a[1]) is GuiList l && int.TryParse(a[4], out var lid))
                l.AddEntry(a[2] + " " + a[3], lid);
            return "";
        });
        CtrlCmd<GuiList>("FGMasterList::deleteEntry", 3, "FGMasterList::deleteEntry(control, id);", (l, a) => { if (int.TryParse(a[2], out var id)) l.DeleteById(id); return ""; });
        CtrlCmd<GuiList>("FGMasterList::clear", 2, "FGMasterList::clear(control);", (l, a) => { l.Clear(); return ""; });
        CtrlCmd<GuiList>("FGMasterList::setSelected", 3, "FGMasterList::setSelected(control, id);", (l, a) => { if (int.TryParse(a[2], out var id)) l.Select(l.FindId(id), false); return ""; });
        CtrlCmd<GuiList>("FGMasterList::getSelected", 2, "FGMasterList::getSelected(control);", (l, a) => l.Selected >= 0 ? l.Entries[l.Selected].Id.ToString(CultureInfo.InvariantCulture) : "-1");
        CtrlCmd<GuiList>("Popup::addLine", 4, "Popup::addLine(control, string, id);", (l, a) => { l.AddEntry(a[2], int.TryParse(a[3], out var id) ? id : 0); return ""; });
        CtrlCmd<GuiList>("Popup::clear", 2, "Popup::clear(control);", (l, a) => { l.Clear(); return ""; });
        CtrlCmd<GuiList>("Popup::setSelected", 3, "Popup::setSelected(control, id);", (l, a) => { if (int.TryParse(a[2], out var id)) l.Select(l.FindId(id), false); return ""; });
        CtrlCmd<GuiList>("Popup::getSelected", 2, "Popup::getSelected(control);", (l, a) => l.Selected >= 0 ? l.Entries[l.Selected].Id.ToString(CultureInfo.InvariantCulture) : "-1");
        CtrlCmd<GuiControl>("PopupButton::makeCurrent", 2, "PopupButton::makeCurrent(control);", (c, a) => "");

        // ---- 4: commands that feed the HUD ----
        Interp.AddCommand("setHudTimer", (inr, a) =>
        {
            if (a.Length != 2 || !double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                inr.Print(1, "setHudTimer(seconds);");
                return "False";
            }
            HudTimer = s;
            HudTimerSetAt = Host.TickMs;
            return "True";
        });
        Interp.AddCommand("Client::centerPrint", (inr, a) =>
        {
            if (a.Length != 3 || !int.TryParse(a[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                inr.Print(1, "Client::centerPrint(text, type);");
                return "False";
            }
            Host.CenterPrint(a[^2], t);
            return "True";
        });
    }

    /// <summary>The HUD timer value set by setHudTimer, in seconds.</summary>
    public double HudTimer;
    /// <summary>The host time in milliseconds when the timer was set.</summary>
    public long HudTimerSetAt;
    /// <summary>Gets the HUD timer: the value set, advanced with the host clock.</summary>
    public double ClockSeconds => HudTimer + ((Host.TickMs - HudTimerSetAt) / 1000.0);

    private static uint U32(GuiControl c, string k) => c.U(k);

    private string Missing(string[] a)
    {
        Interp.Print(1, $"{a[0]} - invalid control {a[1]}.");
        return "";
    }

    private void RegisterList<T>(string prefix, Func<T, List<(string Text, int Id)>> entries, Func<T, int> selected, Action<T, int> select,
        Action<T, string, int> add, Action<T> clear, Action<T, int> delete, bool findReturnsId) where T : GuiControl
    {
        CtrlCmd<T>(prefix + "::addEntry", 4, prefix + "::addEntry(control, string, id);", (l, a) => { add(l, a[2], int.TryParse(a[3], out var id) ? id : 0); return ""; });
        CtrlCmd<T>(prefix + "::deleteEntry", 3, prefix + "::deleteEntry(control, id);", (l, a) => { if (int.TryParse(a[2], out var id)) delete(l, id); return ""; });
        CtrlCmd<T>(prefix + "::findEntry", 3, prefix + "::findEntry(control, string);", (l, a) =>
        {
            var i = entries(l).FindIndex(e => string.Equals(e.Text, a[2], StringComparison.OrdinalIgnoreCase));
            if (i < 0 && l is GuiCombo combo)
                i = combo.FindEntryByName(a[2]);
            if (i < 0)
                return "-1";
            return (findReturnsId ? entries(l)[i].Id : i).ToString(CultureInfo.InvariantCulture);
        });
        CtrlCmd<T>(prefix + "::clear", 2, prefix + "::clear(control);", (l, a) => { clear(l); return ""; });
        CtrlCmd<T>(prefix + "::setSelected", 3, prefix + "::setSelected(control, id);", (l, a) =>
        {
            if (int.TryParse(a[2], out var id))
                select(l, entries(l).FindIndex(e => e.Id == id));
            return "";
        });
        CtrlCmd<T>(prefix + "::getSelected", 2, prefix + "::getSelected(control);", (l, a) =>
        {
            var s = selected(l);
            return s >= 0 ? entries(l)[s].Id.ToString(CultureInfo.InvariantCulture) : "-1";
        });
        CtrlCmd<T>(prefix + "::getSelectedText", 2, prefix + "::getSelectedText(control);", (l, a) =>
        {
            var s = selected(l);
            return s >= 0 ? entries(l)[s].Text : "";
        });
    }

    /// <summary>Writes a control tree to a file (storeObject). It uses the same layout as the loader (D44), so the HUD layout round-trips.</summary>
    /// <param name="c">The root control.</param>
    /// <param name="file">The file name.</param>
    /// <returns>True when the file was written.</returns>
    public bool StoreControl(GuiControl c, string file)
    {
        try
        {
            var path = file.Replace('\\', '/');
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir != null)
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, GuiWriter.Write(c));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Interp.Print(1, "storeObject: Error writing file " + file);
            return false;
        }
    }
}

/// <summary>The HUD data source of the live client: it forwards to the source with the control object state the stream decoded, and answers the clock itself (docs/gui.md 4.1, client-net.md 4.5).</summary>
public sealed class ClockedHudSource : IHudSource
{
    private readonly IHudSource mInner;
    private readonly Func<double> mClock;
    /// <summary>Creates a source that forwards to another one.</summary>
    /// <param name="inner">The source that holds the state.</param>
    /// <param name="clock">The clock in seconds.</param>
    public ClockedHudSource(IHudSource inner, Func<double> clock) { mInner = inner; mClock = clock; }
    /// <inheritdoc/>
    public bool HasPlayerState => mInner.HasPlayerState;
    /// <inheritdoc/>
    public bool InPlayerView => mInner.InPlayerView;
    /// <inheritdoc/>
    public bool SensorHudShown => mInner.SensorHudShown;
    /// <inheritdoc/>
    public bool ControlIsPlayer => mInner.ControlIsPlayer;
    /// <inheritdoc/>
    public bool FirstPersonView => mInner.FirstPersonView;
    /// <inheritdoc/>
    public HudZoom? Zoom => mInner.Zoom;
    /// <inheritdoc/>
    public double DamageLevel => mInner.DamageLevel;
    /// <inheritdoc/>
    public double EnergyLevel => mInner.EnergyLevel;
    /// <inheritdoc/>
    public double Yaw => mInner.Yaw;
    /// <inheritdoc/>
    public HudWaypoint? Waypoint => mInner.Waypoint;
    /// <inheritdoc/>
    public double ClockSeconds => mClock();
    /// <inheritdoc/>
    public IReadOnlyList<WeaponRow> Weapons => mInner.Weapons;
    /// <inheritdoc/>
    public bool SensorPing => mInner.SensorPing;
    /// <inheritdoc/>
    public bool SensorLocked => mInner.SensorLocked;
    /// <inheritdoc/>
    public HudTargetResult? CrosshairTarget(int width, int height) => mInner.CrosshairTarget(width, height);
    /// <inheritdoc/>
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height) => mInner.TargetMarkers(width, height);
}
