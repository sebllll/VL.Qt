using System;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// An audio tap as protocol.h "Audio tap" describes it, without Qt: a producer emits buffers of 1115 frames in real
    /// time on the tap clock (emit time jittered, due = emit time + k / (sample rate * rate)), segments with a
    /// discontinuity counter, the reader drops frames of older segments (flush). The samples encode their source frame
    /// index (channel 0 = index * <see cref="Scale"/>), so a test can tell which frame left the speaker when.
    /// Allocation free after construction, like the real reader.
    /// </summary>
    internal sealed class SimulatedTap : IAudioTapReader
    {
        public const ulong Id = 7;
        public const float Scale = 1e-5f;
        private const int bufferFrames = 1115;
        private const int videoPeriodUs = 33_333;

        private struct Chunk
        {
            public long EmitNs;
            public long FirstIndex;
            public int Frames;
            public int Read;
            public uint Discontinuity;
            public double Rate;
            public long MediaUs;
        }

        // A piece of the true schedule: frame i of it is due at StartNs + (i - FirstIndex) / (rate * sample rate).
        private struct Schedule
        {
            public long FirstIndex;
            public double StartNs;
            public double Rate;
            public long MediaUs;
            public uint Discontinuity;
        }

        private readonly Chunk[] _chunks = new Chunk[512];
        private readonly Schedule[] _schedules = new Schedule[64];
        private readonly Random _random;
        private readonly double _emitJitterNs;
        private int _head, _count, _scheduleCount;
        private long _nextIndex = 1000;
        private int _chunkInSchedule;
        private double _nextJitterNs;
        private bool _producing = true;

        public SimulatedTap(int sampleRate, double emitJitterNs, int seed, long startNs)
        {
            SampleRate = sampleRate;
            _emitJitterNs = emitJitterNs;
            _random = new Random(seed);
            NowNsValue = startNs;
            StartSegment(1.0, 0, startNs, newDiscontinuity: false);
        }

        public int SampleRate { get; }

        public uint Discontinuity { get; private set; }

        /// <summary>The tap clock as the reader sees it.</summary>
        public long NowNsValue { get; set; }

        /// <summary>Length of one loop pass in us on the audio axis; 0 = no loop. The video axis restarts per pass.</summary>
        public long LoopUs { get; set; }

        public double Rate => _schedules[_scheduleCount - 1].Rate;

        /// <summary>No buffer is emitted before this time (ns): Qt's burst after a re-attach, all buffers at once.</summary>
        public long EmitNotBefore { get; set; }

        /// <summary>Frames copying reads asked for but did not get (QtNative's underrun_frames).</summary>
        public long UnderrunFrames { get; private set; }

        public long NowNs() => NowNsValue;

        /// <summary>New segment (rate change, source change, play after pause): discontinuity + 1, due from startNs on.</summary>
        public void StartSegment(double rate, long mediaUs, double startNs, bool newDiscontinuity = true)
        {
            if (newDiscontinuity)
                Discontinuity++;
            _schedules[_scheduleCount++] = new Schedule { FirstIndex = _nextIndex, StartNs = startNs, Rate = rate, MediaUs = mediaUs, Discontinuity = Discontinuity };
            _chunkInSchedule = 0;
            _nextJitterNs = Jitter();
            _producing = true;
        }

        /// <summary>The producer delivers its next buffers gapNs late, same segment (decoder stall: an underrun at the reader).</summary>
        public void Stall(double gapNs)
        {
            var s = _schedules[_scheduleCount - 1];
            var index = s.FirstIndex + (long)Math.Round(_chunkInSchedule * (double)bufferFrames);
            _schedules[_scheduleCount++] = new Schedule
            {
                FirstIndex = index,
                StartNs = s.StartNs + (index - s.FirstIndex) * StepNs(s.Rate) + gapNs,
                Rate = s.Rate,
                MediaUs = s.MediaUs + (long)((index - s.FirstIndex) * 1e6 / SampleRate),
                Discontinuity = s.Discontinuity,
            };
            _chunkInSchedule = 0;
        }

        public void Pause() => _producing = false;

        private double StepNs(double rate) => 1e9 / (SampleRate * rate);

        private double Jitter() => (_random.NextDouble() * 2 - 1) * _emitJitterNs;

        /// <summary>Emits every buffer whose (jittered) emit time is not after nowNs.</summary>
        public void Advance(long nowNs)
        {
            NowNsValue = nowNs;
            while (_producing)
            {
                ref readonly var s = ref _schedules[_scheduleCount - 1];
                var offset = _chunkInSchedule * bufferFrames;
                var trueEmit = s.StartNs + offset * StepNs(s.Rate);
                var emit = (long)(trueEmit + _nextJitterNs);
                if (emit > nowNs || EmitNotBefore > nowNs)
                    return;
                emit = Math.Max(emit, EmitNotBefore);
                if (_count < _chunks.Length)
                {
                    _chunks[(_head + _count) % _chunks.Length] = new Chunk
                    {
                        EmitNs = emit,
                        FirstIndex = s.FirstIndex + offset,
                        Frames = bufferFrames,
                        Discontinuity = s.Discontinuity,
                        Rate = s.Rate,
                        MediaUs = s.MediaUs + (long)(offset * 1e6 / SampleRate),
                    };
                    _count++;
                }
                _nextIndex = s.FirstIndex + offset + bufferFrames;
                _chunkInSchedule++;
                _nextJitterNs = Jitter();
            }
        }

        /// <summary>When source frame <paramref name="index"/> is truly due (the picture of its media time is shown).</summary>
        public double TrueDueNs(double index)
        {
            for (var k = _scheduleCount - 1; k >= 0; k--)
            {
                ref readonly var s = ref _schedules[k];
                if (index >= s.FirstIndex)
                    return s.StartNs + (index - s.FirstIndex) * StepNs(s.Rate);
            }
            return double.NaN;
        }

        /// <summary>Discontinuity counter of source frame <paramref name="index"/>.</summary>
        public uint SegmentOf(double index)
        {
            for (var k = _scheduleCount - 1; k >= 0; k--)
            {
                if (index >= _schedules[k].FirstIndex)
                    return _schedules[k].Discontinuity;
            }
            return 0;
        }

        public int Read(ulong tapId, Span<float> dst, int channels, out Native.vlqt_audio_tap_state state)
            => Move(tapId, dst, dst.Length / Math.Max(1, channels), channels, out state);

        public int Drop(ulong tapId, int maxFrames, int channels, out Native.vlqt_audio_tap_state state)
            => Move(tapId, Span<float>.Empty, maxFrames, channels, out state, drop: true);

        private int Move(ulong tapId, Span<float> dst, int maxFrames, int channels, out Native.vlqt_audio_tap_state state, bool drop = false)
        {
            state = default;
            if (tapId != Id)
                return -9;
            if (channels != 2 || maxFrames < 0)
                return -7;
            FlushOldSegments();
            state.now_ns = NowNsValue;
            state.channels = 2;
            state.sample_rate = SampleRate;
            state.capacity = SampleRate / 2;
            state.flags = Protocol.AudioTap.Attached | (_producing ? Protocol.AudioTap.Playing : 0);
            state.discontinuity = Discontinuity;
            FillVideo(ref state);
            if (_count == 0)
                return 0;
            ref var first = ref _chunks[_head];
            state.discontinuity = first.Discontinuity;
            state.rate = first.Rate;
            state.due_ns = first.EmitNs + (long)(first.Read * StepNs(first.Rate));
            state.media_us = first.MediaUs + (long)(first.Read * 1e6 / SampleRate);
            var segment = first.Discontinuity;
            var done = 0;
            while (done < maxFrames && _count > 0 && _chunks[_head].Discontinuity == segment)
            {
                ref var c = ref _chunks[_head];
                var n = Math.Min(maxFrames - done, c.Frames - c.Read);
                if (!drop)
                {
                    for (var k = 0; k < n; k++)
                    {
                        var value = (c.FirstIndex + c.Read + k) * Scale;
                        dst[(done + k) * 2] = value;
                        dst[(done + k) * 2 + 1] = -value;
                    }
                }
                c.Read += n;
                done += n;
                if (c.Read == c.Frames)
                {
                    _head = (_head + 1) % _chunks.Length;
                    _count--;
                }
            }
            if (!drop)
                UnderrunFrames += maxFrames - done;
            for (var k = 0; k < _count; k++)
            {
                ref readonly var c = ref _chunks[(_head + k) % _chunks.Length];
                if (c.Discontinuity != segment)
                    break;
                state.available += c.Frames - c.Read;
            }
            return done;
        }

        // The reader flushes: frames of a segment older than the newest buffered one are dropped.
        private void FlushOldSegments()
        {
            if (_count == 0)
                return;
            var newest = _chunks[(_head + _count - 1) % _chunks.Length].Discontinuity;
            while (_count > 0 && _chunks[_head].Discontinuity != newest)
            {
                _head = (_head + 1) % _chunks.Length;
                _count--;
            }
        }

        // The last video frame at the sink: frames every 33.3 ms of media time from the start of each loop pass, shown when
        // their media time is due.
        private void FillVideo(ref Native.vlqt_audio_tap_state state)
        {
            ref readonly var s = ref _schedules[_scheduleCount - 1];
            var mediaNowUs = s.MediaUs + (NowNsValue - s.StartNs) * s.Rate / 1e3;
            if (mediaNowUs < s.MediaUs)
                return;
            var passStartUs = LoopUs > 0 ? Math.Floor(mediaNowUs / LoopUs) * LoopUs : 0;
            var frameUs = passStartUs + Math.Floor((mediaNowUs - passStartUs) / videoPeriodUs) * videoPeriodUs;
            if (frameUs < s.MediaUs)
                return;
            state.video_media_us = (long)(frameUs - passStartUs);
            state.video_ns = (long)(s.StartNs + (frameUs - s.MediaUs) * 1e3 / s.Rate);
        }
    }
}
