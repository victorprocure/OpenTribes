// Port of src/game/command_map.hpp and src/game/command_map.cpp: commander commands and positional sound events
// (docs/script.md 6.8, docs/player-manager.md 10): issueCommand, issueCommandI, issueTargCommand, setCommandStatus,
// remoteSelectCommander, playSound, GameBase::playSound and playVoice.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

/// <summary>Routes sounds to clients: what the network layer adds to the command map.</summary>
/// <remarks>Both hooks are optional. Without a network no sound reaches anyone.</remarks>
public static class SoundRoute
{
    /// <summary>Returns the ghost index of an object on a client's connection, or -1 when it is not ghosted.</summary>
    public static Func<int, SimObject, int>? GhostIndex;
    /// <summary>Returns the scope object of a client. The default is the control object.</summary>
    public static Func<int, SimObject?>? ScopeObject;
}

/// <summary>The command map events and script commands.</summary>
public static class CommandMap
{
    // src/game/command_map.cpp putUnit: BitStream::writeFloat (0x00582fb0) into an event.
    private static void PutUnit(EventWriter w, float v, uint bits)
    {
        double x = Mul((double)(int)((1u << (int)bits) - 1), v);
        long i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        w.PutInt((uint)i & ((1u << (int)bits) - 1), bits);
    }

    // src/game/command_map.cpp putSignedUnit: BitStream::writeSignedFloat (0x00583018) into an event.
    private static void PutSignedUnit(EventWriter w, float v, uint bits)
    {
        double x = Mul(Mul(Add(v, 1.0f), 0.5f), (double)(int)((1u << (int)bits) - 1));
        long i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        w.PutInt((uint)i & ((1u << (int)bits) - 1), bits);
    }

    // src/game/command_map.cpp internText: the command texts go through the string table with its case-sensitive flag
    // (0x00584760 with cl = 1): the same spelling is the same reference.
    private static string InternText(string s) => string.Intern(s);

    // src/game/command_map.cpp serverObject.
    private static SimObject? ServerObject(GameState game, int id) => id != 0 ? game.Sim?.Server()?.FindById(id) : null;

    // src/game/command_map.cpp scopeOf: the client's scope object: the connection's (ghost manager) when the network layer
    // knows it, else the control object.
    private static GameBase? ScopeOf(GameState game, int client)
    {
        SimObject? o = null;
        if (SoundRoute.ScopeObject != null)
            o = SoundRoute.ScopeObject(client);
        else if (game.Players.FindHuman(client) is ClientRecord r)
            o = ServerObject(game, r.ControlObject);
        return o as GameBase;
    }

    // src/game/command_map.cpp distance3.
    private static float Distance3(ReadOnlySpan<float> d) => Store(Sqrt(Add(Add(Mul(d[0], d[0]), Mul(d[1], d[1])), Mul(d[2], d[2]))));

    // src/game/command_map.cpp soundProfile: SoundData block of a name (group 1) and its profile's two distances: the
    // maximum (+0x5c) and the audible range (+0x60 = min + (max - min) * 0.65, computed by 0x00485100).
    private static bool SoundProfile(GameState game, string name, out int index, out float maxDist, out float range)
    {
        index = 0;
        maxDist = range = 0;
        if (game.Sim == null)
            return false;
        DataBlockManager db = game.Sim.DataBlocks();
        DataBlockManager.Block? sound = db.Find(name);
        if (sound == null || sound.Group != 1)
            return false;
        index = sound.Index;
        if (!db.GetField(sound, "profile", "", out string profileName) || profileName.Length == 0)
            return false;
        DataBlockManager.Block? profile = db.Find(profileName);
        if (profile == null || profile.Group != 0)
            return false;
        if (!db.GetField(profile, "minDistance", "", out string mn) || !db.GetField(profile, "maxDistance", "", out string mx))
            return false;
        float lo = Store(Numbers.BorlandAtof(mn)), hi = Store(Numbers.BorlandAtof(mx));
        maxDist = hi;
        range = Store(Add(Mul(Sub(hi, lo), 0.65), lo));
        return true;
    }

    // src/game/command_map.cpp issueCommand: 0x0040c828.
    private static void IssueCommand(GameState game, Interpreter interp, int issuer, int commanded, int icon, string text, int[] pair, int target)
    {
        PlayerManager pm = game.Players;
        if (game.Sim?.Server() == null)
            return; // isServer
        if (target < -1 || target > 255)
            return;
        ClientRecord? issuerRec = pm.FindHuman(issuer);
        ClientRecord? cmd = pm.Find(commanded);
        ++pm.CommandCounter();
        if (issuer != 0)
        {
            if (issuerRec == null || cmd == null || issuerRec.Team != cmd.Team)
                return;
        }
        if (issuer != commanded && issuer != 0)
        {
            // 0x0040cad4: is the issuer on the commander chain above the commanded client?
            bool above = false;
            for (ClientRecord? r = cmd; r != null;)
            {
                if (r.Commander == issuer)
                {
                    above = true;
                    break;
                }
                r = pm.FindHuman(r.Commander);
            }
            if (!above)
            {
                if (cmd!.Commander != -1)
                    return;
                if (cmd.CommandIcon != -1 && cmd.CommandIssuer == commanded)
                    return;
            }
        }
        if (cmd == null)
            return; // issuer 0 with an unknown client: the original dereferences null (D11)
        cmd.CommandStamp = pm.CommandCounter();
        cmd.CommandIssuer = issuer;
        cmd.CommandIcon = icon;
        cmd.CommandText = text;
        cmd.CommandStatus = (issuer == 0 || issuer == commanded) ? 2 : 1;
        cmd.WaypointX = pair[0];
        cmd.WaypointY = pair[1];
        cmd.CommandTarget = target;
        if (cmd.IsAI)
        {
            if (target != -1)
                return;
            // AI::onCommand(name, issuer, icon, "x y", target, text, status, stamp) as a script call.
            string[] args =
            {
                cmd.Name, Numbers.FormatInt(cmd.CommandIssuer), Numbers.FormatInt(cmd.CommandIcon),
                Numbers.FormatInt(cmd.WaypointX) + " " + Numbers.FormatInt(cmd.WaypointY), Numbers.FormatInt(cmd.CommandTarget),
                cmd.CommandText ?? "(null)", Numbers.FormatInt(cmd.CommandStatus), Numbers.FormatInt((int)cmd.CommandStamp),
            };
            interp.CallFunction("AI::onCommand", args);
        }
        if (issuerRec == null)
            return; // issuer 0: the original dereferences null in the delivery loop (D11)
        foreach (int id in pm.Humans())
        {
            ClientRecord? h = pm.Find(id);
            if (h == null)
                continue;
            if (h.GuiMirror == 2)
                h.CommandMark = pm.CommandCounter();
            if (h.Team != issuerRec.Team || !(h.GuiMirror == 2 || h == cmd))
                continue;
            string? send = (h.Id == commanded || h.Id == issuer) ? cmd.CommandText : null;
            bool isNew = send != null && !ReferenceEquals(h.LastCommandText, send);
            if (isNew)
                h.LastCommandText = send;
            pm.SendEvent(h.Id, MakeCommandEvent(commanded, issuer, cmd.CommandStatus, false, cmd.CommandIcon, cmd.CommandTarget,
                                                pair[0], pair[1], send != null, isNew, send));
        }
    }

    // src/game/command_map.cpp selectCommander: 0x0040cb50 remoteSelectCommander.
    private static void SelectCommander(GameState game, int client, int commander)
    {
        PlayerManager pm = game.Players;
        if (game.Sim?.Server() == null)
            return;
        // 0x0040b46c: a commander that is not a human in the list is dropped first.
        for (int id = 2048; id < 2048 + 128; ++id)
            if (pm.Find(id) is ClientRecord r && (r.Commander == 0 || pm.FindHuman(r.Commander) == null))
                r.Commander = -1;
        if (commander == client)
            return;
        ClientRecord? rec = pm.Find(client);
        ClientRecord? cmdr = commander >= 0 ? pm.FindHuman(commander) : null;
        if (rec == null)
            return;
        if (cmdr != null && cmdr.Team != rec.Team)
            return;
        if (cmdr == rec)
            return;
        ClientRecord? current = pm.FindHuman(rec.Commander);
        if (cmdr == current)
            return;
        int old = rec.Commander;
        // 0x0040cb0c: no cycle through the new commander's chain.
        if (cmdr == null)
        {
            rec.Commander = -1;
        }
        else
        {
            ClientRecord? r = cmdr;
            while (r != null && rec.Id != r.Commander)
                r = pm.FindHuman(r.Commander);
            if (r == null)
                rec.Commander = cmdr.Id;
        }
        if (rec.Commander == old)
            return;
        foreach (int id in pm.Humans())
            pm.SendEvent(id, MakeSelectCommanderEvent(client, commander));
    }

    // src/game/command_map.cpp setCommandStatus: 0x0040cbf0.
    private static bool SetCommandStatus(GameState game, int client, int status, string text)
    {
        PlayerManager pm = game.Players;
        if (game.Sim?.Server() == null)
            return false;
        ClientRecord? rec = pm.FindHuman(client);
        if (rec == null)
            return false;
        ++pm.CommandCounter();
        if (!(rec.CommandStatus == 1 || (rec.CommandStatus == 2 && status == 0)))
            return false;
        rec.CommandStamp = pm.CommandCounter();
        rec.CommandStatus = status != 0 ? 2 : 0;
        int oldIssuer = rec.CommandIssuer;
        if (status == 0)
        {
            rec.CommandIcon = -1;
            rec.CommandIssuer = -1;
        }
        foreach (int id in pm.Humans())
        {
            ClientRecord? h = pm.Find(id);
            if (h == null)
                continue;
            if (h.GuiMirror == 2)
                h.CommandMark = pm.CommandCounter();
            if (h.Team != rec.Team || !(h.GuiMirror == 2 || h == rec))
                continue;
            string? send = (h == rec || h.Id == oldIssuer) ? text : null;
            bool isNew = false;
            if (send != null && !ReferenceEquals(h.LastCommandText, send))
            {
                isNew = true;
                h.LastCommandText = send;
            }
            pm.SendEvent(h.Id, MakeCommandEvent(client, -1, rec.CommandStatus, true, -1, -1, 0, 0, send != null, isNew, send));
        }
        return true;
    }

    /// <summary>Builds a PlayerCommandEvent.</summary>
    /// <param name="commanded">The id of the commanded player.</param>
    /// <param name="issuer">The id of the issuing player.</param>
    /// <param name="status">The command status.</param>
    /// <param name="ack">True for an acknowledge event, which carries no command part.</param>
    /// <param name="icon">The command icon.</param>
    /// <param name="target">The target id.</param>
    /// <param name="x">The waypoint x.</param>
    /// <param name="y">The waypoint y.</param>
    /// <param name="hasText">True when the event carries text.</param>
    /// <param name="newText">False when the text was already sent to this recipient (hasText 1, new 0).</param>
    /// <param name="text">The command text, or null for none.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows PlayerCommandEvent (0x455, 0x0040a5a8).</remarks>
    public static OutEvent MakeCommandEvent(int commanded, int issuer, int status, bool ack, int icon, int target, int x, int y,
                                            bool hasText, bool newText, string? text)
    {
        var w = new EventWriter();
        w.PutInt((uint)(commanded - 2048), 10);
        w.PutInt((uint)status, 2);
        w.PutFlag(ack);
        if (!ack)
        {
            w.PutFlag(icon != -1);
            if (icon != -1)
            {
                w.PutInt((uint)(issuer - 2048), 10);
                w.PutInt((uint)icon, 6);
                w.PutFlag(target != -1);
                if (target != -1)
                    w.PutInt((uint)target, 8);
                w.PutInt((uint)x, 10);
                w.PutInt((uint)y, 10);
            }
        }
        w.PutFlag(hasText);
        if (hasText)
        {
            w.PutFlag(newText);
            if (newText)
                w.PutString(text ?? "");
        }
        return w.Finish(0x455);
    }

    /// <summary>Builds a PlayerSelectCmdrEvent.</summary>
    /// <param name="client">The client id.</param>
    /// <param name="commander">The commander id, written raw. 0 means absent.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows PlayerSelectCmdrEvent (0x456, 0x0040a29c).</remarks>
    public static OutEvent MakeSelectCommanderEvent(int client, int commander)
    {
        var w = new EventWriter();
        w.PutInt((uint)(client - 2048), 7);
        w.PutFlag(commander != 0);
        if (commander != 0)
            w.PutInt((uint)(commander - 2048), 7);
        return w.Finish(0x456);
    }

    /// <summary>Builds a VoiceEvent.</summary>
    /// <param name="client">The client id.</param>
    /// <param name="wav">The wav name, cut to 7 characters.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows VoiceEvent (0x45f, 0x0040adf8).</remarks>
    public static OutEvent MakeVoiceEvent(int client, string wav)
    {
        var w = new EventWriter();
        w.PutInt((uint)(client - 2048), 7);
        w.PutString(wav.Length > 7 ? wav[..7] : wav);
        return w.Finish(0x45f, true, false);
    }

    /// <summary>Builds a SoundEvent, guaranteed unordered.</summary>
    /// <param name="ghostIndex">The ghost index of the source object.</param>
    /// <param name="channel">The sound channel.</param>
    /// <param name="sound">The sound id.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows SoundEvent (0x460, 0x0040aec0).</remarks>
    public static OutEvent MakeSoundEvent(int ghostIndex, int channel, int sound)
    {
        var w = new EventWriter();
        w.PutInt((uint)ghostIndex, 10);
        w.PutInt((uint)channel, 2);
        w.PutInt((uint)(sound + 1), 8);
        return w.Finish(0x460, true, false);
    }

    /// <summary>Builds a LocSoundEvent.</summary>
    /// <param name="sound">The sound id.</param>
    /// <param name="delta">The offset of the sound from the listener.</param>
    /// <param name="dist">The sound distance.</param>
    /// <param name="maxDist">The maximum distance.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows LocSoundEvent (0x45c, 0x00485714).</remarks>
    public static OutEvent MakeLocSoundEvent(int sound, ReadOnlySpan<float> delta, float dist, float maxDist)
    {
        var w = new EventWriter();
        w.PutInt((uint)(sound + 1), 8);
        bool far = dist > 0.05;
        w.PutFlag(far);
        if (far)
        {
            float inv = Store(Div(1.0, dist));
            Span<float> v = stackalloc float[3];
            for (int i = 0; i < 3; ++i)
                v[i] = Store(Mul(inv, delta[i]));
            PutSignedUnit(w, v[0], 8);
            PutSignedUnit(w, v[1], 8);
            w.PutFlag(v[2] < 0.0f);
            PutUnit(w, Store(Div(dist, maxDist)), 9);
        }
        return w.Finish(0x45c, false, false);
    }

    /// <summary>Registers the command map commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        // The plugin commands 10-14 return NULL (False) on every path.
        interp.AddCommand("issueCommand", (inr, a) =>
        {
            if (a.Length != 7)
                return "False";
            float[] area = new float[4];
            game.Players.MissionArea(area);
            int[] pair = new int[2];
            for (int i = 0; i < 2; ++i)
            {
                // (coordinate - origin) * 1023 / extent, truncated by the float to int helper.
                double v = Div(Mul(Sub(Store(Numbers.BorlandAtof(a[5 + i])), area[i]), 1023.0f), area[2 + i]);
                pair[i] = Numbers.FloatToInt(v);
            }
            IssueCommand(game, inr, Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]), Numbers.BorlandAtoi(a[3]), InternText(a[4]), pair, -1);
            return "False";
        });
        interp.AddCommand("issueCommandI", (inr, a) =>
        {
            if (a.Length != 7)
                return "False";
            int[] pair = { Numbers.BorlandAtoi(a[5]), Numbers.BorlandAtoi(a[6]) };
            IssueCommand(game, inr, Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]), Numbers.BorlandAtoi(a[3]), InternText(a[4]), pair, -1);
            return "False";
        });
        interp.AddCommand("issueTargCommand", (inr, a) =>
        {
            if (a.Length != 6)
                return "False";
            int[] pair = { 0, 0 };
            IssueCommand(game, inr, Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]), Numbers.BorlandAtoi(a[3]), InternText(a[4]), pair,
                         Numbers.BorlandAtoi(a[5]));
            return "False";
        });
        interp.AddCommand("remoteSelectCommander", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "RemoteSelectCommander with " + Numbers.FormatInt(a.Length) + " args.");
                return "False";
            }
            SelectCommander(game, Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]));
            return "False";
        });
        interp.AddCommand("setCommandStatus", (_, a) =>
        {
            if (a.Length != 4)
                return "False";
            return SetCommandStatus(game, Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]), InternText(a[3])) ? "True" : "False";
        });

        // playVoice(client, wav) 0x00421dbc: VoiceEvent to every human whose scope object is within the maximum distance
        // of SoundProfileData #0 of the speaker's owned object.
        interp.AddCommand("playVoice", (_, a) =>
        {
            if (game.Sim?.Server() == null || a.Length != 3)
                return "False";
            int speaker = Numbers.BorlandAtoi(a[1]);
            ClientRecord? rec = game.Players.FindHuman(speaker);
            if (rec == null || rec.OwnedObject == 0)
                return "True";
            GameBase? owned = ServerObject(game, rec.OwnedObject) as GameBase;
            DataBlockManager.Block? profile = game.Sim.DataBlocks().At(0, 0);
            if (owned == null || profile == null || !game.Sim.DataBlocks().GetField(profile, "maxDistance", "", out string mx))
                return "True";
            float maxDist = Store(Numbers.BorlandAtof(mx));
            float[] d = new float[3];
            foreach (int id in game.Players.Humans())
            {
                GameBase? scope = ScopeOf(game, id);
                if (scope == null)
                    continue;
                for (int i = 0; i < 3; ++i)
                    d[i] = Store(Sub(scope.Transform.P[i], owned.Transform.P[i]));
                if (Distance3(d) < maxDist)
                    game.Players.SendEvent(id, MakeVoiceEvent(speaker, a[2]));
            }
            return "True";
        });

        // playSound(sound, "x y z") 0x00421ca0: LocSoundEvent to every client in range.
        interp.AddCommand("playSound", (_, a) =>
        {
            if (game.Sim?.Server() == null || a.Length != 3)
                return "False";
            if (!SoundProfile(game, a[1], out int index, out float maxDist, out float range))
                return "False";
            float[] pos = { 0, 0, 0 };
            CScan.Floats(a[2], pos);
            float[] d = new float[3];
            foreach (int id in game.Players.Humans())
            {
                GameBase? scope = ScopeOf(game, id);
                if (scope == null)
                    continue;
                for (int i = 0; i < 3; ++i)
                    d[i] = Store(Sub(scope.Transform.P[i], pos[i]));
                float dist = Distance3(d);
                if (dist < range)
                    game.Players.SendEvent(id, MakeLocSoundEvent(index, d, dist, maxDist));
            }
            return "True";
        });

        // GameBase::playSound(object, sound, channel) 0x00421d34: SoundEvent through the ghost of the object on every
        // connection that has it and whose scope object is in range.
        interp.AddCommand("GameBase::playSound", (inr, a) =>
        {
            if (game.Sim?.Server() == null || a.Length != 4)
                return "False";
            GameBase? gb = game.Players.FindServerObject(a[1]);
            if (gb == null)
                return "False";
            if (!SoundProfile(game, a[2], out int index, out _, out float range))
                return "False";
            int channel = Numbers.BorlandAtoi(a[3]);
            float[] d = new float[3];
            foreach (int id in game.Players.Humans())
            {
                int ghost = SoundRoute.GhostIndex != null ? SoundRoute.GhostIndex(id, gb) : -1;
                GameBase? scope = ScopeOf(game, id);
                if (ghost < 0 || scope == null)
                    continue;
                for (int i = 0; i < 3; ++i)
                    d[i] = Store(Sub(scope.Transform.P[i], gb.Transform.P[i]));
                if (Distance3(d) < range)
                    game.Players.SendEvent(id, MakeSoundEvent(ghost, channel, index));
            }
            return "True";
        });
    }
}
