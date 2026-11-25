using NUnit.Framework;
using System;
using System.Drawing;
using System.Collections.Immutable;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Styles;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

// Styles S2: SET_STYLE through the real QtNative. Base style at a Button (frame color, text unchanged by an override),
// override without CLEAR, CLEAR, Hovered state via the test mouse, error codes (Rectangle -6, wrong value type -7, unknown
// field -6), and the gallery <outDir>/style-gallery.png (all controls with the base style, for the visual check).
[TestFixture, NonParallelizable]
public static class StyleTest
{
    /// <summary>Styles of controls.</summary>
    [Test, Category("style")]
    public static void Style() => Scenario.Run(Run);

    private const uint Btn = 1, Rect = 2;
    private const float BgGray = 0x10 / 255f;

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).
    private static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    private static bool Near(Color c, uint rgb) =>
        Math.Abs(c.R - (int)((rgb >> 16) & 0xFF)) < 8 && Math.Abs(c.G - (int)((rgb >> 8) & 0xFF)) < 8 && Math.Abs(c.B - (int)(rgb & 0xFF)) < 8;
    private static bool IsBg(Color c) => Near(c, 0x101010);

    // Any pixel in the logical rectangle at least this bright (text of the Button).
    private static bool HasBright(Bitmap a, int x0, int y0, int x1, int y1, int min)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = Px(a, x, y);
                if (c.R >= min && c.G >= min && c.B >= min)
                    return true;
            }
        return false;
    }

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt style test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        Native.Result Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Clear();
        }
        void Mouse(int type, float x, float y) => s.Check(NativeTest.vlqt_test_send_mouse(w, type, x, y) == 0, $"test_send_mouse {type}");
        void ExpectRc(Native.Result expected, string what)
        {
            var rc = Apply();
            s.Check(rc == expected, $"{what} -> {expected} ({rc}: {Native.LastError()})");
            cmd.Clear();
        }

        // Button 200 x 60 at (100, 100), fixed size; the probe point (110, 110) lies in the frame, away from the text.
        cmd.Clear();
        cmd.ClearColor(BgGray, BgGray, BgGray, 1);
        cmd.Create(Btn, Protocol.ElementType.Button);
        cmd.SetBool(Btn, Protocol.PropertyId.AutoSize, false);
        cmd.SetPlace(Btn, 100, 100, 200, 60);
        cmd.SetString(Btn, Protocol.PropertyId.Text, "WWWWWW");
        cmd.Reparent(Btn, 0, 0);
        cmd.Create(Rect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(Rect, 500, 100, 100, 100);
        cmd.Reparent(Rect, 0, 1);
        CheckApply("create button + rectangle");
        Mouse(TestProtocol.Mouse.Move, 790, 590);
        s.Pump(5);

        // Without a style (CREATE only): no frame (transparent), nothing but the background.
        using (var a = s.Grab(w, "style0.png"))
            s.Check(IsBg(Px(a, 110, 110)), $"button without style: frame transparent ({Px(a, 110, 110)})");

        // (a) Base style: Normal frame color 0x383838, text (0xE0E0E0) visible.
        BaseStyle.Write(cmd, Btn);
        CheckApply("base style");
        s.Pump(5);
        using (var a = s.Grab(w, "style1.png"))
        {
            s.Check(Near(Px(a, 110, 110), 0x383838), $"base style: frame color Normal ({Px(a, 110, 110)})");
            s.Check(HasBright(a, 130, 105, 270, 155, 0xC0), "base style: text drawn bright");
        }

        // (b) Override without CLEAR: only FRAME_COLOR Normal red, the rest stays (text color unchanged).
        cmd.BeginStyle(Btn, 0);
        cmd.StyleColor(Protocol.StyleField.FrameColor, Protocol.StyleState.Normal, 1, 0, 0, 1);
        cmd.EndStyle();
        CheckApply("override frame color red");
        s.Pump(5);
        using (var a = s.Grab(w, "style2.png"))
        {
            s.Check(Near(Px(a, 110, 110), 0xFF0000), $"override: frame red ({Px(a, 110, 110)})");
            s.Check(HasBright(a, 130, 105, 270, 155, 0xC0), "override: text color unchanged");
        }

        // (c) CLEAR with an empty table: back to transparent / no text.
        cmd.BeginStyle(Btn, Protocol.StyleSet.Clear);
        cmd.EndStyle();
        CheckApply("clear");
        s.Pump(5);
        using (var a = s.Grab(w, "style3.png"))
        {
            s.Check(IsBg(Px(a, 110, 110)), $"clear: frame transparent again ({Px(a, 110, 110)})");
            s.Check(!HasBright(a, 130, 105, 270, 155, 0xC0), "clear: no text color (transparent)");
        }

        // (d) Hover: Hovered frame color 0x454545 (the base style sets Normal + Hovered).
        BaseStyle.Write(cmd, Btn);
        CheckApply("base style again");
        Mouse(TestProtocol.Mouse.Move, 105, 105);
        s.Pump(5);
        using (var a = s.Grab(w, "style4.png"))
            s.Check(Near(Px(a, 110, 110), 0x454545), $"hover: frame color Hovered ({Px(a, 110, 110)})");
        Mouse(TestProtocol.Mouse.Move, 790, 590);
        s.Pump(5);
        using (var a = s.Grab(w, "style5.png"))
            s.Check(Near(Px(a, 110, 110), 0x383838), $"mouse away: frame color Normal again ({Px(a, 110, 110)})");

        // (e) Error codes: a Rectangle takes no style, a field has a fixed value type, unknown field.
        cmd.BeginStyle(Rect, 0);
        cmd.StyleColor(Protocol.StyleField.FrameColor, Protocol.StyleState.Normal, 1, 0, 0, 1);
        cmd.EndStyle();
        ExpectRc(Native.Result.UnknownProperty, "SET_STYLE at a Rectangle");
        cmd.BeginStyle(Btn, 0);
        cmd.StyleF32(Protocol.StyleField.FrameColor, Protocol.StyleState.Normal, 1);
        cmd.EndStyle();
        ExpectRc(Native.Result.BadArgument, "SET_STYLE wrong value type (f32 for a color field)");
        cmd.BeginStyle(Btn, 0);
        cmd.StyleF32(999, Protocol.StyleState.Normal, 1);
        cmd.EndStyle();
        ExpectRc(Native.Result.UnknownProperty, "SET_STYLE unknown field");
        s.Pump(3);
        using (var a = s.Grab(w, "style6.png"))
            s.Check(Near(Px(a, 110, 110), 0x383838), $"failed SET_STYLE left the style alone ({Px(a, 110, 110)})");

        cmd.Destroy(Btn);
        cmd.Destroy(Rect);
        CheckApply("destroy");
        Native.vlqt_scene_destroy(w);
        s.Pump(1);

        Gallery(s);
        Inherit(s);
        Nodeset(s);
    }

    private static Color4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    private static Style GroupStyle(uint frame, uint indicator, uint fill, float radius) => new()
    {
        Frame = new FrameStyle
        {
            Color = default(StateValues<Color4>).With(StyleStates.All, Rgb(frame)), // Normal and Hovered alike, so a stray hover does not matter
            Radius = default(StateValues<float>).With(StyleStates.All, radius),
        },
        Indicator = new IndicatorStyle { Color = default(StateValues<Color4>).With(StyleStates.All, Rgb(indicator)) },
        Track = new TrackStyle { FillColor = default(StateValues<Color4>).With(StyleStates.All, Rgb(fill)) },
    };

    // Styles S3: inheritance through the real reconciler. Left Group with its own style (children without style inherit it,
    // the second Button overrides the frame color), right Group without a style (base style) for comparison.
    // Images <outDir>/style-inherit.png, then the Group style is replaced by a new instance → style-inherit2.png.
    private static void Inherit(Scenario s)
    {
        const uint groupFrame = 0xC04000, groupIndicator = 0x00C0C0, groupFill = 0xC0C000, ownFrame = 0x2040E0;
        const uint frame2 = 0x8000C0, indicator2 = 0xE000E0, fill2 = 0x00A000;

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt style inherit");
        s.Check(w != SceneHandle.Null, "inherit window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());

        // Positions in units (1 unit = 100 DIP), relative to the group.
        var ownStyle = new Style { Frame = new FrameStyle { Color = default(StateValues<Color4>).With(StyleStates.All, Rgb(ownFrame)) } };
        ImmutableArray<Element> Kids(Style? buttonBStyle) => ImmutableArray.Create<Element>(
            new Button { Size = new Vector2(2, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 0.2f, 0), AutoSize = false, Text = "Button" },
            new Button { Size = new Vector2(1.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(2.4f, 0.2f, 0), AutoSize = false, Text = "Own", Style = buttonBStyle },
            new CheckBox { Size = new Vector2(0.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 1.0f, 0), AutoSize = false, Value = true },
            new Switch { Size = new Vector2(0.8f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1.0f, 1.0f, 0), AutoSize = false, Value = true },
            new Slider { Size = new Vector2(3.5f, 0.3f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 1.8f, 0), AutoSize = false, Value = 0.5f },
            new SpinBox { Size = new Vector2(2, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 2.4f, 0), AutoSize = false, Value = 42, Minimum = 0, Maximum = 100 },
            new TextField { Size = new Vector2(3, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 3.1f, 0), AutoSize = false, Value = "Some text" });
        var styledKids = Kids(ownStyle);
        var plainKids = Kids(null);

        Element Root(Style groupStyle) => new Group
        { Anchor = RectangleAnchor.TopLeft,
            Children = ImmutableArray.Create<Element>(
                new Group { Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Style = groupStyle, Children = styledKids },
                new Group { Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(4.0f, 0, 0), Children = plainKids }),
        };

        void Frame(Element root, string what, bool clear = false)
        {
            cmd.Clear();
            if (clear)
                cmd.ClearColor(BgGray, BgGray, BgGray, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"inherit apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(5);
        }

        void Probes(Bitmap a, uint frame, uint indicator, uint fill, string when)
        {
            // Left group (x offset 0).
            s.Check(Near(Px(a, 40, 24), frame), $"{when}: Button inherits the group frame color ({Px(a, 40, 24)})");
            s.Check(Near(Px(a, 260, 24), ownFrame), $"{when}: Button with own style keeps its own frame color ({Px(a, 260, 24)})");
            s.Check(Near(Px(a, 80, 195), fill), $"{when}: Slider fill = group TrackFill ({Px(a, 80, 195)})");
            s.Check(Near(Px(a, 195, 195), indicator), $"{when}: Slider handle = group Indicator ({Px(a, 195, 195)})");
            s.Check(Near(Px(a, 100, 244), frame), $"{when}: SpinBox frame = group frame ({Px(a, 100, 244)})");
            s.Check(Near(Px(a, 300, 314), frame), $"{when}: TextField frame = group frame ({Px(a, 300, 314)})");
            // Right group (x offset 400): base style.
            s.Check(Near(Px(a, 460, 24), 0x383838), $"{when}: unstyled Group → Button with base style ({Px(a, 460, 24)})");
            s.Check(!Near(Px(a, 600, 314), frame), $"{when}: unstyled Group → TextField not in the group color ({Px(a, 600, 314)})");
            s.Check(!Near(Px(a, 500, 195), fill) && !Near(Px(a, 615, 195), indicator), $"{when}: unstyled Group → Slider not in the group colors ({Px(a, 500, 195)}, {Px(a, 615, 195)})");
        }

        Frame(Root(GroupStyle(groupFrame, groupIndicator, groupFill, 14)), "inherit", clear: true);
        s.Pump(5);
        using (var a = s.Grab(w, "style-inherit.png"))
            Probes(a, groupFrame, groupIndicator, groupFill, "inherit");

        // New Group style instance at runtime: the children recolor, the own style of Button B stays.
        Frame(Root(GroupStyle(frame2, indicator2, fill2, 4)), "group style changed");
        using (var a = s.Grab(w, "style-inherit2.png"))
            Probes(a, frame2, indicator2, fill2, "group style changed");

        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }

    // Styles S5: the same inheritance, but the styles are built only with the nodeset methods. Group style: Colors
    // (frame, track fill) + Frame radius; the second Button overrides only the frame color with FrameNodes.SetColor.
    // Image <outDir>/style-nodeset.png.
    private static void Nodeset(Scenario s)
    {
        const uint groupFrame = 0xC04000, groupFill = 0xC0C000, ownFrame = 0x2040E0;

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt style nodeset");
        s.Check(w != SceneHandle.Null, "nodeset window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());

        var groupColors = new SetColorsNode().Update(null, new(Rgb(groupFrame)), default, default, default, default, new(Rgb(groupFill)), StyleStates.All);
        var groupStyle = new SetFrameNode().Update(groupColors, new(14f), default, default, default, default, default, StyleStates.All);
        var ownStyle = FrameNodes.SetColor(null, new(Rgb(ownFrame)));

        var root = new Group
        { Anchor = RectangleAnchor.TopLeft,
            Children = ImmutableArray.Create<Element>(
                new Group
                {
                    Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0),
                    Style = groupStyle,
                    Children = ImmutableArray.Create<Element>(
                        new Button { Size = new Vector2(2, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 0.2f, 0), AutoSize = false, Text = "Button" },
                        new Button { Size = new Vector2(1.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(2.4f, 0.2f, 0), AutoSize = false, Text = "Own", Style = ownStyle },
                        new Switch { Size = new Vector2(0.8f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1.0f, 1.0f, 0), AutoSize = false, Value = true },
                        new Slider { Size = new Vector2(3.5f, 0.3f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 1.8f, 0), AutoSize = false, Value = 0.5f },
                        new SpinBox { Size = new Vector2(2, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 2.5f, 0), AutoSize = false, Value = 42, Minimum = 0, Maximum = 100 }),
                }),
        };

        cmd.Clear();
        cmd.ClearColor(BgGray, BgGray, BgGray, 1);
        rec.Update(root, Space.DIPTopLeft);
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"nodeset apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(10);

        var path = Path.Combine(s.OutDir, "style-nodeset.png");
        using (var a = s.Grab(w, path))
        {
            s.Check(File.Exists(path), $"nodeset image written to {path}");
            s.Check(Near(Px(a, 40, 24), groupFrame), $"Button inherits the frame color from Colors ({Px(a, 40, 24)})");
            s.Check(Near(Px(a, 260, 24), ownFrame), $"Button with FrameNodes.SetColor keeps its own frame color ({Px(a, 260, 24)})");
            s.Check(Near(Px(a, 80, 195), groupFill), $"Slider fill = Colors TrackFill ({Px(a, 80, 195)})");
            // frame radius 14: the + button follows the rounded outer corners (corner pixel = background, not button color).
            s.Check(Near(Px(a, 218, 252), 0x101010) && Near(Px(a, 218, 298), 0x101010), $"SpinBox + button stays inside the rounded corners ({Px(a, 218, 252)}, {Px(a, 218, 298)})");
        }

        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }

    // All controls with the base style in a grid on a dark background; hover on the first button. For Sebastian's visual check.
    private static void Gallery(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt style gallery");
        s.Check(w != SceneHandle.Null, "gallery window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        uint next = 1;
        uint Add(ushort type, float x, float y, float width, float height, bool enabled = true)
        {
            var id = next++;
            cmd.Create(id, type);
            BaseStyle.Write(cmd, id);
            cmd.SetPlace(id, x, y, width, height); // 0 = implicit size
            if (!enabled)
                cmd.SetBool(id, Protocol.PropertyId.Enabled, false);
            cmd.Reparent(id, 0, id);
            return id;
        }

        cmd.Clear();
        cmd.ClearColor(0x18 / 255f, 0x18 / 255f, 0x18 / 255f, 1);
        const float c0 = 30, c1 = 230, c2 = 430, c3 = 630;
        float y = 30;
        // Row 1: Button (hovered), Button, Button disabled, SpinBox.
        var hover = Add(Protocol.ElementType.Button, c0, y, 0, 0);
        cmd.SetString(hover, Protocol.PropertyId.Text, "Hovered");
        var b = Add(Protocol.ElementType.Button, c1, y, 0, 0);
        cmd.SetString(b, Protocol.PropertyId.Text, "Button");
        b = Add(Protocol.ElementType.Button, c2, y, 0, 0, enabled: false);
        cmd.SetString(b, Protocol.PropertyId.Text, "Disabled");
        var spin = Add(Protocol.ElementType.SpinBox, c3, y, 0, 0);
        cmd.SetI32(spin, Protocol.PropertyId.Value, 42);
        cmd.SetI32(spin, Protocol.PropertyId.Minimum, 0);
        cmd.SetI32(spin, Protocol.PropertyId.Maximum, 100);
        y += 70;
        // Row 2: CheckBox off / on / disabled, Switch off / on / disabled, SpinBox disabled.
        var c = Add(Protocol.ElementType.CheckBox, c0, y, 0, 0);
        cmd.SetBool(c, Protocol.PropertyId.Value, false);
        c = Add(Protocol.ElementType.CheckBox, c0 + 60, y, 0, 0);
        cmd.SetBool(c, Protocol.PropertyId.Value, true);
        c = Add(Protocol.ElementType.CheckBox, c0 + 120, y, 0, 0, enabled: false);
        cmd.SetBool(c, Protocol.PropertyId.Value, true);
        c = Add(Protocol.ElementType.Switch, c1 + 30, y, 0, 0);
        cmd.SetBool(c, Protocol.PropertyId.Value, false);
        c = Add(Protocol.ElementType.Switch, c1 + 120, y, 0, 0);
        cmd.SetBool(c, Protocol.PropertyId.Value, true);
        c = Add(Protocol.ElementType.Switch, c1 + 210, y, 0, 0, enabled: false);
        cmd.SetBool(c, Protocol.PropertyId.Value, true);
        spin = Add(Protocol.ElementType.SpinBox, c3, y, 0, 0, enabled: false);
        cmd.SetI32(spin, Protocol.PropertyId.Value, 7);
        cmd.SetI32(spin, Protocol.PropertyId.Minimum, 0);
        cmd.SetI32(spin, Protocol.PropertyId.Maximum, 100);
        y += 70;
        // Row 3: Slider horizontal 0.3, disabled, vertical (own column).
        var sl = Add(Protocol.ElementType.Slider, c0, y, 300, 30);
        cmd.SetF32(sl, Protocol.PropertyId.Value, 0.3f);
        sl = Add(Protocol.ElementType.Slider, c0, y + 50, 300, 30, enabled: false);
        cmd.SetF32(sl, Protocol.PropertyId.Value, 0.3f);
        sl = Add(Protocol.ElementType.Slider, c3 + 40, y, 30, 200);
        cmd.SetI32(sl, Protocol.PropertyId.Orientation, 2);
        cmd.SetF32(sl, Protocol.PropertyId.Value, 0.3f);
        y += 130;
        // Row 4: TextField with text, with placeholder, disabled.
        var t = Add(Protocol.ElementType.TextField, c0, y, 0, 0);
        cmd.SetString(t, Protocol.PropertyId.Value, "Some text");
        t = Add(Protocol.ElementType.TextField, c0 + 220, y, 0, 0);
        cmd.SetString(t, Protocol.PropertyId.Placeholder, "Placeholder");
        t = Add(Protocol.ElementType.TextField, c0 + 440, y, 0, 0, enabled: false);
        cmd.SetString(t, Protocol.PropertyId.Value, "Disabled");
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"gallery apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        cmd.Clear();
        NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Move, c0 + 10, 40);
        s.Pump(10);
        var path = Path.Combine(s.OutDir, "style-gallery.png");
        using (var a = s.Grab(w, path))
            s.Check(a.Width > 0 && File.Exists(path), $"gallery written to {path}");
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }
}
