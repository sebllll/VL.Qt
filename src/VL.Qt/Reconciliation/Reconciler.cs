using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Styles;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Compares the element tree of one renderer with the mirror of the last frame and writes the minimal commands
    /// (CREATE / SET / REPARENT / DESTROY / BIND) into an <see cref="ICommandSink"/>. Runs on the vvvv main thread in the
    /// update of the renderer node, once per frame; allocation-free in the steady state; not thread-safe, one per renderer.
    /// Design: docs/design/reconciler.md; identity: docs/design/reconciler-identity.md, "Qt objects: position and type".
    /// </summary>
    internal sealed class Reconciler
    {
        // Stylables returning stylables: more than this many in a row count as a cycle.
        private const int maxStylableDepth = 32;

        private readonly ICommandSink _sink;
        private readonly IdAllocator _ids;
        private readonly OccurrenceRegistry _occurrences;
        private readonly InteractionRegistry _interactions;
        private readonly Dictionary<uint, FeedbackSlot> _bindings = new();
        private readonly FeedbackRouter _feedbackRouter = new();
        private readonly PrimaryOccurrences _primaries;
        private readonly HandlerReconciler _handlers;
        private readonly PropertyDiff _properties;
        // Warnings of the last Segment; used up before the objects of the chain are reconciled, so one list serves every chain.
        private readonly List<string> _effectWarnings = new();
        private ItemMirror? _root;
        private PassInfo _pass = new(0, false, false, SpaceMapping.Default);
        private bool _hasMapping;
        private bool _invalidated;

        /// <param name="sink">Target of the commands.</param>
        /// <param name="ids">Object id allocator of the window.</param>
        /// <param name="occurrences">Occurrence registry of the app (<see cref="OccurrenceRegistry.ForApp"/>), shared by its renderers.</param>
        /// <param name="interactions">Interaction registry of the app (<see cref="InteractionRegistry.ForApp"/>), shared by its renderers.</param>
        public Reconciler(ICommandSink sink, IdAllocator ids, OccurrenceRegistry occurrences, InteractionRegistry interactions)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _occurrences = occurrences ?? throw new ArgumentNullException(nameof(occurrences));
            _interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
            _primaries = new PrimaryOccurrences(this, occurrences, _feedbackRouter, sink);
            _handlers = new HandlerReconciler(this, sink, ids, _feedbackRouter, interactions, _bindings);
            _properties = new PropertyDiff(sink);
        }

        /// <summary>Native window of the renderer (vlqt_window_*), Zero in tests; used by Snapshot to find an element.</summary>
        public SceneHandle Window { get; set; }

        /// <summary>Mirror of the object under the window root (index 0); null = empty element tree.</summary>
        internal ItemMirror? Root => _root;

        /// <summary>Object id → feedback slot of every bound object.</summary>
        public IReadOnlyDictionary<uint, FeedbackSlot> Bindings => _bindings;

        /// <summary>Feedback ids of this window; <see cref="FeedbackRouter.Dispatch"/> takes the window's poll buffer.</summary>
        public FeedbackRouter FeedbackRouter => _feedbackRouter;

        /// <summary>
        /// Makes the next <see cref="Update(Element?, in SpaceMapping)"/> visit the whole tree (no reference-equality shortcut), e.g. because
        /// a video or an interaction this reconciler holds a placeholder of became free.
        /// </summary>
        public void Invalidate() => _invalidated = true;

        /// <summary>
        /// The element <paramref name="key"/> was handed over to this renderer (<see cref="OccurrenceRegistry.HandOver"/>): the
        /// next <see cref="Update(Element?, in SpaceMapping)"/> picks the first of its occurrences in this tree as the primary one.
        /// </summary>
        internal void TakeOver(OccurrenceKey key) => _primaries.TakeOver(key);

        /// <summary>
        /// Error of the Build of a stylable passed as root to the last <see cref="Update(Element?, in SpaceMapping)"/>
        /// (exception, cycle, foreign result), null if none; the renderer node shows it. Errors further down show at the
        /// container that holds the stylable.
        /// </summary>
        public string? RootBuildError => _root is { Stylable: not null } root ? root.BuildError : null;

        /// <summary>Mapping of the last <see cref="Update(Element?, in SpaceMapping)"/>: units ↔ logical pixels of this renderer.</summary>
        public SpaceMapping Mapping => _pass.Mapping;

        /// <summary>
        /// <see cref="Update(Element?, in SpaceMapping)"/> without scene metrics (size 0, dpr 1): for tests and callers in
        /// DIPTopLeft or DIP, whose scale does not depend on the scene.
        /// </summary>
        public void Update(Element? root, Space space) => Update(root, new SpaceMapping(space, default, 1f));

        /// <summary>
        /// Reconciles the element tree against <paramref name="root"/> (null = empty) and writes the commands, lengths
        /// converted with <paramref name="mapping"/>. A change of its <see cref="SpaceMapping.UnitScale"/> disables the
        /// reference-equality shortcut for this frame, so the property diff writes every length again; a change of the
        /// space alone only moves the origin in QtNative (SET_SPACE, written by <see cref="Scene"/>). An exception leaves the
        /// mirror half updated: the caller drops the commands of the frame and resyncs (<see cref="ResyncController.OnReconcileFailed"/>),
        /// see docs/design/reconciler.md, "Errors and resync".
        /// </summary>
        public void Update(Element? root, in SpaceMapping mapping)
        {
            var rescale = _hasMapping && mapping.UnitScale != _pass.Mapping.UnitScale;
            _pass = new PassInfo(_pass.Pass + 1, Force: !_hasMapping || rescale || _invalidated, Rescale: rescale, mapping);
            _invalidated = false;
            _feedbackRouter.Mapping = mapping;
            _hasMapping = true;

            if (root is null)
            {
                if (_root is not null)
                {
                    DestroySubtree(_root);
                    _root = null;
                }
                _primaries.ResolveAtEndOfUpdate();
                return;
            }

            // Mirror, registries and feedback ids agree once the update ends; a visit cut short by an exception leaves them
            // apart, and the caller resyncs (docs/design/reconciler.md, "Invariants").
            _root = Reconcile(_root, root, new Position(null, default, 0, BaseStyle.Default, false));
            _primaries.ResolveAtEndOfUpdate();
        }

        /// <summary>
        /// Forgets the whole mirror WITHOUT writing commands (ids, occurrences, placeholders, bindings, the messages it
        /// shows) and restarts the id allocator. For the resync after a failed apply or a failed
        /// <see cref="Update(Element?, in SpaceMapping)"/>, once QtNative has been wiped (<see cref="ResyncController"/>). Does
        /// not walk the mirror for the registries: an update an exception cut short leaves occurrences and handlers no mirror
        /// reaches and mirrors whose ids are already freed. The next <see cref="Update(Element?, in SpaceMapping)"/> builds
        /// the element tree from scratch.
        /// </summary>
        internal void Discard()
        {
            if (_root is not null)
            {
                ClearMessages(_root);
                _root = null;
            }
            _occurrences.RemoveAll(this);
            _interactions.RemoveAll(this);
            _primaries.Reset();
            _feedbackRouter.Clear();
            _bindings.Clear();
            _ids.Reset();
            _hasMapping = false;
        }

        /// <summary>Clears the build errors and effect warnings the subtree of <paramref name="item"/> shows (may run twice on a mirror).</summary>
        private static void ClearMessages(ItemMirror item)
        {
            if (item.BuildError is not null)
                SetBuildError(item.Parent?.Element?.Feedback ?? default, item, null);
            foreach (var child in item.Children)
                ClearMessages(child);
            if (item.Effect is { } effect)
            {
                foreach (var o in effect.Objects)
                    ClearMessages(o);
                if (effect.Warning is not null)
                    SetEffectWarning(effect, item.Element?.Feedback ?? default, null);
            }
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
            _primaries.ResolveAtEndOfUpdate();
            _hasMapping = false;
        }

        /// <summary>
        /// Reconciles one position. Returns the mirror now living at that position (the same instance when the
        /// type matched, a new one or a placeholder otherwise).
        /// </summary>
        private ItemMirror Reconcile(ItemMirror? existing, Element next, in Position at)
        {
            if (next is StylableElement stylable)
                return ReconcileStylable(existing, stylable, at);

            var item = ReconcileElement(existing, next, at);
            item.Stylable = null;
            return item;
        }

        /// <summary>
        /// Position of an <see cref="IStylable"/>: calls its Build with the inherited style (every pass that reaches it; its
        /// ancestors are never skipped, see <see cref="ItemMirror.ContainsStylable"/>) and reconciles the returned element
        /// in its place. A reference-equal result costs nothing more than the call. An exception is caught and shown at the
        /// parent's feedback (<see cref="FeedbackState.BuildError"/>); the element of the last successful Build of the same
        /// stylable stays. Null (or a failure without such an element) shows the placeholder's empty Group.
        /// </summary>
        private ItemMirror ReconcileStylable(ItemMirror? existing, StylableElement next, in Position at)
        {
            Element? built;
            string? error;
            try
            {
                built = Build(next.Stylable, at.Inherited, out error);
            }
            catch (Exception e)
            {
                built = null;
                error = $"Build of {next.Stylable.GetType().Name} failed: {e.GetType().Name}: {e.Message}";
            }
            if (error is not null)
                built = existing?.Stylable is { } s && ReferenceEquals(s.Stylable, next.Stylable) ? existing.Element : null;
            built ??= next.Empty;

            var item = ReconcileElement(existing, built, at);
            item.Stylable = next;
            next.Built = built;
            SetBuildError(at.ParentSlot, item, error);
            return item;
        }

        /// <summary>
        /// Calls Build of <paramref name="stylable"/>, and of every stylable it returns in turn, with the same style. Returns
        /// the element (null for "nothing"), or null with <paramref name="error"/> set for a stylable that returns itself, a
        /// cycle of stylables, or a foreign implementation of <see cref="IElement"/>. Exceptions of Build pass through.
        /// </summary>
        private static Element? Build(IStylable stylable, Style style, out string? error)
        {
            var current = stylable;
            for (int depth = 0; ; depth++)
            {
                var result = current.Build(style);
                if (result is IStylable next)
                {
                    if (ReferenceEquals(next, current) || ReferenceEquals(next, stylable) || depth >= maxStylableDepth)
                    {
                        error = $"Build of {current.GetType().Name} returns itself or a cycle of stylables; nothing is shown.";
                        return null;
                    }
                    current = next;
                    continue;
                }
                if (ElementConversion.IsForeign(result))
                {
                    error = $"Build of {current.GetType().Name}: {ElementConversion.ForeignWarning(result!.GetType())}";
                    return null;
                }
                error = null;
                return (Element?)result;
            }
        }

        /// <summary>
        /// Shows <paramref name="error"/> of the Build at <paramref name="item"/> in <paramref name="slot"/> (the parent's), or
        /// clears what this mirror showed there (null). Every sibling keeps its own message.
        /// </summary>
        private static void SetBuildError(FeedbackSlot slot, ItemMirror item, string? error)
        {
            if (error == item.BuildError)
                return;
            slot.SetMessage(item, MessageKind.BuildError, error);
            item.BuildError = error;
        }

        /// <summary>Reconciles a plain element at one position (see <see cref="Reconcile"/>).</summary>
        private ItemMirror ReconcileElement(ItemMirror? existing, Element next, in Position at)
        {
            var pass = _pass.Pass;
            if (existing is not null && existing.IsPlaceholder)
            {
                // Still a placeholder as long as another mirror (in this or another renderer) shows the video.
                if (!_pass.Force && OccurrenceKey.Of(existing.Element!) == OccurrenceKey.Of(next) && _primaries.IsShownElsewhere(next))
                {
                    existing.Element = next;
                    return existing;
                }
                _primaries.RemovePlaceholder(existing.Element!);
                existing = null;
            }

            if (existing is not null && existing.Type == next.Type)
            {
                bool same = ReferenceEquals(existing.Element, next);
                // Skip the subtree only if all five hold: same instance, pass not forced, no video taken over inside, no self-made
                // widget inside, inherited style unchanged (docs/design/reconciler.md, "Skipping unchanged subtrees").
                if (same && !_pass.Force && !existing.MustVisit && !existing.ContainsStylable && ReferenceEquals(at.Inherited, existing.InheritedStyle))
                {
                    existing.SkippedPass = pass;
                    return existing;
                }

                existing.MustVisit = false;
                existing.VisitedPass = pass;
                // A new instance of the same node (same key) is the same element: its occurrence stays as it is.
                bool sameKey = same || OccurrenceKey.Of(existing.Element!) == OccurrenceKey.Of(next);
                if (!sameKey || (OccurrenceRegistry.IsShownOnce(next) && !_primaries.IsPrimaryOf(next, existing)))
                {
                    if (!_primaries.TryClaimShownOnce(next, existing, pass))
                    {
                        DestroySubtree(existing);
                        return _primaries.CreatePlaceholder(next, at.Parent, pass);
                    }
                    if (!sameKey)
                        _primaries.Release(existing.Element!, existing);
                    _primaries.Register(next, existing, pass);
                }

                var resolved = ResolveStyle(existing, next, at.Inherited);
                _properties.Write(existing.Id, existing.Element, next, _pass.Mapping, _pass.Rescale);
                BindFeedback(existing, next);
                _handlers.Reconcile(existing, existing.Element?.Interaction, next.Interaction, _pass);
                ReconcileEffect(existing, next, resolved);
                ReconcileChildren(existing, next, resolved);
                if (next.Type == ElementType.Shader)
                    _properties.WriteShader(existing, (Shader)next);
                existing.Element = next;
                return existing;
            }

            if (existing is not null)
                DestroySubtree(existing);

            if (!_primaries.TryClaimShownOnce(next, null, pass))
                return _primaries.CreatePlaceholder(next, at.Parent, pass);

            var item = new ItemMirror(_ids.Allocate(), next.Type) { Parent = at.Parent, VisitedPass = pass };
            _primaries.Register(next, item, pass);
            _sink.Create(item.Id, (ushort)item.Type);
            var childStyle = ResolveStyle(item, next, at.Inherited);
            _properties.Write(item.Id, null, next, _pass.Mapping, _pass.Rescale);
            if (!at.Attached)
                _sink.Reparent(item.Id, at.Parent?.Id ?? IdAllocator.Root, (uint)at.Index);
            BindFeedback(item, next);
            _handlers.Reconcile(item, null, next.Interaction, _pass);
            ReconcileEffect(item, next, childStyle);
            ReconcileChildren(item, next, childStyle);
            if (next.Type == ElementType.Shader)
                _properties.WriteShader(item, (Shader)next);
            item.Element = next;
            return item;
        }

        /// <summary>
        /// Resolves the style of <paramref name="item"/> and returns the style its children inherit. Group and
        /// widgets merge their own style over <paramref name="inherited"/> (only when one of both changed by reference;
        /// an equal result keeps the old reference, so unchanged children can still be skipped); widgets send the
        /// resolved style as SET_STYLE diff against what QtNative holds. Other elements pass <paramref name="inherited"/> on.
        /// </summary>
        private Style ResolveStyle(ItemMirror item, Element next, Style inherited)
        {
            if (!StylableElements.ResolvesStyle(next))
            {
                item.InheritedStyle = inherited;
                return inherited;
            }

            var resolved = item.ResolvedStyle;
            if (resolved is null
                || !ReferenceEquals(inherited, item.InheritedStyle)
                || !ReferenceEquals(next.Style, item.Element?.Style))
            {
                var merged = StyleMerge.Merge(inherited, next.Style);
                if (resolved is null || !merged.Equals(resolved))
                    resolved = merged;
            }
            item.InheritedStyle = inherited;
            item.ResolvedStyle = resolved;

            if (StylableElements.IsWidget(next))
            {
                // Writes nothing when QtNative already holds an equal style.
                StyleWriter.Write(_sink, item.Id, resolved, item.SentStyle);
                item.SentStyle = resolved;
            }
            return resolved;
        }

        /// <summary>Reconciles the children and updates <see cref="ItemMirror.ContainsStylable"/> of <paramref name="item"/>.</summary>
        private void ReconcileChildren(ItemMirror item, Element next, Style inherited)
        {
            ImmutableArray<Element> children = next.ChildItems;
            List<ItemMirror> old = item.Children;
            int newCount = children.Length;
            int oldCount = old.Count;
            int common = Math.Min(newCount, oldCount);
            int live = 0; // real objects before the current position = REPARENT index; placeholders and effect objects do not count
            // The effect objects were reconciled just before (ReconcileEffect); a stylable among them needs every pass too.
            bool stylable = HasStylable(item.Effect?.Objects);

            for (int i = 0; i < common; i++)
            {
                var child = Reconcile(old[i], children[i], new Position(item, next.Feedback, live, inherited, false));
                old[i] = child;
                if (!child.IsPlaceholder)
                    live++;
                stylable |= child.HoldsStylable;
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
                    var child = Reconcile(null, children[i], new Position(item, next.Feedback, live, inherited, false));
                    old.Add(child);
                    if (!child.IsPlaceholder)
                        live++;
                    stylable |= child.HoldsStylable;
                }
            }
            item.ContainsStylable = stylable;
        }

        private static bool HasStylable(List<ItemMirror>? objects)
        {
            if (objects is not null)
            {
                foreach (var o in objects)
                    if (o.HoldsStylable)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// BIND when the feedback slot changed (QtNative cancels the press of a Tap handler with a new feedback itself).
        /// The new feedback id is primary or not after <see cref="ItemMirror.IsPrimary"/>; a mirror that becomes primary with
        /// an unchanged slot is re-bound by <see cref="PrimaryOccurrences"/> when it is promoted.
        /// </summary>
        private void BindFeedback(ItemMirror item, Element next)
        {
            FeedbackSlot slot = next.Feedback;
            FeedbackSlot old = item.BoundSlot;
            if (slot == old)
                return;

            if (slot.IsNone)
            {
                _sink.Bind(item.Id, 0);
                _bindings.Remove(item.Id);
                _feedbackRouter.Release(item.BoundFeedbackId);
                item.BoundFeedbackId = 0;
            }
            else
            {
                // New feedback id: late events of the old binding are dropped, the old slot loses hover/press.
                _feedbackRouter.Release(item.BoundFeedbackId);
                item.BoundFeedbackId = _feedbackRouter.Acquire(slot, item.IsPrimary);
                _sink.Bind(item.Id, item.BoundFeedbackId);
                _bindings[item.Id] = slot;
            }
            item.BoundSlot = slot;
        }

        /// <summary>DESTROY for the top mirror only (QtNative destroys its children), all ids freed.</summary>
        private void DestroySubtree(ItemMirror item)
        {
            if (!item.IsPlaceholder)
                _sink.Destroy(item.Id);
            Forget(item);
        }

        /// <summary>Frees ids, occurrences, placeholders and bindings of the whole subtree.</summary>
        private void Forget(ItemMirror item)
        {
            if (item.BuildError is not null)
                SetBuildError(item.Parent?.Element?.Feedback ?? default, item, null);
            if (item.IsPlaceholder)
            {
                _primaries.RemovePlaceholder(item.Element!);
                return;
            }
            var children = item.Children;
            for (int i = 0; i < children.Count; i++)
                Forget(children[i]);
            if (item.Effect is { } effect)
            {
                var objects = effect.Objects;
                for (int i = 0; i < objects.Count; i++)
                    Forget(objects[i]);
                objects.Clear();
                if (effect.Warning is not null)
                    SetEffectWarning(effect, item.Element?.Feedback ?? default, null);
            }
            if (item.Element is not null)
                _primaries.Release(item.Element, item);
            if (!item.BoundSlot.IsNone)
            {
                _bindings.Remove(item.Id);
                _feedbackRouter.Release(item.BoundFeedbackId);
            }
            _handlers.Forget(item);
            _ids.Free(item.Id);
        }

        /// <summary>
        /// Reconciles the Effect pin of <paramref name="item"/>: a new chain instance is segmented and sent, an unchanged one
        /// only retries its objects.
        /// </summary>
        private void ReconcileEffect(ItemMirror item, Element next, Style inherited)
        {
            var chain = next.Effect;
            if (chain is null && item.Effect?.Chain is null)
                return;

            var effect = item.Effect ??= new EffectChainMirror();
            if (ReferenceEquals(chain, effect.Chain))
            {
                RetryEffectObjects(item, effect, inherited);
                return;
            }

            ChangeEffectChain(item, effect, next, chain, inherited);
        }

        /// <summary>The chain instance changed: segments it, reconciles the objects, sends it and destroys the objects it no longer uses.</summary>
        private void ChangeEffectChain(ItemMirror item, EffectChainMirror effect, Element next, Effect? chain, Style inherited)
        {
            Effect.Segment(chain, effect.Links, _effectWarnings);
            effect.Chain = chain;
            SetEffectWarning(effect, next.Feedback, _effectWarnings.Count == 0 ? null : string.Join(" ", _effectWarnings));

            var elements = effect.Elements;
            CollectEffectElements(effect.Links, elements);
            var old = effect.Objects;
            if (elements.Count == 0 && old.Count == 0)
            {
                EffectChainWriter.Send(_sink, item.Id, effect);
                return;
            }

            // Rented, not kept on the reconciler: reconciling an object can change the chain of that object itself.
            var used = old.Count > 0 ? ArrayPool<bool>.Shared.Rent(old.Count) : null;
            var matches = elements.Count > 0 ? ArrayPool<ItemMirror?>.Shared.Rent(elements.Count) : null;
            try
            {
                if (used is not null)
                {
                    // The same element keeps its object; leftovers of the same type are updated in place.
                    for (int i = 0; i < elements.Count; i++)
                        matches![i] = TakeObject(old, used, elements[i], sameElement: true);
                    for (int i = 0; i < elements.Count; i++)
                        matches![i] ??= TakeObject(old, used, elements[i], sameElement: false);
                }

                var objects = effect.SpareObjects;
                for (int i = 0; i < elements.Count; i++)
                    objects.Add(Reconcile(matches![i], elements[i], new Position(item, default, 0, inherited, Attached: true)));
                effect.ReplaceObjects(objects);

                EffectChainWriter.Send(_sink, item.Id, effect);

                // The chain no longer refers to the objects it did not reuse.
                for (int i = 0; i < old.Count; i++)
                    if (!used![i])
                        DestroySubtree(old[i]);
                old.Clear();
            }
            finally
            {
                if (used is not null)
                    ArrayPool<bool>.Shared.Return(used, clearArray: true);
                if (matches is not null)
                    ArrayPool<ItemMirror?>.Shared.Return(matches, clearArray: true);
            }
        }

        /// <summary>
        /// Unchanged chain: the objects are reconciled again (their element may have changed inside, and a video shown
        /// elsewhere stays a placeholder that is retried every pass); the chain is sent again when an object got
        /// another id.
        /// </summary>
        private void RetryEffectObjects(ItemMirror item, EffectChainMirror effect, Style inherited)
        {
            var objects = effect.Objects;
            var elements = effect.Elements;
            var resend = false;
            for (int i = 0; i < objects.Count; i++)
            {
                var o = Reconcile(objects[i], elements[i], new Position(item, default, 0, inherited, Attached: true));
                objects[i] = o;
                if (o.Id != effect.SentIds[i])
                    resend = true;
            }
            if (resend)
                EffectChainWriter.Send(_sink, item.Id, effect);
        }

        /// <summary>First unused old object of <paramref name="element"/> itself (<paramref name="sameElement"/>) or of its type.</summary>
        private static ItemMirror? TakeObject(List<ItemMirror> old, bool[] used, Element element, bool sameElement)
        {
            for (int i = 0; i < old.Count; i++)
            {
                if (used[i])
                    continue;
                // The object of a stylable holds its built element; the placeholder in the chain is the stylable's.
                var shown = (Element?)old[i].Stylable ?? old[i].Element;
                if (sameElement ? ReferenceEquals(shown, element) : old[i].Type == element.Type)
                {
                    used[i] = true;
                    return old[i];
                }
            }
            return null;
        }

        /// <summary>Fills <paramref name="elements"/> with the elements the links refer to (mask sources and uniform textures), each once by reference, in chain order.</summary>
        private static void CollectEffectElements(List<EffectChainLink> links, List<Element> elements)
        {
            elements.Clear();
            foreach (var link in links)
            {
                if (link.Shader is { } shader)
                {
                    foreach (var u in shader.Uniforms)
                        if (u?.Texture is { } texture)
                            AddOnce(elements, texture);
                }
                else if (link.Multi.MaskSource is { } mask)
                {
                    AddOnce(elements, mask);
                }
            }
        }

        private static void AddOnce(List<Element> elements, Element element)
        {
            foreach (var e in elements)
                if (ReferenceEquals(e, element))
                    return;
            elements.Add(element);
        }

        /// <summary>Writes the fold warning of <paramref name="effect"/> into <paramref name="slot"/> (null clears what it wrote).</summary>
        private static void SetEffectWarning(EffectChainMirror effect, FeedbackSlot slot, string? warning)
        {
            if (warning == effect.Warning)
                return;
            slot.SetMessage(effect, MessageKind.Warning, warning);
            effect.Warning = warning;
        }
    }
}
