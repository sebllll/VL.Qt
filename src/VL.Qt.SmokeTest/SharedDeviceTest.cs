using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VL.Qt;

/// <summary>
/// #358 (images package 01): all windows render on one D3D11 device created by QtNative; next to them an upload QRhi on
/// the same device. Two windows report the same ID3D11Device, closing and reopening one keeps it, GPU timestamps keep
/// coming per window, and the frame intervals of two VSync windows are printed (compare with VLQT_SHARED_DEVICE=0).
/// The two VSync windows stay open on purpose: the process must end cleanly with open windows.
/// </summary>
static class SharedDeviceTest
{
    const uint Rect = 1;

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        if (!OperatingSystem.IsWindows() || Program.QuickSoftware)
        {
            Console.WriteLine("SKIP shared-device: D3D11 only (Linux keeps a device per window)");
            return;
        }
        var shared = Environment.GetEnvironmentVariable("VLQT_SHARED_DEVICE") != "0";
        var cmd = new CommandWriter();

        IntPtr Open(string title, float x, int swapInterval)
        {
            var w = Native.vlqt_window_create_at(x, 100, 400, 300, title, swapInterval);
            check(w != IntPtr.Zero, $"358: create {title} " + (w == IntPtr.Zero ? Native.LastError() : ""));
            cmd.Reset();
            cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
            cmd.Create(Rect, Protocol.Type.Rectangle);
            cmd.SetPlace(Rect, 20, 20, 100, 60);
            cmd.SetColor(Rect, Protocol.Prop.Color, 1, 0.6f, 0, 1);
            cmd.Reparent(Rect, 0, 0);
            check(Native.Apply(w, cmd.EndFrame()) == 0, $"358: apply {title}");
            Native.vlqt_window_show(w);
            return w;
        }

        int frame = 0;
        void Animate(IntPtr w)
        {
            cmd.Reset();
            cmd.SetPlace(Rect, 20 + frame % 200, 20, 100, 60);
            Native.Apply(w, cmd.EndFrame());
        }

        // The window's QRhi exists after the first expose/render.
        (IntPtr dev, IntPtr rhi) Info(IntPtr w)
        {
            for (int i = 0; i < 60; i++)
            {
                if (Native.vlqt_test_device_info(w, out var dev, out var rhi) == 0)
                    return (dev, rhi);
                pump(1);
            }
            return (IntPtr.Zero, IntPtr.Zero);
        }

        var a = Open("VL.Qt shared device A", 50, 0);
        var b = Open("VL.Qt shared device B", 500, 0);
        var (devA, rhiA) = Info(a);
        var (devB, rhiB) = Info(b);
        check(devA != IntPtr.Zero && devB != IntPtr.Zero && rhiA != rhiB, $"358: two windows, two QRhis with a device ({devA:X}, {devB:X})");
        if (shared)
        {
            check(devA == devB, $"358: both windows on the same ID3D11Device ({devA:X} / {devB:X})");
            var urc = Native.vlqt_test_device_info(IntPtr.Zero, out var devU, out var rhiU);
            check(urc == 0 && rhiU != IntPtr.Zero && devU == devA, $"358: upload QRhi on the same device ({urc}, {devU:X})");
        }
        else
        {
            Console.WriteLine($"INFO VLQT_SHARED_DEVICE=0: devices {devA:X} / {devB:X}");
        }

        // Close and reopen one window twice: the shared device stays.
        for (int round = 1; round <= 2; round++)
        {
            Native.vlqt_window_destroy(b);
            pump(2);
            b = Open($"VL.Qt shared device B{round}", 500, 0);
            var (dev, rhi) = Info(b);
            check(dev != IntPtr.Zero && (!shared || dev == devA), $"358: reopen {round}: window on the shared device ({dev:X})");
        }

        // GPU timestamps per window (F5 overlay): both windows render every pump.
        for (int i = 0; i < 30; i++) { frame++; Animate(a); Animate(b); pump(1); }
        Native.vlqt_window_get_perf(a, out var pa);
        Native.vlqt_window_get_perf(b, out var pb);
        check(pa.gpu_ms > 0 && pb.gpu_ms > 0, $"358: GPU timestamps in both windows ({pa.gpu_ms:F3} / {pb.gpu_ms:F3} ms)");
        Native.vlqt_window_destroy(a);
        Native.vlqt_window_destroy(b);
        pump(2);

        // Frame intervals with two VSync windows, pumped at 60 Hz like the vvvv mainloop. With VSync 1 not every pump
        // renders, so the intervals between this window's renders are measured here (the stats count consecutive frames).
        var va = Open("VL.Qt shared device VSync A", 50, 1);
        var vb = Open("VL.Qt shared device VSync B", 500, 1);
        pump(30);
        var sw = Stopwatch.StartNew();
        var renders = new[] { new List<double>(), new List<double>() };
        int pumps = 0;
        while (sw.ElapsedMilliseconds < 5000)
        {
            frame++;
            Animate(va);
            Animate(vb);
            pump(1);
            pumps++;
            var t = sw.Elapsed.TotalMilliseconds;
            if (Native.vlqt_window_get_perf_sample(va, out var s0) == 0 && s0.swaps > 0) renders[0].Add(t);
            if (Native.vlqt_window_get_perf_sample(vb, out var s1) == 0 && s1.swaps > 0) renders[1].Add(t);
            var next = pumps * 1000.0 / 60;
            while (sw.Elapsed.TotalMilliseconds < next - 2) Thread.Sleep(1);
            while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
        }
        string Intervals(List<double> r)
        {
            var d = new List<double>();
            for (int i = 1; i < r.Count; i++) d.Add(r[i] - r[i - 1]);
            d.Sort();
            double P(double q) => d.Count == 0 ? 0 : d[Math.Min(d.Count - 1, (int)Math.Ceiling(q * d.Count) - 1)];
            return $"{r.Count / sw.Elapsed.TotalSeconds:F1} fps, interval p50/p95/p99/max {P(0.5):F1}/{P(0.95):F1}/{P(0.99):F1}/{P(1):F1} ms";
        }
        Console.WriteLine($"INFO 358 VSync 1, two windows, {(shared ? "shared device" : "device per window")}, {pumps / sw.Elapsed.TotalSeconds:F1} pumps/s: " +
            $"A {Intervals(renders[0])}; B {Intervals(renders[1])}");
        check(renders[0].Count > 0 && renders[1].Count > 0, "358: VSync windows render");
        // va/vb stay open: process end with open windows (teardown order, device never released).
    }
}
