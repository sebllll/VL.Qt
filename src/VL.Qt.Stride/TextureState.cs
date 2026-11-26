using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Qt.Stride
{
    /// <summary>
    /// The bookkeeping of <see cref="SharedTextureScene"/> without Stride's device: which inputs the texture state was built for,
    /// what a change of the inputs requires, and which texture and error the scene shows. No D3D11 here, so the unit tests
    /// compile it in. Main thread only.
    /// </summary>
    internal sealed class TextureState
    {
        /// <summary>What the scene has to do with its texture after <see cref="Update"/>.</summary>
        public enum Change
        {
            /// <summary>Nothing: the texture matches the inputs.</summary>
            None,
            /// <summary>The sample count changed: the scene is unbound and bound again to the same texture.</summary>
            Rebind,
            /// <summary>Same inputs: bind again only if QtNative unbound the scene meanwhile.</summary>
            RebindIfUnbound,
            /// <summary>Size, format or device changed (or there was no texture state yet): release and create the texture.</summary>
            Recreate,
        }

        private bool _created;
        private Int2 _size;
        private PixelFormat _format;
        private MultisampleCount _samples;
        private object? _device;

        /// <summary>Size of the current texture state (clamped).</summary>
        public Int2 Size => _size;

        /// <summary>Format of the current texture state.</summary>
        public PixelFormat Format => _format;

        /// <summary>Sample count of the current texture state.</summary>
        public MultisampleCount Samples => _samples;

        /// <summary>Returns <paramref name="size"/> clamped to 1 ... <paramref name="maxSize"/> in both directions.</summary>
        public static Int2 ClampSize(Int2 size, int maxSize)
            => new(Math.Clamp(size.X, 1, maxSize), Math.Clamp(size.Y, 1, maxSize));

        /// <summary>
        /// Compares the inputs (<paramref name="size"/> already clamped) with the remembered ones and returns what the scene has to do;
        /// remembers the inputs. <paramref name="hasTexture"/>: the scene holds a texture for the remembered inputs.
        /// </summary>
        public Change Update(Int2 size, PixelFormat format, object? device, MultisampleCount samples, bool hasTexture)
        {
            if (_created && size == _size && format == _format && ReferenceEquals(device, _device))
            {
                var samplesChanged = samples != _samples;
                _samples = samples;
                if (!hasTexture)
                    return Change.None;
                return samplesChanged ? Change.Rebind : Change.RebindIfUnbound;
            }
            _created = true;
            _size = size;
            _format = format;
            _samples = samples;
            _device = device;
            return Change.Recreate;
        }

        /// <summary>Stride resets or disposes its device: the next <see cref="Update"/> asks to recreate.</summary>
        public void DeviceGone()
        {
            _created = false;
            _device = null;
        }

        /// <summary>
        /// Returns the texture to show or null. Enabled the scene has to be bound; disabled it keeps the last content, also after
        /// QtNative unbound the scene. A texture with an error is never shown.
        /// </summary>
        public static T? Visible<T>(T? texture, string textureError, bool enabled, bool isBound, bool hasContent) where T : class
            => texture is not null && textureError.Length == 0 && (enabled ? isBound : hasContent) ? texture : null;

        /// <summary>Returns the first non-empty text of texture, scene and batch error, or an empty string.</summary>
        public static string FirstError(string textureError, string sceneError, string batchError)
            => textureError.Length > 0 ? textureError : sceneError.Length > 0 ? sceneError : batchError;
    }
}
