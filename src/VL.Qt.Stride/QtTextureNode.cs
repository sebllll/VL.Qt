using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
using VL.Lib.IO.Notifications;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Renders the element tree at Input offscreen into a Stride texture (no window), on the GPU and in the same frame:
    /// Stride waits GPU-side until Qt has written the texture. The output is premultiplied alpha; 1 DIP = 1 pixel.
    /// </summary>
    [ProcessNode(Name = "QtTexture", Category = "Stride.Textures.Source")]
    [Smell(SymbolSmell.Experimental)]
    [SupportedOSPlatform("windows")]
    public sealed class QtTextureNode : IDisposable
    {
        private readonly SharedTextureScene _scene;

        /// <param name="nodeContext">Injected by vvvv.</param>
        public QtTextureNode(NodeContext nodeContext)
            => _scene = new SharedTextureScene(nodeContext);

        /// <summary>Renders the element tree into the texture at Output.</summary>
        /// <param name="output">The texture Qt renders into (premultiplied alpha). The same instance until Size or Format change; do not dispose it. Null while the texture cannot be created or bound (see Error).</param>
        /// <param name="error">Last error (texture, binding, apply, poll or render in QtNative), empty if ok. Also logged once per distinct error. An invalid Format or a texture that cannot be created gives the error and a null Output.</param>
        /// <param name="perf">Timings of the scene. Not measured yet for textures: always empty.</param>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="size">Size of the texture in pixels (1 DIP = 1 pixel), clamped to 1x1 ... the largest Texture2D of Stride's device (16384 on feature level 11).</param>
        /// <param name="space">Coordinate space of the elements: origin and size of one unit, see <see cref="Space"/>.</param>
        /// <param name="clearColor">Background of the texture (straight alpha, premultiplied here). Alpha 0 gives a transparent texture. While Clear is off, its alpha is the fade strength of the previous frame (0 = no fade, 1 = like Clear on).</param>
        /// <param name="clear">True clears the texture to Clear Color every frame. False keeps the previous frame and draws the new one over it (trails, smearing, feedback); the alpha of Clear Color then sets how strongly each frame fades the old content toward the color: 0 keeps everything (the default Clear Color), 1 shows no trail. Fading to transparency is not possible, and the fade speed depends on the frame rate. Switchable at runtime without recreating the texture; the first frame after switching off, and after a Size or Format change, starts from Clear Color. Costs one full-size texture while off, and Qt renders every frame then, also when the scene did not change.</param>
        /// <param name="format">Pixel format of the texture: R8G8B8A8 or B8G8R8A8 (UNorm or SRgb) or R16G16B16A16_Float; other formats give an error. SRgb and Float textures hold linear premultiplied values, UNorm textures the gamma-space values of a Qt window. Half-transparent colours still differ from a Qt window: Stride blends in linear light, a Qt window in gamma space.</param>
        /// <param name="enabled">False freezes the node: no updates of Input, Space, Clear Color, Clear, Size or Format, nothing is created or recreated and nothing renders; Output keeps the last content (null if there is none yet).</param>
        /// <param name="inputNotifications">Mouse, touch and keyboard input for the scene (vvvv notifications) with positions in texture pixels, top-left origin. Map a window's input with ToTexturePixels. A press inside the texture gives the scene keyboard focus, a press outside takes it.</param>
        /// <param name="debug">Debug overlay for all elements of the scene (same as the Debug pin of Renderer [Qt]).</param>
        /// <param name="multisampleCount">Anti-aliasing of the edges (MSAA samples per pixel), resolved into the texture; live. More samples look smoother and cost GPU time and memory (X8 is several times the render time of None in a scene with many shapes); a count the device does not support is lowered to the next supported one, with a warning at the node. A change rebinds the scene to the texture. Effect layers in the scene render without MSAA.</param>
        public void Update(
            out Texture? output,
            out string error,
            [Pin(Visibility = PinVisibility.Optional)] out PerfStats perf,
            Element? input,
            [DefaultValue(typeof(Int2), "1920, 1080")] Int2 size,
            [DefaultValue(typeof(Space), nameof(Space.DIPTopLeft))] Space space,
            [DefaultValue("0, 0, 0, 0")] Color4 clearColor,
            [DefaultValue(true)] bool clear,
            [DefaultValue(typeof(PixelFormat), nameof(PixelFormat.B8G8R8A8_UNorm_SRgb))] PixelFormat format,
            [DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] IObservable<INotification>? inputNotifications,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(false)] bool debug,
            [Pin(Name = "Multisample Count", Visibility = PinVisibility.Optional), DefaultValue(typeof(MultisampleCount), nameof(MultisampleCount.None))] MultisampleCount multisampleCount)
        {
            _scene.Input.Subscribe(inputNotifications);
            _scene.Update(input, size, format, space, clearColor, clear, enabled, debug, multisampleCount);

            output = _scene.Texture;
            error = _scene.Error;
            // TODO: Perf of offscreen scenes: QtNative has no perf ring for them (vlqt_scene_get_perf needs a window).
            perf = PerfStats.Empty;
        }

        public void Dispose() => _scene.Dispose();
    }
}
