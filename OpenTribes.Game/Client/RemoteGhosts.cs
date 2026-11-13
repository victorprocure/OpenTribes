// The remote ghosts of the client that are simulated and smoothed (RemoteSmoothing.cs): one copy per Player or Vehicle ghost
// that is not the control object, made in the prediction world, fed by the ghost updates and advanced once per frame.
namespace OpenTribes.Game;

/// <summary>The remote ghosts of the client that are simulated and smoothed: one copy per Player or Vehicle ghost that is not the control object.</summary>
/// <remarks>The copies are made in the prediction world, fed by the ghost updates and advanced once per frame (RemoteSmoothing.cs).</remarks>
public sealed class RemoteGhosts
{
    /// <summary>The field name of the echoed move.</summary>
    public const string MoveKey = "moveEcho";

    /// <summary>Makes the copy of a ghost at a position. Returns null when there is none, and the ghost is then drawn from its stream state.</summary>
    public Func<ClientGhost, float[], GameBase?>? CreateObject { get; set; }
    /// <summary>Deletes a copy.</summary>
    public Action<GameBase>? DeleteObject { get; set; }
    /// <summary>Reads <c>$net::interpolateTime</c>.</summary>
    public Func<int> Interpolate { get; set; } = () => SmoothWindow.DefaultInterpolate;
    /// <summary>Reads <c>$net::predictForwardTime</c>.</summary>
    public Func<int> Predict { get; set; } = () => SmoothWindow.DefaultPredict;

    /// <summary>The remote Players by ghost index.</summary>
    public Dictionary<int, RemotePlayer> Players { get; } = [];
    /// <summary>The remote Vehicles and Fliers by ghost index.</summary>
    public Dictionary<int, RemoteVehicle> Vehicles { get; } = [];

    /// <summary>Deletes every copy.</summary>
    public void Reset()
    {
        foreach (var p in Players.Values)
            DeleteObject?.Invoke(p.P);
        foreach (var v in Vehicles.Values)
            DeleteObject?.Invoke(v.V);
        Players.Clear();
        Vehicles.Clear();
    }

    private static float[] F3(object? v)
    {
        var r = new float[3];
        if (v is double[] { Length: >= 3 } a)
            for (var i = 0; i < 3; ++i)
                r[i] = (float)a[i];
        return r;
    }

    private static bool Flag(ClientGhost g, string k) => g.State.TryGetValue(k, out var v) && v is long l && l != 0;
    private static float Num(ClientGhost g, string k) => g.State.TryGetValue(k, out var v) && v is double d ? (float)d : 0.0f;

    /// <summary>Applies the ghost updates of one packet.</summary>
    /// <param name="block">The decoded ghost block.</param>
    /// <param name="world">The client world.</param>
    /// <param name="now">The net time in ms.</param>
    /// <param name="controlGhost">The ghost index of the control object, -1 for none. It follows the prediction.</param>
    public void OnBlock(DecodedGhostBlock block, ClientWorld world, uint now, int controlGhost)
    {
        _controlGhost = controlGhost;
        foreach (var u in block.Updates)
        {
            if (u.Op == "delete")
            {
                Remove(u.Index);
                continue;
            }
            var g = world.Ghosts.Get(u.Index);
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
        if (Players.Remove(index, out var p))
            DeleteObject?.Invoke(p.P);
        if (Vehicles.Remove(index, out var v))
        {
            // The riders lose their mount (the delete notification clears +0x1028): their copies go, the stream state is drawn.
            foreach (var rider in Players.Where(kv => kv.Value.MountGhost == index).Select(kv => kv.Key).ToList())
                Remove(rider);
            DeleteObject?.Invoke(v.V);
        }
    }

    private static bool Has(DecodedGhost u, string n)
    {
        foreach (var f in u.Fields)
            if (f.Name == n)
                return true;
        return false;
    }

    /// <summary>Returns the mount object of a ghost: the copy of a remote Vehicle or Flier, or the control object (the predicted copy) when the Player rides the vehicle of this client. Null when there is none yet.</summary>
    public Func<GameBase?>? ControlObject { get; set; }
    private int _controlGhost = -1;

    /// <summary>Returns the mount object of a rider that is not the control Player: the copy of a remote Vehicle or Flier ghost.</summary>
    /// <param name="ghostIndex">The ghost index of the mount.</param>
    /// <returns>The copy, or null.</returns>
    public GameBase? RemoteMount(int ghostIndex) => Vehicles.TryGetValue(ghostIndex, out var rv) ? rv.V : null;

    private GameBase? ResolveMount(int index)
    {
        if (Vehicles.TryGetValue(index, out var rv))
            return rv.V;
        return index == _controlGhost ? ControlObject?.Invoke() : null;
    }

    /// <summary>Parses the mount fields of the Player unpack.</summary>
    /// <param name="g">The Player ghost.</param>
    /// <param name="ghost">Receives the ghost index of the mount.</param>
    /// <param name="point">Receives the mount point.</param>
    /// <returns>True when the ghost has a mount.</returns>
    /// <remarks>The fields are a ghost int(10) and a point int(5), and point 1 sets the rotation to 0 (0x00417e..). The pointer +0x1028 is the client object of that ghost.</remarks>
    public static bool ParseMount(ClientGhost g, out int ghost, out int point)
    {
        ghost = -1;
        point = 0;
        if (!g.State.TryGetValue("mount", out var m) || m is not string ms || ms.StartsWith("none", StringComparison.Ordinal))
            return false;
        var a = ms.Split('/');
        return a.Length == 2 && int.TryParse(a[0], out ghost) && int.TryParse(a[1], out point);
    }

    // Binds +0x1028 and +0x102c of every Player copy that has a mount ghost.
    private void BindMounts()
    {
        foreach (var rp in Players.Values)
        {
            if (rp.MountGhost < 0)
                rp.P.MountId = 0;
            else
            {
                var mo = ResolveMount(rp.MountGhost);
                rp.P.MountId = mo != null ? mo.Id() : 0;
                rp.P.MountPoint = rp.MountPoint;
            }
        }
    }

    private void OnPlayer(DecodedGhost u, ClientGhost g, uint now)
    {
        var mounted = ParseMount(g, out var mountGhost, out var mountPoint);
        if (mounted && ResolveMount(mountGhost) == null)
        {
            // Nothing to follow (the mount object has no copy): the stream state is drawn.
            Remove(u.Index);
            return;
        }
        if (!Has(u, "pos"))
            return;
        var pos = F3(g.State.GetValueOrDefault("pos"));
        var vel = Has(u, "vel") ? F3(g.State.GetValueOrDefault("vel")) : new float[3];
        float pitch = Num(g, "pitch"), yaw = Num(g, "rotZ");
        var contact = Flag(g, "flag1f4");
        if (!Players.TryGetValue(u.Index, out var rp))
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
        if (u.Op == "new" || !Has(u, "move") || !g.State.TryGetValue(MoveKey, out var mv) || mv is not Move move)
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
        var created = false;
        if (!Vehicles.TryGetValue(u.Index, out var rv))
        {
            if (CreateObject?.Invoke(g, pos) is not Vehicle v)
                return;
            rv = new RemoteVehicle(v);
            Vehicles[u.Index] = rv;
            created = true;
        }
        rv.V.Piloted = g.State.TryGetValue("controlClient", out var cc) && cc is long c && c != 0;
        if (!Has(u, "move") || !g.State.TryGetValue(MoveKey, out var mv) || mv is not Move move)
        {
            rv.Place(rot, pos);
            if (created)
                rv.Register(now);
            return;
        }
        var d = rv.V.Data;
        var speedF = ((d.MaxSpeed - d.MinSpeed) * Num(g, "unit10")) + d.MinSpeed;
        rv.SetState(now, move, pos, rot, (int)(g.State.GetValueOrDefault("f8d8") is long f ? f : 0), Num(g, "f898"), speedF,
                    Interpolate(), Predict());
        if (created)
            rv.Register(now);
    }

    /// <summary>Advances the remote copies once per frame.</summary>
    /// <param name="now">The net time in ms.</param>
    /// <remarks>Follows Player_clientUpdate and Vehicle_clientUpdate. The client frame (docs/client-net.md 2.1 step 9) calls vtable +0x154 for the members of set 718 (vehicles) and then of set 719 (Players), skipping an object whose +0x218 equals now. A rider thus follows the vehicle of the same frame.</remarks>
    public void Advance(uint now)
    {
        foreach (var v in Vehicles.Values)
            v.Advance(now);
        foreach (var p in Players.Values)
            p.Advance(now);
    }
}
