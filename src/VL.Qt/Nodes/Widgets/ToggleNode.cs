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
    /// Toggle without text, as check box or switch. A click or Space flips the value. The value goes into Channel
    /// (author "Qt") and to Value; Has Changed reports a change by the user. Changing Look replaces the element by one of the
    /// other type; the value is kept. Connect to Renderer [Qt] or a Group.
    /// </summary>
    /// <remarks>QtQuick.Controls CheckBox or Switch, style Basic.</remarks>
    [ProcessNode(Name = "Toggle", Category = Categories.Widgets)]
    public sealed class ToggleNode : IDisposable
    {
        internal readonly record struct Props(bool Value, ToggleLook Look, bool Enabled, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<Element, Props> _node;
        private readonly ValueChannel<bool> _value = new(false);

        public ToggleNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Element, Props>(nodeContext, s_create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ToggleNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Element, Props>(addWarning, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Element> s_create = static (c, p, slot) => p.Look == ToggleLook.Switch
            ? new Switch(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Enabled = p.Enabled,
            }
            : new CheckBox(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Enabled = p.Enabled,
            };

        internal ElementNodeCore<Element, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies (check box about 0.4 x 0.4, switch about 0.68 x 0.4). Off: Size counts and the toggle is centered in it.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='channel']"/>
        /// <param name="look">Checkbox or Switch. Changing it replaces the element.</param>
        /// <param name="enabled">False greys the toggle out; it then takes no input.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The state shown. Works without a channel too.</param>
        /// <param name="hasChanged">True for one frame when the user changed the state since the last frame.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("0.68, 0.4")] Vector2 size,
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
            out bool hasChanged,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            value = _value.Update(channel, _node.Feedback);
            hasChanged = _value.Bang;
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, look, enabled, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
