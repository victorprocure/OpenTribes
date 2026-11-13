// Port of src/game/displace.hpp and src/game/displace.cpp (Moveable displacement of riders and overlapping objects,
// docs/collision.md E3, docs/static-sim.md 2.5: SimMovement_displace 0x00505ed4, the rider and overlap phases 0x0050613c
// and 0x005062e4, the push 0x005064f8 and the blocking test of doors 0x00505790), of src/game/move_candidates.cpp (the
// candidate list of a stepped move, docs/collision.md 2.1, 2.2) and of the collision event of src/game/player.cpp
// (GameState::postCollisionEvent and dispatchCollisionEvent, docs/collision.md 8 E1).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>Pushing and displacing objects out of the way of moving objects, plus the collision candidate lists.</summary>
public static class Displace
{
    // 1/3 as the 80-bit constant at 0x005067b4 (mantissa 0xAAAAAAAAAAAAAAAB), a double here.
    private const double Third = 1.0 / 3.0;

    /// <summary>Moves an object by a delta through up to three stepped moves.</summary>
    /// <param name="deltaIn">The displacement.</param>
    /// <param name="mover">The velocity of the Moveable. It raises the object's velocity along it (SimMovement_matchVelocity, 0x00505008).</param>
    /// <param name="box">The object box (+0x1d4), which sets the sub-step lengths.</param>
    /// <param name="velocity">The object velocity.</param>
    /// <param name="atRest">The at-rest flag of the object.</param>
    /// <param name="position">Reads a component of the live position of the object.</param>
    /// <param name="stepMove">Runs the object's own stepped move toward a target with the two sub-step lengths. Returns whether it was not blocked in every iteration.</param>
    /// <returns>True when the whole displacement was done.</returns>
    /// <remarks>Follows SimMovement_pushBy (0x005064f8).</remarks>
    public static bool PushBy(ReadOnlySpan<float> deltaIn, ReadOnlySpan<float> mover, float[] box, float[] velocity, ref bool atRest,
                              Func<int, float> position, Func<float[], float, float, bool> stepMove)
    {
        float[] delta = [deltaIn[0], deltaIn[1], deltaIn[2]];
        // Sub-steps: one third of the sum of the box extents, and half of it.
        var extent = Sub(box[3], box[0]);
        extent = Add(extent, Sub(box[4], box[1]));
        extent = Add(extent, Sub(box[5], box[2]));
        var step0 = X87.F32(Mul(Third, extent));
        var step1 = X87.F32(Mul(step0, 0.5f));
        float[] target = new float[3], goal = new float[3], moved = new float[3];
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            // SimMovement_matchVelocity (0x00505008): raise the velocity along the mover's to the mover's.
            var uu = X87.F32(X87.Dot(mover, mover));
            if (uu != 0.0f)
            {
                var ov = X87.F32(X87.Dot(mover, velocity));
                if (ov < uu)
                {
                    var factor = X87.F32(Div(ov, uu));
                    for (var i = 0; i < 3; ++i)
                        velocity[i] = X87.F32(Add(Sub(mover[i], Mul(mover[i], factor)), velocity[i]));
                    atRest = false;
                }
            }
            for (var i = 0; i < 3; ++i)
                goal[i] = target[i] = X87.F32(Add(delta[i], position(i)));
            if (!stepMove(target, step0, step1))
                return false;
            for (var i = 0; i < 3; ++i)
            {
                var shortfall = X87.F32(Sub(position(i), goal[i]));
                moved[i] = X87.F32(Add(delta[i], shortfall));
            }
            var l = X87.F32(X87.Len(delta));
            var d = X87.F32(X87.Dot(moved, delta));
            if (!(Mul(l, l) > d))
                return true;
            var scale = X87.F32(Div(d, Mul(l, l)));
            for (var i = 0; i < 3; ++i)
            {
                var part = X87.F32(Mul(scale, delta[i]));
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
        public GameBase Object { get; set; } = null!;
        public ref Transform Xf => ref _xf;
        private Transform _xf;
        public float[] Velocity { get; set; } = new float[3];
        public uint Mask { get; set; }
        public int Flags { get; set; }
    }

    // src/game/displace.cpp typeOf.
    private uint ContainerTypeOf(GameBase gb) => World.Find(gb)?.Type ?? 0;

    // src/game/displace.cpp sphereImageOf: a ShapeBase object as the image that runs a shape/shape test: its bounding
    // sphere (image +0x64, +0x70) under its transform.
    private static MoverImage SphereImageOf(GameBase gb, in Float9 rot, in Float3 pos)
    {
        var output = new MoverImage
        {
            Pos = pos,
            Rot = rot,
            BoxMin = Float3.Of(gb.ShapeBox.AsSpan(0, 3)),
            BoxMax = Float3.Of(gb.ShapeBox.AsSpan(3, 3)),
            Radius = gb.ShapeRadius,
            CentreLocal = Float3.Of(gb.ShapeCenter),
            ShapeMover = true
        };
        return output;
    }

    // src/game/displace.cpp imagesOverlap: SimCollisionImage_test (0x005036a4) of the mover's image against the image of
    // `o`, as the overlap phase of a displace runs it. Shape against shape is the mover's bounding sphere against the
    // candidate (0x0043873c): an Item or Mine in bounds mode, and the hull of a vehicle when the mover is a vehicle.
    // `moved`: the mover's shape image at another transform (the door test), else its current one.
    private bool ImagesOverlap(GameBase mover, GameBase o, CollisionImage? moved = null)
    {
        var hit = new CollisionHit();
        if (Player.From(mover) is Player pm)
        {
            var mv = new MoverImage();
            pm.MoverImageOf(mv);
            var oth = new CollisionImage();
            if (Player.From(o) is Player po)
            {
                oth.ImageKind = CollisionImage.Kind.Sphere;
                oth.Owner = o;
                oth.Pos = po.Transform.P;
                oth.Centre[2] = po.Radius;
                oth.Radius = po.Radius;
            }
            else if (!ShapeImage(o, oth))
            {
                return false;
            }
            return Collision.TestImage(mv, oth, hit);
        }
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
        if (Player.From(o) is Player p)
        {
            var mv = new MoverImage();
            p.MoverImageOf(mv);
            return Collision.TestImage(mv, image, hit);
        }
        var cls = o.ShapeClass();
        var hulls = cls == GameBase.ShapeKind.Vehicle && mover.ShapeClass() == GameBase.ShapeKind.Vehicle;
        if (cls != GameBase.ShapeKind.Item && cls != GameBase.ShapeKind.Mine && !hulls)
            return false;
        var other = new CollisionImage();
        if (!ShapeImage(o, other))
            return false;
        var self = SphereImageOf(mover, image.Rot, image.Pos);
        return Collision.TestImage(self, other, hit);
    }

    // src/game/displace.cpp isItemClass.
    private static bool IsItemClass(GameBase o) =>
        o.ShapeClass() is GameBase.ShapeKind.Item or GameBase.ShapeKind.Mine;

    // src/game/displace.cpp pushObject.
    private static bool PushObject(GameBase o, ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        if (Player.From(o) is Player p)
            return p.PushBy(delta, mover);
        if (Vehicle.From(o) is Vehicle v)
            return v.PushBy(delta, mover);
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
    private const int MaxDisplaceDepth = 32;

    /// <inheritdoc/>
    /// <remarks>Builds the shape image of a ShapeBase object at its current transform (docs/collision.md T1.2).</remarks>
    public override bool ShapeImage(SimObject obj, CollisionImage image)
    {
        if (obj is not GameBase gb)
            return false;
        image.Rot = gb.Transform.M;
        image.Pos = gb.Transform.P;
        image.Owner = gb;
        var b = Sim != null && gb.DataBlockName != null ? Sim.DataBlocks().Find(gb.DataBlockName) : null;
        Span<float> box = stackalloc float[6];
        if (b == null || Resources == null || !Sim!.DataBlocks().GetField(b, "shapeFile", "", out var shape) || shape.Length == 0 ||
            !Shape.ShapeBox(Resources, shape, box))
            return false;
        image.Box = Float6.Of(box);
        image.ImageKind = CollisionImage.Kind.Shape;
        image.Boxes = Shape.ShapeCollisionBoxes(Resources, shape);
        var cls = gb.ShapeClass();
        image.NonBlocking = cls == GameBase.ShapeKind.Item && !gb.ItemCollideable;
        // StaticBase_bindShape (0x0044f68c): the collision detail, else detail 0. Item_applyCollideable (0x00451574, also
        // the Mine bind) puts the image in bounds mode.
        image.Instance = ShapeState.ShapeInstanceOf(this, gb);
        image.Detail = image.Instance?.ShapeData != null ? image.Instance.ShapeData.CollisionDetail() : 0;
        image.MeshMode = cls is not GameBase.ShapeKind.Item and not GameBase.ShapeKind.Mine;
        return true;
    }

    /// <inheritdoc/>
    public override bool MoveableDisplace(SimObject mover, ReadOnlySpan<float> oldPos)
    {
        if (mover is not GameBase gameMover)
            return true;
        using var chop = X87.Chop();
        var delta = new float[3];
        for (var i = 0; i < 3; ++i)
            delta[i] = X87.F32(Sub(gameMover.Transform.P[i], oldPos[i]));
        return DisplaceBy(gameMover, delta, true);
    }

    /// <summary>Displaces the objects that a mover pushes.</summary>
    /// <param name="mover">The moving object.</param>
    /// <param name="delta">The displacement.</param>
    /// <param name="top">True when called from the mover's own update (a Moveable, or Flier_update with the collision mask 0x80). Otherwise it is for an object that a displace has just pushed, with a zero delta and the object's own collision mask.</param>
    /// <returns>True when the displacement was done.</returns>
    /// <remarks>Follows SimMovement_displace (0x00505ed4).</remarks>
    public bool DisplaceBy(GameBase mover, float[] delta, bool top)
    {
        var playerMover = Player.From(mover);
        var vehicleMover = mover.ShapeClass() == GameBase.ShapeKind.Vehicle && playerMover == null;
        if (playerMover == null)
        {
            // No image (vtable +0xcc, the first image of query 0x407), no displace: a destroyed StaticBase object or one
            // whose datablock disables collision, and nothing to find with a Trigger's mask of 0.
            var image = new CollisionImage();
            if (!ShapeImage(mover, image) || (!top && !vehicleMover && DisplaceMaskOf(mover) == 0) ||
                (!vehicleMover && !IsItemClass(mover) && !StaticHasImage(mover)))
                return true;
        }
        if (DisplaceHidden.Count >= MaxDisplaceDepth)
            return true;
        var manager = mover.Manager();
        var records = new List<DisplaceRecord>();
        void Record(GameBase o)
        {
            var r = new DisplaceRecord { Object = o, Xf = o.Transform, Velocity = [o.Velocity[0], o.Velocity[1], o.Velocity[2]] };
            if (Player.From(o) is Player p)
            {
                r.Mask = p.CollisionMask;
                r.Flags = p.RestFlag ? 1 : 0;
            }
            else
            {
                r.Flags = o.ItemFlags;
            }
            records.Add(r);
        }
        // The object type of the mover is 0 while its two lists are walked: no query of a pushed object or of a nested
        // displace finds it. The type is back before a blocker callback and stays.
        DisplaceHidden.Add(mover);
        var hiddenHere = true;
        void Unhide()
        {
            if (!hiddenHere)
                return;
            hiddenHere = false;
            var it = DisplaceHidden.LastIndexOf(mover);
            if (it >= 0)
                DisplaceHidden.RemoveAt(it);
        }
        bool Hidden(GameBase o) => DisplaceHidden.Contains(o);
        bool Overlaps(GameBase o) => ImagesOverlap(mover, o);
        float[] zero = [0, 0, 0];
        // Moveable::onBlocker (0x004449f0, vtable +0xbc): Items and corpses are never blockers.
        bool Blocker(GameBase o)
        {
            Unhide();
            if (playerMover != null)
                return true; // SimMovement_onBlocker 0x005067c4
            if ((ContainerTypeOf(o) & 0x40200000u) != 0)
            {
                o.AtRest = false;
                return false;
            }
            if (vehicleMover)
                return ((Vehicle)mover).Blocker(o); // Vehicle_onBlocker 0x0044642c
            ScriptCallback(mover, "onBlocker", [o.Id().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            return true;
        }
        void Rollback()
        {
            Unhide();
            foreach (var r in records)
            {
                var o = r.Object;
                o.Transform = r.Xf;
                if (Player.From(o) is Player p)
                {
                    p.CollisionMask = r.Mask;
                    p.RestFlag = r.Flags != 0;
                    p.UpdateContainer();
                }
                else
                {
                    o.ItemFlags = r.Flags;
                    ObjectMoved(o);
                }
                for (var i = 0; i < 3; ++i)
                    o.Velocity[i] = r.Velocity[i];
            }
        }
        // Phase A (0x0050613c): the objects on the carry list. A rider that could not be pushed and is clear of the
        // mover's image displaces what it overlaps itself.
        var riders = new List<int>(mover.Carried);
        foreach (var id in riders)
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
        if (playerMover != null)
        {
            // Every image of the candidate list is tested on its own (0x005062e4 walks the list of images, not of
            // objects): Players, vehicles, Items, Mines, StaticBase objects, Moveables and Triggers, each pushed by the
            // delta, which is zero here (a Player never starts a displace). An object with two images that both overlap
            // (a station with a trigger sphere) is recorded and pushed twice.
            Span<float> query = stackalloc float[6];
            Collision.SweptBox(playerMover.BoxMin, playerMover.BoxMax, mover.Transform.P, mover.Velocity, 0.032f, query);
            var images = new List<CollisionImage>();
            CollisionCandidates(mover, playerMover.CollisionMask & ~0x46u, query, images);
            var self = new MoverImage();
            playerMover.MoverImageOf(self);
            // Sphere and box images (a Trigger, the trigger sphere of a station, a door or a Mine) are tested against the
            // Player's own shape (Player::buildCandidates).
            foreach (var im in images)
                if (im.ImageKind is CollisionImage.Kind.TriggerSphere or CollisionImage.Kind.Trigger)
                {
                    self.Instance = ShapeState.ShapeInstanceOf(this, mover);
                    self.Detail = 0;
                    self.MeshMode = true;
                    break;
                }
            foreach (var im in images)
            {
                if (im.Owner is not GameBase o || o.IsInterior() || Hidden(o) || riders.Contains(o.Id()))
                    continue;
                var hit = new CollisionHit();
                if (!Collision.TestImage(self, im, hit))
                    continue;
                Record(o);
                if ((!PushObject(o, delta, mover.Velocity) || !DisplaceBy(o, zero, false)) && Blocker(o))
                {
                    Rollback();
                    return false;
                }
            }
        }
        else
        {
            Span<float> query = stackalloc float[6];
            var overlapMask = (top ? 0x24a00080u : DisplaceMaskOf(mover)) & ~0x46u;
            if (vehicleMover)
            {
                // A Vehicle moves with the collision mask 0x80 during its own displace (Flier_update U16). Its object box
                // is turned by its matrix (bit 0x40 of +0x164, HF6).
                overlapMask = top ? 0x80u : (Flier.HullMask & ~0x46u);
                var m = mover.WorldMat();
                Span<float> box = stackalloc float[6];
                Shape.RotatedWorldBox(mover.ShapeQueryBox, m, box);
                Collision.SweptBox(box[..3], box[3..], zero, mover.Velocity, 0.032f, query);
            }
            else
            {
                Collision.SweptBox(mover.ShapeQueryBox.AsSpan(0, 3), mover.ShapeQueryBox.AsSpan(3, 3), mover.Transform.P,
                                   mover.Velocity, 0.032f, query);
            }
            foreach (var so in World.FindObjects(query, overlapMask, mover))
                if (so is GameBase o)
                    found.Add(o);
        }
        foreach (var o in found)
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
        foreach (var r in records)
        {
            var o = r.Object;
            if (Player.From(o) != null)
            {
                o.SetMaskBits(0x12000);
            }
            else if (IsItemClass(o))
            {
                o.AtRest = false;
                o.SetMaskBits(0x08);
            }
            for (var i = 0; i < 3; ++i)
                o.Velocity[i] = r.Velocity[i];
        }
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows SimMovement_testTransform (0x00505790) for a door: its shape image at the new transform against the Players (and collideable Items) in its way.</remarks>
    public override bool MoveableTest(SimObject mover, ReadOnlySpan<float> rotation, ReadOnlySpan<float> position)
    {
        if (mover is not GameBase gameMover)
            return true;
        using var chop = X87.Chop();
        var xf = new Transform
        {
            Flags = 3
        };
        MissionTransform.EulerToMatrix(rotation, ref xf);
        for (var i = 0; i < 3; ++i)
            xf.P[i] = position[i];
        var image = new CollisionImage();
        if (!ShapeImage(gameMover, image))
            return true;
        image.Rot = xf.M;
        image.Pos = xf.P;
        var wm = new Mat
        {
            M = xf.M,
            P = new Vec3(position[0], position[1], position[2])
        };
        Span<float> box = stackalloc float[6];
        Shape.RotatedWorldBox(gameMover.ShapeQueryBox, wm, box);
        foreach (var so in World.FindObjects(box, 0x24a00080u, gameMover))
        {
            if (so is not GameBase o)
                continue;
            if (Player.From(o) == null && !(o.ShapeClass() == GameBase.ShapeKind.Item && o.ItemCollideable))
                continue;
            if (ImagesOverlap(gameMover, o, image))
                return false;
        }
        return true;
    }

    /// <summary>Tells whether a StaticBase object has a collision image.</summary>
    /// <param name="gb">The object.</param>
    /// <returns>False when its datablock disables collision or it is destroyed, unless its shape has a visibility sequence.</returns>
    /// <remarks>Follows StaticBase_getCollisionImages (0x0044fff0).</remarks>
    public bool StaticHasImage(GameBase gb)
    {
        var b = gb.DataBlockName != null ? Sim!.DataBlocks().Find(gb.DataBlockName) : null;
        var disabled = b != null && Sim!.DataBlocks().GetField(b, "disableCollision", "", out var v) && Numbers.ParseBool(v);
        return !((disabled || gb.DamageState == GameBase.Destroyed) && !gb.VisibilityThread);
    }

    /// <inheritdoc/>
    /// <remarks>The images of the objects of the mask whose world box meets the query, in container order, with the mover excluded (docs/collision.md 2.1).</remarks>
    public override void CollisionCandidates(SimObject mover, uint mask, ReadOnlySpan<float> query, List<CollisionImage> output)
    {
        // InteriorShape world boxes are padded by 3.0 (2.2): widen the container query and gate every object on its own
        // box.
        Span<float> wide = stackalloc float[6];
        for (var i = 0; i < 3; ++i)
        {
            wide[i] = SubF(query[i], 3.5f);
            wide[i + 3] = AddF(query[i + 3], 3.5f);
        }
        // Objects other than interiors are gated by the container query itself, whose database tests are not symmetric
        // (an object whose box starts exactly at the query's end is not found: 0x004fc3fc, docs/collision.md 11.8).
        var exact = World.FindObjects(query, mask, mover);
        Span<float> padded = stackalloc float[6];
        foreach (var obj in World.FindObjects(wide, mask, mover))
        {
            var e = World.Find(obj);
            if (e == null)
                continue;
            var image = new CollisionImage
            {
                Owner = obj,
                Type = e.Type
            };
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
            if (Player.From(gb) is Player other)
            {
                image.ImageKind = CollisionImage.Kind.Sphere;
                image.Centre[2] = other.Radius;
                image.Radius = other.Radius;
                image.Rot = Float9.Identity();
                // Player_kill (0x004112ac) sets object byte +0x7f9, which is the image byte +0xd that
                // SimMovement_testImages (0x00506bfc) reads: against a Player mover the corpse posts one event and leaves
                // the candidate list at once, in candidate order, and never reaches the pair events of the notify list
                // (docs/collision.md 11.13). Items and vehicles keep their own handling of a corpse.
                image.NonBlocking = other.Dead && Player.From(mover) != null;
            }
            else if (gb.GetKind() == GameBase.Kind.Trigger)
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
                var type = image.Type;
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
                        var sphere = new CollisionImage
                        {
                            ImageKind = CollisionImage.Kind.TriggerSphere,
                            Owner = obj,
                            Type = type,
                            NonBlocking = true,
                            Rot = image.Rot,
                            Pos = image.Pos,
                            Centre = Float3.Of(gb.ShapeCenter),
                            Radius = gb.ShapeTriggerRadius
                        };
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
        if (Player.From(gb) is Player p)
            return p.ObjectTypeBits;
        if (gb.IsInterior())
            return ObjectType.InteriorType;
        return ContainerType(gb);
    }

    /// <inheritdoc/>
    /// <remarks>Event 0x41f (8 E1) is queued at the manager's current time, so it runs at the start of the next tick. It is dropped when the sender is gone by then.</remarks>
    public override void PostCollisionEvent(SimObject? target, SimObject? sender)
    {
        var m = target?.Manager();
        if (m == null || sender == null)
            return;
        var senderId = sender.Id();
        m.PostEvent(target, m.Time(), () =>
        {
            if (m.FindById(senderId) != sender)
                return;
            if (target is GameBase t && sender is GameBase s)
                DispatchCollisionEvent(t, s);
        }, sender);
    }

    /// <summary>Dispatches event 0x41f at its target: the class handler, the 1 s throttle and the script onCollision(target, sender).</summary>
    /// <param name="target">The object that receives the event.</param>
    /// <param name="sender">The object that collided.</param>
    /// <remarks>See docs/collision.md 8 E1.3 and E1.4.</remarks>
    public void DispatchCollisionEvent(GameBase target, GameBase sender)
    {
        // Handlers (vtable +0xe8) by class and the type mask of `other` (8 E1.4).
        var other = TypeOf(sender);
        uint mask;
        if (Player.From(target) != null)
        {
            mask = 0x64a00080u; // 0x0060fb68
        }
        else if (target.GetKind() == GameBase.Kind.Trigger)
        {
            target.TriggerCollide(sender); // Trigger_onCollision 0x004874b4 (static-sim 6.2): no script onCollision
            return;
        }
        else if (target.IsInterior())
        {
            return;
        }
        else
        {
            switch (target.ShapeClass())
            {
                case GameBase.ShapeKind.Item:
                case GameBase.ShapeKind.Mine:
                    if (target.ItemThrower == sender)
                        return;
                    if (target.ShapeClass() == GameBase.ShapeKind.Mine && target.DamageState == GameBase.Destroyed)
                        return;
                    mask = 0x24800080u; // 0x0061acb0
                    break;
                case GameBase.ShapeKind.Vehicle:
                    mask = (other & 0x80) != 0 || (other & 0x200000) != 0 ? 0x24a00080u : 0; // 0x00619ce8, 0x00445a38
                    break;
                case GameBase.ShapeKind.NoShape:
                    return;
                case GameBase.ShapeKind.StaticShape:
                case GameBase.ShapeKind.Sensor:
                case GameBase.ShapeKind.Turret:
                case GameBase.ShapeKind.Moveable:
                default:
                    mask = 0x24200080u; // 0x0061a7e8: StaticBase classes
                    break;
            }
        }
        if ((other & mask) == 0)
            return;
        // GameBase::onCollision (0x00448840): one callback per pair per 1.0 s, with the record list the receiver owns
        // (+0x214).
        target.OnCollision(sender, this);
    }
}
