namespace OpenTribes.Client.World;

using System.Globalization;
using System.IO.Compression;
using OpenTribes.Platform.Gl;

/// <summary>The local mission renderer: loads a mission and renders one frame per camera to an image file.</summary>
/// <remarks>Started with <c>tribes --mission</c>.</remarks>
public static class MissionRender
{
    /// <summary>A named camera.</summary>
    /// <param name="Name">The camera name, used in the output file name.</param>
    /// <param name="X">The x position.</param>
    /// <param name="Y">The y position.</param>
    /// <param name="Z">The z position.</param>
    /// <param name="Yaw">The yaw angle.</param>
    /// <param name="Pitch">The pitch angle.</param>
    /// <param name="Fov">The field of view.</param>
    public readonly record struct CameraSpec(string Name, double X, double Y, double Z, double Yaw, double Pitch, double Fov);

    /// <summary>Parses a camera of the form <c>X,Y,Z,YAW,PITCH[,FOV]</c>.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="name">The camera name.</param>
    /// <param name="defaultFov">The field of view when the text has none.</param>
    /// <param name="cam">The camera on success.</param>
    /// <returns>True when the text parsed.</returns>
    public static bool TryParseCamera(string text, string name, double defaultFov, out CameraSpec cam)
    {
        cam = default;
        var p = text.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length is < 5 or > 6)
            return false;
        var v = new double[6];
        v[5] = defaultFov;
        for (var i = 0; i < p.Length; ++i)
            if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return false;
        cam = new CameraSpec(name, v[0], v[1], v[2], v[3], v[4], v[5]);
        return true;
    }

    /// <summary>Parses a camera file with one camera per line: <c>NAME X,Y,Z,YAW,PITCH[,FOV]</c>. Blank lines and lines starting with <c>#</c> or <c>//</c> are skipped.</summary>
    /// <param name="text">The file text.</param>
    /// <param name="defaultFov">The field of view for lines without one.</param>
    /// <param name="error">The error text, or empty when the file parsed.</param>
    /// <returns>The cameras.</returns>
    public static List<CameraSpec> ParseCameraFile(string text, double defaultFov, out string error)
    {
        error = "";
        var list = new List<CameraSpec>();
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            ++n;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;
            var sp = line.IndexOfAny([' ', '\t']);
            if (sp < 0 || !TryParseCamera(line[(sp + 1)..].Trim(), line[..sp], defaultFov, out CameraSpec c))
            {
                error = $"camera file line {n}: expected NAME X,Y,Z,YAW,PITCH[,FOV]";
                return [];
            }
            list.Add(c);
        }
        return list;
    }

    /// <summary>Loads a mission and renders it from each camera to PNG files.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="mission">The mission file name.</param>
    /// <param name="camera">One camera as <c>X,Y,Z,YAW,PITCH[,FOV]</c>, or null.</param>
    /// <param name="camerasFile">A camera file, or null.</param>
    /// <param name="fov">The default field of view.</param>
    /// <param name="output">The output file, or a directory when several cameras render.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="videoDriver">The video driver name, or null for the default.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string root, string mission, string? camera, string? camerasFile, double fov, string? output, int width,
                          int height, string? videoDriver)
    {
        var cams = new List<CameraSpec>();
        if (camera != null)
        {
            if (!TryParseCamera(camera, "camera", fov, out CameraSpec c))
            {
                Console.Error.WriteLine("tribes: --camera expects X,Y,Z,YAW,PITCH");
                return 2;
            }
            cams.Add(c);
        }
        if (camerasFile != null)
        {
            List<CameraSpec> fromFile = ParseCameraFile(File.ReadAllText(camerasFile), fov, out var cerr);
            // A file shared by several missions names its cameras `<Mission>_<label>`: only those of this mission render.
            if (fromFile.Any(c => c.Name.StartsWith(mission + "_", StringComparison.OrdinalIgnoreCase)))
                fromFile = [.. fromFile.Where(c => c.Name.StartsWith(mission + "_", StringComparison.OrdinalIgnoreCase))];
            cams.AddRange(fromFile);
            if (cerr.Length > 0)
            {
                Console.Error.WriteLine("tribes: " + cerr);
                return 2;
            }
        }
        if (cams.Count == 0 || output == null)
        {
            Console.Error.WriteLine("tribes: --mission needs --camera or --cameras, and --screenshot");
            return 2;
        }
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out var error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        GlWindow.SetSwapInterval(0);
        using WorldScene scene = WorldScene.Load(dev, root, mission, out var loadError);
        if (loadError.Length > 0)
            Console.Error.WriteLine("tribes: " + loadError);
        dev.SetPalette(scene.Assets?.Palette ?? Platform.Gfx.PaletteTables.Grayscale());
        Console.Error.WriteLine($"{mission}: visible {scene.Terrain.Visible} hazeStart {scene.Terrain.HazeStart:F1} block {scene.TerrainBlockSize} origin {scene.TerrainOriginX},{scene.TerrainOriginY} interiors {scene.InteriorCount} (failed {scene.InteriorFailed}) planets {scene.Planets.Count} stars {scene.Stars.Count}");
        var dir = camerasFile != null;
        if (dir)
            Directory.CreateDirectory(output);
        foreach (CameraSpec c in cams)
        {
            scene.Render(c.X, c.Y, c.Z, c.Yaw, c.Pitch, c.Fov);
            var px = dev.ReadPixels();
            var path = dir ? Path.Combine(output, c.Name + ".png") : output;
            if (path.EndsWith(".ppm", StringComparison.OrdinalIgnoreCase))
                Program.WritePpm(path, px, dev.Width, dev.Height);
            else
                WritePng(path, px, dev.Width, dev.Height);
            Console.Error.WriteLine($"  {c.Name}: terrain squares {scene.TerrainDraw?.LastSquares} tris {scene.TerrainDraw?.LastTriangles} interiors drawn {scene.Interiors?.DrawnInstances} surfaces {scene.Interiors?.DrawnSurfaces} -> {path}");
        }
        return 0;
    }

    private static readonly uint[] sCrc = MakeCrc();

    private static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; ++n)
        {
            var c = n;
            for (var k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xffffffffu;
        foreach (var b in type)
            c = sCrc[(c ^ b) & 0xff] ^ (c >> 8);
        foreach (var b in data)
            c = sCrc[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }

    /// <summary>Writes an 8 bit RGB PNG (filter 0, zlib) from RGBA rows, top row first.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="rgba">The RGBA pixels.</param>
    /// <param name="w">The width in pixels.</param>
    /// <param name="h">The height in pixels.</param>
    public static void WritePng(string path, byte[] rgba, int w, int h)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
        {
            var row = new byte[1 + (w * 3)];
            for (var y = 0; y < h; ++y)
            {
                row[0] = 0;
                for (var x = 0; x < w; ++x)
                {
                    row[1 + (x * 3)] = rgba[((y * w) + x) * 4];
                    row[2 + (x * 3)] = rgba[(((y * w) + x) * 4) + 1];
                    row[3 + (x * 3)] = rgba[(((y * w) + x) * 4) + 2];
                }
                z.Write(row);
            }
        }
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        void Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            f.Write(len);
            f.Write(t);
            f.Write(data);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data));
            f.Write(len);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", []);
    }
}
