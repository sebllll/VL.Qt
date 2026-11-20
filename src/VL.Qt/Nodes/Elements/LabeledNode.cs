using System;
using System.Collections.Immutable;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>Where <see cref="LabeledNode"/> puts the label relative to the element.</summary>
    public enum LabelPosition
    {
        /// <summary>Label to the left of the element, vertically centered.</summary>
        Left,
        /// <summary>Label above the element, left aligned.</summary>
        Top,
    }

    /// <summary>
    /// Gives any element a text label: a Row (label left) or Column (label above) of a Text and the element.
    /// Set the same Label Width on several Labeled nodes and their elements line up. The look of the label
    /// (font, color) is the Text default for now. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Labeled", Category = Categories.Elements)]
    public sealed class LabeledNode : IDisposable
    {
        internal readonly record struct TextProps(string Content, float Width, float Height);
        internal readonly record struct LayoutProps(ImmutableArray<Element> Children, float Spacing);

        private readonly ElementNode<Text, TextProps> _text;
        private readonly ElementNode<Row, LayoutProps> _row;
        private readonly ElementNode<Column, LayoutProps> _column;
        private Element? _lastElement;
        private Text? _lastText;
        private ImmutableArray<Element> _children = ImmutableArray<Element>.Empty;

        public LabeledNode(NodeContext nodeContext)
        {
            _text = new ElementNode<Text, TextProps>(nodeContext, CreateText);
            _row = new ElementNode<Row, LayoutProps>(nodeContext, CreateRow);
            _column = new ElementNode<Column, LayoutProps>(nodeContext, CreateColumn);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal LabeledNode(Func<string, IDisposable> addWarning)
        {
            _text = new ElementNode<Text, TextProps>(addWarning, CreateText);
            _row = new ElementNode<Row, LayoutProps>(addWarning, CreateRow);
            _column = new ElementNode<Column, LayoutProps>(addWarning, CreateColumn);
        }

        // The label box is Width x Height: the text is aligned left and vertically centered in it, so it stays
        // centered next to elements that are taller than the text.
        private static readonly Func<CommonInputs, TextProps, FeedbackSlot, Text> CreateText = static (c, p, slot) => new Text
            {
                Size = new Vector2(p.Width, p.Height),
                Anchor = RectangleAnchor.TopLeft,
                Feedback = slot,
                Content = p.Content,
                Color = Color4.White,
                VAlign = VerticalAlignment.Center,
            };

        private static readonly Func<CommonInputs, LayoutProps, FeedbackSlot, Row> CreateRow = static (c, p, slot) => new Row
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Feedback = slot,
                Children = p.Children,
                Spacing = p.Spacing,
                ChildAlignment = VerticalAlignment.Center,
            };

        private static readonly Func<CommonInputs, LayoutProps, FeedbackSlot, Column> CreateColumn = static (c, p, slot) => new Column
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Feedback = slot,
                Children = p.Children,
                Spacing = p.Spacing,
                ChildAlignment = HorizontalAlignment.Left,
            };

        /// <summary>Describes the label and the layout around it. A new description is only created when an input changes.</summary>
        /// <param name="element">The element to label (Button, Slider, Input, …). Unconnected: only the label is shown.</param>
        /// <param name="text">The label text (plain, no markup).</param>
        /// <param name="labelWidth">Width of the label box. Use the same value on all Labeled nodes of a panel so the elements line up. With the label above the element it only limits the box, not the layout.</param>
        /// <param name="labelPosition">Label left of the element or above it.</param>
        /// <param name="spacing">Gap between label and element.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="labelHeight">Height of the label box; the text is vertically centered in it. Must be at least the text height (12 DIP font: about 0.2).</param>
        /// <param name="opacity">Opacity of label and element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …) for label and element together. Without interaction the label row is transparent for input; the element keeps its own Interaction.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Implicit size reported by Qt (label + spacing + element), in units (last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            Element? element,
            [DefaultValue("Label")] string text,
            [DefaultValue(1.5f)] float labelWidth,
            [DefaultValue(typeof(LabelPosition), nameof(LabelPosition.Left))] LabelPosition labelPosition,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0.1f)] float spacing,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0.2f)] float labelHeight,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var label = _text.Describe(
                new CommonInputs(default, Matrix.Identity, RectangleAnchor.TopLeft, 1, false, false, null, null),
                new TextProps(text ?? string.Empty, labelWidth, labelHeight));
            if (!ReferenceEquals(label, _lastText) || !ReferenceEquals(element, _lastElement))
            {
                _lastText = label;
                _lastElement = element;
                _children = element is null
                    ? ImmutableArray.Create<Element>(label)
                    : ImmutableArray.Create(label, element);
            }

            var common = new CommonInputs(default, transform, anchor, opacity, false, false, interaction, effect);
            var props = new LayoutProps(_children, spacing);
            if (labelPosition == LabelPosition.Top)
            {
                var column = _column.Describe(common, props);
                _column.ReadOutputs(out measuredSize, out sceneBounds);
                return column;
            }
            var row = _row.Describe(common, props);
            _row.ReadOutputs(out measuredSize, out sceneBounds);
            return row;
        }

        public void Dispose()
        {
            _text.Dispose();
            _row.Dispose();
            _column.Dispose();
        }
    }
}
