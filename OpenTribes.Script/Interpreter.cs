namespace OpenTribes.Script;

using OpenTribes.Core;

/// <summary>The simulation-side object access of the interpreter (original interface at console+0x10, docs/script.md 3.10).</summary>
public interface IObjectInterface
{
    /// <summary>Starts a datablock.</summary>
    /// <param name="className">The class name.</param>
    /// <param name="name">The datablock name.</param>
    public void BeginDataBlock(string className, string name);
    /// <summary>Ends the current datablock.</summary>
    public void EndDataBlock();
    /// <summary>Starts an object.</summary>
    /// <param name="className">The class name.</param>
    /// <param name="name">The object name.</param>
    public void BeginObject(string className, string name);
    /// <summary>Ends the current object.</summary>
    public void EndObject();
    /// <summary>Assigns a slot inside a datablock or object body.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The subscript, empty when none.</param>
    public void SetSlot(string field, IReadOnlyList<string> values, string index);
    /// <summary>Reads a field of a named or numbered object.</summary>
    /// <param name="obj">The object name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="index">The subscript, empty when none.</param>
    /// <param name="output">Receives the value.</param>
    /// <returns>False when the object is missing.</returns>
    public bool GetField(string obj, string field, string index, out string output);
    /// <summary>Writes a field of a named or numbered object.</summary>
    /// <param name="obj">The object name or id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <param name="index">The subscript, empty when none.</param>
    public void SetField(string obj, string field, IReadOnlyList<string> values, string index);
}

/// <summary>The console script interpreter (docs/script.md section 3): tree-walking, with every value a string.</summary>
/// <remarks>Port of src/script/interpreter.cpp. Names are interned in the global string table and the C++ pointer keys are the canonical strings here.</remarks>
public sealed class Interpreter
{
    /// <summary>A native command. The C++ returns false for a NULL result, which becomes "False", and the result through a reference. Here null is the NULL result.</summary>
    /// <param name="interp">The interpreter.</param>
    /// <param name="argv">The arguments. The first is the command name.</param>
    /// <returns>The result string, or null for the NULL result.</returns>
    public delegate string? Command(Interpreter interp, string[] argv);

    private enum Completion { Normal = 0, ContinueLoop = 1, BreakLoop = 2, ReturnFn = 3 }
    private enum StaticType { TString = 0, TFloat = 1, TInt = 2 }

    /// <summary>One sim world of a process that runs two, for example a hosted game with a client world and a server world.</summary>
    /// <remarks>The console variables and the script functions are shared, like the one console of the stock exe. The commands and the object interface follow the focus (the stock focus pointer DAT_006a8494, set per world by FearWorld_advance 004eba40 and FearWorld_clientAdvance 004eb7ac and by focusServer and focusClient, cases 0x7a and 0x79 of FearGame_consoleCallback).</remarks>
    public sealed class World
    {
        internal Dictionary<string, Function> Commands { get; } = new(StringComparer.Ordinal);
        /// <summary>The object interface of this world.</summary>
        public IObjectInterface? Objects { get; set; }
    }

    internal sealed class Function
    {
        public Node? Decl { get; set; }
        public Command? Builtin { get; set; }
    }
    private sealed class Frame
    {
        public Dictionary<string, string> Locals { get; } = new(StringComparer.Ordinal);
        public string Result { get; set; } = "";
    }

    private readonly StringTable _strings;
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Function> _functions = new(StringComparer.Ordinal);
    private World? _focus;               // the world whose commands answer first (null: only the shared set)
    private World? _registerTarget;      // AddCommand fills this world while a registration runs
    private IObjectInterface? _sharedObjects;
    private readonly List<World> _worlds = [];
    private readonly List<Frame> _frames = []; // [0] is the global frame
    private readonly Dictionary<int, string> _tagText = [];
    private readonly Dictionary<int, string> _tagRaw = [];
    private readonly Dictionary<string, int> _tagIds = new(StringComparer.Ordinal);
    private Action<string>? _output;
    private IObjectInterface? _objects;
    private ResourceManager? _resources;
    private int _printLevel = 1;
    private bool _lastHope;
    private readonly List<string> _replacedCommands = [];
    private string _lastValue = "";
    private readonly Dictionary<string, Action<string>> _bindings = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _unknown = new(StringComparer.Ordinal);

    // x87 tests against zero: NaN compares unordered, which the original treats as equal to 0.
    private static bool Truthy(double v) => v != 0 && !double.IsNaN(v);

    private static bool EqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < b.Length; ++i)
            if ((a[i] | 0x20) != (b[i] | 0x20))
                return false;
        return true;
    }

    private static string ConcatArgs(string[] argv, int from)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = from; i < argv.Length; ++i)
            sb.Append(argv[i]);
        return sb.ToString();
    }

    /// <summary>Creates an interpreter with the engine-bound console variables and the console core commands (005f3a7c).</summary>
    /// <param name="strings">The string table, or null for a new one.</param>
    public Interpreter(StringTable? strings = null)
    {
        _strings = strings ?? StringTable.Global();
        Numbers.EnterScriptFpuMode();
        _frames.Add(new Frame());
        // Engine-bound console variables exist from the start (0x005f3a7c) [live].
        _globals[_strings.Intern("$Console::printLevel")] = "1";
        _globals[_strings.Intern("$Console::logBufferEnabled")] = "False";
        _globals[_strings.Intern("$Console::updateMetrics")] = "False";

        // Console core commands (0x005f3a7c).
        AddCommand("echo", (interp, argv) =>
        {
            interp.Print(1, ConcatArgs(argv, 1));
            return "True";
        });
        AddCommand("dbecho", (interp, argv) =>
        {
            if (argv.Length >= 2)
                interp.Print(Numbers.BorlandAtoi(argv[1]), ConcatArgs(argv, 2));
            return "True";
        });
        AddCommand("strcat", (interp, argv) => ConcatArgs(argv, 1));
        AddCommand("floor", (interp, argv) =>
            argv.Length >= 2 ? Numbers.FormatInt(Numbers.FloatToInt(Numbers.BorlandAtof(argv[1]))) : "0");
        AddCommand("sqrt", (interp, argv) =>
        {
            if (argv.Length != 2)
            {
                interp.Print(1, "wrong argcount to sqrt(Value)");
                return "-1";
            }
            var v = Numbers.BorlandAtof(argv[1]);
            return Numbers.FormatFloat(v < 0 ? 0.0 : Fpu.Sqrt(v)); // Borland's matherr turns the domain error into 0
        });
        AddCommand("eval", (interp, argv) =>
        {
            if (argv.Length != 2)
            {
                interp.Print(1, "eval(consoleString);");
                return null;
            }
            return interp.Evaluate(argv[1]);
        });
        AddCommand("exec", (interp, argv) =>
        {
            if (argv.Length != 2)
            {
                interp.Print(1, "exec(filename);");
                return null;
            }
            return interp.ExecFile(argv[1]) ? "True" : "False";
        });
    }

    /// <summary>Registers a command, replacing an earlier one of the same name.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="cmd">The command.</param>
    public void AddCommand(string name, Command cmd)
    {
        var key = _strings.Intern(name);
        if (_registerTarget != null)
        {
            _registerTarget.Commands[key] = new Function { Builtin = cmd };
            return;
        }
        if (_functions.TryGetValue(key, out var f) && f.Builtin != null)
            _replacedCommands.Add(name);
        _functions[key] = new Function { Builtin = cmd };
    }

    /// <summary>Gets the native commands that were registered more than once.</summary>
    /// <returns>The names.</returns>
    public IReadOnlyList<string> ReplacedCommands() => _replacedCommands;
    /// <summary>Switches the LastHope 1.30 behaviour on or off (docs/lasthope-1.30.md L5).</summary>
    /// <param name="on">Whether LastHope mode is on.</param>
    public void SetLastHope(bool on) => _lastHope = on;
    /// <summary>Gets whether LastHope 1.30 behaviour is on.</summary>
    /// <returns>True when on.</returns>
    public bool LastHope() => _lastHope;
    /// <summary>Sets the console output sink.</summary>
    /// <param name="output">The sink, or null.</param>
    public void SetOutput(Action<string>? output) => _output = output;
    /// <summary>Sets the object interface of the focused world.</summary>
    /// <param name="objects">The interface, or null.</param>
    public void SetObjects(IObjectInterface? objects)
    {
        _sharedObjects = objects;
        if (_focus == null)
            _objects = objects;
    }

    /// <summary>Creates a world. Commands registered inside <see cref="Register"/> answer only while it has the focus.</summary>
    /// <returns>The new world.</returns>
    public World NewWorld()
    {
        var w = new World();
        _worlds.Add(w);
        return w;
    }

    /// <summary>Drops a world, moving the focus to the shared world if it had it.</summary>
    /// <param name="w">The world.</param>
    public void DropWorld(World w)
    {
        if (_focus == w)
            Focus(null);
        _worlds.Remove(w);
    }

    /// <summary>Runs registrations so that the commands they add belong to a world.</summary>
    /// <param name="w">The world.</param>
    /// <param name="registrations">The registrations to run.</param>
    public void Register(World w, Action registrations)
    {
        var previous = _registerTarget;
        _registerTarget = w;
        try
        {
            registrations();
        }
        finally
        {
            _registerTarget = previous;
        }
    }

    /// <summary>Removes commands from a world so that the shared command answers in every focus, for example the world commands newServer and focusServer.</summary>
    /// <param name="w">The world.</param>
    /// <param name="names">The command names.</param>
    public void Unregister(World w, params string[] names)
    {
        foreach (var n in names)
            w.Commands.Remove(_strings.Intern(n));
    }

    /// <summary>Gets the world that has the focus.</summary>
    /// <returns>The world, or null for the shared (client) world.</returns>
    public World? FocusedWorld() => _focus;

    /// <summary>Moves the focus to a world, as the stock focus switch does.</summary>
    /// <param name="w">The world, or null for the shared (client) world.</param>
    public void Focus(World? w)
    {
        _focus = w;
        _objects = w?.Objects ?? _sharedObjects;
    }
    /// <summary>Sets the resource manager.</summary>
    /// <param name="res">The resource manager, or null.</param>
    public void SetResources(ResourceManager? res) => _resources = res;
    /// <summary>Gets the current print level.</summary>
    /// <returns>The level.</returns>
    public int PrintLevel() => _printLevel;
    /// <summary>Gets the calls to names that have no function or command.</summary>
    /// <returns>The call counts by interned name.</returns>
    public IReadOnlyDictionary<string, int> UnknownCommands() => _unknown;

    /// <summary>Prints console output at a print level (printf is 1, dbprintf(n) is n).</summary>
    /// <param name="level">The print level.</param>
    /// <param name="line">The text.</param>
    public void Print(int level, string line)
    {
        if (level <= _printLevel && _printLevel != 0 && _output != null)
            _output(line);
    }

    // ---- variables ----

    // src/script/interpreter.cpp Interpreter::scopeFor.
    private Dictionary<string, string> ScopeFor(string name) => name.Length != 0 && name[0] == '%' ? _frames[^1].Locals : _globals;

    /// <summary>Reads a variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value.</returns>
    public string GetVariable(string name) => GetVariable(name, out _);

    /// <summary>Reads a variable and reports whether it exists.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="found">Receives whether the variable exists.</param>
    /// <returns>The value.</returns>
    public string GetVariable(string name, out bool found)
    {
        var key = _strings.Intern(name);
        var scope = ScopeFor(key);
        found = scope.TryGetValue(key, out var v);
        if (!found)
        {
            Print(2, "Varaible referenced before assignment: " + key);
            return "";
        }
        return v!;
    }

    /// <summary>Reads a global from the engine side.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, empty when unset.</returns>
    public string PeekVariable(string name) => _globals.TryGetValue(_strings.Intern(name), out var v) ? v : "";

    /// <summary>Tests whether a variable exists.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>True when it exists.</returns>
    public bool HasVariable(string name)
    {
        var key = _strings.Lookup(name);
        return key != null && _globals.ContainsKey(key);
    }

    /// <summary>Writes a variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value.</param>
    public void SetVariable(string name, string value)
    {
        var key = _strings.Intern(name);
        if (key == _strings.Intern("$Console::printLevel"))
        {
            // Engine-bound int variable: stores atoi of the value.
            _printLevel = Numbers.BorlandAtoi(value);
            _globals[key] = Numbers.FormatInt(_printLevel);
            return;
        }
        if (key == _strings.Intern("$Console::logBufferEnabled") || key == _strings.Intern("$Console::updateMetrics"))
        {
            // Engine-bound bool variables store True or False.
            _globals[key] = Numbers.IsTrueString(value) || Numbers.BorlandAtoi(value) != 0 ? "True" : "False";
            return;
        }
        ScopeFor(key)[key] = value;
        if (_bindings.TryGetValue(key, out var onSet))
            onSet(value);
    }

    /// <summary>Binds a callback to a variable. It runs after every assignment, like the original's variable callbacks.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="onSet">The callback, given the new value.</param>
    public void BindVariable(string name, Action<string> onSet) => _bindings[_strings.Intern(name)] = onSet;

    // src/script/interpreter.cpp Interpreter::indexString.
    private string IndexString(List<Node> index)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < index.Count; ++i)
        {
            if (i != 0)
                sb.Append('_');
            sb.Append(EvalString(index[i]));
        }
        return sb.ToString();
    }

    /// <summary>Gets the globals for the export and delete built-ins.</summary>
    /// <returns>The name and value pairs.</returns>
    public List<KeyValuePair<string, string>> Globals() => [.. _globals];

    /// <summary>Lists the function names, including global-frame % variables (docs/script.md 3.6.9).</summary>
    /// <returns>The names.</returns>
    public List<string> FunctionNames()
    {
        var output = new List<string>(_functions.Keys);
        output.AddRange(_frames[0].Locals.Keys);
        return output;
    }

    /// <summary>Removes a global variable.</summary>
    /// <param name="name">The variable name.</param>
    public void EraseGlobal(string name)
    {
        var key = _strings.Lookup(name);
        if (key != null)
            _globals.Remove(key);
    }

    /// <summary>Removes a function.</summary>
    /// <param name="name">The function name.</param>
    public void EraseFunction(string name)
    {
        var key = _strings.Lookup(name);
        if (key != null)
        {
            _functions.Remove(key);
            _frames[0].Locals.Remove(key);
        }
    }

    // ---- running text ----

    /// <summary>Parses and runs script text.</summary>
    /// <param name="text">The script text.</param>
    /// <param name="fileName">The file name for error messages, empty for console input.</param>
    /// <returns>The string value of the last expression statement run.</returns>
    public string Evaluate(string text, string fileName = "")
    {
        var nul = text.IndexOf('\0');
        var src = nul < 0 ? text : text[..nul];
        Numbers.EnterScriptFpuMode(); // pow can leave round-to-nearest behind; the original's next ftol resets it
        var r = ScriptParser.Parse(src);
        if (r.Error)
            Print(1, ScriptParser.SyntaxErrorMessage(r, fileName.Length == 0 ? "" : _strings.Intern(fileName)));

        var saved = _lastValue;
        _lastValue = "";
        foreach (var s in r.Statements)
            Exec(s); // top-level completion codes are ignored
        var result = _lastValue;
        _lastValue = saved;
        return result;
    }

    /// <summary>Runs a script file through the resource manager.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>False when the file cannot be read.</returns>
    public bool ExecFile(string fileName)
    {
        var name = fileName;
        if (!name.Contains(".cs", StringComparison.Ordinal) && !name.Contains(".mis", StringComparison.Ordinal) &&
            !name.Contains(".dsc", StringComparison.Ordinal) && !name.Contains(".mac", StringComparison.Ordinal))
            name += ".cs";
        var entry = _resources?.Find(name);
        var data = Array.Empty<byte>();
        if (entry == null || !_resources!.Read(entry, out data))
        {
            Print(1, "exec: invalid script file " + name + ".");
            return false;
        }
        Print(1, "Executing " + name + ".");
        Evaluate(ByteString.FromBytes(data), name);
        return true;
    }

    // ---- statements ----

    // src/script/interpreter.cpp Interpreter::execList.
    private Completion ExecList(List<Node> list)
    {
        foreach (var s in list)
        {
            var c = Exec(s);
            if (c != Completion.Normal)
                return c;
        }
        return Completion.Normal;
    }

    // src/script/interpreter.cpp Interpreter::exec.
    private Completion Exec(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.ExprStmt:
            {
                var v = EvalString(n.A!);
                _frames[^1].Result = v;
                _lastValue = v;
                return Completion.Normal;
            }
            case NodeKind.If:
                return Truthy(EvalFloat(n.A!)) ? ExecList(n.Body) : ExecList(n.ElseBody);
            case NodeKind.While:
                while (Truthy(EvalFloat(n.A!)))
                {
                    var c = ExecList(n.Body);
                    if (c == Completion.BreakLoop)
                        break;
                    if (c == Completion.ReturnFn)
                        return c;
                }
                return Completion.Normal;
            case NodeKind.For:
                EvalString(n.A!);
                while (Truthy(EvalFloat(n.B!)))
                {
                    var c = ExecList(n.Body);
                    if (c == Completion.BreakLoop)
                        break;
                    if (c == Completion.ReturnFn)
                        return c;
                    EvalString(n.C!);
                }
                return Completion.Normal;
            case NodeKind.Function:
            {
                var fname = _strings.Intern(n.Name);
                _functions[fname] = new Function { Decl = n };
                foreach (var w in _worlds) // a script function replaces a native command, as in the shared set
                    w.Commands.Remove(fname);
                return Completion.Normal;
            }
            case NodeKind.Return:
                _frames[^1].Result = n.A != null ? EvalString(n.A) : "";
                return Completion.ReturnFn;
            case NodeKind.Continue:
                return Completion.ContinueLoop;
            case NodeKind.BreakStmt:
                return Completion.BreakLoop;
            case NodeKind.TagDecl:
            {
                var id = Numbers.BorlandAtoi(EvalString(n.A!));
                var text = n.B != null ? EvalString(n.B) : "";
                var name = _strings.Intern(n.Name);
                // Id 0 is ignored without a line; a negative id registers the name but is never
                // found by id (see the C++ interpreter, [code 005ee5ac]).
                if (id == 0)
                    return Completion.Normal;
                if (id > 0 && _tagText.ContainsKey(id))
                {
                    Print(1, "Error: id " + Numbers.FormatInt(id) + " already defined to a tag.");
                }
                else if (_tagIds.TryGetValue(name, out var existing))
                {
                    Print(1, "Error: define " + name + " already defined to tag " + Numbers.FormatInt(existing) + ".");
                }
                else
                {
                    // Tag text lives in the string table, so it also fixes the spelling of later
                    // case variants (fear.strings.cs "ARMOR" makes className = "Armor" read ARMOR).
                    if (id > 0)
                    {
                        _tagText[id] = text.Length == 0 ? text : _strings.Intern(text);
                        _tagRaw[id] = text;
                    }
                    _tagIds[name] = id;
                }
                return Completion.Normal;
            }
            case NodeKind.Datablock:
                _objects?.BeginDataBlock(n.ClassName, n.Name);
                foreach (var s in n.Slots)
                    Exec(s);
                _objects?.EndDataBlock();
                return Completion.Normal;
            case NodeKind.Instant:
            {
                var name = n.A != null ? EvalString(n.A) : "";
                _objects?.BeginObject(n.ClassName, name);
                foreach (var s in n.Slots)
                    Exec(s);
                foreach (var c in n.Children)
                    Exec(c);
                _objects?.EndObject();
                return Completion.Normal;
            }
            case NodeKind.Slot:
            {
                var index = IndexString(n.Index);
                var values = new List<string>();
                if (n.IsList)
                    foreach (var v in n.List)
                        values.Add(EvalString(v));
                else
                    values.Add(EvalString(n.A!));
                _objects?.SetSlot(n.Name, values, index);
                return Completion.Normal;
            }
            case NodeKind.IntLiteral:
            case NodeKind.FloatLiteral:
            case NodeKind.StringLiteral:
            case NodeKind.Ident:
            case NodeKind.BreakWord:
            case NodeKind.TagRef:
            case NodeKind.Var:
            case NodeKind.Field:
            case NodeKind.Call:
            case NodeKind.MethodCall:
            case NodeKind.Unary:
            case NodeKind.Binary:
            case NodeKind.Assign:
            case NodeKind.FieldListAssign:
            case NodeKind.CompoundAssign:
            case NodeKind.PostIncDec:
            default:
                EvalString(n);
                return Completion.Normal;
        }
    }

    // ---- expressions ----

    // src/script/interpreter.cpp Interpreter::staticType.
    private static StaticType StaticTypeOf(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.FloatLiteral:
                return StaticType.TFloat;
            case NodeKind.IntLiteral:
                return StaticType.TInt;
            case NodeKind.Unary:
                return n.OpChar == '-' ? StaticType.TFloat : n.OpChar == '*' ? StaticType.TString : StaticType.TInt;
            case NodeKind.Binary:
                if (n.Op == Tok.SingleChar)
                {
                    return n.OpChar switch
                    {
                        '+' or '-' or '*' or '/' => StaticType.TFloat,
                        '@' => StaticType.TString,
                        _ => StaticType.TInt,
                    };
                }
                return n.Op == Tok.DollarPlus ? StaticType.TString : StaticType.TInt;
            case NodeKind.StringLiteral:
            case NodeKind.Ident:
            case NodeKind.BreakWord:
            case NodeKind.TagRef:
            case NodeKind.Var:
            case NodeKind.Field:
            case NodeKind.Call:
            case NodeKind.MethodCall:
            case NodeKind.Assign:
            case NodeKind.FieldListAssign:
            case NodeKind.CompoundAssign:
            case NodeKind.PostIncDec:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.For:
            case NodeKind.Function:
            case NodeKind.Return:
            case NodeKind.Continue:
            case NodeKind.BreakStmt:
            case NodeKind.TagDecl:
            case NodeKind.Datablock:
            case NodeKind.Instant:
            case NodeKind.Slot:
            default:
                return StaticType.TString;
        }
    }

    // 2 = unordered (NaN difference): == < <= are true, != > >= false, as on x87.
    private static int Sign(double d) => double.IsNaN(d) ? 2 : d < 0 ? -1 : d > 0 ? 1 : 0;

    // src/script/interpreter.cpp Interpreter::compare.
    private int Compare(Node l, Node r)
    {
        StaticType tl = StaticTypeOf(l), tr = StaticTypeOf(r);
        if (tl == StaticType.TInt && tr == StaticType.TInt)
        {
            var d = unchecked((uint)EvalInt(l) - (uint)EvalInt(r));
            return Sign(unchecked((int)d));
        }
        if (tl != StaticType.TString || tr != StaticType.TString)
        {
            var a = EvalFloat(l);
            var b = EvalFloat(r);
            return Sign(Fpu.Sub(a, b));
        }
        string sa = EvalString(l), sb = EvalString(r);
        float fa = 0, fb = 0;
        var boolMode = false;
        bool Classify(string s, ref float f)
        {
            if (Numbers.IsTrueString(s))
            {
                f = 1.0f;
                boolMode = true;
            }
            else if (Numbers.LooksNumeric(s))
            {
                f = Fpu.Store(Numbers.BorlandAtof(s));
            }
            else if (EqualsIgnoreCase(s, "false"))
            {
                f = 0.0f;
                boolMode = true;
            }
            else
            {
                return false;
            }
            return true;
        }
        if (!Classify(sa, ref fa) || !Classify(sb, ref fb))
        {
            var c = CString.Strcmp(sa, sb);
            return c < 0 ? -1 : c > 0 ? 1 : 0;
        }
        if (boolMode)
            return fa != 0 != (fb != 0) ? 1 : 0;
        var fd = fa - fb;
        return fd < 0 ? -1 : fd > 0 ? 1 : 0;
    }

    // src/script/interpreter.cpp Interpreter::evalFloat.
    private double EvalFloat(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.FloatLiteral:
                return Numbers.BorlandAtof(n.Name);
            case NodeKind.IntLiteral:
                return Numbers.ParseIntLiteral(n.Name);
            case NodeKind.Unary:
                if (n.OpChar == '-')
                    return -EvalFloat(n.A!);
                break;
            case NodeKind.Binary:
                if (n.Op == Tok.SingleChar && (n.OpChar == '+' || n.OpChar == '-' || n.OpChar == '*' || n.OpChar == '/'))
                {
                    var l = EvalFloat(n.A!); // left operand is stored to a 64-bit double
                    var r = EvalFloat(n.B!);
                    return n.OpChar switch
                    {
                        '+' => Fpu.Add(l, r),
                        '-' => Fpu.Sub(l, r),
                        '*' => Fpu.Mul(l, r),
                        _ => Fpu.Div(l, r),
                    };
                }
                break;
            case NodeKind.StringLiteral:
            case NodeKind.Ident:
            case NodeKind.BreakWord:
            case NodeKind.TagRef:
            case NodeKind.Var:
            case NodeKind.Field:
            case NodeKind.Call:
            case NodeKind.MethodCall:
            case NodeKind.Assign:
            case NodeKind.FieldListAssign:
            case NodeKind.CompoundAssign:
            case NodeKind.PostIncDec:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.For:
            case NodeKind.Function:
            case NodeKind.Return:
            case NodeKind.Continue:
            case NodeKind.BreakStmt:
            case NodeKind.TagDecl:
            case NodeKind.Datablock:
            case NodeKind.Instant:
            case NodeKind.Slot:
            default:
                break;
        }
        if (StaticTypeOf(n) == StaticType.TInt)
            return EvalInt(n);
        var s = EvalString(n);
        return Numbers.IsTrueString(s) ? 1.0 : Numbers.BorlandAtof(s);
    }

    // src/script/interpreter.cpp Interpreter::evalInt.
    private int EvalInt(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.IntLiteral:
                return Numbers.ParseIntLiteral(n.Name);
            case NodeKind.Unary:
                if (n.OpChar == '!')
                    return Truthy(EvalFloat(n.A!)) ? 0 : 1;
                if (n.OpChar == '~')
                    return ~EvalInt(n.A!);
                break;
            case NodeKind.Binary:
            {
                if (n.Op == Tok.AndAnd)
                    return Truthy(EvalFloat(n.A!)) && Truthy(EvalFloat(n.B!)) ? 1 : 0;
                if (n.Op == Tok.OrOr)
                    return Truthy(EvalFloat(n.A!)) || Truthy(EvalFloat(n.B!)) ? 1 : 0;
                int c;
                switch (n.Op)
                {
                    case Tok.Eq:
                        c = Compare(n.A!, n.B!);
                        return c is 0 or 2 ? 1 : 0;
                    case Tok.Ne:
                        c = Compare(n.A!, n.B!);
                        return c is (-1) or 1 ? 1 : 0;
                    case Tok.Le:
                        c = Compare(n.A!, n.B!);
                        return c != 1 ? 1 : 0;
                    case Tok.Ge:
                        c = Compare(n.A!, n.B!);
                        return c is 0 or 1 ? 1 : 0;
                    case Tok.Shl:
                        c = EvalInt(n.A!);
                        return unchecked((int)((uint)c << (EvalInt(n.B!) & 31)));
                    case Tok.Shr:
                        c = EvalInt(n.A!);
                        return c >> (EvalInt(n.B!) & 31);
                    case Tok.SingleChar:
                        switch (n.OpChar)
                        {
                            case '<':
                                c = Compare(n.A!, n.B!);
                                return c is (-1) or 2 ? 1 : 0;
                            case '>':
                                return Compare(n.A!, n.B!) == 1 ? 1 : 0;
                            case '%':
                            {
                                var r = EvalInt(n.B!); // right first; left skipped when right is 0
                                if (r == 0)
                                    return 0;
                                var l = EvalInt(n.A!);
                                if (r == -1)
                                    return 0; // INT_MIN % -1 traps on x86 (D11); every other x % -1 is 0
                                return l % r;
                            }
                            case '&':
                                c = EvalInt(n.A!);
                                return c & EvalInt(n.B!);
                            case '|':
                                c = EvalInt(n.A!);
                                return c | EvalInt(n.B!);
                            case '^':
                                c = EvalInt(n.A!);
                                return c ^ EvalInt(n.B!);
                            default:
                                break;
                        }
                        break;
                    case Tok.End:
                    case Tok.Illegal:
                    case Tok.StringLiteral:
                    case Tok.IntLiteral:
                    case Tok.FloatLiteral:
                    case Tok.Var:
                    case Tok.Tag:
                    case Tok.Ident:
                    case Tok.Break:
                    case Tok.Nil:
                    case Tok.Class:
                    case Tok.Return:
                    case Tok.Else:
                    case Tok.Global:
                    case Tok.While:
                    case Tok.If:
                    case Tok.For:
                    case Tok.Continue:
                    case Tok.Function:
                    case Tok.Instant:
                    case Tok.AndAnd:
                    case Tok.OrOr:
                    case Tok.ColonColon:
                    case Tok.MinusMinus:
                    case Tok.PlusPlus:
                    case Tok.DollarPlus:
                    case Tok.PlusAssign:
                    case Tok.MinusAssign:
                    case Tok.MulAssign:
                    case Tok.DivAssign:
                    case Tok.ModAssign:
                    case Tok.AndAssign:
                    case Tok.XorAssign:
                    case Tok.OrAssign:
                    case Tok.ShlAssign:
                    case Tok.ShrAssign:
                    default:
                        break;
                }
                break;
            }
            case NodeKind.FloatLiteral:
            case NodeKind.StringLiteral:
            case NodeKind.Ident:
            case NodeKind.BreakWord:
            case NodeKind.TagRef:
            case NodeKind.Var:
            case NodeKind.Field:
            case NodeKind.Call:
            case NodeKind.MethodCall:
            case NodeKind.Assign:
            case NodeKind.FieldListAssign:
            case NodeKind.CompoundAssign:
            case NodeKind.PostIncDec:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.For:
            case NodeKind.Function:
            case NodeKind.Return:
            case NodeKind.Continue:
            case NodeKind.BreakStmt:
            case NodeKind.TagDecl:
            case NodeKind.Datablock:
            case NodeKind.Instant:
            case NodeKind.Slot:
            default:
                break;
        }
        if (StaticTypeOf(n) == StaticType.TFloat)
            return Numbers.FloatToInt(EvalFloat(n));
        var s = EvalString(n);
        return Numbers.IsTrueString(s) ? 1 : Numbers.BorlandAtoi(s);
    }

    // src/script/interpreter.cpp Interpreter::readLvalue.
    private string ReadLvalue(Node lv)
    {
        if (lv.Kind == NodeKind.Var)
            return GetVariable(lv.Name + IndexString(lv.Index));
        var obj = EvalString(lv.A!);
        if (_objects == null || !_objects.GetField(obj, lv.Name, IndexString(lv.Index), out var output))
            return "False";
        return output;
    }

    // src/script/interpreter.cpp Interpreter::writeLvalue.
    private void WriteLvalue(Node lv, string value)
    {
        if (lv.Kind == NodeKind.Var)
        {
            SetVariable(lv.Name + IndexString(lv.Index), value);
            return;
        }
        var obj = EvalString(lv.A!);
        _objects?.SetField(obj, lv.Name, [value], IndexString(lv.Index));
    }

    // src/script/interpreter.cpp Interpreter::compoundAssign.
    private string CompoundAssign(Node lv, Tok op, char opChar, Node? rhs)
    {
        var current = ReadLvalue(lv);
        string result;
        var floatPath = opChar == '+' || opChar == '-' || op == Tok.PlusAssign || op == Tok.MinusAssign ||
                         op == Tok.MulAssign || op == Tok.DivAssign;
        if (floatPath)
        {
            var l = Numbers.IsTrueString(current) ? 1.0 : Numbers.BorlandAtof(current);
            var r = rhs != null ? EvalFloat(rhs) : 1.0;
            double v;
            if (op == Tok.PlusAssign || opChar == '+')
                v = Fpu.Add(l, r);
            else if (op == Tok.MinusAssign || opChar == '-')
                v = Fpu.Sub(l, r);
            else if (op == Tok.MulAssign)
                v = Fpu.Mul(l, r);
            else
                v = Fpu.Div(l, r);
            result = _lastHope ? Numbers.FormatFloat15(v) : Numbers.FormatFloat(v);
        }
        else
        {
            var l = Numbers.IsTrueString(current) ? 1 : Numbers.BorlandAtoi(current);
            var r = EvalInt(rhs!);
            var v = op switch
            {
                Tok.ModAssign => (r is 0 or (-1)) ? 0 : l % r,
                Tok.AndAssign => l & r,
                Tok.OrAssign => l | r,
                Tok.XorAssign => l ^ r,
                Tok.ShlAssign => unchecked((int)((uint)l << (r & 31))),
                Tok.End or Tok.Illegal or Tok.StringLiteral or Tok.IntLiteral or Tok.FloatLiteral or Tok.Var or Tok.Tag or Tok.Ident or Tok.Break or Tok.Nil or Tok.Class or Tok.Return or Tok.Else or Tok.Global or Tok.While or Tok.If or Tok.For or Tok.Continue or Tok.Function or Tok.Instant or Tok.Eq or Tok.Ne or Tok.Ge or Tok.Le or Tok.AndAnd or Tok.OrOr or Tok.ColonColon or Tok.MinusMinus or Tok.PlusPlus or Tok.DollarPlus or Tok.Shl or Tok.Shr or Tok.PlusAssign or Tok.MinusAssign or Tok.MulAssign or Tok.DivAssign or Tok.ShrAssign or Tok.SingleChar or _ => l >> (r & 31),
            };
            result = Numbers.FormatInt(v);
        }
        WriteLvalue(lv, result);
        return result;
    }

    // src/script/interpreter.cpp Interpreter::evalString.
    private string EvalString(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.IntLiteral:
                return Numbers.FormatInt(Numbers.ParseIntLiteral(n.Name));
            case NodeKind.FloatLiteral:
                return Numbers.FormatFloat(Numbers.BorlandAtof(n.Name));
            case NodeKind.Ident: // already the interned spelling (lexer, script.md 1.2)
            case NodeKind.StringLiteral:
            case NodeKind.BreakWord:
                return n.Name;
            case NodeKind.TagRef:
                return Numbers.FormatInt(_tagIds.TryGetValue(_strings.Intern(n.Name), out var tagId) ? tagId : 0);
            case NodeKind.Var:
            case NodeKind.Field:
                return ReadLvalue(n);
            case NodeKind.Call:
            {
                var args = new List<string>();
                foreach (var a in n.List)
                    args.Add(EvalString(a));
                return Call(n.Name, args);
            }
            case NodeKind.MethodCall:
            {
                var args = new List<string> { EvalString(n.A!) };
                foreach (var a in n.List)
                    args.Add(EvalString(a));
                return Call("dot_op_" + n.Name, args);
            }
            case NodeKind.Unary:
                if (n.OpChar == '*')
                    return _tagText.TryGetValue(EvalInt(n.A!), out var text) ? text : "<INVALID TAG>";
                break;
            case NodeKind.Binary:
                if (n.OpChar == '@' || n.Op == Tok.DollarPlus)
                {
                    var l = EvalString(n.A!);
                    return l + EvalString(n.B!);
                }
                break;
            case NodeKind.Assign:
            {
                var v = EvalString(n.B!);
                WriteLvalue(n.A!, v);
                return v;
            }
            case NodeKind.FieldListAssign:
            {
                var obj = EvalString(n.A!.A!);
                var values = new List<string>();
                foreach (var v in n.List)
                    values.Add(EvalString(v));
                _objects?.SetField(obj, n.A.Name, values, IndexString(n.A.Index));
                return values.Count == 0 ? "" : values[^1];
            }
            case NodeKind.CompoundAssign:
                return CompoundAssign(n.A!, n.Op, '\0', n.B);
            case NodeKind.PostIncDec:
                return CompoundAssign(n.A!, Tok.End, n.OpChar, null);
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.For:
            case NodeKind.Function:
            case NodeKind.Return:
            case NodeKind.Continue:
            case NodeKind.BreakStmt:
            case NodeKind.TagDecl:
            case NodeKind.Datablock:
            case NodeKind.Instant:
            case NodeKind.Slot:
            default:
                break;
        }
        if (StaticTypeOf(n) == StaticType.TFloat)
        {
            // 1.30 changed the format of FloatBinaryExprNode only; literals and negation keep %g.
            if (_lastHope && n.Kind == NodeKind.Binary)
                return Numbers.FormatFloat15(EvalFloat(n));
            return Numbers.FormatFloat(EvalFloat(n));
        }
        return Numbers.FormatInt(EvalInt(n));
    }

    /// <summary>Gets the name of the tag defined with an id (005ee788).</summary>
    /// <param name="id">The tag id.</param>
    /// <returns>The name, or empty.</returns>
    public string TagName(int id)
    {
        foreach (var kv in _tagIds)
            if (kv.Value == id)
                return kv.Key;
        return "";
    }

    /// <summary>Gets the id a tag name was defined with.</summary>
    /// <param name="name">The tag name.</param>
    /// <returns>The id, or 0.</returns>
    public int TagId(string name) => _tagIds.TryGetValue(name, out var id) ? id : 0;

    /// <summary>Gets the text of a tag id from the GUI tag dictionary (docs/gui.md 1.5).</summary>
    /// <param name="id">The tag id.</param>
    /// <returns>The text, or null when no tag has that id.</returns>
    public string? TagText(int id) => _tagText.TryGetValue(id, out var t) ? t : null;

    /// <summary>Gets the text of a tag id as the dictionary file spells it.</summary>
    /// <param name="id">The tag id.</param>
    /// <returns>The text, or null when no tag has that id.</returns>
    /// <remarks>A tag used as a script value takes the first spelling the string table saw: a command name such as <c>version</c> fixes the case of the tag "Version". The screens draw the file's spelling.</remarks>
    public string? TagTextRaw(int id) => _tagRaw.TryGetValue(id, out var t) ? t : null;

    // ---- calls ----

    /// <summary>Tests whether a script function exists.</summary>
    /// <param name="name">The function name.</param>
    /// <returns>True when it exists.</returns>
    public bool HasFunction(string name)
    {
        var key = _strings.Lookup(name);
        return key != null && ((_focus != null && _focus.Commands.ContainsKey(key)) || _functions.ContainsKey(key));
    }

    /// <summary>Gets the parameter count of a script function.</summary>
    /// <param name="name">The function name.</param>
    /// <returns>The count, -1 for a built-in or an unknown name.</returns>
    public int FunctionParamCount(string name)
    {
        var key = _strings.Lookup(name);
        if (key == null)
            return -1;
        if (_focus != null && _focus.Commands.ContainsKey(key))
            return -1;
        return !_functions.TryGetValue(key, out var f) || f.Decl == null ? -1 : f.Decl.Params.Count;
    }

    /// <summary>Calls a function or command by name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The result string.</returns>
    public string CallFunction(string name, IReadOnlyList<string> args) => Call(name, [.. args]);

    // src/script/interpreter.cpp Interpreter::call.
    private string Call(string name, List<string> args)
    {
        var key = _strings.Intern(name);
        if (_focus == null || !_focus.Commands.TryGetValue(key, out var fn))
            _functions.TryGetValue(key, out fn);
        if (fn == null)
        {
            _unknown[key] = _unknown.TryGetValue(key, out var count) ? count + 1 : 1;
            Print(1, key + ": Unknown command.");
            return "False";
        }
        if (fn.Builtin != null)
        {
            args.Insert(0, key);
            var cmd = fn.Builtin; // the command may redefine itself
            return cmd(this, [.. args]) ?? "False";
        }
        var decl = fn.Decl!;
        _frames.Add(new Frame());
        for (var i = 0; i < decl.Params.Count && i < args.Count; ++i)
            SetVariable(decl.Params[i], args[i]);
        ExecList(decl.Body);
        var result = _frames[^1].Result;
        _frames.RemoveAt(_frames.Count - 1);
        return result;
    }
}

/// <summary>The C string functions the C++ applies to <c>std::string::c_str()</c>: they stop at the first NUL and compare bytes unsigned.</summary>
public static class CString
{
    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>Compares two strings like strcmp.</summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>A negative value, 0 or a positive value.</returns>
    public static int Strcmp(string a, string b) => Strncmp(a, b, int.MaxValue);

    /// <summary>Compares at most n characters of two strings like strncmp.</summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <param name="n">The most characters to compare.</param>
    /// <returns>A negative value, 0 or a positive value.</returns>
    public static int Strncmp(string a, string b, int n)
    {
        for (var i = 0; i < n; ++i)
        {
            char ca = At(a, i), cb = At(b, i);
            if (ca != cb)
                return ca < cb ? -1 : 1;
            if (ca == '\0')
                return 0;
        }
        return 0;
    }

    /// <summary>Gets the text of <c>s.c_str()</c>.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The string up to its first NUL.</returns>
    public static string CStr(string s)
    {
        var nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }
}
