// Ghost shape visuals (docs/shapes-render.md 2.1, 3.5): the object classes that draw one shape (StaticShape, Turret,
// Sensor, Moveable, Item, Mine, Vehicle, Flier) with the three ShapeBase thread slots, damage skins, fade and the
// destroy effects.
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;

namespace OpenTribes.Client.Scene;

public abstract class Visual
{
    public int Index;
    public string Class = "";
    public ClientGhost Ghost = null!;
    protected SceneModel M = null!;

    public virtual void Init(SceneModel m, ClientGhost g)
    {
        M = m;
        Ghost = g;
        Index = g.Index;
        Class = g.Class;
    }

    public abstract void OnUpdate(DecodedGhost u, bool isNew);
    public virtual void Tick(double dt) { }
    public virtual void Build(FrameContext ctx) { }
    public virtual void OnDelete() { }
    public virtual (double x, double y, double z)? Position => null;
    // The world box of the object (min xyz, max xyz) used to file it in a container interior, null when unknown.
    public virtual double[]? WorldBox => null;
    // Where a beam or projectile of this object starts: the muzzle (4.7).
    public virtual (double x, double y, double z)? MuzzlePoint => Position;

    protected double Num(string key, double def = 0) => Ghost.State.TryGetValue(key, out object? v) ? Db.ToDouble(v, def) : def;
    protected bool Has(string key) => Ghost.State.ContainsKey(key);
    protected double[] Vec(string key) => Ghost.State.TryGetValue(key, out object? v) ? Db.Vec(v) : new double[3];
}

public class ShapeVisual : Visual
{
    protected RenderShape? Shape;
    protected RenderInstance? Inst;
    protected ClientDataBlock? Block;
    protected int Group;
    protected int BlockIndex = -1;
    protected readonly RenderThread?[] Slots = new RenderThread?[3];
    protected readonly int[] SlotSeq = { -1, -1, -1 };
    protected readonly bool[] SlotRunning = new bool[3];
    protected RenderThread? Ambient, Visibility, Turn, Elevate, TurretPower;
    // Turret_onNewDataBlock 0x00452970: the mode driven power / fire thread (+0x8a4) and its sequences.
    protected TurretGhostPose? TurretPose;
    private int mPowerSeq = -1, mFireSeq = -1;
    protected double X, Y, Z, Yaw, Pitch, Roll;
    protected bool HavePos;
    protected double Damage;
    protected int DamageState = 1;
    protected double FadeStart = -1, FadeDir;       // seconds of scene time; dir 1 fading out, -1 in
    protected bool Hidden;
    protected string Skin = "";
    protected double SpinZ;
    protected double Age;
    private (RenderShape, string) mPreloaded;

    public override (double x, double y, double z)? Position => HavePos ? (X, Y, Z) : null;

    // True while a simulated copy of the ghost (RemoteGhosts) sets the state each frame: the stream update leaves it alone.
    public bool Driven;
    // The drawn heading (Euler z), the compass input of a piloted vehicle (docs/gui.md 4.2 Compass).
    public double HeadingRad => Yaw;

    // The state of the simulated copy of a Vehicle or Flier: Euler angles x, y, z and the position.
    public void SetSmoothed(double rx, double ry, double rz, double x, double y, double z)
    {
        (Roll, Pitch, Yaw) = (rx, ry, rz);
        (X, Y, Z) = (x, y, z);
        HavePos = true;
    }

    // Seconds since the ghost was created (Item::onAdd stores the time, Item::updateLight counts the light pulse from it).
    public double LightAge => Age;

    public override double[]? WorldBox
    {
        get
        {
            if (!HavePos || Shape == null)
                return null;
            Xf xf = PoseXf();
            float[] b = Shape.Box;
            var box = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (int c = 0; c < 8; ++c)
            {
                var (wx, wy, wz) = xf.Apply(b[(c & 1) != 0 ? 3 : 0], b[(c & 2) != 0 ? 4 : 1], b[(c & 4) != 0 ? 5 : 2]);
                box[0] = Math.Min(box[0], wx); box[1] = Math.Min(box[1], wy); box[2] = Math.Min(box[2], wz);
                box[3] = Math.Max(box[3], wx); box[4] = Math.Max(box[4], wy); box[5] = Math.Max(box[5], wz);
            }
            return box;
        }
    }

    // Sequence name of the shape, for the sequence sounds (docs/audio.md 3.3).
    public string SequenceName(int sequence) => Shape?.SequenceName(sequence) ?? "";

    public override (double x, double y, double z)? MuzzlePoint
    {
        get
        {
            if (!HavePos)
                return null;
            if (Inst == null || Shape == null)
                return (X, Y, Z + 1.5);
            int n = Shape.FindNodeBase("dummy muzzle", 0);
            if (n < 0)
                return (X, Y, Z + 1.5);
            Inst.Animate(0);
            return Inst.NodeWorld[n].Then(PoseXf()).Apply(0, 0, 0);
        }
    }

    private static int GroupOf(string cls) => cls switch
    {
        "StaticShape" => 4, "Turret" => 19, "Sensor" => 8, "Item" => 5, "Mine" => 23, "Moveable" => 7, "Vehicle" => 9, "Flier" => 10, "Player" => 3,
        _ => 4,
    };

    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        Group = GroupOf(g.Class);
    }

    protected void LoadBlock()
    {
        int idx = (int)Num("datablock", -1);
        if (idx == BlockIndex && Shape != null)
            return;
        BlockIndex = idx;
        Block = M.Db.Block(Group, idx);
        string name = Db.Str(Block, "shapeFile");
        if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        Shape = M.Assets.Shape(name);
        Inst = Shape != null ? new RenderInstance(Shape) : null;
        Array.Clear(Slots);
        Ambient = Visibility = Turn = Elevate = TurretPower = null;
        TurretPose = null;
        if (Shape != null && Inst != null)
        {
            if (Group != 3 && Shape.FindSequence("ambient") is int a && a >= 0 && Group is 4 or 8 or 19)
            {
                Ambient = Inst.AddThread(a, M.Rng.NextDouble(), 0);
            }
            if (Group != 3 && Shape.FindSequence("visibility") is int v && v >= 0)
            {
                Visibility = Inst.AddThread(v, 0, 0);
                Visibility.TimeScale = 0;
            }
            if (Group == 19)
            {
                if (Shape.FindSequence("turn") is int t && t >= 0) { Turn = Inst.AddThread(t, 0, 0); Turn.TimeScale = 0; }
                if (Shape.FindSequence("elevate") is int e && e >= 0) { Elevate = Inst.AddThread(e, 0, 0); Elevate.TimeScale = 0; }
                // Turret_onNewDataBlock 0x00452970: the third thread exists only when the shape has both `fire` and `power`; it starts on
                // `power` (priority -1, Turret_setPowerSequence 0x00452e1c) at position 0.
                mFireSeq = Shape.FindSequence("fire");
                mPowerSeq = Shape.FindSequence("power");
                if (mFireSeq >= 0 && mPowerSeq >= 0)
                {
                    TurretPower = Inst.AddThread(mPowerSeq, 0, -1);
                    TurretPower.TimeScale = 0;
                    TurretPose = new TurretGhostPose(true, Shape.Sequences[mPowerSeq].Duration, Shape.Sequences[mPowerSeq].Cyclic,
                        Shape.Sequences[mFireSeq].Duration, Shape.Sequences[mFireSeq].Cyclic, Db.Num(Block, "speed", 1));
                }
            }
            if (Group is 5 or 23 && Shape.FindSequence("ambient") is int ia && ia >= 0)
                Ambient = Inst.AddThread(ia, M.Rng.NextDouble(), 0);
            if (Group == 10)
                AddFlierThreads();
        }
    }

    // Flier_onNewDataBlock 0042e0c0 on a ghost: when the shape has the names `jet` and `idle`, thread +0x950 runs the sequence `jet` at
    // position 0.5 (Flier_clientUpdate sets its position every frame, so it is not advanced) and thread +0x954 the sequence `idle`
    // at 0; the indices of `thrust` (+0x968) and `idle` (+0x96c) are looked up for the switch.
    protected RenderThread? FlierJet, FlierIdle;
    private int mThrustSeq = -1, mIdleSeq = -1;
    private bool mFlierBraking, mFlierActive;
    private float mFlierF, mFlierT, mFlierMax = 1;

    private void AddFlierThreads()
    {
        FlierJet = FlierIdle = null;
        if (Shape == null || Inst == null)
            return;
        if (Shape.FindSequence("jet") is int j && j >= 0)
        {
            FlierJet = Inst.AddThread(j, 0.5, 0);
            FlierJet.TimeScale = 0;
        }
        if (Shape.FindSequence("idle") is int i && i >= 0)
        {
            FlierIdle = Inst.AddThread(i, 0, 0);
            mIdleSeq = i;
            mThrustSeq = Shape.FindSequence("thrust");
        }
    }

    // The values Flier_clientUpdate reads: braking and active (+0x960, +0x961), forward speed F (+0x940), desired speed T (+0x8c8) and the
    // datablock maxSpeed (+0x110). A Flier nobody pilots has both flags cleared first.
    public void SetFlierState(bool piloted, bool braking, bool active, float f, float t, float maxSpeed)
    {
        mFlierBraking = piloted && braking;
        mFlierActive = piloted && active;
        (mFlierF, mFlierT, mFlierMax) = (f, t, maxSpeed);
    }

    // The thread part of Flier_clientUpdate 0042e254 (the 0042e2c1 .. 0042e416 block, found by disassembly): the `jet` position moves toward 1 while braking
    // with a forward speed, else toward 0.5 - 0.5 * T / maxSpeed (1 - (0.5 * T / maxSpeed + 0.5)) at 1 per second, clamped to 0..1;
    // the second thread runs `thrust` while active and `idle` otherwise.
    private void TickFlier(double dt)
    {
        if (FlierJet != null)
        {
            float cur = (float)FlierJet.Position;
            float target = mFlierBraking && mFlierF != 0f ? 1f : 1f - ((mFlierT / mFlierMax) * 0.5f + 0.5f);
            float step = (float)dt;
            if (cur < target)
            {
                cur += step;
                if (cur > target)
                    cur = target;
            }
            else if (cur > target)
            {
                cur -= step;
                if (cur < target)
                    cur = target;
            }
            if (cur > 1f)
                cur = 1f;
            if (cur < 0f)
                cur = 0f;
            FlierJet.Position = cur;
        }
        if (FlierIdle != null)
        {
            int seq = mFlierActive ? mThrustSeq : mIdleSeq;
            if (seq >= 0 && FlierIdle.Sequence != seq)
            {
                FlierIdle.Sequence = seq;
                FlierIdle.Position = 0;
            }
            Inst!.Advance(FlierIdle, dt);
        }
    }

    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        LoadBlock();
        UpdateTeamSkin();
        if (Shape != null && mPreloaded != (Shape, Skin))
        {
            mPreloaded = (Shape, Skin);
            M.Assets.Preload(Shape, Skin);
        }
        if (Has("pos") && !Driven && u.Fields.Any(f => f.Name == "pos"))
        {
            double[] p = Vec("pos");
            (X, Y, Z) = (p[0], p[1], p[2]);
            HavePos = true;
        }
        if (!Driven && u.Fields.Any(f => f.Name == "rot"))
        {
            double[] r = Vec("rot");
            (Roll, Pitch, Yaw) = (r[0], r[1], r[2]);
        }
        if (u.Fields.Any(f => f.Name == "rotZ") && Group is 5 or 23)
            Yaw = Num("rotZ") * Math.PI * 2;
        foreach (DecodedField f in u.Fields)
            if (f.Name == "threads")
                foreach (ThreadSlot s in ThreadSlot.ParseAll(f.Value))
                    ApplySlot(s);
        if (u.Fields.Any(f => f.Name == "fade"))
            StartFade((long)Num("fade") == 2);
        if (Group == 19)
            ApplyTurret(u);
        if (u.Fields.Any(f => f.Name == "damageState"))
        {
            int ds = (int)Num("damageState");
            // StaticBase_setDamageState: a state of 0 (Disabled) 2, Destroyed 3 in the client numbering of the ghost.
            int state = ds == 1 ? 1 : ds == 2 ? 2 : ds == 0 ? 3 : ds;
            if (state != DamageState && !isNew && state == 3)
                Destroyed();
            // StaticBase_setDamageState: Enabled and Disabled call 0044cfe4, which clears the no-draw flag +0x71d set by ShapeBase_explode.
            if (state != 3 && DamageState == 3)
                Hidden = false;
            DamageState = state;
        }
        Damage = Num("damage");
    }

    // The ghost carries 0 Enabled(1) | Disabled(2) | Destroyed(0 after flags). Written by UnShapeBase: st = flag ? 1 : (flag ? 2 : 0).
    private void ApplySlot(ThreadSlot s)
    {
        if (Inst == null || s.Slot < 0 || s.Slot > 2)
            return;
        RenderThread? t = Slots[s.Slot];
        if (s.Sequence < 0 || s.Sequence >= Inst.Shape.Sequences.Length)
        {
            if (t != null)
                t.TimeScale = 0;
            return;
        }
        if (t == null)
        {
            t = Inst.AddThread(s.Sequence, 0, 0);
            Slots[s.Slot] = t;
            SlotSeq[s.Slot] = s.Sequence;
        }
        else if (SlotSeq[s.Slot] != s.Sequence)
        {
            t.Sequence = s.Sequence;
            t.Position = 0;
            SlotSeq[s.Slot] = s.Sequence;
        }
        // state 0 playing, 1 stopped (position 0), 2 paused (3.5, static-sim.md 1.7)
        double dir = s.Forward ? 1 : -1;
        if (s.State == 0)
        {
            t.TimeScale = s.AtEnd ? 0 : dir;
            if (s.AtEnd)
                t.Position = s.Forward ? 1 : 0;
        }
        else if (s.State == 1)
        {
            t.TimeScale = 0;
            t.Position = 0;
        }
        else
            t.TimeScale = 0;
    }

    // The aim of a Turret that is the control object (Turret_readControlState 0045711c and Turret_clientUpdate 00456428 write +0x8b4 and
    // +0x8b8, Turret_updateAim 00456874 hands the thread positions on, item 7 of docs/static-sim.md 14.5): elevate = (elevation - min) /
    // (max - min) when the limits differ, turn = rotation / 2 pi (the rotation limits are 0 and 0). The ghost sends no aim to its owner.
    public void SetTurretAim(float rotation, float elevation, float minElevation, float maxElevation)
    {
        if (TurretPose is { } pose)
        {
            pose.Rotation = rotation;
            SyncTurret();
        }
        else if (Turn != null)
            Turn.Position = Math.Clamp(rotation / TurretGhostPose.TwoPi, 0, 1);
        if (Elevate != null && maxElevation != minElevation)
            Elevate.Position = Math.Clamp((elevation - minElevation) / (maxElevation - minElevation), 0, 1);
    }

    private float mMinElevation, mMaxElevation;
    private bool mHaveLimits;

    // The elevation limits of the turret shape: Turret_onNewDataBlock 00452970 reads the Euler x of the node `dummy muzzle` with the
    // `elevate` thread at position 0 and at 1 (a minimum above pi loses 2 pi). False without the shape, the thread or the node.
    public bool TryElevationLimits(out float min, out float max)
    {
        (min, max) = (mMinElevation, mMaxElevation);
        if (mHaveLimits)
            return true;
        int n = Inst != null && Shape != null ? Shape.FindNodeBase("dummy muzzle", 0) : -1;
        if (n < 0 || Elevate == null || Inst == null)
            return false;
        (min, max) = ElevationLimitsOf(Inst, n, Elevate, Turn, TurretPower);
        (mMinElevation, mMaxElevation, mHaveLimits) = (min, max, true);
        return true;
    }

    // Turret_onNewDataBlock 00452970 (limits at +0x8bc and +0x8c0): the Euler x of the `dummy muzzle` node entry (Mat3_toEuler 005eceb4)
    // with the `elevate` thread at position 0 and at 1. The routine runs before the power / fire thread (+0x8a4) is created and with the
    // turn thread at 0, so the nodes carry neither the power pose (it folds the barrels) nor the current turn.
    public static (float Min, float Max) ElevationLimitsOf(RenderInstance inst, int node, RenderThread elevate, RenderThread? turn, RenderThread? power)
    {
        double keep = elevate.Position;
        double keepTurn = turn?.Position ?? 0;
        int powerAt = power != null ? inst.Threads.IndexOf(power) : -1;
        if (powerAt >= 0)
            inst.Threads.RemoveAt(powerAt);
        if (turn != null)
            turn.Position = 0;
        float EulerX(double position)
        {
            elevate.Position = position;
            inst.Animate(0);
            Xf e = inst.NodeWorld[node];
            var t = new OpenTribes.Sim.Transform { Flags = 3u };
            double[] m = { e.M0, e.M1, e.M2, e.M3, e.M4, e.M5, e.M6, e.M7, e.M8 };
            for (int i = 0; i < 9; ++i)
                t.M[i] = (float)m[i];
            var eu = new float[3];
            OpenTribes.Sim.MissionTransform.MatrixToEuler(t, eu);
            return eu[0];
        }
        float lo = EulerX(0), hi = EulerX(1);
        elevate.Position = keep;
        if (turn != null)
            turn.Position = keepTurn;
        if (powerAt >= 0)
            inst.Threads.Insert(powerAt, power!);
        if (lo > (float)Math.PI)
            lo -= 2.0f * (float)Math.PI;      // a minimum above pi loses 2 pi (the exe compares with pi and subtracts 2 pi)
        return (lo, hi);
    }

    private void ApplyTurret(DecodedGhost u)
    {
        if (u.Fields.Any(f => f.Name == "rotation") && Turn != null)
            Turn.Position = Math.Clamp(Num("rotation"), 0, 1);
        if (u.Fields.Any(f => f.Name == "elevation") && Elevate != null)
            Elevate.Position = Math.Clamp(Num("elevation"), 0, 1);
        if (TurretPose is not { } pose)
            return;
        // Turret_unpackUpdate 0x0045750c reads rotation, elevation, fire count, mode in this order.
        if (u.Fields.Any(f => f.Name == "rotation"))
            pose.Rotation = Num("rotation") * TurretGhostPose.TwoPi;
        if (u.Fields.Any(f => f.Name == "fireState"))
            pose.OnFireCount();
        if (u.Fields.Any(f => f.Name == "mode"))
            pose.OnMode((int)Num("mode"));
        SyncTurret();
    }

    // Copies the pose model to the render threads: the power / fire thread and the turn thread (Turret_updateAim 0x00456874 sets the turn
    // thread from the rotation every tick).
    private void SyncTurret()
    {
        if (TurretPose is not { } pose || TurretPower == null)
            return;
        TurretPower.Sequence = pose.OnFire ? mFireSeq : mPowerSeq;
        TurretPower.Priority = pose.OnFire ? 0 : -1;
        TurretPower.Position = pose.Position;
        TurretPower.TimeScale = 0;
        if (Turn != null)
            Turn.Position = Math.Clamp(pose.Rotation / TurretGhostPose.TwoPi, 0, 1);
    }

    private void UpdateTeamSkin()
    {
        int team = (int)Num("team", 15);
        Skin = M.TeamSkin(team);
    }

    protected virtual void Destroyed()
    {
        if (Inst == null || Block == null)
            return;
        int expl = (int)Db.Num(Block, "explosionId", -1);
        int debris = (int)Db.Num(Block, "debrisId", -1);
        var (cx, cy, cz) = BoxCenter();
        if (expl >= 0)
            M.SpawnExplosion(expl, cx, cy, cz, 0, 0, 1);
        // ShapeBase_explode 0044cdec sets the no-draw flag +0x71d only when the datablock has no debris or the shape has no `debris0` detail.
        // Otherwise the shape stays drawn (the frame detail pick never selects the size 0 debris0 detail), so a destroyed generator stays
        // on the map to be repaired, and the pieces fly from the debris0 detail.
        if (debris < 0)
        {
            Hidden = true;
            return;
        }
        M.Effects.Add(M.NewDebris(Inst, PoseXf(), 0, 0, 0, debris));
        if (!HasDebrisDetail(Inst.Shape))
            Hidden = true;
    }

    // TSShapeInstance_findDetailNode (005b96a0) with "debris0": a detail whose root node is named debris0.
    public static bool HasDebrisDetail(RenderShape sh)
    {
        int node = sh.FindNode("debris0");
        if (node < 0)
            return false;
        foreach (RsDetail d in sh.Details)
            if (d.RootNode == node)
                return true;
        return false;
    }

    // The world box of the object [code 004aca84 reads +0x70..+0x84]: the shape box bound by ShapeBase::onNewDataBlock (0044b274), widened to
    // +-triggerRadius by StaticBase (0044f68c) when the datablock has one, rotated to an axis aligned box (005edbb4). Position is the object
    // transform origin (+0x188), which the pick measures the distance from.
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Px, double Py, double Pz)? HudBox()
    {
        if (!HavePos || Shape == null)
            return null;
        Xf xf = PoseXf();
        double[] lo = { Shape.Box[0], Shape.Box[1], Shape.Box[2] }, hi = { Shape.Box[3], Shape.Box[4], Shape.Box[5] };
        double r = Block == null ? 0 : Db.Num(Block, "triggerRadius");
        if (r != 0)
            for (int i = 0; i < 3; ++i)
            {
                lo[i] = Math.Min(lo[i], -r);
                hi[i] = Math.Max(hi[i], r);
            }
        double x0 = double.MaxValue, y0 = x0, z0 = x0, x1 = -x0, y1 = x1, z1 = x1;
        for (int i = 0; i < 8; ++i)
        {
            var c = xf.Apply((i & 1) == 0 ? lo[0] : hi[0], (i >> 1 & 1) == 0 ? lo[1] : hi[1], (i >> 2 & 1) == 0 ? lo[2] : hi[2]);
            x0 = Math.Min(x0, c.x); x1 = Math.Max(x1, c.x);
            y0 = Math.Min(y0, c.y); y1 = Math.Max(y1, c.y);
            z0 = Math.Min(z0, c.z); z1 = Math.Max(z1, c.z);
        }
        return (x0, y0, z0, x1, y1, z1, xf.Px, xf.Py, xf.Pz);
    }

    protected (double x, double y, double z) BoxCenter()
    {
        if (Shape == null)
            return (X, Y, Z);
        return PoseXf().Apply(Shape.Cx, Shape.Cy, Shape.Cz);
    }

    // Vehicle_isDriverVisible 004453cc (vtable +0x164): the datablock byte +0x170, `visibleDriver`.
    // Vehicle_getDriverPose (vtable +0x168): the datablock int +0x174, `driverPose`; 0 without a datablock.
    public int DriverPose => Block == null ? 0 : (int)Db.Num(Block, "driverPose");

    public bool DriverVisible => Block == null || Db.Flag(Block, "visibleDriver");

    // The inputs of Vehicle_getCameraTransform 00445d88: the object transform as drawn (Euler x, y, z and the position, rotation and
    // translation flags) and the `dummy eye` node transform (animated, in object space), null without that node.
    public bool TryCameraInputs(out OpenTribes.Sim.Transform obj, out OpenTribes.Sim.Transform? eye) =>
        TryNodeInputs(s => s.FindNodeBase("dummy eye", 0), out obj, out eye, out _);

    // The object transform as drawn and the animated transform (object space) of the node `find` picks, null when it picks none.
    // `haveShape` is false while the visual has no shape instance (the stock vtable +0x10c then reports no transform).
    public bool TryNodeInputs(Func<RenderShape, int> find, out OpenTribes.Sim.Transform obj, out OpenTribes.Sim.Transform? node, out bool haveShape)
    {
        obj = new OpenTribes.Sim.Transform();
        node = null;
        haveShape = Inst != null && Shape != null;
        if (!HavePos)
            return false;
        OpenTribes.Sim.MissionTransform.EulerToMatrix(new[] { (float)Roll, (float)Pitch, (float)Yaw }, ref obj);
        obj.Flags |= 3u;
        obj.P[0] = (float)X; obj.P[1] = (float)Y; obj.P[2] = (float)Z;
        int n = haveShape ? find(Shape!) : -1;
        if (n < 0)
            return true;
        Inst!.Animate(0);
        Xf e = Inst.NodeWorld[n];
        var t = new OpenTribes.Sim.Transform { Flags = 3u };
        double[] m = { e.M0, e.M1, e.M2, e.M3, e.M4, e.M5, e.M6, e.M7, e.M8 };
        for (int i = 0; i < 9; ++i)
            t.M[i] = (float)m[i];
        t.P[0] = (float)e.Px; t.P[1] = (float)e.Py; t.P[2] = (float)e.Pz;
        node = t;
        return true;
    }

    protected virtual Xf PoseXf() => Xf.Euler(Roll, Pitch, Yaw + SpinZ).Then(Xf.Translate(X, Y, Z));

    public override void Tick(double dt)
    {
        Age += dt;
        if (Inst == null)
            return;
        if (Group is 5 or 23 && Num("rotates") != 0 && Num("atRest", 1) != 0)
            SpinZ += 3.0 * dt;   // Item_clientUpdate: spin about z at 3 rad/s (3.5)
        foreach (RenderThread? t in Slots)
            if (t != null)
                Inst.Advance(t, dt);
        if (Ambient != null)
            Inst.Advance(Ambient, dt);
        if (Visibility != null)
            Visibility.Position = DamageState == 3 ? 1.0 : 0.0;
        if (TurretPose != null)
        {
            TurretPose.Tick(dt);
            if (TurretPose.PowerSound != 0)
                M.Audio?.TurretPower(this, TurretPose.PowerSound > 0);
            SyncTurret();
        }
        if (Group == 10)
            TickFlier(dt);
    }

    // ShapeBase::startFadeOut / startFadeIn (0044e..., client side of the 0x200 mask): a fade that is not running starts now. Reversing a
    // running fade keeps the fraction it had reached (the start time moves to now - (2.5 s - elapsed)), unless the elapsed time is past 2.5 s.
    // A finished fade in (state 2) is state 0 again, so asking for it again starts it again.
    private void StartFade(bool fadeIn)
    {
        int dir = fadeIn ? -1 : 1;
        double now = M.Time, elapsed = now - FadeStart;
        if (FadeStart < 0 || FadeDir == 0 || (FadeDir < 0 && elapsed >= FadeSeconds))
            FadeStart = now;
        else if (dir != FadeDir)
            FadeStart = elapsed <= FadeSeconds ? now - (FadeSeconds - elapsed) : now;
        FadeDir = dir;
    }

    // The fade runs 2500 ms (the divisor at 0061a2fc).
    private const double FadeSeconds = 2.5;

    // Item::updateLight (00451bb0) multiplies the pulse by 1 - fade: the light fades with the shape on hide and show.
    public double LightFade => 1 - FadeAmount();

    protected virtual double FadeAmount()
    {
        if (FadeStart < 0)
            return 0;
        double f = (M.Time - FadeStart) / FadeSeconds;
        if (FadeDir > 0)
            return Math.Clamp(f, 0, 1);
        return Math.Clamp(1 - f, 0, 1);
    }

    protected virtual int DamageLevel => Skins.DamageLevel(Damage, M.Assets.DamageSkinDetail);

    protected readonly ProbeState mProbe = new();

    // The light set of this object's main image: the scene light adjusted by the probe state. Items and vehicles probe again every 250 ms
    // (Item_queryRender, Vehicle_renderQuery); the other shapes probe once (ShapeBase_processQuery).
    protected LightSet ProbedLight(FrameContext ctx)
    {
        LightProbe? probe = M.Probe;
        double[]? box = probe == null ? null : WorldBox;
        if (probe == null || box == null)
            return ctx.Light;
        bool repeat = Group is 5 or 23 or 9 or 10;
        if (mProbe.LastUpdate < 0 || (Group == 3 && M.Time != mProbe.LastUpdate) || (repeat && M.Time - mProbe.LastUpdate >= 0.25))
        {
            var (ok, rgb, strength) = probe.Query(box);
            mProbe.Update(ok, rgb, strength);
            mProbe.LastUpdate = M.Time;
        }
        return mProbe.Active ? ctx.Light.WithProbe(mProbe.Colour.r, mProbe.Colour.g, mProbe.Colour.b, mProbe.Strength) : ctx.Light;
    }

    public override void Build(FrameContext ctx)
    {
        if (Inst == null || Shape == null || !HavePos || Hidden)
            return;
        ShapeDraw d = ctx.Draw(PoseXf(), Shape.Name);
        d.Light = ProbedLight(ctx);
        d.Skin = Skin;
        d.DamageLevel = Group is 3 or 4 or 19 ? DamageLevel : 0;
        d.Fade = FadeAmount();
        d.DetailScale = 1;
        RenderImage? img = ctx.Renderer.Build(Inst, ctx.Camera, d);
        if (img != null)
            ctx.Lists.Add(img);
    }
}
