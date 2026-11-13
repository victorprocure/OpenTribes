// Port of src/game/damage.cpp: damage (docs/projectile-sim.md 3.1-3.3, 4; docs/player-sim.md 6.4): the ShapeBase onDamage
// call, coverage, radius damage and the GameBase damage commands. Player::damageCoverage (0x00411538) is in Player.cs.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public partial class GameBase
{
    /// <summary>Applies damage to the object by calling its script onDamage handler.</summary>
    /// <param name="type">The damage type.</param>
    /// <param name="value">The damage amount.</param>
    /// <param name="pos">The hit position.</param>
    /// <param name="vec">The hit direction.</param>
    /// <param name="mom">The momentum.</param>
    /// <param name="source">The id of the damage source.</param>
    /// <remarks>Follows ShapeBase_applyDamage (0x0044bc80, vtable +0x198): the script decides what the damage does.</remarks>
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

    /// <summary>Returns how much of the object a blast at a position reaches.</summary>
    /// <param name="pos">The blast position.</param>
    /// <returns>The coverage factor.</returns>
    /// <remarks>Follows the ShapeBase coverage (0x0044e324, vtable +0x180): the ray from the explosion to the box centre reaches the object itself, or nothing.</remarks>
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

/// <summary>The GameBase damage commands and radius damage.</summary>
public static class DamageCommands
{
    /// <summary>Formats a value as the script literal the onDamage call passes.</summary>
    /// <param name="s">The formatted value text.</param>
    /// <returns>The text as the engine's second parse and <c>%g</c> pass leaves it.</returns>
    /// <remarks>The onDamage call is a formatted script line (format 0x0061a537, evaluated by 0x005f4280). The value is an unquoted <c>%g</c> field that the script lexes as a literal and passes on as its string form. The vectors are quoted strings and arrive unchanged (docs/projectile-sim.md 3.1, PQ11).</remarks>
    public static string Literal(string s)
    {
        var i = 0;
        var neg = s.Length != 0 && s[0] == '-';
        if (neg)
            i = 1;
        var digits = 0;
        var isFloat = false;
        for (var k = i; k < s.Length; ++k)
        {
            var c = s[k];
            if (c is >= '0' and <= '9')
                ++digits;
            else if (c is '.' or 'e' or 'E' or '+' or '-')
                isFloat = true;
            else
                return s; // INF, NAN: not a literal
        }
        if (digits == 0)
            return s;
        var body = s[i..];
        if (!isFloat && !neg)
            return Numbers.FormatInt(Numbers.BorlandAtoi(body));
        // Unary minus is a float node whatever its operand is.
        var v = isFloat ? Numbers.BorlandAtof(body) : Numbers.BorlandAtoi(body);
        return Numbers.FormatFloat(neg ? -v : v);
    }

    /// <summary>Applies radius damage around a point to the objects in range.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="type">The damage type.</param>
    /// <param name="pos">The blast centre.</param>
    /// <param name="vel">The blast velocity.</param>
    /// <param name="radius">The blast radius.</param>
    /// <param name="damage">The damage at the centre.</param>
    /// <param name="kick">The impulse scale.</param>
    /// <param name="source">The id of the damage source.</param>
    /// <remarks>Follows the radius damage routine (0x0046edd0, docs/projectile-sim.md 3.2).</remarks>
    public static void ApplyRadiusDamage(GameState game, int type, ReadOnlySpan<float> pos, ReadOnlySpan<float> vel, float radius,
                                         float damage, float kick, int source)
    {
        SimManager? mgr = game.Sim?.Server();
        if (mgr == null)
            return;
        using var chop = X87.Chop();
        Span<float> box = stackalloc float[6];
        for (var i = 0; i < 3; ++i)
        {
            box[i] = X87.F32(Sub(pos[i], radius));
            box[i + 3] = X87.F32(Add(pos[i], radius));
        }
        List<object?> found = game.World.FindObjects(box, 0x24c00080u, null);
        var ids = new List<int>();
        foreach (var o in found)
            if (o is SimObject so)
                ids.Add(so.Id());
        Span<float> c = stackalloc float[3], d = stackalloc float[3], v = stackalloc float[3], mom = stackalloc float[3];
        foreach (var oid in ids)
        {
            // Scripts may delete objects between two damage calls.
            GameBase? gb = mgr.FindById(oid) as GameBase;
            Container.Entry? e = gb != null ? game.World.Find(gb) : null;
            if (e == null)
                continue;
            for (var i = 0; i < 3; ++i)
            {
                c[i] = X87.F32(Mul(0.5, X87.F32(Add(e.Box[i + 3], e.Box[i]))));
                d[i] = X87.F32(Sub(c[i], pos[i]));
            }
            var dist = X87.F32(X87.Len(d));
            var ext = X87.F32(Sub(e.Box[3], e.Box[0]));
            for (var i = 1; i < 3; ++i)
            {
                var w = X87.F32(Sub(e.Box[i + 3], e.Box[i]));
                if (w < ext)
                    ext = w;
            }
            dist = X87.F32(Sub(dist, ext));
            if (!(dist > 1.0f))
                dist = 1.0f;
            if (dist >= radius)
                continue;
            var inv = X87.F32(Div(1.0, dist));
            if (Add(Add(vel[0], vel[1]), vel[2]) == 0.0)
            {
                for (var i = 0; i < 3; ++i)
                    v[i] = X87.F32(Mul(d[i], inv));
            }
            else
            {
                for (var i = 0; i < 3; ++i)
                    v[i] = vel[i];
            }
            var fall = X87.F32(Div(Sub(radius, dist), radius));
            var value = X87.F32(Mul(damage, fall));
            var k = X87.F32(Mul(Mul(kick, fall), inv));
            for (var i = 0; i < 3; ++i)
                mom[i] = X87.F32(Mul(k, d[i]));
            var cover = gb!.DamageCoverage(pos);
            if (mgr.FindById(oid) != gb)
                continue;
            value = X87.F32(Mul(cover, value));
            for (var i = 0; i < 3; ++i)
                mom[i] = X87.F32(Mul(cover, mom[i]));
            if (cover != 0.0f)
                gb.ApplyDamage(type, value, pos, v, mom, source);
        }
    }

    /// <summary>Registers the GameBase damage commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
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
            var v = new float[9];
            var n = CScan.Floats(a[4], v.AsSpan(0, 3));
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
            var pos = new float[3];
            GameUtil.Scan3(a[2], pos);
            float[] zero = [0, 0, 0];
            ApplyRadiusDamage(game, Numbers.BorlandAtoi(a[1]), pos, zero, Store(Numbers.BorlandAtof(a[3])),
                              Store(Numbers.BorlandAtof(a[4])), Store(Numbers.BorlandAtof(a[5])), Numbers.BorlandAtoi(a[6]));
            return "true";
        });
    }
}
