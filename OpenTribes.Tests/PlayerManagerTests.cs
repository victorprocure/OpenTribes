// Port of tests/test_player_manager.cpp: PlayerManager (docs/player-manager.md): records, lists, game events and script
// commands. 103 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;

public class PlayerManagerTests
{
    private sealed record Sent(int To, OutEvent E);

    // Teams -1 (unnamed), 0 (Blood Eagle / beagle), 1 (Diamond Sword / dsword). No sim, no network.
    private sealed class Fx
    {
        public StringTable Strings { get; } = new();
        public Interpreter In { get; }
        public string Out { get; set; } = "";
        public GameState Game { get; } = new();
        public List<Sent> SentEvents { get; } = [];

        public Fx()
        {
            In = new Interpreter(Strings);
            In.SetOutput(l => Out += (Out.Length == 0 ? "" : "\n") + l);
            Game.Console = In;
            GameCommands.Register(In, Game);
            Game.Players.Send = (id, e) => SentEvents.Add(new Sent(id, e));
            In.SetVariable("$Server::teamName0", "Blood Eagle");
            In.SetVariable("$Server::teamSkin0", "beagle");
            In.SetVariable("$Server::teamName1", "Diamond Sword");
            In.SetVariable("$Server::teamSkin1", "dsword");
            Run("resetPlayerManager();");
            Game.AddTeam(0);
            Game.AddTeam(1);
            Assert.Equal(2, Game.TeamCount);
        }

        public string Run(string s)
        {
            Out = "";
            In.Evaluate(s);
            return Out;
        }

        public PlayerManager Pm() => Game.Players;
        public ClientRecord? Add(int id, string name = "P", bool male = true) => Pm().AddClient(id, name, "male1", male);
        public void Finished(int id) => Assert.Equal("True", Run("echo(Client::setDataFinished(" + id + "));"));

        // (recipient, tag) pairs of the captured events, then clears them.
        public List<(int, uint)> Take()
        {
            var r = new List<(int, uint)>();
            foreach (var s in SentEvents)
                r.Add((s.To, s.E.Tag));
            SentEvents.Clear();
            return r;
        }
    }

    private static bool TagsEq(List<(int, uint)> a, params (int, uint)[] b) => a.SequenceEqual(b);

    // First field of the events in `sent`: TeamAdd team, PlayerAdd id, others their first int.
    private static int Key(OutEvent e)
    {
        var b = new Bits(e);
        switch (e.Tag)
        {
            case 0x44f:
                b.U(32);
                return b.S32();
            case 0x44b:
                return b.S32();
            case 0x455:
                return (int)b.U(10) + 2048;
            default:
                return (int)b.U(7) + 2048;
        }
    }

    private static List<(int, int)> Keys(List<Sent> sent, uint tag)
    {
        var r = new List<(int, int)>();
        foreach (var s in sent)
            if (s.E.Tag == tag)
                r.Add((s.To, Key(s.E)));
        return r;
    }

    // DeltaScoreEvent (7): (isTeam, who, sort, text) with who = team or client id.
    private readonly record struct Delta(bool Team, int Who, int Sort, string Text);

    private static Delta DeltaOf(OutEvent e)
    {
        Assert.Equal(0x44d, e.Tag);
        var b = new Bits(e);
        var team = b.Flag();
        var who = team ? (int)b.U(4) - 1 : (int)b.U(7) + 2048;
        var sort = b.S32();
        var text = b.Str();
        Assert.True(b.Done());
        return new Delta(team, who, sort, text);
    }

    private static List<Delta> DeltasTo(List<Sent> sent, int to)
    {
        var r = new List<Delta>();
        foreach (var s in sent)
            if (s.To == to && s.E.Tag == 0x44d)
                r.Add(DeltaOf(s.E));
        return r;
    }

    private static int DeltaCount(List<Sent> sent) => sent.Count(s => s.E.Tag == 0x44d);

    private static void IdsAndLists()
    {
        var fx = new Fx();
        // 1.1: record 0 (2048) is never handed out; 2049..2175 are.
        Assert.True(fx.Add(2048) == null && fx.Add(2176) == null);
        Assert.True(fx.Add(2049) != null && fx.Add(2050) != null && fx.Add(2051) != null);
        Assert.Null(fx.Add(2050)); // in use
        // 3: lists insert at the head: getFirst is the newest human, getNext walks to older ones.
        Assert.Equal("3", fx.Run("echo(getNumClients());"));
        Assert.Equal("2051", fx.Run("echo(Client::getFirst());"));
        Assert.Equal("2050 2049 -1", fx.Run("echo(Client::getNext(2051), \" \", Client::getNext(2050), \" \", Client::getNext(2049));"));
        Assert.Equal("2051 2049 0 0", fx.Run("echo(getClientByIndex(0), \" \", getClientByIndex(2), \" \", getClientByIndex(3), \" \", " +
                           "getClientByIndex(-1));"));
        // 4.2: a removed id leaves the list and can be added again with a fresh record (2.1).
        fx.Run("Client::setSkin(2050, beagle);");
        fx.Pm().RemoveClient(2050);
        Assert.Equal("2049 2 ", fx.Run("echo(Client::getNext(2051), \" \", getNumClients(), \" \", Client::getName(2050));"));
        var r = fx.Add(2050, "Again", false);
        Assert.True(r != null && r.Skin == "base" && r.Team == -1 && r.Commander == -1 && r.JoinOrder == 3);
        Assert.Equal("2050 Female", fx.Run("echo(Client::getFirst(), \" \", Client::getGender(2050));"));
    }

    private static void Connect()
    {
        // 4.1 steps 3 and 5: TeamAdd -1..n-1 to the new client; for every older human H (newest first) the new record
        // to H, then H's record to the new client; then its own record.
        var fx = new Fx();
        fx.Add(2049, "Pilot");
        fx.Add(2050);
        fx.Add(2051);
        Assert.True(Keys(fx.SentEvents, 0x44f).SequenceEqual(new[] { (2049, -1), (2049, 0), (2049, 1), (2050, -1), (2050, 0),
                                                                      (2050, 1), (2051, -1), (2051, 0), (2051, 1) }));
        Assert.True(Keys(fx.SentEvents, 0x44b).SequenceEqual(new[] { (2049, 2049),
                                                                      (2049, 2050), (2050, 2049), (2050, 2050),
                                                                      (2050, 2051), (2051, 2050), (2049, 2051), (2051, 2049),
                                                                      (2051, 2051) }));
        // Each connect posts its TeamAdds before any PlayerAdd.
        Assert.True(fx.SentEvents[3].E.Tag == 0x44b && fx.SentEvents[4].E.Tag == 0x44f);
        // TeamAdd body of team 0: +0x24 (0), team, name, skin (2.5, 2.2).
        {
            var b = new Bits(fx.SentEvents[1].E);
            Assert.True(b.U(32) == 0 && b.S32() == 0 && b.Str() == "Blood Eagle" && b.Str() == "beagle" && b.Done());
            var m = new Bits(fx.SentEvents[0].E);
            Assert.True(m.U(32) == 0 && m.S32() == -1 && m.Str() == "unnamed" && m.Str() == "base" && m.Done());
        }
        // PlayerAdd body (11): id, isAI, name, skin, voice, male, commander -1, no team.
        {
            var b = new Bits(fx.SentEvents[3].E);
            Assert.True(b.U(32) == 2049 && !b.Flag() && b.Str() == "Pilot" && b.Str() == "base" && b.Str() == "male1");
            Assert.True(b.U(1) == 1 && b.S32() == -1 && !b.Flag() && b.Done());
        }
    }

    private static void Disconnect()
    {
        // 4.2: onClientDisconnect runs before the drop message; subordinates inherit the commander; PlayerRemove
        // (int(7) id - 2048) to every remaining human, newest first.
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Add(2051);
        fx.Run("function Server::onClientDisconnect(%c) { echo(\"disc \", %c); }");
        fx.Pm().Find(2049)!.Commander = 2050;
        fx.Pm().Find(2050)!.Commander = 2051;
        fx.SentEvents.Clear();
        fx.Out = "";
        fx.Pm().RemoveClient(2050);
        var disc = fx.Out.IndexOf("disc 2050", StringComparison.Ordinal);
        var drop = fx.Out.IndexOf("dropped - id = 2050", StringComparison.Ordinal);
        Assert.True(disc >= 0 && drop >= 0 && disc < drop);
        Assert.Equal(2051, fx.Pm().Find(2049)!.Commander);
        Assert.True(Keys(fx.SentEvents, 0x44c).SequenceEqual(new[] { (2051, 2050), (2049, 2050) }) && fx.SentEvents.Count == 2);
        var b = new Bits(fx.SentEvents[0].E);
        Assert.True(b.U(7) == 2 && b.Done());
    }

    private static void TeamChange()
    {
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Add(2051);
        fx.SentEvents.Clear();
        // 5: PlayerTeamChange (int(7) id - 2048, int(4) team + 1) to every human, newest first; the team skin without a
        // PlayerSkinEvent; H whose commander was the client takes its commander; then commander -1. 12.3:
        // GameBase::setTeam on a client returns -1.
        fx.Pm().Find(2050)!.Commander = 2049;
        fx.Pm().Find(2049)!.Commander = 2051;
        Assert.Equal("-1", fx.Run("echo(GameBase::setTeam(2049, 0));"));
        Assert.True(TagsEq(fx.Take(), (2051, 0x452), (2050, 0x452), (2049, 0x452)));
        Assert.True(fx.Pm().Find(2050)!.Commander == 2051 && fx.Pm().Find(2049)!.Commander == -1);
        Assert.Equal("0 beagle 0", fx.Run("echo(Client::getTeam(2049), \" \", Client::getSkinBase(2049), \" \", GameBase::getTeam(2049));"));
        fx.Pm().SetTeam(2049, 0);  // unchanged
        fx.Pm().SetTeam(2049, 2);  // >= team count
        fx.Pm().SetTeam(2049, -2);
        Assert.Equal(0, fx.SentEvents.Count);
        fx.Pm().SetTeam(2049, -1);
        Assert.Equal(3, fx.SentEvents.Count);
        var b = new Bits(fx.SentEvents[0].E);
        Assert.True(b.U(7) == 1 && b.U(4) == 0 && b.Done());
        fx.SentEvents.Clear();

        // 12.2: setInitialTeam writes the team only (no event, no skin).
        Assert.Equal("True", fx.Run("echo(Client::setInitialTeam(2051, 1));"));
        Assert.True(fx.SentEvents.Count == 0 && fx.Pm().Find(2051)!.Skin == "base");
        // 6 step 2: entering Command mode replays every teammate's command entry whose stamp is newer or whose target
        // index is set (+0x48 starts 0, never -1: a zeroed allocation [I]).
        fx.Finished(2049);
        fx.Run("Client::setGuiMode(2049, 2);");
        Assert.True(TagsEq(fx.Take(), (2049, 0x455)));
        // 5 step 5: a team change re-runs the Command-mode step for the new teammates first.
        fx.Pm().Find(2051)!.CommandTarget = 7;
        fx.Pm().SetTeam(2049, 1);
        Assert.True(Keys(fx.SentEvents, 0x455).SequenceEqual(new[] { (2049, 2051) }));
        Assert.True(TagsEq(fx.Take(), (2049, 0x455), (2051, 0x452), (2050, 0x452), (2049, 0x452)));
    }

    private static void CommandEventBody()
    {
        // 10 / 6 step 2: int(10) commanded - 2048, int(2) status, flag ack 0; flag icon != -1 -> int(10) issuer - 2048,
        // int(6) icon, flag + int(8) target, int(10) x, int(10) y; flag text 0.
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Finished(2049);
        var t = fx.Pm().Find(2050)!;
        t.CommandIcon = 5;
        t.CommandStatus = 1;
        t.CommandIssuer = 2049;
        t.CommandTarget = 9;
        t.WaypointX = 300;
        t.WaypointY = 700;
        fx.SentEvents.Clear();
        fx.Run("Client::setGuiMode(2049, 2);");
        Assert.Equal(1, fx.SentEvents.Count);
        var b = new Bits(fx.SentEvents[0].E);
        Assert.True(b.U(10) == 2 && b.U(2) == 1 && !b.Flag() && b.Flag() && b.U(10) == 1 && b.U(6) == 5);
        Assert.True(b.Flag() && b.U(8) == 9 && b.U(10) == 300 && b.U(10) == 700 && !b.Flag() && b.Done());
        // Command mark: re-entering sends again only because the target index is set.
        t.CommandTarget = -1;
        fx.SentEvents.Clear();
        fx.Run("Client::setGuiMode(2049, 1); Client::setGuiMode(2049, 2);");
        Assert.Equal(0, fx.SentEvents.Count);
    }

    private static void Objectives()
    {
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Pm().SetTeam(2049, 0);
        fx.Finished(2049);
        fx.SentEvents.Clear();
        List<(uint, string)> Lines()
        {
            var r = new List<(uint, string)>();
            foreach (var s in fx.SentEvents)
            {
                Assert.True(s.To == 2049 && s.E.Tag == 0x459);
                var b = new Bits(s.E);
                var k = b.U(32);
                r.Add((k, b.Str()));
                Assert.True(b.Done());
            }
            fx.SentEvents.Clear();
            return r;
        }
        // 6 step 3: entering 3 or 5 from another mode sends every line with stamp > mark. Stamps start at 1 and the
        // counter at 0, so all 32 lines go on every entry until the first set.
        Assert.Equal("True", fx.Run("echo(Client::setGuiMode(2049, 5));"));
        var all = Lines();
        Assert.True(all.Count == 32 && all[0].Item1 == 0 && all[31].Item1 == 31 && all[7].Item2.Length == 0);
        fx.Run("Client::setGuiMode(2049, 3);"); // 5 -> 3: no objectives
        Assert.Equal(0, Lines().Count);
        fx.Run("Client::setGuiMode(2049, 1); Client::setGuiMode(2049, 5);");
        Assert.Equal(32, Lines().Count);
        // 9: setObjective posts only to team members in mode 5.
        fx.Run("Client::setGuiMode(2049, 1);");
        Assert.Equal("0", fx.Run("echo(Team::setObjective(0, 3, \"Hold the flag\"));"));
        Assert.Equal(0, Lines().Count);
        fx.Run("Client::setGuiMode(2049, 5);");
        all = Lines();
        Assert.True(all.Count == 32 && all[3].Item2 == "Hold the flag");
        fx.Run("Team::setObjective(0, 4, \"Defend\");");
        Assert.True(Lines().SequenceEqual(new[] { (4u, "Defend") }));
        fx.Run("Team::setObjective(0, 4, \"Defend\"); Team::setObjective(0, 32, x); Team::setObjective(2, 1, x); " +
               "Team::setObjective(1, 1, x);");
        Assert.Equal(0, Lines().Count); // unchanged, bad line, bad team, other team
        // Victory members are not updated live; they catch up on the next entry.
        fx.Run("Client::setGuiMode(2049, 3); Team::setObjective(0, 5, \"Late\");");
        Assert.Equal(0, Lines().Count);
        fx.Run("Client::setGuiMode(2049, 1); Client::setGuiMode(2049, 5);");
        Assert.True(Lines().SequenceEqual(new[] { (5u, "Late") }));
        // clearObjectives: an empty text for every non-empty line, to members in mode 5.
        Assert.Equal("0", fx.Run("echo(Team::clearObjectives(0));"));
        Assert.True(Lines().SequenceEqual(new[] { (3u, ""), (4u, ""), (5u, "") }));
        fx.Run("Client::setGuiMode(2049, 1); Client::setGuiMode(2049, 5);");
        Assert.Equal(0, Lines().Count);
    }

    private static void GuiMode()
    {
        var fx = new Fx();
        fx.Add(2049);
        // 12.2: setGuiMode needs a data-finished human; 6: mode 0..6, PSC mode updated.
        Assert.Equal("False", fx.Run("echo(Client::setGuiMode(2049, 1));"));
        fx.Finished(2049);
        Assert.Equal("True 1", fx.Run("echo(Client::setGuiMode(2049, 1), \" \", Client::getGuiMode(2049));"));
        Assert.Equal("True 1", fx.Run("echo(Client::setGuiMode(2049, 7), \" \", Client::getGuiMode(2049));"));
        Assert.Equal("False -1", fx.Run("echo(Client::setGuiMode(2060, 1), \" \", Client::getGuiMode(2060));"));
    }

    private static void Scores()
    {
        var fx = new Fx();
        foreach (var id in new[] { 2049, 2050, 2051, 2052 })
            fx.Add(id);
        foreach (var id in new[] { 2049, 2050, 2051 })
            fx.Finished(id);
        fx.Run("Client::setMenuScoreVis(2049, true); Client::setMenuScoreVis(2050, true);");
        fx.Run("Client::setGuiMode(2050, 3);");
        Assert.Equal("1", fx.Run("echo(Client::setMenuScoreVis(2052, true));")); // not data-finished: not stored
        fx.SentEvents.Clear();
        // 7: setScore to every human with menu score visible and mode != 3.
        Assert.Equal("True", fx.Run("echo(Client::setScore(2051, \"%n\\t10\", 10));"));
        Assert.True(DeltasTo(fx.SentEvents, 2049).SequenceEqual(new[] { new Delta(false, 2051, 10, "%n\t10") }) &&
                    fx.SentEvents.Count == 1);
        fx.SentEvents.Clear();
        fx.Run("Client::setScore(2051, \"%n\\t10\", 10);"); // unchanged
        Assert.Equal(0, fx.SentEvents.Count);
        fx.Run("Client::setScore(2051, \"%n\\t10\", 11); Team::setScore(0, \"BE\", 5); Team::setScore(-1, \"H\", 0);");
        Assert.True(DeltasTo(fx.SentEvents, 2049).SequenceEqual(new[] { new Delta(false, 2051, 11, "%n\t10"),
                                                                        new Delta(true, 0, 5, "BE"),
                                                                        new Delta(true, -1, 0, "H") }));
        Assert.Equal(3, fx.SentEvents.Count);
        fx.SentEvents.Clear();
        // 6 step 4: entering Victory with the menu score hidden flushes once (setMenuScoreVis 1 then 0, checked against
        // the old mode): humans newest first, then teams 0..n-1, never team -1. The menu stays hidden afterwards.
        fx.Run("Client::setGuiMode(2051, 3);");
        Assert.True(DeltasTo(fx.SentEvents, 2051).SequenceEqual(new[] { new Delta(false, 2051, 11, "%n\t10"),
                                                                        new Delta(true, 0, 5, "BE") }));
        Assert.Equal(2, DeltaCount(fx.SentEvents));
        fx.SentEvents.Clear();
        fx.Run("Client::setGuiMode(2051, 1); Client::setScore(2049, a, 1);");
        Assert.Equal(0, DeltasTo(fx.SentEvents, 2051).Count);
        fx.SentEvents.Clear();
        // Leaving Victory with the menu score visible flushes what was held back.
        fx.Run("Client::setGuiMode(2050, 1);");
        Assert.True(DeltasTo(fx.SentEvents, 2050).SequenceEqual(new[] { new Delta(false, 2051, 11, "%n\t10"),
                                                                        new Delta(false, 2049, 1, "a"),
                                                                        new Delta(true, 0, 5, "BE") }));
        Assert.Equal(3, DeltaCount(fx.SentEvents));
        fx.SentEvents.Clear();
        // setMenuScoreVis(1) with nothing newer sends nothing.
        fx.Run("Client::setMenuScoreVis(2049, true);");
        Assert.Equal(0, fx.SentEvents.Count);
        // Headings go to every human, team -1 / client 2048, sort 0.
        Assert.Equal("True", fx.Run("echo(setTeamScoreHeading(\"Team\\tScore\"));"));
        Assert.True(fx.SentEvents.Count == 4 && fx.SentEvents[0].To == 2052 && fx.SentEvents[3].To == 2049);
        Assert.True(DeltaOf(fx.SentEvents[0].E) == new Delta(true, -1, 0, "Team\tScore"));
        fx.SentEvents.Clear();
        fx.Run("setClientScoreHeading(\"Name\");");
        Assert.True(fx.SentEvents.Count == 4 && DeltaOf(fx.SentEvents[0].E) == new Delta(false, 2048, 0, "Name"));
    }

    private static void Ping()
    {
        // 8: every 1.5 s, ping = min(ping, 999), loss; PingPLEvent (unguaranteed) to humans with the menu score visible:
        // int(7) count, per human newest first int(7) id - 2048, int(8) ping >> 2, unit(6) loss.
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Finished(2049);
        fx.Run("Client::setMenuScoreVis(2049, true);");
        fx.Pm().NetStats = id => (id == 2049 ? 1234u : 100u, id == 2049 ? 0.5f : 1.0f);
        fx.SentEvents.Clear();
        var strings = new StringTable();
        var m = new SimManager(strings);
        m.AdvanceTo(10.0f);
        fx.Pm().StartPingTimer(m);
        m.AdvanceTo(11.4f);
        Assert.Equal(0, fx.SentEvents.Count);
        m.AdvanceTo(11.5f);
        Assert.True(fx.SentEvents.Count == 1 && fx.SentEvents[0].To == 2049 && fx.SentEvents[0].E.Tag == 0x461 &&
                    !fx.SentEvents[0].E.Guaranteed);
        var b = new Bits(fx.SentEvents[0].E);
        Assert.Equal(2u, b.U(7));
        Assert.True(b.U(7) == 2 && b.U(8) == 25 && b.U(6) == 63);
        Assert.True(b.U(7) == 1 && b.U(8) == 249 && b.U(6) == 31 && b.Done());
        Assert.Equal(999u, fx.Pm().Find(2049)!.Ping);
        m.AdvanceTo(12.9f);
        Assert.Equal(1, fx.SentEvents.Count);
        m.AdvanceTo(13.0f);
        Assert.True(fx.SentEvents.Count == 2 && m.PendingEvents() == 1);
    }

    private static void SkinAndMessages()
    {
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.SentEvents.Clear();
        // 12.2 setSkin: 1..19 of [0-9A-Za-z]; a different StringTable entry (no case) posts PlayerSkinEvent (int(7) id -
        // 2048, str skin) to every human.
        Assert.Equal("True", fx.Run("echo(Client::setSkin(2049, beagle));"));
        Assert.True(TagsEq(fx.Take(), (2050, 0x462), (2049, 0x462)));
        fx.Run("Client::setSkin(2049, BEAGLE); Client::setSkin(2049, \"has space\"); Client::setSkin(2049, \"\");" +
               "Client::setSkin(2049, abcdefghijklmnopqrst);");
        Assert.Equal(0, fx.SentEvents.Count);
        fx.Run("Client::setSkin(2049, abcdefghijklmnopqrs);");
        {
            var b = new Bits(fx.SentEvents[0].E);
            Assert.True(fx.SentEvents.Count == 2 && b.U(7) == 1 && b.Str() == "abcdefghijklmnopqrs" && b.Done());
        }
        fx.SentEvents.Clear();
        Assert.Equal("False", fx.Run("echo(Client::setSkin(2049));"));
        // sendMessage: PlayerSayEvent (int(12) source, int(5) type, str) to that client only.
        Assert.Equal("TrueTrue", fx.Run("echo(Client::sendMessage(2050, 2, hello), Client::sendMessage(2060, 2, hello));"));
        Assert.True(TagsEq(fx.Take(), (2050, 0x453)));
        Assert.True(fx.Run("echo(Client::sendMessage(2050, 2));") == "false" && fx.SentEvents.Count == 0);
    }

    private static void SayBodies()
    {
        var fx = new Fx();
        fx.Add(2049);
        fx.SentEvents.Clear();
        fx.Run("Client::sendMessage(2049, 2, hello); Client::sendMessage(2049, 0, hi, 2049); " +
               "Client::sendMessage(2049, 1, x, -1); Client::sendMessage(2060, 1, x);");
        Assert.Equal(3, fx.SentEvents.Count);
        (uint, uint)[] want = [(0, 2), (2049, 0), (0, 1)];
        for (var i = 0; i < 3; ++i)
        {
            var b = new Bits(fx.SentEvents[i].E);
            Assert.True(b.U(12) == want[i].Item1 && b.U(5) == want[i].Item2);
            b.Str();
            Assert.True(b.Done());
        }
    }

    private static void TargetNameEvents()
    {
        // SensorManager_setTargetName (0x00439b68): a changed name goes to every human, newest first; the same name, an
        // unknown slot and a first empty name post nothing.
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.Game.TargetNames.Clear();
        fx.Game.TargetNames.AddRange(new[] { "Gen", "" });
        fx.SentEvents.Clear();
        fx.Game.SetTargetName(1, "Station");
        Assert.True(fx.SentEvents.Count == 2 && fx.SentEvents[0].To == 2050 && fx.SentEvents[1].To == 2049 &&
                    fx.SentEvents[0].E.Tag == 0x45d);
        {
            var b = new Bits(fx.SentEvents[0].E);
            Assert.True(b.U(7) == 1 && b.Str() == "Station" && b.Done());
        }
        fx.SentEvents.Clear();
        fx.Game.SetTargetName(1, "Station");
        fx.Game.SetTargetName(7, "x");
        fx.Game.SetTargetName(-1, "x");
        Assert.Equal(0, fx.SentEvents.Count);
        fx.Game.SetTargetName(0, "");
        Assert.True(fx.SentEvents.Count == 2 && fx.Game.TargetNames[0].Length == 0);
    }

    private static void Reset()
    {
        // 1.2: MissionResetEvent (empty) to every human, then addTeam(-1) posts TeamAdd -1 to all.
        var fx = new Fx();
        fx.Add(2049);
        fx.Add(2050);
        fx.SentEvents.Clear();
        fx.Run("resetPlayerManager();");
        foreach (var id in new[] { 2049, 2050 })
        {
            var tags = new List<uint>();
            foreach (var s in fx.SentEvents)
                if (s.To == id)
                    tags.Add(s.E.Tag);
            Assert.True(tags.SequenceEqual(new uint[] { 0x450, 0x44f }));
        }
        Assert.True(fx.SentEvents[0].E.BodyBits == 0 && fx.Game.TeamCount == 0);
    }

    private static void CommandMessages()
    {
        // 12.1 / 12.2: wrong argument counts and misses, message (if any) then return value.
        var fx = new Fx();
        fx.Add(2049);
        (string, string)[] cases =
        [
            ("echo(getNumClients());", "1"),
            ("echo(getClientByIndex());", "0"),
            ("echo(Client::getFirst(1));", "invalid call to Client::getFirst()\n-1"),
            ("echo(Client::getNext());", "invalid call to Client::getNext(client)\n-1"),
            ("echo(Client::getNext(2060));", "-1"),
            ("echo(Client::getName());", "wrong argcount to getPlayerName(playerId);\n"),
            ("echo(Client::getName(2049), Client::getName(2060));", "P"),
            ("echo(Client::getTeam());", "wrong argcount to getPlayerName(playerId);\n-1"),
            ("echo(Client::getTeam(2060));", "-1"),
            ("echo(Client::getSkinBase());", "invalid call to Client::getSkinBase(client)\n"),
            ("echo(Client::getVoiceBase(2049), Client::getVoiceBase(2060));", "male1"),
            ("echo(Client::getGender(2049));", "Male"),
            ("echo(Client::getGender());", "invalid call to Client::getGender(client)\n"),
            ("echo(Client::getGuiMode());", "wrong argcount to Client::getGuiMode(playerId);\n-1"),
            ("echo(Client::setGuiMode(2049));", "wrong argcount to Client::setGuiMode(playerId,guiId);\nFalse"),
            ("echo(Client::setMenuScoreVis(2049));", "invalid call to Client::setMenuScoreVis()\n0"),
            ("echo(Client::setScore(2049, x));",
             "wrong argcount to Client::setScore(playerId, scoreString, sortValue);\nFalse"),
            ("echo(Client::setScore(2060, x, 1));", "True"),
            ("echo(Team::setScore(0, x));", "wrong argcount to Team::setScore(playerId, scoreString, sortValue);\nFalse"),
            ("echo(setTeamScoreHeading());", "wrong argcount to setTeamScoreHeading(headingString);\nFalse"),
            ("echo(setClientScoreHeading(a, b));", "wrong argcount to setClientScoreHeading(headingString);\nFalse"),
            ("echo(Team::setObjective(0, 1));", "wrong argcount to Team::setObjective(teamId, objNum, text);\n0"),
            ("echo(Team::clearObjectives());", "wrong argcount to Team::setObjective(teamId);\n0"),
            ("echo(Client::setInitialTeam(2049));", "wrong argcount to setInitialTeam(playerId, teamId);\nFalse"),
            ("echo(Client::setInitialTeam(2049, -1), Client::setInitialTeam(2049, 2), Client::setInitialTeam(2060, 0));",
             "FalseFalseFalse"),
            ("echo(Client::setInitialTeam(2049, 1), Client::getTeam(2049));", "True1"),
            ("echo(Client::setDataFinished());", "invalid call to Client::setDataFinished(client)\n"),
            ("echo(Client::setDataFinished(2060));", ""),
            ("echo(Client::getObserverCamera());", "-1"),
            ("echo(Observer::setFlyMode(2049));", "false"),
            ("echo(Client::getOwnedObject());", "invalid call to Client::getOwnedObject(client)\n-1"),
            ("echo(Client::getControlObject(2049));", "-1"),
            ("echo(Client::setOwnedObject(2049));", "invalid call to Client::setOwnedObject(client, object)\nfalse"),
            ("echo(Client::setControlObject(2060, 1));", "false"),
            ("echo(GameBase::setTeam(2049));", "wrong argcount to setTeam(object,team)\n-1"),
            ("echo(GameBase::setTeam(2060, 0));", "-1"),
        ];
        var failures = 0;
        foreach ((var src, var want) in cases)
        {
            var got = fx.Run(src);
            if (got != want)
            {
                Console.Error.WriteLine("FAIL " + src + "\n  want: " + want + "\n  got:  " + got);
                ++failures;
            }
        }
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Main()
    {
        IdsAndLists();
        Connect();
        Disconnect();
        TeamChange();
        CommandEventBody();
        Objectives();
        GuiMode();
        Scores();
        Ping();
        SkinAndMessages();
        SayBodies();
        Reset();
        TargetNameEvents();
        CommandMessages();
    }
}
