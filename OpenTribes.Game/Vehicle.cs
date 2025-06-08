// Port of src/game/vehicle.hpp and src/game/vehicle.cpp: Vehicle and Flier (ROADMAP 7.5, docs/vehicle-sim.md):
// datablock binding, the tick, move credit, the flight update, mount transforms, weapon and the stepped move with its
// collision response. The numeric model is the exact one of docs/vehicle-sim.md 3.2; the 80-bit constants of the
// original are the 53-bit rounded doubles of the portable build (docs/portable-float.md 1, 5 item 2). The Chop class of
// the file is X87.Chop; world::x87::f32 is X87.F32.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/vehicle.hpp class Vehicle.
public class Vehicle : GameBase
{
    protected const float kDt = 0.032f; // 0x3d03126f

    // Constants of Flier_update (docs/vehicle-sim.md 3.2): tbytes and doubles of the original. The tbytes are the 64-bit
    // mantissas rounded to 53 bits.
    protected static readonly double kTick = (double)0xf9fffffffffffe89UL * Math.ScaleB(1.0, -59);       // 0x0042f764, just below 31.25
    protected static readonly double kYawPerBank = (double)0x8888888888888889UL * Math.ScaleB(1.0, -67); // 0x0042f7ac, 1/30
    protected static readonly double kThird = (double)0xaaaaaaaaaaaaaaabUL * Math.ScaleB(1.0, -65);      // 0x0042e8e8 and 0x00430768, 1/3
    protected static readonly double kTwoThirds = (double)0xaaaaaaaaaaaaaaabUL * Math.ScaleB(1.0, -64);  // 0x00430780
    protected const double kPi32 = 0.09817477042468103; // 0x0042f774
    protected const double kFloor033 = 0.33;            // 0x0042f780
    protected const double kLevelRate = 0.0125;         // 0x0042f7b8
    protected const float kTiny = 1e-12f;               // 0x0042f7c0

    // src/game/vehicle.hpp Vehicle::Data: VehicleData and FlierData values the simulation reads (docs/vehicle-sim.md 1.4).
    public sealed class VData
    {
        public float Mass = 1, Drag = 1, Density = 1;          // +0x100..+0x108
        public float MinSpeed, MaxSpeed;                       // +0x10c, +0x110
        public float ReloadDelay;                              // +0x134
        public float RamDamage;                                // +0x138
        public int RamDamageType;                              // +0x13c
        public bool VisibleDriver;                             // +0x170
        public int DriverPose;                                 // +0x174
        public int ProjGroup = -1, ProjIndex = -1;             // projectileType (+0x114), -1 none
        public float MaxBank, MaxPitch, Lift, MaxAlt;          // FlierData +0x178..+0x184
        public float MaxVertical, Accel, GroundDamageScale;    // +0x188..+0x190
    }

    public VData Data = new();
    public bool Piloted;          // +0x8a1
    public int Credit;            // +0x8dc
    public int TicksWithoutMove;  // +0x8d8
    public Move LastMove = new(); // +0x8e4
    public int SoundState;        // +0x8c4
    public int FireCount;         // +0x948
    public float NextFireTime;    // +0x94c
    public float SpeedF;          // +0x940 forward speed
    public float SpeedT;          // +0x8c8 desired speed
    public readonly float[] TargetRot = new float[3]; // +0x8cc
    public float SpeedL;          // +0x898 lift speed
    public float SpeedS;          // +0x89c side speed
    public bool Landed;           // +0x8a0
    public int Passengers;        // +0x8ac
    // Node indices into the shape (-1: none): dummy exit, dummy pilot (+ detail size), dummy muzzle, dummy eye.
    public int NodeExit = -1, NodePilot = -1, NodeMuzzle = -1, NodeEye = -1;
    public readonly List<int> MuzzleNodes = new(), PassengerNodes = new();
    public uint TypeMask = 0x24c00080u; // +0x8b0: object types a collision rams

    // src/game/vehicle.cpp Vehicle::Vehicle.
    public Vehicle(ClassContext ctx, string cls, int dataBlockGroup, IEnumerable<PersistDef> fields)
        : base(cls, Kind.ShapeBase, dataBlockGroup, fields, ctx, 718)
    {
        ItemFlags = 0x199; // +0x164: 0x181 | 0x19 (friction, buoyancy, medium drag; no elasticity)
    }

    // src/game/vehicle.cpp Vehicle::from.
    public static Vehicle? From(SimObject? obj) =>
        obj is GameBase gb && gb.ShapeClass() == ShapeKind.Vehicle ? gb as Vehicle : null;

    // src/game/vehicle.cpp Vehicle::game.
    public GameState Game() => (GameState)Services()!;

    // src/game/vehicle.cpp Vehicle::block.
    protected DataBlockManager.Block? Block()
    {
        GameState g = Game();
        return g.Sim != null && DataBlockName != null ? g.Sim.DataBlocks().Find(DataBlockName) : null;
    }

    // Server entry points of the scripts and the tests.
    public int PassengerCount() => PassengerNodes.Count;

    // src/game/vehicle.cpp toTMat.
    protected static TMat3 ToTMat(in Transform t) => ProjectileFire.ToTMat(t);
    // src/game/vehicle.cpp toTransform.
    protected static Transform ToTransform(in TMat3 m) => ProjectileFire.ToTransform(m);
    // src/game/vehicle.cpp g: %g.
    protected static string G(float v) => Numbers.FormatFloat(v);

    // src/game/vehicle.cpp normalise: in place with f32(1 / len) (m_Point3F_len, then the reciprocal scale).
    protected static void Normalise(Span<float> v)
    {
        float k = X87.F32(Div(1.0, X87.F32(X87.Len(v))));
        for (int i = 0; i < 3; ++i)
            v[i] = X87.F32(Mul(k, v[i]));
    }

    // src/game/vehicle.cpp cross.
    protected static void Cross(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        output[0] = X87.F32(Sub(Mul(a[1], b[2]), Mul(a[2], b[1])));
        output[1] = X87.F32(Sub(Mul(a[2], b[0]), Mul(a[0], b[2])));
        output[2] = X87.F32(Sub(Mul(a[0], b[1]), Mul(a[1], b[0])));
    }

    // src/game/vehicle.cpp Vehicle::onNewDataBlock: Vehicle_onNewDataBlock 0x00445230 after the StaticBase part
    // (GameState::resolveDataBlock).
    public override void OnNewDataBlock()
    {
        DataBlockManager.Block? b = Block();
        if (b == null)
            return;
        var d = new VData();
        d.Mass = Pj.Real(b, "mass", 1.0f);
        d.Drag = Pj.Real(b, "drag", 1.0f);
        d.Density = Pj.Real(b, "density", 1.0f);
        d.MinSpeed = Pj.Real(b, "minSpeed", 0.0f);
        d.MaxSpeed = Pj.Real(b, "maxSpeed", 0.0f);
        d.ReloadDelay = Pj.Real(b, "reloadDelay", 0.0f);
        d.RamDamage = Pj.Real(b, "ramDamage", 0.0f);
        d.RamDamageType = Pj.Integer(b, "ramDamageType", 0);
        d.VisibleDriver = Pj.Boolean(b, "visibleDriver", false);
        d.DriverPose = Pj.Integer(b, "driverPose", 0);
        if (Pj.Field(b, "projectileType") is DataBlockManager.Field f && f.Elems[0].I >= 0)
        {
            d.ProjGroup = f.Elems[0].I;
            d.ProjIndex = f.Elems[0].I2;
        }
        d.MaxBank = Pj.Real(b, "maxBank", 0.0f);
        d.MaxPitch = Pj.Real(b, "maxPitch", 0.0f);
        d.Lift = Pj.Real(b, "lift", 0.0f);
        d.MaxAlt = Pj.Real(b, "maxAlt", 0.0f);
        d.MaxVertical = Pj.Real(b, "maxVertical", 0.0f);
        d.Accel = Pj.Real(b, "accel", 0.0f);
        d.GroundDamageScale = Pj.Real(b, "groundDamageScale", 0.0f);
        Data = d;
        TypeMask = 0x24c00080u;
        BindNodes();
    }

    // src/game/vehicle.cpp Vehicle::bindNodes.
    protected void BindNodes()
    {
        NodeExit = NodePilot = NodeMuzzle = NodeEye = -1;
        MuzzleNodes.Clear();
        PassengerNodes.Clear();
        ShapeInstance? inst = ShapeState.ShapeInstanceOf(Game(), this);
        if (inst == null || inst.ShapeData == null)
            return;
        ShapeModel m = inst.ShapeData;
        NodeExit = m.FindNode("dummy exit");
        NodePilot = ProjectileFire.DetailNode(m, "dummy pilot");
        NodeMuzzle = m.FindNode("dummy muzzle");
        NodeEye = m.FindNode("dummy eye");
        // 0x00445a98, 0x00445b58: "dummy muzzle%d" and "dummy passenger%d" from 1 until a name is missing.
        for (int i = 1; i < 256; ++i)
        {
            int n = m.FindNode("dummy muzzle" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (n < 0)
                break;
            MuzzleNodes.Add(n);
        }
        for (int i = 1; i < 256; ++i)
        {
            int n = m.FindNode("dummy passenger" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (n < 0)
                break;
            PassengerNodes.Add(n);
        }
    }

    // src/game/vehicle.cpp Vehicle::getMountTransform: 0x00445c38. The original reads the passenger table without a range
    // check (D11): a point without a node gets the object transform.
    public override void GetMountTransform(int point, out Transform output)
    {
        int node = -1;
        if (point == 0)
            node = NodeExit;
        else if (point == 1)
            node = NodePilot;
        else if (point >= 2 && point - 2 < PassengerNodes.Count)
            node = PassengerNodes[point - 2];
        ShapeInstance? inst = node >= 0 ? ShapeState.ShapeInstanceOf(Game(), this) : null;
        if (inst == null || node >= inst.Entries.Length)
        {
            output = Transform;
            return;
        }
        using var chop = X87.Chop();
        Shape.TmatMul(inst.Entries[node], ToTMat(Transform), out TMat3 res);
        output = ToTransform(res);
    }

    // src/game/vehicle.cpp Vehicle::muzzleTransform: 0x00445cd8: the table entry fireCount % count, else the single muzzle
    // node.
    public bool MuzzleTransform(out Transform output)
    {
        int node = NodeMuzzle;
        if (MuzzleNodes.Count != 0)
            node = MuzzleNodes[(int)((uint)FireCount % (uint)MuzzleNodes.Count)];
        ShapeInstance? inst = node >= 0 ? ShapeState.ShapeInstanceOf(Game(), this) : null;
        if (inst == null || node >= inst.Entries.Length)
        {
            output = Transform;
            return false;
        }
        using var chop = X87.Chop();
        Shape.TmatMul(inst.Entries[node], ToTMat(Transform), out TMat3 res);
        output = ToTransform(res);
        return true;
    }

    // src/game/vehicle.cpp Vehicle::thrustVelocity: SimMovement_setLinearVelocity.
    protected void ThrustVelocity(ReadOnlySpan<float> v)
    {
        Velocity[0] = v[0];
        Velocity[1] = v[1];
        Velocity[2] = v[2];
        AtRest = false;
    }

    // src/game/vehicle.cpp Vehicle::update: vtable +0x1bc, Vehicle_update 0x00446400: energy recharge; the shape threads of
    // a server vehicle do not move.
    public virtual bool Update(ref Move move, float dt)
    {
        RechargeStep(dt);
        return true;
    }

    // src/game/vehicle.cpp Vehicle::serverTick: Vehicle_serverTick 0x0044575c.
    public override void ServerTick(uint tickMs)
    {
        using var chop = X87.Chop();
        Piloted = ControlClient != 0;
        if (Piloted)
        {
            if (SpeedT == 0.0f || SoundState == 2)
            {
                if (SpeedT == 0.0f && SoundState != 1)
                {
                    SoundState = 1;
                    SetMaskBits(0x10000);
                }
            }
            else
            {
                SoundState = 2;
                SetMaskBits(0x10000);
            }
        }
        else if (SoundState != 0)
        {
            SoundState = 0;
            SetMaskBits(0x10000);
        }
        RepairStep(kDt);
        ExpireCollisions();
        if (!Piloted || Credit < -25)
        {
            var zero = new Move();
            Update(ref zero, kDt);
            TicksWithoutMove = 0;
        }
        else
        {
            ++TicksWithoutMove;
            --Credit;
        }
    }

    // src/game/vehicle.cpp Vehicle::applyMoves: Vehicle_applyMoves 0x004463a4: the stored move is executed, then replaced
    // (one-move latency).
    public override void ApplyMoves(Move[] moves, int count)
    {
        for (int i = 0; i < count && Credit < 6; ++i)
        {
            ++Credit;
            Update(ref LastMove, kDt);
            LastMove = moves[i];
        }
        TicksWithoutMove = 0;
    }

    // src/game/vehicle.cpp Vehicle::setDamageLevel: Vehicle_setDamageLevel 0x004458c4: the StaticBase part, then the stop
    // at maxDamage.
    public override void SetDamageLevel(float level)
    {
        base.SetDamageLevel(level);
        SetMaskBits(0x100);
        if (Damage >= MaxDamage)
        {
            SpeedT = 0;
            SpeedF = 0;
            SetMaskBits(0x4000);
            SoundState = 0;
            SetMaskBits(0x10000);
        }
    }

    // src/game/vehicle.cpp Vehicle::fire: Vehicle_fire 0x00446660.
    public void Fire()
    {
        GameState gs = Game();
        if (Data.ProjGroup == -1 || Block() == null)
        {
            if (gs.Console != null)
            {
                string fn = Pj.FindMethod(gs, this, "onFire");
                if (fn.Length != 0)
                    Pj.CallMethod(gs, fn, new[] { Numbers.FormatInt(Id()) });
            }
            return;
        }
        SimManager? m = Manager();
        if (m == null || !(NextFireTime <= m.Time()))
            return;
        using var chop = X87.Chop();
        MuzzleTransform(out Transform xf);
        for (int i = 0; i < 3; ++i)
        {
            float lead = X87.F32(Mul(Velocity[i], 0.096f));
            xf.P[i] = X87.F32(Add(lead, xf.P[i]));
        }
        Projectile? pr = Projectile.Create(gs, Data.ProjGroup, Data.ProjIndex);
        if (pr == null)
            return; // D11: the original dereferences the null result
        pr.Init(xf, Velocity, Id());
        pr.RegisterWith(m);
        NextFireTime = X87.F32(Add(m.Time(), Data.ReloadDelay));
        ++FireCount;
        SetMaskBits(0x8000);
    }

    // src/game/vehicle.cpp Vehicle::blocker: Vehicle_onBlocker 0x0044642c (docs/collision.md E4.3).
    public bool Blocker(GameBase other)
    {
        using var chop = X87.Chop();
        GameState gs = Game();
        uint type = gs.World.Find(other)?.Type ?? 0u;
        if (type == 0x40000000u)
        {
            other.AtRest = false;
            return false;
        }
        // other.v += forward row * invMass (SimMovement_applyImpulse 0x00504b5c); a Player has its own mass.
        float[] fwd = { Transform.M[3], Transform.M[4], Transform.M[5] };
        float invMass = 1.0f;
        if (Player.From(other) is Player p)
            invMass = p.InvMass;
        else if (other.ItemData.Mass != 0)
            invMass = X87.F32(Div(1.0, other.ItemData.Mass));
        for (int i = 0; i < 3; ++i)
            other.Velocity[i] = X87.F32(Add(Mul(fwd[i], invMass), other.Velocity[i]));
        other.AtRest = false;
        if (!other.IsShapeBase())
            return true;
        if (Player.From(other) is Player pm && pm.MountId == Id())
            return false;
        float[] dv = new float[3];
        for (int i = 0; i < 3; ++i)
            dv[i] = X87.F32(Sub(Velocity[i], other.Velocity[i]));
        float s = X87.Lenf(dv);
        if (Data.MaxSpeed < s)
            s = Data.MaxSpeed;
        if (s < X87.F32(Mul(Data.MaxSpeed, 0.25f)))
            s = 0;
        float value = s == 0.0f ? 0.0f : X87.F32(Mul(Div(s, Data.MaxSpeed), Data.RamDamage));
        float[] zero = { 0, 0, 0 };
        other.ApplyDamage(Data.RamDamageType, value, Transform.P, Velocity, zero, Id());
        return true;
    }

    // src/game/vehicle.cpp Vehicle::pushBy: SimMovement_pushBy 0x005064f8 with this vehicle as the pushed object
    // (docs/vehicle-sim.md 12 HF9).
    public virtual bool PushBy(ReadOnlySpan<float> delta, ReadOnlySpan<float> mover) => ItemPushBy(delta, mover);

    // ---- vehicle_net.cpp ----

    // src/game/vehicle_net.cpp speedUnit: (value - minSpeed) / (maxSpeed - minSpeed) as the float handed to
    // BitStream::writeFloat.
    private static float SpeedUnit(VData d, float v) => X87.F32(Div(Sub(v, d.MinSpeed), Sub(d.MaxSpeed, d.MinSpeed)));

    // src/game/vehicle_net.cpp Vehicle::packUpdate: Vehicle_packUpdate 0x00446bec (docs/vehicle-sim.md 6.1).
    public uint PackUpdate(BitStream bs, int clientId, uint mask, bool isNew)
    {
        using var chop = X87.Chop();
        if (ControlClient == clientId && (mask & 0x05) == 0)
        {
            bs.WriteFlag(true); // the controlling client simulates its own vehicle
            return 0;
        }
        bs.WriteFlag(false);
        GhostClasses.PackShapeBasePart(this, bs, mask & ~0x18u);
        if (isNew)
        {
            // GameBase_packDataBlock (0x00447efc): bitLength(group count + 1) bits.
            uint n = (uint)DataBlockCount() + 1, bits = 1;
            while (bits < 32 && (n >> (int)bits) != 0)
                ++bits;
            bs.WriteInt((uint)DataBlockIndex, bits);
        }
        if (bs.WriteFlag((mask & 0x2000) != 0))
        {
            bs.WriteF32s(Rotation);
            bs.WriteF32s(Transform.P);
            if (bs.WriteFlag(!isNew && ControlClient != clientId))
            {
                Moves.PackMove(bs, Piloted ? LastMove : new Move(), false, null);
                Pj.WriteUnit(bs, SpeedUnit(Data, SpeedF), 10);
                bs.WriteF32(SpeedL);
                bs.WriteInt((uint)(TicksWithoutMove > 14 ? 15 : TicksWithoutMove), 4);
            }
        }
        if (bs.WriteFlag((mask & 0x4000) != 0))
        {
            Pj.WriteUnit(bs, SpeedUnit(Data, SpeedF), 10);
            Pj.WriteUnit(bs, SpeedUnit(Data, SpeedT), 10);
        }
        if (bs.WriteFlag((mask & 0x8000) != 0))
            bs.WriteInt((uint)FireCount & 3u, 2);
        if (bs.WriteFlag((mask & 0x100) != 0))
            bs.WriteInt(Pj.Ftol(Mul(Div(Damage, MaxDamage), 63.0f)) & 0x3fu, 6);
        if (bs.WriteFlag((mask & 0x10000) != 0))
            bs.WriteInt((uint)SoundState & 3u, 2);
        return 0;
    }

    // src/game/vehicle_net.cpp Vehicle::writeControlState: Vehicle_writeControlState 0x00446aa8 (docs/vehicle-sim.md 6.2).
    public override void WriteControlState(BitStream bs, ControlContext ctx)
    {
        using var chop = X87.Chop();
        bs.WriteInt((SensorWord >> 24) & 3u, 2); // GameBase flags byte: the sensor state (0044954c)
        bs.WriteF32s(Rotation);
        bs.WriteF32s(TargetRot);
        bs.WriteF32s(Transform.P);
        Pj.WriteUnit(bs, SpeedUnit(Data, SpeedF), 16);
        Pj.WriteUnit(bs, SpeedUnit(Data, SpeedT), 16);
        Pj.WriteUnit(bs, MaxEnergy != 0 ? X87.F32(Div(Energy, MaxEnergy)) : 0.0f, 16);
        bs.WriteInt(Pj.Ftol(Mul(Div(Damage, MaxDamage), 63.0f)) & 0x3fu, 6);
        bs.WriteInt((uint)FireCount & 3u, 2);
        bs.WriteInt((uint)SoundState & 3u, 2);
    }

    // The control state as the client reads it (docs/vehicle-sim.md 6.2, docs/client-net.md 4.4).
    public sealed class ClientControl
    {
        public readonly float[] Rot = new float[3], TargetRot = new float[3], Position = new float[3];
        public uint SpeedF16, SpeedT16, Energy16;                 // unit(16) raw
        public bool HasFlier;
        public float L, S;
        public bool Landed;
    }

    // Vehicle_readControlState 0x00446830 and Flier_readControlState 0x0043079c on the client copy: rotation, target
    // rotation, position and transform; the speeds and the energy from their unit(16) values; Flier L, S and landed, a
    // landed Flier keeps the position it had before the read. Damage, fire and sound state are not predicted.
    public void ApplyClientControlState(ClientControl c)
    {
        using var chop = X87.Chop();
        float[] old = { Transform.P[0], Transform.P[1], Transform.P[2] };
        SetRotation(c.Rot);
        for (int i = 0; i < 3; ++i)
            TargetRot[i] = c.TargetRot[i];
        SetPosition(c.Position);
        VData d = Data;
        float span = X87.F32(Sub(d.MaxSpeed, d.MinSpeed));
        SpeedF = X87.F32(Add(Mul(span, X87.F32(Div(c.SpeedF16, 65535.0))), d.MinSpeed));
        SpeedT = X87.F32(Add(Mul(span, X87.F32(Div(c.SpeedT16, 65535.0))), d.MinSpeed));
        Energy = X87.F32(Mul(X87.F32(Div(c.Energy16, 65535.0)), MaxEnergy));
        if (c.HasFlier)
        {
            SpeedL = c.L;
            SpeedS = c.S;
            Landed = c.Landed;
            if (c.Landed)
                SetPosition(old);
        }
    }

    // src/game/vehicle_net.cpp findVehicleMethod: GameBase_findScriptMethod (0x0044864c): the first defined function of
    // map name (when useMapName), data name, datablock className, C++ class, plain method name; "" when none.
    public static string FindVehicleMethod(GameState game, GameBase obj, string method, bool useMapName)
    {
        if (game.Console == null)
            return "";
        var candidates = new List<string>();
        if (useMapName && !string.IsNullOrEmpty(obj.MapName))
            candidates.Add(obj.MapName + "::" + method);
        if (obj.DataBlockName != null)
        {
            candidates.Add(obj.DataBlockName + "::" + method);
            if (game.Sim != null && game.Sim.DataBlocks().Find(obj.DataBlockName) is DataBlockManager.Block b &&
                game.Sim.DataBlocks().GetField(b, "className", "", out string cls) && cls.Length != 0)
                candidates.Add(cls + "::" + method);
        }
        candidates.Add(obj.ScriptClass() + "::" + method);
        candidates.Add(method);
        foreach (string fn in candidates)
            if (game.Console.HasFunction(fn))
                return fn;
        return "";
    }

    // src/game/vehicle_net.cpp packVehicleGhost.
    private static uint PackVehicleGhost(SimObject obj, GhostContext ctx, BitStream bs, uint mask) =>
        ((Vehicle)obj).PackUpdate(bs, ctx.ClientId, mask, mask == 0xffffffffu);

    // src/game/vehicle_net.cpp registerVehicleGhostClasses.
    public static void RegisterGhostClasses()
    {
        GhostManager.RegisterGhostClass("Flier", new GhostClass { Tag = 0x223, Pack = PackVehicleGhost });
        GhostManager.RegisterGhostClass("Vehicle", new GhostClass { Tag = 0x22c, Pack = PackVehicleGhost });
    }

    private sealed record Cmd(string Name, int Argc, string Usage, string BadArgc, string NotVehicle);

    // src/game/vehicle_net.cpp registerVehicleCommands: Vehicle:: commands (docs/vehicle-sim.md 7.1).
    public static void RegisterCommands(Interpreter interp, GameState g)
    {
        Cmd[] kCmds =
        {
            new("Vehicle::canMount", 3, "wrong argcount to canMount (Vehicle, Player)", "False", "False"),
            new("Vehicle::canRide", 3, "wrong argcount to canRide (Vehicle, Player)", "False", "False"),
            new("Vehicle::nextPassengerPoint", 2, "wrong argcount to nextPassengerPoint (Vehicle)", "-1", "-1"),
            new("Vehicle::incPassengers", 2, "wrong argcount to incPassengers (Vehicle)", "-1", "False"),
            new("Vehicle::decPassengers", 2, "wrong argcount to decPassengers (Vehicle)", "-1", "False"),
            new("Vehicle::getMountPoint", 3, "wrong argcount to getMountPoint (Vehicle, MountPoint)", "-1", "False"),
        };
        foreach (Cmd c in kCmds)
            interp.AddCommand(c.Name, (inr, a) =>
            {
                if (a.Length != c.Argc)
                {
                    inr.Print(1, c.Usage);
                    return c.BadArgc;
                }
                Vehicle? v = From(g.Players.FindServerObject(a[1]));
                if (v == null)
                    return c.NotVehicle;
                string name = a[0];
                if (GameUtil.EqualsNoCase(name, "Vehicle::canMount") || GameUtil.EqualsNoCase(name, "Vehicle::canRide"))
                {
                    // The second argument only has to be a ShapeBase.
                    GameBase? o = g.Players.FindServerObject(a[2]);
                    if (o == null || !o.IsShapeBase())
                        return "False";
                    if (GameUtil.EqualsNoCase(name, "Vehicle::canMount"))
                        return v.Piloted ? "False" : "True";
                    return v.Passengers < v.PassengerCount() ? "True" : "False";
                }
                if (GameUtil.EqualsNoCase(name, "Vehicle::nextPassengerPoint"))
                    return Numbers.FormatInt(v.Passengers + 2);
                if (GameUtil.EqualsNoCase(name, "Vehicle::incPassengers"))
                {
                    ++v.Passengers;
                    return "True";
                }
                if (GameUtil.EqualsNoCase(name, "Vehicle::decPassengers"))
                {
                    v.Passengers = v.Passengers > 1 ? v.Passengers - 1 : 0;
                    return "True";
                }
                v.GetMountTransform(Numbers.BorlandAtoi(a[2]), out Transform t);
                return Numbers.FormatFloat(t.P[0]) + " " + Numbers.FormatFloat(t.P[1]) + " " + Numbers.FormatFloat(t.P[2]);
            });
    }

    // src/game/vehicle.cpp makeFlier / makeVehicle: constructors for the class registry.
    public static SimObject MakeFlier(ClassContext ctx, IEnumerable<PersistDef> fields) => new Flier(ctx, fields);
    public static SimObject MakeVehicle(ClassContext ctx, IEnumerable<PersistDef> fields) => new Vehicle(ctx, "Vehicle", 9, fields);
}

// src/game/vehicle.hpp class Flier.
public sealed class Flier : Vehicle
{
    public const uint kHullMask = 0x34c00086u; // +0x1ec, Flier_onNewDataBlock

    public float Ceiling;      // +0x958
    public float Ground;       // +0x95c
    public readonly float[] Thrust = new float[3]; // +0x884
    public float Margin;       // +0x964
    public bool Braking, ActiveFlag; // +0x960, +0x961

    private readonly List<SimObject> mNotify = new(); // objects hit by the current move (info +0x3c)
    private Float9 mImageRot = Float9.Identity();     // rotation of the move's image transform

    // src/game/vehicle.cpp Flier::Flier.
    public Flier(ClassContext ctx, IEnumerable<PersistDef> fields) : base(ctx, "Flier", 10, fields) { }

    // src/game/vehicle.cpp Flier::onNewDataBlock: Flier_onNewDataBlock 0x0042e0c0: the Vehicle part, then the collision
    // radius and the ceiling.
    public override void OnNewDataBlock()
    {
        base.OnNewDataBlock();
        ShapeRadius = X87.F32(Mul(ShapeRadius, 0.25f)); // +0x85c (collision image sphere), after the step length
        UpdateMaxAltitude();
    }

    // src/game/vehicle.cpp Flier::updateMaxAltitude: Flier_updateMaxAltitude 0x0042e52c: the grid file height range of the
    // terrain container.
    private void UpdateMaxAltitude()
    {
        GameState gs = Game();
        Container.Entry? te = gs.World.TerrainOf(this);
        if (te == null || te.TerrainData == null)
            return;
        using var chop = X87.Chop();
        float hmin = te.TerrainData.Hmin(), hmax = te.TerrainData.Hmax();
        Ceiling = X87.F32(Add(Sub(hmax, Mul(hmin, 0.5f)), Add(hmax, Data.MaxAlt)));
    }

    // src/game/vehicle.cpp Flier::serverTick: Flier_serverTick 0x0042e4e0.
    public override void ServerTick(uint tickMs)
    {
        base.ServerTick(tickMs);
        if (Piloted && 0.0f < SpeedL && SoundState != 2)
        {
            SoundState = 2;
            SetMaskBits(0x10000);
        }
    }

    // src/game/vehicle.cpp Flier::setDamageLevel.
    public override void SetDamageLevel(float level) => base.SetDamageLevel(level);

    // src/game/vehicle.cpp Flier::updateGround: Flier_updateGround 0x0042e5c4 (docs/vehicle-sim.md 3.5).
    public void UpdateGround()
    {
        GameState gs = Game();
        float px = Transform.P[0], py = Transform.P[1];
        float pz = Transform.P[2];
        float h = -1.0e30f;
        Container.Entry? te = gs.World.TerrainOf(this);
        if (te != null && te.TerrainData != null)
        {
            float height = 0;
            if (te.TerrainData.Height(px, py, ref height))
                h = height;
        }
        float hr = -1.0e30f;
        bool counted = false;
        {
            object? hitObj = null;
            Vec3 hit = default, normal = default;
            var from = new Vec3(px, py, pz);
            var to = new Vec3(px, py, X87.F32(Sub(pz, 5.0f)));
            if (gs.World.CastRay(from, to, 0x20800004u, this, ref hitObj, ref hit, ref normal))
            {
                bool zeroSpeed = false;
                Container.Entry? he = hitObj != null ? gs.World.Find(hitObj) : null;
                if (he != null && he.Type == 0x20000000u)
                {
                    var gb = hitObj as GameBase;
                    if (gb != null && gb.ClassName() == "Flier")
                    {
                        // Flier_getVelocity 0x0042e8f8: (0, F, L) rotated; side speed is not part of it. The length is
                        // m_Point3F_lenf, whose bit-shift square root gives 0x1fc00000 for a zero vector: the test against
                        // 0.0 never holds, so every vehicle under the ray counts as ground.
                        var f = (Flier)gb;
                        var m = new Transform();
                        MissionTransform.EulerToMatrix(f.Rotation, ref m);
                        float[] inv = { 0, f.SpeedF, f.SpeedL };
                        float[] v = new float[3];
                        X87.MulVector(inv, m.M, v);
                        zeroSpeed = X87.Lenf(v) == 0.0f;
                    }
                    else if (gb != null)
                    {
                        zeroSpeed = X87.Lenf(gb.Velocity) == 0.0f;
                    }
                }
                if (!zeroSpeed)
                {
                    hr = hit.Z;
                    counted = true;
                }
            }
        }
        float gnd = h;
        if (counted && h < hr)
            gnd = hr;
        if ((!Piloted || SpeedF < Mul(kThird, Data.MaxSpeed)) && Damage < MaxDamage)
            gnd = X87.F32(Add(gnd, 2.0f));
        if (gnd < pz || SpeedF != 0.0f)
        {
            if (gnd < Sub(pz, Margin))
                Landed = false;
        }
        else
        {
            Landed = true;
            if (!Piloted)
            {
                float[] e = { 0, 0, Rotation[2] };
                SetRotation(e);
            }
            pz = gnd;
            float[] np = { px, py, pz };
            SetPosition(np);
            gs.ObjectMoved(this); // SimObject_setTransformRaw 0x005054a4: the world box follows at once
            SetMaskBits(0x2000);
        }
        Ground = gnd;
    }

    // src/game/vehicle.cpp Flier::updateThrust: Flier_updateThrust 0x0042e9b0 (docs/vehicle-sim.md 3.4).
    public void UpdateThrust(float k)
    {
        UpdateGround();
        VData d = Data;
        if (!Landed && SpeedF != 0.0f)
        {
            SpeedL = X87.F32(Sub(SpeedL, Mul(d.Lift, k)));
            SpeedL = X87.F32(Sub(SpeedL, Mul(Mul(Div(SpeedF, d.MaxSpeed), SpeedL), k)));
            if (d.MaxVertical < SpeedL)
                SpeedL = d.MaxVertical;
        }
        else
        {
            SpeedL = X87.F32(Sub(SpeedL, Mul(d.Lift, k)));
            if (Landed && SpeedL < 0.0f)
                SpeedL = 0;
        }
        float alt = X87.F32(Sub(Transform.P[2], Ground));
        float floorL = X87.F32(Mul(d.MaxVertical, -1.75f));
        float fl = floorL;
        if (alt < 4.0f)
            fl = X87.F32(Mul(Mul(alt, 0.25f), floorL));
        if (SpeedL < fl)
            SpeedL = fl;
        if (Ceiling < Transform.P[2] && SpeedL != 0.0f)
            SpeedL = 0;
        var m = new Transform();
        MissionTransform.EulerToMatrix(Rotation, ref m);
        float[] inv = { SpeedS, SpeedF, SpeedL };
        X87.MulVector(inv, m.M, Thrust);
        if (Damage < MaxDamage)
            ThrustVelocity(Thrust);
    }

    // src/game/vehicle.cpp Flier::moveStep: ShapeBase_moveStep 0x0044c644 for a Flier: forces, target, stepped move
    // (docs/vehicle-sim.md 3.6).
    public void MoveStep(float dt)
    {
        GameState gs = Game();
        float slice = dt;
        float[] oldV = { Velocity[0], Velocity[1], Velocity[2] };
        float[] gravity = { 0, 0, 0 };
        float drag = 0;
        bool inContainer = gs.TerrainForces(this, gravity, ref drag);
        if (inContainer)
            for (int i = 0; i < 3; ++i)
                Velocity[i] = X87.F32(Add(Mul(gravity[i], slice), Velocity[i]));
        if (inContainer && drag != 0.0f)
        {
            float s = X87.F32(Mul(X87.F32(Mul(Data.Drag, drag)), slice));
            for (int i = 0; i < 3; ++i)
                Velocity[i] = X87.F32(Sub(Velocity[i], Mul(Velocity[i], s)));
        }
        float half = X87.F32(Mul(slice, 0.5f));
        float[] target = new float[3];
        for (int i = 0; i < 3; ++i)
            target[i] = X87.F32(Add(Mul(Add(oldV[i], Velocity[i]), half), Transform.P[i]));
        // The new rotation goes into the image transform of the move (docs/collision.md 3 step 4.1); the object's own
        // matrix keeps the old rotation until the move commits, so a script called from the collision callback
        // (onDamage, onDestroyed) reads the rotation of the previous update.
        var image = new Transform();
        MissionTransform.EulerToMatrix(Rotation, ref image);
        mImageRot = image.M;
        // The matrix flags of the object are untouched until a sub-step is free: a Flier that has just landed
        // (setPosition with a zero rotation: identity flags) reads "0 0 0" in a callback of its first contact.
        if (SteppedMove(target, ItemSubStep, X87.F32(Mul(ItemSubStep, 0.3f)), slice))
            return;
        // SimMovement_moveTo (0x00505720 -> 0x00505814) after a move blocked in every pass: the image is tested where it
        // stands with a fresh candidate list; a hit runs the collision callback again.
        ItemContact = false;
        AtRest = false;
        var candidates = new List<CollisionImage>();
        var hits = new List<CollisionHit>();
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        HullCandidates(p, candidates, 0x34c00086u);
        bool rest = true;
        for (int round = 0; round < 3 && rest; ++round)
        {
            p[0] = Transform.P[0];
            p[1] = Transform.P[1];
            p[2] = Transform.P[2];
            rest = !TestAt(p, candidates, hits) && ProcessCollision(hits, round);
        }
        var notified = new List<SimObject>(mNotify);
        mNotify.Clear();
        foreach (SimObject other in notified)
        {
            PostCollision(other, this);
            PostCollision(this, other);
        }
        if (rest)
        {
            AtRest = true;
            Velocity[0] = Velocity[1] = Velocity[2] = 0;
        }
    }

    // src/game/vehicle.cpp Flier::update: Flier_update 0x0042ebd4 (docs/vehicle-sim.md 3.3).
    public override bool Update(ref Move move, float dt)
    {
        if (!base.Update(ref move, dt))
            return false;
        using var chop = X87.Chop();
        VData d = Data;
        float k = X87.F32(Mul(kTick, dt));
        float oldT = SpeedT, oldF = SpeedF;
        double hv = Mul(d.MaxVertical, 0.5f);
        float roll0 = X87.F32(Sub(Rotation[1], Mul(Div(SpeedS, hv), kPi32)));
        Braking = ActiveFlag = false;
        float a = X87.F32(Sub(1.0, Div(SpeedF, d.MaxSpeed)));
        if (a < kFloor033)
            a = BitConverter.UInt32BitsToSingle(0x3ea8f5c3u); // f32 0.33 at the compare
        float pitchIn = X87.F32(Mul(Mul(Mul(-(double)move.Pitch, a), 0.25f), k));
        float yawIn = X87.F32(Mul(Mul(Mul(move.Turn, a), 0.25f), k));

        // Fire and the jump (dismount) are server only (Flier_update 0x0042ebd4).
        if (move.Fire && !ClientSide)
            Fire();
        if (move.Jump && !ClientSide)
        {
            var m = new Transform();
            MissionTransform.EulerToMatrix(Rotation, ref m);
            float[] inv = { 0, X87.F32(Sub(Mul(SpeedF, 0.5f), 40.0f)), 40.0f };
            float[] mom = new float[3];
            X87.MulVector(inv, m.M, mom);
            GameState gs = Game();
            string fn = gs.VirtualFunction(this, "jump");
            if (fn.Length != 0 && gs.Console != null)
                gs.Console.CallFunction(fn, new[] { Numbers.FormatInt(Id()), G(mom[0]) + " " + G(mom[1]) + " " + G(mom[2]) });
            move.Forward = move.Backward = 0;
            move.Jet = move.Jump = move.Fire = false;
            SetMaskBits(0x2000);
        }

        if (move.Forward == 0.0f)
        {
            SpeedT = 0;
        }
        else
        {
            SpeedT = d.MaxSpeed;
            ActiveFlag = true;
        }
        if (move.Backward != 0.0f)
        {
            Braking = ActiveFlag = true;
            SpeedT = d.MinSpeed;
            if (d.MinSpeed != 0.0f)
                Landed = false;
        }
        if (move.Jet)
        {
            SpeedL = X87.F32(Add(Mul(k, 1.25f), SpeedL));
            if (d.MaxVertical < SpeedL)
                SpeedL = d.MaxVertical;
            Landed = false;
            ActiveFlag = true;
        }
        if (!Landed)
        {
            float h = X87.F32(hv);
            if (move.Right != 0.0f)
            {
                SpeedS = X87.F32(Add(Mul(k, 0.25f), SpeedS));
                if (h < SpeedS)
                    SpeedS = h;
            }
            else if (move.Left != 0.0f)
            {
                SpeedS = X87.F32(Sub(SpeedS, Mul(k, 0.25f)));
                if (SpeedS < -h)
                    SpeedS = -h;
            }
        }
        if (SpeedS != 0.0f && (Landed || (move.Left == 0.0f && move.Right == 0.0f)))
        {
            float step = SpeedS <= 0.0f ? -0.25f : 0.25f;
            SpeedS = X87.F32(Sub(SpeedS, Mul(step, k)));
            if (SpeedS < 0.25f && -0.25f < SpeedS)
                SpeedS = 0;
        }
        if (!Piloted)
        {
            SpeedT = 0;
            TargetRot[0] = TargetRot[1] = 0;
        }
        if (SpeedT < d.MinSpeed)
            SpeedT = d.MinSpeed;
        if (d.MaxSpeed < SpeedT)
            SpeedT = d.MaxSpeed;

        float[] p0 = { Transform.P[0], Transform.P[1], Transform.P[2] };
        float rx = Rotation[0], ry = roll0, rz = Rotation[2];
        float[] r0 = { Rotation[0], roll0, Rotation[2] };
        float T = SpeedT;
        if (T < SpeedF)
        {
            SpeedF = X87.F32(Sub(SpeedF, Mul(Mul(d.Accel, k), 0.5f)));
            if (!Piloted || Braking)
                SpeedF = X87.F32(Sub(SpeedF, Mul(d.Accel, k)));
            if (SpeedF < T)
                SpeedF = T;
        }
        if (SpeedF < T)
        {
            SpeedF = X87.F32(Add(Mul(d.Accel, k), SpeedF));
            if (T < SpeedF)
                SpeedF = T;
        }
        if (Ceiling < p0[2] && pitchIn < 0.0f)
            pitchIn = 0;
        if (!Landed || SpeedF != 0.0f)
        {
            rx = X87.F32(Sub(rx, pitchIn));
            ry = X87.F32(Sub(ry, Mul(yawIn, 0.5f)));
        }
        // Soft limits: roll against maxBank, then pitch against maxPitch.
        void Soft(ref float v, float lim)
        {
            if (v < -lim)
            {
                float e = X87.F32(Sub(v, -lim));
                v = X87.F32(Sub(v, Mul(Mul(e, 0.5f), k)));
            }
            if (lim < v)
            {
                float e = X87.F32(Sub(v, lim));
                v = X87.F32(Sub(v, Mul(Mul(e, 0.5f), k)));
            }
        }
        Soft(ref ry, d.MaxBank);
        Soft(ref rx, d.MaxPitch);
        rz = X87.F32(Sub(rz, Mul(Mul(kYawPerBank, ry), k)));
        ry = X87.F32(Add(Mul(Div(SpeedS, hv), kPi32), ry));
        if (Piloted)
        {
            TargetRot[0] = rx;
            TargetRot[1] = ry;
            TargetRot[2] = rz;
        }
        if (Landed && SpeedF == 0.0f)
            TargetRot[0] = TargetRot[1] = 0;
        if (Ceiling < p0[2])
            TargetRot[0] = 0;
        double rate = Mul(k, kLevelRate);
        void Level(ref float v, float t)
        {
            if (t <= v)
            {
                if (t < v)
                {
                    v = X87.F32(Sub(v, rate));
                    if (v < t)
                        v = t;
                }
            }
            else
            {
                v = X87.F32(Add(rate, v));
                if (t < v)
                    v = t;
            }
        }
        Level(ref rx, TargetRot[0]);
        Level(ref ry, TargetRot[1]);

        float[] er = { rx, ry, rz };
        SetRotation(er);
        UpdateThrust(k);
        float[] before = { Transform.P[0], Transform.P[1], Transform.P[2] };
        MoveStep(dt);
        if (!Game().MoveableDisplace(this, before))
        {
            SpeedF = 0;
            SpeedT = 0;
            SpeedL = 0;
        }
        static bool Moved(ReadOnlySpan<float> a3, ReadOnlySpan<float> b3)
        {
            for (int i = 0; i < 3; ++i)
            {
                float dlt = X87.F32(Sub(a3[i], b3[i]));
                if (kTiny <= Mul(dlt, dlt))
                    return true;
            }
            return false;
        }
        if (Moved(p0, Transform.P) || Moved(r0, Rotation))
            SetMaskBits(0x2000);
        if (oldT != SpeedT || oldF != SpeedF)
            SetMaskBits(0x4000);
        return true;
    }

    // ---- stepped move and collision (docs/vehicle-sim.md 4, docs/collision.md E4) ----

    // src/game/vehicle.cpp contactNormal: surface normal in world space of one contact (the hit rotation; terrain surfaces
    // are world space).
    private static void ContactNormal(CollisionHit hit, in CollisionSurface s, Span<float> output)
    {
        if ((hit.Type & ObjectType.kTerrainType) != 0)
        {
            output[0] = s.Normal[0];
            output[1] = s.Normal[1];
            output[2] = s.Normal[2];
        }
        else
        {
            Float3 n = s.Normal;
            X87.MulVector(n, hit.Rot, output);
        }
    }

    // src/game/vehicle.cpp Flier::hullCandidates: SimMovement_sweptBox 0x00506a84 -> NetObject_computeWorldBox 0x005069fc:
    // StaticBase_ctor sets bit 0x40 of +0x164, so the object box is turned by the vehicle's matrix (m_Box3F_transform)
    // before it is extended by v * dt. An Item has the bit clear and only translates its box.
    public void HullCandidates(ReadOnlySpan<float> target, List<CollisionImage> output, uint mask)
    {
        var m = new Mat();
        m.M = mImageRot;
        m.P = new Vec3(target[0], target[1], target[2]);
        Span<float> box = stackalloc float[6], query = stackalloc float[6];
        Shape.RotatedWorldBox(ShapeQueryBox, m, box);
        float[] zero = { 0, 0, 0 };
        Collision.SweptBox(box[..3], box[3..], zero, Velocity, 0.032f, query);
        Game().CollisionCandidates(this, mask, query, output);
    }

    // src/game/vehicle.cpp Flier::testAt: SimMovement_testImages 0x00506bfc (docs/collision.md 2.3). The image of a dead
    // Player has its byte +0xd set (Player kill, docs/player-sim.md 6.5 step 9) and that byte is read at every test: a
    // corpse that the hull reaches gets one event 0x41f, leaves the candidate list (the last candidate takes its place)
    // and never blocks, also when it died during this move.
    public bool TestAt(ReadOnlySpan<float> q, List<CollisionImage> candidates, List<CollisionHit> hits)
    {
        bool free;
        var triggered = new List<SimObject>();
        {
            // The image transform of the running move in place of the object matrix, for one test.
            Float9 saved = Transform.M;
            Transform.M = mImageRot;
            try
            {
                free = ItemTestAt(q, candidates, hits, triggered);
            }
            finally
            {
                Transform.M = saved;
            }
        }
        // The trigger sphere of a Mine, a station or a door that the hull reaches: one event 0x41f to its object
        // (SimMovement_postTriggerEvent 0x005070fc), and the image is out of this move's candidate list.
        foreach (SimObject o in triggered)
            PostCollision(o, this);
        if (free)
            return true;
        for (int i = 0; i < hits.Count;)
        {
            if (Player.From(hits[i].Object as SimObject) is not Player p || !p.Dead)
            {
                ++i;
                continue;
            }
            var corpse = (SimObject)hits[i].Object!;
            int c = candidates.FindIndex(im => im.Object == corpse);
            object? last = null;
            if (c >= 0)
            {
                if (c + 1 != candidates.Count)
                    last = candidates[^1].Object;
                candidates[c] = candidates[^1];
                candidates.RemoveAt(candidates.Count - 1);
            }
            hits.RemoveAt(i);
            // The candidate that took the place is tested next: its hit follows here.
            if (last != null && hits.Count != 0 && hits[^1].Object == last && i < hits.Count - 1)
            {
                CollisionHit moved = hits[^1];
                hits.RemoveAt(hits.Count - 1);
                hits.Insert(i, moved);
            }
            PostCollision(corpse, this);
        }
        return hits.Count == 0;
    }

    // src/game/vehicle.cpp Flier::pushBy: SimMovement_pushBy 0x005064f8: bits 0x05 of +0x164 are cleared and stay cleared
    // (a pushed vehicle answers every later contact without friction), the collision mask is 6 for the stepped moves and
    // the image transform is the object's own matrix.
    public override bool PushBy(ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        using var chop = X87.Chop();
        ItemFlags &= ~5;
        mImageRot = Transform.M;
        return Displace.PushBy(delta, mover, ShapeQueryBox, Velocity, ref AtRest, i => Transform.P[i],
                               (target, step0, step1) => SteppedMove(target, step0, step1, 0.032f, 6));
    }

    // src/game/vehicle.cpp Flier::steppedMove: SimMovement_stepMove 0x00505a3c with the Flier collision callback (vtable
    // +0xb8), at most three passes. The image is the ShapeCollisionImage in mesh mode: its bounding sphere (radius *
    // 0.25) against terrain and other shapes, its box against interiors, and its mesh against the sphere of a Player
    // (docs/collision.md T3).
    public bool SteppedMove(float[] target, float step0, float step1, float slice, uint mask = kHullMask)
    {
        GameState gs = Game();
        ItemContact = false;
        AtRest = false;
        var candidates = new List<CollisionImage>();
        var hits = new List<CollisionHit>();
        HullCandidates(target, candidates, mask);
        float step = step0;
        float used = 0;
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        void Commit(ReadOnlySpan<float> at)
        {
            for (int i = 0; i < 3; ++i)
                Transform.P[i] = at[i];
            Transform.Flags |= 3;
        }
        // The image transform is the working position of the move (image +0x10): every pass starts it at the target
        // rotation with the matrix flags 3. A collision image query on this vehicle from inside the callback copies the
        // object's transform over it: the sub-step that was being tried is lost, and a move that ends in that pass leaves
        // the object where it was, with the rotation and matrix flags it had.
        Container.Entry? self = gs.World.Find(this);
        Float9 targetRot = mImageRot;
        bool imageReset = false;
        uint resetFlags = 0;
        bool blocked = true;
        for (int iteration = 0; iteration < 3; ++iteration)
        {
            mImageRot = targetRot;
            imageReset = false;
            float[] delta = new float[3], dd = new float[3];
            for (int i = 0; i < 3; ++i)
                delta[i] = X87.F32(Sub(target[i], p[i]));
            float l = X87.F32(X87.Len(delta));
            double q = Div(l, step);
            double up = Math.Ceiling(q);
            long n = double.IsNaN(up) ? 0 : up >= 9.2e18 ? long.MaxValue : up <= -9.2e18 ? long.MinValue : (long)up;
            if (n > 100000)
                n = 100000;
            bool callbackTrue = false;
            if (n != 0)
            {
                float k = X87.F32(Div(1.0, Store(n)));
                for (int i = 0; i < 3; ++i)
                    dd[i] = X87.F32(Mul(k, delta[i]));
                float[] qpos = { p[0], p[1], p[2] };
                for (long i = 0; i < n; ++i)
                {
                    for (int j = 0; j < 3; ++j)
                        qpos[j] = X87.F32(Add(dd[j], qpos[j]));
                    if (TestAt(qpos, candidates, hits))
                    {
                        for (int j = 0; j < 3; ++j)
                            p[j] = qpos[j];
                        Commit(p);
                        continue;
                    }
                    float sliceT = X87.F32(Div(Sub(slice, used), Store(n)));
                    used = X87.F32(Add(Mul(Store(i), sliceT), used));
                    // The callback sees the object where the last free sub-step left it, matrix flags included.
                    uint queries = self != null ? self.ImageQueries : 0;
                    bool handled = ProcessCollision(hits, iteration);
                    if (self != null && (self = gs.World.Find(this)) != null && self.ImageQueries != queries)
                    {
                        imageReset = true;
                        resetFlags = Transform.Flags;
                        mImageRot = Transform.M;
                        for (int j = 0; j < 3; ++j)
                            qpos[j] = Transform.P[j];
                    }
                    if (handled)
                    {
                        float left = X87.F32(Sub(slice, used));
                        for (int j = 0; j < 3; ++j)
                        {
                            float tv = X87.F32(Mul(left, Velocity[j]));
                            target[j] = X87.F32(Add(p[j], tv));
                        }
                        callbackTrue = true;
                        break;
                    }
                    for (int j = 0; j < 3; ++j)
                        p[j] = qpos[j];
                    Commit(p);
                }
            }
            if (callbackTrue)
            {
                step = step1;
                continue;
            }
            Commit(p);
            blocked = false;
            break;
        }
        _ = step;
        // Collision events to both sides for every notified object (0x00507180).
        var notified = new List<SimObject>(mNotify);
        mNotify.Clear();
        foreach (SimObject other in notified)
        {
            PostCollision(other, this);
            PostCollision(this, other);
        }
        // SimObject_setTransformRaw 0x005054a4 with the image transform: the new rotation becomes the object's.
        Transform.M = mImageRot;
        if (!blocked && imageReset)
            Transform.Flags = resetFlags;
        if (blocked)
        {
            // The object's own transform is written back: the flags stay as the sub-steps left them.
            AtRest = true;
            Velocity[0] = Velocity[1] = Velocity[2] = 0;
            for (int i = 0; i < 3; ++i)
                Transform.P[i] = p[i];
            gs.ObjectMoved(this);
            return false;
        }
        gs.ObjectMoved(this);
        return true;
    }

    // src/game/vehicle.cpp Flier::postCollision: event 0x41f (collision.md 8 E1): queued at the manager's current time;
    // dropped when the sender is gone by then.
    private void PostCollision(SimObject? target, SimObject? sender)
    {
        SimManager? m = Manager();
        if (m == null || target == null || sender == null)
            return;
        GameState gs = Game();
        int senderId = sender.Id();
        m.PostEvent(target, m.Time(), () =>
        {
            if (m.FindById(senderId) != sender)
                return;
            if (target is GameBase t && sender is GameBase s)
                gs.DispatchCollisionEvent(t, s);
        }, sender);
    }

    // src/game/vehicle.cpp Flier::processCollision: Flier_processCollision 0x0042f7c4. Approximate parts: the image test
    // (VQ1, VQ3).
    public bool ProcessCollision(List<CollisionHit> hits, int iteration)
    {
        using var chop = X87.Chop();
        GameState gs = Game();
        float maxSpeed = Data.MaxSpeed;
        // C1: Players mounted here and Mines are not hit.
        for (int i = 0; i < hits.Count;)
        {
            bool drop = (hits[i].Type & 0x4000000u) != 0;
            if (Player.From(hits[i].Object as SimObject) is Player p)
                drop = drop || p.MountId == Id();
            if (drop)
                hits.RemoveAt(i);
            else
                ++i;
        }
        foreach (CollisionHit h in hits)
            if (h.Object is SimObject so && !mNotify.Contains(so))
                mNotify.Add(so);
        float[] n = { 0, 0, 0 };
        bool groundHit = false;
        foreach (CollisionHit h in hits)
        {
            var other = h.Object as GameBase;
            if ((h.Type & TypeMask) != 0 && other != null && other.IsShapeBase())
            {
                // C2: ram damage.
                float[] dv = new float[3];
                for (int i = 0; i < 3; ++i)
                    dv[i] = X87.F32(Sub(Velocity[i], other.Velocity[i]));
                float s = X87.Lenf(dv);
                if (maxSpeed < s)
                    s = maxSpeed;
                if ((h.Type & ObjectType.kPlayerType) == 0)
                    SpeedF = SpeedT = SpeedL = 0;
                if (s < X87.F32(Mul(maxSpeed, 0.25f)))
                    s = 0;
                float value = s == 0.0f ? 0.0f : X87.F32(Mul(Div(s, maxSpeed), Data.RamDamage));
                float[] zero = { 0, 0, 0 };
                float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
                float[] vel = { Velocity[0], Velocity[1], Velocity[2] };
                if (Damage < MaxDamage)
                    other.ApplyDamage(Data.RamDamageType, value, pos, vel, zero, Id());
                ApplyDamage(-1, X87.F32(Mul(value, 0.5f)), pos, vel, zero, Id());
            }
            else if ((h.Type & (ObjectType.kTerrainType | ObjectType.kInteriorType)) != 0)
            {
                // C3: contact normals of terrain and interior hits.
                groundHit = true;
                foreach (CollisionSurface su in h.Surfaces)
                {
                    float[] wn = new float[3];
                    ContactNormal(h, su, wn);
                    for (int i = 0; i < 3; ++i)
                        n[i] = X87.F32(Add(wn[i], n[i]));
                }
            }
        }
        float sp = X87.Lenf(Velocity);
        if (maxSpeed < sp)
            sp = maxSpeed;
        bool nonzeroN = n[0] != 0.0f || n[1] != 0.0f || n[2] != 0.0f;
        bool nonzeroV = Velocity[0] != 0.0f || Velocity[1] != 0.0f || Velocity[2] != 0.0f;
        if (groundHit && nonzeroN && nonzeroV)
        {
            float[] u = { Velocity[0], Velocity[1], Velocity[2] };
            Normalise(n);
            Normalise(u);
            float dd = X87.F32(X87.Dot(n, u));
            if (dd < 0.0f)
            {
                // C5
                if (sp < Mul(kThird, maxSpeed))
                    sp = 0;
                float D = X87.F32(Mul(Mul(Div(sp, maxSpeed), MaxDamage), Data.GroundDamageScale));
                if (dd <= -0.25f || SpeedF <= X87.F32(Mul(maxSpeed, 0.25f)))
                {
                    SpeedF = 0;
                    SpeedT = 0;
                }
                else
                {
                    // C6: glance. Reflected direction, orientation turned toward it.
                    float negd = -dd;
                    float[] c = { X87.F32(Mul(n[0], negd)), X87.F32(Mul(n[1], negd)), X87.F32(Mul(n[2], negd)) };
                    float[] rv = new float[3];
                    for (int i = 0; i < 3; ++i)
                    {
                        float twice = X87.F32(Mul(c[i], 2.0f));
                        rv[i] = X87.F32(Add(u[i], twice));
                    }
                    float before = Rotation[0];
                    ThrustVelocity(rv);
                    // The frame (right, forward, up) of the new direction (m_cross, normalised).
                    float[] fwd = { rv[0], rv[1], rv[2] };
                    Normalise(fwd);
                    float[] upRef = { 0, 0, 1 };
                    if (fwd[0] == 0.0f && fwd[1] == 0.0f)
                    {
                        upRef[0] = 1;
                        upRef[2] = 0;
                    }
                    float[] right = new float[3], up = new float[3];
                    Cross(fwd, upRef, right);
                    Normalise(right);
                    Cross(right, fwd, up);
                    Normalise(up);
                    var f = new Transform();
                    f.Flags = 3;
                    for (int i = 0; i < 3; ++i)
                    {
                        f.M[i] = right[i];
                        f.M[3 + i] = fwd[i];
                        f.M[6 + i] = up[i];
                    }
                    float[] e = new float[3];
                    MissionTransform.MatrixToEuler(f, e);
                    TargetRot[0] = e[0];
                    TargetRot[1] = e[1];
                    TargetRot[2] = e[2];
                    TargetRot[0] = X87.F32(Add(Mul(kTwoThirds, Sub(TargetRot[0], before)), before));
                    SetRotation(TargetRot);
                    SpeedF = X87.F32(Mul(SpeedF, 0.8f));
                    SpeedT = X87.F32(Mul(SpeedT, 0.8f));
                }
                SetMaskBits(0x4000);
                if (D != 0.0f)
                {
                    float dm = dd;
                    if (dm < -1.0f)
                        dm = -1.0f;
                    float value = X87.F32(Mul(-Sub(dm, 0.125f), D));
                    float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
                    ApplyDamage(-1, value, pos, u, u, Id());
                    SetMaskBits(0x100);
                }
            }
        }
        SetMaskBits(0x2000);
        // C8: the default response (0x00506d80) on the remaining hits; passes after the first drop friction (0x01) and
        // 0x20.
        int flags = iteration != 0 ? (ItemFlags & ~0x21) : ItemFlags;
        bool handledAny = false;
        foreach (CollisionHit h in hits)
            foreach (CollisionSurface s2 in h.Surfaces)
            {
                if (!(s2.Distance >= 0))
                    continue;
                float[] wn = new float[3];
                ContactNormal(h, s2, wn);
                Material? mat = null;
                if (s2.Material >= 0 && (h.Type & ObjectType.kTerrainType) != 0)
                    mat = gs.TerrainMaterial(s2.Material);
                if (SurfaceResponse(wn, mat, flags))
                    handledAny = true;
            }
        return handledAny;
    }

    // src/game/vehicle_net.cpp Flier::writeControlState: 0x0043084c.
    public override void WriteControlState(BitStream bs, ControlContext ctx)
    {
        base.WriteControlState(bs, ctx);
        bs.WriteF32(SpeedL);
        bs.WriteF32(SpeedS);
        bs.WriteFlag(Landed);
    }
}
