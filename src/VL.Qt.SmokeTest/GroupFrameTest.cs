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

// Frame of an auto-sized Group in negative coordinates ("Group frame" in protocol.h): the snapshot of the Group is
// the whole frame (not (0, 0, w, h) of the item), and an effect layer on the Group draws the part left of / above its
// origin. Group origin at (300, 200) DIP; orange 100 x 100 centered on the origin (-50..50), blue 100 x 50 at (100, 50):
// frame (-50, -50) .. (200, 100) = 250 x 150 DIP, at (250, 150) in the window. Grabs: <OutDir>/475_*.png.
[TestFixture, NonParallelizable]
public static class GroupFrameTest
{
    private static bool Near(int a, int b, int tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>Group frame with negative coordinates.</summary>
    [Test, Category("group-frame")]
    public static void GroupFrame() => Scenario.Run(Run);

    private static void Run(Scenario s)
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

        // 3) Auto Size off: frame = Size placed by the Anchor around the group origin, the children stay where
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

    /// <summary>NELM-14: the layer effect item of a child does not count in the frame of its Group.</summary>
    [Test, Category("group-frame-effect")]
    public static void GroupFrameEffect() => Scenario.Run(RunEffect);

    // NELM-14: Qt's item layer creates the layer.effect item as a child of the layered item's parent, so the effect item of
    // a child with an effect chain is a child of the Group, and the Group frame (polish) adds its rect, padding included.
    // Decided (C10): the Group counts only tracked children. Group at (200, 150) DIP, top left anchor, auto size; child: a
    // 100 x 100 orange rectangle at (0, 0). Frame without effect: 100 x 100; with a Blur chain on the child it must stay.
    private static void RunEffect(Scenario s)
    {
        const uint g = 1, r = 2, hg = 101;
        var w = Native.vlqt_window_create(800, 600, "VL.Qt group frame effect test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        (float W, float H)? measured = null;
        (float X, float Y, float W, float H)? bounds = null;
        void Poll()
        {
            int n = Native.Poll(w, buf);
            var b = buf.AsSpan(0, Math.Max(n, 0));
            int off = 0;
            while (off + 12 <= b.Length)
            {
                ushort ev = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
                int len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b[(off + 2)..]);
                if (len < 8 || off + 4 + len > b.Length)
                    break;
                uint handle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 4)..]);
                var p = b.Slice(off + 12, len - 8).ToArray();
                float F(int i) => BitConverter.ToSingle(p, 4 * i);
                if (handle == hg && ev == Protocol.Ev.Measured && p.Length >= 8)
                    measured = (F(0), F(1));
                else if (handle == hg && ev == Protocol.Ev.SceneBounds && p.Length >= 16)
                    bounds = (F(0), F(1), F(2), F(3));
                off += (4 + len + 3) & ~3;
            }
        }
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(g, Protocol.Type.Item);
        cmd.SetI32(g, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.SetMat4(g, Protocol.Prop.Transform, Matrix.Translation(200, 150, 0));
        cmd.Reparent(g, 0, 0);
        cmd.Bind(g, hg, 0);
        cmd.Create(r, Protocol.Type.Rectangle);
        cmd.SetPlace(r, 0, 0, 100, 100);
        cmd.SetColor(r, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(r, g, 0);
        Apply("group and child");
        s.Pump(4);
        Poll();
        var plain = measured;
        var plainBounds = bounds;
        s.Check(plain is { } m0 && Near((int)m0.W, 100, 1) && Near((int)m0.H, 100, 1), $"without effect: frame 100 x 100 ({plain}, bounds {plainBounds})");

        // Blur on the child: the MultiEffect grows the child's effect item by its padding (Blur Max 32).
        cmd.BeginEffectChain(r);
        cmd.BeginMultiLink();
        cmd.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
        cmd.EffectF32(Protocol.Prop.EffectBlur, 1f);
        cmd.EffectI32(Protocol.Prop.EffectBlurMax, 32);
        cmd.EndLink();
        cmd.EndEffectChain();
        Apply("Blur chain on the child");
        s.Pump(6);
        Poll();
        s.Info($"frame with the Blur on the child: measured {measured}, bounds {bounds} (without: {plain}, {plainBounds})");
        // The Blur is drawn with its padding: the halo reaches outside the child (else the case would prove nothing).
        using (var a = s.Grab(w, "group-frame-effect.png"))
        {
            var halo = Px(a, 197, 200);
            s.NeedsRhi(halo.R > 0x20 + 12 && halo.R > halo.B + 12, $"the Blur's halo is drawn 3 DIP left of the child ({halo})", "the Blur is a MultiEffect");
        }
        s.Check(measured == plain && bounds == plainBounds,
            $"the Blur on the child does not change the Group frame (measured {measured}, bounds {bounds}; without: {plain}, {plainBounds})");

        // The child goes with its Group.
        cmd.Destroy(g);
        Apply("destroy");
        s.Pump(2);
        Native.vlqt_window_destroy(w);
    }
}
