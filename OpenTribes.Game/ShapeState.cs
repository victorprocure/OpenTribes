// Port of src/game/shape_state.hpp and src/game/shape_state.cpp: shape instances of the server's ShapeBase objects
// (docs/collision.md 6 T1.3): the node entries and object states a line test, a muzzle or an eye transform reads, kept
// in step with the object's animation threads, the Player threads (animation and look, docs/player-sim.md 5.5)
// included.
using OpenTribes.Core;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public static class ShapeState
{
    // src/game/shape_state.cpp Slot.
    private sealed class Slot
    {
        public int Id;
        public ShapeModel? Model;
        public ShapeInstance Inst = new();
        public List<ShapeThread> Applied = new();
        public byte ViewMode;
    }

    private static readonly Dictionary<GameBase, Slot> sSlots = new();
    private static readonly Dictionary<string, ShapeInstance?> sStatic = new(StringComparer.Ordinal);

    // src/game/shape_state.cpp shapeNameOf.
    private static string ShapeNameOf(GameState game, GameBase obj)
    {
        if (game.Sim == null || obj.DataBlockName == null)
            return "";
        DataBlockManager.Block? b = game.Sim.DataBlocks().Find(obj.DataBlockName);
        string shape = "";
        if (b != null)
            game.Sim.DataBlocks().GetField(b, "shapeFile", "", out shape);
        return shape;
    }

    // src/game/shape_state.cpp sameThreads.
    private static bool SameThreads(List<ShapeThread> a, List<ShapeThread> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; ++i)
            if (a[i].Sequence != b[i].Sequence || a[i].Priority != b[i].Priority ||
                BitConverter.SingleToUInt32Bits(a[i].Position) != BitConverter.SingleToUInt32Bits(b[i].Position))
                return false;
        return true;
    }

    // src/game/shape_state.cpp normalisePosition: position of a thread after the sequence normalises it (0x005b8d94):
    // wrap by floor when cyclic, clamp to 0..1 otherwise.
    private static float NormalisePosition(ShapeModel model, int sequence, float position)
    {
        bool cyclic = sequence >= 0 && sequence < model.Sequences.Length && model.Sequences[sequence].Cyclic;
        if (position < 0.0f || position > 1.0f)
        {
            if (!cyclic)
                return position < 0.0f ? 0.0f : 1.0f;
            double fl = Math.Floor((double)position);
            return X87.F32(Sub(position, fl));
        }
        return position;
    }

    // src/game/shape_state.cpp sequencePriority.
    private static int SequencePriority(ShapeModel model, int sequence) =>
        sequence >= 0 && sequence < model.Sequences.Length ? model.Sequences[sequence].Priority : 0;

    // src/game/shape_state.cpp collectThreads: the instance threads of an object in creation order, with their current
    // positions.
    private static void CollectThreads(GameBase obj, ShapeModel model, List<ShapeThread> output, out byte viewMode)
    {
        viewMode = 0;
        if (Player.From(obj) is Player pl)
        {
            // Animation thread (+0xd54), then the look thread (+0xd58), as the last Player_updateAnimation left them
            // (docs/player-sim.md 5.5).
            Player.ShapePose pose = pl.Pose;
            int animSeq = pose.AnimIndex >= 0 && pose.AnimIndex < 51 ? pl.PData.AnimSequence[pose.AnimIndex] : 0;
            output.Add(new ShapeThread(animSeq, NormalisePosition(model, animSeq, pose.AnimPosition), SequencePriority(model, animSeq)));
            int lookSeq;
            if (pose.LookSet)
            {
                lookSeq = model.FindSequence(pose.CrouchLooks ? "crouch looks" : "looks");
                if (lookSeq < 0)
                    lookSeq = 0; // a missing sequence gives handle 0
            }
            else
            {
                lookSeq = 0;
            }
            output.Add(new ShapeThread(lookSeq, NormalisePosition(model, lookSeq, pose.LookPosition), pose.FirstPerson ? -1 : 5000));
            viewMode = (byte)(pose.FirstPerson ? 2 : 0);
            return;
        }
        void AddThread(int sequence, float position) =>
            output.Add(new ShapeThread(sequence, NormalisePosition(model, sequence, position), SequencePriority(model, sequence)));
        // StaticBase: the visibility thread (+0x7e0) sits at 0, and at 1 once the object is destroyed
        // (StaticBase_setDamageState 0x0044fae8).
        if (obj.VisibilityThread)
            AddThread(model.FindSequence("visibility"), obj.DamageState == GameBase.kDestroyed ? 1.0f : 0.0f);
        // The three script threads (docs/static-sim.md 1.7).
        foreach (GameBase.Thread th in obj.Threads)
            if (th.Created)
                AddThread(th.Sequence, th.Position);
        if (GameUtil.EqualsNoCase(obj.ClassName(), "Turret"))
        {
            // Turret threads in creation order (0x00452970): turn, elevate, power / fire, at the positions the object
            // keeps (docs/static-sim.md 14.6). The power / fire thread has priority -1 on `power` and 0 on `fire`.
            int Seq(string name)
            {
                int s = model.FindSequence(name);
                return s < 0 ? 0 : s; // a missing sequence gives handle 0
            }
            if (obj.TurretTurnPos >= 0)
                AddThread(Seq("turn"), obj.TurretTurnPos);
            if (obj.TurretElevatePos >= 0)
                AddThread(Seq("elevate"), obj.TurretElevatePos);
            if (obj.TurretHasPower)
            {
                AddThread(Seq(obj.TurretPowerOnFire ? "fire" : "power"), obj.TurretPowerPos);
                ShapeThread last = output[^1];
                last.Priority = obj.TurretPowerOnFire ? 0 : -1;
                output[^1] = last;
            }
        }
    }

    // src/game/shape_state.cpp shapeInstanceOf: instance of the object's datablock shape with its current thread state,
    // animated. Null when the object has no shape. Valid until the next call for another object evicts it.
    public static ShapeInstance? ShapeInstanceOf(GameState game, GameBase obj)
    {
        if (game.Resources == null)
            return null;
        string name = ShapeNameOf(game, obj);
        ShapeModel? model = name.Length == 0 ? null : Shape.GetShapeModel(game.Resources, name);
        if (model == null)
            return null;
        if (sSlots.Count > 4096)
        {
            // Entries of objects that left the container are stale.
            foreach (GameBase k in sSlots.Keys.ToList())
                if (game.World.Find(k) == null)
                    sSlots.Remove(k);
        }
        if (!sSlots.TryGetValue(obj, out Slot? slot) || slot.Id != obj.Id() || slot.Model != model)
        {
            slot = new Slot { Id = obj.Id(), Model = model };
            slot.Inst.Build(model);
            // Player ctor (0x0040fd5d): the lowerback nodes hang under the override matrix.
            if (Player.From(obj) != null)
            {
                slot.Inst.SetOverride("lowerback", "dummyalways root");
                // ShapeBase_resetRootTransform (0x0044b7c4), called at the datablock bind and before every animate of
                // Player_updateAnimation: instance +0xa0 is the identity with no flags, not the default transform of
                // node 0 (larmor: 0.026 0.040 -0.027).
                slot.Inst.Root = new TMat3();
            }
            slot.Applied.Clear();
            slot.ViewMode = 0xff;
            sSlots[obj] = slot;
        }
        var threads = new List<ShapeThread>();
        CollectThreads(obj, model, threads, out byte viewMode);
        if (!SameThreads(threads, slot.Applied) || viewMode != slot.ViewMode)
        {
            slot.Applied = threads;
            slot.ViewMode = viewMode;
            slot.Inst.Threads = new List<ShapeThread>(threads);
            slot.Inst.ViewMode = viewMode;
            slot.Inst.Animate();
        }
        return slot.Inst;
    }

    // src/game/shape_state.cpp staticShapeInstance: instance of a named shape with no threads (item images), animated once
    // at build.
    public static ShapeInstance? StaticShapeInstance(GameState game, string shape)
    {
        if (game.Resources == null || shape.Length == 0)
            return null;
        if (!sStatic.TryGetValue(shape, out ShapeInstance? inst))
        {
            inst = null;
            if (Shape.GetShapeModel(game.Resources, shape) is ShapeModel model)
            {
                inst = new ShapeInstance();
                inst.Build(model);
            }
            sStatic[shape] = inst;
        }
        return inst;
    }

    // src/game/shape_state.cpp shapeObjectLine: line test of the object's ShapeCollisionImage (docs/interiors.md 9.1,
    // docs/collision.md T3 case 3): false when the object has no image for rays or the line misses it.
    public static bool ShapeObjectLine(GameState game, GameBase obj, Vec3 s, Vec3 e, ref Vec3 hit, ref Vec3 normal)
    {
        // 0x0044fff0: no image when collision is disabled or the object is destroyed, unless the shape has a visibility
        // thread.
        if (game.Sim == null || obj.DataBlockName == null)
            return false;
        DataBlockManager.Block? b = game.Sim.DataBlocks().Find(obj.DataBlockName);
        if (b == null)
            return false;
        bool disabled = game.Sim.DataBlocks().GetField(b, "disableCollision", "", out string v) && Script.Numbers.ParseBool(v);
        if ((disabled || obj.DamageState == GameBase.kDestroyed) && !obj.VisibilityThread)
            return false;
        // Item_applyCollideable (0x00451574): rays skip a non-collideable Item, and an Item image is always in bounds
        // mode.
        bool item = GameUtil.EqualsNoCase(obj.ClassName(), "Item") || GameUtil.EqualsNoCase(obj.ClassName(), "Mine");
        if (item && !obj.ItemCollideable)
            return false;
        ShapeInstance? inst = ShapeInstanceOf(game, obj);
        if (inst == null)
            return false;
        var tm = new TMat3();
        tm.Flags = obj.Transform.Flags;
        tm.M = obj.Transform.M;
        tm.P = obj.Transform.P;
        Span<float> S = stackalloc float[3] { s.X, s.Y, s.Z }, E = stackalloc float[3] { e.X, e.Y, e.Z };
        Span<float> p = stackalloc float[3], n = stackalloc float[3];
        if (!Shape.ShapeLine(inst, tm, S, E, !item, p, n))
            return false;
        hit = new Vec3(p[0], p[1], p[2]);
        normal = new Vec3(n[0], n[1], n[2]);
        return true;
    }

    // Test reset: the instance caches of a fresh process.
    public static void Reset()
    {
        sSlots.Clear();
        sStatic.Clear();
    }
}
