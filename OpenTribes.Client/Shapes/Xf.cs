namespace OpenTribes.Client.Shapes;

/// <summary>The row vector affine transform of the game: p' = p * M + P, matrices row major like TMat3.</summary>
/// <remarks>docs/collision.md T1.1. Composition <c>a.Then(b)</c> applies a first and b second (TmatMul(a, b)).</remarks>
public struct Xf
{
    /// <summary>Matrix element 0 (row 0, column 0).</summary>
    public double M0;
    /// <summary>Matrix element 1 (row 0, column 1).</summary>
    public double M1;
    /// <summary>Matrix element 2 (row 0, column 2).</summary>
    public double M2;
    /// <summary>Matrix element 3 (row 1, column 0).</summary>
    public double M3;
    /// <summary>Matrix element 4 (row 1, column 1).</summary>
    public double M4;
    /// <summary>Matrix element 5 (row 1, column 2).</summary>
    public double M5;
    /// <summary>Matrix element 6 (row 2, column 0).</summary>
    public double M6;
    /// <summary>Matrix element 7 (row 2, column 1).</summary>
    public double M7;
    /// <summary>Matrix element 8 (row 2, column 2).</summary>
    public double M8;
    /// <summary>The translation x.</summary>
    public double Px;
    /// <summary>The translation y.</summary>
    public double Py;
    /// <summary>The translation z.</summary>
    public double Pz;

    /// <summary>The transform that changes nothing.</summary>
    public static Xf Identity => new() { M0 = 1, M4 = 1, M8 = 1 };

    /// <summary>Builds a translation.</summary>
    /// <param name="x">The x offset.</param>
    /// <param name="y">The y offset.</param>
    /// <param name="z">The z offset.</param>
    /// <returns>The transform.</returns>
    public static Xf Translate(double x, double y, double z)
    {
        Xf r = Identity;
        r.Px = x; r.Py = y; r.Pz = z;
        return r;
    }

    /// <summary>Builds the Euler matrix of the game (m_mat3_setEuler): rotation x, y, z in radians. Yaw (z) turns +X toward +Y.</summary>
    /// <param name="ex">The rotation about x.</param>
    /// <param name="ey">The rotation about y.</param>
    /// <param name="ez">The rotation about z.</param>
    /// <returns>The transform.</returns>
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

    /// <summary>Converts a quaternion to the row major matrix of QuatToMatrix (docs/collision.md T2.5).</summary>
    /// <param name="x">The quaternion x.</param>
    /// <param name="y">The quaternion y.</param>
    /// <param name="z">The quaternion z.</param>
    /// <param name="w">The quaternion w.</param>
    /// <returns>The transform.</returns>
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

    /// <summary>Transforms a point.</summary>
    /// <param name="x">The point x.</param>
    /// <param name="y">The point y.</param>
    /// <param name="z">The point z.</param>
    /// <returns>The transformed point.</returns>
    public readonly (double x, double y, double z) Apply(double x, double y, double z) =>
        (x * M0 + y * M3 + z * M6 + Px, x * M1 + y * M4 + z * M7 + Py, x * M2 + y * M5 + z * M8 + Pz);

    /// <summary>Rotates a vector, without the translation (normals).</summary>
    /// <param name="x">The vector x.</param>
    /// <param name="y">The vector y.</param>
    /// <param name="z">The vector z.</param>
    /// <returns>The rotated vector.</returns>
    public readonly (double x, double y, double z) Rotate(double x, double y, double z) =>
        (x * M0 + y * M3 + z * M6, x * M1 + y * M4 + z * M7, x * M2 + y * M5 + z * M8);

    /// <summary>Composes two transforms: this one is applied first and <paramref name="b"/> second.</summary>
    /// <param name="b">The transform applied second.</param>
    /// <returns>The composed transform.</returns>
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
