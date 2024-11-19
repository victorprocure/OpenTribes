// Port of src/script/builtins.hpp and src/script/builtins.cpp: built-in commands that do not need the
// simulation: console core (0x005f3a7c), File/String utilities (0x004018c8), getWord. Behavior notes:
// docs/script.md 5. Game paths are byte strings; rootDir is a host path.
using System.Text;
using OpenTribes.Core;

namespace OpenTribes.Script;

public static class Builtins
{
    private static char LowerAscii(char c) => (c >= 'A' && c <= 'Z') ? (char)(c + 0x20) : c;

    // src/script/builtins.cpp lessIgnoreCase, as a three-way comparison.
    private static int CompareIgnoreCase(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; ++i)
        {
            char x = LowerAscii(a[i]), y = LowerAscii(b[i]);
            if (x != y)
                return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    // src/script/builtins.cpp stricmpAscii (0x00488150): upper-cases a-z and returns the difference of the first mismatch.
    internal static int StricmpAscii(string a, string b)
    {
        for (int i = 0; ; ++i)
        {
            char ca = i < a.Length ? a[i] : '\0';
            char cb = i < b.Length ? b[i] : '\0';
            if (ca > '`' && ca < '{')
                ca = (char)(ca - 0x20);
            if (cb > '`' && cb < '{')
                cb = (char)(cb - 0x20);
            if (ca != cb || cb == '\0')
                return (sbyte)(byte)ca - (sbyte)(byte)cb;
        }
    }

    private static int Sign(int v) => v < 0 ? -1 : v > 0 ? 1 : 0;

    // src/script/builtins.cpp writeable: directories file commands may write to (ResManager 0x00587610,
    // docs/resources.md 8.1).
    private static bool Writeable(string path)
    {
        string[] kDirs = { "recordings", "temp", "config", "base\\missions", "base\\recordings", "base\\gui" };
        string p = path.Replace('/', '\\');
        int slash = p.LastIndexOf('\\');
        string dir = slash < 0 ? "" : p[..slash];
        foreach (string d in kDirs)
            if (StricmpAscii(dir, d) == 0)
                return true;
        return false;
    }

    // src/script/builtins.cpp hostPath: resolves a game path below root, matching each component
    // case-insensitively like Windows.
    private static string HostPath(string root, string path)
    {
        path = path.Replace('\\', '/');
        string output = root;
        int start = 0;
        while (start <= path.Length)
        {
            int end = path.IndexOf('/', start);
            string part = end < 0 ? path[start..] : path[start..end];
            start = end < 0 ? path.Length + 1 : end + 1;
            if (part.Length == 0)
                continue;
            string next = Path.Join(output, ByteString.ToHost(part));
            if (!File.Exists(next) && !Directory.Exists(next) && Directory.Exists(output))
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(output))
                        if (StricmpAscii(ByteString.FromHost(Path.GetFileName(entry)), part) == 0)
                        {
                            next = entry;
                            break;
                        }
                }
                catch (Exception)
                {
                    // std::error_code: an unreadable directory ends the search.
                }
            }
            output = next;
        }
        return output;
    }

    // src/script/builtins.cpp struct PathParts: path split used by File::getExt/getBase/getTitle/getPath (0x00583aac).
    private sealed class PathParts
    {
        public string Path = "", Title = "", Base = "", Ext = "";
    }
    private static PathParts SplitPath(string s)
    {
        var p = new PathParts();
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        p.Path = slash < 0 ? "" : s[..(slash + 1)];
        p.Title = slash < 0 ? s : s[(slash + 1)..];
        int dot = p.Title.LastIndexOf('.');
        p.Base = dot < 0 ? p.Title : p.Title[..dot];
        // The extension is searched in the whole string: "dir.d\file" has extension "d\file".
        int anyDot = s.LastIndexOf('.');
        p.Ext = anyDot < 0 ? "" : s[(anyDot + 1)..];
        return p;
    }

    // src/script/builtins.cpp emit.
    private static void Emit(Interpreter interp, List<string> lines, string[] argv, string root)
    {
        if (argv.Length >= 3)
        {
            if (!Writeable(argv[2]))
                return;
            bool append = argv.Length >= 4 && (Numbers.IsTrueString(argv[3]) || Numbers.BorlandAtoi(argv[3]) != 0);
            try
            {
                using var f = new FileStream(HostPath(root, argv[2]), append ? FileMode.Append : FileMode.Create, FileAccess.Write);
                foreach (string l in lines)
                    f.Write(ByteString.ToBytes(l + "\r\n"));
            }
            catch (Exception)
            {
                // An ofstream that cannot open writes nothing.
            }
            return;
        }
        foreach (string l in lines)
            interp.Print(1, l);
    }

    // src/script/builtins.cpp resolveGamePath: host path of a game path below rootDir; components match
    // case-insensitively and either slash separates. Empty when the path is absolute, names a drive or contains "..".
    public static string ResolveGamePath(string rootDir, string path)
    {
        string p = path.Replace('\\', '/');
        if (p.Length == 0 || p[0] == '/' || p.Contains(':'))
            return "";
        for (int start = 0; start <= p.Length;)
        {
            int end = p.IndexOf('/', start);
            if (end < 0)
                end = p.Length;
            if (p[start..end] == "..")
                return "";
            start = end + 1;
        }
        return HostPath(rootDir, path);
    }

    // src/script/builtins.cpp isWriteablePath: file write check of the resource manager (0x00587528).
    public static bool IsWriteablePath(string path) => Writeable(path);

    // src/script/builtins.cpp hostFilePath: host path of a game path below rootDir.
    public static string HostFilePath(string rootDir, string path) => HostPath(rootDir, path);

    // src/script/builtins.cpp applyLastHopeMode: switches the interpreter to the LastHope 1.30 command set
    // (docs/lasthope-1.30.md L1, L5, L8, L9).
    public static void ApplyLastHopeMode(Interpreter interp)
    {
        interp.SetLastHope(true);
        foreach (string gone in new[] { "debug", "trace", "dbecho" })
            interp.EraseFunction(gone);
        interp.AddCommand("version", (i, a) => "1.30");
        // L6: timeStamp() is the local wall clock, "YYYY/MM/DD HH:MM:SS.mmm" [live 1.30].
        interp.AddCommand("timeStamp", (i, a) =>
        {
            DateTime t = DateTime.Now;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:D4}/{1:D2}/{2:D2} {3:D2}:{4:D2}:{5:D2}.{6:D3}",
                                 t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Millisecond);
        });
        // 0x0060b316: plays a wave through the sound manager and returns "true". A dedicated
        // server has none and the original crashes there [live]; rejected per D11.
        interp.AddCommand("sound", (i, a) => "true");
    }

    // src/script/builtins.cpp exportEscape: escaping used by export (0x005f3744): \t \n \r \\ \" and \xHH for
    // other control and high bytes, with the original's hex digit bug (10-15 print as K-P).
    public static string ExportEscape(string value)
    {
        var output = new StringBuilder();
        foreach (char c in value)
        {
            if (c == '\t')
                output.Append("\\t");
            else if (c == '\n')
                output.Append("\\n");
            else if (c == '\r')
                output.Append("\\r");
            else if (c == '\\')
                output.Append("\\\\");
            else if (c == '"')
                output.Append("\\\"");
            else if (c < 0x20 || c >= 0x80)
            {
                static char Digit(int d) => (char)(d < 10 ? '0' + d : 'A' + d);
                output.Append("\\x");
                output.Append(Digit(c >> 4));
                output.Append(Digit(c & 15));
            }
            else
                output.Append(c);
        }
        return output.ToString();
    }

    // src/script/builtins.cpp registerBuiltins. rootDir: game directory for file commands.
    public static void RegisterBuiltins(Interpreter interp, ResourceManager? res, string rootDir)
    {
        // Client-side commands exist on a dedicated server too (docs/mods.md 5).
        ClientCommands.RegisterClientCommands(interp, res, rootDir);
        MathCommands.RegisterMathCommands(interp, res);
        // ---- console core ----
        interp.AddCommand("export", (i, argv) =>
        {
            if (argv.Length < 2 || argv.Length > 4)
            {
                i.Print(1, "export(searchString, [fileName], [append]);");
                return "False";
            }
            string pattern = argv[1];
            if (pattern.Length == 0 || pattern[0] != '$')
                pattern = "$" + pattern;
            var vars = i.Globals();
            vars.Sort((x, y) => CompareIgnoreCase(x.Key, y.Key));
            var lines = new List<string>();
            foreach (var kv in vars)
                if (ResourceManager.WildcardMatch(CString.CStr(pattern), kv.Key))
                    lines.Add(kv.Key + " = \"" + ExportEscape(kv.Value) + "\";");
            Emit(i, lines, argv, rootDir);
            return "True";
        });
        interp.AddCommand("exportFunctions", (i, argv) =>
        {
            if (argv.Length < 2 || argv.Length > 4)
            {
                i.Print(1, "exportFunctions(searchString, [fileName], [append]);");
                return "False";
            }
            var names = i.FunctionNames();
            names.Sort(CompareIgnoreCase);
            var lines = new List<string>();
            foreach (string n in names)
                if (ResourceManager.WildcardMatch(CString.CStr(argv[1]), n))
                    lines.Add(n + "();");
            Emit(i, lines, argv, rootDir);
            return "True";
        });
        interp.AddCommand("deleteVariables", (i, argv) =>
        {
            if (argv.Length != 2)
            {
                i.Print(1, "deleteVariables(searchString);");
                return "False";
            }
            string pattern = argv[1];
            if (pattern.Length == 0 || pattern[0] != '$')
                pattern = "$" + pattern;
            foreach (var kv in i.Globals())
                if (ResourceManager.WildcardMatch(CString.CStr(pattern), kv.Key))
                    i.EraseGlobal(kv.Key);
            return "True";
        });
        interp.AddCommand("deleteFunctions", (i, argv) =>
        {
            if (argv.Length != 2)
            {
                i.Print(1, "deleteFunctions(searchString);");
                return "False";
            }
            foreach (string n in i.FunctionNames())
                if (ResourceManager.WildcardMatch(CString.CStr(argv[1]), n))
                    i.EraseFunction(n);
            return "True";
        });
        interp.AddCommand("cls", (i, argv) => "True");
        interp.AddCommand("debug", (i, argv) => "True");
        interp.AddCommand("trace", (i, argv) =>
        {
            i.Print(1, "TRACE: ");
            return "True";
        });

        // isFile (sim plugin id 106): real files relative to the game directory, all must exist.
        interp.AddCommand("isFile", (i, argv) =>
        {
            if (argv.Length < 2)
            {
                i.Print(1, "isFile: fileName [fileName] ...");
                return "False";
            }
            for (int k = 1; k < argv.Length; ++k)
                if (!File.Exists(HostPath(rootDir, argv[k])))
                    return "False";
            return "True";
        });

        interp.AddCommand("escapeString", (i, argv) => argv.Length == 2 ? ExportEscape(argv[1]) : "");
        interp.AddCommand("version", (i, argv) => "1.11"); // the build's version string (docs/lasthope-1.30.md L1 for 1.30)

        // ---- getWord (game plugin, 0x0041c9f4) ----
        interp.AddCommand("getWord", (i, argv) =>
        {
            if (argv.Length != 3)
            {
                i.Print(1, "wrong argcount to getWord(String,nWord)");
                return "-1";
            }
            int want = Numbers.BorlandAtoi(argv[2]);
            string s = argv[1];
            int p = 0;
            for (int word = 0; p < s.Length; ++word)
            {
                while (p < s.Length && s[p] == ' ')
                    ++p;
                if (p >= s.Length)
                    break;
                int start = p;
                while (p < s.Length && s[p] != ' ')
                    ++p;
                if (word == want)
                    return s[start..p];
            }
            return "-1";
        });

        // ---- String:: ----
        interp.AddCommand("String::findSubStr", (i, argv) =>
        {
            if (argv.Length != 3)
                return "-1";
            string a = Lower(argv[1]), b = Lower(argv[2]);
            int pos = a.IndexOf(b, StringComparison.Ordinal);
            return pos < 0 ? "-1" : Numbers.FormatInt(pos);
        });
        interp.AddCommand("String::getSubStr", (i, argv) =>
        {
            if (argv.Length != 4)
                return "";
            int start = Numbers.BorlandAtoi(argv[2]);
            int len = Numbers.BorlandAtoi(argv[3]);
            if (start < 0 || len < 1 || argv[1].Length < start)
                return "";
            return argv[1].Substring(start, Math.Min(Math.Min(len, 255), argv[1].Length - start));
        });
        interp.AddCommand("String::Compare", (i, argv) =>
            argv.Length == 3 ? Numbers.FormatInt(Sign(CString.Strcmp(argv[1], argv[2]))) : "");
        interp.AddCommand("String::NCompare", (i, argv) =>
            argv.Length == 4 ? Numbers.FormatInt(Sign(CString.Strncmp(argv[1], argv[2], Math.Max(0, Numbers.BorlandAtoi(argv[3]))))) : "");
        interp.AddCommand("String::ICompare", (i, argv) =>
            argv.Length == 3 ? Numbers.FormatInt(StricmpAscii(argv[1], argv[2])) : "");
        interp.AddCommand("String::empty", (i, argv) =>
        {
            if (argv.Length != 2)
                return "";
            return argv[1].Trim(' ').Length == 0 ? "true" : "false";
        });
        interp.AddCommand("String::convertSpaces", (i, argv) => (argv.Length == 2 ? argv[1] : "").Replace(' ', '_'));

        // ---- File:: ----
        void PathCommand(string name, Func<PathParts, string> part)
        {
            interp.AddCommand(name, (i, argv) => argv.Length == 2 ? part(SplitPath(argv[1])) : "");
        }
        PathCommand("File::getExt", p => p.Ext);
        PathCommand("File::getBase", p => p.Base);
        PathCommand("File::getTitle", p => p.Title);
        PathCommand("File::getPath", p => p.Path);

        interp.AddCommand("File::copy", (i, argv) =>
        {
            if (argv.Length != 3)
                return "FALSE";
            if (!File.Exists(HostPath(rootDir, argv[1])))
                return "FALSE";
            if (!Writeable(argv[2]))
                return "False";
            try
            {
                File.Copy(HostPath(rootDir, argv[1]), HostPath(rootDir, argv[2]), true);
                return "TRUE";
            }
            catch (Exception)
            {
                return "FALSE";
            }
        });
        interp.AddCommand("File::delete", (i, argv) =>
        {
            if (argv.Length != 2 || !Writeable(argv[1]))
                return "FALSE";
            string host = HostPath(rootDir, argv[1]);
            try
            {
                // std::filesystem::remove: a file or an empty directory; false when nothing was there.
                if (File.Exists(host))
                {
                    File.Delete(host);
                    return "TRUE";
                }
                if (Directory.Exists(host) && !Directory.EnumerateFileSystemEntries(host).Any())
                {
                    Directory.Delete(host);
                    return "TRUE";
                }
            }
            catch (Exception)
            {
            }
            return "FALSE";
        });

        // One cursor shared by findFirst/findNext, like the original's global (0x0060ea78).
        ResourceManager.Entry? cursor = null;
        interp.AddCommand("File::findFirst", (i, argv) =>
        {
            if (argv.Length != 2 || res == null)
                return "";
            cursor = res.FindNext(argv[1], null, out string r);
            return cursor == null ? "" : r;
        });
        interp.AddCommand("File::findNext", (i, argv) =>
        {
            if (argv.Length != 2 || res == null || cursor == null)
                return "";
            cursor = res.FindNext(argv[1], cursor, out string r);
            return cursor == null ? "" : r;
        });
    }

    private static string Lower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(LowerAscii(c));
        return sb.ToString();
    }
}
