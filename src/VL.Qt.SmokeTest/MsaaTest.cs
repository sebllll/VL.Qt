#if WINDOWS
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Core.Mathematics;
using VL.Qt;

/// <summary>
/// #118 (MSAA): sample count of a window and of an offscreen scene. A rotated rectangle and a thin diagonal bar: without
/// MSAA no pixel has an intermediate value, with it the edges do. Live change of a window (X4, None, X8, X4: swap chain
/// count, rect, scene, apply), no replacement at the same count, real key input and fullscreen after a swap, Clear off across a swap (#117: trail lost, feedback layer rebuilt), offscreen
/// (UNORM, SRGB and float through the linear pass; rebind 1, 4, 1) and the clamp of unsupported counts. The checks count
/// edge pixels and read swap chain counts; nothing depends on time. The cost per count (spike) is in
/// docs/decisions/render-texture.md.
/// </summary>
[TestFixture, NonParallelizable]
public static class MsaaTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; public override string ToString() => $"{L},{T},{R},{B}"; }

    private const int W = 400, H = 300;
    private const string Title = "VL.Qt SmokeTest MSAA";

    // Thin bar + rotated square, white on black: hard edges without AA.
    private static void Scene(CommandWriter cmd)
    {
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 1);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetPlaceXf(1, 60, 140, 280, 3, Matrix.RotationZ(MathUtil.DegreesToRadians(11)));
        cmd.SetColor(1, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(1, 0, 0);
        cmd.Create(2, Protocol.Type.Rectangle);
        cmd.SetPlaceXf(2, 150, 60, 100, 60, Matrix.RotationZ(MathUtil.DegreesToRadians(27)));
        cmd.SetColor(2, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(2, 0, 1);
    }

    // Pixels with an intermediate red value (edge pixels) among the pixels of the area, and the number of distinct red values.
    private static (int Mid, int Levels) Edges(Func<int, int, int> red, int x0, int y0, int x1, int y1)
    {
        int mid = 0;
        var levels = new HashSet<int>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int r = red(x, y);
                if (r > 24 && r < 231) mid++;
                levels.Add(r);
            }
        return (mid, levels.Count);
    }

    // The area of the bar and the square in DIP, without the image and the text of the window scene. The grab is in device pixels.
    private static (int Mid, int Levels) Edges(Bitmap b)
    {
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        int stride = d.Stride;
        return Edges((x, y) => px[y * stride + x * 4 + 2], 55 * b.Width / W, 20 * b.Height / H, 395 * b.Width / W, 245 * b.Height / H);
    }

    // Magnified crop of a grab (DIP area), nearest neighbour, so single pixels show.
    private static Bitmap Zoom(Bitmap b, System.Drawing.Rectangle dip, int scale)
    {
        float sx = (float)b.Width / W, sy = (float)b.Height / H;
        var src = new System.Drawing.Rectangle((int)(dip.X * sx), (int)(dip.Y * sy), (int)(dip.Width * sx), (int)(dip.Height * sy));
        var z = new Bitmap(src.Width * scale, src.Height * scale);
        using var g = Graphics.FromImage(z);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(b, new System.Drawing.Rectangle(0, 0, z.Width, z.Height), src, GraphicsUnit.Pixel);
        return z;
    }

    // None left, X4 right: the left end of the bar (thin line) above, a corner of the square below.
    private static void SaveCompare(string path, Bitmap none, Bitmap x4)
    {
        var areas = new[] { new System.Drawing.Rectangle(60, 115, 60, 40), new System.Drawing.Rectangle(145, 30, 60, 40) };
        const int scale = 6, gap = 8;
        var crops = new List<Bitmap>();
        foreach (var a in areas) { crops.Add(Zoom(none, a, scale)); crops.Add(Zoom(x4, a, scale)); }
        using var o = new Bitmap(crops[0].Width * 2 + gap, crops[0].Height * 2 + gap);
        using (var g = Graphics.FromImage(o))
        {
            g.Clear(System.Drawing.Color.DarkRed);
            for (int i = 0; i < crops.Count; i++)
                g.DrawImageUnscaled(crops[i], i % 2 * (crops[0].Width + gap), i / 2 * (crops[0].Height + gap));
        }
        foreach (var c in crops) c.Dispose();
        o.Save(path);
    }

    /// <summary>Window and offscreen sample counts (#118).</summary>
    [Test, Category("Msaa")]
    public static void Msaa() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("msaa: D3D11 and Win32 only (the OpenGL path is not tested yet)");
            return;
        }
        if (s.QuickSoftware)
        {
            s.Skip("msaa: Qt Quick software backend has no multisampling");
            return;
        }
        s.Info("msaa: #118 window and offscreen sample counts");
        ClampPart(s);
        WindowPart(s);
        InputPart(s);
        FeedbackPart(s);
        OffscreenPart(s);
    }

    // Clamp and the format-only path: a window not shown yet changes its format, no swap. QRhi's D3D11 backend offers 1/2/4/8.
    private static void ClampPart(Scenario s)
    {
        var w = Native.vlqt_window_create(W, H, Title + " clamp");
        int r16 = Native.vlqt_window_set_samples(w, 16), r3 = Native.vlqt_window_set_samples(w, 3), r0 = Native.vlqt_window_set_samples(w, 0);
        s.Info($"msaa: hidden window set 16 -> {r16}, 3 -> {r3}, 0 -> {r0}, samples {Native.vlqt_window_samples(w)}");
        s.Check(r16 is 4 or 8, $"msaa: window request 16 -> highest supported count ({r16})");
        s.Check(r3 == 2, $"msaa: window request 3 -> 2 ({r3})");
        s.Check(r0 == 1 && Native.vlqt_window_samples(w) == 1, $"msaa: window request 0 -> 1 ({r0}, samples {Native.vlqt_window_samples(w)})");
        Native.vlqt_window_destroy(w);
        var scene = Native.vlqt_offscreen_create(W, H);
        s.Check(Native.vlqt_window_set_samples(scene, 4) < 0 && Native.vlqt_window_samples(scene) == 0, "msaa: set_samples refused on an offscreen scene");
        Native.vlqt_window_destroy(scene);
    }

    private static void WindowPart(Scenario s)
    {
        var w = Native.vlqt_window_create(W, H, Title);
        var cmd = new CommandWriter();
        Scene(cmd);
        // Green image (resource on the shared device) and a text: must survive the window swap.
        Native.vlqt_res_create(Protocol.ResKind.Image, out var img);
        var d = new byte[32 * 32 * 4];
        for (int i = 0; i < d.Length; i += 4) { d[i + 1] = 255; d[i + 3] = 255; }
        Native.ResSetPixels(img, new Native.vlqt_pixel_desc { width = 32, height = 32, format = 7, premultiplied = 1 }, d);
        Native.vlqt_res_wait(img, 10000);
        cmd.Create(3, Protocol.Type.Image);
        cmd.SetU64(3, Protocol.Prop.Image, img);
        cmd.SetPlace(3, 10, 10, 40, 40);
        cmd.Reparent(3, 0, 2);
        cmd.Create(4, Protocol.Type.Text);
        cmd.SetString(4, Protocol.Prop.Text, "MSAA");
        cmd.SetColor(4, Protocol.Prop.Color, 1, 1, 0, 1);
        cmd.SetPlace(4, 300, 250, 90, 40);
        cmd.Reparent(4, 0, 3);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "msaa: window scene");
        Native.vlqt_window_show(w);
        s.Pump(5);
        var hwnd = FindWindowW(null, Title);
        GetWindowRect(hwnd, out var rect0);
        s.Check(Native.vlqt_window_samples(w) == 4, $"msaa: new window has 4 samples ({Native.vlqt_window_samples(w)})");
        Bitmap? none = null, x4 = null;
        System.Drawing.Size size0 = default;
        int prev = 4;
        // The first 4 is the count the window has: nothing changes. Then None, X8 and X4 as in the pin; X2 on the way.
        foreach (var n in new[] { 4, 1, 8, 2, 4 })
        {
            long count0 = Native.vlqt_window_change_count(w);
            int rc = Native.vlqt_window_set_samples(w, n);
            s.Pump(5);
            var h2 = FindWindowW(null, Title);
            GetWindowRect(h2, out var rect);
            int actual = Native.vlqt_window_samples(w);
            // The first X4 grab stays open for the comparison picture (a loaded Bitmap locks its file): the way back gets its own name.
            var b = s.Grab(w, Path.Combine(s.OutDir, n == 4 && x4 != null ? "msaa_window_x4_back.png" : $"msaa_window_x{n}.png"));
            if (size0.IsEmpty) size0 = b.Size;
            var e = Edges(b);
            var green = b.GetPixel(30 * b.Width / W, 30 * b.Height / H);
            s.Info($"msaa: window set {n}: rc {rc}, swap chain {actual}, hwnd changed {h2 != hwnd}, rect {rect}, edge px {e.Mid}, levels {e.Levels}");
            s.Check(rc == n && actual == n && Native.vlqt_window_is_visible(w) == 1, $"msaa: window live change to {n} (rc {rc}, swap chain {actual})");
            s.Check(green.G > 240 && green.R < 20, $"msaa: image survives change to {n} ({green})");
            s.Check(n == 1 ? e.Mid == 0 : e.Mid > 0, $"msaa: window x{n} edge pixels {(n == 1 ? "none" : "present")} ({e.Mid})");
            s.Check(rect.ToString() == rect0.ToString() && b.Size == size0, $"msaa: window rect and size kept after change to {n} ({rect}, {b.Size} vs {rect0}, {size0})");
            long count1 = Native.vlqt_window_change_count(w);
            s.Check(count1 >= count0, $"msaa: change count goes on across a swap ({count0} -> {count1})");
            // The same count must not replace the window.
            s.Check(n == prev ? h2 == hwnd && count1 == count0 : h2 != hwnd, $"msaa: window replaced only on a change ({prev} -> {n}, hwnd changed {h2 != hwnd})");
            if (n == 1) none = b;
            else if (n == 4 && x4 == null) x4 = b;
            else b.Dispose();
            prev = n;
            hwnd = h2;
        }
        if (none != null && x4 != null)
            SaveCompare(Path.Combine(s.OutDir, "msaa_window_compare.png"), none, x4);
        none?.Dispose();
        x4?.Dispose();
        // Scene still alive and editable after the changes: recolour, check.
        cmd.Reset();
        cmd.SetColor(2, Protocol.Prop.Color, 1, 0, 0, 1);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "msaa: apply after changes");
        s.Pump(3);
        using (var b = s.Grab(w, "msaa_window_recolor.png"))
        {
            var c = b.GetPixel(200 * b.Width / W, 90 * b.Height / H);
            s.Check(c.R > 200 && c.G < 40, $"msaa: scene updates after the changes ({c})");
        }
        // Fullscreen survives.
        Native.vlqt_window_set_mode(w, Protocol.WindowMode.Fullscreen, Protocol.WindowMode.Fullscreen);
        s.Pump(10);
        GetWindowRect(FindWindowW(null, Title), out var fs0);
        Native.vlqt_window_set_samples(w, 8);
        s.Pump(10);
        GetWindowRect(FindWindowW(null, Title), out var fs1);
        s.Check(fs0.ToString() == fs1.ToString() && Native.vlqt_window_samples(w) == 8, $"msaa: fullscreen kept across a change ({fs0} -> {fs1})");
        Native.vlqt_window_set_mode(w, Protocol.WindowMode.Fullscreen, 0);
        s.Pump(10);
        Native.vlqt_window_destroy(w);
        // The image resource would stay Ready and show in the resource counts of later scenarios.
        Native.vlqt_res_release(img);
    }

    // Clear off (#117) across a swap: Scene::moveToWindow releases the feedback layer and rebuilds it for the new window.
    // The trail is lost (the first frame starts from the clear colour), the scene draws on, and a new trail builds up.
    private static void FeedbackPart(Scenario s)
    {
        const string title = Title + " clear off";
        var w = Native.vlqt_window_create(W, H, title);
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 0); // alpha 0: pure accumulation (an opaque colour with Clear off is Clear on since #138)
        cmd.SetClear(false);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetPlace(1, 20, 130, 30, 30);
        cmd.SetColor(1, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(1, 0, 0);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "msaa: clear-off scene");
        Native.vlqt_window_show(w);
        s.Pump(5);
        // Moves the square to x0, x0 + 40, ... (one frame each).
        void Move(int x0, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                cmd.Reset();
                cmd.SetPlace(1, x0 + i * 40, 130, 30, 30);
                s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "msaa: clear-off move");
                s.Pump(1);
            }
        }
        bool White(Bitmap b, int x) { var c = b.GetPixel(x * b.Width / W, 145 * b.Height / H); return c.R > 230 && c.G > 230 && c.B > 230; }
        // Alpha 0 clears to transparent black in the grabbed window, whatever the RGB of the clear colour.
        bool Dark(Bitmap b, int x) { var c = b.GetPixel(x * b.Width / W, 145 * b.Height / H); return c.R < 20 && c.G < 20 && c.B < 20; }

        Move(20, 5); // 20 .. 180
        using (var b = s.Grab(w, "msaa_clear_off_before.png"))
            s.Check(White(b, 35) && White(b, 115) && White(b, 195), "msaa: Clear off leaves a trail before the change");

        int rc = Native.vlqt_window_set_samples(w, 1);
        s.Pump(3);
        using (var b = s.Grab(w, "msaa_clear_off_swapped.png"))
            s.Check(rc == 1 && Native.vlqt_window_samples(w) == 1 && Dark(b, 35) && Dark(b, 115) && White(b, 195),
                $"msaa: count change with Clear off drops the trail, the square still draws (rc {rc})");

        long c0 = Native.vlqt_window_change_count(w);
        s.Pump(10);
        long idle = Native.vlqt_window_change_count(w) - c0;
        // The recursive layer renders every pump also without a scene change (docs "Clear pin", Rendering).
        s.Check(idle > 0, $"msaa: feedback layer active again after the change ({idle} changes / 10 pumps)");

        Move(220, 3); // 220 .. 300
        using (var b = s.Grab(w, "msaa_clear_off_after.png"))
            s.Check(Dark(b, 35) && White(b, 195) && White(b, 235) && White(b, 315), "msaa: a new trail builds up after the change");
        Native.vlqt_window_destroy(w);
    }

    // Real Win32 keys (WM_KEYDOWN/UP dispatched by the WinForms loop like in vvvv, see KeyRouteTest) reach the new HWND
    // after a swap, WM_GETDLGCODE is answered there, and the TextField keeps the active focus across the swap.
    private static void InputPart(Scenario s)
    {
#if WINDOWS
        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_GETDLGCODE = 0x0087;
        const int VK_RETURN = 0x0D, DLGC_WANTALLKEYS = 0x0004;
        const uint T = 1, HT = 61;
        const string title = Title + " input";
        var w = Native.vlqt_window_create(400, 200, title);
        var cmd = new CommandWriter();
        cmd.Create(T, Protocol.Type.TextField);
        VL.Qt.Styles.BaseStyle.Write(cmd, T);
        cmd.SetBool(T, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(T, 50, 50, 300, 40);
        cmd.SetString(T, Protocol.Prop.Value, "");
        cmd.Reparent(T, 0, 0);
        cmd.Bind(T, HT, 0);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "msaa: input scene");
        Native.vlqt_window_show(w);
        s.Pump(3);
        var buf = new byte[1 << 16];
        // The last string EV_VALUE of the TextField in this frame, or null.
        string? Committed()
        {
            s.Pump(1);
            int n = Native.Poll(w, buf);
            string? value = null;
            for (int off = 0; off + 12 <= n;)
            {
                ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off));
                int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + 2));
                if (len < 8 || off + 4 + len > n)
                    break;
                var payload = buf.AsSpan(off + 12, len - 8);
                if (ev == Protocol.Ev.Value && BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 4)) == HT
                    && BinaryPrimitives.ReadUInt16LittleEndian(payload) == Protocol.Vt.String)
                    value = Encoding.UTF8.GetString(payload.Slice(8, (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4))));
                off += (4 + len + 3) & ~3;
            }
            return value;
        }
        var hwnd = FindWindowW(null, title);
        void Key(int vk)
        {
            long scan = MapVirtualKeyW((uint)vk, 0);
            PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)(1 | (scan << 16)));
            PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)(1 | (scan << 16) | (1L << 30) | (1L << 31)));
            System.Windows.Forms.Application.DoEvents();
        }
        // As in KeyRouteTest: without the foreground Qt types into nothing; outside the CI that is a SKIP.
        void CheckKeys(bool ok, string what)
        {
            if (ok || GetForegroundWindow() == hwnd || Environment.GetEnvironmentVariable("CI") != null)
                s.Check(ok, what);
            else
                s.Skip($"{what}: the test window is not in the foreground");
        }
        Committed();
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 200, 70);
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Press, 200, 70);
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Release, 200, 70);
        Committed();
        Key('4'); Key('2'); Key(VK_RETURN);
        var v = Committed();
        CheckKeys(v == "42", $"msaa: keys before the swap -> \"42\" ({v})");
        s.Check(Native.vlqt_window_set_samples(w, 1) == 1, "msaa: input window swap to 1");
        s.Pump(3);
        var old = hwnd;
        hwnd = FindWindowW(null, title);
        s.Check(hwnd != IntPtr.Zero && hwnd != old, "msaa: input window has a new HWND");
        var code = (long)SendMessageW(hwnd, WM_GETDLGCODE, (IntPtr)VK_RETURN, IntPtr.Zero);
        s.Check((code & DLGC_WANTALLKEYS) != 0, $"msaa: WM_GETDLGCODE on the new HWND -> DLGC_WANTALLKEYS (0x{code:x})");
        // No click: the TextField must still have the active focus in the new window.
        Key('7'); Key(VK_RETURN);
        v = Committed();
        CheckKeys(v == "427", $"msaa: keys after the swap reach the focused TextField -> \"427\" ({v})");
        Native.vlqt_window_destroy(w);
        s.Pump(1);
#endif
    }

    private static void OffscreenPart(Scenario s)
    {
        if (Native.vlqt_test_d3d11_create_device(0, out var device, out var context) != 0)
        {
            s.Check(false, "msaa: test device");
            return;
        }
        // texture format, bind format, bytes per pixel, counts: UNORM through all, SRGB and float (linear pass) rebind 1, 4, 1.
        var formats = new (string Name, int Tex, int View, int Bytes, int[] Counts)[]
        {
            ("B8G8R8A8_TYPELESS/UNORM", 90, 87, 4, new[] { 1, 2, 4, 8, 1, 4 }),
            ("R8G8B8A8_TYPELESS/UNORM_SRGB", 27, 29, 4, new[] { 1, 4, 1 }),
            ("R16G16B16A16_FLOAT", 10, 10, 8, new[] { 1, 4, 1 }),
        };
        foreach (var f in formats)
        {
            // One scene per format, rebound for every count (a change of the pin = rebind).
            var scene = Native.vlqt_offscreen_create(W, H);
            var cmd = new CommandWriter();
            Scene(cmd);
            Native.Apply(scene, cmd.EndFrame());
            var tag = f.Tex == 90 ? "unorm" : f.Tex == 27 ? "srgb" : "float";
            foreach (var n in f.Counts)
            {
                Native.vlqt_test_d3d11_create_shared_texture(device, W, H, f.Tex, out var tex, out var handle);
                int rc = Native.vlqt_offscreen_bind(scene, device, handle, W, H, f.View, n, out var used);
                Native.vlqt_test_close_handle(handle);
                int rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
                var px = new byte[W * H * f.Bytes];
                int read = Native.TestReadTexture(tex, px, out _, out _, out _);
                int Red(int x, int y)
                {
                    int i = y * W + x;
                    return f.Bytes == 8 ? (int)Math.Round((float)BitConverter.ToHalf(px, i * 8) * 255f)
                        : f.Tex == 90 ? px[i * 4 + 2] : px[i * 4];
                }
                var e = Edges(Red, 0, 0, W, H);
                // White pixel inside the square and black corner: resolve must not change the solid colours.
                int inside = Red(200, 90), corner = Red(5, 5);
                s.Info($"msaa: offscreen {f.Name} x{n}: bind {rc}, used {used}, edge px {e.Mid}, levels {e.Levels}, inside {inside}, corner {corner}");
                s.Check(rc == 0 && used == n && rendered == 1 && read == px.Length, $"msaa: offscreen {tag} x{n} binds and renders (rc {rc}, used {used}, rendered {rendered}, read {read})");
                s.Check(inside > 250 && corner < 3, $"msaa: offscreen {tag} x{n} solid colours exact ({inside}, {corner})");
                s.Check(n == 1 ? e.Mid == 0 : e.Mid > 0, $"msaa: offscreen {tag} x{n} edge pixels {(n == 1 ? "none" : "present")} ({e.Mid})");
                if (f.Tex == 90)
                    SavePng(px, Path.Combine(s.OutDir, $"msaa_offscreen_x{n}.png"));
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_test_com_release(tex);
            }
            // Clamp: 16 is above what QRhi's D3D11 backend offers (1/2/4/8), 3 is no count.
            foreach (var (request, ok) in new (int, Func<int, bool>)[] { (16, u => u is 4 or 8), (3, u => u == 2) })
            {
                Native.vlqt_test_d3d11_create_shared_texture(device, W, H, f.Tex, out var t, out var h);
                int rc = Native.vlqt_offscreen_bind(scene, device, h, W, H, f.View, request, out var used);
                Native.vlqt_test_close_handle(h);
                s.Check(rc == 0 && ok(used), $"msaa: offscreen {tag} request {request} clamped (used {used})");
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_test_com_release(t);
            }
            Native.vlqt_window_destroy(scene);
        }
        Native.vlqt_test_d3d11_release_device(device, context);
    }

    private static void SavePng(byte[] bgra, string path)
    {
        using var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, d.Scan0, bgra.Length);
        b.UnlockBits(d);
        b.Save(path);
    }
}
#endif
