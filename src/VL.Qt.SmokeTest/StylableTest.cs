using System;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Styles;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

// Styles S4 (#413): IStylable through the real reconciler. A C#-made widget (mutable class, same reference) builds Rectangle +
// Text from the resolved style. Left Group with a style, right Group without (base style), each with the widget and a built-in
// Button. Group style changed at runtime, a field of the widget changed (same reference, no style change), a throwing
// IStylable next to a sibling. Images <outDir>/stylable.png and stylable2.png.
static class StylableTest
{
    const float BgGray = 0x10 / 255f;

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool Near(Color c, uint rgb) =>
        Math.Abs(c.R - (int)((rgb >> 16) & 0xFF)) < 8 && Math.Abs(c.G - (int)((rgb >> 8) & 0xFF)) < 8 && Math.Abs(c.B - (int)(rgb & 0xFF)) < 8;
    static bool IsBg(Color c) => Near(c, 0x101010);
    static Color4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    static bool HasColor(Bitmap a, int x0, int y0, int x1, int y1, uint rgb)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (Near(Px(a, x, y), rgb))
                    return true;
        return false;
    }

    static StateValues<T> All<T>(T value) => default(StateValues<T>).With(StyleStates.All, value);

    static Style GroupStyle(uint frame, uint border, uint text, float radius) => new()
    {
        Frame = new FrameStyle { Color = All(Rgb(frame)), BorderColor = All(Rgb(border)), Radius = All(radius), BorderWidth = All(6f) },
        Text = new TextStyle { Color = All(Rgb(text)), FontWeight = All(700), FontSize = All(24f) },
    };

    // Behaves like a patched widget: mutable, the reference stays; Build caches its element and rebuilds on a style or field change.
    sealed class PatchedWidget : IStylable
    {
        Style? _lastStyle;
        string? _lastText;
        float _lastWidth;
        Element? _element;

        public string Text { get; set; } = "Patched";
        public float Width { get; set; } = 2;
        public int Rebuilds { get; private set; }
        public int Calls { get; private set; }

        public Element? Build(Style style)
        {
            Calls++;
            if (_element is not null && Equals(_lastStyle, style) && _lastText == Text && _lastWidth == Width)
                return _element;
            _lastStyle = style;
            _lastText = Text;
            _lastWidth = Width;
            Rebuilds++;
            var frame = style.Frame;
            var text = style.Text;
            var fill = frame is null ? Color4.White : frame.Color.Get(StyleState.Normal, Color4.White);
            var border = frame is null ? Color4.Black : frame.BorderColor.Get(StyleState.Normal, Color4.Black);
            var radius = frame is null ? 0f : frame.Radius.Get(StyleState.Normal, 0f);
            var borderWidth = frame is null ? 0f : frame.BorderWidth.Get(StyleState.Normal, 0f);
            var textColor = text is null ? Color4.Black : text.Color.Get(StyleState.Normal, Color4.Black);
            var weight = text is null ? 400 : text.FontWeight.Get(StyleState.Normal, 400);
            var size = text is null ? 12f : text.FontSize.Get(StyleState.Normal, 12f);
            _element = new Group
            {
                Size = new Vector2(Width, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 0.2f, 0),
                AutoSize = false,
                Children = ImmutableArray.Create<Element>(
                    new VL.Qt.Elements.Rectangle { Size = new Vector2(Width, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Fill = fill, StrokeColor = border, StrokeWidth = borderWidth / 100, Radius = radius / 100 },
                    new Text { Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.1f, 0.1f, 0), Content = Text, Color = textColor, FontSize = size / 100, Weight = (FontWeight)weight }),
            };
            return _element;
        }
    }

    sealed class Thrower : IStylable
    {
        public Element? Build(Style style) => throw new InvalidOperationException("boom");
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        const uint frame1 = 0xC04000, border1 = 0x00FF00, text1 = 0xFFFF00;
        const uint frame2 = 0x8000C0, border2 = 0x00A0FF, text2 = 0xFF8080;

        var w = Native.vlqt_window_create(800, 600, "VL.Qt stylable test");
        check(w != IntPtr.Zero, "413: stylable window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());

        var leftWidget = new PatchedWidget();
        var rightWidget = new PatchedWidget();
        var thrower = new Thrower();
        var leftSink = new ElementSink();

        ImmutableArray<Element> Kids(IStylable widget, bool withThrower) => ImmutableArray.Create<Element>(
            new StylableElement(widget),
            new Button { Size = new Vector2(2, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 1.0f, 0), AutoSize = false, Text = "Button" },
            withThrower ? new StylableElement(thrower) : (Element)new Group(),
            new VL.Qt.Elements.Rectangle { Size = new Vector2(0.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(2.6f, 2.0f, 0), Fill = Rgb(0xFF00FF) });
        var leftKids = Kids(leftWidget, true);
        var rightKids = Kids(rightWidget, false);

        Element Root(Style groupStyle) => new Group
        { Anchor = RectangleAnchor.TopLeft,
            Children = ImmutableArray.Create<Element>(
                new Group { Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Style = groupStyle, Sink = new SinkSlot(leftSink, 0), Children = leftKids },
                new Group { Size = new Vector2(0, 0), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(4.0f, 0, 0), Children = rightKids }),
        };

        void Frame(Element root, string what, bool clear = false)
        {
            cmd.Reset();
            if (clear)
                cmd.ClearColor(BgGray, BgGray, BgGray, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                check(rc == 0, $"413: stylable apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            pump(5);
        }

        void Probes(Bitmap a, uint frame, uint border, uint text, string when)
        {
            // Left group: widget fill, border (6 px, inside the bounds, straight edge at mid height), text color, built-in Button.
            check(Near(Px(a, 200, 60), frame), $"413: {when}: widget fill follows the group style ({Px(a, 200, 60)})");
            check(Near(Px(a, 22, 45), border), $"413: {when}: widget border color follows the group style ({Px(a, 22, 45)})");
            check(HasColor(a, 28, 22, 140, 66, text), $"413: {when}: widget text color follows the group style");
            check(Near(Px(a, 40, 110), frame), $"413: {when}: built-in Button has the same frame color ({Px(a, 40, 110)})");
            // Throwing IStylable next to the sibling: the sibling shows.
            check(Near(Px(a, 280, 220), 0xFF00FF), $"413: {when}: sibling of the throwing IStylable shown ({Px(a, 280, 220)})");
            // Right group: base style (same frame color as its built-in Button).
            check(Near(Px(a, 440, 110), 0x383838), $"413: {when}: unstyled group: Button with base style ({Px(a, 440, 110)})");
            check(Near(Px(a, 600, 60), 0x383838), $"413: {when}: unstyled group: widget fill = base frame color ({Px(a, 600, 60)})");
            check(!HasColor(a, 428, 22, 540, 66, text), $"413: {when}: unstyled group: widget text not in the group color");
        }

        Frame(Root(GroupStyle(frame1, border1, text1, 14)), "initial", clear: true);
        check(!string.IsNullOrEmpty(leftSink[0].BuildError) && leftSink[0].BuildError!.Contains("boom"), $"413: throwing IStylable: error at the group sink ({leftSink[0].BuildError})");
        using (var a = grab(w, Path.Combine(outDir, "stylable.png")))
            Probes(a, frame1, border1, text1, "initial");

        // Unchanged frames: Build is called every frame, the widget rebuilds nothing.
        var rebuilds = leftWidget.Rebuilds;
        var calls = leftWidget.Calls;
        Frame(Root(GroupStyle(frame1, border1, text1, 14)), "unchanged");
        check(leftWidget.Calls > calls && leftWidget.Rebuilds == rebuilds, $"413: unchanged style: Build called ({leftWidget.Calls - calls}), no rebuild ({leftWidget.Rebuilds - rebuilds})");

        // Group style changed at runtime: the widget follows.
        Frame(Root(GroupStyle(frame2, border2, text2, 4)), "group style changed");
        check(leftWidget.Rebuilds == rebuilds + 1, $"413: group style changed: one rebuild ({leftWidget.Rebuilds - rebuilds})");
        using (var a = grab(w, Path.Combine(outDir, "stylable2.png")))
        {
            Probes(a, frame2, border2, text2, "group style changed");
            check(IsBg(Px(a, 300, 40)), $"413: field change: area right of the widget empty before ({Px(a, 300, 40)})");
        }

        // Field changed on the same reference, style unchanged: wider widget and new text become visible.
        leftWidget.Width = 3.4f;
        leftWidget.Text = "Changed";
        Frame(Root(GroupStyle(frame2, border2, text2, 4)), "field changed");
        using (var a = grab(w, Path.Combine(outDir, "stylable3.png")))
            check(Near(Px(a, 300, 40), frame2), $"413: field change on the same reference becomes visible ({Px(a, 300, 40)})");
        check(leftWidget.Rebuilds == rebuilds + 2, $"413: field change: one more rebuild ({leftWidget.Rebuilds - rebuilds})");

        Native.vlqt_window_destroy(w);
        pump(1);
    }
}
