using OpenTribes.Client.Shapes;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

namespace OpenTribes.Client.World;

// Shelf packing of rectangles into one atlas (Internal: the original keeps one light map per surface, 3.5).
public static class AtlasPacker
{
    // Returns the position of each rectangle and the atlas size. Rectangles are placed on shelves in order of decreasing
    // height; the width is fixed, the height grows.
    public static (int x, int y)[] Pack(IReadOnlyList<(int w, int h)> sizes, int width, out int height)
    {
        var order = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i].h).ThenBy(i => i).ToArray();
        var pos = new (int, int)[sizes.Count];
        int x = 0, y = 0, shelf = 0;
        foreach (int i in order)
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

// world-render.md 3.7: the interior light maps of one detail level, built at a light time.
public static class InteriorLightMaps
{
    // Surface light maps (u16 nibbles M R G B) after the base map (or the mission map when the mission lighting has it)
    // and every light's current state data have been applied (3.7 steps 2 and 3). `time` is the same for every light.
    public static ushort[]?[] Build(InteriorLighting baseLt, InteriorLighting? mission, float time)
    {
        var maps = BaseMaps(baseLt, mission);
        for (int i = 0; i < baseLt.Lights.Length; ++i)
            Apply(maps, baseLt, mission, i, time, null);
        return maps;
    }

    public static ushort[]?[] BaseMaps(InteriorLighting baseLt, InteriorLighting? mission)
    {
        var maps = new ushort[]?[baseLt.Surfaces.Length];
        for (int s = 0; s < maps.Length; ++s)
            maps[s] = baseLt.SurfaceMap(s, mission);
        return maps;
    }

    // The surfaces a light writes to in any of its states.
    public static IEnumerable<int> LightSurfaces(InteriorLighting baseLt, InteriorLighting.Light light)
    {
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length)
            yield break;
        for (int j = 0; j < light.StateCount; ++j)
        {
            InteriorLighting.State st = baseLt.States[light.FirstState + j];
            int count = (ushort)st.DataCount, index = (ushort)st.DataIndex;
            for (int k = 0; k < count && index + k < baseLt.StateDatas.Length; ++k)
                yield return baseLt.StateDatas[index + k].Surface;
        }
    }

    // Adds the current state of one light at `time` (its state data, in its interpolated colour) to `maps`; only the surfaces of
    // `only` when given.
    public static void Apply(ushort[]?[] maps, InteriorLighting baseLt, InteriorLighting? mission, int lightIndex, float time, HashSet<int>? only)
    {
        InteriorLighting.Light light = baseLt.Lights[lightIndex];
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length)
            return;
        var records = baseLt.States.AsSpan(light.FirstState, light.StateCount);
        int state = InteriorLighting.SelectState(records, light.StateCount, time);
        Apply(maps, baseLt, mission, lightIndex, state, InteriorLighting.ColorAt(records, light.Duration, state, time), only);
    }

    // The same with the state and colour given (random lights pick their own).
    public static void Apply(ushort[]?[] maps, InteriorLighting baseLt, InteriorLighting? mission, int lightIndex, int state,
                             (int mono, int r, int g, int b) colour, HashSet<int>? only)
    {
        InteriorLighting.Light light = baseLt.Lights[lightIndex];
        if (light.StateCount <= 0 || light.FirstState < 0 || light.FirstState + light.StateCount > baseLt.States.Length || state < 0 ||
            state >= light.StateCount)
            return;
        InteriorLighting.State st = baseLt.States[light.FirstState + state];
        int count = (ushort)st.DataCount, index = (ushort)st.DataIndex;
        for (int k = 0; k < count && index + k < baseLt.StateDatas.Length; ++k)
        {
            InteriorLighting.StateData sd = baseLt.StateDatas[index + k];
            if (sd.MapIndex < 0 || sd.Surface < 0 || sd.Surface >= maps.Length || maps[sd.Surface] == null)
                continue;
            if (only != null && !only.Contains(sd.Surface))
                continue;
            InteriorLighting.Surface surf = baseLt.Surfaces[sd.Surface];
            byte[] src = baseLt.MapData;
            long off = sd.MapIndex;
            if (mission != null && mission.TryMissionOffset((uint)sd.MapIndex, out uint dest))
            {
                src = mission.MapData;
                off = dest;
            }
            ushort[] m = maps[sd.Surface]!;
            int px = surf.SizeX * surf.SizeY;
            for (int p = 0; p < px && p < m.Length && off + p < src.Length; ++p)
                m[p] = InteriorLighting.AddLight(m[p], colour, src[off + p]);
        }
    }
}

// One detail level of a shape: geometry, material bitmaps and one light atlas.
public sealed class InteriorLodModel
{
    public InteriorGeometryData Geo = null!;
    // The geometry with its leaves, for the visible surface selection of the stock renderer (world-render.md 3.3).
    public InteriorGeometry? Leaves;
    public List<string?> Materials = new();
    public InteriorLighting? Base, Mission;
    public (int x, int y)[] LmPos = Array.Empty<(int, int)>();
    public int AtlasW, AtlasH;
    public byte[] AtlasRgba = Array.Empty<byte>();
    public IGfxTexture? Atlas;
    public int MinPixels;
    public float[] Center = new float[3];
    public float Radius;
    // Animated lights (world-render.md 3.7 Timing): the lights that run on the client (auto start flag), the surfaces they touch,
    // the maps those surfaces start from, and the state last built (-1 none yet).
    public int[] Running = Array.Empty<int>();
    public HashSet<int> RunSurfaces = new();
    public ushort[]?[] BaseMaps = Array.Empty<ushort[]?>();
    public long RunKey = long.MinValue;
    public float[] RandomTime = Array.Empty<float>();
    public int[] RandomState = Array.Empty<int>();
    public (int mono, int r, int g, int b)[] RandomColor = Array.Empty<(int, int, int, int)>();
}

public sealed class InteriorModel
{
    public string Name = "";
    public InteriorShapeFile Shape = new();
    public InteriorLodModel?[] Lods = Array.Empty<InteriorLodModel?>();
}

// world-render.md 3: draws the interior instances of a mission.
public sealed class InteriorRenderer : IDisposable
{
    public sealed class Instance
    {
        public InteriorModel Model = null!;
        public double X, Y, Z, Rx, Ry, Rz;
        // The team of the GameBase part (-1 none) and the skin its textures were built with ("" none).
        public int Team = -1;
        public string Skin = "";
        public float[] M = new float[9];
        // World box of the geometry padded by 3 on every side (docs/game-stream-ghosts.md 6.2), null until first needed; IsContainer: some extent >= 6.
        internal double[]? Box;
        internal bool IsContainer;
        // The scope of the camera in this interior (6.2 PVS database): the camera leaf, its visible leaves, whether it sees outside.
        internal double ScopeX = double.NaN, ScopeY, ScopeZ;
        internal bool ScopeNothing, ScopeOutside;
        internal HashSet<int>? ScopeLeaves;
        // World-space triangles of each surface per detail level, built on first draw (the instance never moves).
        internal readonly Dictionary<InteriorLodModel, SurfaceMesh[]> Meshes = new();
    }

    // One surface of one instance, already in world space with haze 0: the fan triangles and the batch key (material index).
    internal readonly record struct SurfaceMesh(Vertex3D[] Triangles, int Key, IGfxTexture? Texture);

    // Draw scratch kept between frames: the vertices of the current run of surfaces with one material.
    private readonly List<Vertex3D> mBatch = new();

    private readonly IGfxDevice mDev;
    private readonly WorldAssets mAssets;
    private readonly Dictionary<string, InteriorModel?> mModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IGfxTexture?> mTextures = new(StringComparer.OrdinalIgnoreCase);
    private IGfxTexture? mWhite;
    public readonly List<Instance> Instances = new();
    // The light clock in seconds: the lights advance in steps of $pref::interiorLightUpdateMS (67 ms, Tribes.exe 0047f3f4).
    public float LightTime;
    public const float LightStep = 0.067f;
    private readonly System.Diagnostics.Stopwatch mClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Random mRandom = new(0x5eed);
    public int DrawnSurfaces, DrawnInstances;

    public InteriorRenderer(IGfxDevice dev, WorldAssets assets)
    {
        mDev = dev;
        mAssets = assets;
    }

    // interiors.md 5.2: the Euler transform, row-vector convention, world = local * M + p.
    public static float[] EulerMatrix(double x, double y, double z)
    {
        double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
        if (x == 0 && y == 0)
            return new float[] { (float)cz, (float)sz, 0, (float)-sz, (float)cz, 0, 0, 0, 1 };
        return new[]
        {
            (float)(cz * cy - sx * sy * sz), (float)(cy * sz + cz * sx * sy), (float)(-cx * sy),
            (float)(-cx * sz), (float)(cx * cz), (float)sx,
            (float)(cz * sy + cy * sx * sz), (float)(sz * sy - cz * cy * sx), (float)(cx * cy),
        };
    }

    // interiors.md L3, 4: <base>-<digits>-<n>.dil is the mission lighting of <base>-<digits>.dil.
    public static string BaseLightingName(string name)
    {
        int dash = name.LastIndexOf('-');
        if (dash <= 0)
            return name;
        int i = dash - 1;
        while (i > 0 && name[i] >= '0' && name[i] <= '9')
            --i;
        return name[i] == '-' ? name[..dash] + ".dil" : name;
    }

    // The skin base of a team (the client's team table); materials named `base.<name>` take `<skin>.<name>` when that exists.
    public Func<int, string>? TeamSkin;

    public bool AddInstance(string fileName, double x, double y, double z, double rx, double ry, double rz, int team = -1)
    {
        string resolved = Interior.ResolveInteriorName(mAssets.Res, fileName);
        if (resolved.Length == 0)
            return false;
        if (!mModels.TryGetValue(resolved, out InteriorModel? model))
        {
            model = null;
            var shape = new InteriorShapeFile();
            if (mAssets.Res.Read(resolved, out byte[] dis) && InteriorShapeFile.Parse(dis, shape) && shape.Lods.Count > 0)
                model = new InteriorModel { Name = resolved, Shape = shape, Lods = new InteriorLodModel?[shape.Lods.Count] };
            mModels[resolved] = model;
        }
        if (model == null)
            return false;
        Instances.Add(new Instance { Model = model, X = x, Y = y, Z = z, Rx = rx, Ry = ry, Rz = rz, Team = team, M = EulerMatrix(rx, ry, rz) });
        return true;
    }

    // Adds a model built in memory (tests, tools): the lighting atlas of `lod` is built here.
    public Instance Install(InteriorModel model, InteriorLodModel lod, double x, double y, double z, double rx, double ry, double rz)
    {
        BuildAtlas(lod);
        float[] b = lod.Geo.Box;
        lod.Center = new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
        lod.Radius = (float)Math.Sqrt(Math.Pow(b[3] - b[0], 2) + Math.Pow(b[4] - b[1], 2) + Math.Pow(b[5] - b[2], 2)) / 2;
        model.Lods = new InteriorLodModel?[] { lod };
        var inst = new Instance { Model = model, X = x, Y = y, Z = z, Rx = rx, Ry = ry, Rz = rz, M = EulerMatrix(rx, ry, rz) };
        Instances.Add(inst);
        return inst;
    }

    private InteriorLodModel? Lod(InteriorModel model, int index)
    {
        if (model.Lods[index] != null)
            return model.Lods[index];
        InteriorShapeFile.Lod l = model.Shape.Lods[index];
        if (!mAssets.Res.Read(l.Geometry, out byte[] dig))
            return null;
        InteriorGeometryData? geo = InteriorGeometryData.Parse(dig);
        if (geo == null)
            return null;
        var lod = new InteriorLodModel { Geo = geo, Leaves = InteriorGeometry.Parse(dig), MinPixels = (int)LodMinPixels(model.Shape, index) };
        lod.Materials = mAssets.MaterialList(model.Shape.MaterialList) ?? new List<string?>();
        int li = (int)l.LightStateIndex;
        if (li < model.Shape.LodLights.Count)
        {
            string lightName = model.Shape.LodLights[li];
            if (mAssets.Res.Read(lightName, out byte[] lb))
            {
                InteriorLighting? first = InteriorLighting.Parse(lb);
                if (first != null && first.IsMission && first.BuildId == geo.BuildId)
                {
                    lod.Mission = first;
                    string baseName = BaseLightingName(lightName);
                    if (mAssets.Res.Read(baseName, out byte[] bb))
                        lod.Base = InteriorLighting.Parse(bb);
                }
                else if (first != null && !first.IsMission)
                {
                    lod.Base = first;
                }
                else if (first != null && first.IsMission)
                {
                    // 3.7: a mismatched mission file falls back to the base lighting.
                    if (mAssets.Res.Read(BaseLightingName(lightName), out byte[] bb))
                        lod.Base = InteriorLighting.Parse(bb);
                }
            }
        }
        if (lod.Base != null && lod.Base.Surfaces.Length != geo.Surfaces.Length)
            lod.Base = null;
        BuildAtlas(lod);
        float[] b = geo.Box;
        lod.Center = new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
        lod.Radius = (float)Math.Sqrt(Math.Pow(b[3] - b[0], 2) + Math.Pow(b[4] - b[1], 2) + Math.Pow(b[5] - b[2], 2)) / 2;
        model.Lods[index] = lod;
        return lod;
    }

    // Loads every detail level of every model in use (geometry, lighting, light atlas, material bitmaps), so the first frame
    // that sees an interior from a new distance does not stall on file reads and texture uploads.
    public void Preload()
    {
        foreach (InteriorModel? model in mModels.Values)
            if (model != null)
                for (int i = 0; i < model.Lods.Length; ++i)
                {
                    InteriorLodModel? lod = Lod(model, i);
                    if (lod?.Base == null)
                        continue;
                    foreach (string? name in lod.Materials)
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
        for (int i = 0; i < lt.Lights.Length; ++i)
        {
            InteriorLighting.Light l = lt.Lights[i];
            if (l.AutoStart && l.StateCount > 1 && l.Duration > 0 && l.FirstState >= 0 && l.FirstState + l.StateCount <= lt.States.Length)
                running.Add(i);
        }
        lod.Running = running.ToArray();
        lod.RunSurfaces = new HashSet<int>();
        foreach (int i in lod.Running)
            foreach (int surf in InteriorLightMaps.LightSurfaces(lt, lt.Lights[i]))
                lod.RunSurfaces.Add(surf);
        lod.RandomTime = new float[lod.Running.Length];
        lod.RandomState = new int[lod.Running.Length];
        lod.RandomColor = new (int, int, int, int)[lod.Running.Length];
        Array.Fill(lod.RandomState, -1); // ITRInstance_changeDetail: state -1, so a random light shows nothing before its first draw
        lod.BaseMaps = InteriorLightMaps.BaseMaps(lt, lod.Mission);
        ushort[]?[] maps = new ushort[]?[lod.BaseMaps.Length];
        for (int i = 0; i < maps.Length; ++i)
            maps[i] = (ushort[]?)lod.BaseMaps[i]?.Clone();
        ApplyLights(lod, maps, null, StepOf(LightTime), true);
        // Every map sits in the atlas with a border of one replicated pixel, so the bilinear filter never reads a neighbour
        // map (or the empty atlas) at the edge of a surface.
        var sizes = new (int, int)[maps.Length];
        for (int i = 0; i < maps.Length; ++i)
            sizes[i] = maps[i] == null ? (0, 0) : (lt.Surfaces[i].SizeX + 2, lt.Surfaces[i].SizeY + 2);
        lod.AtlasW = 1024;
        (int x, int y)[] packed = AtlasPacker.Pack(sizes, lod.AtlasW, out int h);
        lod.LmPos = new (int, int)[maps.Length];
        for (int i = 0; i < maps.Length; ++i)
            lod.LmPos[i] = (packed[i].x + 1, packed[i].y + 1);
        lod.AtlasH = Math.Max(h, 1);
        lod.AtlasRgba = new byte[lod.AtlasW * lod.AtlasH * 4];
        for (int i = 0; i < maps.Length; ++i)
            if (maps[i] != null)
                WriteSurface(lod, i, maps[i]!);
        lod.Atlas = mDev.CreateTexture3D(lod.AtlasRgba, lod.AtlasW, lod.AtlasH, new TextureOptions(false, false, true));
        lod.RunKey = RunKey(lod, StepOf(LightTime));
    }

    private static long StepOf(float time) => (long)(time / LightStep + 0.5f);

    // Every light of the lod: the running ones at their cycle position of `step`, the others at time 0 (their first state).
    private void ApplyLights(InteriorLodModel lod, ushort[]?[] maps, HashSet<int>? only, long step, bool advanceRandom)
    {
        InteriorLighting lt = lod.Base!;
        int run = 0;
        for (int i = 0; i < lt.Lights.Length; ++i)
        {
            if (run < lod.Running.Length && lod.Running[run] == i)
            {
                InteriorLighting.Light l = lt.Lights[i];
                if (!l.Random)
                    InteriorLightMaps.Apply(maps, lt, lod.Mission, i, (float)((step * (double)LightStep) % l.Duration), only);
                else if (lod.RandomState[run] >= 0)
                    InteriorLightMaps.Apply(maps, lt, lod.Mission, i, lod.RandomState[run], lod.RandomColor[run], only);
                ++run;
            }
            else
            {
                InteriorLightMaps.Apply(maps, lt, lod.Mission, i, 0f, only);
            }
        }
    }

    // A number that changes when the state or colour of a running light changes.
    private long RunKey(InteriorLodModel lod, long step)
    {
        InteriorLighting lt = lod.Base!;
        var h = new HashCode();
        for (int r = 0; r < lod.Running.Length; ++r)
        {
            InteriorLighting.Light l = lt.Lights[lod.Running[r]];
            if (l.Random)
            {
                h.Add(lod.RandomState[r]);
                continue;
            }
            float t = (float)((step * (double)LightStep) % l.Duration);
            var records = lt.States.AsSpan(l.FirstState, l.StateCount);
            int state = InteriorLighting.SelectState(records, l.StateCount, t);
            h.Add(state);
            h.Add(InteriorLighting.ColorAt(records, l.Duration, state, t));
        }
        return h.ToHashCode();
    }

    private static void WriteSurface(InteriorLodModel lod, int i, ushort[] map)
    {
        InteriorLighting lt = lod.Base!;
        int w = lt.Surfaces[i].SizeX, hh = lt.Surfaces[i].SizeY;
        if (w <= 0 || hh <= 0)
            return;
        var (px, py) = lod.LmPos[i];
        for (int y = -1; y <= hh; ++y)
            for (int x = -1; x <= w; ++x)
            {
                int k = Math.Clamp(y, 0, hh - 1) * w + Math.Clamp(x, 0, w - 1);
                if (k >= map.Length)
                    continue;
                var (r, g, b) = TerrainData.ExpandLightPacked(map[k]);
                int o = ((py + y) * lod.AtlasW + px + x) * 4;
                lod.AtlasRgba[o] = r;
                lod.AtlasRgba[o + 1] = g;
                lod.AtlasRgba[o + 2] = b;
                lod.AtlasRgba[o + 3] = 255;
            }
    }

    // ITRInstance light advance, once per light step: rebuilds the maps of the surfaces the running lights touch when one of them
    // changed state or colour.
    private void UpdateLights(InteriorLodModel lod, long step)
    {
        if (lod.Running.Length == 0 || lod.Base == null)
            return;
        InteriorLighting lt = lod.Base;
        for (int r = 0; r < lod.Running.Length; ++r)
        {
            InteriorLighting.Light l = lt.Lights[lod.Running[r]];
            if (!l.Random)
                continue;
            // 00556cb0: the time wraps by the end time of record 1 and a state is drawn then
            var records = lt.States.AsSpan(l.FirstState, l.StateCount);
            float period = records[1].EndTime;
            lod.RandomTime[r] += LightStep;
            if (period > 0 && lod.RandomTime[r] >= period)
            {
                while (lod.RandomTime[r] >= period)
                    lod.RandomTime[r] -= period;
                float roll = l.Duration * (float)mRandom.NextDouble();
                int pick = l.StateCount - 1;
                for (int i = 0; i < l.StateCount - 1; ++i)
                    if (roll < records[i + 1].EndTime)
                    {
                        pick = i;
                        break;
                    }
                lod.RandomState[r] = pick;
                var c = records[pick];
                lod.RandomColor[r] = ((c.Mono + 0x80) >> 8, (c.R + 0x80) >> 8, (c.G + 0x80) >> 8, (c.B + 0x80) >> 8);
            }
        }
        long key = RunKey(lod, step);
        if (key == lod.RunKey)
            return;
        lod.RunKey = key;
        var maps = new ushort[]?[lod.BaseMaps.Length];
        foreach (int s in lod.RunSurfaces)
            maps[s] = (ushort[]?)lod.BaseMaps[s]?.Clone();
        ApplyLights(lod, maps, lod.RunSurfaces, step, false);
        foreach (int s in lod.RunSurfaces)
            if (maps[s] != null)
                WriteSurface(lod, s, maps[s]!);
        lod.Atlas?.Dispose();
        lod.Atlas = mDev.CreateTexture3D(lod.AtlasRgba, lod.AtlasW, lod.AtlasH, new TextureOptions(false, false, true));
    }

    private IGfxTexture? MaterialTexture(string? name)
    {
        if (name == null)
        {
            if (mWhite == null)
                mWhite = mDev.CreateTexture3D(new byte[] { 255, 255, 255, 255 }, 1, 1, new TextureOptions(false, true, false));
            return mWhite;
        }
        if (mTextures.TryGetValue(name, out IGfxTexture? t))
            return t;
        PbmpBitmap? bmp = mAssets.Bitmap(name);
        t = bmp != null && bmp.Width > 0 && bmp.Height > 0
            ? mDev.CreateTexture3D(TerrainData.QuantizeRgba5(mAssets.Rgba(bmp, true)), bmp.Width, bmp.Height, new TextureOptions(true, true, true))
            : null;
        mTextures[name] = t;
        return t;
    }

    // 3.2: the first LOD whose minPixels is less than the projected size of the bounding sphere, else the last. Inside the
    // box the first (highest detail) LOD is forced (3.1).
    public static int FindLod(IReadOnlyList<int> minPixels, int projectedPixels)
    {
        for (int i = 0; i < minPixels.Count; ++i)
            if (minPixels[i] < projectedPixels)
                return i;
        return minPixels.Count - 1;
    }

    private static int ModelLod(InteriorModel model, int pixels, bool inside)
    {
        if (inside || model.Shape.Lods.Count == 0)
            return 0;
        IReadOnlyList<InteriorShapeFile.Lod> lods = model.Shape.Lods;
        for (int i = 0; i < lods.Count; ++i)
            if ((int)lods[i].MinPixels < pixels)
                return i;
        return lods.Count - 1;
    }

    // Culled: the object is in a container interior whose camera scope does not reach it.
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
        for (int c = 0; c < 8; ++c)
        {
            double px = b[(c & 1) != 0 ? 3 : 0], py = b[(c & 2) != 0 ? 4 : 1], pz = b[(c & 4) != 0 ? 5 : 2];
            double wx = px * m[0] + py * m[3] + pz * m[6] + inst.X, wy = px * m[1] + py * m[4] + pz * m[7] + inst.Y,
                   wz = px * m[2] + py * m[5] + pz * m[8] + inst.Z;
            box[0] = Math.Min(box[0], wx - 3); box[1] = Math.Min(box[1], wy - 3); box[2] = Math.Min(box[2], wz - 3);
            box[3] = Math.Max(box[3], wx + 3); box[4] = Math.Max(box[4], wy + 3); box[5] = Math.Max(box[5], wz + 3);
        }
        inst.Box = box;
        inst.IsContainer = b[3] - b[0] >= 6 || b[4] - b[1] >= 6 || b[5] - b[2] >= 6;
        return true;
    }

    private static double Volume(double[] b) => (b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2]);

    // The container interior (instance index) that holds an object with this world box (min xyz, max xyz), -1 none: the first
    // container whose padded world box holds the whole box (SimContainer::addObject 004f9c04). Between interiors the larger
    // box is the container (a new container pulls in what it holds, findContained), equal boxes by index.
    public int FindContainer(double[] objectBox, int exclude = -1)
    {
        for (int i = 0; i < Instances.Count; ++i)
        {
            Instance inst = Instances[i];
            if (i == exclude || !EnsureBox(inst) || !inst.IsContainer)
                continue;
            double[] cb = inst.Box!;
            if (objectBox[0] < cb[0] || objectBox[3] > cb[3] || objectBox[1] < cb[1] || objectBox[4] > cb[4] || objectBox[2] < cb[2] || objectBox[5] > cb[5])
                continue;
            if (exclude >= 0 && (Volume(cb) < Volume(objectBox) || (Volume(cb) == Volume(objectBox) && i > exclude)))
                continue;
            return i;
        }
        return -1;
    }

    // Whether the camera scope of container interior `i` reaches the object (the render query of the PVS database, 00483e84): a
    // camera in a solid leaf scopes nothing; otherwise the objects in the leaves of the camera's PVS (leaf 43 and up), in solid
    // leaves, and when the camera leaf sees outside the objects outside the geometry box.
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
            int camLeaf = leaves.LeafAt(lc);
            inst.ScopeNothing = leaves.IsSolid(camLeaf);
            inst.ScopeOutside = leaves.SeesOutside(camLeaf);
            inst.ScopeLeaves = inst.ScopeNothing ? new HashSet<int>() : new HashSet<int>(leaves.VisibleLeaves(camLeaf));
        }
        if (inst.ScopeNothing)
            return false;
        double ox = (objectBox[0] + objectBox[3]) * 0.5, oy = (objectBox[1] + objectBox[4]) * 0.5, oz = (objectBox[2] + objectBox[5]) * 0.5;
        int leaf = leaves.LeafAt(ToLocal(inst, ox, oy, oz));
        return leaves.IsSolid(leaf) || (leaf >= 43 && inst.ScopeLeaves.Contains(leaf)) || (leaf < 43 && inst.ScopeOutside);
    }

    // The container of a shape for the frame: its index, -1 none, Culled when the container's scope misses it.
    public int ContainerOf(double[] objectBox, in Camera3D cam)
    {
        int i = FindContainer(objectBox);
        return i < 0 ? -1 : ScopeReaches(i, objectBox, cam) ? i : Culled;
    }

    private static Vec3 ToLocal(Instance inst, double x, double y, double z)
    {
        float[] m = inst.M;
        double dx = x - inst.X, dy = y - inst.Y, dz = z - inst.Z;
        return new Vec3((float)(dx * m[0] + dy * m[1] + dz * m[2]), (float)(dx * m[3] + dy * m[4] + dz * m[5]), (float)(dx * m[6] + dy * m[7] + dz * m[8]));
    }

    // `contents(i)` runs just before the interior of instance i draws (the stock scope draws a container's contents first:
    // its shapes through `contents`, the interiors filed in its database, then the container itself; InteriorShape_findObjects
    // 00480648 vtable +0x98 runs the database and then the interior's own image).
    public void Draw(in Camera3D camera, TerrainParams haze, Action<int>? contents = null)
    {
        Camera3D cam = camera;
        DrawnSurfaces = DrawnInstances = 0;
        long lightStep = (long)(mClock.Elapsed.TotalSeconds / LightStep);
        LightTime = lightStep * LightStep;
        int n = Instances.Count;
        var parent = new int[n];
        var reached = new bool[n];
        for (int i = 0; i < n; ++i)
        {
            parent[i] = EnsureBox(Instances[i]) ? FindContainer(Instances[i].Box!, i) : -1;
            reached[i] = parent[i] < 0 || ScopeReaches(parent[i], Instances[i].Box!, cam);
        }
        void Tree(int index, int depth)
        {
            contents?.Invoke(index);
            if (depth < 8)
                for (int j = 0; j < n; ++j)
                    if (parent[j] == index && reached[j])
                        Tree(j, depth + 1);
            DrawOne(index);
        }
        for (int i = 0; i < n; ++i)
            if (parent[i] < 0)
                Tree(i, 0);

        void DrawOne(int index)
        {
            Instance inst = Instances[index];
            InteriorModel model = inst.Model;
            // camera in interior space: local = (world - p) * M^T (rotation matrices are orthonormal)
            double dx = cam.X - inst.X, dy = cam.Y - inst.Y, dz = cam.Z - inst.Z;
            float[] m = inst.M;
            double lx = dx * m[0] + dy * m[1] + dz * m[2], ly = dx * m[3] + dy * m[4] + dz * m[5], lz = dx * m[6] + dy * m[7] + dz * m[8];
            InteriorLodModel? lod0 = Lod(model, 0);
            if (lod0 == null)
                return;
            float[] bx = lod0.Geo.Box;
            bool inside = lx >= bx[0] && lx <= bx[3] && ly >= bx[1] && ly <= bx[4] && lz >= bx[2] && lz <= bx[5];
            // distance cut: the box must be within the visible distance
            double ddx = Math.Max(Math.Max(bx[0] - lx, 0), lx - bx[3]), ddy = Math.Max(Math.Max(bx[1] - ly, 0), ly - bx[4]),
                   ddz = Math.Max(Math.Max(bx[2] - lz, 0), lz - bx[5]);
            if (Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) >= haze.Visible)
                return;
            // world centre of the box for the view test and the projected size
            double cxl = lod0.Center[0], cyl = lod0.Center[1], czl = lod0.Center[2];
            double wcx = cxl * m[0] + cyl * m[3] + czl * m[6] + inst.X, wcy = cxl * m[1] + cyl * m[4] + czl * m[7] + inst.Y,
                   wcz = cxl * m[2] + cyl * m[5] + czl * m[8] + inst.Z;
            var (ccx, ccy, ccz) = cam.ToCamera(wcx, wcy, wcz);
            double distC = Math.Sqrt(ccx * ccx + ccy * ccy + ccz * ccz);
            if (!inside && !SphereVisible(cam, ccx, ccy, ccz, lod0.Radius))
                return;
            int pixels = distC > 0.001 ? (int)(2 * cam.ProjectRadius(distC, lod0.Radius)) : int.MaxValue;
            int lodIndex = ModelLod(model, pixels, inside);
            InteriorLodModel? lod = Lod(model, lodIndex);
            if (lod == null || lod.Base == null || lod.Atlas == null)
                return;
            UpdateLights(lod, lightStep);
            // 1.4: one haze amount for the image, from the distance of the object position
            double od = Math.Sqrt((cam.X - wcx) * (cam.X - wcx) + (cam.Y - wcy) * (cam.Y - wcy) + (cam.Z - wcz) * (cam.Z - wcz));
            float hz = (float)TerrainLod.ObjectHaze(od, haze.HazeStart, haze.Visible);
            if (hz <= 0.015f)
                hz = 0; // 1.2: haze source none at 0.015 or less
            string skin = TeamSkin != null && inst.Team >= 0 ? TeamSkin(inst.Team) : "";
            if (skin != inst.Skin)
            {
                inst.Skin = skin;
                inst.Meshes.Clear();
            }
            DrawInstance(inst, lod, cam, (lx, ly, lz), hz);
            ++DrawnInstances;
        }
    }

    internal static bool SphereVisible(in Camera3D cam, double cx, double cy, double cz, double r)
    {
        // camera space: x right, y forward, z up
        if (cy + r < cam.Near)
            return false;
        double ha = cam.HorizontalFov / 2, va = Math.Atan(Math.Tan(ha) * cam.Height / cam.Width);
        if (cx * Math.Cos(ha) + cy * Math.Sin(ha) < -r)
            return false;
        if (-cx * Math.Cos(ha) + cy * Math.Sin(ha) < -r)
            return false;
        if (cz * Math.Cos(va) + cy * Math.Sin(va) < -r)
            return false;
        if (-cz * Math.Cos(va) + cy * Math.Sin(va) < -r)
            return false;
        return true;
    }

    // The world-space triangles of every surface of `lod` for this instance (3.3 steps 7 to 9 minus the per-frame parts).
    private InteriorRenderer.SurfaceMesh[] BuildMeshes(Instance inst, InteriorLodModel lod)
    {
        InteriorGeometryData g = lod.Geo;
        InteriorLighting lt = lod.Base!;
        float[] m = inst.M;
        var meshes = new SurfaceMesh[g.Surfaces.Length];
        int shiftMip = (int)Math.Min(g.HighestMipLevel, 8u);
        for (int s = 0; s < g.Surfaces.Length; ++s)
        {
            InteriorGeometryData.Surface surf = g.Surfaces[s];
            if (surf.VertexCount < 3 || surf.Plane * 4 + 3 >= g.Planes.Length)
                continue;
            // texture region in texels (3.5), light map coordinates in pixels of 2^lightScaleShift texels with one margin pixel
            double sizeX = (surf.TexSizeX + 1) << shiftMip, sizeY = (surf.TexSizeY + 1) << shiftMip;
            double lmDiv = 1 << (int)lt.LightScaleShift;
            int mat = surf.Material == 255 ? -1 : surf.Material;
            string? matName = mat >= 0 && mat < lod.Materials.Count ? lod.Materials[mat] : null;
            if (matName != null && inst.Skin.Length > 0)
                matName = Skins.Resolve(matName, inst.Skin, n => GfxAssets.Read(mAssets.Res, n, out _), false);
            IGfxTexture? tex = MaterialTexture(matName);
            double bw = tex?.Width ?? 1, bh = tex?.Height ?? 1;
            var (ax, ay) = lod.LmPos[s];
            InteriorLighting.Surface lmSurf = lt.Surfaces[s];
            var verts = new Vertex3D[surf.VertexCount];
            for (int k = 0; k < surf.VertexCount; ++k)
            {
                int vi = (int)surf.FirstVertex + k;
                int a = g.VertexPoint3[vi] * 3, b = g.VertexPoint2[vi] * 2;
                float px = g.Point3[a], py = g.Point3[a + 1], pz = g.Point3[a + 2];
                float u = g.Point2[b], v = g.Point2[b + 1];
                double tx = surf.TexOffsetX + u * sizeX, ty = surf.TexOffsetY + v * sizeY;
                // 005d9a84: pixel x = map origin + 0.5 + the surface's own light map offset / 2^shift + u * regionSize / 2^shift
                double lu = (ax + 0.5 + lmSurf.OffsetX / lmDiv + u * sizeX / lmDiv) / lod.AtlasW,
                       lv = (ay + 0.5 + lmSurf.OffsetY / lmDiv + v * sizeY / lmDiv) / lod.AtlasH;
                if (matName == null)
                    (tx, ty, bw, bh) = (0, 0, 1, 1);
                verts[k] = new Vertex3D(px * m[0] + py * m[3] + pz * m[6] + (float)inst.X, px * m[1] + py * m[4] + pz * m[7] + (float)inst.Y,
                                        px * m[2] + py * m[5] + pz * m[8] + (float)inst.Z, (float)(tx / bw), (float)(ty / bh), (float)lu,
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

    // Stock draws the visible surfaces one polygon at a time in surface index order (0055ec98 walks the surface bit set
    // ascending), so equal 16 bit depths resolve by index. Consecutive surfaces of one material share a draw call.
    private void DrawInstance(Instance inst, InteriorLodModel lod, in Camera3D cam, (double x, double y, double z) camLocal, float haze)
    {
        InteriorGeometryData g = lod.Geo;
        bool[]? selected = null;
        if (lod.Leaves != null && lod.Leaves.SurfaceCountTotal == g.Surfaces.Length)
        {
            float[] m = inst.M;
            Camera3D c = cam;
            selected = lod.Leaves.SelectSurfaces(new Vec3((float)camLocal.x, (float)camLocal.y, (float)camLocal.z), (x, y, z, r) =>
            {
                var (cx, cy, cz) = c.ToCamera(x * m[0] + y * m[3] + z * m[6] + inst.X, x * m[1] + y * m[4] + z * m[7] + inst.Y,
                                              x * m[2] + y * m[5] + z * m[8] + inst.Z);
                return SphereVisible(c, cx, cy, cz, r);
            });
        }
        if (!inst.Meshes.TryGetValue(lod, out SurfaceMesh[]? meshes))
            inst.Meshes[lod] = meshes = BuildMeshes(inst, lod);
        List<Vertex3D> list = mBatch;
        list.Clear();
        int curKey = int.MinValue;
        IGfxTexture? curTex = null;
        for (int s = 0; s < meshes.Length; ++s)
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
                int pi = surf.Plane * 4;
                double side = g.Planes[pi] * camLocal.x + g.Planes[pi + 1] * camLocal.y + g.Planes[pi + 2] * camLocal.z + g.Planes[pi + 3];
                if ((side > 0) != surf.PlaneFront)
                    continue;
            }
            if (list.Count > 0 && meshes[s].Key != curKey)
            {
                mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(curTex, lod.Atlas));
                list.Clear();
            }
            curKey = meshes[s].Key;
            curTex = meshes[s].Texture;
            int start = list.Count;
            list.AddRange(tris);
            if (haze != 0)
            {
                Span<Vertex3D> added = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(start);
                for (int k = 0; k < added.Length; ++k)
                    added[k].Haze = haze;
            }
            ++DrawnSurfaces;
        }
        if (list.Count > 0)
            mDev.Draw3D(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new DrawState3D(curTex, lod.Atlas));
        list.Clear();
    }

    public void Dispose()
    {
        foreach (IGfxTexture? t in mTextures.Values)
            t?.Dispose();
        mWhite?.Dispose();
        foreach (InteriorModel? m in mModels.Values)
            if (m != null)
                foreach (InteriorLodModel? l in m.Lods)
                    l?.Atlas?.Dispose();
    }
}
