using System;
using System.Diagnostics;

namespace VL.Qt
{
    /// <summary>
    /// Host-pump values of one vvvv app (one per <see cref="QtPump"/>): interval and GC/alloc are per app frame, not since
    /// another app's pump. <see cref="Begin"/> at the pump start, <see cref="End"/> at its end.
    /// </summary>
    internal sealed class HostPumpMeter
    {
        // Values of the previous pump of this app, 0 before the first one.
        private long _prevStart, _prevAllocBytes;
        private int _prevGc0, _prevGc1, _prevGc2;
        private TimeSpan _prevGcPause;

        /// <summary>Stopwatch timestamp of the current pump start.</summary>
        public long Start { get; private set; }

        /// <summary>GC collections per generation at the current pump start.</summary>
        public int Gc0 { get; private set; }
        public int Gc1 { get; private set; }
        public int Gc2 { get; private set; }

        /// <summary>Total GC pause at the current pump start.</summary>
        public TimeSpan GcPause { get; private set; }

        /// <summary>Bytes allocated by the process at the current pump start.</summary>
        public long AllocBytes { get; private set; }

        /// <summary>Reads the start timestamp and the GC and alloc counters.</summary>
        public void Begin()
        {
            Start = Stopwatch.GetTimestamp();
            Gc0 = GC.CollectionCount(0);
            Gc1 = GC.CollectionCount(1);
            Gc2 = GC.CollectionCount(2);
            GcPause = GC.GetTotalPauseDuration();
            AllocBytes = GC.GetTotalAllocatedBytes(false);
        }

        /// <summary>
        /// Returns the host values of the pump that ends at <paramref name="end"/>: pump time, interval since the previous
        /// pump of this app, vvvv time (<paramref name="vvvvStart"/> → pump start), wait (<paramref name="prevPumpEnd"/> of
        /// any app → vvvv start) and GC/alloc since the previous pump start (= one vvvv frame). Apply, ops and reconcile
        /// are left to the caller.
        /// </summary>
        public Native.vlqt_host_pump End(long vvvvStart, long prevPumpEnd, long end)
        {
            var start = Start;
            var host = new Native.vlqt_host_pump
            {
                host = (float)Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                interval = _prevStart != 0 ? (float)Stopwatch.GetElapsedTime(_prevStart, start).TotalMilliseconds : 0,
                // vvvv = frame start → this pump; wait = previous pump end (any app) → frame start. Interleaved apps
                // (frame start before the previous pump ended) give wait 0.
                vvvv = vvvvStart != 0 && vvvvStart <= start ? (float)Stopwatch.GetElapsedTime(vvvvStart, start).TotalMilliseconds : 0,
                wait = vvvvStart != 0 && prevPumpEnd != 0 && vvvvStart > prevPumpEnd
                    ? (float)Stopwatch.GetElapsedTime(prevPumpEnd, vvvvStart).TotalMilliseconds : 0,
            };
            if (_prevStart != 0)
            {
                host.gc0 = Gc0 - _prevGc0;
                host.gc1 = Gc1 - _prevGc1;
                host.gc2 = Gc2 - _prevGc2;
                host.gc_pause = (float)(GcPause - _prevGcPause).TotalMilliseconds;
                host.alloc_bytes = AllocBytes - _prevAllocBytes;
            }
            _prevStart = start;
            _prevGc0 = Gc0; _prevGc1 = Gc1; _prevGc2 = Gc2;
            _prevGcPause = GcPause;
            _prevAllocBytes = AllocBytes;
            return host;
        }
    }
}
