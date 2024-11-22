// Port of the TMat3 struct of src/world/shape.hpp and eulerMatrix of src/world/shape.cpp, the one piece of
// src/world that the script math commands use (Vector::getFromRot in the portable form). Stage P3 ports the
// rest of src/world. Portable form (docs/portable-float.md 1): the Chop guard keeps the default rounding,
// fsincosLD is sin and cos of the double, x87::f32 is (float)x.
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
    // src/world/shape.cpp eulerMatrix.
    public static void EulerMatrix(ReadOnlySpan<float> e, TMat3 output)
    {
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
            float[] m = { (float)cz1, (float)sz1, 0, (float)(-sz1), (float)cz1, 0, 0, 0, 1 };
            m.CopyTo(output.M, 0);
            output.Flags = (output.Flags | 1u) & ~4u;
            return;
        }
        SinCos(e[2], out double sz, out double cz);
        SinCos(e[1], out double sy, out double cy);
        SinCos(e[0], out double sx, out double cx);
        double cysz = cy * sz, cycz = cy * cz, szsy = sz * sy, czsy = cz * sy;
        output.M[8] = (float)(cy * cx);
        output.M[4] = (float)(cz * cx);
        output.M[2] = (float)(sy * (-cx));
        output.M[3] = (float)((-cx) * sz);
        output.M[5] = (float)sx;
        output.M[7] = (float)(szsy - cycz * sx);
        output.M[0] = (float)(cycz - szsy * sx);
        output.M[1] = (float)(cysz + czsy * sx);
        output.M[6] = (float)(czsy + cysz * sx);
        output.Flags = (output.Flags | 1u) & ~4u;
    }
}
