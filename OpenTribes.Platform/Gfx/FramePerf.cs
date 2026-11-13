using System.Diagnostics;
using System.Text;

namespace OpenTribes.Platform.Gfx;

/// <summary>Counters a device can expose to the frame timer.</summary>
public interface IGfxStats
{
    /// <summary>Gets the draw calls submitted so far.</summary>
    long DrawCalls { get; }
    /// <summary>Gets the triangles submitted so far.</summary>
    long Triangles { get; }
}

/// <summary>The frame timing log for play-tests. <c>OT_PERF=N</c>, the frames per report, turns it on.</summary>
/// <remarks>Sections are timed with <see cref="Scope"/>. Each also records the draw calls and triangles the device saw while it was open. One line per report: frame time, allocation and collections per frame, then ms, draws and triangles per section.</remarks>
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

    /// <summary>Gets the number of frames per report, 0 when off.</summary>
    public static int ReportEvery { get; private set; } = ParseEvery();
    /// <summary>Gets whether the log is on.</summary>
    public static bool On => ReportEvery > 0;
    /// <summary>The device whose draw counters the sections record.</summary>
    public static IGfxStats? Device;
    /// <summary>The writer the reports go to.</summary>
    public static TextWriter Out = Console.Error;

    private static int ParseEvery() => int.TryParse(Environment.GetEnvironmentVariable("OT_PERF"), out int n) && n > 0 ? n : 0;

    /// <summary>Turns the log on.</summary>
    /// <param name="every">The number of frames per report.</param>
    public static void Enable(int every) => ReportEvery = every;

    /// <summary>A timed section. Dispose it to record the time.</summary>
    public readonly struct Section : IDisposable
    {
        private readonly string? mName;
        private readonly long mStart, mDraws, mTris, mBytes;

        /// <summary>Starts a section.</summary>
        /// <param name="name">The section name.</param>
        public Section(string name)
        {
            mName = name;
            mStart = Stopwatch.GetTimestamp();
            mDraws = Device?.DrawCalls ?? 0;
            mTris = Device?.Triangles ?? 0;
            mBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Records the section.</summary>
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

    /// <summary>Starts a timed section.</summary>
    /// <param name="name">The section name.</param>
    /// <returns>The section, or a no-op value when the log is off.</returns>
    public static Section Scope(string name) => On ? new Section(name) : default;

    /// <summary>Marks the start of a frame. Call it once at the top of every frame. The previous frame is closed and, every <see cref="ReportEvery"/> frames, reported.</summary>
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
