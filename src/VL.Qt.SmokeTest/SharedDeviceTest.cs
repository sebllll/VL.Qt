using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (images package 01): all windows render on one D3D11 device created by QtNative; next to them an upload QRhi on
/// the same device. Two windows report the same ID3D11Device, closing and reopening one keeps it, GPU timestamps keep
/// coming per window, and the frame intervals of two and of three VSync windows are printed (compare with
/// VLQT_SHARED_DEVICE=0) and checked: each window renders every refresh (median interval, hard check), each window at
/// the refresh rate and the host pump within one refresh plus margin (perf limits, measured again when exceeded).
/// The three VSync windows stay open on purpose: the process must end cleanly with open windows.
/// </summary>
[TestFixture, NonParallelizable]
public static class SharedDeviceTest
{
    private const uint Rect = 1;

    /// <summary>Windows share one D3D11 device; VSync cost with shared and separate devices.</summary>
    [Test, Category("shared-device")]
    public static void SharedDevice() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows() || s.QuickSoftware)
        {
            s.Skip("shared-device: D3D11 only (Linux keeps a device per window)");
            return;
        }
        var shared = Environment.GetEnvironmentVariable("VLQT_SHARED_DEVICE") != "0";
        var cmd = new CommandWriter();

        IntPtr Open(string title, float x, int swapInterval)
        {
            var w = Native.vlqt_window_create_at(x, 100, 400, 300, title, swapInterval);
            s.Check(w != IntPtr.Zero, $"create {title} " + (w == IntPtr.Zero ? Native.LastError() : ""));
            cmd.Reset();
            cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
            cmd.Create(Rect, Protocol.Type.Rectangle);
            cmd.SetPlace(Rect, 20, 20, 100, 60);
            cmd.SetColor(Rect, Protocol.Prop.Color, 1, 0.6f, 0, 1);
            cmd.Reparent(Rect, 0, 0);
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"apply {title}");
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
                if (NativeTest.vlqt_test_device_info(w, out var dev, out var rhi) == 0)
                    return (dev, rhi);
                s.Pump(1);
            }
            return (IntPtr.Zero, IntPtr.Zero);
        }

        var a = Open("VL.Qt shared device A", 50, 0);
        var b = Open("VL.Qt shared device B", 500, 0);
        var (devA, rhiA) = Info(a);
        var (devB, rhiB) = Info(b);
        s.Check(devA != IntPtr.Zero && devB != IntPtr.Zero && rhiA != rhiB, $"two windows, two QRhis with a device ({devA:X}, {devB:X})");
        if (shared)
        {
            s.Check(devA == devB, $"both windows on the same ID3D11Device ({devA:X} / {devB:X})");
            var urc = NativeTest.vlqt_test_device_info(IntPtr.Zero, out var devU, out var rhiU);
            s.Check(urc == 0 && rhiU != IntPtr.Zero && devU == devA, $"upload QRhi on the same device ({urc}, {devU:X})");
        }
        else
        {
            s.Info($"VLQT_SHARED_DEVICE=0: devices {devA:X} / {devB:X}");
        }

        // Close and reopen one window twice: the shared device stays.
        for (int round = 1; round <= 2; round++)
        {
            Native.vlqt_window_destroy(b);
            s.Pump(2);
            b = Open($"VL.Qt shared device B{round}", 500, 0);
            var (dev, rhi) = Info(b);
            s.Check(dev != IntPtr.Zero && (!shared || dev == devA), $"reopen {round}: window on the shared device ({dev:X})");
        }

        // GPU timestamps per window (F5 overlay): both windows render every pump.
        for (int i = 0; i < 30; i++) { frame++; Animate(a); Animate(b); s.Pump(1); }
        Native.vlqt_window_get_perf(a, out var pa);
        Native.vlqt_window_get_perf(b, out var pb);
        s.Check(pa.gpu_ms > 0 && pb.gpu_ms > 0, $"GPU timestamps in both windows ({pa.gpu_ms:F3} / {pb.gpu_ms:F3} ms)");
        Native.vlqt_window_destroy(a);
        Native.vlqt_window_destroy(b);
        s.Pump(2);

        // several VSync windows do not halve the frame rate. Pumped like the vvvv main loop: paced at the refresh rate,
        // and Win32 messages are dispatched between the pumps (Qt's VBlank thread per monitor posts the update requests
        // there; a pump that dispatches nothing renders a request only one pump later). With VSync 1 not every pump has to
        // render, so the intervals between this window's renders are measured here (the stats count consecutive frames).
        // The windows of the last round stay open: process end with open windows (teardown order, device never released).
        var vsyncWindows = new List<IntPtr>();
        foreach (var count in new[] { 2, 3 })
        {
            foreach (var w in vsyncWindows) Native.vlqt_window_destroy(w);
            vsyncWindows.Clear();
            s.Pump(2);
            for (int i = 0; i < count; i++)
                vsyncWindows.Add(Open($"VL.Qt shared device VSync {count}/{(char)('A' + i)}", 50 + 450 * i, 1));
            s.Pump(30);
            MeasureVSync(vsyncWindows, s, shared, () => { frame++; foreach (var w in vsyncWindows) Animate(w); });
        }
    }

    // Checks of one window count. The regression of #90 (each window at half the refresh rate) is systematic and doubles the
    // median render interval: hard check. fps and host pump p99 also drop when another process loads the GPU (measured on
    // an Adreno X1-45 next to a second SmokeTest: 49..52 fps, while the median interval stays at one refresh), so they are
    // perf limits (PerfLimits: factor, warn-only) and measured again, up to 3 rounds, only while a limit is exceeded.
    // animate: moves the content of every window, so each pump has a change to render.
    private static void MeasureVSync(List<IntPtr> windows, Scenario s, bool shared, Action animate)
    {
        var n = windows.Count;
        Native.vlqt_window_get_perf_stats(windows[0], out var st);
        var budget = st.budget_ms > 0 ? st.budget_ms : 1000.0 / 60;
        var refreshHz = 1000.0 / budget;
        var (minFps, p99, p50) = (0.0, double.MaxValue, double.MaxValue);
        for (int round = 0; round < 3; round++)
        {
            var r = MeasureVSyncRound(windows, s, shared, animate, budget);
            (minFps, p99, p50) = (Math.Max(minFps, r.MinFps), Math.Min(p99, r.HostP99), Math.Min(p50, r.MaxP50));
            if (1000.0 / minFps < budget / 0.9 * PerfLimits.Factor && p99 < 1.5 * budget * PerfLimits.Factor && p50 <= 1.5 * budget)
                break;
        }
        s.Check(p50 <= 1.5 * budget, $"{n} VSync windows each render every refresh (median interval {p50:F1} ms <= 1.5 x {budget:F2} ms)");
        PerfLimits.Below(s, 1000.0 / minFps, budget / 0.9,
            $"{n} VSync windows each at >= 0.9 x refresh (slowest window {1000.0 / minFps:F2} ms mean interval = {minFps:F1} fps, refresh {refreshHz:F1} Hz)");
        PerfLimits.Below(s, p99, 1.5 * budget, $"{n} VSync windows: host pump p99 < 1.5 x budget ({p99:F2} ms, budget {budget:F2} ms)");
    }

    // One measurement: 1 s warm-up, 5 s measured. Returns the fps of the slowest window, the host pump p99 and the largest
    // median render interval of the windows (ms).
    private static (double MinFps, double HostP99, double MaxP50) MeasureVSyncRound(List<IntPtr> windows, Scenario s, bool shared, Action animate, double budget)
    {
        var n = windows.Count;
        var renders = new List<double>[n];
        for (int i = 0; i < n; i++) renders[i] = new List<double>();
        var hostPump = new List<double>();
        using var timer = PumpTest.HighResTimer();
        var start = Stopwatch.GetTimestamp();
        var period = (long)(Stopwatch.Frequency * budget / 1000);
        var deadline = start;
        int pumps = 0;
        double seconds = 0;
        while ((seconds = Stopwatch.GetElapsedTime(start).TotalSeconds) < 6)
        {
            deadline += period;
            PumpTest.ForeignMessageLoopUntil(deadline);
            if (Stopwatch.GetTimestamp() - deadline > period) deadline = Stopwatch.GetTimestamp(); // no catch-up after a stall
            var h0 = Stopwatch.GetTimestamp();
            animate();
            s.Pump(1);
            var hostMs = Stopwatch.GetElapsedTime(h0).TotalMilliseconds;
            var t = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (t < 1000) continue;
            pumps++;
            hostPump.Add(hostMs);
            for (int i = 0; i < n; i++)
                if (Native.vlqt_window_get_perf_sample(windows[i], out var ps) == 0 && ps.swaps > 0) renders[i].Add(t);
        }
        var measured = seconds - 1;
        double P(List<double> v, double q)
        {
            if (v.Count == 0) return 0;
            var d = new List<double>(v);
            d.Sort();
            return d[Math.Min(d.Count - 1, Math.Max(0, (int)Math.Ceiling(q * d.Count) - 1))];
        }
        var parts = new List<string>();
        var minFps = double.MaxValue;
        var maxP50 = 0.0;
        for (int i = 0; i < n; i++)
        {
            var r = renders[i];
            var d = new List<double>();
            for (int k = 1; k < r.Count; k++) d.Add(r[k] - r[k - 1]);
            var fps = r.Count / measured;
            minFps = Math.Min(minFps, fps);
            maxP50 = Math.Max(maxP50, d.Count == 0 ? double.MaxValue : P(d, 0.5));
            parts.Add($"{(char)('A' + i)} {fps:F1} fps, interval p50/p95/p99/max {P(d, 0.5):F1}/{P(d, 0.95):F1}/{P(d, 0.99):F1}/{P(d, 1):F1} ms");
        }
        s.Info($"VSync 1, {n} windows, {(shared ? "shared device" : "device per window")}, {pumps / measured:F1} pumps/s, " +
            $"refresh {1000.0 / budget:F1} Hz, host pump p50/p99 {P(hostPump, 0.5):F2}/{P(hostPump, 0.99):F2} ms: {string.Join("; ", parts)}");
        return (minFps, P(hostPump, 0.99), maxP50);
    }
}
