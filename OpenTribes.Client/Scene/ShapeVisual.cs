namespace OpenTribes.Client.Scene;

using OpenTribes.Client.Shapes;
using OpenTribes.Game;

/// <summary>The visual of one ghost: it applies the updates of the ghost, ticks, and builds its images each frame.</summary>
public abstract class Visual
{
    /// <summary>The ghost index.</summary>
    public int Index;
    /// <summary>The class name of the ghost.</summary>
    public string Class = "";
    /// <summary>The ghost the visual shows.</summary>
    public ClientGhost Ghost = null!;
    /// <summary>The scene model.</summary>
    protected SceneModel M = null!;

    /// <summary>Binds the visual to its ghost and scene.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="g">The ghost.</param>
    public virtual void Init(SceneModel m, ClientGhost g)
    {
        M = m;
        Ghost = g;
        Index = g.Index;
        Class = g.Class;
    }

    /// <summary>Applies a ghost update.</summary>
    /// <param name="u">The decoded update.</param>
    /// <param name="isNew">True for the first update of the ghost.</param>
    public abstract void OnUpdate(DecodedGhost u, bool isNew);
    /// <summary>Advances the visual.</summary>
    /// <param name="dt">The time step in seconds.</param>
    public virtual void Tick(double dt) { }
    /// <summary>Adds the images of the visual to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    public virtual void Build(FrameContext ctx) { }
    /// <summary>Called when the ghost is deleted.</summary>
    public virtual void OnDelete() { }
    /// <summary>The position of the object, or null when unknown.</summary>
    public virtual (double x, double y, double z)? Position => null;
    /// <summary>The world box of the object (min xyz, max xyz) used to file it in a container interior, or null when unknown.</summary>
    public virtual double[]? WorldBox => null;
    /// <summary>Where a beam or projectile of this object starts: the muzzle (4.7).</summary>
    public virtual (double x, double y, double z)? MuzzlePoint => Position;

    /// <summary>Reads a number of the ghost state.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="def">The value when the field is missing.</param>
    /// <returns>The number or the default.</returns>
    protected double Num(string key, double def = 0) => Ghost.State.TryGetValue(key, out var v) ? Db.ToDouble(v, def) : def;
    /// <summary>Tells whether the ghost state has a field.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>True when the field exists.</returns>
    protected bool Has(string key) => Ghost.State.ContainsKey(key);
    /// <summary>Reads a vector of the ghost state.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>The vector, or three zeros when the field is missing.</returns>
    protected double[] Vec(string key) => Ghost.State.TryGetValue(key, out var v) ? Db.Vec(v) : new double[3];
}

/// <summary>The visual of the object classes that draw one shape (StaticShape, Turret, Sensor, Moveable, Item, Mine, Vehicle, Flier): the three ShapeBase thread slots, damage skins, fade and the destroy effects.</summary>
/// <remarks>docs/shapes-render.md 2.1 and 3.5.</remarks>
public class ShapeVisual : Visual
{
    /// <summary>The shape.</summary>
    protected RenderShape? Shape;
    /// <summary>The shape instance.</summary>
    protected RenderInstance? Inst;
    /// <summary>The datablock.</summary>
    protected ClientDataBlock? Block;
    /// <summary>The datablock group of the class.</summary>
    protected int Group;
    /// <summary>The index of the loaded datablock, -1 for none.</summary>
    protected int BlockIndex = -1;
    /// <summary>The threads of the three ShapeBase thread slots.</summary>
    protected readonly RenderThread?[] Slots = new RenderThread?[3];
    /// <summary>The sequence of each thread slot, -1 for none.</summary>
    protected readonly int[] SlotSeq = [-1, -1, -1];
    /// <summary>True for each thread slot that runs.</summary>
    protected readonly bool[] SlotRunning = new bool[3];
    /// <summary>The ambient thread.</summary>
    protected RenderThread? Ambient;
    /// <summary>The visibility thread.</summary>
    protected RenderThread? Visibility;
    /// <summary>The turn thread of a turret.</summary>
    protected RenderThread? Turn;
    /// <summary>The elevate thread of a turret.</summary>
    protected RenderThread? Elevate;
    /// <summary>The power thread of a turret.</summary>
    protected RenderThread? TurretPower;
    /// <summary>The mode driven power and fire thread (+0x8a4) and its sequences, set up by Turret_onNewDataBlock (00452970).</summary>
    protected TurretGhostPose? TurretPose;
    private int mPowerSeq = -1, mFireSeq = -1;
    /// <summary>The x position of the object.</summary>
    protected double X;
    /// <summary>The y position of the object.</summary>
    protected double Y;
    /// <summary>The z position of the object.</summary>
    protected double Z;
    /// <summary>The heading (Euler z).</summary>
    protected double Yaw;
    /// <summary>The pitch (Euler y).</summary>
    protected double Pitch;
    /// <summary>The roll (Euler x).</summary>
    protected double Roll;
    /// <summary>True once the ghost sent a position.</summary>
    protected bool HavePos;
    /// <summary>The damage value.</summary>
    protected double Damage;
    /// <summary>The damage state.</summary>
    protected int DamageState = 1;
    /// <summary>The scene time the fade started, -1 for none.</summary>
    protected double FadeStart = -1;
    /// <summary>The fade direction: 1 fading out, -1 fading in.</summary>
    protected double FadeDir;
    /// <summary>True while the object is hidden.</summary>
    protected bool Hidden;
    /// <summary>The skin name.</summary>
    protected string Skin = "";
    /// <summary>The extra spin about z.</summary>
    protected double SpinZ;
    /// <summary>The seconds since the ghost was created.</summary>
    protected double Age;
    private (RenderShape, string) mPreloaded;

    /// <inheritdoc/>
    public override (double x, double y, double z)? Position => HavePos ? (X, Y, Z) : null;

    /// <summary>True while a simulated copy of the ghost (RemoteGhosts) sets the state each frame: the stream update leaves it alone.</summary>
    public bool Driven;
    /// <summary>The drawn heading (Euler z), the compass input of a piloted vehicle (docs/gui.md 4.2 Compass).</summary>
    public double HeadingRad => Yaw;

    /// <summary>Sets the state of the simulated copy of a Vehicle or Flier.</summary>
    /// <param name="rx">The Euler x.</param>
    /// <param name="ry">The Euler y.</param>
    /// <param name="rz">The Euler z.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    public void SetSmoothed(double rx, double ry, double rz, double x, double y, double z)
    {
        (Roll, Pitch, Yaw) = (rx, ry, rz);
        (X, Y, Z) = (x, y, z);
        HavePos = true;
    }

    /// <summary>The seconds since the ghost was created (Item::onAdd stores the time, Item::updateLight counts the light pulse from it).</summary>
    public double LightAge => Age;

    /// <inheritdoc/>
    public override double[]? WorldBox
    {
        get
        {
            if (!HavePos || Shape == null)
                return null;
            var xf = PoseXf();
            var b = Shape.Box;
            var box = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (var c = 0; c < 8; ++c)
            {
                var (wx, wy, wz) = xf.Apply(b[(c & 1) != 0 ? 3 : 0], b[(c & 2) != 0 ? 4 : 1], b[(c & 4) != 0 ? 5 : 2]);
                box[0] = Math.Min(box[0], wx);
                box[1] = Math.Min(box[1], wy);
                box[2] = Math.Min(box[2], wz);
                box[3] = Math.Max(box[3], wx);
                box[4] = Math.Max(box[4], wy);
                box[5] = Math.Max(box[5], wz);
            }
            return box;
        }
    }

    /// <summary>Gets the name of a sequence of the shape, for the sequence sounds (docs/audio.md 3.3).</summary>
    /// <param name="sequence">The sequence index.</param>
    /// <returns>The name, or empty without a shape.</returns>
    public string SequenceName(int sequence) => Shape?.SequenceName(sequence) ?? "";

    /// <inheritdoc/>
    public override (double x, double y, double z)? MuzzlePoint
    {
        get
        {
            if (!HavePos)
                return null;
            if (Inst == null || Shape == null)
                return (X, Y, Z + 1.5);
            var n = Shape.FindNodeBase("dummy muzzle", 0);
            if (n < 0)
                return (X, Y, Z + 1.5);
            Inst.Animate(0);
            return Inst.NodeWorld[n].Then(PoseXf()).Apply(0, 0, 0);
        }
    }

    private static int GroupOf(string cls) => cls switch
    {
        "StaticShape" => 4,
        "Turret" => 19,
        "Sensor" => 8,
        "Item" => 5,
        "Mine" => 23,
        "Moveable" => 7,
        "Vehicle" => 9,
        "Flier" => 10,
        "Player" => 3,
        _ => 4,
    };

    /// <inheritdoc/>
    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        Group = GroupOf(g.Class);
    }

    /// <summary>Loads the datablock and shape of the ghost when the datablock index changed, and sets up the instance and its threads.</summary>
    protected void LoadBlock()
    {
        var idx = (int)Num("datablock", -1);
        if (idx == BlockIndex && Shape != null)
            return;
        BlockIndex = idx;
        Block = M.Db.Block(Group, idx);
        var name = Db.Str(Block, "shapeFile");
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
                if (Shape.FindSequence("turn") is int t && t >= 0)
                { Turn = Inst.AddThread(t, 0, 0); Turn.TimeScale = 0; }
                if (Shape.FindSequence("elevate") is int e && e >= 0)
                { Elevate = Inst.AddThread(e, 0, 0); Elevate.TimeScale = 0; }
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

    /// <summary>The jet thread of a Flier: Flier_onNewDataBlock (0042e0c0) runs the sequence <c>jet</c> at position 0.5 when the shape has the names <c>jet</c> and <c>idle</c>. Flier_clientUpdate sets its position every frame, so it is not advanced.</summary>
    protected RenderThread? FlierJet;
    /// <summary>The idle thread of a Flier: thread +0x954 runs the sequence <c>idle</c> at 0. The indices of <c>thrust</c> (+0x968) and <c>idle</c> (+0x96c) are looked up for the switch.</summary>
    protected RenderThread? FlierIdle;
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

    /// <summary>Sets the values Flier_clientUpdate reads.</summary>
    /// <param name="piloted">True when somebody pilots the Flier. A Flier nobody pilots has both flags cleared first.</param>
    /// <param name="braking">The braking flag (+0x960).</param>
    /// <param name="active">The active flag (+0x961).</param>
    /// <param name="f">The forward speed (+0x940).</param>
    /// <param name="t">The desired speed (+0x8c8).</param>
    /// <param name="maxSpeed">The datablock maxSpeed (+0x110).</param>
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
            var cur = (float)FlierJet.Position;
            var target = mFlierBraking && mFlierF != 0f ? 1f : 1f - ((mFlierT / mFlierMax * 0.5f) + 0.5f);
            var step = (float)dt;
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
            var seq = mFlierActive ? mThrustSeq : mIdleSeq;
            if (seq >= 0 && FlierIdle.Sequence != seq)
            {
                FlierIdle.Sequence = seq;
                FlierIdle.Position = 0;
            }
            Inst!.Advance(FlierIdle, dt);
        }
    }

    /// <inheritdoc/>
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
            var p = Vec("pos");
            (X, Y, Z) = (p[0], p[1], p[2]);
            HavePos = true;
        }
        if (!Driven && u.Fields.Any(f => f.Name == "rot"))
        {
            var r = Vec("rot");
            (Roll, Pitch, Yaw) = (r[0], r[1], r[2]);
        }
        if (u.Fields.Any(f => f.Name == "rotZ") && Group is 5 or 23)
            Yaw = Num("rotZ") * Math.PI * 2;
        foreach (var f in u.Fields)
            if (f.Name == "threads")
                foreach (var s in ThreadSlot.ParseAll(f.Value))
                    ApplySlot(s);
        if (u.Fields.Any(f => f.Name == "fade"))
            StartFade((long)Num("fade") == 2);
        if (Group == 19)
            ApplyTurret(u);
        if (u.Fields.Any(f => f.Name == "damageState"))
        {
            var ds = (int)Num("damageState");
            // StaticBase_setDamageState: a state of 0 (Disabled) 2, Destroyed 3 in the client numbering of the ghost.
            var state = ds == 1 ? 1 : ds == 2 ? 2 : ds == 0 ? 3 : ds;
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
        var t = Slots[s.Slot];
        if (s.Sequence < 0 || s.Sequence >= Inst.Shape.Sequences.Length)
        {
            t?.TimeScale = 0;
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

    /// <summary>Sets the aim of a Turret that is the control object.</summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="elevation">The elevation.</param>
    /// <param name="minElevation">The lower elevation limit.</param>
    /// <param name="maxElevation">The upper elevation limit.</param>
    /// <remarks>Follows Turret_readControlState (0045711c) and Turret_clientUpdate (00456428), which write +0x8b4 and +0x8b8, and Turret_updateAim (00456874), which hands the thread positions on (item 7 of docs/static-sim.md 14.5): elevate = (elevation - min) / (max - min) when the limits differ, turn = rotation / 2 pi (the rotation limits are 0 and 0). The ghost sends no aim to its owner.</remarks>
    public void SetTurretAim(float rotation, float elevation, float minElevation, float maxElevation)
    {
        if (TurretPose is { } pose)
        {
            pose.Rotation = rotation;
            SyncTurret();
        }
        else
            Turn?.Position = Math.Clamp(rotation / TurretGhostPose.TwoPi, 0, 1);
        if (Elevate != null && maxElevation != minElevation)
            Elevate.Position = Math.Clamp((elevation - minElevation) / (maxElevation - minElevation), 0, 1);
    }

    private float mMinElevation, mMaxElevation;
    private bool mHaveLimits;

    /// <summary>Reads the elevation limits of the turret shape.</summary>
    /// <param name="min">The lower limit.</param>
    /// <param name="max">The upper limit.</param>
    /// <returns>False without the shape, the thread or the node.</returns>
    /// <remarks>Follows Turret_onNewDataBlock (00452970): the Euler x of the node <c>dummy muzzle</c> with the <c>elevate</c> thread at position 0 and at 1. A minimum above pi loses 2 pi.</remarks>
    public bool TryElevationLimits(out float min, out float max)
    {
        (min, max) = (mMinElevation, mMaxElevation);
        if (mHaveLimits)
            return true;
        var n = Inst != null && Shape != null ? Shape.FindNodeBase("dummy muzzle", 0) : -1;
        if (n < 0 || Elevate == null || Inst == null)
            return false;
        (min, max) = ElevationLimitsOf(Inst, n, Elevate, Turn, TurretPower);
        (mMinElevation, mMaxElevation, mHaveLimits) = (min, max, true);
        return true;
    }

    /// <summary>Reads the elevation limits of a turret shape instance.</summary>
    /// <param name="inst">The shape instance.</param>
    /// <param name="node">The node index of <c>dummy muzzle</c>.</param>
    /// <param name="elevate">The elevate thread.</param>
    /// <param name="turn">The turn thread, or null.</param>
    /// <param name="power">The power thread, or null.</param>
    /// <returns>The lower and upper limit.</returns>
    /// <remarks>Follows Turret_onNewDataBlock (00452970, limits at +0x8bc and +0x8c0): the Euler x of the <c>dummy muzzle</c> node entry (Mat3_toEuler 005eceb4) with the <c>elevate</c> thread at position 0 and at 1. The routine runs before the power and fire thread (+0x8a4) is created and with the turn thread at 0, so the nodes carry neither the power pose (it folds the barrels) nor the current turn.</remarks>
    public static (float Min, float Max) ElevationLimitsOf(RenderInstance inst, int node, RenderThread elevate, RenderThread? turn, RenderThread? power)
    {
        var keep = elevate.Position;
        var keepTurn = turn?.Position ?? 0;
        var powerAt = power != null ? inst.Threads.IndexOf(power) : -1;
        if (powerAt >= 0)
            inst.Threads.RemoveAt(powerAt);
        turn?.Position = 0;
        float EulerX(double position)
        {
            elevate.Position = position;
            inst.Animate(0);
            var e = inst.NodeWorld[node];
            var t = new Sim.Transform { Flags = 3u };
            double[] m = [e.M0, e.M1, e.M2, e.M3, e.M4, e.M5, e.M6, e.M7, e.M8];
            for (var i = 0; i < 9; ++i)
                t.M[i] = (float)m[i];
            var eu = new float[3];
            Sim.MissionTransform.MatrixToEuler(t, eu);
            return eu[0];
        }
        float lo = EulerX(0), hi = EulerX(1);
        elevate.Position = keep;
        turn?.Position = keepTurn;
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
        Turn?.Position = Math.Clamp(pose.Rotation / TurretGhostPose.TwoPi, 0, 1);
    }

    private void UpdateTeamSkin()
    {
        var team = (int)Num("team", 15);
        Skin = M.TeamSkin(team);
    }

    /// <summary>Runs the destroy effects: the explosion, the debris and the hide of the shape.</summary>
    protected virtual void Destroyed()
    {
        if (Inst == null || Block == null)
            return;
        var expl = (int)Db.Num(Block, "explosionId", -1);
        var debris = (int)Db.Num(Block, "debrisId", -1);
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

    /// <summary>Tells whether the shape has a <c>debris0</c> detail, a detail whose root node is named debris0 (TSShapeInstance_findDetailNode, 005b96a0).</summary>
    /// <param name="sh">The shape.</param>
    /// <returns>True when the detail exists.</returns>
    public static bool HasDebrisDetail(RenderShape sh)
    {
        var node = sh.FindNode("debris0");
        if (node < 0)
            return false;
        foreach (var d in sh.Details)
            if (d.RootNode == node)
                return true;
        return false;
    }

    /// <summary>Builds the HUD box of the object: the box and the object origin.</summary>
    /// <returns>The box min and max and the origin, or null without a shape.</returns>
    /// <remarks>Follows 004aca84, which reads +0x70 to +0x84: the shape box bound by ShapeBase::onNewDataBlock (0044b274), widened to plus or minus triggerRadius by StaticBase (0044f68c) when the datablock has one, rotated to an axis aligned box (005edbb4). The position is the object transform origin (+0x188), which the pick measures the distance from.</remarks>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Px, double Py, double Pz)? HudBox()
    {
        if (!HavePos || Shape == null)
            return null;
        var xf = PoseXf();
        double[] lo = [Shape.Box[0], Shape.Box[1], Shape.Box[2]], hi = [Shape.Box[3], Shape.Box[4], Shape.Box[5]];
        var r = Block == null ? 0 : Db.Num(Block, "triggerRadius");
        if (r != 0)
            for (var i = 0; i < 3; ++i)
            {
                lo[i] = Math.Min(lo[i], -r);
                hi[i] = Math.Max(hi[i], r);
            }
        double x0 = double.MaxValue, y0 = x0, z0 = x0, x1 = -x0, y1 = x1, z1 = x1;
        for (var i = 0; i < 8; ++i)
        {
            var (x, y, z) = xf.Apply((i & 1) == 0 ? lo[0] : hi[0], ((i >> 1) & 1) == 0 ? lo[1] : hi[1], ((i >> 2) & 1) == 0 ? lo[2] : hi[2]);
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            y1 = Math.Max(y1, y);
            z0 = Math.Min(z0, z);
            z1 = Math.Max(z1, z);
        }
        return (x0, y0, z0, x1, y1, z1, xf.Px, xf.Py, xf.Pz);
    }

    /// <summary>Gets the centre of the shape box in world space.</summary>
    /// <returns>The centre.</returns>
    protected (double x, double y, double z) BoxCenter()
    {
        if (Shape == null)
            return (X, Y, Z);
        return PoseXf().Apply(Shape.Cx, Shape.Cy, Shape.Cz);
    }

    /// <summary>The driver pose of a Vehicle: the datablock int +0x174 <c>driverPose</c> (Vehicle_getDriverPose, vtable +0x168), 0 without a datablock.</summary>
    public int DriverPose => Block == null ? 0 : (int)Db.Num(Block, "driverPose");

    /// <summary>True when the driver is visible: the datablock byte +0x170 <c>visibleDriver</c> (Vehicle_isDriverVisible 004453cc, vtable +0x164).</summary>
    public bool DriverVisible => Block == null || Db.Flag(Block, "visibleDriver");

    /// <summary>True when the visual has a shape instance.</summary>
    public bool HasShapeInstance => Inst != null && Shape != null;

    /// <summary>Reads the inputs of Vehicle_getCameraTransform (00445d88): the object transform as drawn (Euler x, y, z and the position, rotation and translation flags) and the <c>dummy eye</c> node transform (animated, in object space).</summary>
    /// <param name="obj">The object transform.</param>
    /// <param name="eye">The eye node transform, or null without that node.</param>
    /// <returns>True when the visual has a shape instance.</returns>
    public bool TryCameraInputs(out Sim.Transform obj, out Sim.Transform? eye) =>
        TryNodeInputs(s => s.FindNodeBase("dummy eye", 0), out obj, out eye, out _);

    /// <summary>Reads the object transform as drawn and the animated transform (object space) of the node a finder picks.</summary>
    /// <param name="find">Picks a node of the shape.</param>
    /// <param name="obj">The object transform.</param>
    /// <param name="node">The node transform, or null when the finder picks none.</param>
    /// <param name="haveShape">False while the visual has no shape instance (the stock vtable +0x10c then reports no transform).</param>
    /// <returns>True when the transforms are valid.</returns>
    public bool TryNodeInputs(Func<RenderShape, int> find, out Sim.Transform obj, out Sim.Transform? node, out bool haveShape)
    {
        obj = new Sim.Transform();
        node = null;
        haveShape = Inst != null && Shape != null;
        if (!HavePos)
            return false;
        Sim.MissionTransform.EulerToMatrix([(float)Roll, (float)Pitch, (float)Yaw], ref obj);
        obj.Flags |= 3u;
        obj.P[0] = (float)X;
        obj.P[1] = (float)Y;
        obj.P[2] = (float)Z;
        var n = haveShape ? find(Shape!) : -1;
        if (n < 0)
            return true;
        Inst!.Animate(0);
        var e = Inst.NodeWorld[n];
        var t = new Sim.Transform { Flags = 3u };
        double[] m = [e.M0, e.M1, e.M2, e.M3, e.M4, e.M5, e.M6, e.M7, e.M8];
        for (var i = 0; i < 9; ++i)
            t.M[i] = (float)m[i];
        t.P[0] = (float)e.Px;
        t.P[1] = (float)e.Py;
        t.P[2] = (float)e.Pz;
        node = t;
        return true;
    }

    /// <summary>Builds the pose of the object: Euler angles at the position.</summary>
    /// <returns>The transform to the world.</returns>
    protected virtual Xf PoseXf() => Xf.Euler(Roll, Pitch, Yaw + SpinZ).Then(Xf.Translate(X, Y, Z));

    /// <inheritdoc/>
    public override void Tick(double dt)
    {
        Age += dt;
        if (Inst == null)
            return;
        if (Group is 5 or 23 && Num("rotates") != 0 && Num("atRest", 1) != 0)
            SpinZ += 3.0 * dt;   // Item_clientUpdate: spin about z at 3 rad/s (3.5)
        foreach (var t in Slots)
            if (t != null)
                Inst.Advance(t, dt);
        if (Ambient != null)
            Inst.Advance(Ambient, dt);
        Visibility?.Position = DamageState == 3 ? 1.0 : 0.0;
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
        var dir = fadeIn ? -1 : 1;
        double now = M.Time, elapsed = now - FadeStart;
        if (FadeStart < 0 || FadeDir == 0 || (FadeDir < 0 && elapsed >= FadeSeconds))
            FadeStart = now;
        else if (dir != FadeDir)
            FadeStart = elapsed <= FadeSeconds ? now - (FadeSeconds - elapsed) : now;
        FadeDir = dir;
    }

    // The fade runs 2500 ms (the divisor at 0061a2fc).
    private const double FadeSeconds = 2.5;

    /// <summary>The light fade of the object: Item::updateLight (00451bb0) multiplies the pulse by 1 - fade, so the light fades with the shape on hide and show.</summary>
    public double LightFade => 1 - FadeAmount();

    /// <summary>Gets the fade amount of the shape, 0 fully visible to 1 gone.</summary>
    /// <returns>The fade amount.</returns>
    protected virtual double FadeAmount()
    {
        if (FadeStart < 0)
            return 0;
        var f = (M.Time - FadeStart) / FadeSeconds;
        if (FadeDir > 0)
            return Math.Clamp(f, 0, 1);
        return Math.Clamp(1 - f, 0, 1);
    }

    /// <summary>The damage level of the skin.</summary>
    protected virtual int DamageLevel => Skins.DamageLevel(Damage, M.Assets.DamageSkinDetail);

    /// <summary>The smoothed state of the light probe of this object.</summary>
    protected readonly ProbeState mProbe = new();

    /// <summary>Builds the light set of the main image of the object: the scene light adjusted by the probe state.</summary>
    /// <param name="ctx">The frame context.</param>
    /// <returns>The light set.</returns>
    /// <remarks>Items and vehicles probe again every 250 ms (Item_queryRender, Vehicle_renderQuery). The other shapes probe once (ShapeBase_processQuery).</remarks>
    protected LightSet ProbedLight(FrameContext ctx)
    {
        var probe = M.Probe;
        var box = probe == null ? null : WorldBox;
        if (probe == null || box == null)
            return ctx.Light;
        var repeat = Group is 5 or 23 or 9 or 10;
        if (mProbe.LastUpdate < 0 || (Group == 3 && M.Time != mProbe.LastUpdate) || (repeat && M.Time - mProbe.LastUpdate >= 0.25))
        {
            var (ok, rgb, strength) = probe.Query(box);
            mProbe.Update(ok, rgb, strength);
            mProbe.LastUpdate = M.Time;
        }
        return mProbe.Active ? ctx.Light.WithProbe(mProbe.Colour.r, mProbe.Colour.g, mProbe.Colour.b, mProbe.Strength) : ctx.Light;
    }

    /// <inheritdoc/>
    public override void Build(FrameContext ctx)
    {
        if (Inst == null || Shape == null || !HavePos || Hidden)
            return;
        var d = ctx.Draw(PoseXf(), Shape.Name);
        d.Light = ProbedLight(ctx);
        d.Skin = Skin;
        d.DamageLevel = Group is 3 or 4 or 19 ? DamageLevel : 0;
        d.Fade = FadeAmount();
        d.DetailScale = 1;
        var img = ctx.Renderer.Build(Inst, ctx.Camera, d);
        if (img != null)
            ctx.Lists.Add(img);
    }
}
