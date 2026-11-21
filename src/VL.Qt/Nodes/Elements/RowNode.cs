using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Stacks its inputs horizontally (Qt Row positioner). Only the size of a child is used, its Transform
    /// position is ignored (Transform stays visual, around the child's Anchor); children of size 0 take no space (children with Opacity 0 keep theirs). Ctrl+/Ctrl- adds/removes inputs.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Row", Category = Categories.Elements)]
    public sealed class RowNode : IDisposable
    {
        internal readonly record struct Props(ImmutableArray<Element> Children, float Spacing, float Padding, VerticalAlignment ChildAlignment, bool AutoSize);

        private readonly ElementNode<Row, Props> _node;
        private readonly ChildrenInput _children;

        public RowNode(NodeContext nodeContext)
        {
            _node = new ElementNode<Row, Props>(nodeContext, Create);
            _children = new ChildrenInput(_node.AddWarning);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal RowNode(Func<string, IDisposable> addWarning)
        {
            _node = new ElementNode<Row, Props>(addWarning, Create);
            _children = new ChildrenInput(_node.AddWarning);
        }

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Row> Create = static (c, p, slot) => new Row
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Clip = c.Clip,
                Debug = c.Debug,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Feedback = slot,
                Children = p.Children,
                Spacing = p.Spacing,
                Padding = p.Padding,
                ChildAlignment = p.ChildAlignment,
                AutoSize = p.AutoSize,
            };

        internal ElementNode<Row, Props> Core => _node;
        internal ChildrenInput Children => _children;

        /// <summary>Describes the row. A new description is only created when an input changes.</summary>
        /// <param name="children">Child elements, left to right; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the implicit size applies (content + padding). Off: Size counts, also 0.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="spacing">Gap between children.</param>
        /// <param name="padding">Padding on all sides.</param>
        /// <param name="childAlignment">The vertical alignment of the children.</param>
        /// <param name="opacity">Opacity of the row and its children. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Implicit size reported by Qt (content + padding), in units (last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Name = "Input", PinGroupKind = PinGroupKind.Collection, PinGroupDefaultCount = 2)] Spread<IElement?> children,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional)] Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional)] float spacing,
            [Pin(Visibility = PinVisibility.Optional)] float padding,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(VerticalAlignment), nameof(VerticalAlignment.Top))] VerticalAlignment childAlignment,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
            => Describe(children, size, autoSize, transform, anchor, spacing, padding, childAlignment, opacity, clip, debug, interaction, effect, out measuredSize, out sceneBounds);

        /// <summary>Update with the children as any sequence (also used by the Spectral node).</summary>
        internal Element Describe(
            IEnumerable<IElement?>? children, Vector2 size, bool autoSize, Matrix transform,
            RectangleAnchor anchor, float spacing, float padding, VerticalAlignment childAlignment,
            float opacity, bool clip, bool debug, Interaction? interaction, Effect? effect,
            out Vector2 measuredSize, out RectangleF sceneBounds)
        {
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(_children.Update(children), spacing, padding, childAlignment, autoSize));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose()
        {
            _children.Dispose();
            _node.Dispose();
        }
    }
}
