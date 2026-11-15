// #69: addToSet must not put a SimGroup into itself or into one of its descendants (SimGroup_addObject 004ff15c has no check).
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

public class SimGroupCycleTests
{
    private readonly Interpreter _interp;
    private readonly SimConsole _sim;
    private string _output = "";

    // G1 holds G2, G2 holds G3, G3 holds X. The globals hold the ids: names resolve only among the direct children of a group.
    public SimGroupCycleTests()
    {
        var strings = new StringTable();
        _interp = new Interpreter(strings);
        _interp.SetOutput(l => _output += (_output.Length == 0 ? "" : "\n") + l);
        _sim = new SimConsole(_interp, new ClassContext(), strings);
        _sim.RegisterCommands();
        _interp.SetObjects(_sim);
        Run("newServer(); focusServer();");
        Run("$G1 = newObject(G1, SimGroup); $G2 = newObject(G2, SimGroup); $G3 = newObject(G3, SimGroup); $X = newObject(X, SimSet);" +
            "addToSet($G1, $G2); addToSet($G2, $G3); addToSet($G3, $X);");
    }

    private string Run(string s)
    {
        _output = "";
        _interp.Evaluate(s);
        return _output;
    }

    private SimObject Obj(string global) => _sim.Server()!.FindById(Numbers.BorlandAtoi(Run("echo($" + global + ");")))!;

    [Theory]
    [InlineData("G1", "G1")]
    [InlineData("G2", "G1")]
    [InlineData("G3", "G1")]
    [InlineData("G3", "G3")]
    public void AddingAGroupToItselfOrItsDescendantIsRefused(string set, string obj)
    {
        var setId = Run("echo($" + set + ");");
        var objId = Run("echo($" + obj + ");");
        Assert.Equal("addToSet: Object \"" + objId + "\" contains set \"" + setId + "\"\nFalse", Run("echo(addToSet($" + set + ", $" + obj + "));"));
        Assert.Same(Obj("G1"), Obj("G2").Group());
        Assert.Same(Obj("G2"), Obj("G3").Group());
        Assert.Same(Obj("G3"), Obj("X").Group());
        Assert.NotSame(Obj("G1"), Obj("G1").Group());
        Run("deleteObject($G1);");
        Assert.Equal("FalseFalseFalseFalse", Run("echo(isObject($G1), isObject($G2), isObject($G3), isObject($X));"));
    }

    [Fact]
    public void ARefusedAddLeavesTheOtherObjectsOfTheCallAdded()
    {
        Run("$Y = newObject(Y, SimSet);");
        Assert.EndsWith("False", Run("echo(addToSet($G3, $G1, $Y));"), StringComparison.Ordinal);
        Assert.Same(Obj("G3"), Obj("Y").Group());
    }

    [Fact]
    public void MovingAGroupIntoASiblingOrUpTheTreeWorksAsBefore()
    {
        Run("$G4 = newObject(G4, SimGroup); addToSet($G1, $G4);");
        Assert.Equal("True", Run("echo(addToSet($G4, $G3));"));
        Assert.Same(Obj("G4"), Obj("G3").Group());
        Assert.Equal("True", Run("echo(addToSet($G1, $G3));"));
        Assert.Same(Obj("G1"), Obj("G3").Group());
        Assert.Equal("True", Run("echo(addToSet($G3, $G2));"));
        Assert.Same(Obj("G3"), Obj("G2").Group());
        Run("deleteObject($G1);");
        Assert.Equal("FalseFalseFalseFalseFalse", Run("echo(isObject($G1), isObject($G2), isObject($G3), isObject($G4), isObject($X));"));
    }
}
