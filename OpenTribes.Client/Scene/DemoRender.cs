// `tribes --demo FILE.rec --at T,T,... [--camera follow|X,Y,Z,YAW,PITCH|ghost:N|orbit:N,DIST,YAW,PITCH|action[:DIST]] --screenshot OUT`: plays the demo through the
// C3a reader and renders the world, the ghosts as shapes and the effects at the given times (ROADMAP M9 C5).
using System.Globalization;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;

namespace OpenTribes.Client.Scene;

public static class DemoRender
{
    // The environment ghosts as the mission objects the world renderer reads (world-render.md 6.1).
    public static List<MissionObject> EnvironmentObjects(ClientWorld w)
    {
        var list = new List<MissionObject>();
        static string V(object? o) => o is double[] d ? string.Join(" ", d.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) : Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        foreach (ClientGhost g in w.Ghosts.All().OrderBy(g => g.Index))
        {
            var o = new MissionObject { Class = g.Class };
            object? S(string k) => g.State.GetValueOrDefault(k);
            switch (g.Class)
            {
                case "SimVolume":
                case "SimPalette":
                    o.Fields["fileName"] = V(S("file"));
                    break;
                case "SimTerrain":
                    {
                        double[] vh = S("visible/haze/hazeVMin/hazeVMax") as double[] ?? new double[] { 1000, 700, 0, 0 };
                        double[] ps = S("perspectiveDistance/screenSize") as double[] ?? new double[] { 100, 50 };
                        o.Fields["tedFileName"] = V(S("ted"));
                        o.Fields["visibleDistance"] = V(vh[0]);
                        o.Fields["hazeDistance"] = V(vh[1]);
                        o.Fields["screenSize"] = V(ps[1]);
                        o.Fields["position"] = V(S("position"));
                        break;
                    }
                case "Sky":
                    o.Fields["dmlName"] = V(S("dml"));
                    o.Fields["skyColor"] = V(S("vecB8"));
                    o.Fields["size"] = "600";
                    if (S("textures") is List<object?> tex)
                        for (int i = 0; i < tex.Count; ++i)
                            o.Fields[$"textures[{i}]"] = V(tex[i]);
                    break;
                case "StarField":
                    if (S("data7c") is double[] c && c.Length >= 9)
                        for (int i = 0; i < 3; ++i)
                            o.Fields[$"colors[{i}]"] = V(new[] { c[i * 3], c[i * 3 + 1], c[i * 3 + 2] });
                    o.Fields["inFrontOfSky"] = V(S("flagAE")) == "1" ? "true" : "false";
                    break;
                case "Planet":
                    o.Fields["fileName"] = V(S("file"));
                    o.Fields["azimuth"] = V(S("f6c"));
                    o.Fields["incidence"] = V(S("f70"));
                    o.Fields["intensity"] = V(S("v78"));
                    o.Fields["ambient"] = V(S("v84"));
                    o.Fields["size"] = V(S("f128"));
                    o.Fields["distance"] = V(S("f12c"));
                    break;
                case "InteriorShape":
                    o.Fields["fileName"] = V(S("file"));
                    o.Fields["position"] = V(S("pos"));
                    o.Fields["rotation"] = V(S("rot"));
                    break;
                default:
                    continue;
            }
            list.Add(o);
        }
        return list;
    }

    public readonly record struct CameraPose(double X, double Y, double Z, double YawDeg, double PitchDeg, bool FirstPerson, int ControlGhost);

    // The camera of the control object (client-net.md 4.5, game-stream.md 6.6): an ObserverCamera in mode 0 to 2 stands at its
    // position, in mode 3 it orbits the centre of the orbit object at the stored distance (-1: the object's own view), a
    // Player is seen through its eye.
    public static CameraPose? ControlCamera(SceneModel m, ClientWorld w)
    {
        ClientPsc psc = w.Psc;
        if (psc.ControlGhost < 0)
            return null;
        var st = psc.ControlState;
        double D(string k, double def = 0) => st.TryGetValue(k, out object? v) ? Db.ToDouble(v, def) : def;
        double[] Vv(string k) => st.TryGetValue(k, out object? v) ? Db.Vec(v) : new double[3];
        if (psc.ControlClass == "ObserverCamera")
        {
            double[] rot = Vv("rot");
            double pitch = rot[0], yaw = rot[2];
            int mode = (int)D("camMode");
            if (mode < 3 || mode == 4)
            {
                double[] p = Vv("pos");
                if (mode == 4)
                {
                    double[] orbit = Vv("orbit");
                    return Orbit(p[0], p[1], p[2], yaw, pitch, orbit[2]);
                }
                return new CameraPose(p[0], p[1], p[2], yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, psc.ControlGhost);
            }
            int og = (int)D("orbitGhost", -1);
            double[] ob = Vv("orbit");
            if (og >= 0 && m.Visuals.TryGetValue(og, out Visual? tv) && tv.Position is var (x, y, z))
            {
                if (ob[2] < 0)
                {
                    if (tv is PlayerVisual)
                        return new CameraPose(x, y, z + 1.8, yaw * 180 / Math.PI, pitch * 180 / Math.PI, true, og);
                    return new CameraPose(x, y, z + 1.8, yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, og);
                }
                return Orbit(x, y, z + 1.0, yaw, pitch, ob[2]);
            }
            return null;
        }
        if (psc.ControlClass == "Player")
        {
            double[] p = Vv("pos");
            return new CameraPose(p[0], p[1], p[2] + 1.8, D("rotZ") * 180 / Math.PI, D("pitch") * 180 / Math.PI, true, psc.ControlGhost);
        }
        return null;
    }

    // The transform moved back along the view direction by `distance`.
    public static CameraPose Orbit(double cx, double cy, double cz, double yaw, double pitch, double distance)
    {
        double fx = -Math.Sin(yaw) * Math.Cos(pitch), fy = Math.Cos(yaw) * Math.Cos(pitch), fz = Math.Sin(pitch);
        return new CameraPose(cx - fx * distance, cy - fy * distance, cz - fz * distance, yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, -1);
    }

    // `--camera action[:DIST]`: orbits the newest explosion (else a projectile, else a player), pulled towards the nearest
    // player within 25 m, from the side with a clear line over the terrain.
    public static CameraPose? ActionCamera(SceneModel m, OpenTribes.World.Terrain? terrain, double dist)
    {
        (double x, double y, double z)? pivot = null;
        foreach (Effects.ExplosionEffect e in m.Effects.OfType<Effects.ExplosionEffect>().OrderBy(e => e.AgeMs))
        {
            pivot = e.Origin;
            break;
        }
        pivot ??= m.Visuals.Values.OfType<ProjectileVisual>().Select(v => v.Position).FirstOrDefault(p => p != null);
        pivot ??= m.Visuals.Values.OfType<PlayerVisual>().Select(v => v.Position).FirstOrDefault(p => p != null);
        if (pivot is not var (px, py, pz))
            return null;
        double bestD = 25 * 25;
        foreach (PlayerVisual pv in m.Visuals.Values.OfType<PlayerVisual>())
            if (pv.Position is var (qx, qy, qz) && (qx - px) * (qx - px) + (qy - py) * (qy - py) + (qz - pz) * (qz - pz) < bestD)
            {
                bestD = (qx - px) * (qx - px) + (qy - py) * (qy - py) + (qz - pz) * (qz - pz);
                (px, py, pz) = ((px + qx) / 2, (py + qy) / 2, (pz + qz) / 2 + 0.5);
            }
        CameraPose best = default;
        double bestClear = -1e9;
        for (int k = 0; k < 12; ++k)
        {
            double a = k * Math.PI / 6;
            double cx = px + Math.Sin(a) * dist, cy = py - Math.Cos(a) * dist, cz = pz + dist * 0.6;
            double clear = 1e9;
            for (int i = 0; i <= 10; ++i)
            {
                double f = i / 10.0;
                float h = 0;
                double sx = cx + (px - cx) * f, sy = cy + (py - cy) * f, sz = cz + (pz - cz) * f;
                if (terrain != null && terrain.Height((float)sx, (float)sy, ref h))
                    clear = Math.Min(clear, i == 0 ? sz - h - 4 : sz - h);   // the camera itself stays 4 above the ground
            }
            if (clear > bestClear)
            {
                bestClear = clear;
                double dx = px - cx, dy = py - cy, dz = pz - cz;
                double yaw = Math.Atan2(-dx, dy) * 180 / Math.PI, pitch = Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI;
                best = new CameraPose(cx, cy, cz, yaw, pitch, false, -1);
            }
        }
        return best;
    }

    public static CameraPose? GhostCamera(SceneModel m, int ghost)
    {
        if (!m.Visuals.TryGetValue(ghost, out Visual? v) || v.Position is not var (x, y, z) || v is not PlayerVisual p)
            return null;
        return new CameraPose(x, y, z + 1.8, p.YawAngle * 180 / Math.PI, p.ViewPitch * 180 / Math.PI, true, ghost);
    }

    public static int Run(string root, string demoPath, string times, string? camera, double fov, string? output, int width, int height, string? videoDriver, double? atOnly = null, bool hud = false)
    {
        if (output == null)
        {
            Console.Error.WriteLine("tribes: --demo needs --screenshot");
            return 2;
        }
        var ts = new List<double>();
        foreach (string part in times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || t < 0)
            {
                Console.Error.WriteLine("tribes: --at expects seconds, comma separated");
                return 2;
            }
            ts.Add(t);
        }
        ts.Sort();
        if (ts.Count == 0)
        {
            Console.Error.WriteLine("tribes: --at needs at least one time");
            return 2;
        }
        double[]? fixedCam = null;
        double[]? orbitCam = null;      // ghost, distance, yaw, pitch
        double actionDist = 0;
        int ghostCam = -1;
        if (camera != null && camera != "follow")
        {
            if (camera.StartsWith("ghost:", StringComparison.Ordinal) && int.TryParse(camera[6..], out ghostCam)) { }
            else if (camera.StartsWith("action", StringComparison.Ordinal))
                actionDist = camera.Length > 7 ? double.Parse(camera[7..], CultureInfo.InvariantCulture) : 14;
            else if (camera.StartsWith("orbit:", StringComparison.Ordinal))
                orbitCam = camera[6..].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            else
            {
                fixedCam = camera.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (fixedCam.Length != 5)
                {
                    Console.Error.WriteLine("tribes: --camera expects follow, ghost:N or X,Y,Z,YAW,PITCH");
                    return 2;
                }
            }
        }
        byte[] data = File.ReadAllBytes(demoPath);
        // Pass 1: the environment ghosts (volumes, palette, terrain, sky, interiors) of the always phase.
        var pre = new DemoPlayback(data);
        pre.StepUntilEnvironment();
        List<MissionObject> env = EnvironmentObjects(pre.World);
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out string error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        win.SetSwapInterval(0);
        ResourceManager res = GfxAssets.Mount(root, out _, "config;base;base\\missions");
        foreach (MissionObject o in env.Where(o => o.Class == "SimVolume"))
            res.AddVolume(o.Get("fileName"));
        string name = Path.GetFileNameWithoutExtension(demoPath);
        using WorldScene scene = WorldScene.LoadFrom(dev, res, name, env, out string loadError);
        if (loadError.Length > 0)
            Console.Error.WriteLine("tribes: " + loadError);
        dev.SetPalette(scene.Assets.Palette);
        using var assets = new ShapeAssets(dev, scene.Assets);
        var renderer = new ShapeRenderer(assets);
        var play = new DemoPlayback(data);
        Gui.GuiSession? ui = null;
        if (hud)
        {
            ui = Gui.GuiSession.Create(root, dev.Width, dev.Height);
            ui.Dev = dev;
            ui.Host.Hud = new OpenTribes.Gui.ClientHudSource(play.World, () => ui.Runtime.ClockSeconds);
            ui.Host.OnPalette = null;
            ui.Interp.Evaluate("setHudTimer(0);");
            ui.Canvas.CursorOn = false;
            ui.Canvas.LoadContent("gui\\play.gui");
        }
        var model = new SceneModel(play.World, assets, renderer, scene.TerrainModel);
        model.BaseLight.SetSun(scene.SunAzimuth, scene.SunIncidence);
        model.BaseLight.AmbR = (float)scene.SunAmbient[0]; model.BaseLight.AmbG = (float)scene.SunAmbient[1]; model.BaseLight.AmbB = (float)scene.SunAmbient[2];
        model.BaseLight.SunR = (float)scene.SunIntensity[0]; model.BaseLight.SunG = (float)scene.SunIntensity[1]; model.BaseLight.SunB = (float)scene.SunIntensity[2];
        play.OnPacket = model.OnPacket;
        play.OnFrame = model.Tick;
        Console.Error.WriteLine($"{name}: {env.Count} environment objects, terrain visible {scene.Terrain.Visible}, client {play.Demo.ClientId}, {play.Demo.Packets} packets");
        bool dir = ts.Count > 1 || !Path.HasExtension(output);
        if (dir)
            Directory.CreateDirectory(output);
        foreach (double t in ts)
        {
            play.StepTo(t * 1000);
            CameraPose? pose = actionDist > 0 ? ActionCamera(model, scene.TerrainModel, actionDist)
                : orbitCam != null && model.Visuals.TryGetValue((int)orbitCam[0], out Visual? ov) && ov.Position is var (ox, oy, oz)
                    ? Orbit(ox, oy, oz + 1.2, orbitCam[2] * Math.PI / 180, orbitCam[3] * Math.PI / 180, orbitCam[1])
                : fixedCam != null ? new CameraPose(fixedCam[0], fixedCam[1], fixedCam[2], fixedCam[3], fixedCam[4], false, -1)
                : ghostCam >= 0 ? GhostCamera(model, ghostCam) : ControlCamera(model, play.World);
            if (pose == null)
            {
                Console.Error.WriteLine($"  t={t}: no camera (the control object has no position yet)");
                continue;
            }
            CameraPose c = pose.Value;
            var ctx = new FrameContext { HazeStart = scene.Terrain.HazeStart, Visible = scene.Terrain.Visible, FirstPersonOfControl = c.FirstPerson, ControlGhost = c.ControlGhost };
            int images = 0, draws = 0;
            void DrawShapes(Camera3D cam)
            {
                ctx.Camera = cam;
                model.Build(ctx);
                images = ctx.Lists.ImageCount;
                using (FramePerf.Scope("shapes-draw"))
                    draws = ctx.Lists.Draw(dev);
            }
            // OT_BENCH=N: N extra frames of this view with the frame timing log on (one report line), for before/after runs.
            if (int.TryParse(Environment.GetEnvironmentVariable("OT_BENCH"), out int bench) && bench > 0)
            {
                FramePerf.Enable(bench);
                FramePerf.Device = dev;
                for (int i = 0; i <= bench; ++i)
                {
                    FramePerf.FrameStart();
                    scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, DrawShapes, null);
                }
            }
            scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, DrawShapes, ui == null ? null : () =>
            {
                ui.Clock = (long)(t * 1000);
                if (Environment.GetEnvironmentVariable("OT_HUD_DEBUG") != null)
                    for (int i = 0; i < play.World.Psc.ItemCounts.Length; ++i)
                        if (play.World.Psc.ItemCounts[i] > 0 && play.World.DataBlocks.Blocks.TryGetValue((5, i), out var blk))
                            Console.Error.WriteLine($"item {i} x{play.World.Psc.ItemCounts[i]} {blk.Class}: " + string.Join(" ", blk.Fields.Take(14).Select(f => f.Name + "=" + f.Value)));
                ui.Canvas.Frame();
                ui.Canvas.Render(dev);
            });
            byte[] px = dev.ReadPixels();
            string path = dir ? Path.Combine(output, $"{name}_{t.ToString("0.###", CultureInfo.InvariantCulture)}.png") : output;
            MissionRender.WritePng(path, px, dev.Width, dev.Height);
            if (Environment.GetEnvironmentVariable("OT_DEMO_VERBOSE") != null)
                foreach (Visual v in model.Visuals.Values.Where(v => v.Position != null).OrderBy(v => { var q = v.Position!.Value; return (q.x - c.X) * (q.x - c.X) + (q.y - c.Y) * (q.y - c.Y) + (q.z - c.Z) * (q.z - c.Z); }).Take(12))
                    {
                    float gh = float.NaN;
                    scene.TerrainModel?.Height((float)v.Position!.Value.x, (float)v.Position!.Value.y, ref gh);
                    Console.Error.WriteLine($"    {v.Index} {v.Class} {v.Position!.Value.x:F1},{v.Position!.Value.y:F1},{v.Position!.Value.z:F1} ground {gh:F1}" + (v is PlayerVisual pvv ? $" anim {pvv.AnimIndex} skin {pvv.SkinName} shape {pvv.ArmorShape}" : ""));
                }
            Console.Error.WriteLine($"  t={t}: camera {c.X:F1},{c.Y:F1},{c.Z:F1} yaw {c.YawDeg:F0} pitch {c.PitchDeg:F0}; visuals {model.Visuals.Count} effects {model.Effects.Count} puffs {model.Smoke.Live} decals {model.Decals.Count}; images {images} draws {draws} -> {path}");
        }
        Console.Error.WriteLine($"  explosions {model.ExplosionsSpawned}, footprints {model.FootprintsAdded} (triggers {model.TriggerCount}, on ground {model.ContactTriggers}), bullet holes {model.BulletHolesAdded}, missing files: {string.Join(" ", assets.Missing.Take(12))}");
        return 0;
    }
}
