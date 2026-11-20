using System;
using System.Diagnostics;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// Rectangle in QtNative: color, radius, border.color, border.width; 1000-rect performance.
// Grabs go to <OutDir>/05_rect*.png. Values are DIP (the C# side converts units).
[TestFixture, NonParallelizable]
public static class RectangleTest
{
    const uint Stroked = 1, ColorOnly = 2, Rounded = 3, Translucent = 4;

    /// <summary>Color, radius and border of a rectangle, errors, and the cost of 1000 rectangles.</summary>
    [Test, Category("rectangle")]
    public static void Rectangle() => Scenario.Run(Run);

    static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt rectangle test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        int Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Rect(uint id, float x, float y, float r, float g, float b, float a = 1)
        {
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, x, y, 200, 150);
            cmd.SetColor(id, Protocol.Prop.Color, r, g, b, a);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // Blue fill, red border 10 (drawn inside the bounds).
        Rect(Stroked, 100, 100, 0, 0, 1);
        cmd.SetColor(Stroked, Protocol.Prop.BorderColor, 1, 0, 0, 1);
        cmd.SetF32(Stroked, Protocol.Prop.BorderWidth, 10);
        // Border color without width: no border (StrokeWidth default 0, unlike Qt's 1).
        Rect(ColorOnly, 400, 100, 0, 1, 0);
        cmd.SetColor(ColorOnly, Protocol.Prop.BorderColor, 1, 0, 0, 1);
        // Radius 50: corners cut.
        Rect(Rounded, 100, 350, 1, 1, 1);
        cmd.SetF32(Rounded, Protocol.Prop.Radius, 50);
        // Straight alpha 0.5 red over background.
        Rect(Translucent, 400, 350, 1, 0, 0, 0.5f);
        CheckApply("rectangle scene");
        s.Pump(3);
        using (var a = s.Grab(w, "05_rect.png"))
        {
            s.Check(Near(Px(a, 200, 175), 0, 0, 255) && Near(Px(a, 105, 175), 255, 0, 0) && Near(Px(a, 200, 245), 255, 0, 0)
                && IsBg(Px(a, 95, 175)) && Near(Px(a, 115, 175), 0, 0, 255),
                $"fill blue + border red 10 inside bounds ({Px(a, 200, 175)}, {Px(a, 105, 175)}, {Px(a, 95, 175)}, {Px(a, 115, 175)})");
            s.Check(Near(Px(a, 401, 175), 0, 255, 0) && Near(Px(a, 500, 101), 0, 255, 0),
                $"border color alone draws no border ({Px(a, 401, 175)}, {Px(a, 500, 101)})");
            s.Check(IsBg(Px(a, 104, 354)) && Near(Px(a, 200, 425), 255, 255, 255) && Near(Px(a, 200, 352), 255, 255, 255),
                $"radius 50 cuts corners ({Px(a, 104, 354)}, {Px(a, 200, 425)})");
            s.Check(Near(Px(a, 500, 425), 143, 16, 16), $"straight alpha 0.5 blends with background ({Px(a, 500, 425)})");
        }

        // Change border/radius later; width 0 removes the border again.
        cmd.Reset();
        cmd.SetF32(Stroked, Protocol.Prop.BorderWidth, 0);
        cmd.SetF32(Rounded, Protocol.Prop.Radius, 0);
        cmd.SetColor(ColorOnly, Protocol.Prop.BorderColor, 0, 0, 1, 1);
        cmd.SetF32(ColorOnly, Protocol.Prop.BorderWidth, 20);
        CheckApply("rectangle update");
        s.Pump(3);
        using (var a = s.Grab(w, "05_rect_update.png"))
            s.Check(Near(Px(a, 105, 175), 0, 0, 255) && Near(Px(a, 104, 354), 255, 255, 255) && Near(Px(a, 410, 175), 0, 0, 255)
                && Near(Px(a, 430, 175), 0, 255, 0),
                $"border width 0 / radius 0 / new border ({Px(a, 105, 175)}, {Px(a, 104, 354)}, {Px(a, 410, 175)}, {Px(a, 430, 175)})");

        // Wrong value type -> -7, no crash.
        cmd.Reset();
        cmd.SetColor(Rounded, Protocol.Prop.Radius, 1, 1, 1, 1);
        var rcv = Apply();
        s.Check(rcv == -7, $"radius as color -> -7 ({rcv}: {Native.LastError()})");

        // Perf: 1000 rectangles (QQmlComponent::create) + bounds + color + reparent, then the first frame.
        cmd.Reset();
        for (uint i = 0; i < 1000; i++)
        {
            uint id = 100 + i;
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, i % 790, (i * 7) % 590, 10, 10);
            cmd.SetColor(id, Protocol.Prop.Color, (i % 10) / 10f, 1, 0, 1);
            cmd.Reparent(id, 0, 1000);
        }
        var sw = Stopwatch.StartNew();
        var rcp = Apply();
        var applyMs = sw.Elapsed.TotalMilliseconds;
        s.Pump(1);
        var totalMs = sw.Elapsed.TotalMilliseconds;
        s.Check(rcp == 0, $"create 1000 rectangles ({rcp})");
        // Software backend rasterizes the first frame on the CPU (140-185 ms on arm64), so it gets a looser limit.
        var firstFrameMs = s.QuickSoftware ? 500 : 50;
        PerfLimits.Below(s, totalMs, firstFrameMs, $"1000 rectangles create + first frame < {firstFrameMs} ms ({totalMs:F2} ms)");
        s.Info($"1000 RECTANGLE create+bounds+color+reparent: apply {applyMs:F2} ms, incl. first frame {totalMs:F2} ms");

        // 1000 SET color per frame (cached QMetaProperty), each followed by a rendered frame.
        var times = new double[20];
        for (int run = 0; run < times.Length; run++)
        {
            cmd.Reset();
            float r = (run % 2) * 0.5f;
            for (uint i = 0; i < 1000; i++)
                cmd.SetColor(100 + i, Protocol.Prop.Color, r, 0, 1, 1);
            sw.Restart();
            Apply();
            times[run] = sw.Elapsed.TotalMilliseconds;
            s.Pump(1);
        }
        Array.Sort(times);
        var median = times[times.Length / 2];
        PerfLimits.Below(s, median, 1, $"1000 SET color per frame < 1 ms (median {median:F3} ms)");
        s.Info($"1000 SET Color/frame: median {median:F3} ms, min {times[0]:F3} ms, max {times[^1]:F3} ms");
        using (s.Grab(w, "05_rect_1000.png")) { }

        Native.vlqt_window_destroy(w);
    }
}
