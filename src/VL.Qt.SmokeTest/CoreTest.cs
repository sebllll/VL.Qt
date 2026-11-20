using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

/// <summary>
/// Core checks of QtNative (the former inline checks of Program.Main): app init, the test rectangle through the command
/// buffer (CREATE / SET / REPARENT / DESTROY + pixels), perf values and overlay, apply cost and allocations, the Qt log.
/// </summary>
[TestFixture, NonParallelizable]
public static class CoreTest
{
    // Space v1 = DIPTopLeft: 1 unit = 100 DIP = 100 Qt logical pixels (like Renderer [Qt]).
    const float U = 100f;
    const uint RectId = 1, GroupId = 2;

    /// <summary>vlqt_app_init succeeds and a second call is a no-op.</summary>
    [Test, Category("core-app")]
    public static void AppInit() => Scenario.Run(s =>
    {
        s.Check(QtHost.InitResult == 0, "app_init " + QtHost.InitError);
        s.Check(QtHost.InitAgain() == 0, "app_init idempotent");
    });

    /// <summary>Test rect through the command buffer: pixels after create, set + reparent, a broken buffer, destroy.</summary>
    [Test, Category("core-rect")]
    public static void Rect() => Scenario.Run(s =>
    {
        var cmd = new CommandWriter();
        var w = CreateWithScene(s, cmd);
        s.Pump(5);
        s.Check(Native.vlqt_window_is_visible(w) == 1, "is_visible after show");
        using (var a = s.Grab(w, "grab1.png"))
        {
            s.Check(IsOrange(Px(a, 250, 200)), $"rect: orange inside (250,200) ({Px(a, 250, 200)}), size {a.Width}x{a.Height}");
            s.Check(IsOrange(Px(a, 105, 105)) && IsOrange(Px(a, 395, 295)), "rect: orange at its corners (1,1)..(4,3) units");
            s.Check(IsBg(Px(a, 95, 95)) && IsBg(Px(a, 405, 305)) && IsBg(Px(a, 5, 5)), $"rect: #202020 outside ({Px(a, 405, 305)})");
        }

        // SET Bounds + Color, REPARENT into a group at (4,3) units: rect lands at (4.5,3.5)..(5.5,4.5).
        cmd.Reset();
        cmd.Create(GroupId, Protocol.Type.Item);
        cmd.SetPlace(GroupId, 4 * U, 3 * U, 3 * U, 2 * U);
        cmd.Reparent(GroupId, 0, 0);
        cmd.SetPlace(RectId, 0.5f * U, 0.5f * U, 1 * U, 1 * U);
        cmd.SetColor(RectId, Protocol.Prop.Color, 0, 0, 1, 1);
        cmd.Reparent(RectId, GroupId, 0);
        CheckApply(s, w, cmd, "apply set + reparent");
        s.Pump(3);
        using (var a = s.Grab(w, "grab2.png"))
        {
            s.Check(Near(Px(a, 500, 400), 0, 0, 255), $"rect: blue inside group at (5,4) units ({Px(a, 500, 400)})");
            s.Check(IsBg(Px(a, 250, 200)) && IsBg(Px(a, 440, 340)), $"rect: old place and group corner empty ({Px(a, 250, 200)})");
        }

        // Broken buffer: error code, message, earlier state stays.
        cmd.Reset();
        cmd.SetPlace(777, 0, 0, 1, 1);
        var rc = Native.Apply(w, cmd.EndFrame());
        var err = Native.LastError();
        s.Check(rc == -4 && err.Contains("777"), $"broken buffer returns error ({rc}: {err})");

        // DESTROY (recursive): group and rect gone.
        cmd.Reset();
        cmd.Destroy(GroupId);
        CheckApply(s, w, cmd, "apply destroy");
        s.Pump(3);
        using (var a = s.Grab(w, "grab3.png"))
            s.Check(IsBg(Px(a, 500, 400)), $"rect: gone after DESTROY ({Px(a, 500, 400)})");
        cmd.Reset();
        cmd.SetColor(RectId, Protocol.Prop.Color, 1, 1, 1, 1);
        s.Check(Native.Apply(w, cmd.EndFrame()) == -4, "rect id released with its group");
        Native.vlqt_window_destroy(w);
    });

    /// <summary>Perf values and the overlay; 10 000 SETs in one apply; no managed allocation per frame.</summary>
    [Test, Category("core-perf")]
    public static void Perf() => Scenario.Run(s =>
    {
        // Timings are filled, overlay (F2 meter + F5 stats) draws top-left. The rect moves every frame (one SET per frame)
        // so the window renders continuously.
        var cmd = new CommandWriter();
        var w = CreateWithScene(s, cmd);
        var sw = Stopwatch.StartNew();
        int frame = 0;
        void MoveRect()
        {
            cmd.Reset();
            cmd.SetPlace(RectId, (1 + (frame++ % 100) * 0.01f) * U, 1 * U, 3 * U, 2 * U);
            Native.Apply(w, cmd.EndFrame());
        }
        while (sw.ElapsedMilliseconds < 1500)
        {
            MoveRect();
            s.Pump(1);
            System.Threading.Thread.Sleep(16);
        }
        s.Check(Native.vlqt_window_get_perf(w, out var perf) == 0, "get_perf ok");
        s.Info($"perf: fps {perf.fps:F1}, frame {perf.frame_ms:F2} (max {perf.frame_ms_max:F2}), pump {perf.pump_ms:F2} (max {perf.pump_ms_max:F2}), " +
            $"sync {perf.sync_ms:F2}, render {perf.render_ms:F2}, present {perf.present_ms:F2}, gpu {perf.gpu_ms:F3} (max {perf.gpu_ms_max:F3}), swaps/pump {perf.swaps_per_pump:F2}");
        s.Check(perf.samples > 0 && perf.fps > 0 && perf.pump_ms > 0 && perf.render_ms > 0, "perf values filled");
        // Linux + CPU rasterizer (offscreen/llvmpipe, L4a): QRhi's OpenGL backend delivers no timestamps there (gpu 0).
        if (!OperatingSystem.IsWindows() && perf.gpu_ms == 0 && s.Log.Contains("SOFTWARE RENDERING"))
            s.Skip("GPU timestamps: none with software rendering on Linux");
        // Linux + QRhi OpenGL on Mesa (e.g. WSLg d3d12): QRhi reports the Timestamps feature, but lastCompletedGpuTime()
        // stays 0 (L4e, #214). Real values (e.g. a future Mesa) run the normal check again.
        else if (!OperatingSystem.IsWindows() && perf.gpu_ms == 0 && QtHost.MesaGl != null)
            s.Skip($"GPU timestamps: QRhi OpenGL on Mesa reports 0 ({QtHost.MesaGl})");
        else
            s.NeedsRhi(perf.gpu_ms > 0, "GPU timestamps delivered", "no QRhi, no GPU timestamps");
        using (var before = s.Grab(w, "perf0.png"))
        {
            Native.vlqt_window_set_overlay(w, 3);
            s.Check((Native.vlqt_window_get_overlay(w) & 3) == 3, "overlay flags set"); // bits 4..6: F5 page (#244)
            s.Pump(20);
            using var after = s.Grab(w, "perf1.png");
            s.Check(!SameImage(before, after) && !IsBg(after.GetPixel(30, 30)), "overlay drawn top-left");
        }

        // 10 000 SETs in one apply, written by CommandWriter (acceptance: < 2 ms, Release).
        cmd.Reset();
        for (uint i = 0; i < 100; i++)
        {
            cmd.Create(100 + i, Protocol.Type.Rectangle);
            cmd.SetPlace(100 + i, 0, 0, 10, 10);
            cmd.Reparent(100 + i, 0, 1000);
        }
        CheckApply(s, w, cmd, "apply: create 100 perf items");
        var applyMs = new double[20];
        var writeMs = new double[20];
        // #64: best of up to 3 rounds (see PerfLimits.Best); the INFO line shows the last round's spread.
        var applyMedian = PerfLimits.Best(2.0, () =>
        {
            for (int run = 0; run < applyMs.Length; run++)
            {
                var t = Stopwatch.StartNew();
                cmd.Reset();
                for (int i = 0; i < 10_000; i++)
                    // #469: one SET per item like the old BOUNDS: the position = TRANSFORM translation (SIZE/ANCHOR set above).
                    cmd.SetMat4(100 + (uint)(i % 100), Protocol.Prop.Transform, Stride.Core.Mathematics.Matrix.Translation(i % 700, run, 0));
                var buf = cmd.EndFrame();
                writeMs[run] = t.Elapsed.TotalMilliseconds;
                t.Restart();
                var rc = Native.Apply(w, buf);
                applyMs[run] = t.Elapsed.TotalMilliseconds;
                if (rc != 0)
                {
                    s.Check(false, "apply perf " + Native.LastError());
                    break;
                }
            }
            Array.Sort(applyMs);
            Array.Sort(writeMs);
            return applyMs[10];
        }, cpuBound: true);
        s.Info($"10000 SET bounds ({cmd.Length} bytes): apply median {applyMedian:F3} ms (last round: min {applyMs[0]:F3}, max {applyMs[^1]:F3}), " +
            $"CommandWriter median {writeMs[10]:F3} ms");
        s.Perf("core-perf.apply_10000_set", applyMedian);
        s.Perf("core-perf.writer_10000_set", writeMs[10]);
        PerfLimits.Below(s, applyMedian, 2.0, $"10000 SET in one apply < 2 ms (median {applyMedian:F3} ms)", cpuBound: true);

        // No managed allocation per frame: 1000 frames of writer + apply.
        for (int f = 0; f < 2; f++)
            MoveRect(); // warm-up
        var alloc = AllocatedBy1000Frames(w, cmd);
        s.Check(alloc < 1024, $"1000 frames writer + apply allocate < 1 KB ({alloc} bytes)");
        Native.vlqt_window_destroy(w);
    });

    /// <summary>The Qt log shows the swap interval 0 of a window without VSync and the expected graphics backend.</summary>
    [Test, Category("core-log")]
    public static void Log() => Scenario.Run(s =>
    {
        // vlqt_window_create (the short form most scenarios use) makes a window with swap interval 0, so their pumps never
        // wait for VSync (one render per pump is checked in PumpTest 1c, #137). One such window of its own makes the
        // check independent of the order and the selection of the other tests.
        var cmd = new CommandWriter();
        var w = CreateWithScene(s, cmd);
        s.Pump(5);
        Native.vlqt_window_destroy(w);
        var log = s.Log;
        s.NeedsRhi(log.Contains("Swap interval is 0, attempting to disable vsync"), "log: swap interval 0 for the windows without VSync", "no swap chain");
        if (OperatingSystem.IsWindows())
            s.NeedsRhi(log.Contains("D3D11") || log.Contains("Direct3D 11") || log.Contains("D3D 11"), "log shows D3D11", "no QRhi");
        else if (Environment.GetEnvironmentVariable("VLQT_TEST_QUICK_BACKEND") == "software") // test-linux.sh --software
            s.Check(QtHost.QuickSoftware, "log shows the Qt Quick software backend (requested)");
        else // Linux: Qt's default RHI backend (OpenGL), the backend choice is L4b; Qt Quick's software backend fails here
            s.Check(log.Contains("Creating QRhi with backend") && !QtHost.QuickSoftware, "log shows a QRhi backend (not Qt Quick software)");
    });

    // Bytes allocated by 1000 frames of 100 SETs + apply. A method of its own: inside the scenario lambda the JIT'ed loop
    // allocated 80 bytes per frame (on-stack replacement of the long lambda), which is not what the check is about.
    static long AllocatedBy1000Frames(IntPtr w, CommandWriter cmd)
    {
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 1000; f++)
        {
            cmd.Reset();
            for (uint i = 0; i < 100; i++)
                cmd.SetPlace(100 + i, f % 700, i, 10, 10);
            Native.Apply(w, cmd.EndFrame());
        }
        return GC.GetAllocatedBytesForCurrentThread() - alloc0;
    }

    // Same scene as Renderer [Qt]: #202020 background, orange test rect at (1,1) size (3,2) units.
    static IntPtr CreateWithScene(Scenario s, CommandWriter cmd)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt SmokeTest");
        s.Check(w != IntPtr.Zero, "window_create " + (w == IntPtr.Zero ? Native.LastError() : ""));
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(RectId, Protocol.Type.Rectangle);
        cmd.SetPlace(RectId, 1 * U, 1 * U, 3 * U, 2 * U);
        cmd.SetColor(RectId, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(RectId, 0, 0);
        CheckApply(s, w, cmd, "apply test scene");
        Native.vlqt_window_show(w);
        return w;
    }

    static void CheckApply(Scenario s, IntPtr w, CommandWriter cmd, string what)
    {
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
    }
}
