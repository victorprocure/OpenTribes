using System.Runtime.InteropServices;

namespace OpenTribes.Platform.Gfx;

// A 3D texture owned by the device (docs/renderer.md 5.6 item 5: RGBA8 for everything).
public interface IGfxTexture : IDisposable
{
    int Width { get; }
    int Height { get; }
}

public readonly record struct TextureOptions(bool Mipmaps, bool Repeat, bool Linear);

// One world-space vertex (docs/renderer.md 5.6 item 1, with the haze amount of 5.2 VertexColorRecord): position,
// texture coordinate, light map coordinate, shade colour and haze (0 clear, 1 fully the haze colour).
[StructLayout(LayoutKind.Sequential)]
public struct Vertex3D
{
    public float X, Y, Z, U, V, Lu, Lv, R, G, B, A, Haze;

    public Vertex3D(float x, float y, float z, float u, float v, float lu, float lv, float haze, float r = 1, float g = 1,
                    float b = 1, float a = 1)
    {
        X = x; Y = y; Z = z; U = u; V = v; Lu = lu; Lv = lv; R = r; G = g; B = b; A = a; Haze = haze;
    }
}

// docs/renderer.md 5.4 state, reduced to what the world draws use: texture times shade times light map, the haze
// overlay folded into the fragment (5.5, Q7), alpha blend, keyed transparency (alpha test), depth test and write.
// BlendKind picks the blend function when Blend is set (shapes-render.md 1.3.2): Translucent is source alpha, Additive adds
// the colour scaled by alpha, Subtractive multiplies the target by one minus the source colour (renderer.md 5.4).
public readonly record struct DrawState3D(IGfxTexture? Texture = null, IGfxTexture? LightMap = null, bool Blend = false,
                                          float AlphaTest = 0f, bool DepthTest = true, bool DepthWrite = true,
                                          BlendMode BlendKind = BlendMode.Translucent);

// docs/world-render.md 1.3: perspective camera in the game's space: +X right, +Y forward, +Z up. Yaw is the z rotation
// of the Euler transform (interiors.md 5.2: local +Y maps to (-sin yaw, cos yaw, 0), so a positive yaw turns left);
// pitch is positive looking up. HorizontalFov is the full horizontal angle in radians (Q1: assumed horizontal).
public struct Camera3D
{
    public double X, Y, Z, Yaw, Pitch, HorizontalFov;
    public int Width, Height;
    public double Near, Far;

    public Camera3D(double x, double y, double z, double yawDegrees, double pitchDegrees, double fovDegrees, int width,
                    int height, double near, double far)
    {
        X = x; Y = y; Z = z;
        Yaw = yawDegrees * Math.PI / 180;
        Pitch = pitchDegrees * Math.PI / 180;
        HorizontalFov = fovDegrees * Math.PI / 180;
        Width = width; Height = height; Near = near; Far = far;
    }

    public readonly (double x, double y, double z) Forward =>
        (-Math.Sin(Yaw) * Math.Cos(Pitch), Math.Cos(Yaw) * Math.Cos(Pitch), Math.Sin(Pitch));

    public readonly (double x, double y, double z) Right => (Math.Cos(Yaw), Math.Sin(Yaw), 0);

    public readonly (double x, double y, double z) Up
    {
        get
        {
            var f = Forward;
            var r = Right;
            return (r.y * f.z - r.z * f.y, r.z * f.x - r.x * f.z, r.x * f.y - r.y * f.x); // right x forward
        }
    }

    // Pixels from the centre of the view to the image plane at distance one along the view axis.
    public readonly double FocalLength => Width / 2.0 / Math.Tan(HorizontalFov / 2);

    // world-render.md 2.4: the distance at which `radius` projects to `pixels` pixels, and the inverse.
    public readonly double ProjectionDistance(double pixels, double radius) => radius * FocalLength / pixels;
    public readonly double ProjectRadius(double distance, double radius) => radius * FocalLength / distance;

    // Camera space: x right, y forward (depth), z up (1.3).
    public readonly (double x, double y, double z) ToCamera(double wx, double wy, double wz)
    {
        double dx = wx - X, dy = wy - Y, dz = wz - Z;
        var r = Right;
        var f = Forward;
        var u = Up;
        return (dx * r.x + dy * r.y + dz * r.z, dx * f.x + dy * f.y + dz * f.z, dx * u.x + dy * u.y + dz * u.z);
    }

    // Surface pixels (origin top left) and a 0..1 depth between the near and far planes; false behind the near plane.
    public readonly bool Project(double wx, double wy, double wz, out double sx, out double sy, out double depth)
    {
        var (cx, cy, cz) = ToCamera(wx, wy, wz);
        sx = sy = depth = 0;
        if (cy < Near)
            return false;
        double fl = FocalLength;
        sx = Width / 2.0 + cx * fl / cy;
        sy = Height / 2.0 - cz * fl / cy;
        depth = (Far * (cy - Near)) / (cy * (Far - Near));
        return true;
    }

    // Column-major view-projection matrix for OpenGL (clip.w = depth along the view axis).
    public readonly void ViewProjection(Span<float> m)
    {
        var r = Right;
        var f = Forward;
        var u = Up;
        double fx = 2 * FocalLength / Width, fy = 2 * FocalLength / Height;
        double a = (Far + Near) / (Far - Near), b = -2 * Far * Near / (Far - Near);
        double pr = r.x * X + r.y * Y + r.z * Z, pu = u.x * X + u.y * Y + u.z * Z, pf = f.x * X + f.y * Y + f.z * Z;
        // rows: x = fx * right, y = fy * up, z = a * forward + b, w = forward; stored column-major
        double[] c0 = { fx * r.x, fy * u.x, a * f.x, f.x };
        double[] c1 = { fx * r.y, fy * u.y, a * f.y, f.y };
        double[] c2 = { fx * r.z, fy * u.z, a * f.z, f.z };
        double[] c3 = { -fx * pr, -fy * pu, -a * pf + b, -pf };
        for (int i = 0; i < 4; ++i)
        {
            m[i] = (float)c0[i];
            m[4 + i] = (float)c1[i];
            m[8 + i] = (float)c2[i];
            m[12 + i] = (float)c3[i];
        }
    }
}
