using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// Borderless pin via vlqt_window_set_mode: live toggle keeps the client area in place (reported outer top-left
/// moves by the frame, client size stays), a borderless window can be smaller than the captioned minimum width, a
/// window created borderless reports exactly its Bounds (outer top-left = client top-left), toggling back restores the
/// framed geometry.
/// </summary>
[TestFixture, NonParallelizable]
public static class BorderlessTest
{
    /// <summary>Borderless window.</summary>
    [Test, Category("borderless")]
    public static void Borderless() => Scenario.Run(Run);

    private struct Pt { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Pt p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);

    private static void Run(Scenario s)
    {
        var clear = new Color4(0, 0, 0, 1);
        // WSLg/Weston: frame extents late and with shadow (see RendererTest.RunGeometry): position only roughly.
        float posTol = !OperatingSystem.IsWindows() && File.Exists("/dev/dxg")
            && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb" ? 40 : 1;
        bool Close(RectangleF a, RectangleF b) =>
            Math.Abs(a.X - b.X) <= posTol && Math.Abs(a.Y - b.Y) <= posTol
            && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1;

        WindowGeometry? Shown(WindowRenderer r, string title)
        {
            WindowGeometry? last = null;
            for (int i = 0, stable = 0; i < 200 && stable < 5; i++)
            {
                r.Update(null, Space.DIPTopLeft, clear, true, true, title);
                r.ApplyCommands();
                s.Pump(2);
                r.AfterEvents();
                stable = i >= 3 && Equals(r.Geometry, last) ? stable + 1 : 0;
                last = r.Geometry;
                if (stable > 0) System.Threading.Thread.Sleep(20);
            }
            return r.Geometry;
        }
        // Windows: client top-left in DIP straight from user32 (independent of Qt's frame bookkeeping).
        Vector2? Client(string title)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var h = FindWindow(null, title);
            var p = new Pt();
            if (h == IntPtr.Zero || !ClientToScreen(h, ref p)) return null;
            float s = GetDpiForWindow(h) / 96f;
            return new Vector2(p.X / s, p.Y / s);
        }

        const string title = "VL.Qt borderless test";
        var bounds = new RectangleF(200, 160, 400, 300);
        var r = new WindowRenderer(new SceneRegistry(), title, bounds);
        var g0 = Shown(r, title);
        var c0 = Client(title);
        s.Check(g0 is { } a0 && Close(a0.Bounds, bounds), $"framed window at Bounds ({g0?.Bounds})");

        r.SetMode(Protocol.WindowMode.Borderless, true);
        var g1 = Shown(r, title);
        var c1 = Client(title);
        s.Check(Native.vlqt_window_get_mode(r.Window) == Protocol.WindowMode.Borderless, "get_mode = Borderless");
        s.Check(g1 is { } a1 && g0 is { } b1 && a1.Bounds.Width == b1.Bounds.Width && a1.Bounds.Height == b1.Bounds.Height
            && a1.Bounds.Y >= b1.Bounds.Y && a1.Bounds.X >= b1.Bounds.X && a1.IsVisible,
            $"borderless on: client size kept, outer top-left moved in by the frame ({g0?.Bounds} -> {g1?.Bounds})");
        // Wayland (or a window manager that ignores the hint): no frame to remove, position unchanged. offscreen has 2 px
        // margins and drops them here too since #255 (before, its margins were only refreshed by a later resize).
        if (g1 is { } f1 && g0 is { } f0 && f1.Bounds.Y == f0.Bounds.Y)
            s.Info($"borderless: no frame difference on this platform ({f0.Bounds} -> {f1.Bounds})");
        if (c0 is { } p0 && c1 is { } p1)
            s.Check(Math.Abs(p0.X - p1.X) <= 1 && Math.Abs(p0.Y - p1.Y) <= 1, $"client area stays in place ({p0} -> {p1})");
        if (c1 is { } q1 && g1 is { } h1)
            s.Check(Math.Abs(q1.X - h1.Bounds.X) <= 1 && Math.Abs(q1.Y - h1.Bounds.Y) <= 1,
                $"borderless: reported position = client top-left ({h1.Bounds} vs {q1})");

        // Smaller than the minimum width of a captioned window (Windows ~130-150 px).
        Native.vlqt_window_resize(r.Window, 80, 50);
        var g2 = Shown(r, title);
        s.Check(g2 is { } a2 && a2.Bounds.Width == 80 && a2.Bounds.Height == 50, $"borderless 80x50 possible ({g2?.Bounds})");
        Native.vlqt_window_resize(r.Window, 400, 300);
        Shown(r, title);

        // Back to framed: same geometry as before the toggle (no wandering).
        r.SetMode(Protocol.WindowMode.Borderless, false);
        var g3 = Shown(r, title);
        var c3 = Client(title);
        s.Check(g3 is { } a3 && g0 is { } b3 && Close(a3.Bounds, b3.Bounds), $"borderless off restores the framed geometry ({g3?.Bounds} vs {g0?.Bounds})");
        if (c0 is { } p0b && c3 is { } p3)
            s.Check(Math.Abs(p0b.X - p3.X) <= 1 && Math.Abs(p0b.Y - p3.Y) <= 1, $"client area stays in place on frame on ({p0b} -> {p3})");
        // Info only: what the platform does with a captioned window that small.
        Native.vlqt_window_resize(r.Window, 80, 50);
        var g4 = Shown(r, title);
        s.Info($"borderless: framed window resized to 80x50 reports {g4?.Bounds.Width}x{g4?.Bounds.Height}");
        r.Dispose();

        // Created borderless (mode before show): reported geometry = Bounds, small size possible right away.
        const string title2 = "VL.Qt borderless on create";
        var small = new RectangleF(260, 220, 100, 60);
        var r2 = new WindowRenderer(new SceneRegistry(), title2, small, mode: Protocol.WindowMode.Borderless);
        var g5 = Shown(r2, title2);
        var c5 = Client(title2);
        s.Check(g5 is { } s5 && s5.Bounds.Width == small.Width && s5.Bounds.Height == small.Height, $"created borderless: size 100x60 ({g5?.Bounds} vs {small})");
        // The position is the window manager's call under WSLg/xcb (SKIP there, see Scenario.CheckPosition).
        s.CheckPosition(g5 is { } a5 && Close(a5.Bounds, small), $"created borderless at Bounds ({g5?.Bounds} vs {small})");
        if (c5 is { } p5)
            s.Check(Math.Abs(p5.X - small.X) <= 1 && Math.Abs(p5.Y - small.Y) <= 1, $"created borderless: client top-left = Bounds position ({p5})");
        r2.Dispose();
        s.Pump(2);
    }
}
