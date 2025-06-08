// Row-vector affine transform of the game (docs/collision.md T1.1): p' = p * M + P, matrices row-major like TMat3.
// Composition `a.Then(b)` applies a first and b second (TmatMul(a, b)).
namespace OpenTribes.Client.Shapes;

public struct Xf
{
    public double M0, M1, M2, M3, M4, M5, M6, M7, M8, Px, Py, Pz;

    public static Xf Identity => new() { M0 = 1, M4 = 1, M8 = 1 };

    public static Xf Translate(double x, double y, double z)
    {
        Xf r = Identity;
        r.Px = x; r.Py = y; r.Pz = z;
        return r;
    }

    // The Euler matrix of the game (m_mat3_setEuler): rotation x, y, z in radians; yaw (z) turns +X toward +Y.
    public static Xf Euler(double ex, double ey, double ez)
    {
        double sz = Math.Sin(ez), cz = Math.Cos(ez), sy = Math.Sin(ey), cy = Math.Cos(ey), sx = Math.Sin(ex), cx = Math.Cos(ex);
        double cysz = cy * sz, cycz = cy * cz, szsy = sz * sy, czsy = cz * sy;
        return new Xf
        {
            M8 = cy * cx, M4 = cz * cx, M2 = sy * -cx, M3 = -cx * sz, M5 = sx, M7 = szsy - cycz * sx, M0 = cycz - szsy * sx,
            M1 = cysz + czsy * sx, M6 = czsy + cysz * sx,
        };
    }

    // docs/collision.md T2.5: quaternion (x, y, z, w) to the row-major matrix of QuatToMatrix.
    public static Xf FromQuat(double x, double y, double z, double w)
    {
        if (x * x < 1e-12 && y * y < 1e-12 && z * z < 1e-12)
            return Identity;
        double x2 = x * 2, y2 = y * 2, z2 = z * 2;
        double wx = w * x2, wy = w * y2, wz = w * z2, xx = x * x2, xy = x * y2, xz = x * z2, yy = y * y2, yz = y * z2, zz = z * z2;
        return new Xf
        {
            M0 = 1 - (yy + zz), M1 = xy - wz, M2 = xz + wy,
            M3 = xy + wz, M4 = 1 - (xx + zz), M5 = yz - wx,
            M6 = xz - wy, M7 = yz + wx, M8 = 1 - (xx + yy),
        };
    }

    public readonly (double x, double y, double z) Apply(double x, double y, double z) =>
        (x * M0 + y * M3 + z * M6 + Px, x * M1 + y * M4 + z * M7 + Py, x * M2 + y * M5 + z * M8 + Pz);

    // Rotation only (normals).
    public readonly (double x, double y, double z) Rotate(double x, double y, double z) =>
        (x * M0 + y * M3 + z * M6, x * M1 + y * M4 + z * M7, x * M2 + y * M5 + z * M8);

    public readonly Xf Then(in Xf b)
    {
        var o = new Xf
        {
            M0 = M0 * b.M0 + M1 * b.M3 + M2 * b.M6, M1 = M0 * b.M1 + M1 * b.M4 + M2 * b.M7, M2 = M0 * b.M2 + M1 * b.M5 + M2 * b.M8,
            M3 = M3 * b.M0 + M4 * b.M3 + M5 * b.M6, M4 = M3 * b.M1 + M4 * b.M4 + M5 * b.M7, M5 = M3 * b.M2 + M4 * b.M5 + M5 * b.M8,
            M6 = M6 * b.M0 + M7 * b.M3 + M8 * b.M6, M7 = M6 * b.M1 + M7 * b.M4 + M8 * b.M7, M8 = M6 * b.M2 + M7 * b.M5 + M8 * b.M8,
        };
        o.Px = Px * b.M0 + Py * b.M3 + Pz * b.M6 + b.Px;
        o.Py = Px * b.M1 + Py * b.M4 + Pz * b.M7 + b.Py;
        o.Pz = Px * b.M2 + Py * b.M5 + Pz * b.M8 + b.Pz;
        return o;
    }
}
