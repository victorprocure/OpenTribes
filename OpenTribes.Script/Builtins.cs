namespace OpenTribes.Script;

using System.Text;
using OpenTribes.Core;

/// <summary>Built-in script commands that do not need the simulation: the console core (005f3a7c), the File and String utilities (004018c8) and getWord.</summary>
/// <remarks>Port of src/script/builtins.cpp. Behaviour notes are in docs/script.md 5. Game paths are byte strings and the root directory is a host path.</remarks>
public static class Builtins
{
    private static char LowerAscii(char c) => (c is >= 'A' and <= 'Z') ? (char)(c + 0x20) : c;

    // src/script/builtins.cpp lessIgnoreCase, as a three-way comparison.
    private static int CompareIgnoreCase(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; ++i)
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
        for (var i = 0; ; ++i)
        {
            var ca = i < a.Length ? a[i] : '\0';
            var cb = i < b.Length ? b[i] : '\0';
            if (ca is > '`' and < '{')
                ca = (char)(ca - 0x20);
            if (cb is > '`' and < '{')
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
        string[] kDirs = ["recordings", "temp", "config", "base\\missions", "base\\recordings", "base\\gui"];
        var p = path.Replace('/', '\\');
        var slash = p.LastIndexOf('\\');
        var dir = slash < 0 ? "" : p[..slash];
        foreach (var d in kDirs)
            if (StricmpAscii(dir, d) == 0)
                return true;
        return false;
    }

    // src/script/builtins.cpp hostPath: resolves a game path below root, matching each component
    // case-insensitively like Windows.
    private static string HostPath(string root, string path)
    {
        path = path.Replace('\\', '/');
        var output = root;
        var start = 0;
        while (start <= path.Length)
        {
            var end = path.IndexOf('/', start);
            var part = end < 0 ? path[start..] : path[start..end];
            start = end < 0 ? path.Length + 1 : end + 1;
            if (part.Length == 0)
                continue;
            var next = Path.Join(output, ByteString.ToHost(part));
            if (!File.Exists(next) && !Directory.Exists(next) && Directory.Exists(output))
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(output))
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
        var slash = s.LastIndexOfAny(['\\', '/']);
        p.Path = slash < 0 ? "" : s[..(slash + 1)];
        p.Title = slash < 0 ? s : s[(slash + 1)..];
        var dot = p.Title.LastIndexOf('.');
        p.Base = dot < 0 ? p.Title : p.Title[..dot];
        // The extension is searched in the whole string: "dir.d\file" has extension "d\file".
        var anyDot = s.LastIndexOf('.');
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
            var append = argv.Length >= 4 && (Numbers.IsTrueString(argv[3]) || Numbers.BorlandAtoi(argv[3]) != 0);
            try
            {
                using var f = new FileStream(HostPath(root, argv[2]), append ? FileMode.Append : FileMode.Create, FileAccess.Write);
                foreach (var l in lines)
                    f.Write(ByteString.ToBytes(l + "\r\n"));
            }
            catch (Exception)
            {
                // An ofstream that cannot open writes nothing.
            }
            return;
        }
        foreach (var l in lines)
            interp.Print(1, l);
    }

    /// <summary>Resolves a game path below a root directory to a host path.</summary>
    /// <param name="rootDir">The game directory.</param>
    /// <param name="path">The game path. Components match case-insensitively and either slash separates.</param>
    /// <returns>The host path, empty when the path is absolute, names a drive or contains "..".</returns>
    public static string ResolveGamePath(string rootDir, string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Length == 0 || p[0] == '/' || p.Contains(':'))
            return "";
        for (var start = 0; start <= p.Length;)
        {
            var end = p.IndexOf('/', start);
            if (end < 0)
                end = p.Length;
            if (p[start..end] == "..")
                return "";
            start = end + 1;
        }
        return HostPath(rootDir, path);
    }

    /// <summary>Tests whether a file write is allowed (resource manager, 00587528).</summary>
    /// <param name="path">The game path.</param>
    /// <returns>True when the path is writeable.</returns>
    public static bool IsWriteablePath(string path) => Writeable(path);

    /// <summary>Resolves a game path below a root directory to a host file path.</summary>
    /// <param name="rootDir">The game directory.</param>
    /// <param name="path">The game path.</param>
    /// <returns>The host path.</returns>
    public static string HostFilePath(string rootDir, string path) => HostPath(rootDir, path);

    /// <summary>Switches the interpreter to the LastHope 1.30 command set (docs/lasthope-1.30.md L1, L5, L8, L9).</summary>
    /// <param name="interp">The interpreter.</param>
    public static void ApplyLastHopeMode(Interpreter interp)
    {
        interp.SetLastHope(true);
        foreach (var gone in new[] { "debug", "trace", "dbecho" })
            interp.EraseFunction(gone);
        interp.AddCommand("version", (i, a) => "1.30");
        // L6: timeStamp() is the local wall clock, "YYYY/MM/DD HH:MM:SS.mmm" [live 1.30].
        interp.AddCommand("timeStamp", (i, a) =>
        {
            var t = DateTime.Now;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:D4}/{1:D2}/{2:D2} {3:D2}:{4:D2}:{5:D2}.{6:D3}",
                                 t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Millisecond);
        });
        // 0x0060b316: plays a wave through the sound manager and returns "true". A dedicated
        // server has none and the original crashes there [live]; rejected per D11.
        interp.AddCommand("sound", (i, a) => "true");
    }

    /// <summary>Escapes a string the way export does (005f3744).</summary>
    /// <param name="value">The string.</param>
    /// <returns>The string with \t \n \r \\ \" and \xHH for other control and high bytes.</returns>
    /// <remarks>Keeps the original's hex digit bug: the digits 10 to 15 print as K to P.</remarks>
    public static string ExportEscape(string value)
    {
        var output = new StringBuilder();
        foreach (var c in value)
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
            else if (c is < (char)0x20 or >= (char)0x80)
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

    /// <summary>Registers the built-in commands.</summary>
    /// <param name="interp">The interpreter.</param>
    /// <param name="res">The resource manager, or null.</param>
    /// <param name="rootDir">The game directory for file commands.</param>
    /// <returns>The client state the commands share.</returns>
    public static ClientState RegisterBuiltins(Interpreter interp, ResourceManager? res, string rootDir)
    {
        // Client-side commands exist on a dedicated server too (docs/mods.md 5).
        var clientState = ClientCommands.RegisterClientCommands(interp, res, rootDir);
        MathCommands.RegisterMathCommands(interp, res);
        // ---- console core ----
        interp.AddCommand("export", (i, argv) =>
        {
            if (argv.Length is < 2 or > 4)
            {
                i.Print(1, "export(searchString, [fileName], [append]);");
                return "False";
            }
            var pattern = argv[1];
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
            if (argv.Length is < 2 or > 4)
            {
                i.Print(1, "exportFunctions(searchString, [fileName], [append]);");
                return "False";
            }
            var names = i.FunctionNames();
            names.Sort(CompareIgnoreCase);
            var lines = new List<string>();
            foreach (var n in names)
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
            var pattern = argv[1];
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
            foreach (var n in i.FunctionNames())
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
            for (var k = 1; k < argv.Length; ++k)
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
            var want = Numbers.BorlandAtoi(argv[2]);
            var s = argv[1];
            var p = 0;
            for (var word = 0; p < s.Length; ++word)
            {
                while (p < s.Length && s[p] == ' ')
                    ++p;
                if (p >= s.Length)
                    break;
                var start = p;
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
            var pos = a.IndexOf(b, StringComparison.Ordinal);
            return pos < 0 ? "-1" : Numbers.FormatInt(pos);
        });
        interp.AddCommand("String::getSubStr", (i, argv) =>
        {
            if (argv.Length != 4)
                return "";
            var start = Numbers.BorlandAtoi(argv[2]);
            var len = Numbers.BorlandAtoi(argv[3]);
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
        void PathCommand(string name, Func<PathParts, string> part) => interp.AddCommand(name, (i, argv) => argv.Length == 2 ? part(SplitPath(argv[1])) : "");
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
            var host = HostPath(rootDir, argv[1]);
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
            cursor = res.FindNext(argv[1], null, out var r);
            return cursor == null ? "" : r;
        });
        interp.AddCommand("File::findNext", (i, argv) =>
        {
            if (argv.Length != 2 || res == null)
                return "";
            // A null cursor (before any findFirst, or after the end of a search) starts at the first entry [live].
            cursor = res.FindNext(argv[1], cursor, out var r);
            return cursor == null ? "" : r;
        });
        return clientState;
    }

    private static string Lower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(LowerAscii(c));
        return sb.ToString();
    }
}
