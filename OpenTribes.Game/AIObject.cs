// Port of src/game/ai_manager.hpp (AIDirective, AISkill, AIObject) and src/game/ai_object.cpp: AI players, the per-AI
// object, the skill block, directives, steering, aiming and the target bookkeeping (docs/ai-sim.md). The manager tick
// holds a Chop scope; the long double arithmetic is the Fpu double operations in C++ order, world::x87::f32 is X87.F32.
// The x87 helpers of src/core/ldouble.hpp are their portable forms: fpatan Math.Atan2, fptan Math.Tan, fldpi the double
// pi, fprem fmod with the quotient's low three bits; the 80-bit constants are the 53-bit rounded doubles. The C++
// struct copies of a directive are AIDirective.Clone().
namespace OpenTribes.Game;

using System.Runtime.InteropServices;
using static OpenTribes.Core.Fpu;

/// <summary>A directive record of an AI player (docs/ai-sim.md 2.1, 0x38 bytes in the original).</summary>
public sealed class AIDirective
{
    /// <summary>The directive type: waypoint.</summary>
    public const int kWaypoint = 1;
    /// <summary>The directive type: follow.</summary>
    public const int kFollow = 2;
    /// <summary>The directive type: target.</summary>
    public const int kTarget = 4;
    /// <summary>The directive type: guard.</summary>
    public const int kGuard = 8;
    /// <summary>The target sub type: a player.</summary>
    public const int kPlayer = 1;
    /// <summary>The target sub type: a laser.</summary>
    public const int kLaser = 2;
    /// <summary>The target sub type: a point.</summary>
    public const int kPoint = 4;
    /// <summary>The directive flag: the target is in line of sight.</summary>
    public const ushort kLOS = 1;
    /// <summary>The directive flag: the target was in line of sight.</summary>
    public const ushort kHadLOS = 2;
    /// <summary>The directive flag: the death of the target was reported.</summary>
    public const ushort kDeathReported = 4;
    /// <summary>The directive flag: the target is in range.</summary>
    public const ushort kInRange = 8;
    /// <summary>The directive type (one of the k constants for type).</summary>
    public int Type;
    /// <summary>The sub type of a target directive.</summary>
    public int Sub;
    /// <summary>The four callback names, interned. Type 4 defaults in addDirective.</summary>
    public readonly string?[] Callback = new string?[4];
    /// <summary>The directive flags.</summary>
    public ushort Flags;
    /// <summary>The directive order. Directives run in ascending order.</summary>
    public int Order;
    /// <summary>The target client id.</summary>
    public int Client;
    /// <summary>The target point.</summary>
    public readonly float[] Point = new float[3];
    /// <summary>The radius of the directive.</summary>
    public float Radius;

    /// <summary>Copies the directive, as the C++ struct copy does.</summary>
    /// <returns>The copy.</returns>
    public AIDirective Clone()
    {
        var d = new AIDirective { Type = Type, Sub = Sub, Flags = Flags, Order = Order, Client = Client, Radius = Radius };
        Callback.CopyTo(d.Callback, 0);
        Point.CopyTo(d.Point, 0);
        return d;
    }
}

/// <summary>The skill block at +0x114 (docs/ai-sim.md 5.2).</summary>
public sealed class AISkill
{
    /// <summary>The spot distance variable <c>spotDist</c>, negative for the derived value (+0x00).</summary>
    public float SpotDist = -1;
    /// <summary>The derived spot distance (+0x04).</summary>
    public float Spot = 50;
    /// <summary>The think period (+0x08).</summary>
    public int Think = 50;
    /// <summary>The maximum speed (+0x0c).</summary>
    public float MaxSpeed = 0.4f;
    /// <summary>An unused value (+0x10).</summary>
    public float UnusedA = 1.3f;
    /// <summary>An unused value (+0x14).</summary>
    public float UnusedB = 4.0f;
    /// <summary>The unused yaw rates (+0x20).</summary>
    public readonly float[] RateUnused = new float[3];
    /// <summary>The yaw rates r, 2r and 0.1r (+0x2c).</summary>
    public readonly float[] Rate = new float[3];
    /// <summary>The range of a smart AI, a variable (+0x38).</summary>
    public float SmartRange = 145;
    /// <summary>The range of a dumb AI, a variable (+0x3c).</summary>
    public float DumbRange = 55;
    /// <summary>The minimum accuracy of a smart AI, a variable (+0x40).</summary>
    public float SmartMinAccuracy = 0.2f;
    /// <summary>The range in use (+0x44).</summary>
    public float Range;
    /// <summary>The minimum accuracy in use (+0x48).</summary>
    public float MinAccuracy;
    /// <summary>The skill in use (+0x4c).</summary>
    public float Skill;

    // src/game/ai_object.cpp bitsFloat.
    private static float BitsFloat(uint b) => BitConverter.UInt32BitsToSingle(b);

    // src/game/ai_object.cpp triple: 0x00440c30.
    private static void Triple(float[] t, float r)
    {
        t[0] = r;
        t[1] = X87.F32(Mul(2.0f, r));
        t[2] = X87.F32(Mul(0.1, r));
    }

    /// <summary>Builds the skill block for a smart or dumb AI.</summary>
    /// <param name="smart">True for a smart AI.</param>
    /// <remarks>Follows 0x00440ccc.</remarks>
    public void Build(bool smart)
    {
        SpotDist = -1.0f;
        if (smart)
        {
            Think = 3;
            Spot = 150.0f;
            MaxSpeed = 1.0f;
            Triple(RateUnused, BitsFloat(0x3cf5c28f));
            Triple(Rate, BitsFloat(0x3d8f5c29));
            UnusedA = 0.0f;
            UnusedB = BitsFloat(0x3e4ccccd);
        }
        else
        {
            Think = 50;
            Spot = 50.0f;
            MaxSpeed = BitsFloat(0x3ecccccd);
            Triple(RateUnused, BitsFloat(0x3c03126f));
            Triple(Rate, BitsFloat(0x3d4ccccd));
            UnusedA = BitsFloat(0x3fa66666);
            UnusedB = 4.0f;
        }
        SmartRange = 145.0f;
        DumbRange = 55.0f;
        SmartMinAccuracy = BitsFloat(0x3e4ccccd);
    }

    /// <summary>Applies the IQ to the skill block.</summary>
    /// <param name="s">The IQ value.</param>
    /// <remarks>Follows 0x00440d98.</remarks>
    public void ApplyIq(float s)
    {
        AISkill smart = new(), dumb = new();
        smart.Build(true);
        dumb.Build(false);
        Think = dumb.Think + (int)Math.Truncate(Mul(smart.Think - dumb.Think, s));
        Spot = X87.F32(AIMath.LerpF(s, smart.Spot, dumb.Spot));
        MaxSpeed = X87.F32(AIMath.LerpF(s, smart.MaxSpeed, dumb.MaxSpeed));
        Triple(RateUnused, X87.F32(AIMath.LerpD(s, 0.03, 0.008)));
        // The yaw rate is lerp(0.07, 0.05) with the 80-bit intermediate of 0x0043d5d0.
        Triple(Rate, X87.F32(AIMath.LerpD(s, 0.07, 0.05)));
        UnusedA = X87.F32(AIMath.LerpF(s, smart.UnusedA, dumb.UnusedA));
        UnusedB = X87.F32(AIMath.LerpF(s, smart.UnusedB, dumb.UnusedB));
        Range = X87.F32(AIMath.LerpF(s, SmartRange, DumbRange));
        MinAccuracy = X87.F32(AIMath.LerpF(s, SmartMinAccuracy, 0.0f));
        Skill = s;
    }

    /// <summary>Returns the aim accuracy at a distance.</summary>
    /// <param name="dist">The distance to the target.</param>
    /// <returns>The accuracy.</returns>
    /// <remarks>Follows 0x00440eb8.</remarks>
    public float Accuracy(float dist)
    {
        var p = X87.F32(AIMath.FractionOf(Range, 0.0f, dist));
        return X87.F32(AIMath.LerpF(p, MinAccuracy, Skill));
    }

    /// <summary>Returns the distance at which the AI spots targets.</summary>
    /// <returns>The variable <c>spotDist</c> when it is not negative, else the derived value.</returns>
    /// <remarks>Follows 0x00440eec.</remarks>
    public float SpotDistance() => SpotDist < 0.0f ? Spot : SpotDist;
}

// The file-local helpers of src/game/ai_object.cpp (anonymous namespace).
internal static class AIMath
{
    public const double kPiD = 3.14159265358979323846;
    public const double kHalfPiD = 1.5707963267948966;
    public const double kTwoPiD = 6.283185307179586;
    public const float kTwoPiF = 6.2831855f; // 0x40c90fdb

    // src/game/ai_object.cpp x87Fpatan, x87Fptan, x87Pi: fpatanLD, fptanLD, fldpiLD in their portable form.
    public static double X87Fpatan(double y, double x) => Math.Atan2(y, x);
    public static double X87Fptan(double x) => Math.Tan(x);
    public static double X87Pi() => 3.14159265358979323846;

    // src/game/ai_object.cpp borlandAtan: Borland atan (0x00606914) for finite arguments.
    public static double BorlandAtan(double r)
    {
        if (r == 0)
            return r;
        var negative = double.IsNegative(r);
        var a = Math.Abs(r);
        if (a == 1)
        {
            var v1 = Div(X87Pi(), 4);
            return negative ? -v1 : v1;
        }
        if (a < 1)
        {
            var v2 = X87Fpatan(a, 1.0);
            return negative ? -v2 : v2;
        }
        var v = Sub(X87Fpatan(1.0, a), kHalfPiD);
        return negative ? v : -v;
    }

    // src/game/ai_object.cpp borlandAtan2: Borland atan2 (0x006067ac) for finite arguments.
    public static double BorlandAtan2(double a, double b)
    {
        var pi = X87Pi();
        if (a == 0.0)
        {
            if (b == 0.0)
                return 0; // the original's error handler never returns (DECISIONS D11)
            if (double.IsNegative(b))
                return double.IsNegative(a) ? -pi : pi;
            return a;
        }
        if (b == 0.0)
            return double.IsNegative(a) ? -kHalfPiD : kHalfPiD;
        var v = BorlandAtan(Div(a, b));
        if (double.IsNegative(b))
            v = double.IsNegative(a) ? Sub(v, pi) : Add(v, pi);
        return v;
    }

    // src/game/ai_object.cpp atan2Safe: 0x0043a89c: atan2(y, x), 0 when both squares are below 1e-12.
    public static double Atan2Safe(float y, float x)
    {
        const float eps = 1e-12f;
        if (Mul(x, x) < eps && Mul(y, y) < eps)
            return 0;
        return BorlandAtan2(y, x);
    }

    // The 80-bit pi / 4 of crtTrig, rounded to 53 bits.
    private static readonly double kQuarter = Math.ScaleB(0xc90fdaa22168c235UL, -64);

    // src/core/ldouble.hpp fpremLD (portable): the remainder by `quarter`, and the low three bits of the quotient.
    private static double FpremLD(double ax, double quarter, out uint octant)
    {
        var rem = ax % quarter; // std::fmod (exact)
        octant = (uint)((ulong)Math.Truncate(Div(Sub(ax, rem), quarter)) & 7u);
        return rem;
    }

    // src/game/ai_object.cpp crtTrig: Borland sin (0x00606ff0) and cos (0x00606890): reduction by pi / 4 with fprem,
    // fptan.
    public static double CrtTrig(double x, bool cosine)
    {
        if (x == 0.0)
            return cosine ? 1.0 : x;
        if (!(Math.Abs(x) < 9007199254740992.0))
            return 0; // the original reports a range error
        var negative = double.IsNegative(x);
        var quarter = kQuarter;
        var ax = Math.Abs(x);
        var rem = FpremLD(ax, quarter, out var n); // n: the octant, quotient mod 8
        var sign = negative ? 1u : 0u;
        if (cosine)
        {
            n = (n + 2) & 7;
            sign = 0;
        }
        var r = rem;
        if ((n & 1) != 0)
            r = Sub(quarter, r);
        var t = X87Fptan(r);
        var root = Sqrt(Add(1.0, Mul(t, t)));
        if ((((n >> 2) & 1) ^ sign) != 0)
            root = -root;
        var tangentOverRoot = (n & 3) is 0 or 3;
        return tangentOverRoot ? Div(t, root) : Div(1.0, root);
    }

    // src/game/ai_object.cpp wrapAngle: 0x0043c55c: puts b into [a, a + 2 pi].
    public static float WrapAngle(float a, float b)
    {
        while (Sub(b, a) > kTwoPiD)
            b = X87.F32(Sub(b, kTwoPiF));
        while (Sub(b, a) < 0.0f)
            b = X87.F32(Add(kTwoPiF, b));
        return b;
    }

    // src/game/ai_object.cpp lerpF: 0x0043d67c.
    public static double LerpF(float t, float a, float b) => Add(Mul(Sub(a, b), t), b);

    // src/game/ai_object.cpp lerpD: 0x0043d5d0.
    public static double LerpD(float t, double a, double b) => Add(Mul(Sub(a, b), t), b);

    // src/game/ai_object.cpp fractionOf: 0x0043d5e4: position of c between b and a.
    public static double FractionOf(float a, float b, float c)
    {
        if (b < a)
        {
            if (!(c > b))
                return 0.0f;
            if (c >= a)
                return 1.0f;
            float n = X87.F32(Sub(c, b)), d = X87.F32(Sub(a, b));
            return Div(n, d);
        }
        if (b > a)
        {
            if (c >= b)
                return 0.0f;
            if (c <= a)
                return 1.0f;
            float n = X87.F32(Sub(c, b)), d = X87.F32(Sub(a, b));
            return Div(n, d);
        }
        return 0.0f;
    }

    // src/game/ai_object.cpp aiRandom.
    public static R250 AiRandom() => StaticRandom.Ai();

    // src/game/ai_object.cpp cross3.
    public static void Cross3(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        output[0] = X87.F32(Sub(Mul(a[1], b[2]), Mul(a[2], b[1])));
        output[1] = X87.F32(Sub(Mul(a[2], b[0]), Mul(a[0], b[2])));
        output[2] = X87.F32(Sub(Mul(a[0], b[1]), Mul(a[1], b[0])));
    }

    // src/game/ai_object.cpp intern.
    public static string Intern(string s) => StringTable.Global().Intern(s);

    // src/game/ai_object.cpp playerOfRep: 0x0043fe38.
    public static Player? PlayerOfRep(GameState g, int clientId)
    {
        var r = g.Players.Find(clientId);
        var m = g.Sim?.Server();
        if (r == null || r.OwnedObject == 0 || m == null)
            return null;
        return Player.From(m.FindById(r.OwnedObject));
    }

    // src/game/ai_object.cpp targetPoint.
    public static void TargetPoint(GameState g, Player p, Span<float> output) => AIObject.AiTargetPoint(g, p, output);

    // src/game/ai_object.cpp fmtF: the engine's printf rounds by the FPU mode, which it leaves on truncation: 0.169999.
    public static string FmtF(float v)
    {
        using var chop = ChopScope(); // fesetround(kChopMode), the previous mode restored
        return CFormat.F(v, 6);
    }
}

/// <summary>An AI player: the per-AI object with its skill block, directives, steering, aiming and target bookkeeping.</summary>
/// <remarks>Port of class AIObject in src/game/ai_manager.hpp. See docs/ai-sim.md. The field offsets of the original are in docs/ai-sim.md 1.3.</remarks>
public sealed partial class AIObject : SimObject
{
    /// <summary>The game state.</summary>
    public readonly GameState Game;
    /// <summary>The AI flags: 1 death handled, 2 display name differs, 4 laser aim, 8 variables registered.</summary>
    public uint Flags;
    /// <summary>True while the AI is active.</summary>
    public bool Active;
    /// <summary>The aim error phase.</summary>
    public byte ErrorPhase;
    /// <summary>The think counter.</summary>
    public ushort ThinkCounter;
    /// <summary>True once the home position was captured.</summary>
    public bool HomeCaptured;
    /// <summary>The name of the died callback.</summary>
    public string? CbDied;
    /// <summary>The name of the bored callback.</summary>
    public string? CbBored;
    /// <summary>The name of the periodic callback.</summary>
    public string? CbPeriodic;
    /// <summary>The speed of the current projectile.</summary>
    public float ProjectileSpeed;
    /// <summary>The skill.</summary>
    public float Skill;
    /// <summary>The IQ.</summary>
    public float Iq;
    /// <summary>True once the waypoints are done.</summary>
    public bool WaypointsDone;
    /// <summary>The order of the current waypoint.</summary>
    public int WaypointOrder;
    /// <summary>The waypoint direction, 1 forward.</summary>
    public int Direction = 1;
    /// <summary>The path type.</summary>
    public int PathType;
    /// <summary>The client id of the last line of sight check.</summary>
    public int LastLosClient;
    /// <summary>The line of sight counter.</summary>
    public int LosCounter = 5;
    /// <summary>The line of sight period.</summary>
    public int LosPeriod = 5;
    /// <summary>The seek offset.</summary>
    public readonly float[] SeekOff = [3, 3, 0];
    /// <summary>The previous waypoint point.</summary>
    public readonly float[] PreviousPoint = new float[3];
    /// <summary>The attack mode.</summary>
    public int AttackMode;
    /// <summary>The id of the target (+0xb4).</summary>
    public int TargetId = -1;
    /// <summary>The current target as an index into the directives, -1 for none (+0xb8).</summary>
    public int CurrentTarget = -1;
    /// <summary>The automatic target bits.</summary>
    public readonly byte[] AutoBits = new byte[18];
    /// <summary>The periodic callback counter.</summary>
    public int PeriodicCounter;
    /// <summary>The periodic callback period.</summary>
    public int PeriodicPeriod;
    /// <summary>The team id, -1 for none.</summary>
    public int Team = -1;
    /// <summary>The move the AI issues.</summary>
    public Move Move = new();
    /// <summary>The AI state.</summary>
    public int State;

    /// <summary>The trigger state of the AI weapon.</summary>
    public sealed class TriggerState
    {
        /// <summary>The trigger count.</summary>
        public int Count;
        /// <summary>The trigger cooldown.</summary>
        public int Cooldown;
        /// <summary>The trigger probability per roll.</summary>
        public float Pct = 0.17f;
        /// <summary>The trigger windup.</summary>
        public int Windup = 6;
    }
    /// <summary>The trigger state.</summary>
    public readonly TriggerState Trigger = new();
    /// <summary>The skill block.</summary>
    public readonly AISkill Sk = new();
    /// <summary>True when the targets are scripted (+0x164).</summary>
    public bool ScriptedTargets = true;
    /// <summary>The attack id (+0x198).</summary>
    public int AttackId;
    /// <summary>The id of the representative.</summary>
    public int RepId;
    /// <summary>True once a client record was taken.</summary>
    public bool RecordTaken;
    /// <summary>The AI name (+0x1a0, 31 characters at most). <c>SimObject.Name()</c> is the object name.</summary>
    public string AiName = "";  // name (+0x1a0, 31 characters at most); SimObject.Name() is the object name
    /// <summary>The voice set.</summary>
    public string Voice = "";
    /// <summary>The result text of the last command.</summary>
    public string Result = "Done";
    /// <summary>The client id of the AI player.</summary>
    public int PlayerId;
    /// <summary>The Player object of the AI.</summary>
    public Player? PlayerPtr;
    /// <summary>The directives in order.</summary>
    public List<AIDirective> Directives = [];

    /// <summary>The per-tick frame state (docs/ai-sim.md 1.6).</summary>
    public sealed class FrameState
    {
        /// <summary>True when the frame has a move target.</summary>
        public bool HasMoveTarget;
        /// <summary>The move target.</summary>
        public readonly float[] MoveTarget = [-1e6f, -1e6f, -1e6f];
        /// <summary>The own position.</summary>
        public readonly float[] Own = new float[3];
    }
    /// <summary>The frame state of the current tick.</summary>
    public FrameState Frame = new();

    /// <summary>The yaw error of a steering step.</summary>
    public struct YawError
    {
        /// <summary>The absolute error.</summary>
        public float Abs;
        /// <summary>The desired angle.</summary>
        public float Desired;
        /// <summary>The current angle.</summary>
        public float Cur;
        /// <summary>The signed difference.</summary>
        public float Diff;
    }

    /// <summary>Creates an AI object.</summary>
    /// <param name="g">The game state.</param>
    public AIObject(GameState g)
    {
        Game = g;
        CbDied = AIMath.Intern("AI::onDroneKilled");
        CbPeriodic = AIMath.Intern("AI::onPeriodic");
        Sk.Build(false);
        Sk.Range = 0;
        Sk.MinAccuracy = 0;
        Sk.Skill = 0;
    }

    /// <inheritdoc/>
    public override string ClassName() => "AI::Object";
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;

    /// <inheritdoc/>
    public override void Destroy() => Game.Ai?.Remove(this);

    /// <summary>Computes the target point of a Player: the centre of its container box.</summary>
    /// <param name="g">The game state.</param>
    /// <param name="p">The Player.</param>
    /// <param name="output">Receives the point.</param>
    /// <remarks>Follows Player_getTargetPoint (0x004125a0).</remarks>
    public static void AiTargetPoint(GameState g, Player p, Span<float> output)
    {
        Span<float> mn = stackalloc float[3], mx = stackalloc float[3];
        if (!Pj.WorldBox(g, p, mn, mx))
        {
            for (var i = 0; i < 3; ++i)
                output[i] = p.Transform.P[i];
            return;
        }
        for (var i = 0; i < 3; ++i)
        {
            var s = X87.F32(Add(mx[i], mn[i]));
            output[i] = X87.F32(Mul(s, 0.5f));
        }
    }

    /// <summary>Returns the Player of the AI.</summary>
    /// <returns>The Player, or null once it is gone.</returns>
    public Player? Player()
    {
        var m = Game.Sim?.Server();
        if (m == null || PlayerPtr == null)
            return null;
        var o = m.FindById(PlayerId);
        return o != null && o == PlayerPtr ? PlayerPtr : null;
    }

    /// <summary>Tells whether the AI is active and its Player exists.</summary>
    /// <returns>True when valid.</returns>
    public bool Valid() => Active && Player() != null;

    /// <inheritdoc/>
    /// <remarks>Follows 0x0043db28.</remarks>
    public override bool OnAdd()
    {
        var p = Player();
        if (p == null)
            return false;
        RepId = Game.AllocateRecordId != null ? Game.AllocateRecordId() : Game.Players.NextFreeId();
        var playerName = p.MapName ?? "";
        var male = Voice.Length >= 4 && GameUtil.EqualsNoCase(Voice[..4], "male");
        if (RepId < 2049 || RepId >= 2176 || Game.Players.AddClient(RepId, playerName, Voice, male, true) == null)
        {
            if (Game.ReleaseRecordId != null && RepId >= 2049)
                Game.ReleaseRecordId(RepId);
            RepId = 0;
            return false;
        }
        RecordTaken = true;
        if (!GameUtil.EqualsNoCase(playerName, AiName))
            Flags |= 2;
        Game.Players.SetOwnerClient(p, RepId);
        Active = true;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x0043dbd8.</remarks>
    public override void OnRemove()
    {
        Active = false;
        if (RecordTaken)
        {
            RecordTaken = false;
            Game.Players.RemoveClient(RepId);
            Game.ReleaseRecordId?.Invoke(RepId);
        }
        Game.Ai?.Remove(this);
    }

    /// <summary>Tests the AI name against a pattern.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>True when the name matches.</returns>
    /// <remarks>Follows 0x0043ffbc.</remarks>
    public bool Matches(string pattern)
    {
        if (ResourceManager.WildcardMatch(pattern, AiName))
            return true;
        if ((Flags & 2) != 0)
        {
            var p = Player();
            return p != null && p.MapName != null && ResourceManager.WildcardMatch(pattern, p.MapName);
        }
        return false;
    }

    // ---- variables (5.1) ----

    // src/game/ai_object.cpp VarType, VarDef, kVars.
    private enum VarType { FloatG, Int, Point, FloatF }
    private readonly record struct VarDef(string Name, VarType Type, bool Recompute);
    private static readonly VarDef[] kVars =
    [
        new("iq", VarType.FloatG, true),
        new("pathType", VarType.Int, false),
        new("spotDist", VarType.FloatG, false),
        new("seekOff", VarType.Point, false),
        new("attackMode", VarType.Int, false),
        new("triggerWindup", VarType.Int, false),
        new("triggerPct", VarType.FloatF, false),
        new("smartGuyWeaponRange", VarType.FloatG, true),
        new("dumbGuyWeaponRange", VarType.FloatG, true),
        new("smartGuyMinAccuracy", VarType.FloatG, true),
    ];

    /// <summary>Returns the console variable name of an AI variable.</summary>
    /// <param name="var">The variable name.</param>
    /// <returns>The name <c>$AI::name::var</c>.</returns>
    public string VarName(string var) => "$AI::" + AiName + "::" + var;

    // src/game/ai_object.cpp variableChanged: the function-static flag: the stored text is not parsed again.
    private static bool sCanonicalizing;

    /// <summary>Applies a changed AI console variable.</summary>
    /// <param name="var">The variable name.</param>
    /// <param name="value">The new value.</param>
    /// <param name="notify">True to notify the script.</param>
    public void VariableChanged(string var, string value, bool notify = true)
    {
        var type = VarType.Int;
        int which; // the field: the C++ float* / int32_t*
        bool eq(string n) => GameUtil.EqualsNoCase(var, n);
        if (eq("iq"))
        { which = 0; type = VarType.FloatG; }
        else if (eq("pathType"))
        { which = 1; }
        else if (eq("spotDist"))
        { which = 2; type = VarType.FloatG; }
        else if (eq("seekOff"))
        { which = 3; type = VarType.Point; }
        else if (eq("attackMode"))
        { which = 4; }
        else if (eq("triggerWindup"))
        { which = 5; }
        else if (eq("triggerPct"))
        { which = 6; type = VarType.FloatF; }
        else if (eq("smartGuyWeaponRange"))
        { which = 7; type = VarType.FloatG; }
        else if (eq("dumbGuyWeaponRange"))
        { which = 8; type = VarType.FloatG; }
        else if (eq("smartGuyMinAccuracy"))
        { which = 9; type = VarType.FloatG; }
        else
            return;
        if (sCanonicalizing)
            return;
        string canon;
        if (type == VarType.Point)
        {
            SeekOff[0] = SeekOff[1] = SeekOff[2] = 0; // 0x005f5305: components that do not parse are 0
            MathCommands.ScanFloats3(value, SeekOff);
            canon = AIMath.FmtF(SeekOff[0]) + " " + AIMath.FmtF(SeekOff[1]) + " " + AIMath.FmtF(SeekOff[2]);
        }
        else if (type == VarType.Int)
        {
            var v = Numbers.BorlandAtoi(value);
            switch (which)
            {
                case 1:
                    PathType = v;
                    break;
                case 4:
                    AttackMode = v;
                    break;
                default:
                    Trigger.Windup = v;
                    break;
            }
            canon = Numbers.FormatInt(v);
        }
        else
        {
            var f = X87.F32(Numbers.BorlandAtof(value));
            if (type == VarType.FloatF)
            { // the console's percent type (0x005f52bf) clamps to 0..1
                if (f < 0.0f)
                    f = 0.0f;
                else if (f > 1.0f)
                    f = 1.0f;
            }
            switch (which)
            {
                case 0:
                    Iq = f;
                    break;
                case 2:
                    Sk.SpotDist = f;
                    break;
                case 6:
                    Trigger.Pct = f;
                    break;
                case 7:
                    Sk.SmartRange = f;
                    break;
                case 8:
                    Sk.DumbRange = f;
                    break;
                default:
                    Sk.SmartMinAccuracy = f;
                    break;
            }
            canon = type == VarType.FloatF ? AIMath.FmtF(f) : Numbers.FormatFloat(f);
        }
        if (Game.Console != null && canon != value)
        {
            sCanonicalizing = true;
            Game.Console.SetVariable(VarName(var), canon); // reads back in the typed form
            sCanonicalizing = false;
        }
        if (notify && Game.Ai != null && (eq("iq") || eq("smartGuyWeaponRange") || eq("dumbGuyWeaponRange") ||
                                          eq("smartGuyMinAccuracy")))
            Game.Ai.Recompute = true;
    }

    /// <summary>Registers the console variables of the AI.</summary>
    /// <remarks>Follows 0x0043ad04.</remarks>
    public void RegisterVariables()
    {
        if ((Flags & 8) != 0 || Game.Console == null)
            return;
        var inr = Game.Console;
        var g = Game;
        var aiName = AiName;
        foreach (var d in kVars)
        {
            var full = VarName(d.Name);
            var var = d.Name;
            void Bind() =>
                inr.BindVariable(full, v =>
                {
                    var mgr = g.Ai;
                    var ai = mgr?.FindByName(aiName);
                    if (ai != null && (ai.Flags & 8) != 0)
                        ai.VariableChanged(var, v);
                });
            if (inr.HasVariable(full))
            {
                // A script value set before the spawn is applied without the change callback.
                VariableChanged(var, inr.PeekVariable(full), false);
                Bind();
                continue;
            }
            var s = "";
            var n = d.Name;
            switch (d.Type)
            {
                case VarType.FloatG:
                    s = Numbers.FormatFloat(n == "iq" ? Iq : n == "spotDist" ? Sk.SpotDist
                                            : n == "smartGuyWeaponRange" ? Sk.SmartRange
                                            : n == "dumbGuyWeaponRange" ? Sk.DumbRange : Sk.SmartMinAccuracy);
                    break;
                case VarType.Int:
                    s = Numbers.FormatInt(n == "pathType" ? PathType : n == "attackMode" ? AttackMode : Trigger.Windup);
                    break;
                case VarType.Point:
                    s = AIMath.FmtF(SeekOff[0]) + " " + AIMath.FmtF(SeekOff[1]) + " " + AIMath.FmtF(SeekOff[2]);
                    break;
                case VarType.FloatF:
                    s = AIMath.FmtF(Trigger.Pct);
                    break;
            }
            inr.SetVariable(full, s);
            Bind();
        }
        Flags |= 8;
    }

    /// <summary>Removes the console variables of the AI.</summary>
    /// <remarks>Follows 0x0043af04.</remarks>
    public void RemoveVariables()
    {
        if ((Flags & 8) == 0 || Game.Console == null)
            return;
        foreach (var d in kVars)
            Game.Console.EraseGlobal(VarName(d.Name));
        Flags &= ~8u;
    }

    // 0x0043b104 holds 0.01 as an 80-bit constant, which is below 0.01. Rounded toward zero to 53 bits, not to the
    // nearest double (above 0.01): with the nearest double f32(100 x 0.01) is 1, where the original gets 0.99999994 and
    // think period 4 (tests/test_ai.cpp testBehavior iq 100; aidiff motion c_rates, d_follow, target e_target_iq100).
    private static readonly double kHundredth = Math.ScaleB(0xa3d70a3d70a3d70aUL & ~0x7ffUL, -70);

    /// <summary>Recomputes the skill block from the IQ and the variables.</summary>
    /// <remarks>Follows 0x0043b0ac.</remarks>
    public void RecomputeSkill()
    {
        var v = Mul(kHundredth, Iq);
        var s = X87.F32(v);
        if (!(v >= 0.0f))
            s = 0;
        else if (s > 1.0f)
            s = 1.0f;
        Skill = s;
        Sk.ApplyIq(s);
        LosCounter = LosPeriod = 5;
    }

    // ---- directives (2) ----

    // src/game/ai_object.cpp defaultCallbacks: 0x004409bc.
    private static void DefaultCallbacks(AIDirective d)
    {
        for (var i = 0; i < 4; ++i)
            d.Callback[i] = null;
        if (d.Type == AIDirective.kTarget)
        {
            d.Callback[0] = AIMath.Intern("AI::onTargetLOSAcquired");
            d.Callback[1] = AIMath.Intern("AI::onTargetDied");
            d.Callback[2] = AIMath.Intern("AI::onTargetLOSLost");
            d.Callback[3] = AIMath.Intern("AI::onTargetLOSRegained");
        }
    }

    // src/game/ai_object.cpp cmpOrder.
    private static int CmpOrder(AIDirective a, AIDirective b) => a.Order < b.Order ? -1 : (b.Order < a.Order ? 1 : 0);

    private void SortDirectives() => Borland.BorlandQsort(CollectionsMarshal.AsSpan(Directives), CmpOrder);

    // src/game/ai_object.cpp AIObject::findDirective: 0x00440008.
    private int FindDirective(int order, int mask, bool upper)
    {
        for (var i = 0; i < Directives.Count; ++i)
        {
            var d = Directives[i];
            if ((d.Type & mask) == 0)
                continue;
            if (order == d.Order)
                return i;
            if (upper && order < d.Order)
                return i;
        }
        return -1;
    }

    /// <summary>Adds a directive.</summary>
    /// <param name="d">The directive, taken by value.</param>
    /// <param name="order">The order. Below 1 it is assigned.</param>
    /// <param name="sort">True to keep the directives sorted.</param>
    /// <returns>True when the directive was added.</returns>
    /// <remarks>Follows 0x00440420.</remarks>
    public bool AddDirective(AIDirective d, int order, bool sort = true)
    {
        d = d.Clone();
        DefaultCallbacks(d);
        if (order >= 1)
        {
            d.Order = order;
            var at = FindDirective(order, 0xffff, false);
            if (at >= 0)
            {
                Directives[at] = d;
            }
            else
            {
                Directives.Add(d);
                if (sort)
                    SortDirectives();
            }
        }
        else
        {
            int v;
            if (Directives.Count == 0)
            {
                v = 100;
            }
            else
            {
                v = Directives[^1].Order;
                if (v >= 0x2eb2c000)
                {
                    var i = Directives.Count - 1;
                    while (i >= 0 && unchecked(Directives[i].Order + 150) >= 0x2eb2c000)
                        --i;
                    v = i < 0 ? 100 : unchecked(Directives[i].Order + 150);
                }
                v -= v % 100;
            }
            d.Order = v;
            Directives.Add(d);
            if (sort)
                SortDirectives();
        }
        if ((d.Type & 3) != 0)
            WaypointsDone = false;
        return true;
    }

    // ---- distances ----

    /// <summary>Computes the 3D distance between two points.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <returns>The distance of <c>a - b</c>.</returns>
    /// <remarks>Follows 0x0043a9f0.</remarks>
    public float Dist3(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> d = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(a[i], b[i]));
        if ((Flags & 4) != 0)
            return X87.F32(X87.Len(d));
        return X87.Lenf(d);
    }

    /// <summary>Computes the distance between two points in the x and y plane.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <returns>The distance.</returns>
    /// <remarks>Follows 0x0043a924.</remarks>
    public static float Dist2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dx = X87.F32(Sub(a[0], b[0])), dy = X87.F32(Sub(a[1], b[1]));
        var s = X87.F32(Add(Mul(dx, dx), Mul(dy, dy)));
        return X87.F32(Sqrt(s));
    }

    // ---- callbacks (4) ----

    /// <summary>Runs a directive callback.</summary>
    /// <param name="d">The directive, or null.</param>
    /// <param name="slot">The callback slot.</param>
    /// <remarks>Follows 0x00440a28.</remarks>
    public void Callback(AIDirective? d, int slot)
    {
        var fn = d != null ? d.Callback[slot] : (slot == 0 ? CbDied : slot == 1 ? CbBored : CbPeriodic);
        if (string.IsNullOrEmpty(fn) || Game.Console == null)
            return;
        var args = new List<string> { AiName };
        if (d != null && d.Type == AIDirective.kTarget)
            args.Add(Numbers.FormatInt(d.Client));
        Game.Console.CallFunction(fn, args);
    }

    // ---- sense (2.4, 2.5, 3.1) ----

    // src/game/ai_object.cpp AIObject::directivePoint: 0x0043b484.
    private void DirectivePoint(AIDirective d, Span<float> output)
    {
        if ((d.Type & AIDirective.kWaypoint) != 0)
        {
            d.Point.CopyTo(output);
            return;
        }
        var p = AIMath.PlayerOfRep(Game, d.Client);
        if (p != null)
        {
            Span<float> tp = stackalloc float[3];
            AIMath.TargetPoint(Game, p, tp);
            for (var i = 0; i < 3; ++i)
                output[i] = X87.F32(Add(SeekOff[i], tp[i]));
            return;
        }
        Frame.Own.CopyTo(output);
    }

    // src/game/ai_object.cpp AIObject::atWaypoint: 0x0043bc64.
    private bool AtWaypoint(ReadOnlySpan<float> p) => Dist2(Frame.Own, p) < 1.2;

    // src/game/ai_object.cpp AIObject::nextWaypoint: 0x0043b148.
    private int NextWaypoint(int order)
    {
        const int mask = 3;
        var cur = FindDirective(order, mask, false);
        var result = -1;
        var count = Directives.Count;
        if (PathType == 2)
        {
            var list = new List<int>();
            for (var i = 0; i < count; ++i)
                if ((Directives[i].Type & mask) != 0)
                    list.Add(i);
            var n = list.Count;
            var pos = 0;
            while (pos < n && Directives[list[pos]].Order != order)
                ++pos;
            var found = n > pos;
            if (n > 1)
            {
                if (found)
                {
                    if (pos == n - 1)
                        Direction = -1;
                    else if (pos == 0)
                        Direction = 1;
                    result = list[pos + Direction];
                }
                else if (order > Directives[n - 1].Order)
                { // raw array entries (original quirk)
                    Direction = -1;
                    result = n - 1;
                }
                else if (order < Directives[0].Order)
                {
                    Direction = 1;
                    result = 0;
                }
                else
                {
                    for (var i = 0; i < n - 1; ++i)
                    {
                        var lo = Directives[list[i]];
                        var hi = Directives[list[i + 1]];
                        if (order < hi.Order && order > lo.Order)
                        {
                            result = Direction == -1 ? list[i] : list[i + 1];
                            break;
                        }
                    }
                }
            }
            else if (n == 1 && !found)
            {
                result = list[0];
            }
        }
        else if (cur == -1)
        {
            result = FindDirective(order, mask, true);
        }
        else
        {
            var idx = cur;
            for (var left = count - 1; left > 0; --left)
            {
                var next = (idx + 1) % count;
                if (idx > next && PathType != 0)
                    break;
                idx = next;
                if ((Directives[next].Type & mask) != 0)
                {
                    result = next;
                    break;
                }
            }
        }
        if (result < 0)
        {
            WaypointsDone = true;
            return -1;
        }
        WaypointOrder = Directives[result].Order;
        if (cur >= 0)
            DirectivePoint(Directives[cur], PreviousPoint);
        else
            Frame.Own.CopyTo(PreviousPoint, 0);
        return result;
    }

    // src/game/ai_object.cpp AIObject::los: 0x00440abc.
    private bool Los(Player from, Player to)
    {
        Transform a = from.EyeTransform(), b = to.EyeTransform();
        var hit = new RayHit();
        if (!Projectile.CastWorld(Game, a.P, b.P, 0xffdffe77u, from, ref hit))
            return true;
        return hit.Object == to;
    }

    // src/game/ai_object.cpp AIObject::targetInRange: 0x0043b590.
    private bool TargetInRange(AIDirective d)
    {
        if (d.Sub != AIDirective.kPlayer)
            return true;
        var p = AIMath.PlayerOfRep(Game, d.Client);
        if (p == null || p.Dead)
            return false;
        var spot = Sk.SpotDistance();
        Span<float> tp = stackalloc float[3];
        AIMath.TargetPoint(Game, p, tp);
        Span<float> diff = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(Frame.Own[i], tp[i]));
        return X87.Lenf(diff) < spot;
    }

    // src/game/ai_object.cpp AIObject::scanTargets: 0x0043b66c.
    private void ScanTargets()
    {
        var list = new List<int>();
        for (var i = 0; i < Directives.Count; ++i)
            if ((Directives[i].Type & AIDirective.kTarget) != 0)
                list.Add(i);
        var n = list.Count;
        if (n == 0)
            return;
        foreach (var i in list)
        {
            var d = Directives[i];
            if (TargetInRange(d))
                d.Flags |= AIDirective.kInRange;
            else
                d.Flags &= unchecked((ushort)~(AIDirective.kLOS | AIDirective.kHadLOS | AIDirective.kInRange));
        }
        if (++LosCounter >= LosPeriod)
            LosCounter = 0;
        if (LosCounter == 0)
        {
            var idx = -1;
            if (LastLosClient != 0)
                for (var i = 0; i < n; ++i)
                    if (Directives[list[i]].Client == LastLosClient)
                    {
                        idx = i;
                        break;
                    }
            var start = (idx + 1) % n;
            LastLosClient = 0;
            var k = start;
            do
            {
                var d = Directives[list[k]];
                if ((d.Flags & AIDirective.kInRange) != 0)
                {
                    if (d.Sub == AIDirective.kPlayer)
                    {
                        var tp = AIMath.PlayerOfRep(Game, d.Client);
                        var self = Player();
                        var seen = tp != null && self != null && Los(self, tp);
                        if (seen)
                        {
                            if ((d.Flags & AIDirective.kLOS) == 0)
                            {
                                var copy = d.Clone();
                                Callback(copy, (d.Flags & AIDirective.kHadLOS) != 0 ? 3 : 0);
                            }
                            if (list[k] < Directives.Count)
                                Directives[list[k]].Flags |= AIDirective.kLOS | AIDirective.kHadLOS;
                        }
                        else if ((d.Flags & AIDirective.kLOS) != 0)
                        {
                            d.Flags &= unchecked((ushort)~AIDirective.kLOS);
                            var copy = d.Clone();
                            Callback(copy, 2);
                        }
                    }
                    else
                    {
                        d.Flags |= AIDirective.kLOS | AIDirective.kHadLOS;
                    }
                    if (list[k] < Directives.Count)
                        LastLosClient = Directives[list[k]].Client;
                    break;
                }
                k = (k + 1) % n;
            } while (k != start);
        }
        CurrentTarget = -1;
        var best = 3.0e14f; // 0x578edcf4
        for (var k = 0; k < n; ++k)
        {
            if (list[k] >= Directives.Count)
                break;
            var d = Directives[list[k]];
            if (d.Sub != AIDirective.kPlayer)
            {
                CurrentTarget = list[k];
                continue;
            }
            var tp = AIMath.PlayerOfRep(Game, d.Client);
            if (tp != null)
            {
                if (tp.Dead)
                {
                    if ((d.Flags & AIDirective.kDeathReported) == 0)
                    {
                        var copy = d.Clone();
                        Callback(copy, 1);
                        if (list[k] < Directives.Count)
                            Directives[list[k]].Flags |= AIDirective.kDeathReported;
                    }
                }
                else if ((d.Flags & AIDirective.kLOS) != 0)
                {
                    var p = new float[3];
                    AIMath.TargetPoint(Game, tp, p);
                    var diff = new float[3];
                    for (var i = 0; i < 3; ++i)
                        diff[i] = X87.F32(Sub(Frame.Own[i], p[i]));
                    var len = X87.Lenf(diff);
                    if (CurrentTarget < 0 || len < best)
                    {
                        CurrentTarget = list[k];
                        best = len;
                    }
                }
            }
            else if ((d.Flags & AIDirective.kDeathReported) == 0)
            {
                var copy = d.Clone();
                Callback(copy, 1);
                if (list[k] < Directives.Count)
                    Directives[list[k]].Flags |= AIDirective.kDeathReported;
            }
        }
    }

    /// <summary>Senses targets and line of sight.</summary>
    /// <remarks>Follows 0x0043bcb4.</remarks>
    public void Sense()
    {
        var self = Player();
        if (self == null)
            return;
        AIMath.TargetPoint(Game, self, Frame.Own);
        if (!HomeCaptured)
        {
            HomeCaptured = true;
            Frame.Own.CopyTo(PreviousPoint, 0);
        }
        CurrentTarget = -1;
        if (self.Dead)
            AttackId = 0;
        else if (AttackId == 0)
            ScanTargets();
        if (WaypointsDone)
            return;
        var guard = Directives.Count;
        for (; ; )
        {
            if (guard-- == 0)
                break;
            var di = FindDirective(WaypointOrder, 3, false);
            if (di < 0)
                di = NextWaypoint(WaypointOrder);
            if (di < 0)
                break;
            var d = Directives[di].Clone();
            var p = new float[3];
            DirectivePoint(d, p);
            if (d.Type == AIDirective.kFollow || !AtWaypoint(p))
            {
                Frame.HasMoveTarget = true;
                p.CopyTo(Frame.MoveTarget, 0);
                break;
            }
            Callback(d, 0);
            NextWaypoint(WaypointOrder);
        }
        if (!Frame.HasMoveTarget)
            WaypointsDone = true;
    }

    // ---- automatic targets (2.5) ----

    private const uint kAutoBase = 0x5d658000u;

    /// <summary>Refreshes the automatic targets.</summary>
    /// <remarks>Follows 0x00440118.</remarks>
    public void RefreshAutoTargets()
    {
        if (ScriptedTargets)
            return;
        bool Enemy(ClientRecord r)
        { // 0x004400ec
            if (r.Id == RepId)
                return false;
            if (Team != r.Team)
                return true;
            return !(Game.TeamCount - 1 > 0);
        }
        var kept = new List<AIDirective>(Directives.Count);
        foreach (var d in Directives)
        {
            if (d.Type == AIDirective.kTarget && ((uint)d.Order & 0x7fff8000u) == kAutoBase)
            {
                var r = Game.Players.Find(d.Client);
                if (r != null && Enemy(r))
                {
                    kept.Add(d);
                }
                else
                {
                    var bit = d.Client - 2048;
                    if (bit is >= 0 and < 144)
                        AutoBits[bit >> 3] &= (byte)~(1u << (bit & 7));
                }
                continue;
            }
            kept.Add(d);
        }
        Directives = kept;
        var added = false;
        foreach (var id in Game.Players.PlayerIds())
        {
            var r = Game.Players.Find(id);
            if (r == null || !Enemy(r))
                continue;
            var bit = r.Id - 2048;
            if (bit < 0 || bit >= 144 || ((AutoBits[bit >> 3] >> (bit & 7)) & 1) != 0)
                continue;
            var d = new AIDirective
            {
                Type = AIDirective.kTarget,
                Sub = AIDirective.kPlayer,
                Client = r.Id,
                Order = unchecked((int)(kAutoBase | (uint)r.Id)),
            };
            DefaultCallbacks(d);
            Directives.Add(d);
            AutoBits[bit >> 3] |= (byte)(1u << (bit & 7));
            added = true;
        }
        if (added)
            SortDirectives();
    }

    /// <summary>Switches to automatic targets.</summary>
    /// <remarks>Follows 0x00440348.</remarks>
    public void SetAutomaticTargets()
    {
        if (ScriptedTargets)
        {
            ScriptedTargets = false;
            Array.Clear(AutoBits);
        }
    }

    /// <summary>Switches to scripted targets.</summary>
    /// <remarks>Follows 0x00440370.</remarks>
    public void SetScriptedTargets()
    {
        if (!ScriptedTargets)
        {
            ScriptedTargets = true;
            var kept = new List<AIDirective>();
            foreach (var d in Directives)
                if (!(d.Type == AIDirective.kTarget && ((uint)d.Order & 0x7fff8000u) == kAutoBase))
                    kept.Add(d);
            Directives = kept;
        }
    }

    // ---- behavior (3) ----

    // src/game/ai_object.cpp AIObject::thinkGate: 0x0043b564.
    private bool ThinkGate(bool force)
    {
        if (force)
        {
            ThinkCounter = 0;
        }
        else
        {
            ++ThinkCounter;
            if (ThinkCounter >= Sk.Think)
                ThinkCounter = 0;
        }
        return ThinkCounter == 0;
    }

    // src/game/ai_object.cpp AIObject::attackTargetVisible: 0x0043b9d8.
    private Player? AttackTargetVisible(bool force)
    {
        if (AttackId <= 0x800)
            return null;
        if (!ThinkGate(force))
            return null;
        var p = AIMath.PlayerOfRep(Game, AttackId);
        if (p == null || p.Dead)
            return null;
        Span<float> tp = stackalloc float[3];
        AIMath.TargetPoint(Game, p, tp);
        var d = Dist3(Frame.Own, tp);
        if (!(Sk.SpotDistance() > d))
            return null;
        var self = Player();
        if (self == null || !Los(self, p))
            return null;
        return p;
    }

    // src/game/ai_object.cpp AIObject::decide: 0x0043bbd0.
    private void Decide(bool force)
    {
        if (!ThinkGate(force))
            return;
        if (!WaypointsDone && FindDirective(-1, 3, true) != -1)
        {
            State = 2;
            return;
        }
        if (AttackId != 0)
        {
            State = 3;
            return;
        }
        if (CurrentTarget >= 0)
        {
            State = 4;
            return;
        }
        State = 1;
    }

    // src/game/ai_object.cpp AIObject::stateWaypoint: 0x0043ba90.
    private void StateWaypoint()
    {
        if (Frame.HasMoveTarget)
            MoveToward(Frame.MoveTarget);
        if (AttackTargetVisible(false) != null)
        {
            State = 3;
            return;
        }
        if (CurrentTarget >= 0)
        {
            State = 4;
            return;
        }
        if (!Frame.HasMoveTarget)
            State = 1;
    }

    // src/game/ai_object.cpp AIObject::stateAttack: 0x0043bb20.
    private void StateAttack()
    {
        var p = AttackTargetVisible(true);
        if (p != null)
            EngagePlayer(p);
        else
            State = 0;
    }

    // src/game/ai_object.cpp AIObject::stateTarget: 0x0043bb50.
    private void StateTarget(bool force)
    {
        if (CurrentTarget >= 0 && CurrentTarget < Directives.Count)
        {
            var d = Directives[CurrentTarget].Clone();
            switch (d.Sub)
            {
                case AIDirective.kPlayer:
                    if (AIMath.PlayerOfRep(Game, d.Client) is Player p)
                        EngagePlayer(p);
                    break;
                case AIDirective.kLaser:
                    EngageLaser(d);
                    break;
                case AIDirective.kPoint:
                    EngagePoint(d);
                    break;
                default:
                    break;
            }
        }
        else if (!force)
        {
            State = 0;
        }
    }

    /// <summary>Runs the action of one AI tick.</summary>
    /// <remarks>Follows 0x0043bf3c.</remarks>
    public void Act()
    {
        Move = new Move();
        switch (State)
        {
            case 0:
                Decide(true);
                break;
            case 1:
                Decide(false);
                break;
            case 2:
                StateWaypoint();
                break;
            case 3:
                StateAttack();
                break;
            case 4:
                StateTarget(false);
                break;
            default:
                break;
        }
        if (Player() is Player pl)
        {
            Move[] moves = [Move]; // applyMoves(&move, 1)
            pl.ApplyMoves(moves, 1);
            Move = moves[0];
        }
    }

    // ---- steering (3.3) ----

    /// <summary>Computes the yaw error toward a point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The error.</returns>
    /// <remarks>Follows 0x0043c6e4.</remarks>
    public YawError YawErrorOf(ReadOnlySpan<float> point)
    {
        var self = Player()!;
        var dx = X87.F32(Sub(point[0], Frame.Own[0]));
        var dy = X87.F32(Sub(point[1], Frame.Own[1]));
        var t = AIMath.Atan2Safe(dy, dx);
        var desired0 = X87.F32(Sub(t, AIMath.kHalfPiD));
        var desired = AIMath.WrapAngle(0.0f, desired0);
        var cur = AIMath.WrapAngle(AIMath.kTwoPiF, self.Rotation[2]);
        YawError e;
        float diff;
        float abs;
        for (; ; )
        {
            diff = X87.F32(Sub(desired, cur));
            var d = Sub(desired, cur);
            var a = Math.Abs(d);
            abs = X87.F32(a);
            if (a > 3.1415937f)
                desired = X87.F32(Add(desired, AIMath.kTwoPiD));
            else
                break;
        }
        e.Abs = abs;
        e.Desired = desired;
        e.Cur = cur;
        e.Diff = diff;
        return e;
    }

    // src/game/ai_object.cpp rateStep: 0x00440be4: the yaw step against the rate triple (r, threshold, floor).
    private static float RateStep(float[] rates, float x)
    {
        var v = rates[0];
        if (x < rates[1])
        {
            v = X87.F32(Mul(Div(x, rates[1]), v));
            if (!(v >= rates[2]))
                v = rates[2];
            if (v > x)
                v = x;
        }
        return v;
    }

    /// <summary>Steers the yaw toward a point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The yaw error.</returns>
    /// <remarks>Follows 0x0043d228.</remarks>
    public YawError SteerYaw(ReadOnlySpan<float> point)
    {
        var e = YawErrorOf(point);
        var v = RateStep(Sk.Rate, e.Abs);
        Move.Turn = e.Diff >= 0.0f ? v : -v;
        return e;
    }

    /// <summary>Steers the pitch toward a point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The pitch error.</returns>
    /// <remarks>Follows 0x0043c7bc.</remarks>
    public YawError SteerPitch(ReadOnlySpan<float> point)
    {
        var self = Player()!;
        var d2 = Dist2(point, Frame.Own);
        var dz = X87.F32(Sub(point[2], Frame.Own[2]));
        var desired = X87.F32(AIMath.Atan2Safe(dz, d2));
        var cur = self.Pitch;
        var diff = X87.F32(Sub(desired, cur));
        var abs = Math.Abs(diff);
        var v = 0.084f;
        if (abs < 0.168f)
        {
            v = X87.F32(Mul(Div(abs, 0.168f), 0.084f));
            if (!(v >= 0.03f))
                v = 0.03f;
            if (v > abs)
                v = abs;
        }
        Move.Pitch = diff < 0.0f ? -v : v;
        return new YawError { Abs = abs, Desired = desired, Cur = cur, Diff = diff };
    }

    // 2/3 and 1/3 as the 80-bit constants of 0x0043d0e4, rounded to 53 bits.
    private static readonly double kTwoThirds = Math.ScaleB(0xaaaaaaaaaaaaaaabUL, -64);
    private static readonly double kOneThird = Math.ScaleB(0xaaaaaaaaaaaaaaabUL, -65);

    /// <summary>Computes the speed for a distance to the goal.</summary>
    /// <param name="dist">The distance.</param>
    /// <returns>The speed.</returns>
    /// <remarks>Follows 0x0043d0e4.</remarks>
    public float SpeedForDistance(float dist)
    {
        if (!(dist < 1.7))
            return Sk.MaxSpeed;
        if (!(dist > 0.2))
            return 0.0f;
        var t = X87.F32(Mul(Sub(dist, 0.2), kTwoThirds));
        var lo = X87.F32(Mul(kOneThird, Sk.MaxSpeed));
        if (lo > 0.25f)
            lo = 0.25f;
        return X87.F32(AIMath.LerpF(t, Sk.MaxSpeed, lo));
    }

    /// <summary>Sets the move direction from a yaw error.</summary>
    /// <param name="e">The yaw error.</param>
    /// <param name="speed">The speed.</param>
    /// <param name="adjust">Unused.</param>
    /// <remarks>Follows 0x0043c4b4.</remarks>
    public void SetMoveDirection(in YawError e, float speed, float adjust)
    {
        _ = adjust;
        var ang = X87.F32(Sub(e.Desired, e.Cur));
        var s = X87.F32(Mul(AIMath.CrtTrig(ang, false), speed));
        var c = X87.F32(Mul(AIMath.CrtTrig(ang, true), speed));
        if (s > 0.0f)
        {
            Move.Left = s;
            Move.Right = 0;
        }
        else
        {
            Move.Right = -s;
            Move.Left = 0;
        }
        if (c > 0.0f)
        {
            Move.Forward = c;
            Move.Backward = 0;
        }
        else
        {
            Move.Backward = -c;
            Move.Forward = 0;
        }
    }

    /// <summary>Moves toward a point.</summary>
    /// <param name="point">The point.</param>
    /// <remarks>Follows 0x0043d298.</remarks>
    public void MoveToward(ReadOnlySpan<float> point)
    {
        var dist = Dist3(point, Frame.Own);
        if (!(dist > 1.0f))
            return;
        var e = SteerYaw(point);
        SetMoveDirection(e, X87.F32(SpeedForDistance(dist)), 0);
    }

    /// <summary>Moves toward a point and keeps the current turn.</summary>
    /// <param name="point">The point.</param>
    /// <remarks>Follows 0x0043d198.</remarks>
    public void MoveTowardKeepTurn(ReadOnlySpan<float> point)
    {
        var dist = Dist3(point, Frame.Own);
        if (!(dist > 0.2))
            return;
        var e = YawErrorOf(point);
        e.Cur = X87.F32(Add(Move.Turn, e.Cur));
        SetMoveDirection(e, X87.F32(SpeedForDistance(dist)), 0);
    }

    // ---- trigger (3.6) ----

    /// <summary>Rolls a random chance.</summary>
    /// <param name="pct">The probability.</param>
    /// <returns>True when the roll succeeded.</returns>
    /// <remarks>Follows 0x00440c60.</remarks>
    public static bool Roll(float pct)
    {
        if (!(pct >= 0.0f))
            pct = 0.0f;
        else if (pct > 1.0f)
            pct = 1.0f;
        var u = AIMath.AiRandom().Next();
        return Mul(u, 2.3283064365386963e-10) < pct;
    }

    /// <summary>Decides whether to pull the trigger.</summary>
    /// <returns>True when the trigger is pulled.</returns>
    /// <remarks>Follows 0x00440f08.</remarks>
    public bool TriggerPull()
    {
        Trigger.Cooldown = 24;
        if (Trigger.Count < Trigger.Windup)
            return true;
        return Roll(Trigger.Pct);
    }

    // ---- weapon and aim (3.4, 3.5) ----

    // src/game/ai_object.cpp AIObject::weaponSpeed: 0x0043c5b4.
    private bool WeaponSpeed()
    {
        ProjectileSpeed = 0;
        var self = Player();
        if (self == null)
            return false;
        var slot = self.Images[0];
        int group = slot.Info.ProjGroup, index = slot.Info.ProjIndex;
        if (group == -1 || Game.Sim == null)
            return false;
        var b = Game.Sim.DataBlocks().At(group, index);
        if (b == null)
            return false;
        var speed = group switch
        {
            Projectile.kRocketGroup => Pj.Real(b, "terminalVelocity", 0.0f),
            Projectile.kBulletGroup or Projectile.kGrenadeGroup or Projectile.kSeekerGroup => Pj.Real(b, "muzzleVelocity", Pj.Real(b, "maxLevelFlightDist", 0.0f)),
            _ => 1.0e7f,
        };
        ProjectileSpeed = speed;
        return !(Mul(speed, speed) < 1e-12f);
    }

    /// <summary>Computes the aim point with the aim error.</summary>
    /// <param name="target">The target Player, or null.</param>
    /// <param name="point">The target point, or null.</param>
    /// <param name="output">Receives the aim point.</param>
    /// <remarks>Follows 0x0043c004.</remarks>
    public void AimError(Player? target, float[]? point, Span<float> output)
    {
        var p = new float[3];
        if (point != null)
            point.CopyTo(p, 0);
        else
            AIMath.TargetPoint(Game, target!, p);
        var D = new float[3];
        for (var i = 0; i < 3; ++i)
            D[i] = X87.F32(Sub(p[i], Frame.Own[i]));
        const float eps = 1e-12f;
        if (Mul(D[0], D[0]) < eps && Mul(D[1], D[1]) < eps && Mul(D[2], D[2]) < eps)
        {
            D[0] = 1.0f;
            D[1] = D[2] = 0;
        }
        else
        {
            var len = X87.F32(X87.Len(D));
            var k = X87.F32(Div(1.0f, len));
            for (var i = 0; i < 3; ++i)
                D[i] = X87.F32(Mul(k, D[i]));
        }
        float[] up = [0, 0, 1.0f];
        Span<float> side = stackalloc float[3];
        AIMath.Cross3(up, D, side);
        var dist = X87.F32(Dist3(p, Frame.Own));
        var acc = X87.F32(Sk.Accuracy(dist));
        float scale;
        if (Roll(acc))
        {
            scale = X87.F32(Sub(1.0f, Skill));
        }
        else
        {
            var @base = X87.F32(AIMath.LerpD(Skill, 1.0, 2.2));
            var u = AIMath.AiRandom().Next();
            scale = X87.F32(Add(Mul(Mul(u, 2.3283064365386963e-10), @base), @base));
        }
        var phase = X87.F32(Mul(Mul(ErrorPhase, AIMath.kTwoPiD), 0.00390625f));
        {
            var u = AIMath.AiRandom().Next();
            phase = X87.F32(Add(Add(Mul(Mul(u, 2.3283064365386963e-10), 0.6f), -0.3f), phase));
        }
        if (Roll(0.001f))
            ErrorPhase = unchecked((byte)(ErrorPhase + 0x70));
        if (dist < 22.0f)
        {
            if (!(dist > 1.7f))
            {
                scale = 0;
            }
            else
            {
                var q = X87.F32(AIMath.FractionOf(22.0f, 1.7f, dist));
                q = X87.F32(Mul(q, q));
                scale = X87.F32(AIMath.LerpF(q, scale, 0.0f));
            }
        }
        float hw, hh;
        if (point != null)
        {
            hw = X87.F32(0.6);
            hh = X87.F32(1.2);
        }
        else
        {
            hw = X87.F32(Mul(Sub(target!.BoxMax[0], target.BoxMin[0]), 0.5f));
            hh = X87.F32(Mul(Sub(target.BoxMax[2], target.BoxMin[2]), 0.5f));
        }
        var t1 = X87.F32(Mul(Mul(AIMath.CrtTrig(phase, false), hh), scale));
        Span<float> u3 = stackalloc float[3], s3 = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            u3[i] = X87.F32(Mul(t1, up[i]));
        var t2 = X87.F32(Mul(Mul(AIMath.CrtTrig(phase, true), hw), scale));
        for (var i = 0; i < 3; ++i)
            s3[i] = X87.F32(Mul(t2, side[i]));
        for (var i = 0; i < 3; ++i)
            output[i] = X87.F32(Add(u3[i], s3[i]));
    }
}
