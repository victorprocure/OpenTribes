// Port of src/game/ai_manager.hpp (AIManager, aiSolveAim) and src/game/ai_manager.cpp: the AI manager (object 726) and
// the pre-tick, post-tick and engage routines of the AI object (docs/ai-sim.md 1.6, 3.4, 3.7, 3.8, 4). The Chop class of
// the file is X87.Chop; world::x87::f32 is X87.F32, the long double arithmetic the Fpu double operations in C++ order.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public sealed partial class AIObject
{
    // ---- pre- and post-tick (1.6, 4.2, 4.3) ----

    // src/game/ai_manager.cpp AIObject::preTick: 0x0043bf08.
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

    // src/game/ai_manager.cpp AIObject::postTick: 0x0043be50.
    public void PostTick()
    {
        ++ErrorPhase;
        Player? p = Player();
        if (p != null && p.Dead && (Flags & 1) == 0)
        {
            Flags |= 1;
            Callback(null, 0);
            RemoveVariables();
            string buf = "Corpse" + CorpseCounterNext().ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + AiName;
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

    // src/game/ai_manager.cpp AIObject::corpseCounterNext.
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
            for (int i = 0; i < 3; ++i)
                rel[i] = X87.F32(Sub(target.Velocity[i], self.Velocity[i]));
            PointOf(Game, target, tp);
            float dist = Dist3(Frame.Own, tp);
            float t = X87.F32(Div(dist, ProjectileSpeed));
            for (int i = 0; i < 3; ++i)
            {
                float r = X87.F32(Mul(rel[i], t));
                aim[i] = X87.F32(Add(r, tp[i]));
            }
        }
        else
        {
            tp.CopyTo(aim, 0);
        }
        Span<float> err = stackalloc float[3];
        AimError(target, null, err);
        for (int i = 0; i < 3; ++i)
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
            float[] P = new float[3];
            PointOf(Game, target, P);
            if (Dist2(P, Frame.Own) < 17.0f)
            {
                float[] v = new float[3];
                for (int i = 0; i < 3; ++i)
                    v[i] = X87.F32(Sub(P[i], Frame.Own[i]));
                float len = X87.F32(X87.Len(v));
                float k = X87.F32(Div(1.0f, len));
                for (int i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(k, v[i]));
                const float neg = -17.0f;
                for (int i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(neg, v[i]));
                Span<float> P2 = stackalloc float[3];
                PointOf(Game, target, P2);
                for (int i = 0; i < 3; ++i)
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
        bool moveAfter = AttackMode == 1 && Frame.HasMoveTarget;
        bool aimed = false;
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
                int group = slot.Info.ProjGroup;
                float totalTime = Pj.Real(block, "totalTime", 10000.0f);
                for (int i = 0; i < members.Members().Count; ++i)
                {
                    Projectile? pr = Projectile.From(members.Members()[i]);
                    if (pr == null || pr.Shooter() != owner)
                        continue;
                    float[] point = new float[3];
                    if (!pr.BeamTargetPoint(point, out _))
                        continue;
                    float g = 0.0f;
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
                    float[] use = high;
                    if (!AIAim.AiSolveAim(group, ProjectileSpeed, totalTime, eye.P, point, true, g, ref ok2, high) || !ok2)
                    {
                        if (!ok1)
                            break;
                        use = low;
                    }
                    float[] aim = new float[3];
                    for (int k = 0; k < 3; ++k)
                        aim[k] = X87.F32(Add(eye.P[k], Mul(use[k], 50.0f)));
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

// src/game/ai_manager.hpp aiSolveAim.
public static class AIAim
{
    // src/game/ai_manager.cpp aiSolveAim: the aim solver of the projectile datablock classes (vtable +0x10, docs/ai-sim.md
    // 3.7, 12.4): from `s` to the point `t` for a projectile of `speed`, low arc (`high` false) or high arc, with the
    // gravity `g` (negative). Writes the unit direction and `ok` (the projectile can reach the point). BulletData
    // (0x0045b99c) and RocketData (0x00460b5c): the straight line, `ok` when speed x totalTime is above the distance;
    // GrenadeData (0x0045ed40): the ballistic arc, the straight line with `ok` false when there is none; every other class
    // (0x00459928): the straight line, `ok` true. The high arc is refused (false) by all but a Grenade with a solution.
    // Returns false when there is no answer.
    public static bool AiSolveAim(int group, float speed, float totalTime, ReadOnlySpan<float> s, ReadOnlySpan<float> t,
                                  bool high, float g, ref bool ok, Span<float> dir)
    {
        if (high && group != Projectile.kGrenadeGroup)
            return false;
        static float Straight(ReadOnlySpan<float> s, ReadOnlySpan<float> t, Span<float> dir)
        {
            for (int i = 0; i < 3; ++i)
                dir[i] = X87.F32(Sub(t[i], s[i]));
            float len = X87.F32(X87.Len(dir));
            float k = X87.F32(Div(1.0f, X87.F32(X87.Len(dir))));
            for (int i = 0; i < 3; ++i)
                dir[i] = X87.F32(Mul(k, dir[i]));
            return len;
        }
        float len;
        if (group != Projectile.kGrenadeGroup)
        {
            len = Straight(s, t, dir);
            ok = group == Projectile.kBulletGroup || group == Projectile.kRocketGroup ? Mul(speed, totalTime) > len : true;
            return true;
        }
        // 0x0045ed40 (fastcall: datablock, start, target; arc, ok, dir, force on the stack).
        float dz = X87.F32(Sub(t[2], s[2]));
        double dx = Sub(t[0], s[0]), dy = Sub(t[1], s[1]);
        float h2 = X87.F32(Add(Mul(dx, dx), Mul(dy, dy)));
        float v2 = X87.F32(Mul(speed, speed));
        double A = Add(Mul(dz, g), v2);
        double B = Add(Div(Mul(dz, dz), h2), 1.0);
        double t1 = Mul(B, g);
        t1 = Mul(t1, g);
        t1 = Mul(t1, h2);
        float D = X87.F32(Sub(Mul(A, A), t1));
        if (D < 0.0f)
        {
            Straight(s, t, dir);
            ok = false;
            return true;
        }
        if (D < 1.0f && high)
            return false;
        float S = high ? -1.0f : 1.0f;
        double root = Sqrt(D);
        double num = Mul(S, root);
        num = Add(Add(Mul(dz, g), v2), num);
        float Q = X87.F32(Div(num, Mul(Add(Div(Mul(dz, dz), h2), 1.0), 2.0)));
        if (Q < 0.0f)
        {
            Straight(s, t, dir);
            ok = false;
            return true;
        }
        float w = X87.F32(Sqrt(Sub(v2, Q)));
        Q = X87.F32(Sqrt(Q));
        float r = X87.F32(Div(Sqrt(h2), Q));
        double half = Mul(0.5f, g);
        half = Mul(half, r);
        half = Mul(half, r);
        float p1 = X87.F32(Sub(Add(half, Mul(r, w)), dz));
        float p2 = X87.F32(Sub(Sub(half, Mul(r, w)), dz));
        int i2 = unchecked((int)Pj.Ftol(p2)), i1 = unchecked((int)Pj.Ftol(p1));
        i2 = i2 < 0 ? -i2 : i2;
        i1 = i1 < 0 ? -i1 : i1;
        if (i2 < i1)
            w = X87.F32(Mul(-1.0f, w));
        float[] h = { X87.F32(Sub(t[0], s[0])), X87.F32(Sub(t[1], s[1])), 0.0f };
        {
            float k = X87.F32(Div(1.0f, X87.F32(X87.Len(h))));
            for (int i = 0; i < 3; ++i)
                h[i] = X87.F32(Mul(k, h[i]));
        }
        float[] sum = new float[3];
        float[] b = { X87.F32(Mul(0.0f, w)), X87.F32(Mul(0.0f, w)), X87.F32(Mul(1.0f, w)) };
        for (int i = 0; i < 3; ++i)
            sum[i] = X87.F32(Add(X87.F32(Mul(h[i], Q)), b[i]));
        float k2 = X87.F32(Div(1.0f, X87.F32(X87.Len(sum))));
        for (int i = 0; i < 3; ++i)
            dir[i] = X87.F32(Mul(k2, sum[i]));
        ok = true;
        return true;
    }
}

// src/game/ai_manager.hpp class AIManager.
public sealed class AIManager : SimObject
{
    public readonly GameState Game;
    public readonly List<AIObject> Ais = new(); // spawn order
    public bool Recompute = true;   // 0x00617ddc
    public int TickCount;           // +0x4c
    public int Counter;             // +0x50

    // src/game/ai_manager.cpp AIManager::AIManager.
    public AIManager(GameState g) => Game = g;

    public override string ClassName() => "AI::Manager";
    public override int FixedId() => 726;
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;

    // src/game/ai_manager.cpp AIManager::~AIManager.
    public override void Destroy()
    {
        if (Game.Ai == this)
            Game.Ai = null;
    }

    // src/game/ai_manager.cpp AIManager::onRemove.
    public override void OnRemove()
    {
        if (Game.Ai == this)
            Game.Ai = null;
    }

    // src/game/ai_manager.cpp AIManager::remove.
    public void Remove(AIObject ai) => Ais.RemoveAll(a => a == ai);

    // src/game/ai_manager.cpp AIManager::findByName: exact, case-insensitive (0x0043e590).
    public AIObject? FindByName(string n)
    {
        foreach (AIObject a in Ais)
            if (GameUtil.EqualsNoCase(a.AiName, n))
                return a;
        return null;
    }

    // src/game/ai_manager.cpp AIManager::findByRep.
    public AIObject? FindByRep(int rep)
    {
        foreach (AIObject a in Ais)
            if (a.RepId == rep)
                return a;
        return null;
    }

    // src/game/ai_manager.cpp AIManager::serverTick: 0x0043dd3c, between sets 718 and 719.
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
        for (int i = remove.Count; i-- > 0;)
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

    // src/game/ai_manager.cpp ensureAIManager: creates the manager when no AI command ran in this mission yet
    // (0x0043f8fc). Null with the documented message when the server or MissionCleanup is missing.
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
