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
    /// <item>change detection (<see cref="ChainMemo{TEffect, TProps}"/>): a new link only if the chain at Input (by reference) or the node
    /// specific <typeparamref name="TProps"/> changed, otherwise the last instance (reference equal → no QtNative ops);</item>
    /// <item>the messages at the node, all through <see cref="NodeErrors"/>: the feedback warning
    /// (interaction used more than once, a warning in phase "Warning"), the handler error (e.g. a property Qt rejected, a
    /// warning in phase "Qt") and the input error (inputs the node cannot use, an error in phase "Input", see
    /// <see cref="SetInputError"/>).</item>
    /// </list>
    /// The public node pulls the feedback state into its channels in its Update with <see cref="FeedbackChannels"/>.
    /// No static state. Main thread only.
    /// </summary>
    /// <typeparam name="TProps">Node specific inputs that go to QtNative; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class InteractionNodeCore<TProps> : IDisposable
        where TProps : struct, IEquatable<TProps>
    {
        private const string handlerPhase = "Qt";
        private const string inputPhase = "Input";
        private const string warningPhase = "Warning";

        private readonly NodeErrors _errors;
        private readonly Func<Interaction?, TProps, FeedbackSlot, Interaction> _create;
        private ChainMemo<Interaction, TProps> _memo;
        private string? _warning;
        private string? _handlerError;
        private string? _inputError;

        /// <param name="nodeContext">Context of the public node (all messages are shown there).</param>
        /// <param name="create">Builds a link; use a static lambda (no capture → no allocation).</param>
        public InteractionNodeCore(NodeContext nodeContext, Func<Interaction?, TProps, FeedbackSlot, Interaction> create)
            : this(new NodeErrors(nodeContext), create)
        {
        }

        /// <param name="errors">Store of the node, owned by the core (disposed with it).</param>
        /// <param name="create">Builds a link; use a static lambda (no capture → no allocation).</param>
        public InteractionNodeCore(NodeErrors errors, Func<Interaction?, TProps, FeedbackSlot, Interaction> create)
        {
            _errors = errors;
            _create = create;
        }

        /// <summary>Receives the events of the handler (slot 0).</summary>
        public ElementFeedback Feedback { get; } = new();

        /// <summary>Slot written into every link (one handler per node → index 0).</summary>
        public FeedbackSlot Slot => new(Feedback, 0);

        /// <summary>Warning currently shown at the node, null if none.</summary>
        public string? Warning => _warning;

        /// <summary>Error of the handler (e.g. a property Qt rejected) currently shown at the node as a warning, null if none.</summary>
        public string? HandlerError => _handlerError;

        /// <summary>Error about the inputs currently shown at the node (e.g. Minimum greater than Maximum), null if none.</summary>
        public string? InputError => _inputError;

        /// <summary>
        /// Returns the link for the inputs: the last instance if nothing changed, else a new one. Also updates the node
        /// warning and error from the feedback. Call once per Update.
        /// </summary>
        public Interaction Describe(Interaction? input, in TProps props)
        {
            var link = _memo.Get(input, props, Slot, _create);
            UpdateWarning();
            UpdateError();
            return link;
        }

        /// <summary>
        /// Shows <paramref name="error"/> at the node as an error until it is called with another text or null (the node
        /// shows it once; null removes it). Call every Update with the verdict about the inputs.
        /// </summary>
        public void SetInputError(string? error)
        {
            if (error == _inputError)
                return;
            _inputError = error;
            if (error is null)
                _errors.Clear(inputPhase);
            else
                _errors.Set(inputPhase, MessageSeverity.Error, error);
        }

        private void UpdateWarning()
        {
            var warning = Feedback.Warning;
            if (ReferenceEquals(warning, _warning) || warning == _warning)
                return;
            _warning = warning;
            if (warning is null)
                _errors.Clear(warningPhase);
            else
                _errors.Warn(warning, warningPhase);
        }

        private void UpdateError()
        {
            var error = Feedback.Error;
            if (error == _handlerError)
                return;
            _handlerError = error;
            if (error is null)
                _errors.Clear(handlerPhase);
            else
                _errors.Warn(error, handlerPhase);
        }

        public void Dispose()
        {
            _errors.Dispose();
            _handlerError = null;
            _inputError = null;
            _warning = null;
            _memo.Reset();
        }
    }
}
