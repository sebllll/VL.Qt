using System;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (tooltips package 03): the tooltip preview of image resources. With VLQT_IMG_PREVIEW the decode worker keeps a
/// BGRA8 premultiplied copy with a longest edge of 256 px (never upscaled); readable on a Ready resource only, without
/// the flag -7. Files and set_pixels, a replacement shows the preview of the visible content. No window needed.
/// </summary>
[TestFixture, NonParallelizable]
public static class ImagePreviewTest
{
    /// <summary>Image preview.</summary>
    [Test, Category("image-preview")]
    public static void ImagePreview() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "image-preview");
        Directory.CreateDirectory(dir);
        var big = Path.Combine(dir, "big.png");
        var small = Path.Combine(dir, "small.png");
        s.Check(NativeTest.vlqt_test_write_image(big, 4000, 2000, 90) == 0, "4000x2000 test image written");
        s.Check(NativeTest.vlqt_test_write_image(small, 100, 80, 90) == 0, "100x80 test image written");

        ulong Create()
        {
            var rc = Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id);
            if (rc != 0) s.Check(false, $"create ({rc})");
            return id;
        }
        ulong Load(string path, uint flags)
        {
            var id = Create();
            s.Check(Native.vlqt_image_load(id, path, flags) == 0, $"load {Path.GetFileName(path)} flags {flags}");
            s.Check(Native.vlqt_res_wait(id, 20000) == Protocol.ResourceState.Ready, $"{Path.GetFileName(path)} Ready");
            return id;
        }
        // rc, size and the first pixel (BGRA) of the preview.
        (int rc, int w, int h, byte[] px) Read(ulong id)
        {
            var rc = NativeCalls.ReadPreview(id, Span<byte>.Empty, out var d, out var required);
            if (rc != (int)Native.Result.BufferTooSmall) return (rc, d.width, d.height, []);
            var px = new byte[required];
            var n = NativeCalls.ReadPreview(id, px, out d);
            return (n, d.width, d.height, px);
        }
        bool Is(ulong id, int w, int h, string what)
        {
            var (rc, rw, rh, px) = Read(id);
            var ok = rc == w * h * 4 && rw == w && rh == h && px.Length == rc;
            s.Check(ok, $"{what}: preview {w}x{h} (rc {rc}, got {rw}x{rh})");
            return ok;
        }
        var dummy = new byte[16];

        // Files: with the flag 4000x2000 -> 256x128 (aspect kept), 100x80 stays, without the flag -7.
        var a = Load(big, Protocol.ImageLoadFlags.Preview);
        if (Is(a, 256, 128, "4000x2000 file"))
        {
            var (_, _, _, px) = Read(a);
            s.Check(px[0] == 0 && px[1] == 0 && px[2] == 255 && px[3] == 255, $"first stripe red, BGRA {px[0]},{px[1]},{px[2]},{px[3]}");
        }
        var b = Load(small, Protocol.ImageLoadFlags.Preview);
        Is(b, 100, 80, "100x80 file not upscaled");
        var c = Load(big, 0);
        s.Check(NativeCalls.ReadPreview(c, Span<byte>.Empty, out _) == (int)Native.Result.NoData, "without the flag -> NoData");
        s.Check(NativeCalls.ReadPreview(c, dummy, out _) == (int)Native.Result.NoData, "without the flag, with a buffer -> NoData");

        // cap too small: BufferTooSmall with the required size and the desc filled, nothing copied.
        var rcSmall = NativeCalls.ReadPreview(a, dummy, out var d, out var smallRequired);
        s.Check(rcSmall == (int)Native.Result.BufferTooSmall && smallRequired == 256 * 128 * 4 && d.width == 256 && d.height == 128 && d.row_bytes == 256 * 4 && d.format == 9 && d.premultiplied == 1, $"small cap -> BufferTooSmall, required {smallRequired}, desc filled ({rcSmall}, {d.width}x{d.height} rb {d.row_bytes} fmt {d.format} pm {d.premultiplied})");
        s.Check(dummy[0] == 0 && dummy[15] == 0, "small cap: nothing copied");

        // Unknown and released id; wrong kind: BadArgument; no preview (not Ready, no flag): NoData.
        s.Check(NativeCalls.ReadPreview(0xdeadbeefUL << 8, Span<byte>.Empty, out _) == (int)Native.Result.UnknownId, "unknown id -> UnknownId");
        var pending = Create();
        s.Check(NativeCalls.ReadPreview(pending, Span<byte>.Empty, out _) == (int)Native.Result.NoData, "Pending resource -> NoData");
        s.Check(Native.vlqt_res_create(TestProtocol.ResourceKind.Test, out var testId) == Native.Result.Ok && NativeCalls.ReadPreview(testId, Span<byte>.Empty, out _) == (int)Native.Result.BadArgument, "test resource (wrong kind) -> BadArgument");
        Native.vlqt_res_release(testId);
        Native.vlqt_res_release(pending);

        // set_pixels (B8G8R8A8 premultiplied): the flags travel in desc.flags.
        const int pw = 4000, ph = 2000;
        var data = new byte[pw * ph * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = 255; data[i + 1] = 0; data[i + 2] = 0; data[i + 3] = 255; } // blue
        ulong Pixels(uint flags)
        {
            var id = Create();
            var rc = Native.ImageSetPixels(id, new Native.vlqt_pixel_desc { width = pw, height = ph, format = 9, premultiplied = 1, flags = (int)flags }, data);
            s.Check(rc == 0, $"set_pixels flags {flags} ({rc})");
            s.Check(Native.vlqt_res_wait(id, 20000) == Protocol.ResourceState.Ready, $"pixels flags {flags} Ready");
            return id;
        }
        var p1 = Pixels(Protocol.ImageLoadFlags.Preview);
        if (Is(p1, 256, 128, "4000x2000 pixels"))
        {
            var (_, _, _, px) = Read(p1);
            s.Check(px[0] == 255 && px[1] == 0 && px[2] == 0 && px[3] == 255, $"pixels preview blue, BGRA {px[0]},{px[1]},{px[2]},{px[3]}");
        }
        var p0 = Pixels(0);
        s.Check(NativeCalls.ReadPreview(p0, Span<byte>.Empty, out _) == (int)Native.Result.NoData, "set_pixels without the flag -> NoData");

        // Replacement of a Ready resource: the preview belongs to the visible content until the swap.
        s.Check(Native.vlqt_image_load(a, small, Protocol.ImageLoadFlags.Preview) == 0, "replacement started");
        Is(a, 256, 128, "replacement in flight: old preview");
        var t = Stopwatch.StartNew();
        while (Read(a).w == 256 && t.ElapsedMilliseconds < 10000) s.Pump(1);
        Is(a, 100, 80, "replacement swapped: new preview");
        s.Check(Native.vlqt_image_load(a, big, 0) == 0, "replacement without the flag started");
        t.Restart();
        while (Read(a).rc != (int)Native.Result.NoData && t.ElapsedMilliseconds < 10000) s.Pump(1);
        s.Check(NativeCalls.ReadPreview(a, Span<byte>.Empty, out _) == (int)Native.Result.NoData, "replaced content without the flag -> NoData");

        // Released: UnknownId.
        foreach (var id in new[] { a, b, c, p1, p0 })
            Native.vlqt_res_release(id);
        s.Pump(5);
        s.Check(NativeCalls.ReadPreview(a, Span<byte>.Empty, out _) == (int)Native.Result.UnknownId, "released id -> UnknownId");
    }
}
