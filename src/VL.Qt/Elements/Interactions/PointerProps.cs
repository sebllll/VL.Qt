using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Shared SETs of the pointer handlers: only values that differ from the old ones (a new handler: from QtNative defaults).</summary>
    internal static class PointerProps
    {
        public static void Write(ICommandSink sink, uint id, PointerButtons buttons, PointerDevices devices,
            PointerButtons oldButtons, PointerDevices oldDevices, bool writeButtons)
        {
            if (writeButtons && buttons != oldButtons)
                sink.SetI32(id, Protocol.PropertyId.AcceptedButtons, (int)buttons);
            if (devices != oldDevices)
                sink.SetI32(id, Protocol.PropertyId.AcceptedDevices, PointerMasks.ToQt(devices));
        }
    }
}
