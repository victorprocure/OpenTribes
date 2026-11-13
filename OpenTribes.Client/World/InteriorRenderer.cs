namespace OpenTribes.Client.World;

using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

/// <summary>Shelf packing of rectangles into one atlas.</summary>
/// <remarks>Internal in mechanism: the original keeps one light map per surface (3.5).</remarks>
public static class AtlasPacker
{
    /// <summary>Places rectangles on shelves in order of decreasing height. The width is fixed and the height grows.</summary>
    /// <param name="sizes">The width and height of each rectangle.</param>
    /// <param name="width">The atlas width.</param>
    /// <param name="height">The atlas height.</param>
    /// <returns>The position of each rectangle.</returns>
    public static (int x, int y)[] Pack(IReadOnlyList<(int w, int h)> sizes, int width, out int height)
    {
        var order = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i].h).ThenBy(i => i).ToArray();
        var pos = new (int, int)[sizes.Count];
        int x = 0, y = 0, shelf = 0;
        foreach (var i in order)
        {
            var (w, h) = sizes[i];
            if (x + w > width)
            {
                x = 0;
                y += shelf;
                shelf = 0;
            }
            pos[i] = (x, y);
            x += w;
            shelf = Math.Max(shelf, h);
        }
        height = y + shelf;
        return pos;
    }
}

/// <summary>The interior light maps of one detail level, built at a light time (world-render.md 3.7).</summary>
public static class InteriorLightMaps
{
    /// <summary>Builds the surface light maps (u16 nibbles M R G B): the base map (or the mission map when the mission lighting has it) with the current state data of every light applied (3.7 steps 2 and 3).</summary>
    /// <param name="baseLt">The base lighting.</param>
    /// <param name="mission">The mission lighting, or null.</param>
    /// <param name="time">The light time, the same for every light.</param>
    /// <returns>The map of each surface, null for a surface without a map.</returns>
    public static ushort[]?[] Build(InteriorLighting baseLt, InteriorLighting? mission, float time)
    {
        var maps = BaseMaps(baseLt, mission);
        for (var i = 0; i < baseLt.Lights.Length; ++i)
            Apply(maps, baseLt, mission, i, time, null);
        return maps;
    }

    /// <summary>Builds the surface light maps without any light applied.</summary>
    /// <param name="baseLt">The base lighting.</param>
    /// <param name="mission">The mission lighting, or null.</param>
    /// <returns>The map of each surface, null for a surface without a map.</returns>
    public static ushort[]?[] BaseMaps(InteriorLighting baseLt, InteriorLighting? mission)
    {
        var maps = new ushort[]?[baseLt.Surfaces.Length];
        for (var s = 0; s < maps.Length; ++s)
            maps[s] = baseLt.SurfaceMap(s, mission);
        return maps;
    }

    /// <summary>Adds the sun to the light maps of the surfaces visible from outside (3.7 step 5).</summary>
    /// <param name="maps">The surface light maps.</param>
    /// <param name="geo">The geometry of the interior.</param>
    /// <param name="sun">The scene light of the planet.</param>
    /// <remarks>Follows ITRInstance_mergeSunLight (005581a0, identical bytes in 1.11): for a surface with the visible to outside flag (0x40) the planet light (type 6) gives t = (n . toSun + 1) / 2, n the plane normal (negated when the plane front flag 0x80 is clear), added to the scene ambient: c = ambient + sunColour * t. mono = 0.3 r + 0.6 g + 0.1 b (before the clamp), each channel above 0.99999999 becomes 1, nibble = trunc(c * 16), and every pixel of the map takes the saturating nibble sum. Nothing is added when t &lt;= 0. The stock code does not transform the light into interior space (the dot is of the light direction with the plane as stored), so the result does not depend on the instance rotation.</remarks>
    public static void AddSun(ushort[]?[] maps, InteriorGeometryData geo, LightSet sun)
    {
        for (var s = 0; s < maps.Length && s < geo.Surfaces.Length; ++s)
        {
            var map = maps[s];
            InteriorGeometryData.Surface surf = geo.Surfaces[s];
            if (map == null || !surf.VisibleToOutside || (surf.Plane * 4) + 2 >= geo.Planes.Length)
                continue;
            var pi = surf.Plane * 4;
            double nx = geo.Planes[pi], ny = geo.Planes[pi + 1], nz = geo.Planes[pi + 2];
            if (!surf.PlaneFront)
                (nx, ny, nz) = (-nx, -ny, -nz);
            var t = ((nx * sun.SunX) + (ny * sun.SunY) + (nz * sun.SunZ) + 1.0) * 0.5;
            if (t <= 0)
                continue;
            double r = sun.AmbR + (sun.SunR * t), g = sun.AmbG + (sun.SunG * t), b = sun.AmbB + (sun.SunB * t);
            var mono = (0.3 * r) + (0.6 * g) + (0.1 * b);
            static int Nibble(double c) => (int)((c > 0.99999999 ? 1.0 : c) * 16.0);
            int nm = Nibble(mono), nr = Nibble(r), ng = Nibble(g), nb = Nibble(b);
            if (nm == 0 && nr == 0 && ng == 0 && nb == 0)
                continue;
            for (var i = 0; i < map.Length; ++i)
            {
                int w = map[i];
                int m = Math.Min(15, (w >> 12) + nm), rr = Math.Min(15, ((w >> 8) & 15) + nr), gg = Math.Min(15, ((w >> 4) & 15) + ng),
                    bb = Math.Min(15, (w & 15) + nb);
                map[i] = (ushort)((m << 12) | (rr << 8) | (gg << 4) | bb);
            }
        }
    }

    /// <summary>Lists the surfaces a light writes to in any of its states.</summary>
    /// <param name="baseLt">The base lighting.</param>
    /// <param name="light">The light.</param>
    /// <returns>The surface indices.</returns>
    public static IEnumerable<int> LightSurfaces(InteriorLighting baseLt, InteriorLighting.Light light)
    {
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length)
            yield break;
        for (var j = 0; j < light.StateCount; ++j)
        {
            InteriorLighting.State st = baseLt.States[light.FirstState + j];
            int count = (ushort)st.DataCount, index = (ushort)st.DataIndex;
            for (var k = 0; k < count && index + k < baseLt.StateDatas.Length; ++k)
                yield return baseLt.StateDatas[index + k].Surface;
        }
    }

    /// <summary>Adds the current state of one light at a time (its state data, in its interpolated colour) to the maps.</summary>
    /// <param name="maps">The surface light maps.</param>
    /// <param name="baseLt">The base lighting.</param>
    /// <param name="mission">The mission lighting, or null.</param>
    /// <param name="lightIndex">The light index.</param>
    /// <param name="time">The light time.</param>
    /// <param name="only">The surfaces to write, or null for all.</param>
    public static void Apply(ushort[]?[] maps, InteriorLighting baseLt, InteriorLighting? mission, int lightIndex, float time, HashSet<int>? only)
    {
        InteriorLighting.Light light = baseLt.Lights[lightIndex];
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length)
            return;
        var records = baseLt.States.AsSpan(light.FirstState, light.StateCount);
        var state = InteriorLighting.SelectState(records, light.StateCount, time);
        Apply(maps, baseLt, mission, lightIndex, state, InteriorLighting.ColorAt(records, light.Duration, state, time), only);
    }

    /// <summary>Adds a light to the maps with the state and colour given. Random lights pick their own.</summary>
    /// <param name="maps">The surface light maps.</param>
    /// <param name="baseLt">The base lighting.</param>
    /// <param name="mission">The mission lighting, or null.</param>
    /// <param name="lightIndex">The light index.</param>
    /// <param name="state">The state index.</param>
    /// <param name="colour">The colour bytes.</param>
    /// <param name="only">The surfaces to write, or null for all.</param>
    public static void Apply(ushort[]?[] maps, InteriorLighting baseLt, InteriorLighting? mission, int lightIndex, int state,
                             (int mono, int r, int g, int b) colour, HashSet<int>? only)
    {
        InteriorLighting.Light light = baseLt.Lights[lightIndex];
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length || state < 0 ||
            state >= light.StateCount)
            return;
        InteriorLighting.State st = baseLt.States[light.FirstState + state];
        int count = (ushort)st.DataCount, index = (ushort)st.DataIndex;
        for (var k = 0; k < count && index + k < baseLt.StateDatas.Length; ++k)
        {
            InteriorLighting.StateData sd = baseLt.StateDatas[index + k];
            if (sd.MapIndex < 0 || sd.Surface < 0 || sd.Surface >= maps.Length || maps[sd.Surface] == null)
                continue;
            if (only != null && !only.Contains(sd.Surface))
                continue;
            InteriorLighting.Surface surf = baseLt.Surfaces[sd.Surface];
            var src = baseLt.MapData;
            long off = sd.MapIndex;
            if (mission != null && mission.TryMissionOffset((uint)sd.MapIndex, out var dest))
            {
                src = mission.MapData;
                off = dest;
            }
            var m = maps[sd.Surface]!;
            var px = surf.SizeX * surf.SizeY;
            for (var p = 0; p < px && p < m.Length && off + p < src.Length; ++p)
                m[p] = InteriorLighting.AddLight(m[p], colour, src[off + p]);
        }
    }
}

/// <summary>One detail level of a shape: geometry, material bitmaps and one light atlas.</summary>
public sealed class InteriorLodModel
{
    /// <summary>The geometry.</summary>
    public InteriorGeometryData Geo = null!;
    /// <summary>The geometry with its leaves, for the visible surface selection of the stock renderer (world-render.md 3.3).</summary>
    public InteriorGeometry? Leaves;
    /// <summary>The bitmap name of each material.</summary>
    public List<string?> Materials = [];
    /// <summary>The base lighting.</summary>
    public InteriorLighting? Base;
    /// <summary>The mission lighting.</summary>
    public InteriorLighting? Mission;
    /// <summary>The position of each surface map in the atlas.</summary>
    public (int x, int y)[] LmPos = [];
    /// <summary>The atlas width.</summary>
    public int AtlasW;
    /// <summary>The atlas height.</summary>
    public int AtlasH;
    /// <summary>The minimum projected size in pixels for this detail level.</summary>
    public int MinPixels;
    /// <summary>The centre of the geometry box.</summary>
    public float[] Center = new float[3];
    /// <summary>The radius of the bounding sphere.</summary>
    public float Radius;
    /// <summary>The animated lights that run on the client (auto start flag, world-render.md 3.7 Timing).</summary>
    public int[] Running = [];
    /// <summary>The surfaces the running lights touch.</summary>
    public HashSet<int> RunSurfaces = [];
    /// <summary>The maps those surfaces start from.</summary>
    public ushort[]?[] BaseMaps = [];
    /// <summary>The light atlases built so far, one per light state seen (the first is the state at light time 0). The stock instance holds its own light state and maps. Here instances whose lights are in the same state share one atlas.</summary>
    public readonly List<LightAtlas> Atlases = [];
}

/// <summary>One light atlas of a detail level: the packed surface maps for one state of its running lights.</summary>
public sealed class LightAtlas
{
    /// <summary>The state of each running light.</summary>
    public int[] Key = [];
    /// <summary>The atlas pixels.</summary>
    public byte[] Rgba = [];
    /// <summary>The atlas texture.</summary>
    public IGfxTexture? Texture;
    /// <summary>The frame this atlas was last used.</summary>
    public long UsedFrame;
}

/// <summary>A loaded interior shape file with its detail levels.</summary>
public sealed class InteriorModel
{
    /// <summary>The resolved file name.</summary>
    public string Name = "";
    /// <summary>The shape file.</summary>
    public InteriorShapeFile Shape = new();
    /// <summary>The detail levels, null while not loaded.</summary>
    public InteriorLodModel?[] Lods = [];
}

/// <summary>Draws the interior instances of a mission (world-render.md 3).</summary>
public sealed class InteriorRenderer : IDisposable
{
    /// <summary>One placed interior.</summary>
    public sealed class Instance
    {
        /// <summary>The model.</summary>
        public InteriorModel Model = null!;
        /// <summary>The position x.</summary>
        public double X;
        /// <summary>The position y.</summary>
        public double Y;
        /// <summary>The position z.</summary>
        public double Z;
        /// <summary>The rotation about x.</summary>
        public double Rx;
        /// <summary>The rotation about y.</summary>
        public double Ry;
        /// <summary>The rotation about z.</summary>
        public double Rz;
        /// <summary>The team of the GameBase part, -1 for none.</summary>
        public int Team = -1;
        /// <summary>The skin its textures were built with, empty for none.</summary>
        public string Skin = "";
        /// <summary>The rotation matrix, row major.</summary>
        public float[] M = new float[9];
        // World box of the geometry padded by 3 on every side (docs/game-stream-ghosts.md 6.2), null until first needed; IsContainer: some extent >= 6.
        internal double[]? Box;
        internal bool IsContainer;
        // The scope of the camera in this interior (6.2 PVS database): the camera leaf, its visible leaves, whether it sees outside.
        internal double ScopeX = double.NaN, ScopeY, ScopeZ;
        internal bool ScopeNothing, ScopeOutside;
        internal HashSet<int>? ScopeLeaves;
        // World-space triangles of each surface per detail level, built on first draw (the instance never moves).
        internal readonly Dictionary<InteriorLodModel, SurfaceMesh[]> Meshes = [];
        // The light state of the instance (the run state array of the stock instance, 3.7): the sim time of its first light tick, the
        // step last processed and, per light, the private state of a random light. Detail changes reset the random states.
        internal double? LightOrigin;
        internal long LightStep;
        internal InteriorLodModel? DrawnLod;
        internal int LodIndex; // the detail level in use (ITRInstance +0xb4), 0 at the start
        internal LightAtlas? DrawnAtlas;
        internal InteriorLodModel? LightLod;
        internal float[] RandomTime = [];
        internal int[] RandomState = [];
        internal (int mono, int r, int g, int b)[] RandomColor = [];
    }

    // One surface of one instance, already in world space with haze 0: the fan triangles and the batch key (material index).
    internal readonly record struct SurfaceMesh(Vertex3D[] Triangles, int Key, IGfxTexture? Texture);

    // Draw scratch kept between frames: the vertices of the current run of surfaces with one material.
    private readonly List<Vertex3D> mBatch = [];

    private readonly IGfxDevice mDev;
    private readonly WorldAssets mAssets;
    private readonly Dictionary<string, InteriorModel?> mModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IGfxTexture?> mTextures = new(StringComparer.OrdinalIgnoreCase);
    private IGfxTexture? mWhite;
    /// <summary>The placed interiors.</summary>
    public readonly List<Instance> Instances = [];
    /// <summary>The length of a light step in seconds: the lights advance in steps of <c>$pref::interiorLightUpdateMS</c> (67 ms, 0047f3f4).</summary>
    public const float LightStep = 0.067f;
    private readonly System.Diagnostics.Stopwatch mClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>The simulation time in seconds, set by the host before each frame. Without a sim time (tools) the wall clock stands in.</summary>
    /// <remarks>The stock instance keeps an accumulator that its first tick sets to the sim time and then adds 67 ms to while the tick time is above it (0047f3f4), so the light time of an instance is a whole number of steps counted from its own first tick (<c>Instance.LightOrigin</c>, the first frame that sees it).</remarks>
    public double? SimSeconds;
    private long mFrame;
    private readonly Random mRandom = new(0x5eed);
    /// <summary>The number of surfaces drawn in the last frame.</summary>
    public int DrawnSurfaces;
    /// <summary>The number of instances drawn in the last frame.</summary>
    public int DrawnInstances;

    /// <summary>Creates the renderer.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="assets">The world assets.</param>
    public InteriorRenderer(IGfxDevice dev, WorldAssets assets)
    {
        mDev = dev;
        mAssets = assets;
    }

    /// <summary>Builds the Euler transform (interiors.md 5.2): row vector convention, world = local * M + p.</summary>
    /// <param name="x">The rotation about x.</param>
    /// <param name="y">The rotation about y.</param>
    /// <param name="z">The rotation about z.</param>
    /// <returns>The nine matrix elements, row major.</returns>
    public static float[] EulerMatrix(double x, double y, double z)
    {
        double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
        if (x == 0 && y == 0)
            return [(float)cz, (float)sz, 0, (float)-sz, (float)cz, 0, 0, 0, 1];
        return
        [
            (float)((cz * cy) - (sx * sy * sz)), (float)((cy * sz) + (cz * sx * sy)), (float)(-cx * sy),
            (float)(-cx * sz), (float)(cx * cz), (float)sx,
            (float)((cz * sy) + (cy * sx * sz)), (float)((sz * sy) - (cz * cy * sx)), (float)(cx * cy),
        ];
    }

    /// <summary>Gets the name of the base lighting of a mission lighting (interiors.md L3, 4): <c>&lt;base&gt;-&lt;digits&gt;-&lt;n&gt;.dil</c> is the mission lighting of <c>&lt;base&gt;-&lt;digits&gt;.dil</c>.</summary>
    /// <param name="name">The lighting file name.</param>
    /// <returns>The base name.</returns>
    public static string BaseLightingName(string name)
    {
        var dash = name.LastIndexOf('-');
        if (dash <= 0)
            return name;
        var i = dash - 1;
        while (i > 0 && name[i] >= '0' && name[i] <= '9')
            --i;
        return name[i] == '-' ? name[..dash] + ".dil" : name;
    }

    /// <summary>Gets the skin base of a team (the team table of the client). Materials named <c>base.&lt;name&gt;</c> take <c>&lt;skin&gt;.&lt;name&gt;</c> when that exists.</summary>
    public Func<int, string>? TeamSkin;

    /// <summary>The scene light of the planet (ambient, sun colour and direction) for 3.7 step 5, or null for no sun term.</summary>
    public LightSet? Sun;

    /// <summary>Adds an interior instance, loading the model on first use.</summary>
    /// <param name="fileName">The interior file name.</param>
    /// <param name="x">The position x.</param>
    /// <param name="y">The position y.</param>
    /// <param name="z">The position z.</param>
    /// <param name="rx">The rotation about x.</param>
    /// <param name="ry">The rotation about y.</param>
    /// <param name="rz">The rotation about z.</param>
    /// <param name="team">The team of the GameBase part, -1 for none.</param>
    /// <returns>False when the interior did not load.</returns>
    public bool AddInstance(string fileName, double x, double y, double z, double rx, double ry, double rz, int team = -1)
    {
        var resolved = Interior.ResolveInteriorName(mAssets.Res, fileName);
        if (resolved.Length == 0)
            return false;
        if (!mModels.TryGetValue(resolved, out InteriorModel? model))
        {
            model = null;
            var shape = new InteriorShapeFile();
            if (mAssets.Res.Read(resolved, out var dis) && InteriorShapeFile.Parse(dis, shape) && shape.Lods.Count > 0)
                model = new InteriorModel { Name = resolved, Shape = shape, Lods = new InteriorLodModel?[shape.Lods.Count] };
            mModels[resolved] = model;
            if (model == null)
                Console.Error.WriteLine($"interior: {fileName} ({resolved}) did not load");
        }
        if (model == null)
            return false;
        Instances.Add(new Instance { Model = model, X = x, Y = y, Z = z, Rx = rx, Ry = ry, Rz = rz, Team = team, M = EulerMatrix(rx, ry, rz) });
        return true;
    }

    /// <summary>Adds a model built in memory (tests, tools). The lighting atlas of the level is built here.</summary>
    /// <param name="model">The model.</param>
    /// <param name="lod">The detail level.</param>
    /// <param name="x">The position x.</param>
    /// <param name="y">The position y.</param>
    /// <param name="z">The position z.</param>
    /// <param name="rx">The rotation about x.</param>
    /// <param name="ry">The rotation about y.</param>
    /// <param name="rz">The rotation about z.</param>
    /// <returns>The instance.</returns>
    public Instance Install(InteriorModel model, InteriorLodModel lod, double x, double y, double z, double rx, double ry, double rz)
    {
        BuildAtlas(lod);
        var b = lod.Geo.Box;
        lod.Center = [(b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2];
        lod.Radius = (float)Math.Sqrt(Math.Pow(b[3] - b[0], 2) + Math.Pow(b[4] - b[1], 2) + Math.Pow(b[5] - b[2], 2)) / 2;
        model.Lods = [lod];
        var inst = new Instance { Model = model, X = x, Y = y, Z = z, Rx = rx, Ry = ry, Rz = rz, M = EulerMatrix(rx, ry, rz) };
        Instances.Add(inst);
        return inst;
    }

    private InteriorLodModel? Lod(InteriorModel model, int index)
    {
        if (model.Lods[index] != null)
            return model.Lods[index];
        InteriorShapeFile.Lod l = model.Shape.Lods[index];
        if (!mAssets.Res.Read(l.Geometry, out var dig))
        {
            Console.Error.WriteLine($"interior: {model.Name} lod {index}: geometry {l.Geometry} not found");
            return null;
        }
        InteriorGeometryData? geo = InteriorGeometryData.Parse(dig);
        if (geo == null)
        {
            Console.Error.WriteLine($"interior: {model.Name} lod {index}: geometry {l.Geometry} rejected");
            return null;
        }
        var lod = new InteriorLodModel
        {
            Geo = geo,
            Leaves = InteriorGeometry.Parse(dig),
            MinPixels = (int)LodMinPixels(model.Shape, index),
            Materials = mAssets.MaterialList(model.Shape.MaterialList) ?? []
        };
        if (lod.Materials.Count == 0)
            Console.Error.WriteLine($"interior: {model.Name}: material list {model.Shape.MaterialList} empty or missing");
        var li = (int)l.LightStateIndex;
        if (li < model.Shape.LodLights.Count)
        {
            var lightName = model.Shape.LodLights[li];
            if (mAssets.Res.Read(lightName, out var lb))
            {
                InteriorLighting? first = InteriorLighting.Parse(lb);
                if (first != null && first.IsMission && first.BuildId == geo.BuildId)
                {
                    lod.Mission = first;
                    var baseName = BaseLightingName(lightName);
                    if (mAssets.Res.Read(baseName, out var bb))
                        lod.Base = InteriorLighting.Parse(bb);
                }
                else if (first != null && !first.IsMission)
                {
                    lod.Base = first;
                }
                else if (first != null && first.IsMission)
                {
                    // 3.7: a mismatched mission file falls back to the base lighting.
                    if (mAssets.Res.Read(BaseLightingName(lightName), out var bb))
                        lod.Base = InteriorLighting.Parse(bb);
                }
            }
        }
        if (lod.Base != null && lod.Base.Surfaces.Length != geo.Surfaces.Length)
            lod.Base = null;
        if (lod.Base == null)
            Console.Error.WriteLine($"interior: {model.Name} lod {index}: no usable lighting ({(li < model.Shape.LodLights.Count ? model.Shape.LodLights[li] : "none")})");
        BuildAtlas(lod);
        var b = geo.Box;
        lod.Center = [(b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2];
        lod.Radius = (float)Math.Sqrt(Math.Pow(b[3] - b[0], 2) + Math.Pow(b[4] - b[1], 2) + Math.Pow(b[5] - b[2], 2)) / 2;
        model.Lods[index] = lod;
        return lod;
    }

    /// <summary>Loads every detail level of every model in use (geometry, lighting, light atlas, material bitmaps), so the first frame that sees an interior from a new distance does not stall on file reads and texture uploads.</summary>
    public void Preload()
    {
        foreach (InteriorModel? model in mModels.Values)
            if (model != null)
                for (var i = 0; i < model.Lods.Length; ++i)
                {
                    InteriorLodModel? lod = Lod(model, i);
                    if (lod?.Base == null)
                        continue;
                    foreach (var name in lod.Materials)
                        MaterialTexture(name);
                }
    }

    private static uint LodMinPixels(InteriorShapeFile shape, int index) => shape.Lods[index].MinPixels;

    private void BuildAtlas(InteriorLodModel lod)
    {
        InteriorLighting? lt = lod.Base;
        if (lt == null)
            return;
        // The lights the client runs by itself: auto start flag (the ghost light record On, Tribes.exe 0047f3f4), with a cycle.
        var running = new List<int>();
        for (var i = 0; i < lt.Lights.Length; ++i)
        {
            InteriorLighting.Light l = lt.Lights[i];
            if (l.AutoStart && l.StateCount > 1 && l.Duration > 0 && l.FirstState >= 0 && l.FirstState + l.StateCount <= lt.States.Length)
                running.Add(i);
        }
        lod.Running = [.. running];
        lod.RunSurfaces = [];
        foreach (var i in lod.Running)
            foreach (var surf in InteriorLightMaps.LightSurfaces(lt, lt.Lights[i]))
                lod.RunSurfaces.Add(surf);
        lod.BaseMaps = InteriorLightMaps.BaseMaps(lt, lod.Mission);
        // 3.7 step 5: the planet light on the surfaces visible to the outside of an instance without mission lighting.
        if (Sun != null && lod.Mission == null)
            InteriorLightMaps.AddSun(lod.BaseMaps, lod.Geo, Sun);
        var maps = new ushort[]?[lod.BaseMaps.Length];
        for (var i = 0; i < maps.Length; ++i)
            maps[i] = (ushort[]?)lod.BaseMaps[i]?.Clone();
        // The first atlas: every light at time 0, a random light showing nothing (ITRInstance_changeDetail sets state -1).
        var noRandom = new int[lt.Lights.Length];
        Array.Fill(noRandom, -1);
        var noColor = new (int, int, int, int)[lt.Lights.Length];
        ApplyLights(lod, maps, null, 0, noRandom, noColor);
        // Every map sits in the atlas with a border of one replicated pixel, so the bilinear filter never reads a neighbour
        // map (or the empty atlas) at the edge of a surface.
        var sizes = new (int, int)[maps.Length];
        for (var i = 0; i < maps.Length; ++i)
            sizes[i] = maps[i] == null ? (0, 0) : (lt.Surfaces[i].SizeX + 2, lt.Surfaces[i].SizeY + 2);
        lod.AtlasW = 1024;
        (int x, int y)[] packed = AtlasPacker.Pack(sizes, lod.AtlasW, out var h);
        lod.LmPos = new (int, int)[maps.Length];
        for (var i = 0; i < maps.Length; ++i)
            lod.LmPos[i] = (packed[i].x + 1, packed[i].y + 1);
        lod.AtlasH = Math.Max(h, 1);
        var first = new LightAtlas { Rgba = new byte[lod.AtlasW * lod.AtlasH * 4], Key = LightKey(lod, 0, noRandom) };
        for (var i = 0; i < maps.Length; ++i)
            if (maps[i] != null)
                WriteSurface(lod, first.Rgba, i, maps[i]!);
        first.Texture = mDev.CreateTexture3D(first.Rgba, lod.AtlasW, lod.AtlasH, new TextureOptions(false, false, true));
        lod.Atlases.Clear();
        lod.Atlases.Add(first);
    }

    // Light time of a running light at `step` (a whole number of steps from the first tick of the instance).
    private static float StepTime(long step, InteriorLighting.Light l) => (float)(step * (double)LightStep % l.Duration);

    // Every light of the lod: the running ones at their cycle position of `step` (a random one in its private state `randomState`), the
    // others at time 0 (their first state).
    private static void ApplyLights(InteriorLodModel lod, ushort[]?[] maps, HashSet<int>? only, long step, int[] randomState,
                                    (int mono, int r, int g, int b)[] randomColor)
    {
        InteriorLighting lt = lod.Base!;
        var run = 0;
        for (var i = 0; i < lt.Lights.Length; ++i)
        {
            if (run < lod.Running.Length && lod.Running[run] == i)
            {
                InteriorLighting.Light l = lt.Lights[i];
                if (!l.Random)
                    InteriorLightMaps.Apply(maps, lt, lod.Mission, i, StepTime(step, l), only);
                else if (randomState[i] >= 0)
                    InteriorLightMaps.Apply(maps, lt, lod.Mission, i, randomState[i], randomColor[i], only);
                ++run;
            }
            else
            {
                InteriorLightMaps.Apply(maps, lt, lod.Mission, i, 0f, only);
            }
        }
    }

    // The light state of the running lights at `step` as numbers: equal keys give equal atlases.
    private static int[] LightKey(InteriorLodModel lod, long step, int[] randomState)
    {
        InteriorLighting lt = lod.Base!;
        var key = new int[lod.Running.Length * 5];
        for (var r = 0; r < lod.Running.Length; ++r)
        {
            var i = lod.Running[r];
            InteriorLighting.Light l = lt.Lights[i];
            if (l.Random)
            {
                key[r * 5] = randomState[i];
                continue;
            }
            var t = StepTime(step, l);
            var records = lt.States.AsSpan(l.FirstState, l.StateCount);
            var state = InteriorLighting.SelectState(records, l.StateCount, t);
            var c = InteriorLighting.ColorAt(records, l.Duration, state, t);
            key[r * 5] = state;
            key[(r * 5) + 1] = c.mono;
            key[(r * 5) + 2] = c.r;
            key[(r * 5) + 3] = c.g;
            key[(r * 5) + 4] = c.b;
        }
        return key;
    }

    private static void WriteSurface(InteriorLodModel lod, byte[] rgba, int i, ushort[] map)
    {
        InteriorLighting lt = lod.Base!;
        int w = lt.Surfaces[i].SizeX, hh = lt.Surfaces[i].SizeY;
        if (w <= 0 || hh <= 0)
            return;
        var (px, py) = lod.LmPos[i];
        for (var y = -1; y <= hh; ++y)
            for (var x = -1; x <= w; ++x)
            {
                var k = (Math.Clamp(y, 0, hh - 1) * w) + Math.Clamp(x, 0, w - 1);
                if (k >= map.Length)
                    continue;
                var (r, g, b) = TerrainData.ExpandLightInterior(map[k]);
                var o = (((py + y) * lod.AtlasW) + px + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
    }

    // The 67 ms steps of an instance: counted from its first light tick, in sim time (the wall clock without a host).
    private long InstanceStep(Instance inst)
    {
        if (SimSeconds is double sim)
        {
            inst.LightOrigin ??= sim;
            return (long)Math.Ceiling(((sim - inst.LightOrigin.Value) / LightStep) - 1e-9);
        }
        return (long)(mClock.Elapsed.TotalSeconds / LightStep);
    }

    // The light atlas of an instance at this frame (ITRInstance light advance, once per light step): advances the random lights of the
    // instance by the steps taken and finds or builds the atlas of its light state.
    private LightAtlas InstanceAtlas(Instance inst, InteriorLodModel lod, long step)
    {
        InteriorLighting lt = lod.Base!;
        var n = lt.Lights.Length;
        if (inst.RandomState.Length < n)
        {
            // the run state array of the instance (a light's time survives a detail change, its state does not)
            inst.RandomTime = new float[n];
            inst.RandomState = new int[n];
            inst.RandomColor = new (int, int, int, int)[n];
            Array.Fill(inst.RandomState, -1);
        }
        if (inst.LightLod != lod)
        {
            // ITRInstance_changeDetail: state -1, so a random light shows nothing until its next draw
            inst.LightLod = lod;
            Array.Fill(inst.RandomState, -1);
        }
        if (inst.LightStep > step || step - inst.LightStep > 4096)
            inst.LightStep = step;
        for (; inst.LightStep < step; ++inst.LightStep)
            foreach (var i in lod.Running)
            {
                InteriorLighting.Light l = lt.Lights[i];
                if (!l.Random)
                    continue;
                // 00556cb0: the time wraps by the end time of record 1 and a state is drawn then
                var records = lt.States.AsSpan(l.FirstState, l.StateCount);
                var period = records[1].EndTime;
                inst.RandomTime[i] += LightStep;
                if (period > 0 && inst.RandomTime[i] >= period)
                {
                    while (inst.RandomTime[i] >= period)
                        inst.RandomTime[i] -= period;
                    var roll = l.Duration * (float)mRandom.NextDouble();
                    var pick = l.StateCount - 1;
                    for (var j = 0; j < l.StateCount - 1; ++j)
                        if (roll < records[j + 1].EndTime)
                        {
                            pick = j;
                            break;
                        }
                    inst.RandomState[i] = pick;
                    var c = records[pick];
                    inst.RandomColor[i] = ((c.Mono + 0x80) >> 8, (c.R + 0x80) >> 8, (c.G + 0x80) >> 8, (c.B + 0x80) >> 8);
                }
            }
        var key = LightKey(lod, step, inst.RandomState);
        LightAtlas? found = null;
        foreach (LightAtlas a in lod.Atlases)
            if (a.Key.AsSpan().SequenceEqual(key))
            {
                found = a;
                break;
            }
        if (found == null)
        {
            // rebuild the maps of the surfaces the running lights touch on a copy of the first atlas
            var maps = new ushort[]?[lod.BaseMaps.Length];
            foreach (var s in lod.RunSurfaces)
                maps[s] = (ushort[]?)lod.BaseMaps[s]?.Clone();
            ApplyLights(lod, maps, lod.RunSurfaces, step, inst.RandomState, inst.RandomColor);
            found = new LightAtlas { Key = key, Rgba = (byte[])lod.Atlases[0].Rgba.Clone() };
            foreach (var s in lod.RunSurfaces)
                if (maps[s] != null)
                    WriteSurface(lod, found.Rgba, s, maps[s]!);
            found.Texture = mDev.CreateTexture3D(found.Rgba, lod.AtlasW, lod.AtlasH, new TextureOptions(false, false, true));
            // drop the atlases no instance used for a while (never the first)
            for (var i = lod.Atlases.Count - 1; i > 0 && lod.Atlases.Count > 12; --i)
                if (lod.Atlases[i].UsedFrame < mFrame - 1)
                {
                    lod.Atlases[i].Texture?.Dispose();
                    lod.Atlases.RemoveAt(i);
                }
            lod.Atlases.Add(found);
        }
        found.UsedFrame = mFrame;
        return found;
    }

    private IGfxTexture? MaterialTexture(string? name)
    {
        if (name == null)
        {
            mWhite ??= mDev.CreateTexture3D([255, 255, 255, 255], 1, 1, new TextureOptions(false, true, false));
            return mWhite;
        }
        if (mTextures.TryGetValue(name, out IGfxTexture? t))
            return t;
        PbmpBitmap? bmp = mAssets.Bitmap(name);
        t = bmp != null && bmp.Width > 0 && bmp.Height > 0
            ? mAssets.CreateTexture(mDev, bmp, mAssets.PackedRgba(bmp, true), true, true, true)
            : null;
        mTextures[name] = t;
        if (t == null)
            Console.Error.WriteLine($"interior: material bitmap {name} not found");
        return t;
    }

    /// <summary>Finds the detail level for a projected size (3.2): the first level whose minPixels is less than the projected size of the bounding sphere, else the last. Inside the box the first (highest detail) level is forced (3.1).</summary>
    /// <param name="minPixels">The minimum size of each level.</param>
    /// <param name="projectedPixels">The projected size of the bounding sphere.</param>
    /// <returns>The level index.</returns>
    public static int FindLod(IReadOnlyList<int> minPixels, int projectedPixels)
    {
        for (var i = 0; i < minPixels.Count; ++i)
            if (minPixels[i] < projectedPixels)
                return i;
        return minPixels.Count - 1;
    }

    /// <summary>Chooses the detail level of the frame.</summary>
    /// <param name="inside">True when the camera is inside the geometry box of the current detail.</param>
    /// <param name="linkFlag">The image link flag.</param>
    /// <param name="dataLoaded">True when the shape resource of the instance is loaded.</param>
    /// <param name="previous">The previous level.</param>
    /// <param name="computed">The level from the projected size.</param>
    /// <returns>The level.</returns>
    /// <remarks>Follows the draw function 00480be4 with ITRInstance_updateDetail (00556fa4). Inside the geometry box of the current detail, or when the image link flag (image +0x18, bit 2 of +0x418 of the interior: the walk reached it through a link record of a neighbour) is set, ITRInstance_changeDetail(0) forces the full detail level. Otherwise the level comes from the projected size, but updateDetail only runs when the shape resource of the instance (+0x3c, data +0x14) is loaded and keeps the previous level when it is not. The port's analogue of the missing data is a level without its lighting: the previous level stays.</remarks>
    public static int ChooseDetail(bool inside, bool linkFlag, bool dataLoaded, int previous, int computed)
    {
        if (inside || linkFlag)
            return 0;
        return dataLoaded ? computed : previous;
    }

    private static int ModelLod(InteriorModel model, int pixels, bool inside)
    {
        if (inside || model.Shape.Lods.Count == 0)
            return 0;
        IReadOnlyList<InteriorShapeFile.Lod> lods = model.Shape.Lods;
        for (var i = 0; i < lods.Count; ++i)
            if ((int)lods[i].MinPixels < pixels)
                return i;
        return lods.Count - 1;
    }

    /// <summary>Computes the sphere the detail choice projects.</summary>
    /// <param name="box">The geometry box of the last detail level.</param>
    /// <returns>The centre and the radius.</returns>
    /// <remarks>Follows ITRInstance_setState (005570c0): the sphere comes from the geometry box of the last detail level of the state, with the centre of the box as centre and the longest box extent as radius (not half the diagonal).</remarks>
    public static (double cx, double cy, double cz, double radius) DetailSphere(float[] box)
    {
        var r = Math.Max(Math.Max(box[3] - box[0], box[4] - box[1]), box[5] - box[2]);
        return ((box[0] + box[3]) * 0.5, (box[1] + box[4]) * 0.5, (box[2] + box[5]) * 0.5, r);
    }

    /// <summary>Computes the projected size the detail choice compares with minPixels.</summary>
    /// <param name="depth">The view depth of the centre.</param>
    /// <param name="radius">The sphere radius.</param>
    /// <param name="focalLength">The pixels per unit at distance 1.</param>
    /// <returns>The size in pixels.</returns>
    /// <remarks>00480be4 calls the project sphere slot of the camera (vtable +0x10, 005aa704), multiplies by 2.0 (constant at 00481094), stores a float and truncates it (00556fa4). The slot divides the radius by the view depth (the camera space y of the centre, at least 0.001), not by the distance, and scales by pixels per unit at distance 1. Single precision throughout.</remarks>
    public static int DetailPixels(double depth, double radius, double focalLength)
    {
        var d = depth < 0.001 ? 0.001f : (float)depth;
        var size = (float)(radius * focalLength) / d * 2.0f;
        return size >= int.MaxValue ? int.MaxValue : (int)size;
    }

    /// <summary>The container index of an object that is in a container interior whose camera scope does not reach it.</summary>
    public const int Culled = -2;

    private bool EnsureBox(Instance inst)
    {
        if (inst.Box != null)
            return true;
        InteriorLodModel? lod0 = Lod(inst.Model, 0);
        if (lod0 == null)
            return false;
        float[] b = lod0.Geo.Box, m = inst.M;
        var box = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
        for (var c = 0; c < 8; ++c)
        {
            double px = b[(c & 1) != 0 ? 3 : 0], py = b[(c & 2) != 0 ? 4 : 1], pz = b[(c & 4) != 0 ? 5 : 2];
            double wx = (px * m[0]) + (py * m[3]) + (pz * m[6]) + inst.X, wy = (px * m[1]) + (py * m[4]) + (pz * m[7]) + inst.Y,
                   wz = (px * m[2]) + (py * m[5]) + (pz * m[8]) + inst.Z;
            box[0] = Math.Min(box[0], wx - 3);
            box[1] = Math.Min(box[1], wy - 3);
            box[2] = Math.Min(box[2], wz - 3);
            box[3] = Math.Max(box[3], wx + 3);
            box[4] = Math.Max(box[4], wy + 3);
            box[5] = Math.Max(box[5], wz + 3);
        }
        inst.Box = box;
        inst.IsContainer = b[3] - b[0] >= 6 || b[4] - b[1] >= 6 || b[5] - b[2] >= 6;
        return true;
    }

    private static double Volume(double[] b) => (b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2]);

    /// <summary>Finds the container interior (instance index) that holds an object with a world box.</summary>
    /// <param name="objectBox">The box as min xyz, max xyz.</param>
    /// <param name="exclude">An instance index to skip.</param>
    /// <returns>The index, or -1 for none.</returns>
    /// <remarks>Follows SimContainer::addObject (004f9c04): the first container whose padded world box holds the whole box. Between interiors the larger box is the container (a new container pulls in what it holds, findContained), equal boxes by index.</remarks>
    public int FindContainer(double[] objectBox, int exclude = -1)
    {
        for (var i = 0; i < Instances.Count; ++i)
        {
            Instance inst = Instances[i];
            if (i == exclude || !EnsureBox(inst) || !inst.IsContainer)
                continue;
            var cb = inst.Box!;
            if (objectBox[0] < cb[0] || objectBox[3] > cb[3] || objectBox[1] < cb[1] || objectBox[4] > cb[4] || objectBox[2] < cb[2] || objectBox[5] > cb[5])
                continue;
            if (exclude >= 0 && (Volume(cb) < Volume(objectBox) || (Volume(cb) == Volume(objectBox) && i > exclude)))
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>Tells whether the camera scope of container interior <paramref name="i"/> reaches an object.</summary>
    /// <param name="i">The container instance index.</param>
    /// <param name="objectBox">The box of the object as min xyz, max xyz.</param>
    /// <param name="cam">The camera.</param>
    /// <returns>True when the object is in scope.</returns>
    /// <remarks>The render query of the PVS database (00483e84): a camera in a solid leaf scopes nothing. Otherwise the objects in the leaves of the PVS of the camera (leaf 43 and up), in solid leaves, and, when the camera leaf sees outside, the objects outside the geometry box.</remarks>
    public bool ScopeReaches(int i, double[] objectBox, in Camera3D cam)
    {
        Instance inst = Instances[i];
        InteriorGeometry? leaves = Lod(inst.Model, 0)?.Leaves;
        if (leaves == null)
            return true;
        if (inst.ScopeX != cam.X || inst.ScopeY != cam.Y || inst.ScopeZ != cam.Z || inst.ScopeLeaves == null)
        {
            (inst.ScopeX, inst.ScopeY, inst.ScopeZ) = (cam.X, cam.Y, cam.Z);
            Vec3 lc = ToLocal(inst, cam.X, cam.Y, cam.Z);
            var camLeaf = leaves.LeafAt(lc);
            inst.ScopeNothing = leaves.IsSolid(camLeaf);
            inst.ScopeOutside = leaves.SeesOutside(camLeaf);
            inst.ScopeLeaves = inst.ScopeNothing ? [] : [.. leaves.VisibleLeaves(camLeaf)];
        }
        if (inst.ScopeNothing)
            return false;
        double ox = (objectBox[0] + objectBox[3]) * 0.5, oy = (objectBox[1] + objectBox[4]) * 0.5, oz = (objectBox[2] + objectBox[5]) * 0.5;
        var leaf = leaves.LeafAt(ToLocal(inst, ox, oy, oz));
        return leaves.IsSolid(leaf) || (leaf >= 43 && inst.ScopeLeaves.Contains(leaf)) || (leaf < 43 && inst.ScopeOutside);
    }

    /// <summary>The SimTerrain world box (min xyz, max xyz, z plus or minus contHeight) for the render scope gate of <see cref="TerrainReached"/>, or null for no gate.</summary>
    public float[]? TerrainBox;
    private Container? mGate;
    private int mGateCount = -1;
    private double mGateX = double.NaN, mGateY, mGateZ;
    private bool mGateResult = true;
    // The interiors the render walk of the camera point reached through a link record (image link flag, see ChooseDetail).
    private readonly HashSet<object> mGateLinked = [];

    /// <summary>Tells whether the render walk reaches the terrain from the camera.</summary>
    /// <param name="cam">The camera.</param>
    /// <returns>True to draw the terrain.</returns>
    /// <remarks>The stock render walk reaches the terrain image only when the container interior the camera point lies in (a container of the terrain database, box = geometry box padded by 3) accepts the scope: 004fa9c8 (terrain, vtable +0x98) calls the scope of the open container of the point first and returns 0, before the terrain query 00518f80, when it returns 0. 004804bc returns whether the camera leaf sees outside (or a neighbour linked through a face does). A camera in an enclosed leaf of a container interior therefore sees no terrain.</remarks>
    public bool TerrainReached(in Camera3D cam)
    {
        if (TerrainBox == null)
            return true;
        if (mGate == null || mGateCount != Instances.Count)
        {
            mGate = BuildGate();
            mGateCount = Instances.Count;
            mGateX = double.NaN;
        }
        if (mGateX != cam.X || mGateY != cam.Y || mGateZ != cam.Z)
        {
            (mGateX, mGateY, mGateZ) = (cam.X, cam.Y, cam.Z);
            mGateLinked.Clear();
            mGateResult = mGate.RenderReaches(new Vec3((float)cam.X, (float)cam.Y, (float)cam.Z), o => mGateLinked.Add(o));
        }
        return mGateResult;
    }

    private Container BuildGate()
    {
        var c = new Container();
        var terrain = new Container.Entry(this, ObjectType.kTerrainType, Float6.Of(TerrainBox!), false, null)
        {
            DatabaseKind = Container.Database.Terrain,
            ContainerMask = 0xffffffffu,
        };
        c.Add(terrain);
        foreach (Instance inst in Instances)
        {
            InteriorLodModel? lod0 = Lod(inst.Model, 0);
            InteriorGeometry? g = lod0?.Leaves;
            if (g == null)
                continue;
            g.LinkedInterior = inst.Model.Shape.LinkedInterior;
            g.LinkableFaces = inst.Model.Shape.Lods[(int)inst.Model.Shape.State0Lod].LinkableFaces;
            var m = new Mat();
            for (var i = 0; i < 9; ++i)
                m.M[i] = inst.M[i];
            m.P = new Vec3((float)inst.X, (float)inst.Y, (float)inst.Z);
            var box = new float[] { 1e30f, 1e30f, 1e30f, -1e30f, -1e30f, -1e30f };
            Float6 gb = g.Box;
            for (var k = 0; k < 8; ++k)
            {
                Vec3 w = m.ToWorld(new Vec3(gb[(k & 1) != 0 ? 3 : 0], gb[(k & 2) != 0 ? 4 : 1], gb[(k & 4) != 0 ? 5 : 2]));
                for (var i = 0; i < 3; ++i)
                {
                    box[i] = Math.Min(box[i], w[i]);
                    box[i + 3] = Math.Max(box[i + 3], w[i]);
                }
            }
            Container.PadInteriorBox(box);
            var e = new Container.Entry(inst, ObjectType.kInteriorType, Float6.Of(box), false, null);
            // isContainer -1 (the mission default): some extent >= 6 (docs/interiors.md 5.1)
            if (gb[3] - gb[0] >= 6 || gb[4] - gb[1] >= 6 || gb[5] - gb[2] >= 6)
            {
                e.DatabaseKind = Container.Database.Interior;
                e.ContainerMask = 0xffffffffu;
                e.Geometry = g;
                e.Transform = m;
            }
            c.Add(e);
        }
        return c;
    }

    /// <summary>Finds the container of a shape for the frame.</summary>
    /// <param name="objectBox">The box of the shape as min xyz, max xyz.</param>
    /// <param name="cam">The camera.</param>
    /// <returns>The container index, -1 for none, or <see cref="Culled"/> when the scope of the container misses the shape.</returns>
    public int ContainerOf(double[] objectBox, in Camera3D cam)
    {
        var i = FindContainer(objectBox);
        return i < 0 ? -1 : ScopeReaches(i, objectBox, cam) ? i : Culled;
    }

    private static Vec3 ToLocal(Instance inst, double x, double y, double z)
    {
        var m = inst.M;
        double dx = x - inst.X, dy = y - inst.Y, dz = z - inst.Z;
        return new Vec3((float)((dx * m[0]) + (dy * m[1]) + (dz * m[2])), (float)((dx * m[3]) + (dy * m[4]) + (dz * m[5])), (float)((dx * m[6]) + (dy * m[7]) + (dz * m[8])));
    }

    /// <summary>Draws the interiors.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="haze">The terrain parameters that give the haze.</param>
    /// <param name="contents">Called with the instance index just before the interior of that instance draws.</param>
    /// <remarks>The stock scope draws the contents of a container first: its shapes through <paramref name="contents"/>, the interiors filed in its database, then the container itself (InteriorShape_findObjects 00480648, vtable +0x98 runs the database and then the own image of the interior).</remarks>
    public void Draw(in Camera3D camera, TerrainParams haze, Action<int>? contents = null)
    {
        Camera3D cam = camera;
        DrawnSurfaces = DrawnInstances = 0;
        ++mFrame;
        // the first tick of an instance comes whether or not it is seen: the first frame that finds it here
        if (SimSeconds is double sim)
            foreach (Instance inst in Instances)
                inst.LightOrigin ??= sim;
        TerrainReached(cam); // refreshes the set of interiors reached through links for this camera point
        var n = Instances.Count;
        var parent = new int[n];
        var reached = new bool[n];
        for (var i = 0; i < n; ++i)
        {
            parent[i] = EnsureBox(Instances[i]) ? FindContainer(Instances[i].Box!, i) : -1;
            reached[i] = parent[i] < 0 || ScopeReaches(parent[i], Instances[i].Box!, cam);
        }
        void Tree(int index, int depth)
        {
            contents?.Invoke(index);
            if (depth < 8)
                for (var j = 0; j < n; ++j)
                    if (parent[j] == index && reached[j])
                        Tree(j, depth + 1);
            DrawOne(index);
        }
        for (var i = 0; i < n; ++i)
            if (parent[i] < 0)
                Tree(i, 0);

        void DrawOne(int index)
        {
            Instance inst = Instances[index];
            InteriorModel model = inst.Model;
            // camera in interior space: local = (world - p) * M^T (rotation matrices are orthonormal)
            double dx = cam.X - inst.X, dy = cam.Y - inst.Y, dz = cam.Z - inst.Z;
            var m = inst.M;
            double lx = (dx * m[0]) + (dy * m[1]) + (dz * m[2]), ly = (dx * m[3]) + (dy * m[4]) + (dz * m[5]), lz = (dx * m[6]) + (dy * m[7]) + (dz * m[8]);
            InteriorLodModel? lod0 = Lod(model, 0);
            if (lod0 == null)
                return;
            var bx = lod0.Geo.Box;
            // 00480be4: the camera is inside the box of the first detail level when each coordinate is at least the minimum and below the maximum
            var inside = lx >= bx[0] && lx < bx[3] && ly >= bx[1] && ly < bx[4] && lz >= bx[2] && lz < bx[5];
            // distance cut: the box must be within the visible distance
            double ddx = Math.Max(Math.Max(bx[0] - lx, 0), lx - bx[3]), ddy = Math.Max(Math.Max(bx[1] - ly, 0), ly - bx[4]),
                   ddz = Math.Max(Math.Max(bx[2] - lz, 0), lz - bx[5]);
            if (Math.Sqrt((ddx * ddx) + (ddy * ddy) + (ddz * ddz)) >= haze.Visible)
                return;
            // world centre of the box for the view test
            double cxl = lod0.Center[0], cyl = lod0.Center[1], czl = lod0.Center[2];
            double wcx = (cxl * m[0]) + (cyl * m[3]) + (czl * m[6]) + inst.X, wcy = (cxl * m[1]) + (cyl * m[4]) + (czl * m[7]) + inst.Y,
                   wcz = (cxl * m[2]) + (cyl * m[5]) + (czl * m[8]) + inst.Z;
            var (ccx, ccy, ccz) = cam.ToCamera(wcx, wcy, wcz);
            if (!inside && !SphereVisible(cam, ccx, ccy, ccz, lod0.Radius))
                return;
            // detail level (3.2): the sphere of the last level's box, projected by view depth
            InteriorLodModel? lastLod = Lod(model, Math.Max(Math.Min(model.Shape.Lods.Count, model.Lods.Length) - 1, 0));
            var lb = (lastLod ?? lod0).Geo.Box;
            var (sx, sy, sz, sr) = DetailSphere(lb);
            var (_, scy, _) = cam.ToCamera((sx * m[0]) + (sy * m[3]) + (sz * m[6]) + inst.X, (sx * m[1]) + (sy * m[4]) + (sz * m[7]) + inst.Y,
                                               (sx * m[2]) + (sy * m[5]) + (sz * m[8]) + inst.Z);
            var pixels = DetailPixels(scy, sr, cam.FocalLength);
            var computed = ModelLod(model, pixels, false);
            InteriorLodModel? cand = Lod(model, computed);
            var loaded = cand != null && cand.Base != null && cand.Atlases.Count > 0;
            var lodIndex = ChooseDetail(inside, mGateLinked.Contains(inst), loaded, inst.LodIndex, computed);
            InteriorLodModel? lod = Lod(model, lodIndex);
            if (lod == null || lod.Base == null || lod.Atlases.Count == 0)
                return;
            inst.LodIndex = lodIndex;
            LightAtlas atlas = InstanceAtlas(inst, lod, InstanceStep(inst));
            inst.DrawnLod = lod;
            inst.DrawnAtlas = atlas;
            // 1.4: one haze amount for the image, from the distance of the object position
            var od = Math.Sqrt(((cam.X - wcx) * (cam.X - wcx)) + ((cam.Y - wcy) * (cam.Y - wcy)) + ((cam.Z - wcz) * (cam.Z - wcz)));
            var hz = (float)TerrainLod.ObjectHaze(od, haze.HazeStart, haze.Visible);
            if (hz <= 0.015f)
                hz = 0; // 1.2: haze source none at 0.015 or less
            var skin = TeamSkin != null && inst.Team >= 0 ? TeamSkin(inst.Team) : "";
            if (skin != inst.Skin)
            {
                inst.Skin = skin;
                inst.Meshes.Clear();
            }
            DrawInstance(inst, lod, atlas.Texture, cam, (lx, ly, lz), hz);
            ++DrawnInstances;
        }
    }

    internal static bool SphereVisible(in Camera3D cam, double cx, double cy, double cz, double r)
    {
        // camera space: x right, y forward, z up
        if (cy + r < cam.Near)
            return false;
        double ha = cam.HorizontalFov / 2, va = Math.Atan(Math.Tan(ha) * cam.Height / cam.Width);
        if ((cx * Math.Cos(ha)) + (cy * Math.Sin(ha)) < -r)
            return false;
        if ((-cx * Math.Cos(ha)) + (cy * Math.Sin(ha)) < -r)
            return false;
        if ((cz * Math.Cos(va)) + (cy * Math.Sin(va)) < -r)
            return false;
        if ((-cz * Math.Cos(va)) + (cy * Math.Sin(va)) < -r)
            return false;
        return true;
    }

    // The world-space triangles of every surface of `lod` for this instance (3.3 steps 7 to 9 minus the per-frame parts).
    private SurfaceMesh[] BuildMeshes(Instance inst, InteriorLodModel lod)
    {
        InteriorGeometryData g = lod.Geo;
        InteriorLighting lt = lod.Base!;
        var m = inst.M;
        var meshes = new SurfaceMesh[g.Surfaces.Length];
        var shiftMip = (int)Math.Min(g.HighestMipLevel, 8u);
        for (var s = 0; s < g.Surfaces.Length; ++s)
        {
            InteriorGeometryData.Surface surf = g.Surfaces[s];
            if (surf.VertexCount < 3 || (surf.Plane * 4) + 3 >= g.Planes.Length)
                continue;
            // texture region in texels (3.5), light map coordinates in pixels of 2^lightScaleShift texels with one margin pixel
            double sizeX = (surf.TexSizeX + 1) << shiftMip, sizeY = (surf.TexSizeY + 1) << shiftMip;
            double lmDiv = 1 << (int)lt.LightScaleShift;
            var mat = surf.Material == 255 ? -1 : surf.Material;
            var matName = mat >= 0 && mat < lod.Materials.Count ? lod.Materials[mat] : null;
            if (matName != null && inst.Skin.Length > 0)
                matName = Skins.Resolve(matName, inst.Skin, n => GfxAssets.Read(mAssets.Res, n, out _), false);
            IGfxTexture? tex = MaterialTexture(matName);
            double bw = tex?.Width ?? 1, bh = tex?.Height ?? 1;
            var (ax, ay) = lod.LmPos[s];
            InteriorLighting.Surface lmSurf = lt.Surfaces[s];
            var verts = new Vertex3D[surf.VertexCount];
            for (var k = 0; k < surf.VertexCount; ++k)
            {
                var vi = (int)surf.FirstVertex + k;
                int a = g.VertexPoint3[vi] * 3, b = g.VertexPoint2[vi] * 2;
                float px = g.Point3[a], py = g.Point3[a + 1], pz = g.Point3[a + 2];
                float u = g.Point2[b], v = g.Point2[b + 1];
                double tx = surf.TexOffsetX + (u * sizeX), ty = surf.TexOffsetY + (v * sizeY);
                // 005d9a84: pixel x = map origin + 0.5 + the surface's own light map offset / 2^shift + u * regionSize / 2^shift
                double lu = (ax + 0.5 + (lmSurf.OffsetX / lmDiv) + (u * sizeX / lmDiv)) / lod.AtlasW,
                       lv = (ay + 0.5 + (lmSurf.OffsetY / lmDiv) + (v * sizeY / lmDiv)) / lod.AtlasH;
                if (matName == null)
                    (tx, ty, bw, bh) = (0, 0, 1, 1);
                verts[k] = new Vertex3D((px * m[0]) + (py * m[3]) + (pz * m[6]) + (float)inst.X, (px * m[1]) + (py * m[4]) + (pz * m[7]) + (float)inst.Y,
                                        (px * m[2]) + (py * m[5]) + (pz * m[8]) + (float)inst.Z, (float)(tx / bw), (float)(ty / bh), (float)lu,
                                        (float)lv, 0);
            }
            var tris = new Vertex3D[(verts.Length - 2) * 3];
            for (int k = 1, n = 0; k + 1 < verts.Length; ++k)
            {
                tris[n++] = verts[0];
                tris[n++] = verts[k];
                tris[n++] = verts[k + 1];
            }
            meshes[s] = new SurfaceMesh(tris, mat, tex);
        }
        return meshes;
    }

    /// <summary>Tells whether a triangle is seen counter clockwise from the camera, the winding the device cull drops.</summary>
    /// <param name="a">The first vertex.</param>
    /// <param name="b">The second vertex.</param>
    /// <param name="c">The third vertex.</param>
    /// <param name="camX">The camera x.</param>
    /// <param name="camY">The camera y.</param>
    /// <param name="camZ">The camera z.</param>
    /// <returns>True for a back face.</returns>
    /// <remarks>The same sign rule as the shape renderer: the kept faces are clockwise seen from outside. Stock draws the visible surfaces one polygon at a time in surface index order (0055ec98 walks the surface bit set ascending), so equal 16 bit depths resolve by index. Consecutive surfaces of one material share a draw call.</remarks>
    public static bool BackFacing(in Vertex3D a, in Vertex3D b, in Vertex3D c, double camX, double camY, double camZ)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
        return (nx * (camX - a.X)) + (ny * (camY - a.Y)) + (nz * (camZ - a.Z)) > 0;
    }

    private void DrawInstance(Instance inst, InteriorLodModel lod, IGfxTexture? atlas, in Camera3D cam, (double x, double y, double z) camLocal, float haze)
    {
        InteriorGeometryData g = lod.Geo;
        bool[]? selected = null;
        if (lod.Leaves != null && lod.Leaves.SurfaceCountTotal == g.Surfaces.Length)
        {
            var m = inst.M;
            Camera3D c = cam;
            selected = lod.Leaves.SelectSurfaces(new Vec3((float)camLocal.x, (float)camLocal.y, (float)camLocal.z), (x, y, z, r) =>
            {
                var (cx, cy, cz) = c.ToCamera((x * m[0]) + (y * m[3]) + (z * m[6]) + inst.X, (x * m[1]) + (y * m[4]) + (z * m[7]) + inst.Y,
                                              (x * m[2]) + (y * m[5]) + (z * m[8]) + inst.Z);
                return SphereVisible(c, cx, cy, cz, r);
            });
        }
        if (!inst.Meshes.TryGetValue(lod, out SurfaceMesh[]? meshes))
            inst.Meshes[lod] = meshes = BuildMeshes(inst, lod);
        List<Vertex3D> list = mBatch;
        list.Clear();
        var curKey = int.MinValue;
        IGfxTexture? curTex = null;
        for (var s = 0; s < meshes.Length; ++s)
        {
            Vertex3D[]? tris = meshes[s].Triangles;
            if (tris == null)
                continue;
            InteriorGeometryData.Surface surf = g.Surfaces[s];
            if (selected != null)
            {
                if (!selected[s])
                    continue;
            }
            else
            {
                // 3.3 step 6: back-face removal against the surface plane
                var pi = surf.Plane * 4;
                var side = (g.Planes[pi] * camLocal.x) + (g.Planes[pi + 1] * camLocal.y) + (g.Planes[pi + 2] * camLocal.z) + g.Planes[pi + 3];
                if ((side > 0) != surf.PlaneFront)
                    continue;
            }
            if (list.Count > 0 && meshes[s].Key != curKey)
            {
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(curTex, atlas));
                list.Clear();
            }
            curKey = meshes[s].Key;
            curTex = meshes[s].Texture;
            var start = list.Count;
            // Device cull (renderer.md 5.6 point 4): beginScene3d (005daa30) turns the GL back face cull on with GL_CW as the front
            // face, so every polygon of the interior draw loses the triangles seen from behind, clipped or not. The stock surface
            // vertices run clockwise seen from the plane's front side (checked on the stock .dig files: the vertex order normal
            // points away from the viewing side for every surface), the winding BackFacing keeps.
            for (var k = 0; k + 2 < tris.Length; k += 3)
                if (!BackFacing(tris[k], tris[k + 1], tris[k + 2], cam.X, cam.Y, cam.Z))
                {
                    list.Add(tris[k]);
                    list.Add(tris[k + 1]);
                    list.Add(tris[k + 2]);
                }
            if (haze != 0)
            {
                Span<Vertex3D> added = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[start..];
                for (var k = 0; k < added.Length; ++k)
                    added[k].Haze = haze;
            }
            ++DrawnSurfaces;
        }
        if (list.Count > 0)
            mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(curTex, atlas));
        list.Clear();
    }

    /// <summary>Casts the ray of the shape light probe (ShapeBase_lightProbe 0044e6f4).</summary>
    /// <param name="sx">The start x.</param>
    /// <param name="sy">The start y.</param>
    /// <param name="sz">The start z.</param>
    /// <param name="dx">The direction x.</param>
    /// <param name="dy">The direction y.</param>
    /// <param name="dz">The direction z.</param>
    /// <param name="length">The ray length.</param>
    /// <param name="t">The distance of the hit.</param>
    /// <param name="light">The light at the hit, 0 to 1 colours.</param>
    /// <returns>True when the ray hit a drawn interior instance.</returns>
    /// <remarks>It finds the nearest surface along start + t * dir, 0 &lt;= t &lt;= length, and the nibbles of its light map at the hit point (the current atlas of the instance, nearest texel of the light coordinates of the surface there).</remarks>
    public bool Probe(double sx, double sy, double sz, double dx, double dy, double dz, double length, out double t, out (float r, float g, float b) light) =>
        Probe(sx, sy, sz, dx, dy, dz, length, out t, out light, out _);

    /// <summary>Casts the same ray and also returns the unit normal of the surface it hits, turned to face the ray origin (the debris collision, Debris_collide 00472ad0).</summary>
    /// <param name="sx">The start x.</param>
    /// <param name="sy">The start y.</param>
    /// <param name="sz">The start z.</param>
    /// <param name="dx">The direction x.</param>
    /// <param name="dy">The direction y.</param>
    /// <param name="dz">The direction z.</param>
    /// <param name="length">The ray length.</param>
    /// <param name="t">The distance of the hit.</param>
    /// <param name="light">The light at the hit, 0 to 1 colours.</param>
    /// <param name="normal">The unit normal of the surface.</param>
    /// <returns>True when the ray hit a drawn interior instance.</returns>
    public bool Probe(double sx, double sy, double sz, double dx, double dy, double dz, double length, out double t, out (float r, float g, float b) light,
                      out (double x, double y, double z) normal)
    {
        t = double.MaxValue;
        normal = default;
        light = default;
        foreach (Instance inst in Instances)
        {
            LightAtlas? atlas = inst.DrawnAtlas;
            InteriorLodModel? lod = inst.DrawnLod;
            if (atlas == null || lod == null || !inst.Meshes.TryGetValue(lod, out SurfaceMesh[]? meshes) || inst.Box == null)
                continue;
            var bx = inst.Box;
            if (sx < bx[0] - length && dx >= 0 || sx > bx[3] + length && dx <= 0 || sy < bx[1] - length && dy >= 0 || sy > bx[4] + length && dy <= 0 ||
                sz < bx[2] - length && dz >= 0 || sz > bx[5] + length && dz <= 0)
                continue;
            foreach (SurfaceMesh mesh in meshes)
            {
                Vertex3D[]? tr = mesh.Triangles;
                if (tr == null)
                    continue;
                for (var i = 0; i + 2 < tr.Length; i += 3)
                {
                    Vertex3D a = tr[i], b = tr[i + 1], c = tr[i + 2];
                    double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z, e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
                    double px = (dy * e2z) - (dz * e2y), py = (dz * e2x) - (dx * e2z), pz = (dx * e2y) - (dy * e2x);
                    var det = (e1x * px) + (e1y * py) + (e1z * pz);
                    if (Math.Abs(det) < 1e-12)
                        continue;
                    double ix = sx - a.X, iy = sy - a.Y, iz = sz - a.Z, u = ((ix * px) + (iy * py) + (iz * pz)) / det;
                    if (u is < 0 or > 1)
                        continue;
                    double qx = (iy * e1z) - (iz * e1y), qy = (iz * e1x) - (ix * e1z), qz = (ix * e1y) - (iy * e1x);
                    var v = ((dx * qx) + (dy * qy) + (dz * qz)) / det;
                    if (v < 0 || u + v > 1)
                        continue;
                    var tt = ((e2x * qx) + (e2y * qy) + (e2z * qz)) / det;
                    if (tt < 0 || tt > length || tt >= t)
                        continue;
                    double lu = a.Lu + (u * (b.Lu - a.Lu)) + (v * (c.Lu - a.Lu)), lv = a.Lv + (u * (b.Lv - a.Lv)) + (v * (c.Lv - a.Lv));
                    int tx = Math.Clamp((int)(lu * lod.AtlasW), 0, lod.AtlasW - 1), ty = Math.Clamp((int)(lv * lod.AtlasH), 0, lod.AtlasH - 1);
                    var o = ((ty * lod.AtlasW) + tx) * 4;
                    if (o + 2 >= atlas.Rgba.Length)
                        continue;
                    t = tt;
                    light = (atlas.Rgba[o] / 255f, atlas.Rgba[o + 1] / 255f, atlas.Rgba[o + 2] / 255f);
                    double nx = (e1y * e2z) - (e1z * e2y), ny = (e1z * e2x) - (e1x * e2z), nz = (e1x * e2y) - (e1y * e2x), nl = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                    if (nl > 1e-12)
                    {
                        double sign = (nx * dx) + (ny * dy) + (nz * dz) > 0 ? -1 : 1;
                        normal = (sign * nx / nl, sign * ny / nl, sign * nz / nl);
                    }
                }
            }
        }
        return t != double.MaxValue;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (IGfxTexture? t in mTextures.Values)
            t?.Dispose();
        mWhite?.Dispose();
        foreach (InteriorModel? m in mModels.Values)
            if (m != null)
                foreach (InteriorLodModel? l in m.Lods)
                    if (l != null)
                        foreach (LightAtlas a in l.Atlases)
                            a.Texture?.Dispose();
    }
}
