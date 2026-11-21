using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VL.Qt
{
    /// <summary>
    /// Diagnostics of the <see cref="QtApp"/> pump: cumulative counters and [vlqt.stats], the slow-frame warning and the
    /// rate-limited log of participant errors. Main (Qt) thread only.
    /// </summary>
    internal sealed class PumpDiagnostics
    {
        private const long StatsIntervalMs = 10_000;
        private const long WarningIntervalMs = 5_000;

        private long _lastWarningTicks, _lastErrorTicks;
        // Cumulative counters (since process start), logged as [vlqt.stats].
        private long _pumps, _advances, _processEvents, _registered, _unregistered, _participantErrors;
        private int _participantCount, _activePumps;
        // Stats interval.
        private long _statsStartTicks, _statsPumps;
        private double _statsPumpMsSum, _statsPumpMsMax;

        public PumpDiagnostics()
        {
            _statsStartTicks = Environment.TickCount64;
        }

        /// <summary>Participants over all live pumps (apps).</summary>
        public int ParticipantCount => _participantCount;
        /// <summary>Pumps (apps) currently subscribed to a frame clock.</summary>
        public int ActivePumps => _activePumps;
        public long Pumps => _pumps;
        public long Advances => _advances;
        public long ProcessEventsCount => _processEvents;

        public void OnParticipantsChanged(int delta)
        {
            _participantCount += delta;
            if (delta > 0)
                _registered += delta;
            else
                _unregistered -= delta;
        }

        public void OnPumpChanged(int delta) => _activePumps += delta;

        public void CountAdvance() => _advances++;
        public void CountProcessEvents() => _processEvents++;
        public void CountPump() => _pumps++;

        /// <summary>
        /// After a pump: the slow-frame warning and the [vlqt.stats] interval. <paramref name="meter"/> holds the pump start
        /// and the GC counters read there, <paramref name="host"/> the host values of this pump; the timestamps end the
        /// apply, advance and processEvents phases and the pump.
        /// </summary>
        public void AfterPump(IQtFrameParticipant[] participants, HostPumpMeter meter, in Native.vlqt_host_pump host, double budgetMs,
            long tApplied, long tAdvanced, long tProcessed, long tEnd)
        {
            var start = meter.Start;
            var ms = Stopwatch.GetElapsedTime(start, tEnd).TotalMilliseconds;
            var now = Environment.TickCount64;

            // Warn only when Qt eats more than half a vvvv frame; single small spikes are normal. The VSync wait (in
            // QRhi::beginFrame with D3D11) and Present are not Qt work: the sample is read only after the cheap checks, so a
            // normal frame costs nothing extra.
            double waitMs = 0;
            if (ms > budgetMs * 0.5 && now - _lastWarningTicks > WarningIntervalMs
                && ExceedsBudget(ms, waitMs = SumWaitMs(participants), budgetMs))
            {
                _lastWarningTicks = now;
                // Phase breakdown: which part of the pump, and did a .NET GC run meanwhile? Plus the Qt phases of each
                // window's last pump (is a spike in sync, render, present, gpu or events?).
                QtApp.Logger?.LogWarning("Qt frame took {Ms:F1} ms of {Budget:F1} ms (pump {Pump:F1} ms minus vsync wait and present {Wait:F1} ms), vvvv before it {Vvvv:F1} ms (apply {Apply:F1}, advance {Advance:F1}, processEvents {Process:F1}, after {After:F1}; GC gen0 +{Gc0}, gen1 +{Gc1}, gen2 +{Gc2}, pause {GcPause:F1} ms; alloc {AllocKb:F0} KB/frame){Phases}",
                    ms - Math.Min(waitMs, ms), budgetMs, ms, waitMs, host.vvvv,
                    Stopwatch.GetElapsedTime(start, tApplied).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tApplied, tAdvanced).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tAdvanced, tProcessed).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tProcessed, tEnd).TotalMilliseconds,
                    GC.CollectionCount(0) - meter.Gc0, GC.CollectionCount(1) - meter.Gc1, GC.CollectionCount(2) - meter.Gc2,
                    (GC.GetTotalPauseDuration() - meter.GcPause).TotalMilliseconds,
                    host.alloc_bytes / 1024.0,
                    FormatPhases(participants));
            }

            _statsPumps++;
            _statsPumpMsSum += ms;
            if (ms > _statsPumpMsMax)
                _statsPumpMsMax = ms;
            if (now - _statsStartTicks >= StatsIntervalMs)
                LogStats(now);
        }

        /// <summary>
        /// VSync wait plus Present time of the last pump summed over the windows of the participants (ms, 0 without samples).
        /// Both may wait for the display refresh or the GPU, so they do not count as Qt work. No allocation.
        /// </summary>
        internal static double SumWaitMs(IQtFrameParticipant[] participants)
        {
            double sum = 0;
            for (int i = 0; i < participants.Length; i++)
            {
                try
                {
                    if (participants[i].TryGetLastSample(out var s))
                        sum += s.vsync + s.present;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Diagnostics only: a failing sample counts as no waiting time.
                }
            }
            return sum;
        }

        /// <summary>True if the pump took more than half a frame budget of Qt work, i.e. without the VSync wait and the Present time.</summary>
        internal static bool ExceedsBudget(double pumpMs, double waitMs, double budgetMs) =>
            pumpMs - Math.Min(waitMs, pumpMs) > budgetMs * 0.5;

        /// <summary>
        /// " · Qt last pump: vsync wait … sync … render … present … gpu … events … ms (n swaps)" per window with a sample;
        /// windows are numbered when there are several. "" without samples. Allocates: warning path only.
        /// </summary>
        internal static string FormatPhases(IQtFrameParticipant[] participants)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < participants.Length; i++)
            {
                Native.vlqt_perf_sample s;
                try
                {
                    if (!participants[i].TryGetLastSample(out s))
                        continue;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Diagnostics only (slow-frame warning): show the failure instead of the sample.
                    sb.Append(" · Qt last pump");
                    if (participants.Length > 1)
                        sb.Append(" #").Append(i + 1);
                    sb.Append(": sample failed (").Append(e.Message).Append(')');
                    continue;
                }
                sb.Append(" · Qt last pump");
                if (participants.Length > 1)
                    sb.Append(" #").Append(i + 1);
                sb.Append(System.FormattableString.Invariant(
                    $": vsync wait {s.vsync:F1}, sync {s.sync:F1}, render {s.render:F1}, present {s.present:F1}, gpu {s.gpu:F1}, events {s.events:F1} ms ({s.swaps} swaps)"));
            }
            return sb.ToString();
        }

        private void LogStats(long now)
        {
            var seconds = (now - _statsStartTicks) / 1000.0;
            QtApp.Logger?.LogInformation(
                "[vlqt.stats] {Pumps} pumps in {Seconds:F1} s (pump avg {Avg:F2} ms, max {Max:F2} ms) · total: pumps {TotalPumps}, advances {Advances}, processEvents {ProcessEvents} · participants {Participants} (registered {Registered}, unregistered {Unregistered}, errors {Errors}) · pumps (apps) {ActivePumps}",
                _statsPumps, seconds, _statsPumps > 0 ? _statsPumpMsSum / _statsPumps : 0, _statsPumpMsMax,
                _pumps, _advances, _processEvents, _participantCount, _registered, _unregistered, _participantErrors, _activePumps);
            _statsStartTicks = now;
            _statsPumps = 0;
            _statsPumpMsSum = 0;
            _statsPumpMsMax = 0;
        }

        /// <summary>Counts the error and logs it, at most once every 5 s (further errors within 5 s are counted, not logged).</summary>
        public void LogParticipantError(Exception e, string phase)
        {
            _participantErrors++;
            var now = Environment.TickCount64;
            if (now - _lastErrorTicks <= WarningIntervalMs)
                return;
            _lastErrorTicks = now;
            QtApp.Logger?.LogError(e, "VL.Qt: participant failed in {Phase} (further errors within 5 s are counted, not logged)", phase);
        }
    }
}
