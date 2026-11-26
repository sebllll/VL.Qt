using System;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>State flags of a <see cref="WindowGeometry"/> (EVENT_WINDOW_GEOMETRY, <see cref="Protocol.WindowState"/>).</summary>
    [Flags]
    internal enum WindowStates
    {
        None = 0,
        Minimized = (int)Protocol.WindowState.Minimized,
        Maximized = (int)Protocol.WindowState.Maximized,
        Fullscreen = (int)Protocol.WindowState.Fullscreen,
        Visible = (int)Protocol.WindowState.Visible,
    }
}
