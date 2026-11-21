using System;

namespace VL.Qt.Elements
{
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
}
