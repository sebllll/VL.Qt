using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (images package 08): stress test of the resource manager. Runs <c>seconds</c> with a fixed, logged seed and mixes
/// load/release (file and pixels), stale ids after release, image elements created/destroyed/re-pointed, a second window
/// opened/closed and a simulated hotswap (all handles dropped and recreated). At the end vlqt_res_list must be empty, no
/// leak warning in the log, no crash, no hang (watchdog). Only runs when named explicitly (--only resource-stress),
/// also the target of the ASan/TSan runs under Linux.
/// One System.Random(seed) drives everything on the main thread: the resource API is main-thread only (vlqt.h), so there
/// is deliberately no background thread calling vlqt_res_* (a stray call is only guarded against table corruption).
/// Plain net10.0 (no System.Drawing): runs on Windows and Linux alike.
/// </summary>
[TestFixture, NonParallelizable]
public static class ResourceStressTest
{
    public const int DefaultSeconds = 60;
    private const int MaxLive = 48, MaxStale = 256;
    private const uint FirstElement = 1000;

    private sealed record Elem(uint Id, SceneHandle Win);

    /// <summary>Random resource actions for StressSeconds (default 60 s) with StressSeed (sanitizer runs).</summary>
    [Test, Explicit("s by default; parameters StressSeconds / StressSeed"), Category("resource-stress")]
    public static void ResourceStress() => Scenario.Run(s => Run(s, SmokeParameters.StressSeconds, SmokeParameters.StressSeed));

    private static void Run(Scenario s, int seconds, int seed)
    {
        s.Info($"resource-stress {seconds} s, seed {seed} (repeat with --stress-seed {seed})");
        var dir = Path.Combine(s.OutDir, "resource-stress");
        Directory.CreateDirectory(dir);
        var rng = new Random(seed);
        var logStart = s.Log.Length;
        var sw = Stopwatch.StartNew();

        // Watchdog: the run (incl. cleanup) must end within seconds + 30 s, else report the last action and abort.
        var lastAction = "start";
        var done = false;
        var watchdog = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                if (sw.Elapsed.TotalSeconds > seconds + 30)
                {
                    Console.Error.WriteLine($"FAIL 365: resource-stress hangs (> {seconds + 30} s), seed {seed}, last action: {Volatile.Read(ref lastAction)}");
                    Environment.Exit(3);
                }
                Thread.Sleep(200);
            }
        }) { IsBackground = true, Name = "resource-stress watchdog" };
        watchdog.Start();

        // Test images: tiny to 4K (the 4K JPG keeps a worker busy long enough to release during the decode).
        var files = new List<(string path, int weight)>();
        foreach (var (name, w, h, weight) in new[] { ("s64.png", 64, 48, 4), ("m256.jpg", 256, 256, 4), ("m640.png", 640, 480, 3), ("l1080.png", 1920, 1080, 2), ("xl4k.jpg", 3840, 2160, 1) })
        {
            var path = Path.Combine(dir, name);
            s.Check(NativeTest.vlqt_test_write_image(path, w, h, 90) == 0, $"test image {name} written");
            files.Add((path, weight));
        }
        var broken = Path.Combine(dir, "broken.png");
        File.WriteAllBytes(broken, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8]);
        var missing = Path.Combine(dir, "does-not-exist.png");
        var pixelBuf = new byte[1920 * 1080 * 4];
        rng.NextBytes(pixelBuf);

        long frame = 0;
        var counts = new SortedDictionary<string, int>();
        var violations = new List<string>();
        void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
        void Violation(string what)
        {
            Count("violation");
            if (violations.Count < 10) violations.Add($"{what} [frame {frame}, last action {lastAction}]");
        }

        var live = new List<ulong>();
        var stale = new List<(ulong id, long frame)>();
        var elements = new List<Elem>();
        var windows = new List<SceneHandle>();
        uint nextElement = FirstElement;
        var cmd = new CommandWriter();
        var buf = new byte[256 * 1024];
        var states = new int[8];

        void Act(string what)
        {
            lastAction = what;
            Count(what);
        }

        void Poll()
        {
            var n = NativeCalls.ResPoll(buf, out var required);
            if (n == (int)Native.Result.BufferTooSmall)
            {
                buf = new byte[required * 2];
                n = NativeCalls.ResPoll(buf);
            }
            for (int at = 0; at < n;)
            {
                var ev = BitConverter.ToUInt16(buf, at);
                var len = (int)BitConverter.ToUInt32(buf, at + 4);
                if (ev == Protocol.ResourceEventId.State && len >= 9)
                    states[buf[at + 16] & 7]++;
                at += (8 + len + 3) & ~3;
            }
        }

        void Frame()
        {
            frame++;
            s.Pump(1);
            Poll();
        }

        SceneHandle OpenWindow(string title, float x)
        {
            var w = Native.vlqt_window_create(x, 60, 320, 200, title, 0);
            if (w == SceneHandle.Null)
            {
                Violation($"window {title}: {Native.LastError()}");
                return SceneHandle.Null;
            }
            Native.vlqt_window_show(w);
            windows.Add(w);
            return w;
        }

        void Apply(SceneHandle w, string what, params Native.Result[] okCodes)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            if (rc != 0 && !okCodes.Contains(rc)) Violation($"apply {what} -> {rc} ({Native.LastError()})");
            else if (rc != 0) Count("apply " + what + " rejected");
        }

        ulong PickLive() => live.Count == 0 ? 0 : live[rng.Next(live.Count)];
        ulong PickAnyId()
        {
            var r = rng.Next(100);
            if (r < 8 && stale.Count > 0) return stale[rng.Next(stale.Count)].id; // an element on a stale id: BadArgument, nothing drawn
            if (r < 12) return 0;
            return PickLive();
        }

        void Forget(ulong id)
        {
            live.Remove(id);
            stale.Add((id, frame));
            if (stale.Count > MaxStale) stale.RemoveAt(rng.Next(stale.Count));
        }

        void LoadFileInto(ulong id)
        {
            string path;
            var r = rng.Next(100);
            if (r < 4) path = missing;
            else if (r < 8) path = broken;
            else
            {
                var pick = rng.Next(files.Sum(f => f.weight));
                path = files.First(f => (pick -= f.weight) < 0).path;
            }
            var rc = Native.vlqt_image_load(id, path, 0);
            if (rc != 0) Violation($"load_image {Path.GetFileName(path)} on a live id -> {rc}");
        }

        void SetPixelsInto(ulong id)
        {
            int w, h;
            if (rng.Next(100) < 5) (w, h) = (1920, 1080);
            else (w, h) = (rng.Next(1, 400), rng.Next(1, 300));
            var desc = new Native.vlqt_pixel_desc { width = w, height = h, row_bytes = 0, format = 7, premultiplied = 0 };
            var rc = Native.ImageSetPixels(id, desc, pixelBuf.AsSpan(0, w * h * 4));
            if (rc != 0) Violation($"set_pixels {w}x{h} on a live id -> {rc}");
        }

        void NewResource()
        {
            if (live.Count >= MaxLive) return;
            var rc = Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id);
            if (rc != 0 || id == 0) { Violation($"res_create -> {rc}"); return; }
            live.Add(id);
            if (rng.Next(100) < 55)
            {
                Act("load file");
                LoadFileInto(id);
            }
            else
            {
                Act("set pixels");
                SetPixelsInto(id);
            }
        }

        void Release()
        {
            if (live.Count == 0) return;
            Act("release");
            var id = live[rng.Next(live.Count)];
            var rc = Native.vlqt_res_release(id);
            if (rc != 0) Violation($"release of a live id -> {rc}");
            Forget(id);
        }

        void Replace()
        {
            if (live.Count == 0) return;
            var id = live[rng.Next(live.Count)];
            if (rng.Next(2) == 0) { Act("replace file"); LoadFileInto(id); }
            else { Act("replace pixels"); SetPixelsInto(id); }
        }

        // Any vlqt_res_* call with an id released in an earlier frame may only report an error. Ids released in this very
        // frame (before the pump frees them) must not crash either, but their result is not asserted.
        void StaleCall()
        {
            if (stale.Count == 0) return;
            var (id, since) = stale[rng.Next(stale.Count)];
            var strict = since < frame;
            Act("stale call");
            var aliveBefore = ReleasedButAlive(id);
            string name;
            int rc;
            switch (rng.Next(6))
            {
                case 0: name = "release"; rc = (int)Native.vlqt_res_release(id); break;
                case 1: name = "load_image"; rc = (int)Native.vlqt_image_load(id, files[rng.Next(files.Count)].path, 0); break;
                case 2: name = "set_pixels"; rc = (int)Native.ImageSetPixels(id, new Native.vlqt_pixel_desc { width = 8, height = 8, format = 7 }, pixelBuf.AsSpan(0, 8 * 8 * 4)); break;
                case 3: name = "wait"; rc = Native.vlqt_res_wait(id, 0); break;
                case 4: name = "read_pixels"; rc = NativeCalls.ReadPixels(id, new byte[64], out _, out _); break;
                default: name = "snapshot"; rc = windows.Count > 0 ? (int)Native.vlqt_snapshot_start(id, windows[0], FirstElement, 1) : (int)Native.Result.UnknownId; break;
            }
            // Also while the id is still in the table (element ref or running decode): vlqt.h promises UnknownId for every call.
            if (strict && aliveBefore)
                Count("stale call, id still in the table");
            if (strict && rc >= 0)
                Violation($"stale id {id:X}: {name} succeeded ({rc}{(aliveBefore ? ", still in the table" : "")})");
        }

        bool ReleasedButAlive(ulong id)
        {
            var all = new Native.vlqt_res_info[Math.Max(Native.ResList(Span<Native.vlqt_res_info>.Empty), 1)];
            var n = Math.Min(Native.ResList(all), all.Length);
            return all.Take(n).Any(r => r.id == id && r.host_refs == 0);
        }

        void NewElement()
        {
            if (windows.Count == 0 || elements.Count > 80) return;
            Act("element create");
            var w = windows[rng.Next(windows.Count)];
            var e = new Elem(nextElement++, w);
            cmd.Reset();
            cmd.Create(e.Id, Protocol.ElementType.Image);
            cmd.SetPlace(e.Id, rng.Next(0, 250), rng.Next(0, 150), rng.Next(10, 120), rng.Next(10, 100));
            var img = PickAnyId();
            if (img != 0) cmd.SetU64(e.Id, Protocol.PropertyId.Image, img);
            cmd.Reparent(e.Id, 0, 0);
            Apply(w, "element create", Native.Result.BadArgument); // the stale id of the property
            elements.Add(e);
        }

        void Retarget()
        {
            if (elements.Count == 0) return;
            Act("element retarget");
            var e = elements[rng.Next(elements.Count)];
            cmd.Reset();
            cmd.SetU64(e.Id, Protocol.PropertyId.Image, PickAnyId());
            Apply(e.Win, "element retarget", Native.Result.BadArgument);
        }

        void DestroyElement()
        {
            if (elements.Count == 0) return;
            Act("element destroy");
            var i = rng.Next(elements.Count);
            var e = elements[i];
            elements.RemoveAt(i);
            cmd.Reset();
            cmd.Destroy(e.Id);
            Apply(e.Win, "element destroy");
        }

        void ToggleSecondWindow()
        {
            if (windows.Count < 2)
            {
                Act("window open");
                var w = OpenWindow("VL.Qt resource-stress 2", 420);
                if (w == SceneHandle.Null) return;
                for (int i = rng.Next(1, 6); i > 0; i--) NewElement();
            }
            else
            {
                Act("window close");
                var w = windows[1];
                windows.RemoveAt(1);
                elements.RemoveAll(e => e.Win == w);
                Native.vlqt_scene_destroy(w);
            }
        }

        // Hotswap as in vvvv: all elements, windows and resources gone at once, then everything anew.
        void Hotswap()
        {
            Act("hotswap");
            foreach (var w in windows.Skip(1)) Native.vlqt_scene_destroy(w);
            windows.RemoveRange(1, windows.Count - 1);
            cmd.Reset();
            foreach (var e in elements.Where(e => e.Win == windows[0])) cmd.Destroy(e.Id);
            Apply(windows[0], "hotswap destroy elements");
            elements.Clear();
            foreach (var id in live.ToArray())
            {
                var rc = Native.vlqt_res_release(id);
                if (rc != 0) Violation($"hotswap release -> {rc}");
                Forget(id);
            }
            for (int i = rng.Next(0, 3); i > 0; i--) Frame();
            for (int i = rng.Next(3, 10); i > 0; i--) NewResource();
            for (int i = rng.Next(1, 6); i > 0; i--) NewElement();
        }

        // --- run ---
        var w1 = OpenWindow("VL.Qt resource-stress", 60);
        s.Check(w1 != SceneHandle.Null, "window " + (w1 == SceneHandle.Null ? Native.LastError() : ""));
        if (w1 == SceneHandle.Null)
        {
            Volatile.Write(ref done, true);
            return;
        }
        var leftover = Native.ResList(new Native.vlqt_res_info[64]);
        s.Check(leftover == 0, $"no resources left from earlier scenarios ({leftover})");
        for (int i = 0; i < 5; i++) Frame();

        var forced = new[] { seconds / 3.0, seconds * 2 / 3.0 };
        var forcedDone = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (forcedDone < forced.Length && sw.Elapsed.TotalSeconds >= forced[forcedDone])
            {
                forcedDone++;
                Hotswap();
            }
            for (int n = rng.Next(1, 4); n > 0; n--)
            {
                var r = rng.Next(1000);
                if (r < 230) NewResource();
                else if (r < 400) Release();
                else if (r < 520) Replace();
                else if (r < 700) StaleCall();
                else if (r < 800) NewElement();
                else if (r < 900) Retarget();
                else if (r < 965) DestroyElement();
                else if (r < 985) ToggleSecondWindow();
                else if (r < 988) Hotswap();
                else Act("idle");
            }
            Frame();
            if (rng.Next(4) == 0) Thread.Sleep(1); // give the workers real time
        }
        var runSeconds = sw.Elapsed.TotalSeconds;

        // --- end: elements, windows and resources go, the table must run empty ---
        lastAction = "cleanup";
        foreach (var w in windows.Skip(1)) Native.vlqt_scene_destroy(w);
        windows.RemoveRange(1, windows.Count - 1);
        cmd.Reset();
        foreach (var e in elements.Where(e => e.Win == windows[0])) cmd.Destroy(e.Id);
        Apply(windows[0], "cleanup destroy elements");
        Frame();
        foreach (var id in live.ToArray())
        {
            if (Native.vlqt_res_release(id) != 0) Violation("cleanup release of a live id");
            Forget(id);
        }
        Native.vlqt_scene_destroy(windows[0]);
        windows.Clear();
        var list = new Native.vlqt_res_info[64];
        var left = -1;
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed.TotalSeconds < 15)
        {
            Frame();
            left = Native.ResList(list);
            if (left == 0) break;
            Thread.Sleep(2);
        }
        Volatile.Write(ref done, true);

        var text = s.Log[logStart..];
        var leakWarnings = text.Split('\n').Where(l => l.Contains("still referenced") || l.Contains("leaking ids")).ToList();
        var total = counts.Where(c => c.Key != "violation" && c.Key != "stale call, id still in the table" && !c.Key.StartsWith("apply")).Sum(c => c.Value);
        s.Info($"seed {seed}, {runSeconds:F1} s, {frame} frames, {total} actions: " + string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        s.Info($"resource states reported (Pending {states[1]}, Decoding {states[2]}, Uploading {states[3]}, Ready {states[4]}, Failed {states[5]}), cleanup {settle.ElapsedMilliseconds} ms");
        foreach (var v in violations) s.Info("violation: " + v);

        s.Check(counts.GetValueOrDefault("violation") == 0,
            $"all calls with live ids ok, stale ids only errors, applies ok (seed {seed}, {counts.GetValueOrDefault("violation")} violations, first: {violations.FirstOrDefault()})");
        s.Check(left == 0, $"vlqt_res_list empty at the end ({left} left after {settle.ElapsedMilliseconds} ms, seed {seed})");
        s.Check(leakWarnings.Count == 0, $"no leak warning in the log ({leakWarnings.FirstOrDefault()})");
        s.Check(total > 100 && counts.GetValueOrDefault("hotswap") >= 1 && counts.GetValueOrDefault("window open") >= 1,
            $"the run covered hotswap/second window ({total} actions, seed {seed})");
    }
}
