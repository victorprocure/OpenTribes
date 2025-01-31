// Port of src/game/damage.cpp: damage (docs/projectile-sim.md 3.1-3.3, 4; docs/player-sim.md 6.4): the ShapeBase onDamage
// call, coverage, radius damage and the GameBase damage commands. Player::damageCoverage (0x00411538) is stage P4b.
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public partial class GameBase
{
    // src/game/damage.cpp GameBase::applyDamage: vtable +0x198, ShapeBase_applyDamage (0x0044bc80): the script decides
    // what the damage does.
    public virtual void ApplyDamage(int type, float value, ReadOnlySpan<float> pos, ReadOnlySpan<float> vec,
                                    ReadOnlySpan<float> mom, int source)
    {
        if (mCtx.Game == null)
            return;
        mCtx.Game.ScriptCallback(this, "onDamage", new[]
        {
            Numbers.FormatInt(type), DamageCommands.Literal(GameUtil.G(value)), GameUtil.Fmt3(pos), GameUtil.Fmt3(vec), GameUtil.Fmt3(mom),
            Numbers.FormatInt(source),
        });
    }

    // src/game/damage.cpp GameBase::damageCoverage: vtable +0x180, ShapeBase coverage (0x0044e324): the ray from the
    // explosion to the box centre reaches the object itself, or nothing.
    public virtual float DamageCoverage(ReadOnlySpan<float> pos)
    {
        Container? c = mCtx.Game?.GetContainer();
        Container.Entry? e = c?.Find(this);
        if (e == null)
            return 1.0f;
        using var chop = X87.Chop();
        var centre = new Vec3(X87.F32(Mul(0.5, X87.F32(Add(e.Box[3], e.Box[0])))), X87.F32(Mul(0.5, X87.F32(Add(e.Box[4], e.Box[1])))),
                              X87.F32(Mul(0.5, X87.F32(Add(e.Box[5], e.Box[2])))));
        object? hit = null;
        Vec3 h = default, n = default;
        if (!c!.CastRay(new Vec3(pos[0], pos[1], pos[2]), centre, 0xffffffffu, null, ref hit, ref h, ref n))
            return 1.0f;
        return hit == this ? 1.0f : 0.0f;
    }
}

public static class DamageCommands
{
    // src/game/damage.cpp literal: the onDamage call is a formatted script line (format 0x0061a537, evaluated by
    // 0x005f4280). The value is an unquoted %g field: the script lexes it as a literal and passes its string form on, so
    // it goes through a second parse and %g (docs/projectile-sim.md 3.1, PQ11). The vectors are quoted strings and arrive
    // unchanged.
    public static string Literal(string s)
    {
        int i = 0;
        bool neg = s.Length != 0 && s[0] == '-';
        if (neg)
            i = 1;
        int digits = 0;
        bool isFloat = false;
        for (int k = i; k < s.Length; ++k)
        {
            char c = s[k];
            if (c >= '0' && c <= '9')
                ++digits;
            else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                isFloat = true;
            else
                return s; // INF, NAN: not a literal
        }
        if (digits == 0)
            return s;
        string body = s[i..];
        if (!isFloat && !neg)
            return Numbers.FormatInt(Numbers.BorlandAtoi(body));
        // Unary minus is a float node whatever its operand is.
        double v = isFloat ? Numbers.BorlandAtof(body) : Numbers.BorlandAtoi(body);
        return Numbers.FormatFloat(neg ? -v : v);
    }

    // src/game/damage.cpp applyRadiusDamage: radius damage (3.2, 0x0046edd0).
    public static void ApplyRadiusDamage(GameState game, int type, ReadOnlySpan<float> pos, ReadOnlySpan<float> vel, float radius,
                                         float damage, float kick, int source)
    {
        SimManager? mgr = game.Sim?.Server();
        if (mgr == null)
            return;
        using var chop = X87.Chop();
        Span<float> box = stackalloc float[6];
        for (int i = 0; i < 3; ++i)
        {
            box[i] = X87.F32(Sub(pos[i], radius));
            box[i + 3] = X87.F32(Add(pos[i], radius));
        }
        List<object?> found = game.World.FindObjects(box, 0x24c00080u, null);
        var ids = new List<int>();
        foreach (object? o in found)
            if (o is SimObject so)
                ids.Add(so.Id());
        Span<float> c = stackalloc float[3], d = stackalloc float[3], v = stackalloc float[3], mom = stackalloc float[3];
        foreach (int oid in ids)
        {
            // Scripts may delete objects between two damage calls.
            GameBase? gb = mgr.FindById(oid) as GameBase;
            Container.Entry? e = gb != null ? game.World.Find(gb) : null;
            if (e == null)
                continue;
            for (int i = 0; i < 3; ++i)
            {
                c[i] = X87.F32(Mul(0.5, X87.F32(Add(e.Box[i + 3], e.Box[i]))));
                d[i] = X87.F32(Sub(c[i], pos[i]));
            }
            float dist = X87.F32(X87.Len(d));
            float ext = X87.F32(Sub(e.Box[3], e.Box[0]));
            for (int i = 1; i < 3; ++i)
            {
                float w = X87.F32(Sub(e.Box[i + 3], e.Box[i]));
                if (w < ext)
                    ext = w;
            }
            dist = X87.F32(Sub(dist, ext));
            if (!(dist > 1.0f))
                dist = 1.0f;
            if (dist >= radius)
                continue;
            float inv = X87.F32(Div(1.0, dist));
            if (Add(Add(vel[0], vel[1]), vel[2]) == 0.0)
            {
                for (int i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(d[i], inv));
            }
            else
            {
                for (int i = 0; i < 3; ++i)
                    v[i] = vel[i];
            }
            float fall = X87.F32(Div(Sub(radius, dist), radius));
            float value = X87.F32(Mul(damage, fall));
            float k = X87.F32(Mul(Mul(kick, fall), inv));
            for (int i = 0; i < 3; ++i)
                mom[i] = X87.F32(Mul(k, d[i]));
            float cover = gb!.DamageCoverage(pos);
            if (mgr.FindById(oid) != gb)
                continue;
            value = X87.F32(Mul(cover, value));
            for (int i = 0; i < 3; ++i)
                mom[i] = X87.F32(Mul(cover, mom[i]));
            if (cover != 0.0f)
                gb.ApplyDamage(type, value, pos, v, mom, source);
        }
    }

    // src/game/damage.cpp registerDamageCommands.
    public static void RegisterCommands(Interpreter interp, GameState game)
    {
        // 0x0042027c: pos, vec and mom are scanned with %f; the call goes to the object's own vtable +0x198 so a Player
        // adds its hit location.
        interp.AddCommand("GameBase::applyDamage", (inr, a) =>
        {
            if (a.Length != 8)
            {
                inr.Print(1, "wrong argcount to applyDamage(object,type,value,pos,vec,mom,sourceObj)");
                return "False";
            }
            if (game.Sim?.Server()?.Find(a[1]) is not GameBase gb || !gb.IsShapeBase())
                return "False";
            float[] v = new float[9];
            int n = CScan.Floats(a[4], v.AsSpan(0, 3));
            n += CScan.Floats(a[5], v.AsSpan(3, 3));
            n += CScan.Floats(a[6], v.AsSpan(6, 3));
            if (n != 9)
                return "False";
            gb.ApplyDamage(Numbers.BorlandAtoi(a[2]), Store(Numbers.BorlandAtof(a[3])), v.AsSpan(0, 3), v.AsSpan(3, 3),
                           v.AsSpan(6, 3), Numbers.BorlandAtoi(a[7]));
            return "True";
        });
        // 0x0041ccec.
        interp.AddCommand("GameBase::applyRadiusDamage", (inr, a) =>
        {
            if (a.Length != 7)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(type,pos,radius,damageValue,force,srcObjectId)");
                return "false";
            }
            if (game.Sim?.Server() == null)
                return "false";
            float[] pos = new float[3];
            GameUtil.Scan3(a[2], pos);
            float[] zero = { 0, 0, 0 };
            ApplyRadiusDamage(game, Numbers.BorlandAtoi(a[1]), pos, zero, Store(Numbers.BorlandAtof(a[3])),
                              Store(Numbers.BorlandAtof(a[4])), Store(Numbers.BorlandAtof(a[5])), Numbers.BorlandAtoi(a[6]));
            return "true";
        });
    }
}
