using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt.Elements
{
    /// <summary>State of one sink slot (one element instance of a spread output). Main thread only.</summary>
    public sealed class SinkState
    {
        public bool IsHovered { get; set; }
        public bool IsPressed { get; set; }
        public Vector2 PointerPosition { get; set; }
        public Vector2 MeasuredSize { get; set; }
        public RectangleF SceneBounds { get; set; }
        /// <summary>Video: playback position in seconds (0 until loaded).</summary>
        public float MediaPosition { get; set; }
        /// <summary>Video: media duration in seconds (0 until loaded).</summary>
        public float MediaDuration { get; set; }

        /// <summary>Clicks not yet taken by <see cref="ElementSink.TakeClicks"/>.</summary>
        public int PendingClicks { get; set; }

        /// <summary>Tap handler: tap count of the last tap (2 = double click), 0 before the first tap.</summary>
        public int TapCount { get; set; }

        /// <summary>Tap handler: long presses not yet taken by <see cref="ElementSink.TakeLongPresses"/>.</summary>
        public int PendingLongPresses { get; set; }

        /// <summary>Point handler: a point (mouse button, finger, pen) is down on the item.</summary>
        public bool IsActive { get; set; }

        /// <summary>Wheel handler: notches not yet taken by <see cref="ElementSink.TakeWheel"/>.</summary>
        public Vector2 PendingWheel { get; set; }

        /// <summary>
        /// Drag handler: translation of the running (or last) drag in units; kept after the release until the next
        /// drag.
        /// </summary>
        public Vector2 DragDelta { get; set; }

        /// <summary>Drag handler: number of EV_DRAG received, so a value equal to the last one (new drag) is still seen as new.</summary>
        public int DragUpdates { get; set; }

        /// <summary>Touch Points handler: points currently pressed in the item, sorted by id, in units. Empty when none.</summary>
        public Spread<TouchPoint> TouchPoints { get; set; } = Spread<TouchPoint>.Empty;

        /// <summary>Touch Points handler: number of EV_TOUCH_POINTS received (changes with every event).</summary>
        public int TouchUpdates { get; set; }

        /// <summary>Pinch handler: scale factor of the running (or last) gesture, 1 before the first one.</summary>
        public float PinchScale { get; set; } = 1f;

        /// <summary>Pinch handler: rotation of the running (or last) gesture in cycles, clockwise positive.</summary>
        public float PinchRotation { get; set; }

        /// <summary>Pinch handler: translation of the running (or last) gesture in units, parent space.</summary>
        public Vector2 PinchTranslation { get; set; }

        /// <summary>Pinch handler: pivot of scale and rotation (the fingers' centroid when the gesture began) in units, parent space.</summary>
        public Vector2 PinchPivot { get; set; }

        /// <summary>Pinch handler: number of EV_PINCH received, so an equal value (new gesture) is still seen as new.</summary>
        public int PinchUpdates { get; set; }

        /// <summary>
        /// Value input: the last user change (EV_VALUE) not yet taken by <see cref="ElementSink.TakeValue{T}"/>.
        /// Several changes between two takes keep the last one.
        /// </summary>
        internal bool HasPendingValue { get; set; }

        /// <summary>Value type of <see cref="PendingValue"/> (<see cref="Protocol.Vt"/>).</summary>
        internal ushort PendingValueType { get; set; }

        /// <summary>Pending user value for <see cref="Protocol.Vt.I32"/>, <see cref="Protocol.Vt.F32"/> and <see cref="Protocol.Vt.Bool"/> (the bits of the value).</summary>
        internal int PendingValue { get; set; }

        /// <summary>Pending user value for <see cref="Protocol.Vt.String"/>.</summary>
        internal string? PendingString { get; set; }

        /// <summary>QmlElement: last value of each root property that QML changed, by name; null until the first change. Cleared by <see cref="ElementSink.ResetQml"/>.</summary>
        internal Dictionary<string, object>? QmlProperties { get; set; }

        /// <summary>QmlElement: number of changes written into <see cref="QmlProperties"/> (also by a reset), so a node sees that there is something new.</summary>
        internal int QmlPropertyUpdates { get; set; }

        /// <summary>QmlElement: signals not yet taken by <see cref="ElementSink.TakeQmlSignals"/>, in order of arrival.</summary>
        internal List<QmlSignal>? PendingQmlSignals { get; set; }

        public string? Warning { get; set; }

        /// <summary>Error reported by Qt for the element (e.g. file not found), null if none.</summary>
        public string? Error { get; set; }

        /// <summary>
        /// Error of the last failed apply that addressed this element (e.g. a value Qt rejected), null if none.
        /// Set and cleared by the renderer; kept across the resync, unlike <see cref="Error"/>.
        /// </summary>
        public string? ApplyError { get; set; }

        /// <summary>
        /// Containers: error of the last failed <see cref="Styles.IStylable.Build"/> of a child, null if none.
        /// Set and cleared by the reconciler.
        /// </summary>
        public string? BuildError { get; set; }
    }

    /// <summary>
    /// Stable per-node-instance receiver of runtime state (hover, press, measured size, ...), one
    /// <see cref="SinkState"/> per slot index. Plain data: filled by the event reader (plan 06),
    /// read by the element node. No reference to Qt or native objects. Main thread only.
    /// </summary>
    public sealed class ElementSink
    {
        private readonly List<SinkState> _states = new();

        /// <summary>Number of slots currently allocated.</summary>
        public int Count => _states.Count;

        /// <summary>Returns the state for <paramref name="index"/>, growing the slot list if needed.</summary>
        public SinkState this[int index]
        {
            get
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                while (_states.Count <= index) _states.Add(new SinkState());
                return _states[index];
            }
        }

        /// <summary>Returns the state if the slot exists, without allocating.</summary>
        public bool TryGet(int index, out SinkState state)
        {
            if (index >= 0 && index < _states.Count) { state = _states[index]; return true; }
            state = null!;
            return false;
        }

        /// <summary>Shrinks to <paramref name="count"/> slots (spread got shorter).</summary>
        public void Resize(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count < _states.Count) _states.RemoveRange(count, _states.Count - count);
        }

        // --- API for the element nodes (plan 06). Values are written by the SinkRegistry after each poll,
        // i.e. 1 frame latency. Reading a slot that never received an event returns the default. ---

        /// <summary>Pointer is over the item (Hover node and handler sinks).</summary>
        public bool IsHovered(int index = 0) => TryGet(index, out var s) && s.IsHovered;

        /// <summary>Item is pressed (Hover node and handler sinks).</summary>
        public bool IsPressed(int index = 0) => TryGet(index, out var s) && s.IsPressed;

        /// <summary>
        /// Number of clicks since the last call, and resets it (call it once per Update of the element node; clicks
        /// that arrive while nobody takes them stay pending). Elements: Button clicks only.
        /// </summary>
        public int TakeClicks(int index = 0)
        {
            if (!TryGet(index, out var s) || s.PendingClicks <= 0)
                return 0;
            int clicks = s.PendingClicks;
            s.PendingClicks = 0;
            return clicks;
        }

        /// <summary>
        /// Value input: takes the last user change (EV_VALUE) since the last call, and resets it. Returns
        /// false if there is none or its value type does not match <typeparamref name="T"/> (int = i32, float = f32,
        /// bool, string); a mismatching change is dropped. Call once per Update. No allocation.
        /// </summary>
        public bool TakeValue<T>(out T value, int index = 0)
        {
            value = default!;
            if (!TryGet(index, out var s) || !s.HasPendingValue)
                return false;
            s.HasPendingValue = false;
            int bits = s.PendingValue;
            var str = s.PendingString;
            s.PendingString = null;
            switch (s.PendingValueType)
            {
                case Protocol.Vt.I32 when typeof(T) == typeof(int):
                    value = Unsafe.As<int, T>(ref bits);
                    return true;
                case Protocol.Vt.F32 when typeof(T) == typeof(float):
                    float f = BitConverter.Int32BitsToSingle(bits);
                    value = Unsafe.As<float, T>(ref f);
                    return true;
                case Protocol.Vt.Bool when typeof(T) == typeof(bool):
                    bool b = bits != 0;
                    value = Unsafe.As<bool, T>(ref b);
                    return true;
                case Protocol.Vt.String when typeof(T) == typeof(string):
                    str ??= string.Empty;
                    value = Unsafe.As<string, T>(ref str);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Most signals kept for a node that does not take them (QmlElement); further ones are dropped.</summary>
        internal const int MaxPendingQmlSignals = 4096;

        /// <summary>QmlElement: the last value of each root property that QML changed. The live table: copy it, do not keep it. Null if none yet.</summary>
        internal Dictionary<string, object>? QmlPropertyValues(int index = 0) => TryGet(index, out var s) ? s.QmlProperties : null;

        /// <summary>QmlElement: number of property changes (and resets) so far; differs from the last call when <see cref="QmlPropertyValues"/> has something new.</summary>
        internal int QmlPropertyUpdates(int index = 0) => TryGet(index, out var s) ? s.QmlPropertyUpdates : 0;

        /// <summary>Stores a property change reported by QML (EV_QML_PROPERTY): the last value per name counts.</summary>
        internal void SetQmlProperty(int index, string name, object value)
        {
            var s = this[index];
            (s.QmlProperties ??= new Dictionary<string, object>(StringComparer.Ordinal))[name] = value;
            unchecked { s.QmlPropertyUpdates++; }
        }

        /// <summary>Queues a signal of the QML root (EV_QML_SIGNAL); dropped beyond <see cref="MaxPendingQmlSignals"/> (nobody takes them).</summary>
        internal void AddQmlSignal(int index, QmlSignal signal)
        {
            var s = this[index];
            s.PendingQmlSignals ??= new List<QmlSignal>();
            if (s.PendingQmlSignals.Count < MaxPendingQmlSignals)
                s.PendingQmlSignals.Add(signal);
        }

        /// <summary>QmlElement: takes the signals that arrived since the last call, in order, and resets them. Call once per Update.</summary>
        internal Spread<QmlSignal> TakeQmlSignals(int index = 0)
        {
            if (!TryGet(index, out var s) || s.PendingQmlSignals is not { Count: > 0 } pending)
                return Spread<QmlSignal>.Empty;
            var taken = pending.ToSpread();
            pending.Clear();
            return taken;
        }

        /// <summary>QmlElement: forgets the property values and the pending signals (the node reloaded the file or loads another one).</summary>
        internal void ResetQml(int index = 0)
        {
            if (!TryGet(index, out var s))
                return;
            s.QmlProperties?.Clear();
            s.PendingQmlSignals?.Clear();
            unchecked { s.QmlPropertyUpdates++; }
        }

        /// <summary>Tap count of the last tap, 0 before the first tap (Click node).</summary>
        public int TapCount(int index = 0) => TryGet(index, out var s) ? s.TapCount : 0;

        /// <summary>Number of long presses since the last call, and resets it (Click node; call once per Update).</summary>
        public int TakeLongPresses(int index = 0)
        {
            if (!TryGet(index, out var s) || s.PendingLongPresses <= 0)
                return 0;
            int count = s.PendingLongPresses;
            s.PendingLongPresses = 0;
            return count;
        }

        /// <summary>A point is down on the item (Point node).</summary>
        public bool IsActive(int index = 0) => TryGet(index, out var s) && s.IsActive;

        /// <summary>
        /// Wheel notches (angleDelta / 120) since the last call, and resets it (Wheel node; call once per Update).
        /// </summary>
        public Vector2 TakeWheel(int index = 0)
        {
            if (!TryGet(index, out var s))
                return default;
            var wheel = s.PendingWheel;
            s.PendingWheel = default;
            return wheel;
        }

        /// <summary>Drag handler: translation of the running (or last) drag in units.</summary>
        public Vector2 DragDelta(int index = 0) => TryGet(index, out var s) ? s.DragDelta : default;

        /// <summary>Drag handler: number of drag updates received so far (changes with every EV_DRAG).</summary>
        public int DragUpdates(int index = 0) => TryGet(index, out var s) ? s.DragUpdates : 0;

        /// <summary>Touch Points handler: the points pressed in the item right now. The same instance until the next event.</summary>
        public Spread<TouchPoint> TouchPoints(int index = 0) => TryGet(index, out var s) ? s.TouchPoints : Spread<TouchPoint>.Empty;

        /// <summary>Touch Points handler: number of touch events received so far.</summary>
        public int TouchUpdates(int index = 0) => TryGet(index, out var s) ? s.TouchUpdates : 0;

        /// <summary>Pinch handler: scale factor of the running (or last) gesture, 1 before the first.</summary>
        public float PinchScale(int index = 0) => TryGet(index, out var s) ? s.PinchScale : 1f;

        /// <summary>Pinch handler: rotation in cycles, clockwise positive.</summary>
        public float PinchRotation(int index = 0) => TryGet(index, out var s) ? s.PinchRotation : 0f;

        /// <summary>Pinch handler: translation in units.</summary>
        public Vector2 PinchTranslation(int index = 0) => TryGet(index, out var s) ? s.PinchTranslation : default;

        /// <summary>Pinch handler: pivot in units.</summary>
        public Vector2 PinchPivot(int index = 0) => TryGet(index, out var s) ? s.PinchPivot : default;

        /// <summary>Pinch handler: number of pinch updates received so far.</summary>
        public int PinchUpdates(int index = 0) => TryGet(index, out var s) ? s.PinchUpdates : 0;

        /// <summary>Last pointer position, local to the item, in units (Hover and Point node).</summary>
        public Vector2 PointerPosition(int index = 0) => TryGet(index, out var s) ? s.PointerPosition : default;

        /// <summary>Implicit size of the item (Text: actual text size), in units.</summary>
        public Vector2 MeasuredSize(int index = 0) => TryGet(index, out var s) ? s.MeasuredSize : default;

        /// <summary>Bounding rectangle of the item in window (scene) coordinates, in units.</summary>
        public RectangleF SceneBounds(int index = 0) => TryGet(index, out var s) ? s.SceneBounds : default;

        /// <summary>Video: playback position in seconds (0 until the media is loaded).</summary>
        public float MediaPosition(int index = 0) => TryGet(index, out var s) ? s.MediaPosition : 0;

        /// <summary>Video: media duration in seconds (0 until the media is loaded).</summary>
        public float MediaDuration(int index = 0) => TryGet(index, out var s) ? s.MediaDuration : 0;

        /// <summary>First warning of any slot (e.g. duplicate from the reconciler), null if none.</summary>
        public string? Warning
        {
            get
            {
                for (int i = 0; i < _states.Count; i++)
                    if (_states[i].Warning is { } w)
                        return w;
                return null;
            }
        }

        /// <summary>
        /// First error of any slot (reported by Qt, e.g. a file that cannot be opened, or a rejected apply, or a
        /// failed Build of an IStylable child), null if none.
        /// </summary>
        public string? Error
        {
            get
            {
                for (int i = 0; i < _states.Count; i++)
                    if ((_states[i].ApplyError ?? _states[i].Error ?? _states[i].BuildError) is { } e)
                        return e;
                return null;
            }
        }
    }

    /// <summary>Reference from an element description to one slot of a sink. Default = no sink.</summary>
    public readonly record struct SinkSlot(ElementSink? Sink, int Index)
    {
        public bool IsNone => Sink is null;

        /// <summary>State of the slot, or null for <c>default</c>.</summary>
        public SinkState? State => Sink?[Index];
    }
}
