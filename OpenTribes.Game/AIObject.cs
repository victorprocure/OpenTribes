// Port of src/game/ai_manager.hpp (AIDirective, AISkill, AIObject) and src/game/ai_object.cpp: AI players, the per-AI
// object, the skill block, directives, steering, aiming and the target bookkeeping (docs/ai-sim.md). The manager tick
// holds a Chop scope; the long double arithmetic is the Fpu double operations in C++ order, world::x87::f32 is X87.F32.
// The x87 helpers of src/core/ldouble.hpp are their portable forms: fpatan Math.Atan2, fptan Math.Tan, fldpi the double
// pi, fprem fmod with the quotient's low three bits; the 80-bit constants are the 53-bit rounded doubles. The C++
// struct copies of a directive are AIDirective.Clone().
using System.Runtime.InteropServices;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/ai_manager.hpp struct AIDirective: directive record (docs/ai-sim.md 2.1, 0x38 bytes in the original).
public sealed class AIDirective
{
    public const int kWaypoint = 1, kFollow = 2, kTarget = 4, kGuard = 8;          // Type
    public const int kPlayer = 1, kLaser = 2, kPoint = 4;                           // Sub
    public const ushort kLOS = 1, kHadLOS = 2, kDeathReported = 4, kInRange = 8;    // Flag
    public int Type;
    public int Sub;
    public readonly string?[] Callback = new string?[4]; // interned; type 4 defaults in addDirective
    public ushort Flags;
    public int Order;
    public int Client;
    public readonly float[] Point = new float[3];
    public float Radius;

    // The C++ struct copy.
    public AIDirective Clone()
    {
        var d = new AIDirective { Type = Type, Sub = Sub, Flags = Flags, Order = Order, Client = Client, Radius = Radius };
        Callback.CopyTo(d.Callback, 0);
        Point.CopyTo(d.Point, 0);
        return d;
    }
}

// src/game/ai_manager.hpp struct AISkill: the skill block at +0x114 (docs/ai-sim.md 5.2).
public sealed class AISkill
{
    public float SpotDist = -1;     // +0x00 variable `spotDist`
    public float Spot = 50;         // +0x04 derived
    public int Think = 50;          // +0x08
    public float MaxSpeed = 0.4f;   // +0x0c
    public float UnusedA = 1.3f, UnusedB = 4.0f;           // +0x10, +0x14
    public readonly float[] RateUnused = new float[3];      // +0x20
    public readonly float[] Rate = new float[3];            // +0x2c: yaw rate r, 2r, 0.1r
    public float SmartRange = 145, DumbRange = 55, SmartMinAccuracy = 0.2f; // +0x38..+0x40 variables
    public float Range, MinAccuracy, Skill;                                 // +0x44..+0x4c

    // src/game/ai_object.cpp bitsFloat.
    private static float BitsFloat(uint b) => BitConverter.UInt32BitsToSingle(b);

    // src/game/ai_object.cpp triple: 0x00440c30.
    private static void Triple(float[] t, float r)
    {
        t[0] = r;
        t[1] = X87.F32(Mul(2.0f, r));
        t[2] = X87.F32(Mul(0.1, r));
    }

    // src/game/ai_object.cpp AISkill::build: 0x00440ccc.
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

    // src/game/ai_object.cpp AISkill::applyIq: 0x00440d98.
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

    // src/game/ai_object.cpp AISkill::accuracy: 0x00440eb8.
    public float Accuracy(float dist)
    {
        float p = X87.F32(AIMath.FractionOf(Range, 0.0f, dist));
        return X87.F32(AIMath.LerpF(p, MinAccuracy, Skill));
    }

    // src/game/ai_manager.hpp AISkill::spotDistance: 0x00440eec.
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
        bool negative = double.IsNegative(r);
        double a = Math.Abs(r);
        if (a == 1)
        {
            double v1 = Div(X87Pi(), 4);
            return negative ? -v1 : v1;
        }
        if (a < 1)
        {
            double v2 = X87Fpatan(a, 1.0);
            return negative ? -v2 : v2;
        }
        double v = Sub(X87Fpatan(1.0, a), kHalfPiD);
        return negative ? v : -v;
    }

    // src/game/ai_object.cpp borlandAtan2: Borland atan2 (0x006067ac) for finite arguments.
    public static double BorlandAtan2(double a, double b)
    {
        double pi = X87Pi();
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
        double v = BorlandAtan(Div(a, b));
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
    private static readonly double kQuarter = Math.ScaleB((double)0xc90fdaa22168c235UL, -64);

    // src/core/ldouble.hpp fpremLD (portable): the remainder by `quarter`, and the low three bits of the quotient.
    private static double FpremLD(double ax, double quarter, out uint octant)
    {
        double rem = ax % quarter; // std::fmod (exact)
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
        bool negative = double.IsNegative(x);
        double quarter = kQuarter;
        double ax = Math.Abs(x);
        double rem = FpremLD(ax, quarter, out uint n); // n: the octant, quotient mod 8
        uint sign = negative ? 1u : 0u;
        if (cosine)
        {
            n = (n + 2) & 7;
            sign = 0;
        }
        double r = rem;
        if ((n & 1) != 0)
            r = Sub(quarter, r);
        double t = X87Fptan(r);
        double root = Sqrt(Add(1.0, Mul(t, t)));
        if ((((n >> 2) & 1) ^ sign) != 0)
            root = -root;
        bool tangentOverRoot = (n & 3) == 0 || (n & 3) == 3;
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
        ClientRecord? r = g.Players.Find(clientId);
        SimManager? m = g.Sim?.Server();
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

// src/game/ai_manager.hpp class AIObject.
public sealed partial class AIObject : SimObject
{
    public readonly GameState Game;
    // ---- fields (offsets of the original in docs/ai-sim.md 1.3) ----
    public uint Flags;  // 1 death handled, 2 display name differs, 4 laser aim, 8 variables registered
    public bool Active;
    public byte ErrorPhase;
    public ushort ThinkCounter;
    public bool HomeCaptured;
    public string? CbDied;
    public string? CbBored;
    public string? CbPeriodic;
    public float ProjectileSpeed;
    public float Skill, Iq;
    public bool WaypointsDone;
    public int WaypointOrder;
    public int Direction = 1;
    public int PathType;
    public int LastLosClient;
    public int LosCounter = 5, LosPeriod = 5;
    public readonly float[] SeekOff = { 3, 3, 0 };
    public readonly float[] PreviousPoint = new float[3];
    public int AttackMode;
    public int TargetId = -1;       // +0xb4
    public int CurrentTarget = -1;  // +0xb8 as an index into directives, -1 none
    public readonly byte[] AutoBits = new byte[18];
    public int PeriodicCounter, PeriodicPeriod;
    public int Team = -1;
    public Move Move = new();
    public int State;

    // src/game/ai_manager.hpp AIObject::Trigger.
    public sealed class TriggerState
    {
        public int Count, Cooldown;
        public float Pct = 0.17f;
        public int Windup = 6;
    }
    public readonly TriggerState Trigger = new();
    public readonly AISkill Sk = new();
    public bool ScriptedTargets = true; // +0x164
    public int AttackId;                // +0x198
    public int RepId;
    public bool RecordTaken;
    public string AiName = "";  // name (+0x1a0, 31 characters at most); SimObject.Name() is the object name
    public string Voice = "";
    public string Result = "Done";
    public int PlayerId;
    public Player? PlayerPtr;
    public List<AIDirective> Directives = new();

    // src/game/ai_manager.hpp AIObject::Frame: the per-tick frame (docs/ai-sim.md 1.6).
    public sealed class FrameState
    {
        public bool HasMoveTarget;
        public readonly float[] MoveTarget = { -1e6f, -1e6f, -1e6f };
        public readonly float[] Own = new float[3];
    }
    public FrameState Frame = new();

    // src/game/ai_manager.hpp AIObject::YawError.
    public struct YawError
    {
        public float Abs, Desired, Cur, Diff;
    }

    // src/game/ai_object.cpp AIObject::AIObject.
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

    public override string ClassName() => "AI::Object";
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;

    // src/game/ai_object.cpp AIObject::~AIObject.
    public override void Destroy() => Game.Ai?.Remove(this);

    // src/game/ai_object.cpp aiTargetPoint: Player_getTargetPoint (0x004125a0): the centre of the Player's container box.
    public static void AiTargetPoint(GameState g, Player p, Span<float> output)
    {
        Span<float> mn = stackalloc float[3], mx = stackalloc float[3];
        if (!Pj.WorldBox(g, p, mn, mx))
        {
            for (int i = 0; i < 3; ++i)
                output[i] = p.Transform.P[i];
            return;
        }
        for (int i = 0; i < 3; ++i)
        {
            float s = X87.F32(Add(mx[i], mn[i]));
            output[i] = X87.F32(Mul(s, 0.5f));
        }
    }

    // src/game/ai_object.cpp AIObject::player: null once the Player is gone.
    public Player? Player()
    {
        SimManager? m = Game.Sim?.Server();
        if (m == null || PlayerPtr == null)
            return null;
        SimObject? o = m.FindById(PlayerId);
        return o != null && o == PlayerPtr ? PlayerPtr : null;
    }

    // src/game/ai_manager.hpp AIObject::valid.
    public bool Valid() => Active && Player() != null;

    // src/game/ai_object.cpp AIObject::onAdd: 0x0043db28.
    public override bool OnAdd()
    {
        Player? p = Player();
        if (p == null)
            return false;
        RepId = Game.AllocateRecordId != null ? Game.AllocateRecordId() : Game.Players.NextFreeId();
        string playerName = p.MapName ?? "";
        bool male = Voice.Length >= 4 && GameUtil.EqualsNoCase(Voice[..4], "male");
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

    // src/game/ai_object.cpp AIObject::onRemove: 0x0043dbd8.
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

    // src/game/ai_object.cpp AIObject::matches: 0x0043ffbc.
    public bool Matches(string pattern)
    {
        if (ResourceManager.WildcardMatch(pattern, AiName))
            return true;
        if ((Flags & 2) != 0)
        {
            Player? p = Player();
            return p != null && p.MapName != null && ResourceManager.WildcardMatch(pattern, p.MapName);
        }
        return false;
    }

    // ---- variables (5.1) ----

    // src/game/ai_object.cpp VarType, VarDef, kVars.
    private enum VarType { FloatG, Int, Point, FloatF }
    private readonly record struct VarDef(string Name, VarType Type, bool Recompute);
    private static readonly VarDef[] kVars =
    {
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
    };

    // src/game/ai_manager.hpp AIObject::varName.
    public string VarName(string var) => "$AI::" + AiName + "::" + var;

    // src/game/ai_object.cpp variableChanged: the function-static flag: the stored text is not parsed again.
    private static bool sCanonicalizing;

    // src/game/ai_object.cpp AIObject::variableChanged.
    public void VariableChanged(string var, string value, bool notify = true)
    {
        VarType type = VarType.Int;
        int which; // the field: the C++ float* / int32_t*
        bool eq(string n) => GameUtil.EqualsNoCase(var, n);
        if (eq("iq")) { which = 0; type = VarType.FloatG; }
        else if (eq("pathType")) { which = 1; }
        else if (eq("spotDist")) { which = 2; type = VarType.FloatG; }
        else if (eq("seekOff")) { which = 3; type = VarType.Point; }
        else if (eq("attackMode")) { which = 4; }
        else if (eq("triggerWindup")) { which = 5; }
        else if (eq("triggerPct")) { which = 6; type = VarType.FloatF; }
        else if (eq("smartGuyWeaponRange")) { which = 7; type = VarType.FloatG; }
        else if (eq("dumbGuyWeaponRange")) { which = 8; type = VarType.FloatG; }
        else if (eq("smartGuyMinAccuracy")) { which = 9; type = VarType.FloatG; }
        else return;
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
            int v = Numbers.BorlandAtoi(value);
            switch (which)
            {
                case 1: PathType = v; break;
                case 4: AttackMode = v; break;
                default: Trigger.Windup = v; break;
            }
            canon = Numbers.FormatInt(v);
        }
        else
        {
            float f = X87.F32(Numbers.BorlandAtof(value));
            if (type == VarType.FloatF)
            { // the console's percent type (0x005f52bf) clamps to 0..1
                if (f < 0.0f)
                    f = 0.0f;
                else if (f > 1.0f)
                    f = 1.0f;
            }
            switch (which)
            {
                case 0: Iq = f; break;
                case 2: Sk.SpotDist = f; break;
                case 6: Trigger.Pct = f; break;
                case 7: Sk.SmartRange = f; break;
                case 8: Sk.DumbRange = f; break;
                default: Sk.SmartMinAccuracy = f; break;
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

    // src/game/ai_object.cpp AIObject::registerVariables: 0x0043ad04.
    public void RegisterVariables()
    {
        if ((Flags & 8) != 0 || Game.Console == null)
            return;
        Interpreter inr = Game.Console;
        GameState g = Game;
        string aiName = AiName;
        foreach (VarDef d in kVars)
        {
            string full = VarName(d.Name);
            string var = d.Name;
            void Bind() =>
                inr.BindVariable(full, v =>
                {
                    AIManager? mgr = g.Ai;
                    AIObject? ai = mgr?.FindByName(aiName);
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
            string s = "";
            string n = d.Name;
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

    // src/game/ai_object.cpp AIObject::removeVariables: 0x0043af04.
    public void RemoveVariables()
    {
        if ((Flags & 8) == 0 || Game.Console == null)
            return;
        foreach (VarDef d in kVars)
            Game.Console.EraseGlobal(VarName(d.Name));
        Flags &= ~8u;
    }

    // 0x0043b104 holds 0.01 as an 80-bit constant, which is below 0.01. Rounded toward zero to 53 bits, not to the
    // nearest double (above 0.01): with the nearest double f32(100 x 0.01) is 1, where the original gets 0.99999994 and
    // think period 4 (tests/test_ai.cpp testBehavior iq 100; aidiff motion c_rates, d_follow, target e_target_iq100).
    private static readonly double kHundredth = Math.ScaleB((double)(0xa3d70a3d70a3d70aUL & ~0x7ffUL), -70);

    // src/game/ai_object.cpp AIObject::recomputeSkill: 0x0043b0ac.
    public void RecomputeSkill()
    {
        double v = Mul(kHundredth, Iq);
        float s = X87.F32(v);
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
        for (int i = 0; i < 4; ++i)
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
        for (int i = 0; i < Directives.Count; ++i)
        {
            AIDirective d = Directives[i];
            if ((d.Type & mask) == 0)
                continue;
            if (order == d.Order)
                return i;
            if (upper && order < d.Order)
                return i;
        }
        return -1;
    }

    // src/game/ai_object.cpp AIObject::addDirective: 0x00440420, order < 1: assigned. `d` is taken by value.
    public bool AddDirective(AIDirective d, int order, bool sort = true)
    {
        d = d.Clone();
        DefaultCallbacks(d);
        if (order >= 1)
        {
            d.Order = order;
            int at = FindDirective(order, 0xffff, false);
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
                    int i = Directives.Count - 1;
                    while (i >= 0 && unchecked(Directives[i].Order + 150) >= 0x2eb2c000)
                        --i;
                    v = i < 0 ? 100 : unchecked(Directives[i].Order + 150);
                }
                v = v - v % 100;
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

    // src/game/ai_object.cpp AIObject::dist3: 0x0043a9f0 (a - b).
    public float Dist3(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> d = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(a[i], b[i]));
        if ((Flags & 4) != 0)
            return X87.F32(X87.Len(d));
        return X87.Lenf(d);
    }

    // src/game/ai_object.cpp AIObject::dist2: 0x0043a924.
    public float Dist2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dx = X87.F32(Sub(a[0], b[0])), dy = X87.F32(Sub(a[1], b[1]));
        float s = X87.F32(Add(Mul(dx, dx), Mul(dy, dy)));
        return X87.F32(Sqrt(s));
    }

    // ---- callbacks (4) ----

    // src/game/ai_object.cpp AIObject::callback: 0x00440a28.
    public void Callback(AIDirective? d, int slot)
    {
        string? fn = d != null ? d.Callback[slot] : (slot == 0 ? CbDied : slot == 1 ? CbBored : CbPeriodic);
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
        Player? p = AIMath.PlayerOfRep(Game, d.Client);
        if (p != null)
        {
            Span<float> tp = stackalloc float[3];
            AIMath.TargetPoint(Game, p, tp);
            for (int i = 0; i < 3; ++i)
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
        int cur = FindDirective(order, mask, false);
        int result = -1;
        int count = Directives.Count;
        if (PathType == 2)
        {
            var list = new List<int>();
            for (int i = 0; i < count; ++i)
                if ((Directives[i].Type & mask) != 0)
                    list.Add(i);
            int n = list.Count;
            int pos = 0;
            while (pos < n && Directives[list[pos]].Order != order)
                ++pos;
            bool found = n > pos;
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
                    for (int i = 0; i < n - 1; ++i)
                    {
                        AIDirective lo = Directives[list[i]];
                        AIDirective hi = Directives[list[i + 1]];
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
            int idx = cur;
            for (int left = count - 1; left > 0; --left)
            {
                int next = (idx + 1) % count;
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
        Player? p = AIMath.PlayerOfRep(Game, d.Client);
        if (p == null || p.Dead)
            return false;
        float spot = Sk.SpotDistance();
        Span<float> tp = stackalloc float[3];
        AIMath.TargetPoint(Game, p, tp);
        Span<float> diff = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(Frame.Own[i], tp[i]));
        return X87.Lenf(diff) < spot;
    }

    // src/game/ai_object.cpp AIObject::scanTargets: 0x0043b66c.
    private void ScanTargets()
    {
        var list = new List<int>();
        for (int i = 0; i < Directives.Count; ++i)
            if ((Directives[i].Type & AIDirective.kTarget) != 0)
                list.Add(i);
        int n = list.Count;
        if (n == 0)
            return;
        foreach (int i in list)
        {
            AIDirective d = Directives[i];
            if (TargetInRange(d))
                d.Flags |= AIDirective.kInRange;
            else
                d.Flags &= unchecked((ushort)~(AIDirective.kLOS | AIDirective.kHadLOS | AIDirective.kInRange));
        }
        if (++LosCounter >= LosPeriod)
            LosCounter = 0;
        if (LosCounter == 0)
        {
            int idx = -1;
            if (LastLosClient != 0)
                for (int i = 0; i < n; ++i)
                    if (Directives[list[i]].Client == LastLosClient)
                    {
                        idx = i;
                        break;
                    }
            int start = (idx + 1) % n;
            LastLosClient = 0;
            int k = start;
            do
            {
                AIDirective d = Directives[list[k]];
                if ((d.Flags & AIDirective.kInRange) != 0)
                {
                    if (d.Sub == AIDirective.kPlayer)
                    {
                        Player? tp = AIMath.PlayerOfRep(Game, d.Client);
                        Player? self = Player();
                        bool seen = tp != null && self != null && Los(self, tp);
                        if (seen)
                        {
                            if ((d.Flags & AIDirective.kLOS) == 0)
                            {
                                AIDirective copy = d.Clone();
                                Callback(copy, (d.Flags & AIDirective.kHadLOS) != 0 ? 3 : 0);
                            }
                            if (list[k] < Directives.Count)
                                Directives[list[k]].Flags |= AIDirective.kLOS | AIDirective.kHadLOS;
                        }
                        else if ((d.Flags & AIDirective.kLOS) != 0)
                        {
                            d.Flags &= unchecked((ushort)~AIDirective.kLOS);
                            AIDirective copy = d.Clone();
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
        float best = 3.0e14f; // 0x578edcf4
        for (int k = 0; k < n; ++k)
        {
            if (list[k] >= Directives.Count)
                break;
            AIDirective d = Directives[list[k]];
            if (d.Sub != AIDirective.kPlayer)
            {
                CurrentTarget = list[k];
                continue;
            }
            Player? tp = AIMath.PlayerOfRep(Game, d.Client);
            if (tp != null)
            {
                if (tp.Dead)
                {
                    if ((d.Flags & AIDirective.kDeathReported) == 0)
                    {
                        AIDirective copy = d.Clone();
                        Callback(copy, 1);
                        if (list[k] < Directives.Count)
                            Directives[list[k]].Flags |= AIDirective.kDeathReported;
                    }
                }
                else if ((d.Flags & AIDirective.kLOS) != 0)
                {
                    float[] p = new float[3];
                    AIMath.TargetPoint(Game, tp, p);
                    float[] diff = new float[3];
                    for (int i = 0; i < 3; ++i)
                        diff[i] = X87.F32(Sub(Frame.Own[i], p[i]));
                    float len = X87.Lenf(diff);
                    if (CurrentTarget < 0 || len < best)
                    {
                        CurrentTarget = list[k];
                        best = len;
                    }
                }
            }
            else if ((d.Flags & AIDirective.kDeathReported) == 0)
            {
                AIDirective copy = d.Clone();
                Callback(copy, 1);
                if (list[k] < Directives.Count)
                    Directives[list[k]].Flags |= AIDirective.kDeathReported;
            }
        }
    }

    // src/game/ai_object.cpp AIObject::sense: 0x0043bcb4.
    public void Sense()
    {
        Player? self = Player();
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
        int guard = Directives.Count;
        for (;;)
        {
            if (guard-- == 0)
                break;
            int di = FindDirective(WaypointOrder, 3, false);
            if (di < 0)
                di = NextWaypoint(WaypointOrder);
            if (di < 0)
                break;
            AIDirective d = Directives[di].Clone();
            float[] p = new float[3];
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

    // src/game/ai_object.cpp AIObject::refreshAutoTargets: 0x00440118.
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
        foreach (AIDirective d in Directives)
        {
            if (d.Type == AIDirective.kTarget && ((uint)d.Order & 0x7fff8000u) == kAutoBase)
            {
                ClientRecord? r = Game.Players.Find(d.Client);
                if (r != null && Enemy(r))
                {
                    kept.Add(d);
                }
                else
                {
                    int bit = d.Client - 2048;
                    if (bit >= 0 && bit < 144)
                        AutoBits[bit >> 3] &= (byte)~(1u << (bit & 7));
                }
                continue;
            }
            kept.Add(d);
        }
        Directives = kept;
        bool added = false;
        foreach (int id in Game.Players.PlayerIds())
        {
            ClientRecord? r = Game.Players.Find(id);
            if (r == null || !Enemy(r))
                continue;
            int bit = r.Id - 2048;
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

    // src/game/ai_object.cpp AIObject::setAutomaticTargets: 0x00440348.
    public void SetAutomaticTargets()
    {
        if (ScriptedTargets)
        {
            ScriptedTargets = false;
            Array.Clear(AutoBits);
        }
    }

    // src/game/ai_object.cpp AIObject::setScriptedTargets: 0x00440370.
    public void SetScriptedTargets()
    {
        if (!ScriptedTargets)
        {
            ScriptedTargets = true;
            var kept = new List<AIDirective>();
            foreach (AIDirective d in Directives)
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
        Player? p = AIMath.PlayerOfRep(Game, AttackId);
        if (p == null || p.Dead)
            return null;
        Span<float> tp = stackalloc float[3];
        AIMath.TargetPoint(Game, p, tp);
        float d = Dist3(Frame.Own, tp);
        if (!(Sk.SpotDistance() > d))
            return null;
        Player? self = Player();
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
        Player? p = AttackTargetVisible(true);
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
            AIDirective d = Directives[CurrentTarget].Clone();
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

    // src/game/ai_object.cpp AIObject::act: 0x0043bf3c.
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
            Move[] moves = { Move }; // applyMoves(&move, 1)
            pl.ApplyMoves(moves, 1);
            Move = moves[0];
        }
    }

    // ---- steering (3.3) ----

    // src/game/ai_object.cpp AIObject::yawError: 0x0043c6e4.
    public YawError YawErrorOf(ReadOnlySpan<float> point)
    {
        Player self = Player()!;
        float dx = X87.F32(Sub(point[0], Frame.Own[0]));
        float dy = X87.F32(Sub(point[1], Frame.Own[1]));
        double t = AIMath.Atan2Safe(dy, dx);
        float desired0 = X87.F32(Sub(t, AIMath.kHalfPiD));
        float desired = AIMath.WrapAngle(0.0f, desired0);
        float cur = AIMath.WrapAngle(AIMath.kTwoPiF, self.Rotation[2]);
        YawError e;
        float diff;
        float abs;
        for (;;)
        {
            diff = X87.F32(Sub(desired, cur));
            double d = Sub(desired, cur);
            double a = Math.Abs(d);
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
        float v = rates[0];
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

    // src/game/ai_object.cpp AIObject::steerYaw: 0x0043d228.
    public YawError SteerYaw(ReadOnlySpan<float> point)
    {
        YawError e = YawErrorOf(point);
        float v = RateStep(Sk.Rate, e.Abs);
        Move.Turn = e.Diff >= 0.0f ? v : -v;
        return e;
    }

    // src/game/ai_object.cpp AIObject::steerPitch: 0x0043c7bc.
    public YawError SteerPitch(ReadOnlySpan<float> point)
    {
        Player self = Player()!;
        float d2 = Dist2(point, Frame.Own);
        float dz = X87.F32(Sub(point[2], Frame.Own[2]));
        float desired = X87.F32(AIMath.Atan2Safe(dz, d2));
        float cur = self.Pitch;
        float diff = X87.F32(Sub(desired, cur));
        float abs = Math.Abs(diff);
        float v = 0.084f;
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
    private static readonly double kTwoThirds = Math.ScaleB((double)0xaaaaaaaaaaaaaaabUL, -64);
    private static readonly double kOneThird = Math.ScaleB((double)0xaaaaaaaaaaaaaaabUL, -65);

    // src/game/ai_object.cpp AIObject::speedForDistance: 0x0043d0e4.
    public float SpeedForDistance(float dist)
    {
        if (!(dist < 1.7))
            return Sk.MaxSpeed;
        if (!(dist > 0.2))
            return 0.0f;
        float t = X87.F32(Mul(Sub(dist, 0.2), kTwoThirds));
        float lo = X87.F32(Mul(kOneThird, Sk.MaxSpeed));
        if (lo > 0.25f)
            lo = 0.25f;
        return X87.F32(AIMath.LerpF(t, Sk.MaxSpeed, lo));
    }

    // src/game/ai_object.cpp AIObject::setMoveDirection: 0x0043c4b4 (`adjust` unused).
    public void SetMoveDirection(in YawError e, float speed, float adjust)
    {
        _ = adjust;
        float ang = X87.F32(Sub(e.Desired, e.Cur));
        float s = X87.F32(Mul(AIMath.CrtTrig(ang, false), speed));
        float c = X87.F32(Mul(AIMath.CrtTrig(ang, true), speed));
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

    // src/game/ai_object.cpp AIObject::moveToward: 0x0043d298.
    public void MoveToward(ReadOnlySpan<float> point)
    {
        float dist = Dist3(point, Frame.Own);
        if (!(dist > 1.0f))
            return;
        YawError e = SteerYaw(point);
        SetMoveDirection(e, X87.F32(SpeedForDistance(dist)), 0);
    }

    // src/game/ai_object.cpp AIObject::moveTowardKeepTurn: 0x0043d198.
    public void MoveTowardKeepTurn(ReadOnlySpan<float> point)
    {
        float dist = Dist3(point, Frame.Own);
        if (!(dist > 0.2))
            return;
        YawError e = YawErrorOf(point);
        e.Cur = X87.F32(Add(Move.Turn, e.Cur));
        SetMoveDirection(e, X87.F32(SpeedForDistance(dist)), 0);
    }

    // ---- trigger (3.6) ----

    // src/game/ai_object.cpp AIObject::roll: 0x00440c60.
    public bool Roll(float pct)
    {
        if (!(pct >= 0.0f))
            pct = 0.0f;
        else if (pct > 1.0f)
            pct = 1.0f;
        uint u = AIMath.AiRandom().Next();
        return Mul(u, 2.3283064365386963e-10) < pct;
    }

    // src/game/ai_object.cpp AIObject::triggerPull: 0x00440f08.
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
        Player? self = Player();
        if (self == null)
            return false;
        Player.ImageSlot slot = self.Images[0];
        int group = slot.Info.ProjGroup, index = slot.Info.ProjIndex;
        if (group == -1 || Game.Sim == null)
            return false;
        DataBlockManager.Block? b = Game.Sim.DataBlocks().At(group, index);
        if (b == null)
            return false;
        float speed;
        switch (group)
        {
            case Projectile.kRocketGroup:
                speed = Pj.Real(b, "terminalVelocity", 0.0f);
                break;
            case Projectile.kBulletGroup:
            case Projectile.kGrenadeGroup: // +0x68 of a GrenadeData is maxLevelFlightDist
            case Projectile.kSeekerGroup:
                speed = Pj.Real(b, "muzzleVelocity", Pj.Real(b, "maxLevelFlightDist", 0.0f));
                break;
            default:
                speed = 1.0e7f;
                break;
        }
        ProjectileSpeed = speed;
        return !(Mul(speed, speed) < 1e-12f);
    }

    // src/game/ai_object.cpp AIObject::aimError: 0x0043c004.
    public void AimError(Player? target, float[]? point, Span<float> output)
    {
        float[] p = new float[3];
        if (point != null)
            point.CopyTo(p, 0);
        else
            AIMath.TargetPoint(Game, target!, p);
        float[] D = new float[3];
        for (int i = 0; i < 3; ++i)
            D[i] = X87.F32(Sub(p[i], Frame.Own[i]));
        const float eps = 1e-12f;
        if (Mul(D[0], D[0]) < eps && Mul(D[1], D[1]) < eps && Mul(D[2], D[2]) < eps)
        {
            D[0] = 1.0f;
            D[1] = D[2] = 0;
        }
        else
        {
            float len = X87.F32(X87.Len(D));
            float k = X87.F32(Div(1.0f, len));
            for (int i = 0; i < 3; ++i)
                D[i] = X87.F32(Mul(k, D[i]));
        }
        float[] up = { 0, 0, 1.0f };
        Span<float> side = stackalloc float[3];
        AIMath.Cross3(up, D, side);
        float dist = X87.F32(Dist3(p, Frame.Own));
        float acc = X87.F32(Sk.Accuracy(dist));
        float scale;
        if (Roll(acc))
        {
            scale = X87.F32(Sub(1.0f, Skill));
        }
        else
        {
            float @base = X87.F32(AIMath.LerpD(Skill, 1.0, 2.2));
            uint u = AIMath.AiRandom().Next();
            scale = X87.F32(Add(Mul(Mul(u, 2.3283064365386963e-10), @base), @base));
        }
        float phase = X87.F32(Mul(Mul(ErrorPhase, AIMath.kTwoPiD), 0.00390625f));
        {
            uint u = AIMath.AiRandom().Next();
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
                float q = X87.F32(AIMath.FractionOf(22.0f, 1.7f, dist));
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
        float t1 = X87.F32(Mul(Mul(AIMath.CrtTrig(phase, false), hh), scale));
        Span<float> u3 = stackalloc float[3], s3 = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            u3[i] = X87.F32(Mul(t1, up[i]));
        float t2 = X87.F32(Mul(Mul(AIMath.CrtTrig(phase, true), hw), scale));
        for (int i = 0; i < 3; ++i)
            s3[i] = X87.F32(Mul(t2, side[i]));
        for (int i = 0; i < 3; ++i)
            output[i] = X87.F32(Add(u3[i], s3[i]));
    }
}
