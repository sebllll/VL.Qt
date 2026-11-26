using System;
using System.Globalization;
using NUnit.Framework;

namespace VL.Qt.Tests.Support
{
    /// <summary>
    /// Time limits of the <c>Perf</c> category. Same semantics as the SmokeTest's <c>PerfLimits</c>:
    /// <c>VLQT_PERF_FACTOR=&lt;x&gt;</c> multiplies every limit (&lt; 1 tightens, proves that the check bites),
    /// <c>VLQT_PERF_WARN_ONLY=1</c> turns an exceeded limit into a warning (tools/build.ps1, tools/test-linux.sh and the CI set it).
    /// Allocation checks do not go through here: they are deterministic and stay hard.
    /// </summary>
    internal static class PerfLimit
    {
        public static double Factor { get; } = ReadFactor();

        public static bool WarnOnly { get; } = Environment.GetEnvironmentVariable("VLQT_PERF_WARN_ONLY") is { Length: > 0 } w && w != "0";

        private static double ReadFactor()
        {
            var env = Environment.GetEnvironmentVariable("VLQT_PERF_FACTOR");
            return !string.IsNullOrWhiteSpace(env) && double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1.0;
        }

        /// <summary>Requires <paramref name="value"/> to stay below <paramref name="limit"/> times the factor (same unit, usually ms);
        /// warns instead of failing in warn-only mode.</summary>
        public static void Below(double value, double limit, string what)
        {
            var scaled = limit * Factor;
            if (value < scaled)
                return;
            var message = string.Format(CultureInfo.InvariantCulture, "{0}: {1:F4} >= limit {2:F4} (x{3:0.##})", what, value, scaled, Factor);
            if (WarnOnly)
            {
                TestContext.Out.WriteLine("WARN " + message);
                Assert.Warn(message);
            }
            else
                Assert.Fail(message);
        }
    }
}
