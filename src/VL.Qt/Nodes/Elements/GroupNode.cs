using System;
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
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Plain container (Qt Item): shows its inputs in order (later ones on top), draws nothing itself.
    /// Ctrl+/Ctrl- adds/removes inputs. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Group", Category = Categories.Elements)]
    public sealed class GroupNode : IDisposable
    {
        internal readonly record struct Props(ImmutableArray<Element> Children, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<Group, Props> _node;
        private readonly ChildrenInput _children;

        public GroupNode(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<Group, Props>(nodeContext, Create);
            _children = new ChildrenInput(_node.Messages);
        }

        internal GroupNode(NodeErrors messages)
        {
            _node = new ElementNodeCore<Group, Props>(messages, Create);
            _children = new ChildrenInput(_node.Messages);
        }

        internal static readonly Func<CommonInputs, Props, FeedbackSlot, Group> Create = static (c, p, slot) => new Group(c, slot)
            {
                Style = p.Style,
                Children = p.Children,
                AutoSize = p.AutoSize,
            };

        internal ElementNodeCore<Group, Props> Core => _node;
        internal ChildrenInput Children => _children;

        /// <param name="children">Child elements; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the frame is the extent of the children (incl. their Transform) and of the group
        /// origin; it is the size, hit area and clip rect of the group. Off: Size counts, also 0.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='container']/param[@name='style']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size of the group's frame, in units (last frame; see Auto Size).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Name = "Input", PinGroupKind = PinGroupKind.Collection, PinGroupDefaultCount = 2)] Spread<IElement?> children,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
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
                new Props(_children.Update(children), autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose()
        {
            _children.Dispose();
            _node.Dispose();
        }
    }
}
