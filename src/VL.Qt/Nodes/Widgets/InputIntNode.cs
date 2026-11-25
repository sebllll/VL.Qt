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
    /// Integer input with +/- buttons. +/-, the arrow keys and the mouse wheel change the value at once; a typed value
    /// counts on Enter or focus loss. The value goes into Channel (author "Qt") and to Value; Has Changed reports a change
    /// by the user. Connect to Renderer [Qt] or a Group.
    /// </summary>
    /// <remarks>QtQuick.Controls SpinBox, style Basic.</remarks>
    [ProcessNode(Name = "Input (Int)", Category = Categories.Widgets)]
    public sealed class InputIntNode : IDisposable
    {
        internal readonly record struct Props(int Value, int Minimum, int Maximum, int Step, bool Editable, bool Enabled, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<SpinBox, Props> _node;
        private readonly ValueChannel<int> _value = new(0);

        public InputIntNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<SpinBox, Props>(nodeContext, s_create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal InputIntNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<SpinBox, Props>(addWarning, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, SpinBox> s_create = static (c, p, slot) => new SpinBox(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Minimum = p.Minimum,
                Maximum = p.Maximum,
                Step = p.Step,
                Editable = p.Editable,
                Enabled = p.Enabled,
            };

        internal ElementNodeCore<SpinBox, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies. Off: Size counts.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='channel']"/>
        /// <param name="min">Lower bound. Without a value: the Min attribute of the channel, if any, else the smallest int.</param>
        /// <param name="max">Upper bound. Without a value: the Max attribute of the channel, if any, else the largest int.</param>
        /// <param name="step">Step of +/-, the arrow keys and the mouse wheel.</param>
        /// <param name="editable">The value can be typed (it counts on Enter or focus loss).</param>
        /// <param name="enabled">False greys the input out; it then takes no input.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The value shown, clamped to Min…Max (the channel keeps the patch's value). Works without a channel too.</param>
        /// <param name="hasChanged">True for one frame when the user changed the value since the last frame.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1.4, 0.4")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<int>? channel,
            Optional<int> min,
            Optional<int> max,
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
            out bool hasChanged,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            _value.Update(channel, _node.Feedback);
            int minimum = _value.Min(min, int.MinValue);
            int maximum = _value.Max(max, int.MaxValue);
            value = _value.Clamped(minimum, maximum);
            hasChanged = _value.Bang;
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, minimum, maximum, step, editable, enabled, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
