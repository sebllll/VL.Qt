using System;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Immutable chain of interaction descriptions (Hover, Click, Point, Wheel, …) for the <c>Interaction</c> pin of an element. Each
    /// interaction node adds one link and keeps a reference to the rest of the chain in <see cref="Previous"/>. One link
    /// becomes one Qt Quick input handler at the element's item (a protocol object of its own, see protocol.h).
    /// <para>
    /// Identity is the <see cref="Sink"/> of the interaction node: the reconciler keys the handlers of an element by it.
    /// Create a new instance only when an input of the node changes; an unchanged chain is the same instance (the
    /// reconciler compares by reference). The order of the chain has no meaning (Qt decides by its grab rules).
    /// </para>
    /// </summary>
    public abstract class Interaction
    {
        private protected Interaction(ushort handlerType, Interaction? previous, SinkSlot sink)
        {
            if (sink.IsNone)
                throw new ArgumentException("An interaction needs a sink.", nameof(sink));
            HandlerType = handlerType;
            Previous = previous;
            Sink = sink;
        }

        /// <summary>The rest of the chain (the interactions connected before this one), null at the start.</summary>
        public Interaction? Previous { get; }

        /// <summary>Receives the handler's events (one slot of the interaction node's sink); key of the handler.</summary>
        public SinkSlot Sink { get; }

        /// <summary>CREATE type of the handler (<see cref="Protocol.Type"/>, from 32).</summary>
        internal ushort HandlerType { get; }

        /// <summary>
        /// Writes the SETs of the handler properties that differ from <paramref name="previous"/> (same handler type;
        /// null = just created, send only values that differ from QtNative's defaults).
        /// <paramref name="mapping"/> converts lengths (Drag limits) to logical pixels; when its scale changes the reconciler
        /// passes <paramref name="previous"/> = null so every non-default value is sent again (#143).
        /// </summary>
        internal abstract void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping);
    }

    /// <summary>Hover handler (Qt Quick HoverHandler): reports hover only; positions come from the Point handler (#320).</summary>
    internal sealed class HoverInteraction : Interaction
    {
        public HoverInteraction(Interaction? previous, SinkSlot sink)
            : base(Protocol.Type.HoverHandler, previous, sink)
        {
        }

        /// <summary>No properties: QtNative defaults are all a Hover handler has.</summary>
        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
        }
    }

    /// <summary>Mouse buttons a Click, Point or Drag node reacts to (bit mask, values of Qt::MouseButton). None: Click and Drag accept touch and pen only; Point follows the hovering mouse and touch/pen while down.</summary>
    [Flags]
    public enum PointerButtons
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 4,
        Back = 8,
        Forward = 16,
    }

    /// <summary>
    /// Input devices a Click, Point, Wheel or Drag node reacts to (bit mask of QInputDevice::DeviceType; <see cref="Pen"/> =
    /// stylus, puck and airbrush).
    /// </summary>
    [Flags]
    public enum PointerDevices
    {
        None = 0,
        Mouse = 0x1,
        TouchScreen = 0x2,
        TouchPad = 0x4,
        Pen = 0x8 | 0x10 | 0x20,
        All = Mouse | TouchScreen | TouchPad | Pen,
    }

    static class PointerMasks
    {
        /// <summary>Qt's default of acceptedDevices (all device types, also those without an enum value here).</summary>
        const int allDevices = 0x7FFFFFFF;

        public const PointerButtons DefaultButtons = PointerButtons.Left;
        public const PointerDevices DefaultDevices = PointerDevices.All;

        /// <summary>The value for ACCEPTED_DEVICES: <see cref="PointerDevices.All"/> maps to Qt's AllDevices.</summary>
        public static int ToQt(PointerDevices devices) => devices == PointerDevices.All ? allDevices : (int)devices;
    }

    /// <summary>Tap handler (Qt Quick TapHandler): press, click, tap count, long press.</summary>
    internal sealed class ClickInteraction : Interaction
    {
        public ClickInteraction(Interaction? previous, SinkSlot sink, PointerButtons buttons, PointerDevices devices)
            : base(Protocol.Type.TapHandler, previous, sink)
        {
            Buttons = buttons;
            Devices = devices;
        }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, Buttons, Devices, previous is ClickInteraction p ? p.Buttons : PointerMasks.DefaultButtons,
                previous is ClickInteraction q ? q.Devices : PointerMasks.DefaultDevices, writeButtons: true);
    }

    /// <summary>Point handler (Qt Quick PointHandler): position of a pressed point; a passive grab, presses pass through.</summary>
    internal sealed class PointInteraction : Interaction
    {
        public PointInteraction(Interaction? previous, SinkSlot sink, PointerButtons buttons, PointerDevices devices)
            : base(Protocol.Type.PointHandler, previous, sink)
        {
            Buttons = buttons;
            Devices = devices;
        }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, Buttons, Devices, previous is PointInteraction p ? p.Buttons : PointerMasks.DefaultButtons,
                previous is PointInteraction q ? q.Devices : PointerMasks.DefaultDevices, writeButtons: true);
    }

    /// <summary>Wheel handler (Qt Quick WheelHandler, target null): wheel and trackpad scroll in notches.</summary>
    internal sealed class WheelInteraction : Interaction
    {
        public WheelInteraction(Interaction? previous, SinkSlot sink, PointerDevices devices)
            : base(Protocol.Type.WheelHandler, previous, sink)
            => Devices = devices;

        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                previous is WheelInteraction p ? p.Devices : PointerMasks.DefaultDevices, writeButtons: false);
    }

    /// <summary>Axes along which a Drag node moves the element (values of VLQT_PROP_DRAG_AXIS).</summary>
    public enum DragAxis
    {
        /// <summary>Free movement in both directions.</summary>
        Both = 0,
        /// <summary>Only along x; a mostly vertical drag is left to the elements below (e.g. a parent's Drag).</summary>
        Horizontal = 1,
        /// <summary>Only along y; a mostly horizontal drag is left to the elements below (e.g. a parent's Drag).</summary>
        Vertical = 2,
    }

    /// <summary>
    /// Drag handler (Qt Quick DragHandler, target null, #209): reports the running drag and, with
    /// <see cref="MovesElement"/>, moves the element by its drag offset in QtNative. Limits are in logical pixels
    /// (minX, minY, maxX, maxY of the offset), ±infinity = none; the node validates min ≤ max before.
    /// </summary>
    internal sealed class DragInteraction : Interaction
    {
        public DragInteraction(Interaction? previous, SinkSlot sink, bool movesElement, DragAxis axis, DragLimits limits,
            PointerButtons buttons, PointerDevices devices)
            : base(Protocol.Type.DragHandler, previous, sink)
        {
            MovesElement = movesElement;
            Axis = axis;
            Limits = limits;
            Buttons = buttons;
            Devices = devices;
        }

        public bool MovesElement { get; }
        public DragAxis Axis { get; }

        /// <summary>Limits of the drag offset in units of the renderer's space (converted in <see cref="WriteProperties"/>).</summary>
        public DragLimits Limits { get; }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as DragInteraction;
            if (MovesElement != (p?.MovesElement ?? true))
                sink.SetBool(id, Protocol.Prop.MovesElement, MovesElement);
            if (Axis != (p?.Axis ?? DragAxis.Both))
                sink.SetI32(id, Protocol.Prop.DragAxis, (int)Axis);
            if (Limits != (p?.Limits ?? DragLimits.None))
                sink.SetRect(id, Protocol.Prop.DragLimits, mapping.ToQt(Limits.MinX), mapping.ToQt(Limits.MinY), mapping.ToQt(Limits.MaxX), mapping.ToQt(Limits.MaxY));
            PointerProps.Write(sink, id, Buttons, Devices, p?.Buttons ?? PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: true);
        }
    }

    /// <summary>
    /// Limits of a drag offset in units (written as the rectf slots of VLQT_PROP_DRAG_LIMITS in logical pixels), ±infinity = none. Exact
    /// float equality (unlike Stride's vectors, whose near-equality fails for infinity).
    /// </summary>
    internal readonly record struct DragLimits(float MinX, float MinY, float MaxX, float MaxY)
    {
        public static readonly DragLimits None = new(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity);
    }

    /// <summary>Touch Points handler (own QtNative handler, passive grabs): all points pressed in the item (#321).</summary>
    internal sealed class TouchPointsInteraction : Interaction
    {
        public TouchPointsInteraction(Interaction? previous, SinkSlot sink, bool occlusion, PointerDevices devices)
            : base(Protocol.Type.TouchPointsHandler, previous, sink)
        {
            Occlusion = occlusion;
            Devices = devices;
        }

        public bool Occlusion { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as TouchPointsInteraction;
            if (Occlusion != (p?.Occlusion ?? true))
                sink.SetBool(id, Protocol.Prop.Occlusion, Occlusion);
            PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: false);
        }
    }

    /// <summary>
    /// Pinch handler (Qt Quick PinchHandler, target null): scale, rotation and translation of the running gesture (#321).
    /// The node validates the point counts (both at least 2, minimum not above maximum) before.
    /// </summary>
    internal sealed class PinchInteraction : Interaction
    {
        internal const int DefaultPointCount = 2;

        public PinchInteraction(Interaction? previous, SinkSlot sink, int minimumPointCount, int maximumPointCount, PointerDevices devices)
            : base(Protocol.Type.PinchHandler, previous, sink)
        {
            MinimumPointCount = minimumPointCount;
            MaximumPointCount = maximumPointCount;
            Devices = devices;
        }

        public int MinimumPointCount { get; }
        public int MaximumPointCount { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as PinchInteraction;
            if (MinimumPointCount != (p?.MinimumPointCount ?? DefaultPointCount))
                sink.SetI32(id, Protocol.Prop.MinimumPointCount, MinimumPointCount);
            if (MaximumPointCount != (p?.MaximumPointCount ?? DefaultPointCount))
                sink.SetI32(id, Protocol.Prop.MaximumPointCount, MaximumPointCount);
            PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: false);
        }
    }

    /// <summary>Shared SETs of the pointer handlers: only values that differ from the old ones (a new handler: from QtNative defaults).</summary>
    static class PointerProps
    {
        public static void Write(ICommandSink sink, uint id, PointerButtons buttons, PointerDevices devices,
            PointerButtons oldButtons, PointerDevices oldDevices, bool writeButtons)
        {
            if (writeButtons && buttons != oldButtons)
                sink.SetI32(id, Protocol.Prop.AcceptedButtons, (int)buttons);
            if (devices != oldDevices)
                sink.SetI32(id, Protocol.Prop.AcceptedDevices, PointerMasks.ToQt(devices));
        }
    }
}
