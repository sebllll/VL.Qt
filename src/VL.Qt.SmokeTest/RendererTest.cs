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

// Renderer [Qt] core (07a) without vvvv: WindowRenderer = reconciler + command buffer + poll dispatch of one window.
// Element tree → real QtNative, Clear Color, Enabled (frozen scene), Title at runtime, Dispose frees the registry.
// Grabs go to <outDir>/07a_*.png.
static class RendererTest
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Rc { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rc r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc r, int size);

    static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 8 && Math.Abs(c.G - g) < 8 && Math.Abs(c.B - b) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var scene = new SceneRegistry();
        var clear = new Color4(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        var title = "VL.Qt renderer test";
        var r = new WindowRenderer(scene, title, new RectangleF(100, 100, 800, 600));
        check(r.Window != IntPtr.Zero && r.Error.Length == 0, "07a: WindowRenderer creates the window " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;

        // Bounds on create (#94): outer top-left = Bounds.X/Y in DIP * DPI scale (the Skia rule).
        if (OperatingSystem.IsWindows()) // Linux: no Win32 window lookup (position check only on Windows)
        {
            var hwnd = FindWindow(null, title);
            check(hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rc), "94: Qt window found by title");
            // Qt's frame (QWindow::framePosition, #100) is the visible frame = DWM extended frame bounds; GetWindowRect
            // also contains the invisible resize borders (Windows 10+, left/right/bottom).
            if (hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out rc, 16) == 0)
            {
                uint dpi = GetDpiForWindow(hwnd);
                int ex = (int)MathF.Round(100 * (dpi == 0 ? 96 : dpi) / 96f);
                GetWindowRect(hwnd, out var wr);
                check(Math.Abs(rc.Left - ex) <= 2 && Math.Abs(rc.Top - ex) <= 2,
                    $"94: visible window top-left = Bounds ({rc.Left},{rc.Top} vs {ex},{ex}; GetWindowRect {wr.Left},{wr.Top})");
            }
        }

        // Logical size of the window (Grab is in device pixels).
        int lw = 800, lh = 600;
        Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / lw, y * a.Height / lh);

        void Frame(Element? input, bool enabled = true)
        {
            r.Update(input, Space.DIPTopLeft, clear, enabled, title);
            r.ApplyCommands();
            pump(3);
            r.AfterEvents();
        }

        var red = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), Fill = new Color4(1, 0, 0, 1) };
        var blue = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(4, 3, 0), Fill = new Color4(0, 0, 1, 1) };
        var tree = new Group { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Children = ImmutableArray.Create<Element>(red, blue) };
        Frame(tree);
        check(r.Error.Length == 0, "07a: apply tree ok " + r.Error);
        check(r.IsVisible, "07a: IsVisible after the first pump");
        check(scene.Count == 3, $"07a: registry owns group + 2 rects ({scene.Count})");
        int deviceWidth;
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_tree.png")))
        {
            deviceWidth = a.Width;
            check(Near(Px(a, 150, 150), 255, 0, 0), "07a: red rect at (1,1) units = (100,100) DIP");
            check(Near(Px(a, 500, 350), 0, 0, 255), "07a: blue rect at (4,3) units");
            check(Near(Px(a, 50, 50), 0x20, 0x20, 0x20), "07a: clear color #202020");
        }

        // Clear color change.
        clear = new Color4(0, 0.5f, 0, 1);
        Frame(tree);
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_clear.png")))
            check(Near(Px(a, 50, 50), 0, 128, 0), "07a: clear color changed at runtime");

        // Enabled = false freezes the scene: the moved rect stays where it was, so does the clear color.
        var moved = new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(5, 1, 0), Fill = new Color4(1, 0, 0, 1) };
        var tree2 = new Group { Size = tree.Size, Anchor = tree.Anchor, Transform = tree.Transform, Children = ImmutableArray.Create<Element>(moved, blue) };
        clear = new Color4(0, 0, 0.5f, 1);
        Frame(tree2, enabled: false);
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_disabled.png")))
        {
            check(Near(Px(a, 150, 150), 255, 0, 0), "07a: Enabled=false keeps the old scene");
            check(Near(Px(a, 50, 50), 0, 128, 0), "07a: Enabled=false keeps the old clear color");
        }
        Frame(tree2);
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_enabled.png")))
        {
            check(Near(Px(a, 550, 150), 255, 0, 0) && Near(Px(a, 150, 150), 0, 0, 128), "07a: Enabled=true applies the moved rect");
            check(Near(Px(a, 500, 350), 0, 0, 255), "07a: unchanged sibling stays");
        }

        // Title at runtime (only sent when the pin value changes); resize via QtNative (the Size pin is gone, #94).
        title = "VL.Qt renderer test (renamed)";
        Native.vlqt_window_resize(r.Window, 640, 480);
        Frame(tree2);
        // X11: the window manager answers the resize asynchronously (ConfigureNotify), wait for the new size (#232).
        for (int i = 0; i < 100 && r.Geometry?.Bounds.Width != 640; i++) { System.Threading.Thread.Sleep(10); Frame(tree2); }
        lw = 640; lh = 480;
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_resized.png")))
        {
            int expected = deviceWidth * 640 / 800;
            check(Math.Abs(a.Width - expected) <= 2, $"07a: vlqt_window_resize resizes the window ({a.Width} vs {expected} device px)");
            check(Near(Px(a, 550, 150), 255, 0, 0), "07a: scene survives the resize");
        }
        check(Native.vlqt_window_set_title(r.Window, title) == 0, "07a: set_title ok");
        check(Native.vlqt_window_set_title(IntPtr.Zero, "x") == -1, "07a: set_title null window -> -1");
        check(Native.vlqt_window_resize(IntPtr.Zero, 1, 1) == -1, "07a: resize null window -> -1");

        // Null input = empty scene: everything destroyed, registry empty.
        Frame(null);
        check(scene.Count == 0, $"07a: null input frees the registry ({scene.Count})");
        using (var a = grab(r.Window, Path.Combine(outDir, "07a_empty.png")))
            check(Near(Px(a, 550, 150), 0, 0, 128), "07a: null input clears the window");

        // Dispose: Clear + apply + destroy; the registry is free again (F8/F5, renderer deleted).
        Frame(tree);
        check(scene.Count == 3, "07a: tree shown again");
        r.Dispose();
        check(scene.Count == 0, $"07a: Dispose clears the reconciler ({scene.Count})");
        check(r.Window == IntPtr.Zero, "07a: Dispose destroys the window");
        r.AfterEvents(); // pump after dispose: no-op
        check(!r.IsVisible, "07a: not visible after Dispose");
        r.Dispose();      // idempotent
        pump(2);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    const uint WM_CLOSE = 0x0010;

    // Is Visible (#93): live from Qt, true from the creation on (no pump needed), false after X/Hide, true again after
    // Show, false after Dispose, true for a new window after a Dispose (node recreated, Undo, F8/F5 with a new registry).
    /// <summary>VSync pin (#95): a window with swap interval 1 is created and pumped without errors (the pump may wait for the refresh).</summary>
    public static void RunVSync(Action<bool, string> check, Action<int> pump)
    {
        var r = new WindowRenderer(new SceneRegistry(), "VL.Qt vsync test", new RectangleF(0, 0, 320, 240), vSync: true);
        check(r.Window != IntPtr.Zero, "95: vSync window created " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;
        var clear = new Color4(0, 0, 0, 1);
        for (int i = 0; i < 5; i++)
        {
            r.Update(null, Space.DIPTopLeft, clear, true, "VL.Qt vsync test");
            r.ApplyCommands();
            pump(2);
            r.AfterEvents();
        }
        check(r.IsVisible && r.Error.Length == 0, "95: vSync window visible and pumped without error: " + r.Error);
        r.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out Rc rect, uint winIni);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// <summary>
    /// Geometry on create + EV_WINDOW_GEOMETRY (#100, #165, prep for #143): the reported geometry (outer top-left + client
    /// size, DIP) equals the Bounds given on create, so it round-trips; a resize is reported; empty bounds (sentinel) and
    /// bounds on no screen are both centered on the primary working area with the fallback size.
    /// </summary>
    public static void RunGeometry(Action<bool, string> check, Action<int> pump)
    {
        var clear = new Color4(0, 0, 0, 1);
        // Pumps until the reported geometry has settled: under X11 the window manager answers placement, resize and
        // the frame extents asynchronously (#232); on Windows it is stable after the first frames.
        WindowGeometry? Shown(WindowRenderer r, string title)
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
        // WSLg (Weston's X window manager): the frame extents arrive late or not at all, include a 32 px invisible
        // shadow and change after a resize, so Qt's frame position is off by up to shadow + border (seen 0..38 DIP).
        // Position is only checked roughly there, size exactly (#232). Real X11 desktops: exact like Windows.
        float posTol = !OperatingSystem.IsWindows() && File.Exists("/dev/dxg")
            && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb" ? 40 : 1;
        if (posTol > 1) Console.WriteLine($"INFO geometry: WSLg/Weston, frame position checked with {posTol} DIP tolerance");
        bool Close(RectangleF a, RectangleF b, float tol = 1) =>
            Math.Abs(a.X - b.X) <= Math.Max(tol, posTol) && Math.Abs(a.Y - b.Y) <= Math.Max(tol, posTol)
            && Math.Abs(a.Width - b.Width) <= tol && Math.Abs(a.Height - b.Height) <= tol;

        var bounds = new RectangleF(150, 120, 500, 300);
        var r = new WindowRenderer(new SceneRegistry(), "VL.Qt geometry test", bounds);
        int events = 0;
        r.GeometryChanged += _ => events++;
        var g = Shown(r, "VL.Qt geometry test");
        check(g is { } g0 && Close(g0.Bounds, bounds) && g0.IsVisible && !g0.IsMinimized,
            $"100: reported geometry = Bounds on create ({g?.Bounds} vs {bounds}, {g?.States})");
        check(events >= 1, $"143: GeometryChanged raised after creation ({events})");

        // Resize via QtNative → new client size reported, position stays.
        int before = events;
        Native.vlqt_window_resize(r.Window, 420, 260);
        g = Shown(r, "VL.Qt geometry test");
        check(g is { } g1 && Close(g1.Bounds, new RectangleF(bounds.X, bounds.Y, 420, 260)) && events > before,
            $"143: resize reported ({g?.Bounds})");

        // Round trip: the reported geometry as Bounds of a new window gives the same geometry (no wandering).
        var reported = g!.Value.Bounds;
        r.Dispose();
        var r2 = new WindowRenderer(new SceneRegistry(), "VL.Qt geometry round trip", reported);
        var g2 = Shown(r2, "VL.Qt geometry round trip");
        check(g2 is { } h2 && Close(h2.Bounds, reported, 0.01f), $"143: round trip stable ({g2?.Bounds} vs {reported})");
        r2.Dispose();

        // Sentinel (empty bounds) = 600x400 centered; bounds on no screen = centered as well.
        var rc = new WindowRenderer(new SceneRegistry(), "VL.Qt geometry centered", default);
        var gc = Shown(rc, "VL.Qt geometry centered");
        rc.Dispose();
        var ro = new WindowRenderer(new SceneRegistry(), "VL.Qt geometry off screen", new RectangleF(50000, 50000, 600, 400));
        var go = Shown(ro, "VL.Qt geometry off screen");
        ro.Dispose();
        check(gc is { } c && c.Bounds.Width == 600 && c.Bounds.Height == 400, $"100: empty bounds → 600x400 ({gc?.Bounds})");
        check(gc is { } c1 && go is { } o1 && Close(c1.Bounds, o1.Bounds), $"165: off-screen bounds centered like the sentinel ({go?.Bounds} vs {gc?.Bounds})");
        if (OperatingSystem.IsWindows() && gc is { } cw && SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, out var wa, 0))
        {
            // Primary working area in DIP (primary at system DPI); the client rect (frame ignored) is centered on it.
            float s = GetDpiForSystem() / 96f;
            float cx = (wa.Left + wa.Right) / 2f / s, cy = (wa.Top + wa.Bottom) / 2f / s;
            check(Math.Abs(cw.Bounds.X + 300 - cx) <= 2 && Math.Abs(cw.Bounds.Y + 200 - cy) <= 2,
                $"100: sentinel centered on the primary working area ({cw.Bounds.X + 300},{cw.Bounds.Y + 200} vs {cx},{cy})");
        }
        pump(2);
    }

    /// <summary>
    /// #133: a Renderer added live or restored by Undo/Redo is created by vvvv's hotswap with the VL type default for
    /// Bounds, (-0.5, -0.5, 1, 1), instead of the pin value/default. While a first window is pumped, a second one is
    /// created the way Renderer does it (WindowPlacement.CreateBounds), disposed (Undo) and created again (Redo): each must open
    /// with the pin default size, not as a 1x1 window, and the first window keeps being pumped.
    /// </summary>
    public static void RunHotswap(Action<bool, string> check, Action<int> pump)
    {
        var clear = new Color4(0, 0, 0, 1);
        var vlDefault = new RectangleF(-0.5f, -0.5f, 1, 1); // AppHost.GetDefaultValue<RectangleF>() in vvvv 8.0-0156
        WindowGeometry? Frames(WindowRenderer r, string title, int n = 40)
        {
            for (int i = 0; i < n; i++)
            {
                r.Update(null, Space.DIPTopLeft, clear, true, title);
                r.ApplyCommands();
                pump(2);
                r.AfterEvents();
            }
            return r.Geometry;
        }
        var first = new WindowRenderer(new SceneRegistry(), "VL.Qt hotswap first", new RectangleF(100, 100, 320, 240));
        Frames(first, "VL.Qt hotswap first", 10);
        var want = WindowPlacement.DefaultBounds;
        for (int round = 0; round < 2; round++) // 0: added live, 1: Undo (dispose) + Redo (create again)
        {
            var what = round == 0 ? "added live" : "Undo/Redo";
            var live = new WindowRenderer(new SceneRegistry(), "VL.Qt hotswap live", WindowPlacement.CreateBounds(vlDefault, vlDefault));
            var g = Frames(live, "VL.Qt hotswap live");
            check(live.IsVisible && g is { } h && Math.Abs(h.Bounds.Width - want.Width) <= 1 && Math.Abs(h.Bounds.Height - want.Height) <= 1,
                $"133: renderer {what} opens with the Bounds pin default size ({g?.Bounds} vs {want.Width}x{want.Height})");
            live.Dispose();
            Frames(first, "VL.Qt hotswap first", 3);
        }
        check(first.IsVisible && first.Error.Length == 0, "133: first renderer still shown and pumped " + first.Error);
        first.Dispose();
        pump(2);
    }

    public static void RunVisibility(Action<bool, string> check, Action<int> pump)
    {
        const string title = "VL.Qt visibility test";
        var scene = new SceneRegistry();
        var clear = new Color4(0, 0, 0, 1);
        var r = new WindowRenderer(scene, title, new RectangleF(0, 0, 320, 240));
        check(r.Window != IntPtr.Zero, "93: window created " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;
        void Frame(WindowRenderer w)
        {
            w.Update(null, Space.DIPTopLeft, clear, true, title);
            w.ApplyCommands();
            pump(2);
            w.AfterEvents();
        }

        check(r.IsVisible, "93: Is Visible true right after creation (before the first pump)");
        Frame(r);
        check(r.IsVisible, "93: Is Visible true after the first pump");

        // X: QtNative swallows the close, the Renderer hides on CloseRequested (as Renderer.OnCloseRequested).
        int closes = 0;
        r.CloseRequested += () => { closes++; r.Hide(); };
        if (OperatingSystem.IsWindows()) // Linux: no way to send the X (WM_CLOSE) without a test hook in QtNative -> Hide instead
        {
            var hwnd = FindWindow(null, title);
            check(hwnd != IntPtr.Zero && PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero), "93: WM_CLOSE posted (X)");
            Frame(r);
            check(closes == 1 && !r.IsVisible, $"93: X hides, Is Visible false (closes {closes})");
        }
        else
        {
            Console.WriteLine("SKIP 93: X (WM_CLOSE) is Windows-only, hiding via Hide()");
            r.Hide();
            Frame(r);
            check(!r.IsVisible, "93: Hide, Is Visible false");
        }
        Frame(r);
        check(!r.IsVisible, "93: Is Visible stays false while hidden");

        Native.vlqt_window_show(r.Window);
        check(r.IsVisible, "93: Is Visible true right after Show");
        Frame(r);
        check(r.IsVisible, "93: Is Visible true after Show + pump");

        r.Hide();
        check(!r.IsVisible, "93: Is Visible false right after Hide");
        Native.vlqt_window_show(r.Window);
        Frame(r);
        check(r.IsVisible, "93: Hide -> Show -> Is Visible true");

        r.Dispose();
        check(!r.IsVisible, "93: Is Visible false after Dispose");

        // Recreated (Undo of a delete, Bounds changed: same registry; F8/F5: new app = new registry), several times.
        for (int i = 0; i < 3; i++)
        {
            var reg = i == 0 ? scene : new SceneRegistry();
            var n = new WindowRenderer(reg, title, new RectangleF(0, 0, 320, 240));
            bool before = n.IsVisible;
            Frame(n);
            check(before && n.IsVisible, $"93: recreated window #{i + 1} Is Visible true (before pump {before}, after {n.IsVisible})");
            n.Dispose();
            check(!n.IsVisible, $"93: recreated window #{i + 1} Is Visible false after Dispose");
            pump(1);
        }
    }
}
