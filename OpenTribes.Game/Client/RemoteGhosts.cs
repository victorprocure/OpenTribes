// The remote ghosts of the client that are simulated and smoothed (RemoteSmoothing.cs): one copy per Player or Vehicle ghost
// that is not the control object, made in the prediction world, fed by the ghost updates and advanced once per frame.
using OpenTribes.Core;

namespace OpenTribes.Game;

public sealed class RemoteGhosts
{
    public const string MoveKey = "moveEcho";

    // Makes the copy of a ghost at `position` (null: none, the ghost is drawn from its stream state).
    public Func<ClientGhost, float[], GameBase?>? CreateObject;
    public Action<GameBase>? DeleteObject;
    // $net::interpolateTime and $net::predictForwardTime.
    public Func<int> Interpolate = () => SmoothWindow.DefaultInterpolate;
    public Func<int> Predict = () => SmoothWindow.DefaultPredict;

    public readonly Dictionary<int, RemotePlayer> Players = new();
    public readonly Dictionary<int, RemoteVehicle> Vehicles = new();

    public void Reset()
    {
        foreach (RemotePlayer p in Players.Values)
            DeleteObject?.Invoke(p.P);
        foreach (RemoteVehicle v in Vehicles.Values)
            DeleteObject?.Invoke(v.V);
        Players.Clear();
        Vehicles.Clear();
    }

    private static float[] F3(object? v)
    {
        var r = new float[3];
        if (v is double[] { Length: >= 3 } a)
            for (int i = 0; i < 3; ++i)
                r[i] = (float)a[i];
        return r;
    }

    private static bool Flag(ClientGhost g, string k) => g.State.TryGetValue(k, out object? v) && v is long l && l != 0;
    private static float Num(ClientGhost g, string k) => g.State.TryGetValue(k, out object? v) && v is double d ? (float)d : 0.0f;

    // The ghost updates of one packet, received at net time `now`. `controlGhost` is the control object (-1 none): it
    // follows the prediction.
    public void OnBlock(DecodedGhostBlock block, ClientWorld world, uint now, int controlGhost)
    {
        foreach (DecodedGhost u in block.Updates)
        {
            if (u.Op == "delete")
            {
                Remove(u.Index);
                continue;
            }
            ClientGhost? g = world.Ghosts.Get(u.Index);
            if (g == null || u.Index == controlGhost)
            {
                Remove(u.Index);
                continue;
            }
            if (g.Class == "Player")
                OnPlayer(u, g, now);
            else if (g.Class is "Flier" or "Vehicle")
                OnVehicle(u, g, now);
        }
    }

    private void Remove(int index)
    {
        if (Players.Remove(index, out RemotePlayer? p))
            DeleteObject?.Invoke(p.P);
        if (Vehicles.Remove(index, out RemoteVehicle? v))
            DeleteObject?.Invoke(v.V);
    }

    private static bool Has(DecodedGhost u, string n)
    {
        foreach (DecodedField f in u.Fields)
            if (f.Name == n)
                return true;
        return false;
    }

    private void OnPlayer(DecodedGhost u, ClientGhost g, uint now)
    {
        // Mounted Players keep the stream state (the mount transform is not copied).
        bool mounted = g.State.TryGetValue("mount", out object? m) && m is string ms && !ms.StartsWith("none", StringComparison.Ordinal);
        if (mounted)
        {
            Remove(u.Index);
            return;
        }
        if (!Has(u, "pos"))
            return;
        float[] pos = F3(g.State.GetValueOrDefault("pos"));
        float[] vel = Has(u, "vel") ? F3(g.State.GetValueOrDefault("vel")) : new float[3];
        float pitch = Num(g, "pitch"), yaw = Num(g, "rotZ");
        bool contact = Flag(g, "flag1f4");
        if (!Players.TryGetValue(u.Index, out RemotePlayer? rp))
        {
            if (CreateObject?.Invoke(g, pos) is not Player p)
                return;
            ClientPrediction.SyncPlayerGhost(p, g);
            p.Dead = Flag(g, "dead") && !Has(u, "damage");
            rp = new RemotePlayer(p);
            Players[u.Index] = rp;
            rp.Place(now, pos, vel, yaw, pitch, contact);
            rp.Register(now);
            return;
        }
        ClientPrediction.SyncPlayerGhost(rp.P, g);
        if (Has(u, "dead"))
            rp.P.Dead = true;
        else if (Has(u, "damage"))
            rp.P.Dead = false;       // the living branch of the dead/damage field (+0x1019 = 0)
        if (u.Op == "new" || !Has(u, "move") || !g.State.TryGetValue(MoveKey, out object? mv) || mv is not Move move)
        {
            rp.Place(now, pos, vel, yaw, pitch, contact);
            return;
        }
        if (Has(u, "energy"))
            rp.P.Energy = (float)(Num(g, "energy") * (double)rp.P.MaxEnergy);
        rp.Update(now, move, pos, vel, contact, pitch, yaw, (int)(g.State.GetValueOrDefault("f8c") is long f ? f : 0), Flag(g, "flag10000"),
                  Interpolate(), Predict());
    }

    private void OnVehicle(DecodedGhost u, ClientGhost g, uint now)
    {
        if (!Has(u, "pos") || !Has(u, "rot"))
            return;
        float[] pos = F3(g.State.GetValueOrDefault("pos")), rot = F3(g.State.GetValueOrDefault("rot"));
        bool created = false;
        if (!Vehicles.TryGetValue(u.Index, out RemoteVehicle? rv))
        {
            if (CreateObject?.Invoke(g, pos) is not Vehicle v)
                return;
            rv = new RemoteVehicle(v);
            Vehicles[u.Index] = rv;
            created = true;
        }
        rv.V.Piloted = g.State.TryGetValue("controlClient", out object? cc) && cc is long c && c != 0;
        if (!Has(u, "move") || !g.State.TryGetValue(MoveKey, out object? mv) || mv is not Move move)
        {
            rv.Place(rot, pos);
            if (created)
                rv.Register(now);
            return;
        }
        Vehicle.VData d = rv.V.Data;
        float speedF = (d.MaxSpeed - d.MinSpeed) * Num(g, "unit10") + d.MinSpeed;
        rv.SetState(now, move, pos, rot, (int)(g.State.GetValueOrDefault("f8d8") is long f ? f : 0), Num(g, "f898"), speedF,
                    Interpolate(), Predict());
        if (created)
            rv.Register(now);
    }

    // Player_clientUpdate and Vehicle_clientUpdate, once per frame at net time `now`.
    public void Advance(uint now)
    {
        foreach (RemotePlayer p in Players.Values)
            p.Advance(now);
        foreach (RemoteVehicle v in Vehicles.Values)
            v.Advance(now);
    }
}
