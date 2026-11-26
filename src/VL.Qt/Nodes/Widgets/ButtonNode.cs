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
    /// Push button that reports each click as Bang and into the On Click channel. A click is a release over the button.
    /// Connect to Renderer [Qt] or a Group.
    /// </summary>
    /// <remarks>QtQuick.Controls Button, style Basic.</remarks>
    [ProcessNode(Name = "Button", Category = Categories.Widgets)]
    public sealed class ButtonNode : IDisposable
    {
        internal readonly record struct Props(string Text, bool Enabled, bool AutoRepeat, bool AutoSize, Style? Style);

        private readonly ElementNodeCore<Button, Props> _node;

        public ButtonNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Button, Props>(nodeContext, s_create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ButtonNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Button, Props>(addWarning, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Button> s_create = static (c, p, slot) => new Button(c, slot)
            {
                Style = p.Style,
                AutoSize = p.AutoSize,
                Text = p.Text,
                Enabled = p.Enabled,
                AutoRepeat = p.AutoRepeat,
            };

        internal ElementNodeCore<Button, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='autosize']/param[@name='size']"/>
        /// <param name="autoSize">On: Size is ignored and the implicit size applies (text + padding of the style). Off: Size counts.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="onClick">Channel that receives one value per click (author "Qt"). The clicks come from the button itself, no
        /// Interaction node is needed.</param>
        /// <param name="text">Label of the button.</param>
        /// <param name="enabled">False greys the button out; it then sends no clicks.</param>
        /// <param name="autoRepeat">Repeats the click while the button is held down.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='widget']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <param name="style">Look of the widget. Unset fields come from the enclosing Group's style, else from the base style.</param>
        /// <param name="bang">True for one frame when the button was clicked since the last frame.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 0.4")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue(null)] IChannel<Unit>? onClick,
            [DefaultValue("Button")] string text,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] bool autoRepeat,
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
            // Clicks come from Qt clicked (EVENT_CLICK for every bound Button).
            bang = ClickChannel.Update(onClick, _node.Feedback) > 0;
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(text ?? string.Empty, enabled, autoRepeat, autoSize, style),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
