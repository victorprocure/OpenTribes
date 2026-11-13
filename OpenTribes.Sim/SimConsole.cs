namespace OpenTribes.Sim;

using System.Diagnostics;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using static OpenTribes.Core.Fpu;

/// <summary>The console side of the simulation: the object interface the interpreter uses (fields, datablocks, instant) and the SimConsolePlugin commands (docs/sim.md 5-8).</summary>
/// <remarks>Port of src/sim/sim_console.cpp. The datablock manager the C++ class holds is <see cref="DataBlockManager"/>. Clock arithmetic follows the current rounding mode of <see cref="Fpu"/>, as the C++ double operations do.</remarks>
public sealed class SimConsole : IObjectInterface
{
    private readonly Interpreter _in;
    private readonly ClassContext _ctx;
    private readonly StringTable _strings;
    private SimManager? _server;
    private readonly DataBlockManager _dataBlocks;

    // instant / datablock construction state
    private SimObject? _current;
    private int _skipDepth; // nested unknown classes (IN8, rejected per D11)
    private string _pendingName = "";
    private SimGroup? _instantGroup; // null: the root
    private string _dataBlockTarget = "";
    private bool _inDataBlock;

    private Func<double> _clockSource;
    private double _timerStart;               // timer at +0x1a8
    private double _frameReal;                // +0x1c8: elapsed at updateClock
    private double _clock;                    // +0x1c0: scaled clock, seconds
    private double _clockOffset, _clockScale = 1; // +0x1d8, +0x1e0
    private double _frameAverage;             // +0x1d0
    private uint _frameCount;                 // +0x1e8
    private uint _virtualMs;                  // advanceRealTime clock
    private bool _virtualClock;
    private uint _tick, _tickBase;            // DAT_006a841c, DAT_006a8420

    // glibc atoi (std::atoi): strtol (white space, sign, decimal digits, saturating at the long range), cast to int.
    internal static int CAtoi(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        var negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        long v = 0;
        var overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            var d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = (v * 10) + d;
        }
        if (overflow)
            return negative ? 0 : -1; // (int)LONG_MIN, (int)LONG_MAX
        return unchecked((int)(negative ? -v : v));
    }

    // src/sim/sim_console.cpp joinArgs.
    private static string JoinArgs(string[] a, int from)
    {
        var s = "";
        for (var i = from; i < a.Length; ++i)
        {
            var piece = (i > from ? ", " : "") + a[i];
            if (s.Length + piece.Length > 383)
                break;
            s += piece;
        }
        return s;
    }

    /// <summary>Creates a console on an interpreter.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="ctx">The class creation context.</param>
    /// <param name="strings">The string table. Defaults to the global table.</param>
    /// <param name="clockOwner">The first console in the process, when this is the server world of a hosted game. This console then takes its scaled clock and leaves the console variable alone.</param>
    /// <remarks>The stock exe has one clock for both worlds (client-app.md 2.4): FearWorld_advance (004eba40) and FearWorld_clientAdvance (004eb7ac) read the same updateClock (0050d304) value.</remarks>
    public SimConsole(Interpreter interp, ClassContext ctx, StringTable? strings = null, SimConsole? clockOwner = null)
    {
        _clockOwner = clockOwner;
        _in = interp;
        _ctx = ctx;
        _strings = strings ?? StringTable.Global();
        _dataBlocks = new DataBlockManager(_strings, l => interp.Print(1, l));
        var start = Stopwatch.StartNew();
        _clockSource = () => start.Elapsed.TotalSeconds;
        if (clockOwner == null)
        {
            _in.SetVariable("$SimGame::TimeScale", "1.0"); // initial value
            _in.BindVariable("$SimGame::TimeScale", SetTimeScale);
        }
    }

    private readonly SimConsole? _clockOwner;

    /// <summary>The world commands of a client process that hosts a game (client-app.md 1.7), FearGame_consoleCallback 0x7b-0x7e and 0x79-0x7a.</summary>
    public interface IWorldHost
    {
        /// <summary>Creates the server world.</summary>
        /// <returns>True on success.</returns>
        public bool NewServer();
        /// <summary>Focuses the server world.</summary>
        /// <returns>True on success.</returns>
        public bool FocusServer();
        /// <summary>Focuses the client world.</summary>
        public void FocusClient();
        /// <summary>Destroys the server world.</summary>
        public void DeleteServer();
    }

    /// <summary>The host of the world commands. Null for the <c>tribes</c> client that hosts nothing.</summary>
    public IWorldHost? Worlds { get; set; }

    /// <summary>Creates the server world of this console. The hosting side calls it (<c>newServer</c>, case 0x7c of FearGame_consoleCallback).</summary>
    public void CreateWorld() => NewServer();

    /// <summary>Empties the manager slot and destroys the world with its objects (MG1, cases 0x7d and 0x7e).</summary>
    public void DestroyWorld()
    {
        _server?.Destroy();
        _server = null;
    }

    /// <summary>True when the <c>tribes</c> client keeps its client world in the one manager slot this class has (<c>newClient</c> makes it). The dedicated server leaves it false and keeps the reference behaviour (docs/client-app.md 1.4 step 3).</summary>
    public bool ClientMode { get; set; }

    /// <summary>Gets the world manager.</summary>
    /// <returns>The manager, or null before <c>newServer</c>.</returns>
    public SimManager? Server() => _server;
    /// <summary>Gets the tick clock.</summary>
    /// <returns>The time in milliseconds, a multiple of 32.</returns>
    public uint TickMs() => _tick;
    /// <summary>Gets the scaled clock.</summary>
    /// <returns>The time in seconds.</returns>
    public double ClockSeconds() => _clock;
    /// <summary>Gets the datablock manager.</summary>
    /// <returns>The manager.</returns>
    public DataBlockManager DataBlocks() => _dataBlocks;

    private SimManager? Focus() => _server;
    private double Elapsed() => Sub(_clockSource(), _timerStart);


    private sealed record Fixed(string Cls, int Id, bool Set, string? Name);

    /// <summary>Creates the world manager and its fixed objects (docs/sim.md 2.2) as the manager, world and game plugin constructors do. The tick clock starts at the scaled clock of the current frame (004eb5e4, case 0x7c).</summary>
    private void NewServer()
    {
        _server = new SimManager(_strings);
        _instantGroup = null;
        _current = null;
        var m = _server;
        m.Print = line => _in.Print(1, line);
        m.SetConsoleHandler((target, text) =>
        {
            _in.Print(2, "SEVT: " + Numbers.FormatInt(target != null ? target.Id() : 0) + " => " + text);
            _in.Evaluate(text);
        });
        // Fixed objects of the manager, world and game plugin constructors (docs/sim.md 2.2).
        Fixed[] fixedObjects =
        [
            new("SimSet", 5, true, null), new("SimResource", 2, true, null),
            new("SimInputConsumerSet", 25, true, null), new("SimActionHandler", 1, false, null),
            new("SimCanvasSet", 3, true, null), new("SimSet", 38, true, null),
            new("SimSet", 36, true, null), new("SimSet", 34, true, null),
            new("SimDefaultOpenContainer", 7, false, null), new("SimTimerSet", 12, true, null), // replaced below
            new("SimCameraSet", 9, true, null), new("SimCameraMountSet", 23, true, null),
            new("SimRenderSet", 13, true, null), new("SimLightSet", 14, true, null),
            new("SimSet", 6, true, null), new("SimSet", 44, true, "NamedGuiSet"),
            new("SimSet", 45, true, null), new("SimSet", 718, true, null),
            new("SimSet", 719, true, null), new("SimSet", 725, true, null),
            new("SimSet", 727, true, null), new("PlayerManager", 713, false, null),
            new("SimPathManager", 42, false, null), new("DataBlockManager", 721, false, null),
            new("SimSet", 723, true, null), new("SensorManager", 720, false, null),
        ];
        // 004eb5e4 case 0x7c: the tick clock starts at the scaled clock of the current frame.
        _tick = (uint)(long)Mul(_clock, 1000.0) & ~31u;
        _tickBase = _tick;
        SimSet? timers = null;
        foreach (var f in fixedObjects)
        {
            SimObject obj;
            var made = f.Id == 720 ? SimClasses.CreateObject("SensorManager", _ctx) : null;
            if (f.Id == 12)
                obj = timers = new SimTimerSet(12, 0.03f); // 0050a818: interval 0.03 s
            else if (made != null)
                obj = made;
            else
                obj = f.Set ? new NamedClassSet(f.Cls, f.Id) : new NamedClassObject(f.Cls, f.Id);
            if (f.Id == 25)
                ((SimSet)obj).SetOrdered(true); // SimInputConsumerSet ctor 0050dd44 sets +0x5d
            m.AddToRoot(obj);
            if (f.Name != null)
                m.AssignName(obj, _strings.Intern(f.Name));
            if (f.Id == 720)
                timers!.AddObject(obj); // listObjects(12): the SensorManager
        }
        // listObjects(25) starts with the action handler.
        var handler = m.FindById(1);
        if (handler != null)
            m.FindById(25)?.AsSet()?.AddObject(handler);
        _ctx.ServerCreated?.Invoke(m);
    }


    /// <summary>Replaces the real clock. The default is a steady clock since construction. A virtual-time run or a test replaces it.</summary>
    /// <param name="seconds">Returns the real time in seconds.</param>
    public void SetClockSource(Func<double> seconds)
    {
        _clockSource = seconds;
        _timerStart = 0;
    }

    /// <summary>Applies a new <c>$SimGame::TimeScale</c> (0050b4c4, case 0x65): folds the elapsed time into the offset and restarts the timer. The scale has a floor of 0.0001.</summary>
    private void SetTimeScale(string value)
    {
        var now = _clockSource();
        _clockOffset = Add(_clockOffset, Mul(Sub(now, _timerStart), _clockScale));
        _timerStart = now;
        _frameReal = 0;
        _clockScale = Numbers.BorlandAtof(value);
        if (_clockScale < 0.0001)
            _clockScale = 0.0001;
    }

    /// <summary>Updates the scaled clock (frame step 2, TK1, 0050d304): elapsed time times the scale plus the offset.</summary>
    public void UpdateClock()
    {
        if (_clockOwner != null)
        {
            _frameReal = _clockOwner._frameReal;
            _clock = _clockOwner._clock;
            return;
        }
        _frameReal = Elapsed();
        _clock = Add(Mul(_frameReal, _clockScale), _clockOffset);
    }

    /// <summary>Runs the server ticks without the network (frame step 3, TK2, 004eba40): every 32 ms tick up to the scaled clock runs the events, then <see cref="SimObject.ServerTick"/> on sets 718, 719 and 727.</summary>
    /// <returns>The number of ticks.</returns>
    public int AdvanceServer()
    {
        if (_server == null)
            return 0;
        var m = _server;
        var target = (uint)(long)Mul(_clock, 1000.0) & ~31u;
        ReadOnlySpan<int> kTickSets = [718, 719, 727];
        var ticks = 0;
        while (target > _tick)
        {
            var previous = _tick;
            _tick += 32;
            ++ticks;
            if (_in.PrintLevel() >= 4)
                _in.Print(4, "TIME: " + Numbers.FormatInt((int)(previous >> 5)));
            m.AdvanceTo(Store(Mul(unchecked(_tick - _tickBase), 0.001)));
            foreach (var id in kTickSets)
            {
                var set = m.FindById(id)?.AsSet();
                if (set == null)
                    continue;
                // Index loop over the live list (TK4): additions run in the same pass, a removal moves the last member
                // into the freed slot (GS4).
                for (var i = 0; i < set.Members().Count; ++i)
                    set.Members()[i].ServerTick(_tick);
                // 004eba40: the AI manager (object 726) runs between sets 718 and 719.
                if (id == 718)
                    m.FindById(726)?.ServerTick(_tick);
            }
        }
        return ticks;
    }

    /// <summary>Ends a frame (step 6, TK9, 0050d354): updates the smoothed frame time and calls the <c>Game::EndFrame</c> script function. The two frame statistics variables refresh every 16 frames.</summary>
    public void EndFrame()
    {
        _frameAverage = Add(Mul(Sub(Elapsed(), _frameReal), (double)0.02f), Mul((double)(1.0f - 0.02f), _frameAverage));
        _frameCount = (_frameCount + 1) & 15;
        if (_frameCount == 0)
        {
            _in.SetVariable("$ConsoleWorld::FrameRate", CFormat.F(Div(1.0, _frameAverage), 2) + " fps");
            _in.SetVariable("$ConsoleWorld::msecsPerFrame", CFormat.F(Mul(1000.0, _frameAverage), 2));
        }
        _in.CallFunction("Game::EndFrame", []);
    }

    /// <summary>Advances an internal millisecond clock, then runs <see cref="UpdateClock"/> and <see cref="AdvanceServer"/>.</summary>
    /// <param name="ms">The milliseconds to advance.</param>
    /// <remarks>The clock has the GetTickCount form of 00584adc.</remarks>
    public void AdvanceRealTime(uint ms)
    {
        if (!_virtualClock)
        {
            _virtualClock = true;
            SetClockSource(() => Mul(_virtualMs, 0.001)); // GetTickCount form of 00584adc
        }
        _virtualMs += ms;
        UpdateClock();
        AdvanceServer();
    }


    /// <summary>Starts a <c>datablock</c> declaration. Following slots set its fields.</summary>
    /// <param name="className">The datablock class.</param>
    /// <param name="name">The block name.</param>
    public void BeginDataBlock(string className, string name)
    {
        _inDataBlock = true;
        _dataBlockTarget = name;
        _dataBlocks.Declare(className, name);
    }

    /// <summary>Ends the datablock declaration.</summary>
    public void EndDataBlock()
    {
        _inDataBlock = false;
        _dataBlockTarget = "";
    }


    /// <summary>Starts an <c>instant</c> object. An unknown class skips the subtree (IN8, D11, where the original crashes). A nested object under a non-group also skips the subtree (IN9, D11).</summary>
    /// <param name="className">The object class.</param>
    /// <param name="name">The object name, empty for none.</param>
    public void BeginObject(string className, string name)
    {
        if (_skipDepth != 0 || _server == null)
        {
            ++_skipDepth;
            return;
        }
        var raw = SimClasses.CreateObject(className, _ctx);
        if (raw == null)
        {
            // The original crashes on an unknown class in an instant (IN8); skip the subtree (D11).
            _in.Print(1, "instant: unknown class " + className);
            ++_skipDepth;
            return;
        }
        if (_current == null)
        {
            _current = raw;
            _pendingName = name;
            return;
        }
        var parent = _current.AsGroup();
        if (parent == null)
        {
            raw.Destroy(); // nested instant under a non-group crashes the original (IN9, D11)
            ++_skipDepth;
            return;
        }
        parent.AddObject(raw);
        parent.AssignName(raw, name.Length == 0 ? null : _strings.Intern(name));
        _current = raw;
    }

    /// <summary>Ends the instant object. A top-level object is added to the root, or to the instant group without being registered (IN6).</summary>
    public void EndObject()
    {
        if (_skipDepth != 0)
        {
            --_skipDepth;
            return;
        }
        if (_current == null)
            return;
        if (_current.Group() != null)
        {
            _current = _current.Group();
            return;
        }
        var top = _current;
        _current = null;
        var name = _pendingName.Length == 0 ? null : _strings.Intern(_pendingName);
        if (_instantGroup != null)
        {
            _instantGroup.AddObject(top); // never registered (IN6)
            _instantGroup.AssignName(top, name);
            return;
        }
        if (!_server!.AddToRoot(top))
        {
            _in.Print(1, "Failed to registerObject of type: " + top.ClassName() + " (" + (name ?? "<Unnamed>") + ")");
            top.Destroy();
            return;
        }
        _server.AssignName(top, name);
    }

    /// <summary>Sets a slot of the current instant object or datablock.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The array index text, empty for none.</param>
    public void SetSlot(string field, IReadOnlyList<string> values, string index)
    {
        if (_inDataBlock)
        {
            SetField(_dataBlockTarget, field, values, index);
            return;
        }
        if (_skipDepth != 0 || _current == null)
            return;
        var key = _strings.Intern(field + index);
        if (index.Length == 0 && _current.SetPersistField(key, values))
            return;
        if (index.Length != 0 && index.All(c => c is >= '0' and <= '9') && index.Length < 6 &&
            _current.SetPersistElement(_strings.Intern(field), CAtoi(index), values))
            return;
        _current.SetDataField(key, values.Count == 0 ? "" : values[0]);
    }


    /// <summary>Reads a field of an object or datablock (5.1, 5.2).</summary>
    /// <param name="obj">The object or block name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="index">The array index text, empty for none.</param>
    /// <param name="output">Receives the value.</param>
    /// <returns>False when neither an object nor a block is found, or the block field cannot be read.</returns>
    public bool GetField(string obj, string field, string index, out string output)
    {
        var o = _server?.Find(obj);
        if (o != null)
        {
            output = o.DataField(_strings.Intern(field + index)) ?? "";
            return true;
        }
        output = "";
        var block = _dataBlocks.Find(obj);
        return block != null && _dataBlocks.GetField(block, field, index, out output);
    }

    /// <summary>Writes a field of an object or datablock (5.1, 5.2).</summary>
    /// <param name="obj">The object or block name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The array index text, empty for none.</param>
    public void SetField(string obj, string field, IReadOnlyList<string> values, string index)
    {
        var first = values.Count == 0 ? "" : values[0];
        var o = _server?.Find(obj);
        if (o != null)
        {
            o.SetDataField(_strings.Intern(field + index), first);
            return;
        }
        if (_dataBlocks.Find(obj) is DataBlockManager.Block block)
            _dataBlocks.SetField(block, field, values, index);
    }


    // src/sim/sim_console.cpp SimConsole::listLine.
    private static string ListLine(SimObject obj)
    {
        var s = "   " + Numbers.FormatInt(obj.Id());
        if (obj.Name() != null)
            s += ",\"" + obj.Name() + "\"";
        s += ": " + obj.ClassName() + " " + (obj.AsSet() != null ? "(g)" : "");
        return s;
    }

    // src/sim/sim_console.cpp matches (registerCommands): a set member named or numbered by s.
    private static bool Matches(SimObject m, string s, StringTable strings)
    {
        if (s.Length != 0 && s[0] >= '0' && s[0] <= '9' && Numbers.BorlandAtoi(s) == m.Id())
            return true;
        var n = strings.Lookup(s);
        return n != null && ReferenceEquals(m.Name(), n);
    }

    /// <summary>Registers the SimConsolePlugin commands on the interpreter (docs/sim.md 8).</summary>
    public void RegisterCommands()
    {
        void Cmd(string name, Func<string[], string> f) => _in.AddCommand(name, (_, a) => f(a));
        const string yes = "True", no = "False";

        Cmd("newServer", _ =>
        {
            if (ClientMode)
            {
                // The `tribes` client keeps its server world in a second SimConsole (Worlds), 0x7c of FearGame_consoleCallback.
                if (Worlds == null)
                {
                    _in.Print(1, "newServer: this client does not host games; run tribes-server and join it");
                    return no;
                }
                return Worlds.NewServer() ? yes : no;
            }
            NewServer();
            return yes;
        });
        // 0050d51c case 0x7a: focus slot 1 and return True, False when the slot is empty.
        Cmd("focusServer", _ => ClientMode ? (Worlds != null && Worlds.FocusServer() ? yes : no) : _server != null ? yes : no);
        Cmd("deleteServer", _ =>
        {
            if (ClientMode)
            {
                Worlds?.DeleteServer(); // EndGame() calls it; the client world stays (client-app.md 1.7)
                return yes;
            }
            _server?.Destroy();
            _server = null;
            return yes;
        });
        // The dedicated server never creates a client world (MG4).
        Cmd("newClient", _ =>
        {
            if (ClientMode)
                NewServer();
            return yes;
        });
        Cmd("focusClient", _ =>
        {
            if (ClientMode)
                Worlds?.FocusClient();   // case 0x79: always True
            return yes;
        });
        Cmd("deleteClient", _ => yes);
        // 0050b4c4 case 0x78: purge(force) whatever the arguments (docs/resources.md 8.7).
        Cmd("purgeResources", _ =>
        {
            _ctx.Resources?.Purge();
            return yes;
        });
        // 0050b4c4 case 0x6e: one line per member of object 2, the name and the file name (+0x68). A member that is
        // not a SimVolume reads a null pointer there (a SimSet prints "(null)").
        Cmd("listVolumes", _ =>
        {
            var set = Focus()?.FindById(2)?.AsSet();
            if (set != null)
                foreach (var m in set.Members())
                {
                    var file = SimClasses.SimVolumeInfo(m, out var f, out var _) ? f : "(null)";
                    _in.Print(1, "   \"" + (m.Name() ?? "") + "\": \"" + file + "\"");
                }
            return yes;
        });

        Cmd("newObject", a =>
        {
            if (a.Length < 3)
            {
                _in.Print(1, "newObject: objectName className");
                return "0";
            }
            var focus = Focus();
            if (focus == null)
                return "0";
            var raw = SimClasses.CreateObject(a[2], _ctx);
            if (raw == null)
            {
                _in.Print(1, "newObject: persistent create failed (" + a[2] + ")");
                return "0";
            }
            if (!focus.AddToRoot(raw))
            {
                _in.Print(1, "newObject for: " + a[2] + " - failed register object");
                raw.Destroy();
                return "0";
            }
            if (a[1].Length != 0)
                focus.AssignName(raw, _strings.Intern(a[1]));
            var args = a[3..];
            if (!raw.ProcessArguments(args))
            {
                focus.DeleteObject(raw);
                _in.Print(1, "newObject for: " + a[2] + " failed arg list: " + JoinArgs(a, 3));
                return "0";
            }
            return Numbers.FormatInt(raw.Id());
        });
        Cmd("deleteObject", a =>
        {
            if (a.Length < 2)
            {
                _in.Print(1, "deleteObject: objectName [objectName] ...");
                return no;
            }
            for (var i = 1; i < a.Length; ++i)
            {
                var obj = Focus()?.Find(a[i]);
                if (obj != null && obj != Focus())
                {
                    Focus()!.DeleteObject(obj);
                    return yes;
                }
                _in.Print(1, "deleteObject: Object \"" + a[i] + "\" doesn't exist");
            }
            return no;
        });
        Cmd("isObject", a =>
        {
            if (a.Length < 2)
            {
                _in.Print(1, "isObject: objectName [objectName] ...");
                return no;
            }
            for (var i = 1; i < a.Length; ++i)
                if (Focus() == null || Focus()!.Find(a[i]) == null)
                    return no;
            return yes;
        });
        Cmd("listObjects", a =>
        {
            var obj = a.Length >= 2 ? Focus()?.Find(a[1]) : Focus();
            if (obj == null)
            {
                _in.Print(1, "listObjects: Could not find object");
                return yes;
            }
            var set = obj.AsSet();
            if (set == null)
            {
                _in.Print(1, "listObjects: Object is not a set");
                return yes;
            }
            foreach (var m in set.Members())
                _in.Print(1, ListLine(m));
            return yes;
        });
        Cmd("addToSet", a =>
        {
            if (a.Length < 3)
            {
                _in.Print(1, "addToSet: setName [objectName] ...");
                return no;
            }
            var target = Focus()?.Find(a[1]);
            if (target == null)
            {
                _in.Print(1, "addToSet: Set \"" + a[1] + "\" does not exist");
                return no;
            }
            var set = target.AsSet();
            if (set == null)
            {
                _in.Print(1, "addToSet: Object \"" + a[1] + "\" is not a set");
                return no;
            }
            var error = false;
            for (var i = 2; i < a.Length; ++i)
            {
                var obj = Focus()!.Find(a[i]);
                if (obj == null || obj == Focus())
                {
                    _in.Print(1, "addToSet: Object \"" + a[i] + "\" doesn't exist");
                    error = true;
                    continue;
                }
                set.AddObject(obj);
            }
            return error ? no : yes;
        });
        Cmd("renameObject", a =>
        {
            if (a.Length != 3)
            {
                _in.Print(1, "renameObject: objectName newObjectName");
                return no;
            }
            var obj = Focus()?.Find(a[1]);
            if (obj == null)
            {
                _in.Print(1, "renameObject: object does not exist '" + a[1] + "'.");
                return no;
            }
            var name = a[2].Length == 0 ? null : _strings.Intern(a[2]);
            obj.Group()?.AssignName(obj, name);
            return yes;
        });
        Cmd("getGroup", a =>
        {
            if (a.Length != 2)
            {
                _in.Print(1, "GetGroup( object );");
                return yes; // usage falls through to the shared True exit
            }
            var obj = Focus()?.Find(a[1]);
            if (obj == null || obj.Group() == null)
                return no;
            return Numbers.FormatInt(obj.Group() == Focus() ? Focus()!.ManagerId() : obj.Group()!.Id());
        });
        Cmd("isMember", a =>
        {
            if (a.Length != 3)
            {
                _in.Print(1, "IsMember( set/group, object );");
                return yes;
            }
            var set = Focus()?.Find(a[1])?.AsSet();
            if (set == null)
                return no;
            foreach (var m in set.Members())
                if (Matches(m, a[2], _strings))
                    return yes;
            return no;
        });
        Cmd("getNextObject", a =>
        {
            if (a.Length != 3)
            {
                _in.Print(1, "GetNextObject( set/group, object );");
                _in.Print(1, "GetNextObject( set/group, 0 );  // to retrieve first object in a set");
                return "True";
            }
            var set = Focus()?.Find(a[1])?.AsSet();
            if (set == null)
                return no;
            var m = set.Members();
            for (var i = 0; i < m.Count; ++i)
                if (Matches(m[i], a[2], _strings))
                    return i + 1 < m.Count ? Numbers.FormatInt(m[i + 1].Id()) : "0";
            return m.Count == 0 ? "0" : Numbers.FormatInt(m[0].Id());
        });
        Cmd("setObjectId", a =>
        {
            if (a.Length != 3)
            {
                _in.Print(1, "setObjectId: objectName newObjectId");
                return no;
            }
            var obj = Focus()?.Find(a[1]);
            if (obj == null || obj == Focus())
            {
                _in.Print(1, "setObjectId: object does not exist '" + a[1] + "'.");
                return no;
            }
            Focus()!.SetObjectId(obj, Numbers.BorlandAtoi(a[2]));
            return yes;
        });
        Cmd("nameToID", a =>
        {
            if (a.Length != 2)
            {
                _in.Print(1, "wrong argcount to namToID(name)");
                return "-1";
            }
            var obj = _server?.Find(a[1]);
            return obj != null && obj != _server ? Numbers.FormatInt(obj.Id()) : "-1";
        });
        Cmd("setInstantGroup", a =>
        {
            if (a.Length != 2 || Focus() == null)
                return no;
            var g = Focus()!.Find(a[1])?.AsGroup();
            if (g == null)
                return no;
            _instantGroup = g == Focus() ? null : g;
            return yes;
        });
        Cmd("getSimTime", _ => Numbers.FormatFloat(Focus() != null ? Focus()!.Time() : 0.0f));
        Cmd("resetSimTime", _ =>
        {
            // 00422994: base = tick, then the queue (TM3). Nothing is dispatched here.
            _tickBase = _tick;
            _server?.ResetTime();
            return yes;
        });
        Cmd("schedule", a =>
        {
            if (a.Length is not 3 and not 4)
            {
                _in.Print(1, "schedule(statement, deltaTime, <object>);");
                return no;
            }
            var scheduler = Focus()?.FindById(43);
            if (scheduler == null)
            {
                _in.Print(1, "schedule: scheduler is not running");
                return no;
            }
            var dest = a.Length == 3 ? scheduler : Focus()!.Find(a[3]);
            if (dest == null)
                return no;
            var parsed = Numbers.BorlandAtof(a[2]);
            var delay = 0.0 < parsed ? parsed : 0.0; // std::max(0.0, x)
            var t = Store(Add(Focus()!.Time(), delay));
            Focus()!.PostConsoleEvent(dest, t, a[1]);
            return Numbers.FormatInt(Numbers.FloatToInt(t));
        });
        Cmd("focus", a =>
        {
            if (a.Length < 2)
                return yes; // no argument: True
            return Focus()?.Find(a[1]) != null ? yes : no;
        });
        Cmd("unfocus", a =>
        {
            if (a.Length < 2)
                return yes;
            return Focus()?.Find(a[1]) != null ? yes : no;
        });
        Cmd("inspectObject", a =>
        {
            if (a.Length != 2)
            {
                _in.Print(1, "inspectObject [objectName/Id]");
                return "True";
            }
            if (Focus()?.Find(a[1]) == null && a[1] != "0")
                _in.Print(1, "object " + a[1] + " not found");
            return "True";
        });
        Cmd("getPathOf", a =>
        {
            if (a.Length != 2)
            {
                _in.Print(1, "GetPathof( filename );");
                return "True";
            }
            var e = _ctx.Resources?.Find(a[1]);
            return e != null ? e.FilePath : "";
        });
    }
}
