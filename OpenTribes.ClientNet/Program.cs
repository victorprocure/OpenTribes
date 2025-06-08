// tribes-netclient: the headless Tribes client (ROADMAP M9 stage C3a).
//   --connect host:port [--name N] [--seconds S] [--script moves.txt] [--version V] --dump out.jsonl
//   --demo file.rec --dump out.jsonl
//   --decode capture.jsonl [--client IP:PORT] --dump out.jsonl     (the format tools/stream_decode.py reads)
// Every mode prints one JSON summary line on stdout; the exit status is 0 when no unknown bit and no error was seen.
using System.Text.Json;
using OpenTribes.ClientNet;
using OpenTribes.Game;

static string? Arg(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static void Summary(Action<Utf8JsonWriter> body)
{
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
        w.WriteStartObject();
        body(w);
        w.WriteEndObject();
    }
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
}

static void Counts(Utf8JsonWriter w, string name, IDictionary<string, long> d)
{
    w.WriteStartObject(name);
    foreach (var kv in d)
        w.WriteNumber(kv.Key, kv.Value);
    w.WriteEndObject();
}

if (args.Length == 0 || args.Contains("--help"))
{
    Console.Error.WriteLine("usage: tribes-netclient --connect host:port [--name N] [--seconds S] [--script moves.txt] --dump out.jsonl");
    Console.Error.WriteLine("       tribes-netclient --demo file.rec --dump out.jsonl");
    Console.Error.WriteLine("       tribes-netclient --decode capture.jsonl [--client IP:PORT] --dump out.jsonl");
    return args.Length == 0 ? 2 : 0;
}

string? dumpPath = Arg(args, "--dump");
using var dump = new PacketDump(dumpPath);

string? decode = Arg(args, "--decode");
string? demo = Arg(args, "--demo");
string? connect = Arg(args, "--connect");

if (decode != null)
{
    CaptureTotals t = CaptureDecoder.Decode(decode, dump, Arg(args, "--client"), Console.Error);
    Summary(w =>
    {
        w.WriteString("mode", "decode");
        w.WriteNumber("packets", t.Packets);
        w.WriteNumber("bits", t.Bits);
        w.WriteNumber("decoded", t.Known);
        w.WriteNumber("padding", t.Pad);
        w.WriteNumber("unknown", t.Unknown);
        w.WriteNumber("overruns", t.Overruns);
        w.WriteNumber("duplicates", t.Duplicates);
        Counts(w, "events", t.Events);
        Counts(w, "ghosts", t.Ghosts);
    });
    return t.Unknown == 0 && t.Overruns == 0 ? 0 : 1;
}

if (demo != null)
{
    DemoFile d = DemoFile.Parse(File.ReadAllBytes(demo));
    var world = new ClientWorld();
    var conn = new ClientGameConnection(world);
    double ms = 0;
    d.Play(conn, (rec, pkt) =>
    {
        ms = rec.Time;
        if (pkt != null)
            dump.Write(pkt, rec.Time / 1000.0);
    });
    Summary(w =>
    {
        w.WriteString("mode", "demo");
        w.WriteString("file", Path.GetFileName(demo));
        w.WriteBoolean("complete", d.Complete);
        w.WriteNumber("clientId", d.ClientId);
        w.WriteNumber("packets", world.Packets);
        w.WriteNumber("moves", d.Moves);
        w.WriteNumber("actions", d.Actions);
        w.WriteNumber("events", world.Events);
        w.WriteNumber("ghostUpdates", world.GhostUpdates);
        w.WriteNumber("ghostDeletes", world.GhostDeletes);
        w.WriteNumber("ghostsAlive", world.Ghosts.Count);
        w.WriteNumber("unknown", world.UnknownBits);
        w.WriteNumber("errors", world.Errors);
        w.WriteNumber("lastMs", ms);
        w.WriteBoolean("alwaysDone", world.AlwaysDone);
        w.WriteBoolean("dataFinished", world.DataBlocks.Finished);
        if (world.UnknownReasons.Count != 0)
            w.WriteString("firstUnknown", world.UnknownReasons[0]);
        Counts(w, "eventCounts", world.EventCounts);
        Counts(w, "ghostCounts", world.GhostCounts);
    });
    return d.Complete && world.UnknownBits == 0 && world.Errors == 0 ? 0 : 1;
}

if (connect != null)
{
    string name = Arg(args, "--name") ?? "NetClient";
    double seconds = double.Parse(Arg(args, "--seconds") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
    string? scriptPath = Arg(args, "--script");
    List<ScriptStep>? script = scriptPath != null ? MoveScript.Parse(File.ReadAllText(scriptPath)) : null;
    var session = new NetClientSession(name, dump, script, Arg(args, "--version") ?? "1.11") { Log = s => Console.Error.WriteLine(s) };
    SessionResult r = session.Run(connect, seconds);
    Summary(w =>
    {
        w.WriteString("mode", "connect");
        w.WriteBoolean("connected", r.Connected);
        w.WriteBoolean("rejected", r.Rejected);
        w.WriteBoolean("dropped", r.Dropped);
        w.WriteString("reason", r.Reason);
        w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
        w.WriteNumber("packets", r.Packets);
        w.WriteNumber("events", r.Events);
        w.WriteNumber("ghostUpdates", r.GhostUpdates);
        w.WriteNumber("ghostDeletes", r.GhostDeletes);
        w.WriteNumber("ghostsAlive", r.Ghosts);
        w.WriteNumber("movesSent", r.MovesSent);
        w.WriteNumber("unknown", r.UnknownBits);
        w.WriteNumber("errors", r.Errors);
        w.WriteString("controlClass", r.ControlClass);
        w.WriteBoolean("dataFinished", r.DataFinished);
        w.WriteBoolean("alwaysDone", r.AlwaysDone);
        Counts(w, "eventCounts", r.EventCounts);
        Counts(w, "ghostCounts", r.GhostCounts);
        w.WriteStartArray("unknownReasons");
        foreach (string s in r.UnknownReasons)
            w.WriteStringValue(s);
        w.WriteEndArray();
    });
    return r.Connected && !r.Dropped && r.UnknownBits == 0 && r.Errors == 0 ? 0 : 1;
}

Console.Error.WriteLine("one of --connect, --demo, --decode is required");
return 2;
