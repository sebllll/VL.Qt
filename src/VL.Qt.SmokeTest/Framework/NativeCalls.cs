using System;
using VL.Qt;
using VL.Qt.Interop;

/// <summary>
/// The buffer exports of QtNative in the shape most checks want: the bytes written (>= 0) on <see cref="Native.Result.Ok"/>, else the
/// negative <see cref="Native.Result"/> as an int (compare with <c>(int)Native.Result.X</c>). A check that needs the size asked for
/// by <see cref="Native.Result.BufferTooSmall"/> takes the <c>required</c> overload.
/// </summary>
internal static class NativeCalls
{
    private static int Count(Native.Result rc, int written) => rc == Native.Result.Ok ? written : (int)rc;

    /// <summary>A hidden window of the client size, centered on the primary screen, Present never waits.</summary>
    public static SceneHandle CreateWindow(int width, int height, string title)
        => Native.vlqt_window_create(float.NaN, float.NaN, width, height, title, 0);

    public static int Poll(SceneHandle w, Span<byte> buffer) => Poll(w, buffer, out _);

    public static int Poll(SceneHandle w, Span<byte> buffer, out int required)
    {
        var rc = Native.Poll(w, buffer, out var written, out required);
        return Count(rc, written);
    }

    public static int ResPoll(Span<byte> buffer) => ResPoll(buffer, out _);

    public static int ResPoll(Span<byte> buffer, out int required)
    {
        var rc = Native.ResPoll(buffer, out var written, out required);
        return Count(rc, written);
    }

    public static int ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc) => ReadSnapshot(id, buffer, out desc, out _);

    public static int ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int required)
    {
        var rc = Native.SnapshotRead(id, buffer, out desc, out var written, out required);
        return Count(rc, written);
    }

    public static int ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc) => ReadPreview(id, buffer, out desc, out _);

    public static int ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int required)
    {
        var rc = Native.ImageReadPreview(id, buffer, out desc, out var written, out required);
        return Count(rc, written);
    }

    /// <summary>
    /// Writes a test image of four vertical stripes (red, green, blue, white; opaque) as PNG/JPG by extension through the production
    /// writer <see cref="Native.ImageWriteFile"/>, for the checks that load a file of a known size and colors.
    /// </summary>
    public static Native.Result WriteStripedImage(string path, int width, int height, int quality)
    {
        // B8G8R8A8 (VL PixelFormat 9), premultiplied: opaque, so the same bytes as straight.
        ReadOnlySpan<uint> colors = [0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF];
        var row = new uint[width];
        for (int x = 0; x < width; x++)
            row[x] = colors[x * 4 / width];
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(row, 0, pixels, y * width * 4, width * 4);
        var desc = new Native.vlqt_pixel_desc { width = width, height = height, row_bytes = width * 4, format = 9, premultiplied = 1 };
        return Native.ImageWriteFile(in desc, pixels, path, quality, out _);
    }

    public static int ReadPixels(ulong id, Span<byte> buffer, out int width, out int height) => ReadPixels(id, buffer, out width, out height, out _);

    public static int ReadPixels(ulong id, Span<byte> buffer, out int width, out int height, out int required)
    {
        var rc = NativeTest.ResReadPixels(id, buffer, out width, out height, out var written, out required);
        return Count(rc, written);
    }

    public static int ReadTexture(IntPtr texture, Span<byte> buffer, out int width, out int height, out int dxgiFormat)
        => ReadTexture(texture, buffer, out width, out height, out dxgiFormat, out _);

    public static int ReadTexture(IntPtr texture, Span<byte> buffer, out int width, out int height, out int dxgiFormat, out int required)
    {
        var rc = NativeTest.TestReadTexture(texture, buffer, out width, out height, out dxgiFormat, out var written, out required);
        return Count(rc, written);
    }

    /// <summary>Text of an F5 page with its NUL (<c>buffer</c> null with cap 0 asks for the size).</summary>
    public static int PerfText(SceneHandle w, int page, byte[]? buffer, int cap, out int required)
    {
        var rc = NativeTest.vlqt_test_perf_text(w, page, buffer, cap, out var written, out required);
        return Count(rc, written);
    }

    /// <summary>The plain text of an F5 page without its NUL (the content may grow between the size query and the read: asks again).</summary>
    public static string PerfPageText(SceneHandle w, int page)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var rc = NativeTest.vlqt_test_perf_text(w, page, null, 0, out _, out var required);
            if (rc != Native.Result.BufferTooSmall)
                return "";
            var buffer = new byte[required];
            rc = NativeTest.vlqt_test_perf_text(w, page, buffer, buffer.Length, out var written, out _);
            if (rc == Native.Result.Ok)
                return written > 1 ? System.Text.Encoding.UTF8.GetString(buffer, 0, written - 1) : "";
        }
        return "";
    }

    /// <summary>
    /// Refresh period of the window's screen in ms, from the "refresh" row of the F5 System page (QScreen::refreshRate);
    /// null = unknown. The perf budget (budget_ms) is vvvv's frame interval, not the screen's.
    /// </summary>
    public static double? ScreenRefreshMs(SceneHandle w)
    {
        var m = System.Text.RegularExpressions.Regex.Match(PerfPageText(w, 3), @"refresh\s+(\d+(?:\.\d+)?) Hz");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hz)
            && hz >= 1 ? 1000.0 / hz : null;
    }
}
