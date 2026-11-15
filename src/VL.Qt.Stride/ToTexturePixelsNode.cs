using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.IO.Notifications;
using VL.Model;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Maps the input of a window (vvvv notifications, positions in client pixels) to the pixels of a QtTexture drawn on a
    /// quad, for its Input Notifications pin. The quad is Stride's unit quad (-0.5 ... 0.5 in its local XY plane, +Y up)
    /// placed by World and seen through View and Projection; fullscreen: World = Scaling (2, 2, 1), View and Projection identity.
    /// </summary>
    [ProcessNode(Name = "ToTexturePixels", Category = Categories.Qt)]
    [Smell(SymbolSmell.Experimental)]
    public sealed class ToTexturePixelsNode : IDisposable
    {
        /// <param name="nodeContext">Injected by vvvv.</param>
        public ToTexturePixelsNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
        }

        /// <summary>Maps the positions of the notifications at Input to texture pixels (top-left origin).</summary>
        /// <param name="input">Notifications of the window the texture is drawn in (e.g. of its Input Source).</param>
        /// <param name="world">Transformation of the quad the texture is drawn on (a zero matrix counts as identity).</param>
        /// <param name="view">View of the camera (a zero matrix counts as identity).</param>
        /// <param name="projection">Projection of the camera (a zero matrix counts as identity).</param>
        /// <param name="textureSize">Size of the texture in pixels (Size of the QtTexture).</param>
        /// <returns>The notifications with positions in texture pixels; positions off the quad lie outside 0 ... Texture Size, keyboard notifications pass unchanged.</returns>
        [return: Pin(Name = "Output")]
        public IObservable<INotification>? Update(
            IObservable<INotification>? input,
            Matrix world,
            Matrix view,
            Matrix projection,
            [DefaultValue(typeof(Int2), "1920, 1080")] Int2 textureSize)
        {
            // TODO: #119 task 5: per notification ray from Position/ClientArea through inverse(World * View * Projection),
            // intersected with the quad's plane z = 0, then u = x + 0.5, v = 0.5 - y, times Texture Size.
            return input;
        }

        public void Dispose()
        {
        }
    }
}
