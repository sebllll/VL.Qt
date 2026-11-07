using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Settings of the Qt window: icon, title, frame and placement. Connect Output to the Window pin of Renderer.
    /// A value that is set here wins over the matching pin of Renderer; unset values leave the Renderer pin (or the default).
    /// </summary>
    [ProcessNode(Name = "WindowSettings", Category = Categories.Qt)]
    public sealed class WindowSettingsNode
    {
        WindowSettings? _last;
        Optional<string> _icon, _title;
        Optional<bool> _borderless, _fullscreen;
        Optional<RectangleF> _bounds;

        /// <summary>Describes the window settings; the same instance as long as no input changes.</summary>
        /// <param name="icon">Icon of the window (title bar, taskbar, Alt-Tab): .ico, .png, .svg or anything Qt reads; a relative path is relative to the patch. Changes apply live; empty or unset shows the built-in default icon. A file that cannot be loaded keeps the default icon and shows a warning at the Renderer. Wayland: the icon comes from the app id and .desktop file, not from here.</param>
        /// <param name="title">Window title, live. Wins over the Title pin of Renderer.</param>
        /// <param name="borderless">Window without frame and title bar, live. Wins over the Borderless pin of Renderer.</param>
        /// <param name="fullscreen">Fullscreen, live; acts only when the value changes (F11 / Alt+Enter still toggle it). Wins over the Fullscreen pin of Renderer.</param>
        /// <param name="bounds">Initial position and size in DIP, only when the window is created (like Bounds of Renderer, which it wins over; a change does not move an open window). Save Bounds still writes to the Bounds pin of Renderer.</param>
        [return: Pin(Name = "Output")]
        public WindowSettings Update(
            [Pin(Visibility = PinVisibility.Optional)] Optional<Path> icon,
            [Pin(Visibility = PinVisibility.Optional)] Optional<string> title,
            [Pin(Visibility = PinVisibility.Optional)] Optional<bool> borderless,
            [Pin(Visibility = PinVisibility.Optional)] Optional<bool> fullscreen,
            [Pin(Visibility = PinVisibility.Optional)] Optional<RectangleF> bounds)
        {
            var iconPath = icon.HasValue ? new Optional<string>(icon.Value?.Value ?? "") : default;
            if (_last is null || !iconPath.Equals(_icon) || !title.Equals(_title) || !borderless.Equals(_borderless)
                || !fullscreen.Equals(_fullscreen) || !bounds.Equals(_bounds))
            {
                _icon = iconPath;
                _title = title;
                _borderless = borderless;
                _fullscreen = fullscreen;
                _bounds = bounds;
                _last = new WindowSettings(iconPath, title, borderless, fullscreen, bounds);
            }
            return _last;
        }
    }
}
