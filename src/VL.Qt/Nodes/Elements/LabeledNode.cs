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
    /// <summary>
    /// Gives any element a text label: a Row (label left) or Column (label above) of a Text and the element.
    /// Set the same Label Width on several Labeled nodes and their elements line up. The look of the label
    /// (font, color) is the Text default for now. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Labeled", Category = Categories.Elements)]
    public sealed class LabeledNode : IDisposable
    {
        internal readonly record struct TextProps(string Content);
        internal readonly record struct LayoutProps(ImmutableArray<Element> Children, float Spacing);

        private readonly NodeErrors _messages;
        private readonly ElementNodeCore<Text, TextProps> _text;
        private readonly ElementNodeCore<Row, LayoutProps> _row;
        private readonly ElementNodeCore<Column, LayoutProps> _column;
        private readonly ElementInput _element;
        private Element? _lastElement;
        private Text? _lastText;
        private ImmutableArray<Element> _children = ImmutableArray<Element>.Empty;

        public LabeledNode(NodeContext nodeContext)
            : this(new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: the node's message goes to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal LabeledNode(Func<string, IDisposable> addWarning)
            : this(NodeErrors.ShownBy(addWarning))
        {
        }

        // One store for the label, both layouts and the element input: one message at the node.
        private LabeledNode(NodeErrors messages)
        {
            _messages = messages;
            _text = new ElementNodeCore<Text, TextProps>(messages, s_createText, "Label ");
            _row = new ElementNodeCore<Row, LayoutProps>(messages, s_createRow, "Row ");
            _column = new ElementNodeCore<Column, LayoutProps>(messages, s_createColumn, "Column ");
            _element = new ElementInput(messages, "Input");
        }

        // The label box is Label Width x Label Height (the Size of s_label): the text is aligned left and vertically
        // centered in it, so it stays centered next to elements that are taller than the text.
        private static readonly CommonInputs s_label = new(default, Matrix.Identity, RectangleAnchor.TopLeft, 1, false, false, null, null);

        private static readonly Func<CommonInputs, TextProps, FeedbackSlot, Text> s_createText = static (c, p, slot) => new Text(c, slot)
            {
                Content = p.Content,
                Color = Color4.White,
                VAlign = VerticalAlignment.Center,
            };

        private static readonly Func<CommonInputs, LayoutProps, FeedbackSlot, Row> s_createRow = static (c, p, slot) => new Row(c, slot)
            {
                Children = p.Children,
                Spacing = p.Spacing,
                ChildAlignment = VerticalAlignment.Center,
            };

        private static readonly Func<CommonInputs, LayoutProps, FeedbackSlot, Column> s_createColumn = static (c, p, slot) => new Column(c, slot)
            {
                Children = p.Children,
                Spacing = p.Spacing,
                ChildAlignment = HorizontalAlignment.Left,
            };

        /// <param name="input">The element to label (Button, Slider, Input, …). Unconnected: only the label is shown.</param>
        /// <param name="text">The label text (plain, no markup).</param>
        /// <param name="labelPosition">Label left of the element or above it.</param>
        /// <param name="labelWidth">Width of the label box. Use the same value on all Labeled nodes of a panel so the elements line up.
        /// With the label above the element it only limits the box, not the layout.</param>
        /// <param name="labelHeight">Height of the label box; the text is vertically centered in it. Must be at least the text height (12
        /// DIP font: about 0.2).</param>
        /// <param name="spacing">Gap between label and element.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …) for label and element together. Without interaction the
        /// label row is transparent for input; the element keeps its own Interaction.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size the node takes (label + spacing + element), in units (last frame).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            IElement? input,
            [DefaultValue("Label")] string text,
            [DefaultValue(typeof(LabelPosition), nameof(LabelPosition.Left))] LabelPosition labelPosition,
            [DefaultValue(1.5f)] float labelWidth,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0.2f)] float labelHeight,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0.1f)] float spacing,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var shown = _element.Update(input);
            var label = _text.Describe(
                s_label with { Size = new Vector2(labelWidth, labelHeight) },
                new TextProps(text ?? string.Empty),
                out _, out _);
            if (!ReferenceEquals(label, _lastText) || !ReferenceEquals(shown, _lastElement))
            {
                _lastText = label;
                _lastElement = shown;
                _children = shown is null
                    ? ImmutableArray.Create<Element>(label)
                    : ImmutableArray.Create(label, shown);
            }

            var common = new CommonInputs(default, transform, anchor, opacity, clip, debug, interaction, effect, enabled);
            var props = new LayoutProps(_children, spacing);
            // Only the active layout is described: the other one must not keep a message it showed before the switch.
            if (labelPosition == LabelPosition.Top)
            {
                _row.ClearMessages();
                return _column.Describe(common, props, out measuredSize, out sceneBounds);
            }
            _column.ClearMessages();
            return _row.Describe(common, props, out measuredSize, out sceneBounds);
        }

        public void Dispose()
        {
            _text.Dispose();
            _row.Dispose();
            _column.Dispose();
            _element.Dispose();
            _messages.Dispose();
        }
    }
}
