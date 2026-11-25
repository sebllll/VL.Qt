using System;
using System.Globalization;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NUnit.Framework;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// Which WASAPI latency does VL.Audio's stream run with? Opens the default render device exactly as VL.Audio's
    /// <c>WasapiInOut</c> does (<c>new WasapiOut(device, Shared, true, ceil(MinimumDevicePeriod / 10000))</c>, VL.Audio
    /// 1.9.21), prints the periods, the stream's buffer and <c>StreamLatency</c>, and checks that
    /// <see cref="WasapiOutputLatency"/>, which <c>VideoAudioSignal</c> uses, reads the buffer length of that stream.
    /// Needs an audio render device (ignored without one); category <c>Perf</c> (see <c>PerfLimit</c> in VL.Qt.Tests).
    /// </summary>
    [TestFixture, NonParallelizable]
    public sealed class WasapiLatencyTests
    {
        private sealed class Silence : IWaveProvider
        {
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

            public int Read(byte[] buffer, int offset, int count)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
        }

        /// <summary>The buffer of the stream VL.Audio opens (shared mode, event sync) against what the signal reads.</summary>
        [Test, Category("Perf")]
        public void StreamBufferIsWhatTheSignalUses()
        {
            MMDevice device;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception e)
            {
                Assert.Ignore("no audio render device: " + e.Message);
                return;
            }

            using (device)
            {
                long minimum, @default;
                using (var probe = device.AudioClient)
                {
                    minimum = probe.MinimumDevicePeriod;
                    @default = probe.DefaultDevicePeriod;
                }
                // The line of VL.Audio's WasapiInOut: the latency argument is the minimum period rounded up to whole ms.
                var latencyMs = (int)Math.Ceiling(minimum / 10000.0);
                using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
                output.Init(new Silence());
                output.Play();
                System.Threading.Thread.Sleep(300);

                var client = (AudioClient?)typeof(WasapiOut).GetField("audioClient", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(output);
                Assert.That(client, Is.Not.Null, "NAudio WasapiOut has no 'audioClient' field any more");
                var rate = client!.MixFormat.SampleRate;
                double streamLatencyMs = client.StreamLatency / 10000.0;
                double bufferMs = client.BufferSize * 1000.0 / rate;
                double assumedMs = minimum / 10000.0;
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"WASAPI shared/event: device '{device.FriendlyName}', mix {rate} Hz; MinimumDevicePeriod {assumedMs:F2} ms, DefaultDevicePeriod {@default / 10000.0:F2} ms, " +
                    $"requested {latencyMs} ms, stream buffer {client.BufferSize} frames = {bufferMs:F2} ms, StreamLatency {streamLatencyMs:F2} ms"));
                output.Stop();

                var used = WasapiOutputLatency.StreamBufferNs(device);
                Assert.That(used, Is.Not.Null, "WasapiOutputLatency could not open a client on the default device");
                Assert.That(used!.Value / 1e6, Is.EqualTo(bufferMs).Within(0.5), "buffer of the real stream (ms)");
            }
        }
    }
}
