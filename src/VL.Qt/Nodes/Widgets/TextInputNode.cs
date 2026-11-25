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
    /// Single-line text input. The text counts on Enter or focus loss, and only if it changed. It goes into Channel
    /// (author "Qt") and to Value; Bang reports a change by the user. A null string counts as empty.
    /// Connect to Renderer [Qt] or a Group.
    /// </summary>
    /// <remarks>QtQuick.Controls TextField, style Basic.</remarks>
    [ProcessNode(Name = "TextInput", Category = Categories.Widgets)]
    public sealed class TextInputNode : IDisposable
    {
        internal readonly record struct Props(string Value, string Placeholder, bool Editable, bool Enabled, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<TextField, Props> _node;
        private readonly ValueChannel<string> _value = new(string.Empty);

        public TextInputNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<TextField, Props>(nodeContext, s_create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal TextInputNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<TextField, Props>(addWarning, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, TextField> s_create = static (c, p, slot) => new TextField(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Placeholder = p.Placeholder,
                Editable = p.Editable,
                Enabled = p.Enabled,
            };

        internal ElementNodeCore<TextField, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies. Off: Size counts.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='channel']"/>
        /// <param name="placeholder">Text shown while the field is empty.</param>
        /// <param name="editable">False makes the field read-only (the text can still be selected).</param>
        /// <param name="enabled">False greys the field out; it then takes no input.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The text shown (never null). Works without a channel too.</param>
        /// <param name="bang">True for one frame when the user changed the text since the last frame.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("2, 0.4")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<string>? channel,
            [DefaultValue("")] string? placeholder,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool editable,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] Style? style,
            out string value,
            out bool bang,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            value = _value.Update(channel, _node.Feedback) ?? string.Empty;
            bang = _value.Bang;
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, placeholder ?? string.Empty, editable, enabled, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
