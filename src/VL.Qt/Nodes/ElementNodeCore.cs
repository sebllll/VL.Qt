using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lang;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shared core of all element nodes (Rectangle, Text, Image, Group/Row/Column). Held as a field
    /// by the public <c>[ProcessNode]</c> class (composition: a public node cannot derive from an internal base,
    /// and a public base would show up as a node).
    /// <list type="bullet">
    /// <item>one <see cref="ElementFeedback"/> per node instance (slot 0), set on every description;</item>
    /// <item>change detection: a new description only if <see cref="CommonInputs"/> or the element specific
    /// <typeparamref name="TProps"/> changed, otherwise the last instance (reference equal → the reconciler skips it);</item>
    /// <item>measurement outputs and the clicks of a Button from the feedback (state of the last pump, 1 frame latency);</item>
    /// <item>the feedback warning (e.g. a Video at several places, an Interaction at a later place) and the feedback error
    /// (reported by Qt for the element, e.g. file not found, or a rejected apply) as warnings in the node's
    /// <see cref="NodeErrors"/>: shown at the node and logged; the element itself may hang at several places of one tree and in several renderers.</item>
    /// </list>
    /// No static state; nothing here depends on the app (F8/F5 just creates new node instances). Main thread only.
    /// </summary>
    /// <typeparam name="TElement">Element type created by the node.</typeparam>
    /// <typeparam name="TProps">Element specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class ElementNodeCore<TElement, TProps> : IDisposable
        where TElement : Element
        where TProps : struct, IEquatable<TProps>
    {
        private readonly bool _ownsMessages;
        private readonly NodeMessage _warning;
        private readonly NodeMessage _error;
        private readonly Func<CommonInputs, TProps, FeedbackSlot, TElement> _create;
        private TElement? _last;
        private CommonInputs _common;
        private TProps _props;

        /// <param name="nodeContext">Context of the public node (warning and error are shown there).</param>
        /// <param name="create">Builds a description; use a static lambda (no capture → no allocation).</param>
        public ElementNodeCore(NodeContext nodeContext, Func<CommonInputs, TProps, FeedbackSlot, TElement> create)
            : this(new NodeErrors(nodeContext), create, string.Empty, ownsMessages: true)
        {
        }

        /// <param name="addWarning">Shows the node's message, the result removes it (no NodeContext/AppHost needed).</param>
        /// <param name="create">Builds a description; use a static lambda (no capture → no allocation).</param>
        public ElementNodeCore(Func<string, IDisposable> addWarning, Func<CommonInputs, TProps, FeedbackSlot, TElement> create)
            : this(NodeErrors.ShownBy(addWarning), create, string.Empty, ownsMessages: true)
        {
        }

        /// <param name="messages">Store of the node, shared with other cores of the same node; the caller disposes it.</param>
        /// <param name="create">Builds a description; use a static lambda (no capture → no allocation).</param>
        /// <param name="part">Distinguishes the phases of several cores in one store.</param>
        public ElementNodeCore(NodeErrors messages, Func<CommonInputs, TProps, FeedbackSlot, TElement> create, string part)
            : this(messages, create, part, ownsMessages: false)
        {
        }

        private ElementNodeCore(NodeErrors messages, Func<CommonInputs, TProps, FeedbackSlot, TElement> create, string part, bool ownsMessages)
        {
            Messages = messages;
            _ownsMessages = ownsMessages;
            _warning = new NodeMessage(messages, part + "Warning");
            _error = new NodeMessage(messages, part + "Qt");
            _create = create;
        }

        public ElementFeedback Feedback { get; } = new();

        /// <summary>Message store of the node (for the element inputs of the node, <see cref="ElementInput"/>).</summary>
        public NodeErrors Messages { get; }

        /// <summary>Slot written into every description (v1 nodes output one element → index 0).</summary>
        public FeedbackSlot Slot => new(Feedback, 0);

        /// <summary>Feedback warning currently shown at the node, null if none.</summary>
        public string? Warning => _warning.Text;

        /// <summary>Error currently shown at the node (reported by Qt for the element), null if none.</summary>
        public string? Error => _error.Text;

        /// <summary>Number of descriptions created so far (for tests).</summary>
        public int Created { get; private set; }

        /// <summary>
        /// Returns the description for the inputs: the last instance if nothing changed, else a new one.
        /// Also updates the node warning and error and reads the outputs from the feedback (state of the last pump, in units).
        /// Call once per Update.
        /// </summary>
        public TElement Describe(in CommonInputs common, in TProps props, out Vector2 measuredSize, out RectangleF sceneBounds)
        {
            if (_last is null || !_common.Equals(common) || !_props.Equals(props))
            {
                _last = _create(common, props, Slot);
                _common = common;
                _props = props;
                Created++;
            }
            _warning.Set(Feedback.Warning);
            _error.Set(Feedback.Error);
            measuredSize = Feedback.MeasuredSize();
            sceneBounds = Feedback.SceneBounds();
            return _last;
        }

        /// <summary>
        /// Removes the warning and error of this core from the node (a core that is not described this frame, e.g. the
        /// inactive layout of Labeled); the next <see cref="Describe"/> shows them again.
        /// </summary>
        public void ClearMessages()
        {
            _warning.Set(null);
            _error.Set(null);
        }

        public void Dispose()
        {
            ClearMessages();
            if (_ownsMessages)
                Messages.Dispose();
            _last = null;
        }
    }
}
