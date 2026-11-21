using System;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf
        {
            public double fps, frame_ms, frame_ms_max, pump_ms, pump_ms_max;
            public double sync_ms, render_ms, present_ms, gpu_ms, gpu_ms_max, swaps_per_pump;
            public int samples;
            public double vsync_ms;
        }

        /// <summary>One pump of a window (vlqt.h vlqt_perf_sample), ms.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf_sample
        {
            public float frame_interval, pump, sync, render, present, gpu, events;
            public int swaps;
            public float vsync;
        }

        /// <summary>Percentiles over the ring, counters since start and over the last 10 s, budget (vlqt.h vlqt_perf_stats).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf_stats
        {
            public double budget_ms;
            public double frame_p50, frame_p95, frame_p99, frame_max;
            public double pump_p50, pump_p95, pump_p99, pump_max;
            public int samples, frames;
            public long pumps, over_budget, long_frames, idle, multi_swap;
            public long recent_pumps, recent_over_budget, recent_long_frames, recent_idle, recent_multi_swap;
        }

        /// <summary>What the host measured around one pump of a scene (vlqt.h vlqt_host_pump), ms.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_host_pump
        {
            public float apply, reconcile, host, interval, gc_pause;
            public int ops, gc0, gc1, gc2;
            public long alloc_bytes;
            /// <summary>vvvv frame start → pump start; end of previous pump → vvvv frame start (0 = unknown).</summary>
            public float vvvv, wait;
        }

        /// <summary>Host pumps over the last ~1 s: averages, maxima, rates per second (vlqt.h vlqt_host_pump_stats).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_host_pump_stats
        {
            public double apply_ms, apply_ms_max, reconcile_ms, reconcile_ms_max, host_ms, host_ms_max;
            public double ops, ops_max, gc0_per_s, gc1_per_s, gc2_per_s, gc_pause_ms_per_s;
            public double alloc_bytes, alloc_bytes_max;
            public int samples;
            public double vvvv_ms, vvvv_ms_max, wait_ms, wait_ms_max;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_perf(SceneHandle w, out vlqt_perf perf);

        /// <summary>Last completed pump; <see cref="Result.NoData"/> = no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_perf_sample(SceneHandle w, out vlqt_perf_sample sample);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_perf_stats(SceneHandle w, out vlqt_perf_stats stats);

        /// <summary>host values of the last completed pump; <see cref="Result.NoData"/> = no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_report_host_pump(SceneHandle w, in vlqt_host_pump frame);

        /// <summary>Last reported host frame; <see cref="Result.NoData"/> = none for the last pump.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_host_pump(SceneHandle w, out vlqt_host_pump frame);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_host_pump_stats(SceneHandle w, out vlqt_host_pump_stats stats);
    }
}
