using System;
using System.IO;
using System.Runtime.InteropServices;
using VL.Qt;

// Window icon (#517): vlqt_window_set_icon return codes and, on Windows, the real icon of the HWND (WM_GETICON).
// Default at create (embedded PNGs, no file), custom PNG, NULL/empty = default again, missing/broken file = -7 and
// the default instead of the custom icon, null window = -1. Linux: return codes only (no WM_GETICON).
static class WindowIconTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    const uint WM_GETICON = 0x007F;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump)
    {
        const string title = "VL.Qt window icon test";
        var w = Native.vlqt_window_create(400, 300, title);
        check(w != IntPtr.Zero, "517: window_create");
        Native.vlqt_window_show(w);
        pump(10);

        IntPtr Big()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var h = FindWindow(null, title);
            return h == IntPtr.Zero ? IntPtr.Zero : SendMessage(h, WM_GETICON, 1 /* ICON_BIG */, IntPtr.Zero);
        }
        bool win = OperatingSystem.IsWindows();

        var def = Big();
        check(!win || def != IntPtr.Zero, "517: default icon set at create (HWND has a big icon)");

        var png = Path.Combine(outDir, "window-icon-test.png");
        Directory.CreateDirectory(outDir);
        // 1x1 PNG from bytes: System.Drawing is not available in the Linux SmokeTest (own Bitmap shim, no Save).
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAEBgIApD5fRAAAAABJRU5ErkJggg=="));

        var rc = Native.vlqt_window_set_icon(w, png);
        check(rc == 0, $"517: set_icon custom png -> 0 ({rc}: {Native.LastError()})");
        pump(5);
        var custom = Big();
        check(!win || (custom != IntPtr.Zero && custom != def), "517: custom icon replaces the default (HWND)");

        rc = Native.vlqt_window_set_icon(w, Path.Combine(outDir, "no-such-icon.png"));
        check(rc == -7, $"517: missing file -> -7 ({rc})");
        check(Native.LastError().Contains("not loadable"), $"517: error text ({Native.LastError()})");
        pump(5);
        check(!win || (Big() != IntPtr.Zero && Big() != custom), "517: default icon instead after a failed load");

        var broken = Path.Combine(outDir, "window-icon-broken.png");
        File.WriteAllText(broken, "not an image");
        rc = Native.vlqt_window_set_icon(w, broken);
        check(rc == -7, $"517: broken file -> -7 ({rc})");

        Native.vlqt_window_set_icon(w, png);
        pump(5);
        rc = Native.vlqt_window_set_icon(w, null);
        check(rc == 0, $"517: NULL -> default, 0 ({rc})");
        pump(5);
        check(!win || (Big() != IntPtr.Zero && Big() != custom), "517: NULL resets to the default (HWND)");
        Native.vlqt_window_set_icon(w, png);
        rc = Native.vlqt_window_set_icon(w, "");
        check(rc == 0, $"517: empty path -> default, 0 ({rc})");

        check(Native.vlqt_window_set_icon(IntPtr.Zero, png) == -1, "517: null window -> -1");

        Native.vlqt_window_destroy(w);
        File.Delete(png);
        File.Delete(broken);
    }
}
