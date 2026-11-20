using System;
using NUnit.Framework;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// The sync loop of Video (VL.Audio) against a simulated tap and a simulated audio device: the device pulls
    /// buffers on its own clock (drift against the tap clock, callback jitter), each output frame leaves the speaker at a
    /// known time, and the error is that time minus the true due time of the source frame it carries minus the target delay.
    /// </summary>
    [TestFixture]
    public sealed class AudioTapPlayerTests
    {
        private const int sampleRate = 48_000;
        private const int bufferSize = 512;
        private const long latencyNs = 20_000_000;
        private const double toleranceNs = 5e6;

        // Pulls buffers like an audio device and measures every frame that leaves.
        private sealed class Device
        {
            private readonly AudioTapPlayer _player;
            private readonly SimulatedTap _tap;
            private readonly double _framesPerSecond;
            private readonly double _pullJitterNs;
            private readonly Random _random = new(3);
            private readonly long _startNs;
            private long _buffer;

            public Device(double drift, double pullJitterNs = 1e6, double emitJitterNs = 1.5e6, int seed = 1)
            {
                _startNs = 1_000_000_000_000;
                _tap = new SimulatedTap(sampleRate, emitJitterNs, seed, _startNs);
                _player = new AudioTapPlayer(_tap, 2) { TapId = SimulatedTap.Id, OutputLatencyNs = latencyNs };
                _framesPerSecond = sampleRate * (1 + drift);
                _pullJitterNs = pullJitterNs;
                Buffers = new[] { new float[bufferSize], new float[bufferSize] };
            }

            public SimulatedTap Tap => _tap;
            public AudioTapPlayer Player => _player;
            public float[][] Buffers { get; }

            /// <summary>Time on the tap clock in ns since the start.</summary>
            public double ElapsedNs => LeaveNs(0) - _startNs;

            public long NowNs => (long)LeaveNs(0) - latencyNs;

            // When frame j of the next buffer leaves the speaker (device clock, drifting against the tap clock).
            private double LeaveNs(int j) => _startNs + latencyNs + (_buffer * bufferSize + j) * 1e9 / _framesPerSecond;

            /// <summary>Pulls one buffer without measuring (allocation test).</summary>
            public void Pull()
            {
                var pullNs = (long)(LeaveNs(0) - latencyNs + (_random.NextDouble() * 2 - 1) * _pullJitterNs);
                _tap.Advance(pullNs);
                _player.Fill(Buffers, 0, bufferSize, sampleRate);
                _buffer++;
            }

            /// <summary>Pulls buffers for the given time; reports every frame as (time since start, error or NaN for silence, segment).</summary>
            public void Run(double seconds, Action<double, double, uint>? frame = null)
            {
                var end = ElapsedNs + seconds * 1e9;
                while (ElapsedNs < end)
                {
                    var leave0 = LeaveNs(0);
                    var t0 = ElapsedNs;
                    Pull();
                    if (frame is null)
                        continue;
                    for (var j = 0; j < bufferSize; j++)
                    {
                        var value = Buffers[0][j];
                        var t = t0 + j * 1e9 / _framesPerSecond;
                        if (value == 0f)
                        {
                            frame(t, double.NaN, 0);
                            continue;
                        }
                        var index = value / SimulatedTap.Scale;
                        var leave = leave0 + j * 1e9 / _framesPerSecond;
                        frame(t, leave - _tap.TrueDueNs(index) - _player.TargetDelayNs, _tap.SegmentOf(index));
                    }
                }
            }

            /// <summary>Largest |error| in ns of the frames over the given time; silence counted separately.</summary>
            public (double MaxErrorNs, int SilentFrames) Measure(double seconds)
            {
                var max = 0.0;
                var silent = 0;
                Run(seconds, (_, error, _) =>
                {
                    if (double.IsNaN(error))
                        silent++;
                    else
                        max = Math.Max(max, Math.Abs(error));
                });
                return (max, silent);
            }

            /// <summary>
            /// Seconds from now until every following frame (within the given time) is in sync and of the newest segment;
            /// silence before that counts as not yet locked, silence after it fails the check.
            /// </summary>
            public double ReLockSeconds(double seconds, uint segment, out double maxErrorAfterNs, out int silentAfter)
            {
                var start = ElapsedNs;
                var lockedAt = double.NaN;
                var max = 0.0;
                var silent = 0;
                Run(seconds, (t, error, seg) =>
                {
                    var good = !double.IsNaN(error) && Math.Abs(error) < toleranceNs && seg == segment;
                    if (double.IsNaN(lockedAt))
                    {
                        if (good)
                            lockedAt = t;
                        return;
                    }
                    if (double.IsNaN(error))
                        silent++;
                    else
                        max = Math.Max(max, Math.Abs(error) + (seg == segment ? 0 : 1e9));
                });
                maxErrorAfterNs = max;
                silentAfter = silent;
                return (lockedAt - start) * 1e-9;
            }
        }

        [TestCase(0.0)]
        [TestCase(0.01)]
        [TestCase(-0.01)]
        [TestCase(0.0001)]
        public void LocksWithClockDriftAndJitter(double drift)
        {
            var device = new Device(drift);
            device.Run(15);
            var (maxError, silent) = device.Measure(15);
            TestContext.Out.WriteLine($"drift {drift:P2}: max error {maxError * 1e-6:F2} ms, target delay {device.Player.TargetDelayNs * 1e-6:F1} ms, filtered {device.Player.FilteredErrorNs * 1e-6:F2} ms");
            Assert.That(device.Player.Locked, Is.True);
            Assert.That(silent, Is.Zero, "no underrun once locked");
            Assert.That(device.Tap.UnderrunFrames, Is.Zero, "reads ask only for available frames");
            Assert.That(maxError, Is.LessThan(toleranceNs));
            // Reachable minimum: output latency + one buffer + margin (Audio Offset 0).
            Assert.That(device.Player.TargetDelayNs, Is.EqualTo(latencyNs + bufferSize * 1e9 / sampleRate + 5e6).Within(1));
        }

        [Test]
        public void LocksFromTheFirstBuffers()
        {
            var device = new Device(0.001);
            var seconds = device.ReLockSeconds(5, device.Tap.Discontinuity, out var maxAfter, out var silentAfter);
            TestContext.Out.WriteLine($"lock after {seconds * 1e3:F0} ms, then max error {maxAfter * 1e-6:F2} ms");
            Assert.That(seconds, Is.LessThan(0.1));
            Assert.That(maxAfter, Is.LessThan(toleranceNs));
            Assert.That(silentAfter, Is.Zero);
        }

        [Test]
        public void AudioOffsetDelaysTheSound()
        {
            var device = new Device(0.0);
            device.Player.AudioOffsetMs = 120;
            device.Run(5);
            var (maxError, silent) = device.Measure(5);
            Assert.That(device.Player.TargetDelayNs, Is.EqualTo(120e6).Within(1));
            Assert.That(maxError, Is.LessThan(toleranceNs));
            Assert.That(silent, Is.Zero);
        }

        [Test]
        public void UnderrunGivesSilenceNeverStretches()
        {
            var device = new Device(0.0);
            device.Run(5);
            device.Tap.Stall(100e6);
            var (maxError, silent) = device.Measure(3);
            var silentMs = silent * 1e3 / sampleRate;
            TestContext.Out.WriteLine($"stall 100 ms: {silentMs:F1} ms silence, max error {maxError * 1e-6:F2} ms");
            // The gap is silence (about its length), and no frame plays at the wrong time (nothing stretched).
            Assert.That(silentMs, Is.InRange(90, 130));
            Assert.That(maxError, Is.LessThan(toleranceNs));
            Assert.That(device.Player.Locked, Is.True);
        }

        [Test]
        public void SourceChangeFlushesAndReLocks()
        {
            var device = new Device(0.002);
            device.Run(5);
            // A new source: media time back to 0, buffers from 20 ms on; old frames still in the ring are flushed.
            device.Tap.StartSegment(1.0, 0, device.NowNs + 20e6);
            var seconds = device.ReLockSeconds(3, device.Tap.Discontinuity, out var maxAfter, out var silentAfter);
            TestContext.Out.WriteLine($"source change: re-lock after {seconds * 1e3:F0} ms, then max error {maxAfter * 1e-6:F2} ms");
            Assert.That(seconds, Is.LessThan(0.25));
            Assert.That(maxAfter, Is.LessThan(toleranceNs), "only frames of the new segment, in sync");
            Assert.That(silentAfter, Is.Zero);
        }

        [Test]
        public void PauseAndPlayReLocks()
        {
            var device = new Device(0.0);
            device.Run(3);
            device.Tap.Pause();
            var (_, silent) = device.Measure(1);
            Assert.That(silent, Is.GreaterThan(sampleRate * 9 / 10), "silence while paused");
            Assert.That(float.IsNaN(device.Player.SyncErrorMs), Is.True);
            device.Tap.StartSegment(1.0, 3_000_000, device.NowNs);
            var seconds = device.ReLockSeconds(2, device.Tap.Discontinuity, out var maxAfter, out _);
            Assert.That(seconds, Is.LessThan(0.25));
            Assert.That(maxAfter, Is.LessThan(toleranceNs));
        }

        [Test]
        public void BurstAfterReAttachReLocks()
        {
            // T4: after a re-attach Qt sends nothing for 0.25-0.9 s, then everything at once (> 500 ms, new segment).
            var device = new Device(0.0);
            device.Run(3);
            device.Player.TapId = 0;
            device.Tap.Pause();
            device.Run(0.7);
            device.Tap.StartSegment(1.0, 3_000_000, device.NowNs - 600e6);
            device.Tap.EmitNotBefore = device.NowNs;
            device.Player.TapId = SimulatedTap.Id;
            var seconds = device.ReLockSeconds(3, device.Tap.Discontinuity, out var maxAfter, out var silentAfter);
            TestContext.Out.WriteLine($"burst: re-lock after {seconds * 1e3:F0} ms, then max error {maxAfter * 1e-6:F2} ms");
            Assert.That(seconds, Is.LessThan(0.5));
            Assert.That(maxAfter, Is.LessThan(toleranceNs));
            Assert.That(silentAfter, Is.Zero);
        }

        [TestCase(2.0)]
        [TestCase(0.5)]
        [TestCase(1.37)]
        public void RateChangeIsStableAfterReLock(double rate)
        {
            var device = new Device(0.003);
            device.Run(5);
            device.Tap.StartSegment(rate, 5_000_000, device.NowNs);
            var seconds = device.ReLockSeconds(8, device.Tap.Discontinuity, out var maxAfter, out var silentAfter);
            TestContext.Out.WriteLine($"rate {rate}: re-lock after {seconds * 1e3:F0} ms, then max error {maxAfter * 1e-6:F2} ms");
            Assert.That(seconds, Is.LessThan(0.5));
            Assert.That(maxAfter, Is.LessThan(toleranceNs));
            Assert.That(silentAfter, Is.Zero);
        }

        [Test]
        public void VolumeScalesTheSignal()
        {
            var a = new Device(0.0);
            var b = new Device(0.0);
            b.Player.Volume = 0.25f;
            a.Run(2);
            b.Run(2);
            var la = a.Buffers[0][bufferSize - 1];
            var lb = b.Buffers[0][bufferSize - 1];
            Assert.That(lb, Is.EqualTo(la * 0.25f).Within(Math.Abs(la) * 1e-5f));
        }

        [Test]
        public void NoTapIsSilence()
        {
            var device = new Device(0.0);
            device.Run(2);
            device.Player.TapId = 0;
            var (_, silent) = device.Measure(0.5);
            Assert.That(silent, Is.GreaterThan(sampleRate * 0.49));
            Assert.That(float.IsNaN(device.Player.SyncErrorMs), Is.True);
        }

        [Test]
        public void SyncErrorFollowsTheVideoAcrossLoopWraps()
        {
            var device = new Device(0.0) { };
            device.Tap.LoopUs = 1_015_500;
            device.Run(1);
            var target = device.Player.TargetDelayNs * 1e-6;
            var max = 0.0;
            for (var k = 0; k < 500; k++)
            {
                device.Run(0.01);
                max = Math.Max(max, Math.Abs(device.Player.SyncErrorMs - target));
            }
            TestContext.Out.WriteLine($"sync error - target: max {max:F2} ms over 5 s (5 loop wraps)");
            // The picture is shown when its media time is due: the sound is late by exactly the target delay.
            Assert.That(max, Is.LessThan(5));
        }

        [Test]
        public void NoAllocationPerBuffer()
        {
            var device = new Device(0.005);
            device.Run(2);
            device.Tap.StartSegment(1.5, 0, device.NowNs);
            for (var k = 0; k < 50; k++)
                device.Pull();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var k = 0; k < 2000; k++)
            {
                if (k == 1000)
                    device.Tap.StartSegment(1.0, 0, device.NowNs);
                device.Pull();
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
        }
    }
}
