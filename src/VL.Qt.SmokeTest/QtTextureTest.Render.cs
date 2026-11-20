using System;
#if WINDOWS
using System.Drawing;
#endif
using System.IO;
using VL.Qt;

/// <summary>
/// task 1.8, part 1: what lands in the texture. Pixels against a window render (a), premultiplied alpha (b),
/// 8-bit typeless textures with UNORM and SRGB views, float, unsupported formats (c), two scenes in one batch (e).
/// SRGB views and float hold linear premultiplied values (QtNative's linear pass). (d) is covered
/// by OffscreenScene (unchanged scene: no render, no fence signal) and extended in Batch.
/// </summary>
public static partial class QtTextureTest
{
    // A rectangle element: place in DIP = texture pixels, straight (non-premultiplied) colour.
    private readonly record struct Box(int X, int Y, int W, int H, float R, float G, float B, float A);

    // A texture format and the format the consumer samples it with (the bind format).
    private readonly record struct Fmt(string Name, int Texture, int View, int Bytes)
    {
        // The consumer reads linear values: QtNative writes a * linear(c) (vlqt_offscreen_bind).
        public bool Linear => View is R8G8B8A8UnormSrgb or B8G8R8A8UnormSrgb or R16G16B16A16Float;
    }

    private static readonly Fmt s_bgra = new("B8G8R8A8_TYPELESS/UNORM", B8G8R8A8Typeless, B8G8R8A8Unorm, 4);
    private static readonly Fmt s_rgba = new("R8G8B8A8_TYPELESS/UNORM", 27, R8G8B8A8Unorm, 4);
    private static readonly Fmt s_bgraSrgb = new("B8G8R8A8_TYPELESS/UNORM_SRGB", B8G8R8A8Typeless, B8G8R8A8UnormSrgb, 4);
    private static readonly Fmt s_rgbaSrgb = new("R8G8B8A8_TYPELESS/UNORM_SRGB", 27, R8G8B8A8UnormSrgb, 4);
    private static readonly Fmt s_float = new("R16G16B16A16_FLOAT", R16G16B16A16Float, R16G16B16A16Float, 8);
    private static readonly Fmt[] s_formats = { s_bgra, s_rgba, s_bgraSrgb, s_rgbaSrgb, s_float };
    // Leak test (here, not in its file: static fields of a partial class initialize per file in an undefined order):
    // UNORM (Qt renders into the texture), SRGB and float (the linear pass); the last one must be float.
    private static readonly Fmt[] s_leakFormats = { s_bgra, s_bgraSrgb, s_float };

    // sRGB decode of a 0..1 value.
    private static double Decode(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    // Premultiplied linear colour of a straight gamma-space colour, 0..255 rounded: a * linear(c).
    private static int[] LinearPremultiplied(float r, float g, float b, float a)
    {
        int L(float c) => (int)Math.Round(255 * a * Decode(c));
        return new[] { L(r), L(g), L(b), (int)Math.Round(255 * a) };
    }

    // A scene of w x h DIP with the boxes (element ids 1..n) on a clear colour; IntPtr.Zero on failure.
    private static IntPtr NewScene(int w, int h, (float R, float G, float B, float A) clear, Box[] boxes, Scenario s, string label)
    {
        var scene = Native.vlqt_offscreen_create(w, h);
        s.Check(scene != IntPtr.Zero, $"{label}: offscreen_create {(scene == IntPtr.Zero ? Native.LastError() : "")}");
        if (scene == IntPtr.Zero)
            return scene;
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(clear.R, clear.G, clear.B, clear.A);
        for (uint i = 0; i < boxes.Length; i++)
        {
            var b = boxes[i];
            cmd.Create(i + 1, Protocol.Type.Rectangle);
            cmd.SetPlace(i + 1, b.X, b.Y, b.W, b.H);
            cmd.SetColor(i + 1, Protocol.Prop.Color, b.R, b.G, b.B, b.A);
            cmd.Reparent(i + 1, 0, i);
        }
        int rc = Native.Apply(scene, cmd.EndFrame());
        s.Check(rc == 0, $"{label}: apply scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        return scene;
    }

    // Changes the colour of element id (1-based, as NewScene numbers them).
    private static void Recolor(IntPtr scene, uint id, float r, float g, float b, float a, Scenario s, string label)
    {
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.SetColor(id, Protocol.Prop.Color, r, g, b, a);
        int rc = Native.Apply(scene, cmd.EndFrame());
        s.Check(rc == 0, $"{label}: apply colour ({rc}) {(rc != 0 ? Native.LastError() : "")}");
    }

    // Pixels of a texture as bytes, null when the readback failed.
    private static byte[]? Read(IntPtr texture, int w, int h, Fmt f)
    {
        var pixels = new byte[w * h * f.Bytes];
        return Native.TestReadTexture(texture, pixels, out _, out _, out _) == pixels.Length ? pixels : null;
    }

    // R, G, B, A (0..255, rounded) of pixel x, y of a readback in format f.
    private static int[] Rgba(byte[] px, int w, int x, int y, Fmt f)
    {
        int i = (y * w + x) * f.Bytes;
        if (f.Texture == R16G16B16A16Float)
        {
            int H(int c) => (int)Math.Round((float)BitConverter.ToHalf(px, i + c * 2) * 255f);
            return new[] { H(0), H(1), H(2), H(3) };
        }
        return f.Texture == B8G8R8A8Typeless ? new[] { (int)px[i + 2], px[i + 1], px[i], px[i + 3] } : new[] { (int)px[i], px[i + 1], px[i + 2], px[i + 3] };
    }

    // R, G, B, A (0..255, rounded) of pixel x, y as the consumer samples it: an SRGB view decodes the bytes.
    private static int[] Seen(byte[] px, int w, int x, int y, Fmt f)
    {
        var c = Rgba(px, w, x, y, f);
        if (f.View is R8G8B8A8UnormSrgb or B8G8R8A8UnormSrgb)
            for (int i = 0; i < 3; i++)
                c[i] = (int)Math.Round(255 * Decode(c[i] / 255.0));
        return c;
    }

    private static bool Near(int[] actual, int[] expected, int tolerance)
    {
        for (int i = 0; i < 4; i++)
            if (Math.Abs(actual[i] - expected[i]) > tolerance)
                return false;
        return true;
    }

    private static string Str(int[] c) => $"{c[0]},{c[1]},{c[2]},{c[3]}";

    // A test device texture + NT handle in format f; false when it could not be created.
    private static bool NewTarget(IntPtr device, int w, int h, int textureFormat, out IntPtr texture, out IntPtr handle)
        => Native.vlqt_test_d3d11_create_shared_texture(device, w, h, textureFormat, out texture, out handle) == 0;

    // (c) The allowed formats render the same scene (UNORM: gamma-space values, SRGB and float: linear values);
    // unsupported formats are refused.
    private static void Formats(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "formats: create test device"))
            return;
        const int w = 64, h = 48;
        // Opaque orange (left), 50 % blue straight alpha (right), transparent between and around.
        var boxes = new[] { new Box(4, 4, 24, 40, 1, 0.5f, 0, 1), new Box(36, 4, 24, 40, 0, 0, 1, 0.5f) };
        var orange = new[] { 255, 128, 0, 255 };
        var linearOrange = LinearPremultiplied(1, 0.5f, 0, 1);
        var blue = new[] { 0, 0, 128, 128 }; // premultiplied; the same in linear light (0 and 1 do not change)
        var clear = new[] { 0, 0, 0, 0 };
        var scene = NewScene(w, h, (0, 0, 0, 0), boxes, s, "formats");
        if (scene == IntPtr.Zero)
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }

        int[]? floatOrange = null, floatBlue = null, srgbOrange = null, srgbBlue = null;
        foreach (var f in s_formats)
        {
            if (!check(NewTarget(device, w, h, f.Texture, out var tex, out var handle), $"formats: {f.Name}: create texture"))
                continue;
            int rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, f.View, 1, out _);
            Native.vlqt_test_close_handle(handle);
            check(rc == 0, $"formats: {f.Name}: bind ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            check(rc == 1, $"formats: {f.Name}: renders ({rc}) {(rc < 0 ? Native.LastError() : "")}");
            var px = Read(tex, w, h, f);
            if (check(px != null, $"formats: {f.Name}: read back"))
            {
                var o = Seen(px!, w, 14, 24, f);
                var b = Seen(px!, w, 48, 24, f);
                var c1 = Seen(px!, w, 32, 24, f);
                var c2 = Seen(px!, w, 1, 1, f);
                // 8-bit: rounding of 127.5; float: no quantisation, compared with the 8-bit SRGB result below.
                check(Near(o, f.Linear ? linearOrange : orange, 1) && Near(b, blue, 2),
                    $"formats: {f.Name}: orange {Str(o)} (expected {Str(f.Linear ? linearOrange : orange)}), 50 % blue premultiplied {Str(b)}");
                check(Near(c1, clear, 0) && Near(c2, clear, 0), $"formats: {f.Name}: clear alpha 0 = transparent black ({Str(c1)}, {Str(c2)})");
                if (f == s_bgraSrgb)
                    (srgbOrange, srgbBlue) = (o, b);
                else if (f == s_float)
                    (floatOrange, floatBlue) = (o, b);
            }
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_test_com_release(tex);
        }
        if (srgbOrange != null && floatOrange != null && srgbBlue != null && floatBlue != null)
            check(Near(floatOrange, srgbOrange, 2) && Near(floatBlue, srgbBlue, 2),
                $"formats: float matches the 8-bit SRGB result (orange {Str(floatOrange)} vs {Str(srgbOrange)}, blue {Str(floatBlue)} vs {Str(srgbBlue)})");

        // Refused: not in the list, 10 bit, a view that does not fit the texture.
        (string Name, int Texture, int View)[] bad =
        {
            ("R32G32B32A32_FLOAT view", R32G32B32A32Float, R32G32B32A32Float),
            ("R10G10B10A2_UNORM view", B8G8R8A8Typeless, 24),
            ("8-bit view on a float texture", R16G16B16A16Float, B8G8R8A8Unorm),
            ("SRGB view on a float texture", R16G16B16A16Float, B8G8R8A8UnormSrgb),
            ("RGBA SRGB view on a BGRA texture", B8G8R8A8Typeless, R8G8B8A8UnormSrgb),
            ("float view on an 8-bit texture", B8G8R8A8Typeless, R16G16B16A16Float),
        };
        foreach (var (name, textureFormat, view) in bad)
        {
            if (!check(NewTarget(device, w, h, textureFormat, out var tex, out var handle), $"formats: {name}: create texture"))
                continue;
            int rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, view, 1, out _);
            check(rc == Protocol.Offscreen.FormatUnsupported && Native.vlqt_offscreen_is_bound(scene) == 0,
                $"formats: {name} = FORMAT_UNSUPPORTED, scene unbound ({rc}) {(rc < 0 ? Native.LastError() : "")}");
            Native.vlqt_test_close_handle(handle);
            Native.vlqt_test_com_release(tex);
        }

        Native.vlqt_window_destroy(scene);
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "formats: test device freed");
    }

    // (e) Two scenes of different size and format in one batch, each into its own texture; then only one changes.
    private static void Batch(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "batch: create test device"))
            return;
        var red = new[] { 255, 0, 0, 255 };
        var green = new[] { 0, 255, 0, 255 };
        var blue = new[] { 0, 0, 255, 255 };
        var none = new[] { 0, 0, 0, 0 };
        var a = NewScene(64, 48, (0, 0, 0, 0), new[] { new Box(8, 8, 32, 24, 1, 0, 0, 1) }, s, "batch: scene A");
        var b = NewScene(80, 40, (0, 0, 0, 0), new[] { new Box(10, 6, 40, 28, 0, 1, 0, 1) }, s, "batch: scene B");
        var idle = Native.vlqt_offscreen_create(16, 16); // never bound
        IntPtr texA = default, handleA = default, texB = default, handleB = default;
        if (a == IntPtr.Zero || b == IntPtr.Zero || idle == IntPtr.Zero
            || !check(NewTarget(device, 64, 48, B8G8R8A8Typeless, out texA, out handleA), "batch: texture A")
            || !check(NewTarget(device, 80, 40, 27, out texB, out handleB), "batch: texture B"))
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        int rcA = Native.vlqt_offscreen_bind(a, device, handleA, 64, 48, B8G8R8A8Unorm, 1, out _);
        int rcB = Native.vlqt_offscreen_bind(b, device, handleB, 80, 40, R8G8B8A8Unorm, 1, out _);
        Native.vlqt_test_close_handle(handleA);
        Native.vlqt_test_close_handle(handleB);
        check(rcA == 0 && rcB == 0, $"batch: bind both ({rcA}, {rcB}) {(rcA != 0 || rcB != 0 ? Native.LastError() : "")}");

        // A null entry and an unbound scene in the list are skipped.
        int rc = Native.OffscreenRenderBatch(context, new[] { IntPtr.Zero, a, idle, b }, 0);
        check(rc == 2, $"batch: two scenes in one batch render ({rc}) {(rc < 0 ? Native.LastError() : "")}");
        var pa = Read(texA, 64, 48, s_bgra);
        var pb = Read(texB, 80, 40, s_rgba);
        if (check(pa != null && pb != null, "batch: read back both"))
        {
            check(Near(Rgba(pa!, 64, 24, 20, s_bgra), red, 0) && Near(Rgba(pa!, 64, 2, 2, s_bgra), none, 0) && Near(Rgba(pa!, 64, 60, 44, s_bgra), none, 0),
                $"batch: A is red on transparent ({Str(Rgba(pa!, 64, 24, 20, s_bgra))}, {Str(Rgba(pa!, 64, 2, 2, s_bgra))})");
            check(Near(Rgba(pb!, 80, 30, 20, s_rgba), green, 0) && Near(Rgba(pb!, 80, 2, 2, s_rgba), none, 0) && Near(Rgba(pb!, 80, 70, 36, s_rgba), none, 0),
                $"batch: B is green on transparent ({Str(Rgba(pb!, 80, 30, 20, s_rgba))}, {Str(Rgba(pb!, 80, 2, 2, s_rgba))})");
        }
        Native.vlqt_test_offscreen_state(a, out var readA1, out var writeA1, out var rendersA1);
        Native.vlqt_test_offscreen_state(b, out var readB1, out var writeB1, out var rendersB1);
        check(rendersA1 == 1 && rendersB1 == 1 && writeA1 == 1 && writeB1 == 1 && readA1 == 1 && readB1 == 1,
            $"batch: each scene signalled its own fences once (A {readA1}/{writeA1}/{rendersA1}, B {readB1}/{writeB1}/{rendersB1})");

        // (d) Nothing changed: no render, no signal, the textures keep their content.
        rc = Native.OffscreenRenderBatch(context, new[] { a, b }, 0);
        Native.vlqt_test_offscreen_state(a, out var readA2, out var writeA2, out var rendersA2);
        Native.vlqt_test_offscreen_state(b, out var readB2, out var writeB2, out var rendersB2);
        check(rc == 0 && (readA2, writeA2, rendersA2) == (readA1, writeA1, rendersA1) && (readB2, writeB2, rendersB2) == (readB1, writeB1, rendersB1),
            $"batch: unchanged scenes render nothing, fence values unchanged (rc {rc})");

        // Only B changes: one render, A's values stay, B's advance; B is blue now, A untouched.
        Recolor(b, 1, 0, 0, 1, 1, s, "batch");
        rc = Native.OffscreenRenderBatch(context, new[] { a, b }, 0);
        Native.vlqt_test_offscreen_state(a, out var readA3, out var writeA3, out var rendersA3);
        Native.vlqt_test_offscreen_state(b, out var readB3, out var writeB3, out var rendersB3);
        check(rc == 1 && (readA3, writeA3, rendersA3) == (readA1, writeA1, rendersA1) && rendersB3 == 2 && writeB3 > writeB2 && readB3 > readB2,
            $"batch: only B changed: one render, A unchanged, B fences advanced (rc {rc}, A {readA3}/{writeA3}/{rendersA3}, B {readB3}/{writeB3}/{rendersB3})");
        pa = Read(texA, 64, 48, s_bgra);
        pb = Read(texB, 80, 40, s_rgba);
        if (pa != null && pb != null)
            check(Near(Rgba(pa, 64, 24, 20, s_bgra), red, 0) && Near(Rgba(pb, 80, 30, 20, s_rgba), blue, 0),
                $"batch: A still red ({Str(Rgba(pa, 64, 24, 20, s_bgra))}), B blue ({Str(Rgba(pb, 80, 30, 20, s_rgba))})");

        Native.vlqt_offscreen_unbind(a);
        Native.vlqt_offscreen_unbind(b);
        Native.vlqt_window_destroy(a);
        Native.vlqt_window_destroy(b);
        Native.vlqt_window_destroy(idle);
        Native.vlqt_test_com_release(texA);
        Native.vlqt_test_com_release(texB);
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "batch: test device freed");
    }

    // (b) Premultiplied alpha: transparent clear = transparent black, semi-transparent elements, over each other,
    // a semi-transparent clear colour; the result on a checkerboard as PNG.
    private static void Alpha(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "alpha: create test device"))
            return;
        const int w = 96, h = 64;
        // Opaque red; 50 % green over its right part and the free space; 25 % blue; 50 % orange (a colour that sRGB
        // decoding changes); all straight alpha.
        var boxes = new[]
        {
            new Box(4, 4, 40, 30, 1, 0, 0, 1),
            new Box(30, 20, 40, 30, 0, 1, 0, 0.5f),
            new Box(66, 4, 26, 40, 0, 0, 1, 0.25f),
            new Box(4, 40, 22, 20, 1, 0.5f, 0, 0.5f),
        };
        var scene = NewScene(w, h, (0, 0, 0, 0), boxes, s, "alpha");
        var clearScene = NewScene(16, 16, (1, 0, 0, 0.5f), Array.Empty<Box>(), s, "alpha clear colour");
        IntPtr tex = default, handle = default, clearTex = default, clearHandle = default;
        if (scene == IntPtr.Zero || clearScene == IntPtr.Zero
            || !check(NewTarget(device, w, h, B8G8R8A8Typeless, out tex, out handle), "alpha: texture")
            || !check(NewTarget(device, 16, 16, B8G8R8A8Typeless, out clearTex, out clearHandle), "alpha: clear colour texture"))
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        int rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
        int rc2 = Native.vlqt_offscreen_bind(clearScene, device, clearHandle, 16, 16, B8G8R8A8Unorm, 1, out _);
        Native.vlqt_test_close_handle(handle);
        Native.vlqt_test_close_handle(clearHandle);
        check(rc == 0 && rc2 == 0, $"alpha: bind ({rc}, {rc2}) {(rc != 0 || rc2 != 0 ? Native.LastError() : "")}");
        rc = Native.OffscreenRenderBatch(context, new[] { scene, clearScene }, 0);
        check(rc == 2, $"alpha: render ({rc}) {(rc < 0 ? Native.LastError() : "")}");

        var px = Read(tex, w, h, s_bgra);
        if (check(px != null, "alpha: read back"))
        {
            var transparent = Rgba(px!, w, 1, 1, s_bgra);
            var green = Rgba(px!, w, 40, 48, s_bgra); // 50 % green alone (below the red box)
            var greenOverRed = Rgba(px!, w, 40, 25, s_bgra);
            var blue = Rgba(px!, w, 80, 24, s_bgra);
            check(Near(transparent, new[] { 0, 0, 0, 0 }, 0), $"alpha: clear alpha 0 = transparent black ({Str(transparent)})");
            check(Near(green, new[] { 0, 128, 0, 128 }, 2), $"alpha: 50 % green is premultiplied (0,128,0,128) ({Str(green)})");
            check(Near(blue, new[] { 0, 0, 64, 64 }, 2), $"alpha: 25 % blue is premultiplied (0,0,64,64) ({Str(blue)})");
            check(Near(greenOverRed, new[] { 128, 128, 0, 255 }, 2), $"alpha: 50 % green over opaque red = (128,128,0,255) ({Str(greenOverRed)})");
            var orange = Rgba(px!, w, 14, 50, s_bgra);
            check(Near(orange, new[] { 128, 64, 0, 128 }, 2), $"alpha: 50 % orange is premultiplied in gamma space (128,64,0,128) ({Str(orange)})");
            WriteChecker(Path.Combine(s.OutDir, "qttexture_alpha_checker.png"), px!, w, h);
        }

        // SRGB view and float: the consumer reads a * linear(c). Opaque pixels and saturated colours (0 and 1) are the
        // same as through the UNORM view; 50 % orange is not (gamma premultiplied it would read 55,13,0,128 through SRGB).
        foreach (var f in new[] { s_bgraSrgb, s_float })
        {
            if (!check(NewTarget(device, w, h, f.Texture, out var linTex, out var linHandle), $"alpha {f.Name}: texture"))
                continue;
            rc = Native.vlqt_offscreen_bind(scene, device, linHandle, w, h, f.View, 1, out _);
            Native.vlqt_test_close_handle(linHandle);
            check(rc == 0, $"alpha {f.Name}: bind ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
            check(rc == 1, $"alpha {f.Name}: render ({rc}) {(rc < 0 ? Native.LastError() : "")}");
            var lp = Read(linTex, w, h, f);
            if (check(lp != null, $"alpha {f.Name}: read back"))
            {
                var transparent = Seen(lp!, w, 1, 1, f);
                var red = Seen(lp!, w, 10, 10, f);
                var green = Seen(lp!, w, 40, 48, f);
                var orange = Seen(lp!, w, 14, 50, f);
                var expected = LinearPremultiplied(1, 0.5f, 0, 0.5f);
                check(Near(transparent, new[] { 0, 0, 0, 0 }, 0), $"alpha {f.Name}: clear alpha 0 = transparent black ({Str(transparent)})");
                check(Near(red, new[] { 255, 0, 0, 255 }, 0), $"alpha {f.Name}: opaque red unchanged ({Str(red)})");
                check(Near(green, new[] { 0, 128, 0, 128 }, 2), $"alpha {f.Name}: 50 % green (0,128,0,128) ({Str(green)})");
                check(Near(orange, expected, 2), $"alpha {f.Name}: 50 % orange = a * linear(c) {Str(expected)} ({Str(orange)})");
            }
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_test_com_release(linTex);
        }

        // Does Qt premultiply a semi-transparent clear colour itself? QtNative passes (1, 0, 0, 0.5) straight to
        // QQuickWindow::setColor. Answer: no, Qt clears with the colour as it is, (255,0,0,128): not premultiplied
        // (premultiplied would be (128,0,0,128)). A Clear Color pin with alpha < 1 must be premultiplied by the caller.
        var cp = Read(clearTex, 16, 16, s_bgra);
        if (check(cp != null, "alpha: read back the clear colour texture"))
        {
            var c = Rgba(cp!, 16, 8, 8, s_bgra);
            s.Info($"alpha: clear colour (1, 0, 0, 0.5) gives the texel {Str(c)}");
            check(Near(c, new[] { 255, 0, 0, 128 }, 1), $"alpha: Qt writes a semi-transparent clear colour as it is, not premultiplied ({Str(c)})");
        }

        Native.vlqt_offscreen_unbind(scene);
        Native.vlqt_offscreen_unbind(clearScene);
        Native.vlqt_window_destroy(scene);
        Native.vlqt_window_destroy(clearScene);
        Native.vlqt_test_com_release(tex);
        Native.vlqt_test_com_release(clearTex);
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "alpha: test device freed");
    }

    // Premultiplied BGRA composited over a grey checkerboard (8 px cells), as an opaque PNG: transparency is visible.
    private static void WriteChecker(string path, byte[] bgra, int w, int h)
    {
        var composed = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int bg = ((x / 8 + y / 8) & 1) == 0 ? 204 : 136;
                int inv = 255 - bgra[i + 3];
                for (int c = 0; c < 3; c++)
                    composed[i + c] = (byte)Math.Min(255, bgra[i + c] + (inv * bg + 127) / 255);
                composed[i + 3] = 255;
            }
        WritePng(path, composed, w, h);
    }

    // (a) The same scene in a window and in the texture: pixels match within a small tolerance.
    private static void WindowMatch(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
#if WINDOWS
        if (!check(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "window: create test device"))
            return;
        const int w = 200, h = 150;
        // Opaque background so that premultiplied and straight values agree; rectangles, one translucent over the other.
        var boxes = new[]
        {
            new Box(20, 20, 100, 60, 1, 0.5f, 0, 1),
            new Box(80, 50, 100, 80, 0, 1, 0.25f, 0.5f),
            new Box(10, 100, 60, 40, 0.2f, 0.4f, 1, 1),
        };
        var clear = (0.125f, 0.25f, 0.5f, 1f);
        var scene = NewScene(w, h, clear, boxes, s, "window: texture scene");
        var win = Native.vlqt_window_create(w, h, "VL.Qt QtTexture window match");
        IntPtr tex = default, handle = default;
        check(win != IntPtr.Zero, "window: window_create");
        if (scene == IntPtr.Zero || win == IntPtr.Zero
            || !check(NewTarget(device, w, h, B8G8R8A8Typeless, out tex, out handle), "window: texture"))
        {
            Native.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        // The window gets the same scene.
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(clear.Item1, clear.Item2, clear.Item3, clear.Item4);
        for (uint i = 0; i < boxes.Length; i++)
        {
            var b = boxes[i];
            cmd.Create(i + 1, Protocol.Type.Rectangle);
            cmd.SetPlace(i + 1, b.X, b.Y, b.W, b.H);
            cmd.SetColor(i + 1, Protocol.Prop.Color, b.R, b.G, b.B, b.A);
            cmd.Reparent(i + 1, 0, i);
        }
        check(Native.Apply(win, cmd.EndFrame()) == 0, "window: apply to the window");
        Native.vlqt_window_show(win);
        s.Pump(3);

        int rc = Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
        Native.vlqt_test_close_handle(handle);
        check(rc == 0, $"window: bind ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        rc = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        check(rc == 1, $"window: render ({rc}) {(rc < 0 ? Native.LastError() : "")}");
        var px = Read(tex, w, h, s_bgra);
        if (check(px != null, "window: read back"))
        {
            WritePng(Path.Combine(s.OutDir, "qttexture_match_texture.png"), px!, w, h);
            using var shot = s.Grab(win, "qttexture_match_window.png");
            // Grabs are in device pixels (HiDPI): the window pixel of the texel centre.
            int bad = 0, maxDiff = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var t = Rgba(px!, w, x, y, s_bgra);
                    var p = shot.GetPixel(Math.Min(shot.Width - 1, (x * 2 + 1) * shot.Width / (2 * w)), Math.Min(shot.Height - 1, (y * 2 + 1) * shot.Height / (2 * h)));
                    int d = Math.Max(Math.Abs(t[0] - p.R), Math.Max(Math.Abs(t[1] - p.G), Math.Abs(t[2] - p.B)));
                    maxDiff = Math.Max(maxDiff, d);
                    if (d > 3)
                        bad++;
                }
            // Edges of the rectangles may differ at a HiDPI window (anti-aliasing); the areas must match.
            check(bad <= w * h / 100, $"window: texture matches the window render ({bad} of {w * h} pixels differ by more than 3, max difference {maxDiff})");
            var sample = Rgba(px!, w, 50, 40, s_bgra);
            check(Near(sample, new[] { 255, 128, 0, 255 }, 1), $"window: texture orange area {Str(sample)}");
        }

        Native.vlqt_offscreen_unbind(scene);
        Native.vlqt_window_destroy(scene);
        Native.vlqt_window_destroy(win);
        Native.vlqt_test_com_release(tex);
        check(Native.vlqt_test_d3d11_release_device(device, context) == 0, "window: test device freed");
#endif
    }
}
