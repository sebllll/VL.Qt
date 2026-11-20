using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;

/// <summary>
/// Options of a SmokeTest run (the former command line of the custom runner). In `dotnet test` they come from the run
/// settings (`-- TestRunParameters.Parameter(name="StressSeconds", value="10")` or a .runsettings file), else from the
/// environment variable VLQT_SMOKE_&lt;NAME&gt; (e.g. VLQT_SMOKE_OUTDIR); the helper executable gets them from its parent
/// through the environment.
/// </summary>
internal static class SmokeParameters
{
    /// <summary>Folder of the run's files: smoke.log (Qt log), checks.tsv (one line per check). Default &lt;repo&gt;/build/smoke.</summary>
    public static string SmokeDir => Get("SmokeDir") ?? DefaultSmokeDir();

    /// <summary>Folder of the grabs (PNG files keep their former names). Default &lt;SmokeDir&gt;/grabs.</summary>
    public static string OutDir => Get("OutDir") ?? Path.Combine(SmokeDir, "grabs");

    /// <summary>resource-stress: duration in seconds (#365).</summary>
    public static int StressSeconds => GetInt("StressSeconds") ?? ResourceStressTest.DefaultSeconds;

    /// <summary>resource-stress: seed; without one a random seed (logged by the scenario).</summary>
    public static int StressSeed => s_stressSeed ??= GetInt("StressSeed") ?? Environment.TickCount;
    private static int? s_stressSeed;

    /// <summary>Seconds a scenario may take before the Qt thread counts as blocked (default 600).</summary>
    public static int ScenarioTimeoutSeconds => GetInt("ScenarioTimeoutSeconds") ?? 600;

    /// <summary>Flags of the manual tearing scenario, e.g. "--windows 2 --interval 0 --seconds 10" (#90).</summary>
    public static string[] TearingArgs => Split(Get("TearingArgs"));

    /// <summary>replay-cli: trace file (01e) and its flags, e.g. "--loop 3 --paced".</summary>
    public static string? ReplayTrace => Get("ReplayTrace");
    public static string[] ReplayArgs => Split(Get("ReplayArgs"));

    /// <summary>Image compare scenarios (reference-scenes, readme-images, readme-gallery): write the references instead of comparing (1/true).</summary>
    public static bool UpdateRefs => Get("UpdateRefs") is "1" or "true";

    /// <summary>readme-images / readme-gallery: write the README pictures into docs/images (normal renderer) instead of comparing (1/true).</summary>
    public static bool DocsImages => Get("DocsImages") is "1" or "true";

    /// <summary>overlay-cost: rounds of 120 pumps per mode (#244, default 8).</summary>
    public static int OverlayCostRounds => GetInt("OverlayCostRounds") ?? 8;

    /// <summary>Arguments for PerfLimits.Configure: PerfFactor and PerfWarnOnly (VLQT_PERF_FACTOR / VLQT_PERF_WARN_ONLY still work).</summary>
    public static string[] PerfArgs()
    {
        var args = new List<string>();
        if (Get("PerfWarnOnly") is "1" or "true")
            args.Add("--perf-warn-only");
        if (Get("PerfFactor") is { } f)
            args.AddRange(new[] { "--perf-factor", f });
        return args.ToArray();
    }

    /// <summary>Environment of a helper process: every parameter of this run as VLQT_SMOKE_&lt;NAME&gt;.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ForChild()
    {
        foreach (var name in s_names)
            if (Get(name) is { } v)
                yield return new(EnvName(name), v);
    }

    private static readonly string[] s_names = { "SmokeDir", "OutDir", "StressSeconds", "StressSeed", "ScenarioTimeoutSeconds", "TearingArgs", "ReplayTrace", "ReplayArgs", "OverlayCostRounds", "PerfWarnOnly", "PerfFactor", "UpdateRefs", "DocsImages" };

    private static string EnvName(string name) => "VLQT_SMOKE_" + name.ToUpperInvariant();

    private static string? Get(string name)
    {
        string? value = null;
        try
        {
            value = TestContext.Parameters.Get(name);
        }
        catch (Exception)
        {
            // Helper executable: no NUnit context.
        }
        return string.IsNullOrEmpty(value) ? Environment.GetEnvironmentVariable(EnvName(name)) is { Length: > 0 } e ? e : null : value;
    }

    private static int? GetInt(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string[] Split(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    // <repo>/build/smoke: the folder above the build output that holds src/VL.Qt.SmokeTest; next to the assembly otherwise.
    private static string DefaultSmokeDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "VL.Qt.SmokeTest")))
                return Path.Combine(dir.FullName, "build", "smoke");
        return Path.Combine(AppContext.BaseDirectory, "smoke");
    }
}
