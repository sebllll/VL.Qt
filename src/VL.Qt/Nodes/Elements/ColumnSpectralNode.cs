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

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Stacks its inputs vertically (Qt Column positioner). Only the size of a child is used, its Transform
    /// position is ignored (Transform stays visual, around the child's Anchor); children of size 0 take no space (children with Opacity 0 keep theirs). Takes all children as one Spread input (for ForEach regions) instead of a pin group.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Column (Spectral)", Category = Categories.Elements)]
    public sealed class ColumnSpectralNode : IDisposable
    {
        private readonly ElementNodeCore<Column, ColumnNode.Props> _node;
        private readonly ChildrenInput _children;

        public ColumnSpectralNode(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<Column, ColumnNode.Props>(nodeContext, ColumnNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ColumnSpectralNode(Func<string, IDisposable> addWarning)
        {
            _node = new ElementNodeCore<Column, ColumnNode.Props>(addWarning, ColumnNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        /// <param name="children">Spread (or any sequence) of child elements, e.g. a Spread of Element or of a self-made widget, top to bottom; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size applies (content + padding). Off: Size counts, also 0.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="spacing">Gap between children.</param>
        /// <param name="padding">Padding on all sides.</param>
        /// <param name="childAlignment">The horizontal alignment of the children.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size the column takes with Auto Size on (content + padding), in units (last frame).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            IEnumerable<IElement?> children,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional)] Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional)] float spacing,
            [Pin(Visibility = PinVisibility.Optional)] float padding,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(HorizontalAlignment), nameof(HorizontalAlignment.Left))] HorizontalAlignment childAlignment,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new ColumnNode.Props(_children.Update(children), spacing, padding, childAlignment, autoSize),
                out measuredSize, out sceneBounds);
        }

        public void Dispose()
        {
            _children.Dispose();
            _node.Dispose();
        }
    }
}
