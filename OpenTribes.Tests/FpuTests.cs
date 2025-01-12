// Tests of Fpu.F32Chop (no C++ test file: the store of the portable truncating build, docs/portable-float.md 1).
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class FpuTests
{
    [Fact]
    public void TestF32Chop()
    {
        Assert.Equal(0.0999999940395355f, Fpu.F32Chop(0.1));
        Assert.Equal("0.0999999", Numbers.FormatFloat(Fpu.F32Chop(0.1)));
        Assert.Equal(-0.0999999940395355f, Fpu.F32Chop(-0.1));
        Assert.Equal(float.MaxValue, Fpu.F32Chop(3.4028236e38));
        Assert.Equal(-float.MaxValue, Fpu.F32Chop(-3.4028236e38));
        Assert.Equal(0.5f, Fpu.F32Chop(0.5));
        Assert.True(float.IsNaN(Fpu.F32Chop(double.NaN)));
        Assert.Equal(float.PositiveInfinity, Fpu.F32Chop(double.PositiveInfinity));
        // Denormal: 1e-40 lies above the nearest float denormal, so the store keeps that one; 1.5e-45 truncates to the
        // smallest denormal, 1e-46 to zero.
        Assert.True(Fpu.F32Chop(1e-40) <= 1e-40 && Fpu.F32Chop(1e-40) > 0);
        Assert.Equal(float.Epsilon, Fpu.F32Chop(1.5e-45));
        Assert.Equal(0.0f, Fpu.F32Chop(1e-46));
    }

    [Fact]
    public void TestScriptStoresTowardZero()
    {
        var interp = new Interpreter();
        string output = "";
        interp.SetOutput(line => output = line);
        Builtins.RegisterBuiltins(interp, null, ".");
        // A script vector component is a float store: 0.1 + 0 prints the truncated float.
        interp.Evaluate("echo(Vector::add(\"0.1 3.4028236e38 0\", \"0 0 0\"));");
        Assert.Equal("0.0999999 3.40282e+38 0", output);
    }
}
