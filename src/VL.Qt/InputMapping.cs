using System;
using VL.Lib.IO;

namespace VL.Qt
{
    /// <summary>
    /// vvvv input values (<see cref="Keys"/>, <see cref="MouseButtons"/>, WinForms numbering) to Qt values
    /// (<c>Qt::Key</c>, <c>Qt::MouseButtons</c>, <c>Qt::KeyboardModifiers</c>) for the input of offscreen scenes (#119).
    /// Pure functions, no allocation.
    /// </summary>
    internal static class InputMapping
    {
        // Qt::MouseButton
        public const int QtLeftButton = 0x1, QtRightButton = 0x2, QtMiddleButton = 0x4, QtBackButton = 0x8, QtForwardButton = 0x10;
        // Qt::KeyboardModifier
        public const int QtShift = 0x02000000, QtControl = 0x04000000, QtAlt = 0x08000000, QtMeta = 0x10000000, QtKeypad = 0x20000000;
        /// <summary>Qt::Key_unknown: a key without a Qt equivalent.</summary>
        public const int QtKeyUnknown = 0x01ffffff;

        /// <summary>Returns the Qt::MouseButtons of the vvvv buttons (left, right, middle, XButton1 = back, XButton2 = forward).</summary>
        public static int ToQtButtons(MouseButtons buttons)
        {
            var result = 0;
            if ((buttons & MouseButtons.Left) != 0) result |= QtLeftButton;
            if ((buttons & MouseButtons.Right) != 0) result |= QtRightButton;
            if ((buttons & MouseButtons.Middle) != 0) result |= QtMiddleButton;
            if ((buttons & MouseButtons.XButton1) != 0) result |= QtBackButton;
            if ((buttons & MouseButtons.XButton2) != 0) result |= QtForwardButton;
            return result;
        }

        /// <summary>Returns the Qt::KeyboardModifiers of the modifier flags (Shift, Control, Alt) of a vvvv notification.</summary>
        public static int ToQtModifiers(Keys modifierKeys)
        {
            var result = 0;
            if ((modifierKeys & Keys.Shift) != 0) result |= QtShift;
            if ((modifierKeys & Keys.Control) != 0) result |= QtControl;
            if ((modifierKeys & Keys.Alt) != 0) result |= QtAlt;
            return result;
        }

        /// <summary>Returns true if <paramref name="keyCode"/> is a key of the numeric keypad (Qt adds the KeypadModifier for it).</summary>
        public static bool IsKeypad(Keys keyCode)
            => keyCode is >= Keys.NumPad0 and <= Keys.Divide and not Keys.Separator;

        /// <summary>
        /// Returns the Qt::Key of a vvvv key code (<see cref="Keys.KeyCode"/> part only), <see cref="QtKeyUnknown"/> if there is
        /// none. The keys of the keypad give the digit and operator keys (see <see cref="IsKeypad"/>); the OEM keys follow the
        /// US layout, typed characters arrive as text anyway.
        /// </summary>
        public static int ToQtKey(Keys keyCode)
        {
            keyCode &= Keys.KeyCode;
            switch (keyCode)
            {
                case >= Keys.A and <= Keys.Z: return (int)keyCode;           // Qt::Key_A = 'A'
                case >= Keys.D0 and <= Keys.D9: return (int)keyCode;         // Qt::Key_0 = '0'
                case >= Keys.NumPad0 and <= Keys.NumPad9: return '0' + (keyCode - Keys.NumPad0);
                case >= Keys.F1 and <= Keys.F24: return 0x01000030 + (keyCode - Keys.F1);
            }
            return keyCode switch
            {
                Keys.Escape => 0x01000000,
                Keys.Tab => 0x01000001,
                Keys.Back => 0x01000003,
                Keys.Return => 0x01000004,        // Keys.Enter is the same value; the keypad Enter arrives as Return too
                Keys.Insert => 0x01000006,
                Keys.Delete => 0x01000007,
                Keys.Pause => 0x01000008,
                Keys.Print => 0x01000009,
                Keys.PrintScreen => 0x0100000a,   // Qt::Key_SysReq
                Keys.Clear => 0x0100000b,
                Keys.Home => 0x01000010,
                Keys.End => 0x01000011,
                Keys.Left => 0x01000012,
                Keys.Up => 0x01000013,
                Keys.Right => 0x01000014,
                Keys.Down => 0x01000015,
                Keys.PageUp => 0x01000016,
                Keys.PageDown => 0x01000017,
                Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => 0x01000020,
                Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => 0x01000021,
                Keys.LWin => 0x01000053,          // Qt::Key_Super_L
                Keys.RWin => 0x01000054,          // Qt::Key_Super_R
                Keys.Menu or Keys.LMenu or Keys.RMenu => 0x01000023,   // Alt
                Keys.CapsLock => 0x01000024,
                Keys.NumLock => 0x01000025,
                Keys.Scroll => 0x01000026,
                Keys.Apps => 0x01000055,          // Qt::Key_Menu
                Keys.Help => 0x01000058,
                Keys.Space => 0x20,
                Keys.Multiply => 0x2a,            // '*'
                Keys.Add => 0x2b,                 // '+'
                Keys.Subtract => 0x2d,            // '-'
                Keys.Decimal => 0x2e,             // '.'
                Keys.Divide => 0x2f,              // '/'
                Keys.OemSemicolon => 0x3b,        // ';'
                Keys.Oemplus => 0x2b,
                Keys.Oemcomma => 0x2c,
                Keys.OemMinus => 0x2d,
                Keys.OemPeriod => 0x2e,
                Keys.OemQuestion => 0x2f,
                Keys.Oemtilde => 0x60,            // '`'
                Keys.OemOpenBrackets => 0x5b,
                Keys.OemPipe => 0x5c,             // '\'
                Keys.OemCloseBrackets => 0x5d,
                Keys.OemQuotes => 0x27,           // '\''
                Keys.OemBackslash => 0x3c,        // '<' on a 102-key layout
                Keys.BrowserBack => 0x01000061,
                Keys.BrowserForward => 0x01000062,
                Keys.BrowserStop => 0x01000063,
                Keys.BrowserRefresh => 0x01000064,
                Keys.VolumeDown => 0x01000070,
                Keys.VolumeMute => 0x01000071,
                Keys.VolumeUp => 0x01000072,
                Keys.MediaPlayPause => 0x01000080,
                Keys.MediaStop => 0x01000081,
                Keys.MediaPreviousTrack => 0x01000082,
                Keys.MediaNextTrack => 0x01000083,
                _ => QtKeyUnknown,
            };
        }
    }
}
