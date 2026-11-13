using System.Diagnostics;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Sim;

/// <summary>The console side of the simulation: the object interface the interpreter uses (fields, datablocks, instant) and the SimConsolePlugin commands (docs/sim.md 5-8).</summary>
/// <remarks>Port of src/sim/sim_console.cpp. The datablock manager the C++ class holds is <see cref="DataBlockManager"/>. Clock arithmetic follows the current rounding mode of <see cref="Fpu"/>, as the C++ double operations do.</remarks>
public sealed class SimConsole : IObjectInterface
{
    private readonly Interpreter mIn;
    private readonly ClassContext mCtx;
    private readonly StringTable mStrings;
    private SimManager? mServer;
    private readonly DataBlockManager mDataBlocks;

    // instant / datablock construction state
    private SimObject? mCurrent;
    private int mSkipDepth; // nested unknown classes (IN8, rejected per D11)
    private string mPendingName = "";
    private SimGroup? mInstantGroup; // null: the root
    private string mDataBlockTarget = "";
    private bool mInDataBlock;

    private Func<double> mClockSource;
    private double mTimerStart;               // timer at +0x1a8
    private double mFrameReal;                // +0x1c8: elapsed at updateClock
    private double mClock;                    // +0x1c0: scaled clock, seconds
    private double mClockOffset, mClockScale = 1; // +0x1d8, +0x1e0
    private double mFrameAverage;             // +0x1d0
    private uint mFrameCount;                 // +0x1e8
    private uint mVirtualMs;                  // advanceRealTime clock
    private bool mVirtualClock;
    private uint mTick, mTickBase;            // DAT_006a841c, DAT_006a8420

    // glibc atoi (std::atoi): strtol (white space, sign, decimal digits, saturating at the long range), cast to int.
    internal static int CAtoi(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        long v = 0;
        bool overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            int d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = v * 10 + d;
        }
        if (overflow)
            return negative ? 0 : -1; // (int)LONG_MIN, (int)LONG_MAX
        return unchecked((int)(negative ? -v : v));
    }

    // src/sim/sim_console.cpp joinArgs.
    private static string JoinArgs(string[] a, int from)
    {
        string s = "";
        for (int i = from; i < a.Length; ++i)
        {
            string piece = (i > from ? ", " : "") + a[i];
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
        mClockOwner = clockOwner;
        mIn = interp;
        mCtx = ctx;
        mStrings = strings ?? StringTable.Global();
        mDataBlocks = new DataBlockManager(mStrings, l => interp.Print(1, l));
        Stopwatch start = Stopwatch.StartNew();
        mClockSource = () => start.Elapsed.TotalSeconds;
        if (clockOwner == null)
        {
            mIn.SetVariable("$SimGame::TimeScale", "1.0"); // initial value
            mIn.BindVariable("$SimGame::TimeScale", SetTimeScale);
        }
    }

    private readonly SimConsole? mClockOwner;

    /// <summary>The world commands of a client process that hosts a game (client-app.md 1.7), FearGame_consoleCallback 0x7b-0x7e and 0x79-0x7a.</summary>
    public interface IWorldHost
    {
        /// <summary>Creates the server world.</summary>
        /// <returns>True on success.</returns>
        bool NewServer();
        /// <summary>Focuses the server world.</summary>
        /// <returns>True on success.</returns>
        bool FocusServer();
        /// <summary>Focuses the client world.</summary>
        void FocusClient();
        /// <summary>Destroys the server world.</summary>
        void DeleteServer();
    }

    /// <summary>The host of the world commands. Null for the <c>tribes</c> client that hosts nothing.</summary>
    public IWorldHost? Worlds;

    /// <summary>Creates the server world of this console. The hosting side calls it (<c>newServer</c>, case 0x7c of FearGame_consoleCallback).</summary>
    public void CreateWorld() => NewServer();

    /// <summary>Empties the manager slot and destroys the world with its objects (MG1, cases 0x7d and 0x7e).</summary>
    public void DestroyWorld()
    {
        mServer?.Destroy();
        mServer = null;
    }

    /// <summary>True when the <c>tribes</c> client keeps its client world in the one manager slot this class has (<c>newClient</c> makes it). The dedicated server leaves it false and keeps the reference behaviour (docs/client-app.md 1.4 step 3).</summary>
    public bool ClientMode;

    /// <summary>Gets the world manager.</summary>
    /// <returns>The manager, or null before <c>newServer</c>.</returns>
    public SimManager? Server() => mServer;
    /// <summary>Gets the tick clock.</summary>
    /// <returns>The time in milliseconds, a multiple of 32.</returns>
    public uint TickMs() => mTick;
    /// <summary>Gets the scaled clock.</summary>
    /// <returns>The time in seconds.</returns>
    public double ClockSeconds() => mClock;
    /// <summary>Gets the datablock manager.</summary>
    /// <returns>The manager.</returns>
    public DataBlockManager DataBlocks() => mDataBlocks;

    private SimManager? Focus() => mServer;
    private double Elapsed() => Sub(mClockSource(), mTimerStart);


    private sealed record Fixed(string Cls, int Id, bool Set, string? Name);

    /// <summary>Creates the world manager and its fixed objects (docs/sim.md 2.2) as the manager, world and game plugin constructors do. The tick clock starts at the scaled clock of the current frame (004eb5e4, case 0x7c).</summary>
    private void NewServer()
    {
        mServer = new SimManager(mStrings);
        mInstantGroup = null;
        mCurrent = null;
        SimManager m = mServer;
        m.Print = line => mIn.Print(1, line);
        m.SetConsoleHandler((target, text) =>
        {
            mIn.Print(2, "SEVT: " + Numbers.FormatInt(target != null ? target.Id() : 0) + " => " + text);
            mIn.Evaluate(text);
        });
        // Fixed objects of the manager, world and game plugin constructors (docs/sim.md 2.2).
        Fixed[] fixedObjects =
        {
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
        };
        // 004eb5e4 case 0x7c: the tick clock starts at the scaled clock of the current frame.
        mTick = (uint)(long)Mul(mClock, 1000.0) & ~31u;
        mTickBase = mTick;
        SimSet? timers = null;
        foreach (Fixed f in fixedObjects)
        {
            SimObject obj;
            SimObject? made = f.Id == 720 ? SimClasses.CreateObject("SensorManager", mCtx) : null;
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
                m.AssignName(obj, mStrings.Intern(f.Name));
            if (f.Id == 720)
                timers!.AddObject(obj); // listObjects(12): the SensorManager
        }
        // listObjects(25) starts with the action handler.
        SimObject? handler = m.FindById(1);
        if (handler != null)
            m.FindById(25)?.AsSet()?.AddObject(handler);
        mCtx.ServerCreated?.Invoke(m);
    }


    /// <summary>Replaces the real clock. The default is a steady clock since construction. A virtual-time run or a test replaces it.</summary>
    /// <param name="seconds">Returns the real time in seconds.</param>
    public void SetClockSource(Func<double> seconds)
    {
        mClockSource = seconds;
        mTimerStart = 0;
    }

    /// <summary>Applies a new <c>$SimGame::TimeScale</c> (0050b4c4, case 0x65): folds the elapsed time into the offset and restarts the timer. The scale has a floor of 0.0001.</summary>
    private void SetTimeScale(string value)
    {
        double now = mClockSource();
        mClockOffset = Add(mClockOffset, Mul(Sub(now, mTimerStart), mClockScale));
        mTimerStart = now;
        mFrameReal = 0;
        mClockScale = Numbers.BorlandAtof(value);
        if (mClockScale < 0.0001)
            mClockScale = 0.0001;
    }

    /// <summary>Updates the scaled clock (frame step 2, TK1, 0050d304): elapsed time times the scale plus the offset.</summary>
    public void UpdateClock()
    {
        if (mClockOwner != null)
        {
            mFrameReal = mClockOwner.mFrameReal;
            mClock = mClockOwner.mClock;
            return;
        }
        mFrameReal = Elapsed();
        mClock = Add(Mul(mFrameReal, mClockScale), mClockOffset);
    }

    /// <summary>Runs the server ticks without the network (frame step 3, TK2, 004eba40): every 32 ms tick up to the scaled clock runs the events, then <see cref="SimObject.ServerTick"/> on sets 718, 719 and 727.</summary>
    /// <returns>The number of ticks.</returns>
    public int AdvanceServer()
    {
        if (mServer == null)
            return 0;
        SimManager m = mServer;
        uint target = (uint)(long)Mul(mClock, 1000.0) & ~31u;
        ReadOnlySpan<int> kTickSets = stackalloc int[3] { 718, 719, 727 };
        int ticks = 0;
        while (target > mTick)
        {
            uint previous = mTick;
            mTick += 32;
            ++ticks;
            if (mIn.PrintLevel() >= 4)
                mIn.Print(4, "TIME: " + Numbers.FormatInt((int)(previous >> 5)));
            m.AdvanceTo(Store(Mul((double)unchecked(mTick - mTickBase), 0.001)));
            foreach (int id in kTickSets)
            {
                SimSet? set = m.FindById(id)?.AsSet();
                if (set == null)
                    continue;
                // Index loop over the live list (TK4): additions run in the same pass, a removal moves the last member
                // into the freed slot (GS4).
                for (int i = 0; i < set.Members().Count; ++i)
                    set.Members()[i].ServerTick(mTick);
                // 004eba40: the AI manager (object 726) runs between sets 718 and 719.
                if (id == 718)
                    m.FindById(726)?.ServerTick(mTick);
            }
        }
        return ticks;
    }

    /// <summary>Ends a frame (step 6, TK9, 0050d354): updates the smoothed frame time and calls the <c>Game::EndFrame</c> script function. The two frame statistics variables refresh every 16 frames.</summary>
    public void EndFrame()
    {
        mFrameAverage = Add(Mul(Sub(Elapsed(), mFrameReal), (double)0.02f), Mul((double)(1.0f - 0.02f), mFrameAverage));
        mFrameCount = (mFrameCount + 1) & 15;
        if (mFrameCount == 0)
        {
            mIn.SetVariable("$ConsoleWorld::FrameRate", CFormat.F(Div(1.0, mFrameAverage), 2) + " fps");
            mIn.SetVariable("$ConsoleWorld::msecsPerFrame", CFormat.F(Mul(1000.0, mFrameAverage), 2));
        }
        mIn.CallFunction("Game::EndFrame", Array.Empty<string>());
    }

    /// <summary>Advances an internal millisecond clock, then runs <see cref="UpdateClock"/> and <see cref="AdvanceServer"/>.</summary>
    /// <param name="ms">The milliseconds to advance.</param>
    /// <remarks>The clock has the GetTickCount form of 00584adc.</remarks>
    public void AdvanceRealTime(uint ms)
    {
        if (!mVirtualClock)
        {
            mVirtualClock = true;
            SetClockSource(() => Mul(mVirtualMs, 0.001)); // GetTickCount form of 00584adc
        }
        mVirtualMs += ms;
        UpdateClock();
        AdvanceServer();
    }


    /// <summary>Starts a <c>datablock</c> declaration. Following slots set its fields.</summary>
    /// <param name="className">The datablock class.</param>
    /// <param name="name">The block name.</param>
    public void BeginDataBlock(string className, string name)
    {
        mInDataBlock = true;
        mDataBlockTarget = name;
        mDataBlocks.Declare(className, name);
    }

    /// <summary>Ends the datablock declaration.</summary>
    public void EndDataBlock()
    {
        mInDataBlock = false;
        mDataBlockTarget = "";
    }


    /// <summary>Starts an <c>instant</c> object. An unknown class skips the subtree (IN8, D11, where the original crashes). A nested object under a non-group also skips the subtree (IN9, D11).</summary>
    /// <param name="className">The object class.</param>
    /// <param name="name">The object name, empty for none.</param>
    public void BeginObject(string className, string name)
    {
        if (mSkipDepth != 0 || mServer == null)
        {
            ++mSkipDepth;
            return;
        }
        SimObject? raw = SimClasses.CreateObject(className, mCtx);
        if (raw == null)
        {
            // The original crashes on an unknown class in an instant (IN8); skip the subtree (D11).
            mIn.Print(1, "instant: unknown class " + className);
            ++mSkipDepth;
            return;
        }
        if (mCurrent == null)
        {
            mCurrent = raw;
            mPendingName = name;
            return;
        }
        SimGroup? parent = mCurrent.AsGroup();
        if (parent == null)
        {
            raw.Destroy(); // nested instant under a non-group crashes the original (IN9, D11)
            ++mSkipDepth;
            return;
        }
        parent.AddObject(raw);
        parent.AssignName(raw, name.Length == 0 ? null : mStrings.Intern(name));
        mCurrent = raw;
    }

    /// <summary>Ends the instant object. A top-level object is added to the root, or to the instant group without being registered (IN6).</summary>
    public void EndObject()
    {
        if (mSkipDepth != 0)
        {
            --mSkipDepth;
            return;
        }
        if (mCurrent == null)
            return;
        if (mCurrent.Group() != null)
        {
            mCurrent = mCurrent.Group();
            return;
        }
        SimObject top = mCurrent;
        mCurrent = null;
        string? name = mPendingName.Length == 0 ? null : mStrings.Intern(mPendingName);
        if (mInstantGroup != null)
        {
            mInstantGroup.AddObject(top); // never registered (IN6)
            mInstantGroup.AssignName(top, name);
            return;
        }
        if (!mServer!.AddToRoot(top))
        {
            mIn.Print(1, "Failed to registerObject of type: " + top.ClassName() + " (" + (name ?? "<Unnamed>") + ")");
            top.Destroy();
            return;
        }
        mServer.AssignName(top, name);
    }

    /// <summary>Sets a slot of the current instant object or datablock.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The array index text, empty for none.</param>
    public void SetSlot(string field, IReadOnlyList<string> values, string index)
    {
        if (mInDataBlock)
        {
            SetField(mDataBlockTarget, field, values, index);
            return;
        }
        if (mSkipDepth != 0 || mCurrent == null)
            return;
        string key = mStrings.Intern(field + index);
        if (index.Length == 0 && mCurrent.SetPersistField(key, values))
            return;
        if (index.Length != 0 && index.All(c => c >= '0' && c <= '9') && index.Length < 6 &&
            mCurrent.SetPersistElement(mStrings.Intern(field), CAtoi(index), values))
            return;
        mCurrent.SetDataField(key, values.Count == 0 ? "" : values[0]);
    }


    /// <summary>Reads a field of an object or datablock (5.1, 5.2).</summary>
    /// <param name="obj">The object or block name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="index">The array index text, empty for none.</param>
    /// <param name="output">Receives the value.</param>
    /// <returns>False when neither an object nor a block is found, or the block field cannot be read.</returns>
    public bool GetField(string obj, string field, string index, out string output)
    {
        SimObject? o = mServer?.Find(obj);
        if (o != null)
        {
            output = o.DataField(mStrings.Intern(field + index)) ?? "";
            return true;
        }
        output = "";
        DataBlockManager.Block? block = mDataBlocks.Find(obj);
        return block != null && mDataBlocks.GetField(block, field, index, out output);
    }

    /// <summary>Writes a field of an object or datablock (5.1, 5.2).</summary>
    /// <param name="obj">The object or block name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The array index text, empty for none.</param>
    public void SetField(string obj, string field, IReadOnlyList<string> values, string index)
    {
        string first = values.Count == 0 ? "" : values[0];
        SimObject? o = mServer?.Find(obj);
        if (o != null)
        {
            o.SetDataField(mStrings.Intern(field + index), first);
            return;
        }
        if (mDataBlocks.Find(obj) is DataBlockManager.Block block)
            mDataBlocks.SetField(block, field, values, index);
    }


    // src/sim/sim_console.cpp SimConsole::listLine.
    private static string ListLine(SimObject obj)
    {
        string s = "   " + Numbers.FormatInt(obj.Id());
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
        string? n = strings.Lookup(s);
        return n != null && ReferenceEquals(m.Name(), n);
    }

    /// <summary>Registers the SimConsolePlugin commands on the interpreter (docs/sim.md 8).</summary>
    public void RegisterCommands()
    {
        void Cmd(string name, Func<string[], string> f) => mIn.AddCommand(name, (_, a) => f(a));
        const string yes = "True", no = "False";

        Cmd("newServer", _ =>
        {
            if (ClientMode)
            {
                // The `tribes` client keeps its server world in a second SimConsole (Worlds), 0x7c of FearGame_consoleCallback.
                if (Worlds == null)
                {
                    mIn.Print(1, "newServer: this client does not host games; run tribes-server and join it");
                    return no;
                }
                return Worlds.NewServer() ? yes : no;
            }
            NewServer();
            return yes;
        });
        // 0050d51c case 0x7a: focus slot 1 and return True, False when the slot is empty.
        Cmd("focusServer", _ => ClientMode ? (Worlds != null && Worlds.FocusServer() ? yes : no) : mServer != null ? yes : no);
        Cmd("deleteServer", _ =>
        {
            if (ClientMode)
            {
                Worlds?.DeleteServer(); // EndGame() calls it; the client world stays (client-app.md 1.7)
                return yes;
            }
            mServer?.Destroy();
            mServer = null;
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
            mCtx.Resources?.Purge();
            return yes;
        });
        // 0050b4c4 case 0x6e: one line per member of object 2, the name and the file name (+0x68). A member that is
        // not a SimVolume reads a null pointer there (a SimSet prints "(null)").
        Cmd("listVolumes", _ =>
        {
            SimSet? set = Focus()?.FindById(2)?.AsSet();
            if (set != null)
                foreach (SimObject m in set.Members())
                {
                    string file = SimClasses.SimVolumeInfo(m, out string f, out bool _) ? f : "(null)";
                    mIn.Print(1, "   \"" + (m.Name() ?? "") + "\": \"" + file + "\"");
                }
            return yes;
        });

        Cmd("newObject", a =>
        {
            if (a.Length < 3)
            {
                mIn.Print(1, "newObject: objectName className");
                return "0";
            }
            SimManager? focus = Focus();
            if (focus == null)
                return "0";
            SimObject? raw = SimClasses.CreateObject(a[2], mCtx);
            if (raw == null)
            {
                mIn.Print(1, "newObject: persistent create failed (" + a[2] + ")");
                return "0";
            }
            if (!focus.AddToRoot(raw))
            {
                mIn.Print(1, "newObject for: " + a[2] + " - failed register object");
                raw.Destroy();
                return "0";
            }
            if (a[1].Length != 0)
                focus.AssignName(raw, mStrings.Intern(a[1]));
            string[] args = a[3..];
            if (!raw.ProcessArguments(args))
            {
                focus.DeleteObject(raw);
                mIn.Print(1, "newObject for: " + a[2] + " failed arg list: " + JoinArgs(a, 3));
                return "0";
            }
            return Numbers.FormatInt(raw.Id());
        });
        Cmd("deleteObject", a =>
        {
            if (a.Length < 2)
            {
                mIn.Print(1, "deleteObject: objectName [objectName] ...");
                return no;
            }
            for (int i = 1; i < a.Length; ++i)
            {
                SimObject? obj = Focus()?.Find(a[i]);
                if (obj != null && obj != Focus())
                {
                    Focus()!.DeleteObject(obj);
                    return yes;
                }
                mIn.Print(1, "deleteObject: Object \"" + a[i] + "\" doesn't exist");
            }
            return no;
        });
        Cmd("isObject", a =>
        {
            if (a.Length < 2)
            {
                mIn.Print(1, "isObject: objectName [objectName] ...");
                return no;
            }
            for (int i = 1; i < a.Length; ++i)
                if (Focus() == null || Focus()!.Find(a[i]) == null)
                    return no;
            return yes;
        });
        Cmd("listObjects", a =>
        {
            SimObject? obj = a.Length >= 2 ? Focus()?.Find(a[1]) : Focus();
            if (obj == null)
            {
                mIn.Print(1, "listObjects: Could not find object");
                return yes;
            }
            SimSet? set = obj.AsSet();
            if (set == null)
            {
                mIn.Print(1, "listObjects: Object is not a set");
                return yes;
            }
            foreach (SimObject m in set.Members())
                mIn.Print(1, ListLine(m));
            return yes;
        });
        Cmd("addToSet", a =>
        {
            if (a.Length < 3)
            {
                mIn.Print(1, "addToSet: setName [objectName] ...");
                return no;
            }
            SimObject? target = Focus()?.Find(a[1]);
            if (target == null)
            {
                mIn.Print(1, "addToSet: Set \"" + a[1] + "\" does not exist");
                return no;
            }
            SimSet? set = target.AsSet();
            if (set == null)
            {
                mIn.Print(1, "addToSet: Object \"" + a[1] + "\" is not a set");
                return no;
            }
            bool error = false;
            for (int i = 2; i < a.Length; ++i)
            {
                SimObject? obj = Focus()!.Find(a[i]);
                if (obj == null || obj == Focus())
                {
                    mIn.Print(1, "addToSet: Object \"" + a[i] + "\" doesn't exist");
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
                mIn.Print(1, "renameObject: objectName newObjectName");
                return no;
            }
            SimObject? obj = Focus()?.Find(a[1]);
            if (obj == null)
            {
                mIn.Print(1, "renameObject: object does not exist '" + a[1] + "'.");
                return no;
            }
            string? name = a[2].Length == 0 ? null : mStrings.Intern(a[2]);
            obj.Group()?.AssignName(obj, name);
            return yes;
        });
        Cmd("getGroup", a =>
        {
            if (a.Length != 2)
            {
                mIn.Print(1, "GetGroup( object );");
                return yes; // usage falls through to the shared True exit
            }
            SimObject? obj = Focus()?.Find(a[1]);
            if (obj == null || obj.Group() == null)
                return no;
            return Numbers.FormatInt(obj.Group() == Focus() ? Focus()!.ManagerId() : obj.Group()!.Id());
        });
        Cmd("isMember", a =>
        {
            if (a.Length != 3)
            {
                mIn.Print(1, "IsMember( set/group, object );");
                return yes;
            }
            SimSet? set = Focus()?.Find(a[1])?.AsSet();
            if (set == null)
                return no;
            foreach (SimObject m in set.Members())
                if (Matches(m, a[2], mStrings))
                    return yes;
            return no;
        });
        Cmd("getNextObject", a =>
        {
            if (a.Length != 3)
            {
                mIn.Print(1, "GetNextObject( set/group, object );");
                mIn.Print(1, "GetNextObject( set/group, 0 );  // to retrieve first object in a set");
                return "True";
            }
            SimSet? set = Focus()?.Find(a[1])?.AsSet();
            if (set == null)
                return no;
            IReadOnlyList<SimObject> m = set.Members();
            for (int i = 0; i < m.Count; ++i)
                if (Matches(m[i], a[2], mStrings))
                    return i + 1 < m.Count ? Numbers.FormatInt(m[i + 1].Id()) : "0";
            return m.Count == 0 ? "0" : Numbers.FormatInt(m[0].Id());
        });
        Cmd("setObjectId", a =>
        {
            if (a.Length != 3)
            {
                mIn.Print(1, "setObjectId: objectName newObjectId");
                return no;
            }
            SimObject? obj = Focus()?.Find(a[1]);
            if (obj == null || obj == Focus())
            {
                mIn.Print(1, "setObjectId: object does not exist '" + a[1] + "'.");
                return no;
            }
            Focus()!.SetObjectId(obj, Numbers.BorlandAtoi(a[2]));
            return yes;
        });
        Cmd("nameToID", a =>
        {
            if (a.Length != 2)
            {
                mIn.Print(1, "wrong argcount to namToID(name)");
                return "-1";
            }
            SimObject? obj = mServer?.Find(a[1]);
            return obj != null && obj != mServer ? Numbers.FormatInt(obj.Id()) : "-1";
        });
        Cmd("setInstantGroup", a =>
        {
            if (a.Length != 2 || Focus() == null)
                return no;
            SimGroup? g = Focus()!.Find(a[1])?.AsGroup();
            if (g == null)
                return no;
            mInstantGroup = g == Focus() ? null : g;
            return yes;
        });
        Cmd("getSimTime", _ => Numbers.FormatFloat(Focus() != null ? Focus()!.Time() : 0.0f));
        Cmd("resetSimTime", _ =>
        {
            // 00422994: base = tick, then the queue (TM3). Nothing is dispatched here.
            mTickBase = mTick;
            mServer?.ResetTime();
            return yes;
        });
        Cmd("schedule", a =>
        {
            if (a.Length != 3 && a.Length != 4)
            {
                mIn.Print(1, "schedule(statement, deltaTime, <object>);");
                return no;
            }
            SimObject? scheduler = Focus()?.FindById(43);
            if (scheduler == null)
            {
                mIn.Print(1, "schedule: scheduler is not running");
                return no;
            }
            SimObject? dest = a.Length == 3 ? scheduler : Focus()!.Find(a[3]);
            if (dest == null)
                return no;
            double parsed = Numbers.BorlandAtof(a[2]);
            double delay = 0.0 < parsed ? parsed : 0.0; // std::max(0.0, x)
            float t = Store(Add(Focus()!.Time(), delay));
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
                mIn.Print(1, "inspectObject [objectName/Id]");
                return "True";
            }
            if (Focus()?.Find(a[1]) == null && a[1] != "0")
                mIn.Print(1, "object " + a[1] + " not found");
            return "True";
        });
        Cmd("getPathOf", a =>
        {
            if (a.Length != 2)
            {
                mIn.Print(1, "GetPathof( filename );");
                return "True";
            }
            ResourceManager.Entry? e = mCtx.Resources?.Find(a[1]);
            return e != null ? e.FilePath : "";
        });
    }
}
