// Port of tests/test_script_eval.cpp: vectors confirmed on the reference 1.11 server (docs/script.md, tools/scriptdiff.py).
// The table keeps the reference outputs. kDouble lists the cases whose output differs in the double form of the port
// (DECISIONS D25: 64-bit values rounded toward zero like the portable truncating build, docs/portable-float.md 1 and 5;
// the original keeps 80-bit operands and parses into 80 bits) with the output the port prints; every other case must
// print the reference output.
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class ScriptEvalTests
{
    private static readonly (string src, string want)[] kCases =
    {
        // Float environment (3.2)
        ("echo(0.1);", "0.0999999"),
        ("echo(0.1 + 0.2);", "0.299999"),
        ("echo(0.0001);", "9.99999e-05"),
        ("echo(1/3, \" \", 2/3);", "0.333333 0.666666"),
        ("echo(10 * 0.1);", "0.999999"),
        ("echo(1 - 0.9);", "0.1"),
        ("echo(100 * 1.1);", "109.999"),
        ("echo(1234567.0, \" \", 999999.9, \" \", 1000000.5);", "1.23456e+06 999999 1e+06"),
        ("echo(1e300 * 1e300);", "1.79769e+308"),
        ("echo(1/0, \" \", -1/0, \" \", 0/0, \" \", -0.0 * 1);", "+INF -INF -NAN -0"),
        ("echo(0.1 == 1/10, 1/10 == 0.1, 0.1 == \"0.1\", \"0.1\" == 0.1);", "0101"),
        // Integers (3.3)
        ("echo(3000000000, \" \", 2147483648, \" \", 4294967296);", "-1294967296 -2147483648 0"),
        ("echo(1e10 | 0, \" \", -1.9 & 255, \" \", 3000000000.0 & 1);", "-2147483648 255 0"),
        ("echo(-2147483647, \" \", -5, \" \", 2147483647 + 1);", "-2.14748e+09 -5 2.14748e+09"),
        ("echo(0xFFFFFFFF, \" \", 0x100000000, \" \", 0x7FFFFFFF);", "-1 0 2147483647"),
        // Operators (3.4)
        ("echo(-7 % 3, \" \", 7.5 % 2, \" \", 7 % 0, \" \", -1 >> 1);", "-1 1 0 -1"),
        ("echo(1 @ 2 + 3, \" \", 1 + 2 @ 3 + 4, \" \", 1 << 2 + 1, \" \", 5 ^ 1 & 3);", "15 37 8 4"),
        ("echo(!\"abc\", !\"True\", \"True\" + 1, \"TRUE\" * 3, \"12abc\" + 1);", "102313"),
        ("echo(\"0x10\" + 1, \" \", \"1e2\" + 1, \" \", \"1e2\" & 255, \" \", \"2.9\" << 1);", "1 101 1 4"),
        // Comparison (3.5)
        ("echo(\"abc\" == \"ABC\", \"abc\" == 0, \"\" == 0, \"e\" == \"E\", \"1.0\" == \"1\");", "01111"),
        ("echo(\"10\" < \"9\", \"10a\" < \"9\", \"True\" == \"5\", \"False\" == \"\", \"\" == \"0\");", "01100"),
        ("echo(0/0 == 0, 0/0 != 0, 0/0 < 0, 0/0 > 0, 0/0 <= 0, 0/0 >= 0);", "101010"),
        ("echo(!(0/0), (0/0) && 1, (0/0) || 0);", "100"),
        // atof special spellings
        ("echo(\"+INF\" + 0, \" \", \"INF\" + 0, \" \", \"-NAN\" + 0, \" \", \"1e400\" + 0, \" \", \"1e-400\" + 0);",
         "+INF 0 -NAN +INF 0"),
        // Variables and arrays (3.6)
        ("$a[1, 2] = 5; echo($a1_2);", "5"),
        ("$b[11] = 7; echo($b1[1]);", "7"),
        ("$n = 5; echo($n++);", "6"),
        ("$c = \"True\"; $c++; echo($c);", "2"),
        // Functions (3.7)
        ("function f1() { $q = 4; } echo(f1());", "4"),
        ("function f2(%a) { if (%a > 1) return %a * f2(%a - 1); return 1; } echo(f2(6));", "720"),
        ("echo(nosuch(1));", "nosuch: Unknown command.\nFalse"),
        ("echo(eval(\"$e = 5;\"));", "5"),
        // Built-ins (5.1)
        ("echo(floor(-2.5), \" \", floor(), \" \", sqrt(2));", "-2 0 1.41421"),
        ("echo(sqrt());", "wrong argcount to sqrt(Value)\n-1"),
        // Errors (4.1)
        ("echo(\"A\"); echo(\"B\") echo(\"C\");", "Syntax error in input.\nA"),
        // Tags (3.9)
        ("IDT_A = 900001, \"text\"; echo(IDT_A, \" \", *900001, \" \", *900002, \" \", IDT_NONE);",
         "900001 text <INVALID TAG> 0"),
        ("IDT_B = 900001;", "Error: id 900001 already defined to a tag."),
        ("IDT_A = 900003;", "Error: define IDT_A already defined to tag 900001."),
    };

    // Outputs of the double form where the reference output needs the 80-bit unit.
    private static readonly Dictionary<string, string> kDouble = new()
    {
        ["echo(0.1 == 1/10, 1/10 == 0.1, 0.1 == \"0.1\", \"0.1\" == 0.1);"] = "1111", // reference "0101"
    };

    [Fact]
    public void TestScriptEval()
    {
        var interp = new Interpreter();
        string output = "";
        interp.SetOutput(line => output += (output.Length == 0 ? "" : "\n") + line);
        string Run(string line)
        {
            output = "";
            interp.Evaluate(line);
            return output;
        }

        var failures = new List<string>();
        foreach (var (src, reference) in kCases)
        {
            string want = kDouble.TryGetValue(src, out string? d) ? d : reference;
            string got = Run(src);
            if (got != want)
                failures.Add("FAIL " + src + "\n  want: " + want + "\n  got:  " + got);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
