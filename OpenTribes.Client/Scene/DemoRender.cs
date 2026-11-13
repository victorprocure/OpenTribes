namespace OpenTribes.Client.Scene;

using System.Globalization;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;

/// <summary>The demo renderer: plays a demo through the reader and renders the world, the ghosts as shapes and the effects at the given times.</summary>
/// <remarks>Started with <c>tribes --demo FILE.rec --at T,T,... [--camera follow|X,Y,Z,YAW,PITCH|ghost:N|orbit:N,DIST,YAW,PITCH|action[:DIST]] --screenshot OUT</c>.</remarks>
public static class DemoRender
{
    /// <summary>Stores the Sky ghost update as the persistent fields the sky renderer reads.</summary>
    /// <param name="o">The mission object that gets the fields.</param>
    /// <param name="state">The ghost state.</param>
    /// <remarks>Sky_unpackUpdate (00477824) reads the material list name, the sky colour (+0xb8), the haze colour (+0xc4), the feature position (+0x11c), the size (+0x124) and the 16 panel indices (+0xdc). The persistent field table of Sky_initPersistFields (004776fc) names +0x11c featurePosition and +0x124 size. Sky_initTextures (00478548) and the band draw (00477b48) read both from the object, so the client uses the values the server sent.</remarks>
    public static void SkyFields(MissionObject o, IReadOnlyDictionary<string, object?> state)
    {
        static string V(object? o) => o is double[] d ? string.Join(" ", d.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) : Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        object? S(string k) => state.GetValueOrDefault(k);
        o.Fields["dmlName"] = V(S("dml"));
        o.Fields["skyColor"] = V(S("vecB8"));
        o.Fields["hazeColor"] = V(S("vecC4"));
        o.Fields["featurePosition"] = V(S("f11c"));
        o.Fields["size"] = V(S("f124"));
        if (S("textures") is List<object?> tex)
            for (var i = 0; i < tex.Count; ++i)
                o.Fields[$"textures[{i}]"] = V(tex[i]);
    }

    /// <summary>Builds the environment ghosts as the mission objects the world renderer reads (world-render.md 6.1).</summary>
    /// <param name="w">The client world.</param>
    /// <returns>The mission objects.</returns>
    public static List<MissionObject> EnvironmentObjects(ClientWorld w)
    {
        var list = new List<MissionObject>();
        static string V(object? o) => o is double[] d ? string.Join(" ", d.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) : Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        foreach (var g in w.Ghosts.All().OrderBy(g => g.Index))
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
                    var vh = S("visible/haze/hazeVMin/hazeVMax") as double[] ?? [1000, 700, 0, 0];
                    var ps = S("perspectiveDistance/screenSize") as double[] ?? [100, 50];
                    o.Fields["tedFileName"] = V(S("ted"));
                    o.Fields["visibleDistance"] = V(vh[0]);
                    o.Fields["hazeDistance"] = V(vh[1]);
                    o.Fields["screenSize"] = V(ps[1]);
                    o.Fields["position"] = V(S("position"));
                    break;
                }
                case "Sky":
                    SkyFields(o, g.State);
                    break;
                case "StarField":
                    if (S("data7c") is double[] c && c.Length >= 9)
                        for (var i = 0; i < 3; ++i)
                            o.Fields[$"colors[{i}]"] = V(new[] { c[i * 3], c[(i * 3) + 1], c[(i * 3) + 2] });
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
                    if (S("team") != null)
                        o.Fields["team"] = V(S("team"));
                    break;
                default:
                    continue;
            }
            list.Add(o);
        }
        return list;
    }

    /// <summary>Gives the near plane of a demo render.</summary>
    /// <param name="c">The camera pose.</param>
    /// <param name="w">The client world.</param>
    /// <returns>The near distance.</returns>
    /// <remarks>It is the same PSC camera query answer as live play (<c>WorldScene.ViewNear</c>, 004077a8). The demo runs the same TSControl camera query (00546dc4) to the control object. A pose that is not the eye of the control object (a free, ghost or orbit camera of this tool) is not first person and keeps 0.01.</remarks>
    public static double NearFor(CameraPose c, ClientWorld w) => WorldScene.ViewNear(c.FirstPerson, w.Psc.ControlClass == "ObserverCamera");

    /// <summary>A camera position and orientation.</summary>
    /// <param name="X">The x position.</param>
    /// <param name="Y">The y position.</param>
    /// <param name="Z">The z position.</param>
    /// <param name="YawDeg">The yaw in degrees.</param>
    /// <param name="PitchDeg">The pitch in degrees.</param>
    /// <param name="FirstPerson">True for the eye of the control object.</param>
    /// <param name="ControlGhost">The ghost index of the control object.</param>
    /// <param name="RollDeg">The roll in degrees.</param>
    public readonly record struct CameraPose(double X, double Y, double Z, double YawDeg, double PitchDeg, bool FirstPerson, int ControlGhost, double RollDeg = 0);

    /// <summary>Builds the camera of the control object.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="w">The client world.</param>
    /// <returns>The pose, or null when there is none.</returns>
    /// <remarks>client-net.md 4.5 and game-stream.md 6.6: an ObserverCamera in mode 0 to 2 stands at its position. In mode 3 it orbits the centre of the orbit object at the stored distance (-1: the own view of the object). A Player is seen through its eye.</remarks>
    public static CameraPose? ControlCamera(SceneModel m, ClientWorld w)
    {
        var psc = w.Psc;
        if (psc.ControlGhost < 0)
            return null;
        var st = psc.ControlState;
        double D(string k, double def = 0) => st.TryGetValue(k, out var v) ? Db.ToDouble(v, def) : def;
        double[] Vv(string k) => st.TryGetValue(k, out var v) ? Db.Vec(v) : new double[3];
        if (psc.ControlClass == "ObserverCamera")
        {
            var rot = Vv("rot");
            double pitch = rot[0], yaw = rot[2];
            var mode = (int)D("camMode");
            if (mode is < 3 or 4)
            {
                var p = Vv("pos");
                if (mode == 4)
                {
                    var orbit = Vv("orbit");
                    return Orbit(p[0], p[1], p[2], yaw, pitch, orbit[2]);
                }
                return new CameraPose(p[0], p[1], p[2], yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, psc.ControlGhost);
            }
            var og = (int)D("orbitGhost", -1);
            var ob = Vv("orbit");
            if (og >= 0 && m.Visuals.TryGetValue(og, out var tv) && tv.Position is var (x, y, z))
            {
                if (ob[2] < 0)
                {
                    if (tv is PlayerVisual)
                    {
                        var (ox, oy, oz) = EyeOf(m, og, x, y, z, yaw, pitch);
                        return new CameraPose(ox, oy, oz, yaw * 180 / Math.PI, pitch * 180 / Math.PI, true, og);
                    }
                    return new CameraPose(x, y, z + 1.8, yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, og);
                }
                return Orbit(x, y, z + 1.0, yaw, pitch, ob[2]);
            }
            return null;
        }
        if (psc.ControlClass == "Player")
        {
            var p = Vv("pos");
            var (px, py, pz) = EyeOf(m, psc.ControlGhost, p[0], p[1], p[2], D("rotZ"), D("pitch"));
            return new CameraPose(px, py, pz, D("rotZ") * 180 / Math.PI, D("pitch") * 180 / Math.PI, true, psc.ControlGhost);
        }
        if (psc.ControlClass is "Vehicle" or "Flier")
            return VehicleCamera(m, psc.ControlGhost, 0f, null, null);
        if (psc.ControlClass == "Turret")
            return TurretCamera(m, psc.ControlGhost, (float)D("aimRot"), (float)D("aimElev"), 0f, null, null);
        return null;
    }

    /// <summary>Builds the camera of a controlled turret from its ghost.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The turret ghost index.</param>
    /// <param name="rotation">The turret rotation.</param>
    /// <param name="elevation">The turret elevation.</param>
    /// <param name="distance">The view distance, 0 for first person.</param>
    /// <param name="world">The container for the clear line test, or null.</param>
    /// <param name="exclude">The object that the line ignores.</param>
    /// <returns>The pose, or null when the ghost is not a shape.</returns>
    public static CameraPose? TurretCamera(SceneModel m, int ghost, float rotation, float elevation, float distance, OpenTribes.World.Container? world, object? exclude)
        => m.Visuals.TryGetValue(ghost, out var v) && v is ShapeVisual sv ? TurretCamera(sv, ghost, rotation, elevation, distance, world, exclude) : null;

    /// <summary>Builds the camera of a controlled turret from its visual.</summary>
    /// <param name="sv">The turret visual.</param>
    /// <param name="ghost">The turret ghost index.</param>
    /// <param name="rotation">The turret rotation.</param>
    /// <param name="elevation">The turret elevation.</param>
    /// <param name="distance">The view distance, 0 for first person.</param>
    /// <param name="world">The container for the clear line test, or null.</param>
    /// <param name="exclude">The object that the line ignores.</param>
    /// <returns>The pose, or null while the turret has no shape instance.</returns>
    /// <remarks>Follows Turret_getCameraTransform (004564fc, vtable +0x170): the <c>dummy eye</c> node position of the animated shape with the Euler angles (elevation, 0, rotation), times the object transform, moved back by 1.3 * distance. Without the node it is the object transform. The function does nothing while the turret has no shape instance (+0x328 is 0) and does not write the output transform. The ghost can exist before its datablock and shape are loaded, and the object transform alone (the body of the turret) is not a camera, so there is no pose until the instance exists.</remarks>
    public static CameraPose? TurretCamera(ShapeVisual sv, int ghost, float rotation, float elevation, float distance, OpenTribes.World.Container? world, object? exclude)
    {
        if (!sv.TryCameraInputs(out var obj, out var eye) || !sv.HasShapeInstance)
            return null;
        var t = Game.TurretCamera.Compute(obj, eye, rotation, elevation, distance, world, exclude);
        return PoseOf(t, distance == 0f || eye == null, ghost);
    }

    /// <summary>Builds the eye camera of a player on a mount.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The player ghost index.</param>
    /// <param name="body">The mount transform.</param>
    /// <param name="pos">The player position.</param>
    /// <param name="pitchRad">The pitch in radians.</param>
    /// <returns>The pose, or null without the eye node.</returns>
    /// <remarks>Follows Player_getEyeTransform (0041248c) for a Player on a mount: the object transform (the mount point times the yaw) times the <c>dummy eye</c> node turned by the pitch about x. The view direction is the second row, up the third.</remarks>
    public static CameraPose? RiderCamera(SceneModel m, int ghost, in Sim.Transform body, float[] pos, double pitchRad)
    {
        if (!m.Visuals.TryGetValue(ghost, out var v) || v is not PlayerVisual pv || !pv.TryEyeLocal(pitchRad, out var ex, out var ey, out var ez))
            return null;
        var t = new Sim.Transform();
        float cp = (float)Math.Cos(pitchRad), sp = (float)Math.Sin(pitchRad);
        float[] r0 = [body.M[0], body.M[1], body.M[2]], r1 = [body.M[3], body.M[4], body.M[5]], r2 = [body.M[6], body.M[7], body.M[8]];
        for (var i = 0; i < 3; ++i)
        {
            t.M[i] = r0[i];
            t.M[3 + i] = (cp * r1[i]) + (sp * r2[i]);
            t.M[6 + i] = (-sp * r1[i]) + (cp * r2[i]);
            t.P[i] = pos[i] + (float)((ex * r0[i]) + (ey * r1[i]) + (ez * r2[i]));
        }
        t.Flags = body.Flags;
        return PoseOf(t, true, ghost);
    }

    /// <summary>Builds the camera of a piloted Vehicle or Flier.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The vehicle ghost index.</param>
    /// <param name="distance">The PSC view distance timer (+0x9c), 0 for first person.</param>
    /// <param name="world">The container for the clear line test, or null.</param>
    /// <param name="exclude">The object that the line ignores.</param>
    /// <returns>The pose, or null without the eye node.</returns>
    /// <remarks>Follows Vehicle_getCameraTransform (00445d88, docs/client-net.md 4.2): the <c>dummy eye</c> node transform times the drawn object transform, moved back by 2 * distance (placeBehind through <paramref name="world"/>, the object itself ignored), pitch * 0.8 and roll * 0.5 while the distance is not 0. First person (distance 0) is the eye node itself, the view mode only changes the distance.</remarks>
    public static CameraPose? VehicleCamera(SceneModel m, int ghost, float distance, OpenTribes.World.Container? world, object? exclude)
    {
        if (!m.Visuals.TryGetValue(ghost, out var v) || v is not ShapeVisual sv || !sv.TryCameraInputs(out var obj, out var eye))
            return null;
        var t = Game.VehicleCamera.Compute(obj, eye, distance, world, exclude);
        return PoseOf(t, distance == 0f, ghost);
    }

    /// <summary>Gets yaw, pitch and roll of a camera transform (the Euler words of Mat3_toEuler, in degrees).</summary>
    /// <param name="t">The camera transform.</param>
    /// <param name="firstPerson">True for a first person view.</param>
    /// <param name="ghost">The control ghost index.</param>
    /// <returns>The pose.</returns>
    public static CameraPose PoseOf(in Sim.Transform t, bool firstPerson, int ghost)
    {
        var e = new float[3];
        Sim.MissionTransform.MatrixToEuler(t, e);
        var d = 180 / Math.PI;
        return new CameraPose(t.P[0], t.P[1], t.P[2], e[2] * d, e[0] * d, firstPerson, ghost, e[1] * d);
    }

    /// <summary>Computes the camera position of a first person view: the body position plus the <c>dummy eye</c> node of the player shape, turned by the yaw, with the looks thread at the camera pitch. Without the node it is 1.8 above the body.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The player ghost index.</param>
    /// <param name="x">The body x.</param>
    /// <param name="y">The body y.</param>
    /// <param name="z">The body z.</param>
    /// <param name="yawRad">The yaw in radians.</param>
    /// <param name="pitchRad">The pitch in radians.</param>
    /// <returns>The eye position.</returns>
    /// <remarks>Follows Player_getEyeTransform.</remarks>
    public static (double x, double y, double z) EyeOf(SceneModel m, int ghost, double x, double y, double z, double yawRad, double pitchRad)
    {
        if (m.Visuals.TryGetValue(ghost, out var v) && v is PlayerVisual pv && pv.TryEyeLocal(pitchRad, out var ex, out var ey, out var ez))
        {
            double c = Math.Cos(yawRad), s = Math.Sin(yawRad);
            return (x + (ex * c) - (ey * s), y + (ex * s) + (ey * c), z + ez);
        }
        return (x, y, z + 1.8);
    }

    /// <summary>Moves a view back along its direction.</summary>
    /// <param name="cx">The x position of the view.</param>
    /// <param name="cy">The y position of the view.</param>
    /// <param name="cz">The z position of the view.</param>
    /// <param name="yaw">The yaw in degrees.</param>
    /// <param name="pitch">The pitch in degrees.</param>
    /// <param name="distance">The distance to move back.</param>
    /// <returns>The pose.</returns>
    public static CameraPose Orbit(double cx, double cy, double cz, double yaw, double pitch, double distance)
    {
        double fx = -Math.Sin(yaw) * Math.Cos(pitch), fy = Math.Cos(yaw) * Math.Cos(pitch), fz = Math.Sin(pitch);
        return new CameraPose(cx - (fx * distance), cy - (fy * distance), cz - (fz * distance), yaw * 180 / Math.PI, pitch * 180 / Math.PI, false, -1);
    }

    /// <summary>Builds the third person camera of the control Player.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The player ghost index.</param>
    /// <param name="x">The body x.</param>
    /// <param name="y">The body y.</param>
    /// <param name="z">The body z.</param>
    /// <param name="yawRad">The yaw in radians.</param>
    /// <param name="pitchRad">The pitch in radians.</param>
    /// <param name="distance">The view distance, 0.01 or more.</param>
    /// <param name="world">The container for the clear line test. Null keeps the full distance.</param>
    /// <param name="exclude">The object that the line ignores.</param>
    /// <returns>The pose.</returns>
    /// <remarks>Follows Player_getCameraTransform (00412324) with a view distance of 0.01 or more: the object transform (yaw, body position) times the pitch rotation at (0, 0, z of the <c>dummyalways chasecam</c> node), moved back along the view direction by GameBase_placeBehind (00449604: ray of 2.5 * distance, mask 0x20c00006, margin 0.025). Without the node the eye position is the start.</remarks>
    public static CameraPose ChaseCamera(SceneModel m, int ghost, double x, double y, double z, double yawRad, double pitchRad, float distance,
                                         OpenTribes.World.Container? world, object? exclude)
    {
        double px = x, py = y, pz = z;
        if (m.Visuals.TryGetValue(ghost, out var v) && v is PlayerVisual pv && pv.TryChaseZ(out var cz))
            pz = z + cz;
        else
            (px, py, pz) = EyeOf(m, ghost, x, y, z, yawRad, pitchRad);
        var t = new Sim.Transform();
        Sim.MissionTransform.EulerToMatrix([(float)pitchRad, 0f, (float)yawRad], ref t);
        var p = new float[] { (float)px, (float)py, (float)pz };
        ObserverCamera.PlaceBehind(world, exclude, p, t.M[3], t.M[4], t.M[5], distance);
        return new CameraPose(p[0], p[1], p[2], yawRad * 180 / Math.PI, pitchRad * 180 / Math.PI, false, -1);
    }

    /// <summary>Builds the camera of <c>--camera action[:DIST]</c>: it orbits the newest explosion (else a projectile, else a player), pulled towards the nearest player within 25 m, from the side with a clear line over the terrain.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="terrain">The terrain for the clear line test, or null.</param>
    /// <param name="dist">The orbit distance.</param>
    /// <returns>The pose, or null when nothing is happening.</returns>
    public static CameraPose? ActionCamera(SceneModel m, OpenTribes.World.Terrain? terrain, double dist)
    {
        (double x, double y, double z)? pivot = null;
        foreach (var e in m.Effects.OfType<Effects.ExplosionEffect>().OrderBy(e => e.AgeMs))
        {
            pivot = e.Origin;
            break;
        }
        pivot ??= m.Visuals.Values.OfType<ProjectileVisual>().Select(v => v.Position).FirstOrDefault(p => p != null);
        pivot ??= m.Visuals.Values.OfType<PlayerVisual>().Select(v => v.Position).FirstOrDefault(p => p != null);
        if (pivot is not var (px, py, pz))
            return null;
        double bestD = 25 * 25;
        foreach (var pv in m.Visuals.Values.OfType<PlayerVisual>())
            if (pv.Position is var (qx, qy, qz) && ((qx - px) * (qx - px)) + ((qy - py) * (qy - py)) + ((qz - pz) * (qz - pz)) < bestD)
            {
                bestD = ((qx - px) * (qx - px)) + ((qy - py) * (qy - py)) + ((qz - pz) * (qz - pz));
                (px, py, pz) = ((px + qx) / 2, (py + qy) / 2, ((pz + qz) / 2) + 0.5);
            }
        CameraPose best = default;
        var bestClear = -1e9;
        for (var k = 0; k < 12; ++k)
        {
            var a = k * Math.PI / 6;
            double cx = px + (Math.Sin(a) * dist), cy = py - (Math.Cos(a) * dist), cz = pz + (dist * 0.6);
            var clear = 1e9;
            for (var i = 0; i <= 10; ++i)
            {
                var f = i / 10.0;
                float h = 0;
                double sx = cx + ((px - cx) * f), sy = cy + ((py - cy) * f), sz = cz + ((pz - cz) * f);
                if (terrain != null && terrain.Height((float)sx, (float)sy, ref h))
                    clear = Math.Min(clear, i == 0 ? sz - h - 4 : sz - h);   // the camera itself stays 4 above the ground
            }
            if (clear > bestClear)
            {
                bestClear = clear;
                double dx = px - cx, dy = py - cy, dz = pz - cz;
                double yaw = Math.Atan2(-dx, dy) * 180 / Math.PI, pitch = Math.Atan2(dz, Math.Sqrt((dx * dx) + (dy * dy))) * 180 / Math.PI;
                best = new CameraPose(cx, cy, cz, yaw, pitch, false, -1);
            }
        }
        return best;
    }

    /// <summary>Builds the camera that follows a player ghost.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="ghost">The ghost index.</param>
    /// <returns>The pose, or null when the ghost is not a player.</returns>
    public static CameraPose? GhostCamera(SceneModel m, int ghost)
    {
        if (!m.Visuals.TryGetValue(ghost, out var v) || v.Position is not var (x, y, z) || v is not PlayerVisual p)
            return null;
        return new CameraPose(x, y, z + 1.8, p.YawAngle * 180 / Math.PI, p.ViewPitch * 180 / Math.PI, true, ghost);
    }

    /// <summary>Plays a demo and renders it at the given times.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="demoPath">The demo file.</param>
    /// <param name="times">The times in seconds, separated by commas.</param>
    /// <param name="camera">The camera mode, or null for the default.</param>
    /// <param name="fov">The field of view.</param>
    /// <param name="output">The PNG path. Required.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="videoDriver">The video driver name, or null for the default.</param>
    /// <param name="atOnly">Renders only this time of the list, or null for all.</param>
    /// <param name="hud">True to draw the HUD.</param>
    /// <returns>The process exit code: 2 without an output path.</returns>
    public static int Run(string root, string demoPath, string times, string? camera, double fov, string? output, int width, int height, string? videoDriver, double? atOnly = null, bool hud = false)
    {
        if (output == null)
        {
            Console.Error.WriteLine("tribes: --demo needs --screenshot");
            return 2;
        }
        var ts = new List<double>();
        foreach (var part in times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || t < 0)
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
        var ghostCam = -1;
        if (camera is not null and not "follow")
        {
            if (camera.StartsWith("ghost:", StringComparison.Ordinal) && int.TryParse(camera[6..], out ghostCam))
            { }
            else if (camera.StartsWith("action", StringComparison.Ordinal))
                actionDist = camera.Length > 7 ? double.Parse(camera[7..], CultureInfo.InvariantCulture) : 14;
            else if (camera.StartsWith("orbit:", StringComparison.Ordinal))
                orbitCam = [.. camera[6..].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture))];
            else
            {
                fixedCam = [.. camera.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture))];
                if (fixedCam.Length != 5)
                {
                    Console.Error.WriteLine("tribes: --camera expects follow, ghost:N or X,Y,Z,YAW,PITCH");
                    return 2;
                }
            }
        }
        var data = File.ReadAllBytes(demoPath);
        // Pass 1: the environment ghosts (volumes, palette, terrain, sky, interiors) of the always phase.
        var pre = new DemoPlayback(data);
        pre.StepUntilEnvironment();
        var env = EnvironmentObjects(pre.World);
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out var error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        GlWindow.SetSwapInterval(0);
        var res = GfxAssets.Mount(root, out _, "config;base;base\\missions");
        foreach (var o in env.Where(o => o.Class == "SimVolume"))
            res.AddVolume(o.Get("fileName"));
        var name = Path.GetFileNameWithoutExtension(demoPath);
        using WorldScene scene = WorldScene.LoadFrom(dev, res, name, env, out var loadError);
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
        model.BaseLight.AmbR = (float)scene.SunAmbient[0];
        model.BaseLight.AmbG = (float)scene.SunAmbient[1];
        model.BaseLight.AmbB = (float)scene.SunAmbient[2];
        model.BaseLight.SunR = (float)scene.SunIntensity[0];
        model.BaseLight.SunG = (float)scene.SunIntensity[1];
        model.BaseLight.SunB = (float)scene.SunIntensity[2];
        play.OnPacket = model.OnPacket;
        play.OnFrame = model.Tick;
        Console.Error.WriteLine($"{name}: {env.Count} environment objects, terrain visible {scene.Terrain.Visible}, client {play.Demo.ClientId}, {play.Demo.Packets} packets");
        var dir = ts.Count > 1 || !Path.HasExtension(output);
        if (dir)
            Directory.CreateDirectory(output);
        foreach (var t in ts)
        {
            play.StepTo(t * 1000);
            var pose = actionDist > 0 ? ActionCamera(model, scene.TerrainModel, actionDist)
                : orbitCam != null && model.Visuals.TryGetValue((int)orbitCam[0], out var ov) && ov.Position is var (ox, oy, oz)
                    ? Orbit(ox, oy, oz + 1.2, orbitCam[2] * Math.PI / 180, orbitCam[3] * Math.PI / 180, orbitCam[1])
                : fixedCam != null ? new CameraPose(fixedCam[0], fixedCam[1], fixedCam[2], fixedCam[3], fixedCam[4], false, -1)
                : ghostCam >= 0 ? GhostCamera(model, ghostCam) : ControlCamera(model, play.World);
            if (pose == null)
            {
                Console.Error.WriteLine($"  t={t}: no camera (the control object has no position yet)");
                continue;
            }
            var c = pose.Value;
            scene.Near = NearFor(c, play.World);
            var ctx = new FrameContext { HazeStart = scene.Terrain.HazeStart, Visible = scene.Terrain.Visible, FirstPersonOfControl = c.FirstPerson, ControlGhost = c.ControlGhost };
            int images = 0, draws = 0;
            ctx.ContainerOf = scene.Interiors == null ? null : box => scene.Interiors.ContainerOf(box, ctx.Camera);
            scene.Prepare = cam =>
            {
                ctx.Camera = cam;
                model.Build(ctx);
                images = ctx.Lists.ImageCount;
                draws = 0;
            };
            scene.DrawContents = i => draws += ctx.Lists.DrawContents(dev, i);
            scene.Interiors?.SimSeconds = model.Time;
            void DrawShapes(Camera3D cam)
            {
                using (FramePerf.Scope("shapes-draw"))
                    draws += ctx.Lists.Draw(dev);
            }
            // OT_BENCH=N: N extra frames of this view with the frame timing log on (one report line), for before/after runs.
            if (int.TryParse(Environment.GetEnvironmentVariable("OT_BENCH"), out var bench) && bench > 0)
            {
                FramePerf.Enable(bench);
                FramePerf.Device = dev;
                for (var i = 0; i <= bench; ++i)
                {
                    FramePerf.FrameStart();
                    scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, DrawShapes, null, c.RollDeg);
                }
            }
            scene.DynamicLights = model.CollectLights().Points;
            scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, DrawShapes, ui == null ? null : () =>
            {
                ui.Clock = (long)(t * 1000);
                if (Environment.GetEnvironmentVariable("OT_HUD_DEBUG") != null)
                    for (var i = 0; i < play.World.Psc.ItemCounts.Length; ++i)
                        if (play.World.Psc.ItemCounts[i] > 0 && play.World.DataBlocks.Blocks.TryGetValue((5, i), out var blk))
                            Console.Error.WriteLine($"item {i} x{play.World.Psc.ItemCounts[i]} {blk.Class}: " + string.Join(" ", blk.Fields.Take(14).Select(f => f.Name + "=" + f.Value)));
                ui.Canvas.Frame();
                ui.Canvas.Render(dev);
            }, c.RollDeg);
            var px = dev.ReadPixels();
            var path = dir ? Path.Combine(output, $"{name}_{t.ToString("0.###", CultureInfo.InvariantCulture)}.png") : output;
            MissionRender.WritePng(path, px, dev.Width, dev.Height);
            if (Environment.GetEnvironmentVariable("OT_DEMO_VERBOSE") != null)
                foreach (var v in model.Visuals.Values.Where(v => v.Position != null).OrderBy(v => { var (x, y, z) = v.Position!.Value; return ((x - c.X) * (x - c.X)) + ((y - c.Y) * (y - c.Y)) + ((z - c.Z) * (z - c.Z)); }).Take(12))
                {
                    var gh = float.NaN;
                    scene.TerrainModel?.Height((float)v.Position!.Value.x, (float)v.Position!.Value.y, ref gh);
                    Console.Error.WriteLine($"    {v.Index} {v.Class} {v.Position!.Value.x:F1},{v.Position!.Value.y:F1},{v.Position!.Value.z:F1} ground {gh:F1}" + (v is PlayerVisual pvv ? $" anim {pvv.AnimIndex} skin {pvv.SkinName} shape {pvv.ArmorShape}" : ""));
                }
            Console.Error.WriteLine($"  t={t}: camera {c.X:F1},{c.Y:F1},{c.Z:F1} yaw {c.YawDeg:F0} pitch {c.PitchDeg:F0}; visuals {model.Visuals.Count} effects {model.Effects.Count} puffs {model.Smoke.Live} decals {model.Decals.Count}; images {images} draws {draws} -> {path}");
        }
        Console.Error.WriteLine($"  explosions {model.ExplosionsSpawned}, footprints {model.FootprintsAdded} (triggers {model.TriggerCount}, on ground {model.ContactTriggers}), bullet holes {model.BulletHolesAdded}, missing files: {string.Join(" ", assets.Missing.Take(12))}");
        return 0;
    }
}
