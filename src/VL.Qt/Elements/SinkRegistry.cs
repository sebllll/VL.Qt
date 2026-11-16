using System;
using System.Collections.Generic;
using System.Text;
using VL.Lib.Collections;
using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Counts of one <see cref="SinkRegistry.Dispatch"/> call.</summary>
    internal readonly record struct DispatchResult(int Applied, int Ignored, string? Error);

    /// <summary>
    /// Sink handles of one window: <c>uint handle ↔ SinkSlot</c>. The reconciler acquires a handle per binding
    /// (BIND sinkHandle) and releases it on unbind, sink change or destroy; <see cref="Dispatch"/> writes the
    /// poll buffer of the window into the bound slots.
    /// <para>
    /// Handles are counted up and not reused (wrap-around skips 0 and live handles): events QtNative still
    /// sends for a released handle (e.g. EV_HOVER 0 / EV_PRESS 0 after an unbind, 1 frame later) are dropped
    /// instead of landing in another slot. Releasing resets hover/press of the slot right away
    /// (docs/decisions/events.md); this also holds for the handles of input handlers (#319: a removed Hover node's
    /// channel falls back to not hovered). Lives with the reconciler (one per window), so a restarted app (F8/F5)
    /// starts empty. Main thread only, allocation-free in the steady state.
    /// </para>
    /// <para>
    /// Several handles may point to the same slot (#455: one element at several places of the tree, one Qt object and
    /// handle per occurrence). Only the primary handle (the first occurrence in traversal order, see
    /// <see cref="SceneRegistry"/>) writes state values (Measured, SceneBounds, Media, Error) and resets the slot when
    /// it is released; events (Click, Value) count from every handle. Handles of input handlers are always primary.
    /// </para>
    /// </summary>
    internal sealed class SinkRegistry
    {
        readonly struct Binding
        {
            public Binding(SinkSlot slot, bool primary)
            {
                Slot = slot;
                Primary = primary;
            }

            public SinkSlot Slot { get; }
            public bool Primary { get; }
        }

        readonly Dictionary<uint, Binding> _slots = new();
        uint _last;

        /// <summary>Number of live handles.</summary>
        public int Count => _slots.Count;

        /// <summary>
        /// New handle (≠ 0) for <paramref name="slot"/>. <paramref name="primary"/> = false for a further occurrence of
        /// an element: its state values are dropped and its release leaves the slot alone.
        /// </summary>
        public uint Acquire(SinkSlot slot, bool primary = true)
        {
            if (slot.IsNone)
                throw new ArgumentException("no sink", nameof(slot));
            do
            {
                unchecked { _last++; }
            }
            while (_last == 0 || _slots.ContainsKey(_last));
            _slots.Add(_last, new Binding(slot, primary));
            return _last;
        }

        /// <summary>Whether <paramref name="handle"/> is live and primary.</summary>
        public bool IsPrimary(uint handle) => _slots.TryGetValue(handle, out var b) && b.Primary;

        /// <summary>
        /// Demotes <paramref name="handle"/> to a further occurrence (false) or promotes it (true); 0 / unknown: no-op.
        /// The reconciler promotes by a new handle and BIND instead, so QtNative sends the current state again.
        /// </summary>
        public void SetPrimary(uint handle, bool primary)
        {
            if (handle != 0 && _slots.TryGetValue(handle, out var b) && b.Primary != primary)
                _slots[handle] = new Binding(b.Slot, primary);
        }

        /// <summary>
        /// Frees <paramref name="handle"/> (0 / unknown: no-op). A primary handle also clears hover/press/active, the
        /// pending taps and wheel and the error of its slot; a further occurrence's handle leaves the slot as it is.
        /// </summary>
        public void Release(uint handle)
        {
            if (handle != 0 && _slots.Remove(handle, out var b) && b.Primary)
                ResetPointerState(b.Slot);
        }

        static void ResetPointerState(SinkSlot slot)
        {
            if (slot.Sink!.TryGet(slot.Index, out var state))
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

        public bool TryResolve(uint handle, out SinkSlot slot)
        {
            var found = _slots.TryGetValue(handle, out var b);
            slot = b.Slot;
            return found;
        }

        /// <summary>Frees all handles (renderer dispose).</summary>
        public void Clear()
        {
            foreach (var b in _slots.Values)
                if (b.Primary)
                    ResetPointerState(b.Slot);
            _slots.Clear();
        }

        /// <summary>Largest accepted record index (offset to the slot index of the binding); larger ones are ignored.</summary>
        public const uint MaxIndexOffset = ushort.MaxValue;

        /// <summary>
        /// Writes the records of a poll buffer (<see cref="EventReader"/>) into the bound slots. The handle resolves
        /// to the slot of the binding (sink + slot index); the record index is an offset to that slot index
        /// (target = <c>slot.Index + index</c>). QtNative sends 0 (one item = one binding, so several elements on
        /// one sink are told apart by their handles); offsets &gt; 0 are for items that serve several slots.
        /// Records with an unknown handle, an index above <see cref="MaxIndexOffset"/> (or a target beyond
        /// int.MaxValue), an unknown event or a too short payload are ignored. Values from Qt (logical pixels)
        /// are converted to units (<see cref="Geometry.FromQt(float)"/>, v1: DIPTopLeft only). State values
        /// (Measured, SceneBounds, Media, Error) of a handle that is not primary are ignored too.
        /// </summary>
        public DispatchResult Dispatch(ReadOnlySpan<byte> poll)
        {
            int applied = 0, ignored = 0;
            var r = new EventReader(poll);
            while (r.MoveNext())
            {
                if (_slots.TryGetValue(r.Handle, out var b) && (b.Primary || !IsState(r.Event))
                    && Target(b.Slot, r.Index, out int index) && Apply(ref r, b.Slot.Sink!, index))
                    applied++;
                else
                    ignored++;
            }
            return new DispatchResult(applied, ignored, r.Error);
        }

        /// <summary>Events that report the state of an element (only the primary occurrence writes them).</summary>
        static bool IsState(ushort ev)
            => ev == Protocol.Ev.Measured || ev == Protocol.Ev.SceneBounds || ev == Protocol.Ev.Media || ev == Protocol.Ev.Error;

        static bool Target(SinkSlot slot, uint offset, out int index)
        {
            long target = slot.Index + (long)offset;
            index = (int)target;
            return offset <= MaxIndexOffset && target <= int.MaxValue;
        }

        static float ReadF32(ReadOnlySpan<byte> p, int offset)
            => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(p.Slice(offset));

        const int touchPointSize = 24;
        const int maxTouchPoints = 64;

        /// <summary>EV_TOUCH_POINTS: u32 count, then per point id, x, y, pressure, w, h. Sent on change only, so allocating is fine.</summary>
        static bool ApplyTouchPoints(ReadOnlySpan<byte> payload, SinkState state)
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
                        Geometry.FromQt(new Vector2(ReadF32(p, 4), ReadF32(p, 8))),
                        ReadF32(p, 12),
                        Geometry.FromQt(new Vector2(ReadF32(p, 16), ReadF32(p, 20)))));
                }
                state.TouchPoints = builder.ToSpread();
            }
            unchecked { state.TouchUpdates++; }
            return true;
        }

        /// <summary>
        /// EV_VALUE: u16 value type, u16 0, then the value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8).
        /// Sent only after a user change, so allocating the string is fine. Keeps the last change until it is taken.
        /// </summary>
        static bool ApplyValue(ReadOnlySpan<byte> payload, ElementSink sink, int index)
        {
            if (payload.Length < 4)
                return false;
            ushort type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var v = payload.Slice(4);
            int bits = 0;
            string? str = null;
            switch (type)
            {
                case Protocol.Vt.I32:
                case Protocol.Vt.F32:
                    if (v.Length < 4) return false;
                    bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v);
                    break;
                case Protocol.Vt.Bool:
                    if (v.Length < 1) return false;
                    bits = v[0] != 0 ? 1 : 0;
                    break;
                case Protocol.Vt.String:
                    if (v.Length < 4) return false;
                    uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(v);
                    if (n > (uint)(v.Length - 4)) return false;
                    str = Encoding.UTF8.GetString(v.Slice(4, (int)n));
                    break;
                default:
                    return false;
            }
            var s = sink[index];
            s.HasPendingValue = true;
            s.PendingValueType = type;
            s.PendingValue = bits;
            s.PendingString = str;
            return true;
        }

        /// <summary>Validates the record first, so ignored records do not grow the sink's slot list.</summary>
        static bool Apply(ref EventReader r, ElementSink sink, int index)
        {
            int len = r.Payload.Length;
            switch (r.Event)
            {
                case Protocol.Ev.Measured:
                    if (len < 8) return false;
                    sink[index].MeasuredSize = Geometry.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    return true;
                case Protocol.Ev.SceneBounds:
                    if (len < 16) return false;
                    sink[index].SceneBounds = Geometry.FromQt(new RectangleF(r.ReadF32(0), r.ReadF32(4), r.ReadF32(8), r.ReadF32(12)));
                    return true;
                case Protocol.Ev.Hover:
                    if (len < 1) return false;
                    sink[index].IsHovered = r.Payload[0] != 0;
                    return true;
                case Protocol.Ev.Press:
                    if (len < 1) return false;
                    sink[index].IsPressed = r.Payload[0] != 0;
                    return true;
                case Protocol.Ev.Click:
                    if (len < 4) return false;
                    int delta = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    if (delta == 0) return true;
                    var c = sink[index];
                    c.PendingClicks = (int)Math.Min((long)c.PendingClicks + delta, int.MaxValue);
                    return true;
                case Protocol.Ev.TapCount:
                    if (len < 4) return false;
                    sink[index].TapCount = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    return true;
                case Protocol.Ev.LongPress:
                    if (len < 4) return false;
                    int presses = (int)Math.Min(r.ReadU32(0), int.MaxValue);
                    if (presses == 0) return true;
                    var lp = sink[index];
                    lp.PendingLongPresses = (int)Math.Min((long)lp.PendingLongPresses + presses, int.MaxValue);
                    return true;
                case Protocol.Ev.Active:
                    if (len < 1) return false;
                    sink[index].IsActive = r.Payload[0] != 0;
                    return true;
                case Protocol.Ev.Wheel:
                    if (len < 8) return false;
                    var wh = sink[index]; // notches, not converted (not a length)
                    wh.PendingWheel += new Vector2(r.ReadF32(0), r.ReadF32(4));
                    return true;
                case Protocol.Ev.Drag:
                    if (len < 8) return false;
                    var dr = sink[index];
                    dr.DragDelta = Geometry.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    unchecked { dr.DragUpdates++; }
                    return true;
                case Protocol.Ev.TouchPoints:
                    return ApplyTouchPoints(r.Payload, sink[index]);
                case Protocol.Ev.Pinch:
                    if (len < 24) return false;
                    var pi = sink[index]; // scale: factor, rotation: degrees -> cycles, translation and pivot: pixels -> units
                    pi.PinchScale = r.ReadF32(0);
                    pi.PinchRotation = r.ReadF32(4) / 360f;
                    pi.PinchTranslation = Geometry.FromQt(new Vector2(r.ReadF32(8), r.ReadF32(12)));
                    pi.PinchPivot = Geometry.FromQt(new Vector2(r.ReadF32(16), r.ReadF32(20)));
                    unchecked { pi.PinchUpdates++; }
                    return true;
                case Protocol.Ev.Pointer:
                    if (len < 8) return false;
                    sink[index].PointerPosition = Geometry.FromQt(new Vector2(r.ReadF32(0), r.ReadF32(4)));
                    return true;
                case Protocol.Ev.Media:
                    if (len < 8) return false;
                    var m = sink[index]; // seconds, not converted
                    m.MediaPosition = r.ReadF32(0);
                    m.MediaDuration = r.ReadF32(4);
                    return true;
                case Protocol.Ev.Value:
                    return ApplyValue(r.Payload, sink, index);
                case Protocol.Ev.QmlProperty:
                    if (!QmlEvents.TryDecodeProperty(r.Payload, out var qmlName, out var qmlValue)) return false;
                    sink.SetQmlProperty(index, qmlName, qmlValue);
                    return true;
                case Protocol.Ev.QmlSignal:
                    if (!QmlEvents.TryDecodeSignal(r.Payload, out var qmlSignal)) return false;
                    sink.AddQmlSignal(index, qmlSignal);
                    return true;
                case Protocol.Ev.Error:
                    // Sent only on change (allocation is fine); empty = cleared.
                    sink[index].Error = len == 0 ? null : Encoding.UTF8.GetString(r.Payload);
                    return true;
                default:
                    return false;
            }
        }
    }
}
