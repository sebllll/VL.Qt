using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VL.Core;
using VL.Qt.Elements;

namespace VL.Qt
{
﻿    /// <summary>
    /// App-wide record of where each element instance is shown (docs/decisions/reconciler-identity.md, #455).
    /// <para>
    /// Inside one renderer an element may hang at several places: every occurrence is a Qt object of its own and binds
    /// the element's sink. The first occurrence in traversal order is the primary one (<see cref="OwnerOf(Element)"/>):
    /// only it reports state values into the sink (<see cref="Elements.SinkRegistry"/>), Snapshot grabs it
    /// (<see cref="TryLocate"/>). The reconciler keeps the primary up to date (see <see cref="Reconciler"/>).
    /// </para>
    /// <para>
    /// Across renderers the first renderer that shows an element keeps it; occurrences in other renderers are ignored by
    /// their reconcilers (placeholders) and registered here as duplicates, which puts <see cref="DuplicateWarning"/> into
    /// the element's sink slot. A <see cref="ElementType.Video"/> is shown at one place only, also inside one tree (two
    /// occurrences would be two players): the first occurrence in traversal order plays, the others are placeholders
    /// with <see cref="VideoDuplicateWarning"/>.
    /// </para>
    /// <para>
    /// Occurrences are kept until their node lets go of the element (its description changes, it is destroyed, or its
    /// reconciler is cleared), not reset every frame: subtrees skipped by the reference-equality shortcut are not
    /// visited. When the last occurrence lets go while duplicates wait, their reconcilers are invalidated, so their
    /// next update visits the whole tree and one of them takes over.
    /// </para>
    /// <para>
    /// Place fallback (#548): an element node outputs a new instance whenever an input changes, always with the same sink
    /// slot. Snapshot and TooltipPreview may run before the renderer of the same frame, when the registry still holds the
    /// previous instance. <see cref="TryLocate"/> then falls back to the last shown instance of the element's slot (same
    /// window, same object). The slot entry goes with the entry of that instance (no extra lifetime, no leak).
    /// </para>
    /// One instance per vvvv app (<see cref="ForApp"/>); tests and the SmokeTest create their own.
    /// Main thread only. Allocation-free as long as the set of occurrences does not change.
    /// </summary>
    internal sealed class SceneRegistry
    {
        /// <summary>Warning written to the sink slot of an element that is used in more than one renderer.</summary>
        public const string DuplicateWarning = "Element is used in another renderer too; only the first renderer shows it.";

        /// <summary>Warning written to the sink slot of a video that is used more than once.</summary>
        public const string VideoDuplicateWarning = "Video is used more than once (in one tree or across renderers); only the first occurrence plays.";

        /// <summary>
        /// The registry shared by all renderers of one vvvv app (service of its <see cref="AppHost"/>). Deliberately
        /// not static: statics survive an app restart (F8/F5) in the same process, and ownership entries of a
        /// renderer that was not disposed cleanly would turn elements of the restarted app into false duplicates.
        /// A new app host gets a new, empty registry.
        /// </summary>
        public static SceneRegistry ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new SceneRegistry(), allowToAskParent: false);

        /// <summary>Whether <paramref name="element"/> may be shown at one place only (inside one tree, too).</summary>
        public static bool IsExclusive(Element element) => element.Type == ElementType.Video;

        /// <summary>Warning for an ignored occurrence of <paramref name="element"/>.</summary>
        public static string WarningFor(Element element) => IsExclusive(element) ? VideoDuplicateWarning : DuplicateWarning;

        struct Entry
        {
            public Reconciler? OwnerReconciler; // set while the element has at least one occurrence
            public Node? Primary;               // null only inside an update, after the primary let go
            public List<Node>? Others;          // further occurrences in the owner's tree, unordered
            public List<Reconciler>? Waiting;   // one entry per ignored occurrence (placeholder)

            public readonly bool HasOccurrences => Primary is not null || Others is { Count: > 0 };
        }

        readonly Dictionary<Element, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        readonly Stack<List<Node>> _lists = new();
        readonly Dictionary<SinkSlot, Element> _shownBySlot = new(); // sink slot → last instance shown with it (#548)

        /// <summary>Number of elements currently shown or waiting as duplicates (tests).</summary>
        public int Count => _entries.Count;

        /// <summary>Number of sink slots with a shown instance (tests).</summary>
        public int SlotCount => _shownBySlot.Count;

        /// <summary>Primary occurrence of <paramref name="element"/> or null; <paramref name="reconciler"/> = the reconciler that shows it.</summary>
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

        /// <summary>Reconciler that shows <paramref name="element"/>, or null if it is not shown.</summary>
        public Reconciler? OwnerReconcilerOf(Element element)
            => _entries.TryGetValue(element, out var e) ? e.OwnerReconciler : null;

        /// <summary>Number of real occurrences (Qt objects) of <paramref name="element"/>.</summary>
        public int OccurrencesOf(Element element)
            => _entries.TryGetValue(element, out var e) ? (e.Primary is null ? 0 : 1) + (e.Others?.Count ?? 0) : 0;

        /// <summary>Further (not primary) occurrences of <paramref name="element"/>, unordered; null if none.</summary>
        public List<Node>? OthersOf(Element element)
            => _entries.TryGetValue(element, out var e) ? e.Others : null;

        /// <summary>
        /// Native window and object id of the primary occurrence of <paramref name="element"/> (#363, Snapshot). An
        /// instance no renderer has seen yet is located by its place: the last shown instance of its sink slot (#548, a
        /// node's new instance before the renderer of this frame ran). False if it is not shown in a renderer (no
        /// occurrence, no shown instance of its slot, or the reconciler has no window).
        /// </summary>
        public bool TryLocate(Element element, out IntPtr window, out uint id)
        {
            if (!_entries.ContainsKey(element) && element.Sink.Sink is not null && _shownBySlot.TryGetValue(element.Sink, out var shown))
                element = shown;
            var node = OwnerOf(element, out var reconciler);
            if (node is not null && !node.IsIgnored && reconciler is not null && reconciler.Window != IntPtr.Zero)
            {
                window = reconciler.Window;
                id = node.Id;
                return true;
            }
            window = IntPtr.Zero;
            id = 0;
            return false;
        }

        /// <summary>Number of ignored occurrences of <paramref name="element"/>.</summary>
        public int DuplicatesOf(Element element) => _entries.TryGetValue(element, out var e) ? e.Waiting?.Count ?? 0 : 0;

        /// <summary>
        /// Registers <paramref name="node"/> as an occurrence of <paramref name="element"/> in <paramref name="reconciler"/>
        /// (the caller checked that no other reconciler shows it). Returns true if it is the only occurrence and therefore
        /// the primary one; otherwise it is added as a further occurrence and the caller decides about the order.
        /// </summary>
        public bool AddOccurrence(Element element, Node node, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, element, out _);
            entry.OwnerReconciler = reconciler;
            if (element.Sink.Sink is not null)
                _shownBySlot[element.Sink] = element;
            if (!entry.HasOccurrences)
            {
                entry.Primary = node;
                return true;
            }
            (entry.Others ??= RentList()).Add(node);
            return false;
        }

        /// <summary>Makes the occurrence <paramref name="node"/> the primary one; the former primary (if any) becomes a further occurrence.</summary>
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
        /// Removes the occurrence <paramref name="node"/> (no-op if it is none, e.g. after a video was taken over by an
        /// earlier occurrence). Returns true if it was the primary one and further occurrences remain: the caller picks
        /// the new primary. When no occurrence is left, waiting duplicates get their reconcilers invalidated (unless
        /// <paramref name="invalidateWaiting"/> is false: a video taken over inside one tree keeps its waiters).
        /// </summary>
        public bool RemoveOccurrence(Element element, Node node, bool invalidateWaiting = true)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, element);
            if (Unsafe.IsNullRef(ref entry))
                return false;
            bool wasPrimary = ReferenceEquals(entry.Primary, node);
            if (wasPrimary)
                entry.Primary = null;
            else if (entry.Others is null || !entry.Others.Remove(node))
                return false;

            if (entry.HasOccurrences)
                return wasPrimary;

            if (entry.Others is { } others)
            {
                ReturnList(others);
                entry.Others = null;
            }
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

        /// <summary>Registers an ignored occurrence held by <paramref name="reconciler"/> and sets the warning.</summary>
        public void AddDuplicate(Element element, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, element, out _);
            (entry.Waiting ??= new List<Reconciler>()).Add(reconciler);
            if (element.Sink.State is { } state)
                state.Warning = WarningFor(element);
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
            var slot = element.Sink;
            if (slot.Sink is not null && slot.Sink.TryGet(slot.Index, out var state) && state.Warning == WarningFor(element))
                state.Warning = null;
            if (!entry.HasOccurrences)
                RemoveEntry(element);
        }

        /// <summary>Removes the entry of <paramref name="element"/> and its slot entry if the slot still points to it.</summary>
        void RemoveEntry(Element element)
        {
            _entries.Remove(element);
            var slot = element.Sink;
            if (slot.Sink is not null && _shownBySlot.TryGetValue(slot, out var shown) && ReferenceEquals(shown, element))
                _shownBySlot.Remove(slot);
        }

        List<Node> RentList() => _lists.Count > 0 ? _lists.Pop() : new List<Node>(2);

        void ReturnList(List<Node> list)
        {
            list.Clear();
            if (_lists.Count < 16)
                _lists.Push(list);
        }

        // --- Input handlers (#319): one interaction node is active at one
        // handler only. Keyed by the sink of the interaction node. Unlike elements the current owner always keeps it
        // (no take-over by traversal order); further occurrences wait as placeholders until the owner lets go. ---

        /// <summary>Warning written to the sink slot of an interaction node that is used more than once.</summary>
        public const string InteractionDuplicateWarning = "Interaction is used more than once (at several elements, in one chain twice or across renderers); only the first occurrence is active. Use one interaction node per element.";

        struct HandlerEntry
        {
            public HandlerNode? Owner;
            public List<Reconciler>? Waiting; // one entry per ignored occurrence (placeholder)
        }

        readonly Dictionary<SinkSlot, HandlerEntry> _handlers = new();

        /// <summary>Number of interaction sinks currently active or waiting as duplicates (tests).</summary>
        public int HandlerCount => _handlers.Count;

        /// <summary>Handler that currently serves the interaction with sink <paramref name="key"/>, or null.</summary>
        public HandlerNode? HandlerOwnerOf(SinkSlot key) => _handlers.TryGetValue(key, out var e) ? e.Owner : null;

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
        public void AddHandlerDuplicate(SinkSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_handlers, key, out _);
            (entry.Waiting ??= new List<Reconciler>()).Add(reconciler);
            if (key.State is { } state)
                state.Warning = InteractionDuplicateWarning;
        }

        /// <summary>Removes one ignored occurrence of <paramref name="key"/>; clears the warning when none is left.</summary>
        public void RemoveHandlerDuplicate(SinkSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_handlers, key);
            if (Unsafe.IsNullRef(ref entry) || entry.Waiting is null || !entry.Waiting.Remove(reconciler))
                return;
            if (entry.Waiting.Count > 0)
                return;
            entry.Waiting = null;
            if (key.Sink is not null && key.Sink.TryGet(key.Index, out var state) && state.Warning == InteractionDuplicateWarning)
                state.Warning = null;
            if (entry.Owner is null)
                _handlers.Remove(key);
        }
    }
}
