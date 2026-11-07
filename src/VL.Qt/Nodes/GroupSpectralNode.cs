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
    /// Takes all children as one Spread input (for ForEach regions) instead of a pin group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Group (Spectral)", Category = Categories.Elements)]
    public sealed class GroupSpectralNode : IDisposable
    {
        readonly GroupNode _inner;

        public GroupSpectralNode(NodeContext nodeContext) => _inner = new GroupNode(nodeContext);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal GroupSpectralNode(Func<string, IDisposable> addWarning) => _inner = new GroupNode(addWarning);

        internal GroupNode Inner => _inner;

        /// <summary>Describes the group. A new description is only created when an input changes.</summary>
        /// <param name="children">Spread of child elements; self-made widgets (IStylable) are built with the style resolved here. Unconnected inputs are skipped.</param>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the frame is the extent of the children (incl. their Transform) and of the group origin; it is the size, hit area and clip rect of the group. Off: Size counts, also 0.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the group that lies at the origin of its parent space; Transform rotates/scales around it. TopLeft (default): the group origin = the origin of its children, a pure transform container like the Skia group. Center/Right/Bottom resolve against the actual frame (with Auto Size: the extent of the children incl. their transform, which may reach into negative coordinates). Inside Row/Column only the pivot of Transform.</param>
        /// <param name="opacity">Opacity of the group and its children. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="style">Style passed on to the widgets inside (field by field; a widget's own style overrides it). The group itself draws nothing.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units (Group: size of its frame, see Auto Size).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            Spread<IElement?> children,
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
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            return _inner.Update(children, size, autoSize, transform, anchor, opacity, clip, debug, interaction, effect, style, out measuredSize, out sceneBounds);
        }

        public void Dispose() => _inner.Dispose();
    }
}
