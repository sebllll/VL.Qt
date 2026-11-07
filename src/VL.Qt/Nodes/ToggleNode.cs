using System;
using System.ComponentModel;
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
    /// <summary>Look of the <see cref="ToggleNode"/>.</summary>
    public enum ToggleLook
    {
        Checkbox,
        Switch,
    }

    /// <summary>
    /// Toggle without text, as check box or switch (QtQuick.Controls CheckBox/Switch, style Basic). Connect to Renderer [Qt] or a Group.
    /// A click or Space flips the value. The value goes into Channel (author "Qt") and to Value; Bang reports a change by the user.
    /// Changing Look replaces the element by one of the other type; the value is kept.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Toggle", Category = Categories.Widgets)]
    public sealed class ToggleNode : IDisposable
    {
        internal readonly record struct Props(bool Value, ToggleLook Look, bool Enabled, bool AutoSize, Style? Style);

        readonly ElementNode<Element, Props> _node;
        readonly ValueChannel<bool> _value = new(false);

        public ToggleNode(NodeContext nodeContext)
            => _node = new ElementNode<Element, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ToggleNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<Element, Props>(addWarning, Create);

        static readonly Func<CommonInputs, Props, SinkSlot, Element> Create = static (c, p, slot) => p.Look == ToggleLook.Switch
            ? new Switch
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
                Value = p.Value,
                Enabled = p.Enabled,
            }
            : new CheckBox
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
                Value = p.Value,
                Enabled = p.Enabled,
            };

        internal ElementNode<Element, Props> Core => _node;

        /// <summary>Describes the toggle. A new description is only created when an input or the value changes.</summary>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies (check box about 0.4 x 0.4, switch about 0.68 x 0.4). Off: Size counts and the toggle is centered in it.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="channel">Channel of the state. Changes from the patch are shown; changes by the user are written into it (author "Qt"). A plain value or an IOBox on the pin acts as start value and sets the state whenever it changes. If the patch and the user change the state in the same frame, the user wins.</param>
        /// <param name="look">Checkbox or Switch. Changing it replaces the element.</param>
        /// <param name="enabled">False greys the toggle out; it then takes no input.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only. Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), magenta cross = origin, yellow diamond = anchor.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). The toggle takes its input itself in any case; the chain adds hover, press and pointer handling.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The state shown. Works without a channel too.</param>
        /// <param name="bang">True for one frame when the user changed the state since the last frame.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units.</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue("0.68, 0.4")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<bool>? channel,
            [DefaultValue(typeof(ToggleLook), nameof(ToggleLook.Checkbox))] ToggleLook look,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            out bool value,
            out bool bang,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            value = _value.Update(channel, _node.Sink);
            bang = _value.Bang;
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, look, enabled, autoSize, style));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
