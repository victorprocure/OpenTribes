// Port of src/game/ai_manager.hpp (AIManager, aiSolveAim) and src/game/ai_manager.cpp: the AI manager (object 726) and
// the pre-tick, post-tick and engage routines of the AI object (docs/ai-sim.md 1.6, 3.4, 3.7, 3.8, 4). The Chop class of
// the file is X87.Chop; world::x87::f32 is X87.F32, the long double arithmetic the Fpu double operations in C++ order.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public sealed partial class AIObject
{
    // ---- pre- and post-tick (1.6, 4.2, 4.3) ----

    /// <summary>Runs the AI pre-tick of the object.</summary>
    /// <remarks>Follows 0x0043bf08.</remarks>
    public void PreTick()
    {
        TargetId = -1;
        // 0x00440f24
        if (Trigger.Cooldown != 0)
        {
            ++Trigger.Count;
            if (Trigger.Count > Trigger.Windup)
                Trigger.Count = Trigger.Windup;
            --Trigger.Cooldown;
            if (Trigger.Cooldown <= 0)
                Trigger.Cooldown = 0;
        }
        else
        {
            Trigger.Count = 0;
        }
        Flags &= ~4u;
        if (Game.Players.Find(RepId) is ClientRecord r)
            Team = r.Team;
    }

    /// <summary>Runs the AI post-tick of the object.</summary>
    /// <remarks>Follows 0x0043be50.</remarks>
    public void PostTick()
    {
        ++ErrorPhase;
        Player? p = Player();
        if (p != null && p.Dead && (Flags & 1) == 0)
        {
            Flags |= 1;
            Callback(null, 0);
            RemoveVariables();
            var buf = "Corpse" + CorpseCounterNext().ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + AiName;
            if (buf.Length > 159)
                buf = buf[..159]; // snprintf into 160 bytes
            AiName = buf.Length > 31 ? buf[..31] : buf;
        }
        if (PeriodicPeriod >= 32)
        {
            ++PeriodicCounter;
            if (PeriodicCounter >= PeriodicPeriod)
                PeriodicCounter = 0;
            if (PeriodicCounter == 0)
                Callback(null, 2);
        }
    }

    // src/game/ai_manager.cpp corpseCounter: 0x006640f4, never reset.
    private static uint sCorpseCounter;

    /// <summary>Returns the next corpse counter value and advances it.</summary>
    /// <returns>The counter value.</returns>
    public static uint CorpseCounterNext() => sCorpseCounter++;

    // ---- engage (3.4, 3.7, 3.8) ----

    // src/game/ai_manager.cpp pointOf.
    private static void PointOf(GameState g, Player p, Span<float> output) => AiTargetPoint(g, p, output);

    // src/game/ai_manager.cpp AIObject::engagePlayer: 0x0043c8bc.
    private void EngagePlayer(Player target)
    {
        Player? self = Player();
        if (self == null)
            return;
        float[] aim = new float[3], tp = new float[3];
        PointOf(Game, target, tp);
        if (WeaponSpeed())
        {
            Span<float> rel = stackalloc float[3];
            for (var i = 0; i < 3; ++i)
                rel[i] = X87.F32(Sub(target.Velocity[i], self.Velocity[i]));
            PointOf(Game, target, tp);
            var dist = Dist3(Frame.Own, tp);
            var t = X87.F32(Div(dist, ProjectileSpeed));
            for (var i = 0; i < 3; ++i)
            {
                var r = X87.F32(Mul(rel[i], t));
                aim[i] = X87.F32(Add(r, tp[i]));
            }
        }
        else
        {
            tp.CopyTo(aim, 0);
        }
        Span<float> err = stackalloc float[3];
        AimError(target, null, err);
        for (var i = 0; i < 3; ++i)
            aim[i] = X87.F32(Add(err[i], aim[i]));
        YawError ye = SteerYaw(aim);
        if (ye.Abs < 0.08f)
        {
            YawError pe = SteerPitch(aim);
            if (pe.Abs < 0.08f && !target.Dead)
                Move.Fire = TriggerPull();
        }
        // 0x0043ca2b reads the current target directive without a check (X1, D11): with none (AI::Attack) the target
        // id stays -1 and the attack goes on.
        if (CurrentTarget >= 0 && CurrentTarget < Directives.Count)
            TargetId = Directives[CurrentTarget].Client;
        if (AttackMode == 1)
        {
            if (Frame.HasMoveTarget)
                MoveTowardKeepTurn(Frame.MoveTarget);
        }
        else if (AttackMode == 0)
        {
            var P = new float[3];
            PointOf(Game, target, P);
            if (Dist2(P, Frame.Own) < 17.0f)
            {
                var v = new float[3];
                for (var i = 0; i < 3; ++i)
                    v[i] = X87.F32(Sub(P[i], Frame.Own[i]));
                var len = X87.F32(X87.Len(v));
                var k = X87.F32(Div(1.0f, len));
                for (var i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(k, v[i]));
                const float neg = -17.0f;
                for (var i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(neg, v[i]));
                Span<float> P2 = stackalloc float[3];
                PointOf(Game, target, P2);
                for (var i = 0; i < 3; ++i)
                    P[i] = X87.F32(Add(P2[i], v[i]));
            }
            MoveTowardKeepTurn(P);
        }
    }

    // src/game/ai_manager.cpp AIObject::engagePoint: 0x0043d068.
    private void EngagePoint(AIDirective d)
    {
        YawError ye = SteerYaw(d.Point);
        if (ye.Abs < 0.08f)
        {
            YawError pe = SteerPitch(d.Point);
            if (pe.Abs < 0.08f)
                Move.Fire = TriggerPull();
        }
    }

    // src/game/ai_manager.cpp AIObject::engageLaser: 0x0043cc78. Needs a weapon with a projectile speed. For every
    // TargetLaser of the owner that reports a hit point the projectile datablock solves the aim from the eye (low arc,
    // then the high arc, which is preferred) with the gravity of the Player's SimMovement force 1; the aim point is the
    // eye plus 50 m of the direction.
    private void EngageLaser(AIDirective d)
    {
        SimManager? m = Game.Sim?.Server();
        var moveAfter = AttackMode == 1 && Frame.HasMoveTarget;
        var aimed = false;
        Player? owner = AIMath.PlayerOfRep(Game, d.Client);
        Player? self = Player();
        if (m != null && self != null && WeaponSpeed())
        {
            Player.ImageSlot slot = self.Images[0];
            DataBlockManager.Block? block = slot.Info.ProjGroup == -1 ? null : Game.Sim!.DataBlocks().At(slot.Info.ProjGroup, slot.Info.ProjIndex);
            SimObject? set = m.FindById(725);
            SimSet? members = set?.AsSet();
            if (block != null && owner != null && members != null)
            {
                var group = slot.Info.ProjGroup;
                var totalTime = Pj.Real(block, "totalTime", 10000.0f);
                for (var i = 0; i < members.Members().Count; ++i)
                {
                    Projectile? pr = Projectile.From(members.Members()[i]);
                    if (pr == null || pr.Shooter() != owner)
                        continue;
                    var point = new float[3];
                    if (!pr.BeamTargetPoint(point, out _))
                        continue;
                    var g = 0.0f;
                    foreach (Player.Force f in self.Forces())
                        if (f.Type == 1)
                        {
                            g = X87.F32(-X87.Len(f.V));
                            break;
                        }
                    Transform eye = self.EyeTransform();
                    bool ok1 = false, ok2 = false;
                    float[] low = new float[3], high = new float[3];
                    if (!AIAim.AiSolveAim(group, ProjectileSpeed, totalTime, eye.P, point, false, g, ref ok1, low))
                        continue;
                    var use = high;
                    if (!AIAim.AiSolveAim(group, ProjectileSpeed, totalTime, eye.P, point, true, g, ref ok2, high) || !ok2)
                    {
                        if (!ok1)
                            break;
                        use = low;
                    }
                    var aim = new float[3];
                    AIAim.LaserAimPoint(eye.P, use, aim);
                    Flags |= 4;
                    YawError ye = SteerYaw(aim);
                    if (ye.Abs < 0.001f)
                    {
                        YawError pe = SteerPitch(aim);
                        if (pe.Abs < 0.001f && TriggerPull())
                            Move.Fire = true;
                    }
                    aimed = true;
                }
            }
        }
        if (moveAfter && !aimed)
            MoveToward(Frame.MoveTarget);
    }
}

/// <summary>The AI aim helpers (aiSolveAim of src/game/ai_manager.hpp).</summary>
public static class AIAim
{
    /// <summary>Computes the aim point of a laser-painted target.</summary>
    /// <param name="eye">The eye position.</param>
    /// <param name="dir">The aim direction.</param>
    /// <param name="aim">Receives the aim point.</param>
    /// <remarks>Follows 0x0043cec0 to 0x0043cf13 (docs/ai-sim.md 12.5). <c>dir x 50</c> is stored as a Point3F first, then the eye is added, so each component is rounded twice.</remarks>
    public static void LaserAimPoint(ReadOnlySpan<float> eye, ReadOnlySpan<float> dir, Span<float> aim)
    {
        for (var k = 0; k < 3; ++k)
            aim[k] = X87.F32(Add(eye[k], X87.F32(Mul(dir[k], 50.0f))));
    }

    /// <summary>Solves the aim of a projectile datablock class from a point to a target point.</summary>
    /// <param name="group">The datablock group of the projectile.</param>
    /// <param name="speed">The projectile speed.</param>
    /// <param name="totalTime">The projectile total time.</param>
    /// <param name="s">The start point.</param>
    /// <param name="t">The target point.</param>
    /// <param name="high">True for the high arc, false for the low arc.</param>
    /// <param name="g">The gravity, negative.</param>
    /// <param name="ok">Receives whether the projectile can reach the point.</param>
    /// <param name="dir">Receives the unit direction.</param>
    /// <returns>False when there is no answer.</returns>
    /// <remarks>Follows the aim solver at vtable +0x10 (docs/ai-sim.md 3.7, 12.4). BulletData (0x0045b99c) and RocketData (0x00460b5c) use the straight line, with <paramref name="ok"/> when <c>speed x totalTime</c> is above the distance. GrenadeData (0x0045ed40) uses the ballistic arc, or the straight line with <paramref name="ok"/> false when there is none. Every other class (0x00459928) uses the straight line with <paramref name="ok"/> true. All but a Grenade with a solution refuse the high arc.</remarks>
    public static bool AiSolveAim(int group, float speed, float totalTime, ReadOnlySpan<float> s, ReadOnlySpan<float> t,
                                  bool high, float g, ref bool ok, Span<float> dir)
    {
        if (high && group != Projectile.kGrenadeGroup)
            return false;
        static float Straight(ReadOnlySpan<float> s, ReadOnlySpan<float> t, Span<float> dir)
        {
            for (var i = 0; i < 3; ++i)
                dir[i] = X87.F32(Sub(t[i], s[i]));
            var len = X87.F32(X87.Len(dir));
            var k = X87.F32(Div(1.0f, X87.F32(X87.Len(dir))));
            for (var i = 0; i < 3; ++i)
                dir[i] = X87.F32(Mul(k, dir[i]));
            return len;
        }
        float len;
        if (group != Projectile.kGrenadeGroup)
        {
            len = Straight(s, t, dir);
            ok = group is not Projectile.kBulletGroup and not Projectile.kRocketGroup || Mul(speed, totalTime) > len;
            return true;
        }
        // 0x0045ed40 (fastcall: datablock, start, target; arc, ok, dir, force on the stack).
        var dz = X87.F32(Sub(t[2], s[2]));
        double dx = Sub(t[0], s[0]), dy = Sub(t[1], s[1]);
        var h2 = X87.F32(Add(Mul(dx, dx), Mul(dy, dy)));
        var v2 = X87.F32(Mul(speed, speed));
        var A = Add(Mul(dz, g), v2);
        var B = Add(Div(Mul(dz, dz), h2), 1.0);
        var t1 = Mul(B, g);
        t1 = Mul(t1, g);
        t1 = Mul(t1, h2);
        var D = X87.F32(Sub(Mul(A, A), t1));
        if (D < 0.0f)
        {
            Straight(s, t, dir);
            ok = false;
            return true;
        }
        if (D < 1.0f && high)
            return false;
        var S = high ? -1.0f : 1.0f;
        var root = Sqrt(D);
        var num = Mul(S, root);
        num = Add(Add(Mul(dz, g), v2), num);
        var Q = X87.F32(Div(num, Mul(Add(Div(Mul(dz, dz), h2), 1.0), 2.0)));
        if (Q < 0.0f)
        {
            Straight(s, t, dir);
            ok = false;
            return true;
        }
        var w = X87.F32(Sqrt(Sub(v2, Q)));
        Q = X87.F32(Sqrt(Q));
        var r = X87.F32(Div(Sqrt(h2), Q));
        var half = Mul(0.5f, g);
        half = Mul(half, r);
        half = Mul(half, r);
        var p1 = X87.F32(Sub(Add(half, Mul(r, w)), dz));
        var p2 = X87.F32(Sub(Sub(half, Mul(r, w)), dz));
        int i2 = unchecked((int)Pj.Ftol(p2)), i1 = unchecked((int)Pj.Ftol(p1));
        i2 = i2 < 0 ? -i2 : i2;
        i1 = i1 < 0 ? -i1 : i1;
        if (i2 < i1)
            w = X87.F32(Mul(-1.0f, w));
        float[] h = [X87.F32(Sub(t[0], s[0])), X87.F32(Sub(t[1], s[1])), 0.0f];
        {
            var k = X87.F32(Div(1.0f, X87.F32(X87.Len(h))));
            for (var i = 0; i < 3; ++i)
                h[i] = X87.F32(Mul(k, h[i]));
        }
        var sum = new float[3];
        float[] b = [X87.F32(Mul(0.0f, w)), X87.F32(Mul(0.0f, w)), X87.F32(Mul(1.0f, w))];
        for (var i = 0; i < 3; ++i)
            sum[i] = X87.F32(Add(X87.F32(Mul(h[i], Q)), b[i]));
        var k2 = X87.F32(Div(1.0f, X87.F32(X87.Len(sum))));
        for (var i = 0; i < 3; ++i)
            dir[i] = X87.F32(Mul(k2, sum[i]));
        ok = true;
        return true;
    }
}

/// <summary>The AI manager object (id 726). It owns the AI objects and runs their tick.</summary>
/// <remarks>Port of class AIManager in src/game/ai_manager.hpp.</remarks>
public sealed class AIManager : SimObject
{
    /// <summary>The game state.</summary>
    public readonly GameState Game;
    /// <summary>The AI objects in spawn order.</summary>
    public readonly List<AIObject> Ais = [];
    /// <summary>True when the AI must recompute (0x00617ddc).</summary>
    public bool Recompute = true;
    /// <summary>The tick count (+0x4c).</summary>
    public int TickCount;
    /// <summary>The counter (+0x50).</summary>
    public int Counter;

    /// <summary>Creates the manager.</summary>
    /// <param name="g">The game state.</param>
    public AIManager(GameState g) => Game = g;

    /// <inheritdoc/>
    public override string ClassName() => "AI::Manager";
    /// <inheritdoc/>
    public override int FixedId() => 726;
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;

    /// <inheritdoc/>
    public override void Destroy()
    {
        if (Game.Ai == this)
            Game.Ai = null;
    }

    /// <inheritdoc/>
    public override void OnRemove()
    {
        if (Game.Ai == this)
            Game.Ai = null;
    }

    /// <summary>Removes an AI object from the manager.</summary>
    /// <param name="ai">The AI object.</param>
    public void Remove(AIObject ai) => Ais.RemoveAll(a => a == ai);

    /// <summary>Finds an AI object by name.</summary>
    /// <param name="n">The name, compared exactly and without case (0x0043e590).</param>
    /// <returns>The object, or null.</returns>
    public AIObject? FindByName(string n)
    {
        foreach (AIObject a in Ais)
            if (GameUtil.EqualsNoCase(a.AiName, n))
                return a;
        return null;
    }

    /// <summary>Finds an AI object by its representative.</summary>
    /// <param name="rep">The representative id.</param>
    /// <returns>The object, or null.</returns>
    public AIObject? FindByRep(int rep)
    {
        foreach (AIObject a in Ais)
            if (a.RepId == rep)
                return a;
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x0043dd3c, which runs between sets 718 and 719.</remarks>
    public override void ServerTick(uint tickMs)
    {
        SimManager? m = Game.Sim?.Server();
        if (m == null)
            return;
        using var chop = X87.Chop();
        List<(AIObject Ai, int Id)> Snapshot()
        {
            var v = new List<(AIObject, int)>();
            foreach (AIObject a in Ais)
                v.Add((a, a.Id()));
            return v;
        }
        bool Alive((AIObject Ai, int Id) e) => m.FindById(e.Id) == e.Ai;

        // 1. pre-tick; AIs whose Player is gone are collected.
        var remove = new List<(AIObject Ai, int Id)>();
        foreach (var e in Snapshot())
        {
            if (!Alive(e) || !e.Ai.Active)
                continue;
            if (e.Ai.Player() == null)
                remove.Add(e);
            else
                e.Ai.PreTick();
        }
        // 2. deletion (0x0043dbf8), from the last collected AI to the first (reference: the record of the AI collected
        // first is freed last and handed out first).
        for (var i = remove.Count; i-- > 0;)
            if (Alive(remove[i]))
                m.DeleteObject(remove[i].Ai);
        // 3. one AI refreshes its automatic targets
        if (Ais.Count != 0)
        {
            AIObject a = Ais[(int)((uint)Counter % (uint)Ais.Count)];
            Counter = (Counter + 1) & 0x7fff;
            if (a.Valid())
                a.RefreshAutoTargets();
        }
        // 4. sense and act
        foreach (var e in Snapshot())
        {
            if (!Alive(e) || !e.Ai.Valid())
                continue;
            e.Ai.Frame = new AIObject.FrameState();
            if (Recompute)
                e.Ai.RecomputeSkill();
            e.Ai.Sense();
            e.Ai.Act();
        }
        Recompute = false;
        // 6. post-tick
        foreach (var e in Snapshot())
            if (Alive(e) && e.Ai.Valid())
                e.Ai.PostTick();
        ++TickCount;
    }

    /// <summary>Creates the manager when no AI command ran in this mission yet.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="commandName">The name of the calling command, used in the message.</param>
    /// <returns>The manager, or null with the documented message when the server or MissionCleanup is missing.</returns>
    /// <remarks>Follows 0x0043f8fc.</remarks>
    public static AIManager? EnsureAIManager(GameState game, string commandName)
    {
        if (game.Ai != null)
            return game.Ai;
        SimManager? m = game.Sim?.Server();
        if (m == null)
        {
            game.Console!.Print(1, "No server manager yet, can't install AI manager thus, cannot execute command " + commandName + ".");
            return null;
        }
        SimObject? cleanup = m.Find("MissionCleanup");
        SimSet? set = cleanup?.AsSet();
        if (set == null)
        {
            game.Console!.Print(1, "No cleanup group yet, can't install AI manager and, therefore, cannot execute command " + commandName);
            return null;
        }
        var mgr = new AIManager(game);
        if (!m.RegisterObject(mgr))
        {
            game.Console!.Print(1, "Couldn't register AI manager...  Cannot execute command " + commandName);
            mgr.Destroy();
            return null;
        }
        set.AddObject(mgr);
        game.Ai = mgr;
        return mgr;
    }
}
