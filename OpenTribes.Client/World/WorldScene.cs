using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

namespace OpenTribes.Client.World;

// world-render.md 1.1: the frame order of the 3D view, for a mission loaded locally (no server): background (sky, stars,
// planet), then the world (terrain, interiors).
public sealed class WorldScene : IDisposable
{
    // Open (world-render.md Q1): near plane, field of view and far plane are not transcribed from the camera slots.
    public const double NearPlane = 0.1;
    public const double DefaultFov = 90;      // $pref::PlayerFov default, taken as the horizontal angle
    public const double BackgroundFar = 40000; // covers planet distances up to 11000

    public string MissionName = "";
    public TerrainParams Terrain;
    public (float r, float g, float b) HazeColor;
    public TerrainRenderer? TerrainDraw;
    public InteriorRenderer? Interiors;
    public SkyRenderer? Sky;
    public List<StarField.Star> Stars = new();
    public bool StarsInFront = true;
    public (float, float, float)[] StarColors = { (1, 1, 1), (.5f, .5f, .5f), (.25f, .25f, .25f) };
    public readonly List<PlanetRenderer> Planets = new();
    public int InteriorCount, InteriorFailed, TerrainBlockSize;
    public double TerrainOriginX, TerrainOriginY;
    public (float r, float g, float b) ClearColor = (0, 0, 0);
    // The first Planet's light for shapes (world-render.md 4.3, 5): azimuth and incidence in degrees, intensity and ambient colours.
    public double SunAzimuth, SunIncidence = 60;
    public double[] SunIntensity = { 0.55, 0.55, 0.55 }, SunAmbient = { 0.45, 0.45, 0.45 };
    public WorldAssets Assets = null!;
    public OpenTribes.World.Terrain? TerrainModel;   // for ground queries of the effects (footprints, bullet holes)
    private readonly IGfxDevice mDev;

    private WorldScene(IGfxDevice dev) => mDev = dev;

    // Mounts the volumes of the mission's `Volumes` group in file order (later volumes win) and builds every renderer.
    public static WorldScene Load(IGfxDevice dev, string root, string missionName, out string error)
    {
        error = "";
        var scene = new WorldScene(dev) { MissionName = missionName };
        string missions = Path.Combine(root, "base", "missions");
        string? file = Directory.EnumerateFiles(missions, "*.mis", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(missionName, StringComparison.OrdinalIgnoreCase));
        if (file == null)
        {
            error = "mission not found: " + missionName;
            return scene;
        }
        List<MissionObject> roots = MissionFile.Parse(File.ReadAllText(file, System.Text.Encoding.Latin1));
        if (roots.Count == 0)
        {
            error = "no objects in " + file;
            return scene;
        }
        var all = new List<MissionObject>();
        foreach (MissionObject r in roots)
        {
            all.Add(r);
            all.AddRange(r.Descendants());
        }
        var res = new ResourceManager(root);
        res.SetSearchPath("config;base;base\\missions");
        foreach (MissionObject o in all.Where(o => o.Class == "SimVolume"))
            res.AddVolume(o.Get("fileName"));
        return LoadFrom(dev, res, missionName, all, out error);
    }

    // The renderers of a world from its objects and an already mounted resource manager. A demo builds the objects from the
    // environment ghosts of the stream (SimVolume, SimPalette, SimTerrain, Sky, StarField, Planet, InteriorShape).
    public static WorldScene LoadFrom(IGfxDevice dev, ResourceManager res, string missionName, List<MissionObject> all, out string error)
    {
        error = "";
        var scene = new WorldScene(dev) { MissionName = missionName };
        MissionObject? pal = all.FirstOrDefault(o => o.Class == "SimPalette");
        PaletteBank? bank = pal != null ? GfxAssets.LoadPalette(res, pal.Get("fileName")) : null;
        bank ??= PaletteTables.Grayscale();
        scene.Assets = new WorldAssets(res, bank);
        scene.HazeColor = scene.Assets.HazeColor();

        MissionObject? terrain = all.FirstOrDefault(o => o.Class == "SimTerrain");
        if (terrain != null)
            scene.LoadTerrain(dev, res, terrain, ref error);
        else
            scene.Terrain = TerrainParams.FromMission(3000, 2000, 120);

        scene.Interiors = new InteriorRenderer(dev, scene.Assets);
        foreach (MissionObject o in all.Where(o => o.Class == "InteriorShape"))
        {
            double[] p = o.GetVector("position"), r = o.GetVector("rotation");
            ++scene.InteriorCount;
            if (!scene.Interiors.AddInstance(o.Get("fileName"), p[0], p[1], p[2], r[0], r[1], r[2]))
                ++scene.InteriorFailed;
        }

        scene.Interiors.Preload();
        scene.TerrainDraw?.Warm();

        double visible = scene.Terrain.Visible;
        MissionObject? sky = all.FirstOrDefault(o => o.Class == "Sky");
        if (sky != null)
        {
            var tex = new int[SkyRenderer.Panels];
            for (int i = 0; i < tex.Length; ++i)
                tex[i] = (int)sky.GetDouble($"textures[{i}]", i % 2);
            string dml = sky.Get("dmlName");
            List<string?>? mats = dml.Length > 0 ? scene.Assets.MaterialList(dml) : null;
            double[] sc = sky.GetVector("skyColor"), hc = sky.GetVector("hazeColor");
            (float, float, float) C(double[] v) => ((float)Math.Clamp(Finite(v[0]), 0, 1), (float)Math.Clamp(Finite(v[1]), 0, 1), (float)Math.Clamp(Finite(v[2]), 0, 1));
            scene.Sky = new SkyRenderer(dev, scene.Assets, mats, tex, sky.GetDouble("size", 600), sky.GetDouble("featurePosition", 0), visible, C(sc), C(hc));
            scene.ClearColor = scene.Sky.Textured ? scene.Sky.SolidColor : C(sc);
        }
        MissionObject? stars = all.FirstOrDefault(o => o.Class == "StarField");
        if (stars != null)
        {
            scene.Stars = StarField.Generate(StarField.DefaultSeed, 1.05 * visible);
            scene.StarsInFront = stars.GetBool("inFrontOfSky", true);
            var cols = new (float, float, float)[3];
            for (int i = 0; i < 3; ++i)
            {
                double[] c = stars.GetVector($"colors[{i}]");
                // 4.2 Init: nearest palette entry of the colour
                int idx = bank.NearestColor((int)(Math.Clamp(Finite(c[0]), 0, 1) * 255), (int)(Math.Clamp(Finite(c[1]), 0, 1) * 255), (int)(Math.Clamp(Finite(c[2]), 0, 1) * 255));
                bank.SubPalettes[0].Rgb(idx, out byte rr, out byte gg, out byte bb);
                cols[i] = (rr / 255f, gg / 255f, bb / 255f);
            }
            scene.StarColors = cols;
        }
        MissionObject? sun = all.FirstOrDefault(o => o.Class == "Planet");
        if (sun != null)
        {
            scene.SunAzimuth = sun.GetDouble("azimuth", 0);
            scene.SunIncidence = sun.GetDouble("incidence", 60);
            scene.SunIntensity = sun.GetVector("intensity");
            scene.SunAmbient = sun.GetVector("ambient");
        }
        foreach (MissionObject o in all.Where(o => o.Class == "Planet"))
        {
            string bmp = o.Get("fileName");
            if (bmp.Length == 0)
                continue;
            var pr = new PlanetRenderer(dev, scene.Assets, bmp, o.GetDouble("azimuth", 0), o.GetDouble("incidence", 30), o.GetDouble("size", 2000), o.GetDouble("distance", 11000));
            if (pr.HasBitmap)
                scene.Planets.Add(pr);
            else
                pr.Dispose();
        }
        return scene;
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;

    private void LoadTerrain(IGfxDevice dev, ResourceManager res, MissionObject o, ref string error)
    {
        string ted = o.Get("tedFileName");
        Terrain = TerrainParams.FromMission(o.GetDouble("visibleDistance", 3000), o.GetDouble("hazeDistance", 2000), o.GetDouble("screenSize", 120));
        OpenTribes.World.Terrain? t = OpenTribes.World.Terrain.Load(res, ted);
        if (t == null)
        {
            error = "terrain not loadable: " + ted;
            return;
        }
        double[] tpos = o.GetVector("position");
        t.SetPosition(new OpenTribes.World.Vec3((float)tpos[0], (float)tpos[1], (float)tpos[2]));
        TerrainModel = t;
        int dot = ted.LastIndexOf('.');
        string stem = dot < 0 ? ted : ted[..dot];
        var block = new GridBlock();
        if (!res.Read(stem + "#0.dtb", out byte[] dtb) || !GridBlock.Parse(dtb, block))
        {
            error = "terrain block not loadable: " + stem;
            return;
        }
        string dml = t.MaterialListName();
        int mdot = dml.LastIndexOf('.');
        dml = (mdot <= 0 ? dml : dml[..mdot]) + ".dml";
        List<string?> mats = Assets.MaterialList(dml) ?? new List<string?>();
        ushort[] light = TerrainData.ReadLightMap(dtb, block.Size);
        var data = new TerrainData(block, mats.Count, light);
        double[] pos = o.GetVector("position");
        TerrainBlockSize = block.Size;
        (TerrainOriginX, TerrainOriginY) = (pos[0], pos[1]);
        TerrainDraw = new TerrainRenderer(dev, Assets, data, mats, pos[0], pos[1], pos[2], Terrain);
    }

    // The camera of a render: near and far follow the terrain's visible distance for the world, a long far for the
    // background (open: world-render.md Q1).
    public Camera3D MakeCamera(double x, double y, double z, double yaw, double pitch, double fov, int w, int h, bool background) =>
        new(x, y, z, yaw, pitch, fov, w, h, NearPlane, background ? BackgroundFar : Math.Max(Terrain.Visible, 10));

    public void Render(double x, double y, double z, double yaw, double pitch, double fov, Action<Camera3D>? overlay = null, Action? overlay2D = null)
    {
        int w = mDev.Width, h = mDev.Height;
        mDev.BeginFrame();
        mDev.ClearRgb((byte)(ClearColor.r * 255), (byte)(ClearColor.g * 255), (byte)(ClearColor.b * 255));
        Camera3D bg = MakeCamera(x, y, z, yaw, pitch, fov, w, h, true);
        Camera3D world = MakeCamera(x, y, z, yaw, pitch, fov, w, h, false);
        mDev.BeginScene3D(bg, HazeColor.r, HazeColor.g, HazeColor.b);
        using (FramePerf.Scope("sky"))
        {
            if (!StarsInFront && Stars.Count > 0)
                StarField.Draw(mDev, bg, Stars, StarColors);
            Sky?.Draw(bg);
            if (StarsInFront && Stars.Count > 0)
                StarField.Draw(mDev, bg, Stars, StarColors);
            foreach (PlanetRenderer p in Planets)
                p.Draw(bg);
        }
        mDev.SetCamera3D(world);
        using (FramePerf.Scope("terrain"))
            TerrainDraw?.Draw(world);
        using (FramePerf.Scope("interiors"))
            Interiors?.Draw(world, Terrain);
        overlay?.Invoke(world);
        mDev.EndScene3D();
        using (FramePerf.Scope("gui-draw"))
            overlay2D?.Invoke();
        using (FramePerf.Scope("present"))
            mDev.EndFrame();
    }

    public void Dispose()
    {
        TerrainDraw?.Dispose();
        Interiors?.Dispose();
        Sky?.Dispose();
        foreach (PlanetRenderer p in Planets)
            p.Dispose();
    }
}
