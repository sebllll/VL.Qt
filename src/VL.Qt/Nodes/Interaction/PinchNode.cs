using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Pinch gesture with two or more fingers on the element (Qt PinchHandler): scale, rotation and translation
    /// since the gesture began, and the pivot they turn around: the fingers' centroid when it began. The values count
    /// from the activation (when Qt starts the gesture, after a small movement), and the node only reports them, it
    /// moves nothing: apply them in the patch about the pivot (move the pivot to the origin, scale and rotate, move it
    /// back by pivot + translation) in the parent's space, so that the content follows the fingers, and combine the
    /// last result with them when Is Active falls. After the gesture the channels keep the last value
    /// until the next gesture. With more fingers than Maximum Point Count the gesture pauses and goes on from the
    /// paused value when a finger is lifted. Connect Output to the Interaction pin of an element, or chain it with
    /// other interaction nodes. Values arrive one frame later.
    /// </summary>
    /// <tags>pinch, zoom, scale, rotate, rotation, gesture, multitouch, touch, trackpad</tags>
    [ProcessNode(Name = "Pinch", Category = Categories.Interaction)]
    public sealed class PinchNode : IDisposable
    {
        internal readonly record struct Props(int MinimumPointCount, int MaximumPointCount, PointerDevices Devices);

        private const string countError = "Minimum and Maximum Point Count must be at least 2, and Minimum must not be greater than Maximum. 2 and 2 are used.";

        private readonly InteractionNodeCore<Props> _node;
        private IChannel<bool>? _active;
        private IChannel<float>? _scale;
        private IChannel<float>? _rotation;
        private IChannel<Vector2>? _translation;
        private IChannel<Vector2>? _pivot;
        private bool _lastActive;
        private int _scaleUpdates;
        private int _rotationUpdates;
        private int _translationUpdates;
        private int _pivotUpdates;

        public PinchNode(NodeContext nodeContext)
        {
            _node = new InteractionNodeCore<Props>(nodeContext, Create);
        }

        /// <summary>For tests: warnings and input errors go to <paramref name="addMessage"/> instead of a NodeContext.</summary>
        internal PinchNode(Func<string, IDisposable> addMessage)
        {
            _node = new InteractionNodeCore<Props>(addMessage, Create);
        }

        private static readonly Func<Interaction?, Props, FeedbackSlot, Interaction> Create
            = static (previous, p, slot) => new PinchInteraction(previous, slot, p.MinimumPointCount, p.MaximumPointCount, p.Devices);

        internal InteractionNodeCore<Props> Core => _node;

        /// <summary>Adds a pinch handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isActive">Optional channel: true while the gesture runs (written by Qt, author "Qt").</param>
        /// <param name="scale">Optional channel: scale factor of the gesture, 1 at the start, 2 = fingers twice as far apart (author "Qt"). Keeps the last value after the gesture.</param>
        /// <param name="rotation">Optional channel: rotation of the gesture in cycles (1 = 360 degrees), clockwise positive as seen on screen (author "Qt"). Keeps the last value after the gesture.</param>
        /// <param name="translation">Optional channel: movement of the pivot since the gesture began (the fingers' centroid moves like that), in units, in the parent's space (author "Qt"). Keeps the last value after the gesture.</param>
        /// <param name="pivot">Optional channel: the fingers' centroid when the gesture began, in units, in the parent's space: the point scale and rotation turn around (author "Qt"). Keeps the last value after the gesture.</param>
        /// <param name="minimumPointCount">Fingers needed to start the gesture, at least 2.</param>
        /// <param name="maximumPointCount">Most fingers the gesture follows, at least 2. With more fingers the gesture pauses, it goes on when a finger is lifted.</param>
        /// <param name="devices">Input devices that count.</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isActive,
            [DefaultValue(null)] IChannel<float>? scale,
            [DefaultValue(null)] IChannel<float>? rotation,
            [DefaultValue(null)] IChannel<Vector2>? translation,
            [DefaultValue(null)] IChannel<Vector2>? pivot,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(PinchInteraction.DefaultPointCount)] int minimumPointCount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(PinchInteraction.DefaultPointCount)] int maximumPointCount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), nameof(PointerDevices.All))] PointerDevices devices)
        {
            bool valid = minimumPointCount >= 2 && maximumPointCount >= 2 && minimumPointCount <= maximumPointCount;
            _node.SetInputError(valid ? null : countError);
            var output = _node.Describe(input, valid
                ? new Props(minimumPointCount, maximumPointCount, devices)
                : new Props(PinchInteraction.DefaultPointCount, PinchInteraction.DefaultPointCount, devices));

            // Pull: state of the last pump (1 frame latency). The values are written once per EVENT_PINCH; a channel
            // connected later gets the current value only if a gesture happened (the channel's own value stays otherwise).
            var feedback = _node.Feedback;
            FeedbackChannels.WriteState(isActive, ref _active, ref _lastActive, feedback.IsActive());

            int updates = feedback.PinchUpdates();
            if (FeedbackChannels.TakeUpdate(scale, ref _scale, ref _scaleUpdates, updates))
                scale!.SetValueAndAuthor(feedback.PinchScale(), ChannelAuthors.QtChannelAuthor);
            if (FeedbackChannels.TakeUpdate(rotation, ref _rotation, ref _rotationUpdates, updates))
                rotation!.SetValueAndAuthor(feedback.PinchRotation(), ChannelAuthors.QtChannelAuthor);
            if (FeedbackChannels.TakeUpdate(translation, ref _translation, ref _translationUpdates, updates))
                translation!.SetValueAndAuthor(feedback.PinchTranslation(), ChannelAuthors.QtChannelAuthor);
            if (FeedbackChannels.TakeUpdate(pivot, ref _pivot, ref _pivotUpdates, updates))
                pivot!.SetValueAndAuthor(feedback.PinchPivot(), ChannelAuthors.QtChannelAuthor);
            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
