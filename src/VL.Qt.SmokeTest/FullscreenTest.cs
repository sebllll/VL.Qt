using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Qt;

// Fullscreen pin (#234) without vvvv: WindowRenderer.SetMode(Fullscreen) live and on create (before show).
// On → EV_WINDOW_GEOMETRY reports VLQT_WINDOW_FULLSCREEN and (Windows) the rect of the window's monitor in DIP;
// off → the geometry from before. Save Bounds (BoundsSaver) writes nothing during or after the round trip.
// A second monitor (Windows) is tested if present, otherwise skipped with INFO.
static class FullscreenTest
{
    [StructLayout(LayoutKind.Sequential)] struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rc Monitor, Work; public uint Flags; }
    delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rc rect, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr m, ref MonitorInfo mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum cb, IntPtr data);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint dpiX, out uint dpiY);

    // Monitor rect in Qt DIP: Qt 6 keeps a screen's top-left in native pixels and scales the size by its DPI.
    static RectangleF MonitorDip(IntPtr m)
    {
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(m, ref mi);
        GetDpiForMonitor(m, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _);
        float s = dpi / 96f;
        return new RectangleF(mi.Monitor.Left, mi.Monitor.Top,
            (float)Math.Round((mi.Monitor.Right - mi.Monitor.Left) / s), (float)Math.Round((mi.Monitor.Bottom - mi.Monitor.Top) / s));
    }

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        var clear = new Color4(0, 0, 0, 1);
        var clock = Stopwatch.StartNew();
        WindowGeometry? Settle(WindowRenderer r, string title)
        {
            WindowGeometry? last = null;
            for (int i = 0, stable = 0; i < 200 && stable < 5; i++)
            {
                r.Update(null, Space.DIPTopLeft, clear, true, title);
                r.ApplyCommands();
                pump(2);
                r.AfterEvents();
                stable = i >= 3 && Equals(r.Geometry, last) ? stable + 1 : 0;
                last = r.Geometry;
                if (stable > 0) System.Threading.Thread.Sleep(20);
            }
            return r.Geometry;
        }
        // Same tolerance as RendererTest.RunGeometry: WSLg/Weston reports the frame position late and with a shadow (#232).
        float posTol = !OperatingSystem.IsWindows() && File.Exists("/dev/dxg")
            && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb" ? 40 : 1;
        bool Close(RectangleF a, RectangleF b, float tol = 1) =>
            Math.Abs(a.X - b.X) <= Math.Max(tol, posTol) && Math.Abs(a.Y - b.Y) <= Math.Max(tol, posTol)
            && Math.Abs(a.Width - b.Width) <= tol && Math.Abs(a.Height - b.Height) <= tol;

        bool SameSize(RectangleF a, RectangleF b) => Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1;

        void RoundTrip(string label, RectangleF bounds, bool onCreate, Func<IntPtr, RectangleF?>? expectedScreen)
        {
            string title = "VL.Qt fullscreen " + label;
            var saver = new BoundsSaver(bounds);
            var r = new WindowRenderer(new SceneRegistry(), title, bounds, false, onCreate ? Protocol.WindowMode.Fullscreen : 0u);
            r.GeometryChanged += g => saver.OnGeometry(g, clock.Elapsed);
            var g = Settle(r, title);
            if (!onCreate)
            {
                check(g is { } n && !n.IsFullscreen && Close(n.Bounds, bounds), $"234 {label}: windowed before ({g?.Bounds}, {g?.States})");
                r.SetMode(Protocol.WindowMode.Fullscreen, true);
                g = Settle(r, title);
            }
            check(Native.vlqt_window_get_mode(r.Window) == Protocol.WindowMode.Fullscreen, $"234 {label}: requested mode = Fullscreen");
            check(g is { } f && f.IsFullscreen && f.IsVisible, $"234 {label}: EV_WINDOW_GEOMETRY reports Fullscreen ({g?.States}{(onCreate ? ", on create" : "")})");
            var hwnd = OperatingSystem.IsWindows() ? FindWindow(null, title) : IntPtr.Zero;
            if (expectedScreen?.Invoke(hwnd) is { } screen)
                check(g is { } f1 && Close(f1.Bounds, screen), $"234 {label}: fullscreen rect = monitor ({g?.Bounds} vs {screen})");
            else
                check(g is { } f2 && f2.Bounds.Width > bounds.Width && f2.Bounds.Height > bounds.Height,
                    $"234 {label}: fullscreen larger than the window ({g?.Bounds})");
            // Save Bounds: nothing to write while fullscreen, even after the delay.
            System.Threading.Thread.Sleep((int)BoundsSaver.Delay.TotalMilliseconds + 100);
            Settle(r, title);
            // Under WSLg/xcb the window manager places the window itself, so the saver sees a position other than the requested
            // one before fullscreen; the position part of these checks is a SKIP there, sizes stay strict (Program.PositionByWindowManager).
            var byWm = Program.PositionByWindowManager(check);
            bool wroteFs = saver.TryTake(clock.Elapsed, out var w1);
            byWm(!wroteFs, $"143/234 {label}: Bounds not written while fullscreen ({w1})");

            r.SetMode(Protocol.WindowMode.Fullscreen, false);
            g = Settle(r, title);
            check(g is { } o && !o.IsFullscreen && SameSize(o.Bounds, bounds), $"234 {label}: off restores size ({g?.Bounds} vs {bounds}, {g?.States})");
            byWm(g is { } o2 && !o2.IsFullscreen && Close(o2.Bounds, bounds), $"234 {label}: off restores position + size ({g?.Bounds} vs {bounds}, {g?.States})");
            System.Threading.Thread.Sleep((int)BoundsSaver.Delay.TotalMilliseconds + 100);
            Settle(r, title);
            bool wrote = saver.TryTake(clock.Elapsed, out var w2);
            check(!wrote || SameSize(w2, bounds), $"143/234 {label}: Bounds after the round trip: size unchanged ({(wrote ? w2.ToString() : "nothing written")})");
            byWm(!wrote || Close(w2, bounds), $"143/234 {label}: Bounds after the round trip unchanged ({(wrote ? w2.ToString() : "nothing written")})");
            r.Dispose();
            pump(2);
        }

        Func<IntPtr, RectangleF?>? ofWindow = OperatingSystem.IsWindows()
            ? h => h == IntPtr.Zero ? null : MonitorDip(MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */))
            : null;
        RoundTrip("live", new RectangleF(150, 120, 500, 300), false, ofWindow);
        RoundTrip("on create", new RectangleF(180, 140, 480, 280), true, ofWindow);

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("INFO fullscreen: second monitor only checked on Windows");
            return;
        }
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Rc rc, IntPtr d) => { monitors.Add(m); return true; }, IntPtr.Zero);
        IntPtr second = IntPtr.Zero;
        foreach (var m in monitors)
        {
            var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(m, ref mi) && (mi.Flags & 1 /* MONITORINFOF_PRIMARY */) == 0) { second = m; break; }
        }
        if (second == IntPtr.Zero)
        {
            Console.WriteLine($"INFO fullscreen: {monitors.Count} monitor(s), second-monitor case skipped");
            return;
        }
        var s2 = MonitorDip(second);
        GetDpiForMonitor(second, 0, out var dpi2, out _);
        Console.WriteLine($"INFO fullscreen: second monitor {s2} at {dpi2 * 100 / 96} %");
        RoundTrip("second monitor", new RectangleF(s2.X + 60, s2.Y + 60, 400, 260), false, _ => s2);
    }
}
