using System;
using NUnit.Framework;
using System.IO;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (FileWriter, native layer): vlqt_image_write_file. A small BGRA8 premultiplied image with opaque and half-transparent
/// pixels as PNG into a not-yet-existing subfolder, read back through the image resource path (size, alpha and
/// un-premultiplied colors within +-2), straight RGBA8 with row padding (the last row without its padding), JPG and BMP
/// (alpha over black), overwrite, R16 and half/full float RGBA as 16-bit PNG (bit depth read from the file header) and as
/// 8-bit JPG, and the error codes: unknown extension Unsupported, bad arguments BadArgument, folder that cannot be
/// created Io. Needs no window.
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
            if (Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id) != 0) return [];
            try
            {
                Native.vlqt_image_load(id, path, 0);
                if (Native.vlqt_res_wait(id, 5000) != Protocol.ResourceState.Ready) return [];
                var rc = NativeCalls.ReadPixels(id, Span<byte>.Empty, out rw, out rh, out var required);
                if (rc != (int)Native.Result.BufferTooSmall) return [];
                var px = new byte[required];
                return NativeCalls.ReadPixels(id, px, out rw, out rh) == px.Length ? px : [];
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
        var rc = Native.ImageWriteFile(desc, bgraPm, png, -1, out var error);
        s.Check(rc == Native.Result.Ok && error.Length == 0 && File.Exists(png), $"PNG written into new folders ({rc} {error})");
        var px = Load(png, out var rw, out var rh);
        s.Check(rw == W && rh == H, $"PNG reads back as {rw}x{rh}");
        s.Check(Colour(px, 0, 0, R, G, B, 255, 2) && Colour(px, W / 2 - 1, H - 1, R, G, B, 255, 2), "PNG opaque pixels (BGRA premultiplied input)");
        s.Check(Colour(px, W / 2, 0, R, G, B, A, 2) && Colour(px, W - 1, H - 1, R, G, B, A, 2), "PNG half-transparent pixels keep alpha and straight colour");

        // Straight RGBA8 with row padding overwrites the existing file; len holds the pixels of the last row only.
        var padded = new byte[(W * 4 + 8) * H - 8];
        for (int y = 0; y < H; y++)
            Array.Copy(rgbaStraight, y * W * 4, padded, y * (W * 4 + 8), W * 4);
        var rgbaDesc = desc with { row_bytes = W * 4 + 8, format = 7, premultiplied = 0 };
        rc = Native.ImageWriteFile(rgbaDesc, padded, png, -1, out error);
        px = Load(png, out rw, out rh);
        s.Check(rc == Native.Result.Ok && rw == W && Colour(px, 1, 1, R, G, B, 255, 2) && Colour(px, W - 2, 1, R, G, B, A, 2), $"straight RGBA8 with padding overwrites the file ({rc} {error})");

        // More than 8 bit: a 16-bit PNG (bit depth and colour type from the IHDR chunk), 8 bit for other file formats.
        static (int depth, int colorType) PngHeader(string path)
        {
            var head = File.ReadAllBytes(path);
            return head.Length > 25 ? (head[24], head[25]) : (0, -1);
        }
        var gray16 = new byte[W * H * 2];
        for (int i = 0; i < W * H; i++)
            BitConverter.TryWriteBytes(gray16.AsSpan(i * 2), (ushort)(i * 2000));
        var half = new byte[W * H * 8];
        var single = new byte[W * H * 16];
        for (int i = 0; i < W * H; i++)
        {
            float[] rgba = [R / 255f, G / 255f, B / 255f, 1f];
            for (int c = 0; c < 4; c++)
            {
                BitConverter.TryWriteBytes(half.AsSpan(i * 8 + c * 2), (Half)rgba[c]);
                BitConverter.TryWriteBytes(single.AsSpan(i * 16 + c * 4), rgba[c]);
            }
        }
        var deep = new (string Name, int Format, int Bpp, byte[] Data, int ColorType)[]
        {
            ("R16", 2, 2, gray16, 0), ("half RGBA", 12, 8, half, 6), ("float RGBA", 10, 16, single, 6),
        };
        foreach (var (name, format, bpp, data, colorType) in deep)
        {
            var deepDesc = desc with { row_bytes = W * bpp, format = format, premultiplied = 0 };
            var deepPng = Path.Combine(dir, $"deep_{format}.png");
            rc = Native.ImageWriteFile(deepDesc, data, deepPng, -1, out error);
            var header = rc == Native.Result.Ok ? PngHeader(deepPng) : (0, -1);
            s.Check(header == (16, colorType), $"{name} PNG has 16 bit and colour type {colorType} ({rc} {error}; {header})");
            if (format != 2)
            {
                px = Load(deepPng, out rw, out rh);
                s.Check(rw == W && Colour(px, 1, 1, R, G, B, 255, 2), $"{name} PNG reads back with the colour");
            }
            var deepJpg = Path.Combine(dir, $"deep_{format}.jpg");
            rc = Native.ImageWriteFile(deepDesc, data, deepJpg, -1, out error);
            s.Check(rc == Native.Result.Ok && File.Exists(deepJpg), $"{name} as JPG is written in 8 bit ({rc} {error})");
        }

        // JPG: no alpha, the half-transparent pixels come out over black. Default quality and explicit quality.
        foreach (var q in new[] { -1, 90 })
        {
            var jpg = Path.Combine(dir, $"image_q{q}.JPG");
            rc = Native.ImageWriteFile(desc, bgraPm, jpg, q, out error);
            px = Load(jpg, out rw, out rh);
            s.Check(rc == Native.Result.Ok && File.Exists(jpg) && new FileInfo(jpg).Length > 0, $"JPG quality {q} written ({rc} {error})");
            s.Check(rw == W && Colour(px, 1, 1, R, G, B, 255, 12) && Colour(px, W - 2, 1, Pm(R, A), Pm(G, A), Pm(B, A), 255, 12),
                $"JPG quality {q} opaque, half-transparent over black");
        }
        var bmp = Path.Combine(dir, "image.bmp");
        rc = Native.ImageWriteFile(desc, bgraPm, bmp, -1, out error);
        px = Load(bmp, out rw, out rh);
        s.Check(rc == Native.Result.Ok && Colour(px, W - 2, 1, Pm(R, A), Pm(G, A), Pm(B, A), 255, 1), $"BMP over black ({rc} {error})");

        // Errors.
        var xyz = Path.Combine(dir, "image.xyz");
        rc = Native.ImageWriteFile(desc, bgraPm, xyz, -1, out error);
        s.Check(rc == Native.Result.Unsupported && error.Length > 0 && !File.Exists(xyz), $"unknown extension -> Unsupported with message ({rc}: {error})");
        rc = Native.ImageWriteFile(desc, bgraPm, Path.Combine(dir, "noextension"), -1, out error);
        s.Check(rc == Native.Result.Unsupported && error.Length > 0, $"no extension -> Unsupported ({rc}: {error})");
        var bad = Path.Combine(dir, "bad.png");
        rc = Native.ImageWriteFile(desc with { width = 0 }, bgraPm, bad, -1, out error);
        s.Check(rc == Native.Result.BadArgument && error.Length > 0, $"width 0 -> BadArgument with message ({rc}: {error})");
        rc = Native.ImageWriteFile(desc, bgraPm.AsSpan(0, bgraPm.Length - 1), bad, -1, out error);
        s.Check(rc == Native.Result.BadArgument && error.Length > 0, $"data too short -> BadArgument ({rc}: {error})");
        rc = Native.ImageWriteFile(desc with { row_bytes = W * 4 - 1 }, bgraPm, bad, -1, out error);
        s.Check(rc == Native.Result.BadArgument, $"row_bytes too small -> BadArgument ({rc}: {error})");
        rc = Native.ImageWriteFile(desc with { format = 0 }, bgraPm, bad, -1, out error);
        s.Check(rc == Native.Result.BadArgument && error.Length > 0, $"unsupported pixel format -> BadArgument ({rc}: {error})");
        rc = Native.ImageWriteFile(desc, bgraPm, "", -1, out error);
        s.Check(rc == Native.Result.BadArgument, $"empty path -> BadArgument ({rc}: {error})");
        s.Check(!File.Exists(bad), "bad arguments write no file");
        // A folder cannot be created below a file.
        var blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "x");
        rc = Native.ImageWriteFile(desc, bgraPm, Path.Combine(blocker, "sub", "image.png"), -1, out error);
        s.Check(rc == Native.Result.Io && error.Length > 0, $"folder below a file -> Io with message ({rc}: {error})");
    }
}
