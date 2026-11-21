using System.Collections.Generic;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Diffs the input handlers of an item against its interaction chain (<see cref="Element.Interaction"/>). Key =
    /// <see cref="Interaction.Feedback"/>; the order of the chain does not matter. A new key creates the handler (CREATE,
    /// property SETs, REPARENT to the item, BIND to a new feedback id), the same instance costs nothing, a changed instance
    /// sends the changed properties, a missing key is destroyed (ids and feedback id freed). An interaction that is already
    /// active at another handler (another element, the same chain twice, another renderer) gets a placeholder and the
    /// duplicate warning (<see cref="OccurrenceRegistry"/>); it takes over once the owner lets go (next update).
    /// One per reconciler.
    /// </summary>
    internal sealed class HandlerReconciler
    {
        private readonly Reconciler _owner;
        private readonly ICommandSink _sink;
        private readonly IdAllocator _ids;
        private readonly FeedbackRouter _feedbackRouter;
        private readonly OccurrenceRegistry _scene;
        private readonly Dictionary<uint, FeedbackSlot> _bindings;

        /// <param name="owner">The reconciler whose items carry the handlers.</param>
        /// <param name="sink">Target of the commands.</param>
        /// <param name="ids">Object id allocator of the window (handlers share the id space of the items).</param>
        /// <param name="feedbackRouter">Feedback ids of the window.</param>
        /// <param name="scene">Registry of the app that knows which handler serves an interaction.</param>
        /// <param name="bindings">Object id → feedback slot of every bound object of the window.</param>
        public HandlerReconciler(Reconciler owner, ICommandSink sink, IdAllocator ids, FeedbackRouter feedbackRouter,
            OccurrenceRegistry scene, Dictionary<uint, FeedbackSlot> bindings)
        {
            _owner = owner;
            _sink = sink;
            _ids = ids;
            _feedbackRouter = feedbackRouter;
            _scene = scene;
            _bindings = bindings;
        }

        /// <summary>
        /// Diffs the handlers of <paramref name="item"/> against the chain <paramref name="next"/>; they are written after the
        /// item's BIND and before its children. A reference-equal chain is skipped unless the whole tree is forced.
        /// </summary>
        public void Reconcile(ItemMirror item, Interaction? previous, Interaction? next, in PassInfo pass)
        {
            var list = item.Handlers;
            if (next is null && (list is null || list.Count == 0))
                return;
            if (!pass.Force && ReferenceEquals(previous, next) && list is not null)
                return;

            list ??= item.Handlers = new List<HandlerMirror>();
            if (next is not null)
                ReconcileLink(item, list, next, pass);

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].SeenPass != pass.Pass)
                {
                    Destroy(list[i]);
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Forgets the handlers of <paramref name="item"/> without commands: QtNative removes the handlers with their item
        /// (or with the RESET of a resync).
        /// </summary>
        public void Forget(ItemMirror item)
        {
            if (item.Handlers is not { } handlers)
                return;
            for (int i = 0; i < handlers.Count; i++)
                ForgetHandler(handlers[i]);
            handlers.Clear();
        }

        /// <summary>
        /// Matches one link (after the links before it, so ids and the first occurrence of a duplicate follow the patch
        /// order; recursion depth = chain length).
        /// </summary>
        private void ReconcileLink(ItemMirror item, List<HandlerMirror> list, Interaction link, in PassInfo pass)
        {
            if (link.Previous is { } previous)
                ReconcileLink(item, list, previous, pass);

            int k = IndexOfUnseen(list, link.Feedback, pass.Pass);
            if (k < 0)
            {
                list.Add(Create(item, link, pass));
                return;
            }

            var h = list[k];
            if (h.IsIgnored)
            {
                if (_scene.HandlerOwnerOf(link.Feedback) is null)
                {
                    _scene.RemoveHandlerDuplicate(h.Key, _owner);
                    list[k] = Create(item, link, pass);
                }
                else
                {
                    h.SeenPass = pass.Pass;
                    h.Last = link;
                }
            }
            else if (h.Type != link.HandlerType)
            {
                Destroy(h);
                list[k] = Create(item, link, pass);
            }
            else
            {
                h.SeenPass = pass.Pass;
                if (pass.Rescale || !ReferenceEquals(h.Last, link))
                {
                    // A new scale sends the limits again: previous = null writes every non-default value.
                    link.WriteProperties(_sink, h.Id, pass.Rescale ? null : h.Last, pass.Mapping);
                    h.Last = link;
                }
            }
        }

        /// <summary>Index of the handler with <paramref name="key"/> not yet matched in <paramref name="pass"/>, -1 if none.</summary>
        private static int IndexOfUnseen(List<HandlerMirror> list, FeedbackSlot key, int pass)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var h = list[i];
                if (h.SeenPass != pass && h.Key == key)
                    return i;
            }
            return -1;
        }

        /// <summary>New handler at <paramref name="item"/>, or a placeholder if the interaction is active elsewhere.</summary>
        private HandlerMirror Create(ItemMirror item, Interaction link, in PassInfo pass)
        {
            if (_scene.HandlerOwnerOf(link.Feedback) is not null)
            {
                _scene.AddHandlerDuplicate(link.Feedback, _owner);
                return new HandlerMirror(0, link.HandlerType, link.Feedback) { SeenPass = pass.Pass, Last = link };
            }

            var h = new HandlerMirror(_ids.Allocate(), link.HandlerType, link.Feedback) { SeenPass = pass.Pass, Last = link };
            _scene.SetHandlerOwner(h);
            _sink.Create(h.Id, h.Type);
            link.WriteProperties(_sink, h.Id, null, pass.Mapping);
            _sink.Reparent(h.Id, item.Id, 0);
            h.BoundFeedbackId = _feedbackRouter.Acquire(h.Key);
            _sink.Bind(h.Id, h.BoundFeedbackId);
            _bindings[h.Id] = h.Key;
            return h;
        }

        /// <summary>DESTROY of one handler (placeholders have no QtNative object), then <see cref="ForgetHandler"/>.</summary>
        private void Destroy(HandlerMirror h)
        {
            if (!h.IsIgnored)
                _sink.Destroy(h.Id);
            ForgetHandler(h);
        }

        /// <summary>Frees id, feedback id (resets the slot: not hovered), binding and ownership of a handler.</summary>
        private void ForgetHandler(HandlerMirror h)
        {
            if (h.IsIgnored)
            {
                _scene.RemoveHandlerDuplicate(h.Key, _owner);
                return;
            }
            _scene.ReleaseHandler(h);
            _bindings.Remove(h.Id);
            _feedbackRouter.Release(h.BoundFeedbackId);
            h.BoundFeedbackId = 0;
            _ids.Free(h.Id);
        }
    }
}
