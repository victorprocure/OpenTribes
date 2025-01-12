// Port of the TMat3 struct of src/world/shape.hpp and eulerMatrix of src/world/shape.cpp, the one piece of
// src/world that the script math commands use (Vector::getFromRot in the portable form). Stage P3 ports the
// rest of src/world. Portable form (docs/portable-float.md 1): the Chop guard keeps the default rounding,
// fsincosLD is sin and cos of the double (libm, not rounded toward zero), the products and sums round toward zero and
// x87::f32 stores toward zero (Fpu, the portable truncating build).
namespace OpenTribes.World;

// src/world/shape.hpp struct TMat3 (TMat3F, T1.1): flags 1 = rotation present, 2 = translation present,
// 4 = general matrix. Only the fields eulerMatrix writes.
public sealed class TMat3
{
    public readonly float[] M = new float[9];
    public uint Flags;
}

public static class Shape
{
    private static float F32(double v) => OpenTribes.Core.Fpu.F32Chop(v);
    private static double Mul(double a, double b) => OpenTribes.Core.Fpu.Mul(a, b);
    private static double Add(double a, double b) => OpenTribes.Core.Fpu.Add(a, b);
    private static double Sub(double a, double b) => OpenTribes.Core.Fpu.Sub(a, b);

    // src/world/shape.cpp eulerMatrix.
    public static void EulerMatrix(ReadOnlySpan<float> e, TMat3 output)
    {
        using var chop = OpenTribes.Core.Fpu.ChopScope();
        static bool Zero(float v) => !(v != 0.0f) || float.IsNaN(v); // ftst: 0, -0 and NaN
        static void SinCos(float a, out double s, out double c)
        {
            s = Math.Sin(a);
            c = Math.Cos(a);
        }
        if (Zero(e[0]) && Zero(e[1]))
        {
            if (Zero(e[2]))
            {
                float[] id = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                id.CopyTo(output.M, 0);
                output.Flags &= ~5u;
                return;
            }
            SinCos(e[2], out double sz1, out double cz1);
            float[] m = { F32(cz1), F32(sz1), 0, F32(-sz1), F32(cz1), 0, 0, 0, 1 };
            m.CopyTo(output.M, 0);
            output.Flags = (output.Flags | 1u) & ~4u;
            return;
        }
        SinCos(e[2], out double sz, out double cz);
        SinCos(e[1], out double sy, out double cy);
        SinCos(e[0], out double sx, out double cx);
        double cysz = Mul(cy, sz), cycz = Mul(cy, cz), szsy = Mul(sz, sy), czsy = Mul(cz, sy);
        output.M[8] = F32(Mul(cy, cx));
        output.M[4] = F32(Mul(cz, cx));
        output.M[2] = F32(Mul(sy, -cx));
        output.M[3] = F32(Mul(-cx, sz));
        output.M[5] = F32(sx);
        output.M[7] = F32(Sub(szsy, Mul(cycz, sx)));
        output.M[0] = F32(Sub(cycz, Mul(szsy, sx)));
        output.M[1] = F32(Add(cysz, Mul(czsy, sx)));
        output.M[6] = F32(Add(czsy, Mul(cysz, sx)));
        output.Flags = (output.Flags | 1u) & ~4u;
    }
}
