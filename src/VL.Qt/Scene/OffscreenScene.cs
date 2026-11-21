using System;
using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// A native offscreen scene, the window-free sibling of <see cref="WindowScene"/> (QtTexture): a <see cref="Scene"/>
    /// rendered into a texture of a foreign D3D11 device (Stride's) shared by NT handle. Stride-free:
    /// <c>VL.Qt.Stride</c> passes native pointers. Not rendered by the window pump (<see cref="QtPump"/>): the caller runs
    /// <see cref="Update"/> and <see cref="ApplyCommands"/> per frame, then <see cref="RenderBatch"/> once for all scenes
    /// of the host frame, then <see cref="PollEvents"/>. Main thread only. Native side: vlqt_offscreen_* in vlqt.h.
    /// </summary>
    internal sealed class OffscreenScene : IDisposable
    {
        private readonly Scene _scene;
        private string _createError = "", _bindError = "";
        private int _usedSamples;

        /// <param name="registry">Duplicate registry of the app (<see cref="SceneRegistry.ForApp"/>).</param>
        /// <param name="width">Initial scene width in DIP (1 DIP = 1 texture pixel); <see cref="Bind"/> sets the final size.</param>
        /// <param name="height">Initial scene height in DIP.</param>
        public OffscreenScene(SceneRegistry registry, int width, int height)
        {
            var handle = Native.vlqt_offscreen_create(width, height);
            if (handle.IsNull)
            {
                var text = Native.LastError();
                _createError = text.Length > 0 ? text : "Qt could not create the offscreen scene";
            }
            _scene = new Scene(registry, handle);
            Input = new OffscreenInput(this);
        }

        /// <summary>The native scene handle, a null handle if creation failed or after <see cref="Dispose"/>.</summary>
        public SceneHandle Handle => _scene.Handle;

        /// <summary>Translates vvvv notifications into the Send* calls of this scene.</summary>
        public OffscreenInput Input { get; }

        /// <summary>Returns true if the native scene exists and is not disposed.</summary>
        public bool IsOpen => _scene.IsOpen;

        /// <summary>
        /// Returns true if the scene is bound to a texture. False before <see cref="Bind"/>, after a failed bind and after a
        /// device loss of QtNative (it unbinds the scene): bind again.
        /// </summary>
        public bool IsBound => _scene.IsOpen && Native.vlqt_offscreen_is_bound(Handle) == 1;

        /// <summary>MSAA count of the last successful <see cref="Bind"/> (what QtNative used, clamped to the device); 0 while unbound.</summary>
        public int UsedSamples => _usedSamples;

        /// <summary>First of: scene creation, last bind, last apply, last poll error. Empty if ok.</summary>
        public string Error =>
            _createError.Length > 0 ? _createError
            : _bindError.Length > 0 ? _bindError
            : _scene.ApplyError.Length > 0 ? _scene.ApplyError
            : _scene.PollError;

        internal Reconciler Reconciler => _scene.Reconciler;

        /// <summary>
        /// Binds the scene to a shared texture. <paramref name="device"/> is the foreign ID3D11Device*,
        /// <paramref name="ntHandle"/> the texture's NT handle (the caller may close it afterwards), <paramref name="size"/>
        /// the texture size in pixels, <paramref name="format"/> the DXGI_FORMAT the caller samples the texture with (with an
        /// SRGB format or R16G16B16A16_FLOAT QtNative writes linear premultiplied values). <paramref name="samples"/> is the
        /// MSAA count (<see cref="MultisampleCount.None"/> = 1), clamped to the device; the count used is in <see cref="UsedSamples"/>.
        /// A different count is a new Bind (after <see cref="Unbind"/>).
        /// Returns <see cref="Native.Result.Ok"/> when bound, else the code of the failure (<see cref="Describe"/>); its text is in <see cref="Error"/>.
        /// </summary>
        public Native.Result Bind(IntPtr device, IntPtr ntHandle, Int2 size, int format, MultisampleCount samples = MultisampleCount.None)
        {
            if (!_scene.IsOpen)
                return Native.Result.NullHandle;
            var rc = Native.vlqt_offscreen_bind(Handle, device, ntHandle, size.X, size.Y, format, (int)samples, out var used);
            _usedSamples = rc == Native.Result.Ok ? used : 0;
            _bindError = rc == Native.Result.Ok ? "" : Describe(rc);
            return rc;
        }

        /// <summary>Frees the binding; the scene and its elements stay.</summary>
        public void Unbind()
        {
            if (!_scene.IsOpen)
                return;
            Native.vlqt_offscreen_unbind(Handle);
            _bindError = "";
            _usedSamples = 0;
        }

        /// <summary>
        /// Writes the commands of this frame (sent by <see cref="ApplyCommands"/>). <paramref name="enabled"/> = false freezes
        /// the scene: no reconcile, clear color or Clear flag, the texture keeps the last state. <paramref name="clearColor"/> with
        /// alpha 0 gives a transparent texture. Offscreen scenes have a device pixel ratio of 1.
        /// </summary>
        public void Update(Element? input, Space space, Color4 clearColor, bool clear, bool enabled)
        {
            if (!_scene.IsOpen || !enabled)
                return;
            _scene.Update(input, space, clearColor, clear);
        }

        /// <summary>Sends the commands written by <see cref="Update"/> to QtNative; call before <see cref="RenderBatch"/>.</summary>
        public void ApplyCommands()
        {
            if (!_scene.IsOpen)
                return;
            _scene.Apply();
        }

        /// <summary>Events and measured values of the scene into the feedback (read by the element nodes in the next Update).</summary>
        public void PollEvents()
        {
            if (!_scene.IsOpen)
                return;
            var events = _scene.ReadEvents(frameStart: false);
            if (!events.IsEmpty)
                _scene.Dispatch(events, frameStart: false);
        }

        // Input (render-texture.md "Input"): positions in texture pixels (top-left origin, 1 DIP = 1 pixel), call
        // between two RenderBatch calls; the scene shows the result in the next batch, its events arrive in PollEvents.
        // Each returns a Native.Result code.

        /// <summary>Pointer event (<see cref="Protocol.Pointer"/>) of <paramref name="device"/> (<see
        /// cref="Protocol.PointerDevice"/>); mouse: <paramref name="buttons"/> = Qt::MouseButtons held after the event;
        /// touch/pen: <paramref name="id"/> of the point.</summary>
        public Native.Result SendPointer(int device, int type, int id, Vector2 position, int buttons, int modifiers)
            => !_scene.IsOpen ? Native.Result.NullHandle
            : Native.vlqt_offscreen_send_pointer(Handle, device, type, id, position.X, position.Y, buttons, modifiers);

        /// <summary>Wheel at <paramref name="position"/>; angle delta in eighths of a degree (120 = one notch).</summary>
        public Native.Result SendWheel(Vector2 position, int dx, int dy, int modifiers)
            => !_scene.IsOpen ? Native.Result.NullHandle
            : Native.vlqt_offscreen_send_wheel(Handle, position.X, position.Y, dx, dy, modifiers);

        /// <summary>Key press or release (<see cref="Protocol.KeyEvent"/>) of a Qt::Key, without text.</summary>
        public Native.Result SendKey(int type, int key, int modifiers, bool autoRepeat)
            => !_scene.IsOpen ? Native.Result.NullHandle
            : Native.vlqt_offscreen_send_key(Handle, type, key, modifiers, autoRepeat ? 1 : 0);

        /// <summary>Typed text to the focus item (e.g. one character of a KeyPress).</summary>
        public Native.Result SendText(ReadOnlySpan<char> text)
        {
            if (!_scene.IsOpen)
                return Native.Result.NullHandle;
            if (text.Length > 64)
                text = text[..64];
            Span<byte> utf8 = stackalloc byte[64 * 3];
            var length = Encoding.UTF8.GetBytes(text, utf8);
            return Native.OffscreenSendText(Handle, utf8[..length]);
        }

        /// <summary>Gives the scene keyboard focus (FocusIn) or takes it (FocusOut); QtNative also does it on a press inside or outside the texture.</summary>
        public Native.Result SetFocus(bool focused)
            => !_scene.IsOpen ? Native.Result.NullHandle
            : Native.vlqt_offscreen_set_focus(Handle, focused ? 1 : 0);

        /// <summary>
        /// Renders the changed, bound scenes of <paramref name="scenes"/> (<see cref="Handle"/>s; a disposed scene is a null
        /// entry, skipped by QtNative) in one native call, ordered against <paramref name="context"/> (the foreign immediate
        /// ID3D11DeviceContext*) by fences. Returns the number of scenes rendered or a negative <see cref="Native.Result"/>
        /// (<see cref="Describe"/> gives the text).
        /// </summary>
        public static int RenderBatch(IntPtr context, ReadOnlySpan<SceneHandle> scenes, double driverSeconds)
            => Native.OffscreenRenderBatch(context, scenes, driverSeconds);

        /// <summary>Returns the text of a <see cref="Native.Result"/>, empty for <see cref="Native.Result.Ok"/>.</summary>
        public static string Describe(Native.Result code) => code switch
        {
            Native.Result.Ok => "",
            Native.Result.NullHandle => "No offscreen scene (null handle or not an offscreen scene)",
            Native.Result.BadArgument => "Bad argument (null device, texture handle or context, or a size out of range or not the texture's)",
            Native.Result.Unbound => "The offscreen scene has no texture",
            Native.Result.AdapterMismatch => "The graphics device is on another adapter than QtNative's",
            Native.Result.OpenFailed => "QtNative could not open the shared texture",
            Native.Result.FormatUnsupported => "Render target format not supported (use R8G8B8A8, B8G8R8A8 or R16G16B16A16_FLOAT)",
            Native.Result.FenceFailed => "Fences for the shared texture could not be created (ID3D11Device5 and ID3D11DeviceContext4 are required)",
            Native.Result.DeviceLost => "The graphics device of QtNative is lost or being rebuilt",
            Native.Result.Unsupported => "Offscreen scenes need Direct3D 11 (Windows)",
            _ => Enum.IsDefined(code) ? $"Offscreen scene error {code} ({(int)code})" : $"Offscreen scene error {(int)code}",
        };

        /// <summary>Clear (releases the elements in the app's registry), apply, free the native scene (unbinding first).</summary>
        public void Dispose() => _scene.Dispose();
    }
}
