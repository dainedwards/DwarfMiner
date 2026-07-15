using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DwarfMiner.Systems;

/// <summary>
/// Per-phase frame profiler (DM_PERF=1): each instrumented phase records its ms into a
/// named bucket, and once a second the console gets one line per busy phase with
/// mean/worst-frame ms. This is the attribution layer DM_FPSLOG lacks — FPSLOG says a
/// frame was slow, PERF says which system ate it. Off (the default) every call is a
/// single branch on a static bool, so the instrumentation can stay in shipping code.
/// </summary>
public static class FramePerf
{
    public static readonly bool On =
        Environment.GetEnvironmentVariable("DM_PERF") is { Length: > 0 };

    private static readonly Dictionary<string, (double Sum, double Worst, int N)> _buckets = new();
    private static readonly List<string> _order = new();
    private static long _reportMark = Environment.TickCount64;
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>Phase start: grab a raw timestamp (0 when profiling is off).</summary>
    public static long Now() => On ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Phase end: bank the elapsed ms since <paramref name="t0"/> under
    /// <paramref name="phase"/>. Multiple Add calls per frame for one phase accumulate
    /// into that frame's worst-tracking as separate samples — fine for attribution.</summary>
    public static void Add(string phase, long t0)
    {
        if (!On) return;
        var ms = (Stopwatch.GetTimestamp() - t0) * TicksToMs;
        if (_buckets.TryGetValue(phase, out var b))
            _buckets[phase] = (b.Sum + ms, Math.Max(b.Worst, ms), b.N + 1);
        else
        {
            _buckets[phase] = (ms, ms, 1);
            _order.Add(phase);
        }
    }

    /// <summary>Once-per-second report, called from the FPS overlay's 1 Hz rollover so the
    /// lines land next to [fps]. Phases under 0.2 ms mean stay silent — the interesting
    /// output is what's actually eating the frame.</summary>
    public static void Report()
    {
        if (!On) return;
        var now = Environment.TickCount64;
        if (now - _reportMark < 1000) return;
        _reportMark = now;
        var sb = new StringBuilder("[perf]");
        foreach (var phase in _order)
        {
            var (sum, worst, n) = _buckets[phase];
            var mean = sum / Math.Max(1, n);
            if (mean < 0.2 && worst < 2.0) continue;
            sb.Append($"  {phase} {mean:0.0}/{worst:0.0}");
        }
        Console.WriteLine(sb.ToString());
        _buckets.Clear();
        _order.Clear();
    }
}
