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
static class PerfLimits
{
    public const double Arm64Factor = 1.5;

    public static double Factor { get; private set; } = 1.0;
    public static bool WarnOnly { get; private set; }
    public static int Warnings { get; private set; }

    // Parses and removes the perf flags from args, so positional arguments stay as before.
    public static string[] Configure(string[] args)
    {
        string? why = null;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) { Factor = Arm64Factor; why = "arm64 default"; }
        var env = Environment.GetEnvironmentVariable("VLQT_PERF_FACTOR");
        if (!string.IsNullOrWhiteSpace(env) && double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0) { Factor = f; why = null; }
        var warn = Environment.GetEnvironmentVariable("VLQT_PERF_WARN_ONLY");
        if (!string.IsNullOrEmpty(warn)) WarnOnly = warn != "0";
        else if (Debugger.IsAttached) { WarnOnly = true; why = (why == null ? "" : why + ", ") + "debugger attached"; }

        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--perf-warn-only") WarnOnly = true;
            else if (args[i] == "--perf-factor" && i + 1 < args.Length
                && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var af) && af > 0) { Factor = af; i++; why = null; }
            else rest.Add(args[i]);
        }
        if (Factor != 1.0 || WarnOnly)
            Console.WriteLine($"INFO perf thresholds: factor {Factor.ToString(CultureInfo.InvariantCulture)}, warn-only {WarnOnly}" + (why != null ? $" ({why})" : ""));
        return rest.ToArray();
    }

    // value must stay below limit * Factor (same unit, usually ms).
    public static void Below(Action<bool, string> check, double value, double limit, string what)
    {
        bool ok = value < limit * Factor;
        var fx = Factor.ToString(CultureInfo.InvariantCulture);
        if (ok || !WarnOnly) { check(ok, what + (Factor != 1.0 ? $" [limit x{fx}]" : "")); return; }
        Warnings++;
        Console.WriteLine($"WARN {what} [limit x{fx} exceeded, warn-only]");
    }
}
