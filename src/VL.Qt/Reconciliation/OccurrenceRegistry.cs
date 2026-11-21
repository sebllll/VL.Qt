using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.Interop;

namespace VL.Qt.Reconciliation
{
﻿    /// <summary>
    /// App-wide record of where each element instance is shown (docs/decisions/reconciler-identity.md).
    /// <para>
    /// An element may hang at several places, inside one renderer and in several renderers: every occurrence is a Qt
    /// object of its own and binds the element's feedback. The primary occurrence (<see cref="OwnerOf(Element)"/>) is the
    /// first occurrence in traversal order in the owner renderer, the first renderer that showed the element (registration
    /// order): only it reports state values into the feedback (<see cref="Elements.FeedbackRouter"/>), Snapshot and
    /// TooltipPreview locate it (<see cref="TryLocate"/>). Events of all occurrences in all renderers reach the feedback. The
    /// owner's reconciler keeps the primary up to date inside its tree (see <see cref="Reconciler"/>).
    /// </para>
    /// <para>
    /// When the last occurrence in the owner renderer lets go while other renderers show the element, the owner's
    /// reconciler hands it over at the end of its update (<see cref="HandOver"/>): the next renderer in registration order
    /// becomes the owner and picks its first occurrence in traversal order as primary in its next update. Until then the
    /// element has no primary (no state values, <see cref="TryLocate"/> fails).
    /// </para>
    /// <para>
    /// A <see cref="ElementType.Video"/> is shown at one place only, app-wide (two occurrences would be two players): the
    /// first occurrence in traversal order of the first renderer plays, the others are placeholders registered here as
    /// duplicates, which puts <see cref="VideoDuplicateWarning"/> into the element's feedback slot. When the last occurrence
    /// lets go while duplicates wait, their reconcilers are invalidated, so their next update visits the whole tree and
    /// one of them takes over.
    /// </para>
    /// <para>
    /// Occurrences are kept until their node lets go of the element (its description changes, it is destroyed, or its
    /// reconciler is cleared), not reset every frame: subtrees skipped by the reference-equality shortcut are not
    /// visited.
    /// </para>
    /// <para>
    /// Place fallback: an element node outputs a new instance whenever an input changes, always with the same feedback
    /// slot. Snapshot and TooltipPreview may run before the renderer of the same frame, when the registry still holds the
    /// previous instance. <see cref="TryLocate"/> then falls back to the last shown instance of the element's slot (same
    /// window, same object). The slot entry goes with the entry of that instance (no extra lifetime, no leak).
    /// </para>
    /// One instance per vvvv app (<see cref="ForApp"/>); tests and the SmokeTest create their own.
    /// Main thread only. Allocation-free as long as the set of occurrences does not change.
    /// </summary>
    internal sealed class OccurrenceRegistry
    {
        /// <summary>Warning written to the feedback slot of a video that is used more than once.</summary>
        public const string VideoDuplicateWarning = "Video is used more than once (in one tree or across renderers); only the first occurrence plays.";

        /// <summary>
        /// The registry shared by all renderers of one vvvv app (service of its <see cref="AppHost"/>). Deliberately
        /// not static: statics survive an app restart (F8/F5) in the same process, and ownership entries of a
        /// renderer that was not disposed cleanly would turn videos of the restarted app into false duplicates.
        /// A new app host gets a new, empty registry.
        /// </summary>
        public static OccurrenceRegistry ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new OccurrenceRegistry(), allowToAskParent: false);

        /// <summary>Whether <paramref name="element"/> may be shown at one place only (app-wide, inside one tree, too).</summary>
        public static bool IsExclusive(Element element) => element.Type == ElementType.Video;

        /// <summary>Occurrences of an element in one renderer that is not (yet) its owner.</summary>
        private sealed class RendererOccurrences(Reconciler reconciler, List<Node> nodes)
        {
            public readonly Reconciler Reconciler = reconciler;
            public readonly List<Node> Nodes = nodes; // unordered
        }

        private struct Entry
        {
            public Reconciler? OwnerReconciler; // set while the element has at least one occurrence
            public Node? Primary;               // null only inside an update after the primary let go, or after a hand-over
            public List<Node>? Others;          // further occurrences in the owner's tree, unordered
            public List<RendererOccurrences>? Elsewhere;  // occurrences in other renderers, in registration order
            public List<Reconciler>? Waiting;   // one entry per ignored occurrence of a video (placeholder)

            public readonly bool HasOwnerOccurrences => Primary is not null || Others is { Count: > 0 };
        }

        private readonly Dictionary<Element, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<List<Node>> _lists = new();
        private readonly Dictionary<FeedbackSlot, Element> _shownBySlot = new(); // feedback slot → last instance shown with it

        /// <summary>Number of elements currently shown or waiting as duplicates (tests).</summary>
        public int Count => _entries.Count;

        /// <summary>Number of feedback slot with a shown instance (tests).</summary>
        public int SlotCount => _shownBySlot.Count;

        /// <summary>Primary occurrence of <paramref name="element"/> or null; <paramref name="reconciler"/> = the owner renderer's reconciler.</summary>
        public Node? OwnerOf(Element element, out Reconciler? reconciler)
        {
            if (_entries.TryGetValue(element, out var e))
            {
                reconciler = e.OwnerReconciler;
                return e.Primary;
            }
            reconciler = null;
            return null;
        }

        /// <summary>Primary occurrence of <paramref name="element"/> or null.</summary>
        public Node? OwnerOf(Element element) => OwnerOf(element, out _);

        /// <summary>Reconciler of the owner renderer of <paramref name="element"/> (the one with the primary occurrence), or null if it is not shown.</summary>
        public Reconciler? OwnerReconcilerOf(Element element)
            => _entries.TryGetValue(element, out var e) ? e.OwnerReconciler : null;

        /// <summary>Number of real occurrences (Qt objects) of <paramref name="element"/> in all renderers.</summary>
        public int OccurrencesOf(Element element)
        {
            if (!_entries.TryGetValue(element, out var e))
                return 0;
            var count = (e.Primary is null ? 0 : 1) + (e.Others?.Count ?? 0);
            if (e.Elsewhere is { } elsewhere)
            {
                foreach (var g in elsewhere)
                    count += g.Nodes.Count;
            }
            return count;
        }

        /// <summary>Further (not primary) occurrences of <paramref name="element"/> in the owner's tree, unordered; null if none.</summary>
        public List<Node>? OthersOf(Element element)
            => _entries.TryGetValue(element, out var e) ? e.Others : null;

        /// <summary>
        /// Native window and object id of the primary occurrence of <paramref name="element"/> (Snapshot). An
        /// instance no renderer has seen yet is located by its place: the last shown instance of its feedback slot (a
        /// node's new instance before the renderer of this frame ran). False if it is not shown in a renderer (no
        /// occurrence, no shown instance of its slot, no primary right after a hand-over, or the reconciler has no window).
        /// </summary>
        public bool TryLocate(Element element, out SceneHandle window, out uint id)
        {
            if (!_entries.ContainsKey(element) && element.Feedback.Feedback is not null && _shownBySlot.TryGetValue(element.Feedback, out var shown))
                element = shown;
            var node = OwnerOf(element, out var reconciler);
            if (node is not null && !node.IsIgnored && reconciler is not null && !reconciler.Window.IsNull)
            {
                window = reconciler.Window;
                id = node.Id;
                return true;
            }
            window = SceneHandle.Null;
            id = 0;
            return false;
        }

        /// <summary>Number of ignored occurrences of <paramref name="element"/> (a video shown elsewhere).</summary>
        public int DuplicatesOf(Element element) => _entries.TryGetValue(element, out var e) ? e.Waiting?.Count ?? 0 : 0;

        /// <summary>
        /// Registers <paramref name="node"/> as an occurrence of <paramref name="element"/> in <paramref name="reconciler"/>.
        /// Returns true if it is the only occurrence and therefore the primary one; it then makes
        /// <paramref name="reconciler"/> the owner if the element had none. Otherwise it is added as a further occurrence
        /// (in the owner's tree, or in the list of its own renderer) and the owner's reconciler decides about the order.
        /// A video must not be registered from a second renderer (the caller checks <see cref="IsExclusive"/>).
        /// </summary>
        public bool AddOccurrence(Element element, Node node, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, element, out _);
            if (element.Feedback.Feedback is not null)
                _shownBySlot[element.Feedback] = element;
            if (entry.OwnerReconciler is null || ReferenceEquals(entry.OwnerReconciler, reconciler))
            {
                entry.OwnerReconciler = reconciler;
                if (!entry.HasOwnerOccurrences)
                {
                    entry.Primary = node;
                    return true;
                }
                (entry.Others ??= RentList()).Add(node);
                return false;
            }

            var elsewhere = entry.Elsewhere ??= new List<RendererOccurrences>(1);
            foreach (var g in elsewhere)
            {
                if (ReferenceEquals(g.Reconciler, reconciler))
                {
                    g.Nodes.Add(node);
                    return false;
                }
            }
            var nodes = RentList();
            nodes.Add(node);
            elsewhere.Add(new RendererOccurrences(reconciler, nodes));
            return false;
        }

        /// <summary>Makes the occurrence <paramref name="node"/> of the owner's tree the primary one; the former primary (if any) becomes a further occurrence.</summary>
        public void SetPrimary(Element element, Node node)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, element);
            if (Unsafe.IsNullRef(ref entry) || ReferenceEquals(entry.Primary, node))
                return;
            if (entry.Others is null || !entry.Others.Remove(node))
                return;
            if (entry.Primary is { } old)
                entry.Others.Add(old);
            entry.Primary = node;
        }

        /// <summary>
        /// Removes the occurrence <paramref name="node"/> of <paramref name="reconciler"/> (no-op if it is none, e.g. after a
        /// video was taken over by an earlier occurrence). Returns true if the owner renderer lost its primary while the
        /// element is still shown (further occurrences in its tree, or in other renderers): the caller picks the new primary
        /// or hands the element over (<see cref="HandOver"/>). When no occurrence is left anywhere, waiting duplicates get
        /// their reconcilers invalidated (unless <paramref name="invalidateWaiting"/> is false: a video taken over inside one
        /// tree keeps its waiters).
        /// </summary>
        public bool RemoveOccurrence(Element element, Node node, Reconciler reconciler, bool invalidateWaiting = true)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, element);
            if (Unsafe.IsNullRef(ref entry))
                return false;

            if (!ReferenceEquals(entry.OwnerReconciler, reconciler))
            {
                RemoveElsewhere(ref entry, node, reconciler);
                return false;
            }

            bool wasPrimary = ReferenceEquals(entry.Primary, node);
            if (wasPrimary)
                entry.Primary = null;
            else if (entry.Others is null || !entry.Others.Remove(node))
                return false;

            if (entry.HasOwnerOccurrences)
                return wasPrimary;
            if (entry.Others is { } others)
            {
                ReturnList(others);
                entry.Others = null;
            }
            // Other renderers still show it: the owner hands it over at the end of its update, so a move inside its own
            // tree in the same pass keeps the primary there.
            if (entry.Elsewhere is not null)
                return true;

            entry.OwnerReconciler = null;
            if (entry.Waiting is null)
            {
                RemoveEntry(element);
                return false;
            }
            if (invalidateWaiting)
            {
                foreach (var r in entry.Waiting)
                    r.Invalidate();
            }
            return false;
        }

        /// <summary>Removes <paramref name="node"/> from the occurrences of <paramref name="reconciler"/>, which is not the owner.</summary>
        private void RemoveElsewhere(ref Entry entry, Node node, Reconciler reconciler)
        {
            if (entry.Elsewhere is not { } elsewhere)
                return;
            for (int i = 0; i < elsewhere.Count; i++)
            {
                var g = elsewhere[i];
                if (!ReferenceEquals(g.Reconciler, reconciler))
                    continue;
                if (!g.Nodes.Remove(node) || g.Nodes.Count > 0)
                    return;
                ReturnList(g.Nodes);
                elsewhere.RemoveAt(i);
                if (elsewhere.Count == 0)
                    entry.Elsewhere = null;
                return;
            }
        }

        /// <summary>
        /// Called by the owner's reconciler at the end of its update for an element that has no occurrence left in its
        /// tree: the next renderer in registration order becomes the owner, its occurrences become the owner's
        /// occurrences without a primary, and its reconciler is told to pick the primary
        /// (<see cref="Reconciler.TakeOver"/>). Returns false if there is nothing to hand over.
        /// </summary>
        public bool HandOver(Element element, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, element);
            if (Unsafe.IsNullRef(ref entry) || !ReferenceEquals(entry.OwnerReconciler, reconciler) || entry.HasOwnerOccurrences
                || entry.Elsewhere is not { Count: > 0 } elsewhere)
                return false;
            var next = elsewhere[0];
            elsewhere.RemoveAt(0);
            if (elsewhere.Count == 0)
                entry.Elsewhere = null;
            entry.OwnerReconciler = next.Reconciler;
            entry.Primary = null;
            entry.Others = next.Nodes;
            next.Reconciler.TakeOver(element);
            return true;
        }

        /// <summary>Registers an ignored occurrence of a video held by <paramref name="reconciler"/> and sets the warning.</summary>
        public void AddDuplicate(Element element, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, element, out _);
            (entry.Waiting ??= new List<Reconciler>()).Add(reconciler);
            if (element.Feedback.State is { } state)
                state.Warning = VideoDuplicateWarning;
        }

        /// <summary>Removes one ignored occurrence; clears the warning when none is left.</summary>
        public void RemoveDuplicate(Element element, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, element);
            if (Unsafe.IsNullRef(ref entry) || entry.Waiting is null || !entry.Waiting.Remove(reconciler))
                return;
            if (entry.Waiting.Count > 0)
                return;
            entry.Waiting = null;
            var slot = element.Feedback;
            if (slot.Feedback is not null && slot.Feedback.TryGet(slot.Index, out var state) && state.Warning == VideoDuplicateWarning)
                state.Warning = null;
            if (entry.OwnerReconciler is null)
                RemoveEntry(element);
        }

        /// <summary>Removes the entry of <paramref name="element"/> and its slot entry if the slot still points to it.</summary>
        private void RemoveEntry(Element element)
        {
            _entries.Remove(element);
            var slot = element.Feedback;
            if (slot.Feedback is not null && _shownBySlot.TryGetValue(slot, out var shown) && ReferenceEquals(shown, element))
                _shownBySlot.Remove(slot);
        }

        private List<Node> RentList() => _lists.Count > 0 ? _lists.Pop() : new List<Node>(2);

        private void ReturnList(List<Node> list)
        {
            list.Clear();
            if (_lists.Count < 16)
                _lists.Push(list);
        }

        // --- Input handlers: one interaction node is active at one
        // handler only. Keyed by the feedback of the interaction node. Unlike elements the current owner always keeps it
        // (no take-over by traversal order); further occurrences wait as placeholders until the owner lets go. ---

        /// <summary>Warning written to the feedback slot of an interaction node that is used more than once.</summary>
        public const string InteractionDuplicateWarning = "Interaction is used more than once (at several elements, in one chain twice or across renderers); only the first occurrence is active. Use one interaction node per element.";

        private struct HandlerEntry
        {
            public HandlerNode? Owner;
            public List<Reconciler>? Waiting; // one entry per ignored occurrence (placeholder)
        }

        private readonly Dictionary<FeedbackSlot, HandlerEntry> _handlers = new();

        /// <summary>Number of interaction feedback slots currently active or waiting as duplicates (tests).</summary>
        public int HandlerCount => _handlers.Count;

        /// <summary>Handler that currently serves the interaction with feedback <paramref name="key"/>, or null.</summary>
        public HandlerNode? HandlerOwnerOf(FeedbackSlot key) => _handlers.TryGetValue(key, out var e) ? e.Owner : null;

        /// <summary>Makes <paramref name="handler"/> the owner of its key (the caller checked that there is none).</summary>
        public void SetHandlerOwner(HandlerNode handler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_handlers, handler.Key, out _);
            entry.Owner = handler;
        }

        /// <summary>Gives up the ownership of <paramref name="handler"/>; waiting duplicates get their reconcilers invalidated.</summary>
        public void ReleaseHandler(HandlerNode handler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_handlers, handler.Key);
            if (Unsafe.IsNullRef(ref entry) || !ReferenceEquals(entry.Owner, handler))
                return;
            entry.Owner = null;
            if (entry.Waiting is null)
            {
                _handlers.Remove(handler.Key);
                return;
            }
            foreach (var r in entry.Waiting)
                r.Invalidate();
        }

        /// <summary>Registers an ignored occurrence of <paramref name="key"/> held by <paramref name="reconciler"/> and sets the warning.</summary>
        public void AddHandlerDuplicate(FeedbackSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_handlers, key, out _);
            (entry.Waiting ??= new List<Reconciler>()).Add(reconciler);
            if (key.State is { } state)
                state.Warning = InteractionDuplicateWarning;
        }

        /// <summary>Removes one ignored occurrence of <paramref name="key"/>; clears the warning when none is left.</summary>
        public void RemoveHandlerDuplicate(FeedbackSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_handlers, key);
            if (Unsafe.IsNullRef(ref entry) || entry.Waiting is null || !entry.Waiting.Remove(reconciler))
                return;
            if (entry.Waiting.Count > 0)
                return;
            entry.Waiting = null;
            if (key.Feedback is not null && key.Feedback.TryGet(key.Index, out var state) && state.Warning == InteractionDuplicateWarning)
                state.Warning = null;
            if (entry.Owner is null)
                _handlers.Remove(key);
        }
    }
}
