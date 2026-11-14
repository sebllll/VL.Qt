using System;
using System.Runtime.InteropServices;
using VL.Qt;

/// <summary>
/// #116 (QtTexture) task 1.7: self-test of the TEST ONLY exports that stand in for Stride's device (vlqt.h): a second
/// D3D11 device on QtNative's adapter and on WARP, shared textures (B8G8R8A8_TYPELESS, R16G16B16A16_FLOAT) with NT
/// handles, readback through a staging copy, COM reference counts, and that releasing everything frees the device and
/// leaves the process handle count where it was. The later scenarios of the offscreen scene build on these probes.
/// </summary>
static class QtTextureTest
{
    const int B8G8R8A8Typeless = 90;
    const int R16G16B16A16Float = 10;
    const int R8G8B8A8Unorm = 28;
    const int R32G32B32A32Float = 2; // not supported by the readback

    public static void Run(Action<bool, string> check)
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
