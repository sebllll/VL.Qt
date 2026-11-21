using VL.Core.Import;

namespace VL.Qt
{
    /// <summary>
    /// Timings of a Qt window, averaged over the last ~1 s (max = worst pump in that range). All times in ms.
    /// Pump is the time Qt takes inside the vvvv frame (events, animation, vsync wait, sync, render, present). The vsync wait
    /// (VsyncMs, the start of the frame) and Present are shown separately: they wait for the display refresh or the GPU,
    /// which is not Qt work.
    /// GPU is 0 when the backend delivers no timestamps.
    /// </summary>
    public sealed record PerfStats(
        float Fps,
        float FrameMs, float FrameMsMax,
        float PumpMs, float PumpMsMax,
        float SyncMs, float RenderMs, float PresentMs,
        float GpuMs, float GpuMsMax,
        float SwapsPerPump)
    {
        public static readonly PerfStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        internal static PerfStats From(in Native.vlqt_perf p) => new(
            (float)p.fps,
            (float)p.frame_ms, (float)p.frame_ms_max,
            (float)p.pump_ms, (float)p.pump_ms_max,
            (float)p.sync_ms, (float)p.render_ms, (float)p.present_ms,
            (float)p.gpu_ms, (float)p.gpu_ms_max,
            (float)p.swaps_per_pump);

        // additive (init-only, 0 when unknown): percentiles/counters/budget of the window's ring and the host side
        // (apply/reconcile, commands, .NET GC) over the last ~1 s.

        /// <summary>Frame budget in ms: 1000 / refresh rate of the window's screen (fallback 60 Hz).</summary>
        public float BudgetMs { get; init; }
        /// <summary>Frame interval percentiles over the last up to 240 pumps (ms, nearest rank; only intervals between consecutive frames, not after idle pumps).</summary>
        public float FrameMsP50 { get; init; }
        public float FrameMsP95 { get; init; }
        public float FrameMsP99 { get; init; }
        /// <summary>Pump percentiles over the last up to 240 pumps (ms, nearest rank).</summary>
        public float PumpMsP50 { get; init; }
        public float PumpMsP95 { get; init; }
        public float PumpMsP99 { get; init; }

        /// <summary>Pumps since the window was created.</summary>
        public long Pumps { get; init; }
        /// <summary>Pumps longer than the budget, since start.</summary>
        public long OverBudget { get; init; }
        /// <summary>Frame intervals longer than 1.5 x budget between consecutive frames (missed frames; not after idle pumps of on-demand rendering), since start.</summary>
        public long LongFrames { get; init; }
        /// <summary>Pumps without a frame (nothing changed), since start.</summary>
        public long IdlePumps { get; init; }
        /// <summary>Pumps with more than one frame, since start.</summary>
        public long MultiSwaps { get; init; }
        /// <summary>The same counters over the last 10 s.</summary>
        public long RecentPumps { get; init; }
        public long RecentOverBudget { get; init; }
        public long RecentLongFrames { get; init; }
        public long RecentIdlePumps { get; init; }
        public long RecentMultiSwaps { get; init; }

        /// <summary>Applying this window's commands in the pump (ms, average/max over ~1 s).</summary>
        public float ApplyMs { get; init; }
        public float ApplyMsMax { get; init; }
        /// <summary>Reconcile and command writing in the node update (ms per frame, average/max over ~1 s).</summary>
        public float ReconcileMs { get; init; }
        public float ReconcileMsMax { get; init; }
        /// <summary>Commands applied per frame (average over ~1 s).</summary>
        public float Ops { get; init; }
        /// <summary>.NET garbage collections per second per generation (over ~1 s).</summary>
        public float Gen0PerSecond { get; init; }
        public float Gen1PerSecond { get; init; }
        public float Gen2PerSecond { get; init; }
        /// <summary>.NET GC pause in ms per second (over ~1 s).</summary>
        public float GcPauseMsPerSecond { get; init; }
        /// <summary>.NET bytes allocated per vvvv frame (process, average over ~1 s).</summary>
        public float AllocatedBytesPerFrame { get; init; }

        // additive: the vvvv side of the frame before the Qt pump.

        /// <summary>vvvv frame (patch update, from the frame clock's tick to the Qt pump) in ms, average/max over ~1 s.</summary>
        public float VvvvMs { get; init; }
        public float VvvvMsMax { get; init; }
        /// <summary>Waiting between the end of the previous Qt pump and the next vvvv frame start (frame timer, vsync) in ms, average/max over ~1 s.</summary>
        public float WaitMs { get; init; }
        public float WaitMsMax { get; init; }

        /// <summary>Start of the frame (QRhi::beginFrame) in ms per frame, average over ~1 s. With VSync this is where Direct3D 11 waits for the display refresh; without VSync it waits there when the GPU is behind. Not Qt work.</summary>
        public float VsyncMs { get; init; }

        // VL imports the primary-constructor parameters as the pins of `Split`, but not the init-only properties above.
        // These explicit operations read them in a patch (PerfStats (Split Percentiles) etc.); `Split` itself is unchanged.

        /// <summary>Frame budget and the frame/pump percentiles (ms) over the last up to 240 pumps.</summary>
        public void SplitPercentiles(
            out float budgetMs,
            out float frameMsP50, out float frameMsP95, out float frameMsP99,
            out float pumpMsP50, out float pumpMsP95, out float pumpMsP99)
        {
            budgetMs = BudgetMs;
            frameMsP50 = FrameMsP50; frameMsP95 = FrameMsP95; frameMsP99 = FrameMsP99;
            pumpMsP50 = PumpMsP50; pumpMsP95 = PumpMsP95; pumpMsP99 = PumpMsP99;
        }

        /// <summary>Pump counters since the window was created, and the same over the last 10 s.</summary>
        public void SplitCounters(
            out long pumps, out long overBudget, out long longFrames, out long idlePumps, out long multiSwaps,
            out long recentPumps, out long recentOverBudget, out long recentLongFrames, out long recentIdlePumps, out long recentMultiSwaps)
        {
            pumps = Pumps; overBudget = OverBudget; longFrames = LongFrames; idlePumps = IdlePumps; multiSwaps = MultiSwaps;
            recentPumps = RecentPumps; recentOverBudget = RecentOverBudget; recentLongFrames = RecentLongFrames;
            recentIdlePumps = RecentIdlePumps; recentMultiSwaps = RecentMultiSwaps;
        }

        /// <summary>The wait at the start of each frame (ms per frame, average over ~1 s): for the display refresh with VSync, for the GPU without.</summary>
        public void SplitVsync(out float vsyncMs)
        {
            vsyncMs = VsyncMs;
        }

        /// <summary>The host side of the frame (ms, average/max over ~1 s): vvvv patch update, wait for the next frame, applying commands and reconcile, and commands per frame.</summary>
        public void SplitHost(
            out float vvvvMs, out float vvvvMsMax, out float waitMs, out float waitMsMax,
            out float applyMs, out float applyMsMax, out float reconcileMs, out float reconcileMsMax, out float ops)
        {
            vvvvMs = VvvvMs; vvvvMsMax = VvvvMsMax; waitMs = WaitMs; waitMsMax = WaitMsMax;
            applyMs = ApplyMs; applyMsMax = ApplyMsMax; reconcileMs = ReconcileMs; reconcileMsMax = ReconcileMsMax; ops = Ops;
        }

        /// <summary>.NET garbage collector activity of the process over ~1 s: collections per second per generation, pause ms per second and allocated bytes per vvvv frame.</summary>
        public void SplitGc(
            [Pin(Name = "Gen0 Per Second")] out float gen0PerSecond, [Pin(Name = "Gen1 Per Second")] out float gen1PerSecond, [Pin(Name = "Gen2 Per Second")] out float gen2PerSecond,
            out float gcPauseMsPerSecond, out float allocatedBytesPerFrame)
        {
            gen0PerSecond = Gen0PerSecond; gen1PerSecond = Gen1PerSecond; gen2PerSecond = Gen2PerSecond;
            gcPauseMsPerSecond = GcPauseMsPerSecond; allocatedBytesPerFrame = AllocatedBytesPerFrame;
        }

        internal static PerfStats From(in Native.vlqt_perf p, in Native.vlqt_perf_stats s, in Native.vlqt_host_pump_stats h) => From(p) with
        {
            BudgetMs = (float)s.budget_ms,
            FrameMsP50 = (float)s.frame_p50, FrameMsP95 = (float)s.frame_p95, FrameMsP99 = (float)s.frame_p99,
            PumpMsP50 = (float)s.pump_p50, PumpMsP95 = (float)s.pump_p95, PumpMsP99 = (float)s.pump_p99,
            Pumps = s.pumps, OverBudget = s.over_budget, LongFrames = s.long_frames, IdlePumps = s.idle, MultiSwaps = s.multi_swap,
            RecentPumps = s.recent_pumps, RecentOverBudget = s.recent_over_budget, RecentLongFrames = s.recent_long_frames,
            RecentIdlePumps = s.recent_idle, RecentMultiSwaps = s.recent_multi_swap,
            ApplyMs = (float)h.apply_ms, ApplyMsMax = (float)h.apply_ms_max,
            ReconcileMs = (float)h.reconcile_ms, ReconcileMsMax = (float)h.reconcile_ms_max,
            Ops = (float)h.ops,
            Gen0PerSecond = (float)h.gc0_per_s, Gen1PerSecond = (float)h.gc1_per_s, Gen2PerSecond = (float)h.gc2_per_s,
            GcPauseMsPerSecond = (float)h.gc_pause_ms_per_s,
            AllocatedBytesPerFrame = (float)h.alloc_bytes,
            VvvvMs = (float)h.vvvv_ms, VvvvMsMax = (float)h.vvvv_ms_max,
            WaitMs = (float)h.wait_ms, WaitMsMax = (float)h.wait_ms_max,
            VsyncMs = (float)p.vsync_ms,
        };
    }
}
