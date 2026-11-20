using NUnit.Framework;
#if WINDOWS
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using VL.Qt;

// Keys on the real Windows path: vvvv's main thread runs a WinForms message loop, and Qt gets its keys from
// Win32 messages to its own HWND, not from QtNative. WinForms' PreTranslateMessage hands every key message for a
// non-WinForms window to IsDialogMessage(root). That uses Enter, Tab, Escape and the arrow keys for dialog navigation
// unless the window answers WM_GETDLGCODE with DLGC_WANT*, so Enter never reached a TextField or SpinBox in vvvv.
// The test posts WM_KEYDOWN/WM_KEYUP to the Qt window and lets Application.DoEvents (the same PreTranslateMessage)
// dispatch them before Qt's own pump can. Typing uses digits, so the keyboard layout does not matter.
[TestFixture, NonParallelizable]
public static class KeyRouteTest
{
    /// <summary>Key routing to the Qt window.</summary>
    [Test, Category("keyroute")]
    public static void KeyRoute() => Scenario.Run(Run);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_GETDLGCODE = 0x0087;
    private const int VK_RETURN = 0x0D;
    private const int VK_LEFT = 0x25;
    private const int DLGC_WANTALLKEYS = 0x0004;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload)
    {
        public ushort ValueType => BinaryPrimitives.ReadUInt16LittleEndian(Payload);
        public int I32 => BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4));
        public string Str => Encoding.UTF8.GetString(Payload, 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(4)));
    }

    private static List<Rec> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length)
                break;
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)), b.Slice(off + 12, len - 8).ToArray()));
            off += (4 + len + 3) & ~3;
        }
        return list;
    }

    private static void Run(Scenario s)
    {
        var hwnd = IntPtr.Zero;
        // Locally the key checks failed in runs beside other sessions (18 FAIL), the same develop commit passed in the
        // CI and in a later local run. Windows does not hand the foreground to a process started in the background,
        // and Qt Quick types into the active window only. Outside the CI a failure without the foreground is a SKIP.
        void Check(bool ok, string what)
        {
            if (ok || hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd || Environment.GetEnvironmentVariable("CI") != null)
                s.Check(ok, what);
            else
                s.Skip($"{what}: the test window is not in the foreground (another window holds it)");
        }
        const string title = "VL.Qt key route test";
        var w = Native.vlqt_window_create(600, 400, title);
        Check(w != IntPtr.Zero, "key route window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            Check(rc == 0, $"key route {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Frame()
        {
            s.Pump(1);
            int n = Native.Poll(w, buf);
            return Parse(buf.AsSpan(0, Math.Max(n, 0)));
        }
        void Click(float x, float y)
        {
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, x, y);
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Press, x, y);
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Release, x, y);
        }
        static List<Rec> Values(List<Rec> recs, uint handle) => recs.FindAll(r => r.Event == Protocol.Ev.Value && r.Handle == handle);
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));

        cmd.Reset();
        cmd.Create(T, Protocol.Type.TextField);
        VL.Qt.Styles.BaseStyle.Write(cmd, T); // like the Reconciler
        cmd.SetBool(T, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(T, 50, 50, 300, 40);
        cmd.SetString(T, Protocol.Prop.Value, "");
        cmd.Reparent(T, 0, 0);
        cmd.Bind(T, HT, 0);
        cmd.Create(S1, Protocol.Type.SpinBox);
        VL.Qt.Styles.BaseStyle.Write(cmd, S1); // like the Reconciler
        cmd.SetBool(S1, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(S1, 50, 150, 240, 40);
        cmd.Reparent(S1, 0, 1);
        cmd.Bind(S1, HS, 0);
        CheckApply("create");
        s.Pump(3);
        Frame();

        hwnd = FindWindowW(null, title);
        Check(hwnd != IntPtr.Zero, "key route Qt window found by title");
        // The Qt window itself, not IsDialogMessage, must handle Enter, Tab, Escape and the arrows.
        var code = (long)SendMessageW(hwnd, WM_GETDLGCODE, (IntPtr)VK_RETURN, IntPtr.Zero);
        Check((code & DLGC_WANTALLKEYS) != 0, $"key route WM_GETDLGCODE -> DLGC_WANTALLKEYS (0x{code:x})");


        // One key like a keyboard: WM_KEYDOWN + WM_KEYUP to the Qt HWND, dispatched by WinForms (TranslateMessage adds
        // WM_CHAR); numpad Enter = VK_RETURN with the extended-key bit.
        void PostKey(int vk, bool extended = false)
        {
            long scan = MapVirtualKeyW((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
            long ext = extended ? 1L << 24 : 0;
            PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)(1 | (scan << 16) | ext));
            PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)(1 | (scan << 16) | ext | (1L << 30) | (1L << 31)));
            System.Windows.Forms.Application.DoEvents();
        }
        void TypeDigits(string digits)
        {
            foreach (var c in digits)
                PostKey(c); // VK '0'..'9' = ASCII
        }

        // TextField: typed + Enter commits (focus stays).
        Click(200, 70);
        Frame();
        TypeDigits("42");
        var recs = Frame();
        Check(Values(recs, HT).Count == 0, $"key route textfield typing without Enter -> no EV_VALUE ({S(recs)})");
        PostKey(VK_RETURN);
        recs = Frame();
        var v = Values(recs, HT);
        Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.String && v[0].Str == "42", $"key route textfield Enter (WinForms loop) -> \"42\" ({S(recs)})");
        TypeDigits("7");
        PostKey(VK_RETURN, extended: true);
        recs = Frame();
        v = Values(recs, HT);
        Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.String && v[0].Str == "427", $"key route textfield numpad Enter -> \"427\" ({S(recs)})");
        // An arrow key moves the cursor in the field instead of the dialog focus: 4 2 7 -> left -> 4 2 9 7.
        PostKey(VK_LEFT);
        TypeDigits("9");
        PostKey(VK_RETURN);
        recs = Frame();
        v = Values(recs, HT);
        Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.String && v[0].Str == "4297", $"key route textfield arrow left + Enter -> \"4297\" ({S(recs)})");

        // SpinBox (Input (Int)): typed digits + Enter -> valueModified.
        Click(170, 170); // text area between the indicators
        Frame();
        PostKey(0x23 /* VK_END */);
        TypeDigits("5");
        PostKey(VK_RETURN);
        recs = Frame();
        var sv = Values(recs, HS);
        Check(sv.Count == 1 && sv[0].ValueType == Protocol.Vt.I32 && sv[0].I32 == 5, $"key route spinbox Enter (WinForms loop) -> 5 ({S(recs)})");
        TypeDigits("3");
        PostKey(VK_RETURN, extended: true);
        recs = Frame();
        sv = Values(recs, HS);
        Check(sv.Count == 1 && sv[0].ValueType == Protocol.Vt.I32 && sv[0].I32 == 53, $"key route spinbox numpad Enter -> 53 ({S(recs)})");

        RunFocus(Check, w, cmd, CheckApply, Frame, PostKey, TypeDigits, Click);

        cmd.Destroy(T);
        cmd.Destroy(S1);
        cmd.Destroy(R);
        CheckApply("destroy");
        Native.vlqt_window_destroy(w);
        s.Pump(1);
    }

    // Esc discards the edit and releases the focus (no EV_VALUE), a press outside the focused element (mouse,
    // touch, also on an element with a Tap handler, which still clicks) releases it and commits, Enter keeps it.
    // "Focus gone" shows as: typing + Enter afterwards sends nothing. Starts with TextField "4297", SpinBox 53.
    private static void RunFocus(Action<bool, string> check, IntPtr w, CommandWriter cmd, Action<string> apply, Func<List<Rec>> frame,
        Action<int, bool> postKey, Action<string> type, Action<float, float> click)
    {
        const int VK_ESCAPE = 0x1B, VK_END = 0x23;
        static List<Rec> Values(List<Rec> recs, uint handle) => recs.FindAll(r => r.Event == Protocol.Ev.Value && r.Handle == handle);
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));
        void Key(int vk) => postKey(vk, false);
        // Qt queues the posted keys and delivers them in its pump, the test presses are sent at once: pump first, so
        // the press comes after the typed keys like on a real keyboard + mouse.
        void Click(float x, float y)
        {
            frame();
            click(x, y);
        }
        void Tap(float x, float y)
        {
            frame();
            Native.vlqt_test_send_touch(w, Protocol.Touch.Press, 1, x, y);
            Native.vlqt_test_send_touch(w, Protocol.Touch.Release, 1, x, y);
        }
        void Focus(float x, float y)
        {
            Click(x, y);
            frame();
            Key(VK_END);
        }
        bool FocusGone(uint handle, out string recs)
        {
            type("6");
            Key(VK_RETURN);
            var r = frame();
            recs = S(r);
            return Values(r, handle).Count == 0;
        }
        bool Text(List<Rec> r, string expected) => Values(r, HT) is { Count: 1 } v && v[0].ValueType == Protocol.Vt.String && v[0].Str == expected;
        bool Int(List<Rec> r, int expected) => Values(r, HS) is { Count: 1 } v && v[0].ValueType == Protocol.Vt.I32 && v[0].I32 == expected;

        // A rectangle with a Tap handler (Click) in an empty corner.
        cmd.Create(R, Protocol.Type.Rectangle);
        cmd.SetPlace(R, 380, 250, 180, 100);
        cmd.Reparent(R, 0, 2);
        cmd.Create(RT, Protocol.Type.TapHandler);
        cmd.Reparent(RT, R, 0);
        cmd.Bind(RT, HR, 0);
        apply("tap rect");
        frame();

        // Enter keeps the focus: a second edit + Enter commits again without a click.
        Focus(200, 70);
        type("1");
        Key(VK_RETURN);
        var recs = frame();
        check(Text(recs, "42971"), $"textfield Enter commits \"42971\" ({S(recs)})");
        type("2");
        Key(VK_RETURN);
        recs = frame();
        check(Text(recs, "429712"), $"textfield Enter keeps the focus, next Enter commits \"429712\" ({S(recs)})");

        // Esc: discard, no EV_VALUE, focus gone; the text is back to the committed one.
        type("99");
        Key(VK_ESCAPE);
        recs = frame();
        check(Values(recs, HT).Count == 0, $"textfield Esc -> no EV_VALUE ({S(recs)})");
        check(FocusGone(HT, out var fr), $"textfield Esc releases the focus ({fr})");
        Focus(200, 70);
        type("3");
        Key(VK_RETURN);
        recs = frame();
        check(Text(recs, "4297123"), $"textfield Esc restored the committed text, \"4297123\" ({S(recs)})");
        // Esc without an edit: still no EV_VALUE.
        Key(VK_ESCAPE);
        recs = frame();
        check(Values(recs, HT).Count == 0, $"textfield Esc without an edit -> no EV_VALUE ({S(recs)})");

        // Mouse press into the empty window: commit (focus loss) + focus gone.
        Focus(200, 70);
        type("4");
        Click(500, 120);
        recs = frame();
        check(Text(recs, "42971234"), $"textfield mouse click into the empty window commits \"42971234\" ({S(recs)})");
        check(FocusGone(HT, out fr), $"textfield mouse click into the empty window releases the focus ({fr})");
        // Pressing into the focused field itself keeps the focus.
        Focus(200, 70);
        Click(120, 70);
        Key(VK_END);
        type("5");
        Key(VK_RETURN);
        recs = frame();
        check(Text(recs, "429712345"), $"textfield click into the focused field keeps the focus ({S(recs)})");

        // Touch tap into the empty window.
        type("7");
        Tap(500, 120);
        recs = frame();
        check(Text(recs, "4297123457"), $"textfield touch tap into the empty window commits ({S(recs)})");
        check(FocusGone(HT, out fr), $"textfield touch tap into the empty window releases the focus ({fr})");

        // Click on an element with a Tap handler: commits, releases the focus and still clicks.
        Focus(200, 70);
        type("8");
        Click(470, 300);
        recs = frame();
        check(Text(recs, "42971234578"), $"textfield click on a Tap handler commits ({S(recs)})");
        check(recs.Exists(r => r.Event == Protocol.Ev.Click && r.Handle == HR), $"click on a Tap handler still clicks ({S(recs)})");
        check(FocusGone(HT, out fr), $"textfield click on a Tap handler releases the focus ({fr})");
        // Same with touch.
        Focus(200, 70);
        type("9");
        Tap(470, 300);
        recs = frame();
        check(Text(recs, "429712345789") && recs.Exists(r => r.Event == Protocol.Ev.Click && r.Handle == HR),
            $"textfield touch tap on a Tap handler commits and clicks ({S(recs)})");
        check(FocusGone(HT, out fr), $"textfield touch tap on a Tap handler releases the focus ({fr})");

        // SpinBox (53): Esc discards the typed text without valueModified, the display shows the value again.
        Focus(170, 170);
        type("9");
        Key(VK_ESCAPE);
        recs = frame();
        check(Values(recs, HS).Count == 0, $"spinbox Esc -> no EV_VALUE ({S(recs)})");
        check(FocusGone(HS, out fr), $"spinbox Esc releases the focus ({fr})");
        Focus(170, 170);
        type("1");
        Key(VK_RETURN);
        recs = frame();
        check(Int(recs, 531), $"spinbox Esc restored the text of the value, 531 ({S(recs)})");
        // Enter keeps the focus.
        type("2");
        Key(VK_RETURN);
        recs = frame();
        check(Int(recs, 5312), $"spinbox Enter keeps the focus, 5312 ({S(recs)})");
        // After an Esc a SET VALUE still shows (the text binding survives the revert).
        type("4");
        Key(VK_ESCAPE);
        frame();
        cmd.SetI32(S1, Protocol.Prop.Value, 7);
        apply("spinbox set 7");
        frame();
        Focus(170, 170);
        type("2");
        Key(VK_RETURN);
        recs = frame();
        check(Int(recs, 72), $"spinbox SET VALUE after Esc shows, typed -> 72 ({S(recs)})");
        // Mouse and touch into the empty window commit and release.
        type("1");
        Click(500, 120);
        recs = frame();
        check(Int(recs, 721), $"spinbox mouse click into the empty window commits 721 ({S(recs)})");
        check(FocusGone(HS, out fr), $"spinbox mouse click into the empty window releases the focus ({fr})");
        Focus(170, 170);
        type("3");
        Tap(500, 120);
        recs = frame();
        check(Int(recs, 7213), $"spinbox touch tap into the empty window commits 7213 ({S(recs)})");
        check(FocusGone(HS, out fr), $"spinbox touch tap into the empty window releases the focus ({fr})");
    }

    private const uint R = 3;
    private const uint RT = 4;
    private const uint HR = 63;
    private const uint T = 1;
    private const uint S1 = 2;
    private const uint HT = 61;
    private const uint HS = 62;
}
#endif
