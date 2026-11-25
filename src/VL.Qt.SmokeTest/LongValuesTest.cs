using System;
using System.Drawing;
using System.Linq;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;
using VL.Qt.Wire;

// Nothing is cut at 64 KB: the length of a command and of an event record is a u32 (protocol.h). A Text of more than
// 65,527 UTF-8 bytes is applied whole (its implicit height follows the number of lines), a Shape with 20,000 points and a
// Path with 20,000 verbs (more than 8,190 points) are drawn to their last point. The 70,000-byte QML string is in QmlTest.
// Grab goes to <OutDir>/long_values.png.
[TestFixture, NonParallelizable]
public static class LongValuesTest
{
    private const uint LongText = 1, LongTextHalf = 2, Polyline = 3, Path = 4;
    private const uint HText = 11, HHalf = 12;
    private const int pointCount = 20_000;

    private static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    private static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 12 && Math.Abs(c.G - g) < 12 && Math.Abs(c.B - b) < 12;

    /// <summary>Long strings and long point lists go through the wire unchanged.</summary>
    [Test, Category("long-values")]
    public static void LongValues() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt long values test");
        s.Check(w != SceneHandle.Null, "long values: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 20];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"long values: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Clear();
        }

        // One character per line: the implicit height of a Text grows with the number of lines. 40,000 lines are 79,999 bytes of
        // UTF-8 (more than the 65,527 a command could carry with a u16 length), 20,000 lines are 39,999 bytes.
        static string Lines(int n) => string.Join("\n", Enumerable.Repeat("a", n));
        cmd.Clear();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        foreach (var (id, handle, lines) in new[] { (LongText, HText, 40_000), (LongTextHalf, HHalf, 20_000) })
        {
            cmd.Create(id, Protocol.ElementType.Text);
            cmd.SetPlace(id, 700, 0, 100, 60);
            cmd.SetString(id, Protocol.PropertyId.Text, Lines(lines));
            cmd.SetF32(id, Protocol.PropertyId.FontSize, 10);
            cmd.Reparent(id, 0, id);
            cmd.Bind(id, handle);
        }

        // Polyline: 20,000 points on a horizontal line from x 0 to 400 (0.02 apart), the old cut was at the 8,190th point (x 164).
        var points = new float[2 * pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            points[2 * i] = i * 0.02f;
            points[2 * i + 1] = 0;
        }
        cmd.Create(Polyline, Protocol.ElementType.Shape);
        cmd.SetPlace(Polyline, 100, 100, 0, 0);
        cmd.Reparent(Polyline, 0, 100);
        cmd.SetF32Array(Polyline, Protocol.PropertyId.ShapePoints, points);
        cmd.SetColor(Polyline, Protocol.PropertyId.ShapeStrokeColor, 0, 1, 0, 1);
        cmd.SetF32(Polyline, Protocol.PropertyId.ShapeStrokeWidth, 10);

        // Path: Move and 19,999 Line verbs over the same points.
        var verbs = new byte[pointCount];
        Array.Fill(verbs, Protocol.Verb.Line);
        verbs[0] = Protocol.Verb.Move;
        cmd.Create(Path, Protocol.ElementType.Shape);
        cmd.SetPlace(Path, 100, 200, 0, 0);
        cmd.Reparent(Path, 0, 101);
        cmd.SetPath(Path, Protocol.PropertyId.ShapePath, verbs, points);
        cmd.SetColor(Path, Protocol.PropertyId.ShapeStrokeColor, 0, 1, 1, 1);
        cmd.SetF32(Path, Protocol.PropertyId.ShapeStrokeWidth, 10);

        CheckApply("Text with 79,999 bytes, Polyline with 20,000 points, Path with 20,000 verbs");
        s.Pump(3);

        var recs = MeasureTest.Parse(buf.AsSpan(0, Math.Max(NativeCalls.Poll(w, buf), 0)), s);
        float? HeightOf(uint handle) => recs.FindLast(r => r.Event == Protocol.EventId.Measured && r.Handle == handle) is { V.Length: 2 } m ? m.V[1] : null;
        var tall = HeightOf(HText);
        var half = HeightOf(HHalf);
        s.Check(tall is > 0 && half is > 0 && Math.Abs(tall.Value / half.Value - 2) < 0.02,
            $"long values: the Text of 40,000 lines is twice as high as the one of 20,000 lines ({half} -> {tall})");
        s.Check(!recs.Any(r => r.Event == Protocol.EventId.Error), "long values: no error text");

        using var a = s.Grab(w, "long_values.png");
        s.Check(Near(Px(a, 150, 100), 0, 255, 0) && Near(Px(a, 400, 100), 0, 255, 0) && Near(Px(a, 490, 100), 0, 255, 0),
            $"long values: Polyline drawn beyond point 8,190 ({Px(a, 150, 100)}, {Px(a, 400, 100)}, {Px(a, 490, 100)})");
        s.Check(Near(Px(a, 150, 200), 0, 255, 255) && Near(Px(a, 400, 200), 0, 255, 255) && Near(Px(a, 490, 200), 0, 255, 255),
            $"long values: Path drawn beyond point 8,190 ({Px(a, 150, 200)}, {Px(a, 400, 200)}, {Px(a, 490, 200)})");
    }
}
