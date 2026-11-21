using System;
using System.Collections.Generic;
using System.Text;
using VL.Lib.Collections;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Feedback ids of one window: <c>uint feedbackId ↔ FeedbackSlot</c>. The reconciler acquires an id per binding
    /// (BIND feedbackId) and releases it on unbind, feedback change or destroy; <see cref="Dispatch"/> writes the
    /// poll buffer of the window into the bound slots.
    /// <para>
    /// Ids are counted up and not reused (wrap-around skips 0 and live ids): events QtNative still
    /// sends for a released id (e.g. EVENT_HOVER 0 / EVENT_PRESS 0 after an unbind, 1 frame later) are dropped
    /// instead of landing in another slot. Releasing resets hover/press of the slot right away
    /// (docs/design/events.md); this also holds for the ids of input handlers (a removed Hover node's
    /// channel falls back to not hovered). Lives with the reconciler (one per window), so a restarted app (F8/F5)
    /// starts empty. Main thread only, allocation-free in the steady state.
    /// </para>
    /// <para>
    /// Several ids may point to the same slot (one element at several places of the tree, one Qt object and
    /// id per occurrence). Only the primary id (the first occurrence in traversal order, see
    /// <see cref="OccurrenceRegistry"/>) writes state values (Measured, SceneBounds, Media, Error) and resets the slot when
    /// it is released; events (Click, Value) count from every id. Ids of input handlers are always primary.
    /// </para>
    /// </summary>
    internal sealed class FeedbackRouter
    {
        private readonly struct Binding
        {
            public Binding(FeedbackSlot slot, bool primary)
            {
                Slot = slot;
                Primary = primary;
            }

            public FeedbackSlot Slot { get; }
            public bool Primary { get; }
        }

        private readonly Dictionary<uint, Binding> _slots = new();
        private uint _last;

        /// <summary>Number of live ids.</summary>
        public int Count => _slots.Count;

        /// <summary>
        /// New id (≠ 0) for <paramref name="slot"/>. <paramref name="primary"/> = false for a further occurrence of
        /// an element: its state values are dropped and its release leaves the slot alone.
        /// </summary>
        public uint Acquire(FeedbackSlot slot, bool primary = true)
        {
            if (slot.IsNone)
                throw new ArgumentException("no feedback", nameof(slot));
            do
            {
                unchecked { _last++; }
            }
            while (_last == 0 || _slots.ContainsKey(_last));
            _slots.Add(_last, new Binding(slot, primary));
            return _last;
        }

        /// <summary>Whether <paramref name="feedbackId"/> is live and primary.</summary>
        public bool IsPrimary(uint feedbackId) => _slots.TryGetValue(feedbackId, out var b) && b.Primary;

        /// <summary>
        /// Demotes <paramref name="feedbackId"/> to a further occurrence (false) or promotes it (true); 0 / unknown: no-op.
        /// The reconciler promotes by a new feedbackId and BIND instead, so QtNative sends the current state again.
        /// </summary>
        public void SetPrimary(uint feedbackId, bool primary)
        {
            if (feedbackId != 0 && _slots.TryGetValue(feedbackId, out var b) && b.Primary != primary)
                _slots[feedbackId] = new Binding(b.Slot, primary);
        }

        /// <summary>
        /// Frees <paramref name="feedbackId"/> (0 / unknown: no-op). A primary feedbackId also clears hover/press/active, the
        /// pending taps and wheel and the error of its slot; a further occurrence's feedbackId leaves the slot as it is.
        /// </summary>
        public void Release(uint feedbackId)
        {
            if (feedbackId != 0 && _slots.Remove(feedbackId, out var b) && b.Primary)
                ResetPointerState(b.Slot);
        }

        private static void ResetPointerState(FeedbackSlot slot)
        {
            if (slot.Feedback!.TryGet(slot.Index, out var state))
            {
                state.IsHovered = false;
                state.IsPressed = false;
                state.IsActive = false;
                state.TapCount = 0;
                state.PendingLongPresses = 0;
                state.PendingWheel = default;
                if (state.TouchPoints.Count > 0)
                {
                    state.TouchPoints = Spread<TouchPoint>.Empty; // the points belong to the old handler
                    unchecked { state.TouchUpdates++; }
                }
                state.Error = null; // the element is gone (or re-bound: QtNative sends its current error again)
            }
        }

        /// <summary>
        /// Mapping of the scene the poll buffers come from, set by <see cref="Reconciler.Update(Element?, in SpaceMapping)"/>:
        /// <see cref="Dispatch"/> converts the logical pixels of Qt to units with it.
        /// </summary>
        public SpaceMapping Mapping { get; set; } = SpaceMapping.Default;

        public bool TryResolve(uint feedbackId, out FeedbackSlot slot)
        {
            var found = _slots.TryGetValue(feedbackId, out var b);
            slot = b.Slot;
            return found;
        }

        /// <summary>Frees all ids (renderer dispose).</summary>
        public void Clear()
        {
            foreach (var b in _slots.Values)
                if (b.Primary)
                    ResetPointerState(b.Slot);
            _slots.Clear();
        }

        /// <summary>
        /// Writes the records of a poll buffer (<see cref="EventReader"/>) into the bound slots. The id resolves
        /// to the slot of the binding (feedback + slot index); one item = one binding, so several elements on
        /// one feedback are told apart by their ids.
        /// Records with an unknown id, an unknown event or a too short payload are ignored. Values from Qt (logical pixels)
        /// are converted to units (<see cref="Mapping"/>; SceneBounds is in root coordinates, QtNative maps it). State values
        /// (Measured, SceneBounds, Media, Error) of an id that is not primary are ignored too.
        /// </summary>
        public DispatchResult Dispatch(ReadOnlySpan<byte> poll)
        {
            int applied = 0, ignored = 0;
            var r = new EventReader(poll);
            while (r.MoveNext())
            {
                if (_slots.TryGetValue(r.FeedbackId, out var b) && (b.Primary || !IsState(r.Event))
                    && Apply(ref r, b.Slot.Feedback!, b.Slot.Index, Mapping))
                    applied++;
                else
                    ignored++;
            }
            return new DispatchResult(applied, ignored, r.Error);
        }

        /// <summary>Events that report the state of an element (only the primary occurrence writes them).</summary>
        private static bool IsState(ushort ev)
            => ev == Protocol.EventId.Measured || ev == Protocol.EventId.SceneBounds || ev == Protocol.EventId.Media || ev == Protocol.EventId.Error;

        private static float ReadF32(ReadOnlySpan<byte> p, int offset)
            => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(p.Slice(offset));

        private const int touchPointSize = 24;
        private const int maxTouchPoints = 64;

        /// <summary>EVENT_TOUCH_POINTS: u32 count, then per point id, x, y, pressure, w, h. Sent on change only, so allocating is fine.</summary>
        private static bool ApplyTouchPoints(ReadOnlySpan<byte> payload, FeedbackState state, in SpaceMapping mapping)
        {
            if (payload.Length < 4)
                return false;
            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (count > maxTouchPoints || payload.Length < 4 + (long)count * touchPointSize)
                return false;
            if (count == 0)
            {
                if (state.TouchPoints.Count == 0)
                    return true; // already empty
                state.TouchPoints = Spread<TouchPoint>.Empty;
            }
            else
            {
                var builder = new SpreadBuilder<TouchPoint>((int)count);
                for (int i = 0; i < count; i++)
                {
                    var p = payload.Slice(4 + i * touchPointSize, touchPointSize);
                    builder.Add(new TouchPoint(
                        (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p), int.MaxValue),
                        mapping.FromQt(new Vector2(ReadF32(p, 4), ReadF32(p, 8))),
                        ReadF32(p, 12),
                        mapping.FromQt(new Vector2(ReadF32(p, 16), ReadF32(p, 20)))));
                }
                state.TouchPoints = builder.ToSpread();
            }
            unchecked { state.TouchUpdates++; }
            return true;
        }

        /// <summary>
        /// EVENT_VALUE: u16 value type, u16 0, then the value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8).
        /// Sent only after a user change, so allocating the string is fine. Keeps the last change until it is taken.
        /// </summary>
        private static bool ApplyValue(ReadOnlySpan<byte> payload, ElementFeedback feedback, int index)
        {
            if (payload.Length < 4)
                return false;
            ushort type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var v = payload.Slice(4);
            int bits = 0;
            string? str = null;
            switch (type)
            {
                case Protocol.ValueType.I32:
                case Protocol.ValueType.F32:
                    if (v.Length < 4) return false;
                    bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v);
                    break;
                case Protocol.ValueType.Bool:
                    if (v.Length < 1) return false;
                    bits = v[0] != 0 ? 1 : 0;
                    break;
                case Protocol.ValueType.String:
                    if (v.Length < 4) return false;
                    uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(v);
                    if (n > (uint)(v.Length - 4)) return false;
                    str = Encoding.UTF8.GetString(v.Slice(4, (int)n));
                    break;
                default:
                    return false;
            }
            var s = feedback[index];
            s.HasPendingValue = true;
            s.PendingValueType = type;
            s.PendingValue = bits;
            s.PendingString = str;
            return true;
        }

        /// <summary>Validates the record first, so ignored records do not grow the feedback's slot list.</summary>
        private static bool Apply(ref EventReader r, ElementFeedback feedback, int index, in SpaceMapping mapping)
        {
            int len = r.Payload.Length;
            switch (r.Event)
            {
                case Protocol.EventId.Measured:
                    if (len < 8) return false;
                    feedback[index].MeasuredSize = mapping.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    return true;
                case Protocol.EventId.SceneBounds:
                    if (len < 16) return false;
                    feedback[index].SceneBounds = mapping.FromQt(new RectangleF(r.ReadF32(0), r.ReadF32(4), r.ReadF32(8), r.ReadF32(12)));
                    return true;
                case Protocol.EventId.Hover:
                    if (len < 1) return false;
                    feedback[index].IsHovered = r.Payload[0] != 0;
                    return true;
                case Protocol.EventId.Press:
                    if (len < 1) return false;
                    feedback[index].IsPressed = r.Payload[0] != 0;
                    return true;
                case Protocol.EventId.Click:
                    if (len < 4) return false;
                    int delta = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    if (delta == 0) return true;
                    var c = feedback[index];
                    c.PendingClicks = (int)Math.Min((long)c.PendingClicks + delta, int.MaxValue);
                    return true;
                case Protocol.EventId.TapCount:
                    if (len < 4) return false;
                    feedback[index].TapCount = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    return true;
                case Protocol.EventId.LongPress:
                    if (len < 4) return false;
                    int presses = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    if (presses == 0) return true;
                    var lp = feedback[index];
                    lp.PendingLongPresses = (int)Math.Min((long)lp.PendingLongPresses + presses, int.MaxValue);
                    return true;
                case Protocol.EventId.Active:
                    if (len < 1) return false;
                    feedback[index].IsActive = r.Payload[0] != 0;
                    return true;
                case Protocol.EventId.Wheel:
                    if (len < 8) return false;
                    var wh = feedback[index]; // notches, not converted (not a length)
                    wh.PendingWheel += new Vector2(r.ReadF32(0), r.ReadF32(4));
                    return true;
                case Protocol.EventId.Drag:
                    if (len < 8) return false;
                    var dr = feedback[index];
                    dr.DragDelta = mapping.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    unchecked { dr.DragUpdates++; }
                    return true;
                case Protocol.EventId.TouchPoints:
                    return ApplyTouchPoints(r.Payload, feedback[index], mapping);
                case Protocol.EventId.Pinch:
                    if (len < 24) return false;
                    var pi = feedback[index]; // scale: factor, rotation: degrees -> cycles, translation and pivot: pixels -> units
                    pi.PinchScale = r.ReadF32(0);
                    pi.PinchRotation = r.ReadF32(4) / 360f;
                    pi.PinchTranslation = mapping.FromQt(new Vector2(r.ReadF32(8), r.ReadF32(12)));
                    pi.PinchPivot = mapping.FromQt(new Vector2(r.ReadF32(16), r.ReadF32(20)));
                    unchecked { pi.PinchUpdates++; }
                    return true;
                case Protocol.EventId.Pointer:
                    if (len < 8) return false;
                    feedback[index].PointerPosition = mapping.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    return true;
                case Protocol.EventId.Media:
                    if (len < 8) return false;
                    var m = feedback[index]; // seconds, not converted
                    m.MediaPosition = r.ReadF32(0);
                    m.MediaDuration = r.ReadF32(4);
                    return true;
                case Protocol.EventId.Value:
                    return ApplyValue(r.Payload, feedback, index);
                case Protocol.EventId.QmlProperty:
                    if (!QmlEvents.TryDecodeProperty(r.Payload, out var qmlName, out var qmlValue)) return false;
                    feedback.SetQmlProperty(index, qmlName, qmlValue);
                    return true;
                case Protocol.EventId.QmlSignal:
                    if (!QmlEvents.TryDecodeSignal(r.Payload, out var qmlSignal)) return false;
                    feedback.AddQmlSignal(index, qmlSignal);
                    return true;
                case Protocol.EventId.Error:
                    // Sent only on change (allocation is fine); empty = cleared.
                    feedback[index].Error = len == 0 ? null : Encoding.UTF8.GetString(r.Payload);
                    return true;
                default:
                    return false;
            }
        }
    }
}
