// Port of src/game/displace.hpp and src/game/displace.cpp (Moveable displacement of riders and overlapping objects,
// docs/collision.md E3, docs/static-sim.md 2.5: SimMovement_displace 0x00505ed4, the rider and overlap phases 0x0050613c
// and 0x005062e4, the push 0x005064f8 and the blocking test of doors 0x00505790), of src/game/move_candidates.cpp (the
// candidate list of a stepped move, docs/collision.md 2.1, 2.2) and of the collision event of src/game/player.cpp
// (GameState::postCollisionEvent and dispatchCollisionEvent, docs/collision.md 8 E1). The Player and Vehicle branches
// are stage P4b and marked where they sit.
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public static class Displace
{
    // 1/3 as the 80-bit constant at 0x005067b4 (mantissa 0xAAAAAAAAAAAAAAAB), a double here.
    private const double kThird = 1.0 / 3.0;

    // src/game/displace.cpp pushBy: SimMovement_pushBy (0x005064f8): moves an object by `delta` through up to three
    // stepped moves. `mover` is the velocity of the Moveable (SimMovement_matchVelocity, 0x00505008, raises the object's
    // velocity along it), `box` the object box (+0x1d4) that sets the sub-step lengths, `position` the live position of
    // the object. `stepMove` runs the object's own stepped move toward a target with the two sub-step lengths and reports
    // whether it was not blocked in every iteration. True when the whole displacement was done.
    public static bool PushBy(ReadOnlySpan<float> deltaIn, ReadOnlySpan<float> mover, float[] box, float[] velocity, ref bool atRest,
                              Func<int, float> position, Func<float[], float, float, bool> stepMove)
    {
        float[] delta = { deltaIn[0], deltaIn[1], deltaIn[2] };
        // Sub-steps: one third of the sum of the box extents, and half of it.
        double extent = Sub(box[3], box[0]);
        extent = Add(extent, Sub(box[4], box[1]));
        extent = Add(extent, Sub(box[5], box[2]));
        float step0 = X87.F32(Mul(kThird, extent));
        float step1 = X87.F32(Mul(step0, 0.5f));
        float[] target = new float[3], goal = new float[3], moved = new float[3];
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            // SimMovement_matchVelocity (0x00505008): raise the velocity along the mover's to the mover's.
            float uu = X87.F32(X87.Dot(mover, mover));
            if (uu != 0.0f)
            {
                float ov = X87.F32(X87.Dot(mover, velocity));
                if (ov < uu)
                {
                    float factor = X87.F32(Div(ov, uu));
                    for (int i = 0; i < 3; ++i)
                        velocity[i] = X87.F32(Add(Sub(mover[i], Mul(mover[i], factor)), velocity[i]));
                    atRest = false;
                }
            }
            for (int i = 0; i < 3; ++i)
                goal[i] = target[i] = X87.F32(Add(delta[i], position(i)));
            if (!stepMove(target, step0, step1))
                return false;
            for (int i = 0; i < 3; ++i)
            {
                float shortfall = X87.F32(Sub(position(i), goal[i]));
                moved[i] = X87.F32(Add(delta[i], shortfall));
            }
            float l = X87.F32(X87.Len(delta));
            float d = X87.F32(X87.Dot(moved, delta));
            if (!(Mul(l, l) > d))
                return true;
            float scale = X87.F32(Div(d, Mul(l, l)));
            for (int i = 0; i < 3; ++i)
            {
                float part = X87.F32(Mul(scale, delta[i]));
                delta[i] = X87.F32(Sub(delta[i], part));
            }
        }
        return false;
    }
}

public sealed partial class GameState
{
    // src/game/displace.cpp Record.
    private sealed class DisplaceRecord
    {
        public GameBase Object = null!;
        public Transform Xf;
        public float[] Velocity = new float[3];
        public int Flags;
    }

    // src/game/displace.cpp typeOf.
    private uint ContainerTypeOf(GameBase gb) => World.Find(gb)?.Type ?? 0;

    // src/game/displace.cpp sphereImageOf: a ShapeBase object as the image that runs a shape/shape test: its bounding
    // sphere (image +0x64, +0x70) under its transform.
    private static MoverImage SphereImageOf(GameBase gb, in Float9 rot, in Float3 pos)
    {
        var output = new MoverImage();
        output.Pos = pos;
        output.Rot = rot;
        output.BoxMin = Float3.Of(gb.ShapeBox.AsSpan(0, 3));
        output.BoxMax = Float3.Of(gb.ShapeBox.AsSpan(3, 3));
        output.Radius = gb.ShapeRadius;
        output.CentreLocal = Float3.Of(gb.ShapeCenter);
        output.ShapeMover = true;
        return output;
    }

    // src/game/displace.cpp imagesOverlap: SimCollisionImage_test (0x005036a4) of the mover's image against the image of
    // `o`, as the overlap phase of a displace runs it. Shape against shape is the mover's bounding sphere against the
    // candidate (0x0043873c): an Item or Mine in bounds mode, and the hull of a vehicle when the mover is a vehicle.
    // `moved`: the mover's shape image at another transform (the door test), else its current one.
    private bool ImagesOverlap(GameBase mover, GameBase o, CollisionImage? moved = null)
    {
        var hit = new CollisionHit();
        // P4b: Player: a Player mover tests its sphere (Player::moverImage) against the other Player's sphere or the
        // other side's shape image.
        CollisionImage image;
        if (moved != null)
        {
            image = moved;
        }
        else
        {
            image = new CollisionImage();
            if (!ShapeImage(mover, image))
                return false;
        }
        // P4b: Player: a Player candidate tests its own sphere against the mover's image (Player::moverImage).
        GameBase.ShapeKind cls = o.ShapeClass();
        bool hulls = cls == GameBase.ShapeKind.Vehicle && mover.ShapeClass() == GameBase.ShapeKind.Vehicle;
        if (cls != GameBase.ShapeKind.Item && cls != GameBase.ShapeKind.Mine && !hulls)
            return false;
        var other = new CollisionImage();
        if (!ShapeImage(o, other))
            return false;
        MoverImage self = SphereImageOf(mover, image.Rot, image.Pos);
        return Collision.TestImage(self, other, hit);
    }

    // src/game/displace.cpp isItemClass.
    private static bool IsItemClass(GameBase o) =>
        o.ShapeClass() == GameBase.ShapeKind.Item || o.ShapeClass() == GameBase.ShapeKind.Mine;

    // src/game/displace.cpp pushObject.
    private static bool PushObject(GameBase o, ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        // P4b: Player: Player::pushBy. P4b: Vehicle: Vehicle::pushBy.
        if (IsItemClass(o))
            return o.ItemPushBy(delta, mover);
        // A StaticBase object (StaticShape, Sensor, Turret, Moveable) or a Trigger, pushed by zero by a Player that a
        // displace has pushed into it (SimMovement_pushBy 0x005064f8 with the default hooks): bits 0x05 of +0x164 go,
        // the stepped move toward the object's own position has no sub-step and succeeds after it has written the
        // transform back with the matrix flags 3.
        o.ItemFlags &= ~5;
        o.AtRest = false;
        o.ItemContact = false;
        o.Transform.Flags |= 3;
        return true;
    }

    // src/game/displace.cpp displaceMaskOf: collision mask (+0x1ec) of an object that runs a displace of its own after a
    // push: StaticBase_bindShape 0x0044f68c for the StaticBase family, the Item mask 0x20c00086, 0 for a Trigger.
    private static uint DisplaceMaskOf(GameBase o)
    {
        if (IsItemClass(o))
            return 0x20c00086u;
        if (o.GetKind() == GameBase.Kind.Trigger || !o.IsStaticBase())
            return 0;
        return 0x24a00080u;
    }

    // Recursion limit of a displace (D11).
    private const int kMaxDisplaceDepth = 32;

    // src/game/displace.cpp GameState::shapeImage: the shape image of a ShapeBase object (docs/collision.md T1.2) at its
    // current transform.
    public override bool ShapeImage(SimObject o, CollisionImage image)
    {
        if (o is not GameBase gb)
            return false;
        image.Rot = gb.Transform.M;
        image.Pos = gb.Transform.P;
        image.Object = gb;
        DataBlockManager.Block? b = Sim != null && gb.DataBlockName != null ? Sim.DataBlocks().Find(gb.DataBlockName) : null;
        Span<float> box = stackalloc float[6];
        if (b == null || Resources == null || !Sim!.DataBlocks().GetField(b, "shapeFile", "", out string shape) || shape.Length == 0 ||
            !Shape.ShapeBox(Resources, shape, box))
            return false;
        image.Box = Float6.Of(box);
        image.ImageKind = CollisionImage.Kind.Shape;
        image.Boxes = Shape.ShapeCollisionBoxes(Resources, shape);
        GameBase.ShapeKind cls = gb.ShapeClass();
        image.NonBlocking = cls == GameBase.ShapeKind.Item && !gb.ItemCollideable;
        // StaticBase_bindShape (0x0044f68c): the collision detail, else detail 0. Item_applyCollideable (0x00451574, also
        // the Mine bind) puts the image in bounds mode.
        image.Instance = ShapeState.ShapeInstanceOf(this, gb);
        image.Detail = image.Instance?.ShapeData != null ? image.Instance.ShapeData.CollisionDetail() : 0;
        image.MeshMode = cls != GameBase.ShapeKind.Item && cls != GameBase.ShapeKind.Mine;
        return true;
    }

    // src/game/displace.cpp GameState::moveableDisplace.
    public override bool MoveableDisplace(SimObject m, ReadOnlySpan<float> oldPos)
    {
        if (m is not GameBase mover)
            return true;
        using var chop = X87.Chop();
        float[] delta = new float[3];
        for (int i = 0; i < 3; ++i)
            delta[i] = X87.F32(Sub(mover.Transform.P[i], oldPos[i]));
        return DisplaceBy(mover, delta, true);
    }

    // src/game/displace.cpp GameState::displaceBy: SimMovement_displace 0x00505ed4 by `delta`. `top`: called by the
    // mover's own update (a Moveable, or Flier_update with the collision mask 0x80); otherwise for an object that a
    // displace has just pushed, with a zero delta and the object's own collision mask.
    public bool DisplaceBy(GameBase mover, float[] delta, bool top)
    {
        // P4b: Player: Player* playerMover = Player::from(&mover) and its branches below.
        bool vehicleMover = mover.ShapeClass() == GameBase.ShapeKind.Vehicle;
        {
            // No image (vtable +0xcc, the first image of query 0x407), no displace: a destroyed StaticBase object or one
            // whose datablock disables collision, and nothing to find with a Trigger's mask of 0.
            var image = new CollisionImage();
            if (!ShapeImage(mover, image) || (!top && !vehicleMover && DisplaceMaskOf(mover) == 0) ||
                (!vehicleMover && !IsItemClass(mover) && !StaticHasImage(mover)))
                return true;
        }
        if (mDisplaceHidden.Count >= kMaxDisplaceDepth)
            return true;
        SimManager? manager = mover.Manager();
        var records = new List<DisplaceRecord>();
        void Record(GameBase o)
        {
            var r = new DisplaceRecord { Object = o, Xf = o.Transform, Velocity = new[] { o.Velocity[0], o.Velocity[1], o.Velocity[2] } };
            // P4b: Player: a Player records its collision mask and rest flag.
            r.Flags = o.ItemFlags;
            records.Add(r);
        }
        // The object type of the mover is 0 while its two lists are walked: no query of a pushed object or of a nested
        // displace finds it. The type is back before a blocker callback and stays.
        mDisplaceHidden.Add(mover);
        bool hiddenHere = true;
        void Unhide()
        {
            if (!hiddenHere)
                return;
            hiddenHere = false;
            int it = mDisplaceHidden.LastIndexOf(mover);
            if (it >= 0)
                mDisplaceHidden.RemoveAt(it);
        }
        bool Hidden(GameBase o) => mDisplaceHidden.Contains(o);
        bool Overlaps(GameBase o) => ImagesOverlap(mover, o);
        float[] zero = { 0, 0, 0 };
        // Moveable::onBlocker (0x004449f0, vtable +0xbc): Items and corpses are never blockers.
        bool Blocker(GameBase o)
        {
            Unhide();
            // P4b: Player: a Player mover is always blocked (SimMovement_onBlocker 0x005067c4).
            if ((ContainerTypeOf(o) & 0x40200000u) != 0)
            {
                o.AtRest = false;
                return false;
            }
            // P4b: Vehicle: a vehicle mover answers with Vehicle_onBlocker (0x0044642c).
            ScriptCallback(mover, "onBlocker", new[] { o.Id().ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return true;
        }
        void Rollback()
        {
            Unhide();
            foreach (DisplaceRecord r in records)
            {
                GameBase o = r.Object;
                o.Transform = r.Xf;
                // P4b: Player: a Player takes back its collision mask and rest flag and refiles (Player::updateContainer).
                o.ItemFlags = r.Flags;
                ObjectMoved(o);
                for (int i = 0; i < 3; ++i)
                    o.Velocity[i] = r.Velocity[i];
            }
        }
        // Phase A (0x0050613c): the objects on the carry list. A rider that could not be pushed and is clear of the
        // mover's image displaces what it overlaps itself.
        var riders = new List<int>(mover.Carried);
        foreach (int id in riders)
        {
            if (manager?.FindById(id) is not GameBase o || ContainerTypeOf(o) == 0 || Hidden(o))
                continue;
            Record(o);
            if (!PushObject(o, delta, mover.Velocity) && (Overlaps(o) || !DisplaceBy(o, zero, false)) && Blocker(o))
            {
                Rollback();
                return false;
            }
        }
        // Phase B (0x005062e4): the other SimMovement objects whose image overlaps the mover's image, found with the
        // mover's collision mask without terrain, interiors and 0x40. Each one that was pushed then displaces what it
        // overlaps itself, with a zero delta.
        var found = new List<GameBase>();
        // P4b: Player: a Player mover tests every candidate image itself (displace.cpp, the playerMover branch).
        {
            Span<float> query = stackalloc float[6];
            uint overlapMask = (top ? 0x24a00080u : DisplaceMaskOf(mover)) & ~0x46u;
            if (vehicleMover)
            {
                // A Vehicle moves with the collision mask 0x80 during its own displace (Flier_update U16). Its object box
                // is turned by its matrix (bit 0x40 of +0x164, HF6). Flier::kHullMask is 0x34c00086.
                overlapMask = top ? 0x80u : (0x34c00086u & ~0x46u);
                Mat m = mover.WorldMat();
                Span<float> box = stackalloc float[6];
                Shape.RotatedWorldBox(mover.ShapeQueryBox, m, box);
                Collision.SweptBox(box[..3], box[3..], zero, mover.Velocity, 0.032f, query);
            }
            else
            {
                Collision.SweptBox(mover.ShapeQueryBox.AsSpan(0, 3), mover.ShapeQueryBox.AsSpan(3, 3), mover.Transform.P,
                                   mover.Velocity, 0.032f, query);
            }
            foreach (object? so in World.FindObjects(query, overlapMask, mover))
                if (so is GameBase o)
                    found.Add(o);
        }
        foreach (GameBase o in found)
        {
            if (Hidden(o) || riders.Contains(o.Id()) || !Overlaps(o))
                continue;
            Record(o);
            if ((!PushObject(o, delta, mover.Velocity) || !DisplaceBy(o, zero, false)) && Blocker(o))
            {
                Rollback();
                return false;
            }
        }
        Unhide();
        // Commit: onDisplaced (Player 0x00416574, Item 0x00451978, a vehicle has the empty default 0x005067cc) and the
        // saved velocity.
        foreach (DisplaceRecord r in records)
        {
            GameBase o = r.Object;
            // P4b: Player: a pushed Player sets mask 0x12000.
            if (IsItemClass(o))
            {
                o.AtRest = false;
                o.SetMaskBits(0x08);
            }
            for (int i = 0; i < 3; ++i)
                o.Velocity[i] = r.Velocity[i];
        }
        return true;
    }

    // src/game/displace.cpp GameState::moveableTest: SimMovement_testTransform (0x00505790) for a door: its shape image
    // at the new transform against the Players (and collideable Items) in its way.
    public override bool MoveableTest(SimObject m, ReadOnlySpan<float> rotation, ReadOnlySpan<float> position)
    {
        if (m is not GameBase mover)
            return true;
        using var chop = X87.Chop();
        var xf = new Transform();
        xf.Flags = 3;
        MissionTransform.EulerToMatrix(rotation, ref xf);
        for (int i = 0; i < 3; ++i)
            xf.P[i] = position[i];
        var image = new CollisionImage();
        if (!ShapeImage(mover, image))
            return true;
        image.Rot = xf.M;
        image.Pos = xf.P;
        var wm = new Mat();
        wm.M = xf.M;
        wm.P = new Vec3(position[0], position[1], position[2]);
        Span<float> box = stackalloc float[6];
        Shape.RotatedWorldBox(mover.ShapeQueryBox, wm, box);
        foreach (object? so in World.FindObjects(box, 0x24a00080u, mover))
        {
            if (so is not GameBase o)
                continue;
            // P4b: Player: a Player in the way is tested too.
            if (!(o.ShapeClass() == GameBase.ShapeKind.Item && o.ItemCollideable))
                continue;
            if (ImagesOverlap(mover, o, image))
                return false;
        }
        return true;
    }

    // src/game/move_candidates.cpp GameState::staticHasImage: StaticBase_getCollisionImages (0x0044fff0): a StaticBase
    // object has no image when its datablock disables collision or it is destroyed, unless its shape has a visibility
    // sequence.
    public bool StaticHasImage(GameBase gb)
    {
        string v = "";
        DataBlockManager.Block? b = gb.DataBlockName != null ? Sim!.DataBlocks().Find(gb.DataBlockName) : null;
        bool disabled = b != null && Sim!.DataBlocks().GetField(b, "disableCollision", "", out v) && Numbers.ParseBool(v);
        return !((disabled || gb.DamageState == GameBase.kDestroyed) && !gb.VisibilityThread);
    }

    // src/game/move_candidates.cpp GameState::collisionCandidates: candidate list of a stepped move (docs/collision.md
    // 2.1): the images of the objects of `mask` whose world box meets `query`, in container order, `mover` excluded.
    public override void CollisionCandidates(SimObject mover, uint mask, ReadOnlySpan<float> query, List<CollisionImage> output)
    {
        // InteriorShape world boxes are padded by 3.0 (2.2): widen the container query and gate every object on its own
        // box.
        Span<float> wide = stackalloc float[6];
        for (int i = 0; i < 3; ++i)
        {
            wide[i] = SubF(query[i], 3.5f);
            wide[i + 3] = AddF(query[i + 3], 3.5f);
        }
        // Objects other than interiors are gated by the container query itself, whose database tests are not symmetric
        // (an object whose box starts exactly at the query's end is not found: 0x004fc3fc, docs/collision.md 11.8).
        List<object?> exact = World.FindObjects(query, mask, mover);
        Span<float> padded = stackalloc float[6];
        foreach (object? obj in World.FindObjects(wide, mask, mover))
        {
            Container.Entry? e = World.Find(obj);
            if (e == null)
                continue;
            var image = new CollisionImage();
            image.Object = obj;
            image.Type = e.Type;
            if (e.TerrainData != null)
            {
                if (!Collision.BoxesOverlap(e.Box, query))
                    continue;
                image.ImageKind = CollisionImage.Kind.Terrain;
                image.TerrainData = e.TerrainData;
                output.Add(image);
                continue;
            }
            if (obj is not GameBase gb)
                continue;
            image.Rot = gb.Transform.M;
            image.Pos = gb.Transform.P;
            if (gb.IsInterior())
            {
                if (gb.InteriorGeo == null || gb.CollisionDisabled)
                    continue;
                Collision.InteriorWorldBox(gb.InteriorGeo.Box, image.Rot, image.Pos, padded);
                if (!Collision.BoxesOverlap(padded, query))
                    continue;
                image.ImageKind = CollisionImage.Kind.Interior;
                image.Geometry = gb.InteriorGeo;
                output.Add(image);
                continue;
            }
            if (!exact.Contains(obj))
                continue;
            ++e.ImageQueries; // SimMovement_collectImages 0x0050693c: query 0x407 on every object found
            // P4b: Player: a Player candidate is its sphere image (move_candidates.cpp, the Player::from branch).
            if (gb.GetKind() == GameBase.Kind.Trigger)
            {
                // Trigger_getCollisionImages (0x00487384): the sphere image (centre at the origin, radius = the smallest
                // box extent, 0x00486df8) or the box image, with the object transform. Neither blocks.
                image.NonBlocking = true;
                if (gb.TriggerSphere)
                {
                    image.ImageKind = CollisionImage.Kind.TriggerSphere;
                    image.Radius = Math.Min(SubF(gb.TriggerBox[3], gb.TriggerBox[0]),
                                            Math.Min(SubF(gb.TriggerBox[4], gb.TriggerBox[1]), SubF(gb.TriggerBox[5], gb.TriggerBox[2])));
                }
                else
                {
                    image.ImageKind = CollisionImage.Kind.Trigger;
                    image.Box = Float6.Of(gb.TriggerBox);
                }
            }
            else
            {
                // Shape image (ShapeCollisionImage): the shape instance with the collision detail and mode. A
                // non-collideable Item never blocks (image byte +0xd).
                uint type = image.Type;
                if (!ShapeImage(gb, image))
                    continue;
                image.Type = type;
                if (gb.IsStaticBase() && gb.ShapeClass() != GameBase.ShapeKind.Vehicle)
                {
                    // StaticBase_getCollisionImages (0x0044fff0): no image when the datablock disables collision or the
                    // object is destroyed, unless the shape has a visibility sequence. A triggerRadius adds a sphere
                    // image that never blocks.
                    if (!StaticHasImage(gb))
                        continue;
                    if (gb.ShapeTriggerRadius != 0.0f)
                    {
                        output.Add(image);
                        var sphere = new CollisionImage();
                        sphere.ImageKind = CollisionImage.Kind.TriggerSphere;
                        sphere.Object = obj;
                        sphere.Type = type;
                        sphere.NonBlocking = true;
                        sphere.Rot = image.Rot;
                        sphere.Pos = image.Pos;
                        sphere.Centre = Float3.Of(gb.ShapeCenter);
                        sphere.Radius = gb.ShapeTriggerRadius;
                        image = sphere;
                    }
                }
            }
            output.Add(image);
        }
    }

    // src/game/player.cpp typeOf: object type (+0x48) of a GameBase in container 7.
    private static uint TypeOf(GameBase gb)
    {
        // P4b: Player: a Player answers its own object type (+0x48).
        if (gb.IsInterior())
            return ObjectType.kInteriorType;
        return ContainerType(gb);
    }

    // src/game/player.cpp GameState::postCollisionEvent: event 0x41f (8 E1): queued at the manager's current time, so it
    // runs at the start of the next tick; dropped when the sender is gone by then.
    public override void PostCollisionEvent(SimObject? target, SimObject? sender)
    {
        SimManager? m = target?.Manager();
        if (m == null || sender == null)
            return;
        int senderId = sender.Id();
        m.PostEvent(target, m.Time(), () =>
        {
            if (m.FindById(senderId) != sender)
                return;
            if (target is GameBase t && sender is GameBase s)
                DispatchCollisionEvent(t, s);
        }, sender);
    }

    // src/game/player.cpp dispatchCollisionEvent: event 0x41f at its target (docs/collision.md 8 E1.3-E1.4): the class
    // handler, the 1 s throttle and the script onCollision(target, sender).
    public void DispatchCollisionEvent(GameBase target, GameBase sender)
    {
        // Handlers (vtable +0xe8) by class and the type mask of `other` (8 E1.4).
        uint other = TypeOf(sender);
        uint mask;
        // P4b: Player: a Player target takes the mask 0x64a00080 (0x0060fb68).
        if (target.GetKind() == GameBase.Kind.Trigger)
        {
            target.TriggerCollide(sender); // Trigger_onCollision 0x004874b4 (static-sim 6.2): no script onCollision
            return;
        }
        if (target.IsInterior())
            return;
        switch (target.ShapeClass())
        {
            case GameBase.ShapeKind.Item:
            case GameBase.ShapeKind.Mine:
                if (target.ItemThrower == sender)
                    return;
                if (target.ShapeClass() == GameBase.ShapeKind.Mine && target.DamageState == GameBase.kDestroyed)
                    return;
                mask = 0x24800080u; // 0x0061acb0
                break;
            case GameBase.ShapeKind.Vehicle:
                mask = (other & 0x80) != 0 || (other & 0x200000) != 0 ? 0x24a00080u : 0; // 0x00619ce8, 0x00445a38
                break;
            case GameBase.ShapeKind.NoShape:
                return;
            default:
                mask = 0x24200080u; // 0x0061a7e8: StaticBase classes
                break;
        }
        if ((other & mask) == 0)
            return;
        // GameBase::onCollision (0x00448840): one callback per pair per 1.0 s, with the record list the receiver owns
        // (+0x214).
        target.OnCollision(sender, this);
    }
}
