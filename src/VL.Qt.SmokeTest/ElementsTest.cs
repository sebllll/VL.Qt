using System;
using NUnit.Framework;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Qt;
using static Pixels;

// Element registry of QtNative (plan 05a): Group (= TYPE_ITEM, QML Item), common properties, Matrix4x4 transform,
// unknown property -> error + log. Grabs go to <OutDir>/05_*.png.
[TestFixture, NonParallelizable]
public static class ElementsTest
{
    private static bool IsRed(System.Drawing.Color c) => Near(c, 255, 0, 0);
    private static bool IsGreen(System.Drawing.Color c) => Near(c, 0, 255, 0);

    private const uint Group = 1, Red = 2, Green = 3;

    /// <summary>Element types and the cost of 1000 groups.</summary>
    [Test, Category("elements")]
    public static void Elements() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt elements test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        int Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }

        // Group with Auto Size off: ANCHOR only places its frame, the children stay at the origin (#475). So "m around
        // the center of the rect" is T(-center) · m · T(center + position) with ANCHOR TopLeft.
        void PlaceGroupXf(float x, float y, float gw, float gh, in Matrix m)
        {
            cmd.SetVec2(Group, Protocol.Prop.Size, gw, gh);
            cmd.SetI32(Group, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            cmd.SetMat4(Group, Protocol.Prop.Transform, Matrix.Translation(-gw / 2, -gh / 2, 0) * m * Matrix.Translation(x + gw / 2, y + gh / 2, 0));
        }

        // Group 400x200 at (200,200): red left half, green right half (local coordinates).
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Group, Protocol.Type.Item);
        cmd.SetPlace(Group, 200, 200, 400, 200);
        cmd.SetBool(Group, Protocol.Prop.AutoSize, false); // explicit size 400 x 200 (Auto Size would ignore it, #83)
        cmd.Reparent(Group, 0, 0);
        cmd.Create(Red, Protocol.Type.Rectangle);
        cmd.SetPlace(Red, 0, 0, 200, 100);
        cmd.SetColor(Red, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(Red, Group, 0);
        cmd.Create(Green, Protocol.Type.Rectangle);
        cmd.SetPlace(Green, 200, 0, 200, 100);
        cmd.SetColor(Green, Protocol.Prop.Color, 0, 1, 0, 1);
        cmd.Reparent(Green, Group, 1);
        CheckApply("group scene");
        s.Pump(3);
        using (var a = s.Grab(w, "05_group.png"))
            s.Check(IsRed(Px(a, 300, 250)) && IsGreen(Px(a, 500, 250)) && IsBg(Px(a, 450, 180)),
                $"group children at group position ({Px(a, 300, 250)}, {Px(a, 500, 250)}, {Px(a, 450, 180)})");

        // Translation only: Stride row-major puts it in M41..M43; QtNative must transpose for Qt.
        cmd.Reset();
        PlaceGroupXf(200, 200, 400, 200, Matrix.Translation(100, 50, 0));
        CheckApply("translation");
        s.Pump(3);
        using (var a = s.Grab(w, "05_group_translate.png"))
            s.Check(IsRed(Px(a, 400, 300)) && IsBg(Px(a, 250, 220)) && IsGreen(Px(a, 650, 300)),
                $"translation (100,50) moves the group ({Px(a, 400, 300)}, {Px(a, 250, 220)}, {Px(a, 650, 300)})");

        // Rotation 90 deg around the group center (200,100 local): ANCHOR Center (#88, #469): v' = (v - c) * Rz * T(c + position).
        // Stride RotationZ(+90) turns clockwise on screen (y down): red (left) goes up, green (right) down.
        var rot = Matrix.RotationZ(MathUtil.PiOverTwo);
        for (int i = 0; i < 3; i++) // the same matrix again must not compound (Matrix4x4 attached once)
        {
            cmd.Reset();
            PlaceGroupXf(200, 200, 400, 200, rot);
            CheckApply($"rotation 90 #{i}");
        }
        s.Pump(3);
        using (var a = s.Grab(w, "05_group_rotate90.png"))
        {
            s.Check(IsRed(Px(a, 450, 180)) && IsGreen(Px(a, 450, 420)),
                $"rotation 90 around center: red above, green below ({Px(a, 450, 180)}, {Px(a, 450, 420)})");
            s.Check(IsBg(Px(a, 300, 250)) && IsBg(Px(a, 550, 250)) && IsBg(Px(a, 450, 90)) && IsBg(Px(a, 450, 510)),
                $"rotation 90: old area empty, nothing beyond 400..500 x 100..500 ({Px(a, 300, 250)}, {Px(a, 450, 90)})");
        }

        // Opacity / Clip on the group (Opacity 0 replaces Visible, #474).
        cmd.Reset();
        PlaceGroupXf(200, 200, 400, 200, Matrix.Identity);
        cmd.SetF32(Group, Protocol.Prop.Opacity, 0.5f);
        CheckApply("opacity 0.5");
        s.Pump(3);
        using (var a = s.Grab(w, "05_group_opacity.png"))
            s.Check(Near(Px(a, 300, 250), 143, 16, 16), $"opacity 0.5 blends red with background ({Px(a, 300, 250)})");

        cmd.Reset();
        cmd.SetF32(Group, Protocol.Prop.Opacity, 1);
        cmd.SetPlace(Group, 200, 200, 300, 200); // green half outside now
        cmd.SetBool(Group, Protocol.Prop.Clip, true);
        CheckApply("clip");
        s.Pump(3);
        using (var a = s.Grab(w, "05_group_clip.png"))
            s.Check(IsGreen(Px(a, 450, 250)) && IsBg(Px(a, 550, 250)), $"clip cuts children at group bounds ({Px(a, 450, 250)}, {Px(a, 550, 250)})");

        cmd.Reset();
        cmd.SetF32(Group, Protocol.Prop.Opacity, 0);
        CheckApply("opacity 0");
        s.Pump(3);
        using (var a = s.Grab(w, "05_group_hidden.png"))
            s.Check(IsBg(Px(a, 300, 250)), $"opacity 0 hides the group ({Px(a, 300, 250)})");

        // Unknown property: error code + message, logged once per type, no crash; the scene stays usable.
        int Hits() => Array.FindAll(s.Log.Split('\n'), l => l.Contains("vlqt.elements") && l.Contains("not supported by type ITEM")).Length;
        var hits0 = Hits(); // ApplyTest logged the same message for its own window (cache is per engine)
        for (int i = 0; i < 2; i++)
        {
            cmd.Reset();
            cmd.SetColor(Group, Protocol.Prop.Color, 1, 1, 1, 1);
            var rc = Apply();
            var e = Native.LastError();
            s.Check(rc == -6 && e.Contains("not supported by type ITEM"), $"unknown property -> -6 ({rc}: {e})");
        }
        var hits = Hits() - hits0;
        s.Check(hits == 1, $"unknown property logged once (vlqt.elements, {hits}x)");
        cmd.Reset();
        cmd.SetF32(Group, Protocol.Prop.Opacity, 1);
        CheckApply("scene usable after error");
        s.Pump(2);
        using (var a = s.Grab(w, "05_group_after_error.png"))
            s.Check(IsRed(Px(a, 300, 250)), $"group visible again after error ({Px(a, 300, 250)})");

        // Perf: 1000 groups (QQmlComponent::create) + first frame.
        cmd.Reset();
        for (uint i = 0; i < 1000; i++)
        {
            cmd.Create(100 + i, Protocol.Type.Item);
            cmd.SetPlace(100 + i, i % 700, i % 500, 10, 10);
            cmd.Reparent(100 + i, 0, 1000);
        }
        var sw = Stopwatch.StartNew();
        var rcp = Apply();
        var applyMs = sw.Elapsed.TotalMilliseconds;
        s.Pump(1);
        var totalMs = sw.Elapsed.TotalMilliseconds;
        s.Check(rcp == 0, $"create 1000 groups ({rcp})");
        s.Perf("elements.create_1000_groups_apply", applyMs);
        s.Perf("elements.create_1000_groups_first_frame_total", totalMs);
        s.Info($"GROUP create+bounds+reparent: apply {applyMs:F2} ms, incl. first frame {totalMs:F2} ms");

        // 1000 transforms per frame (Matrix4x4.matrix via cached QMetaProperty; first one attaches).
        var times = new double[10];
        for (int run = 0; run < times.Length; run++)
        {
            cmd.Reset();
            var m = Matrix.RotationZ(run * 0.1f);
            for (uint i = 0; i < 1000; i++)
                cmd.SetMat4(100 + i, Protocol.Prop.Transform, m);
            sw.Restart();
            Apply();
            times[run] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times, 1, times.Length - 1);
        s.Perf("elements.set_transform_1000_median", times[5]);
        s.Info($"SET Transform: first (attach) {times[0]:F2} ms, then median {times[5]:F3} ms");

        Native.vlqt_window_destroy(w);
    }
}
