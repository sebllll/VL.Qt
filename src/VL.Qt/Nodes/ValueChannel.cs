using System;
using System.Collections.Generic;
using VL.Core;
using VL.Core.EditorAttributes;
using VL.Lib.Collections;
using VL.Lib.Reactive;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Value of a value input element (Input (Int); Toggle, Slider, TextInput) and its optional channel pin,
    /// in the spirit of VL.ImGui's <c>ChannelWidget&lt;T&gt;</c>. Pull, no subscription: <see cref="Update"/> reads
    /// <c>channel.Value</c> once per frame. The channel is the truth of the widget.
    /// <list type="bullet">
    /// <item>Unconnected the pin is null (<c>[DefaultValue(null)] IChannel&lt;T&gt;?</c>); the node then keeps its own
    /// value, changed by the user only. A plain value or an IOBox on the pin is lifted by vvvv into a private channel:
    /// start value and setting from the patch.</item>
    /// <item>A user change (EVENT_VALUE from the feedback) is written into the channel with author <see cref="ChannelAuthors.QtChannelAuthor"/> and sets
    /// <see cref="Bang"/> for one Update. If the patch changed the channel in the same frame, the user wins.</item>
    /// <item>The node describes <see cref="Value"/>; the reconciler diffs it against the last sent description as usual
    /// (no "last sent = user value").</item>
    /// <item>Qt clamps a SET without an event, so what the node shows is the copy <see cref="Clamped"/> returns. The
    /// channel keeps the patch's value, and so does <see cref="Value"/>: when the range is widened the widget shows the
    /// channel's value again.</item>
    /// </list>
    /// Main thread only. No allocation per frame (the attributes of a channel are read once per change).
    /// </summary>
    /// <typeparam name="T">Value type of the element: int, float, bool or string (see <see cref="ElementFeedback.TakeValue{T}"/>).</typeparam>
    internal sealed class ValueChannel<T>
    {
        private IChannel<T>? _channel;
        private T _value;
        private IChannel<Spread<Attribute>>? _attributes;
        private Spread<Attribute>? _lastAttributes;
        private Optional<T> _attributeMin;
        private Optional<T> _attributeMax;

        /// <param name="initial">Value while no channel was seen yet (unconnected pin).</param>
        public ValueChannel(T initial)
        {
            _value = initial;
        }

        /// <summary>The value of the channel (the own value while unconnected), not clamped.</summary>
        public T Value => _value;

        /// <summary>True for one Update after a user change in Qt.</summary>
        public bool Bang { get; private set; }

        /// <summary>
        /// Takes the patch's channel value and the user's change from <paramref name="feedback"/> and returns the new
        /// <see cref="Value"/>. Call once per Update, before <see cref="Clamped"/> and the description.
        /// </summary>
        public T Update(IChannel<T>? channel, ElementFeedback feedback, int index = 0)
        {
            Bang = false;
            if (!ReferenceEquals(channel, _channel))
            {
                _channel = channel;
                _attributes = null;
                _lastAttributes = null;
                _attributeMin = default;
                _attributeMax = default;
            }

            if (feedback.TakeValue<T>(out var user, index))
            {
                // the user wins over a patch change of the same frame: written first, read back below
                _value = user;
                Bang = true;
                if (channel.IsValid())
                    channel.EnsureValue(user, author: ChannelAuthors.QtChannelAuthor);
            }

            if (channel.IsValid())
                _value = channel.Value!;
            return _value;
        }

        /// <summary>
        /// Returns <see cref="Value"/> clamped like Qt does (bounds in any order, as Qt's SpinBox and Slider allow
        /// from &gt; to). Neither <see cref="Value"/> nor the channel is changed.
        /// </summary>
        public T Clamped(T min, T max)
        {
            var c = Comparer<T>.Default;
            var lo = c.Compare(min, max) <= 0 ? min : max;
            var hi = c.Compare(min, max) <= 0 ? max : min;
            return c.Compare(_value, lo) < 0 ? lo : c.Compare(_value, hi) > 0 ? hi : _value;
        }

        /// <summary>Lower bound: the pin if set, else the channel's <see cref="MinAttribute"/>, else <paramref name="fallback"/>.</summary>
        public T Min(Optional<T> pin, T fallback)
        {
            if (pin.HasValue)
                return pin.Value;
            UpdateAttributes();
            return _attributeMin.HasValue ? _attributeMin.Value : fallback;
        }

        /// <summary>Upper bound: the pin if set, else the channel's <see cref="MaxAttribute"/>, else <paramref name="fallback"/>.</summary>
        public T Max(Optional<T> pin, T fallback)
        {
            if (pin.HasValue)
                return pin.Value;
            UpdateAttributes();
            return _attributeMax.HasValue ? _attributeMax.Value : fallback;
        }

        /// <summary>
        /// Reads Min/Max from the attributes of the current channel (as VL.ImGui does), only when a bound pin is unset.
        /// <c>Attributes()</c> may add the attributes component to the channel once; the scan only runs when the
        /// attribute spread changed.
        /// </summary>
        private void UpdateAttributes()
        {
            if (!_channel.IsValid())
                return;
            _attributes ??= _channel.Attributes();
            var attributes = _attributes.Value;
            if (ReferenceEquals(attributes, _lastAttributes))
                return;
            _lastAttributes = attributes;
            _attributeMin = default;
            _attributeMax = default;
            if (attributes is null)
                return;
            foreach (var a in attributes)
            {
                if (a is MinAttribute min && min.GetValue(typeof(T)) is T minValue)
                    _attributeMin = minValue;
                else if (a is MaxAttribute max && max.GetValue(typeof(T)) is T maxValue)
                    _attributeMax = maxValue;
            }
        }
    }
}
