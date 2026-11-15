// #46: the script parser and interpreter stop at a depth limit with a script error instead of overflowing the stack. The stock
// parser (CMDparse 005ee998) reports a plain syntax error when its 350 entry state stack overflows; the stock interpreter has no
// limit and crashes when its native stack overflows.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;

public class ScriptDepthTests
{
    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    // Runs the lines on a thread with an 8 MB stack, the main thread stack of the server on Linux, and returns the console output.
    private static string Run(params string[] lines)
    {
        var output = new List<string>();
        var th = new Thread(() =>
        {
            var interp = new Interpreter(new StringTable());
            interp.SetOutput(output.Add);
            foreach (var l in lines)
                interp.Evaluate(l);
        }, 8 * 1024 * 1024);
        th.Start();
        th.Join();
        return string.Join("\n", output);
    }

    [Fact]
    public void NestingAtTheParserLimitParses()
    {
        // The statement, the assignment and its right side take three levels, each parenthesis one more.
        var r = ScriptParser.Parse("$x = " + Rep("(", ScriptParser.MaxNesting - 3) + "1" + Rep(")", ScriptParser.MaxNesting - 3) + ";");
        Assert.False(r.Error);
        Assert.Equal(ScriptParser.MaxNesting, r.MaxNesting);
    }

    [Theory]
    [InlineData("$x = {0}1{1};", "(", ")")]
    [InlineData("$x = {0}1;", "!", "")]
    [InlineData("$x = {0}1;", "- ", "")]
    [InlineData("{0}$x = 1;{1}", "if (1) ", "")]
    [InlineData("{0}$x = 1;{1}", "while (0) {{ ", "}}")]
    [InlineData("$x = {0}1{1};", "strcat(", ")")]
    [InlineData("$x = {0}1{1};", "$a[", "]")]
    [InlineData("{0}{1}", "instant SimGroup \"g\" {{ ", "}};")]
    public void NestingPastTheParserLimitIsASyntaxError(string format, string open, string close)
    {
        var n = 100000;
        var src = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, Rep(open.Replace("{{", "{"), n), Rep(close.Replace("}}", "}"), n));
        var r = ScriptParser.Parse(src);
        Assert.True(r.Error);
        Assert.Equal("Syntax error in input.", Run(src));
    }

    [Fact]
    public void InfiniteRecursionFailsTheDeepestCallAndReturns()
    {
        var output = Run("function f() { $n++; return f(); }", "$n = 0; echo(f());", "echo($n);", "echo(\"after\");");
        var lines = output.Split('\n');
        Assert.Equal("f: Script calls nested too deep.", lines[0]);
        Assert.Equal("False", lines[1]);
        // The body of f nests two levels (the statement and its expression), so each call weighs CallWeight + 2.
        Assert.InRange(int.Parse(lines[2], System.Globalization.CultureInfo.InvariantCulture), (Interpreter.MaxDepth / 4) - 3, Interpreter.MaxDepth / 4);
        Assert.Equal("after", lines[3]);
    }

    [Fact]
    public void RecursionThroughEvalAndDeepBodiesStopsInsideTheStack()
    {
        var deep = "function g() { " + Rep("if (1) ", 300) + "g(); }";
        var output = Run(deep, "g();", "function e() { eval(\"e();\"); }", "e();", "echo(\"after\");");
        Assert.Contains("g: Script calls nested too deep.", output);
        Assert.EndsWith("after", output);
    }

    // The stock interpreter runs out of its 1 MB stack after about 240 nested calls. sum weighs 2 + 5 and nests 290 calls here.
    [Fact]
    public void RecursionAsDeepAsTheStockStackAllowsRunsAsBefore() =>
        Assert.Equal("250", Run("function sum(%n) { if (%n <= 0) return 0; return 1 + sum(%n - 1); }", "echo(sum(250));"));
}
