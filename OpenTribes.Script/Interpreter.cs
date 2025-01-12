// Port of src/script/interpreter.hpp and src/script/interpreter.cpp: console script interpreter
// (docs/script.md section 3). Tree-walking, every value is a string. Names are interned in the
// global string table; the C++ pointer keys are the canonical strings here.
using OpenTribes.Core;

namespace OpenTribes.Script;

// src/script/interpreter.hpp struct ObjectInterface: simulation-side object access (original
// interface at console+0x10, docs/script.md 3.10).
public interface IObjectInterface
{
    void BeginDataBlock(string className, string name);
    void EndDataBlock();
    void BeginObject(string className, string name);
    void EndObject();
    // Slot assignment inside a datablock or object body.
    void SetSlot(string field, IReadOnlyList<string> values, string index);
    // Field access on a named or numbered object. Returns false when the object is missing.
    bool GetField(string obj, string field, string index, out string output);
    void SetField(string obj, string field, IReadOnlyList<string> values, string index);
}

public sealed class Interpreter
{
    // src/script/interpreter.hpp Interpreter::Command: argv[0] is the command name. The C++ returns false for a
    // NULL result (becomes "False") and the result through a reference; here null is the NULL result.
    public delegate string? Command(Interpreter interp, string[] argv);

    private enum Completion { Normal = 0, ContinueLoop = 1, BreakLoop = 2, ReturnFn = 3 }
    private enum StaticType { TString = 0, TFloat = 1, TInt = 2 }

    private sealed class Function
    {
        public Node? Decl;
        public Command? Builtin;
    }
    private sealed class Frame
    {
        public readonly Dictionary<string, string> Locals = new(StringComparer.Ordinal);
        public string Result = "";
    }

    private readonly StringTable mStrings;
    private readonly Dictionary<string, string> mGlobals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Function> mFunctions = new(StringComparer.Ordinal);
    private readonly List<Frame> mFrames = new(); // [0] is the global frame
    private readonly Dictionary<int, string> mTagText = new();
    private readonly Dictionary<string, int> mTagIds = new(StringComparer.Ordinal);
    private Action<string>? mOutput;
    private IObjectInterface? mObjects;
    private ResourceManager? mResources;
    private int mPrintLevel = 1;
    private bool mLastHope;
    private readonly List<string> mReplacedCommands = new();
    private string mLastValue = "";
    private readonly Dictionary<string, Action<string>> mBindings = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> mUnknown = new(StringComparer.Ordinal);

    // x87 tests against zero: NaN compares unordered, which the original treats as equal to 0.
    private static bool Truthy(double v) => v != 0 && !double.IsNaN(v);

    private static bool EqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < b.Length; ++i)
            if ((a[i] | 0x20) != (b[i] | 0x20))
                return false;
        return true;
    }

    private static string ConcatArgs(string[] argv, int from)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = from; i < argv.Length; ++i)
            sb.Append(argv[i]);
        return sb.ToString();
    }

    // src/script/interpreter.cpp Interpreter::Interpreter.
    public Interpreter(StringTable? strings = null)
    {
        mStrings = strings ?? StringTable.Global();
        Numbers.EnterScriptFpuMode();
        mFrames.Add(new Frame());
        // Engine-bound console variables exist from the start (0x005f3a7c) [live].
        mGlobals[mStrings.Intern("$Console::printLevel")] = "1";
        mGlobals[mStrings.Intern("$Console::logBufferEnabled")] = "False";
        mGlobals[mStrings.Intern("$Console::updateMetrics")] = "False";

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
            double v = Numbers.BorlandAtof(argv[1]);
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

    // src/script/interpreter.cpp Interpreter::addCommand.
    public void AddCommand(string name, Command cmd)
    {
        string key = mStrings.Intern(name);
        if (mFunctions.TryGetValue(key, out Function? f) && f.Builtin != null)
            mReplacedCommands.Add(name);
        mFunctions[key] = new Function { Builtin = cmd };
    }

    // src/script/interpreter.hpp replacedCommands: native commands registered more than once.
    public IReadOnlyList<string> ReplacedCommands() => mReplacedCommands;
    // src/script/interpreter.hpp setLastHope / lastHope (docs/lasthope-1.30.md L5).
    public void SetLastHope(bool on) => mLastHope = on;
    public bool LastHope() => mLastHope;
    public void SetOutput(Action<string>? output) => mOutput = output;
    public void SetObjects(IObjectInterface? objects) => mObjects = objects;
    public void SetResources(ResourceManager? res) => mResources = res;
    public int PrintLevel() => mPrintLevel;
    // src/script/interpreter.hpp unknownCommands: calls to names with no function or command, by interned name.
    public IReadOnlyDictionary<string, int> UnknownCommands() => mUnknown;

    // src/script/interpreter.cpp Interpreter::print: console output at a print level (printf = 1, dbprintf(n) = n).
    public void Print(int level, string line)
    {
        if (level <= mPrintLevel && mPrintLevel != 0 && mOutput != null)
            mOutput(line);
    }

    // ---- variables ----

    // src/script/interpreter.cpp Interpreter::scopeFor.
    private Dictionary<string, string> ScopeFor(string name) => name.Length != 0 && name[0] == '%' ? mFrames[^1].Locals : mGlobals;

    // src/script/interpreter.cpp Interpreter::getVariable.
    public string GetVariable(string name) => GetVariable(name, out _);

    public string GetVariable(string name, out bool found)
    {
        string key = mStrings.Intern(name);
        var scope = ScopeFor(key);
        found = scope.TryGetValue(key, out string? v);
        if (!found)
        {
            Print(2, "Varaible referenced before assignment: " + key);
            return "";
        }
        return v!;
    }

    // src/script/interpreter.cpp Interpreter::peekVariable: engine-side read of a global, "" when unset.
    public string PeekVariable(string name)
    {
        return mGlobals.TryGetValue(mStrings.Intern(name), out string? v) ? v : "";
    }

    // src/script/interpreter.cpp Interpreter::hasVariable.
    public bool HasVariable(string name)
    {
        string? key = mStrings.Lookup(name);
        return key != null && mGlobals.ContainsKey(key);
    }

    // src/script/interpreter.cpp Interpreter::setVariable.
    public void SetVariable(string name, string value)
    {
        string key = mStrings.Intern(name);
        if (key == mStrings.Intern("$Console::printLevel"))
        {
            // Engine-bound int variable: stores atoi of the value.
            mPrintLevel = Numbers.BorlandAtoi(value);
            mGlobals[key] = Numbers.FormatInt(mPrintLevel);
            return;
        }
        if (key == mStrings.Intern("$Console::logBufferEnabled") || key == mStrings.Intern("$Console::updateMetrics"))
        {
            // Engine-bound bool variables store True or False.
            mGlobals[key] = Numbers.IsTrueString(value) || Numbers.BorlandAtoi(value) != 0 ? "True" : "False";
            return;
        }
        ScopeFor(key)[key] = value;
        if (mBindings.TryGetValue(key, out Action<string>? onSet))
            onSet(value);
    }

    // src/script/interpreter.cpp Interpreter::bindVariable: onSet runs after every assignment (original variable callbacks).
    public void BindVariable(string name, Action<string> onSet) => mBindings[mStrings.Intern(name)] = onSet;

    // src/script/interpreter.cpp Interpreter::indexString.
    private string IndexString(List<Node> index)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < index.Count; ++i)
        {
            if (i != 0)
                sb.Append('_');
            sb.Append(EvalString(index[i]));
        }
        return sb.ToString();
    }

    // src/script/interpreter.cpp Interpreter::globals: dictionary access for export/delete built-ins.
    public List<KeyValuePair<string, string>> Globals() => mGlobals.ToList();

    // src/script/interpreter.cpp Interpreter::functionNames: including global-frame % variables (docs/script.md 3.6.9).
    public List<string> FunctionNames()
    {
        var output = new List<string>(mFunctions.Keys);
        output.AddRange(mFrames[0].Locals.Keys);
        return output;
    }

    // src/script/interpreter.cpp Interpreter::eraseGlobal.
    public void EraseGlobal(string name)
    {
        string? key = mStrings.Lookup(name);
        if (key != null)
            mGlobals.Remove(key);
    }

    // src/script/interpreter.cpp Interpreter::eraseFunction.
    public void EraseFunction(string name)
    {
        string? key = mStrings.Lookup(name);
        if (key != null)
        {
            mFunctions.Remove(key);
            mFrames[0].Locals.Remove(key);
        }
    }

    // ---- running text ----

    // src/script/interpreter.cpp Interpreter::evaluate: parses and runs text; returns the string value of the
    // last expression statement run.
    public string Evaluate(string text, string fileName = "")
    {
        int nul = text.IndexOf('\0');
        string src = nul < 0 ? text : text[..nul];
        Numbers.EnterScriptFpuMode(); // pow can leave round-to-nearest behind; the original's next ftol resets it
        ParseResult r = ScriptParser.Parse(src);
        if (r.Error)
            Print(1, ScriptParser.SyntaxErrorMessage(r, fileName.Length == 0 ? "" : mStrings.Intern(fileName)));

        string saved = mLastValue;
        mLastValue = "";
        foreach (Node s in r.Statements)
            Exec(s); // top-level completion codes are ignored
        string result = mLastValue;
        mLastValue = saved;
        return result;
    }

    // src/script/interpreter.cpp Interpreter::execFile.
    public bool ExecFile(string fileName)
    {
        string name = fileName;
        if (!name.Contains(".cs", StringComparison.Ordinal) && !name.Contains(".mis", StringComparison.Ordinal) &&
            !name.Contains(".dsc", StringComparison.Ordinal) && !name.Contains(".mac", StringComparison.Ordinal))
            name += ".cs";
        ResourceManager.Entry? entry = mResources?.Find(name);
        byte[] data = Array.Empty<byte>();
        if (entry == null || !mResources!.Read(entry, out data))
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
        foreach (Node s in list)
        {
            Completion c = Exec(s);
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
            string v = EvalString(n.A!);
            mFrames[^1].Result = v;
            mLastValue = v;
            return Completion.Normal;
        }
        case NodeKind.If:
            return Truthy(EvalFloat(n.A!)) ? ExecList(n.Body) : ExecList(n.ElseBody);
        case NodeKind.While:
            while (Truthy(EvalFloat(n.A!)))
            {
                Completion c = ExecList(n.Body);
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
                Completion c = ExecList(n.Body);
                if (c == Completion.BreakLoop)
                    break;
                if (c == Completion.ReturnFn)
                    return c;
                EvalString(n.C!);
            }
            return Completion.Normal;
        case NodeKind.Function:
            mFunctions[mStrings.Intern(n.Name)] = new Function { Decl = n };
            return Completion.Normal;
        case NodeKind.Return:
            mFrames[^1].Result = n.A != null ? EvalString(n.A) : "";
            return Completion.ReturnFn;
        case NodeKind.Continue:
            return Completion.ContinueLoop;
        case NodeKind.BreakStmt:
            return Completion.BreakLoop;
        case NodeKind.TagDecl:
        {
            int id = Numbers.BorlandAtoi(EvalString(n.A!));
            string text = n.B != null ? EvalString(n.B) : "";
            string name = mStrings.Intern(n.Name);
            if (mTagText.ContainsKey(id))
            {
                Print(1, "Error: id " + Numbers.FormatInt(id) + " already defined to a tag.");
            }
            else if (mTagIds.TryGetValue(name, out int existing))
            {
                Print(1, "Error: define " + name + " already defined to tag " + Numbers.FormatInt(existing) + ".");
            }
            else
            {
                // Tag text lives in the string table, so it also fixes the spelling of later
                // case variants (fear.strings.cs "ARMOR" makes className = "Armor" read ARMOR).
                mTagText[id] = text.Length == 0 ? text : mStrings.Intern(text);
                mTagIds[name] = id;
            }
            return Completion.Normal;
        }
        case NodeKind.Datablock:
            mObjects?.BeginDataBlock(n.ClassName, n.Name);
            foreach (Node s in n.Slots)
                Exec(s);
            mObjects?.EndDataBlock();
            return Completion.Normal;
        case NodeKind.Instant:
        {
            string name = n.A != null ? EvalString(n.A) : "";
            mObjects?.BeginObject(n.ClassName, name);
            foreach (Node s in n.Slots)
                Exec(s);
            foreach (Node c in n.Children)
                Exec(c);
            mObjects?.EndObject();
            return Completion.Normal;
        }
        case NodeKind.Slot:
        {
            string index = IndexString(n.Index);
            var values = new List<string>();
            if (n.IsList)
                foreach (Node v in n.List)
                    values.Add(EvalString(v));
            else
                values.Add(EvalString(n.A!));
            mObjects?.SetSlot(n.Name, values, index);
            return Completion.Normal;
        }
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
        case NodeKind.Float:
            return StaticType.TFloat;
        case NodeKind.Int:
            return StaticType.TInt;
        case NodeKind.Unary:
            return n.OpChar == '-' ? StaticType.TFloat : n.OpChar == '*' ? StaticType.TString : StaticType.TInt;
        case NodeKind.Binary:
            if (n.Op == Tok.Char)
            {
                switch (n.OpChar)
                {
                case '+': case '-': case '*': case '/': return StaticType.TFloat;
                case '@': return StaticType.TString;
                default: return StaticType.TInt;
                }
            }
            return n.Op == Tok.DollarPlus ? StaticType.TString : StaticType.TInt;
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
            uint d = unchecked((uint)EvalInt(l) - (uint)EvalInt(r));
            return Sign(unchecked((int)d));
        }
        if (tl != StaticType.TString || tr != StaticType.TString)
        {
            double a = EvalFloat(l);
            double b = EvalFloat(r);
            return Sign(Fpu.Sub(a, b));
        }
        string sa = EvalString(l), sb = EvalString(r);
        float fa = 0, fb = 0;
        bool boolMode = false;
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
            int c = CString.Strcmp(sa, sb);
            return c < 0 ? -1 : c > 0 ? 1 : 0;
        }
        if (boolMode)
            return (fa != 0) != (fb != 0) ? 1 : 0;
        float fd = fa - fb;
        return fd < 0 ? -1 : fd > 0 ? 1 : 0;
    }

    // src/script/interpreter.cpp Interpreter::evalFloat.
    private double EvalFloat(Node n)
    {
        switch (n.Kind)
        {
        case NodeKind.Float:
            return Numbers.BorlandAtof(n.Name);
        case NodeKind.Int:
            return Numbers.ParseIntLiteral(n.Name);
        case NodeKind.Unary:
            if (n.OpChar == '-')
                return -EvalFloat(n.A!);
            break;
        case NodeKind.Binary:
            if (n.Op == Tok.Char && (n.OpChar == '+' || n.OpChar == '-' || n.OpChar == '*' || n.OpChar == '/'))
            {
                double l = EvalFloat(n.A!); // left operand is stored to a 64-bit double
                double r = EvalFloat(n.B!);
                switch (n.OpChar)
                {
                case '+': return Fpu.Add(l, r);
                case '-': return Fpu.Sub(l, r);
                case '*': return Fpu.Mul(l, r);
                default: return Fpu.Div(l, r);
                }
            }
            break;
        default:
            break;
        }
        if (StaticTypeOf(n) == StaticType.TInt)
            return EvalInt(n);
        string s = EvalString(n);
        return Numbers.IsTrueString(s) ? 1.0 : Numbers.BorlandAtof(s);
    }

    // src/script/interpreter.cpp Interpreter::evalInt.
    private int EvalInt(Node n)
    {
        switch (n.Kind)
        {
        case NodeKind.Int:
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
            case Tok.Eq: c = Compare(n.A!, n.B!); return c == 0 || c == 2 ? 1 : 0;
            case Tok.Ne: c = Compare(n.A!, n.B!); return c == -1 || c == 1 ? 1 : 0;
            case Tok.Le: c = Compare(n.A!, n.B!); return c != 1 ? 1 : 0;
            case Tok.Ge: c = Compare(n.A!, n.B!); return c == 0 || c == 1 ? 1 : 0;
            case Tok.Shl: c = EvalInt(n.A!); return unchecked((int)((uint)c << (EvalInt(n.B!) & 31)));
            case Tok.Shr: c = EvalInt(n.A!); return c >> (EvalInt(n.B!) & 31);
            case Tok.Char:
                switch (n.OpChar)
                {
                case '<': c = Compare(n.A!, n.B!); return c == -1 || c == 2 ? 1 : 0;
                case '>': return Compare(n.A!, n.B!) == 1 ? 1 : 0;
                case '%':
                {
                    int r = EvalInt(n.B!); // right first; left skipped when right is 0
                    if (r == 0)
                        return 0;
                    int l = EvalInt(n.A!);
                    if (r == -1)
                        return 0; // INT_MIN % -1 traps on x86 (D11); every other x % -1 is 0
                    return l % r;
                }
                case '&': c = EvalInt(n.A!); return c & EvalInt(n.B!);
                case '|': c = EvalInt(n.A!); return c | EvalInt(n.B!);
                case '^': c = EvalInt(n.A!); return c ^ EvalInt(n.B!);
                default: break;
                }
                break;
            default:
                break;
            }
            break;
        }
        default:
            break;
        }
        if (StaticTypeOf(n) == StaticType.TFloat)
            return Numbers.FloatToInt(EvalFloat(n));
        string s = EvalString(n);
        return Numbers.IsTrueString(s) ? 1 : Numbers.BorlandAtoi(s);
    }

    // src/script/interpreter.cpp Interpreter::readLvalue.
    private string ReadLvalue(Node lv)
    {
        if (lv.Kind == NodeKind.Var)
            return GetVariable(lv.Name + IndexString(lv.Index));
        string obj = EvalString(lv.A!);
        if (mObjects == null || !mObjects.GetField(obj, lv.Name, IndexString(lv.Index), out string output))
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
        string obj = EvalString(lv.A!);
        mObjects?.SetField(obj, lv.Name, new[] { value }, IndexString(lv.Index));
    }

    // src/script/interpreter.cpp Interpreter::compoundAssign.
    private string CompoundAssign(Node lv, Tok op, char opChar, Node? rhs)
    {
        string current = ReadLvalue(lv);
        string result;
        bool floatPath = opChar == '+' || opChar == '-' || op == Tok.PlusAssign || op == Tok.MinusAssign ||
                         op == Tok.MulAssign || op == Tok.DivAssign;
        if (floatPath)
        {
            double l = Numbers.IsTrueString(current) ? 1.0 : Numbers.BorlandAtof(current);
            double r = rhs != null ? EvalFloat(rhs) : 1.0;
            double v;
            if (op == Tok.PlusAssign || opChar == '+')
                v = Fpu.Add(l, r);
            else if (op == Tok.MinusAssign || opChar == '-')
                v = Fpu.Sub(l, r);
            else if (op == Tok.MulAssign)
                v = Fpu.Mul(l, r);
            else
                v = Fpu.Div(l, r);
            result = mLastHope ? Numbers.FormatFloat15(v) : Numbers.FormatFloat(v);
        }
        else
        {
            int l = Numbers.IsTrueString(current) ? 1 : Numbers.BorlandAtoi(current);
            int r = EvalInt(rhs!);
            int v;
            switch (op)
            {
            case Tok.ModAssign: v = (r == 0 || r == -1) ? 0 : l % r; break;
            case Tok.AndAssign: v = l & r; break;
            case Tok.OrAssign: v = l | r; break;
            case Tok.XorAssign: v = l ^ r; break;
            case Tok.ShlAssign: v = unchecked((int)((uint)l << (r & 31))); break;
            default: v = l >> (r & 31); break;
            }
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
        case NodeKind.Int:
            return Numbers.FormatInt(Numbers.ParseIntLiteral(n.Name));
        case NodeKind.Float:
            return Numbers.FormatFloat(Numbers.BorlandAtof(n.Name));
        case NodeKind.Ident: // already the interned spelling (lexer, script.md 1.2)
        case NodeKind.String:
        case NodeKind.BreakWord:
            return n.Name;
        case NodeKind.TagRef:
            return Numbers.FormatInt(mTagIds.TryGetValue(mStrings.Intern(n.Name), out int tagId) ? tagId : 0);
        case NodeKind.Var:
        case NodeKind.Field:
            return ReadLvalue(n);
        case NodeKind.Call:
        {
            var args = new List<string>();
            foreach (Node a in n.List)
                args.Add(EvalString(a));
            return Call(n.Name, args);
        }
        case NodeKind.MethodCall:
        {
            var args = new List<string> { EvalString(n.A!) };
            foreach (Node a in n.List)
                args.Add(EvalString(a));
            return Call("dot_op_" + n.Name, args);
        }
        case NodeKind.Unary:
            if (n.OpChar == '*')
                return mTagText.TryGetValue(EvalInt(n.A!), out string? text) ? text : "<INVALID TAG>";
            break;
        case NodeKind.Binary:
            if (n.OpChar == '@' || n.Op == Tok.DollarPlus)
            {
                string l = EvalString(n.A!);
                return l + EvalString(n.B!);
            }
            break;
        case NodeKind.Assign:
        {
            string v = EvalString(n.B!);
            WriteLvalue(n.A!, v);
            return v;
        }
        case NodeKind.FieldListAssign:
        {
            string obj = EvalString(n.A!.A!);
            var values = new List<string>();
            foreach (Node v in n.List)
                values.Add(EvalString(v));
            mObjects?.SetField(obj, n.A.Name, values, IndexString(n.A.Index));
            return values.Count == 0 ? "" : values[^1];
        }
        case NodeKind.CompoundAssign:
            return CompoundAssign(n.A!, n.Op, '\0', n.B);
        case NodeKind.PostIncDec:
            return CompoundAssign(n.A!, Tok.End, n.OpChar, null);
        default:
            break;
        }
        if (StaticTypeOf(n) == StaticType.TFloat)
        {
            // 1.30 changed the format of FloatBinaryExprNode only; literals and negation keep %g.
            if (mLastHope && n.Kind == NodeKind.Binary)
                return Numbers.FormatFloat15(EvalFloat(n));
            return Numbers.FormatFloat(EvalFloat(n));
        }
        return Numbers.FormatInt(EvalInt(n));
    }

    // src/script/interpreter.cpp Interpreter::tagName: name of the tag defined with this id, or "" (0x005ee788).
    public string TagName(int id)
    {
        foreach (var kv in mTagIds)
            if (kv.Value == id)
                return kv.Key;
        return "";
    }

    // ---- calls ----

    // src/script/interpreter.cpp Interpreter::hasFunction.
    public bool HasFunction(string name)
    {
        string? key = mStrings.Lookup(name);
        return key != null && mFunctions.ContainsKey(key);
    }

    // src/script/interpreter.cpp Interpreter::functionParamCount: -1 for a built-in or an unknown name.
    public int FunctionParamCount(string name)
    {
        string? key = mStrings.Lookup(name);
        if (key == null)
            return -1;
        return !mFunctions.TryGetValue(key, out Function? f) || f.Decl == null ? -1 : f.Decl.Params.Count;
    }

    // src/script/interpreter.cpp Interpreter::callFunction.
    public string CallFunction(string name, IReadOnlyList<string> args) => Call(name, new List<string>(args));

    // src/script/interpreter.cpp Interpreter::call.
    private string Call(string name, List<string> args)
    {
        string key = mStrings.Intern(name);
        if (!mFunctions.TryGetValue(key, out Function? fn))
        {
            mUnknown[key] = mUnknown.TryGetValue(key, out int count) ? count + 1 : 1;
            Print(1, key + ": Unknown command.");
            return "False";
        }
        if (fn.Builtin != null)
        {
            args.Insert(0, key);
            Command cmd = fn.Builtin; // the command may redefine itself
            return cmd(this, args.ToArray()) ?? "False";
        }
        Node decl = fn.Decl!;
        mFrames.Add(new Frame());
        for (int i = 0; i < decl.Params.Count && i < args.Count; ++i)
            SetVariable(decl.Params[i], args[i]);
        ExecList(decl.Body);
        string result = mFrames[^1].Result;
        mFrames.RemoveAt(mFrames.Count - 1);
        return result;
    }
}

// Port convention (no C++ file): the C string functions the C++ applies to std::string::c_str(), which stop
// at the first NUL and compare bytes unsigned.
public static class CString
{
    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // strcmp
    public static int Strcmp(string a, string b) => Strncmp(a, b, int.MaxValue);

    // strncmp
    public static int Strncmp(string a, string b, int n)
    {
        for (int i = 0; i < n; ++i)
        {
            char ca = At(a, i), cb = At(b, i);
            if (ca != cb)
                return ca < cb ? -1 : 1;
            if (ca == '\0')
                return 0;
        }
        return 0;
    }

    // The text of s.c_str(): s up to its first NUL.
    public static string CStr(string s)
    {
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }
}
