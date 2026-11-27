using System.Reactive;
using VL.Core;
using VL.Lib.Reactive;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Writes events (clicks, long presses) as one <see cref="Unit"/> per event into a channel with author
    /// <see cref="ChannelAuthors.QtChannelAuthor"/>. A pin without a link arrives as null
    /// (<c>[DefaultValue(null)] IChannel&lt;Unit&gt;?</c>, otherwise vvvv lifts it to a private channel); the events are then dropped.
    /// </summary>
    internal static class ClickChannel
    {
        /// <summary>
        /// Takes the clicks (Qt <c>clicked</c>) the <paramref name="feedback"/> received since the last call, writes them
        /// into <paramref name="channel"/> and returns their number. Call once per Update.
        /// </summary>
        public static int Update(IChannel<Unit>? channel, ElementFeedback feedback)
        {
            var clicks = feedback.TakeClicks();
            FeedbackChannels.WriteEvents(channel, clicks);
            return clicks;
        }
    }
}
