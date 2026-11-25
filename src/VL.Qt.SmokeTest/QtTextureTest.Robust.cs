using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// task 5.4, robustness of the Qt render thread: the thread mode in use, WARP as the foreign device (the fallback
/// that waits for the submission), QtNative's device removed while the foreign context has a queued wait for a value
/// that may never be signalled, and a window next to a bound scene when the device is lost. The device loss parts need
/// VLQT_TEST_D3D11ON12=1 (set for "-Only qttexture"); the WARP part needs QtNative's device not to be in use by a window
/// (also "-Only qttexture"); each says so and skips otherwise. A hang in QtNative stops the run through <see cref="Watchdog"/>.
/// </summary>
public static partial class QtTextureTest
{
    // Hang guard for code that waits on the render thread or on the GPU: after the limit the run ends with a FAIL line
    // (a deadlock would otherwise only end at the SmokeTest time limit, without telling where).
    internal sealed class Watchdog : IDisposable
    {
        private readonly Timer _timer;

        public Watchdog(string what, int seconds)
            => _timer = new Timer(_ =>
            {
                Console.WriteLine($"FAIL {what}: no progress for {seconds} s (hang), run aborted");
                Console.Out.Flush();
                Environment.Exit(1);
            }, null, seconds * 1000, Timeout.Infinite);

        public void Dispose() => _timer.Dispose();
    }

    // QtNative's last error text for the message of a failed check; nothing when the call went well (the text is not cleared).
    private static string ErrIf(bool failed) => failed ? Native.LastError() : "";

    // The thread count QtNative uses: the run's RenderThreads (QtHost sets it), else 1 (render-texture.md, "Qt render thread").
    private static int ExpectedThreads => SmokeParameters.RenderThreads ?? 1;

    // The pool size may change at any time and applies to the scenes created afterwards (E7); the mode the suite runs in
    // is the log line of its first scene (RunMode). Here the export takes the count in use, another one and an out-of-range
    // one, and the count in use is set again for the scenes this run creates later.
    private static void ThreadMode(Scenario s)
    {
        int expected = ExpectedThreads;
        int other = expected == 0 ? 1 : 0;
        bool same = Native.vlqt_offscreen_set_render_threads(expected) == Native.Result.Ok;
        bool otherOk = Native.vlqt_offscreen_set_render_threads(other) == Native.Result.Ok;
        bool restored = Native.vlqt_offscreen_set_render_threads(expected) == Native.Result.Ok;
        bool range = Native.vlqt_offscreen_set_render_threads(17) == Native.Result.BadArgument
            && Native.vlqt_offscreen_set_render_threads(-1) == Native.Result.BadArgument;
        s.Check(same && otherOk && restored && range, $"mode: offscreen scenes render on {(expected == 0 ? "the main thread" : $"{expected} Qt render thread(s)")} (count in use {(same ? "accepted" : "refused")}, other {(otherOk ? "accepted" : "refused")}, restored {(restored ? "yes" : "no")}, 17 and -1 {(range ? "refused" : "accepted")})");
        s.Info($"mode: {(expected == 0 ? "main thread" : $"{expected} Qt render thread(s)")} (RenderThreads={SmokeParameters.RenderThreads?.ToString() ?? "unset"})");
    }

    // (4) QtNative's device moves to WARP and the scene renders against a software foreign device: 10 runs of three frames
    // each (the fallback waits on the main thread for the render thread's signal; a deadlock showed in 9 of 10 runs without it).
    private static void WarpRuns(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 64, h = 48, runs = 10;
        if (!check(NativeTest.CreateDevice(0, out var hw, out var hwCtx) == 0, "warp: create device on QtNative's adapter"))
            return;
        if (!check(NativeTest.CreateDevice(1, out var warp, out var warpCtx) == 0, "warp: create WARP device"))
        {
            NativeTest.vlqt_test_d3d11_release_device(hw, hwCtx);
            return;
        }
        NewTarget(warp, w, h, B8G8R8A8Typeless, out var warpTex, out var warpHandle);
        NewTarget(hw, w, h, B8G8R8A8Typeless, out var hwTex, out var hwHandle);
        var colours = new[] { (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f) };
        var expected = new[] { new[] { 255, 0, 0, 255 }, new[] { 0, 255, 0, 255 }, new[] { 0, 0, 255, 255 } };
        int failedRuns = 0;
        bool skipped = false;
        string? first = null;
        var clock = Stopwatch.StartNew();
        for (int run = 0; run < runs && !skipped; run++)
        {
            var scene = NewScene(w, h, (0, 0, 0, 0), new[] { new Box(0, 0, w, h, colours[0].Item1, colours[0].Item2, colours[0].Item3, 1) }, s, $"warp run {run}");
            if (scene == SceneHandle.Null)
            {
                failedRuns++;
                continue;
            }
            NewTarget(warp, w, h, B8G8R8A8Typeless, out var tex, out var handle);
            int rc = (int)Native.vlqt_offscreen_bind(scene, warp, handle, w, h, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(handle);
            if (rc == (int)Native.Result.AdapterMismatch)
            {
                // A window has rendered on QtNative's device: it cannot move to WARP (the qttexture run of its own: -Only qttexture).
                s.Info("warp: skipped, a window renders on QtNative's device (-Only qttexture checks it)");
                skipped = true;
            }
            else
            {
                bool ok = rc == 0;
                string got = "";
                using (new Watchdog($"warp run {run}", 30))
                    for (int frame = 0; ok && frame < 3; frame++)
                    {
                        if (frame > 0)
                            Recolor(scene, 1, colours[frame].Item1, colours[frame].Item2, colours[frame].Item3, 1, s, $"warp run {run}");
                        int rendered = Native.OffscreenRenderBatch(warpCtx, new[] { scene }, 0);
                        var px = rendered == 1 ? Read(tex, w, h, s_bgra) : null;
                        got = px == null ? $"rendered {rendered} {Native.LastError()}" : Str(Rgba(px, w, w / 2, h / 2, s_bgra));
                        ok = px != null && Near(Rgba(px, w, w / 2, h / 2, s_bgra), expected[frame], 1);
                    }
                if (!ok)
                {
                    failedRuns++;
                    first ??= $"run {run}: bind {rc}, got {got}";
                }
            }
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_scene_destroy(scene);
            NativeTest.vlqt_test_com_release(tex);
        }
        if (!skipped)
        {
            check(failedRuns == 0, $"warp: {runs} runs of three frames on a WARP foreign device render correctly ({failedRuns} failed{(first == null ? "" : ", first " + first)})");
            s.Info($"warp: {runs} runs in {clock.Elapsed.TotalMilliseconds:F0} ms");
            // QtNative moves back to the hardware adapter, as later parts of the suite expect.
            s.Pump(2);
            var scene = RectScene(w, h, s, "warp: back on the hardware adapter");
            int rc = scene == SceneHandle.Null ? (int)Native.Result.NullHandle : (int)Native.vlqt_offscreen_bind(scene, hw, hwHandle, w, h, B8G8R8A8Unorm, 1, out _);
            int rendered = rc == 0 ? Native.OffscreenRenderBatch(hwCtx, new[] { scene }, 0) : rc;
            check(rendered == 1 && Centre(hwTex, w, h) == "255,0,0,255", $"warp: QtNative moved back to the hardware adapter and renders (bind {rc}, rendered {rendered}) {ErrIf(rendered < 1)}");
            if (scene != SceneHandle.Null)
            {
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_scene_destroy(scene);
            }
            s.Pump(2);
        }
        NativeTest.vlqt_test_close_handle(warpHandle);
        NativeTest.vlqt_test_close_handle(hwHandle);
        NativeTest.vlqt_test_com_release(warpTex);
        NativeTest.vlqt_test_com_release(hwTex);
        check(NativeTest.vlqt_test_d3d11_release_device(warp, warpCtx) == 0, "warp: WARP device freed");
        check(NativeTest.vlqt_test_d3d11_release_device(hw, hwCtx) == 0, "warp: hardware device freed");
    }

    // A host that recreated its device without binding again passes the context of the new one: render_batch must refuse
    // (BAD_ARGUMENT, nothing rendered) instead of letting D3D11 use a fence of the first device on the second one, and the
    // scene stays bound and renders with the context of its own device.
    private static void WrongContext(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 64, h = 48;
        if (!check(NativeTest.CreateDevice(0, out var device1, out var context1) == 0, "wrong context: create device 1"))
            return;
        if (!check(NativeTest.CreateDevice(0, out var device2, out var context2) == 0, "wrong context: create device 2"))
        {
            NativeTest.vlqt_test_d3d11_release_device(device1, context1);
            return;
        }
        var scene = RectScene(w, h, s, "wrong context");
        if (scene != SceneHandle.Null && NewTarget(device1, w, h, B8G8R8A8Typeless, out var tex, out var handle))
        {
            int rc = (int)Native.vlqt_offscreen_bind(scene, device1, handle, w, h, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(handle);
            if (check(rc == 0, $"wrong context: bind on device 1 ({rc}) {ErrIf(rc != 0)}"))
            {
                int wrong = Native.OffscreenRenderBatch(context2, new[] { scene }, 0);
                string message = Native.LastError();
                check(wrong == (int)Native.Result.BadArgument && message.Contains("another device"),
                    $"wrong context: render_batch with the context of device 2 = BAD_ARGUMENT ({wrong}, \"{message}\")");
                check(Native.vlqt_offscreen_is_bound(scene) == 1 && Centre(tex, w, h) != "255,0,0,255",
                    "wrong context: the scene stays bound and nothing was rendered");
                int rendered = Native.OffscreenRenderBatch(context1, new[] { scene }, 0);
                check(rendered == 1 && Centre(tex, w, h) == "255,0,0,255",
                    $"wrong context: the scene renders red with the context of its own device ({rendered}) {ErrIf(rendered != 1)}");
            }
            Native.vlqt_offscreen_unbind(scene);
            NativeTest.vlqt_test_com_release(tex);
        }
        else
            check(false, "wrong context: setup " + Native.LastError());
        if (scene != SceneHandle.Null)
            Native.vlqt_scene_destroy(scene);
        s.Pump(2);
        check(NativeTest.vlqt_test_d3d11_release_device(device1, context1) == 0, "wrong context: device 1 freed");
        check(NativeTest.vlqt_test_d3d11_release_device(device2, context2) == 0, "wrong context: device 2 freed");
    }

    // (3) QtNative's device removed right after render_batch queued a wait for the write value on the foreign context:
    // the foreign side must not hang. The readback on the foreign device (copy + Map READ) queues behind that wait, so it
    // would block for ever if the value never came; it runs on a worker thread with a limit. Whether the render still
    // finished before the removal depends on timing: both outcomes are legal, the result is logged.
    private static void NeverSignalled(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 64, h = 48, rounds = 8, limitMs = 5000;
        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "never-signalled: create device"))
            return;
        int finished = 0, discarded = 0, other = 0, hung = 0;
        long slowest = 0;
        for (int round = 0; round < rounds && hung == 0; round++)
        {
            var scene = RectScene(w, h, s, "never-signalled");
            if (scene == SceneHandle.Null || !NewTarget(device, w, h, B8G8R8A8Typeless, out var tex, out var handle))
            {
                check(false, "never-signalled: setup " + Native.LastError());
                break;
            }
            int rc = (int)Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(handle);
            int rendered = rc == 0 ? Native.OffscreenRenderBatch(context, new[] { scene }, 0) : rc;
            if (!check(rendered == 1 && Centre(tex, w, h) == "255,0,0,255", $"never-signalled: first frame renders red ({rc}, {rendered}) {ErrIf(rendered != 1)}"))
                break;
            // The second frame (green, wait value 2 queued on the foreign context), then QtNative's device is removed
            // after 0 ... 2 ms, so the removal meets the render job at different points.
            Recolor(scene, 1, 0, 1, 0, 1, s, "never-signalled");
            int renderedGreen;
            using (new Watchdog("never-signalled: render_batch", 30))
            {
                renderedGreen = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
                if (round % 3 > 0)
                    Thread.Sleep(round % 3);
                var lost = NativeTest.vlqt_test_device_lost(TestProtocol.DeviceLost.Remove);
                if (lost != 0)
                {
                    s.Info($"never-signalled: skipped ({lost}, needs VLQT_TEST_D3D11ON12=1: -Only qttexture)");
                    Native.vlqt_offscreen_unbind(scene);
                    Native.vlqt_scene_destroy(scene);
                    NativeTest.vlqt_test_com_release(tex);
                    NativeTest.vlqt_test_d3d11_release_device(device, context);
                    return;
                }
                var wait = Stopwatch.StartNew();
                while (Native.vlqt_offscreen_is_bound(scene) != 0 && wait.ElapsedMilliseconds < 5000)
                    s.Pump(1);
                if (Native.vlqt_offscreen_is_bound(scene) != 0)
                    check(false, $"never-signalled round {round}: scene still bound 5 s after the removal");
            }
            // The foreign side: a readback on the foreign context, which holds the queued wait.
            var sw = Stopwatch.StartNew();
            var read = Task.Run(() => Read(tex, w, h, s_bgra));
            bool done = read.Wait(limitMs);
            slowest = Math.Max(slowest, sw.ElapsedMilliseconds);
            if (!done)
            {
                hung++;
                s.Info($"never-signalled round {round}: foreign readback still blocked after {limitMs} ms (render_batch gave {renderedGreen}); the device and context are left alone");
                break;
            }
            var px = read.Result;
            var c = px == null ? "read failed" : Str(Rgba(px, w, w / 2, h / 2, s_bgra));
            if (c == "0,255,0,255")
                finished++;
            else if (c == "255,0,0,255")
                discarded++;
            else
            {
                other++;
                s.Info($"never-signalled round {round}: unexpected centre {c}");
            }
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_scene_destroy(scene);
            NativeTest.vlqt_test_com_release(tex);
            s.Pump(2);
        }
        check(hung == 0, $"never-signalled: the foreign readback returned in every round (limit {limitMs} ms, slowest {slowest} ms)");
        check(other == 0, $"never-signalled: the foreign texture holds a whole frame in every round ({other} unexpected)");
        s.Info($"never-signalled: {rounds} rounds, render finished before the removal in {finished}, frame discarded in {discarded}");
        if (hung == 0)
            check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "never-signalled: device freed");
    }

    // (5) A window and a bound scene (both on QtNative's device, the window's frames hold the context lock), device loss,
    // then pumping while the host keeps calling render_batch: the main thread never blocks, the scene is unbound, binds
    // again and renders red, the window renders again.
    private static void WindowAndDeviceLoss(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 200, h = 150;
        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "window loss: create device"))
            return;
        var scene = RectScene(64, 48, s, "window loss");
        var win = NativeCalls.CreateWindow(w, h, "VL.Qt QtTexture window and device loss");
        NewTarget(device, 64, 48, B8G8R8A8Typeless, out var tex, out var handle);
        if (scene == SceneHandle.Null || win == SceneHandle.Null || tex == IntPtr.Zero)
        {
            check(false, "window loss: setup " + Native.LastError());
            NativeTest.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0.125f, 0.25f, 0.5f, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetPlace(1, 20, 20, 100, 60);
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.5f, 0, 1);
        cmd.Reparent(1, 0, 0);
        check(Native.Apply(win, cmd.EndFrame()) == 0, "window loss: apply to the window");
        Native.vlqt_window_show(win);
        s.Pump(3);

        // The window as a witness: orange at (70, 50) DIP, the clear colour (32, 64, 128) outside the rectangle.
        string WindowPixels(string name)
        {
            try
            {
                using var shot = s.Grab(win, Path.Combine(s.OutDir, name));
                var o = shot.GetPixel(70 * shot.Width / w, 50 * shot.Height / h);
                var b = shot.GetPixel(160 * shot.Width / w, 130 * shot.Height / h);
                return o.R > 245 && Math.Abs(o.G - 128) < 6 && o.B < 8 && Math.Abs(b.R - 32) < 6 && Math.Abs(b.G - 64) < 6 && Math.Abs(b.B - 128) < 6
                    ? "ok" : $"orange area {o.R},{o.G},{o.B}, background {b.R},{b.G},{b.B}";
            }
            catch (Exception e)
            {
                return "grab failed: " + e.Message;
            }
        }
        check(WindowPixels("qttexture_windowloss_before.png") == "ok", "window loss: the window renders before the loss");

        int rc = (int)Native.vlqt_offscreen_bind(scene, device, handle, 64, 48, B8G8R8A8Unorm, 1, out _);
        NativeTest.vlqt_test_close_handle(handle);
        int rendered = rc == 0 ? Native.OffscreenRenderBatch(context, new[] { scene }, 0) : rc;
        check(rendered == 1 && Centre(tex, 64, 48) == "255,0,0,255", $"window loss: scene renders red next to the window ({rc}, {rendered}) {ErrIf(rendered != 1)}");
        Recolor(scene, 1, 0, 0, 1, 1, s, "window loss");
        Native.OffscreenRenderBatch(context, new[] { scene }, 0);

        var lost = NativeTest.vlqt_test_device_lost(TestProtocol.DeviceLost.Remove);
        if (lost != 0)
            s.Info($"window loss: device loss skipped ({lost}, needs VLQT_TEST_D3D11ON12=1: -Only qttexture)");
        else
        {
            long slowestMs = 0;
            int calls = 0;
            var total = Stopwatch.StartNew();
            using (new Watchdog("window loss: pump with render_batch", 30))
                while (Native.vlqt_offscreen_is_bound(scene) != 0 && total.ElapsedMilliseconds < 10000)
                {
                    var call = Stopwatch.StartNew();
                    s.Pump(1);
                    Native.OffscreenRenderBatch(context, new[] { scene }, 0);
                    slowestMs = Math.Max(slowestMs, call.ElapsedMilliseconds);
                    calls++;
                }
            check(Native.vlqt_offscreen_is_bound(scene) == 0, $"window loss: the scene reports unbound ({total.ElapsedMilliseconds} ms, {calls} pumps)");
            check(slowestMs < 5000, $"window loss: the main thread returned within 5 s on every pump and render_batch (slowest {slowestMs} ms)");

            NewTarget(device, 64, 48, B8G8R8A8Typeless, out var fresh, out var freshHandle);
            rc = (int)Native.vlqt_offscreen_bind(scene, device, freshHandle, 64, 48, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(freshHandle);
            // The scene kept its elements (blue by the last recolour): red again after one more change.
            Recolor(scene, 1, 1, 0, 0, 1, s, "window loss");
            rendered = rc == 0 ? Native.OffscreenRenderBatch(context, new[] { scene }, 0) : rc;
            check(rendered == 1 && Centre(fresh, 64, 48) == "255,0,0,255", $"window loss: the scene binds again and renders red ({rc}, {rendered}, centre {Centre(fresh, 64, 48)}) {ErrIf(rendered != 1)}");
            NativeTest.vlqt_test_com_release(fresh);
            s.Pump(3);
            check(WindowPixels("qttexture_windowloss_after.png") == "ok", "window loss: the window renders again after the loss");
        }
        Native.vlqt_offscreen_unbind(scene);
        Native.vlqt_scene_destroy(scene);
        Native.vlqt_scene_destroy(win);
        NativeTest.vlqt_test_com_release(tex);
        s.Pump(2);
        check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "window loss: device freed");
    }
}
