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
    /// Slider for a number. Dragging, the arrow keys and the mouse wheel change the value at once. The value goes into
    /// Channel (author "Qt") and to Value; Bang reports a change by the user. Connect to Renderer [Qt] or a Group.
    /// </summary>
    /// <remarks>QtQuick.Controls Slider, style Basic.</remarks>
    [ProcessNode(Name = "Slider (Float)", Category = Categories.Widgets)]
    public sealed class SliderNode : IDisposable
    {
        internal readonly record struct Props(float Value, float Minimum, float Maximum, float Step, SliderOrientation Orientation, bool Enabled, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<Slider, Props> _node;
        private readonly ValueChannel<float> _value = new(0f);

        public SliderNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Slider, Props>(nodeContext, s_create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal SliderNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Slider, Props>(addWarning, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Slider> s_create = static (c, p, slot) => new Slider(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Minimum = p.Minimum,
                Maximum = p.Maximum,
                Step = p.Step,
                Orientation = p.Orientation,
                Enabled = p.Enabled,
            };

        internal ElementNodeCore<Slider, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies. Off: Size counts.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='channel']"/>
        /// <param name="min">Lower bound. Without a value: the Min attribute of the channel, if any, else 0.</param>
        /// <param name="max">Upper bound. Without a value: the Max attribute of the channel, if any, else 1.</param>
        /// <param name="step">Step size; 0 = continuous. With a step the value snaps to the grid, also while dragging.</param>
        /// <param name="orientation">Horizontal or vertical (vertical: Min at the bottom).</param>
        /// <param name="enabled">False greys the slider out; it then takes no input.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The value shown, clamped to Min…Max (the channel keeps the patch's value). Works without a channel too.</param>
        /// <param name="bang">True for one frame when the user changed the value since the last frame.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("2, 0.28")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<float>? channel,
            Optional<float> min,
            Optional<float> max,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0f)] float step,
            [DefaultValue(typeof(SliderOrientation), nameof(SliderOrientation.Horizontal))] SliderOrientation orientation,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            out float value,
            out bool bang,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            _value.Update(channel, _node.Feedback);
            float minimum = _value.Min(min, 0f);
            float maximum = _value.Max(max, 1f);
            value = _value.Clamped(minimum, maximum);
            bang = _value.Bang;
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, minimum, maximum, step, orientation, enabled, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
