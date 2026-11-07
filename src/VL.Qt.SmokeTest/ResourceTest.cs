using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using VL.Qt;

/// <summary>
/// #359 (images package 02): the generic resource manager of QtNative with its test kind. State machine incl. rejected
/// transitions, stale ids after release, upload budget per pump, release while decoding/uploading, element references,
/// vlqt_res_wait (sync decode + upload, others keep going), shutdown with running jobs (10 rounds), poll cap.
/// Ends with running jobs on purpose: the process must end cleanly with busy workers.
/// </summary>
static class ResourceTest
{
    const int Mb = 1 << 20;

    sealed record Rec(ushort Ev, ulong Id, byte[] Payload);

    public static void Run(Action<bool, string> check, Action<int> pump, Func<string> log)
    {
        // A window as in vvvv: uploads run in the pump before the windows render.
        var w = Native.vlqt_window_create(320, 200, "VL.Qt resources");
        check(w != IntPtr.Zero, "359: window " + (w == IntPtr.Zero ? Native.LastError() : ""));
        Native.vlqt_window_show(w);
        var events = new List<Rec>();
        var buf = new byte[64 * 1024];

        void Poll()
        {
            var n = Native.ResPoll(buf);
            if (n < 0)
            {
                buf = new byte[-n * 2];
                n = Native.ResPoll(buf);
            }
            for (int at = 0; at < n;)
            {
                var ev = BitConverter.ToUInt16(buf, at);
                var len = BitConverter.ToUInt16(buf, at + 2);
                var id = BitConverter.ToUInt64(buf, at + 4);
                events.Add(new Rec(ev, id, buf.AsSpan(at + 12, len - 8).ToArray()));
                at += (4 + len + 3) & ~3;
            }
        }

        void Frame()
        {
            pump(1);
            Poll();
        }

        byte[] States(ulong id) => events.Where(e => e.Id == id && e.Ev == Protocol.ResEv.State).Select(e => e.Payload[0]).ToArray();
        byte Last(ulong id) => States(id) is { Length: > 0 } s ? s[^1] : (byte)0;
        string Seq(ulong id) => string.Join(",", States(id));

        ulong Create()
        {
            var rc = Native.vlqt_res_create(Protocol.ResKind.Test, out var id);
            if (rc != 0 || id == 0) check(false, $"359: create ({rc}, {id:X})");
            return id;
        }

        ulong Start(int decodeMs, long bytes, int flags = 0)
        {
            var id = Create();
            var rc = Native.vlqt_test_res_start(id, decodeMs, bytes, flags);
            if (rc != 0) check(false, $"359: test start {id:X} ({rc})");
            return id;
        }

        // Pumps (with a little real time for the workers) until all ids are Ready/Failed or the timeout passes.
        bool Settle(int timeoutMs, params ulong[] ids)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                if (ids.All(i => Last(i) is Protocol.ResState.Ready or Protocol.ResState.Failed))
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

        check(Native.vlqt_res_create(999, out _) == -7, "359: unknown kind rejected (-7)");
        check(Native.vlqt_res_set_budget(0) == -7, "359: budget 0 rejected (-7)");

        // 1. All states of the test kind, ERROR + INFO, rejected transitions.
        var a = Start(20, 1 * Mb);
        var bad = Start(5, Mb, 1);
        var badUp = Start(5, Mb, 2);
        check(Settle(3000, a, bad, badUp), "359: test resources settle");
        check(Seq(a) == "1,2,3,4", $"359: states Pending,Decoding,Uploading,Ready ({Seq(a)})");
        var info = events.FirstOrDefault(e => e.Id == a && e.Ev == Protocol.ResEv.Info);
        check(info != null && BitConverter.ToUInt64(info.Payload, 12) == Mb, "359: INFO with the uploaded bytes");
        check(events.FindIndex(e => e.Id == a && e.Ev == Protocol.ResEv.Info) < events.FindLastIndex(e => e.Id == a && e.Ev == Protocol.ResEv.State),
            "359: INFO before STATE Ready");
        check(Seq(bad) == "1,2,5", $"359: decode failure -> Failed ({Seq(bad)})");
        var err = events.FirstOrDefault(e => e.Id == bad && e.Ev == Protocol.ResEv.Error);
        check(err != null && Encoding.UTF8.GetString(err.Payload).Contains("decode failed"), "359: ERROR text of the failed decode");
        check(Seq(badUp) == "1,2,3,5", $"359: upload failure -> Failed ({Seq(badUp)})");

        check(Native.vlqt_test_res_transition(a, Protocol.ResState.Ready, Protocol.ResState.Decoding) == -7, "359: Ready -> Decoding rejected");
        check(Native.vlqt_test_res_transition(a, Protocol.ResState.Pending, Protocol.ResState.Decoding) == -7, "359: wrong from-state rejected");
        check(Native.vlqt_test_res_start(a, 0, 0, 0) == 0 && Native.vlqt_res_wait(a, 2000) == Protocol.ResState.Ready, "359/360: start of a Ready resource = replacement, stays Ready");
        var p = Create();
        check(Native.vlqt_test_res_transition(p, Protocol.ResState.Pending, Protocol.ResState.Ready) == -7, "359: Pending -> Ready rejected");
        check(Native.vlqt_test_res_transition(p, Protocol.ResState.Pending, Protocol.ResState.Released) == -7, "359: Released only by release + frame end");
        check(Native.vlqt_test_res_transition(p, Protocol.ResState.Pending, 99) == -7, "359: unknown state rejected");
        check(Native.vlqt_test_res_transition(p, Protocol.ResState.Pending, Protocol.ResState.Failed) == 0, "359: Pending -> Failed allowed");
        check(Native.vlqt_test_res_transition(p, Protocol.ResState.Failed, Protocol.ResState.Ready) == -7, "359: Failed -> Ready rejected");
        check(log().Contains("invalid transition Ready -> Decoding rejected"), "359: rejected transition logged");
        check(log().Contains("VL.Qt.Resources: #") && log().Contains("Uploading -> Ready"), "359: transitions logged (debug, VL.Qt.Resources)");

        // 2. Stale id after release: -9 + warning, the reused index belongs to the new resource only.
        foreach (var id in new[] { bad, badUp, p })
            check(Native.vlqt_res_release(id) == 0, "359: release");
        Frame();
        check(Native.vlqt_res_release(a) == 0, "359: release a");
        check(Native.vlqt_res_release(a) == -9, "359: double release -9");
        var before = events.Count;
        Frame();
        check(!Listed(a), "359: released resource gone after the frame end");
        var e2 = Create();
        check((uint)e2 == (uint)a && e2 != a, $"359: index reused with a new generation ({a:X} -> {e2:X})");
        check(Native.vlqt_res_release(a) == -9 && Native.vlqt_test_res_start(a, 0, 0, 0) == -9 && Native.vlqt_res_wait(a, 10) == -9
            && Native.vlqt_test_res_transition(a, 1, 5) == -9 && Native.vlqt_test_res_element_ref(a, 1) == -9, "359: stale id -> -9 everywhere");
        var e2Info = List().FirstOrDefault(r => r.id == e2);
        check(e2Info.state == Protocol.ResState.Pending && e2Info.host_refs == 1 && e2Info.element_refs == 0, "359: new resource untouched by the stale id");
        check(log().Contains("stale resource id"), "359: stale id logged as warning");
        Frame();
        check(!events.Skip(before).Any(e => e.Id == a), "359: no event for the released id");
        check(Native.vlqt_res_release(e2) == 0, "359: release e2");

        // 3. Budget: 10 x 4 MB at 8 MB per frame need >= 5 frames, at most 2 per frame.
        check(Native.vlqt_res_set_budget(8 * Mb) == 0, "359: set budget 8 MB");
        var batch = Enumerable.Range(0, 10).Select(_ => Start(0, 4 * Mb)).ToArray();
        int frames = 0, maxPerFrame = 0, ready = 0;
        var sw = Stopwatch.StartNew();
        while (ready < 10 && sw.ElapsedMilliseconds < 5000)
        {
            var mark = events.Count;
            Frame();
            var now = events.Skip(mark).Count(e => e.Ev == Protocol.ResEv.State && e.Payload[0] == Protocol.ResState.Ready && batch.Contains(e.Id));
            if (now > 0 || ready > 0) frames++;
            ready += now;
            maxPerFrame = Math.Max(maxPerFrame, now);
            Thread.Sleep(1);
        }
        check(ready == 10, $"359: budget: all 10 Ready ({ready})");
        check(frames >= 5 && maxPerFrame <= 2, $"359: budget: {frames} frames with uploads (>= 5), at most {maxPerFrame} per frame (<= 2)");
        check(List().Where(r => batch.Contains(r.id)).All(r => r.bytes == 4 * Mb), "359: list shows 4 MB per resource");
        foreach (var id in batch)
            Native.vlqt_res_release(id);
        Frame();

        // 4. Release while Decoding / Uploading: no event afterwards, freed at a frame end.
        var dec = Start(2000, Mb);
        sw.Restart();
        while (Last(dec) != Protocol.ResState.Decoding && sw.ElapsedMilliseconds < 1000) Frame();
        check(Native.vlqt_res_release(dec) == 0, "359: release while Decoding");
        before = events.Count;
        sw.Restart();
        while (Listed(dec) && sw.ElapsedMilliseconds < 1000) { Frame(); Thread.Sleep(2); }
        check(!Listed(dec) && sw.ElapsedMilliseconds < 500, $"359: decode cancelled, freed after {sw.ElapsedMilliseconds} ms");
        check(log().Contains("decode result dropped"), "359: dropped decode result logged");

        var up = Start(0, 40 * Mb);
        sw.Restart();
        while (Last(up) != Protocol.ResState.Uploading && sw.ElapsedMilliseconds < 1000) { Frame(); Thread.Sleep(1); }
        Frame();
        var upInfo = List().FirstOrDefault(r => r.id == up);
        check(upInfo.state == Protocol.ResState.Uploading && upInfo.bytes > 0 && upInfo.bytes < 40 * Mb, $"359: upload in stripes ({upInfo.bytes} bytes)");
        check(Native.vlqt_res_release(up) == 0, "359: release while Uploading");
        var beforeUp = events.Count;
        Frame();
        Frame();
        check(!Listed(up), "359: uploading resource freed");
        check(!events.Skip(before).Any(e => e.Id == dec) && !events.Skip(beforeUp).Any(e => e.Id == up), "359: no event after release while Decoding/Uploading");

        // Element references: the host release alone does not free.
        var el = Start(0, Mb);
        check(Settle(2000, el), "359: element test resource Ready");
        check(Native.vlqt_test_res_element_ref(el, 1) == 0, "359: element acquire");
        check(Native.vlqt_res_release(el) == 0, "359: host release with an element ref");
        Frame();
        var elInfo = List().FirstOrDefault(r => r.id == el);
        check(elInfo.id == el && elInfo.host_refs == 0 && elInfo.element_refs == 1, "359: kept while an element holds it");
        check(Native.vlqt_test_res_element_ref(el, -1) == 0 && Native.vlqt_test_res_element_ref(el, -1) == -9, "359: element release, second one -9");
        Frame();
        check(!Listed(el), "359: freed after the last element release");

        // 5. vlqt_res_wait: Ready/Failed synchronously; busy workers do not block it, the others keep going.
        var busy = Enumerable.Range(0, 6).Select(_ => Start(400, Mb)).ToArray();
        var quick = Start(10, 16 * Mb); // 16 MB > budget: wait uploads outside the budget
        sw.Restart();
        var st = Native.vlqt_res_wait(quick, 5000);
        check(st == Protocol.ResState.Ready && sw.ElapsedMilliseconds < 350, $"359: wait -> Ready ({st}) in {sw.ElapsedMilliseconds} ms despite busy workers");
        check(Native.vlqt_res_wait(Start(5, Mb, 1), 5000) == Protocol.ResState.Failed, "359: wait -> Failed");
        check(Settle(3000, busy), "359: the other resources still reach Ready after the waits");
        var slow = Start(3000, Mb);
        Thread.Sleep(50); // in a worker by now (a queued decode would run inside the wait, without timeout)
        sw.Restart();
        st = Native.vlqt_res_wait(slow, 50);
        check(st == Protocol.ResState.Decoding && sw.ElapsedMilliseconds < 1000, $"359: wait timeout returns the current state ({st}, {sw.ElapsedMilliseconds} ms)");
        Native.vlqt_res_release(slow);
        foreach (var id in busy.Append(quick))
            Native.vlqt_res_release(id);

        // Poll with a too small buffer: -required, records stay.
        var pc = Create();
        var small = new byte[4];
        var req = Native.ResPoll(small);
        check(req <= -16, $"359: poll cap too small -> -required ({req})");
        Poll();
        check(States(pc).SequenceEqual(new[] { Protocol.ResState.Pending }), "359: records kept after the too small poll");
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
            check(List().Length == 0, $"359: shutdown round {round + 1}: nothing left");
        }
        check(maxShutdown < 500, $"359: shutdown with running jobs took at most {maxShutdown} ms (< 500)");
        check(log().Contains("shutdown: 8 resource(s) still referenced"), "359: shutdown leak warning with the count");
        check(Native.vlqt_res_release(old) == -9, "359: ids from before the shutdown are stale");
        var again = Start(5, Mb);
        check(Settle(2000, again), "359: manager works again after shutdown");
        Native.vlqt_res_release(again);
        Frame();
        check(List().Length == 0, "359: no resource left at the end of the test");

        Native.vlqt_window_destroy(w);
        Frame();
    }

    /// <summary>
    /// Called by Program after the last scenario: three 10 s decodes left running on purpose, the process must end
    /// cleanly with busy workers. Not at the end of Run: with a single worker (2-core CI runners) they would queue 30 s
    /// in front of every later decode (image-resource).
    /// </summary>
    public static void LeaveRunningJobs(Action<bool, string> check, Action<int> pump)
    {
        for (int i = 0; i < 3; i++)
        {
            var ok = Native.vlqt_res_create(Protocol.ResKind.Test, out var id) == 0 && Native.vlqt_test_res_start(id, 10000, Mb, 0) == 0;
            if (!ok) check(false, $"359: running job {i} for the process end started");
        }
        pump(1);
    }
}
