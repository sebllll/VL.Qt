using System;
using System.Diagnostics;
using System.IO;
using VL.Qt;

/// <summary>
/// #546 (tooltips package 03): the tooltip preview of image resources. With VLQT_IMG_PREVIEW the decode worker keeps a
/// BGRA8 premultiplied copy with a longest edge of 256 px (never upscaled); readable on a Ready resource only, without
/// the flag -7. Files and set_pixels, a replacement shows the preview of the visible content. No window needed.
/// </summary>
static class ImagePreviewTest
{
    public static void Run(string outDir, Action<bool, string> check, Action<int> pump)
    {
        var dir = Path.Combine(outDir, "image-preview");
        Directory.CreateDirectory(dir);
        var big = Path.Combine(dir, "big.png");
        var small = Path.Combine(dir, "small.png");
        check(Native.vlqt_test_write_image(big, 4000, 2000, 90) == 0, "546: 4000x2000 test image written");
        check(Native.vlqt_test_write_image(small, 100, 80, 90) == 0, "546: 100x80 test image written");

        ulong Create()
        {
            var rc = Native.vlqt_res_create(Protocol.ResKind.Image, out var id);
            if (rc != 0) check(false, $"546: create ({rc})");
            return id;
        }
        ulong Load(string path, uint flags)
        {
            var id = Create();
            check(Native.vlqt_res_load_image(id, path, flags) == 0, $"546: load {Path.GetFileName(path)} flags {flags}");
            check(Native.vlqt_res_wait(id, 20000) == Protocol.ResState.Ready, $"546: {Path.GetFileName(path)} Ready");
            return id;
        }
        // rc, size and the first pixel (BGRA) of the preview.
        (int rc, int w, int h, byte[] px) Read(ulong id)
        {
            var rc = Native.ResReadPreview(id, Span<byte>.Empty, out var d);
            if (rc >= 0 || rc == -7 || rc == -9) return (rc, d.width, d.height, []);
            var px = new byte[-rc];
            var n = Native.ResReadPreview(id, px, out d);
            return (n, d.width, d.height, px);
        }
        bool Is(ulong id, int w, int h, string what)
        {
            var (rc, rw, rh, px) = Read(id);
            var ok = rc == w * h * 4 && rw == w && rh == h && px.Length == rc;
            check(ok, $"546: {what}: preview {w}x{h} (rc {rc}, got {rw}x{rh})");
            return ok;
        }
        var dummy = new byte[16];

        // Files: with the flag 4000x2000 -> 256x128 (aspect kept), 100x80 stays, without the flag -7.
        var a = Load(big, Protocol.Img.Preview);
        if (Is(a, 256, 128, "4000x2000 file"))
        {
            var (_, _, _, px) = Read(a);
            check(px[0] == 0 && px[1] == 0 && px[2] == 255 && px[3] == 255, $"546: first stripe red, BGRA {px[0]},{px[1]},{px[2]},{px[3]}");
        }
        var b = Load(small, Protocol.Img.Preview);
        Is(b, 100, 80, "100x80 file not upscaled");
        var c = Load(big, 0);
        check(Native.ResReadPreview(c, Span<byte>.Empty, out _) == -7, "546: without the flag -> -7");
        check(Native.ResReadPreview(c, dummy, out _) == -7, "546: without the flag, with a buffer -> -7");

        // cap too small: -required with the desc filled, nothing copied.
        var rcSmall = Native.ResReadPreview(a, dummy, out var d);
        check(rcSmall == -256 * 128 * 4 && d.width == 256 && d.height == 128 && d.row_bytes == 256 * 4 && d.format == 9 && d.premultiplied == 1, $"546: small cap -> -required, desc filled ({rcSmall}, {d.width}x{d.height} rb {d.row_bytes} fmt {d.format} pm {d.premultiplied})");
        check(dummy[0] == 0 && dummy[15] == 0, "546: small cap: nothing copied");

        // Unknown, released and wrong kind; not Ready (Pending) -> -7.
        check(Native.ResReadPreview(0xdeadbeefUL << 8, Span<byte>.Empty, out _) == -9, "546: unknown id -> -9");
        var pending = Create();
        check(Native.ResReadPreview(pending, Span<byte>.Empty, out _) == -7, "546: Pending resource -> -7");
        check(Native.vlqt_res_create(Protocol.ResKind.Test, out var testId) == 0 && Native.ResReadPreview(testId, Span<byte>.Empty, out _) == -7, "546: test resource (wrong kind) -> -7");
        Native.vlqt_res_release(testId);
        Native.vlqt_res_release(pending);

        // set_pixels (B8G8R8A8 premultiplied): the flags travel in desc.flags.
        const int pw = 4000, ph = 2000;
        var data = new byte[pw * ph * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = 255; data[i + 1] = 0; data[i + 2] = 0; data[i + 3] = 255; } // blue
        ulong Pixels(uint flags)
        {
            var id = Create();
            var rc = Native.ResSetPixels(id, new Native.vlqt_pixel_desc { width = pw, height = ph, format = 9, premultiplied = 1, flags = (int)flags }, data);
            check(rc == 0, $"546: set_pixels flags {flags} ({rc})");
            check(Native.vlqt_res_wait(id, 20000) == Protocol.ResState.Ready, $"546: pixels flags {flags} Ready");
            return id;
        }
        var p1 = Pixels(Protocol.Img.Preview);
        if (Is(p1, 256, 128, "4000x2000 pixels"))
        {
            var (_, _, _, px) = Read(p1);
            check(px[0] == 255 && px[1] == 0 && px[2] == 0 && px[3] == 255, $"546: pixels preview blue, BGRA {px[0]},{px[1]},{px[2]},{px[3]}");
        }
        var p0 = Pixels(0);
        check(Native.ResReadPreview(p0, Span<byte>.Empty, out _) == -7, "546: set_pixels without the flag -> -7");

        // Replacement of a Ready resource: the preview belongs to the visible content until the swap.
        check(Native.vlqt_res_load_image(a, small, Protocol.Img.Preview) == 0, "546: replacement started");
        Is(a, 256, 128, "replacement in flight: old preview");
        var t = Stopwatch.StartNew();
        while (Read(a).w == 256 && t.ElapsedMilliseconds < 10000) pump(1);
        Is(a, 100, 80, "replacement swapped: new preview");
        check(Native.vlqt_res_load_image(a, big, 0) == 0, "546: replacement without the flag started");
        t.Restart();
        while (Read(a).rc != -7 && t.ElapsedMilliseconds < 10000) pump(1);
        check(Native.ResReadPreview(a, Span<byte>.Empty, out _) == -7, "546: replaced content without the flag -> -7");

        // Released: -9.
        foreach (var id in new[] { a, b, c, p1, p0 })
            Native.vlqt_res_release(id);
        pump(5);
        check(Native.ResReadPreview(a, Span<byte>.Empty, out _) == -9, "546: released id -> -9");
    }
}
