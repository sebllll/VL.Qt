using System;
using Stride.Core.Mathematics;
using VL.Core;

namespace VL.Qt
{
    /// <summary>
    /// Immutable window settings for the <c>Window</c> pin of Renderer: the icon and the initial bounds. Title, Borderless
    /// and Fullscreen are pins of Renderer only. Every value is optional; an unset Bounds leaves the Renderer's Bounds pin.
    /// <see cref="Nodes.WindowSettingsNode"/> hands out the same instance as long as its inputs do not change.
    /// </summary>
    public sealed class WindowSettings
    {
        internal WindowSettings(Optional<string> icon, Optional<RectangleF> bounds)
        {
            Icon = icon;
            Bounds = bounds;
            // Resolved once: the settings are immutable, so nothing calls Path.GetFullPath per frame.
            IconPath = icon.HasValue ? IconFullPath(icon.Value) : null;
        }

        /// <summary>Icon file as given in the patch; unset or empty = the default icon.</summary>
        internal Optional<string> Icon { get; }

        /// <summary>
        /// The full path of <see cref="Icon"/> (Qt resolves a relative path against the working directory, which is not the
        /// patch); null = the default icon (unset, empty or blank).
        /// </summary>
        internal string? IconPath { get; }

        /// <summary>Only used when the window is created.</summary>
        internal Optional<RectangleF> Bounds { get; }

        /// <summary>Bounds for creating the window: the settings value if set, else the Renderer's Bounds pin.</summary>
        internal static RectangleF ResolveBounds(WindowSettings? settings, RectangleF boundsPin)
            => settings is { Bounds.HasValue: true } ? settings.Bounds.Value : boundsPin;

        /// <summary>Empty or blank → null (default icon); a relative path is made absolute, a path .NET rejects stays as given (Qt reports it).</summary>
        internal static string? IconFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return path;
            }
        }
    }
}
