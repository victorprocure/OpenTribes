// Events, datablocks, the PSC blocks, the move buffer, rate sections and demos of the client receive path
// (ROADMAP M9 C3a, docs/client-net.md 2, 4, 5), against the event builders and datablock packers of the server.
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using static OpenTribes.Tests.ClientRoundTrip;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class ClientNetStreamTests
{
    // Writes one event the way GameConnection.WriteEvents does (game-stream.md 2.1): guaranteed, ordered, explicit or
    // implicit sequence.
    private static void WriteEv(BitStream bs, OutEvent e, int seq, bool implicitSeq)
    {
        bs.WriteFlag(true);
        bs.WriteFlag(true);
        bs.WriteFlag(implicitSeq);
        if (!implicitSeq)
        {
            bs.WriteFlag(true);
            bs.WriteInt((uint)seq, 7);
        }
        bs.WriteInt((uint)(e.Tag - 0x400), 7);
        bs.WriteBits(e.BodyBits, e.Body);
    }

    private static byte[] Payload(Action<BitStream> body)
    {
        byte[] buf = new byte[1400];
        var bs = new BitStream(buf, buf.Length);
        body(bs);
        return buf.AsSpan(0, bs.BytePosition()).ToArray();
    }

    // PSC member of a server packet without a control object (game-stream.md 6.3): mode, flag, lastMove, no control.
    private static void EmptyPsc(BitStream bs, uint mode = 0, int lastMove = -1)
    {
        bs.WriteInt(mode, 3);
        bs.WriteFlag(false);
        bs.WriteInt((uint)lastMove, 32);
        bs.WriteFlag(false);
    }

    // A packet: no rate sections, an event block, the empty PSC member, no ghost block.
    private static byte[] EventPacket(params (OutEvent e, int seq, bool implicitSeq)[] events) => Payload(bs =>
    {
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        bs.WriteFlag(true);
        foreach (var (e, seq, imp) in events)
            WriteEv(bs, e, seq, imp);
        bs.WriteFlag(false);
        bs.WriteFlag(true);
        EmptyPsc(bs);
        bs.WriteFlag(false);
    });

    [Fact]
    public void EventsReachTheHandlers()
    {
        // client-net.md 2.3: PlayerAddEvent, TeamAddEvent, DeltaScoreEvent, TargetNameEvent, SimConsoleEvent.
        var world = new ClientWorld();
        var conn = new ClientGameConnection(world);
        OutEvent add = GameEvents.MakePlayerAdd(2050, false, "Bob", "base", "Male1", true, -1, 1);
        OutEvent team = GameEvents.MakeTeamAdd(-1, "Storm", "base");
        OutEvent score = GameEvents.MakeDeltaScore(false, 2050, 7, "Bob 7");
        OutEvent tname = GameEvents.MakeTargetName(5, "Generator");
        OutEvent eval = GameEvents.MakeRemoteEval(new[] { "SetInfoLine", "hello" });
        // Sequence 1 arrives before sequence 0 in the same packet: the handlers still run in order.
        DecodedPacket p = conn.Receive(EventPacket((team, 1, false), (add, 0, false), (score, 2, false), (tname, 3, false), (eval, 4, false)));
        Assert.True(p.UnknownBits == 0 && !p.Overrun && p.Events.Count == 5);
        Assert.True(p.Events[0].Name == "TeamAddEvent" && p.Events[1].Name == "PlayerAddEvent");
        ClientPlayerManager pm = world.Players;
        Assert.True(pm.TeamCount == 0 && pm.Teams[-1].Name == "Storm");
        Assert.True(pm.Clients.ContainsKey(2050) && pm.Clients[2050].Name == "Bob" && pm.Clients[2050].Team == 1 && pm.HumanCount == 1 && pm.PlayerCount == 1);
        Assert.True(pm.Callbacks.SequenceEqual(new[] { "onClientJoin(2050);", "onTeamAdd(-1);" }) || pm.Callbacks.SequenceEqual(new[] { "onTeamAdd(-1);", "onClientJoin(2050);" }));
        Assert.True(pm.Clients[2050].ScoreText == "Bob 7" && pm.Clients[2050].SortValue == 7);
        Assert.True(pm.TargetNames[5] == "Generator");
        Assert.True(world.RemoteCalls.Count == 1 && world.RemoteCalls[0].Name == "SetInfoLine" && world.RemoteCalls[0].Args[0] == "hello");
        Assert.Contains("Bob connected to the game.", pm.Chat);
        // An ordered event whose predecessor is missing waits until the predecessor arrives.
        var w2 = new ClientWorld();
        var c2 = new ClientGameConnection(w2);
        c2.Receive(EventPacket((add, 1, false)));
        Assert.True(w2.Players.Clients.Count == 0 && c2.Events.Dispatched.Count == 0);
        c2.Receive(EventPacket((eval, 0, false)));
        Assert.True(w2.Players.Clients.ContainsKey(2050) && w2.RemoteCalls.Count == 1);
    }

    [Fact]
    public void TeamChangeGivesTheRecordTheTeamSkin()
    {
        // 00406bc4 case 7: the record skin (+0xa0) becomes the skin of the new team; the server sends no PlayerSkinEvent for it, so the
        // own player of a client that joined with the skin base shows the team colours in third person.
        var world = new ClientWorld { PlayerId = 2049 };
        var conn = new ClientGameConnection(world);
        // The first team added takes the slot of the observer team -1, the next one slot 0 (client-net.md 2.3).
        OutEvent team0 = GameEvents.MakeTeamAdd(-1, "Observer", "base");
        OutEvent team1 = GameEvents.MakeTeamAdd(0, "Blood Eagle", "beagle");
        OutEvent add = GameEvents.MakePlayerAdd(2049, false, "Rec", "base", "Male1", true, -1, 0);
        var w = new EventWriter();
        w.PutInt(2049 - 2048, 7);
        w.PutInt(0 + 1, 4);
        OutEvent change = w.Finish(0x452);
        var w2 = new EventWriter();
        w2.PutInt(2049 - 2048, 7);
        w2.PutInt(7 + 1, 4);
        OutEvent unknown = w2.Finish(0x452);
        conn.Receive(EventPacket((team0, 0, false), (team1, 1, false), (add, 2, false)));
        Assert.Equal("base", world.Players.Clients[2049].Skin);
        conn.Receive(EventPacket((change, 3, false)));
        Assert.Equal("beagle", world.Players.Clients[2049].Skin);
        Assert.Equal(0, world.Players.Clients[2049].Team);
        // A team the client does not know leaves the skin alone (findTeam returns null).
        conn.Receive(EventPacket((unknown, 4, false)));
        Assert.Equal("beagle", world.Players.Clients[2049].Skin);
    }

    [Fact]
    public void CommandTextsLoseTheirVoiceTagAndKeepTheirType()
    {
        // 0040ce84 case 10 with 0040940c: the ~w tag ends the shown text, the voice goes to the audio callback, the line type is 3
        // (ack, "Cmd. to") or 4 (the commanded client sees its commander).
        var world = new ClientWorld { PlayerId = 2049 };
        var conn = new ClientGameConnection(world);
        var voices = new List<(int, string)>();
        world.Players.ChatVoice = (c, t) => voices.Add((c, t));
        OutEvent add = GameEvents.MakePlayerAdd(2049, false, "Rec", "base", "Male1", true, -1, 0);
        OutEvent cmd = CommandMap.MakeCommandEvent(2049, 2049, 0, false, 5, -1, 10, 20, true, true, "Escort Rec.~wescfr");
        OutEvent ack = CommandMap.MakeCommandEvent(2049, -1, 1, true, -1, -1, 0, 0, true, false, null);
        conn.Receive(EventPacket((add, 0, false), (cmd, 1, false), (ack, 2, false)));
        Assert.Equal(new[] { "Rec connected to the game.", "Rec: Escort Rec.", "Rec: Escort Rec." }, world.Players.Chat);
        Assert.Equal(new[] { 0, 4, 3 }, world.Players.ChatTypes);
        Assert.Equal(new[] { (2049, "Escort Rec.~wescfr"), (2049, "Escort Rec.~wescfr") }, voices);
    }

    [Fact]
    public void CommandEventSetsAndAckClearsTheWaypoint()
    {
        // 0040ce84 case 10: a command to the local client sets 006a83e4 (the icon) and the position (value * size / 1024 + origin of
        // the mission area); an ack with status 0 or a mission reset clears it; a command to another client leaves it.
        var world = new ClientWorld { PlayerId = 2049 };
        var conn = new ClientGameConnection(world);
        world.Players.WaypointToWorld = (x, y) => (x * 2.0 * 0.0009765625 * 1000 + 100, y * 4.0 * 0.0009765625 * 1000 - 50);
        OutEvent add = GameEvents.MakePlayerAdd(2049, false, "Rec", "base", "Male1", true, -1, 0);
        OutEvent add2 = GameEvents.MakePlayerAdd(2050, false, "Other", "base", "Male1", true, -1, 0);
        OutEvent other = CommandMap.MakeCommandEvent(2050, 2049, 0, false, 5, -1, 5, 5, false, false, null);
        OutEvent cmd = CommandMap.MakeCommandEvent(2049, 2049, 0, false, 5, -1, 512, 256, false, false, null);
        OutEvent ack = CommandMap.MakeCommandEvent(2049, -1, 0, true, -1, -1, 0, 0, false, false, null);
        conn.Receive(EventPacket((add, 0, false), (add2, 1, false), (other, 2, false)));
        Assert.Equal(-1, world.Players.WaypointTarget);
        conn.Receive(EventPacket((cmd, 3, false)));
        Assert.Equal(5, world.Players.WaypointTarget);
        Assert.Equal(512 * 2.0 * 0.0009765625 * 1000 + 100, world.Players.WaypointX);
        Assert.Equal(256 * 4.0 * 0.0009765625 * 1000 - 50, world.Players.WaypointY);
        conn.Receive(EventPacket((ack, 4, false)));
        Assert.Equal(-1, world.Players.WaypointTarget);
        conn.Receive(EventPacket((cmd, 5, false)));
        Assert.Equal(5, world.Players.WaypointTarget);
        conn.Receive(EventPacket((new EventWriter().Finish(0x450), 6, false)));
        Assert.Equal(-1, world.Players.WaypointTarget);
    }

    [Fact]
    public void DatablocksOfEveryGroupRoundTrip()
    {
        // client-net.md 2.6, game-stream.md 4.2: the server pack of a default block of each class, read back by the client.
        var strings = new StringTable();
        string log = "";
        var db = new DataBlockManager(strings, l => log += l);
        (string cls, int group)[] kinds =
        {
            ("SoundProfileData", 0), ("SoundData", 1), ("DamageSkinData", 2), ("PlayerData", 3), ("StaticShapeData", 4), ("ItemData", 5),
            ("ItemImageData", 6), ("MoveableData", 7), ("SensorData", 8), ("VehicleData", 9), ("FlierData", 10), ("BulletData", 14),
            ("GrenadeData", 15), ("RocketData", 16), ("LaserData", 17), ("TurretData", 19), ("ExplosionData", 20), ("MarkerData", 21),
            ("DebrisData", 22), ("MineData", 23), ("TargetLaserData", 24), ("SeekingMissileData", 25), ("TriggerData", 26),
            ("LightningData", 28), ("RepairEffectData", 29),
        };
        var world = new ClientWorld();
        foreach ((string cls, int group) in kinds)
        {
            Assert.True(db.Declare(cls, "blk" + group), cls);
            var w = new EventWriter();
            Assert.True(DatablockPack.PackDataBlock(db, db.Find("blk" + group)!, w), cls);
            OutEvent e = w.Finish(0x458);
            var r = new ClientReader(Payload(b =>
            {
                b.WriteInt((uint)group, 6);       // the header the caller writes: group, count, index
                b.WriteInt(1, 8);
                b.WriteInt(0, 8);
                b.WriteBits(e.BodyBits, e.Body);
            }), world) { Out = new List<DecodedField>() };
            world.DataBlocks.ReadEvent(r, out string summary);
            Assert.True(r.Pos == (int)e.BodyBits + 22, cls + ": client read " + r.Pos + " of " + (e.BodyBits + 22) + " bits");
            Assert.True(world.DataBlocks.Counts[group] == 1 && world.DataBlocks.Blocks[(group, 0)].Class == cls &&
                        summary.Contains("group=" + group, StringComparison.Ordinal));
        }
        // Values of a SoundData block.
        var db2 = new DataBlockManager(new StringTable(), l => log += l);
        db2.Declare("SoundProfileData", "sp0");
        Assert.True(db2.Declare("SoundData", "shot"));
        DataBlockManager.Block s = db2.Find("shot")!;
        db2.SetField(s, "wavFileName", new[] { "shot.wav" }, "");
        db2.SetField(s, "priority", new[] { "0.5" }, "");
        var w1 = new EventWriter();
        Assert.True(DatablockPack.PackDataBlock(db2, s, w1));
        OutEvent ev = w1.Finish(0x458);
        var cw = new ClientWorld();
        var rr = new ClientReader(Payload(b =>
        {
            b.WriteInt(1, 6);
            b.WriteInt(1, 8);
            b.WriteInt(0, 8);
            b.WriteBits(ev.BodyBits, ev.Body);
        }), cw) { Out = new List<DecodedField>() };
        cw.DataBlocks.ReadEvent(rr, out _);
        ClientDataBlock blk = cw.DataBlocks.Blocks.Values.Single();
        Assert.True(blk.Group == 1 && (string)blk.Fields[0].Value! == "shot.wav" && Math.Abs((double)blk.Fields[1].Value! - 31 / 63.0) < 1e-9);
        // 2.6 step 3: the last block of group 30 (or an empty group 30) is dataFinished().
        var wf = new ClientWorld();
        var rf = new ClientReader(Payload(bs =>
        {
            bs.WriteInt(30, 6);
            bs.WriteInt(0, 8);
            bs.WriteInt(255, 8);
        }), wf) { Out = new List<DecodedField>() };
        wf.DataBlocks.ReadEvent(rf, out _);
        Assert.True(wf.DataBlocks.Finished && wf.DataBlocks.Counts[30] == 0);
        // The index width of a ghost datablock follows the group count (2.3): 52 blocks give 6 bits.
        wf.DataBlocks.Counts[4] = 52;
        Assert.True(rf.DbBits(4) == 6);
    }

    [Fact]
    public void PscBlocksAndTheMoveBuffer()
    {
        // client-net.md 4.3: the client block carries the first move index and the unacknowledged moves.
        var world = new ClientWorld();
        ClientPsc psc = world.Psc;
        for (int i = 0; i < 5; ++i)
            psc.AddMove(new Move { Forward = i / 15.0f, Fire = i == 3, Turn = i == 2 ? 0.25f : 0 });
        byte[] buf = new byte[256];
        var bs = new BitStream(buf, buf.Length);
        psc.WriteClient(bs);
        DecodedPsc c2s = ClientPsc.ReadClientBlock(new ClientReader(buf, world));
        Assert.True((long)c2s.Fields[0].Value! == 169 && (long)c2s.Fields[1].Value! == 0 && (long)c2s.Fields[2].Value! == 5);
        // The server reads the same bytes (GameConnection.ReadPsc): the first move index and the moves match.
        var r = new BitStream(buf, buf.Length);
        Assert.True(r.ReadInt(8) == 169 && r.ReadInt(32) == 0);
        var m = new Move();
        for (int i = 0; i < 5; ++i)
        {
            Assert.True(r.ReadFlag());
            Moves.UnpackMove(r, ref m, true, i > 0);
            Assert.True(Math.Abs(m.Forward - i / 15.0f) < 1e-6 && m.Fire == (i == 3));
        }
        Assert.True(!r.ReadFlag());
        // 4.4 step 1: a server block with lastMove 3 drops three buffered moves; the buffer starts at move 3.
        var wr = new ClientReader(Payload(b =>
        {
            b.WriteInt(1, 3);
            b.WriteFlag(false);
            b.WriteInt(3, 32);
            b.WriteFlag(false);
        }), world);
        DecodedPsc s2c = psc.ReadServerBlock(wr);
        Assert.True(psc.FirstMove == 3 && psc.Moves.Count == 2 && psc.LastMove == 3 && psc.GuiMode == 1 && psc.GuiModeChanges == 1);
        Assert.True(s2c.Text.StartsWith("psc mode=1", StringComparison.Ordinal) && s2c.Text.Contains("control=none", StringComparison.Ordinal));
        // At most 30 moves wait for acknowledgement (4.2 step 7).
        for (int i = 0; i < 40; ++i)
            psc.AddMove(new Move());
        Assert.True(psc.Moves.Count == 30);
        // 4.5: ghost 5 is an ObserverCamera made in the always phase; its control state, inventory deltas and waypoint.
        world.Ghosts.Read(new ClientReader(Payload(b =>
        {
            b.WriteFlag(true);               // always phase
            b.WriteInt(1, 3);                // 4 id bits
            b.WriteFlag(true);
            b.WriteInt(5, 4);
            b.WriteFlag(false);              // not a delete
            b.WriteInt(7, 32);               // priority value of the always phase
            b.WriteInt(0x231, 10);           // ObserverCamera
            b.WriteFlag(false);              // GameBase part: nothing
            b.WriteFlag(false);              // end of entries
            b.WriteFlag(true);               // done
        }), world));
        Assert.True(world.Ghosts.ClassOf(5) == "ObserverCamera" && world.AlwaysDone);
        var cr = new ClientReader(Payload(b =>
        {
            b.WriteInt(1, 3);
            b.WriteFlag(false);
            b.WriteInt(8, 32);
            b.WriteFlag(true);               // control object present
            b.WriteInt(128, 8);              // damage flash
            b.WriteInt(5, 10);               // control ghost 5
            b.WriteF32s(new float[] { 0.1f, 0.2f, 0.3f }); // rotation
            b.WriteInt(1, 3);                // camera mode 1: position follows
            b.WriteF32s(new float[] { 10, 20, 30 });
            b.WriteFlag(false);              // inventory reset
            b.WriteFlag(true);               // one delta
            b.WriteInt(12, 8);
            b.WriteSignedInt(-3, 10);
            b.WriteFlag(false);
            b.WriteFlag(true);               // waypoint
            b.WriteInt(100, 10);
            b.WriteInt(200, 10);
            b.WriteFlag(true);               // visible mask xor
            b.WriteInt(2, 3);
            b.WriteInt(0xf0, 32);
            b.WriteFlag(false);
        }), world);
        psc.ReadServerBlock(cr);
        Assert.True(psc.ControlGhost == 5 && psc.ControlClass == "ObserverCamera" && VecIs(psc.ControlState["pos"], 10, 20, 30));
        Assert.True(psc.ItemCounts[12] == -3 && psc.Waypoint == (100, 200) && psc.Visible[2] == 0xf0 && psc.LastMove == 8);
        Assert.True(Math.Abs(psc.DamageFlash - 128 / 255.0) < 1e-9);
    }

    [Fact]
    public void RateSectionsFollowTheNegotiationRules()
    {
        // game-stream.md 1.3: rateSet is stored unclamped, rateReq becomes max(interval) and min(size) of both sides.
        var conn = new ClientGameConnection(new ClientWorld());
        conn.Receive(Payload(bs =>
        {
            bs.WriteFlag(true);
            bs.WriteInt(200, 10);
            bs.WriteInt(100, 10);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(true);
            EmptyPsc(bs);
            bs.WriteFlag(false);
        }));
        Assert.True(conn.IntervalMs == 200 && conn.PacketBytes == 100);
        conn.Receive(Payload(bs =>
        {
            bs.WriteFlag(false);
            bs.WriteFlag(true);
            bs.WriteInt(150, 10);
            bs.WriteInt(1023, 10);
            bs.WriteFlag(false);
            bs.WriteFlag(true);
            EmptyPsc(bs);
            bs.WriteFlag(false);
        }));
        Assert.True(conn.IntervalMs == 150 && conn.PacketBytes == 200); // max(150, 100), min(1023, 200)
        // The next packet carries rateSet with the result.
        byte[] sent = Array.Empty<byte>();
        Assert.True(conn.WritePacket(1000, (b, len, cookie) =>
        {
            sent = b.AsSpan(0, len).ToArray();
            return true;
        }));
        var r = new BitStream(sent, sent.Length);
        Assert.True(r.ReadFlag() && r.ReadInt(10) == 150 && r.ReadInt(10) == 200 && !r.ReadFlag());
    }

    [Fact]
    public void UnknownLayoutsAndOverrunsAreCounted()
    {
        // Unknown bits are detected and counted, never skipped (client-net.md 2.2).
        var world = new ClientWorld();
        var conn = new ClientGameConnection(world);
        DecodedPacket p = conn.Receive(Payload(bs =>
        {
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(true);               // ghost block
            bs.WriteFlag(true);
            bs.WriteInt(1, 3);
            bs.WriteFlag(true);
            bs.WriteInt(1, 4);
            bs.WriteFlag(false);
            bs.WriteInt(1, 32);
            bs.WriteInt(0x3ff, 10);           // no such class
            bs.WriteInt(0, 32);
            bs.WriteInt(0, 32);
        }));
        Assert.True(p.UnknownBits > 0 && world.UnknownBits == p.UnknownBits && p.UnknownReason!.Contains("0x3ff", StringComparison.Ordinal));
        DecodedPacket p2 = conn.Receive(new byte[] { 0x0c });   // the events flag, an event flag, then the end of the packet
        Assert.True(p2.Overrun && world.Errors == 1);
        // Eight or more trailing bits that no member explains are unknown too.
        DecodedPacket p3 = conn.Receive(Payload(bs =>
        {
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteInt(0xaa, 8);
        }));
        Assert.True(p3.UnknownBits >= 8);
    }

    [Fact]
    public void DemoFilesPlayThroughTheReceivePath()
    {
        // client-net.md 5.2, 5.4: header, 0xff packet records, PSC move and action records, the end record.
        var f = new List<byte>();
        PutU32(f, 2052);
        PutU32(f, 2048);
        byte[] pkt = Payload(bs =>
        {
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(true);
            EmptyPsc(bs, 1, 1);
            bs.WriteFlag(false);
        });
        for (int k = 0; k < 2; ++k)
        {
            PutU32(f, (uint)(33 + 32 * k));
            PutU8(f, 0x01);
            PutU8(f, 1);
            PutF32(f, 1.0f);
            PutF32(f, 0);
            PutF32(f, 0);
            PutF32(f, 0);
            PutU8(f, 0);
            PutU8(f, 0);
            PutU8(f, 1);
            PutU8(f, 1);
            PutU32(f, 0xffffffff);
            PutF32(f, 0.5f);
            PutF32(f, -0.25f);
        }
        PutU32(f, 100);
        PutU8(f, 0x01);
        PutU8(f, 0);
        PutF32(f, 1.0f);
        PutU32(f, 220113);
        PutU32(f, 120);
        PutU8(f, 0xff);
        PutU16(f, (ushort)pkt.Length);
        f.AddRange(pkt);
        PutU32(f, 0xffffffff);
        DemoFile d = DemoFile.Parse(f.ToArray());
        Assert.True(d.ClientId == 2052 && d.ServerId == 2048 && d.Packets == 1 && d.Moves == 2 && d.Actions == 1 && d.Complete);
        Assert.True(d.Records[0].Move.Forward == 1.0f && d.Records[0].Move.Jump && d.Records[0].Move.Fire && d.Records[0].Move.UseItem == -1);
        Assert.True(d.Records[0].Move.Turn == 0.5f && d.Records[0].Move.Pitch == -0.25f && d.Records[2].ActionId == 220113);
        var world = new ClientWorld();
        var conn = new ClientGameConnection(world);
        d.Play(conn);
        // Two moves were buffered; the packet's lastMove 1 removes the first one.
        Assert.True(world.PlayerId == 2052 && world.Packets == 1 && world.UnknownBits == 0 && world.Psc.GuiMode == 1);
        Assert.True(world.Psc.FirstMove == 1 && world.Psc.Moves.Count == 1);
        Assert.Throws<InvalidDataException>(() => DemoFile.Parse(new byte[] { 1, 2, 3 }));
    }
}
