using System;
using NAudio.CoreAudioApi;

namespace VL.Qt.Audio
{
    /// <summary>
    /// The output latency of the WASAPI shared stream VL.Audio opens. VL.Audio 1.9.21 (<c>WasapiInOut</c>) opens
    /// <c>new WasapiOut(device, Shared, eventSync: true, ceil(MinimumDevicePeriod in ms))</c>, 3 ms on a typical device,
    /// but Windows runs the shared stream with its own period: the buffer is two periods plus the stream's headroom
    /// (1056 frames = 22 ms at 48 kHz on the reference machine), and <c>StreamLatency</c> reports 0 in shared mode. The
    /// buffer is what the sound waits in at most between VL.Audio handing it over and the engine mixing it, so it is
    /// the latency used here (see docs/video-audio.md, "Output latency").
    /// </summary>
    internal static class WasapiOutputLatency
    {
        /// <summary>
        /// Returns the buffer length in ns of a shared event-driven stream opened on <paramref name="device"/> the way VL.Audio
        /// opens its stream, or null if the device refuses the stream. Opens a client, reads its buffer size and drops it
        /// without starting it. Main thread, at most when the driver or the sample rate change.
        /// </summary>
        public static long? StreamBufferNs(MMDevice device)
        {
            try
            {
                // MMDevice.AudioClient activates a new client per call.
                using var client = device.AudioClient;
                var format = client.MixFormat;
                var latencyMs = (int)Math.Ceiling(client.MinimumDevicePeriod / 10000.0);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, latencyMs * 10000L, 0, format, Guid.Empty);
                return client.BufferSize * 1_000_000_000L / format.SampleRate;
            }
            catch (Exception)
            {
                // COM errors (device busy, format refused) only cost accuracy: the caller has a fallback, Audio Offset calibrates the rest.
                return null;
            }
        }
    }
}
