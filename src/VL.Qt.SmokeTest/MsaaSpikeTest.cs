using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Core.Mathematics;
using VL.Qt;

/// <summary>
/// #118 (MSAA), not part of --all: <c>--only msaaspike</c>. Live change of a window's sample count (window swap), clamp,
/// real Win32 key input and focus after a swap, edge pixels per count in a window and in an offscreen scene (UNORM, SRGB
/// view, float through the linear pass), rough cost per count. The scenario Msaa (task 6) replaces it.
/// </summary>
static class MsaaSpikeTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; public override string ToString() => $"{L},{T},{R},{B}"; }

    const int W = 400, H = 300;
    const string Title = "VL.Qt MSAA spike";

    // Thin bar + rotated square, white on black: hard edges without AA.
    static void Scene(CommandWriter cmd)
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

    // Pixels with an intermediate red value (edge pixels), and the number of distinct red values.
    static (int Mid, int Levels) Edges(Func<int, int> red, int count)
    {
        int mid = 0;
        var levels = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int r = red(i);
            if (r > 24 && r < 231) mid++;
            levels.Add(r);
        }
        return (mid, levels.Count);
    }

    static (int Mid, int Levels) Edges(Bitmap b)
    {
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        int w = b.Width, stride = d.Stride;
        return Edges(i => px[(i / w) * stride + (i % w) * 4 + 2], w * b.Height);
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        ClampPart(check);
        WindowPart(outDir, check, pump, grab);
        InputPart(check, pump);
        OffscreenPart(outDir, check);
        WindowPerf(check, pump);
        OffscreenPerf(check);
    }

    // Clamp and the format-only path: a window not shown yet changes its format, no swap.
    static void ClampPart(Action<bool, string> check)
    {
        var w = Native.vlqt_window_create(W, H, Title + " clamp");
        int r16 = Native.vlqt_window_set_samples(w, 16), r3 = Native.vlqt_window_set_samples(w, 3), r0 = Native.vlqt_window_set_samples(w, 0);
        Console.WriteLine($"INFO msaaspike: hidden window set 16 -> {r16}, 3 -> {r3}, 0 -> {r0}, samples {Native.vlqt_window_samples(w)}");
        check(r16 is > 1 and <= 8 && r3 == 2 && r0 == 1 && Native.vlqt_window_samples(w) == 1, $"msaaspike: window clamp (16 -> {r16}, 3 -> {r3}, 0 -> {r0})");
        Native.vlqt_window_destroy(w);
        var scene = Native.vlqt_offscreen_create(W, H);
        check(Native.vlqt_window_set_samples(scene, 4) < 0 && Native.vlqt_window_samples(scene) == 0, "msaaspike: set_samples refused on an offscreen scene");
        Native.vlqt_window_destroy(scene);
    }

    static void WindowPart(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
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
        check(Native.Apply(w, cmd.EndFrame()) == 0, "msaaspike: window scene");
        Native.vlqt_window_show(w);
        pump(5);
        var hwnd = FindWindowW(null, Title);
        GetWindowRect(hwnd, out var rect0);
        Console.WriteLine($"INFO msaaspike: window created, swap chain samples {Native.vlqt_window_samples(w)}, rect {rect0}");
        check(Native.vlqt_window_samples(w) == 4, $"msaaspike: new window has 4 samples ({Native.vlqt_window_samples(w)})");
        int prev = 4;
        foreach (var n in new[] { 4, 1, 2, 8, 4, 1 })
        {
            long count0 = Native.vlqt_window_change_count(w);
            var sw = Stopwatch.StartNew();
            int rc = Native.vlqt_window_set_samples(w, n);
            var setMs = sw.Elapsed.TotalMilliseconds;
            pump(1);
            var firstMs = sw.Elapsed.TotalMilliseconds;
            pump(4);
            var h2 = FindWindowW(null, Title);
            GetWindowRect(h2, out var rect);
            int actual = Native.vlqt_window_samples(w);
            using var b = grab(w, Path.Combine(outDir, $"msaa_window_x{n}.png"));
            var e = Edges(b);
            var green = b.GetPixel(30 * b.Width / W, 30 * b.Height / H);
            check(green.G > 240 && green.R < 20, $"msaaspike: image survives change to {n} ({green})");
            Console.WriteLine($"INFO msaaspike: window set {n}: rc {rc}, swap chain {actual}, set {setMs:F1} ms, +first pump {firstMs:F1} ms, visible {Native.vlqt_window_is_visible(w)}, hwnd changed {h2 != hwnd}, rect {rect}, edge px {e.Mid}, levels {e.Levels}");
            check(rc == n && actual == n && Native.vlqt_window_is_visible(w) == 1, $"msaaspike: window live change to {n} (rc {rc}, swap chain {actual})");
            long count1 = Native.vlqt_window_change_count(w);
            check(count1 >= count0 && (h2 == hwnd || count1 > count0), $"msaaspike: change count goes on across a swap ({count0} -> {count1})");
            check(n == prev ? h2 == hwnd : h2 != hwnd, $"msaaspike: window replaced only on a change ({prev} -> {n}, hwnd changed {h2 != hwnd})");
            prev = n;
            check(rect.ToString() == rect0.ToString(), $"msaaspike: window rect kept after change to {n} ({rect} vs {rect0})");
            hwnd = h2;
        }
        // Scene still alive and editable after the changes: recolour, check.
        cmd.Reset();
        cmd.SetColor(2, Protocol.Prop.Color, 1, 0, 0, 1);
        check(Native.Apply(w, cmd.EndFrame()) == 0, "msaaspike: apply after changes");
        pump(3);
        using (var b = grab(w, Path.Combine(outDir, "msaa_window_recolor.png")))
        {
            var c = b.GetPixel(200 * b.Width / W, 90 * b.Height / H);
            check(c.R > 200 && c.G < 40, $"msaaspike: scene updates after the changes ({c})");
        }
        // Fullscreen survives.
        Native.vlqt_window_set_mode(w, Protocol.WindowMode.Fullscreen, Protocol.WindowMode.Fullscreen);
        pump(10);
        GetWindowRect(FindWindowW(null, Title), out var fs0);
        Native.vlqt_window_set_samples(w, 4);
        pump(10);
        GetWindowRect(FindWindowW(null, Title), out var fs1);
        Console.WriteLine($"INFO msaaspike: fullscreen rect {fs0} -> after change {fs1}, samples {Native.vlqt_window_samples(w)}");
        check(fs0.ToString() == fs1.ToString() && Native.vlqt_window_samples(w) == 4, "msaaspike: fullscreen kept across a change");
        Native.vlqt_window_set_mode(w, Protocol.WindowMode.Fullscreen, 0);
        pump(10);
        GetWindowRect(FindWindowW(null, Title), out var back);
        Console.WriteLine($"INFO msaaspike: after fullscreen off rect {back} (before {rect0})");
        Native.vlqt_window_destroy(w);
    }

    // Real Win32 keys (WM_KEYDOWN/UP dispatched by the WinForms loop like in vvvv, see KeyRouteTest) reach the new HWND
    // after a swap, WM_GETDLGCODE is answered there, and the TextField keeps the active focus across the swap.
    static void InputPart(Action<bool, string> check, Action<int> pump)
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
        check(Native.Apply(w, cmd.EndFrame()) == 0, "msaaspike: input scene");
        Native.vlqt_window_show(w);
        pump(3);
        var buf = new byte[1 << 16];
        // The last string EV_VALUE of the TextField in this frame, or null.
        string? Committed()
        {
            pump(1);
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
                check(ok, what);
            else
                Console.WriteLine($"SKIP {what}: the test window is not in the foreground");
        }
        Committed();
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 200, 70);
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Press, 200, 70);
        Native.vlqt_test_send_mouse(w, Protocol.Mouse.Release, 200, 70);
        Committed();
        Key('4'); Key('2'); Key(VK_RETURN);
        var v = Committed();
        CheckKeys(v == "42", $"msaaspike: keys before the swap -> \"42\" ({v})");
        check(Native.vlqt_window_set_samples(w, 1) == 1, "msaaspike: input window swap to 1");
        pump(3);
        var old = hwnd;
        hwnd = FindWindowW(null, title);
        check(hwnd != IntPtr.Zero && hwnd != old, "msaaspike: input window has a new HWND");
        var code = (long)SendMessageW(hwnd, WM_GETDLGCODE, (IntPtr)VK_RETURN, IntPtr.Zero);
        check((code & DLGC_WANTALLKEYS) != 0, $"msaaspike: WM_GETDLGCODE on the new HWND -> DLGC_WANTALLKEYS (0x{code:x})");
        // No click: the TextField must still have the active focus in the new window.
        Key('7'); Key(VK_RETURN);
        v = Committed();
        CheckKeys(v == "427", $"msaaspike: keys after the swap reach the focused TextField -> \"427\" ({v})");
        Native.vlqt_window_destroy(w);
        pump(1);
#endif
    }

    static void OffscreenPart(string outDir, Action<bool, string> check)
    {
        if (Native.vlqt_test_d3d11_create_device(0, out var device, out var context) != 0)
        {
            check(false, "msaaspike: test device");
            return;
        }
        // texture format, bind format, bytes per pixel
        var formats = new (string Name, int Tex, int View, int Bytes)[]
        {
            ("B8G8R8A8_TYPELESS/UNORM", 90, 87, 4),
            ("R8G8B8A8_TYPELESS/UNORM_SRGB", 27, 29, 4),
            ("R16G16B16A16_FLOAT", 10, 10, 8),
        };
        foreach (var f in formats)
        {
            // One scene per format, rebound for every count (a change of the pin = rebind).
            var scene = Native.vlqt_offscreen_create(W, H);
            var cmd = new CommandWriter();
            Scene(cmd);
            Native.Apply(scene, cmd.EndFrame());
            foreach (var n in new[] { 1, 2, 4, 8, 1, 4 })
            {
                Native.vlqt_test_d3d11_create_shared_texture(device, W, H, f.Tex, out var tex, out var handle);
                int rc = Native.vlqt_offscreen_bind(scene, device, handle, W, H, f.View, n, out var used);
                Native.vlqt_test_close_handle(handle);
                int rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
                var px = new byte[W * H * f.Bytes];
                int read = Native.TestReadTexture(tex, px, out _, out _, out _);
                int Red(int i) => f.Bytes == 8 ? (int)Math.Round((float)BitConverter.ToHalf(px, i * 8) * 255f)
                    : f.Tex == 90 ? px[i * 4 + 2] : px[i * 4];
                var e = Edges(Red, W * H);
                // White pixel inside the square and black corner: resolve must not change the solid colours.
                int inside = Red(90 * W + 200), corner = Red(5 * W + 5);
                Console.WriteLine($"INFO msaaspike: offscreen {f.Name} x{n}: bind {rc}, rendered {rendered}, read {read}, edge px {e.Mid}, levels {e.Levels}, inside {inside}, corner {corner}");
                check(rc == 0 && used == n && rendered == 1 && read == px.Length && inside > 250 && corner < 3, $"msaaspike: offscreen {f.Name} x{n} renders (used {used})");
                if (f.Tex == 90)
                    SavePng(px, Path.Combine(outDir, $"msaa_offscreen_x{n}.png"));
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_test_com_release(tex);
            }
            // Clamp: 16 is above what QRhi's D3D11 backend offers (1/2/4/8).
            Native.vlqt_test_d3d11_create_shared_texture(device, W, H, f.Tex, out var t16, out var h16);
            int rc16 = Native.vlqt_offscreen_bind(scene, device, h16, W, H, f.View, 16, out var used16);
            Native.vlqt_test_close_handle(h16);
            Console.WriteLine($"INFO msaaspike: offscreen {f.Name} request 16: bind {rc16}, used {used16}");
            check(rc16 == 0 && used16 is > 1 and <= 8, $"msaaspike: offscreen {f.Name} 16 clamped ({used16})");
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_test_com_release(t16);
            Native.vlqt_window_destroy(scene);
        }
        Native.vlqt_test_d3d11_release_device(device, context);
    }

    static void SavePng(byte[] bgra, string path)
    {
        using var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, d.Scan0, bgra.Length);
        b.UnlockBits(d);
        b.Save(path);
    }

    // 2000 rotated rectangles in 1280x720, 50 recoloured per frame.
    static void Heavy(CommandWriter cmd, int frame, bool create)
    {
        var rnd = new Random(1);
        cmd.Reset();
        if (create) cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
        for (uint i = 1; i <= 2000; i++)
        {
            float x = rnd.Next(1240), y = rnd.Next(680), a = rnd.Next(360);
            if (create)
            {
                cmd.Create(i, Protocol.Type.Rectangle);
                cmd.SetPlaceXf(i, x, y, 40, 40, Matrix.RotationZ(MathUtil.DegreesToRadians(a)));
                cmd.Reparent(i, 0, i);
            }
            if (create || (i + frame) % 40 == 0)
                cmd.SetColor(i, Protocol.Prop.Color, (i * 37 + frame) % 255 / 255f, (i * 91) % 255 / 255f, 0.5f, 1);
        }
    }

    static void WindowPerf(Action<bool, string> check, Action<int> pump)
    {
        var w = Native.vlqt_window_create(1280, 720, Title + " perf");
        var cmd = new CommandWriter();
        Heavy(cmd, 0, true);
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_show(w);
        pump(10);
        foreach (var n in new[] { 1, 2, 4, 8, 1, 4 })
        {
            Native.vlqt_window_set_samples(w, n);
            pump(10);
            var sw = Stopwatch.StartNew();
            int f = 0;
            while (sw.Elapsed.TotalSeconds < 2.5)
            {
                Heavy(cmd, ++f, false);
                Native.Apply(w, cmd.EndFrame());
                pump(1);
            }
            Native.vlqt_window_get_perf(w, out var p);
            Console.WriteLine($"INFO msaaspike: window perf x{n} (swap chain {Native.vlqt_window_samples(w)}): fps {p.fps:F0}, frame {p.frame_ms:F2}, render {p.render_ms:F2}, present {p.present_ms:F2}, gpu {p.gpu_ms:F2} (max {p.gpu_ms_max:F2}) ms");
        }
        Native.vlqt_window_destroy(w);
    }

    static void OffscreenPerf(Action<bool, string> check)
    {
        if (Native.vlqt_test_d3d11_create_device(0, out var device, out var context) != 0)
            return;
        const int PW = 1280, PH = 720;
        var px = new byte[PW * PH * 4];
        foreach (var n in new[] { 1, 2, 4, 8, 1 })
        {
            var scene = Native.vlqt_offscreen_create(PW, PH);
            var cmd = new CommandWriter();
            Heavy(cmd, 0, true);
            Native.Apply(scene, cmd.EndFrame());
            Native.vlqt_test_d3d11_create_shared_texture(device, PW, PH, 90, out var tex, out var handle);
            Native.vlqt_offscreen_bind(scene, device, handle, PW, PH, 87, n, out _);
            Native.vlqt_test_close_handle(handle);
            var times = new List<double>();
            for (int f = 1; f <= 130; f++)
            {
                Heavy(cmd, f, false);
                Native.Apply(scene, cmd.EndFrame());
                var sw = Stopwatch.StartNew();
                Native.OffscreenRenderBatch(context, new[] { scene }, f / 60.0);
                Native.TestReadTexture(tex, px, out _, out _, out _);
                if (f > 30) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"INFO msaaspike: offscreen perf x{n}: render_batch + readback median {times[times.Count / 2]:F2} ms, p90 {times[times.Count * 9 / 10]:F2} ms");
            Native.vlqt_window_destroy(scene);
            Native.vlqt_test_com_release(tex);
        }
        Native.vlqt_test_d3d11_release_device(device, context);
    }
}
