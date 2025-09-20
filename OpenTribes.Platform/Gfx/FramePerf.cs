using System.Diagnostics;
using System.Text;

namespace OpenTribes.Platform.Gfx;

// Counters a device can expose to the frame timer: draw calls and triangles submitted so far.
public interface IGfxStats
{
    long DrawCalls { get; }
    long Triangles { get; }
}

// Frame timing log for play-tests: `OT_PERF=N` (frames per report) turns it on. Sections are timed with `Scope`, each one also
// records the draw calls and triangles the device saw while it was open. One line per report: frame time, allocation and
// collections per frame, then ms, draws and triangles per section.
public static class FramePerf
{
    private sealed class Acc
    {
        public double Ms;
        public long Draws, Tris, Count, Bytes;
    }

    private static readonly Dictionary<string, Acc> sSections = new();
    private static readonly List<string> sOrder = new();
    private static readonly List<double> sFrameMs = new();
    private static readonly StringBuilder sFrameDetail = new();
    private static int sGcAtFrame;
    private static TimeSpan sPauseAtFrame, sPauseStart;
    private const double SpikeMs = 30;
    private static long sFrameStart, sAllocStart;
    private static int sGc0, sGc1, sGc2;
    private static int sFrames;
    private static readonly double sTickMs = 1000.0 / Stopwatch.Frequency;

    public static int ReportEvery { get; private set; } = ParseEvery();
    public static bool On => ReportEvery > 0;
    public static IGfxStats? Device;
    public static TextWriter Out = Console.Error;

    private static int ParseEvery() => int.TryParse(Environment.GetEnvironmentVariable("OT_PERF"), out int n) && n > 0 ? n : 0;

    public static void Enable(int every) => ReportEvery = every;

    public readonly struct Section : IDisposable
    {
        private readonly string? mName;
        private readonly long mStart, mDraws, mTris, mBytes;

        public Section(string name)
        {
            mName = name;
            mStart = Stopwatch.GetTimestamp();
            mDraws = Device?.DrawCalls ?? 0;
            mTris = Device?.Triangles ?? 0;
            mBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (mName == null)
                return;
            long end = Stopwatch.GetTimestamp();
            if (!sSections.TryGetValue(mName, out Acc? a))
            {
                sSections[mName] = a = new Acc();
                sOrder.Add(mName);
            }
            a.Ms += (end - mStart) * sTickMs;
            a.Draws += (Device?.DrawCalls ?? 0) - mDraws;
            a.Tris += (Device?.Triangles ?? 0) - mTris;
            a.Bytes += GC.GetAllocatedBytesForCurrentThread() - mBytes;
            sFrameDetail.Append($" {mName} {(end - mStart) * sTickMs:F1}");
            ++a.Count;
        }
    }

    public static Section Scope(string name) => On ? new Section(name) : default;

    // Call once at the top of every frame; the previous frame is closed and, every ReportEvery frames, reported.
    public static void FrameStart()
    {
        if (!On)
            return;
        long now = Stopwatch.GetTimestamp();
        if (sFrameStart != 0)
        {
            double ms = (now - sFrameStart) * sTickMs;
            sFrameMs.Add(ms);
            if (ms > SpikeMs)
                Out.WriteLine($"perf spike {ms:F1} ms (gc2 +{GC.CollectionCount(2) - sGcAtFrame}, gc pause {(GC.GetTotalPauseDuration() - sPauseAtFrame).TotalMilliseconds:F1} ms):{sFrameDetail}");
        }
        sFrameDetail.Clear();
        sGcAtFrame = GC.CollectionCount(2);
        sPauseAtFrame = GC.GetTotalPauseDuration();
        sFrameStart = now;
        if (sFrameMs.Count >= ReportEvery)
            Report();
        if (sFrameMs.Count == 0 && sFrames == 0)
        {
            sAllocStart = GC.GetAllocatedBytesForCurrentThread();
            (sGc0, sGc1, sGc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            sPauseStart = GC.GetTotalPauseDuration();
        }
        ++sFrames;
    }

    private static void Report()
    {
        int n = sFrameMs.Count;
        double sum = 0;
        foreach (double d in sFrameMs)
            sum += d;
        sFrameMs.Sort();
        double avg = sum / n, p95 = sFrameMs[Math.Min(n - 1, (int)(n * 0.95))], max = sFrameMs[n - 1];
        long alloc = GC.GetAllocatedBytesForCurrentThread() - sAllocStart;
        var sb = new StringBuilder();
        sb.Append($"perf {n} frames: {avg:F2} ms avg ({1000 / avg:F0} fps) p95 {p95:F2} max {max:F2} | alloc {alloc / (double)n / 1024:F0} KB/frame gc {GC.CollectionCount(0) - sGc0}/{GC.CollectionCount(1) - sGc1}/{GC.CollectionCount(2) - sGc2} pause {(GC.GetTotalPauseDuration() - sPauseStart).TotalMilliseconds / n:F2} ms/frame");
        foreach (string name in sOrder)
        {
            Acc a = sSections[name];
            sb.Append($" | {name} {a.Ms / n:F2}ms");
            if (a.Draws > 0 || a.Tris > 0)
                sb.Append($" {a.Draws / (double)n:F0}dc {a.Tris / (double)n:F0}tri");
            if (a.Bytes > 0)
                sb.Append($" {a.Bytes / (double)n / 1024:F0}KB");
            a.Ms = 0;
            a.Draws = a.Tris = a.Count = a.Bytes = 0;
        }
        Out.WriteLine(sb.ToString());
        sFrameMs.Clear();
        sAllocStart = GC.GetAllocatedBytesForCurrentThread();
        (sGc0, sGc1, sGc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        sPauseStart = GC.GetTotalPauseDuration();
    }
}
