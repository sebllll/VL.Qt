using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;
using VL.Qt;
using VL.Qt.Interop;

// Window icon: vlqt_window_set_icon return codes and, on Windows, the real icon of the HWND (WM_GETICON).
// Default at create (embedded PNGs, no file), custom PNG, NULL/empty = default again, missing/broken file = Io and
// the default instead of the custom icon, null window = -1. Linux: return codes only (no WM_GETICON).
[TestFixture, NonParallelizable]
public static class WindowIconTest
{
    /// <summary>Window icon.</summary>
    [Test, Category("window-icon")]
    public static void WindowIcon() => Scenario.Run(Run);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_GETICON = 0x007F;

    private static void Run(Scenario s)
    {
        const string title = "VL.Qt window icon test";
        var w = NativeCalls.CreateWindow(400, 300, title);
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(10);

        IntPtr Big()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var h = FindWindow(null, title);
            return h == IntPtr.Zero ? IntPtr.Zero : SendMessage(h, WM_GETICON, 1 /* ICON_BIG */, IntPtr.Zero);
        }
        bool win = OperatingSystem.IsWindows();

        var def = Big();
        s.Check(!win || def != IntPtr.Zero, "default icon set at create (HWND has a big icon)");

        var png = Path.Combine(s.OutDir, "window-icon-test.png");
        Directory.CreateDirectory(s.OutDir);
        // 1x1 PNG from bytes: System.Drawing is not available in the Linux SmokeTest (own Bitmap shim, no Save).
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAEBgIApD5fRAAAAABJRU5ErkJggg=="));

        var rc = Native.vlqt_window_set_icon(w, png);
        s.Check(rc == 0, $"set_icon custom png -> 0 ({rc}: {Native.LastError()})");
        s.Pump(5);
        var custom = Big();
        s.Check(!win || (custom != IntPtr.Zero && custom != def), "custom icon replaces the default (HWND)");

        rc = Native.vlqt_window_set_icon(w, Path.Combine(s.OutDir, "no-such-icon.png"));
        s.Check(rc == Native.Result.Io, $"missing file -> Io ({rc})");
        s.Check(Native.LastError().Contains("not loadable"), $"error text ({Native.LastError()})");
        s.Pump(5);
        s.Check(!win || (Big() != IntPtr.Zero && Big() != custom), "default icon instead after a failed load");

        var broken = Path.Combine(s.OutDir, "window-icon-broken.png");
        File.WriteAllText(broken, "not an image");
        rc = Native.vlqt_window_set_icon(w, broken);
        s.Check(rc == Native.Result.Io, $"broken file -> Io ({rc})");

        Native.vlqt_window_set_icon(w, png);
        s.Pump(5);
        rc = Native.vlqt_window_set_icon(w, null);
        s.Check(rc == 0, $"NULL -> default, 0 ({rc})");
        s.Pump(5);
        s.Check(!win || (Big() != IntPtr.Zero && Big() != custom), "NULL resets to the default (HWND)");
        Native.vlqt_window_set_icon(w, png);
        rc = Native.vlqt_window_set_icon(w, "");
        s.Check(rc == 0, $"empty path -> default, 0 ({rc})");

        s.Check(Native.vlqt_window_set_icon(SceneHandle.Null, png) == Native.Result.NullHandle, "null window -> NullHandle");

        Native.vlqt_scene_destroy(w);
        File.Delete(png);
        File.Delete(broken);
    }
}
