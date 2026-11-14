using System;
using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// The window-free sibling of <see cref="WindowRenderer"/> (#116, QtTexture): one offscreen Qt scene, its reconciler
    /// and command buffer, rendered into a texture of a foreign D3D11 device (Stride's) shared by NT handle. Stride-free:
    /// <c>VL.Qt.Stride</c> passes native pointers. Not rendered by the window pump: <see cref="RenderBatch"/> renders all
    /// scheduled renderers of one host frame. Main thread only. Native side: vlqt_offscreen_* in vlqt.h.
    /// </summary>
    internal sealed class OffscreenRenderer : IDisposable
    {
        /// <summary>Returns true if the scene is bound to a texture (false after a device loss of QtNative: bind again).</summary>
        public bool IsBound => throw new NotImplementedException(); // TODO: Task 1.6, vlqt_offscreen_is_bound.

        /// <summary>
        /// Binds the scene to a shared texture. <paramref name="device"/> is the foreign ID3D11Device*,
        /// <paramref name="ntHandle"/> the texture's NT handle (the caller may close it afterwards), <paramref name="size"/>
        /// the texture size in pixels, <paramref name="rtvFormat"/> the DXGI_FORMAT of Qt's render target view.
        /// Returns a <see cref="Protocol.Offscreen"/> code.
        /// </summary>
        public int Bind(IntPtr device, IntPtr ntHandle, Int2 size, int rtvFormat)
        {
            // TODO: Task 1.6, vlqt_offscreen_bind.
            throw new NotImplementedException();
        }

        /// <summary>Frees the binding; the scene and its elements stay.</summary>
        public void Unbind()
        {
            // TODO: Task 1.6, vlqt_offscreen_unbind.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Renders the changed, bound scenes of <paramref name="renderers"/> in one native call, ordered against
        /// <paramref name="context"/> (the foreign immediate ID3D11DeviceContext*) by fences. Returns the number of scenes
        /// rendered or a negative <see cref="Protocol.Offscreen"/> code.
        /// </summary>
        public static int RenderBatch(IntPtr context, ReadOnlySpan<OffscreenRenderer> renderers, double driverSeconds)
        {
            // TODO: Task 1.6, collect the scene handles without allocating, then Native.OffscreenRenderBatch.
            throw new NotImplementedException();
        }

        /// <summary>Frees the native scene (unbinding first).</summary>
        public void Dispose()
        {
            // TODO: Task 1.6, vlqt_window_destroy on the scene handle.
        }
    }
}
