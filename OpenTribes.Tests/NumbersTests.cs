// Port of tests/test_numbers.cpp: script number conversion edge cases (docs/script.md 3.1-3.3 and 6.4 S2) not covered
// by the interpreter vectors in ScriptEvalTests. The C++ runs in the script FPU mode (round toward zero); the port
// parses with double to nearest (D25) and prints truncated digits, and every case below holds in that form.
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class NumbersTests
{
    [Fact]
    public void TestNumbers()
    {
        int cases = 0;

        // 3.3.1: Borland atoi skips whitespace, takes one sign, stops at the first non-digit and
        // wraps modulo 2^32.
        {
            Assert.Equal(1, Numbers.BorlandAtoi("4294967297"));
            Assert.Equal(2147483647, Numbers.BorlandAtoi("-2147483649"));
            Assert.Equal(1, Numbers.BorlandAtoi("-4294967295"));
            Assert.Equal(12, Numbers.BorlandAtoi(" \t\n+12x"));
            Assert.Equal(-7, Numbers.BorlandAtoi("-7.9"));
            Assert.True(Numbers.BorlandAtoi("") == 0 && Numbers.BorlandAtoi("-") == 0 && Numbers.BorlandAtoi("abc") == 0);
            Assert.Equal(0, Numbers.BorlandAtoi("+-5"));
            ++cases;
        }

        // 3.1: Borland atof is decimal only; an incomplete exponent is ignored; only the signed
        // upper-case "+INF"/"-INF"/"+NAN"/"-NAN" spellings are special.
        {
            Assert.Equal(0.0, Numbers.BorlandAtof("0x10"));
            Assert.True(Numbers.BorlandAtof(".5") == 0.5 && Numbers.BorlandAtof("5.") == 5.0 && Numbers.BorlandAtof(".") == 0);
            Assert.True(Numbers.BorlandAtof("1e") == 1 && Numbers.BorlandAtof("1e+") == 1 && Numbers.BorlandAtof("2E2") == 200);
            Assert.True(Numbers.BorlandAtof("-.5e1") == -5 && Numbers.BorlandAtof("\v 7") == 7);
            Assert.Equal(double.NegativeInfinity, Numbers.BorlandAtof("-INF"));
            double nan = Numbers.BorlandAtof("+NANx");
            Assert.True(double.IsNaN(nan) && !double.IsNegative(nan));
            Assert.True(Numbers.BorlandAtof("+inf") == 0 && Numbers.BorlandAtof("NAN") == 0);
            Assert.Equal(double.NegativeInfinity, Numbers.BorlandAtof("-1e400"));
            Assert.NotEqual(0.0, Numbers.BorlandAtof("1e-320")); // double denormals survive
            ++cases;
        }

        // 3.2: %g with the 6th significant digit truncated (round toward zero), Borland INF/NAN
        // spellings, negative zero.
        {
            double two = 2.0, three = 3.0;
            Assert.Equal("0.666666", Numbers.FormatFloat(two / three));
            Assert.Equal("-0.666666", Numbers.FormatFloat(-(two / three)));
            Assert.Equal("999999", Numbers.FormatFloat(999999.95));
            Assert.Equal("9.99999e+06", Numbers.FormatFloat(9999995.0));
            Assert.True(Numbers.FormatFloat(1e20) == "1e+20" && Numbers.FormatFloat(2.5) == "2.5" && Numbers.FormatFloat(3.0) == "3");
            Assert.Equal("-0", Numbers.FormatFloat(-0.0));
            Assert.Equal("+INF", Numbers.FormatFloat(double.PositiveInfinity));
            Assert.Equal("-INF", Numbers.FormatFloat(double.NegativeInfinity));
            Assert.True(Numbers.FormatFloat(Numbers.NaN) == "+NAN" && Numbers.FormatFloat(Numbers.NegNaN) == "-NAN");
            ++cases;
        }

        // 3.3.2: fistp truncation; out of range, infinities and NaN give 0x80000000.
        {
            Assert.Equal(2147483647, Numbers.FloatToInt(2147483647.9));
            Assert.Equal(int.MinValue, Numbers.FloatToInt(2147483648.0));
            Assert.Equal(int.MinValue, Numbers.FloatToInt(-2147483648.0));
            Assert.Equal(int.MinValue, Numbers.FloatToInt(-2147483649.0));
            Assert.True(Numbers.FloatToInt(2.9) == 2 && Numbers.FloatToInt(-2.9) == -2 && Numbers.FloatToInt(-0.5) == 0);
            Assert.True(Numbers.FloatToInt(double.PositiveInfinity) == int.MinValue && Numbers.FloatToInt(double.NegativeInfinity) == int.MinValue);
            Assert.Equal(int.MinValue, Numbers.FloatToInt(Numbers.NaN));
            ++cases;
        }

        // Console_parseBool (0x005f47d8): T/t true, F/f false, else atof != 0 (NaN is true).
        {
            Assert.True(Numbers.ParseBool("True") && Numbers.ParseBool("t") && Numbers.ParseBool("tx"));
            Assert.True(!Numbers.ParseBool("False") && !Numbers.ParseBool("f1") && !Numbers.ParseBool("F"));
            Assert.True(Numbers.ParseBool("1") && Numbers.ParseBool("0.5") && Numbers.ParseBool("  2") && Numbers.ParseBool("-NAN"));
            Assert.True(!Numbers.ParseBool("0") && !Numbers.ParseBool("0.0") && !Numbers.ParseBool("") && !Numbers.ParseBool("Yes"));
            ++cases;
        }

        // 6.4 S2: hex INT literals wrap modulo 2^32, either case of x and digits.
        {
            Assert.Equal(0x23456789, Numbers.ParseIntLiteral("0x123456789"));
            Assert.Equal(-1, Numbers.ParseIntLiteral("0X1FFFFFFFF"));
            Assert.Equal(0xabcdef, Numbers.ParseIntLiteral("0xabcDEF"));
            Assert.Equal(int.MinValue, Numbers.ParseIntLiteral("0x80000000"));
            Assert.Equal(1, Numbers.ParseIntLiteral("4294967297")); // decimal goes through atoi (3.3.1)
            ++cases;
        }

        // 3.1: "True" ignoring ASCII case is 1; numeric-looking strings (0x005f6280).
        {
            Assert.True(Numbers.IsTrueString("TRUE") && Numbers.IsTrueString("tRuE"));
            Assert.True(!Numbers.IsTrueString("true ") && !Numbers.IsTrueString("Tru") && !Numbers.IsTrueString("1"));
            Assert.True(Numbers.LooksNumeric("1e5") && Numbers.LooksNumeric("-1.5E+3") && Numbers.LooksNumeric("."));
            Assert.True(!Numbers.LooksNumeric("") && !Numbers.LooksNumeric(" 1") && !Numbers.LooksNumeric("0x1"));
            ++cases;
        }

        _ = cases;
    }
}
