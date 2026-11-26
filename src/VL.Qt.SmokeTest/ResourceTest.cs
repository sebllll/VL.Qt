using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (images package 02): the generic resource manager of QtNative with its test kind. State machine, new content after a failure,
/// stale ids after release, upload budget per pump, release while decoding/uploading, element references (an Image element),
/// vlqt_res_wait (sync decode + upload, others keep going), shutdown with running jobs (10 rounds), poll cap.
/// Ends with running jobs on purpose: the process must end cleanly with busy workers.
/// </summary>
[TestFixture, NonParallelizable]
public static class ResourceTest
{
    private const int Mb = 1 << 20;

    private sealed record Rec(ushort Ev, ulong Id, byte[] Payload);

    /// <summary>Resources.</summary>
    [Test, Category("resources")]
    public static void Resources() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        // A window as in vvvv: uploads run in the pump before the windows render.
        var w = NativeCalls.CreateWindow(320, 200, "VL.Qt resources");
        s.Check(w != SceneHandle.Null, "window " + (w == SceneHandle.Null ? Native.LastError() : ""));
        Native.vlqt_window_show(w);
        var events = new List<Rec>();
        var buf = new byte[64 * 1024];

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
                var id = BitConverter.ToUInt64(buf, at + 8);
                events.Add(new Rec(ev, id, buf.AsSpan(at + 16, len - 8).ToArray()));
                at += (8 + len + 3) & ~3;
            }
        }

        void Frame()
        {
            s.Pump(1);
            Poll();
        }

        byte[] States(ulong id) => events.Where(e => e.Id == id && e.Ev == Protocol.ResourceEventId.State).Select(e => e.Payload[0]).ToArray();
        byte Last(ulong id) => States(id) is { Length: > 0 } s ? s[^1] : (byte)0;
        string Seq(ulong id) => string.Join(",", States(id));

        ulong Create()
        {
            var rc = Native.vlqt_res_create(TestProtocol.ResourceKind.Test, out var id);
            if (rc != 0 || id == 0) s.Check(false, $"create ({rc}, {id:X})");
            return id;
        }

        ulong Start(int decodeMs, long bytes, int flags = 0)
        {
            var id = Create();
            var rc = NativeTest.vlqt_test_res_start(id, decodeMs, bytes, flags);
            if (rc != 0) s.Check(false, $"test start {id:X} ({rc})");
            return id;
        }

        // Pumps (with a little real time for the workers) until all ids are Ready/Failed or the timeout passes.
        bool Settle(int timeoutMs, params ulong[] ids)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                if (ids.All(i => Last(i) is Protocol.ResourceState.Ready or Protocol.ResourceState.Failed))
                    return true;
                Thread.Sleep(1);
            }
            return false;
        }

        Native.vlqt_res_info[] List()
        {
            var n = Native.ResList(Span<Native.vlqt_res_info>.Empty);
            var arr = new Native.vlqt_res_info[Math.Max(n, 1)];
            var m = Native.ResList(arr);
            return arr.Take(Math.Min(m, arr.Length)).ToArray();
        }
        bool Listed(ulong id) => List().Any(r => r.id == id);

        s.Check(Native.vlqt_res_create(999, out _) == Native.Result.BadArgument, "unknown kind rejected (BadArgument)");
        s.Check(Native.vlqt_res_set_budget(0) == Native.Result.BadArgument, "budget 0 rejected (BadArgument)");

        // 1. All states of the test kind, ERROR + INFO, rejected transitions.
        var a = Start(20, 1 * Mb);
        var bad = Start(5, Mb, 1);
        var badUp = Start(5, Mb, 2);
        s.Check(Settle(3000, a, bad, badUp), "test resources settle");
        s.Check(Seq(a) == "1,2,3,4", $"states Pending,Decoding,Uploading,Ready ({Seq(a)})");
        var info = events.FirstOrDefault(e => e.Id == a && e.Ev == Protocol.ResourceEventId.Info);
        s.Check(info != null && BitConverter.ToUInt64(info.Payload, 12) == Mb, "INFO with the uploaded bytes");
        s.Check(events.FindIndex(e => e.Id == a && e.Ev == Protocol.ResourceEventId.Info) < events.FindLastIndex(e => e.Id == a && e.Ev == Protocol.ResourceEventId.State),
            "INFO before STATE Ready");
        s.Check(Seq(bad) == "1,2,5", $"decode failure -> Failed ({Seq(bad)})");
        var err = events.FirstOrDefault(e => e.Id == bad && e.Ev == Protocol.ResourceEventId.Error);
        s.Check(err != null && Encoding.UTF8.GetString(err.Payload).Contains("decode failed"), "ERROR text of the failed decode");
        s.Check(Seq(badUp) == "1,2,3,5", $"upload failure -> Failed ({Seq(badUp)})");

        s.Check(NativeTest.vlqt_test_res_start(a, 0, 0, 0) == 0 && Native.vlqt_res_wait(a, 2000) == Protocol.ResourceState.Ready, "start of a Ready resource = replacement, stays Ready");
        s.Check(NativeTest.vlqt_test_res_start(bad, 5, Mb, 0) == 0 && Native.vlqt_res_wait(bad, 2000) == Protocol.ResourceState.Ready, "new content after a failure: Failed -> Ready");
        var p = Create();
        s.Check(s.Log.Contains("VL.Qt.Resources: #") && s.Log.Contains("Uploading -> Ready"), "transitions logged (debug, VL.Qt.Resources)");

        // 2. Stale id after release: -9 + warning, the reused index belongs to the new resource only.
        foreach (var id in new[] { bad, badUp, p })
            s.Check(Native.vlqt_res_release(id) == 0, "release");
        Frame();
        s.Check(Native.vlqt_res_release(a) == 0, "release a");
        s.Check(Native.vlqt_res_release(a) == Native.Result.UnknownId, "double release -9");
        var before = events.Count;
        Frame();
        s.Check(!Listed(a), "released resource gone after the frame end");
        var e2 = Create();
        s.Check((uint)e2 == (uint)a && e2 != a, $"index reused with a new generation ({a:X} -> {e2:X})");
        s.Check(Native.vlqt_res_release(a) == Native.Result.UnknownId && NativeTest.vlqt_test_res_start(a, 0, 0, 0) == Native.Result.UnknownId && Native.vlqt_res_wait(a, 10) == (int)Native.Result.UnknownId
            , "stale id -> UnknownId everywhere");
        var e2Info = List().FirstOrDefault(r => r.id == e2);
        s.Check(e2Info.state == Protocol.ResourceState.Pending && e2Info.host_refs == 1 && e2Info.element_refs == 0, "new resource untouched by the stale id");
        s.Check(s.Log.Contains("stale resource id"), "stale id logged as warning");
        Frame();
        s.Check(!events.Skip(before).Any(e => e.Id == a), "no event for the released id");
        s.Check(Native.vlqt_res_release(e2) == 0, "release e2");

        // 3. Budget: 10 x 4 MB at 8 MB per frame need >= 5 frames, at most 2 per frame.
        s.Check(Native.vlqt_res_set_budget(8 * Mb) == 0, "set budget 8 MB");
        var batch = Enumerable.Range(0, 10).Select(_ => Start(0, 4 * Mb)).ToArray();
        int frames = 0, maxPerFrame = 0, ready = 0;
        var sw = Stopwatch.StartNew();
        while (ready < 10 && sw.ElapsedMilliseconds < 5000)
        {
            var mark = events.Count;
            Frame();
            var now = events.Skip(mark).Count(e => e.Ev == Protocol.ResourceEventId.State && e.Payload[0] == Protocol.ResourceState.Ready && batch.Contains(e.Id));
            if (now > 0 || ready > 0) frames++;
            ready += now;
            maxPerFrame = Math.Max(maxPerFrame, now);
            Thread.Sleep(1);
        }
        s.Check(ready == 10, $"budget: all 10 Ready ({ready})");
        s.Check(frames >= 5 && maxPerFrame <= 2, $"budget: {frames} frames with uploads (>= 5), at most {maxPerFrame} per frame (<= 2)");
        s.Check(List().Where(r => batch.Contains(r.id)).All(r => r.bytes == 4 * Mb), "list shows 4 MB per resource");
        foreach (var id in batch)
            Native.vlqt_res_release(id);
        Frame();

        // 4. Release while Decoding / Uploading: no event afterwards, freed at a frame end.
        var dec = Start(2000, Mb);
        sw.Restart();
        while (Last(dec) != Protocol.ResourceState.Decoding && sw.ElapsedMilliseconds < 1000) Frame();
        s.Check(Native.vlqt_res_release(dec) == 0, "release while Decoding");
        before = events.Count;
        sw.Restart();
        while (Listed(dec) && sw.ElapsedMilliseconds < 1000) { Frame(); Thread.Sleep(2); }
        s.Check(!Listed(dec) && sw.ElapsedMilliseconds < 500, $"decode cancelled, freed after {sw.ElapsedMilliseconds} ms");
        s.Check(s.Log.Contains("decode result dropped"), "dropped decode result logged");

        var up = Start(0, 40 * Mb);
        sw.Restart();
        while (Last(up) != Protocol.ResourceState.Uploading && sw.ElapsedMilliseconds < 1000) { Frame(); Thread.Sleep(1); }
        Frame();
        var upInfo = List().FirstOrDefault(r => r.id == up);
        s.Check(upInfo.state == Protocol.ResourceState.Uploading && upInfo.bytes > 0 && upInfo.bytes < 40 * Mb, $"upload in stripes ({upInfo.bytes} bytes)");
        s.Check(Native.vlqt_res_release(up) == 0, "release while Uploading");
        var beforeUp = events.Count;
        Frame();
        Frame();
        s.Check(!Listed(up), "uploading resource freed");
        s.Check(!events.Skip(before).Any(e => e.Id == dec) && !events.Skip(beforeUp).Any(e => e.Id == up), "no event after release while Decoding/Uploading");

        // Element references: an Image element holds its resource, the host release alone does not free.
        Native.vlqt_res_create(Protocol.ResourceKind.Image, out var el);
        Native.ImageSetPixels(el, new Native.vlqt_pixel_desc { width = 8, height = 8, format = 7, premultiplied = 1 }, new byte[8 * 8 * 4]);
        s.Check(Native.vlqt_res_wait(el, 2000) == Protocol.ResourceState.Ready, "element test image Ready");
        var cmd = new CommandWriter();
        cmd.Create(1, Protocol.ElementType.Image);
        cmd.SetPlace(1, 0, 0, 40, 40);
        cmd.SetU64(1, Protocol.PropertyId.Image, el);
        cmd.Reparent(1, 0, 0);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "image element applied");
        Frame();
        s.Check(List().FirstOrDefault(r => r.id == el).element_refs == 1, "the element acquired the resource");
        s.Check(Native.vlqt_res_release(el) == 0, "host release with an element ref");
        Frame();
        var elInfo = List().FirstOrDefault(r => r.id == el);
        s.Check(elInfo.id == el && elInfo.host_refs == 0 && elInfo.element_refs == 1, "kept while an element holds it");
        cmd.Clear();
        cmd.Destroy(1);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "image element destroyed");
        Frame();
        Frame();
        s.Check(!Listed(el), "freed after the last element release");

        // 5. vlqt_res_wait: Ready/Failed synchronously; busy workers do not block it, the others keep going.
        var busy = Enumerable.Range(0, 6).Select(_ => Start(400, Mb)).ToArray();
        var quick = Start(10, 16 * Mb); // 16 MB > budget: wait uploads outside the budget
        sw.Restart();
        var st = Native.vlqt_res_wait(quick, 5000);
        s.Check(st == Protocol.ResourceState.Ready && sw.ElapsedMilliseconds < 350, $"wait -> Ready ({st}) in {sw.ElapsedMilliseconds} ms despite busy workers");
        s.Check(Native.vlqt_res_wait(Start(5, Mb, 1), 5000) == Protocol.ResourceState.Failed, "wait -> Failed");
        s.Check(Settle(3000, busy), "the other resources still reach Ready after the waits");
        var slow = Start(3000, Mb);
        Thread.Sleep(50); // in a worker by now (a queued decode would run inside the wait, without timeout)
        sw.Restart();
        st = Native.vlqt_res_wait(slow, 50);
        s.Check(st == Protocol.ResourceState.Decoding && sw.ElapsedMilliseconds < 1000, $"wait timeout returns the current state ({st}, {sw.ElapsedMilliseconds} ms)");
        Native.vlqt_res_release(slow);
        foreach (var id in busy.Append(quick))
            Native.vlqt_res_release(id);

        // Poll with a too small buffer: BufferTooSmall with the required size, records stay.
        var pc = Create();
        var small = new byte[4];
        var tooSmall = NativeCalls.ResPoll(small, out var req);
        s.Check(tooSmall == (int)Native.Result.BufferTooSmall && req >= 16, $"poll cap too small -> BufferTooSmall, required {req} ({tooSmall})");
        Poll();
        s.Check(States(pc).SequenceEqual(new[] { Protocol.ResourceState.Pending }), "records kept after the too small poll");
        Native.vlqt_res_release(pc);
        Frame();

        // 6. Shutdown with running jobs, 10 rounds: no hang, everything freed, old ids stale, restart works.
        var maxShutdown = 0L;
        ulong old = 0;
        for (int round = 0; round < 10; round++)
        {
            var running = Enumerable.Range(0, 8).Select(_ => Start(1000, Mb)).ToArray();
            old = running[0];
            Frame();
            sw.Restart();
            Native.vlqt_res_shutdown();
            maxShutdown = Math.Max(maxShutdown, sw.ElapsedMilliseconds);
            s.Check(List().Length == 0, $"shutdown round {round + 1}: nothing left");
        }
        s.Check(maxShutdown < 500, $"shutdown with running jobs took at most {maxShutdown} ms (< 500)");
        s.Check(s.Log.Contains("shutdown: 8 resource(s) still referenced"), "shutdown leak warning with the count");
        s.Check(Native.vlqt_res_release(old) == Native.Result.UnknownId, "ids from before the shutdown are stale");
        var again = Start(5, Mb);
        s.Check(Settle(2000, again), "manager works again after shutdown");
        Native.vlqt_res_release(again);
        Frame();
        s.Check(List().Length == 0, "no resource left at the end of the test");

        Native.vlqt_scene_destroy(w);
        Frame();
    }

    /// <summary>
    /// Called by ZzLastTest (last fixture): three 10 s decodes left running on purpose, the process must end
    /// cleanly with busy workers. Not at the end of Run: with a single worker (2-core CI runners) they would queue 30 s
    /// in front of every later decode (image-resource).
    /// </summary>
    internal static void LeaveRunningJobs(Scenario s)
    {
        for (int i = 0; i < 3; i++)
        {
            var ok = Native.vlqt_res_create(TestProtocol.ResourceKind.Test, out var id) == 0 && NativeTest.vlqt_test_res_start(id, 10000, Mb, 0) == 0;
            if (!ok) s.Check(false, $"running job {i} for the process end started");
        }
        s.Pump(1);
    }
}
