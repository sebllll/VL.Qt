using System;
#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
#endif
using System.IO;
using System.Runtime.InteropServices;
using VL.Qt;

/// <summary>
/// #116 (QtTexture) task 1.7: self-test of the TEST ONLY exports that stand in for Stride's device (vlqt.h): a second
/// D3D11 device on QtNative's adapter and on WARP, shared textures (B8G8R8A8_TYPELESS, R16G16B16A16_FLOAT) with NT
/// handles, readback through a staging copy, COM reference counts, and that releasing everything frees the device and
/// leaves the process handle count where it was. Tasks 1.3/1.4: an offscreen scene bound to a shared texture, rendered
/// through the fences and read back (<c>qttexture_scene.png</c>); the full matrix is task 1.8.
/// </summary>
static class QtTextureTest
{
    const int B8G8R8A8Typeless = 90;
    const int R16G16B16A16Float = 10;
    const int R8G8B8A8Unorm = 28;
    const int B8G8R8A8Unorm = 87;
    const int R32G32B32A32Float = 2; // not supported by the readback

    public static void Run(string outDir, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP qttexture: D3D11 only");
            return;
        }
        Console.WriteLine("INFO qttexture: #116 test exports");

        // Invalid arguments.
        check(Native.vlqt_test_d3d11_create_device(2, out var none, out var noneCtx) == Protocol.Offscreen.BadArgument
              && none == IntPtr.Zero && noneCtx == IntPtr.Zero, "116: create_device with an unknown adapter = bad argument");
        check(Native.vlqt_test_d3d11_release_device(IntPtr.Zero, IntPtr.Zero) == Protocol.Offscreen.BadArgument, "116: release_device(null) = bad argument");
        check(Native.vlqt_test_com_ref_count(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "116: com_ref_count(null) = bad argument");
        check(Native.vlqt_test_com_release(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "116: com_release(null) = bad argument");
        check(Native.vlqt_test_close_handle(IntPtr.Zero) == Protocol.Offscreen.BadArgument, "116: close_handle(null) = bad argument");
        check(Native.vlqt_test_d3d11_create_shared_texture(IntPtr.Zero, 16, 16, B8G8R8A8Typeless, out var t0, out var h0) == Protocol.Offscreen.BadArgument
              && t0 == IntPtr.Zero && h0 == IntPtr.Zero, "116: create_shared_texture(null device) = bad argument");

        // One round per adapter warms up lazily created process state (D3D runtime, driver), so the baseline below is stable.
        RoundTrip(0, check, verbose: false);
        RoundTrip(1, check, verbose: false);

        var baseline = Native.vlqt_test_process_handle_count();
        check(baseline > 0, $"116: process handle count {baseline}");
        RoundTrip(0, check, verbose: true);
        RoundTrip(1, check, verbose: true);
        // Several rounds: a leak of one handle per round does not hide in noise.
        for (int i = 0; i < 20; i++)
            RoundTrip(i % 2, check, verbose: false);
        var after = Native.vlqt_test_process_handle_count();
        check(after == baseline, $"116: handle count back at baseline ({baseline} -> {after})");

        OffscreenScene(outDir, check);
    }

    // Task 1.3/1.4: a scene bound to a B8G8R8A8_TYPELESS texture of the test device, one render through the fences,
    // readback; an unchanged scene renders nothing; unbind and destroy give back every device reference.
    static void OffscreenScene(string outDir, Action<bool, string> report)
    {
        bool check(bool ok, string what)
        {
            report(ok, what);
            return ok;
        }
        const int w = 64, h = 48;
        const uint rect = 1;
        // Rectangle (16, 12) .. (48, 36), red, on a transparent clear colour.
        const int rx = 16, ry = 12, rw = 32, rh = 24;

        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "116 scene: create test device"))
            return;
        // Device children (textures, views, fences, Qt's scene graph resources) count on their device: compare counts
        // taken at the same point of the lifetime.
        Native.vlqt_test_device_info(IntPtr.Zero, out var qtDevice, out _);
        int QtRefs() => qtDevice != IntPtr.Zero ? Native.vlqt_test_com_ref_count(qtDevice) : -1;
        int qtRefsBefore = QtRefs();
        var scene = Native.vlqt_offscreen_create(w, h);
        if (!check(scene != IntPtr.Zero, "116 scene: offscreen_create " + Native.LastError()))
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        check(Native.vlqt_offscreen_is_bound(scene) == 0, "116 scene: new scene is unbound");
        check(Native.vlqt_test_offscreen_state(scene, out _, out _, out _) == Protocol.Offscreen.Unbound, "116 scene: state of an unbound scene = unbound");

        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 0);
        cmd.Create(rect, Protocol.Type.Rectangle);
        cmd.SetPlace(rect, rx, ry, rw, rh);
        cmd.SetColor(rect, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(rect, 0, 0);
        int rc = Native.Apply(scene, cmd.EndFrame());
        check(rc == 0, $"116 scene: apply rectangle ({rc}) {(rc != 0 ? Native.LastError() : "")}");

        rc = Native.vlqt_test_d3d11_create_shared_texture(device, w, h, B8G8R8A8Typeless, out var texture, out var handle);
        if (check(rc == 0, "116 scene: create shared texture " + rc))
        {
            int deviceRefs = Native.vlqt_test_com_ref_count(device);
            rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm);
            check(rc == Protocol.Offscreen.Ok, $"116 scene: bind ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            check(Native.vlqt_offscreen_is_bound(scene) == 1, "116 scene: is_bound after bind");

            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read1, out var write1, out var renders1);
            check(rc == 1 && read1 == 1 && write1 == 1 && renders1 == 1,
                $"116 scene: first render_batch renders once (rc {rc}, read {read1}, write {write1}, renders {renders1}) {(rc < 0 ? Native.LastError() : "")}");

            var pixels = new byte[w * h * 4];
            int read = Native.TestReadTexture(texture, pixels, out _, out _, out _);
            check(read == pixels.Length, $"116 scene: read back {read} bytes");
            // B8G8R8A8: bytes B, G, R, A (premultiplied).
            string Px(int x, int y) { int i = (y * w + x) * 4; return $"{pixels[i + 2]},{pixels[i + 1]},{pixels[i]},{pixels[i + 3]}"; }
            check(Px(rx + rw / 2, ry + rh / 2) == "255,0,0,255" && Px(rx + 1, ry + 1) == "255,0,0,255" && Px(rx + rw - 2, ry + rh - 2) == "255,0,0,255",
                $"116 scene: rectangle red ({Px(rx + rw / 2, ry + rh / 2)}, {Px(rx + 1, ry + 1)}, {Px(rx + rw - 2, ry + rh - 2)})");
            check(Px(2, 2) == "0,0,0,0" && Px(rx - 2, ry + rh / 2) == "0,0,0,0" && Px(rx + rw + 1, ry + rh / 2) == "0,0,0,0" && Px(w - 1, h - 1) == "0,0,0,0",
                $"116 scene: rest transparent black ({Px(2, 2)}, {Px(rx - 2, ry + rh / 2)}, {Px(rx + rw + 1, ry + rh / 2)}, {Px(w - 1, h - 1)})");
            WritePng(Path.Combine(outDir, "qttexture_scene.png"), pixels, w, h);

            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read2, out var write2, out var renders2);
            check(rc == 0 && read2 == read1 && write2 == write1 && renders2 == renders1,
                $"116 scene: unchanged scene renders nothing (rc {rc}, read {read2}, write {write2}, renders {renders2})");

            check(Native.vlqt_offscreen_unbind(scene) == Protocol.Offscreen.Ok && Native.vlqt_offscreen_is_bound(scene) == 0, "116 scene: unbind");
            check(Native.vlqt_test_offscreen_state(scene, out _, out _, out _) == Protocol.Offscreen.Unbound, "116 scene: state after unbind = unbound");
            int deviceRefsAfter = Native.vlqt_test_com_ref_count(device);
            check(deviceRefsAfter == deviceRefs, $"116 scene: test device references back after unbind ({deviceRefs} -> {deviceRefsAfter})");
            int qtRefsUnbound = QtRefs();

            // A second binding (a just bound scene renders) leaves no reference behind on either device.
            rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm);
            int rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            Native.vlqt_test_offscreen_state(scene, out var read3, out var write3, out var renders3);
            check(rc == 0 && rendered == 1 && read3 == 1 && write3 == 1 && renders3 == 1,
                $"116 scene: bind again renders once, fences from 1 (bind {rc}, rendered {rendered}, read {read3}, write {write3}, renders {renders3})");
            // The caller may close the handle any time after bind.
            Native.vlqt_test_close_handle(handle);
            Native.vlqt_offscreen_unbind(scene);
            int qtRefsUnbound2 = QtRefs();
            deviceRefsAfter = Native.vlqt_test_com_ref_count(device);
            check(qtRefsUnbound > 0 && qtRefsUnbound2 == qtRefsUnbound && deviceRefsAfter == deviceRefs,
                $"116 scene: second unbind, references unchanged (QtNative {qtRefsUnbound} -> {qtRefsUnbound2}, test device {deviceRefs} -> {deviceRefsAfter})");
            check(Native.vlqt_test_com_release(texture) == 0, "116 scene: texture released -> 0 references");
        }

        Native.vlqt_window_destroy(scene);
        int qtRefsAfter = QtRefs();
        check(qtRefsBefore > 0 && qtRefsAfter == qtRefsBefore, $"116 scene: QtNative device references back after destroy ({qtRefsBefore} -> {qtRefsAfter})");
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "116 scene: test device freed");
    }

    // Premultiplied BGRA rows as a PNG, to look at.
    static void WritePng(string path, byte[] bgra, int w, int h)
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
    static void RoundTrip(int adapter, Action<bool, string> check, bool verbose)
    {
        string name = adapter == 0 ? "QtNative adapter" : "WARP";
        bool Check(bool ok, string what)
        {
            if (verbose || !ok) check(ok, $"116 {name}: {what}");
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
