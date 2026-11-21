using System;
using NUnit.Framework;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using Rectangle = VL.Qt.Elements.Rectangle;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using static Pixels;

// End-to-end path of plan 04e: element tree → Reconciler → CommandWriter → real QtNative.
// Grabs go to <OutDir>/04_reconciler*.png. Element values are units (Space DIPTopLeft, 1 unit = 100 DIP);
// the expected pixel positions below are logical pixels (DIP).
[TestFixture, NonParallelizable]
public static class ReconcilerTest
{
    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    private static bool IsBlue(Color c) => c.B > 200 && c.R < 40 && c.G < 40;
    private static bool IsYellow(Color c) => c.R > 200 && c.G > 200 && c.B < 40;

    private static readonly Color4 Red = new(1, 0, 0, 1), Green = new(0, 1, 0, 1), Blue = new(0, 0, 1, 1), Yellow = new(1, 1, 0, 1);

    // Column at (1, 1) units, 2 x 4 units, padding 0.1, spacing 0.2, Left. The children have no position
    // (Transform identity, the layout places them): only their size goes to Qt.
    private static Column Scene(Element c0, Element c1, Element? c2) => new()
    {
        Size = new Vector2(2, 4), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0),
        Padding = 0.1f,
        Spacing = 0.2f,
        Children = c2 is null ? ImmutableArray.Create(c0, c1) : ImmutableArray.Create(c0, c1, c2),
    };

    private static Rectangle R(float w, float h, Color4 fill) => new() { Size = new Vector2(w, h), Anchor = RectangleAnchor.TopLeft, Fill = fill };

    /// <summary>Reconciler (scene diffing).</summary>
    [Test, Category("reconciler")]
    public static void Reconciler() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt reconciler test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());

        // One renderer frame: Reset, Update, apply (skipped when the reconciler wrote nothing, like Renderer).
        int Frame(Element? root, string what, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(root, Space.DIPTopLeft);
            int count = cmd.Count - (clear ? 1 : 0);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(3);
            return count;
        }

        // Frame 1: whole tree. Expected DIP: children at x 110 (padding 10), red 100x50 at y 110..160,
        // green 150x50 at y 180..230, blue 50x50 at y 250..300.
        var red = R(1, 0.5f, Red);
        var green = R(1.5f, 0.5f, Green);
        var blue = R(0.5f, 0.5f, Blue);
        var scene = Scene(red, green, blue);
        int n = Frame(scene, "column with 3 rectangles", clear: true);
        s.Check(n > 0 && rec.Root is not null && rec.Root.Children.Count == 3, $"first frame creates the tree ({n} commands)");
        using (var a = s.Grab(w, "04_reconciler.png"))
        {
            s.Check(IsRed(Px(a, 150, 135)) && IsGreen(Px(a, 240, 205)) && IsBlue(Px(a, 135, 275)),
                $"3 rectangles stacked in the column ({Px(a, 150, 135)}, {Px(a, 240, 205)}, {Px(a, 135, 275)})");
            s.Check(IsRed(Px(a, 111, 111)) && IsRed(Px(a, 208, 158)) && IsBg(Px(a, 105, 105)) && IsBg(Px(a, 212, 135)),
                $"red at (110,110)..(210,160) DIP = column (1,1) units + padding ({Px(a, 105, 105)})");
            s.Check(IsBg(Px(a, 150, 170)) && IsBg(Px(a, 150, 240)), "spacing 0.2 units = 20 DIP gaps");
            s.Check(IsGreen(Px(a, 258, 228)) && IsBg(Px(a, 262, 205)) && IsBg(Px(a, 165, 275)) && IsBg(Px(a, 135, 305)),
                "children keep their size (green 150 DIP wide, blue 50x50)");
            s.Check(IsBg(Px(a, 310, 310)) && IsBg(Px(a, 350, 350)), "children without position stay in the column cell");
        }

        // Same tree again (reference-equal): no commands.
        n = Frame(scene, "unchanged tree");
        s.Check(n == 0, $"unchanged tree → 0 commands ({n})");

        // New tree, one fill changed: exactly 1 SET.
        var yellow = R(1, 0.5f, Yellow);
        scene = Scene(yellow, green, blue);
        n = Frame(scene, "one fill changed");
        s.Check(n == 1, $"one property changed → 1 command ({n})");
        // Green gets taller: the column moves blue down (green 180..280, blue 300..350).
        var tallGreen = R(1.5f, 1, Green);
        scene = Scene(yellow, tallGreen, blue);
        n = Frame(scene, "green taller");
        s.Check(n == 1, $"child size changed → 1 command ({n})");
        using (var a = s.Grab(w, "04_reconciler_update.png"))
        {
            s.Check(IsYellow(Px(a, 150, 135)), $"red → yellow ({Px(a, 150, 135)})");
            s.Check(IsGreen(Px(a, 240, 270)) && IsBlue(Px(a, 135, 325)) && IsBg(Px(a, 135, 290)),
                $"column re-stacks after size change ({Px(a, 240, 270)}, {Px(a, 135, 325)})");
        }

        // Remove the last child: 1 DESTROY.
        scene = Scene(yellow, tallGreen, null);
        n = Frame(scene, "remove last child");
        s.Check(n == 1 && rec.Root!.Children.Count == 2, $"last child removed → 1 command ({n})");
        // Clear (renderer dispose): 1 DESTROY for the column.
        cmd.Reset();
        rec.Clear();
        n = cmd.Count;
        var rcClear = Native.Apply(w, cmd.EndFrame());
        s.Pump(3);
        s.Check(n == 1 && rcClear == 0, $"Clear → 1 DESTROY ({n}, rc {rcClear})");
        using (var a = s.Grab(w, "04_reconciler_clear.png"))
            s.Check(IsBg(Px(a, 150, 135)) && IsBg(Px(a, 240, 205)), "scene empty after Clear");

        Perf(w, s);
        Native.vlqt_scene_destroy(w);
    }

    // Steady state through the real QtNative: 1000 rectangles, one color change per frame.
    // Limits are generous; the measured values go to the log.
    private static void Perf(SceneHandle w, Scenario s)
    {
        const int N = 1000, Frames = 1000;
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var children = new Element[N];
        for (int i = 0; i < N; i++)
            children[i] = new Rectangle { Size = new Vector2(0.15f, 0.15f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(i % 40 * 0.2f, i / 40 * 0.2f, 0), Fill = Color4.White };
        var changed = (Element[])children.Clone();
        changed[N / 2] = new Rectangle { Size = children[N / 2].Size, Anchor = children[N / 2].Anchor, Transform = children[N / 2].Transform, Fill = new Color4(1, 0, 0, 1) };
        var a = new Group { Anchor = RectangleAnchor.TopLeft, Children = ImmutableArray.Create(children) };
        var b = new Group { Anchor = RectangleAnchor.TopLeft, Children = ImmutableArray.Create(changed) };

        var t = Stopwatch.StartNew();
        rec.Update(a, Space.DIPTopLeft);
        double createReconcileMs = t.Elapsed.TotalMilliseconds;
        int createCommands = cmd.Count;
        t.Restart();
        var rc = Native.Apply(w, cmd.EndFrame());
        double createApplyMs = t.Elapsed.TotalMilliseconds;
        s.Check(rc == 0, $"perf: apply 1000 rectangles ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(2);

        int errors = 0;
        void Step(int i)
        {
            cmd.Reset();
            rec.Update((i & 1) == 0 ? b : a, Space.DIPTopLeft);
            if (Native.Apply(w, cmd.EndFrame()) != 0) errors++;
        }
        for (int i = 0; i < 100; i++) Step(i); // warm-up

        var reconcileMs = new double[Frames];
        var applyMs = new double[Frames];
        int commands = 0;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            cmd.Reset();
            rec.Update((i & 1) == 0 ? b : a, Space.DIPTopLeft);
            var buf = cmd.EndFrame();
            long t1 = Stopwatch.GetTimestamp();
            if (Native.Apply(w, buf) != 0) errors++;
            reconcileMs[i] = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            applyMs[i] = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            commands += cmd.Count;
        }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        s.Pump(2);
        Array.Sort(reconcileMs);
        Array.Sort(applyMs);
        s.Info($"first frame 1000 rectangles: reconcile {createReconcileMs:F2} ms ({createCommands} commands), apply {createApplyMs:F2} ms");
        s.Info($"steady state 1000 rectangles, 1 change/frame: reconcile+write median {reconcileMs[Frames / 2]:F4} ms " +
            $"(p99 {reconcileMs[Frames * 99 / 100]:F4}, max {reconcileMs[^1]:F4}), apply median {applyMs[Frames / 2]:F4} ms " +
            $"(p99 {applyMs[Frames * 99 / 100]:F4}, max {applyMs[^1]:F4}), {alloc} bytes allocated in {Frames} frames");
        s.Check(errors == 0 && commands == Frames, $"perf: 1 command per frame, no apply errors ({commands}, {errors})");
        s.Perf("reconciler.first_frame_1000_reconcile", createReconcileMs);
        s.Perf("reconciler.first_frame_1000_apply", createApplyMs);
        s.Perf("reconciler.steady_reconcile_write_median", reconcileMs[Frames / 2]);
        s.Perf("reconciler.steady_apply_median", applyMs[Frames / 2]);
        PerfLimits.Below(s, reconcileMs[Frames / 2], 1.0, $"perf: reconcile+write median < 1 ms ({reconcileMs[Frames / 2]:F4} ms)");
        PerfLimits.Below(s, applyMs[Frames / 2], 1.0, $"perf: apply median < 1 ms ({applyMs[Frames / 2]:F4} ms)");
        s.Check(alloc < 1024, $"perf: {Frames} frames reconcile + apply allocate < 1 KB ({alloc} bytes)");

        cmd.Reset();
        rec.Clear();
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "perf: Clear");
    }
}
