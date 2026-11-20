using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt
{
    /// <summary>
    /// Compares the element tree of one window with the tree of the last frame and writes the minimal
    /// commands (CREATE / SET / REPARENT / DESTROY / BIND) into an <see cref="ICommandSink"/>.
    /// <para>
    /// Identity rules (docs/decisions/reconciler-identity.md): an object is identified by its position under
    /// its parent plus its element type. A type change at a position destroys the old object and creates a
    /// new one. Inserting or removing in the middle of a child list lets the following objects take over the
    /// data of their neighbours; only the tail is created or destroyed. Moving an element to another parent
    /// therefore creates a new object there. A description that is reference-equal to the last frame's skips
    /// its whole subtree.
    /// </para>
    /// <para>
    /// An element may hang at several places of this tree (<see cref="SceneRegistry"/>): every occurrence gets
    /// its own Qt object and its own feedback id on the element's feedback. The first occurrence in traversal order
    /// (depth-first, children in order; an effect mask counts at its place, before the children) is the primary one
    /// (<see cref="Node.IsPrimary"/>): only its feedback id reports state values. A new occurrence is primary if the current
    /// primary was not reached yet in this pass (positions of existing nodes never change their order); when the
    /// primary lets go, the first of the remaining occurrences is picked at the end of the update and re-bound with a
    /// new feedback id, so QtNative sends its current state again. Other renderers may show the same element at the same
    /// time, each with its own Qt objects; only the owner renderer (the first one) has the primary occurrence.
    /// When this tree lets go of the last occurrence of an element it owns, it hands the element over at the end of the
    /// update (<see cref="SceneRegistry.HandOver"/>); the next renderer picks its primary in its next update
    /// (<see cref="TakeOver"/>). A video is shown once only, app-wide (inside this tree the first occurrence in traversal
    /// order wins and takes it over from a later position if needed): an ignored occurrence becomes a placeholder node
    /// without Qt object and sets a warning in the element's feedback. REPARENT indices count only the real objects.
    /// </para>
    /// <para>
    /// Every visited object is bound to the feedback slot of its description (BIND with a feedback id from
    /// <see cref="FeedbackRouter"/>, a new feedback id per slot change, no BIND flags), no feedback sends an unbind (feedback id 0).
    /// </para>
    /// <para>
    /// Input handlers: the interaction chain of an element (<see cref="Element.Interaction"/>) becomes a list of
    /// handler objects per item (<see cref="Node.Handlers"/>, not children), diffed by the feedback of each interaction
    /// node (see ReconcileHandlers); they are written after the item's BIND and before its children.
    /// </para>
    /// <para>
    /// Commands are written parent before child (CREATE, property SETs, REPARENT, BIND, then the children).
    /// DESTROY is sent only for the topmost object of a removed subtree (QtNative destroys recursively);
    /// the ids of the whole subtree are returned to the <see cref="IdAllocator"/>.
    /// </para>
    /// <para>
    /// Styles are resolved top-down: the base style (<see cref="BaseStyle.Default"/>) under the window root, each
    /// Group and widget merges its <see cref="Element.Style"/> over the style it inherited (<see cref="StyleMerge"/>),
    /// all other elements pass the inherited style on unchanged. A resolved style that stays equal keeps its reference
    /// (<see cref="Node.ResolvedStyle"/>), so a reference-equal subtree is skipped only if its inherited style is
    /// reference-equal too (<see cref="Node.InheritedStyle"/>); otherwise it is visited and only SET_STYLE diffs go out.
    /// Widgets send their resolved style right after CREATE and later as diff against <see cref="Node.SentStyle"/>
    /// (<see cref="StyleWriter"/>); primitives never get SET_STYLE.
    /// </para>
    /// <para>
    /// Self-made widgets: a child that is an <see cref="IStylable"/> (a <see cref="StylableElement"/> in the
    /// children array) is built with the style inherited at its position, and the returned element is reconciled in its
    /// place. Build runs on every pass, because a patched widget is usually a mutable instance whose reference does not
    /// change; subtrees containing one are therefore never skipped (<see cref="Node.ContainsStylable"/>), the elements
    /// around them still are. Exceptions are caught per call and shown at the parent's feedback (<see cref="FeedbackState.BuildError"/>).
    /// </para>
    /// <para>
    /// Thread: <see cref="Update(Element?, in SpaceMapping)"/> runs on the vvvv main thread inside the Update of the Renderer [Qt] node
    /// (<see cref="WindowRenderer.Update"/>), once per frame and window, after the element tree of that frame was
    /// evaluated; it only writes commands. The pump applies them later in the same frame (frame finished, after all node
    /// updates, <see cref="WindowRenderer.ApplyCommands"/>). An exception here is not caught by the renderer: it ends at the
    /// node like any exception of its Update.
    /// </para>
    /// Allocation-free in the steady state (no structural change). Not thread-safe; one per window.
    /// </summary>
    internal sealed partial class Reconciler
    {
        private readonly ICommandSink _sink;
        private readonly IdAllocator _ids;
        private readonly SceneRegistry _scene;
        private readonly Dictionary<uint, FeedbackSlot> _bindings = new();
        private readonly FeedbackRouter _feedbackRouter = new();
        private readonly List<Element> _pendingPrimary = new(); // elements whose primary occurrence let go in this pass
        private Node? _root;
        private SpaceMapping _mapping = SpaceMapping.Default;
        private bool _hasMapping;
        private bool _force;
        private bool _rescale; // the unit scale changed in this pass: every length is written again
        private bool _invalidated;
        private int _pass;

        /// <param name="sink">Target of the commands.</param>
        /// <param name="ids">Object id allocator of the window.</param>
        /// <param name="scene">Occurrence registry of the app (<see cref="SceneRegistry.ForApp"/>), shared by its renderers.</param>
        public Reconciler(ICommandSink sink, IdAllocator ids, SceneRegistry scene)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        }

        /// <summary>Native window of the renderer (vlqt_window_*), Zero in tests; used by Snapshot to find an element.</summary>
        public IntPtr Window { get; set; }

        /// <summary>Mirror of the object under the window root (index 0); null = empty scene.</summary>
        internal Node? Root => _root;

        /// <summary>Object id → feedback slot of every bound object.</summary>
        public IReadOnlyDictionary<uint, FeedbackSlot> Bindings => _bindings;

        /// <summary>Feedback ids of this window; <see cref="FeedbackRouter.Dispatch"/> takes the window's poll buffer.</summary>
        public FeedbackRouter FeedbackRouter => _feedbackRouter;

        /// <summary>
        /// Makes the next <see cref="Update(Element?, in SpaceMapping)"/> visit the whole tree (no reference-equality shortcut), e.g. because
        /// a video this reconciler ignored as a duplicate became free.
        /// </summary>
        public void Invalidate() => _invalidated = true;

        /// <summary>
        /// <paramref name="element"/> was handed over to this renderer (<see cref="SceneRegistry.HandOver"/>): the next
        /// <see cref="Update(Element?, in SpaceMapping)"/> picks the first of its occurrences in this tree as the primary one.
        /// </summary>
        internal void TakeOver(Element element) => _pendingPrimary.Add(element);

        /// <summary>Mapping of the last <see cref="Update(Element?, in SpaceMapping)"/>: units ↔ logical pixels of this scene.</summary>
        public SpaceMapping Mapping => _mapping;

        /// <summary>
        /// <see cref="Update(Element?, in SpaceMapping)"/> without scene metrics (size 0, dpr 1): for tests and callers in
        /// DIPTopLeft or DIP, whose scale does not depend on the scene.
        /// </summary>
        public void Update(Element? root, Space space) => Update(root, new SpaceMapping(space, default, 1f));

        /// <summary>
        /// Reconciles the scene against <paramref name="root"/> (null = empty scene) and writes the commands, lengths
        /// converted with <paramref name="mapping"/>. A change of its <see cref="SpaceMapping.UnitScale"/> disables the
        /// reference-equality shortcut for this frame, so the property diff writes every length again; a change of the
        /// space alone only moves the origin in QtNative (SET_SPACE, written by <see cref="SceneCore"/>).
        /// </summary>
        public void Update(Element? root, in SpaceMapping mapping)
        {
            _rescale = _hasMapping && mapping.UnitScale != _mapping.UnitScale;
            _force = !_hasMapping || _rescale || _invalidated;
            _invalidated = false;
            _mapping = mapping;
            _feedbackRouter.Mapping = mapping;
            _hasMapping = true;
            _pass++;

            if (root is null)
            {
                if (_root is not null)
                {
                    DestroySubtree(_root);
                    _root = null;
                }
                ResolvePrimaries();
                return;
            }

            _root = Reconcile(_root, root, null, null, 0, BaseStyle.Default);
            ResolvePrimaries();
        }

        /// <summary>
        /// Forgets the whole mirror WITHOUT writing commands (ids, element ownership, duplicates, bindings) and
        /// restarts the id allocator. For the resync after a failed apply, once QtNative has been wiped
        /// (<see cref="ResyncController"/>). The next <see cref="Update(Element?, in SpaceMapping)"/> builds the scene from scratch.
        /// </summary>
        internal void Discard()
        {
            if (_root is not null)
            {
                Forget(_root);
                _root = null;
            }
            ResolvePrimaries();
            _bindings.Clear();
            _ids.Reset();
            _hasMapping = false;
        }

        /// <summary>
        /// Destroys every object (renderer dispose): one DESTROY per top-level object. Elements other renderers still show
        /// are handed over to them.
        /// </summary>
        public void Clear()
        {
            if (_root is not null)
            {
                DestroySubtree(_root);
                _root = null;
            }
            ResolvePrimaries();
            _hasMapping = false;
        }

        /// <summary>
        /// Reconciles one position. Returns the node now living at that position (the same instance when the
        /// type matched, a new one or a placeholder otherwise). <paramref name="index"/> = REPARENT index
        /// (number of real objects before this position). <paramref name="inherited"/> = resolved style of the parent
        /// chain (the base style under the window root).
        /// </summary>
        private Node Reconcile(Node? existing, Element next, Element? parent, Node? parentNode, int index, Style inherited, bool attached = false)
        {
            if (next is StylableElement stylable)
                return ReconcileStylable(existing, stylable, parent, parentNode, index, inherited, attached);

            var node = ReconcileElement(existing, next, parent, parentNode, index, inherited, attached);
            node.Stylable = null;
            return node;
        }

        /// <summary>
        /// Position of an <see cref="IStylable"/>: calls its Build with <paramref name="inherited"/> (every pass that
        /// reaches it; its ancestors are never skipped, see <see cref="Node.ContainsStylable"/>) and reconciles the returned
        /// element in its place. A reference-equal result costs nothing more than the call. An exception is caught and shown
        /// at the parent's feedback (<see cref="FeedbackState.BuildError"/>); the element of the last successful Build of the same
        /// stylable stays. Null (or a failure without such an element) shows the placeholder's empty Group.
        /// </summary>
        private Node ReconcileStylable(Node? existing, StylableElement next, Element? parent, Node? parentNode, int index, Style inherited, bool attached)
        {
            Element built;
            string? error = null;
            try
            {
                built = next.Stylable.Build(inherited) ?? next.Empty;
            }
            catch (Exception e)
            {
                error = $"Build of {next.Stylable.GetType().Name} failed: {e.GetType().Name}: {e.Message}";
                var previous = existing?.Stylable is { } s && ReferenceEquals(s.Stylable, next.Stylable) ? existing.Last : null;
                built = previous ?? next.Empty;
            }

            var node = ReconcileElement(existing, built, parent, parentNode, index, inherited, attached);
            node.Stylable = next;
            SetBuildError(parent?.Feedback ?? default, node, error);
            return node;
        }

        /// <summary>
        /// Shows <paramref name="error"/> of the Build at <paramref name="node"/> in <paramref name="slot"/> (the parent's), or
        /// clears what this node showed there (null). An error of a sibling that is already shown is kept.
        /// </summary>
        private static void SetBuildError(FeedbackSlot slot, Node node, string? error)
        {
            if (error == node.BuildError)
                return;
            if (error is null)
            {
                if (slot.Feedback is { } feedback && feedback.TryGet(slot.Index, out var state) && state.BuildError == node.BuildError)
                    state.BuildError = null;
            }
            else if (slot.State is { } shown && (shown.BuildError is null || shown.BuildError == node.BuildError))
            {
                shown.BuildError = error;
            }
            node.BuildError = error;
        }

        /// <summary>Reconciles a plain element at one position (see <see cref="Reconcile"/>).</summary>
        private Node ReconcileElement(Node? existing, Element next, Element? parent, Node? parentNode, int index, Style inherited, bool attached = false)
        {
            if (existing is not null && existing.IsIgnored)
            {
                // Still a duplicate as long as another node (in this or another renderer) shows the video.
                if (!_force && ReferenceEquals(existing.Last, next) && IsShownElsewhere(next))
                    return existing;
                _scene.RemoveDuplicate(existing.Last!, this);
                existing = null;
            }

            if (existing is not null && existing.Type == next.Type)
            {
                bool same = ReferenceEquals(existing.Last, next);
                if (same && !_force && !existing.Dirty && !existing.ContainsStylable && ReferenceEquals(inherited, existing.InheritedStyle))
                {
                    existing.SkippedPass = _pass;
                    return existing;
                }

                existing.Dirty = false;
                existing.VisitedPass = _pass;
                if (!same || (SceneRegistry.IsExclusive(next) && _scene.OwnerOf(next) != existing))
                {
                    if (!CanOwn(next, existing))
                    {
                        DestroySubtree(existing);
                        return Ignore(next, parentNode);
                    }
                    if (!same)
                        Release(existing.Last!, existing);
                    Register(next, existing);
                }

                var resolved = ResolveStyle(existing, next, inherited);
                DiffProperties(existing, existing.Last, next, parent);
                BindFeedback(existing, next);
                ReconcileHandlers(existing, existing.Last?.Interaction, next.Interaction);
                ReconcileEffect(existing, next, resolved);
                ReconcileChildren(existing, next, resolved);
                ChildrenReconciled(existing, next);
                existing.Last = next;
                return existing;
            }

            if (existing is not null)
                DestroySubtree(existing);

            if (!CanOwn(next, null))
                return Ignore(next, parentNode);

            var node = new Node(_ids.Allocate(), next.Type) { Parent = parentNode, VisitedPass = _pass };
            Register(next, node);
            _sink.Create(node.Id, (ushort)node.Type);
            var childStyle = ResolveStyle(node, next, inherited);
            DiffProperties(node, null, next, parent);
            if (!attached)
                _sink.Reparent(node.Id, parentNode?.Id ?? IdAllocator.Root, (uint)index);
            BindFeedback(node, next);
            ReconcileHandlers(node, null, next.Interaction);
            ReconcileEffect(node, next, childStyle);
            ReconcileChildren(node, next, childStyle);
            ChildrenReconciled(node, next);
            node.Last = next;
            return node;
        }

        /// <summary>
        /// Resolves the style of <paramref name="node"/> and returns the style its children inherit. Group and
        /// widgets merge their own style over <paramref name="inherited"/> (only when one of both changed by reference;
        /// an equal result keeps the old reference, so unchanged children can still be skipped); widgets send the
        /// resolved style as SET_STYLE diff against what QtNative holds. Other elements pass <paramref name="inherited"/> on.
        /// </summary>
        private Style ResolveStyle(Node node, Element next, Style inherited)
        {
            var styled = next is Group || BaseStyle.AppliesTo((ushort)node.Type);
            if (!styled)
            {
                node.InheritedStyle = inherited;
                return inherited;
            }

            var resolved = node.ResolvedStyle;
            if (resolved is null
                || !ReferenceEquals(inherited, node.InheritedStyle)
                || !ReferenceEquals(next.Style, node.Last?.Style))
            {
                var merged = StyleMerge.Merge(inherited, next.Style);
                if (resolved is null || !merged.Equals(resolved))
                    resolved = merged;
            }
            node.InheritedStyle = inherited;
            node.ResolvedStyle = resolved;

            if (next is not Group)
            {
                // Writes nothing when QtNative already holds an equal style.
                StyleWriter.Write(_sink, node.Id, resolved, node.SentStyle);
                node.SentStyle = resolved;
            }
            return resolved;
        }

        /// <summary>Reconciles the children and updates <see cref="Node.ContainsStylable"/> of <paramref name="node"/>.</summary>
        private void ReconcileChildren(Node node, Element next, Style inherited)
        {
            ImmutableArray<Element> children = next.Children;
            List<Node> old = node.Children;
            int newCount = children.Length;
            int oldCount = old.Count;
            int common = Math.Min(newCount, oldCount);
            int live = 0; // real objects before the current position = REPARENT index
            bool stylable = false;

            for (int i = 0; i < common; i++)
            {
                var child = Reconcile(old[i], children[i], next, node, live, inherited);
                old[i] = child;
                if (!child.IsIgnored)
                    live++;
                stylable |= child.Stylable is not null || child.ContainsStylable;
            }

            if (oldCount > newCount)
            {
                for (int i = oldCount - 1; i >= newCount; i--)
                    DestroySubtree(old[i]);
                old.RemoveRange(newCount, oldCount - newCount);
            }
            else
            {
                for (int i = oldCount; i < newCount; i++)
                {
                    var child = Reconcile(null, children[i], next, node, live, inherited);
                    old.Add(child);
                    if (!child.IsIgnored)
                        live++;
                    stylable |= child.Stylable is not null || child.ContainsStylable;
                }
            }
            node.ContainsStylable = stylable;
        }

        /// <summary>
        /// Whether <paramref name="self"/> (null = a node about to be created) may show <paramref name="element"/>.
        /// Always true, except for a video (shown once only, app-wide): false if another renderer shows it; true if nobody
        /// shows it, or its occurrence is a node of this tree that comes later in this pass (not visited yet,
        /// or about to be destroyed); then the video is taken over and the old occurrence's subtree is marked dirty, so
        /// it is not skipped and turns into a placeholder when it is visited.
        /// </summary>
        private bool CanOwn(Element element, Node? self)
        {
            var ownerReconciler = _scene.OwnerReconcilerOf(element);
            if (ownerReconciler is null || !SceneRegistry.IsExclusive(element))
                return true;
            if (!ReferenceEquals(ownerReconciler, this))
                return false;
            var owner = _scene.OwnerOf(element);
            if (owner is null || ReferenceEquals(owner, self))
                return true;
            if (IsVisitedThisPass(owner))
                return false;
            for (var n = owner; n is not null && !n.Dirty && n.VisitedPass != _pass; n = n.Parent)
                n.Dirty = true;
            _scene.RemoveOccurrence(element, owner, this, invalidateWaiting: false);
            return true;
        }

        /// <summary>Whether a placeholder of <paramref name="element"/> has to stay one: it is a video shown at another node.</summary>
        private bool IsShownElsewhere(Element element)
            => SceneRegistry.IsExclusive(element) && _scene.OwnerReconcilerOf(element) is not null;

        /// <summary>
        /// Registers <paramref name="node"/> as an occurrence of <paramref name="element"/> and decides whether it is the
        /// primary one: it is if it is the only one, or if this renderer owns the element and the current primary was not
        /// reached yet in this pass (it comes later in traversal order or is about to be destroyed); the old primary is
        /// then demoted. While the primary is pending (it let go earlier in this pass, or the element was just handed over)
        /// the node waits for <see cref="ResolvePrimaries"/>. In a renderer that does not own the element it stays a
        /// further occurrence.
        /// </summary>
        private void Register(Element element, Node node)
        {
            if (_scene.AddOccurrence(element, node, this))
            {
                node.IsPrimary = true;
                return;
            }
            node.IsPrimary = false;
            var primary = _scene.OwnerOf(element, out var owner);
            if (!ReferenceEquals(owner, this) || primary is null || IsVisitedThisPass(primary))
                return;
            Demote(primary);
            _scene.SetPrimary(element, node);
            node.IsPrimary = true;
        }

        /// <summary>
        /// Removes the occurrence <paramref name="node"/> of <paramref name="element"/>; a primary that leaves others behind
        /// (here or in other renderers) is re-picked or handed over at the end of the update.
        /// </summary>
        private void Release(Element element, Node node)
        {
            if (_scene.RemoveOccurrence(element, node, this))
                _pendingPrimary.Add(element);
            node.IsPrimary = false;
        }

        /// <summary>Makes <paramref name="node"/> a further occurrence; its feedback id stops reporting state values right away (no BIND needed).</summary>
        private void Demote(Node node)
        {
            node.IsPrimary = false;
            if (node.BoundPrimary)
            {
                _feedbackRouter.SetPrimary(node.BoundFeedbackId, false);
                node.BoundPrimary = false;
            }
        }

        /// <summary>
        /// Picks the new primary occurrence of every element whose primary let go in this pass, or that was handed over to
        /// this renderer: the first of the remaining occurrences in traversal order (<see cref="IsBefore"/>), re-bound with
        /// a new feedback id so QtNative sends its state. An element without occurrences left in this tree goes to the next
        /// renderer that shows it (<see cref="SceneRegistry.HandOver"/>). Runs once per update after the whole tree (and
        /// after a clear); nothing to do (and no allocation) in the steady state.
        /// </summary>
        private void ResolvePrimaries()
        {
            for (int i = 0; i < _pendingPrimary.Count; i++)
            {
                var element = _pendingPrimary[i];
                if (!ReferenceEquals(_scene.OwnerReconcilerOf(element), this) || _scene.OwnerOf(element) is not null)
                    continue;
                if (_scene.OthersOf(element) is not { Count: > 0 } others)
                {
                    _scene.HandOver(element, this);
                    continue;
                }
                var first = others[0];
                for (int k = 1; k < others.Count; k++)
                {
                    if (IsBefore(others[k], first))
                        first = others[k];
                }
                _scene.SetPrimary(element, first);
                first.IsPrimary = true;
                if (!first.BoundFeedback.IsNone)
                    SyncPrimary(first);
            }
            _pendingPrimary.Clear();
        }

        /// <summary>
        /// Brings the feedback id of <paramref name="node"/> in line with <see cref="Node.IsPrimary"/>: a promotion takes a
        /// new primary feedback id and sends BIND (QtNative then sends the current Measured/SceneBounds/Media/Error again), a
        /// demotion only flips the flag of the feedback id.
        /// </summary>
        private void SyncPrimary(Node node)
        {
            if (node.IsPrimary)
            {
                _feedbackRouter.Release(node.BoundFeedbackId); // not primary: leaves the slot alone
                node.BoundFeedbackId = _feedbackRouter.Acquire(node.BoundFeedback, primary: true);
                _sink.Bind(node.Id, node.BoundFeedbackId, 0);
            }
            else
            {
                _feedbackRouter.SetPrimary(node.BoundFeedbackId, false);
            }
            node.BoundPrimary = node.IsPrimary;
        }

        /// <summary>True if <paramref name="a"/> comes before <paramref name="b"/> in traversal order (both in this tree, not equal).</summary>
        private static bool IsBefore(Node a, Node b)
        {
            int da = Depth(a), db = Depth(b);
            var x = a;
            var y = b;
            for (; da > db; da--)
                x = x.Parent!;
            for (; db > da; db--)
                y = y.Parent!;
            if (ReferenceEquals(x, y))
                return ReferenceEquals(x, a); // an ancestor comes first
            while (!ReferenceEquals(x.Parent, y.Parent))
            {
                x = x.Parent!;
                y = y.Parent!;
            }
            return Position(x) < Position(y);
        }

        private static int Depth(Node node)
        {
            int d = 0;
            for (var p = node.Parent; p is not null; p = p.Parent)
                d++;
            return d;
        }

        /// <summary>Order among siblings: the effect objects (negative, in chain order) before the children (index).</summary>
        private static int Position(Node node)
        {
            if (node.Parent is not { } parent)
                return 0;
            if (parent.EffectObjects is { } objects)
            {
                var i = objects.IndexOf(node);
                if (i >= 0)
                    return i - objects.Count;
            }
            return parent.Children.IndexOf(node);
        }

        /// <summary>True if <paramref name="node"/> was reached earlier in this pass (itself or a skipped ancestor).</summary>
        private bool IsVisitedThisPass(Node node)
        {
            if (node.VisitedPass == _pass)
                return true;
            for (var n = node; n is not null; n = n.Parent)
                if (n.SkippedPass == _pass)
                    return true;
            return false;
        }

        /// <summary>Placeholder for an occurrence of a video that is shown elsewhere (in this or another renderer).</summary>
        private Node Ignore(Element next, Node? parentNode)
        {
            var node = new Node(0, next.Type) { Parent = parentNode, VisitedPass = _pass, Last = next };
            _scene.AddDuplicate(next, this);
            return node;
        }

        /// <summary>
        /// BIND when the feedback slot changed (no flags: input comes from handlers since #319; QtNative cancels the press of a
        /// Tap handler with a new feedback itself since #320). The feedback id is primary or not after <see cref="Node.IsPrimary"/>;
        /// a node that became primary with an unchanged slot is re-bound too (<see cref="SyncPrimary"/>).
        /// </summary>
        private void BindFeedback(Node node, Element next)
        {
            FeedbackSlot slot = next.Feedback;
            FeedbackSlot old = node.BoundFeedback;
            if (slot == old)
            {
                if (!slot.IsNone && node.IsPrimary != node.BoundPrimary)
                    SyncPrimary(node);
                return;
            }

            if (slot.IsNone)
            {
                _sink.Bind(node.Id, 0, 0);
                _bindings.Remove(node.Id);
                _feedbackRouter.Release(node.BoundFeedbackId);
                node.BoundFeedbackId = 0;
                node.BoundPrimary = false;
            }
            else
            {
                // New feedback id: late events of the old binding are dropped, the old slot loses hover/press.
                _feedbackRouter.Release(node.BoundFeedbackId);
                node.BoundFeedbackId = _feedbackRouter.Acquire(slot, node.IsPrimary);
                node.BoundPrimary = node.IsPrimary;
                _sink.Bind(node.Id, node.BoundFeedbackId, 0);
                _bindings[node.Id] = slot;
            }
            node.BoundFeedback = slot;
        }

        /// <summary>DESTROY for the top node only (QtNative destroys its children), all ids freed.</summary>
        private void DestroySubtree(Node node)
        {
            if (!node.IsIgnored)
                _sink.Destroy(node.Id);
            Forget(node);
        }

        /// <summary>Frees ids, ownership, duplicate registrations and bindings of the whole subtree.</summary>
        private void Forget(Node node)
        {
            if (node.BuildError is not null)
                SetBuildError(node.Parent?.Last?.Feedback ?? default, node, null);
            if (node.IsIgnored)
            {
                _scene.RemoveDuplicate(node.Last!, this);
                return;
            }
            var children = node.Children;
            for (int i = 0; i < children.Count; i++)
                Forget(children[i]);
            if (node.EffectObjects is { } objects)
            {
                for (int i = 0; i < objects.Count; i++)
                    Forget(objects[i]);
                node.EffectObjects = null;
            }
            if (node.EffectWarning is not null)
                SetEffectWarning(node, node.Last?.Feedback ?? default, null);
            if (node.Last is not null)
                Release(node.Last, node);
            if (!node.BoundFeedback.IsNone)
            {
                _bindings.Remove(node.Id);
                _feedbackRouter.Release(node.BoundFeedbackId);
            }
            if (node.Handlers is { } handlers)
            {
                // QtNative removes the handlers with their item (or with the RESET of a resync).
                for (int i = 0; i < handlers.Count; i++)
                    ForgetHandler(handlers[i]);
                handlers.Clear();
            }
            _ids.Free(node.Id);
        }

        /// <summary>
        /// Diffs the input handlers of <paramref name="node"/> against the interaction chain <paramref name="next"/>
        /// (#319). Key = <see cref="Interaction.Feedback"/>; the order of the chain does not matter. A new key creates the
        /// handler (CREATE, property SETs, REPARENT to the item, BIND to a new feedback id), the same instance costs
        /// nothing, a changed instance sends the changed properties, a missing key is destroyed (ids and feedback id freed).
        /// An interaction that is already active at another handler (another element, the same chain twice, another
        /// renderer) gets a placeholder and the duplicate warning (<see cref="SceneRegistry"/>); it takes over once
        /// the owner lets go (next update). A reference-equal chain is skipped unless the whole tree is forced.
        /// </summary>
        private void ReconcileHandlers(Node node, Interaction? previous, Interaction? next)
        {
            var list = node.Handlers;
            if (next is null && (list is null || list.Count == 0))
                return;
            if (!_force && ReferenceEquals(previous, next) && list is not null)
                return;

            list ??= node.Handlers = new List<HandlerNode>();
            if (next is not null)
                ReconcileLink(node, list, next);

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].SeenPass != _pass)
                {
                    DestroyHandler(list[i]);
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Matches one link (after the links before it, so ids and the first occurrence of a duplicate follow the patch
        /// order; recursion depth = chain length).
        /// </summary>
        private void ReconcileLink(Node node, List<HandlerNode> list, Interaction link)
        {
            if (link.Previous is { } previous)
                ReconcileLink(node, list, previous);

            int k = IndexOfUnseen(list, link.Feedback);
            if (k < 0)
            {
                list.Add(CreateHandler(node, link));
                return;
            }

            var h = list[k];
            if (h.IsIgnored)
            {
                if (_scene.HandlerOwnerOf(link.Feedback) is null)
                {
                    _scene.RemoveHandlerDuplicate(h.Key, this);
                    list[k] = CreateHandler(node, link);
                }
                else
                {
                    h.SeenPass = _pass;
                    h.Last = link;
                }
            }
            else if (h.Type != link.HandlerType)
            {
                DestroyHandler(h);
                list[k] = CreateHandler(node, link);
            }
            else
            {
                h.SeenPass = _pass;
                if (_rescale || !ReferenceEquals(h.Last, link))
                {
                    // A new scale sends the limits again: previous = null writes every non-default value.
                    link.WriteProperties(_sink, h.Id, _rescale ? null : h.Last, _mapping);
                    h.Last = link;
                }
            }
        }

        /// <summary>Index of the handler with <paramref name="key"/> not yet matched in this pass, -1 if none.</summary>
        private int IndexOfUnseen(List<HandlerNode> list, FeedbackSlot key)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var h = list[i];
                if (h.SeenPass != _pass && h.Key == key)
                    return i;
            }
            return -1;
        }

        /// <summary>New handler at the item of <paramref name="node"/>, or a placeholder if the interaction is active elsewhere.</summary>
        private HandlerNode CreateHandler(Node node, Interaction link)
        {
            if (_scene.HandlerOwnerOf(link.Feedback) is not null)
            {
                _scene.AddHandlerDuplicate(link.Feedback, this);
                return new HandlerNode(0, link.HandlerType, link.Feedback) { SeenPass = _pass, Last = link };
            }

            var h = new HandlerNode(_ids.Allocate(), link.HandlerType, link.Feedback) { SeenPass = _pass, Last = link };
            _scene.SetHandlerOwner(h);
            _sink.Create(h.Id, h.Type);
            link.WriteProperties(_sink, h.Id, null, _mapping);
            _sink.Reparent(h.Id, node.Id, 0);
            h.BoundFeedbackId = _feedbackRouter.Acquire(h.Key);
            _sink.Bind(h.Id, h.BoundFeedbackId, 0);
            _bindings[h.Id] = h.Key;
            return h;
        }

        /// <summary>DESTROY of one handler (placeholders have no QtNative object), then <see cref="ForgetHandler"/>.</summary>
        private void DestroyHandler(HandlerNode h)
        {
            if (!h.IsIgnored)
                _sink.Destroy(h.Id);
            ForgetHandler(h);
        }

        /// <summary>Frees id, feedback id (resets the slot: not hovered), binding and ownership of a handler.</summary>
        private void ForgetHandler(HandlerNode h)
        {
            if (h.IsIgnored)
            {
                _scene.RemoveHandlerDuplicate(h.Key, this);
                return;
            }
            _scene.ReleaseHandler(h);
            _bindings.Remove(h.Id);
            _feedbackRouter.Release(h.BoundFeedbackId);
            h.BoundFeedbackId = 0;
            _ids.Free(h.Id);
        }

        /// <summary>
        /// Hook for the property diff (04c): writes the SETs for the properties of <paramref name="next"/>
        /// that differ from <paramref name="previous"/> (null = just created, send all non-default values).
        /// <paramref name="parent"/> is the new parent description (null under the window root), needed e.g.
        /// to treat the anchor only as pivot for children of Row/Column. Called before REPARENT for new objects.
        /// </summary>
        partial void DiffProperties(Node node, Element? previous, Element next, Element? parent);

        /// <summary>
        /// Hook after the children of <paramref name="node"/> were reconciled (their ids are final), e.g. to
        /// reference the ShaderEffect input child (04c).
        /// </summary>
        partial void ChildrenReconciled(Node node, Element next);
    }
}
