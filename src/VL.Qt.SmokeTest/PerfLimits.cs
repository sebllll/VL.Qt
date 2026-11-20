using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

// Central handling of timing thresholds (issue #71). Functional checks (pixels, counts, allocations) do not go through here.
//   VLQT_PERF_FACTOR=<x>     multiplies every time limit (default 1; CI e.g. 3..5). < 1 tightens (proves the checks bite).
//   VLQT_PERF_WARN_ONLY=1    or --perf-warn-only: exceeded limits print "WARN" and do not fail the run.
//   --perf-factor <x>        same as the env var (flag wins).
// Defaults without env/flag (#132): factor 1.5 on arm64 (limits were set on x64; arm64 laptops land ~1.2x above),
// warn-only while a debugger is attached. VLQT_PERF_WARN_ONLY=0 forces hard failures even then.
// CPU-bound micro limits (Below(..., cpuBound: true), #64): the limits were set on a current Windows x64 desktop CPU. On Linux
// a short calibration loop measures the core speed (CpuFactor, 1 on a fast core, at most MaxCpuFactor) and scales only these
// limits, so an old CPU does not fail them while a real regression (several times the calibrated time) still does.
// VLQT_PERF_FACTOR / --perf-factor set the factor by hand: no calibration then.
static class PerfLimits
{
    public const double Arm64Factor = 1.5;

    // Calibration loop time (ms, best of several) of the reference core the CPU-bound limits were set for, and the cap of CpuFactor.
    const double CalibrationReferenceMs = 6.8; // ~5 GHz-class core: the loop is a dependent chain (8 cycles per step), its time follows the clock
    public const double MaxCpuFactor = 3.0;

    static bool s_factorSet;
    static double? s_cpuFactor;
    static uint s_sink;

    public static double Factor { get; private set; } = 1.0;
    public static bool WarnOnly { get; private set; }
    public static int Warnings { get; private set; }

    // Two different switches (P16/F4): WarnOnly = measure and report WARN instead of FAIL (the local gate and the CI set it).
    // SkipHeavy = do not run a long perf measurement at all: only on a shared CI runner (CI is set), where it is too slow and
    // too noisy to say anything. Locally every measurement runs, warn-only or not.
    public static bool SkipHeavy { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    // Parses and removes the perf flags from args, so positional arguments stay as before.
    public static string[] Configure(string[] args)
    {
        string? why = null;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) { Factor = Arm64Factor; why = "arm64 default"; }
        var env = Environment.GetEnvironmentVariable("VLQT_PERF_FACTOR");
        if (!string.IsNullOrWhiteSpace(env) && double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0) { Factor = f; why = null; s_factorSet = true; }
        var warn = Environment.GetEnvironmentVariable("VLQT_PERF_WARN_ONLY");
        if (!string.IsNullOrEmpty(warn)) WarnOnly = warn != "0";
        else if (Debugger.IsAttached) { WarnOnly = true; why = (why == null ? "" : why + ", ") + "debugger attached"; }

        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--perf-warn-only") WarnOnly = true;
            else if (args[i] == "--perf-factor" && i + 1 < args.Length
                && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var af) && af > 0) { Factor = af; i++; why = null; s_factorSet = true; }
            else rest.Add(args[i]);
        }
        if (Factor != 1.0 || WarnOnly)
            Console.WriteLine($"INFO perf thresholds: factor {Factor.ToString(CultureInfo.InvariantCulture)}, warn-only {WarnOnly}" + (why != null ? $" ({why})" : ""));
        return rest.ToArray();
    }

    // Speed of this machine's core relative to the reference core (>= 1, <= MaxCpuFactor); 1 on Windows and macOS and when the
    // factor was set by hand. Measured once, on first use (Linux only).
    public static double CpuFactor => s_cpuFactor ??= MeasureCpuFactor();

    static double MeasureCpuFactor()
    {
        if (!OperatingSystem.IsLinux() || s_factorSet) return 1.0;
        // Scalar integer work with dependent stores into a small buffer, like writing and applying a command buffer; no SIMD.
        var buf = new uint[16384];
        uint x = 0x9E3779B9;
        var best = double.MaxValue;
        for (int rep = 0; rep < 9; rep++)
        {
            var t = Stopwatch.GetTimestamp();
            for (int i = 0; i < 4_000_000; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                buf[x & 16383] += x;
            }
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        s_sink = buf[x & 16383]; // keeps the loop from being optimized away
        var f = Math.Clamp(best / CalibrationReferenceMs, 1.0, MaxCpuFactor);
        Console.WriteLine($"INFO perf calibration: reference loop {best:F2} ms (reference core {CalibrationReferenceMs:F2} ms), CPU-bound limits x{f.ToString("F2", CultureInfo.InvariantCulture)}");
        return f;
    }

    // Noise vs regression (#64): a median can land above the limit when the core clocks up late or another process wakes up
    // (one round ~ +45 %). Measures up to `rounds` times, stops at the first round within the limit and returns the best
    // value: a real regression is systematic and stays above the limit in every round.
    public static double Best(double limit, Func<double> measure, int rounds = 3, bool cpuBound = false)
    {
        var scale = Factor * (cpuBound ? CpuFactor : 1.0);
        var best = double.MaxValue;
        for (int r = 0; r < rounds && !(best < limit * scale); r++)
            best = Math.Min(best, measure());
        return best;
    }

    // value must stay below limit * Factor (same unit, usually ms). cpuBound: the limit also scales with the speed of the core (CpuFactor).
    public static void Below(Action<bool, string> check, double value, double limit, string what, bool cpuBound = false)
    {
        var scale = Factor * (cpuBound ? CpuFactor : 1.0);
        bool ok = value < limit * scale;
        var fx = scale.ToString("0.##", CultureInfo.InvariantCulture);
        if (ok || !WarnOnly) { check(ok, what + (scale != 1.0 ? $" [limit x{fx}]" : "")); return; }
        Warnings++;
        Console.WriteLine($"WARN {what} [limit x{fx} exceeded, warn-only]");
    }

    // Same for a converted scenario: a check, or an NUnit warning in warn-only mode.
    public static void Below(Scenario s, double value, double limit, string what, bool cpuBound = false)
    {
        var scale = Factor * (cpuBound ? CpuFactor : 1.0);
        bool ok = value < limit * scale;
        var fx = scale.ToString("0.##", CultureInfo.InvariantCulture);
        if (ok || !WarnOnly) { s.Check(ok, what + (scale != 1.0 ? $" [limit x{fx}]" : "")); return; }
        Warnings++;
        s.Warn($"{what} [limit x{fx} exceeded, warn-only]");
    }
}
