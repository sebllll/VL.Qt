using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VL.Qt;

// #22: the sound of a Video through an audio tap (QtNative audio_tap.cpp, protocol.h "Audio tap"). A reader thread
// plays the part of VL.Audio's audio thread and reads the tap the whole time. Checks: samples not silent, about 1 s of
// samples per s of wall time, loop wrap without discontinuity and gap, source change and pause -> play raise the
// discontinuity, audio due times match the video frames at the sink, an MSAA window swap (#118) keeps the tap running,
// a tap switch and a second Video on the same tap during playback, detach, DESTROY and release.
// Asset: help/Qt/assets/qt-help-video-audio.mp4 (video with an audio track).
static class VideoAudioTest
{
    const uint VideoId = 1, Video2Id = 2, Handle = 41;
    const int Channels = 2;

    // Statistics of one tap, written by the reader thread.
    sealed class TapStats
    {
        public long Frames, Gaps, Calls;
        public float Peak;
        public uint Discontinuity, Changes;
        public long MaxGapUs, LastMediaUs, MaxMediaUs, LastEndUs = -1;
        public int Flags;
        public ulong Overrun, Underrun;
        public readonly List<double> SyncErrorsMs = new();
    }

    sealed class Reader : IDisposable
    {
        readonly Thread _thread;
        readonly object _syncRoot = new();
        readonly Dictionary<ulong, TapStats> _stats = new();
        volatile bool _stop;
        ulong _id;

        public Reader()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "audio tap reader" };
            _thread.Start();
        }

        public ulong Id { get => Interlocked.Read(ref _id); set => Interlocked.Exchange(ref _id, value); }

        public TapStats Snapshot(ulong id)
        {
            lock (_syncRoot)
            {
                return _stats.TryGetValue(id, out var s) ? s.Copy() : new TapStats();
            }
        }

        public List<double> SyncErrors(ulong id)
        {
            lock (_syncRoot)
                return _stats.TryGetValue(id, out var s) ? new List<double>(s.SyncErrorsMs) : new List<double>();
        }

        void Loop()
        {
            var buf = new float[4800 * Channels];
            while (!_stop)
            {
                var id = Id;
                if (id != 0)
                {
                    int n;
                    do
                    {
                        // Ask for what is there, like a reader that needs exactly that much (no underrun counted).
                        n = Native.ResReadAudio(id, Span<float>.Empty, Channels, out var avail);
                        if (n < 0 || avail.available == 0)
                        {
                            if (n >= 0)
                                Account(id, buf, 0, avail);
                            break;
                        }
                        n = Native.ResReadAudio(id, buf.AsSpan(0, Math.Min(avail.available, buf.Length / Channels) * Channels), Channels, out var st);
                        if (n >= 0)
                            Account(id, buf, n, st);
                    } while (n > 0);
                }
                Thread.Sleep(5);
            }
        }

        void Account(ulong id, float[] buf, int n, Native.vlqt_audio_tap_state st)
        {
            lock (_syncRoot)
            {
                if (!_stats.TryGetValue(id, out var s))
                    _stats[id] = s = new TapStats { Discontinuity = st.discontinuity };
                s.Calls++;
                s.Flags = st.flags;
                s.Overrun = st.overrun_frames;
                s.Underrun = st.underrun_frames;
                if (n == 0)
                    return;
                if (st.discontinuity != s.Discontinuity)
                {
                    s.Discontinuity = st.discontinuity;
                    s.Changes++;
                }
                else if (s.LastEndUs >= 0)
                {
                    // Within a segment the media time continues without a gap (also across a loop wrap).
                    var gap = Math.Abs(st.media_us - s.LastEndUs);
                    if (gap > 1000)
                    {
                        s.Gaps++;
                        s.MaxGapUs = Math.Max(s.MaxGapUs, gap);
                    }
                }
                s.LastMediaUs = st.media_us;
                s.MaxMediaUs = Math.Max(s.MaxMediaUs, st.media_us);
                s.LastEndUs = st.media_us + n * 1_000_000L / st.sample_rate;
                s.Frames += n;
                for (int i = 0; i < n * Channels; i++)
                    s.Peak = Math.Max(s.Peak, Math.Abs(buf[i]));
                // Sync: when the audio frame of the video frame's media time is due vs. when that frame reached the
                // sink. Only for a recent frame on the same axis (the video axis restarts at a loop wrap).
                if (st.video_ns != 0 && st.now_ns - st.video_ns < 100_000_000 && Math.Abs(st.video_media_us - st.media_us) < 200_000)
                {
                    var due = st.due_ns + (st.video_media_us - st.media_us) * 1000.0 / st.rate;
                    s.SyncErrorsMs.Add((due - st.video_ns) / 1e6);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    static TapStats Copy(this TapStats s) => new()
    {
        Frames = s.Frames, Gaps = s.Gaps, Calls = s.Calls, Peak = s.Peak, Discontinuity = s.Discontinuity, Changes = s.Changes,
        MaxGapUs = s.MaxGapUs, LastMediaUs = s.LastMediaUs, MaxMediaUs = s.MaxMediaUs, LastEndUs = s.LastEndUs, Flags = s.Flags,
        Overrun = s.Overrun, Underrun = s.Underrun,
    };

    static string? FindAsset(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "help", "Qt", "assets", name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    static ulong CreateTap(Action<bool, string> check, int sampleRate)
    {
        var rc = Native.vlqt_res_create(Protocol.ResKind.AudioTap, out var id);
        var init = rc == 0 ? Native.vlqt_res_audio_tap_init(id, Channels, sampleRate) : rc;
        check(rc == 0 && init == 0, $"videoaudio: tap {Channels} ch {sampleRate} Hz created ({rc}, {init})");
        return id;
    }

    static double Median(List<double> v)
    {
        if (v.Count == 0)
            return double.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        string? a = FindAsset("qt-help-video-audio.mp4"), b = FindAsset("qt-help-video.mp4");
        check(a != null && b != null, "videoaudio: assets found");
        if (a == null || b == null)
            return;
        var w = Native.vlqt_window_create(640, 360, "VL.Qt video audio test");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var resBuf = new byte[1 << 14];
        float dur = 0;

        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            cmd.Reset();
            check(rc == 0, $"videoaudio: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Frames(int ms, Func<bool>? until = null)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !(until?.Invoke() ?? false))
            {
                pump(1);
                int n = Native.Poll(w, buf);
                foreach (var r in MeasureTest.Parse(buf.AsSpan(0, Math.Max(n, 0)), check))
                    if (r.Handle == Handle && r.Event == Protocol.Ev.Media)
                        dur = r.V[1];
                Native.ResPoll(resBuf);
                Thread.Sleep(10);
            }
        }
        // Frames per wall second of the tap over ms.
        double RateOver(Reader reader, ulong id, int sampleRate, int ms)
        {
            var f0 = reader.Snapshot(id).Frames;
            var t = Stopwatch.StartNew();
            Frames(ms);
            return (reader.Snapshot(id).Frames - f0) / (sampleRate * t.Elapsed.TotalSeconds);
        }

        var tap1 = CreateTap(check, 48000);
        Native.ResReadAudio(tap1, Span<float>.Empty, Channels, out var st0);
        check(st0.flags == 0 && st0.capacity == 24000 && st0.available == 0, $"videoaudio: fresh tap not attached, 500 ms ring (flags {st0.flags}, capacity {st0.capacity})");
        using var reader = new Reader { Id = tap1 };

        cmd.Reset();
        cmd.Create(VideoId, Protocol.Type.Video);
        cmd.SetPlace(VideoId, 0, 0, 640, 360);
        cmd.SetString(VideoId, Protocol.Prop.Source, a);
        cmd.SetBool(VideoId, Protocol.Prop.Loop, true);
        cmd.SetU64(VideoId, Protocol.Prop.AudioTap, tap1);
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        cmd.Reparent(VideoId, 0, 0);
        cmd.Bind(VideoId, Handle, 0);
        Apply("create video with tap, playing");

        // 1. Playback: samples, rate, sync.
        Frames(1000);
        var rate1 = RateOver(reader, tap1, 48000, 3000);
        var s = reader.Snapshot(tap1);
        check(s.Peak > 0.01f, $"videoaudio: samples not silent (peak {s.Peak:F3})");
        check(rate1 > 0.97 && rate1 < 1.03, $"videoaudio: {rate1:F3} s of samples per s of wall time");
        check(s.Flags == (Protocol.AudioTap.Attached | Protocol.AudioTap.Playing), $"videoaudio: flags attached + playing ({s.Flags})");
        var sync = reader.SyncErrors(tap1);
        var syncMedian = Median(sync);
        var syncAbs = sync.Select(Math.Abs).ToList();
        var syncP90 = syncAbs.Count > 0 ? syncAbs.OrderBy(x => x).ElementAt(syncAbs.Count * 9 / 10) : double.NaN;
        check(sync.Count > 20 && Math.Abs(syncMedian) < 5 && syncP90 < 10,
            $"videoaudio: audio due vs video frame at the sink: median {syncMedian:F2} ms, |p90| {syncP90:F2} ms ({sync.Count} samples)");

        // 2. Loop wrap: Qt's audio startTime keeps counting; no discontinuity, no gap.
        Frames(20000, () => dur > 0 && reader.Snapshot(tap1).MaxMediaUs > dur * 1_000_000 + 500_000);
        var wrap = reader.Snapshot(tap1);
        check(dur > 0 && wrap.MaxMediaUs > dur * 1_000_000 + 500_000, $"videoaudio: loop wrap reached (duration {dur:F2} s, media {wrap.MaxMediaUs / 1e6:F2} s)");
        check(wrap.Changes == s.Changes && wrap.Gaps == 0, $"videoaudio: loop wrap without discontinuity ({s.Changes} -> {wrap.Changes}) and gap ({wrap.Gaps}, max {wrap.MaxGapUs} us)");

        // 3. Pause -> play: PLAYING off, then a new segment.
        cmd.SetBool(VideoId, Protocol.Prop.Playing, false);
        Apply("pause");
        Frames(800);
        var paused = reader.Snapshot(tap1);
        check(paused.Flags == Protocol.AudioTap.Attached, $"videoaudio: paused: flags attached only ({paused.Flags})");
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        Apply("play");
        Frames(800);
        var played = reader.Snapshot(tap1);
        check(played.Changes == paused.Changes + 1 && played.Frames > paused.Frames, $"videoaudio: play after pause raises the discontinuity ({paused.Changes} -> {played.Changes})");

        // 4. MSAA window swap (#118): the tap hangs on the player, not on the window.
        var swap = Native.vlqt_window_set_samples(w, 4);
        Frames(300);
        var before = reader.Snapshot(tap1);
        var rateMsaa = RateOver(reader, tap1, 48000, 1500);
        var after = reader.Snapshot(tap1);
        check(swap == 4 && rateMsaa > 0.95 && rateMsaa < 1.05, $"videoaudio: MSAA window swap ({swap}): tap keeps running ({rateMsaa:F3} s/s)");
        check(after.Changes == before.Changes && after.Gaps == before.Gaps, $"videoaudio: MSAA swap without discontinuity or gap ({before.Changes} -> {after.Changes})");

        // 5. Tap switch during playback (C# never sends 0 in between): the old tap stops, the new one runs at its rate.
        var tap2 = CreateTap(check, 44100);
        cmd.SetU64(VideoId, Protocol.Prop.AudioTap, tap2);
        Apply("switch to tap 2 (44.1 kHz) while playing");
        reader.Id = tap2;
        Frames(300);
        var rate2 = RateOver(reader, tap2, 44100, 2000);
        var s2 = reader.Snapshot(tap2);
        Native.ResReadAudio(tap1, Span<float>.Empty, Channels, out var st1);
        var leftover = st1.available;
        Native.ResDropAudio(tap1, 1 << 20, Channels, out _);
        Frames(300);
        Native.ResReadAudio(tap1, Span<float>.Empty, Channels, out st1);
        check(st1.flags == 0 && st1.available == 0, $"videoaudio: old tap detached, no new frames (flags {st1.flags}, {leftover} frames left over, then {st1.available})");
        check(rate2 > 0.97 && rate2 < 1.03 && s2.Peak > 0.01f && s2.Flags == (Protocol.AudioTap.Attached | Protocol.AudioTap.Playing),
            $"videoaudio: tap switch live: {rate2:F3} s/s at 44.1 kHz, peak {s2.Peak:F3}, flags {s2.Flags}");

        // 6. A second Video with the same tap is ignored (one player per tap): still 1 s per s, not 2.
        cmd.Create(Video2Id, Protocol.Type.Video);
        cmd.SetPlace(Video2Id, 0, 0, 160, 90);
        cmd.SetString(Video2Id, Protocol.Prop.Source, a);
        cmd.SetBool(Video2Id, Protocol.Prop.Loop, true);
        cmd.SetU64(Video2Id, Protocol.Prop.AudioTap, tap2);
        cmd.SetBool(Video2Id, Protocol.Prop.Playing, true);
        cmd.Reparent(Video2Id, 0, 1);
        Apply("second video on the same tap (warning, ignored)");
        Frames(500);
        var rateShared = RateOver(reader, tap2, 44100, 1500);
        check(rateShared > 0.95 && rateShared < 1.05, $"videoaudio: second Video on the same tap ignored ({rateShared:F3} s/s)");
        cmd.Destroy(Video2Id);
        Apply("destroy second video");

        // 7. Source change: new segment.
        var src0 = reader.Snapshot(tap2);
        cmd.SetString(VideoId, Protocol.Prop.Source, b);
        Apply("source without audio");
        Frames(800);
        cmd.SetString(VideoId, Protocol.Prop.Source, a);
        Apply("source with audio again");
        Frames(1500);
        var src1 = reader.Snapshot(tap2);
        check(src1.Changes > src0.Changes && src1.Frames > src0.Frames + 44100 / 2, $"videoaudio: source change raises the discontinuity ({src0.Changes} -> {src1.Changes})");

        // 8. Detach (0): flags off, nothing written any more.
        cmd.SetU64(VideoId, Protocol.Prop.AudioTap, 0);
        Apply("tap 0 (Qt output again)");
        Frames(300);
        var det0 = reader.Snapshot(tap2);
        Frames(500);
        var det1 = reader.Snapshot(tap2);
        check(det1.Flags == 0 && det1.Frames == det0.Frames, $"videoaudio: detached: flags {det1.Flags}, {det1.Frames - det0.Frames} frames after detach");

        // 9. Attach again, then DESTROY of the playing element: the element reference goes, the release frees the tap.
        cmd.SetU64(VideoId, Protocol.Prop.AudioTap, tap2);
        Apply("attach tap 2 again");
        var reT = Stopwatch.StartNew();
        Frames(5000, () => reader.Snapshot(tap2).Frames > det1.Frames);
        var re = reader.Snapshot(tap2);
        Console.WriteLine($"INFO videoaudio: first frames after re-attach in {reT.ElapsedMilliseconds} ms; overrun before {det1.Overrun}, after {re.Overrun}; src {src1.Overrun}, shared {src0.Overrun}");
        check(re.Flags == (Protocol.AudioTap.Attached | Protocol.AudioTap.Playing) && re.Frames > det1.Frames, $"videoaudio: re-attached after 0 (flags {re.Flags})");
        cmd.Destroy(VideoId);
        Apply("destroy video with tap");
        reader.Id = 0;
        Native.vlqt_res_release(tap1);
        Native.vlqt_res_release(tap2);
        Frames(100);
        int r1 = Native.ResReadAudio(tap1, Span<float>.Empty, Channels, out _), r2 = Native.ResReadAudio(tap2, Span<float>.Empty, Channels, out _);
        check(r1 == -9 && r2 == -9, $"videoaudio: released taps are freed after DESTROY (read {r1}, {r2})");
        var end = reader.Snapshot(tap2);
        Console.WriteLine($"INFO videoaudio: sync median {syncMedian:F2} ms |p90| {syncP90:F2} ms; rate 48k {rate1:F3}, msaa {rateMsaa:F3}, 44.1k {rate2:F3}; " +
            $"overrun {end.Overrun}, underrun {end.Underrun}, reads {end.Calls}");

        Native.vlqt_window_destroy(w);
        pump(2);
    }
}
