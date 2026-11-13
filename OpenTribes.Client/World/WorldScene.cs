namespace OpenTribes.Client.World;

using OpenTribes.Client.Shapes;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

/// <summary>The frame order of the 3D view for a mission loaded locally (no server): background (sky, stars, planet), then the world (terrain, interiors).</summary>
/// <remarks>world-render.md 1.1.</remarks>
public sealed class WorldScene : IDisposable
{
    /// <summary>The near plane of a view that is not first person.</summary>
    /// <remarks>Open (world-render.md Q1): the field of view and the far plane are not transcribed from the camera slots. The near plane is the camera query answer of the PSC (0.01 or 0.2, see <see cref="ViewNear"/>), and the device takes it as the scale of the stored depth: z = near / d (setDepthRange 005daa14 gets camera +0x178 from FUN_004fb0e4).</remarks>
    public const double NearPlane = 0.01;
    /// <summary>The near plane of a first person view.</summary>
    public const double FirstPersonNearPlane = 0.2;
    /// <summary>The default field of view, the <c>$pref::PlayerFov</c> default, taken as the horizontal angle.</summary>
    public const double DefaultFov = 90;
    /// <summary>The far plane of the background, which covers planet distances up to 11000.</summary>
    public const double BackgroundFar = 40000;

    /// <summary>The mission name.</summary>
    public string MissionName = "";
    /// <summary>The terrain parameters.</summary>
    public TerrainParams Terrain;
    /// <summary>The haze colour.</summary>
    public (float r, float g, float b) HazeColor;
    /// <summary>The terrain renderer, or null.</summary>
    public TerrainRenderer? TerrainDraw;
    /// <summary>The interior renderer, or null.</summary>
    public InteriorRenderer? Interiors;
    /// <summary>The sky renderer, or null.</summary>
    public SkyRenderer? Sky;
    /// <summary>The stars.</summary>
    public List<StarField.Star> Stars = [];
    /// <summary>True to draw the stars after the sky, false before it.</summary>
    public bool StarsInFront = true;
    /// <summary>The colours of the star brightness classes.</summary>
    public (float, float, float)[] StarColors = [(1, 1, 1), (.5f, .5f, .5f), (.25f, .25f, .25f)];
    /// <summary>The planets.</summary>
    public readonly List<PlanetRenderer> Planets = [];
    /// <summary>The number of interiors loaded.</summary>
    public int InteriorCount;
    /// <summary>The number of interiors that failed to load.</summary>
    public int InteriorFailed;
    /// <summary>The squares per side of the terrain block.</summary>
    public int TerrainBlockSize;
    /// <summary>The x of the terrain origin.</summary>
    public double TerrainOriginX;
    /// <summary>The y of the terrain origin.</summary>
    public double TerrainOriginY;
    /// <summary>The clear colour of the frame.</summary>
    public (float r, float g, float b) ClearColor = (0, 0, 0);
    /// <summary>The azimuth in degrees of the light of the first Planet for shapes (world-render.md 4.3, 5).</summary>
    public double SunAzimuth;
    /// <summary>The incidence in degrees of the light of the first Planet.</summary>
    public double SunIncidence = 60;
    /// <summary>The sun colour intensity.</summary>
    public double[] SunIntensity = [0.55, 0.55, 0.55];
    /// <summary>The ambient colour.</summary>
    public double[] SunAmbient = [0.45, 0.45, 0.45];
    /// <summary>The world assets.</summary>
    public WorldAssets Assets = null!;
    /// <summary>The terrain for ground queries of the effects (footprints, bullet holes).</summary>
    public Terrain? TerrainModel;
    private readonly IGfxDevice mDev;

    private WorldScene(IGfxDevice dev) => mDev = dev;

    /// <summary>Loads a mission: mounts the volumes of its <c>Volumes</c> group in file order (later volumes win) and builds every renderer.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="root">The game root directory.</param>
    /// <param name="missionName">The mission name.</param>
    /// <param name="error">The error text, or empty on success.</param>
    /// <returns>The scene.</returns>
    public static WorldScene Load(IGfxDevice dev, string root, string missionName, out string error)
    {
        error = "";
        var scene = new WorldScene(dev) { MissionName = missionName };
        var missions = Path.Combine(root, "base", "missions");
        var file = Directory.EnumerateFiles(missions, "*.mis", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(missionName, StringComparison.OrdinalIgnoreCase));
        if (file == null)
        {
            error = "mission not found: " + missionName;
            return scene;
        }
        var roots = MissionFile.Parse(File.ReadAllText(file, System.Text.Encoding.Latin1));
        if (roots.Count == 0)
        {
            error = "no objects in " + file;
            return scene;
        }
        var all = new List<MissionObject>();
        foreach (var r in roots)
        {
            all.Add(r);
            all.AddRange(r.Descendants());
        }
        var res = new ResourceManager(root);
        res.SetSearchPath("config;base;base\\missions");
        foreach (var o in all.Where(o => o.Class == "SimVolume"))
            res.AddVolume(o.Get("fileName"));
        return LoadFrom(dev, res, missionName, all, out error);
    }

    /// <summary>Builds the renderers of a world from its objects and an already mounted resource manager. A demo builds the objects from the environment ghosts of the stream (SimVolume, SimPalette, SimTerrain, Sky, StarField, Planet, InteriorShape).</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="res">The mounted resources.</param>
    /// <param name="missionName">The mission name.</param>
    /// <param name="all">The mission objects.</param>
    /// <param name="error">The error text, or empty on success.</param>
    /// <param name="gamma">The display gamma.</param>
    /// <returns>The scene.</returns>
    public static WorldScene LoadFrom(IGfxDevice dev, ResourceManager res, string missionName, List<MissionObject> all, out string error, float gamma = 1f)
    {
        error = "";
        var scene = new WorldScene(dev) { MissionName = missionName };
        var pal = all.FirstOrDefault(o => o.Class == "SimPalette");
        var bank = pal != null ? GfxAssets.LoadPalette(res, pal.Get("fileName")) : null;
        bank ??= PaletteTables.Grayscale();
        scene.Assets = new WorldAssets(res, bank) { Gamma = gamma };
        scene.HazeColor = scene.Assets.HazeColor();

        var terrain = all.FirstOrDefault(o => o.Class == "SimTerrain");
        if (terrain != null)
            scene.LoadTerrain(dev, res, terrain, ref error);
        else
            scene.Terrain = TerrainParams.FromMission(3000, 2000, 120);

        scene.Interiors = new InteriorRenderer(dev, scene.Assets);
        if (scene.TerrainModel != null && all.FirstOrDefault(o => o.Class == "SimTerrain") is MissionObject tm)
        {
            // SimTerrain_updateBox 005193cc: x/y of the grid, z +-contHeight (10000 when unset) around the terrain z.
            var tb = new float[6];
            scene.TerrainModel.WorldBox(tb);
            var h = (float)tm.GetDouble("contHeight", 10000);
            var tz = (float)tm.GetVector("position")[2];
            if (h == 0)
                h = tb[5] - tz;
            tb[2] = tz - h;
            tb[5] = tz + h;
            scene.Interiors.TerrainBox = tb;
        }
        if (all.FirstOrDefault(o => o.Class == "Planet") is MissionObject planet)
        {
            // The planet light reaches the surfaces of interiors that see the outside (world-render.md 3.7 step 5).
            var light = new LightSet();
            light.SetSun(planet.GetDouble("azimuth", 0), planet.GetDouble("incidence", 60));
            double[] amb = planet.GetVector("ambient"), inten = planet.GetVector("intensity");
            light.AmbR = (float)amb[0];
            light.AmbG = (float)amb[1];
            light.AmbB = (float)amb[2];
            light.SunR = (float)inten[0];
            light.SunG = (float)inten[1];
            light.SunB = (float)inten[2];
            scene.Interiors.Sun = light;
        }
        foreach (var o in all.Where(o => o.Class == "InteriorShape"))
        {
            double[] p = o.GetVector("position"), r = o.GetVector("rotation");
            ++scene.InteriorCount;
            if (!scene.Interiors.AddInstance(o.Get("fileName"), p[0], p[1], p[2], r[0], r[1], r[2], (int)o.GetDouble("team", -1)))
                ++scene.InteriorFailed;
        }

        scene.Interiors.Preload();
        scene.TerrainDraw?.Warm();

        var visible = scene.Terrain.Visible;
        var sky = all.FirstOrDefault(o => o.Class == "Sky");
        if (sky != null)
        {
            var tex = new int[SkyRenderer.Panels];
            for (var i = 0; i < tex.Length; ++i)
                tex[i] = (int)sky.GetDouble($"textures[{i}]", i % 2);
            var dml = sky.Get("dmlName");
            var mats = dml.Length > 0 ? scene.Assets.MaterialList(dml) : null;
            double[] sc = sky.GetVector("skyColor"), hc = sky.GetVector("hazeColor");
            static (float, float, float) C(double[] v) => ((float)Math.Clamp(Finite(v[0]), 0, 1), (float)Math.Clamp(Finite(v[1]), 0, 1), (float)Math.Clamp(Finite(v[2]), 0, 1));
            scene.Sky = new SkyRenderer(dev, scene.Assets, mats, tex, sky.GetDouble("size", 600), sky.GetDouble("featurePosition", 0), visible, C(sc), C(hc));
            if (scene.Sky.Textured)
                scene.ClearColor = scene.Sky.SolidColor;
            else
            {
                // Solid sky (Sky_drawSolid 004779ac, colour pick 004778d4): the fill is the palette entry nearest to trunc(skyColor * 255),
                // not the colour itself (a night sky of 0.05 0.05 0.1 on lush.night.ppl is entry 69, 19 22 20, a dark green).
                var (cr, cg, cb) = C(sc);
                var idx = scene.Assets.Palette.NearestColor((int)(cr * 255f), (int)(cg * 255f), (int)(cb * 255f));
                scene.Assets.Palette.SubPalettes[0].Rgb(idx, out var pr, out var pg, out var pb);
                scene.ClearColor = (pr / 255f, pg / 255f, pb / 255f);
            }
        }
        var stars = all.FirstOrDefault(o => o.Class == "StarField");
        if (stars != null)
        {
            scene.Stars = StarField.Generate(StarField.DefaultSeed, 1.05 * visible);
            scene.StarsInFront = stars.GetBool("inFrontOfSky", true);
            var cols = new (float, float, float)[3];
            for (var i = 0; i < 3; ++i)
            {
                var c = stars.GetVector($"colors[{i}]");
                // 4.2 Init: nearest palette entry of the colour
                var idx = bank.NearestColor((int)(Math.Clamp(Finite(c[0]), 0, 1) * 255), (int)(Math.Clamp(Finite(c[1]), 0, 1) * 255), (int)(Math.Clamp(Finite(c[2]), 0, 1) * 255));
                bank.SubPalettes[0].Rgb(idx, out var rr, out var gg, out var bb);
                cols[i] = (rr / 255f, gg / 255f, bb / 255f);
            }
            scene.StarColors = cols;
        }
        var sun = all.FirstOrDefault(o => o.Class == "Planet");
        if (sun != null)
        {
            scene.SunAzimuth = sun.GetDouble("azimuth", 0);
            scene.SunIncidence = sun.GetDouble("incidence", 60);
            scene.SunIntensity = sun.GetVector("intensity");
            scene.SunAmbient = sun.GetVector("ambient");
        }
        foreach (var o in all.Where(o => o.Class == "Planet"))
        {
            var bmp = o.Get("fileName");
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
        var ted = o.Get("tedFileName");
        Terrain = TerrainParams.FromMission(o.GetDouble("visibleDistance", 3000), o.GetDouble("hazeDistance", 2000), o.GetDouble("screenSize", 120));
        Terrain? t = OpenTribes.World.Terrain.Load(res, ted);
        if (t == null)
        {
            error = "terrain not loadable: " + ted;
            return;
        }
        var tpos = o.GetVector("position");
        t.SetPosition(new Vec3((float)tpos[0], (float)tpos[1], (float)tpos[2]));
        TerrainModel = t;
        var dot = ted.LastIndexOf('.');
        var stem = dot < 0 ? ted : ted[..dot];
        var block = new GridBlock();
        if (!res.Read(stem + "#0.dtb", out var dtb) || !GridBlock.Parse(dtb, block))
        {
            error = "terrain block not loadable: " + stem;
            return;
        }
        var dml = t.MaterialListName();
        var mdot = dml.LastIndexOf('.');
        dml = (mdot <= 0 ? dml : dml[..mdot]) + ".dml";
        var mats = Assets.MaterialList(dml) ?? [];
        var light = TerrainData.ReadLightMap(dtb, block.Size);
        var data = new TerrainData(block, mats.Count, light);
        var pos = o.GetVector("position");
        TerrainBlockSize = block.Size;
        (TerrainOriginX, TerrainOriginY) = (pos[0], pos[1]);
        TerrainDraw = new TerrainRenderer(dev, Assets, data, mats, pos[0], pos[1], pos[2], Terrain);
    }

    /// <summary>Gives the near plane the PSC camera query answers.</summary>
    /// <param name="firstPerson">True when first person is on.</param>
    /// <param name="controlIsObserver">True when the control object is an ObserverCamera.</param>
    /// <returns>The near plane.</returns>
    /// <remarks>Follows the PlayerPSC camera query (004077a8, message 0x400: bytes 004078a0 to 00407901): 0.2 (0x3e4ccccd) when first person is on (+0xa0) with the view distance (+0x9c) at 0 and the control object is not an ObserverCamera (the dynamic cast at 004078ec to the 0x2a4 byte class fails), else 0.01 (0x3c23d70a). A Player, a Vehicle and a Flier in first person all answer 0.2.</remarks>
    public static double ViewNear(bool firstPerson, bool controlIsObserver) =>
        firstPerson && !controlIsObserver ? FirstPersonNearPlane : NearPlane;

    /// <summary>The near plane of the next <see cref="Render"/> calls (camera +0x178). The caller sets it from the view state.</summary>
    public double Near = NearPlane;

    /// <summary>Builds the camera of a render: near and far follow the visible distance of the terrain for the world, a long far for the background (open: world-render.md Q1).</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="yaw">The yaw.</param>
    /// <param name="pitch">The pitch.</param>
    /// <param name="fov">The field of view.</param>
    /// <param name="w">The view width in pixels.</param>
    /// <param name="h">The view height in pixels.</param>
    /// <param name="background">True for the background camera.</param>
    /// <param name="roll">The roll.</param>
    /// <returns>The camera.</returns>
    public Camera3D MakeCamera(double x, double y, double z, double yaw, double pitch, double fov, int w, int h, bool background, double roll = 0) =>
        new(x, y, z, yaw, pitch, fov, w, h, Near, background ? BackgroundFar : Math.Max(Terrain.Visible, 10), roll);

    /// <summary>The point lights of the frame (explosions, projectiles, items). They add to the light map of terrain and interior surfaces within range (world-render.md 2.9, 3.7). The caller sets them before <see cref="Render"/>, and the nearest ones to the camera are used.</summary>
    public IReadOnlyList<PointLight>? DynamicLights;

    /// <summary>Called after the terrain with the world camera and before the interiors, so the caller builds its images there.</summary>
    public Action<Camera3D>? Prepare;
    /// <summary>Called with the instance index before the interior of that instance draws, so the objects in its database come first (3.8).</summary>
    public Action<int>? DrawContents;

    private List<PointLight> NearLights(double cx, double cy, double cz)
    {
        var near = new List<PointLight>();
        if (DynamicLights == null)
            return near;
        foreach (var l in DynamicLights)
            if (l.Range > 0)
                near.Add(l);
        near.Sort((a, b) => (((a.X - cx) * (a.X - cx)) + ((a.Y - cy) * (a.Y - cy)) + ((a.Z - cz) * (a.Z - cz)))
                  .CompareTo(((b.X - cx) * (b.X - cx)) + ((b.Y - cy) * (b.Y - cy)) + ((b.Z - cz) * (b.Z - cz))));
        return near;
    }

    private void SendDynamicLights(double cx, double cy, double cz)
    {
        var near = NearLights(cx, cy, cz);
        if (near.Count == 0)
        {
            mDev.SetDynamicLights([]);
            return;
        }
        var n = Math.Min(near.Count, IGfxDevice.MaxDynamicLights);
        var arr = new DynamicLight[n];
        for (var i = 0; i < n; ++i)
            arr[i] = new DynamicLight((float)near[i].X, (float)near[i].Y, (float)near[i].Z, (float)near[i].Range, near[i].R, near[i].G, near[i].B);
        mDev.SetDynamicLights(arr);
    }

    /// <summary>Renders one frame: background, terrain, interiors and the overlays.</summary>
    /// <param name="x">The camera x.</param>
    /// <param name="y">The camera y.</param>
    /// <param name="z">The camera z.</param>
    /// <param name="yaw">The camera yaw.</param>
    /// <param name="pitch">The camera pitch.</param>
    /// <param name="fov">The field of view.</param>
    /// <param name="overlay">Called with the world camera after the interiors, or null.</param>
    /// <param name="overlay2D">Called after the 3D scene for the 2D draw, or null.</param>
    /// <param name="roll">The camera roll.</param>
    /// <param name="view">The rectangle of a TSControl that draws the world inside the GUI draw, or null for the whole frame.</param>
    /// <remarks>With a <paramref name="view"/> the frame was started and cleared already: the scene goes into that rectangle only (camera size and depth clear), with no 2D overlay and no present (00546dc4).</remarks>
    public void Render(double x, double y, double z, double yaw, double pitch, double fov, Action<Camera3D>? overlay = null, Action? overlay2D = null, double roll = 0, GfxRect? view = null)
    {
        // `view`: a TSControl that draws the world inside its own rectangle in the middle of the GUI draw [code 00546dc4]: the frame was
        // started and cleared already, the scene goes into that rectangle only (camera size and depth clear), no 2D overlay, no present.
        int w = view?.Width ?? mDev.Width, h = view?.Height ?? mDev.Height;
        if (view is GfxRect vr)
            mDev.SetViewport3D(vr);
        else
        {
            mDev.BeginFrame();
            mDev.ClearRgb((byte)(ClearColor.r * 255), (byte)(ClearColor.g * 255), (byte)(ClearColor.b * 255));
        }
        var bg = MakeCamera(x, y, z, yaw, pitch, fov, w, h, true, roll);
        var world = MakeCamera(x, y, z, yaw, pitch, fov, w, h, false, roll);
        mDev.BeginScene3D(bg, HazeColor.r, HazeColor.g, HazeColor.b);
        using (FramePerf.Scope("sky"))
        {
            if (!StarsInFront && Stars.Count > 0)
                StarField.Draw(mDev, bg, Stars, StarColors);
            Sky?.Draw(bg);
            if (StarsInFront && Stars.Count > 0)
                StarField.Draw(mDev, bg, Stars, StarColors);
            foreach (var p in Planets)
                p.Draw(bg);
        }
        mDev.SetCamera3D(world);
        // The terrain merges the lights per square on the CPU (TerrainLightSet), the interiors per fragment.
        mDev.SetDynamicLights([]);
        TerrainDraw?.DynamicLights = NearLights(x, y, z);
        using (FramePerf.Scope("terrain"))
            if (Interiors?.TerrainReached(world) ?? true)
                TerrainDraw?.Draw(world);
        SendDynamicLights(x, y, z);
        Prepare?.Invoke(world);
        using (FramePerf.Scope("interiors"))
            Interiors?.Draw(world, Terrain, DrawContents == null ? null : i =>
            {
                mDev.SetDynamicLights(ReadOnlySpan<DynamicLight>.Empty);
                DrawContents(i);
                SendDynamicLights(x, y, z);
            });
        mDev.SetDynamicLights([]);
        overlay?.Invoke(world);
        mDev.EndScene3D();
        if (view != null)
        {
            mDev.SetViewport3D(null);
            return;
        }
        using (FramePerf.Scope("gui-draw"))
            overlay2D?.Invoke();
        using (FramePerf.Scope("present"))
            mDev.EndFrame();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TerrainDraw?.Dispose();
        Interiors?.Dispose();
        Sky?.Dispose();
        foreach (var p in Planets)
            p.Dispose();
    }
}
