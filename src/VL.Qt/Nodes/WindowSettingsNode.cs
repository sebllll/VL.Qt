using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Settings of the Qt window: icon and initial placement. Connect Output to the Window pin of Renderer.
    /// Title, Borderless and Fullscreen are pins of Renderer.
    /// </summary>
    [ProcessNode(Name = "WindowSettings", Category = Categories.Qt)]
    public sealed class WindowSettingsNode
    {
        private WindowSettings? _last;
        private Optional<string> _icon;
        private Optional<RectangleF> _bounds;

        /// <summary>Describes the window settings; the same instance as long as no input changes.</summary>
        /// <param name="icon">Icon of the window (title bar, taskbar, Alt-Tab): .ico, .png, .svg or anything Qt reads; a relative path is
        /// relative to the patch. Changes apply live; empty or unset shows the built-in default icon. A file that cannot be loaded keeps
        /// the default icon and shows a warning at the Renderer. Wayland: the icon comes from the app id and .desktop file, not from
        /// here.</param>
        /// <param name="bounds">Initial position and size in DIP, only when the window is created (like Bounds of Renderer, which it wins
        /// over when set; a change does not move an open window). Save Bounds still writes to the Bounds pin of Renderer.</param>
        [return: Pin(Name = "Output")]
        public WindowSettings Update(
            [Pin(Visibility = PinVisibility.Optional)] Optional<Path> icon,
            [Pin(Visibility = PinVisibility.Optional)] Optional<RectangleF> bounds)
        {
            var iconPath = icon.HasValue ? new Optional<string>(icon.Value?.Value ?? "") : default;
            if (_last is null || !iconPath.Equals(_icon) || !bounds.Equals(_bounds))
            {
                _icon = iconPath;
                _bounds = bounds;
                _last = new WindowSettings(iconPath, bounds);
            }
            return _last;
        }
    }
}
