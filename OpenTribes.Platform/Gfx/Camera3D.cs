namespace OpenTribes.Platform.Gfx;

using System.Runtime.InteropServices;

/// <summary>A 3D texture owned by the device (docs/renderer.md 5.6 item 5: RGBA8 for everything).</summary>
public interface IGfxTexture : IDisposable
{
    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }
}

/// <summary>The creation options of a 3D texture.</summary>
/// <param name="Mipmaps">Whether to build mipmaps.</param>
/// <param name="Repeat">Whether the texture wraps instead of clamping.</param>
/// <param name="Linear">Whether to filter linearly.</param>
/// <param name="NearestMag">Whether the magnification filter is nearest. The stock client does this for bitmaps with the transparent attribute (005d3f93).</param>
public readonly record struct TextureOptions(bool Mipmaps, bool Repeat, bool Linear, bool NearestMag = false);

/// <summary>One world-space vertex (docs/renderer.md 5.6 item 1, with the haze amount of 5.2 VertexColorRecord): position, texture coordinate, light map coordinate, shade colour and haze.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex3D
{
    /// <summary>The position, the texture coordinate, the light map coordinate, the shade colour and alpha, and the haze (0 clear, 1 fully the haze colour).</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    public float Lu { get; set; }
    public float Lv { get; set; }
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }
    public float Haze { get; set; }

    /// <summary>Creates a vertex.</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="u">The texture u.</param>
    /// <param name="v">The texture v.</param>
    /// <param name="lu">The light map u.</param>
    /// <param name="lv">The light map v.</param>
    /// <param name="haze">The haze amount.</param>
    /// <param name="r">The shade red.</param>
    /// <param name="g">The shade green.</param>
    /// <param name="b">The shade blue.</param>
    /// <param name="a">The shade alpha.</param>
    public Vertex3D(float x, float y, float z, float u, float v, float lu, float lv, float haze, float r = 1, float g = 1,
                    float b = 1, float a = 1)
    {
        X = x;
        Y = y;
        Z = z;
        U = u;
        V = v;
        Lu = lu;
        Lv = lv;
        R = r;
        G = g;
        B = b;
        A = a;
        Haze = haze;
    }
}

/// <summary>A point light that adds to the light map of the world surfaces (docs/world-render.md 2.9, 3.7): colour times (1 - d / range) for every texel within range, clamped to 1 with the light map.</summary>
/// <param name="X">The x position.</param>
/// <param name="Y">The y position.</param>
/// <param name="Z">The z position.</param>
/// <param name="Range">The range.</param>
/// <param name="R">The red.</param>
/// <param name="G">The green.</param>
/// <param name="B">The blue.</param>
public readonly record struct DynamicLight(float X, float Y, float Z, float Range, float R, float G, float B);

/// <summary>The draw state of the world draws (docs/renderer.md 5.4, reduced): texture times shade times light map, the haze overlay folded into the fragment (5.5, Q7), alpha blend, keyed transparency (alpha test), depth test and write.</summary>
/// <param name="Texture">The texture, or null.</param>
/// <param name="LightMap">The light map, or null.</param>
/// <param name="Blend">Whether blending is on.</param>
/// <param name="AlphaTest">The alpha test threshold, 0 for none.</param>
/// <param name="DepthTest">Whether the depth test is on.</param>
/// <param name="DepthWrite">Whether depth writes are on.</param>
/// <param name="BlendKind">The blend function used when Blend is set (shapes-render.md 1.3.2): Translucent is source alpha, Additive adds the colour scaled by alpha, Subtractive multiplies the target by one minus the source colour (renderer.md 5.4).</param>
public readonly record struct DrawState3D(IGfxTexture? Texture = null, IGfxTexture? LightMap = null, bool Blend = false,
                                          float AlphaTest = 0f, bool DepthTest = true, bool DepthWrite = true,
                                          BlendMode BlendKind = BlendMode.Translucent);

/// <summary>A perspective camera in the game's space (docs/world-render.md 1.3): +X right, +Y forward, +Z up.</summary>
/// <remarks>Yaw is the z rotation of the Euler transform (interiors.md 5.2: local +Y maps to (-sin yaw, cos yaw, 0), so a positive yaw turns left). Pitch is positive looking up. The field of view is the full horizontal angle in radians (Q1: assumed horizontal).</remarks>
public struct Camera3D
{
    /// <summary>The position, the yaw and pitch in radians, and the horizontal field of view in radians.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public double HorizontalFov { get; set; }
    /// <summary>The rotation about the view axis, the y word of the Euler transform. A positive roll turns the right vector toward -up.</summary>
    public double Roll { get; set; }
    /// <summary>The viewport size in pixels.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The near and far plane distances.</summary>
    public double Near { get; set; }
    public double Far { get; set; }

    /// <summary>Creates a camera from angles in degrees.</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="yawDegrees">The yaw.</param>
    /// <param name="pitchDegrees">The pitch.</param>
    /// <param name="fovDegrees">The full horizontal field of view.</param>
    /// <param name="width">The viewport width in pixels.</param>
    /// <param name="height">The viewport height in pixels.</param>
    /// <param name="near">The near plane distance.</param>
    /// <param name="far">The far plane distance.</param>
    /// <param name="rollDegrees">The roll.</param>
    public Camera3D(double x, double y, double z, double yawDegrees, double pitchDegrees, double fovDegrees, int width,
                    int height, double near, double far, double rollDegrees = 0)
    {
        Roll = rollDegrees * Math.PI / 180;
        X = x;
        Y = y;
        Z = z;
        Yaw = yawDegrees * Math.PI / 180;
        Pitch = pitchDegrees * Math.PI / 180;
        HorizontalFov = fovDegrees * Math.PI / 180;
        Width = width;
        Height = height;
        Near = near;
        Far = far;
    }

    /// <summary>Gets the view direction.</summary>
    public readonly (double x, double y, double z) Forward =>
        (-Math.Sin(Yaw) * Math.Cos(Pitch), Math.Cos(Yaw) * Math.Cos(Pitch), Math.Sin(Pitch));

    // The level right vector (yaw only) and the up vector that goes with it, before the roll.
    private readonly (double x, double y, double z) LevelUp
    {
        get
        {
            var (x, y, z) = Forward;
            double rx = Math.Cos(Yaw), ry = Math.Sin(Yaw);
            return (ry * z, -rx * z, (rx * y) - (ry * x)); // right x forward
        }
    }

    /// <summary>Gets the right vector, rolled about the view axis.</summary>
    public readonly (double x, double y, double z) Right
    {
        get
        {
            if (Roll == 0)
                return (Math.Cos(Yaw), Math.Sin(Yaw), 0);
            var (x, y, z) = LevelUp;
            double c = Math.Cos(Roll), s = Math.Sin(Roll);
            return ((Math.Cos(Yaw) * c) - (x * s), (Math.Sin(Yaw) * c) - (y * s), -z * s);
        }
    }

    /// <summary>Gets the up vector, rolled about the view axis.</summary>
    public readonly (double x, double y, double z) Up
    {
        get
        {
            var (x, y, z) = Forward;
            var r = Right;
            return ((r.y * z) - (r.z * y), (r.z * x) - (r.x * z), (r.x * y) - (r.y * x)); // right x forward
        }
    }

    /// <summary>Gets the pixels from the centre of the view to the image plane at distance one along the view axis.</summary>
    public readonly double FocalLength => Width / 2.0 / Math.Tan(HorizontalFov / 2);

    /// <summary>Gets the distance at which a radius projects to a number of pixels (docs/world-render.md 2.4).</summary>
    /// <param name="pixels">The projected size in pixels.</param>
    /// <param name="radius">The world radius.</param>
    /// <returns>The distance.</returns>
    public readonly double ProjectionDistance(double pixels, double radius) => radius * FocalLength / pixels;
    /// <summary>Gets the projected size in pixels of a radius at a distance (docs/world-render.md 2.4).</summary>
    /// <param name="distance">The distance.</param>
    /// <param name="radius">The world radius.</param>
    /// <returns>The size in pixels.</returns>
    public readonly double ProjectRadius(double distance, double radius) => radius * FocalLength / distance;

    /// <summary>Transforms a world point to camera space: x right, y forward (depth), z up (docs/world-render.md 1.3).</summary>
    /// <param name="wx">The world x.</param>
    /// <param name="wy">The world y.</param>
    /// <param name="wz">The world z.</param>
    /// <returns>The camera-space point.</returns>
    public readonly (double x, double y, double z) ToCamera(double wx, double wy, double wz)
    {
        double dx = wx - X, dy = wy - Y, dz = wz - Z;
        var (x, y, z) = Right;
        var f = Forward;
        var u = Up;
        return ((dx * x) + (dy * y) + (dz * z), (dx * f.x) + (dy * f.y) + (dz * f.z), (dx * u.x) + (dy * u.y) + (dz * u.z));
    }

    /// <summary>Projects a world point to the surface.</summary>
    /// <param name="wx">The world x.</param>
    /// <param name="wy">The world y.</param>
    /// <param name="wz">The world z.</param>
    /// <param name="sx">Receives the surface x in pixels, origin top left.</param>
    /// <param name="sy">Receives the surface y in pixels.</param>
    /// <param name="depth">Receives near / distance, 0 to 1, 1 at the near plane (reversed).</param>
    /// <returns>False behind the near plane.</returns>
    public readonly bool Project(double wx, double wy, double wz, out double sx, out double sy, out double depth)
    {
        var (cx, cy, cz) = ToCamera(wx, wy, wz);
        sx = sy = depth = 0;
        if (cy < Near)
            return false;
        var fl = FocalLength;
        sx = (Width / 2.0) + (cx * fl / cy);
        sy = (Height / 2.0) - (cz * fl / cy);
        depth = Near / cy;
        return true;
    }

    /// <summary>Builds the column-major view-projection matrix for OpenGL (clip.w is the depth along the view axis).</summary>
    /// <param name="m">Receives 16 floats.</param>
    public readonly void ViewProjection(Span<float> m)
    {
        var (x, y, z) = Right;
        var f = Forward;
        var u = Up;
        double fx = 2 * FocalLength / Width, fy = 2 * FocalLength / Height;
        // reversed depth (renderer.md 5.2): the device stores near / d, so ndc z = 2 near / d - 1 and clip z = 2 near - w
        double a = -1, b = 2 * Near;
        double pr = (x * X) + (y * Y) + (z * Z), pu = (u.x * X) + (u.y * Y) + (u.z * Z), pf = (f.x * X) + (f.y * Y) + (f.z * Z);
        // rows: x = fx * right, y = fy * up, z = a * forward + b (a = -1), w = forward; stored column-major
        double[] c0 = [fx * x, fy * u.x, a * f.x, f.x];
        double[] c1 = [fx * y, fy * u.y, a * f.y, f.y];
        double[] c2 = [fx * z, fy * u.z, a * f.z, f.z];
        double[] c3 = [-fx * pr, -fy * pu, (-a * pf) + b, -pf];
        for (var i = 0; i < 4; ++i)
        {
            m[i] = (float)c0[i];
            m[4 + i] = (float)c1[i];
            m[8 + i] = (float)c2[i];
            m[12 + i] = (float)c3[i];
        }
    }
}
