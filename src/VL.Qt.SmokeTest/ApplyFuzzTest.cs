using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using VL.Qt;

// Decoder robustness (NCMD-15): the recorded traces (src/VL.Qt.SmokeTest/traces) are damaged in two ways and applied to QtNative:
// every command buffer truncated at every 4th byte, and single bytes flipped (fixed seed). Whatever arrives, vlqt_window_apply must
// return 0 or a negative ApplyResult, never crash, and vlqt_window_get_apply_error must tell the same story as the return code.
// The damaged frame k is applied to the scene that frames 0..k-1 built; afterwards the scene is rebuilt (RESET + replay), so the
// next frame starts from a clean state.
[TestFixture, NonParallelizable]
public static class ApplyFuzzTest
{
    const int Seed = 20260101;
    const int TruncateStep = 4;
    // Flipped buffers per frame: one per 2 bytes, at least 64.
    const int FlipsPerBytes = 2, MinFlips = 64;

    /// <summary>One recorded trace, damaged: truncated at every 4th byte and byte flips; apply returns 0 or an ApplyResult and agrees with apply_error.</summary>
    [Test, TestCaseSource(typeof(TraceReplay), "TraceNames"), Category("apply-fuzz")]
    public static void Fuzz(string name) => Scenario.Run(s => Run(s, name));

    static void Run(Scenario s, string name)
    {
        var traces = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "traces");
        if (traces == null)
        {
            s.Check(false, $"fuzz-{name}: traces folder not found");
            return;
        }
        var repo = Path.GetFullPath(Path.Combine(traces, "..", "..", ".."));
        var frames = ReplayTest.ReadFrames(Path.Combine(traces, name + ".bin"));
        s.Check(frames.Count > 0, $"fuzz-{name}: {frames.Count} frames");
        if (frames.Count == 0)
            return;

        // Image resources the trace refers to (same preparation as TraceReplay).
        var images = new List<ulong>();
        var imagesFile = Path.Combine(traces, name + ".images");
        if (File.Exists(imagesFile))
            foreach (var line in File.ReadAllLines(imagesFile))
            {
                var rel = line.Trim();
                if (rel.Length > 0)
                    images.Add(ImageTest.Load(Path.GetFullPath(Path.Combine(repo, rel)), (ok, what) => s.Check(ok, $"fuzz-{name}: {what}"), rel));
            }
        frames = TraceReplay.Rewrite(frames, Path.Combine(repo, "help", "Qt"), images, out _);

        var w = Native.vlqt_window_create(800, 600, "VL.Qt apply fuzz " + name);
        s.Check(w != IntPtr.Zero, $"fuzz-{name}: window_create");
        if (w == IntPtr.Zero)
            return;
        Native.vlqt_window_show(w);
        s.Pump(2);

        var reset = new byte[4];
        reset[0] = (byte)Protocol.Op.Reset; // u16 op, u16 payload length 0
        var rng = new Random(Seed);
        var clock = Stopwatch.StartNew();
        int applies = 0, accepted = 0, violations = 0;
        var firstViolations = new List<string>();

        void Probe(string what, byte[] buf)
        {
            var rc = Native.Apply(w, buf);
            var got = Native.vlqt_window_get_apply_error(w, out var ae);
            applies++;
            string? problem = null;
            if (rc > 0 || rc < -8 || rc == -1)
                problem = $"return code {rc} is not 0 or a negative ApplyResult";
            else if (got != 0)
                problem = $"apply_error returned {got}";
            else if (ae.rc != rc)
                problem = $"apply_error.rc {ae.rc} differs from the return code {rc}";
            else if (rc == 0 && (ae.Reason.Length != 0 || ae.id != 0 || ae.command != 0))
                problem = $"success, but apply_error holds \"{ae.Reason}\" (id {ae.id}, command {ae.command})";
            else if (rc < 0 && (ae.Reason.Length == 0 || ae.command < 0 || !Native.LastError().Contains("apply: command #" + ae.command)))
                problem = $"failure {rc}, but apply_error has reason \"{ae.Reason}\", command {ae.command}, last error \"{Native.LastError()}\"";
            if (rc == 0)
                accepted++;
            if (problem != null)
            {
                violations++;
                if (firstViolations.Count < 5)
                    firstViolations.Add($"{what}: {problem}");
            }
        }

        // Rebuilds the scene of frames 0..k-1 after damaged commands may have changed it.
        void Rebuild(int k)
        {
            Native.Apply(w, reset);
            for (int i = 0; i < k; i++)
                Native.Apply(w, frames[i]);
            s.Pump(1);
        }

        for (int k = 0; k < frames.Count; k++)
        {
            var frame = frames[k];
            for (int cut = 0; cut < frame.Length; cut += TruncateStep)
                Probe($"frame {k} cut at {cut}/{frame.Length}", frame.AsSpan(0, cut).ToArray());
            s.Pump(1);
            var flips = Math.Max(MinFlips, frame.Length / FlipsPerBytes);
            for (int f = 0; f < flips && frame.Length > 0; f++)
            {
                var copy = (byte[])frame.Clone();
                var pos = rng.Next(copy.Length);
                var bits = (byte)rng.Next(1, 256);
                copy[pos] ^= bits;
                Probe($"frame {k} byte {pos} ^ 0x{bits:X2}", copy);
                if (f % 32 == 31)
                    s.Pump(1);
            }
            s.Pump(1);
            Rebuild(k);
            // The undamaged frame still applies to the rebuilt scene.
            var rc = Native.Apply(w, frame);
            if (rc != 0)
            {
                violations++;
                firstViolations.Add($"frame {k}: undamaged frame returns {rc} after the damaged ones: {Native.LastError()}");
            }
            s.Pump(1);
        }
        s.Info($"apply fuzz {name}: {applies} damaged buffers ({accepted} accepted, {applies - accepted} rejected) in {clock.Elapsed.TotalSeconds:F1} s");
        s.Check(violations == 0, $"fuzz-{name}: {violations} violations of {applies} applies" + (violations > 0 ? " (" + string.Join("; ", firstViolations) + ")" : ""));
        s.Pump(2);
        Native.vlqt_window_destroy(w);
        s.Pump(1);
    }
}
