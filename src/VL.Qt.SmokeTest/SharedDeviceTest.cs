using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VL.Qt;

/// <summary>
/// #358 (images package 01): all windows render on one D3D11 device created by QtNative; next to them an upload QRhi on
/// the same device. Two windows report the same ID3D11Device, closing and reopening one keeps it, GPU timestamps keep
/// coming per window, and the frame intervals of two and of three VSync windows are printed (compare with
/// VLQT_SHARED_DEVICE=0) and checked: each window at the refresh rate, the host pump within one refresh plus margin.
/// The three VSync windows stay open on purpose: the process must end cleanly with open windows.
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

        // #90: several VSync windows do not halve the frame rate. Pumped like the vvvv main loop: paced at the refresh rate,
        // and Win32 messages are dispatched between the pumps (Qt's VBlank thread per monitor posts the update requests
        // there; a pump that dispatches nothing renders a request only one pump later). With VSync 1 not every pump has to
        // render, so the intervals between this window's renders are measured here (the stats count consecutive frames).
        // The windows of the last round stay open: process end with open windows (teardown order, device never released).
        var vsyncWindows = new List<IntPtr>();
        foreach (var count in new[] { 2, 3 })
        {
            foreach (var w in vsyncWindows) Native.vlqt_window_destroy(w);
            vsyncWindows.Clear();
            pump(2);
            for (int i = 0; i < count; i++)
                vsyncWindows.Add(Open($"VL.Qt shared device VSync {count}/{(char)('A' + i)}", 50 + 450 * i, 1));
            pump(30);
            MeasureVSync(vsyncWindows, check, pump, shared, () => { frame++; foreach (var w in vsyncWindows) Animate(w); });
        }
    }

    // animate: moves the content of every window, so each pump has a change to render.
    static void MeasureVSync(List<IntPtr> windows, Action<bool, string> check, Action<int> pump, bool shared, Action animate)
    {
        var n = windows.Count;
        Native.vlqt_window_get_perf_stats(windows[0], out var st);
        var budget = st.budget_ms > 0 ? st.budget_ms : 1000.0 / 60;
        var renders = new List<double>[n];
        for (int i = 0; i < n; i++) renders[i] = new List<double>();
        var hostPump = new List<double>();
        using var timer = PumpTest.HighResTimer();
        var start = Stopwatch.GetTimestamp();
        var period = (long)(Stopwatch.Frequency * budget / 1000);
        var deadline = start;
        int pumps = 0;
        double seconds = 0;
        // 1 s warm-up (first presents), then 5 s measured.
        while ((seconds = Stopwatch.GetElapsedTime(start).TotalSeconds) < 6)
        {
            deadline += period;
            PumpTest.ForeignMessageLoopUntil(deadline);
            if (Stopwatch.GetTimestamp() - deadline > period) deadline = Stopwatch.GetTimestamp(); // no catch-up after a stall
            var h0 = Stopwatch.GetTimestamp();
            animate();
            pump(1);
            var hostMs = Stopwatch.GetElapsedTime(h0).TotalMilliseconds;
            var t = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (t < 1000) continue;
            pumps++;
            hostPump.Add(hostMs);
            for (int i = 0; i < n; i++)
                if (Native.vlqt_window_get_perf_sample(windows[i], out var s) == 0 && s.swaps > 0) renders[i].Add(t);
        }
        var measured = seconds - 1;
        double P(List<double> v, double q)
        {
            if (v.Count == 0) return 0;
            var d = new List<double>(v);
            d.Sort();
            return d[Math.Min(d.Count - 1, Math.Max(0, (int)Math.Ceiling(q * d.Count) - 1))];
        }
        string Intervals(List<double> r)
        {
            var d = new List<double>();
            for (int i = 1; i < r.Count; i++) d.Add(r[i] - r[i - 1]);
            return $"{r.Count / measured:F1} fps, interval p50/p95/p99/max {P(d, 0.5):F1}/{P(d, 0.95):F1}/{P(d, 0.99):F1}/{P(d, 1):F1} ms";
        }
        var refreshHz = 1000.0 / budget;
        var parts = new List<string>();
        for (int i = 0; i < n; i++) parts.Add($"{(char)('A' + i)} {Intervals(renders[i])}");
        Console.WriteLine($"INFO 358 VSync 1, {n} windows, {(shared ? "shared device" : "device per window")}, {pumps / measured:F1} pumps/s, " +
            $"refresh {refreshHz:F1} Hz, host pump p50/p99 {P(hostPump, 0.5):F2}/{P(hostPump, 0.99):F2} ms: {string.Join("; ", parts)}");
        var minFps = double.MaxValue;
        foreach (var r in renders) minFps = Math.Min(minFps, r.Count / measured);
        check(minFps >= 0.9 * refreshHz, $"90: {n} VSync windows each at >= 0.9 x refresh (min {minFps:F1} fps, refresh {refreshHz:F1} Hz)");
        var p99 = P(hostPump, 0.99);
        check(p99 <= 1.5 * budget, $"90: {n} VSync windows: host pump p99 <= 1.5 x budget ({p99:F2} ms, budget {budget:F2} ms)");
    }
}
