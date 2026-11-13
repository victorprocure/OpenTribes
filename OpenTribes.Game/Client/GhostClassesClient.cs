// Client unpackUpdate of the mission environment classes (docs/game-stream-ghosts.md 3, client-net.md 3.2, 3.3) and of
// the GameBase family (4.1 .. 4.9). The pack routines sit in GhostClasses.cs; every reader here consumes exactly the bits
// of the writer of the same class. A reader records each field in r.Out under the name tools/stream_decode.py uses.
namespace OpenTribes.Game;

/// <summary>The client unpack routines of the mission environment classes and of the GameBase family.</summary>
/// <remarks>See docs/game-stream-ghosts.md 3 and 4.1 to 4.9, and docs/client-net.md 3.2 and 3.3. The pack routines are in GhostClasses.cs. Every reader consumes exactly the bits of the writer of the same class and records each field in <c>r.Out</c> under the name tools/stream_decode.py uses.</remarks>
public static partial class GhostClasses
{
    // ---- registry ----

    internal static void RegisterClient()
    {
        void R(uint tag, string name, ClientUnpackFn fn) => ClientGhostManager.RegisterClass(tag, name, fn);
        R(0x001, "SimVolume", (r, g, n) => r.Add("file", r.Str()));
        R(0x002, "SimTerrain", UnTerrain);
        R(0x003, "SimPalette", (r, g, n) => r.Add("file", r.Str()));
        R(0x006, "SimLight", UnSimLight);
        R(0x202, "Turret", UnTurret);
        R(0x20a, "InteriorShape", UnInterior);
        R(0x20b, "Debris", UnDebris);
        R(0x20c, "DebrisCloud", UnDebrisCloud);
        R(0x216, "Snowfall", UnSnowfall);
        R(0x217, "Explosion", (r, g, n) => UnGameBase(r, g));
        R(0x21b, "Marker", UnMarker);
        R(0x21e, "Mine", (r, g, n) => UnItem(r, g, n, 23));
        R(0x21f, "Trigger", UnTrigger);
        R(0x220, "Moveable", UnMoveable);
        R(0x221, "StaticShape", UnStatic);
        R(0x223, "Flier", (r, g, n) => UnVehicle(r, g, n, 10));
        R(0x229, "Sensor", UnSensor);
        R(0x22a, "Item", (r, g, n) => UnItem(r, g, n, 5));
        R(0x22c, "Vehicle", (r, g, n) => UnVehicle(r, g, n, 9));
        R(0x22f, "Planet", UnPlanet);
        R(0x230, "Sky", UnSky);
        R(0x231, "ObserverCamera", (r, g, n) => UnGameBase(r, g));
        R(0x232, "StarField", UnStarField);
        R(0x2cc, "MissionCenterPos", (r, g, n) => r.Add("rect", new[] { (double)r.F32(), r.F32(), r.F32(), r.F32() }));
    }

    // ---- 4.1 GameBase part (client-net 3.2) ----

    // GameBase_unpackUpdate 00448e1c: team, then the control and owner clients, re-linked on the PlayerManager records.
    internal static void UnGameBase(ClientReader r, ClientGhost g)
    {
        if (!r.Flag())
            return;
        r.Add("team", (long)r.Int(4));
        if (r.Flag())
        {
            int id = (int)r.Int(7) + 0x800;
            r.Add("controlClient", (long)id);
            foreach (ClientPlayerRecord rec in r.World.Players.Clients.Values.Where(x => x.ControlGhost == g.Index))
                rec.ControlGhost = -1;
            if (r.World.Players.Get(id) is ClientPlayerRecord c)
                c.ControlGhost = g.Index;
        }
        if (r.Flag())
        {
            int id = (int)r.Int(7) + 0x800;
            r.Add("ownerClient", (long)id);
            foreach (ClientPlayerRecord rec in r.World.Players.Clients.Values.Where(x => x.OwnedGhost == g.Index))
                rec.OwnedGhost = -1;
            if (r.World.Players.Get(id) is ClientPlayerRecord c)
                c.OwnedGhost = g.Index;
        }
    }

    // ShapeBase part 1 (0044d210): damage flash, the three threads, fade.
    internal static void UnShapeBase1(ClientReader r, ClientGhost g)
    {
        UnGameBase(r, g);
        if (r.Flag())
        {
            r.Add("flashNormal", r.Normal(7));
            r.Add("flashAmount", r.Unit(8) * 10 - 5);
        }
        if (r.Flag())
        {
            var th = new List<object?>();
            for (int i = 0; i < 3; ++i)
                if (r.Flag())
                {
                    long seq = r.Int(4), state = r.Int(2);
                    bool fwd = r.Flag(), end = r.Flag();
                    th.Add(i + ":seq=" + seq + " state=" + state + " fwd=" + (fwd ? 1 : 0) + " end=" + (end ? 1 : 0));
                }
            r.Add("threads", th);
        }
        if (r.Flag())
            r.Add("fade", r.Flag() ? 2L : 1L);
    }

    // ShapeBase part 2 (00450184): transform, damage, target flag.
    internal static void UnShapeBase(ClientReader r, ClientGhost g)
    {
        UnShapeBase1(r, g);
        if (r.Flag())
        {
            r.AddVec("pos");
            r.AddVec("rot");
        }
        if (r.Flag())
        {
            long st = r.Flag() ? 1 : (r.Flag() ? 2 : 0);
            r.Add("damageState", st);
            r.Add("damage", r.Unit(6));
        }
        if (r.Flag())
            r.AddFlag("isTarget");
    }

    // ---- 3 mission environment ----

    private static void UnTerrain(ClientReader r, ClientGhost g, bool isNew)
    {
        if (r.Flag()) r.Add("ted", r.Str());
        if (r.Flag()) r.Add("visible/haze/hazeVMin/hazeVMax", new[] { (double)r.F32(), r.F32(), r.F32(), r.F32() });
        if (r.Flag()) r.Add("perspectiveDistance/screenSize", new[] { (double)r.F32(), r.F32() });
        if (r.Flag())
        {
            r.AddVec("position");
            r.AddVec("rotation");
        }
        if (r.Flag())
        {
            r.AddVec("contGravity");
            r.AddF32("contDrag");
            r.AddF32("contHeight");
        }
        r.AddFlag("flag6a4");
        if (r.Flag()) r.Add("crc", r.Int(32).ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void UnSimLight(ClientReader r, ClientGhost g, bool isNew)
    {
        if (r.Flag()) r.AddFlag("isStatic");
        if (r.Flag()) r.AddInt("type", 3);
        if (r.Flag()) r.AddF32("range");
        if (r.Flag()) r.AddVec("color");
        if (r.Flag()) r.AddVec("ambient");
        if (r.Flag()) r.AddVec("position");
        if (r.Flag()) r.AddVec("aim");
    }

    private static void UnInterior(ClientReader r, ClientGhost g, bool isNew)
    {
        UnGameBase(r, g);
        if (r.Flag())
        {
            r.Add("file", r.Str());
            r.Add("isContainer", (long)r.SInt(2));
            r.AddVec("pos");
            r.AddVec("rot");
            long n = r.Int(32);
            var states = new List<object?>();
            for (long i = 0; i < n; ++i)
                states.Add((long)r.Int(32));
            r.Add("states", states);
            long m = r.Int(32);
            var lights = new List<byte>();
            for (long i = 0; i < m; ++i)
                lights.AddRange(r.Bytes(16));
            r.Add("lights", lights.Count == 0 ? "-" : Convert.ToHexString(lights.ToArray()).ToLowerInvariant());
            g.State["lightCount"] = m;
            r.AddFlag("flag41c");
            return;
        }
        if (r.Flag())
        {
            r.AddVec("pos");
            r.AddVec("rot");
        }
        if (r.Flag())
        {
            if (!g.State.TryGetValue("lightCount", out object? mc))
                throw new UnsupportedException("InteriorShape light flags without the initial update");
            var on = new List<object?>();
            for (long i = 0; i < (long)mc!; ++i)
                on.Add(r.Flag() ? 1L : 0L);
            r.Add("lightOn", on);
        }
        if (r.Flag())
            r.AddFlag("flag418");
    }

    private static void UnSnowfall(ClientReader r, ClientGhost g, bool isNew)
    {
        if (r.Flag()) r.AddF32("intensity");
        if (r.Flag()) r.AddVec("wind");
        r.AddFlag("flagf0");
        r.AddFlag("flagf1");
    }

    private static void UnSky(ClientReader r, ClientGhost g, bool isNew)
    {
        r.Add("dml", r.Str());
        r.AddVec("vecB8");
        r.AddVec("vecC4");
        r.AddF32("f11c");
        r.AddF32("f124");
        var tex = new List<object?>();
        for (int i = 0; i < 16; ++i)
            tex.Add((long)r.Int(4));
        r.Add("textures", tex);
    }

    private static void UnPlanet(ClientReader r, ClientGhost g, bool isNew)
    {
        r.Add("file", r.Str());
        r.AddF32("f6c");
        r.AddF32("f70");
        r.AddInt("b74", 8);
        r.AddInt("b75", 8);
        r.AddVec("v78");
        r.AddVec("v84");
        r.AddF32("f128");
        r.AddF32("f12c");
    }

    private static void UnStarField(ClientReader r, ClientGhost g, bool isNew)
    {
        r.AddFlag("flagAF");
        r.AddFlag("flagB0");
        var c = new double[9];
        for (int i = 0; i < 9; ++i)
            c[i] = r.F32();
        r.Add("data7c", c);
        r.AddFlag("flagAE");
    }

    // ---- 4 GameBase family ----

    private static void UnDatablock(ClientReader r, int group) => r.AddInt("datablock", r.DbBits(group));

    private static void UnStatic(ClientReader r, ClientGhost g, bool isNew)
    {
        UnShapeBase(r, g);
        if (r.Flag())
        {
            UnDatablock(r, 4);
            if (r.Flag())
                r.AddInt("target", 7);
        }
    }

    private static void UnSensor(ClientReader r, ClientGhost g, bool isNew)
    {
        UnShapeBase(r, g);
        if (r.Flag())
            UnDatablock(r, 8);
    }

    private static void UnItem(ClientReader r, ClientGhost g, bool isNew, int group)
    {
        UnShapeBase(r, g);
        if (r.Flag())
        {
            UnDatablock(r, group);
            if (r.Flag())
                r.AddInt("target", 7);
            if (r.Flag())
                r.AddInt("thrower", 10);
            r.AddFlag("rotates");
            r.AddFlag("collideable");
        }
        if (r.Flag())
            r.AddUnit("rotZ", 8);
        if (r.Flag())
            r.AddVec("pos");
        if (r.Flag())
            r.Add("atRest", 1L);
        else
        {
            r.Add("atRest", 0L);
            if (r.Flag())
                r.AddVec("vel");
        }
    }

    private static void UnTurret(ClientReader r, ClientGhost g, bool isNew)
    {
        if (r.Flag())
        {
            r.Add("ownerOnly", 1L);
            return;
        }
        UnShapeBase(r, g);
        if (r.Flag())
            UnDatablock(r, 19);
        if (r.Flag())
            r.AddUnit("rotation", 8);
        if (r.Flag())
            r.AddUnit("elevation", 8);
        if (r.Flag())
            r.AddInt("fireState", 2);
        if (r.Flag())
            r.AddInt("mode", 4);
        r.AddFlag("barrelFiring");
    }

    private static void UnMoveable(ClientReader r, ClientGhost g, bool isNew)
    {
        UnShapeBase(r, g);
        if (isNew)
            UnDatablock(r, 7);
        if (r.Flag())
            r.AddInt("path", 8);
        if (r.Flag())
        {
            r.AddInt("waypoint", 4);
            r.AddF32("waypointTime");
        }
        if (r.Flag())
        {
            r.AddFlag("blocked");
            r.AddInt("targetWaypoint", 4);
            r.AddInt("state", 2);
        }
    }

    private static void UnMarker(ClientReader r, ClientGhost g, bool isNew)
    {
        UnGameBase(r, g);
        if (r.Flag())
            UnDatablock(r, 21);
        if (r.Flag())
        {
            r.AddVec("pos");
            r.AddVec("rot");
        }
    }

    private static void UnTrigger(ClientReader r, ClientGhost g, bool isNew)
    {
        if (r.Flag())
            r.Add("transform", Convert.ToHexString(r.Bytes(0x34)).ToLowerInvariant());
        if (r.Flag())
        {
            var b = new double[6];
            for (int i = 0; i < 6; ++i)
                b[i] = r.F32();
            r.Add("box", b);
        }
    }

    // Vehicle / Flier (client-net 3.5, game-stream-ghosts 4.8, 00446ecc).
    private static void UnVehicle(ClientReader r, ClientGhost g, bool isNew, int group)
    {
        if (r.Flag())
        {
            r.Add("ownerOnly", 1L);
            return;
        }
        UnShapeBase(r, g);
        if (isNew)
            UnDatablock(r, group);
        if (r.Flag())
        {
            r.AddVec("rot");
            r.AddVec("pos");
            if (r.Flag())
            {
                var m = new Move();
                Moves.UnpackMove(r.Bs, ref m, false, false);
                if (r.Bs.Error())
                    throw new OverrunException();
                r.Add("move", ClientPsc.FormatMove(m));
                g.State[RemoteGhosts.MoveKey] = m;       // the echoed move of the step (+0x8e4)
                r.AddUnit("unit10", 10);
                r.AddF32("f898");
                r.AddInt("f8d8", 4);
            }
        }
        if (r.Flag())
        {
            r.AddUnit("unit10a", 10);
            r.AddUnit("unit10b", 10);
        }
        if (r.Flag())
            r.AddInt("f948", 2);
        if (r.Flag())
            r.AddInt("i6", 6);
        if (r.Flag())
            r.AddInt("f8c4", 2);
    }

    // ---- classes a stock server never ghosts (game-stream-ghosts.md 6.9, projectile-sim.md 5.3) ----

    // Scaled byte 0046f9d4 on the wire; the reader scales it back. Stream_readScaledByte is Q11 of client-net.md: the
    // scale below is the writer's.
    private static double ScaledByte(ClientReader r, double scale) => r.Int(8) * scale;

    // Byte vector 0046f8fc: magnitude as a scaled byte, then a direction byte (4 bits azimuth, 4 bits elevation) when the
    // magnitude is not 0.
    private static double[] ByteVector(ClientReader r, double scale)
    {
        double mag = ScaledByte(r, scale);
        if (mag == 0)
            return new double[] { 0, 0, 0 };
        uint d = r.Int(8);
        double az = (d & 15) * (2 * Math.PI / 16), el = (d >> 4) * (Math.PI / 16) - Math.PI / 2;
        return new[] { mag * Math.Cos(el) * Math.Cos(az), mag * Math.Cos(el) * Math.Sin(az), mag * Math.Sin(el) };
    }

    private static void UnDebris(ClientReader r, ClientGhost g, bool isNew)
    {
        UnGameBase(r, g);
        r.Add("transform", Convert.ToHexString(r.Bytes(0x34)).ToLowerInvariant());
        r.AddVec("vel");
        r.AddVec("angVel");
        if (r.Flag())
        {
            UnDatablock(r, 22);
            r.AddInt("f344", 32);
            r.Add("shape", r.Str());
            if (r.Flag()) r.Add("mass", r.Int(8) * 50.0);
            if (r.Flag()) r.AddInt("f248", 4);
            if (r.Flag()) r.Add("f24c", r.Int(8) / 10.0);
        }
    }

    private static void UnDebrisCloud(ClientReader r, ClientGhost g, bool isNew)
    {
        r.Add("f94", ScaledByte(r, 50.0));
        r.AddInt("mask", 32);
        r.AddVec("pos");
        r.Add("radius", ScaledByte(r, 0.5));
        r.Add("velocity", ByteVector(r, 0.1));
        r.Add("strength", ScaledByte(r, 50.0));
        uint angle = r.Int(8);
        if (angle == 0xff)
            r.Add("angle", Math.PI);
        else
        {
            r.Add("angle", angle * 0.0125);
            r.AddInt("direction", 8);
        }
        r.Add("fF0", ByteVector(r, 100.0));
        r.Add("ffc", ScaledByte(r, 1.0));
        if (r.Flag())
            r.AddInt("sound", 20);
    }
}
