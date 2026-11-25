using System;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// Pure parts of the window placement. Screen logic (rect on no screen → centered on the primary working
    /// area, fallback size) lives in QtNative (vlqt_window_create, QScreen), so it is platform neutral.
    /// </summary>
    internal static class WindowPlacement
    {
        /// <summary>Default of the Renderer's Bounds pin ([DefaultValue] string; equality with <see cref="DefaultBounds"/> is unit-tested).</summary>
        public const string DefaultBoundsString = "1000, 40, 786, 432";
        public static readonly RectangleF DefaultBounds = new RectangleF(1000, 40, 786, 432);

        /// <summary>
        /// Bounds a Renderer creates its window with. A node added live, or restored by Undo/Redo, is created by
        /// vvvv's hotswap (IL of vvvv 8.0-0156, VL.AppServices: CompilationHelper.RestoreManagedField → CreateNew →
        /// AppHost.CreateInstance without arguments): create pins get neither their pin value nor the [DefaultValue]
        /// attribute, only C# parameter defaults, otherwise the VL default of the type, for RectangleF (-0.5, -0.5, 1, 1).
        /// That gave a 1x1 DIP window (Windows: just a caption, centered as partly off screen) until the app was
        /// restarted. A Bounds equal to <paramref name="typeDefault"/> is therefore read as "no value" → <see cref="DefaultBounds"/>.
        /// </summary>
        // The same holds for a Bounds value set in the patch: the hotswap does not pass it either, so such a node opens at the
        // default bounds until the next restart (Save Bounds writes only once the window is moved, so the pin keeps its value).
        public static RectangleF CreateBounds(RectangleF bounds, RectangleF typeDefault)
            => bounds == typeDefault ? DefaultBounds : bounds;

        /// <summary>Arguments of vlqt_window_create.</summary>
        public readonly record struct CreateArgs(float X, float Y, int Width, int Height);

        /// <summary>
        /// Bounds pin (DIP) → create arguments. Empty bounds (width or height &lt; 1) → NaN position (= centered) and size 0
        /// (= QtNative's 600x400 fallback), like VL.Skia's SkiaRenderer.SetBounds.
        /// </summary>
        public static CreateArgs FromBounds(RectangleF bounds)
        {
            if (!(bounds.Width >= 1 && bounds.Height >= 1))
                return new CreateArgs(float.NaN, float.NaN, 0, 0);
            return new CreateArgs(bounds.X, bounds.Y, (int)MathF.Round(bounds.Width), (int)MathF.Round(bounds.Height));
        }

        /// <summary>Last EVENT_WINDOW_GEOMETRY record (handle 0) of a poll buffer; false if there is none.</summary>
        public static bool TryReadGeometry(ReadOnlySpan<byte> poll, out WindowGeometry geometry)
        {
            geometry = default;
            bool found = false;
            var r = new EventReader(poll);
            while (r.MoveNext())
            {
                if (r.Event != Protocol.EventId.WindowGeometry || r.FeedbackId != 0 || r.Payload.Length < 20)
                    continue;
                var bounds = new RectangleF(r.ReadF32(0), r.ReadF32(4), r.ReadF32(8), r.ReadF32(12));
                // Client top-left since #465 (28 bytes); an older record has none: client = outer position.
                var client = r.Payload.Length >= 28 ? new RectangleF(r.ReadF32(20), r.ReadF32(24), bounds.Width, bounds.Height) : bounds;
                geometry = new WindowGeometry(bounds, (WindowStates)r.ReadU32(16), client);
                found = true;
            }
            return found;
        }
    }
}
