using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

namespace OpenTribes.Tests;

// world-render.md 3.7 Timing: every interior instance holds its own light state; instances in the same state share a light atlas.
public class InteriorLightStateTests
{
    private sealed class Tex : IGfxTexture
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public void Dispose() { }
    }

    private sealed class RecDevice : IGfxDevice
    {
        public readonly List<IGfxTexture?> LightMaps = new();
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) { }
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) { }
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) { }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => Array.Empty<byte>();
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => new Tex { Width = width, Height = height };
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) => LightMaps.Add(state.LightMap);
        public void EndScene3D() { }
    }

    // A cube face with one light that runs on the client: state 0 black, state 1 lit from 0.05 s, a cycle of two steps.
    private static InteriorLodModel Lod()
    {
        var geo = new InteriorGeometryData { Box = new float[] { -2, -2, -2, 2, 2, 2 } };
        geo.Point3 = new float[] { -2, -2, -2, 2, -2, -2, 2, -2, 2, -2, -2, 2 };
        geo.Point2 = new float[] { 0, 1, 1, 1, 1, 0, 0, 0 };
        geo.VertexPoint3 = new ushort[] { 0, 3, 2, 1 }; // clockwise seen from the front side, as the stock surfaces
        geo.VertexPoint2 = new ushort[] { 0, 3, 2, 1 };
        geo.Planes = new float[] { 0, -1, 0, -2 };
        geo.Surfaces = new[]
        {
            new InteriorGeometryData.Surface { Flags = 0x80, Material = 255, TexSizeX = 3, TexSizeY = 3, Plane = 0, FirstVertex = 0, VertexCount = 4 },
        };
        var map = new byte[9];
        Array.Fill(map, (byte)255);
        var lt = new InteriorLighting
        {
            LightScaleShift = 4,
            Surfaces = new[] { new InteriorLighting.Surface { Word = 0x80000000, SizeX = 3, SizeY = 3, LightCount = 1 } },
            States = new[]
            {
                new InteriorLighting.State { EndTime = 0f, DataCount = 1, DataIndex = 0 },
                new InteriorLighting.State { R = 0xff00, G = 0xff00, B = 0xff00, Mono = 0xff00, EndTime = 0.05f, DataCount = 1, DataIndex = 1 },
            },
            StateDatas = new[]
            {
                new InteriorLighting.StateData { Surface = 0, Slot = 0, MapIndex = 0 },
                new InteriorLighting.StateData { Surface = 0, Slot = 0, MapIndex = 0 },
            },
            Lights = new[] { new InteriorLighting.Light { StateCount = 2, FirstState = 0, Duration = 0.134f, Flags = 1, Name = "pulse" } },
            MapData = map,
        };
        return new InteriorLodModel { Geo = geo, Base = lt };
    }

    private static List<IGfxTexture?> Frame(RecDevice dev, InteriorRenderer r, double sim)
    {
        dev.LightMaps.Clear();
        r.SimSeconds = sim;
        var cam = new Camera3D(40, 100, 10, 0, -15, 90, dev.Width, dev.Height, 0.5, 400);
        r.Draw(cam, TerrainParams.FromMission(400, 200, 5));
        return new List<IGfxTexture?>(dev.LightMaps);
    }

    [Fact]
    public void InstancesKeepTheirOwnLightPhaseAndShareEqualStates() // WR 3.7 Timing: the steps count from each instance's first tick
    {
        var dev = new RecDevice();
        var assets = new WorldAssets(new ResourceManager(Path.GetTempPath()), PaletteTables.Grayscale());
        using var r = new InteriorRenderer(dev, assets);
        var model = new InteriorModel { Name = "synthetic" };
        InteriorLodModel lod = Lod();
        r.Install(model, lod, 40, 120, 5, 0, 0, 0);
        // a second instance of the same lod, added a step later: its first tick is one light step after the first
        r.Instances.Add(new InteriorRenderer.Instance { Model = model, X = 40, Y = 120, Z = 5, M = InteriorRenderer.EulerMatrix(0, 0, 0) });

        // frame 0: both instances start now (step 0): one atlas for the two
        List<IGfxTexture?> f0 = Frame(dev, r, 10.0);
        Assert.Equal(2, f0.Count);
        Assert.NotNull(f0[0]);
        Assert.Same(f0[0], f0[1]);
        // a step later a third instance appears: its first tick is that frame
        r.Instances.Add(new InteriorRenderer.Instance { Model = model, X = 40, Y = 120, Z = 5, M = InteriorRenderer.EulerMatrix(0, 0, 0) });
        List<IGfxTexture?> f1 = Frame(dev, r, 10.0 + InteriorRenderer.LightStep);
        Assert.Equal(3, f1.Count);
        Assert.Same(f1[0], f1[1]);              // step 1: state 1 (lit)
        Assert.NotSame(f1[0], f1[2]);           // the instance that is at its own step 0: state 0
        Assert.Same(f0[0], f1[2]);              // an equal state shares the atlas of before
    }
}
