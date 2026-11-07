using System;
using System.Drawing;
using System.IO;
using VL.Qt;

// Image in QtNative (plan 05d, #361): image resource (PROP_IMAGE), fillMode, smooth. Grabs go to <outDir>/05_image*.png.
// Test asset: assets/stripes_rgb_30x10.png (30x10 px, 10 px stripes red | green | blue). Values are DIP.
static class ImageTest
{
    const string AssetName = "stripes_rgb_30x10.png";
    const uint Stretch = 1, Smooth = 2, Fit = 3, Crop = 4, Tile = 5, Missing = 6, Unicode = 7;

    /// <summary>Finds src/VL.Qt.SmokeTest/assets by walking up from the output directory (no copy step needed).</summary>
    internal static string? FindAsset(string name = AssetName)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "VL.Qt.SmokeTest", "assets", name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    /// <summary>Image resource from a file, loaded synchronously (vlqt_res_wait); 0 if create failed.</summary>
    internal static ulong Load(string path, Action<bool, string> check, string what, uint flags = 0)
    {
        if (Native.vlqt_res_create(Protocol.ResKind.Image, out var id) != 0)
        {
            check(false, $"361: res_create for {what} ({Native.LastError()})");
            return 0;
        }
        Native.vlqt_res_load_image(id, path, flags);
        Native.vlqt_res_wait(id, 10000);
        return id;
    }

    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    internal static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    internal static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    internal static bool IsBlue(Color c) => c.B > 200 && c.R < 40 && c.G < 40;
    internal static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var asset = FindAsset();
        check(asset != null, $"05d: test asset {AssetName} found");
        if (asset == null)
            return;
        // Copy to a path with a space and non-ASCII characters (resource load of a UTF-8 path).
        var unicodeDir = Path.Combine(outDir, "bild ä");
        Directory.CreateDirectory(unicodeDir);
        var unicodePath = Path.Combine(unicodeDir, "streifen ö.png");
        File.Copy(asset, unicodePath, true);
        var missingPath = Path.Combine(outDir, "missing_vlqt_05d.png");

        // #361: the element draws an image resource (no Source any more); load errors belong to the resource.
        var stripes = Load(asset, check, "stripes");
        var unicode = Load(unicodePath, check, "unicode path");
        var missing = Load(missingPath, check, "missing file");
        check(Native.vlqt_res_wait(stripes, 0) == Protocol.ResState.Ready && Native.vlqt_res_wait(unicode, 0) == Protocol.ResState.Ready
            && Native.vlqt_res_wait(missing, 0) == Protocol.ResState.Failed, "361: resources Ready (2) / Failed (missing file)");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt image test");
        check(w != IntPtr.Zero, "05d: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        int Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            check(rc == 0, $"05d: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Image(uint id, ulong image, float x, float y, float width, float height, int fillMode, bool smooth)
        {
            cmd.Create(id, Protocol.Type.Image);
            cmd.SetPlace(id, x, y, width, height);
            cmd.SetU64(id, Protocol.Prop.Image, image);
            if (fillMode != 0)
                cmd.SetI32(id, Protocol.Prop.FillMode, fillMode);
            if (!smooth)
                cmd.SetBool(id, Protocol.Prop.Smooth, false);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        Image(Stretch, stripes, 20, 20, 300, 100, 0, smooth: false);
        Image(Smooth, stripes, 20, 140, 300, 100, 0, smooth: true);
        Image(Fit, stripes, 360, 20, 100, 200, 1, smooth: false);
        Image(Crop, stripes, 500, 20, 100, 200, 2, smooth: false);
        Image(Tile, stripes, 620, 20, 90, 30, 3, smooth: false);
        Image(Missing, missing, 20, 260, 100, 100, 0, smooth: true);
        Image(Unicode, unicode, 140, 260, 100, 100, 0, smooth: false);
        CheckApply("image scene (incl. failed resource)");
        pump(3);

        using (var a = grab(w, Path.Combine(outDir, "05_image.png")))
        {
            // Stretch 30x10 -> 300x100: stripes at 20..120 red, 120..220 green, 220..320 blue.
            check(IsRed(Px(a, 70, 70)) && IsGreen(Px(a, 170, 70)) && IsBlue(Px(a, 270, 70)),
                $"05d: stretch stripes r/g/b ({Px(a, 70, 70)}, {Px(a, 170, 70)}, {Px(a, 270, 70)})");
            check(IsBg(Px(a, 70, 125)) && IsBg(Px(a, 325, 70)), "05d: stretch stays in bounds");
            // smooth false: hard edge at x 120; smooth true: bilinear mix just left of the edge.
            var hard = Px(a, 118, 70);
            var soft = Px(a, 118, 190);
            check(IsRed(hard), $"05d: smooth=false hard edge ({hard})");
            check(soft.R > 60 && soft.G > 40 && soft.R < 245, $"05d: smooth=true blends at edge ({soft})");
            // Fit 3:1 into 100x200 -> 100x33 centered vertically (y 103..137).
            check(IsBg(Px(a, 410, 40)) && IsBg(Px(a, 410, 200)), "05d: fit leaves bands above/below");
            check(IsRed(Px(a, 370, 120)) && IsGreen(Px(a, 410, 120)) && IsBlue(Px(a, 450, 120)),
                $"05d: fit shows all stripes ({Px(a, 370, 120)}, {Px(a, 410, 120)}, {Px(a, 450, 120)})");
            // Crop 3:1 into 100x200 -> scaled to 600x200, center crop = middle (green) stripe only.
            check(IsGreen(Px(a, 505, 30)) && IsGreen(Px(a, 595, 210)) && IsGreen(Px(a, 550, 120)),
                $"05d: crop shows only the middle stripe ({Px(a, 505, 30)}, {Px(a, 595, 210)})");
            check(IsBg(Px(a, 605, 120)), "05d: crop stays in bounds");
            // Tile 90x30 with natural size 30x10: 3x3 repeats.
            check(IsRed(Px(a, 625, 25)) && IsGreen(Px(a, 635, 25)) && IsRed(Px(a, 655, 25)) && IsRed(Px(a, 625, 45))
                && IsBlue(Px(a, 705, 45)), $"05d: tile repeats ({Px(a, 655, 25)}, {Px(a, 625, 45)})");
            check(IsBg(Px(a, 70, 310)), "361: failed resource renders nothing");
            check(IsRed(Px(a, 150, 310)) && IsBlue(Px(a, 230, 310)), $"05d: space/umlaut path loads ({Px(a, 150, 310)})");
        }

        // Updates: image 0, fillMode change, failed -> Ready resource.
        cmd.Reset();
        cmd.SetU64(Smooth, Protocol.Prop.Image, 0);
        cmd.SetI32(Crop, Protocol.Prop.FillMode, 0);
        cmd.SetU64(Missing, Protocol.Prop.Image, stripes);
        CheckApply("image updates");
        pump(2);
        using (var a = grab(w, Path.Combine(outDir, "05_image_update.png")))
        {
            check(IsBg(Px(a, 70, 190)) && IsBg(Px(a, 270, 190)), "361: image 0 clears image");
            check(IsRed(Px(a, 510, 120)) && IsBlue(Px(a, 590, 120)), $"05d: crop -> stretch ({Px(a, 510, 120)})");
            check(IsRed(Px(a, 25, 310)) && IsBlue(Px(a, 115, 310)), "361: failed -> Ready resource draws");
        }

        // Errors: invalid fillMode / smooth as i32 -> -7, Rectangle property / Source (#361) -> -6, unknown resource id /
        // image as i32 -> -7, no crash.
        void Expect(int expected, string what)
        {
            var rc = Apply();
            check(rc == expected, $"05d: {what} -> {expected} ({rc}: {Native.LastError()})");
        }
        cmd.Reset();
        cmd.SetI32(Fit, Protocol.Prop.FillMode, 4);
        Expect(-7, "fillMode 4");
        cmd.Reset();
        cmd.SetI32(Fit, Protocol.Prop.Smooth, 1);
        Expect(-7, "smooth as i32");
        cmd.Reset();
        cmd.SetF32(Fit, Protocol.Prop.Radius, 1);
        Expect(-6, "radius on image");
        cmd.Reset();
        cmd.SetString(Fit, Protocol.Prop.Source, asset);
        Expect(-6, "361: source on image");
        cmd.Reset();
        cmd.SetU64(Fit, Protocol.Prop.Image, 0xDEAD_0000_BEEFUL);
        Expect(-7, "361: unknown resource id");
        cmd.Reset();
        cmd.SetI32(Fit, Protocol.Prop.Image, 1);
        Expect(-7, "361: image as i32");

        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(stripes);
        Native.vlqt_res_release(unicode);
        Native.vlqt_res_release(missing);
        pump(1);
    }
}
