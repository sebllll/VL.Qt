using System;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// F8 in vvvv with a playing Video [Qt] (with audio) hung for good in Renderer.Dispose -> Native.Apply (DESTROY of
// the video during playback). Scenario: video with an audio track plays, then the element is destroyed by an apply
// (like Reconciler.Clear), then the window is destroyed. A watchdog ends the process if a step hangs.
// Asset: help/Qt/assets/qt-help-video-audio.mp4 (help video with an audio track).
[TestFixture, NonParallelizable]
public static class VideoStopTest
{
    private const string Asset = "qt-help-video-audio.mp4";
    private const uint Handle = 31;
    private const int HangMs = 10000;

    private static string? FindAsset(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "help", "Qt", "assets", name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    /// <summary>Stopping videos.</summary>
    [Test, Category("videostop")]
    public static void VideoStop() => Scenario.Run(s => Run(s));

    /// <summary>Quick repro of #130 with 5 rounds.</summary>
    [Test, Explicit("quick repro of #130 with 5 rounds"), Category("video-stop-repro")]
    public static void VideoStopRepro() => Scenario.Run(s => Run(s, 5));

    private static void Run(Scenario s, int rounds = 3)
    {
        var a = FindAsset(Asset);
        s.Check(a != null, "test video with audio found");
        if (a == null)
            return;
        for (int i = 0; i < rounds; i++)
        {
            Round(s, a, i, destroyWindowOnly: false);
            Round(s, a, i, destroyWindowOnly: true);
        }
    }

    // destroyWindowOnly: no DESTROY of the element, the window goes with the playing video still in it.
    private static void Round(Scenario s, string a, int round, bool destroyWindowOnly)
    {
        var tag = $"round {round}{(destroyWindowOnly ? " (window only)" : "")}";
        var w = NativeCalls.CreateWindow(400, 300, "VL.Qt video stop test");
        s.Check(w != SceneHandle.Null, $"{tag}: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        float pos = 0, dur = 0;
        const uint Id = 1;

        void Frame()
        {
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            foreach (var r in MeasureTest.Parse(buf.AsSpan(0, Math.Max(n, 0)), s.Check))
                if (r.Handle == Handle && r.Event == Protocol.EventId.Media) { pos = r.V[0]; dur = r.V[1]; }
            Thread.Sleep(15);
        }

        cmd.Reset();
        cmd.Create(Id, Protocol.ElementType.Video);
        cmd.SetPlace(Id, 0, 0, 320, 180);
        cmd.SetString(Id, Protocol.PropertyId.Source, a);
        cmd.SetBool(Id, Protocol.PropertyId.Loop, true);
        cmd.SetBool(Id, Protocol.PropertyId.Playing, true);
        cmd.Reparent(Id, 0, 0);
        cmd.Bind(Id, Handle);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"{tag}: create playing video");
        cmd.Reset();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !(dur > 0 && pos > 0.3f))
            Frame();
        s.Check(dur > 0 && pos > 0.3f, $"{tag}: video with audio plays (pos {pos:F2}, dur {dur:F2})");

        using var done = new ManualResetEventSlim();
        var step = "destroy video (apply)";
        var watchdog = new Thread(() =>
        {
            if (done.Wait(HangMs))
                return;
            Console.WriteLine($"FAIL {tag}: hang > {HangMs} ms in '{step}'");
            Console.Out.Flush();
            Environment.Exit(3);
        }) { IsBackground = true };
        watchdog.Start();
        var t = Stopwatch.StartNew();
        if (!destroyWindowOnly)
        {
            cmd.Destroy(Id);
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"{tag}: destroy video ({Native.LastError()})");
            cmd.Reset();
        }
        step = "window_destroy";
        Native.vlqt_scene_destroy(w);
        done.Set();
        s.Check(t.ElapsedMilliseconds < 2000, $"{tag}: destroy without hang ({t.ElapsedMilliseconds} ms)");
        s.Pump(2);
    }
}
