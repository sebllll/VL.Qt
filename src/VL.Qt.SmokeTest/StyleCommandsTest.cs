using System;
using System.Buffers.Binary;
using System.Drawing;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;
using VL.Qt.Styles;
using VL.Qt.Wire;

// SET_STYLE as a command (applyStyleEntries in scene/commands.cpp): every malformed variant returns its ApplyResult and,
// because the whole command is checked before the table is written, leaves the style as it was. No API reads the style
// table, so "unchanged" is read from one pixel of the button frame (the base style paints it grey, a written FrameColor
// entry would paint it red). Every bad command starts with a valid red FrameColor entry, so a table written before the
// check would show. Payloads are patched byte by byte after the CommandWriter wrote them.
[TestFixture, NonParallelizable]
public static class StyleCommandsTest
{
    /// <summary>SET_STYLE: bad field, bad state, truncated entry, padded end.</summary>
    [Test, Category("style-commands")]
    public static void StyleCommands() => Scenario.Run(Run);

    private const uint Btn = 1;
    private const float BgGray = 0x10 / 255f;
    private const Native.Result BadLength = Native.Result.BadLength, UnknownProperty = Native.Result.UnknownProperty, BadValue = Native.Result.BadArgument;

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).
    private static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    private static bool Near(Color c, uint rgb) =>
        Math.Abs(c.R - (int)((rgb >> 16) & 0xFF)) < 8 && Math.Abs(c.G - (int)((rgb >> 8) & 0xFF)) < 8 && Math.Abs(c.B - (int)(rgb & 0xFF)) < 8;

    private static int PayloadLen(byte[] b) => (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
    private static void SetPayloadLen(byte[] b, int len) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)len);

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt style commands test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }

        // Button 200 x 60 at (100, 100); the probe (110, 110) lies in the frame, away from the text.
        cmd.Clear();
        cmd.ClearColor(BgGray, BgGray, BgGray, 1);
        cmd.Create(Btn, Protocol.ElementType.Button);
        cmd.SetBool(Btn, Protocol.PropertyId.AutoSize, false);
        cmd.SetPlace(Btn, 100, 100, 200, 60);
        cmd.SetString(Btn, Protocol.PropertyId.Text, "WWWWWW");
        cmd.Reparent(Btn, 0, 0);
        CheckApply("create button");
        // The pointer stays away from the button (no hover state).
        NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Move, 790, 590);

        void BaseStyleAgain(string what)
        {
            cmd.Clear();
            BaseStyle.Write(cmd, Btn);
            CheckApply(what);
        }
        BaseStyleAgain("base style");

        Color Frame(string name)
        {
            s.Pump(5);
            using var a = s.Grab(w, $"style-commands_{name}.png");
            return Px(a, 110, 110);
        }
        s.Check(Near(Frame("base"), 0x383838), "base style paints the frame grey");

        // SET_STYLE with the red FrameColor entry, then what the case adds; patch edits the command bytes; returns the apply result.
        Native.Result Send(Action<CommandWriter> entries, Func<byte[], byte[]>? patch = null, ushort flags = 0)
        {
            cmd.Clear();
            cmd.BeginStyle(Btn, flags);
            cmd.StyleColor(Protocol.StyleField.FrameColor, Protocol.StyleState.Normal, 1, 0, 0, 1);
            entries(cmd);
            cmd.EndStyle();
            var bytes = cmd.EndFrame().ToArray();
            if (patch != null)
                bytes = patch(bytes);
            return Native.Apply(w, bytes);
        }

        void Rejected(string name, Native.Result expected, Action<CommandWriter> entries, Func<byte[], byte[]>? patch = null, ushort flags = 0)
        {
            var got = Send(entries, patch, flags);
            var error = Native.LastError();
            s.Check(got == expected, $"{name} -> {expected} ({got}: {error})");
            var c = Frame(name.Replace(' ', '_').Replace(',', '_'));
            s.Check(Near(c, 0x383838), $"{name}: the table is unchanged, the valid entry in front was not written ({c})");
        }

        // bad field: no slot has the field 999
        Rejected("bad field", UnknownProperty, c => c.StyleF32(999, Protocol.StyleState.Normal, 1));
        // bad state: there are five states
        Rejected("bad state", UnknownProperty, c => c.StyleF32(Protocol.StyleField.FrameRadius, 99, 4));
        // value type that is not the field's type (checked in the same pass)
        Rejected("bad value type", BadValue, c => c.StyleF32(Protocol.StyleField.FrameColor, Protocol.StyleState.Hovered, 1));
        // flag bits that are not defined
        Rejected("unknown flags", UnknownProperty, c => { }, null, 0x8000);
        // truncated entry: the payload ends 8 bytes into the 16 byte color value of the last entry
        Rejected("truncated entry", BadLength,
            c => c.StyleColor(Protocol.StyleField.BorderColor, Protocol.StyleState.Normal, 0, 1, 0, 1),
            b =>
            {
                int len = PayloadLen(b) - 8;
                SetPayloadLen(b, len);
                return b.AsSpan(0, Protocol.HeaderSize + len).ToArray();
            });
        // declared count (2) greater than the entries that are there (1)
        Rejected("count too high", BadLength, c => { }, b =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(Protocol.HeaderSize + 4), 2);
            return b;
        });
        // payload with a word more than the entries need
        Rejected("payload too long", BadLength, c => c.StyleF32(Protocol.StyleField.FrameRadius, Protocol.StyleState.Normal, 4), b =>
        {
            int len = PayloadLen(b) + 4;
            var longer = new byte[Protocol.HeaderSize + len];
            b.CopyTo(longer, 0);
            SetPayloadLen(longer, len);
            return longer;
        });
        // payload a word shorter than the entries need (more than the padding of the last entry)
        Rejected("payload short by a word", BadLength, c => c.StyleF32(Protocol.StyleField.FrameRadius, Protocol.StyleState.Normal, 4), b =>
        {
            int len = PayloadLen(b) - 4;
            SetPayloadLen(b, len);
            return b.AsSpan(0, Protocol.HeaderSize + len).ToArray();
        });

        // padded end: the last entry is a string of 3 bytes (4 + 4 + 3 = 11 bytes, padded to 12). Both payloadLen forms are
        // accepted: with the padding (as the CommandWriter writes it) and without (as every other command does).
        void Accepted(string name, Func<byte[], byte[]>? patch)
        {
            var got = Send(c => c.StyleString(Protocol.StyleField.TextFontFamily, Protocol.StyleState.Normal, "Abc"), patch);
            s.Check(got == 0, $"{name} -> 0 ({got}: {(got != 0 ? Native.LastError() : "")})");
            var c = Frame(name.Replace(' ', '_').Replace(',', '_'));
            s.Check(Near(c, 0xFF0000), $"{name}: the entries were written, the frame is red ({c})");
            BaseStyleAgain("back to the base style with CLEAR");
        }
        Accepted("padded end, payloadLen with padding", b =>
        {
            s.Check(PayloadLen(b) % 4 == 0, $"the CommandWriter pads the payload ({PayloadLen(b)})");
            return b;
        });
        Accepted("padded end, payloadLen without padding", b =>
        {
            SetPayloadLen(b, PayloadLen(b) - 1);
            return b;
        });

        cmd.Clear();
        cmd.Destroy(Btn);
        CheckApply("destroy button");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
    }
}
