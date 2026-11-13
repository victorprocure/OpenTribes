// Tests of Fpu (no C++ test file): the rounding of the portable truncating build (docs/portable-float.md 1).
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;

public class FpuTests
{
    private static double Down(double x) => Math.BitDecrement(x);

    [Fact]
    public void TestF32Chop()
    {
        Assert.Equal(0.0999999940395355f, Fpu.F32Chop(0.1));
        Assert.Equal(-0.0999999940395355f, Fpu.F32Chop(-0.1));
        Assert.Equal(float.MaxValue, Fpu.F32Chop(3.4028236e38));
        Assert.Equal(-float.MaxValue, Fpu.F32Chop(-3.4028236e38));
        Assert.Equal(0.5f, Fpu.F32Chop(0.5));
        Assert.True(float.IsNaN(Fpu.F32Chop(double.NaN)));
        Assert.Equal(float.PositiveInfinity, Fpu.F32Chop(double.PositiveInfinity));
        var chopped = Fpu.F32Chop(1e-40);
        Assert.True(chopped <= 1e-40 && chopped > 0);
        Assert.Equal(float.Epsilon, Fpu.F32Chop(1.5e-45));
        Assert.Equal(0.0f, Fpu.F32Chop(1e-46));
    }

    [Fact]
    public void TestParseChop()
    {
        Assert.Equal(Down(0.1), Fpu.ParseChop("0.1"));         // nearest 0.1 is above the decimal value
        Assert.Equal(-Down(0.1), Fpu.ParseChop("-0.1"));
        Assert.Equal(0.3, Fpu.ParseChop("0.3"));               // nearest 0.3 is below: kept
        Assert.Equal(0.5, Fpu.ParseChop("0.5"));
        Assert.Equal(double.NegativeInfinity, Fpu.ParseChop("-1e400"));  // the original's store overflows
        Assert.Equal(double.PositiveInfinity, Fpu.ParseChop("1e400"));
        Assert.Equal(0.0, Fpu.ParseChop("1e-400"));
        Assert.True(double.IsNegative(Fpu.ParseChop("-0")));
        Assert.Equal(0.0, Fpu.ParseChop("3e-324"));            // nearest is the smallest denormal, above 3e-324
        Assert.Equal(double.Epsilon, Fpu.ParseChop("5e-324"));
        Assert.Equal(Down(123456789012345678.0), Fpu.ParseChop("123456789012345678")); // nearest ...680 is above
    }

    [Fact]
    public void TestOperations()
    {
        // 0.1 + 0.2: nearest rounds up (0.30000000000000004), toward zero is one ulp below.
        Assert.Equal(Down(0.1 + 0.2), Fpu.AddChop(0.1, 0.2));
        Assert.Equal(-Down(0.1 + 0.2), Fpu.AddChop(-0.1, -0.2));
        // 0.1 + 0.7: nearest rounds down: kept.
        Assert.Equal(0.1 + 0.7, Fpu.AddChop(0.1, 0.7));
        Assert.Equal(1.0, Fpu.AddChop(1.0, 1e-17));
        Assert.Equal(Down(1.0), Fpu.SubChop(1.0, 1e-17));
        Assert.Equal(0.0, Fpu.SubChop(0.5, 0.5));
        Assert.Equal(double.MaxValue, Fpu.AddChop(double.MaxValue, double.MaxValue));
        Assert.Equal(double.PositiveInfinity, Fpu.AddChop(double.PositiveInfinity, 1));
        Assert.True(double.IsNaN(Fpu.AddChop(double.NaN, 1)));
        // 0.1 * 3: nearest rounds up; 1.1 * 1.1: nearest rounds down.
        Assert.Equal(Down(0.1 * 3), Fpu.MulChop(0.1, 3));
        Assert.Equal(-Down(0.1 * 3), Fpu.MulChop(-0.1, 3));
        Assert.Equal(1.1 * 1.1, Fpu.MulChop(1.1, 1.1));
        Assert.Equal(-double.MaxValue, Fpu.MulChop(-1e300, 1e300));
        Assert.Equal(0.0, Fpu.MulChop(0, 5));
        Assert.True(double.IsNaN(Fpu.MulChop(double.PositiveInfinity, 0)));
        // 1/10: nearest rounds up; 2/3: nearest rounds down.
        Assert.Equal(Down(0.1), Fpu.DivChop(1, 10));
        Assert.Equal(-Down(0.1), Fpu.DivChop(1, -10));
        Assert.Equal(-Down(0.1), Fpu.DivChop(-1, 10));
        Assert.Equal(2.0 / 3, Fpu.DivChop(2, 3));
        Assert.Equal(double.PositiveInfinity, Fpu.DivChop(1, 0));
        Assert.Equal(double.MaxValue, Fpu.DivChop(1e300, 1e-300));
        Assert.True(double.IsNaN(Fpu.DivChop(0, 0)));
        // sqrt(2): nearest rounds up; sqrt(3): nearest rounds down.
        Assert.Equal(Down(Math.Sqrt(2)), Fpu.SqrtChop(2));
        Assert.Equal(Math.Sqrt(3), Fpu.SqrtChop(3));
        Assert.Equal(0.0, Fpu.SqrtChop(0));
        Assert.True(double.IsNaN(Fpu.SqrtChop(-1)));
    }

    [Fact]
    public void TestScriptToward()
    {
        var interp = new Interpreter();
        var output = "";
        interp.SetOutput(line => output = line);
        Builtins.RegisterBuiltins(interp, null, ".");
        interp.Evaluate("echo(0.1 + 0.1);");
        Assert.Equal("0.199999", output);
        // A script vector component is a float store toward zero.
        interp.Evaluate("echo(Vector::add(\"0.1 3.4028236e38 0\", \"0 0 0\"));");
        Assert.Equal("0.0999999 3.40282e+38 0", output);
        // 1/3 == 1/3 and 1/3 < 1/3 need the 80-bit right operand of the original (reference 0 and 1).
        interp.Evaluate("echo(1/3 == 1/3, 1/3 < 1/3);");
        Assert.Equal("10", output);
    }
}
