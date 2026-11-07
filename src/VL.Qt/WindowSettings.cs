using System;
using Stride.Core.Mathematics;
using VL.Core;

namespace VL.Qt
{
    /// <summary>
    /// Immutable window settings for the <c>Window</c> pin of Renderer (#517, #518). Every value is optional: a value
    /// that is set wins over the matching Renderer pin (Title, Borderless, Fullscreen, Bounds), an unset one leaves the
    /// Renderer pin (or the default). <see cref="Nodes.WindowSettingsNode"/> hands out the same instance as long as its inputs
    /// do not change.
    /// </summary>
    public sealed class WindowSettings
    {
        internal WindowSettings(Optional<string> icon, Optional<string> title, Optional<bool> borderless, Optional<bool> fullscreen, Optional<RectangleF> bounds)
        {
            Icon = icon;
            Title = title;
            Borderless = borderless;
            Fullscreen = fullscreen;
            Bounds = bounds;
        }

        /// <summary>Icon file as given in the patch; unset or empty = the default icon.</summary>
        internal Optional<string> Icon { get; }

        internal Optional<string> Title { get; }

        internal Optional<bool> Borderless { get; }

        internal Optional<bool> Fullscreen { get; }

        /// <summary>Only used when the window is created.</summary>
        internal Optional<RectangleF> Bounds { get; }

        /// <summary>The live values of a window after applying the precedence rule (<see cref="Resolve"/>).</summary>
        internal readonly record struct Resolved(string? Title, bool Borderless, bool Fullscreen, string? IconPath);

        /// <summary>
        /// Precedence of #518: a value set in <paramref name="settings"/> wins over the Renderer pin, otherwise the Renderer
        /// pin counts. The icon has no Renderer pin: unset or empty = the default icon (<see cref="Resolved.IconPath"/> null),
        /// else the full path (Qt resolves a relative path against the working directory, which is not the patch).
        /// </summary>
        internal static Resolved Resolve(WindowSettings? settings, string? titlePin, bool borderlessPin, bool fullscreenPin)
            => new(
                settings is { Title.HasValue: true } ? settings.Title.Value : titlePin,
                settings is { Borderless.HasValue: true } ? settings.Borderless.Value : borderlessPin,
                settings is { Fullscreen.HasValue: true } ? settings.Fullscreen.Value : fullscreenPin,
                settings is { Icon.HasValue: true } ? IconFullPath(settings.Icon.Value) : null);

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
