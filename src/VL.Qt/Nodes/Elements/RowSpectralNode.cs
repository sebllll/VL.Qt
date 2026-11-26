using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Stacks its inputs horizontally (Qt Row positioner). Only the size of a child is used, its Transform
    /// position is ignored (Transform stays visual, around the child's Anchor); children of size 0 take no space (children with Opacity 0
    /// keep theirs). Takes all children as one Spread input (for ForEach regions) instead of a pin group.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Row (Spectral)", Category = Categories.Elements)]
    public sealed class RowSpectralNode : IDisposable
    {
        private readonly ElementNodeCore<Row, RowNode.Props> _node;
        private readonly ChildrenInput _children;

        public RowSpectralNode(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<Row, RowNode.Props>(nodeContext, RowNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        internal RowSpectralNode(NodeErrors messages)
        {
            _node = new ElementNodeCore<Row, RowNode.Props>(messages, RowNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        /// <param name="input">Spread (or any sequence) of child elements, e.g. a Spread of Element or of a self-made widget, left to
        /// right; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size applies (content + padding). Off: Size counts, also 0.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="spacing">Gap between children.</param>
        /// <param name="padding">Padding on all sides.</param>
        /// <param name="childAlignment">The vertical alignment of the children.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='container']/param[@name='style']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size the row takes with Auto Size on (content + padding), in units (last frame).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            IEnumerable<IElement?> input,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
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
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new RowNode.Props(_children.Update(input), spacing, padding, childAlignment, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose()
        {
            _children.Dispose();
            _node.Dispose();
        }
    }
}
