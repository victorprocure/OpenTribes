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
    protected RenderThread? Ambient, Visibility, Turn, Elevate;
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
        Ambient = Visibility = Turn = Elevate = null;
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
            }
            if (Group is 5 or 23 && Shape.FindSequence("ambient") is int ia && ia >= 0)
                Ambient = Inst.AddThread(ia, M.Rng.NextDouble(), 0);
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
        if (Has("pos") && u.Fields.Any(f => f.Name == "pos"))
        {
            double[] p = Vec("pos");
            (X, Y, Z) = (p[0], p[1], p[2]);
            HavePos = true;
        }
        if (u.Fields.Any(f => f.Name == "rot"))
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
        {
            long fv = (long)Num("fade");
            FadeStart = M.Time;
            FadeDir = fv == 2 ? -1 : 1;
        }
        if (Group == 19)
            ApplyTurret(u);
        if (u.Fields.Any(f => f.Name == "damageState"))
        {
            int ds = (int)Num("damageState");
            // StaticBase_setDamageState: a state of 0 (Disabled) 2, Destroyed 3 in the client numbering of the ghost.
            int state = ds == 1 ? 1 : ds == 2 ? 2 : ds == 0 ? 3 : ds;
            if (state != DamageState && !isNew && state == 3)
                Destroyed();
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
        // Turret_setPowerSequence [code Turret_setPowerSequence, Turret_setFireSequence]: the thread of slot 0 of a turret runs the power sequence at priority -1 (above the
        // turn and elevate threads that drive the same nodes), Turret_setFireSequence the fire sequence at priority 0.
        if (Group == 19 && s.Slot == 0)
            t.Priority = Shape?.SequenceName(s.Sequence) == "power" ? -1 : 0;
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

    private void ApplyTurret(DecodedGhost u)
    {
        if (u.Fields.Any(f => f.Name == "rotation") && Turn != null)
            Turn.Position = Math.Clamp(Num("rotation"), 0, 1);
        if (u.Fields.Any(f => f.Name == "elevation") && Elevate != null)
            Elevate.Position = Math.Clamp(Num("elevation"), 0, 1);
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
        if (debris >= 0)
        {
            M.Effects.Add(new DebrisEffect(Inst, PoseXf(), 0, 0, 0, M.Terrain, M.Rng));
            Hidden = true;
        }
    }

    // The bounding sphere of the shape at its pose: the stand-in for the object's world box in the crosshair label pick.
    public (double X, double Y, double Z, double R)? HudSphere()
    {
        if (!HavePos || Shape == null)
            return null;
        var c = PoseXf().Apply(Shape.Cx, Shape.Cy, Shape.Cz);
        return (c.x, c.y, c.z, Shape.Radius);
    }

    // The crosshair label anchor of the stock pick [code 004aca84]: the centre of the world box in x and y, its top in z.
    public (double X, double Y, double Z)? HudTop()
    {
        if (!HavePos || Shape == null)
            return null;
        Xf xf = PoseXf();
        double x0 = double.MaxValue, y0 = x0, x1 = -x0, y1 = x1, z1 = x1;
        for (int i = 0; i < 8; ++i)
        {
            var c = xf.Apply(Shape.Box[(i & 1) * 3], Shape.Box[1 + (i >> 1 & 1) * 3], Shape.Box[2 + (i >> 2 & 1) * 3]);
            x0 = Math.Min(x0, c.x); x1 = Math.Max(x1, c.x);
            y0 = Math.Min(y0, c.y); y1 = Math.Max(y1, c.y);
            z1 = Math.Max(z1, c.z);
        }
        return ((x0 + x1) * 0.5, (y0 + y1) * 0.5, z1);
    }

    protected (double x, double y, double z) BoxCenter()
    {
        if (Shape == null)
            return (X, Y, Z);
        return PoseXf().Apply(Shape.Cx, Shape.Cy, Shape.Cz);
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
    }

    protected virtual double FadeAmount()
    {
        if (FadeStart < 0)
            return 0;
        double f = (M.Time - FadeStart) / 2.5;
        if (FadeDir > 0)
            return Math.Clamp(f, 0, 1);
        return Math.Clamp(1 - f, 0, 1);
    }

    protected virtual int DamageLevel => Skins.DamageLevel(Damage, M.Assets.DamageSkinDetail);

    public override void Build(FrameContext ctx)
    {
        if (Inst == null || Shape == null || !HavePos || Hidden)
            return;
        ShapeDraw d = ctx.Draw(PoseXf(), Shape.Name);
        d.Skin = Skin;
        d.DamageLevel = Group is 3 or 4 or 19 ? DamageLevel : 0;
        d.Fade = FadeAmount();
        d.DetailScale = 1;
        RenderImage? img = ctx.Renderer.Build(Inst, ctx.Camera, d);
        if (img != null)
            ctx.Lists.Add(img);
    }
}
