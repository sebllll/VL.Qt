using System;
using NUnit.Framework;
using System.IO;
using VL.Qt;

/// <summary>
/// (FileWriter, native layer): vlqt_image_write. A small BGRA8 premultiplied image with opaque and half-transparent
/// pixels as PNG into a not-yet-existing subfolder, read back through the image resource path (size, alpha and
/// un-premultiplied colors within +-2), straight RGBA8 with row padding, JPG and BMP (alpha over black), overwrite, and
/// the error codes: unknown extension -2, bad arguments -1, folder that cannot be created -3. Needs no window.
/// </summary>
[TestFixture, NonParallelizable]
public static class ImageWriteTest
{
    private const int W = 8, H = 4;

    /// <summary>Writing images.</summary>
    [Test, Category("imagewrite")]
    public static void ImageWrite() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "image-write");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // Left half opaque, right half alpha 128; the colour is (200, 100, 50) in both.
        const byte R = 200, G = 100, B = 50, A = 128;
        static byte Pm(byte c, byte a) => (byte)((c * a + 127) / 255);
        var bgraPm = new byte[W * H * 4];
        var rgbaStraight = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var a = x < W / 2 ? (byte)255 : A;
                var i = (y * W + x) * 4;
                bgraPm[i] = Pm(B, a); bgraPm[i + 1] = Pm(G, a); bgraPm[i + 2] = Pm(R, a); bgraPm[i + 3] = a;
                rgbaStraight[i] = R; rgbaStraight[i + 1] = G; rgbaStraight[i + 2] = B; rgbaStraight[i + 3] = a;
            }
        var desc = new Native.vlqt_pixel_desc { width = W, height = H, row_bytes = W * 4, format = 9, premultiplied = 1 };

        // Reads a file back as RGBA8 premultiplied through the image resource path (synchronous wait, no pump).
        static byte[] Load(string path, out int rw, out int rh)
        {
            rw = rh = 0;
            if (Native.vlqt_res_create(Protocol.ResKind.Image, out var id) != 0) return [];
            try
            {
                Native.vlqt_res_load_image(id, path, 0);
                if (Native.vlqt_res_wait(id, 5000) != Protocol.ResState.Ready) return [];
                var n = Native.ResReadPixels(id, Span<byte>.Empty, out rw, out rh);
                if (n >= 0) return [];
                var px = new byte[-n];
                return Native.ResReadPixels(id, px, out rw, out rh) == px.Length ? px : [];
            }
            finally { Native.vlqt_res_release(id); }
        }
        // Un-premultiplied colour of a premultiplied pixel read back, within tol of the expected values.
        static bool Colour(byte[] px, int x, int y, int r, int g, int b, int a, int tol)
        {
            var i = (y * W + x) * 4;
            if (px.Length <= i + 3 || Math.Abs(px[i + 3] - a) > tol) return false;
            int Un(int c) => a == 0 ? 0 : Math.Min(255, (c * 255 + a / 2) / a);
            return Math.Abs(Un(px[i]) - r) <= tol && Math.Abs(Un(px[i + 1]) - g) <= tol && Math.Abs(Un(px[i + 2]) - b) <= tol;
        }

        // PNG into a subfolder that does not exist yet.
        var png = Path.Combine(dir, "new", "sub folder", "image.png");
        var rc = Native.ImageWrite(desc, bgraPm, png, -1, out var error);
        s.Check(rc == 0 && error.Length == 0 && File.Exists(png), $"PNG written into new folders ({rc} {error})");
        var px = Load(png, out var rw, out var rh);
        s.Check(rw == W && rh == H, $"PNG reads back as {rw}x{rh}");
        s.Check(Colour(px, 0, 0, R, G, B, 255, 2) && Colour(px, W / 2 - 1, H - 1, R, G, B, 255, 2), "PNG opaque pixels (BGRA premultiplied input)");
        s.Check(Colour(px, W / 2, 0, R, G, B, A, 2) && Colour(px, W - 1, H - 1, R, G, B, A, 2), "PNG half-transparent pixels keep alpha and straight colour");

        // Straight RGBA8 with row padding overwrites the existing file.
        var padded = new byte[(W * 4 + 8) * H];
        for (int y = 0; y < H; y++)
            Array.Copy(rgbaStraight, y * W * 4, padded, y * (W * 4 + 8), W * 4);
        var rgbaDesc = desc with { row_bytes = W * 4 + 8, format = 7, premultiplied = 0 };
        rc = Native.ImageWrite(rgbaDesc, padded, png, -1, out error);
        px = Load(png, out rw, out rh);
        s.Check(rc == 0 && rw == W && Colour(px, 1, 1, R, G, B, 255, 2) && Colour(px, W - 2, 1, R, G, B, A, 2), $"straight RGBA8 with padding overwrites the file ({rc} {error})");

        // JPG: no alpha, the half-transparent pixels come out over black. Default quality and explicit quality.
        foreach (var q in new[] { -1, 90 })
        {
            var jpg = Path.Combine(dir, $"image_q{q}.JPG");
            rc = Native.ImageWrite(desc, bgraPm, jpg, q, out error);
            px = Load(jpg, out rw, out rh);
            s.Check(rc == 0 && File.Exists(jpg) && new FileInfo(jpg).Length > 0, $"JPG quality {q} written ({rc} {error})");
            s.Check(rw == W && Colour(px, 1, 1, R, G, B, 255, 12) && Colour(px, W - 2, 1, Pm(R, A), Pm(G, A), Pm(B, A), 255, 12),
                $"JPG quality {q} opaque, half-transparent over black");
        }
        var bmp = Path.Combine(dir, "image.bmp");
        rc = Native.ImageWrite(desc, bgraPm, bmp, -1, out error);
        px = Load(bmp, out rw, out rh);
        s.Check(rc == 0 && Colour(px, W - 2, 1, Pm(R, A), Pm(G, A), Pm(B, A), 255, 1), $"BMP over black ({rc} {error})");

        // Errors.
        var xyz = Path.Combine(dir, "image.xyz");
        rc = Native.ImageWrite(desc, bgraPm, xyz, -1, out error);
        s.Check(rc == -2 && error.Length > 0 && !File.Exists(xyz), $"unknown extension -> -2 with message ({rc}: {error})");
        rc = Native.ImageWrite(desc, bgraPm, Path.Combine(dir, "noextension"), -1, out error);
        s.Check(rc == -2 && error.Length > 0, $"no extension -> -2 ({rc}: {error})");
        var bad = Path.Combine(dir, "bad.png");
        rc = Native.ImageWrite(desc with { width = 0 }, bgraPm, bad, -1, out error);
        s.Check(rc == -1 && error.Length > 0, $"width 0 -> -1 with message ({rc}: {error})");
        rc = Native.ImageWrite(desc, bgraPm.AsSpan(0, bgraPm.Length - 1), bad, -1, out error);
        s.Check(rc == -1 && error.Length > 0, $"data too short -> -1 ({rc}: {error})");
        rc = Native.ImageWrite(desc with { row_bytes = W * 4 - 1 }, bgraPm, bad, -1, out error);
        s.Check(rc == -1, $"row_bytes too small -> -1 ({rc}: {error})");
        rc = Native.ImageWrite(desc with { format = 0 }, bgraPm, bad, -1, out error);
        s.Check(rc == -1 && error.Length > 0, $"unsupported pixel format -> -1 ({rc}: {error})");
        rc = Native.ImageWrite(desc, bgraPm, "", -1, out error);
        s.Check(rc == -1, $"empty path -> -1 ({rc}: {error})");
        s.Check(!File.Exists(bad), "bad arguments write no file");
        // A folder cannot be created below a file.
        var blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "x");
        rc = Native.ImageWrite(desc, bgraPm, Path.Combine(blocker, "sub", "image.png"), -1, out error);
        s.Check(rc == -3 && error.Length > 0, $"folder below a file -> -3 with message ({rc}: {error})");
    }
}
