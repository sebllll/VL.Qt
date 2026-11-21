using System;
using System.Reactive;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lang;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Inputs every element node has (the common pins of <see cref="Element"/>). Value equality is used for
    /// change detection. Values are in units of the renderer's space; nothing is converted here (the reconciler does, with the SpaceMapping of the scene).
    /// </summary>
    internal readonly record struct CommonInputs
    {
        public CommonInputs(Vector2 size, Matrix transform, RectangleAnchor anchor, float opacity, bool clip, bool debug, Interaction? interaction,
            Effect? effect)
        {
            Size = size;
            Transform = NormalizeTransform(transform);
            Anchor = anchor;
            Opacity = opacity;
            Clip = clip;
            Debug = debug;
            Interaction = interaction;
            Effect = effect;
        }

        public Vector2 Size { get; }
        public Matrix Transform { get; }
        public RectangleAnchor Anchor { get; }
        public float Opacity { get; }
        public bool Clip { get; }

        public bool Debug { get; }

        /// <summary>Chain of the Interaction pin; compared by reference (an unchanged chain is the same instance).</summary>
        public Interaction? Interaction { get; }

        /// <summary>Chain of the Effect pin (plan effects/01); compared by reference like <see cref="Interaction"/>.</summary>
        public Effect? Effect { get; }

        /// <summary>
        /// An all-zero matrix (default(Matrix), e.g. a pin without a VL default) is treated as Identity: it would
        /// collapse the item to a point and is never produced by transform nodes (they always keep M44 = 1).
        /// </summary>
        public static Matrix NormalizeTransform(Matrix m) => m == default ? Matrix.Identity : m;
    }

    /// <summary>
    /// Shared core of all element nodes (Rectangle, Text, Image, Group/Row/Column). Held as a field
    /// by the public <c>[ProcessNode]</c> class (composition: a public node cannot derive from an internal base,
    /// and a public base would show up as a node).
    /// <list type="bullet">
    /// <item>one <see cref="ElementFeedback"/> per node instance (slot 0), set on every description;</item>
    /// <item>change detection: a new description only if <see cref="CommonInputs"/> or the element specific
    /// <typeparamref name="TProps"/> changed, otherwise the last instance (reference equal → the reconciler skips it);</item>
    /// <item>measurement outputs and the clicks of a Button from the feedback (state of the last pump, 1 frame latency);</item>
    /// <item>the feedback warning (e.g. a Video at several places, an Interaction at a later place) as persistent node warning; the element itself may hang at several places of one tree and in several renderers;</item>
    /// <item>the feedback error (reported by Qt for the element, e.g. file not found, or a rejected apply) as persistent node warning,
    /// logged with the node's logger (<see cref="NodeErrors"/>).</item>
    /// </list>
    /// No static state; nothing here depends on the app (F8/F5 just creates new node instances). Main thread only.
    /// </summary>
    /// <typeparam name="TElement">Element type created by the node.</typeparam>
    /// <typeparam name="TProps">Element specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class ElementNode<TElement, TProps> : IDisposable
        where TElement : Element
        where TProps : struct, IEquatable<TProps>
    {
        private const string errorPhase = "Qt";

        private readonly Func<string, IDisposable> _addWarning;
        private readonly NodeErrors? _errors;
        private readonly Func<CommonInputs, TProps, FeedbackSlot, TElement> _create;
        private TElement? _last;
        private CommonInputs _common;
        private TProps _props;
        private string? _warning;
        private IDisposable? _warningMessage;
        private string? _error;

        /// <param name="nodeContext">Context of the public node (warning and error are shown there).</param>
        /// <param name="create">Builds a description; use a static lambda (no capture → no allocation).</param>
        public ElementNode(NodeContext nodeContext, Func<CommonInputs, TProps, FeedbackSlot, TElement> create)
            : this(w => nodeContext.AddPersistentMessage(MessageSeverity.Warning, w), create)
        {
            _errors = new NodeErrors(nodeContext);
        }

        /// <param name="addWarning">Shows a warning, the result removes it (tests: no NodeContext/AppHost needed; the
        /// error is then only tracked in <see cref="Error"/>).</param>
        /// <param name="create">Builds a description; use a static lambda (no capture → no allocation).</param>
        public ElementNode(Func<string, IDisposable> addWarning, Func<CommonInputs, TProps, FeedbackSlot, TElement> create)
        {
            _addWarning = addWarning;
            _create = create;
        }

        public ElementFeedback Feedback { get; } = new();

        /// <summary>Shows a warning at the node, the result removes it (for the element inputs of the node, <see cref="ElementInput"/>).</summary>
        public Func<string, IDisposable> AddWarning => _addWarning;

        /// <summary>Slot written into every description (v1 nodes output one element → index 0).</summary>
        public FeedbackSlot Slot => new(Feedback, 0);

        /// <summary>Warning currently shown at the node, null if none.</summary>
        public string? Warning => _warning;

        /// <summary>Error currently shown at the node (reported by Qt for the element), null if none.</summary>
        public string? Error => _error;

        /// <summary>Number of descriptions created so far (for tests).</summary>
        public int Created { get; private set; }

        /// <summary>
        /// Returns the description for the inputs: the last instance if nothing changed, else a new one.
        /// Also updates the node warning and error from the feedback. Call once per Update.
        /// </summary>
        public TElement Describe(in CommonInputs common, in TProps props)
        {
            if (_last is null || !_common.Equals(common) || !_props.Equals(props))
            {
                _last = _create(common, props, Slot);
                _common = common;
                _props = props;
                Created++;
            }
            UpdateWarning();
            UpdateError();
            return _last;
        }

        /// <summary>Author of every value written by Qt into a channel.</summary>
        public const string Author = "Qt";

        /// <summary>
        /// Takes the clicks (Qt <c>clicked</c>) the feedback received since the last call, writes one <see cref="Unit"/> per
        /// click into <paramref name="isClicked"/> if connected (author <see cref="Author"/>) and returns their number.
        /// Button only; call once per Update. Unconnected pins arrive as null (<c>[DefaultValue(null)] IChannel&lt;T&gt;?</c>,
        /// otherwise vvvv lifts them to a private channel).
        /// </summary>
        public int TakeClicks(IChannel<Unit>? isClicked)
        {
            var clicks = Feedback.TakeClicks();
            if (isClicked.IsValid())
                for (int i = 0; i < clicks; i++)
                    isClicked.SetValueAndAuthor(Unit.Default, Author);
            return clicks;
        }

        /// <summary>Outputs from the feedback (state of the last pump, in units).</summary>
        public void ReadOutputs(out Vector2 measuredSize, out RectangleF sceneBounds)
        {
            var feedback = Feedback;
            measuredSize = feedback.MeasuredSize();
            sceneBounds = feedback.SceneBounds();
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
        }
    }
}
