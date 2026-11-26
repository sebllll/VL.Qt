using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// vlqt_audio_tap_read takes no lock and never frees a tap (slot table of resources/audio_tap.cpp). A reader
// thread reads a stable tap and the tap the main thread is creating and destroying, while the main thread creates,
// initialises and frees 100 taps: the stable reads stay fast and valid, the churned ones return frames or UnknownId,
// never anything else and never a crash. Then the table fills (init refuses tap 65 with BadArgument) and frees again.
// No video, no audio device: runs everywhere.
[TestFixture, NonParallelizable]
public static class AudioTapChurnTest
{
    private const int Channels = 2, SampleRate = 48000, Churn = 100, MaxTaps = 64;

    /// <summary>Audio tap reads while taps are created and destroyed; the slot table limit.</summary>
    [Test, Category("audiotap-churn")]
    public static void AudioTapChurn() => Scenario.Run(Run);

    private static ulong CreateTap(out Native.Result init)
    {
        var rc = Native.vlqt_res_create(Protocol.ResourceKind.AudioTap, out var id);
        init = rc == Native.Result.Ok ? Native.vlqt_audio_tap_init(id, Channels, SampleRate) : rc;
        return id;
    }

    private static void Run(Scenario s)
    {
        var resBuf = new byte[1 << 14];
        var stable = CreateTap(out var init0);
        s.Check(init0 == Native.Result.Ok, $"audiotap-churn: stable tap created ({init0})");

        // Reader thread: one reader per tap (protocol.h), here one thread for both taps.
        ulong churned = 0;
        var stop = false;
        long reads = 0, slow = 0, maxTicks = 0, stableBad = 0, churnBad = 0, churnReads = 0;
        var reader = new Thread(() =>
        {
            var dst = new float[256 * Channels];
            var slowTicks = Stopwatch.Frequency / 1000; // 1 ms
            while (!Volatile.Read(ref stop))
            {
                var t0 = Stopwatch.GetTimestamp();
                var n = Native.AudioTapRead(stable, dst, Channels, out _);
                var dt = Stopwatch.GetTimestamp() - t0;
                reads++;
                maxTicks = Math.Max(maxTicks, dt);
                if (dt > slowTicks)
                    slow++;
                if (n < 0)
                    stableBad++;
                var id = Volatile.Read(ref churned);
                if (id != 0)
                {
                    var m = Native.AudioTapRead(id, dst, Channels, out _);
                    churnReads++;
                    if (m < 0 && m != (int)Native.Result.UnknownId)
                        churnBad++;
                }
                Thread.Sleep(0);
            }
        }) { IsBackground = true, Name = "audio tap churn reader", Priority = ThreadPriority.Highest };
        reader.Start();

        var created = 0;
        for (var i = 0; i < Churn; i++)
        {
            var id = CreateTap(out var init);
            if (init == Native.Result.Ok)
                created++;
            Volatile.Write(ref churned, id);
            s.Pump(1);
            Native.vlqt_res_release(id);
            // The manager frees at the end of the pump: Kind::destroy waits there for the reader to leave the slot.
            s.Pump(1);
            NativeCalls.ResPoll(resBuf);
        }
        Volatile.Write(ref stop, true);
        reader.Join();

        s.Check(created == Churn, $"audiotap-churn: {created}/{Churn} taps created and freed");
        s.Check(stableBad == 0, $"audiotap-churn: reads of the stable tap never fail ({stableBad} of {reads})");
        s.Check(churnBad == 0, $"audiotap-churn: reads of a churned tap return frames or UnknownId ({churnBad} of {churnReads})");
        var maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
        s.Info($"audiotap-churn: {reads} reads, max {maxMs:F3} ms, {slow} over 1 ms, {churnReads} churned reads");
        // A preempted reader thread can exceed 1 ms without any lock: allow one in a thousand.
        s.Check(reads > 0 && slow <= reads / 1000, $"audiotap-churn: reads stay under 1 ms ({slow} of {reads} over, max {maxMs:F3} ms)");

        // Slot table: the stable tap holds one slot, so at most MaxTaps - 1 more fit; the next init is refused.
        var held = new System.Collections.Generic.List<ulong>();
        var refused = Native.Result.Ok;
        var refusedText = "";
        for (var i = 0; i < MaxTaps + 1 && refused == Native.Result.Ok; i++)
        {
            var id = CreateTap(out var init);
            held.Add(id);
            if (init != Native.Result.Ok)
            {
                refused = init;
                refusedText = Native.LastError();
            }
        }
        var fitted = held.Count - (refused == Native.Result.Ok ? 0 : 1);
        s.Check(refused == Native.Result.BadArgument && refusedText.Contains("too many audio taps") && fitted <= MaxTaps - 1,
            $"audiotap-churn: a full table refuses init after {fitted} taps ({refused}) {refusedText}");
        foreach (var id in held)
            Native.vlqt_res_release(id);
        s.Pump(2);
        NativeCalls.ResPoll(resBuf);
        var again = CreateTap(out var initAgain);
        s.Check(initAgain == Native.Result.Ok, $"audiotap-churn: freed slots are used again ({initAgain})");
        Native.vlqt_res_release(again);
        Native.vlqt_res_release(stable);
        s.Pump(2);
        NativeCalls.ResPoll(resBuf);
        s.Check(Native.AudioTapRead(stable, Span<float>.Empty, Channels, out _) == (int)Native.Result.UnknownId,
            "audiotap-churn: the released stable tap reads as UnknownId");
    }
}
