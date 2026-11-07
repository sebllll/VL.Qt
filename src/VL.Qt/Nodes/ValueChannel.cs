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
    /// Value of a value input element (Input (Int) #210; Toggle, Slider, TextInput #301) and its optional channel pin,
    /// in the spirit of VL.ImGui's <c>ChannelWidget&lt;T&gt;</c>. Pull, no subscription: <see cref="Update"/> reads
    /// <c>channel.Value</c> once per frame and compares it with the last value it saw.
    /// <list type="bullet">
    /// <item>Unconnected the pin is null (<c>[DefaultValue(null)] IChannel&lt;T&gt;?</c>); the node then keeps its own
    /// value, changed by the user only. A plain value or an IOBox on the pin is lifted by vvvv into a private channel:
    /// start value and setting from the patch.</item>
    /// <item>A user change (EV_VALUE from the sink) is written into the channel with author <see cref="Author"/> and sets
    /// <see cref="Bang"/> for one Update. It is not echoed as a channel change. If the patch changed the channel in the
    /// same frame, the user wins.</item>
    /// <item>The node describes <see cref="Value"/>; the reconciler diffs it against the last sent description as usual
    /// (no "last sent = user value", PR #354).</item>
    /// <item>Qt clamps a SET without an event, so the node clamps the own copy with <see cref="Clamp"/>; the channel keeps
    /// the patch's value.</item>
    /// </list>
    /// Main thread only. No allocation per frame (the attributes of a channel are read once per change).
    /// </summary>
    /// <typeparam name="T">Value type of the element: int, float, bool or string (see <see cref="ElementSink.TakeValue{T}"/>).</typeparam>
    internal sealed class ValueChannel<T>
    {
        /// <summary>Author of every value the user produced in Qt.</summary>
        public const string Author = "Qt";

        IChannel<T>? _channel;
        T _lastSeen;
        T _value;
        IChannel<Spread<Attribute>>? _attributes;
        Spread<Attribute>? _lastAttributes;
        Optional<T> _attributeMin;
        Optional<T> _attributeMax;

        /// <param name="initial">Value while no channel was seen yet (unconnected pin).</param>
        public ValueChannel(T initial)
        {
            _value = initial;
            _lastSeen = initial;
        }

        /// <summary>The value the node shows (patch value, user change, clamped).</summary>
        public T Value => _value;

        /// <summary>True for one Update after a user change in Qt.</summary>
        public bool Bang { get; private set; }

        /// <summary>
        /// Takes the patch's channel value and the user's change from <paramref name="sink"/> and returns the new
        /// <see cref="Value"/>. Call once per Update, before <see cref="Clamp"/> and the description.
        /// </summary>
        public T Update(IChannel<T>? channel, ElementSink sink, int index = 0)
        {
            Bang = false;
            bool fromPatch = false;
            T patchValue = default!;
            if (!ReferenceEquals(channel, _channel))
            {
                _channel = channel;
                _attributes = null;
                _lastAttributes = null;
                _attributeMin = default;
                _attributeMax = default;
                if (channel.IsValid())
                {
                    patchValue = channel.Value!;
                    fromPatch = true;
                    _lastSeen = patchValue;
                }
            }
            else if (channel.IsValid())
            {
                var v = channel.Value!;
                if (!EqualityComparer<T>.Default.Equals(v, _lastSeen))
                {
                    patchValue = v;
                    fromPatch = true;
                    _lastSeen = v;
                }
            }

            if (sink.TakeValue<T>(out var user, index))
            {
                // the user wins over a patch change of the same frame
                _value = user;
                Bang = true;
                if (channel.IsValid())
                {
                    channel.EnsureValue(user, author: Author);
                    _lastSeen = user;
                }
            }
            else if (fromPatch)
                _value = patchValue;
            return _value;
        }

        /// <summary>
        /// Clamps the own copy like Qt does (bounds in any order, as Qt's SpinBox and Slider allow from &gt; to) and
        /// returns it. The channel is not written.
        /// </summary>
        public T Clamp(T min, T max)
        {
            var c = Comparer<T>.Default;
            var lo = c.Compare(min, max) <= 0 ? min : max;
            var hi = c.Compare(min, max) <= 0 ? max : min;
            if (c.Compare(_value, lo) < 0)
                _value = lo;
            else if (c.Compare(_value, hi) > 0)
                _value = hi;
            return _value;
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
        void UpdateAttributes()
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
