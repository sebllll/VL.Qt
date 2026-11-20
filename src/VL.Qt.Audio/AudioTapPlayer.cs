using System;
using System.Threading;

namespace VL.Qt.Audio
{
    /// <summary>
    /// The sync loop of Video (VL.Audio) (protocol.h "Audio tap"), free of VL.Audio so that tests can drive it with
    /// simulated clocks. Pulls frames from an audio tap and resamples them so that a frame due at d (the moment Qt shows the
    /// matching picture) leaves the speaker at d + <see cref="TargetDelayNs"/>.
    /// <para>
    /// Control loop, once per <see cref="Fill"/>: error = (now + output latency) - due of the next input frame - target
    /// delay. The target delay is Audio Offset, but at least what is reachable (output latency + one buffer + margin): Qt
    /// emits a buffer when its first frame is due, so the sound cannot leave earlier. A large error (start, discontinuity,
    /// underrun, lost lock) is removed at once by dropping input frames or inserting silence; then the loop is locked and a
    /// PI controller steers the resampling ratio: proportional part within +-0.5 %, integral part (clock drift between tap
    /// and audio device) within +-1.2 %. Rate != 1 is tape-style: the tap delivers media content, consumed at Rate input
    /// frames per output frame (the pitch follows the rate).
    /// </para>
    /// Threads: <see cref="Fill"/> on the audio thread, allocation free; the settings are volatile fields written by the
    /// main thread.
    /// </summary>
    internal sealed class AudioTapPlayer
    {
        // Proportional gain (1/s) and integral gain (1/s^2): critically damped, time constant about 1 s.
        private const double kp = 2.0;
        private const double ki = 1.0;
        private const double maxTrim = 0.005;
        private const double maxDrift = 0.012;
        // Low-pass of the measured error against the jitter of Qt's emit times and of the audio callback.
        private const double errorFilterSeconds = 0.1;
        private const double syncFilterSeconds = 0.25;
        // A filtered error beyond this is not slewed but removed at once (drop or silence).
        private const double lockLossNs = 25e6;
        // Headroom of the reachable delay for the jitter of Qt's emit times (+-1.5 ms in the spike) and of the callback.
        private const double marginNs = 5e6;
        // Input scratch in frames; the loop refills it as often as one output buffer needs.
        private const int capacityFrames = 8192;

        private readonly IAudioTapReader _reader;
        private readonly int _channels;
        private readonly float[] _in;

        // Settings (main thread).
        private ulong _tapId;
        private float _volume = 1f;
        private float _audioOffsetMs;
        private long _outputLatencyNs;
        private float _syncErrorMs = float.NaN;

        // Audio thread state.
        private ulong _tap;
        private int _inFrames;
        private double _pos;
        private bool _hasSegment;
        private uint _segment;
        private int _sampleRate;
        private double _rate = 1;
        private double _stepNs;
        private int _anchorIndex;
        private double _anchorDueNs;
        private double _anchorMediaUs;
        private long _lastMediaUs;
        private bool _locked;
        // Frames of the next segment in the ring as the last state said; reads ask for no more (a read beyond it counts
        // as underrun_frames in QtNative).
        private int _available;
        private double _filteredNs;
        private double _drift;
        private double _targetDelayNs;
        private float _lastVolume = float.NaN;
        // Sync Error from the video sink: Qt's video axis restarts at a loop wrap, the audio axis does not.
        private long _videoNs;
        private long _videoMediaUs = -1;
        private long _loopOffsetUs;
        private double _syncFilteredNs = double.NaN;

        public AudioTapPlayer(IAudioTapReader reader, int channels)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            if (channels < 1)
                throw new ArgumentOutOfRangeException(nameof(channels));
            _channels = channels;
            _in = new float[capacityFrames * channels];
        }

        /// <summary>Tap to read, 0 = none (silence). A new id flushes and re-locks.</summary>
        public ulong TapId
        {
            get => Volatile.Read(ref _tapId);
            set => Volatile.Write(ref _tapId, value);
        }

        /// <summary>Linear gain, ramped over one buffer on a change.</summary>
        public float Volume
        {
            get => Volatile.Read(ref _volume);
            set => Volatile.Write(ref _volume, value);
        }

        /// <summary>Wanted delay of the sound against the picture in ms (Audio Offset pin).</summary>
        public float AudioOffsetMs
        {
            get => Volatile.Read(ref _audioOffsetMs);
            set => Volatile.Write(ref _audioOffsetMs, value);
        }

        /// <summary>Time from a <see cref="Fill"/> call until its first frame leaves the speaker, in ns (buffer + driver).</summary>
        public long OutputLatencyNs
        {
            get => Volatile.Read(ref _outputLatencyNs);
            set => Volatile.Write(ref _outputLatencyNs, value);
        }

        /// <summary>Filtered offset of the sound against the picture in ms (positive: late); NaN while not locked.</summary>
        public float SyncErrorMs
        {
            get => Volatile.Read(ref _syncErrorMs);
            private set => Volatile.Write(ref _syncErrorMs, value);
        }

        /// <summary>Audio thread: true while the loop is locked.</summary>
        internal bool Locked => _locked;

        /// <summary>Audio thread: the delay of the last <see cref="Fill"/> (Audio Offset or the reachable minimum), in ns.</summary>
        internal double TargetDelayNs => _targetDelayNs;

        /// <summary>Audio thread: the filtered control error in ns.</summary>
        internal double FilteredErrorNs => _filteredNs;

        /// <summary>Audio thread: fills buffers[channel][offset .. offset + count) at the given output sample rate.</summary>
        public void Fill(float[][] buffers, int offset, int count, int sampleRate)
        {
            var tap = TapId;
            if (tap != _tap)
            {
                _tap = tap;
                Flush();
                ResetVideo();
            }
            var written = 0;
            if (tap != 0 && count > 0 && Prepare(tap, sampleRate))
                written = Render(buffers, offset, count, sampleRate, tap);
            else
                Flush();
            for (var c = 0; c < buffers.Length; c++)
                Array.Clear(buffers[c], offset + written, count - written);
            if (!_locked)
            {
                _syncFilteredNs = double.NaN;
                SyncErrorMs = float.NaN;
            }
        }

        // State-only read: is the tap attached in our format? Also takes the video sink diagnostics.
        private bool Prepare(ulong tap, int sampleRate)
        {
            var rc = _reader.Read(tap, Span<float>.Empty, _channels, out var state);
            if (rc < 0 || (state.flags & Protocol.AudioTap.Attached) == 0 || state.sample_rate != sampleRate || state.channels != _channels)
                return false;
            if (_sampleRate != sampleRate)
            {
                Flush();
                _sampleRate = sampleRate;
            }
            _available = state.available;
            TrackVideo(state.video_media_us, state.video_ns);
            return true;
        }

        private int Render(float[][] buffers, int offset, int count, int sampleRate, ulong tap)
        {
            var nsPerOut = 1e9 / sampleRate;
            var latency = (double)OutputLatencyNs;
            var target = Math.Max(AudioOffsetMs * 1e6, latency + count * nsPerOut + marginNs);
            _targetDelayNs = target;
            var leave0 = _reader.NowNs() + latency;

            var ratio = _rate;
            if (_locked)
            {
                var raw = leave0 - DueAt(_pos) - target;
                _filteredNs += Math.Min(1.0, count / (sampleRate * errorFilterSeconds)) * (raw - _filteredNs);
                if (Math.Abs(_filteredNs) > lockLossNs)
                    _locked = false;
                else
                {
                    var error = _filteredNs * 1e-9;
                    _drift = Math.Clamp(_drift + ki * error * count / sampleRate, -maxDrift, maxDrift);
                    ratio = _rate * (1 + Math.Clamp(kp * error, -maxTrim, maxTrim) + _drift);
                    UpdateSyncError(leave0, count, sampleRate);
                }
            }

            var volume = Volume;
            var fromVolume = float.IsNaN(_lastVolume) ? volume : _lastVolume;
            _lastVolume = volume;
            var channels = Math.Min(buffers.Length, _channels);
            var j = 0;
            while (j < count)
            {
                var i = (int)_pos;
                if (i + 2 >= _inFrames)
                {
                    var segment = _segment;
                    if (!Refill(tap, sampleRate, count - j, ratio))
                    {
                        // Underrun: silence for the rest, never stretch. The frames kept for interpolation are due before
                        // the gap: dropped; the re-lock places the next frames by their own due time.
                        _locked = false;
                        _inFrames = 0;
                        _pos = 0;
                        _anchorIndex = 0;
                        break;
                    }
                    if (!_locked || segment != _segment)
                        ratio = _rate;
                    continue;
                }
                if (!_locked)
                {
                    j = Resync(tap, buffers, offset, j, count, leave0 + j * nsPerOut, target, nsPerOut);
                    if (_locked)
                        ratio = _rate * (1 + _drift);
                    continue;
                }
                var t = (float)(_pos - i);
                var gain = fromVolume + (volume - fromVolume) * (j + 1) / count;
                var i0 = (i > 0 ? i - 1 : 0) * _channels;
                var i1 = i * _channels;
                for (var c = 0; c < channels; c++)
                    buffers[c][offset + j] = gain * Cubic(_in[i0 + c], _in[i1 + c], _in[i1 + _channels + c], _in[i1 + 2 * _channels + c], t);
                _pos += ratio;
                j++;
            }
            Compact();
            return j;
        }

        // Removes a large error at output frame j: drops input frames (sound late) or writes silence (sound early).
        // Returns the next output frame; sets _locked when the error is gone.
        private int Resync(ulong tap, float[][] buffers, int offset, int j, int count, double leaveNs, double target, double nsPerOut)
        {
            var error = leaveNs - DueAt(_pos) - target;
            if (error >= 0)
            {
                var wanted = _pos + error / _stepNs;
                if (wanted + 2 < _inFrames)
                {
                    _pos = wanted;
                    Lock();
                    return j;
                }
                // Skip all buffered input, then drop the rest in the ring (only of this segment); re-measured after refill.
                var drop = (int)Math.Floor(wanted) - _inFrames;
                _inFrames = 0;
                _pos = 0;
                _anchorIndex = 0;
                while (drop > 0)
                {
                    var rc = _reader.Read(tap, Span<float>.Empty, _channels, out var state);
                    if (rc < 0 || state.available <= 0 || state.discontinuity != _segment)
                        break;
                    rc = _reader.Drop(tap, Math.Min(drop, state.available), _channels, out state);
                    if (rc <= 0)
                        break;
                    drop -= rc;
                    _available = state.available;
                }
                // The caller refills (a new anchor) and measures again.
                return j;
            }
            var silence = (int)Math.Round(-error / nsPerOut);
            var end = Math.Min(count, j + silence);
            for (var c = 0; c < buffers.Length; c++)
                Array.Clear(buffers[c], offset + j, end - j);
            if (j + silence <= count)
                Lock();
            return end;
        }

        private void Lock()
        {
            _locked = true;
            _filteredNs = 0;
        }

        // Reads frames of the current segment until the rest of this buffer is covered (bounded by the scratch). A frame of
        // a new segment flushes the old ones. False when no frame came (underrun) or the tap failed.
        private bool Refill(ulong tap, int sampleRate, int framesLeft, double ratio)
        {
            Compact();
            var want = (int)Math.Min(capacityFrames, _pos + framesLeft * ratio + 3) - _inFrames;
            var any = false;
            while (want > 0)
            {
                if (_available <= 0)
                {
                    // State only: frames may have arrived since the last call.
                    if (_reader.Read(tap, Span<float>.Empty, _channels, out var peek) < 0 || peek.available <= 0)
                        break;
                    _available = peek.available;
                }
                var frames = Math.Min(want, _available);
                var rc = _reader.Read(tap, _in.AsSpan(_inFrames * _channels, frames * _channels), _channels, out var state);
                if (rc <= 0)
                    break;
                _available = state.available;
                Append(state, rc, sampleRate);
                any = true;
                want -= rc;
            }
            return any;
        }

        private void Append(in Native.vlqt_audio_tap_state state, int frames, int sampleRate)
        {
            if (!_hasSegment || state.discontinuity != _segment)
            {
                // Discontinuity: the frames before belong to the old segment. A media time going back is a new source.
                if (_inFrames > 0)
                    Array.Copy(_in, _inFrames * _channels, _in, 0, frames * _channels);
                if (_hasSegment && state.media_us < _lastMediaUs)
                    ResetVideo();
                _inFrames = 0;
                _pos = 0;
                _locked = false;
                _hasSegment = true;
                _segment = state.discontinuity;
            }
            _anchorIndex = _inFrames;
            _anchorDueNs = state.due_ns;
            _anchorMediaUs = state.media_us;
            _rate = state.rate > 0 ? state.rate : 1;
            _stepNs = 1e9 / (sampleRate * _rate);
            _inFrames += frames;
            _lastMediaUs = state.media_us;
        }

        // Keeps one frame before the read position (cubic interpolation).
        private void Compact()
        {
            var shift = Math.Min((int)_pos - 1, _inFrames);
            if (shift <= 0)
                return;
            Array.Copy(_in, shift * _channels, _in, 0, (_inFrames - shift) * _channels);
            _inFrames -= shift;
            _pos -= shift;
            _anchorIndex -= shift;
        }

        private void Flush()
        {
            _inFrames = 0;
            _pos = 0;
            _anchorIndex = 0;
            _hasSegment = false;
            _locked = false;
        }

        private double DueAt(double pos) => _anchorDueNs + (pos - _anchorIndex) * _stepNs;

        private double MediaAt(double pos) => _anchorMediaUs + (pos - _anchorIndex) * 1e6 / _sampleRate;

        private void ResetVideo()
        {
            _videoNs = 0;
            _videoMediaUs = -1;
            _loopOffsetUs = 0;
        }

        // Follows the frames at the video sink and the loop offset between Qt's video axis (restarts at a wrap) and the
        // audio axis (keeps counting). The offset comes from the data (spike in #22, never from QMediaPlayer::duration()):
        // at a wrap, or at a first frame that lies in another pass, it is the audio media time due when the frame came
        // minus the frame's media time; it is held until the next wrap, so the error measures what changes within a pass.
        private void TrackVideo(long videoMediaUs, long videoNs)
        {
            if (videoNs == 0 || videoNs == _videoNs)
                return;
            var wrapped = _videoMediaUs >= 0 && videoMediaUs < _videoMediaUs;
            var firstInOtherPass = _videoMediaUs < 0 && _hasSegment && Math.Abs(AudioMediaAtNs(videoNs) - videoMediaUs) > 1e6;
            if (_hasSegment && (wrapped || firstInOtherPass))
                _loopOffsetUs = (long)Math.Round(AudioMediaAtNs(videoNs)) - videoMediaUs;
            _videoMediaUs = videoMediaUs;
            _videoNs = videoNs;
        }

        private double AudioMediaAtNs(long ns) => MediaAt(_anchorIndex + (ns - _anchorDueNs) / _stepNs);

        // Sync Error: when the next frame leaves against when the video sink showed (or shows) its media time. Without a
        // video frame yet the due time stands for the picture (Qt emits both in step).
        private void UpdateSyncError(double leave0, int count, int sampleRate)
        {
            double picture;
            if (_videoNs != 0)
                picture = _videoNs + (MediaAt(_pos) - _videoMediaUs - _loopOffsetUs) * 1e3 / _rate;
            else
                picture = DueAt(_pos);
            var raw = leave0 - picture;
            if (Math.Abs(raw) > 1e9)
            {
                // No plausible relation (e.g. a stale frame of the previous source): no number rather than a wrong one.
                _syncFilteredNs = double.NaN;
                SyncErrorMs = float.NaN;
                return;
            }
            _syncFilteredNs = double.IsNaN(_syncFilteredNs)
                ? raw
                : _syncFilteredNs + Math.Min(1.0, count / (sampleRate * syncFilterSeconds)) * (raw - _syncFilteredNs);
            SyncErrorMs = (float)(_syncFilteredNs * 1e-6);
        }

        // Catmull-Rom between y1 (t = 0) and y2 (t = 1); exact for linear signals.
        private static float Cubic(float y0, float y1, float y2, float y3, float t)
            => y1 + 0.5f * t * (y2 - y0 + t * (2f * y0 - 5f * y1 + 4f * y2 - y3 + t * (3f * (y1 - y2) + y3 - y0)));
    }
}