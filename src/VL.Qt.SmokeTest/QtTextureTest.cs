using NUnit.Framework;
using System;
#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
#endif
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using VL.Qt;

/// <summary>
/// (QtTexture) task 1.7: self-test of the TEST ONLY exports that stand in for Stride's device (vlqt.h): a second
/// D3D11 device on QtNative's adapter and on WARP, shared textures (B8G8R8A8_TYPELESS, R16G16B16A16_FLOAT) with NT
/// handles, readback through a staging copy, COM reference counts, and that releasing everything frees the device and
/// leaves the process handle count where it was. Tasks 1.3/1.4: an offscreen scene bound to a shared texture, rendered
/// through the fences and read back (<c>qttexture_scene.png</c>); the full matrix is task 1.8. Task 1.5: a WARP texture
/// moves QtNative's device while nothing renders on it, else gives ADAPTER_MISMATCH; a device loss of QtNative's device
/// unbinds the scenes and a rebind renders again (needs VLQT_TEST_D3D11ON12=1, set for "-Only qttexture"); a scene
/// keeps a released foreign device alive until unbind. Task 1.8 (QtTextureTest.Render.cs, .Leak.cs): pixels against a window
/// render, premultiplied alpha, formats, a batch of two scenes, the leak test.
/// </summary>
[TestFixture, NonParallelizable]
public static partial class QtTextureTest
{
    private const int B8G8R8A8Typeless = 90;
    private const int R16G16B16A16Float = 10;
    private const int R8G8B8A8Unorm = 28;
    private const int B8G8R8A8Unorm = 87;
    private const int R8G8B8A8UnormSrgb = 29;
    private const int B8G8R8A8UnormSrgb = 91;
    private const int R32G32B32A32Float = 2; // not supported by the readback

    /// <summary>QtTexture (offscreen scenes into foreign D3D11 textures): round trips, adapters, device loss, leaks, window match.</summary>
    [Test, Category("qttexture")]
    public static void QtTexture() => Scenario.Run(Run);

    /// <summary>QtTexture in the render thread mode, in a process of its own with D3D11On12 (device loss) and the debug layer (leak check).</summary>
    [Test, Category("qttexture-render-thread"), Category("own-process")]
    public static void QtTextureRenderThread() => Scenario.RunLocal(s => RunMode(s, null, "qttexture-render-thread", "offscreen scenes render on 1 Qt render thread(s)"));

    /// <summary>QtTexture in the main thread mode (VLQT_OFFSCREEN_THREAD=0), in a process of its own with D3D11On12 and the debug layer.</summary>
    [Test, Category("qttexture-main-thread"), Category("own-process")]
    public static void QtTextureMainThread() => Scenario.RunLocal(s => RunMode(s, "0", "qttexture-main-thread", "offscreen scenes render on the main thread"));

    // Both scenarios are D3D11 only (they SKIP elsewhere), so the helper would log no thread mode outside Windows; the former
    // runner ran the thread modes only from tools/build.ps1 (Windows), tools/test-linux.sh never did.
    private static void RunMode(Scenario s, string? threads, string folder, string modeLine)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip($"{folder}: QtTexture is D3D11 only");
            return;
        }
        OwnProcess.Run(s, new[] { "qttexture", "QtTextureInput" }, Env(threads), Path.Combine(SmokeParameters.SmokeDir, folder, "grabs"), modeLine);
    }

    private static Dictionary<string, string> Env(string? threads)
    {
        var env = new Dictionary<string, string> { ["VLQT_TEST_D3D11ON12"] = "1", ["VLQT_SMOKE_DEBUG_LAYER"] = DebugLayerAvailable() ? "1" : "0" };
        if (threads != null)
            env["VLQT_OFFSCREEN_THREAD"] = threads;
        return env;
    }

    // The leak test reads ReportLiveDeviceObjects of QtNative's device, which needs the D3D SDK layers.
    private static bool DebugLayerAvailable() => OperatingSystem.IsWindows()
        && (File.Exists(Path.Combine(Environment.SystemDirectory, "d3d11sdklayers.dll"))
            || File.Exists(Path.Combine(Environment.SystemDirectory, "d3d11_3sdklayers.dll")));

    internal static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("qttexture: D3D11 only");
            return;
        }
        s.Info("qttexture: #116 test exports");

        // Invalid arguments.
        s.Check(Native.vlqt_test_d3d11_create_device(2, out var none, out var noneCtx) == Protocol.Offscreen.BadArgument
              && none == IntPtr.Zero && noneCtx == IntPtr.Zero, "create_device with an unknown adapter = bad argument");
        s.Check(Native.vlqt_test_d3d11_release_device(IntPtr.Zero, IntPtr.Zero) == Protocol.Offscreen.BadArgument, "release_device(null) = bad argument");
        s.Check(Native.vlqt_test_com_ref_count(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "com_ref_count(null) = bad argument");
        s.Check(Native.vlqt_test_com_release(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "com_release(null) = bad argument");
        s.Check(Native.vlqt_test_close_handle(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "close_handle(null) = bad argument");
        s.Check(Native.vlqt_test_d3d11_create_shared_texture(IntPtr.Zero, 16, 16, B8G8R8A8Typeless, out var t0, out var h0) == Protocol.Offscreen.BadArgument
              && t0 == IntPtr.Zero && h0 == IntPtr.Zero, "create_shared_texture(null device) = bad argument");

        // One round per adapter warms up lazily created process state (D3D runtime, driver), so the baseline below is stable.
        RoundTrip(0, s, verbose: false);
        RoundTrip(1, s, verbose: false);

        var baseline = Native.vlqt_test_process_handle_count();
        s.Check(baseline > 0, $"process handle count {baseline}");
        RoundTrip(0, s, verbose: true);
        RoundTrip(1, s, verbose: true);
        // Several rounds: a leak of one handle per round does not hide in noise.
        for (int i = 0; i < 20; i++)
            RoundTrip(i % 2, s, verbose: false);
        // Up to about 2 s for handles that close late (finalizers, threads of the process that end); a leak stays. A one-off of lazily
        // created state may still land after the baseline (in the test host 573 -> 577 in some runs, 577 -> 577 in others): the leak
        // tolerance absorbs it, a leak of one handle per round grows by 22.
        var after = Native.vlqt_test_process_handle_count();
        for (int attempt = 0; attempt < 20 && after > baseline; attempt++)
        {
            s.Pump(2);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(100);
            after = Native.vlqt_test_process_handle_count();
        }
        s.Check(after - baseline <= handleTolerance, $"handle count back at baseline ({baseline} -> {after}, tolerance {handleTolerance})");

        // Before the first scene: QtNative can only move to WARP while no window renders on its device.
        // The robustness parts wait on the render thread and the GPU: a hang ends the run with a FAIL line (Watchdog).
        using (new Watchdog("warp", 60))
            WarpRuns(s);
        OffscreenScene(s);
        ThreadMode(s);
        AdapterAndDeviceLoss(s);
        // Task 1.8. Order matters: the window comparison goes last (a window renders on QtNative's device, which makes an adapter move impossible).
        Formats(s);
        Batch(s);
        Alpha(s);
        Leak(s);
        using (new Watchdog("never-signalled", 90))
            NeverSignalled(s);
        WindowMatch(s);
        using (new Watchdog("window loss", 90))
            WindowAndDeviceLoss(s);
    }

    // A scene of w x h DIP with a red rectangle (16, 12, 32, 24) on a transparent clear colour; null on failure.
    private static IntPtr RectScene(int w, int h, Scenario s, string label)
    {
        var scene = Native.vlqt_offscreen_create(w, h);
        s.Check(scene != IntPtr.Zero, $"{label}: offscreen_create {(scene == IntPtr.Zero ? Native.LastError() : "")}");
        if (scene == IntPtr.Zero)
            return scene;
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 0);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetPlace(1, 16, 12, 32, 24);
        cmd.SetColor(1, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(1, 0, 0);
        int rc = Native.Apply(scene, cmd.EndFrame());
        s.Check(rc == 0, $"{label}: apply rectangle ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        return scene;
    }

    // Centre pixel of a B8G8R8A8 texture as "R,G,B,A".
    private static string Centre(IntPtr texture, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        if (Native.TestReadTexture(texture, pixels, out _, out _, out _) != pixels.Length)
            return "read failed";
        int i = (h / 2 * w + w / 2) * 4;
        return $"{pixels[i + 2]},{pixels[i + 1]},{pixels[i]},{pixels[i + 3]}";
    }

    // Task 1.5: adapter mismatch or move, device loss of QtNative's device, foreign device released while bound.
    private static void AdapterAndDeviceLoss(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 64, h = 48;
        const string red = "255,0,0,255";
        IntPtr QtDevice() => Native.vlqt_test_device_info(IntPtr.Zero, out var d, out _) == 0 ? d : IntPtr.Zero;
        string Err(int rc) => rc < 0 ? Native.LastError() : "";

        if (!check(Native.vlqt_test_d3d11_create_device(0, out var hw, out var hwCtx) == 0, "adapter: create device on QtNative's adapter"))
            return;
        if (!check(Native.vlqt_test_d3d11_create_device(1, out var warp, out var warpCtx) == 0, "adapter: create WARP device"))
        {
            Native.vlqt_test_d3d11_release_device(hw, hwCtx);
            return;
        }
        Native.vlqt_test_d3d11_create_shared_texture(hw, w, h, B8G8R8A8Typeless, out var hwTex, out var hwHandle);
        Native.vlqt_test_d3d11_create_shared_texture(warp, w, h, B8G8R8A8Typeless, out var warpTex, out var warpHandle);
        var a = RectScene(w, h, s, "adapter: scene A");
        var b = RectScene(w, h, s, "adapter: scene B");
        if (hwTex == IntPtr.Zero || warpTex == IntPtr.Zero || a == IntPtr.Zero || b == IntPtr.Zero)
        {
            check(false, "adapter: setup " + Native.LastError());
            return;
        }

        // (i) A WARP texture: QtNative moves its device to WARP while nothing renders on it (no on-screen window has
        // rendered, no scene bound; so with -Only qttexture), else ADAPTER_MISMATCH.
        var qtBefore = QtDevice();
        int rc = Native.vlqt_offscreen_bind(a, warp, warpHandle, w, h, B8G8R8A8Unorm, 1, out _);
        if (rc == Protocol.Offscreen.Ok)
        {
            var qtWarp = QtDevice();
            check(qtWarp != IntPtr.Zero && qtWarp != qtBefore, $"adapter: bind to WARP moved QtNative's device ({qtBefore:X} -> {qtWarp:X})");
            rc = Native.OffscreenRenderBatch(warpCtx, new[] { a }, 0);
            check(rc == 1 && Centre(warpTex, w, h) == red, $"adapter: scene renders on WARP (rc {rc}, centre {Centre(warpTex, w, h)}) {Err(rc)}");
            Native.vlqt_offscreen_unbind(a);
            s.Pump(2); // releases the retired device
            rc = Native.vlqt_offscreen_bind(a, hw, hwHandle, w, h, B8G8R8A8Unorm, 1, out _);
            check(rc == 0 && QtDevice() != qtWarp, $"adapter: bind to the first adapter moves back ({rc}) {Err(rc)}");
            s.Pump(2);
        }
        else
        {
            check(rc == Protocol.Offscreen.AdapterMismatch && Native.vlqt_offscreen_is_bound(a) == 0,
                $"adapter: bind to WARP = ADAPTER_MISMATCH (a window renders on QtNative's device) ({rc}) {Err(rc)}");
            rc = Native.vlqt_offscreen_bind(a, hw, hwHandle, w, h, B8G8R8A8Unorm, 1, out _);
            check(rc == 0, $"adapter: bind A on QtNative's adapter ({rc}) {Err(rc)}");
        }
        rc = Native.OffscreenRenderBatch(hwCtx, new[] { a }, 0);
        check(rc == 1 && Centre(hwTex, w, h) == red, $"adapter: A renders on QtNative's adapter (rc {rc}, centre {Centre(hwTex, w, h)}) {Err(rc)}");

        // A bound: a second scene on WARP must not move the device under it.
        var qtBound = QtDevice();
        rc = Native.vlqt_offscreen_bind(b, warp, warpHandle, w, h, B8G8R8A8Unorm, 1, out _);
        check(rc == Protocol.Offscreen.AdapterMismatch && Native.vlqt_offscreen_is_bound(b) == 0 && Native.vlqt_offscreen_is_bound(a) == 1
              && QtDevice() == qtBound, $"adapter: B on WARP while A is bound = ADAPTER_MISMATCH, A stays bound ({rc})");

        // (ii) Device loss of QtNative's device while A is bound.
        rc = Native.vlqt_test_device_lost(Protocol.DeviceLost.Remove);
        if (rc != 0)
            s.Info($"device lost: skipped ({rc}, needs VLQT_TEST_D3D11ON12=1: -Only qttexture)");
        else
        {
            rc = Native.OffscreenRenderBatch(hwCtx, new[] { a }, 0);
            check(rc == Protocol.Offscreen.DeviceLost || rc == 0, $"device lost: render_batch before the rebuild = DEVICE_LOST or nothing ({rc})");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Native.vlqt_offscreen_is_bound(a) != 0 && sw.ElapsedMilliseconds < 5000)
                s.Pump(1);
            check(Native.vlqt_offscreen_is_bound(a) == 0, "device lost: A unbound by the rebuild");
            check(QtDevice() != qtBound && QtDevice() != IntPtr.Zero, $"device lost: new QtNative device ({qtBound:X} -> {QtDevice():X})");
            // A new (zeroed) texture: the red must come from a render on the new device, with the scene's elements kept.
            Native.vlqt_test_d3d11_create_shared_texture(hw, w, h, B8G8R8A8Typeless, out var fresh, out var freshHandle);
            rc = Native.vlqt_offscreen_bind(a, hw, freshHandle, w, h, B8G8R8A8Unorm, 1, out _);
            check(rc == 0, $"device lost: rebind A ({rc}) {Err(rc)}");
            rc = Native.OffscreenRenderBatch(hwCtx, new[] { a }, 0);
            check(rc == 1 && Centre(fresh, w, h) == red, $"device lost: A renders again (rc {rc}, centre {Centre(fresh, w, h)}) {Err(rc)}");
            Native.vlqt_offscreen_unbind(a);
            Native.vlqt_test_close_handle(freshHandle);
            Native.vlqt_test_com_release(fresh);
            s.Pump(2);
        }

        // (iii) The owner releases the foreign device while B is bound: the fences keep it alive, unbind frees it.
        if (check(Native.vlqt_test_d3d11_create_device(0, out var gone, out var goneCtx) == 0, "foreign release: create device"))
        {
            Native.vlqt_test_d3d11_create_shared_texture(gone, w, h, B8G8R8A8Typeless, out var goneTex, out var goneHandle);
            rc = Native.vlqt_offscreen_bind(b, gone, goneHandle, w, h, B8G8R8A8Unorm, 1, out _);
            int rendered = Native.OffscreenRenderBatch(goneCtx, new[] { b }, 0);
            check(rc == 0 && rendered == 1, $"foreign release: bind and render B ({rc}, {rendered}) {Err(rc)}");
            Native.vlqt_test_close_handle(goneHandle);
            Native.vlqt_test_com_release(goneTex);
            int left = Native.vlqt_test_d3d11_release_device(gone, goneCtx);
            check(left > 0, $"foreign release: device released by its owner, the bound scene keeps {left} reference(s)");
            check(Native.vlqt_offscreen_unbind(b) == 0 && Native.vlqt_offscreen_is_bound(b) == 0, "foreign release: unbind after the release");
        }

        Native.vlqt_window_destroy(a);
        Native.vlqt_window_destroy(b);
        Native.vlqt_test_close_handle(hwHandle);
        Native.vlqt_test_close_handle(warpHandle);
        Native.vlqt_test_com_release(hwTex);
        Native.vlqt_test_com_release(warpTex);
        check(Native.vlqt_test_d3d11_release_device(hw, hwCtx) == 0, "adapter: device on QtNative's adapter freed");
        check(Native.vlqt_test_d3d11_release_device(warp, warpCtx) == 0, "adapter: WARP device freed");
    }

    // Task 1.3/1.4: a scene bound to a B8G8R8A8_TYPELESS texture of the test device, one render through the fences,
    // readback; an unchanged scene renders nothing; unbind and destroy give back every device reference.
    private static void OffscreenScene(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 64, h = 48;
        const uint rect = 1;
        // Rectangle (16, 12) .. (48, 36), red, on a transparent clear colour.
        const int rx = 16, ry = 12, rw = 32, rh = 24;

        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "scene: create test device"))
            return;
        // Device children (textures, views, fences, Qt's scene graph resources) count on their device: compare counts
        // taken at the same point of the lifetime.
        Native.vlqt_test_device_info(IntPtr.Zero, out var qtDevice, out _);
        int QtRefs() => qtDevice != IntPtr.Zero ? Native.vlqt_test_com_ref_count(qtDevice) : -1;
        int qtRefsBefore = QtRefs();
        var scene = Native.vlqt_offscreen_create(w, h);
        if (!check(scene != IntPtr.Zero, "scene: offscreen_create " + Native.LastError()))
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        check(Native.vlqt_offscreen_is_bound(scene) == 0, "scene: new scene is unbound");
        check(Native.vlqt_test_offscreen_state(scene, out _, out _, out _) == Protocol.Offscreen.Unbound, "scene: state of an unbound scene = unbound");

        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 0);
        cmd.Create(rect, Protocol.Type.Rectangle);
        cmd.SetPlace(rect, rx, ry, rw, rh);
        cmd.SetColor(rect, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(rect, 0, 0);
        int rc = Native.Apply(scene, cmd.EndFrame());
        check(rc == 0, $"scene: apply rectangle ({rc}) {(rc != 0 ? Native.LastError() : "")}");

        rc = Native.vlqt_test_d3d11_create_shared_texture(device, w, h, B8G8R8A8Typeless, out var texture, out var handle);
        if (check(rc == 0, "scene: create shared texture " + rc))
        {
            int deviceRefs = Native.vlqt_test_com_ref_count(device);
            rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
            check(rc == Protocol.Offscreen.Ok, $"scene: bind ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            check(Native.vlqt_offscreen_is_bound(scene) == 1, "scene: is_bound after bind");

            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read1, out var write1, out var renders1);
            check(rc == 1 && read1 == 1 && write1 == 1 && renders1 == 1,
                $"scene: first render_batch renders once (rc {rc}, read {read1}, write {write1}, renders {renders1}) {(rc < 0 ? Native.LastError() : "")}");

            var pixels = new byte[w * h * 4];
            int read = Native.TestReadTexture(texture, pixels, out _, out _, out _);
            check(read == pixels.Length, $"scene: read back {read} bytes");
            // B8G8R8A8: bytes B, G, R, A (premultiplied).
            string Px(int x, int y) { int i = (y * w + x) * 4; return $"{pixels[i + 2]},{pixels[i + 1]},{pixels[i]},{pixels[i + 3]}"; }
            check(Px(rx + rw / 2, ry + rh / 2) == "255,0,0,255" && Px(rx + 1, ry + 1) == "255,0,0,255" && Px(rx + rw - 2, ry + rh - 2) == "255,0,0,255",
                $"scene: rectangle red ({Px(rx + rw / 2, ry + rh / 2)}, {Px(rx + 1, ry + 1)}, {Px(rx + rw - 2, ry + rh - 2)})");
            check(Px(2, 2) == "0,0,0,0" && Px(rx - 2, ry + rh / 2) == "0,0,0,0" && Px(rx + rw + 1, ry + rh / 2) == "0,0,0,0" && Px(w - 1, h - 1) == "0,0,0,0",
                $"scene: rest transparent black ({Px(2, 2)}, {Px(rx - 2, ry + rh / 2)}, {Px(rx + rw + 1, ry + rh / 2)}, {Px(w - 1, h - 1)})");
            WritePng(Path.Combine(s.OutDir, "qttexture_scene.png"), pixels, w, h);

            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read2, out var write2, out var renders2);
            check(rc == 0 && read2 == read1 && write2 == write1 && renders2 == renders1,
                $"scene: unchanged scene renders nothing (rc {rc}, read {read2}, write {write2}, renders {renders2})");
            OffscreenSnapshot(scene, rect, rw, rh, context, s);

            check(Native.vlqt_offscreen_unbind(scene) == Protocol.Offscreen.Ok && Native.vlqt_offscreen_is_bound(scene) == 0, "scene: unbind");
            check(Native.vlqt_test_offscreen_state(scene, out _, out _, out _) == Protocol.Offscreen.Unbound, "scene: state after unbind = unbound");
            int deviceRefsAfter = Native.vlqt_test_com_ref_count(device);
            check(deviceRefsAfter == deviceRefs, $"scene: test device references back after unbind ({deviceRefs} -> {deviceRefsAfter})");
            int qtRefsUnbound = QtRefs();

            // A second binding (a just bound scene renders) leaves no reference behind on either device.
            rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
            int rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read3, out var write3, out var renders3);
            check(rc == 0 && rendered == 1 && read3 == 1 && write3 == 1 && renders3 == 1,
                $"scene: bind again renders once, fences from 1 (bind {rc}, rendered {rendered}, read {read3}, write {write3}, renders {renders3})");
            // The caller may close the handle any time after bind.
            Native.vlqt_test_close_handle(handle);
            Native.vlqt_offscreen_unbind(scene);
            int qtRefsUnbound2 = QtRefs();
            deviceRefsAfter = Native.vlqt_test_com_ref_count(device);
            check(qtRefsUnbound > 0 && qtRefsUnbound2 == qtRefsUnbound && deviceRefsAfter == deviceRefs,
                $"scene: second unbind, references unchanged (QtNative {qtRefsUnbound} -> {qtRefsUnbound2}, test device {deviceRefs} -> {deviceRefsAfter})");
            check(Native.vlqt_test_com_release(texture) == 0, "scene: texture released -> 0 references");
        }

        Native.vlqt_window_destroy(scene);
        int qtRefsAfter = QtRefs();
        check(qtRefsBefore > 0 && qtRefsAfter == qtRefsBefore, $"scene: QtNative device references back after destroy ({qtRefsBefore} -> {qtRefsAfter})");
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "scene: test device freed");
    }

    // snapshot of an element of a bound offscreen scene that has nothing to render (not dirty). The scene is never
    // exposed; the grab asks for a frame, the next render_batch renders it and the pump delivers the pixels (red, rw x rh).
    private static void OffscreenSnapshot(IntPtr scene, uint element, int rw, int rh, IntPtr context, Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!check(Native.vlqt_res_create(Protocol.ResKind.Snapshot, out var id) == 0, "snapshot: create resource"))
            return;
        int rc = Native.vlqt_res_snapshot(id, scene, element, 1);
        s.Check(rc == 0, $"snapshot: start on the offscreen scene ({rc})");
        int state = 0, frames = 0, rendered = 0;
        for (; frames < 120 && state is not (Protocol.ResState.Ready or Protocol.ResState.Failed); frames++)
        {
            rendered += Math.Max(0, Native.OffscreenRenderBatch(context, new[] { scene }, 0));
            s.Pump(1);
            state = Native.vlqt_res_wait(id, 0);
        }
        var error = SnapshotError(id);
        s.Check(state == Protocol.ResState.Ready && rendered >= 1,
            $"snapshot: Ready after {frames} frame(s), {rendered} render(s) (state {state}) {error}");
        var pixels = new byte[rw * rh * 4];
        int read = Native.ResReadSnapshot(id, pixels, out var desc);
        // BGRA8 premultiplied: bytes B, G, R, A.
        string Px(int x, int y) { int i = (y * rw + x) * 4; return read == pixels.Length ? $"{pixels[i + 2]},{pixels[i + 1]},{pixels[i]},{pixels[i + 3]}" : "-"; }
        s.Check(read == pixels.Length && desc.width == rw && desc.height == rh && Px(rw / 2, rh / 2) == "255,0,0,255" && Px(0, 0) == "255,0,0,255"
              && Px(rw - 1, rh - 1) == "255,0,0,255",
            $"snapshot: {desc.width}x{desc.height} red ({read} bytes, {Px(rw / 2, rh / 2)}, {Px(0, 0)}, {Px(rw - 1, rh - 1)})");
        Native.vlqt_res_release(id);
        s.Pump(1);
    }

    // Error text of resource id among the queued resource events ("" if none); drains the queue.
    internal static string SnapshotError(ulong id)
    {
        var error = "";
        var buf = new byte[64 * 1024];
        int n = Native.ResPoll(buf);
        for (int at = 0; at < n;)
        {
            var ev = BitConverter.ToUInt16(buf, at);
            var len = BitConverter.ToUInt16(buf, at + 2);
            if (ev == Protocol.ResEv.Error && BitConverter.ToUInt64(buf, at + 4) == id)
                error = System.Text.Encoding.UTF8.GetString(buf, at + 12, len - 8);
            at += (4 + len + 3) & ~3;
        }
        return error;
    }

    // Premultiplied BGRA rows as a PNG, to look at.
    internal static void WritePng(string path, byte[] bgra, int w, int h)
    {
#if WINDOWS
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++)
            Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
#endif
    }

    // Device on the adapter (0 = QtNative's, 1 = WARP), shared textures, readback, ref counts, release everything.
    private static void RoundTrip(int adapter, Scenario s, bool verbose)
    {
        string name = adapter == 0 ? "QtNative adapter" : "WARP";
        bool Check(bool ok, string what)
        {
            if (verbose || !ok) s.Check(ok, $"116 {name}: {what}");
            return ok;
        }

        int hr = Native.vlqt_test_d3d11_create_device(adapter, out var device, out var context);
        if (!Check(hr == 0 && device != IntPtr.Zero && context != IntPtr.Zero, "create device " + hr))
            return;
        int deviceRefs = Native.vlqt_test_com_ref_count(device);
        Check(deviceRefs >= 1, $"device ref count {deviceRefs}");
        Check(Native.vlqt_test_com_ref_count(device) == deviceRefs, "ref count probe leaves the count unchanged");

        // Typeless BGRA8 and float RGBA16: one pixel is 4 and 8 bytes.
        foreach (var (format, bpp, label) in new[] { (B8G8R8A8Typeless, 4, "B8G8R8A8_TYPELESS"), (R16G16B16A16Float, 8, "R16G16B16A16_FLOAT") })
        {
            const int w = 37, h = 21; // odd sizes: the staging row pitch is not the packed row size
            hr = Native.vlqt_test_d3d11_create_shared_texture(device, w, h, format, out var texture, out var handle);
            if (!Check(hr == 0 && texture != IntPtr.Zero && handle != IntPtr.Zero, $"{label}: create shared texture {hr}"))
                continue;
            Check(Native.vlqt_test_com_ref_count(texture) == 1, $"{label}: texture ref count 1");

            var pixels = new byte[w * h * bpp];
            int read = Native.TestReadTexture(texture, pixels, out var rw, out var rh, out var rf);
            Check(read == pixels.Length && rw == w && rh == h && rf == format, $"{label}: read {read} bytes of {rw}x{rh} format {rf}");
            Check(Array.TrueForAll(pixels, b => b == 0), $"{label}: new texture reads back as zeros");

            var small = new byte[pixels.Length - 1];
            read = Native.TestReadTexture(texture, small, out rw, out rh, out rf);
            Check(read == -pixels.Length && rw == w && rh == h && rf == format, $"{label}: too small buffer reports -{pixels.Length} (got {read})");
            Check(Native.TestReadTexture(texture, Span<byte>.Empty, out _, out _, out _) == -pixels.Length, $"{label}: empty buffer reports the required size");

            Check(Native.vlqt_test_close_handle(handle) == 0, $"{label}: close handle");
            Check(Native.vlqt_test_close_handle(handle) != 0, $"{label}: closing a closed handle fails");
            Check(Native.vlqt_test_com_release(texture) == 0, $"{label}: release texture -> 0 references");
        }

        // A format without a readback (16 bytes per pixel) is rejected, the texture itself is fine.
        hr = Native.vlqt_test_d3d11_create_shared_texture(device, 8, 8, R32G32B32A32Float, out var wide, out var wideHandle);
        if (Check(hr == 0, "R32G32B32A32_FLOAT: create " + hr))
        {
            Check(Native.TestReadTexture(wide, new byte[8 * 8 * 16], out _, out _, out _) == Protocol.Offscreen.BadArgument, "unsupported readback format = bad argument");
            Native.vlqt_test_close_handle(wideHandle);
            Native.vlqt_test_com_release(wide);
        }

        // Two readbacks in a row on one texture (the staging copy is per call and freed).
        hr = Native.vlqt_test_d3d11_create_shared_texture(device, 4, 4, R8G8B8A8Unorm, out var again, out var againHandle);
        if (Check(hr == 0, "R8G8B8A8_UNORM: create " + hr))
        {
            var buf = new byte[64];
            Check(Native.TestReadTexture(again, buf, out _, out _, out _) == 64 && Native.TestReadTexture(again, buf, out _, out _, out _) == 64, "read twice");
            Native.vlqt_test_close_handle(againHandle);
            Check(Native.vlqt_test_com_release(again) == 0, "release texture -> 0 references");
        }

        Check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "release device -> 0 references");
    }
}
