// PartDebris radius, end and split (docs/shapes-render.md 4.4): loadShape 0047603c, part box 005c3b18 / 005ad14c, end of a piece 004762e0,
// split cloud 004764f8 / 004765c0, row choice 00475edc, part split 005c4060 / 005c41ac.
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using Xunit;

namespace OpenTribes.Tests;

public class PartDebrisTests
{
    // A mesh with the packed vertices of its frames: 4 bytes each (x, y, z, normal), frame k reads the two vertices from FirstVert.
    private static RsMesh Mesh(params (byte[] v0, byte[] v1, float[] scale, float[] origin)[] frames)
    {
        var m = new RsMesh();
        var verts = new List<byte>();
        var fr = new List<RsFrame>();
        foreach (var f in frames)
        {
            fr.Add(new RsFrame { FirstVert = verts.Count / 4, Sx = f.scale[0], Sy = f.scale[1], Sz = f.scale[2], Ox = f.origin[0], Oy = f.origin[1], Oz = f.origin[2] });
            verts.AddRange(f.v0); verts.Add(0);
            verts.AddRange(f.v1); verts.Add(0);
        }
        m.Verts = verts.ToArray();
        m.Frames = fr.ToArray();
        return m;
    }

    [Fact]
    public void RadiusIsHalfTheBitTrickLengthOfTheFrameBoxSize() // 0047603c, 005ed031, 005c3b18
    {
        // box (-1, -0.5, -0.25)..(1, 0.5, 0.25): size (2, 1, 0.5), squared length 5.25. m_Point3F_lenf gives 2.3125 (bits >> 1 + 0x1fc00000), not 2.2913
        var m = Mesh((new byte[] { 0, 0, 0 }, new byte[] { 200, 100, 50 }, new[] { 0.01f, 0.01f, 0.01f }, new[] { -1f, -0.5f, -0.25f }));
        PartBox b = PartDebris.Measure(m, 0);
        Assert.Equal(1.15625f, b.Radius);
        Assert.NotEqual((float)(Math.Sqrt(5.25) * 0.5), b.Radius);
        Assert.InRange(Math.Abs(b.Cx) + Math.Abs(b.Cy) + Math.Abs(b.Cz), 0, 1e-6);   // a box symmetric about 0 (up to float rounding of the decode)
        Assert.InRange(Math.Abs(b.SphereX) + Math.Abs(b.SphereY) + Math.Abs(b.SphereZ), 0, 1e-6);   // the part box is centred (up to float rounding): the sphere sits on the piece origin
    }

    [Fact]
    public void BoxIsReadAtTheObjectsCurrentFrameAndTheCentreIsTheMiddleOfIt() // 005ad14c, 005c3b18
    {
        var m = Mesh((new byte[] { 0, 0, 0 }, new byte[] { 200, 100, 50 }, new[] { 0.01f, 0.01f, 0.01f }, new[] { -1f, -0.5f, -0.25f }),
                     (new byte[] { 10, 20, 30 }, new byte[] { 110, 220, 130 }, new[] { 0.02f, 0.01f, 0.05f }, new[] { 1f, 2f, 3f }));
        PartBox b = PartDebris.Measure(m, 1);
        Assert.Equal((1.2f, 2.2f, 4.5f), (b.MinX, b.MinY, b.MinZ));
        Assert.Equal((3.2f, 4.2f, 9.5f), (b.MaxX, b.MaxY, b.MaxZ), new BoxComparer());
        Assert.Equal(2.2f, b.Cx);
        Assert.Equal(7f, b.Cz);
        Assert.Equal(3.03125f, b.Radius, 1e-5f);   // size (2, 2, 5), squared length 33: the bit trick root 6.0625 halved (the exact half diagonal is 2.872)
        Assert.Equal(PartDebris.Measure(m, 1).Radius, PartDebris.Measure(m, 7).Radius);   // an index past the mesh clamps to the last frame
    }

    private sealed class BoxComparer : IEqualityComparer<(float, float, float)>
    {
        public bool Equals((float, float, float) a, (float, float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-5 && Math.Abs(a.Item2 - b.Item2) < 1e-5 && Math.Abs(a.Item3 - b.Item3) < 1e-5;
        public int GetHashCode((float, float, float) o) => 0;
    }
}
