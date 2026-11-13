namespace OpenTribes.Platform.Gfx;

using System.Diagnostics;
using System.Text;

/// <summary>Counters a device can expose to the frame timer.</summary>
public interface IGfxStats
{
    /// <summary>Gets the draw calls submitted so far.</summary>
    public long DrawCalls { get; }
    /// <summary>Gets the triangles submitted so far.</summary>
    public long Triangles { get; }
}

/// <summary>The frame timing log for play-tests. <c>OT_PERF=N</c>, the frames per report, turns it on.</summary>
/// <remarks>Sections are timed with <see cref="Scope"/>. Each also records the draw calls and triangles the device saw while it was open. One line per report: frame time, allocation and collections per frame, then ms, draws and triangles per section.</remarks>
public static class FramePerf
{
    private sealed class Acc
    {
        public double Ms { get; set; }
        public long Draws { get; set; }
        public long Tris { get; set; }
        public long Count { get; set; }
        public long Bytes { get; set; }
    }

    private static readonly Dictionary<string, Acc> Sections = [];
    private static readonly List<string> Order = [];
    private static readonly List<double> FrameMs = [];
    private static readonly StringBuilder FrameDetail = new();
    private static int _gcAtFrame;
    private static TimeSpan _pauseAtFrame, _pauseStart;
    private const double SpikeMs = 30;
    private static long _frameStart, _allocStart;
    private static int _gc0, _gc1, _gc2;
    private static int _frames;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>Gets the number of frames per report, 0 when off.</summary>
    public static int ReportEvery { get; private set; } = ParseEvery();
    /// <summary>Gets whether the log is on.</summary>
    public static bool On => ReportEvery > 0;
    /// <summary>The device whose draw counters the sections record.</summary>
    public static IGfxStats? Device { get; set; }
    /// <summary>The writer the reports go to.</summary>
    public static TextWriter Out { get; set; } = Console.Error;

    private static int ParseEvery() => int.TryParse(Environment.GetEnvironmentVariable("OT_PERF"), out var n) && n > 0 ? n : 0;

    /// <summary>Turns the log on.</summary>
    /// <param name="every">The number of frames per report.</param>
    public static void Enable(int every) => ReportEvery = every;

    /// <summary>A timed section. Dispose it to record the time.</summary>
    public readonly struct Section : IDisposable
    {
        private readonly string? _name;
        private readonly long _start, _draws, _tris, _bytes;

        /// <summary>Starts a section.</summary>
        /// <param name="name">The section name.</param>
        public Section(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
            _draws = Device?.DrawCalls ?? 0;
            _tris = Device?.Triangles ?? 0;
            _bytes = GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Records the section.</summary>
        public void Dispose()
        {
            if (_name == null)
                return;
            var end = Stopwatch.GetTimestamp();
            if (!Sections.TryGetValue(_name, out var a))
            {
                Sections[_name] = a = new Acc();
                Order.Add(_name);
            }
            a.Ms += (end - _start) * TickMs;
            a.Draws += (Device?.DrawCalls ?? 0) - _draws;
            a.Tris += (Device?.Triangles ?? 0) - _tris;
            a.Bytes += GC.GetAllocatedBytesForCurrentThread() - _bytes;
            FrameDetail.Append($" {_name} {(end - _start) * TickMs:F1}");
            ++a.Count;
        }
    }

    /// <summary>Starts a timed section.</summary>
    /// <param name="name">The section name.</param>
    /// <returns>The section, or a no-op value when the log is off.</returns>
    public static Section Scope(string name) => On ? new Section(name) : default;

    /// <summary>Marks the start of a frame. Call it once at the top of every frame. The previous frame is closed and, every <see cref="ReportEvery"/> frames, reported.</summary>
    public static void FrameStart()
    {
        if (!On)
            return;
        var now = Stopwatch.GetTimestamp();
        if (_frameStart != 0)
        {
            var ms = (now - _frameStart) * TickMs;
            FrameMs.Add(ms);
            if (ms > SpikeMs)
                Out.WriteLine($"perf spike {ms:F1} ms (gc2 +{GC.CollectionCount(2) - _gcAtFrame}, gc pause {(GC.GetTotalPauseDuration() - _pauseAtFrame).TotalMilliseconds:F1} ms):{FrameDetail}");
        }
        FrameDetail.Clear();
        _gcAtFrame = GC.CollectionCount(2);
        _pauseAtFrame = GC.GetTotalPauseDuration();
        _frameStart = now;
        if (FrameMs.Count >= ReportEvery)
            Report();
        if (FrameMs.Count == 0 && _frames == 0)
        {
            _allocStart = GC.GetAllocatedBytesForCurrentThread();
            (_gc0, _gc1, _gc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            _pauseStart = GC.GetTotalPauseDuration();
        }
        ++_frames;
    }

    private static void Report()
    {
        var n = FrameMs.Count;
        double sum = 0;
        foreach (var d in FrameMs)
            sum += d;
        FrameMs.Sort();
        double avg = sum / n, p95 = FrameMs[Math.Min(n - 1, (int)(n * 0.95))], max = FrameMs[n - 1];
        var alloc = GC.GetAllocatedBytesForCurrentThread() - _allocStart;
        var sb = new StringBuilder();
        sb.Append($"perf {n} frames: {avg:F2} ms avg ({1000 / avg:F0} fps) p95 {p95:F2} max {max:F2} | alloc {alloc / (double)n / 1024:F0} KB/frame gc {GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2} pause {(GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds / n:F2} ms/frame");
        foreach (var name in Order)
        {
            var a = Sections[name];
            sb.Append($" | {name} {a.Ms / n:F2}ms");
            if (a.Draws > 0 || a.Tris > 0)
                sb.Append($" {a.Draws / (double)n:F0}dc {a.Tris / (double)n:F0}tri");
            if (a.Bytes > 0)
                sb.Append($" {a.Bytes / (double)n / 1024:F0}KB");
            a.Ms = 0;
            a.Draws = a.Tris = a.Count = a.Bytes = 0;
        }
        Out.WriteLine(sb.ToString());
        FrameMs.Clear();
        _allocStart = GC.GetAllocatedBytesForCurrentThread();
        (_gc0, _gc1, _gc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        _pauseStart = GC.GetTotalPauseDuration();
    }
}
