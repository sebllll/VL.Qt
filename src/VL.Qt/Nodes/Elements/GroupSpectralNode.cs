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
    /// Plain container (Qt Item): shows its inputs in order (later ones on top), draws nothing itself.
    /// Takes all children as one Spread input (for ForEach regions) instead of a pin group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Group (Spectral)", Category = Categories.Elements)]
    public sealed class GroupSpectralNode : IDisposable
    {
        private readonly ElementNodeCore<Group, GroupNode.Props> _node;
        private readonly ChildrenInput _children;

        public GroupSpectralNode(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<Group, GroupNode.Props>(nodeContext, GroupNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal GroupSpectralNode(Func<string, IDisposable> addWarning)
        {
            _node = new ElementNodeCore<Group, GroupNode.Props>(addWarning, GroupNode.Create);
            _children = new ChildrenInput(_node.Messages);
        }

        /// <param name="children">Spread (or any sequence) of child elements, e.g. a Spread of Element or of a self-made widget; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the frame is the extent of the children (incl. their Transform) and of the group origin; it is the size, hit area and clip rect of the group. Off: Size counts, also 0.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Style passed on to the widgets inside (field by field; a widget's own style overrides it). The group itself draws nothing.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size of the group's frame, in units (last frame; see Auto Size).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            IEnumerable<IElement?> children,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional)] Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
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
                new GroupNode.Props(_children.Update(children), autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose()
        {
            _children.Dispose();
            _node.Dispose();
        }
    }
}
