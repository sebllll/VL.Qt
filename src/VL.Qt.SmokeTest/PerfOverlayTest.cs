using NUnit.Framework;
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// F5 overlay pages (Summary, Timeline, Scene, System, Resources), F5/Shift+F5/F6 keys, and an injected 12 ms render spike
/// that shows up in the timeline and in the render phase of the sample. Grabs perf_f5_*.png for a visual check.
/// </summary>
[TestFixture, NonParallelizable]
public static class PerfOverlayTest
{
    // plain text of the Scene/System page, single spaces (columns collapsed) for robust Contains checks.
    internal static string PageText(SceneHandle w, int page)
    {
        var text = NativeCalls.PerfPageText(w, page);
        return System.Text.RegularExpressions.Regex.Replace(text, " +", " ");
    }

    private static string Line(string text, string label) => text.Split('\n').FirstOrDefault(l => l.StartsWith(label))?.Trim() ?? "(missing)";
    // Indented rows (the summary's phases): the page text collapsed the spaces, so StartsWith does not find them.
    private static string LineWith(string text, string part) => text.Split('\n').FirstOrDefault(l => l.Contains(part))?.Trim() ?? "(missing)";

    private const int KeyF5 = 0x01000034, KeyF6 = 0x01000035, KeyF7 = 0x01000036, Shift = 0x02000000;
    private const int Stats = 2, Freeze = 8, PageShift = 4;
    internal const int ResourcesPage = 4;
    private const int Summary = 0, Timeline = 1, Scene = 2, SystemPage = 3;
    private const int PhaseVsync = 1, PhaseSync = 2, PhaseRender = 3;

    private static int Page(SceneHandle w) => ((Native.vlqt_scene_get_overlay(w) >> PageShift) & 7) - 1;
    private static bool Visible(SceneHandle w) => (Native.vlqt_scene_get_overlay(w) & Stats) != 0;

    // timing checks measure one pump / one grab on a machine that runs other work (an unrelated 10 ms render or a
    // stalled present lands in the same sample). Retry a few times; a real regression is systematic and fails every try.
    private const int Attempts = 3;

    private static void Retry(Scenario s, string what, Func<(bool ok, string detail)> once) => Retry(s.Check, what, once);

    // The report goes through a delegate where the caller turns a failure into a SKIP (host too loaded).
    private static void Retry(Action<bool, string> check, string what, Func<(bool ok, string detail)> once)
    {
        (bool ok, string detail) r = default;
        int n = 0;
        while (n < Attempts) { n++; r = once(); if (r.ok) break; }
        check(r.ok, $"{what} ({r.detail}{(n > 1 ? $"; attempt {n}/{Attempts}" : "")})");
    }

    // the timeline legend and the summary of a window with VSync (swap interval 1) show the segment "vsync wait" (the
    // wait in QRhi::beginFrame) next to a plain "present", a spike there lands in vsync wait, and the System page says
    // VSync on / allow tearing no.
    private static void VSyncWindowLabels(Scenario s)
    {
        var v = Native.vlqt_window_create(float.NaN, float.NaN, 640, 480, "VL.Qt perf overlay VSync", 1);
        s.Check(v != SceneHandle.Null, "perfoverlay: VSync window created " + (v == SceneHandle.Null ? Native.LastError() : ""));
        if (v == SceneHandle.Null) return;
        var cmd = new CommandWriter();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(1, 0, 0);
        Native.Apply(v, cmd.EndFrame());
        Native.vlqt_window_show(v);
        NativeTest.vlqt_test_set_overlay(v, Stats | ((Summary + 1) << PageShift));
        for (var i = 0; i < 40; i++)
        {
            cmd.Clear();
            cmd.SetPlace(1, 100 + 50 * MathF.Sin(i * 0.2f), 200, 100, 60);
            Native.Apply(v, cmd.EndFrame());
            s.Pump(1);
        }
        var sum = PageText(v, Summary);
        var tl = PageText(v, Timeline);
        var sys = PageText(v, SystemPage);
        Console.WriteLine("  vsync window system page: " + sys.Replace("\n", " | "));
        s.Check(sum.Contains("vsync wait") && tl.Contains("vsync wait") && !sum.Contains("incl.") && !tl.Contains("incl.")
            && sum.Contains(" present") && tl.Contains("present"),
            $"perfoverlay: VSync on: 'vsync wait' and plain 'present' in summary and timeline ({LineWith(sum, "vsync wait")} | {LineWith(sum, "present")})");
        Retry(s, "perfoverlay: VSync on: vsync spike in the vsync wait segment", () =>
        {
            if (NativeTest.vlqt_test_perf_spike(v, PhaseVsync, 10) != 0) return (false, "inject failed");
            cmd.Clear();
            cmd.SetPlace(1, 120, 200, 100, 60);
            Native.Apply(v, cmd.EndFrame());
            // With VSync not every pump renders (the update request comes from Qt's VBlank thread): the sample to read
            // is the next pump with a frame, messages dispatched in between like vvvv.
            Native.vlqt_perf_sample ps = default;
            for (var k = 0; k < 10; k++)
            {
                s.Pump(1);
                if (Native.vlqt_scene_get_perf_sample(v, out ps) == 0 && ps.swaps > 0) break;
                PumpTest.ForeignMessageLoop(2);
            }
            var ok = ps.vsync >= 10 && ps.sync < ps.vsync / 2 && ps.events < ps.vsync / 2 && ps.present < ps.vsync / 2;
            if (!ok) s.Pump(20);
            return (ok, $"pump {ps.pump:F1}, events {ps.events:F1}, vsync {ps.vsync:F1}, sync {ps.sync:F1}, render {ps.render:F1}, present {ps.present:F1}");
        });
        // Allow tearing is a D3D11 notion; other backends (Linux GL) show n/a.
        var tearOn = sys.Contains("backend d3d11", StringComparison.OrdinalIgnoreCase) ? "allow tearing no" : "allow tearing n/a";
        s.Check(sys.Contains("swap int. 1 VSync on") && Line(sys, "allow tearing").StartsWith(tearOn),
            $"perfoverlay: VSync on: swap int. 1, {tearOn} ({Line(sys, "swap int.")} | {Line(sys, "allow tearing")} | {Line(sys, "backend")})");
        foreach (var (page, name) in new[] { (Summary, "summary"), (Timeline, "timeline"), (SystemPage, "system") })
        {
            NativeTest.vlqt_test_set_overlay(v, Stats | ((page + 1) << PageShift));
            s.Pump(3);
            using var a = s.Grab(v, Path.Combine(s.OutDir, $"perf_f5_{name}_vsync.png"));
            s.Check(a.Width > 0, $"perfoverlay: {name} grabbed with VSync");
        }
        Native.vlqt_scene_destroy(v);
    }

    /// <summary>F5 performance overlay: modes, labels, VSync windows, values against the native counters.</summary>
    [Test, Category("perfoverlay")]
    public static void PerfOverlay() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        using var timer = PumpTest.HighResTimer(); // ~60 Hz pacing with Sleep(15)
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt perf overlay");
        var cmd = new CommandWriter();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(1, 0, 0);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "perfoverlay: scene");
        Native.vlqt_window_show(w);
        int frame = 0;
        // pumps whose loop step took longer than the smallest host spike (150 ms) are host stalls (parallel builds,
        // other sessions): such a step reports a vvvv time above the injected 180-190 ms spikes and takes over the timeline's
        // peaks. The checks that read the peaks use these to tell "machine overloaded" from "regression".
        const float stallMs = 150;
        var bigStalls = new List<int>();
        bool StallsWithin(int pumps) => bigStalls.Any(f => frame - f < pumps);
        // Moving rect: every pump renders a frame. Real time passes so the text (4 Hz) and frame intervals update.
        // spikeMs > 0: a sync spike of that length in every pump (minimum load, the real pump of this scene takes < 1 ms).
        void Run(int frames, float spikeMs = 0)
        {
            long lastEnd = 0;
            for (int i = 0; i < frames; i++, frame++)
            {
                cmd.Clear();
                cmd.SetPlace(1, 500 + 100 * MathF.Sin(frame * 0.1f), 400, 100, 60);
                var t0 = Stopwatch.GetTimestamp();
                if (lastEnd != 0 && Stopwatch.GetElapsedTime(lastEnd, t0).TotalMilliseconds > stallMs) bigStalls.Add(frame);
                Native.Apply(w, cmd.EndFrame());
                var apply = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (spikeMs > 0) NativeTest.vlqt_test_perf_spike(w, PhaseSync, spikeMs);
                s.Pump(1);
                // Host values like QtApp reports them, so the Summary shows its apply/GC rows. vvvv: the
                // time between pumps stands in for the patch update, so the F2 graph shows its vvvv line.
                var vvvv = lastEnd == 0 ? 0 : (float)Stopwatch.GetElapsedTime(lastEnd, t0).TotalMilliseconds;
                Native.vlqt_scene_report_host_pump(w, new Native.vlqt_host_pump { apply = apply, ops = 1, vvvv = vvvv });
                lastEnd = Stopwatch.GetTimestamp();
                Thread.Sleep(15);
            }
        }
        Run(250); // startup frames (slow first presents) leave the 240-pump ring
        var sw = s.QuickSoftware ? "_sw" : ""; // known after the first frame

        // Pages via the API: bits 4..6 = page + 1.
        NativeTest.vlqt_test_set_overlay(w, Stats | ((Summary + 1) << PageShift));
        s.Check(Visible(w) && Page(w) == Summary, $"perfoverlay: API opens Summary ({Native.vlqt_scene_get_overlay(w)})");
        Run(30);

        // A 12 ms render spike and a 10 ms sync spike, each in its own pump, attributed to their phase.
        // Other phases only have to stay well below the spike: under load (full SmokeTest run) a single unrelated render of
        // ~6 ms can land in the same pump, the attribution is still clear.
        Retry(s, "perfoverlay: render spike in the render phase", () =>
        {
            if (NativeTest.vlqt_test_perf_spike(w, PhaseRender, 12) != 0) return (false, "inject failed");
            Run(1);
            Native.vlqt_scene_get_perf_sample(w, out var ps);
            var ok = ps.render >= 12 && ps.sync < ps.render / 2 && ps.events < ps.render / 2 && ps.pump >= 12;
            if (!ok) Run(40);
            return (ok, $"pump {ps.pump:F1}, events {ps.events:F1}, sync {ps.sync:F1}, render {ps.render:F1}, present {ps.present:F1}");
        });
        Run(40);
        Retry(s, "perfoverlay: sync spike in the sync phase", () =>
        {
            if (NativeTest.vlqt_test_perf_spike(w, PhaseSync, 10) != 0) return (false, "inject failed");
            Run(1);
            Native.vlqt_scene_get_perf_sample(w, out var ps);
            var ok = ps.sync >= 10 && ps.render < ps.sync / 2 && ps.events < ps.sync / 2;
            if (!ok) Run(40);
            return (ok, $"pump {ps.pump:F1}, events {ps.events:F1}, sync {ps.sync:F1}, render {ps.render:F1}");
        });

        // host-side spikes (simulated host frames, overwriting the report of that pump): vvvv update, GC
        // pause (inside the vvvv update) and apply. Far larger than the Qt spikes above and stray present stalls (~35 ms seen), so they are
        // the timeline's 3 peaks,
        // each attributed to its own segment, not to a Qt phase.
        // On a loaded machine a stalled loop step reports a vvvv time of ~90 ms itself (time between pumps, see Run):
        // spikes of ~190 ms stay above it, and a stall that still takes a peak leaves the ring before the retry.
        // With heavier load (several times more busy processes than cores) stalls of seconds happen and take the peaks in
        // every try: when the 240-pump ring of the last try holds such a stall (a step > 150 ms), the host is too loaded
        // to read the peaks and the check is SKIPPED, not failed. Without stalls the same failure is a regression.
        void HostSpike(Native.vlqt_host_pump f) { Run(1); Native.vlqt_scene_report_host_pump(w, f); Run(3); }
        const string hostSpikeCheck = "perfoverlay: host spikes attributed to vvvv/GC/apply in the timeline";
        var hostSpikesOverloaded = false;
        Retry((ok, detail) =>
        {
            if (!ok && hostSpikesOverloaded) s.Skip($"{hostSpikeCheck}: host too loaded, loop steps > {stallMs:F0} ms in the last 240 pumps ({detail})");
            else s.Check(ok, detail);
        }, hostSpikeCheck, () =>
        {
            HostSpike(new Native.vlqt_host_pump { vvvv = 190, ops = 1 });
            HostSpike(new Native.vlqt_host_pump { vvvv = 186, gc_pause = 185, gc0 = 1, ops = 1 });
            HostSpike(new Native.vlqt_host_pump { apply = 180, vvvv = 0.5f, ops = 1 });
            hostSpikesOverloaded = StallsWithin(240); // read before the Run(250) below pushes them out of the window
            var tl = PageText(w, Timeline);
            var peaks = string.Join(" | ", tl.Split('\n').SkipWhile(l => !l.StartsWith("peaks")).Take(3).Select(l => l.Trim()));
            var ok = peaks.Contains("vvvv 190.00") && peaks.Contains("GC 185.00") && peaks.Contains("apply 180.00") && tl.Contains("F7 vvvv/GC/apply on");
            if (!ok) Run(250); // the stray stall (and the spikes) leave the 240-pump ring
            return (ok, peaks);
        });
        // F7 (stats visible) hides the host segments: the same pumps are now attributed to Qt phases only.
        NativeTest.vlqt_test_send_key(w, KeyF7, 0);
        var tlOff = PageText(w, Timeline);
        var peaksOff = string.Join(" | ", tlOff.Split('\n').SkipWhile(l => !l.StartsWith("peaks")).Take(3).Select(l => l.Trim()));
        s.Check(tlOff.Contains("F7 vvvv/GC/apply off") && !peaksOff.Contains("vvvv") && !peaksOff.Contains("GC") && !peaksOff.Contains("apply"),
            $"perfoverlay: F7 hides the host segments ({peaksOff})");
        NativeTest.vlqt_test_send_key(w, KeyF7, 0);
        s.Check(PageText(w, Timeline).Contains("F7 vvvv/GC/apply on"), "perfoverlay: F7 again shows them");
        Run(20); // all 3 spikes stay in the summary window (60 pumps)
        var sum = PageText(w, Summary);
        // The row shows the peak of the 60-pump window: a host stall above the 190 ms spike (see above) replaces it.
        var sumOk = Line(sum, "vvvv").Contains("190.00") && System.Text.RegularExpressions.Regex.IsMatch(sum, @"Qt pump \d+% of frame, vvvv \d+%");
        var sumDetail = $"perfoverlay: summary vvvv row + share ({Line(sum, "vvvv")}; {Line(sum, "fps")})";
        if (!sumOk && StallsWithin(60)) s.Skip($"{sumDetail}: host too loaded, loop steps > {stallMs:F0} ms in the last 60 pumps");
        else s.Check(sumOk, sumDetail);
        // a fresh spike in the last pump instead of the one from ~90 pumps ago (the stats window is shorter): which
        // other pump of the window was above 12 ms depended on the machine (page renders, stalls).
        NativeTest.vlqt_test_perf_spike(w, PhaseRender, 14);
        Run(1);
        Native.vlqt_scene_get_perf_stats(w, out var st);
        s.Check(st.pump_max >= 12, $"perfoverlay: stats see the spike (pump max {st.pump_max:F1})");
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_summary{sw}.png")))
            s.Check(a.Width > 0, "perfoverlay: summary grabbed");
        s.Check(PageText(w, 0).Contains("fps") && !PageText(w, 0).Contains("on demand"), "perfoverlay: animated summary is not 'on demand'");

        NativeTest.vlqt_test_set_overlay(w, Stats | ((Timeline + 1) << PageShift));
        Run(5);
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_timeline{sw}.png")))
            s.Check(Page(w) == Timeline, "perfoverlay: timeline grabbed");

        // F6 freezes: the snapshot stays while new pumps arrive; F6 again releases.
        s.Check(NativeTest.vlqt_test_send_key(w, KeyF6, 0) == 0 && (Native.vlqt_scene_get_overlay(w) & Freeze) != 0, "perfoverlay: F6 freezes");
        Run(10);
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_timeline_frozen{sw}.png")))
        {
            NativeTest.vlqt_test_send_key(w, KeyF6, 0);
            s.Check((Native.vlqt_scene_get_overlay(w) & Freeze) == 0, "perfoverlay: F6 again releases");
        }

        // F5 cycles Timeline -> Scene -> System -> Resources -> off; next F5 starts at Summary.
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        Run(2);
        s.Check(Visible(w) && Page(w) == Scene, $"perfoverlay: F5 -> Scene (page {Page(w)})");
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_scene{sw}.png"))) { }
        // element count from the reconciler (1 rectangle), ops/apply from the reported host frames (ops = 1),
        // draw calls n/a; GPU memory and pipelines n/a without QRhi.
        var scene = PageText(w, Scene);
        s.Check(scene.Contains("elements 1 rectangle 1"), $"perfoverlay: scene counts 1 rectangle element ({Line(scene, "elements")})");
        s.Check(scene.Contains("QML items") && !scene.Contains("animations"), $"perfoverlay: scene items, no animations row ({Line(scene, "QML items")})");
        // Every element type is counted by its own name (Shape, Shader), none in "other".
        cmd.Clear();
        cmd.Create(2, Protocol.ElementType.Shape);
        cmd.Create(3, Protocol.ElementType.Shader);
        cmd.Reparent(2, 0, 1);
        cmd.Reparent(3, 0, 2);
        Native.Apply(w, cmd.EndFrame());
        Run(2);
        var typed = PageText(w, Scene);
        s.Check(typed.Contains("elements 3") && typed.Contains("shape 1") && typed.Contains("shader 1") && typed.Contains("rectangle 1") && !typed.Contains("other"),
            $"perfoverlay: scene names every element type ({Line(typed, "elements")})");
        cmd.Clear();
        cmd.Destroy(2);
        cmd.Destroy(3);
        Native.Apply(w, cmd.EndFrame());
        Run(1);
        s.Check(scene.Contains("last 1"), $"perfoverlay: scene ops/apply from host frames ({Line(scene, "ops/apply")})");
        s.Check(scene.Contains("draw calls n/a"), "perfoverlay: scene draw calls n/a");
        if (s.QuickSoftware)
            s.Check(scene.Contains("gpu memory n/a") && scene.Contains("pipelines n/a"), $"perfoverlay: scene GPU values n/a on software ({Line(scene, "gpu memory")})");
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        Run(2);
        s.Check(Visible(w) && Page(w) == SystemPage, $"perfoverlay: F5 -> System (page {Page(w)})");
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_system{sw}.png"))) { }
        var sys = PageText(w, SystemPage);
        Console.WriteLine("  system page: " + sys.Replace("\n", " | "));
        s.Check(sys.Contains(" logical") && sys.Contains("DPR ") && sys.Contains("refresh ") && sys.Contains("Qt 6."),
            $"perfoverlay: system window/DPR/refresh/Qt rows ({Line(sys, "window")})");
        if (s.QuickSoftware)
            s.Check(sys.Contains("backend software") && sys.Contains("adapter n/a") && sys.Contains("swap int. n/a") && sys.Contains("rendering CPU"),
                $"perfoverlay: system on software: backend software, adapter/swap n/a, CPU ({Line(sys, "backend")} | {Line(sys, "adapter")} | {Line(sys, "rendering")})");
        else
            s.Check(!sys.Contains("adapter n/a") && sys.Contains("swap int. 0 VSync off"),
                $"perfoverlay: system on QRhi: adapter named, swap interval 0 ({Line(sys, "adapter")} | {Line(sys, "swap int.")})");
        // allow tearing (D3D11 only, "no" with VSync) and refresh rate sit next to the swap interval; with VSync off the
        // segments are the same (vsync wait then shows a GPU that is behind).
        var tear = Line(sys, "allow tearing");
        var hz = System.Text.RegularExpressions.Regex.IsMatch(Line(sys, "refresh"), @"^refresh \d+(\.\d+)? Hz");
        s.Check(hz, $"perfoverlay: system refresh rate row ({Line(sys, "refresh")})");
        if (s.QuickSoftware || !sys.Contains("backend d3d11", StringComparison.OrdinalIgnoreCase))
            s.Check(tear.StartsWith("allow tearing n/a"), $"perfoverlay: allow tearing n/a without D3D11 ({tear} | {Line(sys, "backend")})");
        else
            s.Check(tear.StartsWith("allow tearing yes") || tear.StartsWith("allow tearing no"), $"perfoverlay: allow tearing yes/no on D3D11 with VSync off ({tear})");
        var sumOff = PageText(w, Summary);
        var tlOff0 = PageText(w, Timeline);
        s.Check(!sumOff.Contains("incl.") && !tlOff0.Contains("incl.") && sumOff.Contains(" present") && tlOff0.Contains("present")
            && sumOff.Contains("vsync wait") && tlOff0.Contains("vsync wait"),
            $"perfoverlay: VSync off: 'vsync wait' and plain 'present' in summary and timeline ({LineWith(sumOff, "present")})");
        if (!s.QuickSoftware)
            VSyncWindowLabels(s);
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        Run(2);
        s.Check(Visible(w) && Page(w) == ResourcesPage, $"perfoverlay: F5 -> Resources (page {Page(w)})");
        s.Check(PageText(w, ResourcesPage).Contains("RESOURCES 5/5"), "perfoverlay: resources page header");
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        s.Check(!Visible(w), "perfoverlay: F5 after Resources hides");
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        s.Check(Visible(w) && Page(w) == Summary, $"perfoverlay: F5 from off -> Summary (page {Page(w)})");
        // Shift+F5 hides and re-shows the same page.
        NativeTest.vlqt_test_send_key(w, KeyF5, 0);
        NativeTest.vlqt_test_send_key(w, KeyF5, Shift);
        s.Check(!Visible(w), "perfoverlay: Shift+F5 hides");
        NativeTest.vlqt_test_send_key(w, KeyF5, Shift);
        s.Check(Visible(w) && Page(w) == Timeline, $"perfoverlay: Shift+F5 shows the remembered page (page {Page(w)})");
        // F6 without the stats is not consumed / changes nothing.
        NativeTest.vlqt_test_set_overlay(w, 0);
        NativeTest.vlqt_test_send_key(w, KeyF6, 0);
        s.Check((Native.vlqt_scene_get_overlay(w) & (Stats | Freeze)) == 0, "perfoverlay: F6 ignored while hidden");

        // the F2 meter draws its lines also on the software backend (there as an image, QSGGeometryNode is not drawn).
        // The line follows the pump times: a grab in a quiet moment can show fewer pixels (and the grab itself renders),
        // so the pixel count gets a few tries with fresh frames in between.
        // the pump of this scene takes ~0.5 ms on a fast machine, then the orange line lies on the GPU/frame lines
        // (drawn later, on top) at the bottom of the graph and fewer than 40 px stay orange. Every pump therefore gets a
        // sync spike of 8 ms, so the line runs clearly above the baseline (~20 % of the graph height) over the whole 240-pump
        // ring (filled once before the grab) and the 40 px (~14 % of the 282 columns) mean a continuous line, as intended.
        NativeTest.vlqt_test_set_overlay(w, 1);
        Run(250, 8);
        Retry(s, "perfoverlay: F2 meter draws the pump line", () =>
        {
            Run(20, 8);
            using var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f2_meter{sw}.png"));
            int line = 0;
            for (int y = 26; y < 76; y++)
                for (int x = 12; x < 294; x++)
                {
                    var c = a.GetPixel(x * a.Width / 800, y * a.Height / 600);
                    if (c.R > 180 && c.G > 100 && c.G < 200 && c.B < 110) line++; // pump line (255, 160, 40)
                }
            return (line >= 40, $"{line} px");
        });
        NativeTest.vlqt_test_set_overlay(w, 0);

        // A new window opens on the remembered page.
        var w2 = NativeCalls.CreateWindow(320, 240, "VL.Qt perf overlay 2");
        NativeTest.vlqt_test_set_overlay(w2, Stats);
        s.Check(Page(w2) == Timeline, $"perfoverlay: new window opens the remembered page (page {Page(w2)})");
        Native.vlqt_scene_destroy(w2);

        // static scene -> Qt renders only on changes (here: the 4 Hz overlay text). The intervals after idle
        // pumps are no missed frames, and the Summary shows "on demand" instead of a red fps.
        NativeTest.vlqt_test_set_overlay(w, Stats | ((Summary + 1) << PageShift));
        for (int i = 0; i < 10; i++) { s.Pump(1); Thread.Sleep(15); } // settle: switching pages renders
        // A regression (idle gaps counted as missed frames) flags ~one long frame per overlay text update, i.e. a
        // systematic count over the 150 pumps; a single OS stall adds at most a few. Tolerate a few, retry the window.
        // On a loaded machine (other builds, parallel sessions) many loop steps overrun: each one can make a real long
        // frame, so it is excused. The regression comes without stalls and still fails. With more than 20 % stalled
        // steps the excuse hides the regression: SKIP instead of a PASS that proves nothing.
        const string idleCheck = "perfoverlay: static scene -> few new long frames, mostly idle, summary 'on demand'";
        string? overloaded = null;
        Retry((ok, detail) =>
        {
            if (overloaded != null) s.Skip($"{idleCheck}: host too loaded ({overloaded})");
            else s.Check(ok, detail);
        }, idleCheck, () =>
        {
            overloaded = null;
            Native.vlqt_scene_get_perf_stats(w, out var s0);
            var stallMs = 1.5 * (s0.budget_ms > 0 ? s0.budget_ms : 1000.0 / 60);
            int stalls = 0;
            var last = Stopwatch.GetTimestamp();
            for (int i = 0; i < 150; i++)
            {
                s.Pump(1);
                Thread.Sleep(15);
                var now = Stopwatch.GetTimestamp();
                if ((now - last) * 1000.0 / Stopwatch.Frequency > stallMs) stalls++;
                last = now;
            }
            Native.vlqt_scene_get_perf_stats(w, out var s1);
            var head = PageText(w, 0);
            var newLong = s1.long_frames - s0.long_frames;
            var detail = $"long {s0.long_frames} -> {s1.long_frames}, host stalls {stalls}/150 (> {stallMs:F1} ms), idle pumps {s1.idle - s0.idle}/150, head '{head}'";
            var ok = newLong <= 2 + stalls && s1.idle - s0.idle > 75 && head.Contains("on demand");
            if (!ok && stalls > 30) overloaded = detail;
            return (ok, detail);
        });
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_summary_static{sw}.png"))) { }
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }

    /// <summary>
    /// (images package 07): F5 Resources page with the test kind. One resource per state (Pending,
    /// Decoding stuck > 2 s, Uploading against a 1 MB budget, Ready, Failed): counts, upload queue, budget use, the
    /// Failed/stuck ones first in the list. Then 1000 resources: build cost of the page. Grabs perf_f5_resources*.png.
    /// </summary>
    /// <summary>Resource overlay: counters of the resource list in the overlay.</summary>
    [Test, Category("resoverlay")]
    public static void ResourceOverlay() => Scenario.Run(RunResources);

    private static void RunResources(Scenario s)
    {
        const int Mb = 1 << 20;
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt resources overlay");
        Native.vlqt_window_show(w);
        var poll = new byte[256 * 1024];
        void Run(int n) { for (int i = 0; i < n; i++) { s.Pump(1); NativeCalls.ResPoll(poll); Thread.Sleep(15); } }
        var ids = new List<ulong>();
        ulong Start(int decodeMs, long bytes, int flags)
        {
            Native.vlqt_res_create(TestProtocol.ResourceKind.Test, out var id);
            ids.Add(id);
            if (decodeMs >= 0) NativeTest.vlqt_test_res_start(id, decodeMs, bytes, flags);
            return id;
        }
        s.Check(Native.vlqt_res_set_budget(Mb) == 0, "budget 1 MB");
        // Ready ones first and synchronously (vlqt_res_wait): the endless upload below blocks the upload queue.
        Native.vlqt_res_wait(Start(0, 3 * Mb / 2, 0), 5000); // Ready
        Start(-1, 0, 0);                    // Pending (never started)
        Start(0, Mb, 1);                    // Failed (decode)
        Start(0, 1L << 40, 0);              // Uploading: uses the whole budget every frame
        // The 60 s decode last: decodes run FIFO, and with a single worker (2-core CI runners: min(4, cores - 1) = 1)
        // it would hold back the two decodes above.
        var stuckDecode = Start(60000, Mb, 0); // Decoding, stuck after 2 s
        NativeTest.vlqt_test_set_overlay(w, Stats | ((ResourcesPage + 1) << PageShift));
        // Wait for the states instead of assuming a time: the decode and the upload count as stuck after 2 s in their state.
        const string Expected = "live 5 pending 1 decoding 1 uploading 1 ready 1 failed 1 released 0 stuck 2";
        var text = "";
        for (var t = Stopwatch.StartNew(); t.Elapsed.TotalSeconds < 15; )
        {
            Run(10);
            text = PageText(w, ResourcesPage);
            if (text.Contains(Expected))
                break;
        }
        Console.WriteLine("  resources page: " + text.Replace("\n", " | "));
        s.Check(text.Contains(Expected), $"counts per state ({Line(text, "live")})");
        s.Check(Line(text, "budget").Contains("1.0 MB / frame last frame 1.0 MB (100%)"), $"budget and its use ({Line(text, "budget")})");
        s.Check(Line(text, "upload queue").Contains("job(s)") && Line(text, "upload queue").Contains("1 job(s), decode queue 0"),
            $"upload queue ({Line(text, "upload queue")})");
        s.Check(Line(text, "uploaded").StartsWith("uploaded") && !Line(text, "uploaded").Contains(" 0 B"), $"uploaded bytes ({Line(text, "uploaded")})");
        var rows = text.Split('\n').SkipWhile(l => !l.StartsWith("id ")).Skip(1).Take(5).ToArray();
        // Stuck: the 60 s decode and the endless upload (> 2 s in their state).
        s.Check(rows.Length == 5 && new[] { "Failed", "Decoding", "Uploading" }.All(s => rows.Take(3).Any(r => r.Contains(s)))
            && rows[3].Contains("Pending") && rows[4].Contains("Ready"),
            $"Failed/stuck first, then in progress, Ready last ({string.Join(" | ", rows)})");
        var sw = s.QuickSoftware ? "_sw" : "";
        Run(20); // text interval 250 ms
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_resources{sw}.png"))) { }

        // 1000 resources: the page is built at most every 250 ms (text interval), so its build time bounds the cost per
        // frame. Measured with the plain text conversion of vlqt_test_perf_text on top (upper bound).
        // The stuck decode goes first (it cancels within ms), so a single worker is free for the 996 decodes.
        Native.vlqt_res_release(stuckDecode);
        ids.Remove(stuckDecode);
        Run(5);
        for (int i = 0; i < 996; i++) Native.vlqt_res_wait(Start(0, 4096, 0), 5000);
        Run(10);
        var probe = new byte[1 << 16];
        for (int i = 0; i < 5; i++) NativeTest.vlqt_test_perf_text(w, ResourcesPage, probe, probe.Length, out _, out _); // warm up
        const int Builds = 50;
        var t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < Builds; i++) NativeTest.vlqt_test_perf_text(w, ResourcesPage, probe, probe.Length, out _, out _);
        var perBuild = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Builds;
        Console.WriteLine($"  resources page with 1000 resources: {perBuild:F3} ms per build (incl. plain text), every 250 ms");
        // The page's own build time (footer, without the plain text conversion of this test export): best of a few.
        var built = Enumerable.Range(0, 20).Select(_ => System.Text.RegularExpressions.Regex.Match(PageText(w, ResourcesPage), @"page built in ([0-9.]+) ms"))
            .Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(double.NaN).Min();
        Console.WriteLine($"  resources page with 1000 resources: built in {built:F3} ms (best of 20)");
        PerfLimits.Below(s, built, 0.5, "page build with 1000 resources (ms)");
        var t1000 = PageText(w, ResourcesPage);
        s.Check(t1000.Contains("live 1000") && t1000.Contains("... 984 more"), $"1000 resources, 16 rows listed ({Line(t1000, "live")} | {Line(t1000, "...")})");
        Run(20);
        using (var a = s.Grab(w, Path.Combine(s.OutDir, $"perf_f5_resources_1000{sw}.png"))) { }

        foreach (var id in ids) Native.vlqt_res_release(id);
        Run(10); // the stuck decode cancels first
        s.Check(Native.ResList(Span<Native.vlqt_res_info>.Empty) == 0, "all resources freed");
        Native.vlqt_res_set_budget(8 * Mb);
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }

    /// <summary>
    /// CLI <c>&lt;OutDir&gt; --overlay-cost</c>: native pump time per overlay mode on a small animated scene (20 moving
    /// rectangles), 60 Hz paced like vvvv. Modes alternate in blocks of 120 pumps (8 rounds) so drift hits all alike;
    /// prints median / mean / p90 per mode and the difference to "off".
    /// </summary>
    /// <summary>Native pump time per overlay mode on a small animated scene (F5 overlay cost); parameter OverlayCostRounds.</summary>
    [Test, Explicit("measurement, about a minute"), Category("overlay-cost")]
    public static void OverlayCost() => Scenario.Run(s => s.Check(RunCost(s, SmokeParameters.OverlayCostRounds) == 0, "overlay-cost"));

    private static int RunCost(Scenario s, int rounds = 8)
    {
        using var timer = PumpTest.HighResTimer();
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt overlay cost");
        var cmd = new CommandWriter();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        for (uint i = 0; i < 20; i++)
        {
            cmd.Create(1 + i, Protocol.ElementType.Rectangle);
            cmd.SetColor(1 + i, Protocol.PropertyId.Color, 1, 0.6f, i / 20f, 1);
            cmd.Reparent(1 + i, 0, i);
        }
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_show(w);
        var modes = new[] { (0, "off"), (Stats | ((Summary + 1) << PageShift), "F5 Summary"),
            (Stats | ((Timeline + 1) << PageShift), "F5 Timeline"), (Stats | ((Scene + 1) << PageShift), "F5 Scene"),
            (Stats | ((SystemPage + 1) << PageShift), "F5 System"), (Stats | ((ResourcesPage + 1) << PageShift), "F5 Resources"),
            (1, "F2 meter") };
        var data = new List<double>[modes.Length];
        var phases = new double[modes.Length, 3]; // sum of sync / render / present, to see where a cost lands
        for (int m = 0; m < modes.Length; m++) data[m] = new();
        long period = Stopwatch.Frequency / 60;
        var deadline = Stopwatch.GetTimestamp();
        int frame = 0;
        for (int round = 0; round <= rounds; round++) // round 0 = warmup
            for (int m = 0; m < modes.Length; m++)
            {
                NativeTest.vlqt_test_set_overlay(w, modes[m].Item1);
                for (int i = 0; i < 120; i++, frame++)
                {
                    deadline += period;
                    PumpTest.ForeignMessageLoopUntil(deadline);
                    if (Stopwatch.GetElapsedTime(deadline).TotalMilliseconds > 16.7) deadline = Stopwatch.GetTimestamp();
                    cmd.Clear();
                    for (uint k = 0; k < 20; k++)
                        cmd.SetPlace(1 + k, 300 + 250 * MathF.Sin(frame * 0.05f + k), 30 * k, 80, 25);
                    Native.Apply(w, cmd.EndFrame());
                    s.Pump(1);
                    // skip the switch
                    if (round > 0 && i >= 10 && Native.vlqt_scene_get_perf_sample(w, out var ps) == 0) { data[m].Add(ps.pump); phases[m, 0] += ps.sync; phases[m, 1] += ps.render; phases[m, 2] += ps.present; }
                }
            }
        Native.vlqt_scene_destroy(w);
        double Median(List<double> l) { l.Sort(); return l[l.Count / 2]; }
        var offMed = Median(data[0]);
        var offMean = data[0].Average();
        for (int m = 0; m < modes.Length; m++)
        {
            var l = data[m];
            var med = Median(l);
            var mean = l.Average();
            Console.WriteLine($"OVERLAY COST {modes[m].Item2,-12} n {l.Count}: median {med:F3} ms (+{med - offMed:F3}), mean {mean:F3} ms (+{mean - offMean:F3}), p90 {l[l.Count * 9 / 10]:F3} ms; mean sync {phases[m, 0] / l.Count:F3} render {phases[m, 1] / l.Count:F3} present {phases[m, 2] / l.Count:F3}");
        }
        return 0;
    }
}
