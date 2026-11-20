using System;
using System.Globalization;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NUnit.Framework;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// COMP-04 / C19: which WASAPI latency does VL.Audio's stream run with, against what <c>VideoAudioSignal</c> assumes
    /// (<c>AudioClient.MinimumDevicePeriod</c>)? Opens the default render device exactly as VL.Audio's <c>WasapiInOut</c>
    /// does (<c>new WasapiOut(device, Shared, true, ceil(MinimumDevicePeriod / 10000))</c>, VL.Audio 1.9.21) and prints
    /// the periods, the stream's buffer and <c>StreamLatency</c>. Needs an audio render device (ignored without one);
    /// numbers only, a difference above 1 ms is a warning (category <c>Perf</c>, see <c>PerfLimit</c> in VL.Qt.Tests).
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

        /// <summary>The latency of the stream VL.Audio opens (shared mode, event sync) next to the minimum device period.</summary>
        [Test, Category("Perf")]
        public void StreamLatencyAgainstMinimumDevicePeriod()
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

                if (Math.Abs(streamLatencyMs - assumedMs) > 1)
                    Assert.Warn(string.Create(CultureInfo.InvariantCulture,
                        $"StreamLatency {streamLatencyMs:F2} ms differs from MinimumDevicePeriod {assumedMs:F2} ms by more than 1 ms"));
            }
        }
    }
}
