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
    /// <summary>
    /// Integer input with +/- buttons (QtQuick.Controls SpinBox, style Basic). Connect to Renderer [Qt] or a Group.
    /// +/-, arrow keys and the mouse wheel change the value at once; a typed value counts on Enter or focus loss.
    /// The value goes into Channel (author "Qt") and to Value; Bang reports a change by the user.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Input (Int)", Category = Categories.Widgets)]
    public sealed class InputIntNode : IDisposable
    {
        internal readonly record struct Props(int Value, int Minimum, int Maximum, int Step, bool Editable, bool Enabled, bool AutoSize, Style? Style);

        readonly ElementNode<SpinBox, Props> _node;
        readonly ValueChannel<int> _value = new(0);

        public InputIntNode(NodeContext nodeContext)
            => _node = new ElementNode<SpinBox, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal InputIntNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<SpinBox, Props>(addWarning, Create);

        static readonly Func<CommonInputs, Props, SinkSlot, SpinBox> Create = static (c, p, slot) => new SpinBox
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
                Minimum = p.Minimum,
                Maximum = p.Maximum,
                Step = p.Step,
                Editable = p.Editable,
                Enabled = p.Enabled,
            };

        internal ElementNode<SpinBox, Props> Core => _node;

        /// <summary>Describes the input. A new description is only created when an input or the value changes.</summary>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies. Off: Size counts.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="channel">Channel of the value. Changes from the patch are shown; changes by the user are written into it (author "Qt"). A plain value or an IOBox on the pin acts as start value and sets the value whenever it changes. If the patch and the user change the value in the same frame, the user wins.</param>
        /// <param name="min">Lower bound. Without a value: the Min attribute of the channel, if any, else the smallest int.</param>
        /// <param name="max">Upper bound. Without a value: the Max attribute of the channel, if any, else the largest int.</param>
        /// <param name="step">Step of +/-, the arrow keys and the mouse wheel.</param>
        /// <param name="editable">The value can be typed (it counts on Enter or focus loss).</param>
        /// <param name="enabled">False greys the input out; it then takes no input.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only. Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), magenta cross = origin, yellow diamond = anchor.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). The input takes its input itself in any case; the chain adds hover, press and pointer handling.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The value shown, clamped to Min…Max (the channel keeps the patch's value). Works without a channel too.</param>
        /// <param name="bang">True for one frame when the user changed the value since the last frame.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units.</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue("1.4, 0.4")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<int>? channel,
            [Pin(Visibility = PinVisibility.Optional)] Optional<int> min,
            [Pin(Visibility = PinVisibility.Optional)] Optional<int> max,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1)] int step,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool editable,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            out int value,
            out bool bang,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            _value.Update(channel, _node.Sink);
            int minimum = _value.Min(min, int.MinValue);
            int maximum = _value.Max(max, int.MaxValue);
            value = _value.Clamp(minimum, maximum);
            bang = _value.Bang;
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, minimum, maximum, step, editable, enabled, autoSize, style));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
