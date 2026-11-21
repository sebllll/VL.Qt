using System;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// Translates vvvv notifications into the Send* calls of one <see cref="OffscreenScene"/> and keeps the input state
    /// for it: vvvv notifications carry the changed mouse button, Qt wants the buttons held after the event; the keys held
    /// down detect auto-repeat (a KeyDown of a key that is already down); a pending high surrogate joins the two KeyPress
    /// notifications of a character outside the BMP. Main thread only.
    /// </summary>
    internal sealed class OffscreenInput
    {
        private readonly OffscreenScene _scene;
        private int _mouseButtons;
        private readonly bool[] _keysDown = new bool[256];
        // '\0' = no pending high surrogate.
        private char _highSurrogate;

        public OffscreenInput(OffscreenScene scene)
        {
            _scene = scene;
        }

        /// <summary>
        /// Translates a vvvv notification (mouse, touch, keyboard, focus; positions already in texture pixels) into the
        /// Send* calls of the scene. Main thread. Allocates nothing. Not translated: <see cref="MouseClickNotification"/> (Qt derives
        /// clicks and double clicks itself), <see cref="GotFocusNotification"/> (a press gives the scene focus) and gestures.
        /// Errors of the Send* calls are dropped: a rejected event (e.g. a touch id in the wrong state) must not stop the stream.
        /// </summary>
        public void Notify(INotification notification)
        {
            if (!_scene.IsOpen)
                return;
            switch (notification)
            {
                case MouseDownNotification down:
                    _mouseButtons |= InputMapping.ToQtButtons(down.Buttons);
                    _scene.SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Press, 0, down.Position, _mouseButtons, InputMapping.ToQtModifiers(down.ModifierKeys));
                    break;
                case MouseUpNotification up:
                    _mouseButtons &= ~InputMapping.ToQtButtons(up.Buttons);
                    _scene.SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Release, 0, up.Position, _mouseButtons, InputMapping.ToQtModifiers(up.ModifierKeys));
                    break;
                case MouseMoveNotification move:
                    _scene.SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Move, 0, move.Position, _mouseButtons, InputMapping.ToQtModifiers(move.ModifierKeys));
                    break;
                case MouseWheelNotification wheel:
                    _scene.SendWheel(wheel.Position, 0, wheel.WheelDelta, InputMapping.ToQtModifiers(wheel.ModifierKeys));
                    break;
                case MouseHorizontalWheelNotification hwheel:
                    _scene.SendWheel(hwheel.Position, hwheel.WheelDelta, 0, InputMapping.ToQtModifiers(hwheel.ModifierKeys));
                    break;
                case MouseLostNotification lost:
                    // The mouse left the window: hover ends. Held buttons stay: vvvv still delivers their release.
                    _scene.SendPointer(Protocol.PointerDevice.Mouse, Protocol.Pointer.Leave, 0, lost.Position, _mouseButtons, InputMapping.ToQtModifiers(lost.ModifierKeys));
                    break;
                case TouchNotification touch:
                    _scene.SendPointer(Protocol.PointerDevice.Touch, touch.Kind switch
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
                    _highSurrogate = '\0';
                    break;
                case LostFocusNotification:
                    // The host window lost the focus: unfocus the scene, forget pressed keys.
                    Array.Clear(_keysDown);
                    _highSurrogate = '\0';
                    _scene.SetFocus(false);
                    break;
            }
        }

        private void SendKeyCode(int type, KeyCodeNotification notification)
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
            _scene.SendKey(type, key, modifiers, autoRepeat);
        }

        // KeyPress carries the typed character (layout, shift and dead keys applied). Control characters (Enter, Tab, Backspace,
        // Ctrl+letter) come as key events already and are not text. A character outside the BMP arrives as two notifications.
        private void SendKeyChar(char c)
        {
            if (char.IsHighSurrogate(c))
            {
                _highSurrogate = c;
                return;
            }
            if (char.IsLowSurrogate(c))
            {
                if (_highSurrogate != '\0')
                {
                    Span<char> pair = stackalloc char[2] { _highSurrogate, c };
                    _scene.SendText(pair);
                }
                _highSurrogate = '\0';
                return;
            }
            _highSurrogate = '\0';
            if (char.IsControl(c))
                return;
            _scene.SendText(new ReadOnlySpan<char>(in c));
        }
    }
}
