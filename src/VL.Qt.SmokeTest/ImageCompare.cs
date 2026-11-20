using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Styles;

/// <summary>
/// Compares grabbed windows with reference images (TEST-03). The compare always runs on WARP (the software D3D11 adapter,
/// forced by QSG_RHI_PREFER_SOFTWARE_RENDERER=1 before Qt starts, so the compare scenes run in a helper process of their own:
/// <see cref="Run"/>), so a local run and the CI see the same pixels. References live in
/// src/VL.Qt.SmokeTest/references/&lt;name&gt;.png (not in docs/images, README pictures never change through a test run).
/// They change only through an explicit update: the [Explicit] test `update-refs` (or the run parameter UpdateRefs=1), run on
/// the x64 workstation, committed on its own with the diff images in the pull request description.
/// </summary>
/// <remarks>
/// Check on another machine (e.g. the arm64 notebook), one command: `dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=image-compare`.
/// Green = WARP there matches the x64 references within the limits; a FAIL names the grab and the written diff image.
/// </remarks>
static class ImageCompare
{
    // The limits were set from x64 WARP runs, which are deterministic (difference 0.000 in every scene, 0.002 in the collage).
    // Keep them tight: a 20 x 20 block on a 400 x 300 picture (0.33 %) must fail. If WARP on arm64 does not fit, the arm64
    // WARP check of P9 decides about a second reference set (win-arm64); do not loosen the limits instead.

    /// <summary>Limit of the mean absolute difference per channel (0..255) over the whole picture.</summary>
    public const double MaxMeanDifference = 0.25;

    /// <summary>Limit of the share of pixels in which any channel differs by more than <see cref="PixelThreshold"/>.</summary>
    public const double MaxDifferingShare = 0.001;

    /// <summary>Per-channel difference from which a pixel counts as differing.</summary>
    public const int PixelThreshold = 24;

    /// <summary>Compare scenes the helper process knows (see OwnProcess).</summary>
    public static readonly string[] Scenes = { "readme-images", "readme-gallery", "reference-scenes" };

    /// <summary>
    /// Runs the README scenarios (readme-images, readme-gallery) in a helper process on the normal renderer and writes the
    /// pictures to docs/images (the former behaviour); never touches the references.
    /// </summary>
    public static void RunDocs(Scenario s, string[] scenarios)
    {
        var env = new Dictionary<string, string> { ["VLQT_SMOKE_DOCSIMAGES"] = "1" };
        OwnProcess.Run(s, scenarios, env);
    }

    /// <summary>
    /// Runs the compare scenario <paramref name="scenario"/> in a helper process on WARP; <paramref name="update"/> writes the
    /// references instead of comparing.
    /// </summary>
    public static void Run(Scenario s, string[] scenarios, bool update = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip($"{string.Join("+", scenarios)}: the compare runs on WARP (Windows only)");
            return;
        }
        var env = new Dictionary<string, string> { ["QSG_RHI_PREFER_SOFTWARE_RENDERER"] = "1" };
        if (update || SmokeParameters.UpdateRefs)
            env["VLQT_SMOKE_UPDATEREFS"] = "1";
        OwnProcess.Run(s, scenarios, env);
    }

    /// <summary>
    /// Registers the bundled DejaVu Sans (the text of the compare scenes must not depend on the installed fonts) and returns
    /// its family name, "" if it is not found.
    /// </summary>
    public static string LoadFont(Scenario s)
    {
        // README pictures keep the default font (as before).
        if (SmokeParameters.DocsImages)
            return "";
        var file = FindUp("src", "VL.Qt.Native", "fonts", "DejaVuSans.ttf");
        s.Check(file != null, "bundled font src/VL.Qt.Native/fonts/DejaVuSans.ttf found");
        if (file == null || Native.FontAdd(file, out _, out var families) != 0)
            return "";
        return families.Split('\n')[0];
    }

    internal static string? FindUp(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p) || Directory.Exists(p))
                return p;
        }
        return null;
    }

    /// <summary>The style with the text font family set for the normal state (ignored if <paramref name="family"/> is empty).</summary>
    public static Style WithFont(Style style, string family) => family.Length == 0
        ? style
        : style with { Text = style.Text! with { FontFamily = default(StateValues<string>).With(StyleStates.Normal, family) } };

    /// <summary>Share of the sampled pixels that differ from the ground: &gt; 0 = something is drawn.</summary>
    public static double Ink(Bitmap a, int ground = 0x16)
    {
        int n = 0, ink = 0;
        for (var y = 0; y < a.Height; y += 6)
            for (var x = 0; x < a.Width; x += 6, n++)
            {
                var c = a.GetPixel(x, y);
                if (Math.Abs(c.R - ground) + Math.Abs(c.G - ground) + Math.Abs(c.B - ground) > 60)
                    ink++;
            }
        return ink / (double)Math.Max(n, 1);
    }

#if WINDOWS
    /// <summary>
    /// Compares the grab with references/&lt;name&gt;.png (scaled to <paramref name="width"/>, the grab is in device pixels) and
    /// records the result; with UpdateRefs it writes the reference instead.
    /// </summary>
    public static void Check(Scenario s, string name, string grabPath, int width)
    {
        if (SmokeParameters.DocsImages)
        {
            WriteDocs(s, name, grabPath, width);
            return;
        }
        var refDir = FindUp("src", "VL.Qt.SmokeTest");
        if (refDir == null)
        {
            s.Check(false, $"{name}: src/VL.Qt.SmokeTest not found (references folder)");
            return;
        }
        refDir = Path.Combine(refDir, "references");
        var refPath = Path.Combine(refDir, name + ".png");
        using var actual = Scale(grabPath, width);
        if (SmokeParameters.UpdateRefs)
        {
            Directory.CreateDirectory(refDir);
            actual.Save(refPath, System.Drawing.Imaging.ImageFormat.Png);
            s.Info($"{name}: reference written {refPath} ({new FileInfo(refPath).Length / 1024} KB)");
            return;
        }
        if (!File.Exists(refPath))
        {
            s.Check(false, $"{name}: no reference {refPath} (grab {grabPath}); create it with the explicit test `update-refs`");
            return;
        }
        using var reference = new Bitmap(refPath);
        var diffPath = Path.Combine(Path.GetDirectoryName(grabPath)!, name + "_diff.png");
        var (ok, text) = Compare(actual, reference, diffPath);
        s.Info($"{name}: {text}");
        s.Check(ok, $"{name}: matches reference ({text}); grab {grabPath}" + (ok ? "" : $", diff image {diffPath}"));
    }

    // README picture: scaled to the target width, docs/images/<name without the scenario prefix>.png.
    static void WriteDocs(Scenario s, string name, string grabPath, int width)
    {
        var imagesDir = FindUp("docs", "images");
        s.Check(imagesDir != null, $"{name}: docs/images found ({imagesDir})");
        if (imagesDir == null)
            return;
        var target = Path.Combine(imagesDir, DocsName(name) + ".png");
        using (var scaled = Scale(grabPath, width))
            scaled.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        s.Info($"{name}: {target} ({new FileInfo(target).Length / 1024} KB)");
    }

    static string DocsName(string name) => name.StartsWith("readme-images_") ? name["readme-images_".Length..] : name.StartsWith("readme-gallery_") ? name["readme-gallery_".Length..] : name;

    /// <summary>The picture scaled to the target width (bicubic, deterministic); the caller disposes it.</summary>
    static Bitmap Scale(string path, int width)
    {
        using var a = new Bitmap(path);
        var height = (int)MathF.Round(a.Height * width / (float)a.Width);
        var scaled = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(a, 0, 0, width, height);
        return scaled;
    }

    /// <summary>
    /// Mean absolute difference per channel and share of differing pixels against the limits; writes an amplified difference
    /// image (x4, differing pixels in the original red channel at full) to <paramref name="diffPath"/> if the limits are exceeded.
    /// </summary>
    public static (bool ok, string text) Compare(Bitmap actual, Bitmap reference, string diffPath)
    {
        if (actual.Width != reference.Width || actual.Height != reference.Height)
            return (false, $"size {actual.Width}x{actual.Height} instead of {reference.Width}x{reference.Height}");
        var w = actual.Width;
        var h = actual.Height;
        var a = Bytes(actual);
        var r = Bytes(reference);
        long sum = 0;
        long differing = 0;
        var diff = new byte[a.Length];
        for (var i = 0; i < a.Length; i += 4)
        {
            var max = 0;
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(a[i + c] - r[i + c]);
                sum += d;
                max = Math.Max(max, d);
                diff[i + c] = (byte)Math.Min(255, d * 4);
            }
            diff[i + 3] = 255;
            if (max > PixelThreshold)
            {
                differing++;
                diff[i + 2] = 255; // red (BGRA)
            }
        }
        var mean = sum / (3.0 * w * h);
        var share = differing / (double)(w * h);
        var ok = mean <= MaxMeanDifference && share <= MaxDifferingShare;
        var text = $"mean difference {mean:F3} (limit {MaxMeanDifference}), differing pixels {share:P2} (limit {MaxDifferingShare:P1})";
        if (!ok)
        {
            using var img = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bits = img.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Copy(diff, bits, toBitmap: true);
            img.UnlockBits(bits);
            img.Save(diffPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        return (ok, text);
    }

    static byte[] Bytes(Bitmap b)
    {
        var bits = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = new byte[b.Width * b.Height * 4];
        Copy(data, bits, toBitmap: false);
        b.UnlockBits(bits);
        return data;
    }

    // Row by row (the stride may be padded).
    static void Copy(byte[] data, System.Drawing.Imaging.BitmapData bits, bool toBitmap)
    {
        var row = bits.Width * 4;
        for (var y = 0; y < bits.Height; y++)
        {
            var p = bits.Scan0 + y * bits.Stride;
            if (toBitmap)
                System.Runtime.InteropServices.Marshal.Copy(data, y * row, p, row);
            else
                System.Runtime.InteropServices.Marshal.Copy(p, data, y * row, row);
        }
    }
#else
    public static void Check(Scenario s, string name, string grabPath, int width)
    {
        if (!SmokeParameters.DocsImages)
        {
            s.Skip($"{name}: the compare runs on WARP (Windows only)");
            return;
        }
        var imagesDir = FindUp("docs", "images");
        s.Check(imagesDir != null, $"{name}: docs/images found ({imagesDir})");
        if (imagesDir != null)
            File.Copy(grabPath, Path.Combine(imagesDir, DocsName(name) + ".png"), true);
    }

    static string DocsName(string name) => name.StartsWith("readme-images_") ? name["readme-images_".Length..] : name.StartsWith("readme-gallery_") ? name["readme-gallery_".Length..] : name;
#endif
}
