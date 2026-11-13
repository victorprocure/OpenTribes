namespace OpenTribes.Script;

using System.Text;
using OpenTribes.Core;
using OpenTribes.World;

/// <summary>The generator behind getRandom: a 250-word shift register seeded from a linear congruential generator (005eb5a0, 005eb660).</summary>
/// <remarks>The original seeds it with 0x8377 on the first getRandom call and never reseeds.</remarks>
public sealed class RandomR250
{
    /// <summary>The seed the original uses on the first getRandom call.</summary>
    public const uint kOriginalSeed = 0x8377;
    private readonly uint[] mWord = new uint[250];
    private uint mIndex;

    /// <summary>Creates a generator.</summary>
    /// <param name="seed">The seed.</param>
    public RandomR250(uint seed = kOriginalSeed) => Reseed(seed);

    /// <summary>Seeds the generator.</summary>
    /// <param name="seed">The seed.</param>
    public void Reseed(uint seed)
    {
        var state = seed;
        uint Lcg() => state = unchecked((state * 0x19660dU) + 0x3c6ef35fU);
        mIndex = 0;
        for (var i = 0; i < 250; ++i)
            mWord[i] = Lcg();
        for (var i = 0; i < 250; ++i)
            if (Lcg() > 0x40000000U)
                mWord[i] |= 0x80000000U;
        uint mask = 0xffffffffU, bit = 0x80000000U;
        for (uint i = 0; i < 32; ++i)
        {
            mWord[(7 * i) + 3] = (mWord[(7 * i) + 3] & mask) | bit;
            mask >>= 1;
            bit >>= 1;
        }
    }

    /// <summary>Gets the next random number.</summary>
    /// <returns>The number.</returns>
    public uint Next()
    {
        var other = mIndex >= 0x93 ? mIndex - 0x93 : mIndex + 0x67;
        var v = mWord[mIndex] ^= mWord[other];
        mIndex = mIndex >= 249 ? 0 : mIndex + 1;
        return v;
    }
}

/// <summary>The math, vector and utility commands that need no simulation state: sprintf, pow, getRandom, timeStamp, which and the Vector:: family (docs/script.md 6.7).</summary>
/// <remarks>Port of src/script/math_commands.cpp. Portable form only (DECISIONS D25, docs/portable-float.md 1): the x87 instruction wrappers are nearbyint, expm1(x ln2), ldexp, y log2(x), atan2 and plain double operations, and the rotation matrix is <c>world::eulerMatrix</c>. The basic operations, the parse and the float stores follow the rounding mode of <c>Fpu</c>. The C library functions (log2, exp, atan2, sin, cos) round to nearest.</remarks>
public static class MathCommands
{
    // ---- x87 instruction wrappers, portable form ----
    private static double X87Frndint(double x) => Fpu.TowardZero ? Math.Truncate(x) : Math.Round(x, MidpointRounding.ToEven); // nearbyint
    private static double X87F2xm1(double x) => Expm1(Fpu.Mul(x, 0.69314718055994530942));
    private static double X87Fscale(double x, double scale) => Math.ScaleB(x, (int)Math.Truncate(scale));
    private static double X87Fyl2x(double y, double x) => Fpu.Mul(y, Math.Log2(x)); // y * log2(x)
    private static double X87Fpatan(double y, double x) => Math.Atan2(y, x);
    private static double X87Fldl2e() => 1.44269504088896340736;
    private static double X87Fldln2() => 0.69314718055994530942;
    private static double X87Fldpi() => 3.14159265358979323846;
    // Plain double operations. When both operands are NaNs the result is one of them; the C++ portable build
    // (x86-64 SSE) returns the right operand of + and * and the left one of - and /, made explicit here.
    private static double NanPair(double first, double second, double r) => double.IsNaN(first) && double.IsNaN(second) ? first : r;
    private static double X87AddMem(float a, float b) => NanPair(b, a, Fpu.Add(a, b));
    private static double X87SubMem(float a, float b) => NanPair(a, b, Fpu.Sub(a, b));  // a - b
    private static double X87SubrMem(float a, float b) => NanPair(b, a, Fpu.Sub(b, a)); // b - a
    private static double X87MulMem(float a, float b) => NanPair(b, a, Fpu.Mul(a, b));
    private static double X87DivMem(float a, float b) => NanPair(a, b, Fpu.Div(a, b));  // a / b

    // expm1 (C# has none): exp(x) - 1 with the cancellation corrected by the log of the rounded exp.
    private static double Expm1(double x)
    {
        if (Math.Abs(x) >= 0.5 || !double.IsFinite(x))
            return Math.Exp(x) - 1.0;
        var u = Math.Exp(x);
        if (u == 1.0)
            return x;
        var um1 = u - 1.0;
        if (um1 == -1.0)
            return -1.0;
        return um1 * x / Math.Log(u);
    }

    // src/script/math_commands.cpp ldConstant: ldexp of the 64-bit mantissa, rounded to the 53 bits of a double.
    private static double LdConstant(ulong mantissa, int exp2)
    {
        var shift = 11;
        var kept = mantissa >> shift;
        var rest = mantissa & ((1UL << shift) - 1);
        var half = 1UL << (shift - 1);
        if (rest > half || (rest == half && (kept & 1) != 0))
            ++kept;
        return Math.ScaleB(kept, exp2 + shift);
    }
    // Constants the library loads from its data segment (0x0065c5c0.. in the exe): pi/2, pi/4, pi and sqrt(2).
    private static readonly double kHalfPi = LdConstant(0xc90fdaa22168c235UL, -63);
    private static readonly double kQuarterPi = LdConstant(0xc90fdaa22168c235UL, -64);
    private static readonly double kSqrt2 = LdConstant(0xb504f333f9de6485UL, -63);

    private static readonly double kTwo128 = Math.ScaleB(1.0, 128);  // 0x1p128
    private static readonly double kTwoMinus32 = Math.ScaleB(1.0, -32); // 0x1p-32

    private static ulong BitsOf(double v) => (ulong)BitConverter.DoubleToInt64Bits(v);

    // ---- number parsing: the original's sscanf("%f") ----

    private static bool IsSpaceC(char c) => c is ' ' or (>= '\t' and <= '\r');
    private static bool IsDigitC(char c) => c is >= '0' and <= '9';
    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // src/script/math_commands.cpp scanNumber: one %f after optional white space. The text forms are decimal
    // digits with an optional fraction and exponent, or the signed upper-case +INF -INF +NAN -NAN; hex, "inf" and
    // "nan" fail (docs/script.md 6.4 S3). p indexes the C string text (it stops at a NUL).
    private static bool ScanNumber(string text, ref int p, out double output)
    {
        output = 0;
        var s = p;
        while (IsSpaceC(At(text, s)))
            ++s;
        if ((At(text, s) == '+' || At(text, s) == '-') && At(text, s + 1) == 'I' && At(text, s + 2) == 'N' && At(text, s + 3) == 'F')
        {
            output = At(text, s) == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            p = s + 4;
            return true;
        }
        if ((At(text, s) == '+' || At(text, s) == '-') && At(text, s + 1) == 'N' && At(text, s + 2) == 'A' && At(text, s + 3) == 'N')
        {
            output = At(text, s) == '-' ? Numbers.NegNaN : Numbers.NaN;
            p = s + 4;
            return true;
        }
        var q = s;
        if (At(text, q) == '+' || At(text, q) == '-')
            ++q;
        var digits = 0;
        while (IsDigitC(At(text, q)))
        {
            ++q;
            ++digits;
        }
        if (At(text, q) == '.')
        {
            ++q;
            while (IsDigitC(At(text, q)))
            {
                ++q;
                ++digits;
            }
        }
        if (digits == 0)
            return false;
        var mantissaEnd = q;
        var exponentDigits = false;
        if (At(text, q) == 'e' || At(text, q) == 'E')
        {
            // The marker, its sign and its digits are consumed even when no digit follows ("1e" is 1).
            ++q;
            if (At(text, q) == '+' || At(text, q) == '-')
                ++q;
            while (IsDigitC(At(text, q)))
            {
                ++q;
                exponentDigits = true;
            }
        }
        output = Numbers.Strtod(text[s..(exponentDigits ? q : mantissaEnd)]);
        p = q;
        return true;
    }

    // src/script/math_commands.cpp storeScanned: the float store of scanf: values beyond 2^128 become infinity,
    // the rest convert toward zero (the script FPU mode).
    private static float StoreScanned(double v)
    {
        if (v > kTwo128)
            return float.PositiveInfinity;
        if (v < -kTwo128)
            return float.NegativeInfinity;
        return Fpu.Store(v);
    }

    // src/script/math_commands.cpp f32: static_cast<float>, a store in the current mode.
    private static float F32(double v) => Fpu.Store(v);

    // src/script/math_commands.cpp sumOfThree: fxch / faddp order of the dot and length helpers (0x005ece49).
    private static double SumOfThree(double a, double b, double c) => Fpu.Add(Fpu.Add(a, b), c);

    // src/script/math_commands.cpp length3: length helper (0x005ed014), fsqrt of the three squares.
    private static double Length3(float[] v) => Fpu.Sqrt(SumOfThree((double)v[0] * v[0], (double)v[1] * v[1], (double)v[2] * v[2]));

    // src/script/math_commands.cpp kUninitialized: the stack residue the original prints for an unset z: 1.12949e-38.
    private static readonly float kUninitialized = BitConverter.Int32BitsToSingle(0x007afd96);

    // src/script/math_commands.cpp rotationMatrix: Euler rotation matrix (0x005ed42e), world::eulerMatrix in the
    // portable form.
    private static void RotationMatrix(float[] rot, float[] m)
    {
        var t = new TMat3();
        Shape.EulerMatrix(rot, ref t);
        ((ReadOnlySpan<float>)t.M).CopyTo(m);
    }

    // ---- Borland atan2 (0x006067ac) and atan (0x00606914) ----

    // src/script/math_commands.cpp borlandAtan.
    private static double BorlandAtan(double r)
    {
        if (double.IsNaN(r))
            return r;
        var negative = double.IsNegative(r);
        if (double.IsInfinity(r))
            return negative ? -kHalfPi : kHalfPi;
        if (r == 0)
            return r;
        var a = Math.Abs(r);
        double v;
        if (a == 1)
        {
            v = kQuarterPi;
            return negative ? -v : v;
        }
        if (a < 1)
        {
            v = X87Fpatan(a, 1.0);
            return negative ? -v : v;
        }
        v = Fpu.Sub(X87Fpatan(1.0, a), kHalfPi);
        return negative ? v : -v;
    }

    // src/script/math_commands.cpp borlandAtan2.
    private static double BorlandAtan2(double a, double b)
    {
        ushort ah = (ushort)(BitsOf(a) >> 48), bh = (ushort)(BitsOf(b) >> 48);
        bool aNeg = (ah & 0x8000) != 0, bNeg = (bh & 0x8000) != 0;
        int aExp = ah & 0x7ff0, bExp = bh & 0x7ff0;
        var pi = X87Fldpi();
        if (aExp == 0)
        {
            if (bExp == 0)
                return 0; // atan2(0, 0): the original's error handler never returns (DECISIONS D11)
            if (bNeg)
                return aNeg ? -pi : pi;
            return a;
        }
        if (bExp == 0)
            return aNeg ? -kHalfPi : kHalfPi;
        if (bExp == 0x7ff0)
        {
            if (aExp == 0x7ff0)
                return 0; // both infinite or NaN: error handler (D11)
            double w = 0;
            if (bNeg)
                w = aNeg ? Fpu.Sub(w, pi) : Fpu.Add(w, pi);
            return w;
        }
        if (aExp == 0x7ff0)
            return aNeg ? -kHalfPi : kHalfPi;
        var v = BorlandAtan(Fpu.Div(a, b));
        if (bNeg)
            v = aNeg ? Fpu.Sub(v, pi) : Fpu.Add(v, pi);
        return v;
    }

    // src/script/math_commands.cpp wrongArgs.
    private static void WrongArgs(Interpreter interp, string what, string name) => interp.Print(1, "wrong argcount to " + name + what);

    /// <summary>Formats a float the way the commands print it (%g of the double).</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string FormatF32(float v) => Numbers.FormatFloat(v);

    /// <summary>Formats a string like sprintf (0041cc54).</summary>
    /// <param name="format">The format string.</param>
    /// <param name="args">The arguments. The first is the format and argument k replaces "%k".</param>
    /// <returns>The text, cut at 1063 characters, the size of the original's buffer.</returns>
    /// <remarks>A missing argument reads as empty and a "%" not followed by 1 to 9 is copied. Both crash or hang the original (DECISIONS D11).</remarks>
    public static string FormatSprintf(string format, IReadOnlyList<string> args)
    {
        const int kLimit = 1063;
        var output = new StringBuilder();
        var f = CString.CStr(format);
        var p = 0;
        while (p < f.Length && output.Length < kLimit)
        {
            if (f[p] == '%' && p + 1 < f.Length && f[p + 1] >= '1' && f[p + 1] <= '9')
            {
                var k = f[p + 1] - '0';
                if (k < args.Count)
                {
                    var s = CString.CStr(args[k]);
                    for (var i = 0; i < s.Length && output.Length < kLimit; ++i)
                        output.Append(s[i]);
                }
                p += 2;
            }
            else
            {
                output.Append(f[p++]);
            }
        }
        return output.ToString();
    }

    /// <summary>Reads "%f %f %f" the way the original's sscanf does.</summary>
    /// <param name="text">The text.</param>
    /// <param name="output">Receives the components. Components that do not parse keep their value, where the original leaves stack garbage (DECISIONS D12 uses the caller's value).</param>
    /// <returns>The number of components read.</returns>
    public static int ScanFloats3(string text, float[] output)
    {
        var t = CString.CStr(text);
        var p = 0;
        var n = 0;
        for (; n < 3; ++n)
        {
            if (!ScanNumber(t, ref p, out var v))
                break;
            output[n] = StoreScanned(v);
        }
        return n;
    }

    /// <summary>Computes a power like the Borland pow (00606d90) as the script command calls it.</summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>The value. After a math error it is what the original's handler would return.</returns>
    public static double BorlandPow(double x, double y) => BorlandPow(x, y, out _);

    /// <summary>Computes a power like the Borland pow (00606d90) and reports math errors.</summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <param name="error">Set when the original reports a math error. Its handler never returns on the dedicated server (DECISIONS D11).</param>
    /// <returns>The value. After a math error it is what the handler would return.</returns>
    public static double BorlandPow(double x, double y, out bool error)
    {
        error = false;
        var err = false;
        double Fail(double v)
        {
            err = true;
            return v;
        }
        var result = Pow(x, y, Fail);
        error = err;
        return result;
    }

    private static double Pow(double x, double y, Func<double, double> fail)
    {
        var inf = double.PositiveInfinity;
        const ulong kDomainNan = 0xfff8048000000000UL;
        ulong xb = BitsOf(x), yb = BitsOf(y);
        ushort xh = (ushort)(xb >> 48), yh = (ushort)(yb >> 48);
        if ((xh & 0x7ff0) == 0)
        {
            var s = unchecked((short)yh);
            if (s > 0)
                return 0;
            return s == 0 ? fail(1.0) : fail(inf);
        }
        if ((xh & 0x7ff0) == 0x7ff0)
            return unchecked((short)yh) >= 0 ? fail(inf) : fail(0.0);
        var yExp = yh & 0x7ff0;
        if (yExp == 0)
            return 1.0;
        if (yExp == 0x7ff0)
            return fail(inf);

        double xl = x, yl = y;
        var negate = false;
        var integral = X87Frndint(yl) == yl;
        var useLoop = false;
        if (integral)
        {
            var n = (ushort)((yExp >> 4) - 0x3ff);
            if (n < 0x3f)
            {
                if ((byte)n < 12)
                {
                    // Overflow guard on the exponent of x (16-bit shift and arithmetic shift back).
                    var bx = unchecked((short)((short)(ushort)(xh << 1) - 0x7fe0));
                    var shifted = unchecked((short)((ushort)bx << (n + 1)));
                    shifted = (short)(shifted >> (n + 1));
                    useLoop = shifted == bx;
                }
                if (!useLoop)
                {
                    var yi = (long)yl;
                    if ((xh & 0x8000) != 0)
                    {
                        xl = -xl;
                        if ((yi & 1) != 0)
                            negate = true;
                    }
                }
            }
        }
        if (useLoop)
        {
            var n = (ushort)((yExp >> 4) - 0x3ff);
            var w = (ushort)(yb >> 32);
            var low = (byte)((w & 0xf0) | (int)((yb >> 48) & 0x0f));
            w = (ushort)((w & 0xff00) | low);
            w = (ushort)((w >> 4) | (w << 12));
            var acc = xl;
            for (int i = n; i > 0; --i)
            {
                acc = Fpu.Mul(acc, acc);
                var bit = (w & 0x8000) != 0;
                w = (ushort)(w << 1);
                if (bit)
                    acc = Fpu.Mul(acc, xl);
            }
            if ((yh & 0x8000) != 0)
                acc = Fpu.Div(1.0, acc);
            return acc;
        }

        // exp(y * ln x)
        if (xl < 0)
            return fail(BitConverter.Int64BitsToDouble(unchecked((long)kDomainNan)));
        var t = Fpu.Mul(X87Fyl2x(X87Fldln2(), xl), y);
        {
            // The 80-bit sign and exponent word and the top mantissa word of a double.
            var db = BitsOf(t);
            var de = (uint)(db >> 52) & 0x7ff;
            var se = (ushort)(((db >> 63) << 15) | (de == 0x7ff ? 0x7fffu : de == 0 ? 0u : de - 1023 + 0x3fff));
            var top = (ushort)(de == 0 ? 0u : 0x8000u | (uint)((db >> 37) & 0x7fff));
            var e = se & 0x7fffu;
            if (e >= 0x4008)
            {
                var m = e > 0x4008 ? 0xffffu : top;
                if (m >= 0xb172)
                    return (se & 0x8000) != 0 ? fail(0.0) : fail(inf);
            }
        }
        var tNegative = double.IsNegative(t);
        double result;
        if (t == 0)
        {
            result = 1.0;
        }
        else
        {
            var scaled = Math.Abs(Fpu.Mul(t, X87Fldl2e()));
            if (scaled <= 0.5)
            {
                result = Fpu.Add(X87F2xm1(scaled), 1.0);
            }
            else
            {
                var two = X87Fscale(scaled, 1.0);
                // The C++ rounds `two` to an integer with the rounding mode set toward zero, then sets it to
                // nearest (the original leaves the FPU rounding to nearest until the next float to int conversion).
                var whole = Math.Truncate(two);
                Fpu.TowardZero = false;
                var i = unchecked((short)(long)whole);
                var half = X87Fscale(whole, -1.0);
                var frac = Fpu.Sub(scaled, half);
                result = Fpu.Add(X87F2xm1(frac), 1.0);
                if ((i & 1) != 0)
                    result = Fpu.Mul(result, kSqrt2);
                result = X87Fscale(result, unchecked((short)((ushort)i >> 1)));
            }
            if (tNegative)
                result = Fpu.Div(1.0, result);
        }
        return negate ? -result : result;
    }

    // ---- Vector:: commands ----

    private static string Vec3(float[] o) => FormatF32(o[0]) + " " + FormatF32(o[1]) + " " + FormatF32(o[2]);

    // src/script/math_commands.cpp registerVectors.
    private static void RegisterVectors(Interpreter interp)
    {
        void Binary(string name, bool subtract) => interp.AddCommand(name, (i, a) =>
                                                            {
                                                                if (a.Length != 3)
                                                                {
                                                                    WrongArgs(i, "(vec,vec);", a[0]);
                                                                    return "0 0 0";
                                                                }
                                                                float[] u = [0, 0, 0], v = [0, 0, 0];
                                                                ScanFloats3(a[1], u);
                                                                ScanFloats3(a[2], v);
                                                                var o = new float[3];
                                                                for (var k = 0; k < 3; ++k)
                                                                    o[k] = F32(subtract ? X87SubMem(u[k], v[k]) : X87AddMem(u[k], v[k]));
                                                                return Vec3(o);
                                                            });
        Binary("Vector::add", false);
        Binary("Vector::sub", true);

        interp.AddCommand("Vector::dot", (i, a) =>
        {
            if (a.Length != 3)
            {
                i.Print(1, "wrong argcount to dotVec(vec,vec);");
                return "0";
            }
            float[] u = [0, 0, 0], v = [0, 0, 0];
            ScanFloats3(a[1], u);
            ScanFloats3(a[2], v);
            return FormatF32(F32(SumOfThree(X87MulMem(u[0], v[0]), X87MulMem(u[1], v[1]), X87MulMem(u[2], v[2]))));
        });
        interp.AddCommand("Vector::getDistance", (i, a) =>
        {
            if (a.Length != 3)
            {
                WrongArgs(i, "(vec,vec);", a[0]);
                return "0";
            }
            float[] u = [0, 0, 0], v = [0, 0, 0];
            ScanFloats3(a[1], u);
            ScanFloats3(a[2], v);
            var d = new float[3];
            for (var k = 0; k < 3; ++k)
                d[k] = F32(X87SubrMem(v[k], u[k]));
            return FormatF32(F32(Length3(d)));
        });
        interp.AddCommand("Vector::neg", (i, a) =>
        {
            if (a.Length != 2)
            {
                i.Print(1, "wrong argcount to negVec(vec);");
                return "0 0 0";
            }
            float[] v = [0, 0, 0];
            ScanFloats3(a[1], v);
            return FormatF32(-v[0]) + " " + FormatF32(-v[1]) + " " + FormatF32(-v[2]);
        });
        interp.AddCommand("Vector::normalize", (i, a) =>
        {
            if (a.Length != 2)
            {
                i.Print(1, "wrong argcount to Vector::normalize(vec);");
                return "false";
            }
            float[] v = [0, 0, 0];
            ScanFloats3(a[1], v);
            var len = F32(Length3(v));
            var inv = F32(X87DivMem(1.0f, len));
            var o = new float[3];
            for (var k = 0; k < 3; ++k)
                o[k] = F32(X87MulMem(inv, v[k]));
            return Vec3(o);
        });
        interp.AddCommand("Vector::getFromRot", (i, a) =>
        {
            if (a.Length is < 2 or > 4)
            {
                i.Print(1, a[0] + ": <Rot 3F> [<length>]");
                i.Print(1, "Returns a vector in the direction given by <Rot 3F>,");
                i.Print(1, "of the given length if supplied, else of unit length.");
                return "0 0 0";
            }
            float[] rot = [0, 0, 0];
            ScanFloats3(a[1], rot);
            var m = new float[9];
            RotationMatrix(rot, m);
            float[] v = [0, 1.0f, 0];
            if (a.Length > 2)
                v[1] = F32(Numbers.BorlandAtof(a[2]));
            if (a.Length > 3)
                v[2] = F32(Numbers.BorlandAtof(a[3]));
            var o = new float[3];
            for (var c = 0; c < 3; ++c)
                o[c] = F32(SumOfThree(X87MulMem(v[0], m[c]), X87MulMem(v[1], m[3 + c]), X87MulMem(v[2], m[6 + c])));
            return Vec3(o);
        });
        interp.AddCommand("Vector::getRotation", (i, a) =>
        {
            if (a.Length != 2)
            {
                i.Print(1, "wrong argcount to " + a[0] + "(normal)");
                return "0 0 0";
            }
            float[] v = [0, 0, 0];
            ScanFloats3(a[1], v);
            float pitch, yaw;
            var sum = Fpu.Add(v[1], v[0]);
            if (sum is not (> 0 or < 0))
            {
                // x + y is zero (or NaN): straight up or down; the yaw slot is never written.
                pitch = F32(v[2] < 0 || float.IsNaN(v[2]) ? 3.14159265358979311600 : 0.0);
                yaw = kUninitialized;
            }
            else
            {
                yaw = F32(-BorlandAtan2(v[0], v[1]));
                var sumSquares = F32(Fpu.Add((double)v[0] * v[0], (double)v[1] * v[1])); // the squares are exact
                var horizontal = Fpu.Sqrt(sumSquares);
                pitch = F32(-BorlandAtan2(horizontal, v[2]));
            }
            return FormatF32(pitch) + " 0 " + FormatF32(yaw);
        });
    }

    /// <summary>Registers the math commands.</summary>
    /// <param name="interp">The interpreter.</param>
    /// <param name="res">The resource manager, or null.</param>
    /// <param name="seed">The seed of getRandom. Tests fix it. The default is the original's.</param>
    public static void RegisterMathCommands(Interpreter interp, ResourceManager? res, uint seed = RandomR250.kOriginalSeed)
    {
        RegisterVectors(interp);

        interp.AddCommand("pow", (i, a) =>
        {
            if (a.Length != 3)
            {
                i.Print(1, "wrong argcount to pow(value, power)");
                return "-1";
            }
            var y = Numbers.BorlandAtof(a[2]);
            var x = Numbers.BorlandAtof(a[1]);
            return FormatF32(F32(BorlandPow(x, y)));
        });

        // getRandom: seeded with the same constant on first use, like the original.
        var rng = new RandomR250(seed);
        interp.AddCommand("getRandom", (i, a) => FormatF32(F32(rng.Next() * kTwoMinus32)));

        interp.AddCommand("sprintf", (i, a) =>
        {
            if (a.Length is < 2 or > 10)
            {
                i.Print(1, "wrong argcount to " + a[0] + "(string, [Arg1 ... Arg9] )");
                return "false";
            }
            return FormatSprintf(a[1], new ArraySegment<string>(a, 1, a.Length - 1));
        });

        // Build time and date of the 1.11 executable.
        interp.AddCommand("timeStamp", (i, a) => "11:55:21 Apr 26 2000");

        interp.AddCommand("which", (i, a) =>
        {
            if (a.Length != 2)
            {
                i.Print(1, "Invalid argcount for " + a[0] + ".");
                return null;
            }
            var e = res?.Find(a[1]);
            if (e == null)
            {
                i.Print(1, "Couldn't find file: " + a[1]);
                return "";
            }
            i.Print(1, "path: " + e.FilePath + "\\" + e.FileName);
            return "";
        });
    }
}
