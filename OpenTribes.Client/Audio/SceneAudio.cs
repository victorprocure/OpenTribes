namespace OpenTribes.Client.Audio;

using System.Globalization;
using OpenTribes.Client.Effects;
using OpenTribes.Client.Scene;
using OpenTribes.Game;
using OpenTribes.World;

/// <summary>Starts the sounds of the scene: network events and the sounds driven by ghosts.</summary>
/// <remarks>docs/audio.md 3 (where sounds start): network events (3.1) and the ghost driven sounds (3.2 to 3.4) of the scene model, over the <see cref="SoundManager"/>. The scene model calls the <c>On*</c> hooks while it applies packets and ghost updates. <see cref="Frame"/> runs once per client frame after the visuals ticked (listener, follow the object positions, stops).</remarks>
public sealed class SceneAudio
{
    /// <summary>The sound manager the sounds play on.</summary>
    public SoundManager Sound { get; }
    private readonly SceneModel _m;
    private Db Db => _m.Db;

    /// <summary>True when voices play: <c>$pref::playVoices</c> (1.5), which is true when unset.</summary>
    public bool PlayVoices => _m.World.Players.PlayVoices();
    /// <summary>The materials of the terrain, to pick the footstep surface type of a terrain material byte (3.2). Empty means type 0.</summary>
    public List<Material>? TerrainMaterials { get; set; }
    /// <summary>Reads the surface type (0 to 14) under a foot from the prediction container (terrain and interiors, <see cref="FootSurface"/>), -1 for none. Null offline.</summary>
    public Func<double, double, double, int>? SurfaceType { get; set; }

    /// <summary>The number of sounds started per source, for the offline report.</summary>
    public Dictionary<string, int> Started { get; } = [];

    private sealed class GhostSounds
    {
        public int Looper { get; set; }                         // jet, vehicle idle, static ambient, projectile
        public int Move { get; set; }
        public int Whir { get; set; }
        public int Run { get; set; }
        public int Sustained { get; set; }
        public int[] SeqSound { get; } = new int[3];     // sequence sound handle per thread slot (3.3)
        public int[] SeqPlaying { get; } = [.. Enumerable.Repeat(-1, 3)];   // sequence the sound was started for
        public ImageVoiceSlots Image { get; } = new();
        public int[] Channel { get; } = new int[4];
        public int LastMode { get; set; } = -1;
        public int LastFire { get; set; } = -1;
        public int LastDamage { get; set; } = -1;
        public int LastSound { get; set; } = -1;
        public int LastState { get; set; } = -1;
        public double JetOffSince { get; set; } = -1;
        public double LastRotation { get; set; } = double.NaN;
        public double LastElevation { get; set; } = double.NaN;
        public double WhirUntil { get; set; }
        public int DatablockStarted { get; set; } = -1;
        public bool Stopped { get; set; }
    }

    private readonly Dictionary<int, GhostSounds> _ghosts = [];
    private readonly Dictionary<int, int> _voiceByClient = [];
    private int _precip;
    /// <summary>The queue of sounds that play one after another.</summary>
    public SoundSequencer Sequencer { get; }

    /// <summary>Creates the scene audio.</summary>
    /// <param name="m">The scene model.</param>
    /// <param name="sound">The sound manager.</param>
    public SceneAudio(SceneModel m, SoundManager sound)
    {
        _m = m;
        Sound = sound;
        Sequencer = new SoundSequencer(sound);
        _m.World.Players.ChatVoice = ChatWave;
    }

    private void Count(string what, int handle)
    {
        if (handle != 0)
            Started[what] = Started.GetValueOrDefault(what) + 1;
    }

    private GhostSounds Gs(int ghost)
    {
        if (!_ghosts.TryGetValue(ghost, out var g))
            _ghosts[ghost] = g = new GhostSounds();
        return g;
    }

    private static SoundXf At((double x, double y, double z)? p) => p is var (x, y, z) ? new SoundXf(x, y, z) : new SoundXf(0, 0, 0);

    private int Play(string what, long sound, (double x, double y, double z)? at)
    {
        if (sound < 0 || at == null)
            return 0;
        var h = Sound.PlayDatablock(sound, At(at));
        Count(what, h);
        return h;
    }

    private void Follow(int handle, (double x, double y, double z)? at)
    {
        if (handle != 0 && at is var (x, y, z))
            Sound.SetPosition(handle, x, y, z);
    }

    private static long Idx(ClientDataBlock? b, string field) => b == null ? -1 : (long)Db.Num(b, field, -1);


    /// <summary>Moves the listener (2.6).</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="yawRad">The heading in radians.</param>
    public void SetListener(double x, double y, double z, double yawRad)
    {
        var (rx, ry, rz) = SoundGain.RightOfYaw(yawRad);
        Sound.SetListener(x, y, z, rx, ry, rz);
    }


    /// <summary>Starts the sounds of the events of a packet (3.1).</summary>
    /// <param name="pkt">The decoded packet.</param>
    public void OnPacket(DecodedPacket pkt)
    {
        foreach (var e in pkt.Events)
        {
            object? F(string n) => e.Fields.FirstOrDefault(f => f.Name == n).Value;
            switch (e.Tag)
            {
                case 0x45c:
                    LocSound((long)Db.ToDouble(F("sound"), -1), F("dir") as double[], F("dist") is { } d ? Db.ToDouble(d) : -1);
                    break;
                case 0x460:
                    ShapeSound((int)Db.ToDouble(F("ghost")), (int)Db.ToDouble(F("channel")), (long)Db.ToDouble(F("sound"), -1));
                    break;
                case 0x45f:
                    Voice((int)Db.ToDouble(F("client")), F("wav") as string ?? "");
                    break;
                case 0x40f:
                    SfxEvent((int)Db.ToDouble(F("kind")), F("name") as string ?? "", (int)Db.ToDouble(F("id")), F("pos") as double[]);
                    break;
                case 0x423:
                    SoundSequence(e.Text);
                    break;
                case 0x453:
                    ChatWave((int)Db.ToDouble(F("from")), F("msg") as string ?? "");
                    break;
                default:
                    break;
            }
        }
    }

    // LocSoundEvent: listener position + direction * (distance * profile maxDist), identity orientation, zero velocity.
    private void LocSound(long sound, double[]? dir, double dist)
    {
        if (!Sound.TryDatablock(sound, out _, out var p, out _))
            return;
        var l = Sound.Listener;
        var at = new SoundXf(l.X, l.Y, l.Z);
        if (dir != null && dist >= 0)
        {
            at.X += dir[0] * dist * p.MaxDist;
            at.Y += dir[1] * dist * p.MaxDist;
            at.Z += dir[2] * dist * p.MaxDist;
        }
        Count("LocSoundEvent", Sound.PlayDatablock(sound, at));
    }

    // SoundEvent: stop the channel's voice, play at the object, remember the handle (0..3).
    private void ShapeSound(int ghost, int channel, long sound)
    {
        if (!_m.Visuals.TryGetValue(ghost, out var v) || channel is < 0 or > 3)
            return;
        var g = Gs(ghost);
        Sound.Stop(g.Channel[channel]);
        g.Channel[channel] = Play("SoundEvent", sound, v.Position);
    }

    private bool HasWave(string name) => Sound.ReadWave?.Invoke(name) != null;

    // 1.5 name building: `<voiceBase>.w<name>.wav`, else `w<name>.wav`.
    private string? VoiceWave(int client, string text)
    {
        var a = $"{_m.World.Players.Get(client)?.Voice}.w{text}.wav";
        if (HasWave(a))
            return a;
        var b = $"w{text}.wav";
        return HasWave(b) ? b : null;
    }

    private void Voice(int client, string text)
    {
        if (!PlayVoices)
            return;
        var rec = _m.World.Players.Get(client);
        if (rec == null || rec.OwnedGhost < 0 || !_m.Visuals.TryGetValue(rec.OwnedGhost, out var v) || v.Position == null)
            return;
        var wave = VoiceWave(client, text);
        if (wave == null || !ProfileZero(out var p))
            return;
        if (_voiceByClient.TryGetValue(client, out var old))
            Sound.Stop(old);
        var h = Sound.PlayWave(wave, p, 0, At(v.Position));
        _voiceByClient[client] = h;
        Count("VoiceEvent", h);
    }

    private bool ProfileZero(out SoundProfile p)
    {
        p = SoundProfile.Default;
        if (Sound.DataBlocks == null || !Sound.DataBlocks.Blocks.TryGetValue((0, 0), out var b))
            return false;
        p = SoundManager.ProfileOf(b);
        return true;
    }

    // Chat text with ~w (1.5): a 2D wave, the sender's previous voice stopped first.
    private void ChatWave(int from, string msg)
    {
        var i = msg.IndexOf("~w", StringComparison.Ordinal);
        if (i < 0 || !PlayVoices)
            return;
        var text = msg[(i + 2)..];
        var wave = _m.World.Players.Get(from) != null ? VoiceWave(from, text) : text;
        if (wave == null)
            return;
        if (_voiceByClient.TryGetValue(from, out var old))
            Sound.Stop(old);
        var h = Sound.PlayWave(wave);
        _voiceByClient[from] = h;
        Count("ChatWave", h);
    }

    // Sfx::SoundEvent (kinds 0 and 2), priority 1.0.
    private void SfxEvent(int kind, string name, int profileId, double[]? pos)
    {
        if (!Sound.Profiles.TryGetValue(profileId, out var p))
            return;
        if (kind == 0)
            Count("Sfx::SoundEvent", Sound.PlayWave(name, p, 1.0));
        else if (kind == 2 && pos != null)
            Count("Sfx::SoundEvent", Sound.PlayWave(name, p, 1.0, new SoundXf(pos[0], pos[1], pos[2])));
    }

    // SimSoundSequenceEvent text: "<dest> field20=<sound> field5c=<channel> [record=52 bytes]"; the transform record is not kept by the decoder.
    private void SoundSequence(string text)
    {
        int s = ParseKey(text, "field20="), c = ParseKey(text, "field5c=");
        if (s != int.MinValue)
            Sequencer.Add(c == int.MinValue ? -1 : c, s, null);
    }

    private static int ParseKey(string text, string key)
    {
        var p = text.IndexOf(key, StringComparison.Ordinal);
        if (p < 0)
            return int.MinValue;
        p += key.Length;
        var e = p;
        while (e < text.Length && (char.IsDigit(text[e]) || text[e] == '-'))
            ++e;
        return int.TryParse(text.AsSpan(p, e - p), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : int.MinValue;
    }


    /// <summary>Plays the sound of an explosion datablock (3.4).</summary>
    /// <param name="db">The explosion datablock.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    public void OnExplosion(ClientDataBlock db, double x, double y, double z) =>
        Count("Explosion", Idx(db, "soundId") is long s && s >= 0 ? Sound.PlayDatablock(s, new SoundXf(x, y, z)) : 0);


    /// <summary>Stops the sounds of a ghost that goes away (3.2 to 3.4).</summary>
    /// <param name="ghost">The ghost index.</param>
    public void OnDelete(int ghost)
    {
        if (!_ghosts.Remove(ghost, out var g))
            return;
        StopAll(g);
    }

    private void StopAll(GhostSounds g)
    {
        Sound.Stop(g.Looper);
        Sound.Stop(g.Move);
        Sound.Stop(g.Whir);
        Sound.Stop(g.Run);
        Sound.Stop(g.Sustained);
        foreach (var h in g.Image.Handles)
            Sound.Stop(h);
        foreach (var h in g.SeqSound)
            Sound.Stop(h);
        Array.Clear(g.SeqSound);
        Array.Fill(g.SeqPlaying, -1);
        g.Looper = g.Move = g.Whir = g.Run = g.Sustained = 0;
        g.Image.Clear();
    }

    /// <summary>Plays the power sound of a turret.</summary>
    /// <param name="sv">The turret visual.</param>
    /// <param name="on">True for power up, false for power down.</param>
    /// <remarks>Follows Turret_activate (00455b34) and Turret_deactivate (00455cc4), ghost branches: the sound starts from the own tick of the ghost, at the thread position the pose model reports, and first stops the whir handle (+0x910). The datablock fields are activationSound (+0x13c) and deactivateSound (+0x140).</remarks>
    public void TurretPower(ShapeVisual sv, bool on)
    {
        var b = Db.Block(19, DatablockIndex(sv));
        if (b == null)
            return;
        var g = Gs(sv.Index);
        var sound = Idx(b, on ? "activationSound" : "deactivateSound");
        if (sound < 0)
            return;
        Sound.Stop(g.Whir);
        g.Whir = 0;
        Play("TurretPower", sound, sv.Position);
    }

    private static int DatablockIndex(Visual v) => (int)Db.ToDouble(v.Ghost.State.GetValueOrDefault("datablock"), -1);

    /// <summary>Starts and stops the sounds that follow the state of a ghost after an update.</summary>
    /// <param name="v">The visual of the ghost.</param>
    /// <param name="u">The decoded update.</param>
    public void OnGhost(Visual v, DecodedGhost u)
    {
        var g = Gs(v.Index);
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        switch (v)
        {
            case ProjectileVisual pv:
                if (g.Looper == 0 && !pv.Exploded && !g.Stopped && pv.Position != null)
                {
                    var group = v.Class switch { "Bullet" => 14, "Grenade" => 15, "RocketDumb" => 16, "SeekingMissile" => 25, _ => 13 };
                    g.Looper = Play("Projectile", Idx(Db.Block(group, DatablockIndex(v)), "soundId"), pv.Position);
                }
                if (pv.Exploded)
                {
                    Sound.Stop(g.Looper);
                    g.Looper = 0;
                    g.Stopped = true;
                }
                break;
            case BeamVisual bv when v.Class == "LaserProjectile":
                if (g.Looper == 0 && !g.Stopped && bv.Position != null)
                {
                    g.Looper = Play("Projectile", Idx(Db.Block(17, DatablockIndex(v)), "soundId"), bv.Position);
                    g.Stopped = true;
                }
                break;
            case PlayerVisual:
                break;
            case ShapeVisual sv:
                ShapeGhost(sv, g, u, has);
                break;
            default:
                break;
        }
    }

    /// <summary>Plays the sound of the state a weapon image entered (3.2): Activate, Fire, Ready, Reload, SpinUp or SpinDown.</summary>
    /// <param name="pl">The player visual.</param>
    /// <param name="slot">The image slot.</param>
    /// <remarks>The image state machine of the ghost (<c>ImageStateMachine</c>) names the datablock sound of each state it enters. A still playing looping image sound stops when the state changes.</remarks>
    public void ImageStateChanged(PlayerVisual pl, int slot)
    {
        if (slot is < 0 or > 7)
            return;
        Sound.Stop(Gs(pl.Index).Image.StateChanged(slot));
    }

    /// <summary>Stops the stored voice of a slot whose image was replaced or removed, looping or not.</summary>
    /// <param name="pl">The player visual.</param>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows 0041a64c Player_resetImage.</remarks>
    public void ImageReset(PlayerVisual pl, int slot)
    {
        if (slot is < 0 or > 7)
            return;
        Sound.Stop(Gs(pl.Index).Image.Reset(slot));
    }

    /// <summary>Plays one sound field of an image datablock and keeps the voice in the slot.</summary>
    /// <param name="pl">The player visual.</param>
    /// <param name="slot">The image slot, 0 to 7.</param>
    /// <param name="image">The image datablock.</param>
    /// <param name="field">The sound field name, starting with <c>snd</c>.</param>
    public void ImageSound(PlayerVisual pl, int slot, ClientDataBlock? image, string field)
    {
        if (slot is < 0 or > 7)
            return;
        var g = Gs(pl.Index);
        var h = Play("Image" + field[3..], Idx(image, field), pl.Position);
        g.Image.Started(slot, h, h != 0 && Sound.IsLooping(h));
    }

    /// <summary>Plays the one shot at the player position when a ghost starts an animation whose PlayerData entry has a sound (3.2).</summary>
    /// <param name="p">The player visual.</param>
    /// <param name="sound">The sound datablock index.</param>
    public void AnimationSound(PlayerVisual p, long sound)
    {
        Sound.TryDatablock(sound, out var wave, out _, out _);
        Play("Animation " + wave, sound, p.Position);
    }

    /// <summary>Plays a footstep (3.2): the surface type indexes the 15 pairs of the datablock.</summary>
    /// <param name="p">The player visual.</param>
    /// <param name="right">True for the right foot (1), false for the left foot (2). The foot is on the ground.</param>
    /// <param name="x">The foot x.</param>
    /// <param name="y">The foot y.</param>
    /// <param name="z">The foot z.</param>
    public void Footstep(PlayerVisual p, bool right, double x, double y, double z)
    {
        var pd = Db.Block(3, (int)Db.ToDouble(p.Ghost.State.GetValueOrDefault("datablock"), -1));
        var m = SurfaceType?.Invoke(x, y, z) ?? -1;
        if (m < 0 && _m.Terrain != null)
        {
            m = 0;
            float h = 0;
            var n = new Vec3(0, 0, 1);
            var mat = 0;
            if (_m.Terrain.Surface((float)x, (float)y, ref h, ref n, ref mat) && TerrainMaterials != null && mat >= 0 && mat < TerrainMaterials.Count)
                m = Math.Clamp(TerrainMaterials[mat].Type, 0, 14);
        }
        if (m < 0)
            m = 0;
        if (Db.Get(pd, $"footSounds[{m}]") is object[] pair && pair.Length >= 2)
            Play("Footstep", (long)Db.ToDouble(pair[right ? 0 : 1], -1), (x, y, z));
    }

    private void ShapeGhost(ShapeVisual sv, GhostSounds g, DecodedGhost u, Func<string, bool> has)
    {
        var idx = DatablockIndex(sv);
        var group = sv.Class switch { "StaticShape" => 4, "Turret" => 19, "Sensor" => 8, "Moveable" => 7, "Vehicle" => 9, "Flier" => 10, _ => -1 };
        if (group < 0)
            return;
        var b = Db.Block(group, idx);
        if (b == null)
            return;
        double Num(string k, double def = 0) => Db.ToDouble(sv.Ghost.State.GetValueOrDefault(k), def);
        var pos = sv.Position;
        if (has("threads"))
            SequenceSounds(sv, g, b, pos, u.Fields.First(f => f.Name == "threads").Value);
        switch (sv.Class)
        {
            case "Vehicle":
            case "Flier":
            {
                if (g.DatablockStarted != idx)
                {
                    g.DatablockStarted = idx;
                    Sound.Stop(g.Looper);
                    g.Looper = Play("VehicleIdle", Idx(b, "idleSound"), pos);
                }
                // client-net.md 3.5 fields: f8c4 the 2 bit sound state, f948 the fire counter, i6 the damage level.
                if (has("f8c4"))
                {
                    var ss = (int)Num("f8c4");
                    if (ss == 2 && g.Move == 0)
                        g.Move = Play("VehicleMove", Idx(b, "moveSound"), pos);
                    else if (ss != 2)
                    {
                        Sound.Stop(g.Move);
                        g.Move = 0;
                    }
                }
                if (has("f948"))
                {
                    var fc = (int)Num("f948");
                    if (g.LastFire >= 0 && fc != g.LastFire)
                        Play("VehicleFire", Idx(b, "fireSound"), pos);
                    g.LastFire = fc;
                }
                if (has("i6"))
                {
                    var dl = (int)Num("i6");
                    if (g.LastDamage >= 0 && dl > g.LastDamage)
                        Play("VehicleDamage", Idx(b, "damageSound"), pos);
                    g.LastDamage = dl;
                }
                break;
            }
            case "Turret":
            {
                var sustained = Db.Flag(b, "isSustained");
                if (has("fireState"))
                {
                    var fs = (int)Num("fireState");
                    if (!sustained && g.LastFire >= 0 && fs != g.LastFire)
                        Play("TurretFire", Idx(b, "fireSound"), pos);
                    g.LastFire = fs;
                }
                if (sustained && has("barrelFiring"))
                {
                    if (Num("barrelFiring") != 0 && g.Sustained == 0)
                        g.Sustained = Play("TurretFire", Idx(b, "fireSound"), pos);
                    else if (Num("barrelFiring") == 0)
                    {
                        Sound.Stop(g.Sustained);
                        g.Sustained = 0;
                    }
                }
                if (has("rotation") || has("elevation"))
                {
                    double r = Num("rotation"), e = Num("elevation");
                    if (!double.IsNaN(g.LastRotation) && (r != g.LastRotation || e != g.LastElevation))
                    {
                        g.WhirUntil = _m.Time + 0.3;
                        if (g.Whir == 0)
                            g.Whir = Play("TurretWhir", Idx(b, "whirSound"), pos);
                    }
                    g.LastRotation = r;
                    g.LastElevation = e;
                }
                AmbientAndState(sv, g, b, pos);
                break;
            }
            case "Moveable":
            {
                if (has("state"))
                {
                    var st = (int)Num("state");
                    if (g.LastState > 0 && st != g.LastState)
                        Play("MoveableState", Idx(b, st == 3 ? "sfxStop" : "sfxStart"), pos);
                    g.LastState = st;
                    if (st == 3)
                    {
                        Sound.Stop(g.Run);
                        g.Run = 0;
                    }
                    else if (g.Run == 0 && st != 0)
                        g.Run = Play("MoveableRun", Idx(b, "sfxRun"), pos);
                }
                AmbientAndState(sv, g, b, pos);
                break;
            }
            default:
                AmbientAndState(sv, g, b, pos);
                break;
        }
    }

    // 3.3 sequenceSound: a running thread whose sequence name is in the datablock's pairs plays that sound at the object; the sound stops
    // when the thread stops or pauses, reaches the end, or changes sequence.
    private void SequenceSounds(ShapeVisual sv, GhostSounds g, ClientDataBlock b, (double, double, double)? pos, object? threads)
    {
        foreach (var s in ThreadSlot.ParseAll(threads))
        {
            if (s.Slot is < 0 or > 2)
                continue;
            var running = s.State == 0 && !s.AtEnd && s.Sequence >= 0;
            if (!running || g.SeqPlaying[s.Slot] != s.Sequence)
            {
                Sound.Stop(g.SeqSound[s.Slot]);
                g.SeqSound[s.Slot] = 0;
                g.SeqPlaying[s.Slot] = -1;
            }
            if (!running || g.SeqPlaying[s.Slot] == s.Sequence)
                continue;
            g.SeqPlaying[s.Slot] = s.Sequence;
            var name = sv.SequenceName(s.Sequence);
            for (var i = 0; i < 16; ++i)
                if (Db.Get(b, $"sequenceSound[{i}]") is object[] pair && pair.Length >= 2 && pair[0] is string n
                    && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    g.SeqSound[s.Slot] = Play("SequenceSound " + name, (long)Db.ToDouble(pair[1], -1), pos);
                    break;
                }
        }
    }

    // StaticBase ambient sound (3.3): starts on a ghost with a datablock, stops when the damage state becomes 2 or 3.
    private void AmbientAndState(ShapeVisual sv, GhostSounds g, ClientDataBlock b, (double, double, double)? pos)
    {
        var ds = (int)Db.ToDouble(sv.Ghost.State.GetValueOrDefault("damageState"), 1);
        if (ds >= 2)
        {
            Sound.Stop(g.Looper);
            g.Looper = 0;
            g.Stopped = true;
        }
        else if (g.Looper == 0 && !g.Stopped && pos != null)
            g.Looper = Play("Ambient", Idx(b, "sfxAmbient"), pos);
    }


    /// <summary>Runs once per client frame after the visuals ticked: moves the listener, the follow the object voices and the stops.</summary>
    public void Frame()
    {
        foreach (var (index, g) in _ghosts)
        {
            if (!_m.Visuals.TryGetValue(index, out var v))
                continue;
            var pos = v.Position;
            switch (v)
            {
                case PlayerVisual pl:
                {
                    // Jet (3.2): looping while jetting and not mounted; stops when jetting ends.
                    var jet = pl.Jetting && pl.MountGhost < 0;
                    if (jet && g.Looper == 0)
                    {
                        var pd = Db.Block(3, DatablockIndex(v));
                        g.Looper = Play("Jet", Idx(pd, "jetSound"), pos);
                    }
                    else if (jet)
                        g.JetOffSince = -1;
                    else if (g.Looper != 0)
                    {
                        // The animation based jetting flag flickers between movement choices (65 ms); stop after 0.2 s without it.
                        if (g.JetOffSince < 0)
                            g.JetOffSince = _m.Time;
                        if (_m.Time - g.JetOffSince >= 0.2)
                        {
                            Sound.Stop(g.Looper);
                            g.Looper = 0;
                            g.JetOffSince = -1;
                        }
                    }
                    Follow(g.Looper, pos);
                    foreach (var h in g.Image.Handles)
                        Follow(h, pos);
                    break;
                }
                default:
                    Follow(g.Looper, pos);
                    Follow(g.Move, pos);
                    Follow(g.Run, pos);
                    Follow(g.Sustained, pos);
                    Follow(g.Whir, pos);
                    if (g.Whir != 0 && _m.Time > g.WhirUntil)
                    {
                        Sound.Stop(g.Whir);
                        g.Whir = 0;
                    }
                    break;
            }
        }
        // Snow and rain (3.4): a 2D looping pair sound while precipitation is on.
        var precip = _m.HasWeather && WeatherEffect.Draws(_m.WeatherPrecipitation, _m.Weather.Suspended, _m.Weather.Intensity);
        if (precip && _precip == 0)
        {
            _precip = Sound.PlayPair(_m.Weather.Rain ? 450046 : 450045);
            Count("Precipitation", _precip);
        }
        else if (!precip && _precip != 0)
        {
            Sound.Stop(_precip);
            _precip = 0;
        }
        Sequencer.Tick();
    }
}
