using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Scenarios that need a process of their own (device loss needs D3D11On12 before the first window, QtTexture runs in both
/// thread modes, the render-thread count is fixed at the first scene): the NUnit test starts this assembly as helper
/// executable (Program.Main, `--scenario a,b`) and mirrors the child's check lines into its own result.
/// </summary>
static class OwnProcess
{
    // Scenarios the helper can run (name -> body).
    static readonly Dictionary<string, Action<Scenario>> s_bodies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qttexture"] = QtTextureTest.Run,
        ["QtTextureInput"] = QtTextureTest.RunInput,
        ["qttextureperf"] = QtTexturePerfTest.Run,
#if WINDOWS
        ["device-lost"] = DeviceLostTest.Run,
#endif
    };

    /// <summary>Body of a scenario the helper executable can run, or null.</summary>
    public static Action<Scenario>? Find(string name) =>
        s_bodies.TryGetValue(name, out var body) ? body : null;

    /// <summary>
    /// Runs <paramref name="scenarios"/> (in this order, in one fresh process) and records their checks in <paramref name="s"/>.
    /// </summary>
    /// <param name="s">Scenario of the calling NUnit test (created by <see cref="Scenario.RunLocal"/>).</param>
    /// <param name="scenarios">Names the helper runs (see <see cref="Find"/>).</param>
    /// <param name="env">Extra environment of the child, e.g. VLQT_TEST_D3D11ON12=1, VLQT_OFFSCREEN_THREAD=0.</param>
    /// <param name="outDir">Grab folder of the child (default: the run's OutDir).</param>
    /// <param name="expectInQtLog">Text the child's Qt log must contain (e.g. the thread-mode line), checked after the run.</param>
    public static void Run(Scenario s, string[] scenarios, IReadOnlyDictionary<string, string>? env = null, string? outDir = null, string? expectInQtLog = null)
    {
        var label = string.Join("+", scenarios);
        var qtLog = Path.Combine(SmokeParameters.SmokeDir, $"{s.Name}.log");
        Directory.CreateDirectory(SmokeParameters.SmokeDir);
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "VL.Qt.SmokeTest.exe" : "VL.Qt.SmokeTest");
        var dll = Path.Combine(AppContext.BaseDirectory, "VL.Qt.SmokeTest.dll");
        var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { dll } };
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add(string.Join(",", scenarios));
        psi.WorkingDirectory = AppContext.BaseDirectory;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        foreach (var (k, v) in SmokeParameters.ForChild())
            psi.Environment[k] = v;
        psi.Environment["VLQT_SMOKE_QTLOG"] = qtLog;
        if (outDir != null)
            psi.Environment["VLQT_SMOKE_OUTDIR"] = outDir;
        if (env != null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        s.Info($"helper process: {label} (Qt log {qtLog})");
        var done = false;
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            if (e.Data.StartsWith(Program.DoneLine, StringComparison.Ordinal))
                done = true;
            else
                s.OnConsoleLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                s.OnConsoleLine("stderr: " + e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(SmokeParameters.ScenarioTimeoutSeconds * 1000))
        {
            p.Kill(true);
            p.WaitForExit();
            s.Check(false, $"helper process {label} did not end within {SmokeParameters.ScenarioTimeoutSeconds} s (killed)");
            return;
        }
        p.WaitForExit(); // flushes the asynchronous readers
        if (!done)
            s.Check(false, $"helper process {label} ended without a result (exit code {p.ExitCode}): crash?");
        else if (p.ExitCode != 0 && s.Failures.Count == 0)
            s.Check(false, $"helper process {label}: exit code {p.ExitCode}");
        if (expectInQtLog != null)
        {
            var log = File.Exists(qtLog) ? File.ReadAllText(qtLog) : "";
            s.Check(log.Contains(expectInQtLog), $"Qt log shows '{expectInQtLog}'");
        }
    }
}
