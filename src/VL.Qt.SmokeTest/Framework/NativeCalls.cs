using System;
using VL.Qt;

/// <summary>
/// The buffer exports of QtNative in the shape most checks want: the bytes written (>= 0) on <see cref="Native.Result.Ok"/>, else the
/// negative <see cref="Native.Result"/> as an int (compare with <c>(int)Native.Result.X</c>). A check that needs the size asked for
/// by <see cref="Native.Result.BufferTooSmall"/> takes the <c>required</c> overload.
/// </summary>
internal static class NativeCalls
{
    private static int Count(Native.Result rc, int written) => rc == Native.Result.Ok ? written : (int)rc;

    public static int Poll(IntPtr w, Span<byte> buffer) => Poll(w, buffer, out _);

    public static int Poll(IntPtr w, Span<byte> buffer, out int required)
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
        var rc = Native.ResReadSnapshot(id, buffer, out desc, out var written, out required);
        return Count(rc, written);
    }

    public static int ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc) => ReadPreview(id, buffer, out desc, out _);

    public static int ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int required)
    {
        var rc = Native.ResReadPreview(id, buffer, out desc, out var written, out required);
        return Count(rc, written);
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
    public static int PerfText(IntPtr w, int page, byte[]? buffer, int cap, out int required)
    {
        var rc = NativeTest.vlqt_test_perf_text(w, page, buffer, cap, out var written, out required);
        return Count(rc, written);
    }

    /// <summary>The plain text of an F5 page without its NUL (the content may grow between the size query and the read: asks again).</summary>
    public static string PerfPageText(IntPtr w, int page)
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
}
