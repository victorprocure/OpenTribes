namespace OpenTribes.Client.Shapes;

using System.Globalization;
using OpenTribes.Client.Scene;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;

/// <summary>The shape viewer: one shape on a gray background, framed by its radius, for looking at the shape renderer without a mission.</summary>
/// <remarks>Started with <c>tribes --shape NAME [--palette PPL] [--seq NAME] [--at P] [--camera YAW,PITCH] --screenshot OUT.png</c>.</remarks>
public static class ShapeView
{
    /// <summary>Renders the shape to a PNG file.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="shapeName">The shape file name.</param>
    /// <param name="palette">The palette file name, or null for lush.day.ppl.</param>
    /// <param name="seq">The sequence to pose, or null for none.</param>
    /// <param name="pos">The sequence position, 0 to 1.</param>
    /// <param name="camera">The camera as <c>YAW,PITCH</c>, or null for the default view.</param>
    /// <param name="output">The PNG path. Required.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="videoDriver">The video driver name, or null for the default.</param>
    /// <param name="skin">The skin name, or empty for the base skin.</param>
    /// <returns>The process exit code: 0 on success, 1 on a failure, 2 for a missing output path.</returns>
    public static int Run(string root, string shapeName, string? palette, string? seq, double pos, string? camera, string? output, int width, int height, string? videoDriver, string skin = "")
    {
        if (output == null)
        {
            Console.Error.WriteLine("tribes: --shape needs --screenshot");
            return 2;
        }
        var res = GfxAssets.Mount(root, out _);
        var bank = GfxAssets.LoadPalette(res, palette ?? "lush.day.ppl") ?? PaletteTables.Grayscale();
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out var error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        GlWindow.SetSwapInterval(0);
        dev.SetPalette(bank);
        var world = new WorldAssets(res, bank);
        using var assets = new ShapeAssets(dev, world);
        var shape = assets.Shape(shapeName);
        if (shape == null)
        {
            Console.Error.WriteLine("tribes: shape not found: " + shapeName);
            return 1;
        }
        var inst = new RenderInstance(shape);
        if (seq != null)
        {
            var t = inst.AddThread(seq, pos, 0);
            if (t == null)
            {
                Console.Error.WriteLine("tribes: no sequence " + seq);
                return 1;
            }
            t.TimeScale = 0;
        }
        double yaw = 30, pitch = -15;
        if (camera != null)
        {
            var p = camera.Split(',');
            yaw = double.Parse(p[0], CultureInfo.InvariantCulture);
            pitch = double.Parse(p[1], CultureInfo.InvariantCulture);
        }
        var renderer = new ShapeRenderer(assets);
        if (Environment.GetEnvironmentVariable("OT_SHAPE_DEBUG") != null)
        {
            inst.Animate(0);
            for (var i = 0; i < Math.Min(shape.Nodes.Length, 12); ++i)
                Console.Error.WriteLine($"node {i} {shape.Names[shape.Nodes[i].Name]} world p=({inst.NodeWorld[i].Px:F2},{inst.NodeWorld[i].Py:F2},{inst.NodeWorld[i].Pz:F2}) m0={inst.NodeWorld[i].M0:F2}");
            foreach (var (o, k) in shape.Objects.Select((o, k) => (o, k)).Take(8))
                Console.Error.WriteLine($"object {k} node {o.Node} visible {inst.Objects[k].Visible} mesh {o.Mesh} faces {(o.Mesh >= 0 ? shape.Meshes[o.Mesh].Faces.Length : -1)}");
        }
        double r = Math.Max(shape.Radius, 0.5), dist = r * 2.6;
        double cy = yaw * Math.PI / 180, cp = pitch * Math.PI / 180;
        double fx = -Math.Sin(cy) * Math.Cos(cp), fy = Math.Cos(cy) * Math.Cos(cp), fz = Math.Sin(cp);
        double tx = shape.Cx, ty = shape.Cy, tz = shape.Cz;
        var cam = new Camera3D(tx - (fx * dist), ty - (fy * dist), tz - (fz * dist), yaw, pitch, 60, dev.Width, dev.Height, 0.1, 1000);
        var ctx = new FrameContext { Camera = cam, Assets = assets, Renderer = renderer, Light = LightSet.Default() };
        var d = ctx.Draw(Xf.Identity, shapeName);
        d.Skin = skin;
        d.ForceDetail = 0;
        var img = renderer.Build(inst, cam, d);
        if (img != null)
            ctx.Lists.Add(img);
        dev.BeginFrame();
        dev.ClearRgb(110, 120, 130);
        dev.BeginScene3D(cam, 0.7f, 0.7f, 0.7f);
        var draws = ctx.Lists.Draw(dev);
        dev.EndScene3D();
        dev.EndFrame();
        MissionRender.WritePng(output, dev.ReadPixels(), dev.Width, dev.Height);
        Console.Error.WriteLine($"{shapeName}: faces {renderer.LastFaces} culled {renderer.LastCulled} batches {img?.Batches.Count ?? 0} draws {draws} missing {string.Join(" ", assets.Missing)} -> {output}");
        return 0;
    }
}
