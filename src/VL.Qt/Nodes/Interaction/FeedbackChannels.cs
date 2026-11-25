using System;
using System.Reactive;
using VL.Core;
using VL.Lib.Reactive;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// The writers that carry feedback of a handler into the channels of an interaction node, the only ones:
    /// <see cref="WriteState{T}"/> for states, <see cref="WriteEvents"/> for events, <see cref="TakeUpdate{T}"/> for values
    /// whose repetition is an event. All write with author <see cref="ChannelAuthors.QtChannelAuthor"/>, pull in the node's
    /// Update, no subscription. An unconnected channel is forgotten, so a later connection counts as new.
    /// </summary>
    internal static class FeedbackChannels
    {
        /// <summary>Writes <paramref name="value"/> if it differs from the value last written there or the channel is another one than last time.</summary>
        public static void WriteState<T>(IChannel<T>? channel, ref IChannel<T>? last, ref T lastValue, T value)
            where T : struct, IEquatable<T>
        {
            if (!channel.IsValid())
            {
                last = null;
                return;
            }
            if (ReferenceEquals(channel, last) && lastValue.Equals(value))
                return;
            last = channel;
            lastValue = value;
            channel.EnsureValue(value, author: ChannelAuthors.QtChannelAuthor);
        }

        /// <summary>Writes one <see cref="Unit"/> per event; nothing without a channel (the caller drains the events anyway).</summary>
        public static void WriteEvents(IChannel<Unit>? channel, int count)
        {
            if (!channel.IsValid())
                return;
            for (int i = 0; i < count; i++)
                channel.SetValueAndAuthor(Unit.Default, ChannelAuthors.QtChannelAuthor);
        }

        /// <summary>
        /// For values whose repetition is an event (a drag with the same delta, a new pinch with the same scale):
        /// true if the caller has to write, because the feedback was updated since the last call or the channel is
        /// newly connected after an update. A channel connected before any update keeps its own value, so a fresh
        /// value (a preset, a global channel) is not overwritten by the empty feedback; write it with
        /// <c>SetValueAndAuthor</c> (it is an event, also when equal).
        /// </summary>
        public static bool TakeUpdate<T>(IChannel<T>? channel, ref IChannel<T>? last, ref int lastUpdates, int updates)
        {
            if (!channel.IsValid())
            {
                last = null;
                return false;
            }
            if (ReferenceEquals(channel, last) && updates == lastUpdates)
                return false;
            last = channel;
            lastUpdates = updates;
            return updates != 0;
        }
    }
}
