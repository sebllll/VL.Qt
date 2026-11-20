using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

/// <summary>
/// Legacy adapter (W1-01, temporary): the scenarios not yet converted to NUnit tests, in the order of the former
/// Program.Main, run through NUnit with their old delegate signatures. Each row is one test case named and categorized
/// by the old scenario name (`dotnet test --filter TestCategory=apply`). Converting a scenario = rewriting its file as
/// NUnit test(s) + deleting its row here (CONVERTING.md). The file goes when the table is empty.
/// </summary>
[TestFixture, NonParallelizable]
public static class LegacyScenarios
{
    [Flags]
    internal enum Kind
    {
        Normal = 0,
        /// <summary>Runs only when selected by a filter (was: only when named with --only, or a separate flag).</summary>
        Explicit = 1,
        /// <summary>Runs in a helper process (OwnProcess); <see cref="Row.Scenarios"/> are the names the child runs.</summary>
        OwnProcess = 2,
    }

    internal sealed record Row(string Name, Action<Scenario>? Body, Kind Kind = Kind.Normal)
    {
        public string[] Categories { get; init; } = Array.Empty<string>();
        public string[]? Scenarios { get; init; }
        public Dictionary<string, string>? Env { get; init; }
        public string? OutSubDir { get; init; }
        public string? ExpectInQtLog { get; init; }
    }

    internal static readonly Row[] All =
    {
        new("perfoverlay", s => PerfOverlayTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("resoverlay", s => PerfOverlayTest.RunResources(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("snapshot", s => SnapshotTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("tooltip", s => TooltipTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("group-frame", s => GroupFrameTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("qttexture", s => QtTextureTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
#if WINDOWS
        new("Msaa", s => MsaaTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
#endif
        new("multirenderer", s => MultiRendererTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)),
        new("QtTextureInput", s => QtTextureTest.RunInput(s.OutDir, s.Check, s.Pump)),
        // Leaves two VSync windows open on purpose (process end with open windows on the shared device).
        new("clear-pin", s => ClearPinTest.Run(s.OutDir, s.Check, s.Pump, s.Grab)) { Categories = new[] { "ClearPin" } },
        new("shared-device", s => SharedDeviceTest.Run(s.Check, s.Pump)),

        // Only on request. About a minute; the render thread count is fixed at the first scene: own process (#116 5.4).
        new("qttextureperf", s => QtTexturePerfTest.Run(s.Check, s.Pump), Kind.Explicit | Kind.OwnProcess),
        // Former --overlay-cost [rounds]: F5 overlay pump cost (#244), parameter OverlayCostRounds.
        new("overlay-cost", s => s.Check(PerfOverlayTest.RunCost(s.Pump, SmokeParameters.OverlayCostRounds) == 0, "overlay-cost"), Kind.Explicit),

        // QtTexture in both thread modes, each in a process of its own with D3D11On12 (device-loss parts) and the debug layer
        // (leak check) from the start (former second and third run of build.ps1 -Test).
        new("qttexture-render-thread", null, Kind.OwnProcess)
        {
            Scenarios = new[] { "qttexture", "QtTextureInput" },
            Env = QtTextureEnv(null),
            OutSubDir = "qttexture-render-thread",
            ExpectInQtLog = "offscreen scenes render on 1 Qt render thread(s)",
        },
        new("qttexture-main-thread", null, Kind.OwnProcess)
        {
            Scenarios = new[] { "qttexture", "QtTextureInput" },
            Env = QtTextureEnv("0"),
            OutSubDir = "qttexture-main-thread",
            ExpectInQtLog = "offscreen scenes render on the main thread",
        },
    };

    static Dictionary<string, string> QtTextureEnv(string? threads)
    {
        var env = new Dictionary<string, string> { ["VLQT_TEST_D3D11ON12"] = "1", ["VLQT_SMOKE_DEBUG_LAYER"] = D3DDebugLayerAvailable() ? "1" : "0" };
        if (threads != null)
            env["VLQT_OFFSCREEN_THREAD"] = threads;
        return env;
    }

    // The leak test reads ReportLiveDeviceObjects of QtNative's device, which needs the D3D SDK layers.
    static bool D3DDebugLayerAvailable() => OperatingSystem.IsWindows()
        && (System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "d3d11sdklayers.dll"))
            || System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "d3d11_3sdklayers.dll")));

    /// <summary>Body of a legacy row (helper executable), or null.</summary>
    internal static Action<Scenario>? Find(string name) =>
        All.FirstOrDefault(r => r.Body != null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.Body;

    static IEnumerable<TestCaseData> Cases(bool ownProcess)
    {
        foreach (var row in All.Where(r => r.Kind.HasFlag(Kind.OwnProcess) == ownProcess))
        {
            var data = new TestCaseData(row.Name).SetName(row.Name).SetCategory(row.Name);
            foreach (var c in row.Categories)
                data.SetCategory(c);
            if (ownProcess)
                data.SetCategory("own-process");
            if (row.Kind.HasFlag(Kind.Explicit))
                data.Explicit("runs only when selected");
            yield return data;
        }
    }

    static IEnumerable<TestCaseData> InProcessCases() => Cases(false);
    static IEnumerable<TestCaseData> OwnProcessCases() => Cases(true);

    /// <summary>A not yet converted scenario, in the shared Qt application of the run.</summary>
    [TestCaseSource(nameof(InProcessCases))]
    public static void Legacy(string name)
    {
        var row = All.First(r => r.Name == name);
        Scenario.Run(name, row.Body!);
    }

    /// <summary>A not yet converted scenario that needs a process of its own.</summary>
    [TestCaseSource(nameof(OwnProcessCases))]
    public static void LegacyOwnProcess(string name)
    {
        var row = All.First(r => r.Name == name);
        var outDir = row.OutSubDir != null ? System.IO.Path.Combine(SmokeParameters.SmokeDir, row.OutSubDir, "grabs") : null;
        Scenario.RunLocal(s => OwnProcess.Run(s, row.Scenarios ?? new[] { row.Name }, row.Env, outDir, row.ExpectInQtLog));
    }
}
