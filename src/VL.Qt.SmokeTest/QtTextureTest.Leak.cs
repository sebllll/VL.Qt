using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// task 1.8 (f), the leak test: many rounds of create / bind / render / rebind / unbind / destroy with changing sizes and
/// formats, a device loss of QtNative's device with scenes bound, and the foreign device released and recreated (unbound
/// first). Afterwards the device reference counts are back at their baseline (QtNative's device counts the scene-graph
/// resources of the scenes until they are destroyed, so it is compared after the destroy), the process handle count does not grow beyond a small
/// tolerance, and the live D3D objects of QtNative's device did not grow (debug layer, where the SDK layers are installed).
/// </summary>
public static partial class QtTextureTest
{
    // 1000 rounds take about 20 s on a development machine; a slow machine stops at the time limit (at least minLeakRounds).
    private const int leakRounds = 1000;
    private const int minLeakRounds = 100;
    private const double leakSeconds = 50;
    // Handles the count may grow by between two measurements (lazily created process state, e.g. of the driver, or handles the finalizer closes late);
    // a drop is no leak (other parts of the process close handles meanwhile). A leak per round grows by hundreds, so the tolerance only absorbs one-offs.
    private const int handleTolerance = 4;
    // Rounds after the baseline at which the handle count is measured again: the check compares the rest of the run with this mark (slope),
    // so a one-off that happens early does not count.
    private const int handleMarkRounds = 100;
    // Live D3D objects of one type (and the device's own reference count) that may differ.
    private const int liveTolerance = 0;

    // Qt's Windows event dispatcher runs short timers on a multimedia timer (timeSetEvent). winmm creates its timer thread
    // (a thread, an I/O completion port, a waitable timer, events: about 8 handles) once per process at the first such timer and
    // keeps it. Qt starts one at a random point during the device losses, which pushed the count past the tolerance once per
    // process (handle types per device loss: one step, then flat). Creating the thread here keeps it out of the baseline.
    private static void WarmUpMultimediaTimer()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var id = timeSetEvent(1, 1, s_timeProc, IntPtr.Zero, timePeriodic);
        System.Threading.Thread.Sleep(20);
        if (id != 0)
            timeKillEvent(id);
    }

    private const uint timePeriodic = 1;
    private delegate void TimeProc(uint id, uint msg, IntPtr user, IntPtr dw1, IntPtr dw2);
    // Kept in a static field: winmm calls it from its timer thread.
    private static readonly TimeProc s_timeProc = (_, _, _, _, _) => { };
    [DllImport("winmm")] private static extern uint timeSetEvent(uint delay, uint resolution, TimeProc proc, IntPtr user, uint flags);
    [DllImport("winmm")] private static extern uint timeKillEvent(uint id);

    private static int Sum(Dictionary<string, int> live)
    {
        int sum = 0;
        foreach (var c in live.Values)
            sum += c;
        return sum;
    }

    private static void Leak(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "leak: create test device"))
            return;
        IntPtr QtDevice() => NativeTest.vlqt_test_device_info(SceneHandle.Null, out var d, out _) == 0 ? d : IntPtr.Zero;
        Dictionary<string, int>? Live() => OperatingSystem.IsWindows() ? D3DDebugLayer.LiveObjects(QtDevice()) : null;
        int QtRefs() => QtDevice() is var d && d != IntPtr.Zero ? NativeTest.vlqt_test_com_ref_count(d) : -1;

        // One round: a scene bound to a texture of size and format 1, rendered, rebound to a texture of size and format 2
        // (a replacing bind), rendered, unbound, destroyed. Returns false when a step failed (first failure is logged).
        string? failure = null;
        void Fail(string what, int rc)
        {
            failure ??= $"{what} ({rc}) {Native.LastError()}";
        }
        bool Round(int i, IntPtr foreignDevice, IntPtr foreignContext)
        {
            var f1 = s_leakFormats[i % 3];
            var f2 = s_leakFormats[(i + 1) % 3];
            int w1 = 16 + i * 7 % 48, h1 = 12 + i * 5 % 40;
            int w2 = 20 + i * 11 % 60, h2 = 14 + i * 3 % 50;
            var scene = Native.vlqt_offscreen_create(w1, h1);
            if (scene == SceneHandle.Null)
            {
                Fail("offscreen_create", 0);
                return false;
            }
            var cmd = new CommandWriter();
            cmd.Reset();
            cmd.ClearColor(0, 0, 0, 0);
            cmd.Create(1, Protocol.ElementType.Rectangle);
            cmd.SetPlace(1, 2, 2, 12, 8);
            cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0, 0, 1);
            cmd.Reparent(1, 0, 0);
            bool ok = Native.Apply(scene, cmd.EndFrame()) == 0;
            IntPtr tex1 = IntPtr.Zero, tex2 = IntPtr.Zero;
            if (ok && NewTarget(foreignDevice, w1, h1, f1.Texture, out tex1, out var handle1))
            {
                int rc = (int)Native.vlqt_offscreen_bind(scene, foreignDevice, handle1, w1, h1, f1.View, 1, out _);
                NativeTest.vlqt_test_close_handle(handle1);
                int rendered = rc == 0 ? Native.OffscreenRenderBatch(foreignContext, new[] { scene }, 0) : rc;
                if (rc != 0 || rendered != 1)
                {
                    Fail($"round {i} first bind/render", rc != 0 ? rc : rendered);
                    ok = false;
                }
                else if (i % 25 == 0)
                {
                    var px = Read(tex1, w1, h1, f1);
                    if (px == null || !Near(Rgba(px, w1, 8, 6, f1), new[] { 255, 0, 0, 255 }, 1))
                    {
                        Fail($"round {i} content", 0);
                        ok = false;
                    }
                }
            }
            else
                ok = false;
            // Size and format change: the second bind replaces the first (a new texture, as QtTexture does on a change).
            if (ok && NewTarget(foreignDevice, w2, h2, f2.Texture, out tex2, out var handle2))
            {
                int rc = (int)Native.vlqt_offscreen_bind(scene, foreignDevice, handle2, w2, h2, f2.View, 1, out _);
                NativeTest.vlqt_test_close_handle(handle2);
                int rendered = rc == 0 ? Native.OffscreenRenderBatch(foreignContext, new[] { scene }, 0) : rc;
                if (rc != 0 || rendered != 1)
                {
                    Fail($"round {i} rebind/render", rc != 0 ? rc : rendered);
                    ok = false;
                }
            }
            else
                ok = false;
            if (Native.vlqt_offscreen_unbind(scene) != 0)
                Fail("unbind", 0);
            if (tex1 != IntPtr.Zero)
                NativeTest.vlqt_test_com_release(tex1);
            if (tex2 != IntPtr.Zero)
                NativeTest.vlqt_test_com_release(tex2);
            Native.vlqt_scene_destroy(scene);
            return ok;
        }

        // Lets finalizers close handles that wrapper objects still hold, then counts. With a reference count, it waits up to about 2 s
        // for handles of threads and timers that close late (a leak stays, a transient drops back), so only a count that stays above the limit is reported.
        int Handles(int reference = int.MaxValue)
        {
            int count = 0;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                s.Pump(2);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                count = NativeTest.vlqt_test_process_handle_count();
                if (reference == int.MaxValue || count - reference <= handleTolerance)
                    break;
                System.Threading.Thread.Sleep(100);
            }
            return count;
        }

        // Warm-up: lazily created state (D3D runtime, driver, Qt's pipelines per format) stays out of the baseline.
        WarmUpMultimediaTimer();
        for (int i = 0; i < 6; i++)
            Round(i, device, context);
        int handles0 = Handles();
        int deviceRefs0 = NativeTest.vlqt_test_com_ref_count(device);
        int qtRefs0 = QtRefs();
        var live0 = Live();
        check(failure == null, $"leak: warm-up rounds ({failure})");

        // 1. Create / bind / render / rebind / unbind / destroy.
        var clock = Stopwatch.StartNew();
        int failedRounds = 0, refMismatches = 0, rounds = 0, handlesMark = -1;
        for (int i = 6; i < 6 + leakRounds && (rounds < minLeakRounds || clock.Elapsed.TotalSeconds < leakSeconds); i++)
        {
            rounds++;
            if (!Round(i, device, context))
                failedRounds++;
            if (NativeTest.vlqt_test_com_ref_count(device) != deviceRefs0)
                refMismatches++;
            if (rounds == handleMarkRounds)
                handlesMark = Handles();
        }
        double seconds = clock.Elapsed.TotalSeconds;
        int handles1 = Handles(handlesMark >= 0 ? handlesMark : handles0);
        int deviceRefs1 = NativeTest.vlqt_test_com_ref_count(device);
        int qtRefs1 = QtRefs();
        check(failedRounds == 0, $"leak: {rounds} rounds with size and format changes all rendered ({failedRounds} failed; {failure})");
        s.Info($"leak: {rounds} rounds in {seconds:F1} s ({seconds * 1000 / rounds:F1} ms per round)");
        check(refMismatches == 0 && deviceRefs1 == deviceRefs0, $"leak: test device references back after every round ({deviceRefs0} -> {deviceRefs1}, {refMismatches} rounds off)");
        check(qtRefs0 > 0 && qtRefs1 == qtRefs0, $"leak: QtNative device references back after {rounds} rounds ({qtRefs0} -> {qtRefs1})");
        // The slope: a leak per round grows between the mark and the end; a constant offset (lazy state) shows up before the mark.
        if (handlesMark >= 0 && rounds > handleMarkRounds)
            check(handles1 - handlesMark <= handleTolerance, $"leak: process handle count did not grow over the last {rounds - handleMarkRounds} of {rounds} rounds ({handles0} -> {handlesMark} -> {handles1}, tolerance {handleTolerance})");
        else
            check(handles1 - handles0 <= handleTolerance, $"leak: process handle count did not grow after {rounds} rounds ({handles0} -> {handles1}, tolerance {handleTolerance})");

        // 2. The foreign device released and recreated, scenes unbound first.
        int foreignRounds = 20;
        int foreignLeft = 0, foreignFailed = 0;
        for (int i = 0; i < foreignRounds; i++)
        {
            if (NativeTest.CreateDevice(0, out var d2, out var c2) != 0)
            {
                foreignFailed++;
                continue;
            }
            if (!Round(i, d2, c2))
                foreignFailed++;
            foreignLeft += Math.Max(0, NativeTest.vlqt_test_d3d11_release_device(d2, c2));
        }
        int qtRefs2 = QtRefs();
        int handles2 = Handles();
        check(foreignFailed == 0 && foreignLeft == 0, $"leak: {foreignRounds} foreign devices released and recreated, no reference left ({foreignFailed} failed rounds, {foreignLeft} references left)");
        check(qtRefs2 == qtRefs0, $"leak: QtNative device references back after the foreign device rounds ({qtRefs0} -> {qtRefs2})");

        // Debug layer: the live objects of QtNative's device per type did not grow (after a device loss it is a new device).
        var live1 = Live();
        if (live0 == null || live1 == null)
            s.Info("leak: ReportLiveDeviceObjects skipped (debug layer not active: SDK layers not installed, or QSG_RHI_DEBUG_LAYER not set before the device was created)");
        else
        {
            var grown = new List<string>();
            foreach (var (type, count) in live1)
                if (count > live0.GetValueOrDefault(type) + liveTolerance)
                    grown.Add($"{type} {live0.GetValueOrDefault(type)} -> {count}");
            s.Info($"leak: live objects of QtNative's device {Sum(live0)} -> {Sum(live1)} in {live1.Count} types");
            check(grown.Count == 0, $"leak: ReportLiveDeviceObjects of QtNative's device: no type grew by more than {liveTolerance} ({string.Join("; ", grown)})");
        }

        // 3. Device loss of QtNative's device with scenes bound (needs VLQT_TEST_D3D11ON12=1: -Only qttexture).
        var scenes = new SceneHandle[3];
        var textures = new IntPtr[3];
        bool lossRan = false;
        int lossRefsFirst = 0, lossRefsLast = 0;
        for (int round = 0; round < 3; round++)
        {
            for (int k = 0; k < 3; k++)
            {
                var f = s_leakFormats[k];
                scenes[k] = Native.vlqt_offscreen_create(32, 24);
                var cmd = new CommandWriter();
                cmd.Reset();
                cmd.ClearColor(0, 0, 0, 0);
                cmd.Create(1, Protocol.ElementType.Rectangle);
                cmd.SetPlace(1, 2, 2, 16, 12);
                cmd.SetColor(1, Protocol.PropertyId.Color, 0, 1, 0, 1);
                cmd.Reparent(1, 0, 0);
                Native.Apply(scenes[k], cmd.EndFrame());
                NewTarget(device, 32, 24, f.Texture, out textures[k], out var handle);
                int rc = (int)Native.vlqt_offscreen_bind(scenes[k], device, handle, 32, 24, f.View, 1, out _);
                NativeTest.vlqt_test_close_handle(handle);
                if (rc != 0)
                    Fail($"device loss round {round} bind {k}", rc);
            }
            int rendered = Native.OffscreenRenderBatch(context, scenes, 0);
            if (rendered != 3)
                Fail($"device loss round {round} first render", rendered);
            var lost = NativeTest.vlqt_test_device_lost(TestProtocol.DeviceLost.Remove);
            if (lost != 0)
            {
                s.Info($"leak: device loss of QtNative's device skipped ({lost}, needs VLQT_TEST_D3D11ON12=1: -Only qttexture)");
                for (int k = 0; k < 3; k++)
                {
                    Native.vlqt_offscreen_unbind(scenes[k]);
                    Native.vlqt_scene_destroy(scenes[k]);
                    NativeTest.vlqt_test_com_release(textures[k]);
                }
                break;
            }
            lossRan = true;
            var sw = Stopwatch.StartNew();
            bool allUnbound = false;
            while (!allUnbound && sw.ElapsedMilliseconds < 5000)
            {
                s.Pump(1);
                allUnbound = Native.vlqt_offscreen_is_bound(scenes[0]) == 0 && Native.vlqt_offscreen_is_bound(scenes[1]) == 0 && Native.vlqt_offscreen_is_bound(scenes[2]) == 0;
            }
            if (!allUnbound)
                Fail($"device loss round {round}: scenes not unbound", 0);
            // The same textures bind again on the new device and render (the scenes kept their elements).
            for (int k = 0; k < 3; k++)
            {
                var f = s_leakFormats[k];
                NewTarget(device, 32, 24, f.Texture, out var fresh, out var handle);
                int rc = (int)Native.vlqt_offscreen_bind(scenes[k], device, handle, 32, 24, f.View, 1, out _);
                NativeTest.vlqt_test_close_handle(handle);
                rendered = rc == 0 ? 1 : 0;
                if (rc != 0)
                    Fail($"device loss round {round} rebind {k}", rc);
                NativeTest.vlqt_test_com_release(textures[k]);
                textures[k] = fresh;
            }
            rendered = Native.OffscreenRenderBatch(context, scenes, 0);
            var px = Read(textures[2], 32, 24, s_float);
            if (rendered != 3 || px == null || !Near(Rgba(px, 32, 8, 8, s_float), new[] { 0, 255, 0, 255 }, 1))
                Fail($"device loss round {round}: rebind renders", rendered);
            for (int k = 0; k < 3; k++)
            {
                Native.vlqt_offscreen_unbind(scenes[k]);
                Native.vlqt_scene_destroy(scenes[k]);
                NativeTest.vlqt_test_com_release(textures[k]);
            }
            s.Pump(2);
            // Every device loss makes a new QtNative device: its count is compared between the rounds on new devices.
            if (round == 0)
                lossRefsFirst = QtRefs();
            lossRefsLast = QtRefs();
        }
        if (lossRan)
        {
            check(failure == null, $"leak: three device losses of QtNative's device with three scenes bound, rebind renders ({failure})");
            check(lossRefsFirst > 0 && lossRefsLast == lossRefsFirst, $"leak: QtNative device references the same after each device loss ({lossRefsFirst} -> {lossRefsLast})");
            check(NativeTest.vlqt_test_com_ref_count(device) == deviceRefs0, $"leak: test device references unchanged by the device losses ({deviceRefs0} -> {NativeTest.vlqt_test_com_ref_count(device)})");
        }
        else
            check(failure == null, $"leak: ({failure})");

        // 4. Final numbers.
        // Against the higher of baseline and end of the rounds: a few handles of idle state close during the rounds and come back with the
        // first foreign device (e.g. 714 -> 711 -> 717), so the end of the rounds alone is a low point. The foreign devices and device losses
        // add 23 devices, a leak per device would grow beyond the tolerance.
        int handlesRef = Math.Max(handles0, handles1);
        int handles3 = Handles(handlesRef);
        s.Info($"leak: handle count {handles0} (baseline) -> {handles1} (rounds) -> {handles2} (foreign devices) -> {handles3} (device losses)");
        check(handles3 - handlesRef <= handleTolerance, $"leak: process handle count did not grow at the end ({handlesRef} -> {handles3}, tolerance {handleTolerance})");
        s.Info($"leak: device references: test device {deviceRefs0} -> {NativeTest.vlqt_test_com_ref_count(device)}, QtNative {qtRefs0} -> {qtRefs1} (rounds) -> {qtRefs2} (foreign devices)");

        check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "leak: test device freed");
    }
}
