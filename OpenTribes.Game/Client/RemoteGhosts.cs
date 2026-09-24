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
        mControlGhost = controlGhost;
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
        {
            // The riders lose their mount (the delete notification clears +0x1028): their copies go, the stream state is drawn.
            foreach (int rider in Players.Where(kv => kv.Value.MountGhost == index).Select(kv => kv.Key).ToList())
                Remove(rider);
            DeleteObject?.Invoke(v.V);
        }
    }

    private static bool Has(DecodedGhost u, string n)
    {
        foreach (DecodedField f in u.Fields)
            if (f.Name == n)
                return true;
        return false;
    }

    // The mount object of ghost `index`: the copy of a remote Vehicle or Flier, or the control object (the predicted copy) when the
    // Player rides the vehicle of this client. Null when there is none (yet).
    public Func<GameBase?>? ControlObject;
    private int mControlGhost = -1;

    private GameBase? ResolveMount(int index)
    {
        if (Vehicles.TryGetValue(index, out RemoteVehicle? rv))
            return rv.V;
        return index == mControlGhost ? ControlObject?.Invoke() : null;
    }

    // The mount fields of the Player unpack (0x00417e..: ghost int(10), point int(5); point 1 sets the rotation to 0): the pointer
    // +0x1028 is the client object of that ghost.
    private static bool ParseMount(ClientGhost g, out int ghost, out int point)
    {
        ghost = -1;
        point = 0;
        if (!g.State.TryGetValue("mount", out object? m) || m is not string ms || ms.StartsWith("none", StringComparison.Ordinal))
            return false;
        string[] a = ms.Split('/');
        return a.Length == 2 && int.TryParse(a[0], out ghost) && int.TryParse(a[1], out point);
    }

    // Binds +0x1028 and +0x102c of every Player copy that has a mount ghost.
    private void BindMounts()
    {
        foreach (RemotePlayer rp in Players.Values)
        {
            if (rp.MountGhost < 0)
                rp.P.MountId = 0;
            else
            {
                GameBase? mo = ResolveMount(rp.MountGhost);
                rp.P.MountId = mo != null ? mo.Id() : 0;
                rp.P.MountPoint = rp.MountPoint;
            }
        }
    }

    private void OnPlayer(DecodedGhost u, ClientGhost g, uint now)
    {
        bool mounted = ParseMount(g, out int mountGhost, out int mountPoint);
        if (mounted && ResolveMount(mountGhost) == null)
        {
            // Nothing to follow (the mount object has no copy): the stream state is drawn.
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
            SetMount(rp, mounted, mountGhost, mountPoint);
            rp.Place(now, pos, vel, yaw, pitch, contact);
            rp.Register(now);
            return;
        }
        ClientPrediction.SyncPlayerGhost(rp.P, g);
        if (Has(u, "mount"))
            SetMount(rp, mounted, mountGhost, mountPoint);
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

    private void SetMount(RemotePlayer rp, bool mounted, int ghost, int point)
    {
        rp.MountGhost = mounted ? ghost : -1;
        rp.MountPoint = point;
        if (mounted && point == 1)
            rp.P.SetRotation(new float[3]);       // the unpack sets the rotation to 0 for point 1 (vtable +0x1a0)
        BindMounts();
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
    // The vehicles go first so that a rider follows the mount point of the vehicle of this frame (the order of the original's
    // object list is not known).
    public void Advance(uint now)
    {
        foreach (RemoteVehicle v in Vehicles.Values)
            v.Advance(now);
        foreach (RemotePlayer p in Players.Values)
            p.Advance(now);
    }
}
