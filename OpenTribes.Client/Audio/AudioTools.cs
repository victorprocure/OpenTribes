namespace OpenTribes.Client.Audio;

using System.Globalization;
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

/// <summary>Command line helpers of the audio stage.</summary>
/// <remarks><c>--cat NAME</c> writes a resource to stdout. <c>--sound-census</c> runs every wave and sound datablock through the offline mixer. <c>--audio-live WAVE</c> plays on the SDL3 device. <c>--demo ... --audio-out</c> mixes a span of a demo offline.</remarks>
public static class AudioTools
{
    /// <summary>The resource search path of the audio tools.</summary>
    public const string SearchPath = "config;base;base\\voices;base\\missions";

    /// <summary>Writes a resource to standard output.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The process exit code.</returns>
    public static int Cat(string root, string name)
    {
        var res = GfxAssets.Mount(root, out _, SearchPath);
        if (!GfxAssets.Read(res, name, out var d))
        {
            Console.Error.WriteLine("not found: " + name);
            return 1;
        }
        using var o = Console.OpenStandardOutput();
        o.Write(d);
        return 0;
    }

    /// <summary>Writes a 16 bit PCM stereo WAV file.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="interleaved">The interleaved left and right samples.</param>
    /// <param name="rate">The sample rate in Hz.</param>
    public static void WriteWav(string path, short[] interleaved, int rate)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        var bytes = interleaved.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        foreach (var s in interleaved)
            w.Write(s);
    }

    private static List<string> AllWaves(ResourceManager res)
    {
        var names = new List<string>();
        for (var e = res.FindNext("*.wav", null, out var name); e != null; e = res.FindNext("*.wav", e, out name))
            names.Add(name);
        return names;
    }

    private static string? FirstDemo(string root)
    {
        var dir = Path.Combine(root, "Recordings");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.rec").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault() : null;
    }

    // ---- census ----

    /// <summary>Runs every wave and sound datablock through the offline mixer and prints the result.</summary>
    /// <param name="root">The game root directory.</param>
    /// <returns>The process exit code.</returns>
    public static int Census(string root)
    {
        var res = GfxAssets.Mount(root, out var volumes, SearchPath);
        using var s = AudioSession.Create(root, live: false, res);
        var scripts = s.RunSoundScripts();
        Console.WriteLine($"volumes {volumes}; sound scripts ok {scripts}; profiles {s.Sound.Profiles.Count}, pairs {s.Sound.Pairs.Count}");
        foreach (var l in s.Log.Where(l => l.Length > 0).Take(8))
            Console.WriteLine("  log: " + l);

        // Every wave of the volumes decodes and plays through the mixer.
        var waves = AllWaves(res);
        int decoded = 0, failed = 0, played = 0;
        var formats = new Dictionary<string, int>();
        var spare = new float[2 * 1024];
        foreach (var w in waves.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = GfxAssets.Read(res, w, out var bytes) ? WavData.TryParse(bytes) : null;
            if (d == null)
            {
                ++failed;
                Console.WriteLine("  decode failed: " + w);
                continue;
            }
            ++decoded;
            var key = $"{d.Rate} Hz {d.Bits} bit {(d.Channels == 1 ? "mono" : "stereo")}";
            formats[key] = formats.GetValueOrDefault(key) + 1;
            var h = s.Sound.PlayWave(w);
            if (h != 0)
                ++played;
            s.Sound.Tick(0);
            s.Mixer!.Render(spare, 1024);
            s.Sound.StopAll();
            s.Sound.Tick(0);
        }
        Console.WriteLine($"waves {waves.Count} entries, {waves.Distinct(StringComparer.OrdinalIgnoreCase).Count()} names: decoded {decoded}, failed {failed}, played {played}");
        foreach (var kv in formats.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key}: {kv.Value}");

        // The 89 script pairs; the ones whose wave is missing must stay silent (F1).
        int pairOk = 0, pairSilent = 0;
        foreach (var (id, pair) in s.Sound.Pairs.OrderBy(p => p.Key))
        {
            var exists = GfxAssets.Read(res, pair.Wave, out _);
            var h = s.Sound.PlayPair(id);
            if (exists && h != 0)
                ++pairOk;
            else if (!exists && h == 0)
                ++pairSilent;
            else
                Console.WriteLine($"  pair {id} {pair.Wave}: exists {exists}, handle {h}");
            s.Sound.Tick(0);
            s.Sound.StopAll();
            s.Sound.Tick(0);
        }
        Console.WriteLine($"pairs {s.Sound.Pairs.Count}: play {pairOk}, silent because the wave is missing {pairSilent}");

        // Sound datablocks come from a recording (the server sends them at join).
        var demo = FirstDemo(root);
        if (demo == null)
        {
            Console.WriteLine("no recording found: datablock census skipped");
            return failed == 0 ? 0 : 1;
        }
        var pb = new DemoPlayback(File.ReadAllBytes(demo));
        pb.StepUntilEnvironment();
        s.Sound.DataBlocks = pb.World.DataBlocks;
        int sounds = 0, ok = 0, missing = 0, loops = 0, threeD = 0, noProfile = 0;
        for (var i = 0; ; ++i)
        {
            if (!pb.World.DataBlocks.Blocks.ContainsKey((1, i)))
            {
                if (i > 400)
                    break;
                continue;
            }
            ++sounds;
            if (!s.Sound.TryDatablock(i, out var wave, out var p, out _))
            {
                ++noProfile;
                continue;
            }
            if (p.Loops)
                ++loops;
            if (p.Is3D)
                ++threeD;
            s.Sound.SetListener(0, 0, 0, 1, 0, 0);
            var h = s.Sound.PlayDatablock(i, new SoundXf(0, 1, 0));
            s.Sound.Tick(0);
            s.Mixer!.Render(spare, 1024);
            if (h != 0)
                ++ok;
            else
            { ++missing; Console.WriteLine($"  sound {i} {wave}: no voice"); }
            s.Sound.StopAll();
            s.Sound.Tick(0);
        }
        Console.WriteLine($"{Path.GetFileName(demo)}: sound profiles {pb.World.DataBlocks.Blocks.Keys.Count(k => k.Group == 0)}, sound datablocks {sounds}: voice started {ok}, no voice {missing}, unresolved {noProfile}; 3D {threeD}, looping {loops}");
        return failed == 0 && missing == 0 && noProfile == 0 ? 0 : 1;
    }

    // ---- live device ----

    /// <summary>Plays a wave on the live device.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="wave">The wave name.</param>
    /// <param name="seconds">How long to run.</param>
    /// <returns>The process exit code.</returns>
    public static int Live(string root, string wave, double seconds)
    {
        using var s = AudioSession.Create(root, live: true);
        var ok = s.RunSoundScripts();
        foreach (var l in s.Log.Take(4))
            Console.WriteLine("  log: " + l);
        if (s.Device == null || !s.Device.Opened)
        {
            Console.WriteLine("no audio device: " + (s.Device?.Error ?? "not opened") + "; the offline path stays the evidence");
            return 3;
        }
        Console.WriteLine("audio device opened (scripts ok " + ok + "): " + s.Device.Description);
        var h = s.Sound.PlayWave(wave);
        Console.WriteLine($"play {wave}: handle {h}");
        var t0 = Environment.TickCount64;
        var peakVoices = 0;
        while (Environment.TickCount64 - t0 < seconds * 1000)
        {
            s.Sound.Tick(Environment.TickCount64 - t0);
            peakVoices = Math.Max(peakVoices, s.Mixer!.ActiveCount);
            Thread.Sleep(16);
        }
        Console.WriteLine($"mixer frames rendered by the device callback: {s.Mixer!.FramesRendered}, peak voices {peakVoices}");
        return 0;
    }

    // ---- offline demo mix ----

    /// <summary>Mixes a span of a demo offline into a WAV file.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="demoPath">The demo file.</param>
    /// <param name="at">The start time in seconds, optionally followed by more comma separated values.</param>
    /// <param name="seconds">The length of the span.</param>
    /// <param name="outPath">The WAV file to write.</param>
    /// <returns>The process exit code.</returns>
    public static int RenderDemo(string root, string demoPath, string at, double seconds, string outPath)
    {
        var start = double.Parse(at.Split(',')[0], CultureInfo.InvariantCulture);
        var data = File.ReadAllBytes(demoPath);
        var pre = new DemoPlayback(data);
        pre.StepUntilEnvironment();
        var env = DemoRender.EnvironmentObjects(pre.World);
        var res = GfxAssets.Mount(root, out _, SearchPath);
        foreach (var o in env.Where(o => o.Class == "SimVolume"))
            res.AddVolume(o.Get("fileName"));
        using var s = AudioSession.Create(root, live: false, res);
        if (!s.RunSoundScripts())
        {
            Console.Error.WriteLine("tribes: the sound scripts failed");
            return 1;
        }
        var pal = env.FirstOrDefault(o => o.Class == "SimPalette");
        var bank = (pal != null ? GfxAssets.LoadPalette(res, pal.Get("fileName")) : null) ?? PaletteTables.Grayscale();
        var assets = new ShapeAssets(null, new WorldAssets(res, bank));
        var renderer = new ShapeRenderer(assets);
        Terrain? terrain = null;
        List<Material>? materials = null;
        var terr = env.FirstOrDefault(o => o.Class == "SimTerrain");
        if (terr != null && Terrain.Load(res, terr.Get("tedFileName")) is Terrain t)
        {
            var tp = terr.GetVector("position");
            t.SetPosition(new Vec3((float)tp[0], (float)tp[1], (float)tp[2]));
            terrain = t;
            var dml = t.MaterialListName();
            var dot = dml.LastIndexOf('.');
            materials = [];
            if (!res.Read((dot <= 0 ? dml : dml[..dot]) + ".dml", out var dm) || !MaterialList.ParseMaterialList(dm, materials))
                materials = null;
        }
        var play = new DemoPlayback(data);
        var model = new SceneModel(play.World, assets, renderer, terrain);
        s.Sound.DataBlocks = play.World.DataBlocks;
        var audio = new SceneAudio(model, s.Sound) { TerrainMaterials = materials };
        model.Audio = audio;
        play.OnPacket = model.OnPacket;
        play.OnFrame = model.Tick;

        var rate = s.Mixer!.DeviceRate;
        var end = start + seconds;
        var pcm = new List<short>((int)(seconds * rate * 2) + 4096);
        var stats = new List<string>();
        var block = new float[2 * (rate / 20)];
        long rendered = 0;
        int sec = 0, secMax = 0, secEnd = 0;
        float secPeak = 0;
        double clock = 0;
        var demoName = Path.GetFileNameWithoutExtension(demoPath);
        stats.Add($"# {demoName} from {start} s for {seconds} s, device rate {rate}");
        stats.Add("# second voices_max voices_end peak sounds_started_so_far");
        while (clock < end * 1000)
        {
            clock += 32;
            play.StepTo(clock);
            var pose = DemoRender.ControlCamera(model, play.World);
            if (pose is DemoRender.CameraPose c)
                audio.SetListener(c.X, c.Y, c.Z, c.YawDeg * Math.PI / 180);
            s.Sound.Tick((long)clock);
            var frames = (int)(Math.Round(clock * rate / 1000) - rendered);
            rendered += frames;
            if (block.Length < frames * 2)
                block = new float[frames * 2];
            s.Mixer.Render(block, frames);
            if (clock / 1000 < start)
                continue;
            for (var i = 0; i < frames * 2; ++i)
            {
                pcm.Add((short)Math.Clamp((int)Math.Round(block[i] * 32767), -32768, 32767));
                secPeak = Math.Max(secPeak, Math.Abs(block[i]));
            }
            secMax = Math.Max(secMax, s.Sound.PlayingCount);
            secEnd = s.Sound.PlayingCount;
            var cur = (int)((clock / 1000) - start);
            if (cur > sec)
            {
                stats.Add($"{sec}\t{secMax}\t{secEnd}\t{secPeak:F3}\t{audio.Started.Values.Sum()}");
                sec = cur;
                secMax = 0;
                secPeak = 0;
            }
        }
        var by = string.Join(", ", audio.Started.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));
        stats.Add($"# started: {by}");
        stats.Add($"# manager: played {s.Sound.Played}, rejected {s.Sound.Rejected}, waves loaded {s.Sound.WavesLoaded}, failed {s.Sound.WaveFailures}");
        WriteWav(outPath, [.. pcm], rate);
        File.WriteAllLines(outPath + ".stats.txt", stats);
        foreach (var l in stats)
            Console.WriteLine(l);
        Console.WriteLine($"wrote {outPath} ({pcm.Count / 2} frames at {rate} Hz)");
        return 0;
    }
}
