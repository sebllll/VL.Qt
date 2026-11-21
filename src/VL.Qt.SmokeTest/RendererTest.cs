using NUnit.Framework;
using System;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using QtRect = VL.Qt.Elements.Rectangle;

// Renderer [Qt] core (07a) without vvvv: WindowScene = reconciler + command buffer + poll dispatch of one window.
// Element tree → real QtNative, Clear Color, Enabled (frozen scene), Title at runtime, Dispose frees the registry.
// Grabs go to <outDir>/07a_*.png.
[TestFixture, NonParallelizable]
public static class RendererTest
{
    /// <summary>Renderer window and scene.</summary>
    [Test, Category("renderer")]
    public static void Renderer() => Scenario.Run(Run);

    /// <summary>VSync windows.</summary>
    [Test, Category("vsync")]
    public static void VSync() => Scenario.Run(RunVSync);

    /// <summary>Window geometry.</summary>
    [Test, Category("geometry")]
    public static void Geometry() => Scenario.Run(RunGeometry);

    /// <summary>Window visibility.</summary>
    [Test, Category("visibility")]
    public static void Visibility() => Scenario.Run(RunVisibility);

    /// <summary>Hot swap of the renderer.</summary>
    [Test, Category("hotswap")]
    public static void Hotswap() => Scenario.Run(RunHotswap);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rc { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rc r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc r, int size);

    private static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 8 && Math.Abs(c.G - g) < 8 && Math.Abs(c.B - b) < 8;

    private static void Run(Scenario s)
    {
        var scene = new SceneRegistry();
        var clear = new Color4(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        var title = "VL.Qt renderer test";
        var r = new WindowScene(scene, title, new RectangleF(100, 100, 800, 600));
        s.Check(r.Window != SceneHandle.Null && r.Error.Length == 0, "WindowScene creates the window " + r.Error);
        if (r.Window == SceneHandle.Null)
            return;

        // Bounds on create: outer top-left = Bounds.X/Y in DIP * DPI scale (the Skia rule).
        if (OperatingSystem.IsWindows()) // Linux: no Win32 window lookup (position check only on Windows)
        {
            var hwnd = FindWindow(null, title);
            s.Check(hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rc), "Qt window found by title");
            // Qt's frame (QWindow::framePosition) is the visible frame = DWM extended frame bounds; GetWindowRect
            // also contains the invisible resize borders (Windows 10+, left/right/bottom).
            if (hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out rc, 16) == 0)
            {
                uint dpi = GetDpiForWindow(hwnd);
                int ex = (int)MathF.Round(100 * (dpi == 0 ? 96 : dpi) / 96f);
                GetWindowRect(hwnd, out var wr);
                s.Check(Math.Abs(rc.Left - ex) <= 2 && Math.Abs(rc.Top - ex) <= 2,
                    $"visible window top-left = Bounds ({rc.Left},{rc.Top} vs {ex},{ex}; GetWindowRect {wr.Left},{wr.Top})");
            }
        }

        // Logical size of the window (Grab is in device pixels).
        int lw = 800, lh = 600;
        Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / lw, y * a.Height / lh);

        void Frame(Element? input, bool enabled = true)
        {
            r.Update(input, Space.DIPTopLeft, clear, true, enabled, title);
            r.ApplyCommands();
            s.Pump(3);
            r.AfterEvents();
        }

        var red = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), Fill = new Color4(1, 0, 0, 1) };
        var blue = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(4, 3, 0), Fill = new Color4(0, 0, 1, 1) };
        var tree = new Group { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Children = ImmutableArray.Create<Element>(red, blue) };
        Frame(tree);
        s.Check(r.Error.Length == 0, "apply tree ok " + r.Error);
        s.Check(r.IsVisible, "IsVisible after the first pump");
        s.Check(scene.Count == 3, $"registry owns group + 2 rects ({scene.Count})");
        int deviceWidth;
        using (var a = s.Grab(r.Window, "07a_tree.png"))
        {
            deviceWidth = a.Width;
            s.Check(Near(Px(a, 150, 150), 255, 0, 0), "red rect at (1,1) units = (100,100) DIP");
            s.Check(Near(Px(a, 500, 350), 0, 0, 255), "blue rect at (4,3) units");
            s.Check(Near(Px(a, 50, 50), 0x20, 0x20, 0x20), "clear color #202020");
        }

        // Clear color change.
        clear = new Color4(0, 0.5f, 0, 1);
        Frame(tree);
        using (var a = s.Grab(r.Window, "07a_clear.png"))
            s.Check(Near(Px(a, 50, 50), 0, 128, 0), "clear color changed at runtime");

        // Enabled = false freezes the scene: the moved rect stays where it was, so does the clear color.
        var moved = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(5, 1, 0), Fill = new Color4(1, 0, 0, 1) };
        var tree2 = new Group { Size = tree.Size, Anchor = tree.Anchor, Transform = tree.Transform, Children = ImmutableArray.Create<Element>(moved, blue) };
        clear = new Color4(0, 0, 0.5f, 1);
        Frame(tree2, enabled: false);
        using (var a = s.Grab(r.Window, "07a_disabled.png"))
        {
            s.Check(Near(Px(a, 150, 150), 255, 0, 0), "Enabled=false keeps the old scene");
            s.Check(Near(Px(a, 50, 50), 0, 128, 0), "Enabled=false keeps the old clear color");
        }
        Frame(tree2);
        using (var a = s.Grab(r.Window, "07a_enabled.png"))
        {
            s.Check(Near(Px(a, 550, 150), 255, 0, 0) && Near(Px(a, 150, 150), 0, 0, 128), "Enabled=true applies the moved rect");
            s.Check(Near(Px(a, 500, 350), 0, 0, 255), "unchanged sibling stays");
        }

        // Title at runtime (only sent when the pin value changes); resize via QtNative (the Size pin is gone).
        title = "VL.Qt renderer test (renamed)";
        Native.vlqt_window_resize(r.Window, 640, 480);
        Frame(tree2);
        // X11: the window manager answers the resize asynchronously (ConfigureNotify), wait for the new size.
        for (int i = 0; i < 100 && r.Geometry?.Bounds.Width != 640; i++) { System.Threading.Thread.Sleep(10); Frame(tree2); }
        lw = 640; lh = 480;
        using (var a = s.Grab(r.Window, "07a_resized.png"))
        {
            int expected = deviceWidth * 640 / 800;
            s.Check(Math.Abs(a.Width - expected) <= 2, $"vlqt_window_resize resizes the window ({a.Width} vs {expected} device px)");
            s.Check(Near(Px(a, 550, 150), 255, 0, 0), "scene survives the resize");
        }
        s.Check(Native.vlqt_window_set_title(r.Window, title) == 0, "set_title ok");
        s.Check(Native.vlqt_window_set_title(SceneHandle.Null, "x") == Native.Result.NullHandle, "set_title null window -> NullHandle");
        s.Check(Native.vlqt_window_resize(SceneHandle.Null, 1, 1) == Native.Result.NullHandle, "resize null window -> NullHandle");

        // Null input = empty scene: everything destroyed, registry empty.
        Frame(null);
        s.Check(scene.Count == 0, $"null input frees the registry ({scene.Count})");
        using (var a = s.Grab(r.Window, "07a_empty.png"))
            s.Check(Near(Px(a, 550, 150), 0, 0, 128), "null input clears the window");

        // Dispose: Clear + apply + destroy; the registry is free again (F8/F5, renderer deleted).
        Frame(tree);
        s.Check(scene.Count == 3, "tree shown again");
        r.Dispose();
        s.Check(scene.Count == 0, $"Dispose clears the reconciler ({scene.Count})");
        s.Check(r.Window == SceneHandle.Null, "Dispose destroys the window");
        r.AfterEvents(); // pump after dispose: no-op
        s.Check(!r.IsVisible, "not visible after Dispose");
        r.Dispose();      // idempotent
        s.Pump(2);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, SceneHandle w, IntPtr l);
    private const uint WM_CLOSE = 0x0010;

    // Is Visible: live from Qt, true from the creation on (no pump needed), false after X/Hide, true again after
    // Show, false after Dispose, true for a new window after a Dispose (node recreated, Undo, F8/F5 with a new registry).
    /// <summary>
    /// VSync pin: the default window (no vSync argument = swap interval 1) and the opt-out window (vSync: false = swap
    /// interval 0) are created and pumped without errors (with VSync the pump may wait for the refresh).
    /// </summary>
    private static void RunVSync(Scenario s)
    {
        VSyncWindow(s, "default (VSync on)", "VL.Qt vsync test", null);
        VSyncWindow(s, "vSync: false (opt-out)", "VL.Qt vsync off test", false);
    }

    private static void VSyncWindow(Scenario s, string what, string title, bool? vSync)
    {
        var bounds = new RectangleF(0, 0, 320, 240);
        var r = vSync is bool v
            ? new WindowScene(new SceneRegistry(), title, bounds, vSync: v)
            : new WindowScene(new SceneRegistry(), title, bounds);
        s.Check(r.Window != SceneHandle.Null, $"{what} window created " + r.Error);
        if (r.Window == SceneHandle.Null)
            return;
        var clear = new Color4(0, 0, 0, 1);
        for (int i = 0; i < 5; i++)
        {
            r.Update(null, Space.DIPTopLeft, clear, true, true, title);
            r.ApplyCommands();
            s.Pump(2);
            r.AfterEvents();
        }
        s.Check(r.IsVisible && r.Error.Length == 0, $"{what} window visible and pumped without error: " + r.Error);
        r.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, out Rc rect, uint winIni);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>
    /// Geometry on create + EVENT_WINDOW_GEOMETRY (prep for #143): the reported geometry (outer top-left + client
    /// size, DIP) equals the Bounds given on create, so it round-trips; a resize is reported; empty bounds (sentinel) and
    /// bounds on no screen are both centered on the primary working area with the fallback size.
    /// </summary>
    private static void RunGeometry(Scenario s)
    {
        var clear = new Color4(0, 0, 0, 1);
        // Pumps until the reported geometry has settled: under X11 the window manager answers placement, resize and
        // the frame extents asynchronously; on Windows it is stable after the first frames.
        WindowGeometry? Shown(WindowScene r, string title)
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
        // WSLg (Weston's X window manager): the frame extents arrive late or not at all, include a 32 px invisible
        // shadow and change after a resize, so Qt's frame position is off by up to shadow + border (seen 0..38 DIP).
        // Position is only checked roughly there, size exactly. Real X11 desktops: exact like Windows.
        float posTol = !OperatingSystem.IsWindows() && File.Exists("/dev/dxg")
            && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb" ? 40 : 1;
        if (posTol > 1) s.Info($"geometry: WSLg/Weston, frame position checked with {posTol} DIP tolerance");
        bool Close(RectangleF a, RectangleF b, float tol = 1) =>
            Math.Abs(a.X - b.X) <= Math.Max(tol, posTol) && Math.Abs(a.Y - b.Y) <= Math.Max(tol, posTol)
            && Math.Abs(a.Width - b.Width) <= tol && Math.Abs(a.Height - b.Height) <= tol;

        var bounds = new RectangleF(150, 120, 500, 300);
        var r = new WindowScene(new SceneRegistry(), "VL.Qt geometry test", bounds);
        int events = 0;
        r.GeometryChanged += _ => events++;
        var g = Shown(r, "VL.Qt geometry test");
        s.Check(g is { } g0 && Close(g0.Bounds, bounds) && g0.IsVisible && !g0.IsMinimized,
            $"reported geometry = Bounds on create ({g?.Bounds} vs {bounds}, {g?.States})");
        s.Check(events >= 1, $"GeometryChanged raised after creation ({events})");

        // Resize via QtNative → new client size reported, position stays.
        int before = events;
        Native.vlqt_window_resize(r.Window, 420, 260);
        g = Shown(r, "VL.Qt geometry test");
        s.Check(g is { } g1 && Close(g1.Bounds, new RectangleF(bounds.X, bounds.Y, 420, 260)) && events > before,
            $"resize reported ({g?.Bounds})");

        // Round trip: the reported geometry as Bounds of a new window gives the same geometry (no wandering).
        var reported = g!.Value.Bounds;
        r.Dispose();
        var r2 = new WindowScene(new SceneRegistry(), "VL.Qt geometry round trip", reported);
        var g2 = Shown(r2, "VL.Qt geometry round trip");
        s.Check(g2 is { } h2 && Close(h2.Bounds, reported, 0.01f), $"round trip stable ({g2?.Bounds} vs {reported})");
        r2.Dispose();

        // Sentinel (empty bounds) = 600x400 centered; bounds on no screen = centered as well.
        var rc = new WindowScene(new SceneRegistry(), "VL.Qt geometry centered", default);
        var gc = Shown(rc, "VL.Qt geometry centered");
        rc.Dispose();
        var ro = new WindowScene(new SceneRegistry(), "VL.Qt geometry off screen", new RectangleF(50000, 50000, 600, 400));
        var go = Shown(ro, "VL.Qt geometry off screen");
        ro.Dispose();
        s.Check(gc is { } c && c.Bounds.Width == 600 && c.Bounds.Height == 400, $"empty bounds → 600x400 ({gc?.Bounds})");
        s.Check(gc is { } c1 && go is { } o1 && Close(c1.Bounds, o1.Bounds), $"off-screen bounds centered like the sentinel ({go?.Bounds} vs {gc?.Bounds})");
        if (OperatingSystem.IsWindows() && gc is { } cw && SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, out var wa, 0))
        {
            // Primary working area in DIP (primary at system DPI); the client rect (frame ignored) is centered on it.
            float dpi = GetDpiForSystem() / 96f;
            float cx = (wa.Left + wa.Right) / 2f / dpi, cy = (wa.Top + wa.Bottom) / 2f / dpi;
            s.Check(Math.Abs(cw.Bounds.X + 300 - cx) <= 2 && Math.Abs(cw.Bounds.Y + 200 - cy) <= 2,
                $"sentinel centered on the primary working area ({cw.Bounds.X + 300},{cw.Bounds.Y + 200} vs {cx},{cy})");
        }
        s.Pump(2);
    }

    /// <summary>
    /// a Renderer added live or restored by Undo/Redo is created by vvvv's hotswap with the VL type default for
    /// Bounds, (-0.5, -0.5, 1, 1), instead of the pin value/default. While a first window is pumped, a second one is
    /// created the way Renderer does it (WindowPlacement.CreateBounds), disposed (Undo) and created again (Redo): each must open
    /// with the pin default size, not as a 1x1 window, and the first window keeps being pumped.
    /// </summary>
    private static void RunHotswap(Scenario s)
    {
        var clear = new Color4(0, 0, 0, 1);
        var vlDefault = new RectangleF(-0.5f, -0.5f, 1, 1); // AppHost.GetDefaultValue<RectangleF>() in vvvv 8.0-0156
        WindowGeometry? Frames(WindowScene r, string title, int n = 40)
        {
            for (int i = 0; i < n; i++)
            {
                r.Update(null, Space.DIPTopLeft, clear, true, true, title);
                r.ApplyCommands();
                s.Pump(2);
                r.AfterEvents();
            }
            return r.Geometry;
        }
        var first = new WindowScene(new SceneRegistry(), "VL.Qt hotswap first", new RectangleF(100, 100, 320, 240));
        Frames(first, "VL.Qt hotswap first", 10);
        var want = WindowPlacement.DefaultBounds;
        for (int round = 0; round < 2; round++) // 0: added live, 1: Undo (dispose) + Redo (create again)
        {
            var what = round == 0 ? "added live" : "Undo/Redo";
            var live = new WindowScene(new SceneRegistry(), "VL.Qt hotswap live", WindowPlacement.CreateBounds(vlDefault, vlDefault));
            var g = Frames(live, "VL.Qt hotswap live");
            s.Check(live.IsVisible && g is { } h && Math.Abs(h.Bounds.Width - want.Width) <= 1 && Math.Abs(h.Bounds.Height - want.Height) <= 1,
                $"renderer {what} opens with the Bounds pin default size ({g?.Bounds} vs {want.Width}x{want.Height})");
            live.Dispose();
            Frames(first, "VL.Qt hotswap first", 3);
        }
        s.Check(first.IsVisible && first.Error.Length == 0, "first renderer still shown and pumped " + first.Error);
        first.Dispose();
        s.Pump(2);
    }

    private static void RunVisibility(Scenario s)
    {
        const string title = "VL.Qt visibility test";
        var scene = new SceneRegistry();
        var clear = new Color4(0, 0, 0, 1);
        var r = new WindowScene(scene, title, new RectangleF(0, 0, 320, 240));
        s.Check(r.Window != SceneHandle.Null, "window created " + r.Error);
        if (r.Window == SceneHandle.Null)
            return;
        void Frame(WindowScene w)
        {
            w.Update(null, Space.DIPTopLeft, clear, true, true, title);
            w.ApplyCommands();
            s.Pump(2);
            w.AfterEvents();
        }

        s.Check(r.IsVisible, "Is Visible true right after creation (before the first pump)");
        Frame(r);
        s.Check(r.IsVisible, "Is Visible true after the first pump");

        // X: QtNative swallows the close, the Renderer hides on CloseRequested (as RendererNode.OnCloseRequested).
        int closes = 0;
        r.CloseRequested += () => { closes++; r.Hide(); };
        if (OperatingSystem.IsWindows()) // Linux: no way to send the X (WM_CLOSE) without a test hook in QtNative -> Hide instead
        {
            var hwnd = FindWindow(null, title);
            s.Check(hwnd != IntPtr.Zero && PostMessage(hwnd, WM_CLOSE, SceneHandle.Null, IntPtr.Zero), "WM_CLOSE posted (X)");
            Frame(r);
            s.Check(closes == 1 && !r.IsVisible, $"X hides, Is Visible false (closes {closes})");
        }
        else
        {
            s.Skip("X (WM_CLOSE) is Windows-only, hiding via Hide()");
            r.Hide();
            Frame(r);
            s.Check(!r.IsVisible, "Hide, Is Visible false");
        }
        Frame(r);
        s.Check(!r.IsVisible, "Is Visible stays false while hidden");

        Native.vlqt_window_show(r.Window);
        s.Check(r.IsVisible, "Is Visible true right after Show");
        Frame(r);
        s.Check(r.IsVisible, "Is Visible true after Show + pump");

        r.Hide();
        s.Check(!r.IsVisible, "Is Visible false right after Hide");
        Native.vlqt_window_show(r.Window);
        Frame(r);
        s.Check(r.IsVisible, "Hide -> Show -> Is Visible true");

        r.Dispose();
        s.Check(!r.IsVisible, "Is Visible false after Dispose");

        // Recreated (Undo of a delete, Bounds changed: same registry; F8/F5: new app = new registry), several times.
        for (int i = 0; i < 3; i++)
        {
            var reg = i == 0 ? scene : new SceneRegistry();
            var n = new WindowScene(reg, title, new RectangleF(0, 0, 320, 240));
            bool before = n.IsVisible;
            Frame(n);
            s.Check(before && n.IsVisible, $"recreated window #{i + 1} Is Visible true (before pump {before}, after {n.IsVisible})");
            n.Dispose();
            s.Check(!n.IsVisible, $"recreated window #{i + 1} Is Visible false after Dispose");
            s.Pump(1);
        }
    }
}
