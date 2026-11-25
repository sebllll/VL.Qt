using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.Interop;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// App-wide record of where each element is shown, by <see cref="OccurrenceKey"/>: owner renderer, primary occurrence
    /// (<see cref="PrimaryOccurrences"/> keeps it up to date in the owner's tree), further occurrences, hand-over to the next
    /// renderer, and the placeholders of a <see cref="ElementType.Video"/>, which is shown once only. One per vvvv app
    /// (<see cref="ForApp"/>); main thread only; allocation-free while the set of occurrences does not change.
    /// Design: docs/design/reconciler.md, "Occurrences and the primary" and "Placeholders"; docs/design/reconciler-identity.md, "Occurrences".
    /// </summary>
    internal sealed class OccurrenceRegistry
    {
        /// <summary>Warning written to the feedback slot of a video that is used more than once.</summary>
        public const string VideoPlaceholderWarning = "Video is used more than once (in one tree or across renderers); only the first occurrence plays.";

        /// <summary>Source of <see cref="VideoPlaceholderWarning"/> in the message store of the slot.</summary>
        internal static readonly object VideoPlaceholderSource = new();

        /// <summary>
        /// The registry shared by all renderers of one vvvv app (service of its <see cref="AppHost"/>). Deliberately
        /// not static: statics survive an app restart (F8/F5) in the same process, and occurrences of a renderer that was
        /// not disposed cleanly would turn videos of the restarted app into placeholders. A new app host gets a new,
        /// empty registry.
        /// </summary>
        public static OccurrenceRegistry ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new OccurrenceRegistry(), allowToAskParent: false);

        /// <summary>Whether <paramref name="element"/> may be shown at one place only (app-wide, inside one tree, too).</summary>
        public static bool IsShownOnce(Element element) => element.Type == ElementType.Video;

        /// <summary>Occurrences of an element in one renderer that is not (yet) its owner.</summary>
        private sealed class RendererOccurrences(Reconciler reconciler, List<ItemMirror> items)
        {
            public readonly Reconciler Reconciler = reconciler;
            public readonly List<ItemMirror> Items = items; // unordered
        }

        private struct Entry
        {
            public Reconciler? Owner;                    // set while the element has at least one occurrence
            public ItemMirror? Primary;                  // null only inside an update after the primary let go, or after a hand-over
            public List<ItemMirror>? Further;            // further occurrences in the owner's tree, unordered
            public List<RendererOccurrences>? Elsewhere; // occurrences in other renderers, in registration order
            public List<Reconciler>? Placeholders;       // one entry per placeholder of a video

            public readonly bool HasOwnerOccurrences => Primary is not null || Further is { Count: > 0 };
        }

        private readonly Dictionary<OccurrenceKey, Entry> _entries = new();
        private readonly Stack<List<ItemMirror>> _lists = new();

        /// <summary>Number of elements currently shown or waiting as placeholders (tests).</summary>
        public int Count => _entries.Count;

        /// <summary>Primary occurrence of <paramref name="key"/> or null; <paramref name="owner"/> = the owner renderer's reconciler.</summary>
        public ItemMirror? PrimaryOf(OccurrenceKey key, out Reconciler? owner)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                owner = e.Owner;
                return e.Primary;
            }
            owner = null;
            return null;
        }

        /// <summary>Primary occurrence of <paramref name="key"/> or null.</summary>
        public ItemMirror? PrimaryOf(OccurrenceKey key) => PrimaryOf(key, out _);

        /// <summary>Reconciler of the owner renderer of <paramref name="key"/> (the one with the primary occurrence), or null if it is not shown.</summary>
        public Reconciler? OwnerOf(OccurrenceKey key)
            => _entries.TryGetValue(key, out var e) ? e.Owner : null;

        /// <summary>Number of real occurrences (Qt objects) of <paramref name="key"/> in all renderers.</summary>
        public int OccurrencesOf(OccurrenceKey key)
        {
            if (!_entries.TryGetValue(key, out var e))
                return 0;
            var count = (e.Primary is null ? 0 : 1) + (e.Further?.Count ?? 0);
            if (e.Elsewhere is { } elsewhere)
            {
                foreach (var g in elsewhere)
                    count += g.Items.Count;
            }
            return count;
        }

        /// <summary>Further (not primary) occurrences of <paramref name="key"/> in the owner's tree, unordered; null if none.</summary>
        public List<ItemMirror>? FurtherOccurrencesOf(OccurrenceKey key)
            => _entries.TryGetValue(key, out var e) ? e.Further : null;

        /// <summary>Number of placeholders of <paramref name="key"/> (a video shown elsewhere).</summary>
        public int PlaceholdersOf(OccurrenceKey key) => _entries.TryGetValue(key, out var e) ? e.Placeholders?.Count ?? 0 : 0;

        /// <summary>
        /// Native window and object id of the primary occurrence of <paramref name="element"/> (Snapshot, TooltipPreview).
        /// A new instance of a shown node is found before the renderer of this frame ran, because the key is its slot.
        /// False if it is not shown in a renderer (no occurrence, no primary right after a hand-over, or the reconciler has
        /// no window).
        /// </summary>
        public bool TryLocate(Element element, out SceneHandle window, out uint id)
        {
            var item = PrimaryOf(element, out var owner);
            if (item is not null && !item.IsPlaceholder && owner is not null && !owner.Window.IsNull)
            {
                window = owner.Window;
                id = item.Id;
                return true;
            }
            window = SceneHandle.Null;
            id = 0;
            return false;
        }

        /// <summary>
        /// Registers <paramref name="item"/> as an occurrence of <paramref name="key"/> in <paramref name="reconciler"/>.
        /// Returns true if it is the only occurrence and therefore the primary one; it then makes
        /// <paramref name="reconciler"/> the owner if the element had none. Otherwise it is added as a further occurrence
        /// (in the owner's tree, or in the list of its own renderer) and the owner's reconciler decides about the order.
        /// A video must not be registered from a second renderer (the caller checks <see cref="IsShownOnce"/>).
        /// </summary>
        public bool AddOccurrence(OccurrenceKey key, ItemMirror item, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, key, out _);
            if (entry.Owner is null || ReferenceEquals(entry.Owner, reconciler))
            {
                entry.Owner = reconciler;
                if (!entry.HasOwnerOccurrences)
                {
                    entry.Primary = item;
                    return true;
                }
                (entry.Further ??= RentList()).Add(item);
                return false;
            }

            var elsewhere = entry.Elsewhere ??= new List<RendererOccurrences>(1);
            foreach (var g in elsewhere)
            {
                if (ReferenceEquals(g.Reconciler, reconciler))
                {
                    g.Items.Add(item);
                    return false;
                }
            }
            var items = RentList();
            items.Add(item);
            elsewhere.Add(new RendererOccurrences(reconciler, items));
            return false;
        }

        /// <summary>Makes the occurrence <paramref name="item"/> of the owner's tree the primary one; the former primary (if any) becomes a further occurrence.</summary>
        public void SetPrimary(OccurrenceKey key, ItemMirror item)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
            if (Unsafe.IsNullRef(ref entry) || ReferenceEquals(entry.Primary, item))
                return;
            if (entry.Further is null || !entry.Further.Remove(item))
                return;
            if (entry.Primary is { } old)
                entry.Further.Add(old);
            entry.Primary = item;
        }

        /// <summary>
        /// Removes the occurrence <paramref name="item"/> of <paramref name="reconciler"/> (no-op if it is none, e.g. after a
        /// video was taken over by an earlier occurrence). Returns true if the owner renderer lost its primary while the
        /// element is still shown (further occurrences in its tree, or in other renderers): the caller picks the new primary
        /// or hands the element over (<see cref="HandOver"/>). When no occurrence is left anywhere, the reconcilers of waiting
        /// placeholders are invalidated (unless <paramref name="invalidatePlaceholders"/> is false: a video taken over inside
        /// one tree keeps its placeholders).
        /// </summary>
        public bool RemoveOccurrence(OccurrenceKey key, ItemMirror item, Reconciler reconciler, bool invalidatePlaceholders = true)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
            if (Unsafe.IsNullRef(ref entry))
                return false;

            if (!ReferenceEquals(entry.Owner, reconciler))
            {
                RemoveElsewhere(ref entry, item, reconciler);
                return false;
            }

            bool wasPrimary = ReferenceEquals(entry.Primary, item);
            if (wasPrimary)
                entry.Primary = null;
            else if (entry.Further is null || !entry.Further.Remove(item))
                return false;

            if (entry.HasOwnerOccurrences)
                return wasPrimary;
            if (entry.Further is { } further)
            {
                ReturnList(further);
                entry.Further = null;
            }
            // Other renderers still show it: the owner hands it over at the end of its update, so a move inside its own
            // tree in the same pass keeps the primary there.
            if (entry.Elsewhere is not null)
                return true;

            entry.Owner = null;
            if (entry.Placeholders is null)
            {
                _entries.Remove(key);
                return false;
            }
            if (invalidatePlaceholders)
            {
                foreach (var r in entry.Placeholders)
                    r.Invalidate();
            }
            return false;
        }

        /// <summary>Removes <paramref name="item"/> from the occurrences of <paramref name="reconciler"/>, which is not the owner.</summary>
        private void RemoveElsewhere(ref Entry entry, ItemMirror item, Reconciler reconciler)
        {
            if (entry.Elsewhere is not { } elsewhere)
                return;
            for (int i = 0; i < elsewhere.Count; i++)
            {
                var g = elsewhere[i];
                if (!ReferenceEquals(g.Reconciler, reconciler))
                    continue;
                if (!g.Items.Remove(item) || g.Items.Count > 0)
                    return;
                ReturnList(g.Items);
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
        public bool HandOver(OccurrenceKey key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
            if (Unsafe.IsNullRef(ref entry) || !ReferenceEquals(entry.Owner, reconciler) || entry.HasOwnerOccurrences
                || entry.Elsewhere is not { Count: > 0 } elsewhere)
                return false;
            var next = elsewhere[0];
            elsewhere.RemoveAt(0);
            if (elsewhere.Count == 0)
                entry.Elsewhere = null;
            entry.Owner = next.Reconciler;
            entry.Primary = null;
            entry.Further = next.Items;
            next.Reconciler.TakeOver(key);
            return true;
        }

        /// <summary>Registers a placeholder of a video held by <paramref name="reconciler"/> and sets the warning.</summary>
        public void AddPlaceholder(OccurrenceKey key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, key, out _);
            (entry.Placeholders ??= new List<Reconciler>()).Add(reconciler);
            key.Slot.SetMessage(VideoPlaceholderSource, MessageKind.Warning, VideoPlaceholderWarning);
        }

        /// <summary>Removes one placeholder; clears the warning when none is left.</summary>
        public void RemovePlaceholder(OccurrenceKey key, Reconciler reconciler)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
            if (Unsafe.IsNullRef(ref entry) || entry.Placeholders is null || !entry.Placeholders.Remove(reconciler))
                return;
            if (entry.Placeholders.Count > 0)
                return;
            entry.Placeholders = null;
            key.Slot.SetMessage(VideoPlaceholderSource, MessageKind.Warning, null);
            if (entry.Owner is null)
                _entries.Remove(key);
        }

        /// <summary>
        /// Removes every trace of <paramref name="reconciler"/>: its occurrences and its placeholders, also those a visit cut
        /// short by an exception registered but never linked into its mirror. Elements it owned go to the next renderer that
        /// shows them (<see cref="HandOver"/>), the reconcilers of waiting placeholders of other renderers are invalidated.
        /// For the resync (<see cref="Reconciler.Discard"/>); allocates.
        /// </summary>
        public void RemoveAll(Reconciler reconciler)
        {
            foreach (var key in new List<OccurrenceKey>(_entries.Keys))
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
                if (entry.Placeholders is { } placeholders && placeholders.RemoveAll(r => ReferenceEquals(r, reconciler)) > 0 && placeholders.Count == 0)
                {
                    entry.Placeholders = null;
                    key.Slot.SetMessage(VideoPlaceholderSource, MessageKind.Warning, null);
                }
                if (entry.Elsewhere is { } elsewhere)
                {
                    for (int i = elsewhere.Count - 1; i >= 0; i--)
                    {
                        if (!ReferenceEquals(elsewhere[i].Reconciler, reconciler))
                            continue;
                        ReturnList(elsewhere[i].Items);
                        elsewhere.RemoveAt(i);
                    }
                    if (elsewhere.Count == 0)
                        entry.Elsewhere = null;
                }
                if (ReferenceEquals(entry.Owner, reconciler))
                {
                    entry.Primary = null;
                    if (entry.Further is { } further)
                    {
                        ReturnList(further);
                        entry.Further = null;
                    }
                    if (HandOver(key, reconciler))
                        continue;
                    entry.Owner = null;
                    if (entry.Placeholders is { } waiting)
                    {
                        foreach (var r in waiting)
                            r.Invalidate();
                    }
                }
                if (entry.Owner is null && entry.Placeholders is null)
                    _entries.Remove(key);
            }
        }

        private List<ItemMirror> RentList() => _lists.Count > 0 ? _lists.Pop() : new List<ItemMirror>(2);

        private void ReturnList(List<ItemMirror> list)
        {
            list.Clear();
            if (_lists.Count < 16)
                _lists.Push(list);
        }
    }
}
