using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VL.Core;
using VL.Qt.Elements;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// App-wide record of which handler serves an interaction node: one interaction node is active at one handler only,
    /// across all renderers, keyed by its feedback slot. Unlike elements (<see cref="OccurrenceRegistry"/>) the active
    /// handler keeps it as long as it exists (no take-over by traversal order); further occurrences are placeholders
    /// (<see cref="AddPlaceholder"/>, <see cref="InteractionPlaceholderWarning"/>) until the active handler lets go, then
    /// their reconcilers are invalidated and the first to update takes over.
    /// One instance per vvvv app (<see cref="ForApp"/>); tests and the SmokeTest create their own. Main thread only.
    /// </summary>
    internal sealed class InteractionRegistry
    {
        /// <summary>Warning written to the feedback slot of an interaction node that is used more than once.</summary>
        public const string InteractionPlaceholderWarning = "Interaction is used more than once (at several elements, in one chain twice or across renderers); only the first occurrence is active. Use one interaction node per element.";

        /// <summary>Source of <see cref="InteractionPlaceholderWarning"/> in the message store of the slot.</summary>
        internal static readonly object InteractionPlaceholderSource = new();

        /// <summary>
        /// The registry shared by all renderers of one vvvv app (service of its <see cref="AppHost"/>); not static for the
        /// same reason as <see cref="OccurrenceRegistry.ForApp"/>.
        /// </summary>
        public static InteractionRegistry ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new InteractionRegistry(), allowToAskParent: false);

        private struct Entry
        {
            public HandlerMirror? Active;
            public Reconciler? Owner;              // reconciler of Active
            public List<Reconciler>? Placeholders; // one entry per placeholder
        }

        private readonly Dictionary<FeedbackSlot, Entry> _entries = new();

        /// <summary>Number of interaction feedback slots currently active or waiting as placeholders (tests).</summary>
        public int Count => _entries.Count;

        /// <summary>Handler that currently serves the interaction with feedback <paramref name="key"/>, or null.</summary>
        public HandlerMirror? ActiveHandlerOf(FeedbackSlot key) => _entries.TryGetValue(key, out var e) ? e.Active : null;

        /// <summary>Makes <paramref name="handler"/> of <paramref name="reconciler"/> the active handler of its key (the caller checked
        /// that there is none).</summary>
        public void Activate(HandlerMirror handler, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, handler.Key, out _);
            entry.Active = handler;
            entry.Owner = reconciler;
        }

        /// <summary>Lets <paramref name="handler"/> go; the reconcilers of waiting placeholders are invalidated.</summary>
        public void Deactivate(HandlerMirror handler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, handler.Key);
            if (Unsafe.IsNullRef(ref entry) || !ReferenceEquals(entry.Active, handler))
                return;
            entry.Active = null;
            entry.Owner = null;
            if (entry.Placeholders is null)
            {
                _entries.Remove(handler.Key);
                return;
            }
            foreach (var r in entry.Placeholders)
                r.Invalidate();
        }

        /// <summary>Registers a placeholder of <paramref name="key"/> held by <paramref name="reconciler"/> and sets the warning.</summary>
        public void AddPlaceholder(FeedbackSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, key, out _);
            (entry.Placeholders ??= new List<Reconciler>()).Add(reconciler);
            key.SetMessage(InteractionPlaceholderSource, MessageKind.Warning, InteractionPlaceholderWarning);
        }

        /// <summary>Removes one placeholder of <paramref name="key"/>; clears the warning when none is left.</summary>
        public void RemovePlaceholder(FeedbackSlot key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
            if (Unsafe.IsNullRef(ref entry) || entry.Placeholders is null || !entry.Placeholders.Remove(reconciler))
                return;
            if (entry.Placeholders.Count > 0)
                return;
            entry.Placeholders = null;
            key.SetMessage(InteractionPlaceholderSource, MessageKind.Warning, null);
            if (entry.Active is null)
                _entries.Remove(key);
        }

        /// <summary>
        /// Removes every trace of <paramref name="reconciler"/>: its placeholders and the handlers it made active, also those
        /// a visit cut short by an exception registered but never linked into its mirror; the reconcilers of waiting
        /// placeholders of other renderers are invalidated. For the resync (<see cref="Reconciler.Discard"/>); allocates.
        /// </summary>
        public void RemoveAll(Reconciler reconciler)
        {
            foreach (var key in new List<FeedbackSlot>(_entries.Keys))
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
                if (entry.Placeholders is { } placeholders && placeholders.RemoveAll(r => ReferenceEquals(r, reconciler)) > 0 && placeholders.Count == 0)
                {
                    entry.Placeholders = null;
                    key.SetMessage(InteractionPlaceholderSource, MessageKind.Warning, null);
                }
                if (ReferenceEquals(entry.Owner, reconciler))
                {
                    entry.Active = null;
                    entry.Owner = null;
                    if (entry.Placeholders is { } waiting)
                    {
                        foreach (var r in waiting)
                            r.Invalidate();
                    }
                }
                if (entry.Active is null && entry.Placeholders is null)
                    _entries.Remove(key);
            }
        }
    }
}
