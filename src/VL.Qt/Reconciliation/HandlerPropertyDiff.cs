using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes the SETs of the properties of an input handler (one <see cref="Interaction"/> link) as a diff against the
    /// link that was sent before. Handlers are diffed here, items in <see cref="PropertyDiff"/>. The defaults below are the
    /// values QtNative gives a new handler (protocol.h), so a new handler is sent only what differs from them.
    /// </summary>
    internal static class HandlerPropertyDiff
    {
        // Defaults of QtNative (protocol.h, VLQT_PROPERTY_*).
        private const bool defaultMovesElement = true;
        private const DragAxis defaultDragAxis = DragAxis.Both;
        private const bool defaultOcclusion = true;
        private const int defaultPointCount = 2;
        private const PointerButtons defaultButtons = PointerButtons.Left;
        private const PointerDevices defaultDevices = PointerDevices.All;

        /// <summary>Qt's default of acceptedDevices (all device types, also those without an enum value here).</summary>
        private const int allDevices = 0x7FFFFFFF;

        /// <summary>
        /// Writes the SETs of the handler <paramref name="id"/> for the properties of <paramref name="next"/> that differ from
        /// <paramref name="prev"/> (the same handler type; null = just created, send only values that differ from QtNative's
        /// defaults). <paramref name="mapping"/> converts lengths (Drag limits) to logical pixels; when its scale changes the
        /// reconciler passes <paramref name="prev"/> = null so every non-default value is sent again.
        /// </summary>
        public static void Write(ICommandSink sink, uint id, Interaction next, Interaction? prev, in SpaceMapping mapping)
        {
            switch (next)
            {
                case ClickInteraction click:
                {
                    var p = prev as ClickInteraction;
                    WriteButtons(sink, id, click.Buttons, p?.Buttons ?? defaultButtons);
                    WriteDevices(sink, id, click.Devices, p?.Devices ?? defaultDevices);
                    break;
                }
                case PointInteraction point:
                {
                    var p = prev as PointInteraction;
                    WriteButtons(sink, id, point.Buttons, p?.Buttons ?? defaultButtons);
                    WriteDevices(sink, id, point.Devices, p?.Devices ?? defaultDevices);
                    break;
                }
                case DragInteraction drag:
                {
                    var p = prev as DragInteraction;
                    if (drag.MovesElement != (p?.MovesElement ?? defaultMovesElement))
                        sink.SetBool(id, Protocol.PropertyId.MovesElement, drag.MovesElement);
                    if (drag.Axis != (p?.Axis ?? defaultDragAxis))
                        sink.SetI32(id, Protocol.PropertyId.DragAxis, (int)drag.Axis);
                    if (drag.Limits != (p?.Limits ?? DragLimits.None))
                    {
                        var l = drag.Limits;
                        sink.SetRect(id, Protocol.PropertyId.DragLimits, mapping.ToQt(l.MinX), mapping.ToQt(l.MinY), mapping.ToQt(l.MaxX), mapping.ToQt(l.MaxY));
                    }
                    WriteButtons(sink, id, drag.Buttons, p?.Buttons ?? defaultButtons);
                    WriteDevices(sink, id, drag.Devices, p?.Devices ?? defaultDevices);
                    break;
                }
                case WheelInteraction wheel:
                    WriteDevices(sink, id, wheel.Devices, (prev as WheelInteraction)?.Devices ?? defaultDevices);
                    break;
                case PinchInteraction pinch:
                {
                    var p = prev as PinchInteraction;
                    if (pinch.MinimumPointCount != (p?.MinimumPointCount ?? defaultPointCount))
                        sink.SetI32(id, Protocol.PropertyId.MinimumPointCount, pinch.MinimumPointCount);
                    if (pinch.MaximumPointCount != (p?.MaximumPointCount ?? defaultPointCount))
                        sink.SetI32(id, Protocol.PropertyId.MaximumPointCount, pinch.MaximumPointCount);
                    WriteDevices(sink, id, pinch.Devices, p?.Devices ?? defaultDevices);
                    break;
                }
                case TouchPointsInteraction touch:
                {
                    var p = prev as TouchPointsInteraction;
                    if (touch.Occlusion != (p?.Occlusion ?? defaultOcclusion))
                        sink.SetBool(id, Protocol.PropertyId.Occlusion, touch.Occlusion);
                    WriteDevices(sink, id, touch.Devices, p?.Devices ?? defaultDevices);
                    break;
                }
                // HoverInteraction has no properties: QtNative defaults are all a Hover handler has.
            }
        }

        private static void WriteButtons(ICommandSink sink, uint id, PointerButtons buttons, PointerButtons oldButtons)
        {
            if (buttons != oldButtons)
                sink.SetI32(id, Protocol.PropertyId.AcceptedButtons, (int)buttons);
        }

        /// <summary>ACCEPTED_DEVICES: <see cref="PointerDevices.All"/> maps to Qt's AllDevices.</summary>
        private static void WriteDevices(ICommandSink sink, uint id, PointerDevices devices, PointerDevices oldDevices)
        {
            if (devices != oldDevices)
                sink.SetI32(id, Protocol.PropertyId.AcceptedDevices, devices == PointerDevices.All ? allDevices : (int)devices);
        }
    }
}
