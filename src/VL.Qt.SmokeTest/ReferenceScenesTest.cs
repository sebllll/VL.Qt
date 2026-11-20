using System;
using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt;
using VL.Qt.Styles;

// Reference scenes the refactoring touches (TEST-03), compared with src/VL.Qt.SmokeTest/references/reference-*.png like the
// README pictures (ImageCompare: WARP, own process). Each scene is a 400 x 300 DIP window on ground 0x16, raw commands.
//   reference-layout     Column / Row with child alignment, spacing and padding, rounded rectangles
//   reference-shape      Shape: closed polygon with fill and miter stroke, dashed polyline, round-capped cubic with dash offset
//   reference-widgets    Button, CheckBox, Switch and Slider in the base style and in a style with changed colors
//   reference-image      one image in the four fill modes, smoothed, in wide frames
//   reference-group      Group (Item) with opacity, with clip, and both, children that overlap and stick out
[TestFixture, NonParallelizable]
public static class ReferenceScenesTest
{
    /// <summary>Reference scenes against the references (WARP, own process).</summary>
    [Test, Category("reference-scenes"), Category("image-compare"), Category("own-process")]
    public static void ReferenceScenes() => Scenario.RunLocal(s => ImageCompare.Run(s, new[] { "reference-scenes" }));

    /// <summary>
    /// Writes the references of all compare scenarios (readme-images, readme-gallery, reference-scenes) on WARP. Run it on the
    /// x64 workstation only after a change that is meant to alter the pictures, commit the PNGs on their own and put the diff
    /// images of the failing compare into the pull request description.
    /// </summary>
    [Test, Explicit("writes src/VL.Qt.SmokeTest/references"), Category("update-refs")]
    public static void UpdateRefs() => Scenario.RunLocal(ImageCompare.UpdateAll);

    const float Bg = 0x16 / 255f;
    const uint Orange = 0xFF8C33, Cyan = 0x26C7FF, Pink = 0xFF5C9E, Green = 0x7CD66B, Panel = 0x1E2128;
    const int Width = 400, Height = 300;

    static (float r, float g, float b) C(uint rgb) => (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    static StateValues<Color4> Colors(Color4 normal, Color4? disabled = null)
    {
        var v = default(StateValues<Color4>).With(StyleStates.Normal, normal);
        return disabled is { } d ? v.With(StyleStates.Disabled, d) : v;
    }

    internal static void Run(Scenario s)
    {
        var family = ImageCompare.LoadFont(s);
        var cmd = new CommandWriter();
        uint next = 1, order = 0;

        uint Rect(float x, float y, float w, float h, uint rgb, float radius = 0, uint parent = 0, float opacity = 1)
        {
            var id = next++;
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, x, y, w, h);
            var (r, g, b) = C(rgb);
            cmd.SetColor(id, Protocol.Prop.Color, r, g, b, 1);
            if (radius > 0) cmd.SetF32(id, Protocol.Prop.Radius, radius);
            if (opacity != 1) cmd.SetF32(id, Protocol.Prop.Opacity, opacity);
            cmd.Reparent(id, parent, order++);
            return id;
        }
        uint Container(ushort type, float x, float y, float w, float h, float spacing, float padding, int align, uint parent = 0)
        {
            var id = next++;
            cmd.Create(id, type);
            cmd.SetBool(id, Protocol.Prop.AutoSize, false);
            cmd.SetPlace(id, x, y, w, h);
            cmd.SetF32(id, Protocol.Prop.Spacing, spacing);
            cmd.SetF32(id, Protocol.Prop.Padding, padding);
            if (align != 0) cmd.SetI32(id, Protocol.Prop.ChildAlignment, align);
            cmd.Reparent(id, parent, order++);
            return id;
        }
        uint NewShape(float x, float y, uint rgbStroke, float strokeWidth)
        {
            var id = next++;
            cmd.Create(id, Protocol.Type.Shape);
            cmd.SetPlace(id, x, y, 0, 0);
            var (r, g, b) = C(rgbStroke);
            cmd.SetColor(id, Protocol.Prop.ShapeStrokeColor, r, g, b, 1);
            cmd.SetF32(id, Protocol.Prop.ShapeStrokeWidth, strokeWidth);
            cmd.Reparent(id, 0, order++);
            return id;
        }

        void Render(string name, Action build)
        {
            cmd.Reset();
            next = 1;
            order = 0;
            var w = Native.vlqt_window_create(Width, Height, "VL.Qt reference scene");
            s.Check(w != IntPtr.Zero, $"reference-{name}: window_create");
            Native.vlqt_window_show(w);
            s.Pump(4);
            cmd.ClearColor(Bg, Bg, Bg, 1);
            build();
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"reference-{name}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
            s.Pump(10);
            var grabPath = Path.Combine(s.OutDir, $"reference-{name}.png");
            using (var a = s.Grab(w, grabPath))
            {
                var ink = ImageCompare.Ink(a);
                s.Check(ink > 0.02, $"reference-{name}: picture not empty (ink {ink:P1}, {a.Width} x {a.Height})");
            }
            ImageCompare.Check(s, "reference-" + name, grabPath, Width);
            Native.vlqt_window_destroy(w);
            s.Pump(1);
        }

        Render("layout", () =>
        {
            Rect(0, 0, 130, 300, Panel, 10);
            var col = Container(Protocol.Type.Column, 0, 0, 130, 300, 8, 12, 0x4);
            foreach (var (w, rgb) in new[] { (60f, Orange), (100f, Cyan), (40f, Pink), (80f, Green) })
                Rect(0, 0, w, 52, rgb, 8, col);
            Rect(140, 0, 260, 140, Panel, 10);
            var rowBottom = Container(Protocol.Type.Row, 140, 0, 260, 140, 10, 12, 0x40);
            foreach (var (h, rgb) in new[] { (40f, Orange), (90f, Cyan), (60f, Pink), (110f, Green) })
                Rect(0, 0, 40, h, rgb, 6, rowBottom);
            Rect(140, 150, 260, 150, Panel, 10);
            var outer = Container(Protocol.Type.Column, 140, 150, 260, 150, 6, 10, 0);
            var inner1 = Container(Protocol.Type.Row, 0, 0, 240, 60, 6, 0, 0, outer);
            foreach (var rgb in new[] { Orange, Cyan, Pink })
                Rect(0, 0, 70, 60, rgb, 30, inner1);
            var inner2 = Container(Protocol.Type.Row, 0, 0, 240, 60, 20, 0, 0, outer);
            foreach (var rgb in new[] { Green, Pink })
                Rect(0, 0, 100, 60, rgb, 4, inner2);
        });

        Render("shape", () =>
        {
            Rect(0, 0, 400, 300, Panel);
            // Closed polygon: fill, thick miter stroke.
            var poly = NewShape(30, 30, Orange, 12);
            cmd.SetF32Array(poly, Protocol.Prop.ShapePoints, [0, 0, 150, 20, 120, 110, 10, 90]);
            cmd.SetBool(poly, Protocol.Prop.ShapeClosed, true);
            cmd.SetColor(poly, Protocol.Prop.ShapeFill, 0.15f, 0.5f, 0.8f, 1);
            cmd.SetI32(poly, Protocol.Prop.ShapeJoin, 0);
            // Dashed polyline, 2 on / 1 off in stroke widths.
            var dash = NewShape(220, 40, Cyan, 8);
            cmd.SetF32Array(dash, Protocol.Prop.ShapePoints, [0, 0, 80, 60, 150, 0]);
            cmd.SetF32Array(dash, Protocol.Prop.ShapeDash, [2, 1]);
            // Cubic S-curve, round caps, dash with offset.
            var cubic = NewShape(30, 170, Green, 10);
            cmd.SetPath(cubic, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Cubic], [0, 100, 0, 0, 340, 100, 340, 0]);
            cmd.SetI32(cubic, Protocol.Prop.ShapeCap, 1);
            cmd.SetF32Array(cubic, Protocol.Prop.ShapeDash, [3, 2]);
            cmd.SetF32(cubic, Protocol.Prop.ShapeDashOffset, 1);
            // Solid stroke for the contrast.
            var line = NewShape(30, 280, Pink, 6);
            cmd.SetF32Array(line, Protocol.Prop.ShapePoints, [0, 0, 340, 0]);
        });

        // Base style and a style with changed colors; the font is the bundled one in both.
        var baseStyle = ImageCompare.WithFont(BaseStyle.Default, family);
        var b0 = BaseStyle.Default;
        var custom = ImageCompare.WithFont(b0 with
        {
            Frame = b0.Frame! with { Color = Colors(new Color4(0.85f, 0.4f, 0.1f, 1), new Color4(0.4f, 0.3f, 0.25f, 1)) },
            Track = b0.Track! with
            {
                Color = Colors(new Color4(0.25f, 0.25f, 0.3f, 1)),
                FillColor = Colors(new Color4(0.15f, 0.8f, 1, 1)),
            },
        }, family);
        Render("widgets", () =>
        {
            void Column(float ox, Style style)
            {
                Rect(ox, 0, 195, 300, Panel, 10);
                uint Add(ushort type, float x, float y, float w = 0, float h = 0)
                {
                    var id = next++;
                    cmd.Create(id, type);
                    StyleWriter.Write(cmd, id, style, null);
                    cmd.SetPlace(id, ox + x, y, w, h);
                    cmd.Reparent(id, 0, order++);
                    return id;
                }
                cmd.SetString(Add(Protocol.Type.Button, 15, 20), Protocol.Prop.Text, "Button");
                var b = Add(Protocol.Type.Button, 15, 70);
                cmd.SetString(b, Protocol.Prop.Text, "Off");
                cmd.SetBool(b, Protocol.Prop.Enabled, false);
                cmd.SetBool(Add(Protocol.Type.CheckBox, 120, 24), Protocol.Prop.Value, true);
                cmd.SetBool(Add(Protocol.Type.Switch, 120, 74), Protocol.Prop.Value, true);
                cmd.SetF32(Add(Protocol.Type.Slider, 15, 140, 165, 30), Protocol.Prop.Value, 0.65f);
                cmd.SetF32(Add(Protocol.Type.Slider, 15, 190, 165, 30), Protocol.Prop.Value, 0.2f);
            }
            Column(0, baseStyle);
            Column(205, custom);
        });

        var asset = ImageTest.FindAsset();
        s.Check(asset != null, "reference-image: stripes asset found");
        if (asset != null)
        {
            var stripes = ImageTest.Load(asset, s.Check, "stripes");
            Render("image", () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    var y = i * 75;
                    Rect(0, y, 400, 70, Panel, 8);
                    var id = next++;
                    cmd.Create(id, Protocol.Type.Image);
                    cmd.SetPlace(id, 20, y + 8, 360, 54);
                    cmd.SetU64(id, Protocol.Prop.Image, stripes);
                    cmd.SetI32(id, Protocol.Prop.FillMode, i);
                    cmd.SetBool(id, Protocol.Prop.Smooth, true);
                    cmd.Reparent(id, 0, order++);
                }
            });
        }

        Render("group", () =>
        {
            // Three groups side by side; each holds an orange and a cyan rectangle that overlap and stick out of the 100 x 100 frame.
            void Group(float x, float opacity, bool clip)
            {
                Rect(x, 20, 120, 120, Panel, 10);
                var g = next++;
                cmd.Create(g, Protocol.Type.Item);
                cmd.SetPlace(g, x + 10, 30, 100, 100);
                if (opacity != 1) cmd.SetF32(g, Protocol.Prop.Opacity, opacity);
                if (clip) cmd.SetBool(g, Protocol.Prop.Clip, true);
                cmd.Reparent(g, 0, order++);
                var a = Rect(0, 0, 80, 80, Orange, 12, g);
                cmd.SetPlace(a, -20, 10, 80, 80);
                var b = Rect(0, 0, 80, 80, Cyan, 12, g);
                cmd.SetPlace(b, 40, 40, 80, 80);
            }
            Group(5, 0.5f, false);
            Group(140, 1, true);
            Group(275, 0.5f, true);
            // Below: the same overlap without a group, rectangles with their own opacity (the overlap shows through).
            Rect(5, 160, 120, 120, Panel, 10);
            Rect(15, 180, 80, 80, Orange, 12, 0, 0.5f);
            Rect(60, 200, 80, 80, Cyan, 12, 0, 0.5f);
        });
    }
}
