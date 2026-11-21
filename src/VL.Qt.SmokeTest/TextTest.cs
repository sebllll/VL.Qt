using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using VL.Qt.Wire;
using VL.Qt;

// Text in QtNative (plan 05c): text, font.*, color, alignment, wrap, elide, lineHeight, letterSpacing, fontSizeMode.
// Grabs go to <OutDir>/05_text*.png. Values are DIP (the C# side converts units). Checks count non-background
// pixels in regions (text rendering differs per platform/font, so no exact pixel values).
[TestFixture, NonParallelizable]
public static class TextTest
{
    private static bool IsBg(System.Drawing.Color c) => Math.Abs(c.R - 0x20) < 24 && Math.Abs(c.G - 0x20) < 24 && Math.Abs(c.B - 0x20) < 24;

    /// <summary>Non-background pixels in a region given in window coordinates (800x600), plus their x/y extent.</summary>
    private record struct Ink(int Count, int MinX, int MaxX, int MinY, int MaxY, int Reddish);

    private static Ink Scan(Bitmap a, int x, int y, int w, int h)
    {
        int count = 0, minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, red = 0;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                var c = a.GetPixel(xx * a.Width / 800, yy * a.Height / 600);
                if (IsBg(c))
                    continue;
                count++;
                minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx);
                minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy);
                if (c.R > 150 && c.G < 90 && c.B < 90)
                    red++;
            }
        return new Ink(count, minX, maxX, minY, maxY, red);
    }

    private const uint Plain = 1, Right = 2, Bottom = 3, Wrapped = 4, Elided = 5, Fit = 6;
    private const string Long = "one two three four five six seven eight";

    /// <summary>Text element.</summary>
    [Test, Category("text")]
    public static void Text() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt text test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        Native.Result Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Text(uint id, string text, float x, float y, float width, float height, float size)
        {
            cmd.Create(id, Protocol.ElementType.Text);
            cmd.SetPlace(id, x, y, width, height);
            cmd.SetString(id, Protocol.PropertyId.Text, text);
            cmd.SetF32(id, Protocol.PropertyId.FontSize, size);
            cmd.SetColor(id, Protocol.PropertyId.Color, 1, 1, 1, 1);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        Text(Plain, "Hello", 20, 20, 360, 60, 40);
        Text(Right, "Hi", 420, 20, 360, 60, 40);
        cmd.SetI32(Right, Protocol.PropertyId.HorizontalAlignment, 0x2);
        cmd.SetColor(Right, Protocol.PropertyId.Color, 1, 0, 0, 1);
        Text(Bottom, "Hi", 20, 100, 360, 160, 40);
        cmd.SetI32(Bottom, Protocol.PropertyId.VerticalAlignment, 0x40);
        Text(Wrapped, Long, 420, 100, 200, 240, 30);
        cmd.SetI32(Wrapped, Protocol.PropertyId.WrapMode, 1);
        Text(Elided, Long, 20, 300, 200, 40, 30);
        cmd.SetI32(Elided, Protocol.PropertyId.Elide, 1);
        Text(Fit, "FIT", 20, 380, 150, 50, 200);
        cmd.SetI32(Fit, Protocol.PropertyId.FontSizeMode, 3);
        CheckApply("text scene");
        s.Pump(3);

        Ink plain, right, wrapped;
        using (var a = s.Grab(w, "05_text.png"))
        {
            plain = Scan(a, 20, 20, 360, 60);
            var below = Scan(a, 20, 82, 360, 14);
            s.Check(plain.Count > 200 && below.Count == 0, $"text present inside bounds ({plain.Count} px inside, {below.Count} below)");

            right = Scan(a, 420, 20, 360, 60);
            var rightLeftHalf = Scan(a, 420, 20, 180, 60);
            s.Check(right.Count > 50 && rightLeftHalf.Count == 0 && right.MaxX > 740 && right.Reddish > 10,
                $"right aligned + red ({right.Count} px, left half {rightLeftHalf.Count}, maxX {right.MaxX}, red {right.Reddish})");

            var top = Scan(a, 20, 100, 360, 80);
            var bottom = Scan(a, 20, 180, 360, 80);
            s.Check(top.Count == 0 && bottom.Count > 50, $"vertical alignment bottom (top {top.Count}, bottom {bottom.Count})");

            wrapped = Scan(a, 420, 100, 200, 240);
            var wrapOutside = Scan(a, 625, 100, 170, 240);
            s.Check(wrapped.Count > 200 && wrapOutside.Count == 0 && wrapped.MaxY - wrapped.MinY > 70,
                $"word wrap stays in width, several lines ({wrapped.Count} px, outside {wrapOutside.Count}, height {wrapped.MaxY - wrapped.MinY})");

            var elided = Scan(a, 20, 300, 200, 40);
            var elideOutside = Scan(a, 225, 300, 175, 40);
            s.Check(elided.Count > 50 && elideOutside.Count == 0, $"elide right stays in width ({elided.Count} px, outside {elideOutside.Count})");

            var fit = Scan(a, 20, 380, 150, 50);
            var fitRight = Scan(a, 175, 380, 225, 50);
            var fitBelow = Scan(a, 20, 434, 380, 120);
            s.Check(fit.Count > 30 && fitRight.Count == 0 && fitBelow.Count == 0,
                $"fontSizeMode Fit shrinks size 200 into bounds ({fit.Count} px, right {fitRight.Count}, below {fitBelow.Count})");
        }

        // Updates: font read-modify-write keeps the other font fields.
        cmd.Reset();
        cmd.SetI32(Plain, Protocol.PropertyId.FontWeight, 900);
        cmd.SetBool(Plain, Protocol.PropertyId.FontItalic, true);
        cmd.SetF32(Right, Protocol.PropertyId.LetterSpacing, 20);
        cmd.SetF32(Wrapped, Protocol.PropertyId.LineHeight, 1.6f);
        cmd.SetString(Elided, Protocol.PropertyId.FontFamily, "Courier New");
        CheckApply("text update");
        s.Pump(3);
        using (var a = s.Grab(w, "05_text_update.png"))
        {
            var bold = Scan(a, 20, 20, 360, 60);
            s.Check(bold.Count > plain.Count * 1.15 && bold.MaxY - bold.MinY >= plain.MaxY - plain.MinY - 2,
                $"weight 900 + italic, size kept ({plain.Count} -> {bold.Count} px, height {plain.MaxY - plain.MinY} -> {bold.MaxY - bold.MinY})");
            var spaced = Scan(a, 420, 20, 360, 60);
            s.Check(spaced.MinX < right.MinX - 10 && spaced.MaxX > 740 && spaced.Reddish > 10,
                $"letter spacing widens right-aligned text, color kept (minX {right.MinX} -> {spaced.MinX})");
            var tall = Scan(a, 420, 100, 200, 380);
            s.Check(tall.MaxY - tall.MinY > wrapped.MaxY - wrapped.MinY + 20,
                $"lineHeight 1.6 ({wrapped.MaxY - wrapped.MinY} -> {tall.MaxY - tall.MinY})");
        }

        // Empty family = default font again; text change.
        cmd.Reset();
        cmd.SetString(Elided, Protocol.PropertyId.FontFamily, "");
        cmd.SetString(Plain, Protocol.PropertyId.Text, "Hello VL.Qt");
        CheckApply("text reset family + new text");
        s.Pump(2);
        using (var a = s.Grab(w, "05_text_update2.png"))
        {
            var longer = Scan(a, 20, 20, 380, 60);
            s.Check(longer.MaxX > plain.MaxX + 40, $"new text is wider (maxX {plain.MaxX} -> {longer.MaxX})");
        }

        // Errors: invalid enum / size -> BadArgument, Rectangle-only property -> UnknownProperty, no crash.
        cmd.Reset();
        cmd.SetI32(Right, Protocol.PropertyId.HorizontalAlignment, 3);
        var rc = Apply();
        s.Check(rc == Native.Result.BadArgument, $"invalid alignment -> BadArgument ({rc}: {Native.LastError()})");
        cmd.Reset();
        cmd.SetF32(Plain, Protocol.PropertyId.FontSize, 0);
        rc = Apply();
        s.Check(rc == Native.Result.BadArgument, $"font size 0 -> BadArgument ({rc}: {Native.LastError()})");
        cmd.Reset();
        cmd.SetF32(Plain, Protocol.PropertyId.Radius, 1);
        rc = Apply();
        s.Check(rc == Native.Result.UnknownProperty, $"radius on text -> UnknownProperty ({rc}: {Native.LastError()})");
        cmd.Reset();
        cmd.SetF32(Plain, Protocol.PropertyId.FontWeight, 700);
        rc = Apply();
        s.Check(rc == Native.Result.BadArgument, $"weight as f32 -> BadArgument ({rc}: {Native.LastError()})");

        // Real glyphs: without any font all characters are the same empty box (tofu), "iiiiiiii" would be as
        // wide as "WWWWWWWW". Own scene: the checks above scan regions this one would disturb.
        cmd.Reset();
        foreach (var id in new[] { Plain, Right, Bottom, Wrapped, Elided, Fit })
            cmd.Destroy(id);
        Text(7, "iiiiiiii", 20, 20, 360, 60, 40);
        Text(8, "WWWWWWWW", 20, 100, 400, 60, 40);
        CheckApply("glyph scene");
        s.Pump(3);
        using (var a = s.Grab(w, "05_text_glyphs.png"))
        {
            var narrow = Scan(a, 20, 20, 400, 60);
            var wide = Scan(a, 20, 100, 400, 60);
            s.Check(narrow.Count > 20 && (wide.MaxX - wide.MinX) > 2 * (narrow.MaxX - narrow.MinX),
                $"text renders real glyphs, no tofu (\"iiiiiiii\" {narrow.MaxX - narrow.MinX} px wide, \"WWWWWWWW\" {wide.MaxX - wide.MinX} px)");
        }

        Native.vlqt_scene_destroy(w);
    }
}
