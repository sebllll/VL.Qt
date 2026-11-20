using NUnit.Framework;
using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using static Pixels;

// Frame of an auto-sized Group in negative coordinates (#475, "Group frame" in protocol.h): the snapshot of the Group is
// the whole frame (not (0, 0, w, h) of the item), and an effect layer on the Group draws the part left of / above its
// origin. Group origin at (300, 200) DIP; orange 100 x 100 centered on the origin (-50..50), blue 100 x 50 at (100, 50):
// frame (-50, -50) .. (200, 100) = 250 x 150 DIP, at (250, 150) in the window. Grabs: <OutDir>/475_*.png.
[TestFixture, NonParallelizable]
public static class GroupFrameTest
{
    static bool Near(int a, int b, int tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>Group frame with negative coordinates (#475).</summary>
    [Test, Category("group-frame")]
    public static void GroupFrame() => Scenario.Run(Run);

    static void Run(Scenario s)
    {
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);
        void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
            }
        }

        var w = Native.vlqt_window_create(800, 600, "VL.Qt group frame test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var scene = new SceneRegistry();
        var rec = new Reconciler(cmd, new IdAllocator(), scene) { Window = w };

        var orange = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.Center, Transform = Matrix.Translation(0, 0, 0), Fill = new Color4(1, 0.6f, 0, 1) };
        var blue = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 0.5f, 0), Fill = new Color4(0.2f, 0.4f, 1, 1) };
        var group = new Group { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(3, 2, 0), Children = [orange, blue] };
        var root = new Group { Anchor = RectangleAnchor.TopLeft, Children = [group] };

        void Frame(Element tree, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(tree, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
        }

        Frame(root, clear: true);
        Pump(3);

        // 1) Snapshot of the Group = its frame, 250 x 150, orange in the top left corner, blue in the bottom right one.
        var node = new SnapshotNode(client, scene);
        var image = node.Update(group, out var ready, out var error, 1, true);
        for (int frames = 0; !ready && error.Length == 0 && frames < 120; frames++)
        {
            Pump(1);
            image = node.Update(group, out ready, out error, 1);
        }
        s.Check(ready && image is not null && error.Length == 0, $"snapshot of the group is ready ('{error}')");
        if (ready && image is not null)
        {
            SnapshotTest.WritePng(image, Path.Combine(s.OutDir, "475_group_snapshot.png"));
            s.Check(image.Info.Width == 250 && image.Info.Height == 150, $"snapshot is the frame, 250 x 150 ({image.Info.Width} x {image.Info.Height})");
            var tl = SnapshotTest.At(image, 5, 5, 250);
            s.Check(tl.A == 255 && Near(tl.R, 255, 2) && Near(tl.G, 153, 2) && Near(tl.B, 0, 2), $"top left of the frame is the orange part in negative coordinates ({tl})");
            var ctr = SnapshotTest.At(image, 95, 95, 250);
            s.Check(ctr.A == 255 && Near(ctr.R, 255, 2), $"orange up to (50, 50) of the group ({ctr})");
            var br = SnapshotTest.At(image, 245, 145, 250);
            s.Check(br.A == 255 && Near(br.R, 51, 3) && Near(br.G, 102, 3) && Near(br.B, 255, 2), $"bottom right of the frame is blue ({br})");
            Action<bool, string> gpuOnly = (ok, what) => s.NeedsRhi(ok, what, "the software backend renders grabToImage into an opaque pixmap (screen format RGB32), transparent areas come out black, alpha 255; vlqt.h");
            gpuOnly(SnapshotTest.At(image, 5, 145, 250).A == 0 && SnapshotTest.At(image, 245, 5, 250).A == 0, "empty corners of the frame are transparent");
        }
        using (var a = s.Grab(w, "475_window.png"))
        {
            var o = Px(a, 255, 155);
            s.Check(o.R > 240 && Near(o.G, 153, 6) && o.B < 10, $"window shows the orange part left of / above the group origin ({o})");
        }

        // 2) Effect layer on the Group (ColorAdjust, saturation -1 = gray): the part in negative coordinates is drawn
        //    in place, not clipped and not shifted to the origin.
        var adjust = new ColorAdjustNode();
        var fxGroup = new Group { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(3, 2, 0), Children = [orange, blue], Effect = adjust.Update(null, 0, 0, -1) };
        Frame(new Group { Anchor = RectangleAnchor.TopLeft, Children = [fxGroup] });
        Pump(4);
        using (var a = s.Grab(w, "475_effect_layer.png"))
        {
            bool Gray(Color c) => c.R > 90 && Math.Abs(c.R - c.G) < 12 && Math.Abs(c.G - c.B) < 12;
            var neg = Px(a, 260, 160);
            var pos = Px(a, 340, 240);
            var bl = Px(a, 450, 275);
            var outside = Px(a, 245, 145);
            Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "MultiEffect is a ShaderEffect");
            gpu(Gray(neg), $"effect layer draws the orange part above / left of the origin in place, gray ({neg})");
            gpu(Gray(pos), $"effect layer draws the orange part right of the origin, gray ({pos})");
            gpu(Gray(bl), $"effect layer draws the blue child, gray ({bl})");
            s.Check(outside.R < 0x30 && outside.G < 0x30 && outside.B < 0x30, $"outside the frame stays background ({outside})");
            var shifted = Px(a, 390, 290); // the orange corner shifted by (+50, +50) would land here (empty in the group)
            gpu(shifted.R < 0x30, $"effect content is not shifted to the origin ({shifted})");
        }

        // 3) Auto Size off (#475): frame = Size placed by the Anchor around the group origin, the children stay where
        //    they are (Group = pure transform container). Snapshot, effect layer and debug frame use that frame.
        IImage? Snap(Element element, out string err)
        {
            var img = node.Update(element, out var ok, out err, 1, true);
            for (int frames = 0; !ok && err.Length == 0 && frames < 120; frames++)
            {
                Pump(1);
                img = node.Update(element, out ok, out err, 1);
            }
            return ok ? img : null;
        }
        bool IsOrange((int B, int G, int R, int A) p) => p.A == 255 && Near(p.R, 255, 2) && Near(p.G, 153, 2) && Near(p.B, 0, 2);
        bool IsBlue((int B, int G, int R, int A) p) => p.A == 255 && Near(p.R, 51, 3) && Near(p.G, 102, 3) && Near(p.B, 255, 2);
        Action<bool, string> gpuSnap = (ok, what) => s.NeedsRhi(ok, what, "the software backend renders grabToImage into an opaque pixmap (screen format RGB32), transparent areas come out black, alpha 255; vlqt.h");

        // TopLeft, Size 2 x 2: frame (0, 0) .. (200, 200) of the group; only the positive quarter of the orange is inside.
        var fixedTl = new Group { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(3, 2, 0), AutoSize = false, Size = new Vector2(2, 2), Children = [orange, blue] };
        Frame(new Group { Anchor = RectangleAnchor.TopLeft, Children = [fixedTl] });
        Pump(3);
        var tlImg = Snap(fixedTl, out var tlErr);
        s.Check(tlImg is { Info: { Width: 200, Height: 200 } }, $"Auto Size off, TopLeft: snapshot = Size 200 x 200 ({tlImg?.Info.Width} x {tlImg?.Info.Height}, '{tlErr}')");
        if (tlImg is not null)
        {
            SnapshotTest.WritePng(tlImg, Path.Combine(s.OutDir, "475_fixed_topleft.png"));
            s.Check(IsOrange(SnapshotTest.At(tlImg, 5, 5, 200)) && IsBlue(SnapshotTest.At(tlImg, 150, 75, 200)),
                $"Auto Size off, TopLeft: frame starts at the origin, children unmoved ({SnapshotTest.At(tlImg, 5, 5, 200)}, {SnapshotTest.At(tlImg, 150, 75, 200)})");
            gpuSnap(SnapshotTest.At(tlImg, 60, 60, 200).A == 0, "Auto Size off, TopLeft: no orange right of / below (50, 50)");
        }
        using (var a = s.Grab(w, "475_fixed_topleft_window.png"))
        {
            var o = Px(a, 255, 155);
            s.Check(o.R > 240 && Near(o.G, 153, 6) && o.B < 10, $"Auto Size off, TopLeft: children stay at the group origin ({o})");
        }

        // Center, Size 3 x 2: frame (-150, -100) .. (150, 100) of the group, centered on the origin; the whole orange inside.
        var fixedCenter = new Group { Anchor = RectangleAnchor.Center, Transform = Matrix.Translation(3, 2, 0), AutoSize = false, Size = new Vector2(3, 2), Children = [orange, blue] };
        Frame(new Group { Anchor = RectangleAnchor.TopLeft, Children = [fixedCenter] });
        Pump(3);
        var cImg = Snap(fixedCenter, out var cErr);
        s.Check(cImg is { Info: { Width: 300, Height: 200 } }, $"Auto Size off, Center: snapshot = Size 300 x 200 ({cImg?.Info.Width} x {cImg?.Info.Height}, '{cErr}')");
        if (cImg is not null)
        {
            SnapshotTest.WritePng(cImg, Path.Combine(s.OutDir, "475_fixed_center.png"));
            s.Check(IsOrange(SnapshotTest.At(cImg, 105, 55, 300)) && IsOrange(SnapshotTest.At(cImg, 195, 145, 300)),
                $"Auto Size off, Center: the orange around the origin is in the middle of the frame ({SnapshotTest.At(cImg, 105, 55, 300)}, {SnapshotTest.At(cImg, 195, 145, 300)})");
            s.Check(IsBlue(SnapshotTest.At(cImg, 260, 160, 300)), $"Auto Size off, Center: blue at (100, 50) of the group ({SnapshotTest.At(cImg, 260, 160, 300)})");
            gpuSnap(SnapshotTest.At(cImg, 5, 5, 300).A == 0 && SnapshotTest.At(cImg, 95, 45, 300).A == 0, "Auto Size off, Center: frame corners left of the orange are empty");
        }
        using (var a = s.Grab(w, "475_fixed_center_window.png"))
        {
            var o = Px(a, 255, 155);
            s.Check(o.R > 240 && Near(o.G, 153, 6) && o.B < 10, $"Auto Size off, Center: children stay at the group origin ({o})");
        }

        // Effect layer on the Center group: drawn in place, also left of / above the origin.
        var fixedFx = new Group { Anchor = RectangleAnchor.Center, Transform = Matrix.Translation(3, 2, 0), AutoSize = false, Size = new Vector2(3, 2), Children = [orange, blue], Effect = adjust.Update(null, 0, 0, -1) };
        Frame(new Group { Anchor = RectangleAnchor.TopLeft, Children = [fixedFx] });
        Pump(4);
        using (var a = s.Grab(w, "475_fixed_center_effect.png"))
        {
            bool Gray(Color c) => c.R > 90 && Math.Abs(c.R - c.G) < 12 && Math.Abs(c.G - c.B) < 12;
            Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "MultiEffect is a ShaderEffect");
            var neg = Px(a, 260, 160);
            var bl = Px(a, 450, 275);
            gpu(Gray(neg) && Gray(bl), $"Auto Size off, Center: effect layer in place, unclipped ({neg}, {bl})");
            var shifted = Px(a, 390, 290);
            gpu(shifted.R < 0x30, $"Auto Size off, Center: effect content not shifted ({shifted})");
        }

        node.Dispose();
        Pump(3);
        s.Check(client.LiveCount == 0, $"snapshot resource released (live {client.LiveCount})");
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_destroy(w);
        client.SetPumping(false);
        s.Pump(2);
    }
}
