using System;
using System.Reactive;
using VL.Core;
using VL.Lang;
using VL.Lib.Reactive;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shared core of all interaction nodes (Hover, Click, Point, Wheel, …), the counterpart of <see cref="ElementNodeCore{TElement, TProps}"/>.
    /// Held as a field by the public <c>[ProcessNode]</c> class.
    /// <list type="bullet">
    /// <item>one <see cref="ElementFeedback"/> per node instance (slot 0): key of the handler in the reconciler and
    /// receiver of its events;</item>
    /// <item>change detection: a new link only if the chain at Input (by reference) or the node specific
    /// <typeparamref name="TProps"/> changed, otherwise the last instance (reference equal → no QtNative ops);</item>
    /// <item>the feedback warning (interaction used more than once) as persistent node warning, the feedback error (e.g. a
    /// handler property Qt rejected) as persistent node warning, logged (<see cref="NodeErrors"/>).</item>
    /// </list>
    /// The public node pulls the feedback state into its channels in its Update (<see cref="Write{T}"/>: only on change,
    /// author <see cref="ChannelAuthors.QtChannelAuthor"/>). No static state. Main thread only.
    /// </summary>
    /// <typeparam name="TProps">Node specific inputs that go to QtNative; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class InteractionNode<TProps> : IDisposable
        where TProps : struct, IEquatable<TProps>
    {
        private const string errorPhase = "Qt";

        private readonly Func<string, IDisposable> _addWarning;
        private readonly NodeErrors? _errors;
        private readonly Func<Interaction?, TProps, FeedbackSlot, Interaction> _create;
        private Interaction? _last;
        private Interaction? _input;
        private TProps _props;
        private string? _warning;
        private IDisposable? _warningMessage;
        private string? _error;

        /// <param name="nodeContext">Context of the public node (warning and error are shown there).</param>
        /// <param name="create">Builds a link; use a static lambda (no capture → no allocation).</param>
        public InteractionNode(NodeContext nodeContext, Func<Interaction?, TProps, FeedbackSlot, Interaction> create)
            : this(w => nodeContext.AddPersistentMessage(MessageSeverity.Warning, w), create)
        {
            _errors = new NodeErrors(nodeContext);
        }

        /// <param name="addWarning">Shows a warning, the result removes it (tests: no NodeContext/AppHost needed; the
        /// error is then only tracked in <see cref="Error"/>).</param>
        /// <param name="create">Builds a link; use a static lambda (no capture → no allocation).</param>
        public InteractionNode(Func<string, IDisposable> addWarning, Func<Interaction?, TProps, FeedbackSlot, Interaction> create)
        {
            _addWarning = addWarning;
            _create = create;
        }

        /// <summary>Receives the events of the handler (slot 0).</summary>
        public ElementFeedback Feedback { get; } = new();

        /// <summary>Slot written into every link (one handler per node → index 0).</summary>
        public FeedbackSlot Slot => new(Feedback, 0);

        /// <summary>Warning currently shown at the node, null if none.</summary>
        public string? Warning => _warning;

        /// <summary>Error currently shown at the node, null if none.</summary>
        public string? Error => _error;

        /// <summary>Number of links created so far (for tests).</summary>
        public int Created { get; private set; }

        /// <summary>
        /// Returns the link for the inputs: the last instance if nothing changed, else a new one. Also updates the node
        /// warning and error from the feedback. Call once per Update.
        /// </summary>
        public Interaction Describe(Interaction? input, in TProps props)
        {
            if (_last is null || !ReferenceEquals(_input, input) || !_props.Equals(props))
            {
                _last = _create(input, props, Slot);
                _input = input;
                _props = props;
                Created++;
            }
            UpdateWarning();
            UpdateError();
            return _last;
        }

        /// <summary>
        /// Writes <paramref name="value"/> into <paramref name="channel"/> with author <see cref="ChannelAuthors.QtChannelAuthor"/> if it differs
        /// from the value last written there (or the channel is another one than last time). Pull, no subscription.
        /// </summary>
        public static void Write<T>(IChannel<T> channel, ref IChannel<T>? last, ref T lastValue, T value)
            where T : struct, IEquatable<T>
        {
            if (ReferenceEquals(channel, last) && lastValue.Equals(value))
                return;
            last = channel;
            lastValue = value;
            channel.EnsureValue(value, author: ChannelAuthors.QtChannelAuthor);
        }

        private void UpdateWarning()
        {
            var warning = Feedback.Warning;
            if (ReferenceEquals(warning, _warning) || warning == _warning)
                return;
            _warningMessage?.Dispose();
            _warningMessage = warning is null ? null : _addWarning(warning);
            _warning = warning;
        }

        private void UpdateError()
        {
            var error = Feedback.Error;
            if (ReferenceEquals(error, _error) || error == _error)
                return;
            _error = error;
            if (_errors is null)
                return;
            if (error is null)
                _errors.Clear(errorPhase);
            else
                _errors.Warn(error, errorPhase);
        }

        public void Dispose()
        {
            _errors?.Dispose();
            _error = null;
            _warningMessage?.Dispose();
            _warningMessage = null;
            _warning = null;
            _last = null;
            _input = null;
        }
    }
}
