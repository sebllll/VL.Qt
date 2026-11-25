using System.Collections.Generic;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// The occurrences of the elements in the tree of one reconciler: registers and releases them in the
    /// <see cref="OccurrenceRegistry"/>, decides which one is primary (the first in traversal order of the owner renderer),
    /// keeps videos at one place (<see cref="TryClaimExclusive"/>, <see cref="CreatePlaceholder"/>) and picks the new
    /// primaries at the end of an update (<see cref="ResolveAtEndOfUpdate"/>). One per reconciler.
    /// </summary>
    internal sealed class PrimaryOccurrences
    {
        private readonly Reconciler _owner;
        private readonly OccurrenceRegistry _scene;
        private readonly FeedbackRouter _feedbackRouter;
        private readonly ICommandSink _sink;
        private readonly List<Element> _pendingPrimary = new(); // elements whose primary occurrence let go in this pass

        /// <param name="owner">The reconciler whose tree holds the occurrences.</param>
        /// <param name="scene">Occurrence registry of the app, shared by its renderers.</param>
        /// <param name="feedbackRouter">Feedback ids of the owner's window.</param>
        /// <param name="sink">Target of the BIND of a promoted occurrence.</param>
        public PrimaryOccurrences(Reconciler owner, OccurrenceRegistry scene, FeedbackRouter feedbackRouter, ICommandSink sink)
        {
            _owner = owner;
            _scene = scene;
            _feedbackRouter = feedbackRouter;
            _sink = sink;
        }

        /// <summary>
        /// <paramref name="element"/> was handed over to the owner (<see cref="OccurrenceRegistry.HandOver"/>): the next
        /// <see cref="ResolveAtEndOfUpdate"/> picks the first of its occurrences in this tree as the primary one.
        /// </summary>
        public void TakeOver(Element element) => _pendingPrimary.Add(element);

        /// <summary>True if <paramref name="item"/> is the primary occurrence of <paramref name="element"/>.</summary>
        public bool IsPrimaryOf(Element element, ItemMirror item) => ReferenceEquals(_scene.OwnerOf(element), item);

        /// <summary>
        /// Whether <paramref name="self"/> (null = a mirror about to be created) may show <paramref name="element"/>.
        /// Always true, except for a video (shown once only, app-wide): false if another renderer shows it; true if nobody
        /// shows it, or its occurrence is a mirror of this tree that comes later in this pass (not visited yet,
        /// or about to be destroyed); then the video is taken over and the old occurrence's subtree is marked
        /// <see cref="ItemMirror.MustVisit"/>, so it is not skipped and turns into a placeholder when it is visited.
        /// </summary>
        public bool TryClaimExclusive(Element element, ItemMirror? self, int pass)
        {
            var ownerReconciler = _scene.OwnerReconcilerOf(element);
            if (ownerReconciler is null || !OccurrenceRegistry.IsExclusive(element))
                return true;
            if (!ReferenceEquals(ownerReconciler, _owner))
                return false;
            var owner = _scene.OwnerOf(element);
            if (owner is null || ReferenceEquals(owner, self))
                return true;
            if (IsVisitedThisPass(owner, pass))
                return false;
            for (var n = owner; n is not null && !n.MustVisit && n.VisitedPass != pass; n = n.Parent)
                n.MustVisit = true;
            _scene.RemoveOccurrence(element, owner, _owner, invalidateWaiting: false);
            return true;
        }

        /// <summary>Whether a placeholder of <paramref name="element"/> has to stay one: it is a video shown at another mirror.</summary>
        public bool IsShownElsewhere(Element element)
            => OccurrenceRegistry.IsExclusive(element) && _scene.OwnerReconcilerOf(element) is not null;

        /// <summary>Placeholder for an occurrence of a video that is shown elsewhere (in this or another renderer).</summary>
        public ItemMirror CreatePlaceholder(Element next, ItemMirror? parent, int pass)
        {
            var item = new ItemMirror(0, next.Type) { Parent = parent, VisitedPass = pass, Element = next };
            _scene.AddDuplicate(next, _owner);
            return item;
        }

        /// <summary>Unregisters the placeholder of <paramref name="element"/>.</summary>
        public void RemovePlaceholder(Element element) => _scene.RemoveDuplicate(element, _owner);

        /// <summary>
        /// Registers <paramref name="item"/> as an occurrence of <paramref name="element"/> and decides whether it is the
        /// primary one: it is if it is the only one, or if this renderer owns the element and the current primary was not
        /// reached yet in this pass (it comes later in traversal order or is about to be destroyed); the old primary is
        /// then demoted. While the primary is pending (it let go earlier in this pass, or the element was just handed over)
        /// the mirror waits for <see cref="ResolveAtEndOfUpdate"/>. In a renderer that does not own the element it stays a
        /// further occurrence.
        /// </summary>
        public void Register(Element element, ItemMirror item, int pass)
        {
            if (_scene.AddOccurrence(element, item, _owner))
            {
                item.IsPrimary = true;
                return;
            }
            item.IsPrimary = false;
            var primary = _scene.OwnerOf(element, out var owner);
            if (!ReferenceEquals(owner, _owner) || primary is null || IsVisitedThisPass(primary, pass))
                return;
            Demote(primary);
            _scene.SetPrimary(element, item);
            item.IsPrimary = true;
        }

        /// <summary>
        /// Removes the occurrence <paramref name="item"/> of <paramref name="element"/>; a primary that leaves others behind
        /// (here or in other renderers) is re-picked or handed over at the end of the update.
        /// </summary>
        public void Release(Element element, ItemMirror item)
        {
            if (_scene.RemoveOccurrence(element, item, _owner))
                _pendingPrimary.Add(element);
            item.IsPrimary = false;
        }

        /// <summary>Forgets the elements waiting for a new primary (resync: the registry no longer holds the owner's occurrences).</summary>
        public void Reset() => _pendingPrimary.Clear();

        /// <summary>
        /// Picks the new primary occurrence of every element whose primary let go in this pass, or that was handed over to
        /// this renderer: the first of the remaining occurrences in traversal order (<see cref="IsBefore"/>), re-bound with
        /// a new feedback id so QtNative sends its state. An element without occurrences left in this tree goes to the next
        /// renderer that shows it (<see cref="OccurrenceRegistry.HandOver"/>). Runs once per update after the whole tree (and
        /// after a clear); nothing to do (and no allocation) in the steady state.
        /// </summary>
        public void ResolveAtEndOfUpdate()
        {
            for (int i = 0; i < _pendingPrimary.Count; i++)
            {
                var element = _pendingPrimary[i];
                if (!ReferenceEquals(_scene.OwnerReconcilerOf(element), _owner) || _scene.OwnerOf(element) is not null)
                    continue;
                if (_scene.OthersOf(element) is not { Count: > 0 } others)
                {
                    _scene.HandOver(element, _owner);
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
                if (!first.BoundSlot.IsNone)
                    SyncPrimary(first);
            }
            _pendingPrimary.Clear();
        }

        /// <summary>
        /// Brings the feedback id of <paramref name="item"/> in line with <see cref="ItemMirror.IsPrimary"/>: a promotion takes a
        /// new primary feedback id and sends BIND (QtNative then sends the current Measured/SceneBounds/Media/Error again), a
        /// demotion only flips the flag of the feedback id.
        /// </summary>
        public void SyncPrimary(ItemMirror item)
        {
            if (item.IsPrimary)
            {
                _feedbackRouter.Release(item.BoundFeedbackId); // not primary: leaves the slot alone
                item.BoundFeedbackId = _feedbackRouter.Acquire(item.BoundSlot, primary: true);
                _sink.Bind(item.Id, item.BoundFeedbackId);
            }
            else
            {
                _feedbackRouter.SetPrimary(item.BoundFeedbackId, false);
            }
            item.BoundPrimary = item.IsPrimary;
        }

        /// <summary>Makes <paramref name="item"/> a further occurrence; its feedback id stops reporting state values right away (no BIND needed).</summary>
        private void Demote(ItemMirror item)
        {
            item.IsPrimary = false;
            if (item.BoundPrimary)
            {
                _feedbackRouter.SetPrimary(item.BoundFeedbackId, false);
                item.BoundPrimary = false;
            }
        }

        /// <summary>True if <paramref name="a"/> comes before <paramref name="b"/> in traversal order (both in this tree, not equal).</summary>
        private static bool IsBefore(ItemMirror a, ItemMirror b)
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
            return SiblingOrder(x) < SiblingOrder(y);
        }

        private static int Depth(ItemMirror item)
        {
            int d = 0;
            for (var p = item.Parent; p is not null; p = p.Parent)
                d++;
            return d;
        }

        /// <summary>Order among siblings: the effect objects (negative, in chain order) before the children (index).</summary>
        private static int SiblingOrder(ItemMirror item)
        {
            if (item.Parent is not { } parent)
                return 0;
            if (parent.Effect?.Objects is { } objects)
            {
                var i = objects.IndexOf(item);
                if (i >= 0)
                    return i - objects.Count;
            }
            return parent.Children.IndexOf(item);
        }

        /// <summary>True if <paramref name="item"/> was reached earlier in this pass (itself or a skipped ancestor).</summary>
        private static bool IsVisitedThisPass(ItemMirror item, int pass)
        {
            if (item.VisitedPass == pass)
                return true;
            for (var n = item; n is not null; n = n.Parent)
                if (n.SkippedPass == pass)
                    return true;
            return false;
        }
    }
}
