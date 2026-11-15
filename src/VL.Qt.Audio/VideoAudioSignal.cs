using System;
using System.Threading;
using VL.Audio;

namespace VL.Qt.Audio
{
    /// <summary>
    /// The sound of one Video (VL.Audio) as VL.Audio signals, one per channel (#22). Reads the QtNative audio tap whose
    /// id the node sets (<see cref="TapId"/>) and keeps it in sync with the picture.
    /// <para>
    /// Threads: <see cref="FillBuffers"/> runs on VL.Audio's audio thread and allocates nothing. The node writes
    /// <see cref="TapId"/>, <see cref="Volume"/> and <see cref="AudioOffsetMs"/> on the main thread and reads
    /// <see cref="SyncErrorMs"/> and <see cref="EngineSampleRate"/>; each is one volatile field, no locks.
    /// </para>
    /// STUB (T3): outputs silence. TODO(T5) at <see cref="FillBuffers"/>.
    /// </summary>
    internal sealed class VideoAudioSignal : MultiChannelSignal
    {
        /// <summary>Channels of the tap and of <see cref="MultiChannelSignal.Outputs"/>: stereo, Qt mixes mono or multichannel media to it.</summary>
        public const int Channels = 2;

        readonly IAudioTapReader _reader;
        ulong _tapId;
        float _volume = 1f;
        float _audioOffsetMs;
        float _syncErrorMs = float.NaN;

        public VideoAudioSignal(IAudioTapReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            SetOutputCount(Channels);
        }

        /// <summary>Tap to read (main thread writes), 0 = none: silence. A change is a discontinuity: flush and re-lock.</summary>
        public ulong TapId
        {
            get => Volatile.Read(ref _tapId);
            set => Volatile.Write(ref _tapId, value);
        }

        /// <summary>Linear gain 0..1, applied here (Qt's own output is off).</summary>
        public float Volume
        {
            get => Volatile.Read(ref _volume);
            set => Volatile.Write(ref _volume, value);
        }

        /// <summary>Audio Offset pin in ms: positive plays the sound later (calibration of display/device latency).</summary>
        public float AudioOffsetMs
        {
            get => Volatile.Read(ref _audioOffsetMs);
            set => Volatile.Write(ref _audioOffsetMs, value);
        }

        /// <summary>Last measured sync error in ms (audio minus picture, Audio Offset included); NaN while not locked.</summary>
        public float SyncErrorMs
        {
            get => Volatile.Read(ref _syncErrorMs);
            private set => Volatile.Write(ref _syncErrorMs, value);
        }

        /// <summary>Sample rate of the VL.Audio engine as this signal sees it; the node creates a tap with it.</summary>
        public int EngineSampleRate => SampleRate;

        /// <summary>Audio thread: fills buffers[channel][offset .. offset + count) for every channel.</summary>
        protected override void FillBuffers(float[][] buffers, int offset, int count)
        {
            // TODO(T5): the control loop (issue #22 "Sync", protocol.h "Audio tap"), no allocation per call:
            //   - read TapId; 0, -9 or not ATTACHED: silence, SyncErrorMs = NaN; a new id or a new state.discontinuity:
            //     forget the lock (flush, re-lock);
            //   - target: the frame written at buffers[..][offset] leaves the speaker at now + output latency (VL.Audio
            //     BufferSize / SampleRate + driver latency); it should be the frame whose due_ns (+ AudioOffsetMs) is
            //     that time; error = due_ns of the next frame - that time (ms);
            //   - large error (start, after a discontinuity): drop frames with IAudioTapReader.Drop or insert silence
            //     once, then lock; small error: resample within +-0.5 % (linear or cubic) and steer the ratio slowly;
            //     rate != 1: the tap delivers media content, resample by 1 / state.rate (tape, pitch follows the rate);
            //   - underrun: silence for the missing part, never stretch; deinterleave into buffers, apply Volume;
            //   - interleaved scratch buffer allocated outside the audio thread (or once, sized from BufferSize);
            //   - SyncErrorMs = the filtered error; tests: simulated clocks +-1 %, jitter, underrun, discontinuity, rate change.
            for (var c = 0; c < buffers.Length; c++)
                Array.Clear(buffers[c], offset, count);
            SyncErrorMs = float.NaN;
        }
    }
}
