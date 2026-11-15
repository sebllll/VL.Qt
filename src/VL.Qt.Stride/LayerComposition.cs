using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Qt.Stride
{
    /// <summary>
    /// How QtRenderer composes its scene into a render target (render-texture.md, "Stride layer"): Direct draws a UNorm
    /// texture (gamma-space values, as a Qt window) through a UNorm view of the target's resource, Fallback draws a texture in
    /// the target's view format (linear values, as QtTexture). Both blend premultiplied (One, InvSrcAlpha).
    /// </summary>
    /// <param name="Direct">True if the target's resource takes a UNorm view: the layer blends like a Qt window.</param>
    /// <param name="CoreFormat">Format of the scene's texture (a format <see cref="TextureFormats.TryGetTextureFormat"/> allows).</param>
    /// <param name="ViewFormat">Format of the render target view the layer draws through.</param>
    internal readonly record struct LayerComposition(bool Direct, PixelFormat CoreFormat, PixelFormat ViewFormat)
    {
        /// <summary>
        /// Returns the composition for a target whose native resource has <paramref name="resourceFormat"/> and whose Stride
        /// view has <paramref name="viewFormat"/>: Direct for 8-bit RGBA/BGRA resources that are UNorm or typeless (window
        /// back buffer, typeless render texture), otherwise <see cref="Fallback"/>.
        /// </summary>
        public static LayerComposition Choose(PixelFormat resourceFormat, PixelFormat viewFormat) => resourceFormat switch
        {
            PixelFormat.R8G8B8A8_UNorm or PixelFormat.R8G8B8A8_Typeless
                => new LayerComposition(true, PixelFormat.R8G8B8A8_UNorm, PixelFormat.R8G8B8A8_UNorm),
            PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_Typeless
                => new LayerComposition(true, PixelFormat.B8G8R8A8_UNorm, PixelFormat.B8G8R8A8_UNorm),
            _ => Fallback(viewFormat),
        };

        /// <summary>
        /// Returns the fallback for a target view of <paramref name="viewFormat"/>: the scene renders in that format if a
        /// QtTexture allows it, else in an SRgb or float format that holds the same linear values; drawn through the target's own view.
        /// </summary>
        public static LayerComposition Fallback(PixelFormat viewFormat)
        {
            var core = TextureFormats.TryGetTextureFormat(viewFormat, out _) ? viewFormat
                : viewFormat.IsSRgb() ? PixelFormat.B8G8R8A8_UNorm_SRgb
                : PixelFormat.R16G16B16A16_Float;
            return new LayerComposition(false, core, viewFormat);
        }

        /// <summary>Returns the hint for a fallback (shown at Error, logged once per change), empty for Direct.</summary>
        public string Hint(PixelFormat resourceFormat) => Direct ? ""
            : $"QtRenderer draws into a target without a UNorm view (resource {resourceFormat}, view {ViewFormat}): half-transparent colours blend in linear light as with QtTexture. Draw it into a window or a typeless 8-bit render texture to blend like a Qt window.";

        /// <summary>Returns the size of the scene's texture for a viewport of <paramref name="width"/> x <paramref name="height"/> pixels (rounded, at least 1x1).</summary>
        public static Int2 TextureSize(float width, float height)
            => new(Math.Max(1, (int)MathF.Round(width)), Math.Max(1, (int)MathF.Round(height)));

        /// <summary>Returns the texture pixel of a window position (client pixels): the position minus the viewport origin (1 DIP = 1 pixel).</summary>
        public static Vector2 ToTexturePixels(Vector2 position, Vector2 viewportOrigin) => position - viewportOrigin;
    }
}
