// docs/audio.md 3: where sounds start. Network events (3.1) and the ghost-driven sounds (3.2 to 3.4) of the scene model, over the
// SoundManager. The scene model calls the On* hooks while it applies packets and ghost updates; Frame runs once per client frame
// after the visuals ticked (listener, follow-the-object positions, stops).
using System.Globalization;
using OpenTribes.Client.Effects;
using OpenTribes.Client.Scene;
using OpenTribes.Game;
using OpenTribes.World;

namespace OpenTribes.Client.Audio;

public sealed class SceneAudio
{
    public readonly SoundManager Sound;
    private readonly SceneModel M;
    private Db Db => M.Db;

    // $pref::playVoices (1.5): the stock default when unset is true.
    public bool PlayVoices => M.World.Players.PlayVoices();
    // Surface type (0..14) of a terrain material byte for the footstep tables (3.2); empty means type 0.
    public List<Material>? TerrainMaterials;
    // Surface type under a foot from the prediction container (terrain and interiors, FootSurface); -1 none. Null offline.
    public Func<double, double, double, int>? SurfaceType;

    // Statistics for the offline report: sounds started per source.
    public readonly Dictionary<string, int> Started = new();

    private sealed class GhostSounds
    {
        public int Looper;                         // jet, vehicle idle, static ambient, projectile
        public int Move, Whir, Run, Sustained;
        public readonly int[] SeqSound = new int[3];     // sequence sound handle per thread slot (3.3)
        public readonly int[] SeqPlaying = Enumerable.Repeat(-1, 3).ToArray();   // sequence the sound was started for
        public readonly ImageVoiceSlots Image = new();
        public readonly int[] Channel = new int[4];
        public int LastMode = -1, LastFire = -1, LastDamage = -1, LastSound = -1, LastState = -1;
        public double JetOffSince = -1;
        public double LastRotation = double.NaN, LastElevation = double.NaN, WhirUntil;
        public int DatablockStarted = -1;
        public bool Stopped;
    }

    private readonly Dictionary<int, GhostSounds> mGhosts = new();
    private readonly Dictionary<int, int> mVoiceByClient = new();
    private int mPrecip;
    public readonly SoundSequencer Sequencer;

    public SceneAudio(SceneModel m, SoundManager sound)
    {
        M = m;
        Sound = sound;
        Sequencer = new SoundSequencer(sound);
        M.World.Players.ChatVoice = ChatWave;
    }

    private void Count(string what, int handle)
    {
        if (handle != 0)
            Started[what] = Started.GetValueOrDefault(what) + 1;
    }

    private GhostSounds Gs(int ghost)
    {
        if (!mGhosts.TryGetValue(ghost, out GhostSounds? g))
            mGhosts[ghost] = g = new GhostSounds();
        return g;
    }

    private static SoundXf At((double x, double y, double z)? p) => p is var (x, y, z) ? new SoundXf(x, y, z) : new SoundXf(0, 0, 0);

    private int Play(string what, long sound, (double x, double y, double z)? at)
    {
        if (sound < 0 || at == null)
            return 0;
        int h = Sound.PlayDatablock(sound, At(at));
        Count(what, h);
        return h;
    }

    private void Follow(int handle, (double x, double y, double z)? at)
    {
        if (handle != 0 && at is var (x, y, z))
            Sound.SetPosition(handle, x, y, z);
    }

    private static long Idx(ClientDataBlock? b, string field) => b == null ? -1 : (long)Db.Num(b, field, -1);

    // ---- listener (2.6) ----

    public void SetListener(double x, double y, double z, double yawRad)
    {
        var (rx, ry, rz) = SoundGain.RightOfYaw(yawRad);
        Sound.SetListener(x, y, z, rx, ry, rz);
    }

    // ---- network events (3.1) ----

    public void OnPacket(DecodedPacket pkt)
    {
        foreach (DecodedEvent e in pkt.Events)
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
            }
        }
    }

    // LocSoundEvent: listener position + direction * (distance * profile maxDist), identity orientation, zero velocity.
    private void LocSound(long sound, double[]? dir, double dist)
    {
        if (!Sound.TryDatablock(sound, out _, out SoundProfile p, out _))
            return;
        Listener l = Sound.Listener;
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
        if (!M.Visuals.TryGetValue(ghost, out Visual? v) || channel is < 0 or > 3)
            return;
        GhostSounds g = Gs(ghost);
        Sound.Stop(g.Channel[channel]);
        g.Channel[channel] = Play("SoundEvent", sound, v.Position);
    }

    private bool HasWave(string name) => Sound.ReadWave?.Invoke(name) != null;

    // 1.5 name building: `<voiceBase>.w<name>.wav`, else `w<name>.wav`.
    private string? VoiceWave(int client, string text)
    {
        string a = $"{M.World.Players.Get(client)?.Voice}.w{text}.wav";
        if (HasWave(a))
            return a;
        string b = $"w{text}.wav";
        return HasWave(b) ? b : null;
    }

    private void Voice(int client, string text)
    {
        if (!PlayVoices)
            return;
        ClientPlayerRecord? rec = M.World.Players.Get(client);
        if (rec == null || rec.OwnedGhost < 0 || !M.Visuals.TryGetValue(rec.OwnedGhost, out Visual? v) || v.Position == null)
            return;
        string? wave = VoiceWave(client, text);
        if (wave == null || !ProfileZero(out SoundProfile p))
            return;
        if (mVoiceByClient.TryGetValue(client, out int old))
            Sound.Stop(old);
        int h = Sound.PlayWave(wave, p, 0, At(v.Position));
        mVoiceByClient[client] = h;
        Count("VoiceEvent", h);
    }

    private bool ProfileZero(out SoundProfile p)
    {
        p = SoundProfile.Default;
        if (Sound.DataBlocks == null || !Sound.DataBlocks.Blocks.TryGetValue((0, 0), out ClientDataBlock? b))
            return false;
        p = SoundManager.ProfileOf(b);
        return true;
    }

    // Chat text with ~w (1.5): a 2D wave, the sender's previous voice stopped first.
    private void ChatWave(int from, string msg)
    {
        int i = msg.IndexOf("~w", StringComparison.Ordinal);
        if (i < 0 || !PlayVoices)
            return;
        string text = msg[(i + 2)..];
        string? wave = M.World.Players.Get(from) != null ? VoiceWave(from, text) : text;
        if (wave == null)
            return;
        if (mVoiceByClient.TryGetValue(from, out int old))
            Sound.Stop(old);
        int h = Sound.PlayWave(wave);
        mVoiceByClient[from] = h;
        Count("ChatWave", h);
    }

    // Sfx::SoundEvent (kinds 0 and 2), priority 1.0.
    private void SfxEvent(int kind, string name, int profileId, double[]? pos)
    {
        if (!Sound.Profiles.TryGetValue(profileId, out SoundProfile p))
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
        int p = text.IndexOf(key, StringComparison.Ordinal);
        if (p < 0)
            return int.MinValue;
        p += key.Length;
        int e = p;
        while (e < text.Length && (char.IsDigit(text[e]) || text[e] == '-'))
            ++e;
        return int.TryParse(text.AsSpan(p, e - p), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : int.MinValue;
    }

    // ---- effects (3.4) ----

    public void OnExplosion(ClientDataBlock db, double x, double y, double z) =>
        Count("Explosion", Idx(db, "soundId") is long s && s >= 0 ? Sound.PlayDatablock(s, new SoundXf(x, y, z)) : 0);

    // ---- ghosts (3.2, 3.3, 3.4) ----

    public void OnDelete(int ghost)
    {
        if (!mGhosts.Remove(ghost, out GhostSounds? g))
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
        foreach (int h in g.Image.Handles)
            Sound.Stop(h);
        foreach (int h in g.SeqSound)
            Sound.Stop(h);
        Array.Clear(g.SeqSound);
        Array.Fill(g.SeqPlaying, -1);
        g.Looper = g.Move = g.Whir = g.Run = g.Sustained = 0;
        g.Image.Clear();
    }

    // Turret_activate 0x00455b34 / Turret_deactivate 0x00455cc4 (ghost branches): the power sound starts from the ghost's own tick, at the
    // thread position the pose model reports, and first stops the whir handle (+0x910). Datablock fields activationSound (+0x13c) and
    // deactivateSound (+0x140).
    public void TurretPower(ShapeVisual sv, bool on)
    {
        ClientDataBlock? b = Db.Block(19, DatablockIndex(sv));
        if (b == null)
            return;
        GhostSounds g = Gs(sv.Index);
        long sound = Idx(b, on ? "activationSound" : "deactivateSound");
        if (sound < 0)
            return;
        Sound.Stop(g.Whir);
        g.Whir = 0;
        Play("TurretPower", sound, sv.Position);
    }

    private int DatablockIndex(Visual v) => (int)Db.ToDouble(v.Ghost.State.GetValueOrDefault("datablock"), -1);

    public void OnGhost(Visual v, DecodedGhost u, bool isNew)
    {
        GhostSounds g = Gs(v.Index);
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        switch (v)
        {
            case ProjectileVisual pv:
                if (g.Looper == 0 && !pv.Exploded && !g.Stopped && pv.Position != null)
                {
                    int group = v.Class switch { "Bullet" => 14, "Grenade" => 15, "RocketDumb" => 16, "SeekingMissile" => 25, _ => 13 };
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
        }
    }

    // 3.2 weapon image sounds: the image state machine of the ghost (`ImageStateMachine`) names the datablock sound of each state it
    // enters (Activate, Fire, Ready, Reload, SpinUp, SpinDown). A still-playing looping image sound stops when the state changes.
    public void ImageStateChanged(PlayerVisual pl, int slot)
    {
        if (slot is < 0 or > 7)
            return;
        Sound.Stop(Gs(pl.Index).Image.StateChanged(slot));
    }

    // The image of a slot is replaced or removed: the stored voice stops, looping or not [code 0041a64c Player_resetImage].
    public void ImageReset(PlayerVisual pl, int slot)
    {
        if (slot is < 0 or > 7)
            return;
        Sound.Stop(Gs(pl.Index).Image.Reset(slot));
    }

    public void ImageSound(PlayerVisual pl, int slot, ClientDataBlock? image, string field)
    {
        if (slot is < 0 or > 7)
            return;
        GhostSounds g = Gs(pl.Index);
        int h = Play("Image" + field[3..], Idx(image, field), pl.Position);
        g.Image.Started(slot, h, h != 0 && Sound.IsLooping(h));
    }

    // 3.2 animation sound: a one shot at the player position when a ghost starts an animation whose PlayerData entry has a sound.
    public void AnimationSound(PlayerVisual p, long sound)
    {
        Sound.TryDatablock(sound, out string wave, out _, out _);
        Play("Animation " + wave, sound, p.Position);
    }

    // Footstep trigger (3.2): right foot 1, left foot 2, on the ground. The surface type indexes the datablock's 15 pairs.
    public void Footstep(PlayerVisual p, bool right, double x, double y, double z)
    {
        ClientDataBlock? pd = Db.Block(3, (int)Db.ToDouble(p.Ghost.State.GetValueOrDefault("datablock"), -1));
        int m = SurfaceType?.Invoke(x, y, z) ?? -1;
        if (m < 0 && M.Terrain != null)
        {
            m = 0;
            float h = 0;
            var n = new Vec3(0, 0, 1);
            int mat = 0;
            if (M.Terrain.Surface((float)x, (float)y, ref h, ref n, ref mat) && TerrainMaterials != null && mat >= 0 && mat < TerrainMaterials.Count)
                m = Math.Clamp(TerrainMaterials[mat].Type, 0, 14);
        }
        if (m < 0)
            m = 0;
        if (Db.Get(pd, $"footSounds[{m}]") is object[] pair && pair.Length >= 2)
            Play("Footstep", (long)Db.ToDouble(pair[right ? 0 : 1], -1), (x, y, z));
    }

    private void ShapeGhost(ShapeVisual sv, GhostSounds g, DecodedGhost u, Func<string, bool> has)
    {
        int idx = DatablockIndex(sv);
        int group = sv.Class switch { "StaticShape" => 4, "Turret" => 19, "Sensor" => 8, "Moveable" => 7, "Vehicle" => 9, "Flier" => 10, _ => -1 };
        if (group < 0)
            return;
        ClientDataBlock? b = Db.Block(group, idx);
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
                        int ss = (int)Num("f8c4");
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
                        int fc = (int)Num("f948");
                        if (g.LastFire >= 0 && fc != g.LastFire)
                            Play("VehicleFire", Idx(b, "fireSound"), pos);
                        g.LastFire = fc;
                    }
                    if (has("i6"))
                    {
                        int dl = (int)Num("i6");
                        if (g.LastDamage >= 0 && dl > g.LastDamage)
                            Play("VehicleDamage", Idx(b, "damageSound"), pos);
                        g.LastDamage = dl;
                    }
                    break;
                }
            case "Turret":
                {
                    bool sustained = Db.Flag(b, "isSustained");
                    if (has("fireState"))
                    {
                        int fs = (int)Num("fireState");
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
                            g.WhirUntil = M.Time + 0.3;
                            if (g.Whir == 0)
                                g.Whir = Play("TurretWhir", Idx(b, "whirSound"), pos);
                        }
                        g.LastRotation = r;
                        g.LastElevation = e;
                    }
                    AmbientAndState(sv, g, b, pos, has);
                    break;
                }
            case "Moveable":
                {
                    if (has("state"))
                    {
                        int st = (int)Num("state");
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
                    AmbientAndState(sv, g, b, pos, has);
                    break;
                }
            default:
                AmbientAndState(sv, g, b, pos, has);
                break;
        }
    }

    // 3.3 sequenceSound: a running thread whose sequence name is in the datablock's pairs plays that sound at the object; the sound stops
    // when the thread stops or pauses, reaches the end, or changes sequence.
    private void SequenceSounds(ShapeVisual sv, GhostSounds g, ClientDataBlock b, (double, double, double)? pos, object? threads)
    {
        foreach (ThreadSlot s in ThreadSlot.ParseAll(threads))
        {
            if (s.Slot is < 0 or > 2)
                continue;
            bool running = s.State == 0 && !s.AtEnd && s.Sequence >= 0;
            if (!running || g.SeqPlaying[s.Slot] != s.Sequence)
            {
                Sound.Stop(g.SeqSound[s.Slot]);
                g.SeqSound[s.Slot] = 0;
                g.SeqPlaying[s.Slot] = -1;
            }
            if (!running || g.SeqPlaying[s.Slot] == s.Sequence)
                continue;
            g.SeqPlaying[s.Slot] = s.Sequence;
            string name = sv.SequenceName(s.Sequence);
            for (int i = 0; i < 16; ++i)
                if (Db.Get(b, $"sequenceSound[{i}]") is object[] pair && pair.Length >= 2 && pair[0] is string n
                    && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    g.SeqSound[s.Slot] = Play("SequenceSound " + name, (long)Db.ToDouble(pair[1], -1), pos);
                    break;
                }
        }
    }

    // StaticBase ambient sound (3.3): starts on a ghost with a datablock, stops when the damage state becomes 2 or 3.
    private void AmbientAndState(ShapeVisual sv, GhostSounds g, ClientDataBlock b, (double, double, double)? pos, Func<string, bool> has)
    {
        int ds = (int)Db.ToDouble(sv.Ghost.State.GetValueOrDefault("damageState"), 1);
        if (ds >= 2)
        {
            Sound.Stop(g.Looper);
            g.Looper = 0;
            g.Stopped = true;
        }
        else if (g.Looper == 0 && !g.Stopped && pos != null)
            g.Looper = Play("Ambient", Idx(b, "sfxAmbient"), pos);
    }

    // ---- per frame ----

    public void Frame(double dt)
    {
        foreach (var (index, g) in mGhosts)
        {
            if (!M.Visuals.TryGetValue(index, out Visual? v))
                continue;
            var pos = v.Position;
            switch (v)
            {
                case PlayerVisual pl:
                    {
                        // Jet (3.2): looping while jetting and not mounted; stops when jetting ends.
                        bool jet = pl.Jetting && pl.MountGhost < 0;
                        if (jet && g.Looper == 0)
                        {
                            ClientDataBlock? pd = Db.Block(3, DatablockIndex(v));
                            g.Looper = Play("Jet", Idx(pd, "jetSound"), pos);
                        }
                        else if (jet)
                            g.JetOffSince = -1;
                        else if (g.Looper != 0)
                        {
                            // The animation based jetting flag flickers between movement choices (65 ms); stop after 0.2 s without it.
                            if (g.JetOffSince < 0)
                                g.JetOffSince = M.Time;
                            if (M.Time - g.JetOffSince >= 0.2)
                            {
                                Sound.Stop(g.Looper);
                                g.Looper = 0;
                                g.JetOffSince = -1;
                            }
                        }
                        Follow(g.Looper, pos);
                        foreach (int h in g.Image.Handles)
                            Follow(h, pos);
                        break;
                    }
                default:
                    Follow(g.Looper, pos);
                    Follow(g.Move, pos);
                    Follow(g.Run, pos);
                    Follow(g.Sustained, pos);
                    Follow(g.Whir, pos);
                    if (g.Whir != 0 && M.Time > g.WhirUntil)
                    {
                        Sound.Stop(g.Whir);
                        g.Whir = 0;
                    }
                    break;
            }
        }
        // Snow and rain (3.4): a 2D looping pair sound while precipitation is on.
        bool precip = M.HasWeather && WeatherEffect.Draws(M.WeatherPrecipitation, M.Weather.Suspended, M.Weather.Intensity);
        if (precip && mPrecip == 0)
        {
            mPrecip = Sound.PlayPair(M.Weather.Rain ? 450046 : 450045);
            Count("Precipitation", mPrecip);
        }
        else if (!precip && mPrecip != 0)
        {
            Sound.Stop(mPrecip);
            mPrecip = 0;
        }
        Sequencer.Tick();
    }
}
