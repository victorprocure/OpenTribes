// Port of src/game/item_sim.cpp: Item and Mine engine behavior (docs/static-sim.md 5): the tick, the physics step against
// the terrain, interiors and shapes (docs/collision.md T3, E5), throw, hide and the mine alignment. The Chop class of the
// file is Fpu.ChopScope; float(...) inside it is Fpu.Store.
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public partial class GameBase
{
    private const uint kItemMask = 0x20c00086u; // collision mask of an Item (docs/static-sim.md 5.2)

    // src/game/item_sim.cpp dot3: dot(a, b) = (a.x*b.x + a.y*b.y) + a.z*b.z, returned unrounded (0x005ece49).
    private static double Dot3(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        Add(Add(Mul(a[0], b[0]), Mul(a[1], b[1])), Mul(a[2], b[2]));

    // src/game/item_sim.cpp Contact: one contact surface of the terrain test (docs/collision.md 1.3).
    private struct TerrainContactRec
    {
        public float N0, N1, N2;
        public float Distance;
        public int MaterialIndex;
    }

    // src/game/item_sim.cpp terrainContact: sphere against the terrain (docs/collision.md 5.1 steps 1-7 with the Item's
    // bounding sphere, 0x00437b50). `at` is the object position; the sphere centre is rotated by the object matrix.
    private static bool TerrainContact(GameServices game, GameBase obj, ReadOnlySpan<float> at, out TerrainContactRec output)
    {
        output = default;
        Terrain? terr = game.GetTerrain();
        if (terr == null || obj.ShapeRadius <= 0)
            return false;
        Vec3 tp = terr.Position();
        // M.t = P + (-terrainPos), then the centre through the object rotation (m_mul_point).
        Span<float> mt = stackalloc float[3] { Store(Add(at[0], -tp.X)), Store(Add(at[1], -tp.Y)), Store(Add(at[2], -tp.Z)) };
        Span<float> c = stackalloc float[3];
        ReadOnlySpan<float> m = obj.Transform.M;
        for (int j = 0; j < 3; ++j)
        {
            double s = Mul(obj.ShapeCenter[0], m[j]);
            s = Add(s, Mul(obj.ShapeCenter[1], m[3 + j]));
            s = Add(s, Mul(obj.ShapeCenter[2], m[6 + j]));
            c[j] = Store(Add(s, mt[j]));
        }
        float h = 0;
        Vec3 n = default;
        int material = 0;
        if (!terr.SurfaceLocal(c[0], c[1], ref h, ref n, ref material))
            return false;
        Span<float> nn = stackalloc float[3] { n.X, n.Y, n.Z }, pos = stackalloc float[3] { c[0], c[1], h };
        float a = Store(Dot3(c, nn));
        double b = Dot3(pos, nn);
        float d = Store(Sub(a, b));
        if (!(Math.Abs((double)d) < obj.ShapeRadius))
            return false;
        output.N0 = n.X;
        output.N1 = n.Y;
        output.N2 = n.Z;
        output.Distance = d;
        output.MaterialIndex = material;
        return true;
    }

    // src/game/item_sim.cpp surfaceMaterialOf: the material entry behind a contact surface (docs/collision.md 4): terrain
    // squares and interior surfaces index a .dml, shape faces have none.
    private static Material? SurfaceMaterialOf(GameServices game, CollisionHit hit, in CollisionSurface s)
    {
        if (s.Material < 0)
            return null;
        if ((hit.Type & ObjectType.kTerrainType) != 0)
            return game.TerrainMaterial(s.Material);
        if (hit.Object is not GameBase gb || gb.InteriorGeo == null || gb.InteriorGeo.MaterialListName.Length == 0 ||
            game.GetResourceManager() == null)
            return null;
        List<Material>? list = MaterialList.Get(game.GetResourceManager()!, gb.InteriorGeo.MaterialListName);
        return list != null && s.Material < list.Count ? list[s.Material] : null;
    }

    // src/game/item_sim.cpp hitNormal: the contact normal in world space: surface space through the hit rotation
    // (m_mul_vector).
    private static void HitNormal(CollisionHit hit, in CollisionSurface s, Span<float> output)
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

    /// <summary>Finds the shape images that the Item's bounding sphere and box meet at a position.</summary>
    /// <param name="q">The position to test.</param>
    /// <param name="candidates">The candidate images.</param>
    /// <param name="hits">Receives the hits, terrain first, then the candidates in container order.</param>
    /// <param name="triggered">When given, an overlapping non-blocking image is no hit. Its object is added here (one event 0x41f each) and the candidate leaves the list, the last one taking its place.</param>
    /// <returns>True when there is a hit.</returns>
    /// <remarks>See docs/collision.md T3 and SimMovement_testImages (0x00506bfc).</remarks>
    public bool ItemTestAt(ReadOnlySpan<float> q, List<CollisionImage> candidates, List<CollisionHit> hits,
                           List<SimObject>? triggered = null)
    {
        hits.Clear();
        GameServices game = mCtx.Game!;
        if (TerrainContact(game, this, q, out TerrainContactRec c))
        {
            var h = new CollisionHit { Type = ObjectType.kTerrainType };
            var s = new CollisionSurface();
            s.Normal = Float3.Of(c.N0, c.N1, c.N2);
            s.Distance = c.Distance;
            s.Material = c.MaterialIndex;
            h.Surfaces.Add(s);
            hits.Add(h);
        }
        var mover = new MoverImage();
        mover.Pos = Float3.Of(q);
        mover.Rot = Transform.M;
        mover.BoxMin = Float3.Of(ShapeBox.AsSpan(0, 3));
        mover.BoxMax = Float3.Of(ShapeBox.AsSpan(3, 3));
        mover.Radius = ShapeRadius;
        mover.CentreLocal = Float3.Of(ShapeCenter);
        mover.ShapeMover = true;
        // The Item's own image for the candidates that run the test themselves (Players).
        var own = new CollisionImage();
        if (game.ShapeImage(this, own))
        {
            mover.Instance = own.Instance;
            mover.Detail = own.Detail;
            mover.MeshMode = own.MeshMode;
        }
        for (int i = 0; i < candidates.Count; ++i)
        {
            CollisionImage image = candidates[i];
            if (image.ImageKind == CollisionImage.Kind.Terrain)
                continue;
            if (image.NonBlocking)
            {
                // The trigger sphere of a station, a door or a Mine, and the box or sphere image of a Trigger object,
                // with `triggered` only (docs/collision.md E2). A non-collideable Item is not tested here.
                if (triggered == null || image.Object is not GameBase other)
                    continue;
                bool trigger = other.GetKind() == Kind.Trigger;
                if (!trigger && (image.ImageKind != CollisionImage.Kind.TriggerSphere || !other.IsStaticBase()))
                    continue;
                var th = new CollisionHit();
                if (!Collision.TestImage(mover, image, th))
                    continue;
                triggered.Add((SimObject)image.Object);
                candidates[i] = candidates[^1];
                candidates.RemoveAt(candidates.Count - 1);
                --i;
                continue;
            }
            var hit = new CollisionHit();
            if (Collision.TestImage(mover, image, hit))
                hits.Add(hit);
        }
        return hits.Count == 0;
    }

    /// <summary>Collects the candidate images for an Item move: the object box at the target, extended by <c>v * dt</c>.</summary>
    /// <param name="target">The target position.</param>
    /// <param name="output">Receives the candidates.</param>
    /// <param name="mask">The object type mask.</param>
    /// <remarks>See docs/static-sim.md 2.1.</remarks>
    public void ItemCandidates(ReadOnlySpan<float> target, List<CollisionImage> output, uint mask)
    {
        Span<float> query = stackalloc float[6];
        Collision.SweptBox(ShapeQueryBox.AsSpan(0, 3), ShapeQueryBox.AsSpan(3, 3), target, Velocity, 0.032f, query);
        mCtx.Game!.CollisionCandidates(this, mask, query, output);
    }

    /// <summary>Tests whether the collision shape at a position touches nothing.</summary>
    /// <param name="p">The position.</param>
    /// <returns>True when nothing is touched.</returns>
    /// <remarks>Follows GameBase::testPosition (0x0041fba4).</remarks>
    public bool TestPosition(ReadOnlySpan<float> p)
    {
        if (mCtx.Game == null)
            return true;
        using var chop = ChopScope();
        var candidates = new List<CollisionImage>();
        Span<float> query = stackalloc float[6];
        Span<float> zero = stackalloc float[3];
        Collision.SweptBox(ShapeQueryBox.AsSpan(0, 3), ShapeQueryBox.AsSpan(3, 3), p, zero, 0.032f, query);
        mCtx.Game.CollisionCandidates(this, kItemMask, query, candidates);
        var hits = new List<CollisionHit>();
        return ItemTestAt(p, candidates, hits);
    }

    /// <summary>Clears the at-rest bit, as setPosition and setVelocity do.</summary>
    public void ResetItemRest()
    {
        AtRest = false;
        ItemContact = false;
    }

    /// <summary>Runs one Item tick.</summary>
    /// <remarks>Follows 0x0045165c (Item). The Mine adds the alignment (0x00459070).</remarks>
    public void ItemTick()
    {
        if (ItemVisible)
            StaticTick(0.03f);
        // Thrower expiry (0x00451884).
        if (ItemThrower != null && Manager() != null && Store(Add(ItemThrowTime, 1.0f)) < Manager()!.Time())
            ItemThrower = null;
        if (!ItemRotates && (DamageState != kDestroyed || VisibilityThread))
        {
            bool nowRest = AtRest;
            bool changed = nowRest != ItemPrevAtRest;
            ItemPrevAtRest = nowRest;
            if (!nowRest)
            {
                ItemPhysics(0.032f);
                float now = Manager() != null ? Manager()!.Time() : 0.0f;
                if (ItemNextUpdate < now)
                {
                    ItemNextUpdate = Store(Add(now, 1.0f));
                    SetMaskBits(0x08);
                }
            }
            else if (changed)
            {
                SetMaskBits(0x08);
            }
        }
        if (mShapeClass == ShapeKind.Mine)
            MineAlign();
    }

    /// <summary>Moves an Item for one step: forces, the target position, the stepped move and its collision response.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <returns>True when the Item moved.</returns>
    /// <remarks>Follows ShapeBase_moveStep (0x0044c644).</remarks>
    public bool ItemPhysics(float dt)
    {
        using var chop = ChopScope();
        GameServices? game = mCtx.Game;
        if (game == null)
            return false;
        ItemContact = false;
        AtRest = false;
        // 0x005050f0: the time slice. 0x00505144: forces. 0x00505328: target position.
        float slice = dt;
        float[] oldV = { Velocity[0], Velocity[1], Velocity[2] };
        Span<float> gravity = stackalloc float[3];
        gravity.Clear();
        float drag = 0;
        // The gravity force and the drag as taken on container entry (FC2); the drag outlives the container.
        bool inContainer = ItemVisible && game.EnteredForces(this, gravity, ref drag);
        if (inContainer)
            for (int i = 0; i < 3; ++i)
                Velocity[i] = Store(Add(Mul(gravity[i], slice), Velocity[i]));
        if (ItemVisible && drag != 0)
        {
            // Flag 0x10, medium drag: v -= v * ((drag * mediumDrag) * slice).
            float s = Store(Mul(Store(Mul(ItemData.Drag, drag)), slice));
            for (int i = 0; i < 3; ++i)
                Velocity[i] = Store(Sub(Velocity[i], Mul(Velocity[i], s)));
        }
        ItemGravity = inContainer ? Store(Len3(gravity)) : 0.0f;
        float half = Store(Mul(slice, 0.5f));
        float[] target = new float[3];
        for (int i = 0; i < 3; ++i)
            target[i] = Store(Add(Mul(Add(oldV[i], Velocity[i]), half), Transform.P[i]));
        // The matrix is rebuilt from the Euler rotation (+0x244) with rotation and translation flags.
        MissionTransform.EulerToMatrix(Rotation, ref Transform);
        Transform.Flags |= 3;
        // 0x00505a3c with steps radius * 0.45 and radius * 0.45 * 0.3, three passes at most.
        if (ItemSteppedMove(target, ItemSubStep, Store(Mul(ItemSubStep, 0.3f)), slice, kItemMask))
            return true;
        ItemMoveFallback(kItemMask);
        return false;
    }

    /// <summary>Answers the contacts of a move step with the Item collision response.</summary>
    /// <param name="hits">The contacts.</param>
    /// <param name="iteration">The iteration of the stepped move.</param>
    /// <returns>True when some contact answered, so the move restarts toward the new target.</returns>
    /// <remarks>Follows Item_movementCollision (0x0045199c, vtable +0xb8) with the default response 0x00506d80.</remarks>
    public bool ItemProcessCollision(List<CollisionHit> hits, int iteration)
    {
        GameServices game = mCtx.Game!;
        int flags = ItemFlags; // 0x181 | 0x1d (docs/static-sim.md 5.1)
        // No collision with the thrower.
        if (ItemThrower != null)
            for (int h = 0; h < hits.Count;)
            {
                if (hits[h].Object == ItemThrower)
                {
                    hits[h] = hits[^1];
                    hits.RemoveAt(hits.Count - 1);
                }
                else
                {
                    ++h;
                }
            }
        // Every Moveable hit carries the Item from now on (0x0045199c, 0x005067d0).
        foreach (CollisionHit h in hits)
            if ((h.Type & ObjectType.kMoveableType) != 0 && h.Object is GameBase m)
                Attach(m);
        // 0x00506d80: every hit object joins the notify list, once per call and without a duplicate test.
        foreach (CollisionHit h in hits)
            if (h.Object is SimObject o)
                ItemNotify.Add(o);
        Span<float> wn = stackalloc float[3];
        // Mine (0x004589ec): the normals facing up of every terrain or interior hit.
        if (mShapeClass == ShapeKind.Mine)
            foreach (CollisionHit h in hits)
                if ((h.Type & (ObjectType.kTerrainType | ObjectType.kInteriorType)) != 0)
                    foreach (CollisionSurface s in h.Surfaces)
                    {
                        HitNormal(h, s, wn);
                        if (wn[2] > 0)
                            MineHit(wn);
                    }
        // Callback 0x00506d80: the response of every contact with a non-negative distance; iterations after the first
        // drop friction (0x01) and 0x20.
        int f = iteration != 0 ? (flags & ~0x21) : flags;
        bool handled = false;
        foreach (CollisionHit h in hits)
            foreach (CollisionSurface s in h.Surfaces)
            {
                if (!(s.Distance >= 0))
                    continue;
                HitNormal(h, s, wn);
                if (SurfaceResponse(wn, SurfaceMaterialOf(game, h, s), f))
                    handled = true;
            }
        return handled;
    }

    // src/game/item_sim.cpp GameBase::itemMoveFallback: SimMovement_moveTo (0x00505720 -> SimObject_setTransform
    // 0x00505814), which ShapeBase_moveStep runs after a move that was blocked in every pass.
    private void ItemMoveFallback(uint mask)
    {
        ItemContact = false;
        AtRest = false;
        var candidates = new List<CollisionImage>();
        var hits = new List<CollisionHit>();
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        ItemCandidates(p, candidates, mask);
        for (int round = 0; round < 3; ++round)
        {
            if (ItemTestAt(p, candidates, hits) || !ItemProcessCollision(hits, round))
            {
                ItemPostEvents();
                ItemRestCheck(0.032f);
                return;
            }
        }
        AtRest = true;
        Velocity[0] = Velocity[1] = Velocity[2] = 0;
        ItemPostEvents();
    }

    /// <summary>Posts event 0x41f to every notified object and back.</summary>
    /// <remarks>Follows SimMovement_postCollisionEvents (0x00507180).</remarks>
    public void ItemPostEvents()
    {
        List<SimObject> notified = ItemNotify;
        ItemNotify = new List<SimObject>();
        if (mCtx.Game == null)
            return;
        foreach (SimObject other in notified)
        {
            mCtx.Game.PostCollisionEvent(other, this);
            mCtx.Game.PostCollisionEvent(this, other);
        }
    }

    // src/game/item_sim.cpp GameBase::itemRestCheck: rest check 0x00505d64 (docs/collision.md 3.1).
    private void ItemRestCheck(float slice)
    {
        if (!ItemContact || AtRest)
            return;
        float e = Store(Mul(Store(Mul(ItemGravity, slice)), 2.0));
        if (Math.Abs((double)Velocity[0]) < e && Math.Abs((double)Velocity[1]) < e && Math.Abs((double)Velocity[2]) < e)
            AtRest = true;
    }

    /// <summary>Moves an Item toward a target in sub-steps against the objects of a mask.</summary>
    /// <param name="target">The target position.</param>
    /// <param name="step0">The first sub-step length.</param>
    /// <param name="step1">The second sub-step length.</param>
    /// <param name="slice">The time slice of the move.</param>
    /// <param name="mask">The object type mask of the objects to collide with.</param>
    /// <returns>False when every iteration was blocked. The Item is then at rest with no velocity.</returns>
    /// <remarks>Follows SimMovement_stepMove (0x00505a3c).</remarks>
    public bool ItemSteppedMove(float[] target, float step0, float step1, float slice, uint mask)
    {
        GameServices game = mCtx.Game!;
        ItemContact = false;
        AtRest = false;
        // Candidate list (2.1), built once per move before the first iteration. A hidden Item is out of its container
        // but still collides with the world.
        var candidates = new List<CollisionImage>();
        var hits = new List<CollisionHit>();
        ItemCandidates(target, candidates, mask);
        float step = step0;
        float used = 0;
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        void Commit(float[] at)
        {
            for (int i = 0; i < 3; ++i)
                Transform.P[i] = at[i];
            Transform.Flags |= 3;
        }
        bool blocked = true;
        float[] delta = new float[3], dd = new float[3], qpos = new float[3];
        for (int iteration = 0; iteration < 3; ++iteration)
        {
            for (int i = 0; i < 3; ++i)
                delta[i] = Store(Sub(target[i], p[i]));
            float L = Store(Len3(delta));
            double q = Div(L, step);
            long n = (long)Math.Ceiling(q);
            if (n > 100000)
                n = 100000;
            bool callbackTrue = false;
            if (n != 0)
            {
                float k = Store(Div(1.0, (double)(float)n));
                for (int i = 0; i < 3; ++i)
                    dd[i] = Store(Mul(k, delta[i]));
                qpos[0] = p[0];
                qpos[1] = p[1];
                qpos[2] = p[2];
                for (long i = 0; i < n; ++i)
                {
                    for (int j = 0; j < 3; ++j)
                        qpos[j] = Store(Add(dd[j], qpos[j]));
                    bool free = ItemTestAt(qpos, candidates, hits);
                    if (free)
                    {
                        for (int j = 0; j < 3; ++j)
                            p[j] = qpos[j];
                        Commit(p);
                        continue;
                    }
                    float sliceT = Store(Div(Sub(slice, used), (double)(float)n));
                    used = Store(Add(Mul((double)(float)i, sliceT), used));
                    bool handled = ItemProcessCollision(hits, iteration);
                    if (handled)
                    {
                        float left = Store(Sub(slice, used));
                        for (int j = 0; j < 3; ++j)
                        {
                            float tv = Store(Mul(left, Velocity[j]));
                            target[j] = Store(Add(p[j], tv));
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
        if (blocked)
        {
            // Three blocked passes: at rest, no velocity, the transform stays at the last free position.
            AtRest = true;
            Velocity[0] = Velocity[1] = Velocity[2] = 0;
            Commit(p);
            if (ItemVisible)
                game.ObjectMoved(this);
            ItemPostEvents();
            return false;
        }
        // The container box follows the new transform (SimObject_setTransformRaw).
        if (ItemVisible)
            game.ObjectMoved(this);
        ItemPostEvents();
        ItemRestCheck(slice);
        return true;
    }

    // ---- carry lists and displacement (docs/collision.md E3) ----

    /// <summary>Attaches another object: adds it to this object's list unless present, then adds this object to its list.</summary>
    /// <param name="other">The object to attach.</param>
    /// <remarks>Follows 0x005067d0.</remarks>
    public void Attach(GameBase other)
    {
        if (Carried.Contains(other.Id()))
            return;
        Carried.Add(other.Id());
        if (!other.Carried.Contains(Id()))
            other.Carried.Add(Id());
    }

    /// <summary>Removes this object from the list of every object it is attached to.</summary>
    /// <remarks>Follows 0x005067f8.</remarks>
    public void DetachAll()
    {
        if (Carried.Count == 0)
            return;
        SimManager? m = Manager();
        foreach (int other in Carried)
            if (m?.FindById(other) is GameBase g)
                g.Carried.RemoveAll(x => x == Id());
        Carried.Clear();
    }

    /// <summary>Moves the Item by a delta through the world.</summary>
    /// <param name="delta">The displacement.</param>
    /// <param name="mover">The velocity of the pushing object.</param>
    /// <returns>True when the whole displacement was done.</returns>
    /// <remarks>Follows SimMovement_pushBy (0x005064f8).</remarks>
    public bool ItemPushBy(ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        using var chop = ChopScope();
        ItemFlags &= ~5; // friction and elasticity stay off after a displacement (collision E3.2)
        return Displace.PushBy(delta, mover, ShapeQueryBox, Velocity, ref AtRest, i => Transform.P[i],
                               (target, step0, step1) => ItemSteppedMove(target, step0, step1, 0.032f, 6));
    }

    /// <summary>Answers a surface contact.</summary>
    /// <param name="n">The surface normal.</param>
    /// <param name="mat">The surface material, or null.</param>
    /// <param name="flags">The contact flags.</param>
    /// <returns>True when the contact was handled.</returns>
    /// <remarks>Follows SimMovement_collideSurface (0x00506f44).</remarks>
    public bool SurfaceResponse(ReadOnlySpan<float> n, Material? mat, int flags)
    {
        MaterialTable? table = mCtx.Game?.GetMaterialTable();
        float vn = Store(Dot3(n, Velocity));
        if (!(vn < 0))
            return false;
        ItemContact = true;
        float k = Store(Add(-(double)vn, (double)9.99999974737875e-05f));
        Span<float> nk = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            nk[i] = Store(Mul(k, n[i]));
        for (int i = 0; i < 3; ++i)
            Velocity[i] = Store(Add(nk[i], Velocity[i]));
        if ((flags & 0x01) != 0)
        {
            Span<float> tmp = stackalloc float[3] { Velocity[0], Velocity[1], Velocity[2] };
            float f = Store(Mul(-(double)vn, ItemData.Friction));
            if (mat != null && table != null)
                f = Store(Mul(table.FrictionOf(mat), f));
            float s = Store(Len3(tmp));
            if (s > f)
            {
                float scale = Store(Div(f, s));
                for (int i = 0; i < 3; ++i)
                    tmp[i] = Store(Mul(scale, tmp[i]));
            }
            for (int i = 0; i < 3; ++i)
                Velocity[i] = Store(Sub(Velocity[i], tmp[i]));
        }
        if ((flags & 0x04) != 0)
        {
            float e = ItemData.Elasticity;
            if (mat != null && table != null)
                e = Store(Mul(table.ElasticityOf(mat), e));
            Span<float> nk2 = stackalloc float[3];
            for (int i = 0; i < 3; ++i)
                nk2[i] = Store(Mul(e, nk[i]));
            for (int i = 0; i < 3; ++i)
                Velocity[i] = Store(Add(nk2[i], Velocity[i]));
        }
        return true;
    }

    // ---- hide, throw (5.4, 5.6) ----

    /// <summary>Hides or shows an Item. A hidden Item leaves its container, and so every ghost scope.</summary>
    /// <param name="hide">True to hide the Item.</param>
    /// <remarks>Follows 0x00451194.</remarks>
    public void ItemHide(bool hide)
    {
        GameServices? game = mCtx.Game;
        if (game?.GetContainer() is Container c)
        {
            if (hide)
            {
                c.Remove(this);
                // Leaving the terrain container removes the gravity force, the drag stays (0x0051963c).
                EntryForces.Terrain = null;
                EntryForces.Parent = null;
                AtRest = false;
            }
            else
            {
                if (c.Find(this) == null)
                    game.ObjectMoved(this);
                if (Velocity[0] == 0 && Velocity[1] == 0 && Velocity[2] == 0)
                    AtRest = true;
            }
        }
        ItemVisible = !hide;
    }

    /// <summary>Throws an Item from a thrower.</summary>
    /// <param name="thrower">The object that throws.</param>
    /// <param name="speed">The throw speed.</param>
    /// <param name="random">True to add the random deflection.</param>
    /// <remarks>Follows 0x00451284 (docs/static-sim.md 5.4).</remarks>
    public void ItemThrow(GameBase thrower, float speed, bool random)
    {
        using var chop = ChopScope();
        R250 gen = StaticRandom.Item();
        ItemThrower = thrower;
        ItemThrowerClient = thrower.ControlClient != 0 ? thrower.ControlClient : thrower.OwnerClient;
        ItemThrowerJoin = -1;
        float[] pos = { thrower.Transform.P[0], thrower.Transform.P[1], thrower.Transform.P[2] };
        if (mCtx.Game?.GetContainer()?.Find(thrower) is Container.Entry e)
            for (int i = 0; i < 3; ++i)
                pos[i] = Store(Mul(Add(e.Box[i], e.Box[i + 3]), 0.5));
        Span<float> dir = stackalloc float[3];
        if (!random)
        {
            if (!thrower.ThrowDirection(dir))
            {
                // GameBase (0x0044801c): (0, 8, 0) through the thrower matrix (m_mul_vector).
                for (int j = 0; j < 3; ++j)
                    dir[j] = Store(Mul(8.0f, thrower.Transform.M[3 + j]));
            }
        }
        else
        {
            for (int i = 0; i < 3; ++i)
            {
                double rr = Mul(gen.Next(), 2.3283064365386963e-10);
                dir[i] = i < 2 ? Store(Sub(Mul(rr, 1.466f), 0.733f)) : Store(Add(Mul(rr, 0.5f), 0.5f));
            }
        }
        float f = Store(Div(speed, ItemData.Mass));
        for (int i = 0; i < 3; ++i)
            Velocity[i] = Store(Add(Mul(dir[i], f), thrower.Velocity[i]));
        AtRest = false;
        double r = Mul(gen.Next(), 2.3283064365386963e-10);
        Rotation[0] = Rotation[1] = 0;
        Rotation[2] = Store(Mul(r, 6.2831853071795864769));
        SetMaskBits(0x10);
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
        Transform.Flags |= 2;
        MissionTransform.EulerToMatrix(Rotation, ref Transform);
        SetMaskBits(0x08);
        SetMaskBits(0x38);
        AtRest = false;
        ItemThrowTime = Manager() != null ? Manager()!.Time() : 0.0f;
        mCtx.Game?.ObjectMoved(this);
    }

    // ---- Mine (5.7) ----

    /// <summary>Adds a contact normal to the mine's accumulator.</summary>
    /// <param name="n">The contact normal.</param>
    /// <remarks>Follows 0x004589ec: normals of surfaces facing up are summed and normalised into +0x88c.</remarks>
    public void MineHit(ReadOnlySpan<float> n)
    {
        Span<float> sum = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            sum[i] = Store(Add(MineNormal[i], n[i]));
        double l = Len3(sum);
        if (l > 0)
            for (int i = 0; i < 3; ++i)
                MineNormal[i] = Store(Div(sum[i], l));
    }

    /// <summary>Aligns the mine to the surface normal.</summary>
    /// <remarks>Follows 0x00458d14: nothing while the normal is 0 or the mine is aligned. The x and y angles approach the targets derived from the normal (rotated by -rotation.z) by 0.05 rad per tick. The first tick without a change marks it aligned. Not verified live.</remarks>
    public void MineAlign()
    {
        if (MineAligned || (MineNormal[0] == 0 && MineNormal[1] == 0 && MineNormal[2] == 0))
            return;
        using var chop = ChopScope();
        double cz = Math.Cos(-(double)Rotation[2]), sz = Math.Sin(-(double)Rotation[2]);
        float nx = Store(Sub(Mul(MineNormal[0], cz), Mul(MineNormal[1], sz)));
        float ny = Store(Add(Mul(MineNormal[0], sz), Mul(MineNormal[1], cz)));
        // The original's atan2 returns a double: atan2(1, 0) - pi/2 is then exactly 0 [live: minethrow].
        float tx = Store(Sub(Math.Atan2(MineNormal[2], ny), 1.5707963267948966));
        float ty = -Store(Sub(Math.Atan2(MineNormal[2], nx), 1.5707963267948966));
        bool changed = false;
        float[] target = { tx, ty };
        for (int i = 0; i < 2; ++i)
        {
            float d = Store(Sub(target[i], Rotation[i]));
            const float stepRad = 0.05f;
            if (Math.Abs((double)d) > stepRad)
            {
                Rotation[i] = Store(Add(Rotation[i], d > 0 ? stepRad : -stepRad));
                changed = true;
            }
            else if (d != 0)
            {
                Rotation[i] = target[i];
                changed = true;
            }
        }
        if (changed)
        {
            MissionTransform.EulerToMatrix(Rotation, ref Transform);
            Transform.Flags |= 3;
            SetMaskBits(0x4000);
        }
        else
        {
            MineAligned = true;
        }
    }
}
