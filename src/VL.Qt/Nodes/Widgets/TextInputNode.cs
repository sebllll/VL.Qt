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
    /// Single-line text input (QtQuick.Controls TextField, style Basic). Connect to Renderer [Qt] or a Group.
    /// The text counts on Enter or focus loss, and only if it changed. It goes into Channel (author "Qt") and to Value;
    /// Bang reports a change by the user. A null string counts as empty.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "TextInput", Category = Categories.Widgets)]
    public sealed class TextInputNode : IDisposable
    {
        internal readonly record struct Props(string Value, string Placeholder, bool Editable, bool Enabled, bool AutoSize, Style? Style);

        private readonly ElementNode<TextField, Props> _node;
        private readonly ValueChannel<string> _value = new(string.Empty);

        public TextInputNode(NodeContext nodeContext)
            => _node = new ElementNode<TextField, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal TextInputNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<TextField, Props>(addWarning, Create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, TextField> Create = static (c, p, slot) => new TextField
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
                Feedback = slot,
                AutoSize = p.AutoSize,
                Value = p.Value,
                Placeholder = p.Placeholder,
                Editable = p.Editable,
                Enabled = p.Enabled,
            };

        internal ElementNode<TextField, Props> Core => _node;

        /// <summary>Describes the text input. A new description is only created when an input or the text changes.</summary>
        /// <param name="size">Size of the element (width, height), only with Auto Size off. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: Size is ignored and the implicit size of the style applies. Off: Size counts.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="channel">Channel of the text. Changes from the patch are shown; changes by the user are written into it (author "Qt"). A plain value or an IOBox on the pin acts as start value and sets the text whenever it changes. If the patch and the user change the text in the same frame, the user wins.</param>
        /// <param name="placeholder">Text shown while the field is empty.</param>
        /// <param name="editable">False makes the field read-only (the text can still be selected).</param>
        /// <param name="enabled">False greys the field out; it then takes no input.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only. Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), magenta cross = origin, yellow diamond = anchor.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). The field takes its input itself in any case; the chain adds hover, press and pointer handling.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="value">The text shown (never null). Works without a channel too.</param>
        /// <param name="bang">True for one frame when the user changed the text since the last frame.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units.</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("2, 0.4")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
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
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(value, placeholder ?? string.Empty, editable, enabled, autoSize, style));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
