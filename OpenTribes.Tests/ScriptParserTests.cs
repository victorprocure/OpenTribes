// Port of tests/test_script_parser.cpp.
using OpenTribes.Script;

// The script tests share the global string table (Lexer interning), which is single-threaded like the C++ one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpenTribes.Tests;

public class ScriptParserTests
{
    private static bool Ok(string src) => !ScriptParser.Parse(src).Error;

    private static List<Token> Lex(string src)
    {
        var l = new Lexer(src);
        var output = new List<Token>();
        for (var t = l.Next(); t.Kind != Tok.End; t = l.Next())
            output.Add(t);
        return output;
    }

    [Fact]
    public void TestScriptParser()
    {
        // Valid forms (docs/script.md 2.1).
        string[] valid =
        [
            "$x = $y = 2;",
            "echo($x = 1 + 2);",
            "echo(- - 1);",
            "f(1).g(2);",
            "%a.b.c = 1;",
            "$a[1, 2] = 3;",
            "instant Foo \"n\" { a = 1; instant Bar { b = 2; }; };",
            "instant Foo;",
            "instant Foo name;",
            "ArmorData Light { mass = 9; list = {1, 2}; arr[1] = 2; arr[1][2] = 3; };",
            "Foo Bar;",
            "Foo { a = 1; };",
            "IDTEST_X = 5, \"text\";",
            "IDTEST_Y = 5;",
            "if (1) echo(1); else echo(2);",
            "if (1) { echo(1); } else if (2) { echo(2); }",
            "function f(%a, $b) { return; }",
            "function g() { }",
            "x.f();",
            "$x++; $y--; $z += 2; %q <<= 1;",
            "echo(foo, break, IDTAG, *1, ~2, !3);",
            "for ($i = 0; $i < 3; $i++) echo($i);",
            "while ($i) { break; continue; }",
            "obj.f = {1, 2};",
            "echo(1 + $x = 2);",
            "echo(\"a\" $+ \"b\" @ 'c');",
            "",
            "// comment only\n# another\n",
        ];
        foreach (var s in valid)
            Assert.True(Ok(s), "should parse: " + s);

        // Invalid forms.
        string[] invalid =
        [
            ";", "echo(1);;", "if (1) ;", "if (1) {};", "function f() {};", "{ echo(1); }",
            "5+1;", "for(;;) echo(1);", "++$x;", "--5;", "function a.b() {}", "$a[1][2] = 1;",
            "echo(1)(2);", "echo(class);", "($x = 1);", "echo(nil);", "global $x = 1;",
            "Foo { };", "instant Foo { };", "instant Foo { instant Bar; a = 1; };", "a::b::c = 1;",
            "echo(1)", "$x = ;", "\"str\" = 1;", "$0x = 1;",
        ];
        foreach (var s in invalid)
            Assert.False(Ok(s), "should fail: " + s);

        // Statements completed before an error are kept; the error line is the token's line.
        var r = ScriptParser.Parse("echo(\"A\");\necho(\"B\")\necho(\"C\");");
        Assert.True(r.Error && r.Statements.Count == 1 && r.ErrorLine == 3);
        Assert.Equal("x.cs Line: 3 - Syntax error.", ScriptParser.SyntaxErrorMessage(r, "x.cs"));
        Assert.Equal("Syntax error in input.", ScriptParser.SyntaxErrorMessage(r, ""));

        // An invalid \x escape ends input: no error when it falls between statements.
        r = ScriptParser.Parse("echo(1); \"\\x4\"; echo(2);");
        Assert.Single(r.Statements);

        // Lexer details (1.1 notes).
        var t = Lex("1.5.5 IDx ID id5 $0A %0 a::b 0x1F 1e5 1e 'a\\tb' \"\\x41\\q\\n\"");
        Assert.True(t[0].Kind == Tok.FloatLiteral && t[0].Text == "1.5");
        Assert.True(t[1].Kind == Tok.SingleChar && t[1].Ch == '.');
        Assert.True(t[2].Kind == Tok.IntLiteral && t[2].Text == "5");
        Assert.Equal(Tok.Tag, t[3].Kind);
        Assert.True(t[4].Kind == Tok.Ident && t[5].Kind == Tok.Ident);
        Assert.True(t[6].Kind == Tok.Illegal && t[6].Text == "$0A");
        Assert.True(t[7].Kind == Tok.SingleChar && t[7].Ch == '%' && t[8].Kind == Tok.IntLiteral);
        Assert.True(t[9].Kind == Tok.Ident && t[9].Text == "a::b");
        Assert.True(t[10].Kind == Tok.IntLiteral && t[10].Text == "0x1F");
        Assert.True(t[11].Kind == Tok.FloatLiteral && t[11].Text == "1e5");
        Assert.True(t[12].Kind == Tok.IntLiteral && t[13].Kind == Tok.Ident && t[13].Text == "e");
        Assert.True(t[14].Kind == Tok.StringLiteral && t[14].Text == "a\\tb");
        Assert.True(t[15].Kind == Tok.StringLiteral && t[15].Text == "Aq\n");
        // Unterminated string: the quote alone is ILLEGAL; keywords are case-sensitive.
        t = Lex("\"abc\nIf if");
        Assert.True(t[0].Kind == Tok.Illegal && t[1].Kind == Tok.Ident && t[2].Kind == Tok.Ident &&
                    t[3].Kind == Tok.If && t[3].Line == 2);
    }
}
