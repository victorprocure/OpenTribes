// Remote calls of a network peer: SimConsoleEvent_unpack 00510118 builds "remote" + name and the console call interns it and
// prints "name: Unknown command." when it has no function. Interpreter.CallRemote bounds the new names a peer adds.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;

public sealed class RemoteCallTests
{
    private static (Interpreter In, StringTable Strings, List<string> Lines) Make()
    {
        var strings = new StringTable();
        var inp = new Interpreter(strings);
        var lines = new List<string>();
        inp.SetOutput(lines.Add);
        return (inp, strings, lines);
    }

    [Fact]
    public void KnownFunctionRunsAndUnknownNameLogsAsBefore()
    {
        var (inp, strings, lines) = Make();
        inp.Evaluate("function remoteHello(%c, %a) { echo(\"hi \" @ %c @ \" \" @ %a); }");
        lines.Clear();
        inp.CallRemote("remoteHello", ["2049", "x"]);
        Assert.Equal(["hi 2049 x"], lines);
        lines.Clear();
        Assert.Equal("False", inp.CallRemote("remoteNope", ["2049"]));
        Assert.Equal(["remoteNope: Unknown command."], lines);
        Assert.Equal("remoteNope", strings.Lookup("REMOTENOPE"));
        // a later case variant prints the first spelling, as the interned name of stock
        lines.Clear();
        inp.CallRemote("REMOTENOPE", ["2049"]);
        Assert.Equal(["remoteNope: Unknown command."], lines);
        Assert.Equal(2, inp.UnknownCommands()["remoteNope"]);
    }

    [Fact]
    public void DistinctUnknownNamesStopGrowingTheTableAndTheLog()
    {
        var (inp, strings, lines) = Make();
        var total = Interpreter.MaxRemoteNames + 500;
        for (var i = 0; i < total; ++i)
            inp.CallRemote("remoteJunk" + i, ["2049"]);
        Assert.NotNull(strings.Lookup("remoteJunk" + (Interpreter.MaxRemoteNames - 1)));
        Assert.Null(strings.Lookup("remoteJunk" + Interpreter.MaxRemoteNames));
        Assert.Null(strings.Lookup("remoteJunk" + (total - 1)));
        Assert.Equal(Interpreter.MaxRemoteNames, inp.UnknownCommands().Count);
        Assert.Equal(Interpreter.MaxRemoteNames + 1, lines.Count);
        // names already in the table still run and log after the cap
        inp.Evaluate("function remoteHello(%c) { echo(\"hi\"); }");
        lines.Clear();
        inp.CallRemote("remoteHello", ["2049"]);
        inp.CallRemote("remoteJunk3", ["2049"]);
        Assert.Equal(["hi", "remoteJunk3: Unknown command."], lines);
    }
}
