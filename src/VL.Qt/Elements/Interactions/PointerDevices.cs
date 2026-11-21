using System;

namespace VL.Qt.Elements
{
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
}
