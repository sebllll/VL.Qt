using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VL.Qt;
using VL.Qt.Interop;
using VL.Qt.Wire;

// Input inside user QML (docs/user-qml.md, "Input"): a MouseArea follows Qt's grab rules like the handlers of the
// Interaction pin, a user QML element is one focus owner, and Esc is a key like any other except in TextField and SpinBox.
// Test file assets/QmlInput.qml (key sink left, MouseArea middle, nothing right).
public static partial class HandlerTest
{
    private const int KeyA = 0x41, KeyB = 0x42, KeyC = 0x43, KeyD = 0x44, KeyEscape = 0x01000000, KeyRight = 0x01000014;

    private static string? FindAsset(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "VL.Qt.SmokeTest", "assets", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void RunQmlInput(Scenario s)
    {
        var file = FindAsset("QmlInput.qml");
        s.Check(file != null, $"qml-input: QmlInput.qml found ({file})");
        if (file == null) return;

        using var h = new Harness("qml-input", s);
        // R (100..400 x 100..300) below; Q (100..400 x 100..200) on top of R's upper half, with a Tap and a Hover handler.
        const uint R = 1, Q = 2, TR = 10, PR = 11, TQ = 12, HQ = 13, Sl = 3;
        const uint SR = 41, SPR = 42, SQ = 43, SHQ = 44, SQml = 45, SSl = 46;
        h.Rect(R, 100, 100, 300, 200, 0);
        h.Handler(TR, Protocol.ElementType.TapHandler, R, SR);
        h.Handler(PR, Protocol.ElementType.TouchPointsHandler, R, SPR);
        h.Cmd.Create(Q, Protocol.ElementType.Qml);
        h.Cmd.SetString(Q, Protocol.PropertyId.QmlSource, file);
        h.Cmd.SetBool(Q, Protocol.PropertyId.AutoSize, false);
        h.Cmd.SetPlace(Q, 100, 100, 300, 100);
        h.Cmd.Reparent(Q, 0, 1);
        h.Cmd.Bind(Q, SQml);
        h.Handler(TQ, Protocol.ElementType.TapHandler, Q, SQ);
        h.Handler(HQ, Protocol.ElementType.HoverHandler, Q, SHQ);
        h.Apply("scene");
        h.Frame();
        h.Frame();

        // The last value of a property of the QML root in the records of the last Frame (null: not sent).
        object? Prop(string name)
        {
            object? value = null;
            foreach (var r in h.All)
                if (r.Event == Protocol.EventId.QmlProperty && r.Handle == SQml && QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out var v) && n == name)
                    value = v;
            return value;
        }
        long? Int(string name) => Prop(name) is { } v ? Convert.ToInt64(v) : null;
        string Qml() => string.Join(", ", h.All.Where(r => r.Event == Protocol.EventId.QmlProperty && r.Handle == SQml)
            .Select(r => QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out var v) ? $"{n}={v}" : "?"));
        void Key(int key) => s.Check(NativeTest.vlqt_test_send_key(h.W, key, 0) == 0, $"qml-input: send_key {key:X}");

        // ---- pointer: the MouseArea is a child of the element's item and on top of its handlers -----------------------------
        h.Mouse(TestProtocol.Mouse.Move, 250, 150);
        var recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Hover, SHQ, true), $"qml-input: over the MouseArea -> the element's Hover reports ({Dump(recs)})");
        h.Click(250, 150);
        recs = h.Frame();
        s.Check(Int("clicks") == 1 && None(recs, SQ) && None(recs, SR), $"qml-input: click on the MouseArea -> QML clicks, no Click of the element or below ({Qml()}; {Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 350, 150);
        h.Click(350, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SQ, 1) && None(recs, SR) && Int("clicks") == null,
            $"qml-input: click where the QML has no input -> the element's Click ({Qml()}; {Dump(recs)})");

        // ---- stacking: without handlers at the element, a press where the QML has no input reaches the element below --------
        h.Cmd.Destroy(TQ);
        h.Cmd.Destroy(HQ);
        h.Apply("element handlers removed");
        h.Frame();
        h.Click(350, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SR, 1), $"qml-input: click on the QML without input -> the element below clicks ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 250, 150);
        h.Click(250, 150);
        recs = h.Frame();
        s.Check(Int("clicks") == 2 && None(recs, SR), $"qml-input: click on the MouseArea over an element with Click -> only QML ({Qml()}; {Dump(recs)})");

        // ---- touch: the MouseArea occludes Touch Points below, a finger beside it is seen ----------------------------------
        h.Touch(TestProtocol.Touch.Press, 1, 250, 150);
        recs = h.Frame();
        s.Check(None(recs, SPR), $"qml-input: finger on the MouseArea -> Touch Points below does not see it ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Release, 1, 250, 150);
        recs = h.Frame();
        s.Check(None(recs, SPR) && Int("clicks") == 3, $"qml-input: finger released -> QML clicks, Touch Points silent ({Qml()}; {Dump(recs)})");
        h.Touch(TestProtocol.Touch.Press, 2, 250, 250);
        recs = h.Frame();
        s.Check(HasPoints(recs, SPR, (2, 150, 150)), $"qml-input: finger beside the QML -> Touch Points sees it ({Pts(recs, SPR)})");
        h.Touch(TestProtocol.Touch.Release, 2, 250, 250);
        h.Frame();

        // ---- hot reload while the MouseArea is pressed: no crash, no stuck press, the new instance clicks --------------------
        h.Mouse(TestProtocol.Mouse.Move, 250, 150);
        h.Mouse(TestProtocol.Mouse.Press, 250, 150);
        h.Frame();
        s.Check(Prop("pressed") is true, $"qml-input: press -> pressed ({Qml()})");
        h.Cmd.SetI32(Q, Protocol.PropertyId.QmlReload, 1);
        h.Apply("reload while pressed");
        h.Frame();
        h.Mouse(TestProtocol.Mouse.Release, 250, 150);
        recs = h.Frame();
        s.Check(None(recs, SR) && Int("clicks") is null or 0, $"qml-input: release after the reload -> no click anywhere ({Qml()}; {Dump(recs)})");
        h.Click(250, 150);
        recs = h.Frame();
        s.Check(Int("clicks") == 1 && None(recs, SR), $"qml-input: the new instance clicks ({Qml()}; {Dump(recs)})");

        // ---- keyboard: the element is one focus owner, Esc reaches the QML ------------------------------------------------
        h.Click(150, 150);
        h.Frame();
        Key(KeyA);
        h.Frame();
        s.Check(Int("keys") == 1 && Int("lastKey") == KeyA, $"qml-input: press on the key sink + key -> the QML sees it ({Qml()})");
        h.Click(250, 150);
        h.Frame();
        Key(KeyB);
        h.Frame();
        s.Check(Int("keys") == 2 && Int("lastKey") == KeyB, $"qml-input: press on another part of the widget keeps the focus ({Qml()})");
        Key(KeyEscape);
        h.Frame();
        s.Check(Int("keys") == 3 && Int("lastKey") == KeyEscape, $"qml-input: Esc reaches the QML ({Qml()})");
        Key(KeyC);
        h.Frame();
        s.Check(Int("keys") == 4 && Int("lastKey") == KeyC, $"qml-input: Esc keeps the focus ({Qml()})");
        h.Click(600, 450);
        h.Frame();
        Key(KeyD);
        h.Frame();
        s.Check(Int("keys") is null, $"qml-input: press outside the widget releases the focus ({Qml()})");

        // ---- Esc on a Slider: no longer a window-wide rule, the Slider keeps the focus ------------------------------------
        h.Cmd.Create(Sl, Protocol.ElementType.Slider);
        VL.Qt.Styles.BaseStyle.Write(h.Cmd, Sl); // like the Reconciler
        h.Cmd.SetBool(Sl, Protocol.PropertyId.AutoSize, false);
        h.Cmd.SetPlace(Sl, 100, 400, 400, 40);
        h.Cmd.Reparent(Sl, 0, 2);
        h.Cmd.Bind(Sl, SSl);
        h.Apply("slider");
        h.Frame();
        h.Click(300, 420);
        h.Frame();
        s.Check(h.All.Exists(r => r.Event == Protocol.EventId.Value && r.Handle == SSl), $"qml-input: click on the Slider -> value");
        Key(KeyEscape);
        h.Frame();
        Key(KeyRight);
        h.Frame();
        s.Check(h.All.Exists(r => r.Event == Protocol.EventId.Value && r.Handle == SSl), "qml-input: Esc on the Slider keeps the focus, an arrow key moves it");
    }
}
