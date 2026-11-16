using System;
using System.Text;
using Stride.Core.Mathematics;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using VL.Qt.Elements;

namespace VL.Qt
{
    /// <summary>
    /// The window-free sibling of <see cref="WindowRenderer"/> (#116, QtTexture): one offscreen Qt scene, its reconciler
    /// and command buffer, rendered into a texture of a foreign D3D11 device (Stride's) shared by NT handle. Stride-free:
    /// <c>VL.Qt.Stride</c> passes native pointers. Not rendered by the window pump (<see cref="QtPump"/>): the caller runs
    /// <see cref="Update"/> and <see cref="ApplyCommands"/> per frame, then <see cref="RenderBatch"/> once for all renderers
    /// of the host frame, then <see cref="PollEvents"/>. Main thread only. Native side: vlqt_offscreen_* in vlqt.h.
    /// </summary>
    internal sealed class OffscreenRenderer : IDisposable
    {
        // Scene handles of one RenderBatch: reused, so a frame allocates nothing (main thread only).
        static IntPtr[] s_scenes = new IntPtr[8];

        readonly SceneCore _core;
        IntPtr _scene;
        bool _disposed;
        // Input state (Notify): vvvv notifications carry the changed mouse button, Qt wants the buttons held after the event;
        // _keysDown detects auto-repeat (a KeyDown of a key that is already down); _highSurrogate joins the two KeyPress
        // notifications of a character outside the BMP.
        int _mouseButtons;
        readonly bool[] _keysDown = new bool[256];
        char _highSurrogate;
        string _createError = "", _bindError = "";
        int _usedSamples;

        /// <param name="scene">Duplicate registry of the app (<see cref="SceneRegistry.ForApp"/>).</param>
        /// <param name="width">Initial scene width in DIP (1 DIP = 1 texture pixel); <see cref="Bind"/> sets the final size.</param>
        /// <param name="height">Initial scene height in DIP.</param>
        public OffscreenRenderer(SceneRegistry scene, int width, int height)
        {
            _core = new SceneCore(scene);
            _scene = Native.vlqt_offscreen_create(width, height);
            _core.Handle = _scene;
            if (_scene == IntPtr.Zero)
            {
                var text = Native.LastError();
                _createError = text.Length > 0 ? text : "Qt could not create the offscreen scene";
            }
        }

        /// <summary>The native scene handle (also a window handle for the vlqt_window_* functions), IntPtr.Zero if creation failed or after <see cref="Dispose"/>.</summary>
        public IntPtr Scene => _scene;

        /// <summary>
        /// Returns true if the scene is bound to a texture. False before <see cref="Bind"/>, after a failed bind and after a
        /// device loss of QtNative (it unbinds the scene): bind again.
        /// </summary>
        public bool IsBound => !_disposed && _scene != IntPtr.Zero && Native.vlqt_offscreen_is_bound(_scene) == 1;

        /// <summary>MSAA count of the last successful <see cref="Bind"/> (what QtNative used, clamped to the device); 0 while unbound.</summary>
        public int UsedSamples => _usedSamples;

        /// <summary>First of: scene creation, last bind, last apply, last poll error. Empty if ok.</summary>
        public string Error =>
            _createError.Length > 0 ? _createError
            : _bindError.Length > 0 ? _bindError
            : _core.ApplyError.Length > 0 ? _core.ApplyError
            : _core.PollError;

        internal Reconciler Reconciler => _core.Reconciler;

        /// <summary>
        /// Binds the scene to a shared texture. <paramref name="device"/> is the foreign ID3D11Device*,
        /// <paramref name="ntHandle"/> the texture's NT handle (the caller may close it afterwards), <paramref name="size"/>
        /// the texture size in pixels, <paramref name="format"/> the DXGI_FORMAT the caller samples the texture with (with an
        /// SRGB format or R16G16B16A16_FLOAT QtNative writes linear premultiplied values). <paramref name="samples"/> is the
        /// MSAA count (<see cref="MultisampleCount.None"/> = 1), clamped to the device; the count used is in <see cref="UsedSamples"/>.
        /// A different count is a new Bind (after <see cref="Unbind"/>).
        /// Returns a <see cref="Protocol.Offscreen"/> code, 0 = bound; the text of an error is in <see cref="Error"/>.
        /// </summary>
        public int Bind(IntPtr device, IntPtr ntHandle, Int2 size, int format, MultisampleCount samples = MultisampleCount.None)
        {
            if (_disposed || _scene == IntPtr.Zero)
                return Protocol.Offscreen.NullScene;
            int rc = Native.vlqt_offscreen_bind(_scene, device, ntHandle, size.X, size.Y, format, (int)samples, out var used);
            _usedSamples = rc == Protocol.Offscreen.Ok ? used : 0;
            _bindError = rc == Protocol.Offscreen.Ok ? "" : Describe(rc);
            return rc;
        }

        /// <summary>Frees the binding; the scene and its elements stay.</summary>
        public void Unbind()
        {
            if (_disposed || _scene == IntPtr.Zero)
                return;
            Native.vlqt_offscreen_unbind(_scene);
            _bindError = "";
            _usedSamples = 0;
        }

        /// <summary>
        /// Writes the commands of this frame (sent by <see cref="ApplyCommands"/>). <paramref name="enabled"/> = false freezes
        /// the scene: no reconcile, clear color or Clear flag, the texture keeps the last state. <paramref name="clearColor"/> with
        /// alpha 0 gives a transparent texture. <paramref name="space"/> must be resolved (<see cref="Geometry.Resolve"/>).
        /// </summary>
        public void Update(Element? input, Space space, Color4 clearColor, bool clear, bool enabled)
        {
            if (_disposed || _scene == IntPtr.Zero || !enabled)
                return;
            _core.Update(input, space, clearColor, clear);
        }

        /// <summary>Sends the commands written by <see cref="Update"/> to QtNative; call before <see cref="RenderBatch"/>.</summary>
        public void ApplyCommands()
        {
            if (_disposed || _scene == IntPtr.Zero)
                return;
            _core.Apply();
        }

        /// <summary>Events and measured values of the scene into the sinks (read by the element nodes in the next Update).</summary>
        public void PollEvents()
        {
            if (_disposed || _scene == IntPtr.Zero)
                return;
            var events = _core.ReadEvents(frameStart: false);
            if (!events.IsEmpty)
                _core.Dispatch(events, frameStart: false);
        }

        // Input (#119, render-texture.md "Input"): positions in texture pixels (top-left origin, 1 DIP = 1 pixel), call
        // between two RenderBatch calls; the scene shows the result in the next batch, its events arrive in PollEvents.
        // Each returns a Protocol.Offscreen code.

        /// <summary>
        /// Translates a vvvv notification (mouse, touch, keyboard, focus; positions already in texture pixels) into the
        /// Send* calls below. Main thread. Allocates nothing. Not translated: <see cref="MouseClickNotification"/> (Qt derives
        /// clicks and double clicks itself), <see cref="GotFocusNotification"/> (a press gives the scene focus) and gestures.
        /// Errors of the Send* calls are dropped: a rejected event (e.g. a touch id in the wrong state) must not stop the stream.
        /// </summary>
        public void Notify(INotification notification)
        {
            if (_disposed || _scene == IntPtr.Zero)
                return;
            switch (notification)
            {
                case MouseDownNotification down:
                    _mouseButtons |= InputMapping.ToQtButtons(down.Buttons);
                    SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Press, 0, down.Position, _mouseButtons, InputMapping.ToQtModifiers(down.ModifierKeys));
                    break;
                case MouseUpNotification up:
                    _mouseButtons &= ~InputMapping.ToQtButtons(up.Buttons);
                    SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Release, 0, up.Position, _mouseButtons, InputMapping.ToQtModifiers(up.ModifierKeys));
                    break;
                case MouseMoveNotification move:
                    SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Move, 0, move.Position, _mouseButtons, InputMapping.ToQtModifiers(move.ModifierKeys));
                    break;
                case MouseWheelNotification wheel:
                    SendWheel(wheel.Position, 0, wheel.WheelDelta, InputMapping.ToQtModifiers(wheel.ModifierKeys));
                    break;
                case MouseHorizontalWheelNotification hwheel:
                    SendWheel(hwheel.Position, hwheel.WheelDelta, 0, InputMapping.ToQtModifiers(hwheel.ModifierKeys));
                    break;
                case MouseLostNotification lost:
                    // The mouse left the window: hover ends. Held buttons stay: vvvv still delivers their release.
                    SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Leave, 0, lost.Position, _mouseButtons, InputMapping.ToQtModifiers(lost.ModifierKeys));
                    break;
                case TouchNotification touch:
                    SendPointer(Protocol.PointerDevice.Touch, touch.Kind switch
                    {
                        TouchNotificationKind.TouchDown => Protocol.Pointer.Press,
                        TouchNotificationKind.TouchUp => Protocol.Pointer.Release,
                        _ => Protocol.Pointer.Move,
                    }, touch.Id, touch.Position, 0, InputMapping.ToQtModifiers(touch.ModifierKeys));
                    break;
                case KeyDownNotification keyDown:
                    SendKeyCode(Protocol.KeyEvent.Press, keyDown);
                    break;
                case KeyUpNotification keyUp:
                    SendKeyCode(Protocol.KeyEvent.Release, keyUp);
                    break;
                case KeyPressNotification press:
                    SendKeyChar(press.KeyChar);
                    break;
                case KeyboardLostNotification:
                    Array.Clear(_keysDown);
                    _highSurrogate = ' ';
                    break;
                case LostFocusNotification:
                    // The host window lost the focus: unfocus the scene, forget pressed keys.
                    Array.Clear(_keysDown);
                    _highSurrogate = ' ';
                    SetFocus(false);
                    break;
            }
        }

        void SendKeyCode(int type, KeyCodeNotification notification)
        {
            var code = notification.KeyCode & Keys.KeyCode;
            var key = InputMapping.ToQtKey(code);
            if (key == InputMapping.QtKeyUnknown)
                return;
            var modifiers = InputMapping.ToQtModifiers(notification.ModifierKeys);
            if (InputMapping.IsKeypad(code))
                modifiers |= InputMapping.QtKeypad;
            var index = (int)code;
            var autoRepeat = false;
            if (index < _keysDown.Length)
            {
                autoRepeat = type == Protocol.KeyEvent.Press && _keysDown[index];
                _keysDown[index] = type == Protocol.KeyEvent.Press;
            }
            SendKey(type, key, modifiers, autoRepeat);
        }

        // KeyPress carries the typed character (layout, shift and dead keys applied). Control characters (Enter, Tab, Backspace,
        // Ctrl+letter) come as key events already and are not text. A character outside the BMP arrives as two notifications.
        void SendKeyChar(char c)
        {
            if (char.IsHighSurrogate(c))
            {
                _highSurrogate = c;
                return;
            }
            if (char.IsLowSurrogate(c))
            {
                if (_highSurrogate != ' ')
                {
                    Span<char> pair = stackalloc char[2] { _highSurrogate, c };
                    SendText(pair);
                }
                _highSurrogate = ' ';
                return;
            }
            _highSurrogate = ' ';
            if (char.IsControl(c))
                return;
            SendText(new ReadOnlySpan<char>(in c));
        }

        /// <summary>Pointer event (<see cref="Protocol.Pointer"/>) of <paramref name="device"/> (<see cref="Protocol.PointerDevice"/>); mouse: <paramref name="buttons"/> = Qt::MouseButtons held after the event; touch/pen: <paramref name="id"/> of the point.</summary>
        public int SendPointer(int device, int type, int id, Vector2 position, int buttons, int modifiers)
            => _disposed || _scene == IntPtr.Zero ? Protocol.Offscreen.NullScene
            : Native.vlqt_offscreen_send_pointer(_scene, device, type, id, position.X, position.Y, buttons, modifiers);

        /// <summary>Wheel at <paramref name="position"/>; angle delta in eighths of a degree (120 = one notch).</summary>
        public int SendWheel(Vector2 position, int dx, int dy, int modifiers)
            => _disposed || _scene == IntPtr.Zero ? Protocol.Offscreen.NullScene
            : Native.vlqt_offscreen_send_wheel(_scene, position.X, position.Y, dx, dy, modifiers);

        /// <summary>Key press or release (<see cref="Protocol.KeyEvent"/>) of a Qt::Key, without text.</summary>
        public int SendKey(int type, int key, int modifiers, bool autoRepeat)
            => _disposed || _scene == IntPtr.Zero ? Protocol.Offscreen.NullScene
            : Native.vlqt_offscreen_send_key(_scene, type, key, modifiers, autoRepeat ? 1 : 0);

        /// <summary>Typed text to the focus item (e.g. one character of a KeyPress).</summary>
        public int SendText(ReadOnlySpan<char> text)
        {
            if (_disposed || _scene == IntPtr.Zero)
                return Protocol.Offscreen.NullScene;
            if (text.Length > 64)
                text = text[..64];
            Span<byte> utf8 = stackalloc byte[64 * 3];
            var length = Encoding.UTF8.GetBytes(text, utf8);
            return Native.OffscreenSendText(_scene, utf8[..length]);
        }

        /// <summary>Gives the scene keyboard focus (FocusIn) or takes it (FocusOut); QtNative also does it on a press inside or outside the texture.</summary>
        public int SetFocus(bool focused)
            => _disposed || _scene == IntPtr.Zero ? Protocol.Offscreen.NullScene
            : Native.vlqt_offscreen_set_focus(_scene, focused ? 1 : 0);

        /// <summary>
        /// Renders the changed, bound scenes of <paramref name="renderers"/> in one native call, ordered against
        /// <paramref name="context"/> (the foreign immediate ID3D11DeviceContext*) by fences. Returns the number of scenes
        /// rendered or a negative <see cref="Protocol.Offscreen"/> code (<see cref="Describe"/> gives the text).
        /// </summary>
        public static int RenderBatch(IntPtr context, ReadOnlySpan<OffscreenRenderer> renderers, double driverSeconds)
        {
            if (s_scenes.Length < renderers.Length)
                s_scenes = new IntPtr[Math.Max(renderers.Length, s_scenes.Length * 2)];
            // A disposed renderer has no scene: a null entry, skipped by QtNative.
            for (int i = 0; i < renderers.Length; i++)
                s_scenes[i] = renderers[i]._scene;
            return Native.OffscreenRenderBatch(context, new ReadOnlySpan<IntPtr>(s_scenes, 0, renderers.Length), driverSeconds);
        }

        /// <summary>Returns the text of a <see cref="Protocol.Offscreen"/> code, empty for 0 and for a count of rendered scenes (> 0).</summary>
        public static string Describe(int code) => code switch
        {
            >= 0 => "",
            Protocol.Offscreen.NullScene => "No offscreen scene (null handle or not an offscreen scene)",
            Protocol.Offscreen.BadArgument => "Bad argument (null device, texture handle or context, or a size out of range or not the texture's)",
            Protocol.Offscreen.Unbound => "The offscreen scene has no texture",
            Protocol.Offscreen.AdapterMismatch => "The graphics device is on another adapter than QtNative's",
            Protocol.Offscreen.OpenFailed => "QtNative could not open the shared texture",
            Protocol.Offscreen.FormatUnsupported => "Render target format not supported (use R8G8B8A8, B8G8R8A8 or R16G16B16A16_FLOAT)",
            Protocol.Offscreen.FenceFailed => "Fences for the shared texture could not be created (ID3D11Device5 and ID3D11DeviceContext4 are required)",
            Protocol.Offscreen.DeviceLost => "The graphics device of QtNative is lost or being rebuilt",
            Protocol.Offscreen.NotSupported => "Offscreen scenes need Direct3D 11 (Windows)",
            _ => $"Offscreen scene error {code}",
        };

        /// <summary>Clear (releases the elements in the app's registry), apply, free the native scene (unbinding first).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            var scene = _core.Close();
            if (scene != IntPtr.Zero)
                Native.vlqt_window_destroy(scene);
            _scene = IntPtr.Zero;
            _disposed = true;
        }
    }
}
