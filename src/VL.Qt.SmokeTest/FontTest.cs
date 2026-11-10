using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VL.Qt;

// App fonts (#15, LoadFont node): vlqt_font_add / vlqt_font_release in QtNative. Sharing and reference counting per file,
// removal at the end of the pump (and reuse within the same pump), errors (missing file, no font, wrong thread), a Text
// that renders with the loaded family, and the one-time warning for a family Qt does not have (log category vlqt.font).
// Test font: src/VL.Qt.Native/fonts/DejaVuSans.ttf (the Linux fallback font of the bundle, Bitstream Vera license,
// LICENSES/DejaVu-Fonts.txt). Grabs go to <outDir>/15_font*.png.
static class FontTest
{
    const uint Line = 1;
    const string Sample = "Hamburgefonstiv 0123";
    const string Missing = "VlQt Not Installed Family";

    static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    /// <summary>Number of differing pixels in a region given in window coordinates (800x600), both grabs of the same size.</summary>
    static int Diff(Bitmap a, Bitmap b, int x, int y, int w, int h)
    {
        var count = 0;
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
            {
                var ca = a.GetPixel(xx * a.Width / 800, yy * a.Height / 600);
                var cb = b.GetPixel(xx * b.Width / 800, yy * b.Height / 600);
                if (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B) > 24)
                    count++;
            }
        return count;
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab, Func<string> log)
    {
        var fontPath = FindFile("src", "VL.Qt.Native", "fonts", "DejaVuSans.ttf");
        check(fontPath != null, "15: test font src/VL.Qt.Native/fonts/DejaVuSans.ttf found");
        if (fontPath == null)
            return;
        var notAFont = FindFile("src", "VL.Qt.SmokeTest", "assets", "stripes_rgb_30x10.png");

        // Sharing: the same file is one handle (also spelled differently), every add takes a reference.
        var rc = Native.FontAdd(fontPath, out var h1, out var families);
        check(rc == 0 && h1 != 0, $"15: font_add ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        var family = families.Split('\n')[0];
        check(family.Length > 0 && family.Contains("DejaVu"), $"15: family names of the file ('{families.Replace('\n', '|')}')");
        check(Native.vlqt_test_font_refs(h1) == 1, "15: one reference after the first add");

        var alt = Path.Combine(Path.GetDirectoryName(fontPath)!, "..", "fonts", Path.GetFileName(fontPath));
        rc = Native.FontAdd(alt, out var h2, out var families2);
        check(rc == 0 && h2 == h1 && families2 == families, $"15: same file spelled differently shares the handle ({h1} / {h2})");
        check(Native.vlqt_test_font_refs(h1) == 2, "15: two references after the second add");

        // Release: the entry stays while a reference is left; without one it is removed only at the end of the pump.
        check(Native.vlqt_font_release(h1) == 0, "15: release one reference");
        pump(1);
        check(Native.vlqt_test_font_refs(h1) == 1, "15: still registered with one reference after a pump");
        check(Native.vlqt_font_release(h1) == 0, "15: release the last reference");
        check(Native.vlqt_test_font_refs(h1) == 0, "15: no reference, removal pending until the end of the pump");
        check(Native.vlqt_font_release(h1) == -9, "15: release without a reference left -> -9");

        // Reuse inside the same pump: added again before the pump ends, the entry (and Qt's registration) survives.
        rc = Native.FontAdd(fontPath, out var h3, out _);
        check(rc == 0 && h3 == h1, $"15: add in the same pump reuses the pending entry ({h1} / {h3})");
        check(Native.vlqt_test_font_refs(h3) == 1, "15: one reference after the reuse");
        pump(2);
        check(Native.vlqt_test_font_refs(h3) == 1, "15: reused entry survives the pump");

        // Text with the loaded family: grabs differ from a family that is not installed (see the SKIP below).
        var w = Native.vlqt_window_create(800, 600, "VL.Qt font test");
        check(w != IntPtr.Zero, "15: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void Apply(string what)
        {
            var r = Native.Apply(w, cmd.EndFrame());
            check(r == 0, $"15: {what} ({r}) {(r != 0 ? Native.LastError() : "")}");
        }
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Line, Protocol.Type.Text);
        cmd.SetPlace(Line, 20, 40, 760, 100);
        cmd.SetString(Line, Protocol.Prop.Text, Sample);
        cmd.SetF32(Line, Protocol.Prop.FontSize, 56);
        cmd.SetColor(Line, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.SetString(Line, Protocol.Prop.FontFamily, family);
        cmd.Reparent(Line, 0, 0);
        Apply("text with the loaded family");
        pump(3);
        using var loaded = grab(w, Path.Combine(outDir, "15_font_loaded.png"));

        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, Missing);
        Apply("family that is not installed");
        pump(3);
        using var missing = grab(w, Path.Combine(outDir, "15_font_missing.png"));

        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, "");
        Apply("empty family (default font)");
        pump(3);
        using var plain = grab(w, Path.Combine(outDir, "15_font_default.png"));

        var vsMissing = Diff(loaded, missing, 20, 40, 760, 100);
        var vsDefault = Diff(loaded, plain, 20, 40, 760, 100);
        if (vsDefault == 0)
            // The system default font is the loaded one (DejaVu Sans on a Linux with fonts-dejavu): nothing to compare.
            Console.WriteLine("SKIP 15: loaded font differs from the substitute: the default font of this system is the test font itself");
        else
            check(vsMissing > 200, $"15: text with the loaded family differs from a family that is not installed ({vsMissing} px differ)");
        // No check that the substitute equals the default font: Qt picks its own substitute (Tahoma on Windows).

        // Warning for the family Qt does not have: once per name (re-set and switched back and forth), not for "" or the loaded family.
        for (var i = 0; i < 3; i++)
        {
            cmd.Reset();
            cmd.SetString(Line, Protocol.Prop.FontFamily, i % 2 == 0 ? Missing : family);
            Apply("family switched " + i);
            pump(2);
        }
        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, Missing);
        Apply("family missing again");
        pump(2);
        var text = log();
        var warnings = text.Split('\n').Count(l => l.Contains("vlqt.font: Font family '" + Missing + "' not found"));
        check(warnings == 1, $"15: one warning for the missing family, not per change ({warnings})");
        check(!text.Contains("vlqt.font: Font family '' not found"), "15: no warning for the empty family");
        check(!text.Contains("vlqt.font: Font family '" + family + "' not found"), "15: no warning for the loaded family");

        // Errors: file not found, a file that is no font, wrong thread, null path; the failed adds leave no entry behind.
        rc = Native.FontAdd(Path.Combine(outDir, "no-such-font.ttf"), out var hMissing, out _);
        check(rc == -1 && hMissing == 0 && Native.LastError().Contains("not found"), $"15: missing file -> -1 with text ({rc}: {Native.LastError()})");
        if (notAFont != null)
        {
            rc = Native.FontAdd(notAFont, out var hPng, out _);
            check(rc == -1 && hPng == 0 && Native.LastError().Contains("not a font"), $"15: file that is no font -> -1 with text ({rc}: {Native.LastError()})");
        }
        var threadRc = 0;
        ulong threadHandle = 1;
        Task.Run(() => threadRc = Native.FontAdd(fontPath, out threadHandle, out _)).Wait();
        check(threadRc == -1 && threadHandle == 0 && Native.LastError().Contains("thread"), $"15: add from another thread -> -1 with text ({threadRc}: {Native.LastError()})");
        var releaseRc = 0;
        Task.Run(() => releaseRc = Native.vlqt_font_release(h3)).Wait();
        check(releaseRc == -1 && Native.vlqt_test_font_refs(h3) == 1, $"15: release from another thread -> -1, reference kept ({releaseRc})");
        check(Native.vlqt_font_release(0) == -9 && Native.vlqt_font_release(987654) == -9, "15: release of an unknown handle -> -9");

        // Final release: gone after the pump; a later add registers it anew under a new handle.
        check(Native.vlqt_font_release(h3) == 0, "15: final release");
        pump(2);
        check(Native.vlqt_test_font_refs(h3) == -9, "15: removed at the end of the pump");
        rc = Native.FontAdd(fontPath, out var h4, out _);
        check(rc == 0 && h4 != 0 && h4 != h3 && Native.vlqt_test_font_refs(h4) == 1, $"15: add after the removal registers anew ({h3} -> {h4})");
        check(Native.vlqt_font_release(h4) == 0, "15: release again");
        pump(2);
        check(Native.vlqt_test_font_refs(h4) == -9, "15: removed again");

        cmd.Reset();
        cmd.Destroy(Line);
        Apply("destroy text");
        Native.vlqt_window_destroy(w);
        pump(2);
    }
}
