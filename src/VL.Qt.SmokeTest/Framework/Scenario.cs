using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;

/// <summary>
/// Context of one running scenario: soft checks, warnings, the frame pump, grabs and the Qt log. Replaces the delegates
/// (check, pump, grab, log) of the former runner. All checks of a scenario run; failures are collected and reported
/// together at the end (one NUnit failure entry each). Every check also writes one line (PASS/FAIL/WARN/SKIP + message)
/// to the test output and to &lt;SmokeDir&gt;/checks.tsv (scenario, status, message).
/// </summary>
internal sealed class Scenario
{
    private static readonly object s_syncRoot = new();
    private static bool s_tsvStarted;
    private static bool s_perfStarted;

    private readonly TextWriter _out;
    private readonly List<string> _failures = new();
    private readonly List<string> _warnings = new();

    private Scenario(string name, TextWriter output)
    {
        Name = name;
        _out = output;
    }

    /// <summary>Scenario name (the former --only name, also the NUnit category): `dotnet test --filter TestCategory=&lt;Name&gt;`.</summary>
    public string Name { get; }

    /// <summary>Folder of the grabs (exists).</summary>
    public string OutDir => SmokeParameters.OutDir;

    /// <summary>The Qt log of the whole run so far.</summary>
    public string Log => QtHost.Log;

    /// <summary>The active Qt Quick backend is the software one (no QRhi).</summary>
    public bool QuickSoftware => QtHost.QuickSoftware;

    public IReadOnlyList<string> Failures => _failures;
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Soft check: records a failure and goes on.</summary>
    public void Check(bool ok, string what)
    {
        lock (s_syncRoot)
        {
            if (!ok)
                _failures.Add(what);
            Write(ok ? "PASS" : "FAIL", what);
        }
    }

    /// <summary>Warning (a perf limit in warn-only mode, a plausibility note): NUnit warning, the test does not fail.</summary>
    public void Warn(string what)
    {
        lock (s_syncRoot)
        {
            _warnings.Add(what);
            Write("WARN", what);
        }
    }

    /// <summary>A check that cannot run here, with the reason.</summary>
    public void Skip(string what) => Write("SKIP", what);

    /// <summary>Measurement or note for the log (no check).</summary>
    public void Info(string what) => Write("INFO", what);

    /// <summary>
    /// Measured value for the perf baseline (tools/perf-baseline.ps1): one "PERF" line in the test output and one line
    /// (scenario, name, value, unit) in &lt;SmokeDir&gt;/perf.tsv. Not a check; it never fails a scenario.
    /// </summary>
    public void Perf(string name, double value, string unit = "ms")
    {
        var v = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        lock (s_syncRoot)
        {
            _out.WriteLine($"PERF {name} {v} {unit}");
            if (!WriteTsv)
                return;
            try
            {
                var path = Path.Combine(SmokeParameters.SmokeDir, "perf.tsv");
                if (!s_perfStarted)
                {
                    Directory.CreateDirectory(SmokeParameters.SmokeDir);
                    File.WriteAllText(path, "");
                    s_perfStarted = true;
                }
                File.AppendAllText(path, $"{Name}\t{name}\t{v}\t{unit}\n");
            }
            catch (IOException)
            {
                // Convenience output like checks.tsv.
            }
        }
    }

    /// <summary>Check that needs a QRhi scenegraph (Shader/MultiEffect, video output): SKIP under the software backend.</summary>
    public void NeedsRhi(bool ok, string what, string why)
    {
        if (QtHost.QuickSoftware)
            Skip($"{what}: {why} (Qt Quick software backend)");
        else
            Check(ok, what);
    }

    /// <summary>
    /// Check on where a window ends up: under WSLg/xcb the window manager chooses the position of a new window and the
    /// position after leaving fullscreen, so a miss there is a SKIP with reason. A pass counts everywhere.
    /// </summary>
    public void CheckPosition(bool ok, string what)
    {
        if (!ok && QtHost.WslgXcb)
            Skip($"{what}: position chosen by the WSLg window manager (Weston/XWayland)");
        else
            Check(ok, what);
    }

    /// <summary>Advances the frame clock and processes Qt events, <paramref name="frames"/> times.</summary>
    public void Pump(int frames = 1) => QtHost.Pump(frames);

    /// <summary>Grabs the window to a PNG (a relative path goes to <see cref="OutDir"/>) and loads it; the caller disposes it.</summary>
    public Bitmap Grab(SceneHandle window, string path)
    {
        if (!Path.IsPathRooted(path))
            path = Path.Combine(OutDir, path);
        var rc = NativeTest.vlqt_test_window_grab(window, path);
        Check(rc == 0, $"grab {path} " + (rc != 0 ? Native.LastError() : ""));
        return new Bitmap(path);
    }

    private void Write(string status, string what)
    {
        var line = status + " " + what;
        lock (s_syncRoot)
        {
            _out.WriteLine(line);
            if (status != "INFO")
                AppendTsv(Name, status, what);
        }
    }

    /// <summary>Helper executable: the parent records the checks, the child writes no checks.tsv.</summary>
    public static bool WriteTsv { get; set; } = true;

    private static void AppendTsv(string name, string status, string what)
    {
        if (!WriteTsv)
            return;
        try
        {
            var path = Path.Combine(SmokeParameters.SmokeDir, "checks.tsv");
            if (!s_tsvStarted)
            {
                Directory.CreateDirectory(SmokeParameters.SmokeDir);
                File.WriteAllText(path, "");
                s_tsvStarted = true;
            }
            File.AppendAllText(path, $"{name}\t{status}\t{what.Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "")}\n");
        }
        catch (IOException)
        {
            // The list is a convenience for comparing runs; the test result does not depend on it.
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the current NUnit test on the Qt thread and reports every failed check. The scenario
    /// name is the test's first category (else the test name).
    /// </summary>
    public static void Run(Action<Scenario> body)
    {
        var test = TestContext.CurrentContext.Test;
        var name = test.Properties.Get("Category") as string ?? test.Name;
        Run(name, body);
    }

    /// <inheritdoc cref="Run(Action{Scenario})"/>
    public static void Run(string name, Action<Scenario> body)
    {
        if (QtHost.BlockedBy is { } blocker)
            Assert.Fail($"not run: the Qt thread is still blocked by '{blocker}'");
        QtHost.EnsureStarted();
        if (QtHost.InitResult != 0)
            Assert.Fail($"app_init failed ({QtHost.InitResult}): {QtHost.InitError}");

        var output = TextWriter.Synchronized(TestContext.Out);
        var s = new Scenario(name, output);
        var logStart = QtHost.LogLength;
        Exception? error = null;
        var timeout = TimeSpan.FromSeconds(SmokeParameters.ScenarioTimeoutSeconds);
        bool completed;
        try
        {
            completed = QtHost.Invoke(() => s.RunHere(body), timeout);
        }
        catch (Exception e)
        {
            completed = true;
            error = e;
        }
        if (!completed)
            QtHost.BlockedBy = name;
        s.Report(completed, error, timeout, logStart, output);
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the current NUnit test on the test thread, without starting Qt: for checks that need no
    /// GPU (protocol drift) and for tests that start a helper process (<see cref="OwnProcess"/>).
    /// </summary>
    public static void RunLocal(Action<Scenario> body)
    {
        var test = TestContext.CurrentContext.Test;
        var name = test.Properties.Get("Category") as string ?? test.Name;
        var output = TextWriter.Synchronized(TestContext.Out);
        var s = new Scenario(name, output);
        Exception? error = null;
        try
        {
            body(s);
        }
        catch (Exception e)
        {
            error = e;
        }
        s.Report(true, error, TimeSpan.Zero, QtHost.LogLength, output);
    }

    /// <summary>Helper executable: runs <paramref name="body"/> on the calling (Qt) thread; returns the scenario for its result.</summary>
    public static Scenario RunInProcess(string name, Action<Scenario> body, TextWriter output)
    {
        var s = new Scenario(name, output);
        try
        {
            s.RunHere(body);
        }
        catch (Exception e)
        {
            s.Check(false, "exception: " + e);
        }
        return s;
    }

    // Qt thread: the console of the scenario code (INFO/WARN/SKIP lines, PerfLimits) goes to the scenario's output.
    private void RunHere(Action<Scenario> body)
    {
        Directory.CreateDirectory(OutDir);
        var console = Console.Out;
        Console.SetOut(new LineWriter(this));
        try
        {
            body(this);
        }
        finally
        {
            Console.Out.Flush();
            Console.SetOut(console);
        }
    }

    // Lines written to the console by scenario code: WARN counts as a warning, everything goes to the output.
    internal void OnConsoleLine(string line)
    {
        if (line.StartsWith("WARN ", StringComparison.Ordinal))
            Warn(line[5..]);
        else if (line.StartsWith("SKIP ", StringComparison.Ordinal))
            Skip(line[5..]);
        else if (line.StartsWith("PASS ", StringComparison.Ordinal) || line.StartsWith("FAIL ", StringComparison.Ordinal))
            Check(line[0] == 'P', line[5..]);
        else
            lock (s_syncRoot)
                _out.WriteLine(line);
    }

    private void Report(bool completed, Exception? error, TimeSpan timeout, int logStart, TextWriter output)
    {
        foreach (var w in _warnings)
            Assert.Warn(w);
        if (_failures.Count == 0 && error == null && completed)
            return;
        // The Qt log of this scenario helps with a failure; a passing scenario keeps the output short.
        if (QtHost.LogSince(logStart) is { Length: > 0 } log)
        {
            output.WriteLine("---- Qt log of this scenario ----");
            output.Write(log);
        }
        using (Assert.EnterMultipleScope())
        {
            lock (s_syncRoot)
                foreach (var f in _failures)
                    Assert.Fail(f);
            if (error != null)
                Assert.Fail("exception: " + error);
            if (!completed)
                Assert.Fail($"did not return within {timeout.TotalSeconds:F0} s: the Qt thread is blocked, later scenarios do not run");
        }
    }

    private sealed class LineWriter : TextWriter
    {
        private readonly Scenario _owner;
        private readonly StringBuilder _line = new();

        public LineWriter(Scenario owner) => _owner = owner;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    var line = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                    _owner.OnConsoleLine(line);
                }
                else
                    _line.Append(value);
            }
        }

        public override void Flush()
        {
            lock (_line)
            {
                if (_line.Length == 0)
                    return;
                var line = _line.ToString();
                _line.Clear();
                _owner.OnConsoleLine(line);
            }
        }
    }
}
