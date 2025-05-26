// Port of tests/test_lasthope.cpp: LastHope 1.30 script mode (docs/lasthope-1.30.md L1, L5, L8, L9, L15). Vectors
// confirmed on a 1.30 server (reference instance 8, tools/reftest.py lasthope).
using OpenTribes.Script;
using OpenTribes.Server;

namespace OpenTribes.Tests;

public class LastHopeTests
{
    private static readonly string sTmp = TestPaths.TmpDir("lasthope");

    [Fact]
    public void LastHope()
    {
        var interp = new Interpreter();
        string output = "";
        interp.SetOutput(line => output += (output.Length == 0 ? "" : "\n") + line);
        Builtins.RegisterBuiltins(interp, null, sTmp);
        string Run(string line)
        {
            output = "";
            interp.Evaluate(line);
            return output;
        }
        // Stock 1.11 first: the same lines before the switch.
        Assert.Equal("1.11 0.333333 0.299999", Run("echo(version(), \" \", 1/3, \" \", 0.1 + 0.2);"));
        Assert.Equal("TRACE: \nx", Run("debug(); trace(); dbecho(1, \"x\");"));

        Builtins.ApplyLastHopeMode(interp);
        var cases = new (string line, string want)[]
        {
            ("echo(version());", "1.30"),
            // L5: only binary float arithmetic prints 15 digits (still truncated toward zero).
            ("echo(1/3);", "0.333333333333333"),
            ("echo(0.1 + 0.2);", "0.299999999999999"),
            ("echo(1.5 * 2, \" \", 10/4, \" \", 7 - 0.1);", "3 2.5 6.89999999999999"),
            ("echo(1e20 * 10, \" \", 1/0);", "1e+21 +INF"),
            // The variable holds the %g text "0.0999999", so the sum starts from that.
            ("$a = 0.1; $a += 0.2; echo($a);", "0.299999899999999"),
            ("$b = 0.1; $b++; echo($b);", "1.09999989999999"),
            // Negation, literals and functions keep %g.
            ("echo(-(1/3), \" \", 0.5, \" \", sqrt(2));", "-0.333333 0.5 1.41421"),
            ("echo(3 % 2, \" \", 1 < 2.5, \" \", 1 + 2);", "1 1 3"),
            // L9 and L8: debug, trace and dbecho are gone; sound exists and returns "true".
            ("debug();", "debug: Unknown command."),
            ("trace();", "trace: Unknown command."),
            ("dbecho(1, \"x\");", "dbecho: Unknown command."),
            ("echo(sound(), sound(\"a.wav\"));", "truetrue"),
        };
        foreach (var (line, want) in cases)
        {
            string got = Run(line);
            Assert.True(got == want, line + "\n  want " + want + "\n  got  " + got);
        }

        // L6: wall-clock timeStamp, e.g. "2026/10/10 07:58:38.546".
        {
            string t = Run("echo(timeStamp());");
            Assert.True(t.Length == 23 && t[4] == '/' && t[7] == '/' && t[10] == ' ' && t[13] == ':' && t[16] == ':' && t[19] == '.');
        }

        // L15: the log session header has no zero padding and ends "\n\r".
        string path = Path.Combine(sTmp, "lasthope-console.log");
        File.Delete(path);
        {
            var when = new DateTime(2026, 10, 9, 7, 5, 3);
            using var log = new ConsoleLog(path, () => when);
            log.SetLastHope(true);
            log.SetMode(1);
            log.Write("line");
        }
        Assert.Equal("--- 10/9/2026 -- 7:5:3 ---\n\rline\r\n", File.ReadAllText(path, System.Text.Encoding.Latin1));
    }
}
