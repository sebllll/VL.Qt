using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>What a message of a feedback slot is (<see cref="FeedbackState.SetMessage"/>).</summary>
    internal enum MessageKind
    {
        /// <summary>A warning shown at the element node.</summary>
        Warning,

        /// <summary>The failed Build of an <see cref="Styles.IStylable"/> child, shown at the parent's node.</summary>
        BuildError,
    }

    /// <summary>State of one feedback slot (one element instance of a spread output). Main thread only.</summary>
    internal sealed class FeedbackState
    {
        public bool IsHovered { get; set; }
        public bool IsPressed { get; set; }

        /// <summary>Number of feedback ids bound to this slot whose Qt object is hovered; <see cref="IsHovered"/> is true while it is above 0.</summary>
        internal int HoverCount { get; set; }

        /// <summary>Number of feedback ids bound to this slot whose Qt object is pressed; <see cref="IsPressed"/> is true while it is above 0.</summary>
        internal int PressCount { get; set; }

        public Vector2 PointerPosition { get; set; }
        public Vector2 MeasuredSize { get; set; }
        public RectangleF SceneBounds { get; set; }
        /// <summary>Video: playback position in seconds (0 until loaded).</summary>
        public float MediaPosition { get; set; }
        /// <summary>Video: media duration in seconds (0 until loaded).</summary>
        public float MediaDuration { get; set; }

        /// <summary>Clicks not yet taken by <see cref="ElementFeedback.TakeClicks"/>.</summary>
        public int PendingClicks { get; set; }

        /// <summary>Tap handler: tap count of the last tap (2 = double click), 0 before the first tap.</summary>
        public int TapCount { get; set; }

        /// <summary>Tap handler: long presses not yet taken by <see cref="ElementFeedback.TakeLongPresses"/>.</summary>
        public int PendingLongPresses { get; set; }

        /// <summary>Point handler: a point (mouse button, finger, pen) is down on the item.</summary>
        public bool IsActive { get; set; }

        /// <summary>Wheel handler: notches not yet taken by <see cref="ElementFeedback.TakeWheel"/>.</summary>
        public Vector2 PendingWheel { get; set; }

        /// <summary>
        /// Drag handler: translation of the running (or last) drag in units; kept after the release until the next
        /// drag.
        /// </summary>
        public Vector2 DragDelta { get; set; }

        /// <summary>Drag handler: number of EVENT_DRAG received, so a value equal to the last one (new drag) is still seen as new.</summary>
        public int DragUpdates { get; set; }

        /// <summary>Touch Points handler: points currently pressed in the item, sorted by id, in units. Empty when none.</summary>
        public Spread<TouchPoint> TouchPoints { get; set; } = Spread<TouchPoint>.Empty;

        /// <summary>Touch Points handler: number of EVENT_TOUCH_POINTS received (changes with every event).</summary>
        public int TouchUpdates { get; set; }

        /// <summary>Pinch handler: scale factor of the running (or last) gesture, 1 before the first one.</summary>
        public float PinchScale { get; set; } = 1f;

        /// <summary>Pinch handler: rotation of the running (or last) gesture in cycles, clockwise positive.</summary>
        public float PinchRotation { get; set; }

        /// <summary>Pinch handler: translation of the running (or last) gesture in units, parent space.</summary>
        public Vector2 PinchTranslation { get; set; }

        /// <summary>Pinch handler: pivot of scale and rotation (the fingers' centroid when the gesture began) in units, parent space.</summary>
        public Vector2 PinchPivot { get; set; }

        /// <summary>Pinch handler: number of EVENT_PINCH received, so an equal value (new gesture) is still seen as new.</summary>
        public int PinchUpdates { get; set; }

        /// <summary>
        /// Value input: the last user change (EVENT_VALUE) not yet taken by <see cref="ElementFeedback.TakeValue{T}"/>.
        /// Several changes between two takes keep the last one.
        /// </summary>
        internal bool HasPendingValue { get; set; }

        /// <summary>Value type of <see cref="PendingValue"/> (<see cref="Protocol.ValueType"/>).</summary>
        internal ushort PendingValueType { get; set; }

        /// <summary>
        /// Pending user value for <see cref="Protocol.ValueType.I32"/>, <see cref="Protocol.ValueType.F32"/> and <see
        /// cref="Protocol.ValueType.Bool"/> (the bits of the value).
        /// </summary>
        internal int PendingValue { get; set; }

        /// <summary>Pending user value for <see cref="Protocol.ValueType.String"/>.</summary>
        internal string? PendingString { get; set; }

        /// <summary>
        /// QmlElement: last value of each root property that QML changed, by name; null until the first change. Cleared by <see
        /// cref="ElementFeedback.ResetQml"/>.
        /// </summary>
        internal Dictionary<string, object>? QmlProperties { get; set; }

        /// <summary>
        /// QmlElement: number of changes written into <see cref="QmlProperties"/> (also by a reset), so a node sees that there is
        /// something new.
        /// </summary>
        internal int QmlPropertyUpdates { get; set; }

        /// <summary>QmlElement: signals not yet taken by <see cref="ElementFeedback.TakeQmlSignals"/>, in order of arrival.</summary>
        internal List<QmlSignal>? PendingQmlSignals { get; set; }

        /// <summary>
        /// Warnings of every writer (effect fold warning, Video and interaction duplicate), joined with a space in the order
        /// they were set; null if none.
        /// </summary>
        public string? Warning => Joined(MessageKind.Warning);

        /// <summary>Error reported by Qt for the element (e.g. file not found), null if none.</summary>
        public string? Error { get; set; }

        /// <summary>
        /// Error of the last failed apply that addressed this element (e.g. a value Qt rejected), null if none.
        /// Set and cleared by the renderer; kept across the resync, unlike <see cref="Error"/>.
        /// </summary>
        public string? ApplyError { get; set; }

        /// <summary>
        /// Containers: errors of the last failed <see cref="Styles.IStylable.Build"/> of the children (one per child, joined
        /// with a space), null if none. Set and cleared by the reconciler through <see cref="SetMessage"/>.
        /// </summary>
        public string? BuildError => Joined(MessageKind.BuildError);

        /// <summary>
        /// Sets (or, with null, removes) the message of <paramref name="source"/> and <paramref name="kind"/>. Every source
        /// keeps its own message, so writers neither overwrite nor clear each other's; a source is any stable object (the
        /// mirror or a constant of the writer). The list is allocated with the first message.
        /// </summary>
        public void SetMessage(object source, MessageKind kind, string? text)
        {
            if (_messages is { } messages)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    if (!ReferenceEquals(messages[i].Source, source) || messages[i].Kind != kind)
                        continue;
                    if (text is null)
                        messages.RemoveAt(i);
                    else
                        messages[i] = new Message(source, kind, text);
                    return;
                }
            }
            if (text is not null)
                (_messages ??= new List<Message>()).Add(new Message(source, kind, text));
        }

        private string? Joined(MessageKind kind)
        {
            if (_messages is not { } messages)
                return null;
            string? joined = null;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Kind == kind)
                    joined = joined is null ? messages[i].Text : $"{joined} {messages[i].Text}";
            }
            return joined;
        }

        private readonly record struct Message(object Source, MessageKind Kind, string Text);

        private List<Message>? _messages;
    }
}
