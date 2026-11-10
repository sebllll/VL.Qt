using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VL.Qt;

// Smoke test of QtNative without vvvv. Usage: VL.Qt.SmokeTest <outDir> | --drift [protocol.h]
static class Program
{
    // Space v1 = DIPTopLeft: 1 unit = 100 DIP = 100 Qt logical pixels (like Renderer [Qt]).
    const float U = 100f;
    const uint RectId = 1, GroupId = 2;

    static readonly StringBuilder s_log = new();
    static readonly Native.LogFn s_logFn = OnLog;
    static readonly List<string> s_failures = new();
    static readonly CommandWriter s_cmd = new();
    static double s_time;

    static void OnLog(int level, IntPtr category, IntPtr message)
    {
        var line = $"[{level}] {Marshal.PtrToStringUTF8(category)}: {Marshal.PtrToStringUTF8(message)}";
        s_log.AppendLine(line);
        Console.WriteLine(line);
        if (line.Contains("qt.scenegraph.general: Loading backend software")) QuickSoftware = true;
        if (line.Contains("qt.rhi.general: OpenGL VENDOR:") && line.Contains("Mesa")) MesaGl = line[(line.IndexOf("RENDERER:") is var r and >= 0 ? r : 0)..];
    }

    // WSL with GPU passthrough (WSLg): Present copies to the host compositor (L4e, #214).
    internal static bool Wslg => !OperatingSystem.IsWindows() && System.IO.File.Exists("/dev/dxg");
    // WSLg with the xcb platform (the default of an export): Weston's X window manager places windows itself.
    internal static bool WslgXcb => Wslg && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb";

    // Check on where a window ends up: under WSLg/xcb the window manager chooses the position of a new window and the
    // position after leaving fullscreen (e.g. X:0 Y:0 instead of the requested one), so a miss there is a SKIP with
    // reason instead of a failure. A pass counts everywhere; sizes and every other platform stay strict (#10).
    internal static Action<bool, string> PositionByWindowManager(Action<bool, string> check) => (ok, what) =>
    {
        if (!ok && WslgXcb) Console.WriteLine($"SKIP {what}: position chosen by the WSLg window manager (Weston/XWayland)");
        else check(ok, what);
    };

    // QRhi OpenGL on Mesa (Linux): GL renderer line from qt.rhi.general, null otherwise.
    internal static string? MesaGl { get; private set; }

    // Actually active Qt Quick backend is the software one (logged when the first window loads its scenegraph), e.g.
    // Linux offscreen without QT_QUICK_BACKEND=rhi (tools/test-linux.sh --software, CI / GPU-less systems).
    internal static bool QuickSoftware { get; private set; }

    // Check that needs a QRhi scenegraph (ShaderEffect/MultiEffect, video output): SKIP with reason under the
    // software backend, otherwise a normal check. Only for checks the software backend cannot pass in principle.
    internal static Action<bool, string> NeedsRhi(Action<bool, string> check, string why) => (ok, what) =>
    {
        if (QuickSoftware) Console.WriteLine($"SKIP {what}: {why} (Qt Quick software backend)");
        else check(ok, what);
    };

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) s_failures.Add(what);
    }

    static HashSet<string>? s_only;
    static bool Selected(string name) => s_only == null || s_only.Contains(name);

    static void Pump(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            s_time += 1.0 / 60.0;
            Native.vlqt_driver_advance(s_time);
            Native.vlqt_app_process_events();
        }
    }

    static int Apply(IntPtr w) => Native.Apply(w, s_cmd.EndFrame());

    static void CheckApply(IntPtr w, string what)
    {
        var rc = Apply(w);
        Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
    }

    // Same scene as Renderer [Qt] (transitional): #202020 background, orange test rect at (1,1) size (3,2) units.
    static IntPtr CreateWithScene()
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt SmokeTest");
        Check(w != IntPtr.Zero, "window_create " + (w == IntPtr.Zero ? Native.LastError() : ""));
        s_cmd.Reset();
        s_cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        s_cmd.Create(RectId, Protocol.Type.Rectangle);
        s_cmd.SetPlace(RectId, 1 * U, 1 * U, 3 * U, 2 * U);
        s_cmd.SetColor(RectId, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        s_cmd.Reparent(RectId, 0, 0);
        CheckApply(w, "apply test scene");
        Native.vlqt_window_show(w);
        return w;
    }

    static Bitmap Grab(IntPtr w, string path)
    {
        var rc = Native.vlqt_window_grab(w, path);
        Check(rc == 0, $"grab {path} " + (rc != 0 ? Native.LastError() : ""));
        return new Bitmap(path);
    }

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 8 && Math.Abs(c.G - g) < 8 && Math.Abs(c.B - b) < 8;
    static bool IsOrange(Color c) => Near(c, 255, 153, 0);
    static bool IsBg(Color c) => Near(c, 0x20, 0x20, 0x20);

    [STAThread] // like vvvv's main thread; Qt needs STA for OleInitialize
    static int Main(string[] args)
    {
        args = PerfLimits.Configure(args); // --perf-warn-only / --perf-factor, VLQT_PERF_* (#71)
        // "--all [outDir]": run every scenario (also the default without a flag); the flag is accepted for CI/docs (08d).
        args = args.Where(a => a != "--all").ToArray();
        // "--only a,b,...": skip the core checks, run only the named scenarios (names at the calls below, e.g. geometry).
        var oi = Array.IndexOf(args, "--only");
        if (oi >= 0 && oi + 1 < args.Length)
        {
            s_only = new HashSet<string>(args[oi + 1].Split(','), StringComparer.OrdinalIgnoreCase);
            args = args.Where((_, i) => i != oi && i != oi + 1).ToArray();
        }
        // "--stress-seconds N" / "--stress-seed S" for resource-stress (#365); without a seed a random one (logged).
        int stressSeconds = TakeIntArg(ref args, "--stress-seconds") ?? ResourceStressTest.DefaultSeconds;
        int stressSeed = TakeIntArg(ref args, "--stress-seed") ?? Environment.TickCount;
        bool videoStopOnly = args.Contains("--video-stop");
        args = args.Where(a => a != "--video-stop").ToArray();
        // Test windows ignore the real cursor (only vlqt_test_send_mouse input), read by QtNative per window (06c).
        SetEnv("VLQT_TEST_SYNTHETIC_INPUT", "1");
        // Controls hover (base style Hovered state, #292): Qt turns it off where the platform has no hover effects
        // (Linux offscreen in CI), so the style test would see Normal there. Same behaviour on every platform.
        SetEnv("QT_QUICK_CONTROLS_HOVER_ENABLED", "1");
        // Linux: the GL renderer/version line (qt.rhi.general) needs QSG_INFO; used to recognize Mesa GL (#214).
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("QSG_INFO") == null) SetEnv("QSG_INFO", "1");
        // Drift check protocol.h <-> Protocol.cs (no Qt needed). "--drift [protocol.h]" runs only this.
        bool driftOnly = args.Length > 0 && args[0] == "--drift";
        var drift = ProtocolDrift.Run(driftOnly && args.Length > 1 ? args[1] : ProtocolDrift.DefaultHeaderPath);
        foreach (var e in drift) Console.WriteLine("DRIFT " + e);
        Check(drift.Count == 0, "protocol.h <-> Protocol.cs drift check");
        if (driftOnly) return drift.Count == 0 ? 0 : 1;

        var replay = args.Length > 1 && args[0] == "--replay";
        var outDir = replay ? (args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.Combine(AppContext.BaseDirectory, "smoke"))
            : args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(outDir);

        // device-lost (#367): the shared device must be created through D3D11On12 (before the first window/resource).
#if WINDOWS
        if (s_only?.Contains("device-lost") == true) SetEnv("VLQT_TEST_D3D11ON12", "1");
#endif

        // 1. init (twice: idempotent)
        Check(Native.vlqt_app_init(Native.NativeDir, s_logFn) == 0, "app_init " + Native.LastError());
        Check(Native.vlqt_app_init(Native.NativeDir, s_logFn) == 0, "app_init idempotent");
        if (replay) return ReplayTest.RunCli(args[1], outDir, Pump, args); // --replay <trace> [outDir] (01e)
        // F5 overlay pump cost (#244); "--overlay-cost [rounds]", default 8 rounds of 120 pumps per mode.
        var ci = Array.IndexOf(args, "--overlay-cost");
        if (ci >= 0)
            return PerfOverlayTest.RunCost(Pump, ci + 1 < args.Length && int.TryParse(args[ci + 1], out var rounds) ? rounds : 8);
        if (videoStopOnly) // "--video-stop": only the #130 scenario (quick repro)
        {
            VideoStopTest.Run(Check, Pump, 5);
            return s_failures.Count == 0 ? 0 : 1;
        }
        if (s_only != null) goto Scenarios;

        // 2. test rect through the command buffer (01d): CREATE / SET Bounds+Color / REPARENT / DESTROY + pixel checks
        var w = CreateWithScene();
        Pump(5);
        Check(Native.vlqt_window_is_visible(w) == 1, "is_visible after show");
        using (var a = Grab(w, Path.Combine(outDir, "grab1.png")))
        {
            Check(IsOrange(Px(a, 250, 200)), $"rect: orange inside (250,200) ({Px(a, 250, 200)}), size {a.Width}x{a.Height}");
            Check(IsOrange(Px(a, 105, 105)) && IsOrange(Px(a, 395, 295)), "rect: orange at its corners (1,1)..(4,3) units");
            Check(IsBg(Px(a, 95, 95)) && IsBg(Px(a, 405, 305)) && IsBg(Px(a, 5, 5)), $"rect: #202020 outside ({Px(a, 405, 305)})");
        }

        // SET Bounds + Color, REPARENT into a group at (4,3) units: rect lands at (4.5,3.5)..(5.5,4.5).
        s_cmd.Reset();
        s_cmd.Create(GroupId, Protocol.Type.Item);
        s_cmd.SetPlace(GroupId, 4 * U, 3 * U, 3 * U, 2 * U);
        s_cmd.Reparent(GroupId, 0, 0);
        s_cmd.SetPlace(RectId, 0.5f * U, 0.5f * U, 1 * U, 1 * U);
        s_cmd.SetColor(RectId, Protocol.Prop.Color, 0, 0, 1, 1);
        s_cmd.Reparent(RectId, GroupId, 0);
        CheckApply(w, "apply set + reparent");
        Pump(3);
        using (var a = Grab(w, Path.Combine(outDir, "grab2.png")))
        {
            Check(Near(Px(a, 500, 400), 0, 0, 255), $"rect: blue inside group at (5,4) units ({Px(a, 500, 400)})");
            Check(IsBg(Px(a, 250, 200)) && IsBg(Px(a, 440, 340)), $"rect: old place and group corner empty ({Px(a, 250, 200)})");
        }

        // Broken buffer: error code, message, earlier state stays.
        s_cmd.Reset();
        s_cmd.SetPlace(777, 0, 0, 1, 1);
        var rc = Apply(w);
        var err = Native.LastError();
        Check(rc == -4 && err.Contains("777"), $"broken buffer returns error ({rc}: {err})");

        // DESTROY (recursive): group and rect gone.
        s_cmd.Reset();
        s_cmd.Destroy(GroupId);
        CheckApply(w, "apply destroy");
        Pump(3);
        using (var a = Grab(w, Path.Combine(outDir, "grab3.png")))
            Check(IsBg(Px(a, 500, 400)), $"rect: gone after DESTROY ({Px(a, 500, 400)})");
        s_cmd.Reset();
        s_cmd.SetColor(RectId, Protocol.Prop.Color, 1, 1, 1, 1);
        Check(Apply(w) == -4, "rect id released with its group");
        Native.vlqt_window_destroy(w);

        // 3. Perf: timings are filled, overlay (F2 meter + F5 stats) draws top-left.
        // The rect moves every frame (one SET per frame) so the window renders continuously.
        w = CreateWithScene();
        var sw = Stopwatch.StartNew();
        int frame = 0;
        void MoveRect()
        {
            s_cmd.Reset();
            s_cmd.SetPlace(RectId, (1 + (frame++ % 100) * 0.01f) * U, 1 * U, 3 * U, 2 * U);
            Apply(w);
        }
        while (sw.ElapsedMilliseconds < 1500) { MoveRect(); Pump(1); System.Threading.Thread.Sleep(16); }
        Check(Native.vlqt_window_get_perf(w, out var perf) == 0, "get_perf ok");
        Console.WriteLine($"INFO perf: fps {perf.fps:F1}, frame {perf.frame_ms:F2} (max {perf.frame_ms_max:F2}), pump {perf.pump_ms:F2} (max {perf.pump_ms_max:F2}), " +
            $"sync {perf.sync_ms:F2}, render {perf.render_ms:F2}, present {perf.present_ms:F2}, gpu {perf.gpu_ms:F3} (max {perf.gpu_ms_max:F3}), swaps/pump {perf.swaps_per_pump:F2}");
        Check(perf.samples > 0 && perf.fps > 0 && perf.pump_ms > 0 && perf.render_ms > 0, "perf values filled");
        // Linux + CPU rasterizer (offscreen/llvmpipe, L4a): QRhi's OpenGL backend delivers no timestamps there (gpu 0).
        if (!OperatingSystem.IsWindows() && perf.gpu_ms == 0 && s_log.ToString().Contains("SOFTWARE RENDERING"))
            Console.WriteLine("SKIP GPU timestamps: none with software rendering on Linux");
        // Linux + QRhi OpenGL on Mesa (e.g. WSLg d3d12): QRhi reports the Timestamps feature, but lastCompletedGpuTime()
        // stays 0 (L4e, #214). Real values (e.g. a future Mesa) run the normal check again.
        else if (!OperatingSystem.IsWindows() && perf.gpu_ms == 0 && MesaGl != null)
            Console.WriteLine($"SKIP GPU timestamps: QRhi OpenGL on Mesa reports 0 ({MesaGl})");
        else
            NeedsRhi(Check, "no QRhi, no GPU timestamps")(perf.gpu_ms > 0, "GPU timestamps delivered");
        using (var before = Grab(w, Path.Combine(outDir, "perf0.png")))
        {
            Native.vlqt_window_set_overlay(w, 3);
            Check((Native.vlqt_window_get_overlay(w) & 3) == 3, "overlay flags set"); // bits 4..6: F5 page (#244)
            Pump(20);
            using var after = Grab(w, Path.Combine(outDir, "perf1.png"));
            Check(!SameImage(before, after) && !IsBg(after.GetPixel(30, 30)), "overlay drawn top-left");
        }

        // 4. 10 000 SETs in one apply, written by CommandWriter (01d acceptance: < 2 ms, Release).
        s_cmd.Reset();
        for (uint i = 0; i < 100; i++) { s_cmd.Create(100 + i, Protocol.Type.Rectangle); s_cmd.SetPlace(100 + i, 0, 0, 10, 10); s_cmd.Reparent(100 + i, 0, 1000); }
        CheckApply(w, "apply: create 100 perf items");
        var applyMs = new double[20];
        var writeMs = new double[20];
        // #64: best of up to 3 rounds (see PerfLimits.Best); the INFO line shows the last round's spread.
        var applyMedian = PerfLimits.Best(2.0, () =>
        {
            for (int run = 0; run < applyMs.Length; run++)
            {
                var t = Stopwatch.StartNew();
                s_cmd.Reset();
                for (int i = 0; i < 10_000; i++)
                    // #469: one SET per item like the old BOUNDS: the position = TRANSFORM translation (SIZE/ANCHOR set above).
                    s_cmd.SetMat4(100 + (uint)(i % 100), Protocol.Prop.Transform, Stride.Core.Mathematics.Matrix.Translation(i % 700, run, 0));
                var buf = s_cmd.EndFrame();
                writeMs[run] = t.Elapsed.TotalMilliseconds;
                t.Restart();
                rc = Native.Apply(w, buf);
                applyMs[run] = t.Elapsed.TotalMilliseconds;
                if (rc != 0) { Check(false, "apply perf " + Native.LastError()); break; }
            }
            Array.Sort(applyMs);
            Array.Sort(writeMs);
            return applyMs[10];
        }, cpuBound: true);
        Console.WriteLine($"INFO 10000 SET bounds ({s_cmd.Length} bytes): apply median {applyMedian:F3} ms (last round: min {applyMs[0]:F3}, max {applyMs[^1]:F3}), " +
            $"CommandWriter median {writeMs[10]:F3} ms");
        PerfLimits.Below(Check, applyMedian, 2.0, $"10000 SET in one apply < 2 ms (median {applyMedian:F3} ms)", cpuBound: true);

        // 5. No managed allocation per frame: 1000 frames of writer + apply.
        for (int f = 0; f < 2; f++) MoveRect(); // warm-up
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 1000; f++)
        {
            s_cmd.Reset();
            for (uint i = 0; i < 100; i++)
                s_cmd.SetPlace(100 + i, f % 700, i, 10, 10);
            Native.Apply(w, s_cmd.EndFrame());
        }
        var alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        Check(alloc < 1024, $"1000 frames writer + apply allocate < 1 KB ({alloc} bytes)");
        Native.vlqt_window_destroy(w);

        // Command buffer details: error cases, stacking, transform, poll (01c).
    Scenarios:
        var scenarios = Stopwatch.StartNew();
        if (Selected("apply")) ApplyTest.Run(outDir, Check, Pump, Grab);

        // Element registry: Group, common properties, Matrix4x4 transform (05a).
        if (Selected("elements")) ElementsTest.Run(outDir, Check, Pump, Grab, () => s_log.ToString());

        // Global pump with simulated FrameClock: 2 windows, 120 frames, stress (02b).
        if (Selected("pump")) PumpTest.Run(Check);
        if (Selected("input-latency")) HandlerTest.RunInputLatency(Check); // Input at the frame start: one frame earlier, nothing twice or lost, lifecycle (#24).
        if (Selected("perfstats")) PerfStatsTest.Run(Check, Pump); // Percentiles, counters, last sample, budget (#243).
        if (Selected("perfoverlay")) PerfOverlayTest.Run(outDir, Check, Pump, Grab); // F5 pages, keys, spike in the timeline (#244).
        // Rectangle: color, radius, border; 1000-rect performance (05b).
        if (Selected("rectangle")) RectangleTest.Run(outDir, Check, Pump, Grab);
        // Text: font, alignment, wrap, elide, lineHeight, letterSpacing, fontSizeMode (05c).
        if (Selected("text")) TextTest.Run(outDir, Check, Pump, Grab);
        // Image: image resource, fillMode, smooth, failed resource (05d, #361).
        if (Selected("image")) ImageTest.Run(outDir, Check, Pump, Grab);
        if (Selected("image-preview")) ImagePreviewTest.Run(outDir, Check, Pump); // Tooltip preview of image resources, flag, 256 px, replacement (#546).
        if (Selected("image-share")) ImageShareTest.Run(outDir, Check, Pump, Grab); // One image resource in 200 elements / 2 windows, switch, replacement, perf (#361).
        if (Selected("video")) VideoTest.Run(outDir, Check, Pump, Grab, () => s_log.ToString()); // Video: MediaPlayer + VideoOutput, EV_MEDIA, file change, leaks (#115).
        if (Selected("videostop")) VideoStopTest.Run(Check, Pump); // #130: destroy of a playing video with audio, then window_destroy: no hang.
        // Trace replay: live scenario vs. replayed trace (01e).
        if (Selected("replay")) ReplayTest.Run(outDir, Check, Pump, Grab);
        if (Selected("rowcolumn")) RowColumnTest.Run(outDir, Check, Pump, Grab); // Row/Column: spacing, padding, childAlignment (05e).
        if (Selected("measure")) MeasureTest.Run(Check, Pump); // Back channel EV_MEASURED / EV_SCENE_BOUNDS (06b).
        if (Selected("sizereset")) SizeResetTest.Run(outDir, Check, Pump, Grab); // Size 0 -> implicit size (resetWidth/resetHeight, #83, #469).
        if (Selected("interaction-chain")) { InteractionTest.RunChain(Check, Pump); InteractionTest.RunElements(Check, Pump); } // Hover node -> Rectangle Interaction pin -> handler objects -> channels (#319).
        if (Selected("click-node")) InteractionTest.RunClick(Check, Pump); // Click node at a Rectangle: mouse, double click, touch, long press, Buttons, Devices (#320).
        if (Selected("point-node")) InteractionTest.RunPoint(Check, Pump); // Point node: Is Active + Position in units, chain Hover -> Click -> Point (#320).
        if (Selected("wheel-node")) InteractionTest.RunWheel(Check, Pump); // Wheel node: Delta per frame, then once (0, 0) (#320).
        if (Selected("handlers")) HandlerTest.Run(Check, Pump); // Input handlers as protocol objects (Hover), touch test export (#319).
        if (Selected("tap")) HandlerTest.RunTap(Check, Pump); // Tap handler: click, double click, long press, buttons, touch, stacking (#320).
        if (Selected("point")) HandlerTest.RunPoint(Check, Pump); // Point handler: active + position (mouse, touch), passes clicks through (#320).
        if (Selected("point-tap")) HandlerTest.RunPointTap(Check, Pump); // Tap + Point at one item share the button and touch; stacked top wins (#320).
        if (Selected("wheel")) HandlerTest.RunWheel(Check, Pump); // Wheel handler: notches per poll, then once (0, 0) (#320).
        if (Selected("drag")) HandlerTest.RunDrag(Check, Pump); // Drag handler: offset, SET BOUNDS reset, transform order, axis, limits (#209).
        if (Selected("drag-row")) HandlerTest.RunDragRow(Check, Pump); // Drag of a Row child: siblings and layout stay (#209).
        if (Selected("drag-group")) HandlerTest.RunDragGroup(Check, Pump); // Group drags as a whole, child with own drag alone (#209).
        if (Selected("drag-nested")) HandlerTest.RunDragNested(Check, Pump); // Nested drags: innermost wins, axis pass-through (#209).
        if (Selected("touch-points")) HandlerTest.RunTouchPoints(Check, Pump); // Touch Points: all points, occlusion, devices (#321).
        if (Selected("pinch")) HandlerTest.RunPinch(Check, Pump); // Pinch: values while active, third finger without a jump (#321).
        if (Selected("right-click")) HandlerTest.RunSyntheticRightClick(Check, Pump); // Windows press-and-hold right click dropped (#321).
        if (Selected("drag-node")) InteractionTest.RunDragNode(Check, Pump); // Drag node: element follows, Scene Bounds, Bounds pin wins, Moves Element off (#209).
        if (Selected("touch-points-node")) InteractionTest.RunTouchPointsNode(Check, Pump); // Touch Points node: two fingers -> two TouchPoints with stable ids, units, release (#321).
        if (Selected("pinch-node")) InteractionTest.RunPinchNode(Check, Pump); // Pinch node: scale, rotation in cycles, Is Active, last value kept (#321).
        if (Selected("button")) ButtonTest.Run(Check, Pump); // Button (QtQuick.Controls, style Basic): implicit size, CLICK, Enabled, AutoRepeat (#213).
        if (Selected("multiplacement")) MultiPlacementTest.Run(outDir, Check, Pump, Grab); // One element at several places of the tree: drawing, clicks, primary state (#455).
        if (Selected("style")) StyleTest.Run(outDir, Check, Pump, Grab); // SET_STYLE: base style, override, CLEAR, hover, error codes, gallery PNG (#292).
        if (Selected("stylable")) StylableTest.Run(outDir, Check, Pump, Grab); // IStylable: self-made widget follows the Group style, runtime changes, throwing IStylable (#413).
        if (Selected("spinbox")) SpinBoxTest.Run(Check, Pump); // SpinBox (Input (Int)): EV_VALUE on +/-, wheel, Enter, focus loss; no echo; int range (#210).
        if (Selected("valueinputs")) ValueInputsTest.Run(Check, Pump, () => s_log.ToString()); // CheckBox/Switch/Slider/TextField: EV_VALUE, no echo, stale SET, rebind, context menu (#301).
#if WINDOWS
        if (Selected("keyroute")) KeyRouteTest.Run(Check, Pump); // Enter/arrows over Win32 + WinForms loop like vvvv (IsDialogMessage, #301).
#endif
        if (Selected("effect-layer")) EffectLayerTest.Run(outDir, Check, Pump, Grab); // Effect chain, raw (SET_EFFECT_CHAIN, #489): one MultiEffect per run, on/off without leak, two runs, SET EFFECT_* -6.
        if (Selected("opacity-zero")) OpacityZeroTest.Run(outDir, Check, Pump, Grab); // Opacity 0: not drawn (also with effect layer), keeps Row space and input (#474).
        if (Selected("effect-mask")) EffectMaskTest.Run(outDir, Check, Pump, Grab); // Effect mask per MULTI run (EFFECT_MASK_SOURCE entry): text mask, inverted, size rule, destroy, -8 (effects/01, #489).
        if (Selected("effect-chain")) EffectChainTest.Run(outDir, Check, Pump, Grab); // Effect chain through the real nodes: one MultiEffect, order, no change, Row, Mask, duplicate (effects/01 Task 5).
        if (Selected("effect-passes")) EffectPassesTest.Run(outDir, Check, Pump, Grab); // SHADER links of the Effect chain (#489): SINGLE/TWO_PASS, order, padding, texelSize/dpr, REF texture, errors, no leak.
        if (s_only?.Contains("readme-images") == true) ReadmeImagesTest.Run(outDir, Check, Pump, Grab); // README pictures of the Effect chain (#489): chains and single effects -> docs/images/ (only on request).
        if (s_only?.Contains("readme-gallery") == true) ReadmeGalleryTest.Run(outDir, Check, Pump, Grab); // README pictures of styles, layout, images and basic effects -> docs/images/ (only on request).
        if (Selected("shader-effect")) ShaderEffectTest.Run(outDir, Check, Pump, Grab); // ShaderEffect: builtin:wave/gradient, sampler uniforms, mesh, broken paths, uniform errors (#437).
        if (Selected("shader-source")) ShaderSourceTest.Run(outDir, Check, Pump, Grab); // ShaderEffect Phase 2: GLSL text baked at runtime, line numbers, last good version, cache (#483).
        if (Selected("restart")) RestartTest.Run(outDir, Check, Pump); // #526: quick repeated restarts: window destroyed inside the pump (host callback in processEvents), restarts while a shader text bakes.
        if (Selected("group-shader-source")) GroupShaderSourceTest.Run(outDir, Check, Pump, Grab); // Group as ShaderEffect Input / texture uniform: whole frame incl. negative part, live (#486).
        if (Selected("group-shader-window-destroy")) GroupShaderWindowDestroyTest.Run(Check, Pump); // window_destroy with Group-input effects still in the tree: no crash (#534).
        if (Selected("blur-mask"))BlurMaskTest.Run(outDir, Check, Pump, Grab); // Example blur with control mask: separable two-pass ShaderEffect, correctness + FHD/4K timings (#487).
        if (Selected("glow")) GlowTest.Run(outDir, Check, Pump, Grab); // Glow (#438 A1): blur pyramid as nested ShaderEffects, radius 4/32/256, spread/intensity/additive, sweep without jumps.
        if (Selected("innershadow")) InnerShadowTest.Run(outDir, Check, Pump, Grab); // InnerShadow (#438 A4): blur pyramid, shadow inside the alpha, offset, radius 4/32/128 on rectangle and text, dither without rings.
        if (Selected("sampler")) SamplerTest.Run(outDir, Check, Pump, Grab); // Sampler state of a sampler2D: Point/Linear, Clamp/Wrap/Mirror, mip filter on a Group layer, live, errors (#492).
        if (Selected("displace")) DisplaceTest.Run(outDir, Check, Pump, Grab); // builtin:displace: displacement map from an element, shift in DIP, map alpha, live (#438).
        if (Selected("blend")) BlendTest.Run(outDir, Check, Pump, Grab); // builtin:blend: 13 modes against the reference formula, transparent layer, opacity (#439).
        if (Selected("blend-chain")) BlendChainTest.Run(outDir, Check, Pump, Grab); // Blend/Displace behind a Blur: layer on the element, halo kept (#439).
        if (Selected("motion-blur")) MotionBlurTest.Run(outDir, Check, Pump, Grab); // Motion blurs: builtin:directional_blur / zoom_blur / radial_blur, parameters visible, no stepping, sample cap (#438).
        if (Selected("renderer")) RendererTest.Run(outDir, Check, Pump, Grab); // Renderer core: element tree, Clear Color, Enabled, Title/Size, Dispose (07a).
        if (Selected("vsync")) RendererTest.RunVSync(Check, Pump); // VSync pin: window with swap interval 1 (#95).
        if (Selected("geometry")) RendererTest.RunGeometry(Check, Pump); // Geometry on create + EV_WINDOW_GEOMETRY round trip (#100, #165, #143).
        if (Selected("visibility")) RendererTest.RunVisibility(Check, Pump); // Is Visible: creation, X/Hide/Show, Dispose, recreation (#93).
        if (Selected("hotswap")) RendererTest.RunHotswap(Check, Pump); // Renderer added live / Undo-Redo: hotswap Bounds = type default (#133).
        if (Selected("borderless")) BorderlessTest.Run(Check, Pump); // Borderless pin: live toggle, client stays, < 130 px, on create (#233).
        if (Selected("fullscreen")) FullscreenTest.Run(Check, Pump); // Fullscreen pin: live + on create, restore, Save Bounds, second monitor (#234).
        if (Selected("window-icon")) WindowIconTest.Run(outDir, Check, Pump); // Window icon: default at create, custom, NULL, -7, WM_GETICON (#517).
        if (Selected("reconciler")) ReconcilerTest.Run(outDir, Check, Pump, Grab); // Element tree → Reconciler → QtNative: Column with 3 rectangles, perf (04e).
        if (Selected("containernode")) ContainerNodeTest.Run(outDir, Check, Pump, Grab); // Group/Row/Column nodes with Rectangle nodes through QtNative (07d).
        if (Selected("resync")) ResyncTest.Run(outDir, Check, Pump, Grab); // Resync after a failed apply: invalid command (-7) → full rebuild in the next frame (#76).
        if (Selected("anchor")) AnchorTest.Run(outDir, Check, Pump, Grab); // Anchor against the actual size, placement via Transform (Skia semantics), Row/Group Auto Size on/off (#88, #469).
        if (Selected("debug")) DebugOverlayTest.Run(outDir, Check, Pump, Grab); // Debug overlay: per element + window-wide, layout/measure/hit-test unchanged (#279).
        if (Selected("resoverlay")) PerfOverlayTest.RunResources(outDir, Check, Pump, Grab); // F5 Resources page: states, budget, order, 1000 resources (#364).
        if (Selected("resources")) ResourceTest.Run(Check, Pump, () => s_log.ToString()); // Resource manager: states, stale ids, budget, release, wait, shutdown (#359).
        if (Selected("image-resource")) ImageResourceTest.Run(outDir, Check, Pump, () => s_log.ToString()); // Image resources: files, pixel formats, replacement, perf (#360).
        if (Selected("qtimage")) QtImageTest.Run(outDir, Check, Pump, Grab); // QtImage nodes → ResourceClient → QtNative, one image in 100 elements, release queue (#362).
        if (Selected("snapshot")) SnapshotTest.Run(outDir, Check, Pump, Grab); // Snapshot node via ResourceClient and QtNative: pixels vs window grab, latency, error paths, hitches (#363).
        if (Selected("tooltip")) TooltipTest.Run(outDir, Check, Pump, Grab); // Tooltip previews: window change count (static scene, property, hover, video), snapshot_fit sizes, errors (#544).
        if (Selected("font")) FontTest.Run(outDir, Check, Pump, Grab, () => s_log.ToString()); // App fonts (#15, LoadFont): shared per file, refs, removal at the end of the pump, errors, Text with the loaded family, one-time warning for a missing family.
        if (Selected("group-frame")) GroupFrameTest.Run(outDir, Check, Pump, Grab); // Group frame in negative coordinates: snapshot = whole frame, effect layer unclipped (#475).
        // Last: leaves two VSync windows open on purpose (process end with open windows on the shared device).
        if (Selected("shared-device")) SharedDeviceTest.Run(Check, Pump); // One D3D11 device for all windows + upload QRhi (#358).
        // Only when named: needs VLQT_TEST_D3D11ON12 from the start (set above). Real device removal + rebuild (#367).
#if WINDOWS
        if (s_only?.Contains("device-lost") == true) DeviceLostTest.Run(outDir, Check, Pump);
#else
        if (s_only?.Contains("device-lost") == true) Console.WriteLine("INFO device-lost: Windows only (D3D11On12), skipped");
#endif
        // Only when named (60 s by default, too long for the regular run); before LeaveRunningJobs, which ends with busy workers.
        if (s_only?.Contains("resource-stress") == true) ResourceStressTest.Run(outDir, Check, Pump, () => s_log.ToString(), stressSeconds, stressSeed); // Stress test resource manager, ASan/TSan target (#365).
        if (Selected("resources")) ResourceTest.LeaveRunningJobs(Check, Pump); // After everything: process end with busy workers (#359).

        Console.WriteLine($"INFO scenarios after the core checks: {scenarios.Elapsed.TotalSeconds:F1} s");
        var log = s_log.ToString();
        // Present never waits for VSync (the pump must not block); one render per pump is checked in PumpTest 1c (#137).
        NeedsRhi(Check, "no swap chain")(log.Contains("Swap interval is 0, attempting to disable vsync"), "log: swap interval 0 (no VSync wait in the pump)");
        if (OperatingSystem.IsWindows())
            NeedsRhi(Check, "no QRhi")(log.Contains("D3D11") || log.Contains("Direct3D 11") || log.Contains("D3D 11"), "log shows D3D11");
        else if (Environment.GetEnvironmentVariable("VLQT_TEST_QUICK_BACKEND") == "software") // test-linux.sh --software
            Check(QuickSoftware, "log shows the Qt Quick software backend (requested)");
        else // Linux: Qt's default RHI backend (OpenGL), the backend choice is L4b; Qt Quick's software backend fails here
            Check(log.Contains("Creating QRhi with backend") && !QuickSoftware, "log shows a QRhi backend (not Qt Quick software)");
        File.WriteAllText(Path.Combine(outDir, "smoke.log"), log);

        if (PerfLimits.Warnings > 0) Console.WriteLine($"WARN {PerfLimits.Warnings} perf check(s) exceeded their limit (warn-only)");
        Console.WriteLine(s_failures.Count == 0 ? "SMOKE OK" : $"SMOKE FAILED ({s_failures.Count})");
        return s_failures.Count == 0 ? 0 : 1;
    }

    // Removes "name value" from args and returns the value (null if missing or not an int).
    static int? TakeIntArg(ref string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return null;
        var value = int.TryParse(args[i + 1], out var v) ? v : (int?)null;
        args = args.Where((_, j) => j != i && j != i + 1).ToArray();
        return value;
    }

    // Linux: Environment.SetEnvironmentVariable only changes .NET's copy, QtNative reads libc getenv (L2b finding) ->
    // also call libc setenv there. Must run before QtNative reads the variable.
    internal static void SetEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows() && setenv(name, value, 1) != 0)
            throw new InvalidOperationException($"setenv {name} failed");
    }
    [DllImport("libc")] static extern int setenv(string name, string value, int overwrite);

    static bool SameImage(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y += 2)
            for (int x = 0; x < a.Width; x += 2)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }
}
