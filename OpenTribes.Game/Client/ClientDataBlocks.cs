// Client DataBlockManager: unpack of the DataBlockEvent (docs/client-net.md 2.6, docs/game-stream.md 4.2). The
// layouts are the pack order of each group's class (vtable slot 0), the same tables tools/stream_decode.py reads.
namespace OpenTribes.Game;

public sealed class ClientDataBlock
{
    public int Group;
    public int Index;
    public string Class = "";
    public List<DecodedField> Fields = new();
}

// 00402b74 DataBlockManager: block counts and the blocks received, per group.
public sealed class ClientDataBlocks
{
    public readonly Dictionary<int, int> Counts = new();
    public readonly Dictionary<(int Group, int Index), ClientDataBlock> Blocks = new();
    public bool Finished;                          // dataFinished() was evaluated (2.6 step 3)
    public int Received;
    public Action<string, double>? OnProgress;     // dataGotBlock(label, progress)

    // The local models of the model check (7.1). Null: no check (a headless session without game files).
    public ResourceManager? Resources;
    // Set by the check that failed: the text of the "Invalid packet" error (client-net.md 1.4); the session leaves the server.
    public string? PreloadError;

    public void Clear()
    {
        Counts.Clear();
        Blocks.Clear();
        Finished = false;
        Received = 0;
    }

    private readonly record struct E(string K, string N = "", int A = 0, int B = 0, double S = 0);

    private static E S(string n) => new("s", n);
    private static E Shape(string n) => new("shape", n);
    private static E F(string n) => new("f32", n);
    private static E I32(string n) => new("i32", n);
    private static E Raw(string n, int bytes) => new("raw", n, bytes);
    private static E I(string n, int bits, int bias) => new("i", n, bits, bias);
    private static E Flag(string n) => new("flag", n);
    private static E Unit(string n, int bits, double scale) => new("unit", n, bits, 0, scale);
    private static readonly E GameBase = new("gamebase"), ShapeBase = new("shapebase"), StaticBase = new("staticbase"),
        Projectile = new("projectile"), Sensor = new("sensor"), Vehicle = new("vehicle");

    private static E[] Rep(Func<int, E[]> f, int n)
    {
        var l = new List<E>();
        for (int i = 0; i < n; ++i)
            l.AddRange(f(i));
        return l.ToArray();
    }

    private static E[] Cat(params E[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly Dictionary<string, E[]> Layouts = new()
    {
        ["GameBase"] = new[] { new E("si", "mapFilter", 8), S("mapIcon"), S("description") },
        ["ShapeBase"] = new[] { GameBase, Shape("shapeFile"), S("shieldShapeName"), I("unnamed@0x44", 8, 1), I("shadowDetailMask", 8, 0),
            I("explosionId", 8, 1), I("damageSkinData", 8, 1), I32("debrisId"), F("maxEnergy"), new E("seqsound"), Flag("isPerspective") },
        ["StaticBase"] = new[] { ShapeBase, I("sfxAmbient", 8, 1), Flag("isTranslucent"), Flag("disableCollision") },
        ["Projectile"] = new[] { GameBase, Shape("bulletShapeName"), I32("explosionTag"), I32("expRandCycle"), Flag("collideWithOwner"),
            I32("ownerGraceMS"), F("collisionRadius"), F("mass"), F("field@0x68"), F("field@0x6c"), F("field@0x70"), F("totalTime"),
            F("liveTime"), F("field@0x7c"), Flag("isVisible"), F("lightRange"), F("lightColor.r"), F("lightColor.g"), F("lightColor.b"),
            I32("soundId"), F("explosionRadius") },
        ["sensor"] = new[] { StaticBase, I32("range"), I32("dopplerVelocity"), Flag("supression"), Flag("pinger") },
        ["vehicle"] = Cat(new[] { StaticBase, F("mass"), F("drag"), F("density"), F("maxSpeed"), F("minSpeed"), Raw("visibleDriver", 1),
            I32("driverPose"), I("fireSound", 8, 1), I("damageSound", 8, 1), I("mountSound", 8, 1), I("dismountSound", 8, 1),
            I("idleSound", 8, 1), I("moveSound", 8, 1) },
            Rep(i => new[] { F("suspension[" + i + "].x"), F("suspension[" + i + "].y"), F("suspension[" + i + "].z") }, 4)),
        ["item"] = new[] { ShapeBase, I32("imageType"), I32("price"), F("mass"), F("drag"), F("density"), F("friction"), F("elasticity"),
            S("hudIcon"), Flag("showInventory"), Flag("showWeaponBar"), Flag("hiliteOnActive"), S("heading"), I32("lightType"),
            F("lightRadius"), F("lightTime"), F("lightColor.x"), F("lightColor.y"), F("lightColor.z") },
        ["laser"] = new[] { Projectile, S("laserBitmapName"), S("hitName"), Flag("detachFromShooter") },
        ["lightning"] = new[] { Projectile, I32("segmentDivisions"), I32("numSegments"), I32("updateTime"), F("skipPercent"),
            F("displaceBias"), F("beamWidth"), S("bitmapName") },
    };

    private static readonly Dictionary<int, (string Class, string Layout)> Groups = new()
    {
        [0] = ("SoundProfileData", "!soundprofile"),
        [1] = ("SoundData", "!sound"),
        [2] = ("DamageSkinData", "!damageskin"),
        [3] = ("PlayerData", "!armor"),
        [4] = ("StaticShapeData", "!static"),
        [5] = ("ItemData", "item"),
        [6] = ("ItemImageData", "!image"),
        [7] = ("MoveableData", "!moveable"),
        [8] = ("SensorData", "sensor"),
        [9] = ("VehicleData", "vehicle"),
        [10] = ("FlierData", "!flier"),
        [13] = ("ProjectileData", "Projectile"),
        [14] = ("BulletData", "!bullet"),
        [15] = ("GrenadeData", "!grenade"),
        [16] = ("RocketData", "!rocket"),
        [17] = ("LaserData", "laser"),
        [19] = ("TurretData", "!turret"),
        [20] = ("ExplosionData", "!explosion"),
        [21] = ("MarkerData", "!marker"),
        [22] = ("DebrisData", "!debris"),
        [23] = ("MineData", "item"),
        [24] = ("TargetLaserData", "laser"),
        [25] = ("SeekingMissileData", "!seeker"),
        [26] = ("TriggerData", "!trigger"),
        [28] = ("LightningData", "lightning"),
        [29] = ("RepairEffectData", "lightning"),
        [30] = ("IRCChannel", "!trigger"),
    };

    private static E[] Special(string name) => name switch
    {
        "!sound" => new[] { S("wavFileName"), Unit("priority", 6, 1.0), I("profile", 8, 1) },
        "!damageskin" => Rep(i => new[] { S("bmpName[" + i + "]") }, 10),
        "!static" => new[] { StaticBase },
        "!image" => new[] { GameBase, S("shapeFile"), Raw("projectileType", 8), I32("mountPoint"), F("mountOffset.x"), F("mountOffset.y"),
            F("mountOffset.z"), F("mountRotation.x"), F("mountRotation.y"), F("mountRotation.z"), Flag("firstPerson"), I32("weaponType"),
            I32("ammoType"), F("activateTime"), F("fireTime"), F("reloadTime"), F("spinUpTime"), F("spinDownTime"), F("mass"),
            I("sfxActivate", 8, 1), I("sfxFire", 8, 1), I("sfxReady", 8, 1), I("sfxReload", 8, 1), I("sfxSpinUp", 8, 1),
            I("sfxSpinDown", 8, 1), I32("lightType"), F("lightRadius"), F("lightTime"), F("lightColor.x"), F("lightColor.y"),
            F("lightColor.z"), F("minEnergy"), F("maxEnergy") },
        "!moveable" => new[] { StaticBase, Flag("displace"), Flag("blockForward"), Flag("blockBackward"), I("sfxStart", 8, 1),
            I("sfxStop", 8, 1), I("sfxRun", 8, 1), I("sfxBlocked", 8, 1), F("speed") },
        "!flier" => new[] { Vehicle, F("maxBank"), F("maxPitch"), F("lift"), F("maxAlt"), F("maxVertical"), F("accel"),
            F("groundDamageScale") },
        "!bullet" => new[] { Projectile, F("rotationPeriod"), I32("bulletHoleIndex"), F("tracerLength") },
        "!grenade" => new[] { Projectile, F("elasticity"), S("smokeName") },
        "!rocket" => new[] { Projectile, I32("trailType"), S("trailString"), F("trailLength"), F("trailWidth") },
        "!turret" => new[] { Sensor, Raw("projectileType", 8), F("gunRange"), F("speed"), F("speedModifier"), F("FOV"),
            I("fireSound", 8, 1), I("activationSound", 8, 1), I("deactivateSound", 8, 1), I("whirSound", 8, 1), Flag("isSustained") },
        "!marker" => new[] { GameBase, S("shapeFile") },
        "!debris" => new[] { GameBase, I32("type"), S("pShapeName"), I32("imageType"), I32("imageSortValue"), F("mass"),
            F("elasticity"), F("friction"), F("center.x"), F("center.y"), F("center.z"), I32("collisionMask"), I32("knockMask"),
            I32("animationSequence"), F("animRestartTime"), F("minTimeout"), F("maxTimeout"), F("explodeOnBounce"), F("damage"),
            F("damageThreshold"), I32("spawnedDebrisMask"), F("spawnedDebrisStrength"), F("spawnedDebrisRadius"),
            I32("spawnedExplosionID"), F("p"), Flag("explodeOnRest"), I("collisionDetail", 2, 0) },
        "!seeker" => new[] { Projectile, F("smokeDist"), F("seekingTurningRadius"), F("nonSeekingTurningRadius") },
        "!trigger" => new[] { GameBase },
        _ => throw new UnsupportedException("datablock layout " + name),
    };

    private static E[] LayoutOf(string name)
    {
        if (Layouts.TryGetValue(name, out E[]? l))
            return l;
        return Special(name);
    }

    private static void Fields(ClientReader r, E[] layout)
    {
        foreach (E e in layout)
        {
            switch (e.K)
            {
                case "gamebase": Fields(r, Layouts["GameBase"]); break;
                case "shapebase": Fields(r, Layouts["ShapeBase"]); break;
                case "staticbase": Fields(r, Layouts["StaticBase"]); break;
                case "projectile": Fields(r, Layouts["Projectile"]); break;
                case "sensor": Fields(r, Layouts["sensor"]); break;
                case "vehicle": Fields(r, Layouts["vehicle"]); break;
                case "s": r.Add(e.N, r.Str()); break;
                case "shape":
                    {
                        string s = r.Str();
                        // A leading 0xff or 0xfe asks the client to validate the shape: a 32 bit CRC follows.
                        if (s.Length > 0 && (s[0] == 'ÿ' || s[0] == 'þ'))
                        {
                            r.Add(e.N, s.Substring(1));
                            r.Add(e.N + ".crc", (long)r.Int(32));
                            r.Add(e.N + ".materials", s[0] == 'þ' ? 1L : 0L);
                        }
                        else
                            r.Add(e.N, s);
                        break;
                    }
                case "f32": r.AddF32(e.N); break;
                case "i32": r.Add(e.N, (long)(int)r.Int(32)); break;
                case "raw": r.Add(e.N, Convert.ToHexString(r.Bytes(e.A)).ToLowerInvariant()); break;
                case "i": r.Add(e.N, (long)r.Int((uint)e.A) - e.B); break;
                case "si": r.Add(e.N, (long)r.SInt((uint)e.A)); break;
                case "flag": r.AddFlag(e.N); break;
                case "unit": r.Add(e.N, r.Unit((uint)e.A) * e.S); break;
                case "seqsound":
                    for (int i = 0; i < 16; ++i)
                        if (r.Flag())
                            r.Add("sequenceSound[" + i + "]", new object[] { r.Str(), (long)r.Int(8) - 1 });
                    break;
                default: throw new UnsupportedException("datablock element " + e.K);
            }
        }
    }

    private static void SoundProfile(ClientReader r)
    {
        r.Add("flags", (long)r.Int(6));
        r.Add("baseVolume", r.Unit(10));
        if (r.Flag()) r.Add("coneInsideAngle", r.Unit(10) * 360);
        if (r.Flag()) r.Add("coneOutsideAngle", r.Unit(10) * 360);
        if (r.Flag()) r.Add("coneVector", r.Normal(10));
        if (r.Flag()) r.AddF32("coneOutsideVolume");
        if (r.Flag()) r.AddF32("minDistance");
        if (r.Flag()) r.AddF32("maxDistance");
    }

    private static void Armor(ClientReader r)
    {
        Fields(r, Layouts["ShapeBase"]);
        r.Add("flameShapeName", r.Str());
        for (int i = 0; i < 51; ++i)
        {
            string name = r.Str();
            long seq = r.Flag() ? (long)r.Int(8) - 1 : -1;
            long f = r.Flag() ? 1 : 0;
            long a = r.Int(4), b = r.Int(4);
            r.Add("animData[" + i + "]", new object[] { name, seq, f, a, b });
        }
        r.AddF32("minDamageSpeed");
        r.AddF32("jetEnergyDrain");
        foreach (string n in new[] { "maxForwardSpeed", "maxBackwardSpeed", "maxSideSpeed" })
            r.Add(n, r.Unit(10) * 64);
        r.AddFlag("canCrouch");
        r.Add("maxJetSideForceFactor", r.Unit(8));
        foreach (string n in new[] { "groundForce", "groundTraction", "maxJetForwardVelocity" })
            r.AddF32(n);
        r.Add("jumpSurfaceMinDot", r.Unit(8));
        foreach (string n in new[] { "jumpImpulse", "mass", "drag", "horzVelClamp", "jetForce" })
            r.AddF32(n);
        r.Add("jetSound", (long)r.Int(8) - 1);
        for (int i = 0; i < 15; ++i)
            r.Add("footSounds[" + i + "]", new object[] { (long)r.Int(8) - 1, (long)r.Int(8) - 1 });
        r.Add("footPrints", Convert.ToHexString(r.Bytes(8)).ToLowerInvariant());
        foreach (string n in new[] { "boxWidth", "boxDepth", "boxNormalHeight", "boxCrouchHeight" })
            r.AddF32(n);
    }

    private static void Explosion(ClientReader r)
    {
        Fields(r, Layouts["GameBase"]);
        Fields(r, new[] { Shape("shapeName"), I32("soundId"), F("timeScale"), Flag("faceCamera"), Flag("randomSpin") });
        if (r.AddFlag("hasLight"))
        {
            var l = new List<E> { F("lightRange"), F("timeZero"), F("timeOne") };
            for (int i = 0; i < 3; ++i)
                foreach (char c in "rgb")
                    l.Add(F("colors[" + i + "]." + c));
            for (int i = 0; i < 3; ++i)
                l.Add(F("radFactors[" + i + "]"));
            Fields(r, l.ToArray());
        }
        r.AddFlag("shiftPosition");
    }

    // Reads the block body of `group` into r.Out; returns the class name.
    public static string ReadBlock(ClientReader r, int group)
    {
        if (!Groups.TryGetValue(group, out var g))
            throw new UnsupportedException("datablock group " + group);
        switch (g.Layout)
        {
            case "!soundprofile": SoundProfile(r); break;
            case "!armor": Armor(r); break;
            case "!explosion": Explosion(r); break;
            default: Fields(r, LayoutOf(g.Layout)); break;
        }
        return g.Class;
    }

    // DataBlockEvent unpack (004029f0) and process (004031ac). Returns the text summary of the event line.
    public void ReadEvent(ClientReader r, out string summary)
    {
        int group = (int)r.Int(6);
        int count = (int)r.Int(8), index = (int)r.Int(8);
        Counts[group] = count;
        r.Add("group", (long)group);
        r.Add("count", (long)count);
        if (index == 255)
        {
            r.Add("index", -1L);
            summary = "group=" + group + " count=" + count + " index=none";
            AfterBlock(group, count, -1);
            return;
        }
        r.Add("index", (long)index);
        var save = r.Out;
        r.Out = new List<DecodedField>();
        string cls = ReadBlock(r, group);
        var block = new ClientDataBlock { Group = group, Index = index, Class = cls, Fields = r.Out };
        r.Out = save;
        r.Add("class", cls);
        r.Add("fields", block.Fields.Select(f => new object?[] { f.Name, f.Value }).ToList());
        Blocks[(group, index)] = block;
        ++Received;
        summary = "group=" + group + " count=" + count + " index=" + index + " " + cls;
        // Process step 2: the preload fails before dataGotBlock runs.
        if ((PreloadError = CheckModel(block)) != null)
            return;
        AfterBlock(group, count, index);
    }

    public const string kMismatchFormat = "Client side model {0} or its textures do not match the server's and the server requires validation of this model or textures.";

    // Client preload of a block whose shape name came with a CRC (validateShape): ShapeBase 0x0044a204 (shapeFile + ".dts"),
    // Projectile 0x00459804 (bulletShapeName) and Explosion 0x0046de5c (shapeName), all through 0x0044a078. The CRC of the
    // local model (and its textures when the name came with 0xfe) must equal the server's, else the preload fails with
    // kMismatchFormat. The server side of the same call stores the CRC instead (DataBlockManager.Preload).
    public string? CheckModel(ClientDataBlock b)
    {
        if (Resources == null)
            return null;
        foreach (DecodedField f in b.Fields)
        {
            if (!f.Name.EndsWith(".crc", StringComparison.Ordinal) || f.Value is not long wire)
                continue;
            string field = f.Name[..^4];
            string name = b.Fields.First(x => x.Name == field).Value as string ?? "";
            bool materials = b.Fields.First(x => x.Name == field + ".materials").Value is long m && m != 0;
            if (name.Length == 0)
                continue; // preload skips an empty name
            if (field == "shapeFile")
                name += ".dts";
            if (DataBlockManager.ValidationCrc(Resources, name, materials) != (uint)wire)
                return string.Format(kMismatchFormat, name);
        }
        return null;
    }

    // Process (2.6 steps 2, 3): progress = (group * count + index) / (count * 31); group 30 with the last index (or an
    // empty group 30) is dataFinished().
    private void AfterBlock(int group, int count, int index)
    {
        if (index != -1)
            OnProgress?.Invoke(Groups.TryGetValue(group, out var g) ? g.Class : "group" + group,
                count == 0 ? 0 : (group * (double)count + index) / (count * 31.0));
        if (group == 30 && index == count - 1)
            Finished = true;
    }
}
