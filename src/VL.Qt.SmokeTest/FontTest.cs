using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VL.Qt.Wire;
using VL.Qt;

// App fonts (LoadFont node): vlqt_font_add / vlqt_font_release in QtNative. Sharing and reference counting per file,
// removal at the end of the pump (and reuse within the same pump), errors (missing file, no font, wrong thread), a Text
// that renders with the loaded family, and the one-time warning for a family Qt does not have (log category vlqt.font).
// Test font: src/VL.Qt.Native/fonts/DejaVuSans.ttf (the Linux fallback font of the bundle, Bitstream Vera license,
// LICENSES/DejaVu-Fonts.txt). Grabs go to <OutDir>/15_font*.png.
[TestFixture, NonParallelizable]
public static class FontTest
{
    private const uint Line = 1;
    private const string Sample = "Hamburgefonstiv 0123";
    private const string Missing = "VlQt Not Installed Family";

    private static string? FindFile(params string[] rel)
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
    private static int Diff(Bitmap a, Bitmap b, int x, int y, int w, int h)
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

    /// <summary>Fonts.</summary>
    [Test, Category("font")]
    public static void Font() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var fontPath = FindFile("src", "VL.Qt.Native", "fonts", "DejaVuSans.ttf");
        s.Check(fontPath != null, "test font src/VL.Qt.Native/fonts/DejaVuSans.ttf found");
        if (fontPath == null)
            return;
        var notAFont = FindFile("src", "VL.Qt.SmokeTest", "assets", "stripes_rgb_30x10.png");

        // Sharing: the same file is one handle (also spelled differently), every add takes a reference.
        var rc = Native.FontAdd(fontPath, out var h1, out var families);
        s.Check(rc == 0 && h1 != 0, $"font_add ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        var family = families.Split('\n')[0];
        s.Check(family.Length > 0 && family.Contains("DejaVu"), $"family names of the file ('{families.Replace('\n', '|')}')");
        s.Check(NativeTest.vlqt_test_font_refs(h1) == 1, "one reference after the first add");

        var alt = Path.Combine(Path.GetDirectoryName(fontPath)!, "..", "fonts", Path.GetFileName(fontPath));
        rc = Native.FontAdd(alt, out var h2, out var families2);
        s.Check(rc == 0 && h2 == h1 && families2 == families, $"same file spelled differently shares the handle ({h1} / {h2})");
        s.Check(NativeTest.vlqt_test_font_refs(h1) == 2, "two references after the second add");

        // Release: the entry stays while a reference is left; without one it is removed only at the end of the pump.
        s.Check(Native.vlqt_font_release(h1) == 0, "release one reference");
        s.Pump(1);
        s.Check(NativeTest.vlqt_test_font_refs(h1) == 1, "still registered with one reference after a pump");
        s.Check(Native.vlqt_font_release(h1) == 0, "release the last reference");
        s.Check(NativeTest.vlqt_test_font_refs(h1) == 0, "no reference, removal pending until the end of the pump");
        s.Check(Native.vlqt_font_release(h1) == -9, "release without a reference left -> -9");

        // Reuse inside the same pump: added again before the pump ends, the entry (and Qt's registration) survives.
        rc = Native.FontAdd(fontPath, out var h3, out _);
        s.Check(rc == 0 && h3 == h1, $"add in the same pump reuses the pending entry ({h1} / {h3})");
        s.Check(NativeTest.vlqt_test_font_refs(h3) == 1, "one reference after the reuse");
        s.Pump(2);
        s.Check(NativeTest.vlqt_test_font_refs(h3) == 1, "reused entry survives the pump");

        // Text with the loaded family: grabs differ from a family that is not installed (see the SKIP below).
        var w = Native.vlqt_window_create(800, 600, "VL.Qt font test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void Apply(string what)
        {
            var r = Native.Apply(w, cmd.EndFrame());
            s.Check(r == 0, $"{what} ({r}) {(r != 0 ? Native.LastError() : "")}");
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
        s.Pump(3);
        using var loaded = s.Grab(w, "15_font_loaded.png");

        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, Missing);
        Apply("family that is not installed");
        s.Pump(3);
        using var missing = s.Grab(w, "15_font_missing.png");

        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, "");
        Apply("empty family (default font)");
        s.Pump(3);
        using var plain = s.Grab(w, "15_font_default.png");

        var vsMissing = Diff(loaded, missing, 20, 40, 760, 100);
        var vsDefault = Diff(loaded, plain, 20, 40, 760, 100);
        if (vsDefault == 0)
            // The system default font is the loaded one (DejaVu Sans on a Linux with fonts-dejavu): nothing to compare.
            s.Skip("loaded font differs from the substitute: the default font of this system is the test font itself");
        else
            s.Check(vsMissing > 200, $"text with the loaded family differs from a family that is not installed ({vsMissing} px differ)");
        // No check that the substitute equals the default font: Qt picks its own substitute (Tahoma on Windows).

        // Warning for the family Qt does not have: once per name (re-set and switched back and forth), not for "" or the loaded family.
        for (var i = 0; i < 3; i++)
        {
            cmd.Reset();
            cmd.SetString(Line, Protocol.Prop.FontFamily, i % 2 == 0 ? Missing : family);
            Apply("family switched " + i);
            s.Pump(2);
        }
        cmd.Reset();
        cmd.SetString(Line, Protocol.Prop.FontFamily, Missing);
        Apply("family missing again");
        s.Pump(2);
        var text = s.Log;
        var warnings = text.Split('\n').Count(l => l.Contains("vlqt.font: Font family '" + Missing + "' not found"));
        s.Check(warnings == 1, $"one warning for the missing family, not per change ({warnings})");
        s.Check(!text.Contains("vlqt.font: Font family '' not found"), "no warning for the empty family");
        s.Check(!text.Contains("vlqt.font: Font family '" + family + "' not found"), "no warning for the loaded family");

        // Errors: file not found, a file that is no font, wrong thread, null path; the failed adds leave no entry behind.
        rc = Native.FontAdd(Path.Combine(s.OutDir, "no-such-font.ttf"), out var hMissing, out _);
        s.Check(rc == -1 && hMissing == 0 && Native.LastError().Contains("not found"), $"missing file -> -1 with text ({rc}: {Native.LastError()})");
        if (notAFont != null)
        {
            rc = Native.FontAdd(notAFont, out var hPng, out _);
            s.Check(rc == -1 && hPng == 0 && Native.LastError().Contains("not a font"), $"file that is no font -> -1 with text ({rc}: {Native.LastError()})");
        }
        var threadRc = 0;
        ulong threadHandle = 1;
        Task.Run(() => threadRc = Native.FontAdd(fontPath, out threadHandle, out _)).Wait();
        s.Check(threadRc == -1 && threadHandle == 0 && Native.LastError().Contains("thread"), $"add from another thread -> -1 with text ({threadRc}: {Native.LastError()})");
        var releaseRc = 0;
        Task.Run(() => releaseRc = Native.vlqt_font_release(h3)).Wait();
        s.Check(releaseRc == -1 && NativeTest.vlqt_test_font_refs(h3) == 1, $"release from another thread -> -1, reference kept ({releaseRc})");
        s.Check(Native.vlqt_font_release(0) == -9 && Native.vlqt_font_release(987654) == -9, "release of an unknown handle -> -9");

        // Final release: gone after the pump; a later add registers it anew under a new handle.
        s.Check(Native.vlqt_font_release(h3) == 0, "final release");
        s.Pump(2);
        s.Check(NativeTest.vlqt_test_font_refs(h3) == -9, "removed at the end of the pump");
        rc = Native.FontAdd(fontPath, out var h4, out _);
        s.Check(rc == 0 && h4 != 0 && h4 != h3 && NativeTest.vlqt_test_font_refs(h4) == 1, $"add after the removal registers anew ({h3} -> {h4})");
        s.Check(Native.vlqt_font_release(h4) == 0, "release again");
        s.Pump(2);
        s.Check(NativeTest.vlqt_test_font_refs(h4) == -9, "removed again");

        cmd.Reset();
        cmd.Destroy(Line);
        Apply("destroy text");
        Native.vlqt_window_destroy(w);
        s.Pump(2);
    }
}
