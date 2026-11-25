using VL.Qt.Interop;

namespace VL.Qt
{
    /// <summary>
    /// Timings of a Qt window, averaged over the last ~1 s (max = worst pump in that range). All times in ms.
    /// Frame statistics (Fps, FrameMs, FrameMsMax) come only from consecutive frames, like the percentiles: the interval
    /// after idle pumps (a static scene, an animation that stopped) spans the idle time and is not a frame time. Without
    /// a consecutive frame in the range (an idle scene) they are 0, so Fps is 0, not a low number. The phases (SyncMs,
    /// RenderMs, PresentMs, VsyncMs) are averages per pump that rendered, not diluted by idle pumps; PumpMs, WorkMs and
    /// SwapsPerPump are per pump of the range.
    /// A pump is one call of Qt's event processing in the vvvv frame: the time Qt takes inside it is events, animation, vsync
    /// wait, sync, render and present. The vsync wait (VsyncMs, the start of the frame) and Present wait for the display
    /// refresh or the GPU, which is not Qt work: Qt work (WorkMs) is the pump without them, and only Qt work counts against
    /// the budget (<see cref="PerfCounters.OverBudget"/>). GPU is 0 when the backend delivers no timestamps. The groups
    /// hold the percentiles, the pump counters, and the host and .NET GC values.
    /// </summary>
    /// <param name="Fps">Frames per second over the last ~1 s, 0 when there was no consecutive frame.</param>
    /// <param name="FrameMs">Interval between consecutive frames (ms, average).</param>
    /// <param name="FrameMsMax">Longest interval between consecutive frames (ms).</param>
    /// <param name="PumpMs">Time Qt takes inside the vvvv frame, including the vsync wait and Present (ms per pump, average).</param>
    /// <param name="PumpMsMax">Longest pump (ms).</param>
    /// <param name="WorkMs">Qt work in ms per pump, average: events, sync and render, i.e. the pump without the vsync wait and Present. The over budget counter counts the pumps where it exceeds the budget.</param>
    /// <param name="SyncMs">Synchronizing the scene with the render thread (ms per pump that rendered, average).</param>
    /// <param name="RenderMs">Recording the frame (ms per pump that rendered, average).</param>
    /// <param name="PresentMs">Present of the swap chain (ms per pump that rendered, average).</param>
    /// <param name="VsyncMs">Start of the frame (QRhi::beginFrame) in ms per frame, average. With VSync this is where Direct3D 11 waits for the display refresh; without VSync it waits there when the GPU is behind. Not Qt work.</param>
    /// <param name="GpuMs">GPU time of the frame (ms, average), 0 when the backend delivers no timestamps.</param>
    /// <param name="GpuMsMax">Longest GPU time of a frame (ms).</param>
    /// <param name="SwapsPerPump">Swaps (presented frames) per pump, average.</param>
    /// <param name="Percentiles">Budget and the frame and pump percentiles over the last up to 240 pumps.</param>
    /// <param name="Counters">Pump counters since the window was created.</param>
    /// <param name="Recent">The same counters over the last 10 s.</param>
    /// <param name="Host">The host side of the frame: vvvv, wait, apply, reconcile and commands.</param>
    /// <param name="Gc">.NET garbage collector activity of the process.</param>
    public readonly record struct PerfStats(
        float Fps,
        float FrameMs, float FrameMsMax,
        float PumpMs, float PumpMsMax,
        float WorkMs,
        float SyncMs, float RenderMs, float PresentMs,
        float VsyncMs,
        float GpuMs, float GpuMsMax,
        float SwapsPerPump,
        PerfPercentiles Percentiles,
        PerfCounters Counters,
        PerfCounters Recent,
        HostPerfStats Host,
        GcPerfStats Gc)
    {
        // One constructor call, no allocation: the groups are structs.
        internal static PerfStats From(in Native.vlqt_perf p, in Native.vlqt_perf_stats s, in Native.vlqt_host_pump_stats h) => new(
            (float)p.fps,
            (float)p.frame_ms, (float)p.frame_ms_max,
            (float)p.pump_ms, (float)p.pump_ms_max,
            (float)p.work_ms,
            (float)p.sync_ms, (float)p.render_ms, (float)p.present_ms,
            (float)p.vsync_ms,
            (float)p.gpu_ms, (float)p.gpu_ms_max,
            (float)p.swaps_per_pump,
            new PerfPercentiles(
                (float)s.budget_ms,
                (float)s.frame_p50, (float)s.frame_p95, (float)s.frame_p99,
                (float)s.pump_p50, (float)s.pump_p95, (float)s.pump_p99),
            new PerfCounters(s.pumps, s.over_budget, s.long_frames, s.idle, s.multi_swap),
            new PerfCounters(s.recent_pumps, s.recent_over_budget, s.recent_long_frames, s.recent_idle, s.recent_multi_swap),
            new HostPerfStats(
                (float)h.vvvv_ms, (float)h.vvvv_ms_max, (float)h.wait_ms, (float)h.wait_ms_max,
                (float)h.apply_ms, (float)h.apply_ms_max, (float)h.reconcile_ms, (float)h.reconcile_ms_max, (float)h.ops),
            new GcPerfStats(
                (float)h.gc0_per_s, (float)h.gc1_per_s, (float)h.gc2_per_s, (float)h.gc_pause_ms_per_s, (float)h.alloc_bytes));
    }
}
