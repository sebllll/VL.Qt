using System.IO;
using NUnit.Framework;

/// <summary>
/// Once per test run (global namespace: applies to the whole assembly): perf thresholds from the run parameters before the
/// first scenario; at the end the Qt log of the run goes to &lt;SmokeDir&gt;/smoke.log. Qt itself starts on first use
/// (QtHost.EnsureStarted), so a run of GPU-free tests only (drift) never starts it.
/// </summary>
[SetUpFixture]
public sealed class SmokeSetup
{
    [OneTimeSetUp]
    public void Setup() => PerfLimits.Configure(SmokeParameters.PerfArgs());

    [OneTimeTearDown]
    public void TearDown()
    {
        if (QtHost.LogLength == 0)
            return;
        Directory.CreateDirectory(SmokeParameters.SmokeDir);
        File.WriteAllText(Path.Combine(SmokeParameters.SmokeDir, "smoke.log"), QtHost.Log);
        if (PerfLimits.Warnings > 0)
            TestContext.Progress.WriteLine($"WARN {PerfLimits.Warnings} perf check(s) exceeded their limit (warn-only)");
    }
}
