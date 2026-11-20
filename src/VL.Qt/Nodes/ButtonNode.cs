using System;
using System.ComponentModel;
using System.Reactive;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Push button (QtQuick.Controls Button, style Basic). Connect to Renderer [Qt] or a Group.
    /// A click is a release over the button (Qt clicked); it is reported by Bang and Is Clicked.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Button", Category = Categories.Widgets)]
    public sealed class ButtonNode : IDisposable
    {
        internal readonly record struct Props(string Text, bool Enabled, bool AutoRepeat, bool AutoSize, Style? Style);

        private readonly ElementNode<Button, Props> _node;

        public ButtonNode(NodeContext nodeContext)
            => _node = new ElementNode<Button, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ButtonNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<Button, Props>(addWarning, Create);

        private static readonly Func<CommonInputs, Props, SinkSlot, Button> Create = static (c, p, slot) => new Button
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Clip = c.Clip,
                Debug = c.Debug,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Style = p.Style,
                Sink = slot,
                AutoSize = p.AutoSize,
                Text = p.Text,
                Enabled = p.Enabled,
                AutoRepeat = p.AutoRepeat,
            };

        internal ElementNode<Button, Props> Core => _node;

        /// <summary>Describes the button. A new description is only created when an input changes.</summary>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the implicit size applies (text + padding of the style). Off: Size counts.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="text">Label of the button.</param>
        /// <param name="enabled">False greys the button out; it then sends no clicks.</param>
        /// <param name="autoRepeat">Repeats the click while the button is held down.</param>
        /// <param name="isClicked">Channel: receives one value per click (author "Qt"). The clicks come from the button itself (Qt clicked), no Interaction node is needed.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only. Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), magenta cross = origin, yellow diamond = anchor.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). The button takes clicks itself in any case; the chain adds hover, press and pointer handling.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="bang">True for one frame when the button was clicked since the last frame.</param>
        /// <param name="measuredSize">Implicit size reported by Qt (text + padding of the style), in units.</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue("1, 0.4")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue("Button")] string text,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] bool autoRepeat,
            [DefaultValue(null)] IChannel<Unit>? isClicked,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            out bool bang,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            // Clicks come from Qt clicked (EV_CLICK for every bound Button).
            bang = _node.TakeClicks(isClicked) > 0;
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(text ?? string.Empty, enabled, autoRepeat, autoSize, style));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
