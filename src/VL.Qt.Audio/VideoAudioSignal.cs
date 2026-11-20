using System;
using VL.Audio;

namespace VL.Qt.Audio
{
    /// <summary>
    /// The sound of one Video (VL.Audio) as VL.Audio signals, one per channel. Reads the QtNative audio tap whose
    /// id the node sets (<see cref="TapId"/>) and keeps it in sync with the picture; the loop itself is
    /// <see cref="AudioTapPlayer"/>.
    /// <para>
    /// Threads: <see cref="FillBuffers"/> runs on VL.Audio's audio thread and allocates nothing. The node writes
    /// <see cref="TapId"/>, <see cref="Volume"/> and <see cref="AudioOffsetMs"/> and calls <see cref="UpdateOutputLatency"/>
    /// on the main thread and reads <see cref="SyncErrorMs"/> and <see cref="EngineSampleRate"/>; each is one volatile
    /// field, no locks.
    /// </para>
    /// </summary>
    internal sealed class VideoAudioSignal : MultiChannelSignal
    {
        /// <summary>Channels of the tap and of <see cref="MultiChannelSignal.Outputs"/>: stereo, Qt mixes mono or multichannel media to it.</summary>
        public const int Channels = 2;

        // WASAPI shared mode when the device period cannot be read: the usual default period.
        private const long fallbackDriverLatencyNs = 10_000_000;

        private readonly AudioTapPlayer _player;
        private string? _latencyDriver;
        private int _latencySampleRate;
        private long _driverLatencyNs;

        public VideoAudioSignal(IAudioTapReader reader)
        {
            _player = new AudioTapPlayer(reader, Channels);
            SetOutputCount(Channels);
        }

        /// <summary>Tap to read (main thread writes), 0 = none: silence. A change is a discontinuity: flush and re-lock.</summary>
        public ulong TapId
        {
            get => _player.TapId;
            set => _player.TapId = value;
        }

        /// <summary>Linear gain 0..1, applied here (Qt's own output is off).</summary>
        public float Volume
        {
            get => _player.Volume;
            set => _player.Volume = value;
        }

        /// <summary>Audio Offset pin in ms: positive plays the sound later (calibration of display/device latency).</summary>
        public float AudioOffsetMs
        {
            get => _player.AudioOffsetMs;
            set => _player.AudioOffsetMs = value;
        }

        /// <summary>Last measured sync error in ms (sound minus picture, Audio Offset included); NaN while not locked.</summary>
        public float SyncErrorMs => _player.SyncErrorMs;

        /// <summary>Sample rate of the VL.Audio engine as this signal sees it; the node creates a tap with it.</summary>
        public int EngineSampleRate => SampleRate;

        /// <summary>
        /// Main thread: the output latency of VL.Audio as far as it knows it, one buffer plus the driver's latency (ASIO:
        /// the driver's output latency; WASAPI: the device period VL.Audio opens the shared stream with). Queries the
        /// driver only when driver, sample rate or buffer size change. The rest (display, device internals) is what Audio
        /// Offset calibrates.
        /// </summary>
        public void UpdateOutputLatency()
        {
            var engine = AudioService.Engine;
            var driver = engine.CurrentDriverName;
            var sampleRate = SampleRate;
            if (sampleRate <= 0)
                return;
            if (driver != _latencyDriver || sampleRate != _latencySampleRate)
                _driverLatencyNs = QueryDriverLatencyNs(engine, sampleRate);
            _latencyDriver = driver;
            _latencySampleRate = sampleRate;
            _player.OutputLatencyNs = (long)(BufferSize * 1e9 / sampleRate) + _driverLatencyNs;
        }

        private static long QueryDriverLatencyNs(AudioEngine engine, int sampleRate)
        {
            try
            {
                if (engine.CurrentDriverName?.StartsWith("ASIO", StringComparison.Ordinal) == true && engine.AsioDevice is { } asio)
                    return (long)(asio.PlaybackLatency * 1e9 / sampleRate);
                if (engine.WasapiDevice?.MMOutDevice is { } device)
                {
                    // MMDevice.AudioClient activates a new client per call.
                    using var client = device.AudioClient;
                    return client.MinimumDevicePeriod * 100;
                }
            }
            catch (Exception)
            {
                // COM or driver errors only cost accuracy here: Audio Offset calibrates the rest.
            }
            return fallbackDriverLatencyNs;
        }

        /// <summary>Audio thread: fills buffers[channel][offset .. offset + count) for every channel.</summary>
        protected override void FillBuffers(float[][] buffers, int offset, int count)
            => _player.Fill(buffers, offset, count, SampleRate);
    }
}
