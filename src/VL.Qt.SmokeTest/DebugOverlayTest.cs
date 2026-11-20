using System;
using NUnit.Framework;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Qt;
using Color = System.Drawing.Color;
using static Pixels;

// Debug overlay (#279): VLQT_PROP_DEBUG per element and vlqt_window_set_debug for the whole window. The overlay is drawn
// by QtNative on top of the scene (frame cyan, origin magenta, anchor yellow, overflow red, implicit orange, ...). Checks: layout, Measured Size,
// Scene Bounds (Auto Size included) and hit-testing are identical with and without debug; the overlay appears in the
// grab and disappears again. Grabs: <OutDir>/30_debug_off.png, 30_debug_overlay.png, 30_debug_element.png.
[TestFixture, NonParallelizable]
public static class DebugOverlayTest
{
    private const uint Col = 1, Txt = 5, Grp = 6, RowId = 8, Rot = 9, Ovf = 2;
    private static uint Child(uint parent, uint i) => 10 + parent * 10 + i;
    private const uint HHover = 100; // element sink of the green column child
    private const uint HoverId = 900, HHoverHandler = 101; // its Hover handler (#319) and that handler's sink

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload);

    private static List<Rec> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length) break;
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)), b.Slice(off + 12, len - 8).ToArray()));
            off += (4 + len + 3) & ~3;
        }
        return list;
    }

    private static Color Px(Bitmap a, int x, int y) => a.GetPixel(Math.Clamp(x * a.Width / 800, 0, a.Width - 1), Math.Clamp(y * a.Height / 600, 0, a.Height - 1));
    private static bool IsCyan(Color c) => c.R < 90 && c.G > 150 && c.B > 150;
    // Any pixel within +-2 logical px (line width 1, HiDPI rounding). IsCyan needs a pixel fully covered by the frame
    // (alpha 235 over background or padding still has G, B > 150): QtNative snaps lines to whole device pixels and
    // draws them above all fills. A 1 px line split over two pixels by MSAA (#283, DPR 1) fails on purpose.
    private static bool Near(Bitmap a, int x, int y, Func<Color, bool> f)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (f(Px(a, x + dx, y + dy))) return true;
        return false;
    }

    /// <summary>Debug overlay.</summary>
    [Test, Category("debug")]
    public static void Debug() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt debug overlay test");
        s.Check(w != IntPtr.Zero, "debug: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"debug: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Frame(int n = 1)
        {
            s.Pump(n);
            int len = Native.Poll(w, buf);
            s.Check(len >= 0, $"debug: poll ({len})");
            var recs = Parse(buf.AsSpan(0, Math.Max(len, 0)));
            recs.RemoveAll(r => r.Event == Protocol.Ev.WindowGeometry);
            return recs;
        }
        void Rect(uint id, uint parent, float x, float y, float width, float height, float r, float g, float b)
        {
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, x, y, width, height);
            cmd.SetColor(id, Protocol.Prop.Color, r, g, b, 1);
            cmd.Reparent(id, parent, 1000);
        }
        var all = new List<uint>();
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // Column, Auto Size, padding 10, spacing 20.
        cmd.Create(Col, Protocol.Type.Column);
        cmd.SetPlace(Col, 20, 20, 0, 0);
        cmd.SetF32(Col, Protocol.Prop.Padding, 10);
        cmd.SetF32(Col, Protocol.Prop.Spacing, 20);
        cmd.Reparent(Col, 0, 1000);
        Rect(Child(Col, 0), Col, 0, 0, 100, 40, 1, 0, 0);
        Rect(Child(Col, 1), Col, 0, 0, 60, 40, 0, 1, 0);
        Rect(Child(Col, 2), Col, 0, 0, 140, 40, 0, 0, 1);
        cmd.Bind(Child(Col, 1), HHover, 0);
        cmd.Create(HoverId, Protocol.Type.HoverHandler);
        cmd.Reparent(HoverId, Child(Col, 1), 0);
        cmd.Bind(HoverId, HHoverHandler, 0);
        // Text
        cmd.Create(Txt, Protocol.Type.Text);
        cmd.SetPlace(Txt, 300, 30, 300, 70);
        cmd.SetString(Txt, Protocol.Prop.Text, "Debug Hgy");
        cmd.SetF32(Txt, Protocol.Prop.FontSize, 40);
        cmd.SetColor(Txt, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(Txt, 0, 1000);
        // Group, Auto Size: extent of two children.
        cmd.Create(Grp, Protocol.Type.Item);
        cmd.SetPlace(Grp, 300, 160, 0, 0);
        cmd.Reparent(Grp, 0, 1000);
        Rect(Child(Grp, 0), Grp, 0, 0, 80, 40, 0.8f, 0.4f, 0);
        Rect(Child(Grp, 1), Grp, 60, 60, 50, 50, 0.4f, 0.4f, 0.8f);
        // Row 300x120, Auto Size off, padding 10, spacing 15.
        cmd.Create(RowId, Protocol.Type.Row);
        cmd.SetBool(RowId, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(RowId, 300, 330, 300, 120);
        cmd.SetF32(RowId, Protocol.Prop.Padding, 10);
        cmd.SetF32(RowId, Protocol.Prop.Spacing, 15);
        cmd.Reparent(RowId, 0, 1000);
        Rect(Child(RowId, 0), RowId, 0, 0, 50, 60, 0.6f, 0.6f, 0.6f);
        Rect(Child(RowId, 1), RowId, 0, 0, 50, 100, 0.6f, 0.6f, 0.6f);
        Rect(Child(RowId, 2), RowId, 0, 0, 50, 30, 0.6f, 0.6f, 0.6f);
        // Column 120x60, Auto Size off, padding 10, spacing 10, content 110 high (#473): implicit size 120x110 orange,
        // children overflow below the frame (y 390..430) red, frame solid cyan at y 390.
        cmd.Create(Ovf, Protocol.Type.Column);
        cmd.SetBool(Ovf, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(Ovf, 20, 330, 120, 60);
        cmd.SetF32(Ovf, Protocol.Prop.Padding, 10);
        cmd.SetF32(Ovf, Protocol.Prop.Spacing, 10);
        cmd.Reparent(Ovf, 0, 1000);
        Rect(Child(Ovf, 0), Ovf, 0, 0, 100, 40, 0.3f, 0.3f, 0.3f);
        Rect(Child(Ovf, 1), Ovf, 0, 0, 100, 40, 0.3f, 0.3f, 0.3f);
        // Rotated rectangle, anchor top left (rotation around its corner, then moved to (640, 80)).
        Rect(Rot, 0, 640, 80, 100, 60, 0.9f, 0.9f, 0.2f);
        cmd.SetI32(Rot, Protocol.Prop.Anchor, 0x1 | 0x20);
        var rot = Matrix.RotationZ(MathUtil.Pi / 6) * Matrix.Translation(640, 80, 0);
        cmd.SetMat4(Rot, Protocol.Prop.Transform, in rot);
        all.AddRange(new[] { Col, Child(Col, 0), Child(Col, 1), Child(Col, 2), Txt, Grp, Child(Grp, 0), Child(Grp, 1), RowId,
            Child(RowId, 0), Child(RowId, 1), Child(RowId, 2), Rot });
        // Measure sinks on every element (handle = id); the green child keeps its interactive sink.
        foreach (var id in all)
            if (id != Child(Col, 1)) cmd.Bind(id, id, 0);
        CheckApply("scene");

        // Baseline: measured values (MEASURED + SCENE_BOUNDS of every sink).
        var baseline = new Dictionary<(ushort, uint), string>();
        foreach (var r in Frame(3))
            if (r.Event is Protocol.Ev.Measured or Protocol.Ev.SceneBounds)
                baseline[(r.Event, r.Handle)] = Convert.ToHexString(r.Payload);
        s.Check(baseline.Count >= 2 * (all.Count - 1), $"debug: baseline measurements ({baseline.Count})");

        // Hit test: hover in/out of the green column child at (20+10+30, 20+10+40+20+20) = (60, 110).
        string Hover()
        {
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 780, 580);
            var outRecs = Frame(2);
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 60, 110);
            var inRecs = Frame(2);
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 780, 580);
            var out2 = Frame(2);
            string F(List<Rec> recs) => string.Join(",", recs.FindAll(r => r.Event == Protocol.Ev.Hover && r.Handle == HHoverHandler).ConvertAll(r => r.Payload[0]));
            return $"{F(outRecs)}|{F(inRecs)}|{F(out2)}";
        }
        var hoverOff = Hover();
        s.Check(hoverOff.EndsWith("|1|0"), $"debug: hover without debug ({hoverOff})");

        Bitmap off = s.Grab(w, "30_debug_off.png");
        s.Check(!Near(off, 20, 100, IsCyan) && !Near(off, 300, 60, IsCyan) && !Near(off, 450, 330, IsCyan), "debug: no overlay without debug");

        // Per element: Debug on the column only.
        cmd.SetBool(Col, Protocol.Prop.Debug, true);
        CheckApply("debug on column");
        var recs = Frame(2);
        s.Check(!recs.Exists(r => r.Event is Protocol.Ev.Measured or Protocol.Ev.SceneBounds),
            $"debug: column debug changes no measurement ({recs.Count} records)");
        using (var a = s.Grab(w, "30_debug_element.png"))
        {
            s.Check(Near(a, 20, 100, IsCyan), $"debug: column frame drawn ({Px(a, 20, 100)})");
            s.Check(!Near(a, 450, 330, IsCyan) && !Near(a, 300, 60, IsCyan) && !Near(a, 60, 30, IsCyan), "debug: other elements and children not drawn (not recursive)");
        }

        // Window-wide: everything.
        s.Check(Native.vlqt_window_set_debug(w, 1) == 0 && Native.vlqt_window_get_debug(w) == 1, "debug: set_debug(1)");
        s.Check(Native.vlqt_window_set_debug(IntPtr.Zero, 1) == -1, "debug: set_debug(null) -1");
        recs = Frame(2);
        s.Check(!recs.Exists(r => r.Event is Protocol.Ev.Measured or Protocol.Ev.SceneBounds),
            $"debug: window debug changes no measurement ({recs.Count} records)");
        var hoverOn = Hover();
        s.Check(hoverOn == hoverOff, $"debug: hit-testing identical ({hoverOff} vs {hoverOn})");
        using (var a = s.Grab(w, "30_debug_overlay.png"))
        {
            s.Check(Near(a, 20, 100, IsCyan) && Near(a, 300, 60, IsCyan) && Near(a, 450, 330, IsCyan), "debug: frames of column, text, row drawn");
            // Rotated by 30 deg around its top left (640, 80): the right edge midpoint lands at 640 + cos30*100 - sin30*30.
            float rx = 640 + MathF.Cos(MathF.PI / 6) * 100 - MathF.Sin(MathF.PI / 6) * 30, ry = 80 + MathF.Sin(MathF.PI / 6) * 100 + MathF.Cos(MathF.PI / 6) * 30;
            s.Check(Near(a, (int)MathF.Round(rx), (int)MathF.Round(ry), IsCyan), $"debug: rotated frame follows the transform ({Px(a, (int)rx, (int)ry)})");
            // Unchanged layout: pixels away from overlay lines equal the grab without debug (red child center, row child).
            s.Check(Px(a, 60, 40) == Px(off, 60, 40) && Px(a, 390, 360) == Px(off, 390, 360), $"debug: content unchanged under the overlay ({Px(a, 60, 40)}/{Px(off, 60, 40)}, {Px(a, 390, 360)}/{Px(off, 390, 360)})");
            // Size smaller than the content (#473): solid frame at the SIZE, overflow tinted red, implicit size orange.
            s.Check(Near(a, 80, 390, IsCyan), $"debug: fixed frame at SIZE ({Px(a, 80, 390)})");
            Color ov = Px(a, 80, 415), raw = Px(off, 80, 415);
            s.Check(ov.R > raw.R + 20 && ov.G <= raw.G, $"debug: overflow tinted red ({ov} over {raw})");
            s.Check(Near(a, 80, 440, c => c.R > 200 && c.G is > 100 and < 200 && c.B < 80), $"debug: implicit size orange ({Px(a, 80, 440)})");
        }

        // Changes while debug is on: measurements still flow (column child resized -> column measured changes).
        cmd.SetPlace(Child(Col, 2), 0, 0, 200, 40);
        CheckApply("resize child with debug on");
        recs = Frame(2);
        s.Check(recs.Exists(r => r.Event == Protocol.Ev.Measured && r.Handle == Col), "debug: measured still reported with debug on");
        cmd.SetPlace(Child(Col, 2), 0, 0, 140, 40);
        CheckApply("resize child back");
        Frame(2);

        // Off again: overlay gone, measurements identical to the baseline.
        Native.vlqt_window_set_debug(w, 0);
        cmd.SetBool(Col, Protocol.Prop.Debug, false);
        CheckApply("debug off");
        Frame(2);
        using (var a = s.Grab(w, "30_debug_off2.png"))
            s.Check(IsBg(Px(a, 20, 100)) && Native.vlqt_window_get_debug(w) == 0, $"debug: overlay gone after off ({Px(a, 20, 100)})");
        // Re-bind every sink: the first poll after BIND reports the current values, compare with the baseline.
        foreach (var id in all)
        {
            cmd.Bind(id, 0, 0);
            if (id == Child(Col, 1)) cmd.Bind(id, HHover, 0);
            else cmd.Bind(id, id, 0);
        }
        CheckApply("rebind");
        var after = new Dictionary<(ushort, uint), string>();
        foreach (var r in Frame(2))
            if (r.Event is Protocol.Ev.Measured or Protocol.Ev.SceneBounds)
                after[(r.Event, r.Handle)] = Convert.ToHexString(r.Payload);
        int diff = 0;
        foreach (var kv in baseline)
            if (!after.TryGetValue(kv.Key, out var v) || v != kv.Value) diff++;
        s.Check(diff == 0 && after.Count == baseline.Count, $"debug: measurements identical after debug ({diff} differ, {after.Count}/{baseline.Count})");
        off.Dispose();

        // Debug on a destroyed element: overlay switches off (count drops), no crash.
        cmd.SetBool(Rot, Protocol.Prop.Debug, true);
        CheckApply("debug on rotated");
        Frame(1);
        cmd.Destroy(Rot);
        CheckApply("destroy debugged element");
        Frame(1);
        using (var a = s.Grab(w, "30_debug_destroyed.png"))
            s.Check(!Near(a, 20, 100, IsCyan), "debug: overlay off after the debugged element is destroyed");

        Native.vlqt_window_destroy(w);
    }
}
