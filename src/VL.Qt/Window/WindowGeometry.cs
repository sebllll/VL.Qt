using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Window geometry reported by QtNative (EVENT_WINDOW_GEOMETRY) in DIP, same convention as the Renderer's Bounds pin on
    /// create: <see cref="Bounds"/> position = outer top-left incl. frame, size = client size. Setting it back as Bounds
    /// round-trips (vlqt_window_create). Minimized/fullscreen records carry no usable position (Windows: -32000).
    /// <see cref="ClientBounds"/> = client top-left + client size (Renderer's Client Bounds output).
    /// </summary>
    internal readonly record struct WindowGeometry(RectangleF Bounds, WindowStates States, RectangleF ClientBounds = default)
    {
        public bool IsMinimized => (States & WindowStates.Minimized) != 0;
        public bool IsFullscreen => (States & WindowStates.Fullscreen) != 0;
        public bool IsVisible => (States & WindowStates.Visible) != 0;
        public bool IsMaximized => (States & WindowStates.Maximized) != 0;

        /// <summary>Visible, not minimized, maximized or fullscreen: the geometry a restart should restore.</summary>
        public bool IsNormal => IsVisible && (States & (WindowStates.Minimized | WindowStates.Maximized | WindowStates.Fullscreen)) == 0;
    }
}
