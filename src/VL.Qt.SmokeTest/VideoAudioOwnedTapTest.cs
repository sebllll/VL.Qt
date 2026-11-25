using System;
using System.Diagnostics;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
#if WINDOWS
using NAudio.CoreAudioApi;
#endif

// COMP-03: a Video whose audio tap is already attached to another Video must not keep Qt's own AudioOutput (the C# node
// sets Volume 1 whenever it has a tap, so that item would play through the system device while the first one plays
// through VL.Audio). setVideoTap (resources/audio_tap.cpp) mutes it and refuses the prop with ApplyBadValue. The probe
// is the audio session meter of this process (Windows Core Audio): it moves only when Qt itself plays sound. Control: a
// plain Video without a tap must make the meter move, a Video with a tap alone must not.
// Asset: help/Qt/assets/qt-help-video-audio.mp4 (video with an audio track).
[TestFixture, NonParallelizable]
public static class VideoAudioOwnedTapTest
{
    private const uint Video1Id = 1, Video2Id = 2, Video3Id = 3, Handle = 41;

    /// <summary>COMP-03: a second Video on an owned audio tap must not play Qt's sound.</summary>
    [Test, Category("videoaudio-owned")]
    public static void VideoAudioOwnedTap() => Scenario.Run(Run);

#if WINDOWS
    // Peak of all audio sessions of this process on the default render device, 0 without a session.
    private static float ProcessPeak()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        var pid = (uint)Environment.ProcessId;
        float peak = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.GetProcessID == pid)
                peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
        }
        return peak;
    }

    private static void Run(Scenario s)
    {
        string? a = null;
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null && a == null; d = d.Parent)
        {
            var p = System.IO.Path.Combine(d.FullName, "help", "Qt", "assets", "qt-help-video-audio.mp4");
            if (System.IO.File.Exists(p))
                a = p;
        }
        s.Check(a != null, "videoaudio-owned: asset found");
        if (a == null)
            return;
        try
        {
            ProcessPeak();
        }
        catch (Exception e)
        {
            Assert.Ignore("videoaudio-owned: no audio render device: " + e.Message);
        }

        var w = NativeCalls.CreateWindow(640, 360, "VL.Qt audio owned tap test");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var resBuf = new byte[1 << 14];

        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            cmd.Clear();
            s.Check(rc == 0, $"videoaudio-owned: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        // Frames for ms; the highest process peak seen meanwhile.
        float Watch(int ms)
        {
            float peak = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                s.Pump(1);
                NativeCalls.Poll(w, buf);
                NativeCalls.ResPoll(resBuf);
                peak = Math.Max(peak, ProcessPeak());
                System.Threading.Thread.Sleep(10);
            }
            return peak;
        }
        // tapLast: the tap after everything else, so a refused tap (the apply stops at the bad command) still leaves a
        // playing item in the tree.
        void CreateVideo(uint id, ulong tap, float volume, int x, bool tapLast = false)
        {
            cmd.Create(id, Protocol.ElementType.Video);
            cmd.SetPlace(id, x, 0, 200, 112);
            cmd.SetString(id, Protocol.PropertyId.Source, a);
            cmd.SetBool(id, Protocol.PropertyId.Loop, true);
            cmd.SetF32(id, Protocol.PropertyId.Volume, volume);
            if (tap != 0 && !tapLast)
                cmd.SetU64(id, Protocol.PropertyId.AudioTap, tap);
            cmd.SetBool(id, Protocol.PropertyId.Playing, true);
            cmd.Reparent(id, 0, 0);
            if (tap != 0 && tapLast)
                cmd.SetU64(id, Protocol.PropertyId.AudioTap, tap);
        }

        var rc0 = Native.vlqt_res_create(Protocol.ResourceKind.AudioTap, out var tap);
        var init = rc0 == 0 ? Native.vlqt_audio_tap_init(tap, 2, 48000) : rc0;
        s.Check(rc0 == 0 && init == 0, $"videoaudio-owned: tap created ({rc0}, {init})");

        // Control 1: a Video with a tap (the owner) is silent for Qt, the meter stays at 0.
        CreateVideo(Video1Id, tap, 1f, 0);
        Apply("owner: video with tap");
        Watch(1500);
        var owner = Watch(2000);
        s.Info($"videoaudio-owned: owner alone: peak {owner:F4}");
        s.Check(owner < 0.001f, $"videoaudio-owned: a Video with a tap alone does not play Qt sound (peak {owner:F4})");

        // Control 2: a plain Video makes the meter move (the probe works).
        CreateVideo(Video3Id, 0, 1f, 400);
        Apply("control: plain video, Qt output");
        Watch(1000);
        var plain = Watch(2000);
        s.Info($"videoaudio-owned: plain Video: peak {plain:F4}");
        s.Check(plain > 0.001f, $"videoaudio-owned: probe works, a plain Video moves the meter (peak {plain:F4})");
        cmd.Destroy(Video3Id);
        Apply("destroy control");
        Watch(1500);
        var quiet = Watch(1500);
        s.Check(quiet < 0.001f, $"videoaudio-owned: quiet again after the control (peak {quiet:F4})");

        // A second Video on the owned tap, Volume 1 like VideoAudioNode sends: refused (ApplyBadValue) and muted.
        CreateVideo(Video2Id, tap, 1f, 200, tapLast: true);
        var rc2 = Native.Apply(w, cmd.EndFrame());
        cmd.Clear();
        var err2 = rc2 != 0 ? Native.LastError() : "";
        s.Check(rc2 == Native.Result.BadArgument && err2.Contains("already used by another Video"),
            $"videoaudio-owned: the owned tap is refused on the second Video ({rc2}) {err2}");
        Watch(1000);
        var second = Watch(2500);
        s.Info($"videoaudio-owned: second Video on the owned tap: peak {second:F4}");
        s.Check(second < 0.001f, $"videoaudio-owned: the second Video on an owned tap does not play Qt sound (peak {second:F4})");

        cmd.Destroy(Video1Id);
        cmd.Destroy(Video2Id);
        Apply("destroy");
        Watch(200);
        Native.vlqt_res_release(tap);
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
#else
    static void Run(Scenario s) => Assert.Ignore("Windows only (Core Audio session meter)");
#endif
}
