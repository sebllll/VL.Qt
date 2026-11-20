using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

// #320: Click / Point / Wheel nodes at a Rectangle, node level (nodes -> reconciler -> QtNative ->
// SinkRegistry -> pull -> channels). One window per scenario; the rectangle covers (100, 100, 200, 100) DIP. The raw
// QtNative side of the handlers is HandlerTest (scenarios tap / point / wheel).
public static partial class InteractionTest
{
    sealed class Rig : IDisposable
    {
        readonly Scenario _s;
        readonly string _tag;
        readonly CommandWriter _cmd = new();
        readonly Reconciler _rec;
        readonly byte[] _buf = new byte[1 << 16];
        readonly RectangleNode _rect = new(_ => new NoMessage());
        public readonly IntPtr W;
        public Func<Interaction?> Chain = () => null;
        public RectangleF Bounds = new(1, 1, 2, 1);
        public RectangleF SceneBounds;

        public Rig(string tag, Scenario s)
        {
            _tag = tag;
            _s = s;
            _rec = new Reconciler(_cmd, new IdAllocator(), new SceneRegistry());
            W = Native.vlqt_window_create(800, 600, "VL.Qt " + tag + " test");
            s.Check(W != IntPtr.Zero, tag + ": window_create");
            Native.vlqt_window_show(W);
        }

        public int Handlers() => Native.vlqt_test_handler_count(W, _rec.Root?.Id ?? 0);

        public void Frame()
        {
            var e = _rect.Update(Bounds.Sz(), Bounds.Tr(), RectangleAnchor.TopLeft, new Color4(0.2f, 0.4f, 0.8f, 1), 0,
                new Color4(0, 0, 0, 1), 0, 1, false, false, Chain(), null, out _, out SceneBounds);
            _cmd.Reset();
            _rec.Update(e, Space.DIPTopLeft);
            if (_cmd.Length > 0)
            {
                var rc = Native.Apply(W, _cmd.EndFrame());
                _s.Check(rc == 0, $"{_tag}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            _s.Pump(1);
            int n = Native.Poll(W, _buf);
            _s.Check(n >= 0, $"{_tag}: poll ({n})");
            var res = _rec.Sinks.Dispatch(_buf.AsSpan(0, Math.Max(n, 0)));
            _s.Check(res.Error is null, $"{_tag}: dispatch ({res.Error})");
        }

        // Event in frame 1, pulled by the node in frame 2.
        public void Frames(int count = 2) { for (int i = 0; i < count; i++) Frame(); }
        public void Mouse(int type, float x, float y) => Native.vlqt_test_send_mouse(W, type, x, y);
        public void Touch(int type, float x, float y) => Native.vlqt_test_send_touch(W, type, 1, x, y);
        public void Touch(int type, int id, float x, float y) => Native.vlqt_test_send_touch(W, type, id, x, y);
        public void Click(float x, float y) { Mouse(Protocol.Mouse.Press, x, y); Mouse(Protocol.Mouse.Release, x, y); }

        public void Dispose()
        {
            _cmd.Reset();
            _rec.Update(null, Space.DIPTopLeft);
            if (_cmd.Length > 0)
                _s.Check(Native.Apply(W, _cmd.EndFrame()) == 0, _tag + ": clear");
            _rect.Dispose();
            Native.vlqt_window_destroy(W);
            _s.Pump(1);
        }
    }

    static bool Near(Vector2 a, float x, float y) => Math.Abs(a.X - x) < 1e-4f && Math.Abs(a.Y - y) < 1e-4f;

    // Scenario "click-node": mouse, double click, touch, long press, Buttons, Devices.
    static void RunClick(Scenario s)
    {
        using var click = new ClickNode(_ => new NoMessage());
        using var rig = new Rig("click", s);
        var pressed = Channel.Create(false);
        var clicked = Channel.Create(Unit.Default);
        var tapCount = Channel.Create(0);
        var longPressed = Channel.Create(Unit.Default);
        int clicks = 0, longs = 0;
        using var s1 = clicked.Subscribe(_ => clicks++);
        using var s2 = longPressed.Subscribe(_ => longs++);
        clicks = longs = 0;
        var buttons = PointerButtons.Left;
        var devices = PointerDevices.All;
        rig.Chain = () => click.Update(null, pressed, clicked, tapCount, longPressed, buttons, devices);

        rig.Mouse(Protocol.Mouse.Move, 700, 500);
        rig.Frames();
        s.Check(rig.Handlers() == 1, $"click: one TapHandler at the item ({rig.Handlers()})");

        rig.Click(700, 500);
        rig.Frames();
        s.Check(clicks == 0 && !pressed.Value, $"click: outside -> nothing ({clicks})");

        rig.Mouse(Protocol.Mouse.Press, 150, 150);
        rig.Frames();
        s.Check(pressed.Value && pressed.LatestAuthor == "Qt", $"click: Is Pressed while down, author Qt ({pressed.Value}, {pressed.LatestAuthor})");
        s.Check(clicks == 0, "click: no click before the release");
        rig.Mouse(Protocol.Mouse.Release, 150, 150);
        rig.Frames();
        s.Check(!pressed.Value, "click: released -> Is Pressed false");
        s.Check(clicks == 1 && clicked.LatestAuthor == "Qt", $"click: one value per click ({clicks}, {clicked.LatestAuthor})");
        s.Check(tapCount.Value == 1, $"click: Tap Count 1 ({tapCount.Value})");
        rig.Frames();
        s.Check(clicks == 1, "click: nothing more at rest");

        Thread.Sleep(1000); // > double click interval
        rig.Click(150, 150);
        rig.Click(150, 150);
        rig.Frames();
        s.Check(clicks == 3 && tapCount.Value == 2, $"click: double click -> 2 values, Tap Count 2 ({clicks}, {tapCount.Value})");
        // Tap Count keeps the value of the last click until the next one (#320 manual test: it fell back to 0).
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            rig.Frame();
            Thread.Sleep(20);
        }
        s.Check(tapCount.Value == 2, $"click: 2 s later Tap Count still 2 ({tapCount.Value})");

        Thread.Sleep(1000);
        rig.Touch(Protocol.Touch.Press, 150, 150);
        rig.Frames();
        s.Check(pressed.Value, "click: touch press -> Is Pressed");
        rig.Touch(Protocol.Touch.Release, 150, 150);
        rig.Frames();
        s.Check(clicks == 4 && !pressed.Value, $"click: touch tap clicks ({clicks})");

        // Long press: Qt's press-and-hold interval is 800 ms.
        Thread.Sleep(1000);
        int before = clicks;
        rig.Mouse(Protocol.Mouse.Press, 150, 150);
        rig.Frame();
        // Hold until the long press arrived (interval 800 ms), at most 4 s: a stalled machine delays the timer (#379).
        var held = System.Diagnostics.Stopwatch.StartNew();
        while (longs == 0 && held.ElapsedMilliseconds < 4000) { Thread.Sleep(50); rig.Frames(); }
        Thread.Sleep(400); // still held: a second long press would show up here
        rig.Frames();
        s.Check(longs == 1, $"click: long press -> one Long Pressed value ({longs})");
        rig.Mouse(Protocol.Mouse.Release, 150, 150);
        rig.Frames();
        s.Check(longs == 1 && clicks == before, $"click: no second long press, no click after it ({longs}, {clicks})");

        // Buttons: the right button does nothing until accepted.
        Thread.Sleep(1000);
        before = clicks;
        rig.Mouse(Protocol.Mouse.PressRight, 150, 150);
        rig.Mouse(Protocol.Mouse.ReleaseRight, 150, 150);
        rig.Frames();
        s.Check(clicks == before, $"click: right button not accepted by default ({clicks})");
        buttons = PointerButtons.Right;
        rig.Frame(); // SET ACCEPTED_BUTTONS
        rig.Mouse(Protocol.Mouse.PressRight, 150, 150);
        rig.Mouse(Protocol.Mouse.ReleaseRight, 150, 150);
        rig.Frames();
        s.Check(clicks == before + 1, $"click: Buttons = Right -> right click counts ({clicks})");

        // Devices: mouse only, touch ignored.
        buttons = PointerButtons.Left;
        devices = PointerDevices.Mouse;
        rig.Frame();
        Thread.Sleep(1000);
        before = clicks;
        rig.Touch(Protocol.Touch.Press, 150, 150);
        rig.Frame();
        rig.Touch(Protocol.Touch.Release, 150, 150);
        rig.Frames();
        s.Check(clicks == before, $"click: Devices = Mouse -> touch ignored ({clicks})");
        rig.Click(150, 150);
        rig.Frames();
        s.Check(clicks == before + 1, $"click: Devices = Mouse -> mouse clicks ({clicks})");
    }

    // Scenario "point-node": position in units, mouse + touch, passes presses through, chain Hover -> Click -> Point,
    // Buttons None = hover position of the mouse (#320).
    static void RunPoint(Scenario s)
    {
        using var hover = new HoverNode(_ => new NoMessage());
        using var click = new ClickNode(_ => new NoMessage());
        using var point = new PointNode(_ => new NoMessage());
        using var rig = new Rig("point", s);
        var active = Channel.Create(false);
        var position = Channel.Create(Vector2.Zero);
        var hovered = Channel.Create(false);
        var clicked = Channel.Create(Unit.Default);
        int clicks = 0;
        using var s1 = clicked.Subscribe(_ => clicks++);
        clicks = 0;
        bool chained = false;
        var buttons = PointerButtons.Left;
        rig.Chain = () =>
        {
            Interaction? chain = null;
            if (chained)
            {
                chain = hover.Update(chain, hovered);
                chain = click.Update(chain, null, clicked, null, null, PointerButtons.Left, PointerDevices.All);
            }
            return point.Update(chain, active, position, buttons, PointerDevices.All);
        };

        rig.Mouse(Protocol.Mouse.Move, 700, 500);
        rig.Frames();
        s.Check(rig.Handlers() == 1, $"point: one PointHandler ({rig.Handlers()})");
        s.Check(!active.Value, "point: not active");

        rig.Mouse(Protocol.Mouse.Press, 160, 170);
        rig.Frames();
        s.Check(active.Value && active.LatestAuthor == "Qt", $"point: Is Active while down ({active.Value})");
        s.Check(Near(position.Value, 0.6f, 0.7f) && position.LatestAuthor == "Qt", $"point: Position (0.6, 0.7) units, local ({position.Value})");
        rig.Mouse(Protocol.Mouse.Move, 180, 180);
        rig.Frames();
        s.Check(Near(position.Value, 0.8f, 0.8f), $"point: Position follows the drag ({position.Value})");
        rig.Mouse(Protocol.Mouse.Release, 180, 180);
        rig.Frames();
        s.Check(!active.Value, "point: released -> Is Active false");

        rig.Touch(Protocol.Touch.Press, 150, 150);
        rig.Frames();
        s.Check(active.Value && Near(position.Value, 0.5f, 0.5f), $"point: touch -> active, Position ({active.Value}, {position.Value})");
        rig.Touch(Protocol.Touch.Release, 150, 150);
        rig.Frames();
        s.Check(!active.Value, "point: touch released -> not active");

        // Chain Hover -> Click -> Point: three handlers, all report, Point does not take the press.
        chained = true;
        rig.Frame();
        s.Check(rig.Handlers() == 3, $"point: Hover + Click + Point -> three handlers ({rig.Handlers()})");
        rig.Mouse(Protocol.Mouse.Move, 150, 150);
        rig.Frames();
        s.Check(hovered.Value, "point: chain -> hovered");
        rig.Click(150, 150);
        rig.Frames();
        s.Check(clicks == 1 && Near(position.Value, 0.5f, 0.5f), $"point: chain -> Click counts, Point reports ({clicks}, {position.Value})");
        // Same left button for Click and Point (#320 manual test): Point active with the position while held, click on release.
        rig.Mouse(Protocol.Mouse.Press, 120, 130);
        rig.Frames();
        s.Check(active.Value && Near(position.Value, 0.2f, 0.3f), $"point: chain, left held -> Is Active, Position ({active.Value}, {position.Value})");
        rig.Mouse(Protocol.Mouse.Move, 130, 140);
        rig.Frames();
        s.Check(active.Value && Near(position.Value, 0.3f, 0.4f), $"point: chain, drag -> Position follows ({active.Value}, {position.Value})");
        rig.Mouse(Protocol.Mouse.Release, 130, 140);
        rig.Frames();
        s.Check(!active.Value && clicks == 2, $"point: chain, release -> not active, Click counts ({active.Value}, {clicks})");
        System.Threading.Thread.Sleep(600);
        rig.Touch(Protocol.Touch.Press, 120, 130);
        rig.Frames();
        s.Check(active.Value && Near(position.Value, 0.2f, 0.3f), $"point: chain, finger -> Is Active, Position ({active.Value}, {position.Value})");
        rig.Touch(Protocol.Touch.Release, 120, 130);
        rig.Frames();
        s.Check(!active.Value && clicks == 3, $"point: chain, finger up -> not active, Click counts ({active.Value}, {clicks})");

        // Buttons None: the mouse is active while over the element, without a press.
        chained = false;
        rig.Mouse(Protocol.Mouse.Move, 700, 500);
        rig.Frames();
        buttons = PointerButtons.None;
        rig.Frames();
        s.Check(!active.Value, "point: Buttons None, mouse outside -> not active");
        rig.Mouse(Protocol.Mouse.Move, 120, 130);
        rig.Frames();
        s.Check(active.Value && Near(position.Value, 0.2f, 0.3f), $"point: Buttons None -> active while hovering, Position ({active.Value}, {position.Value})");
        rig.Mouse(Protocol.Mouse.Move, 700, 500);
        rig.Frames();
        s.Check(!active.Value && Near(position.Value, 0.2f, 0.3f), $"point: Buttons None, mouse leaves -> not active, Position kept ({active.Value}, {position.Value})");
        rig.Mouse(Protocol.Mouse.Move, 150, 150);
        rig.Frames();
        s.Check(active.Value, "point: Buttons None, mouse back -> active");
        buttons = PointerButtons.Left;
        rig.Frames(3);
        s.Check(!active.Value, $"point: Buttons None -> Left while hovering -> not active ({active.Value})");
    }

    // Scenario "wheel-node": Delta in notches, once per frame with motion, then once (0, 0); Devices.
    static void RunWheel(Scenario s)
    {
        using var wheel = new WheelNode(_ => new NoMessage());
        using var rig = new Rig("wheel", s);
        var delta = Channel.Create(Vector2.Zero);
        var values = new List<Vector2>();
        using var sub = delta.Subscribe(values.Add);
        values.Clear();
        var value = Channel.Create(Vector2.Zero);
        var devices = PointerDevices.All;
        rig.Chain = () => wheel.Update(null, delta, value, devices);

        rig.Frames();
        s.Check(rig.Handlers() == 1 && values.Count == 0, $"wheel: one WheelHandler, nothing at rest ({rig.Handlers()}, {values.Count})");

        Native.vlqt_test_send_wheel(rig.W, 150, 150, 0, 120);
        rig.Frames(3);
        s.Check(values.Count == 2 && Near(values[0], 0, 1) && values[1] == Vector2.Zero,
            $"wheel: one notch -> (0, 1), then once (0, 0) ({string.Join(" ", values)})");
        s.Check(delta.LatestAuthor == "Qt", "wheel: author Qt");

        values.Clear();
        Native.vlqt_test_send_wheel(rig.W, 150, 150, 60, -120);
        Native.vlqt_test_send_wheel(rig.W, 150, 150, 0, -120);
        rig.Frames(3);
        s.Check(values.Count == 2 && Near(values[0], 0.5f, -2) && values[1] == Vector2.Zero,
            $"wheel: summed per frame (0.5, -2) ({string.Join(" ", values)})");
        s.Check(Near(value.Value, 0.5f, -1) && value.LatestAuthor == "Qt", $"wheel: Value sums all motion (0.5, -1) ({value.Value})");
        value.Value = Vector2.Zero; // reset from outside
        Native.vlqt_test_send_wheel(rig.W, 150, 150, 0, 240);
        rig.Frames(3);
        s.Check(Near(value.Value, 0, 2), $"wheel: Value reset from outside, then (0, 2) ({value.Value})");

        values.Clear();
        Native.vlqt_test_send_wheel(rig.W, 700, 500, 0, 120);
        rig.Frames(3);
        s.Check(values.Count == 0, $"wheel: outside the element -> nothing ({values.Count})");

        devices = PointerDevices.TouchScreen;
        rig.Frame();
        Native.vlqt_test_send_wheel(rig.W, 150, 150, 0, 120);
        rig.Frames(3);
        s.Check(values.Count == 0, $"wheel: Devices = TouchScreen -> mouse wheel ignored ({values.Count})");
    }

    // Scenario "drag-node": the Drag node moves the element, Scene Bounds shows it, a changed (also animated) Bounds pin
    // puts it back, Moves Element off only reports (#209).
    static void RunDragNode(Scenario s)
    {
        using var drag = new DragNode(_ => new NoMessage());
        using var rig = new Rig("drag-node", s);
        var dragging = Channel.Create(false);
        var delta = Channel.Create(Vector2.Zero);
        bool moves = true;
        rig.Chain = () => drag.Update(null, dragging, delta, moves, DragAxis.Both, default, default, PointerButtons.Left, PointerDevices.All);

        void Drag(float x, float y, float dx, float dy, bool release)
        {
            rig.Mouse(Protocol.Mouse.Move, x, y);
            rig.Mouse(Protocol.Mouse.Press, x, y);
            for (int i = 1; i <= 4; ++i)
                rig.Mouse(Protocol.Mouse.Move, x + dx * i / 4, y + dy * i / 4);
            if (release)
                rig.Mouse(Protocol.Mouse.Release, x + dx, y + dy);
            rig.Frames(3);
        }
        Vector2 Offset(RectangleF home) => rig.SceneBounds.TopLeft - home.TopLeft;
        static bool Close(Vector2 a, float x, float y) => Math.Abs(a.X - x) < 1e-3f && Math.Abs(a.Y - y) < 1e-3f;

        rig.Mouse(Protocol.Mouse.Move, 700, 500);
        rig.Frames(3);
        s.Check(rig.Handlers() == 1, $"drag-node: one DragHandler ({rig.Handlers()})");
        var home = rig.SceneBounds;
        s.Check(home.Width > 0, $"drag-node: Scene Bounds reported ({home})");

        Drag(150, 150, 40, 20, release: false);
        s.Check(dragging.Value && dragging.LatestAuthor == "Qt", $"drag-node: Is Dragging while dragged ({dragging.Value})");
        s.Check(Near(delta.Value, 0.4f, 0.2f) && delta.LatestAuthor == "Qt", $"drag-node: Delta (0.4, 0.2) units ({delta.Value})");
        s.Check(Close(Offset(home), 0.4f, 0.2f), $"drag-node: element follows, Scene Bounds has the offset ({rig.SceneBounds})");
        rig.Mouse(Protocol.Mouse.Release, 190, 170);
        rig.Frames(3);
        s.Check(!dragging.Value && Near(delta.Value, 0.4f, 0.2f), $"drag-node: release -> not dragging, Delta kept ({dragging.Value}, {delta.Value})");
        s.Check(Close(Offset(home), 0.4f, 0.2f), $"drag-node: unchanged Bounds pin -> element stays ({rig.SceneBounds})");

        // Changed Bounds pin: back to the pin's position.
        rig.Bounds = new RectangleF(1, 1.5f, 2, 1);
        rig.Frames(3);
        s.Check(Close(Offset(home), 0, 0.5f), $"drag-node: changed Bounds pin -> back on the pin ({rig.SceneBounds})");
        rig.Bounds = new RectangleF(1, 1, 2, 1);
        rig.Frames(3);

        // Animated Bounds pin: overrules the drag every frame (the held drag does not move between the frames).
        Drag(150, 150, 40, 20, release: false);
        s.Check(Close(Offset(home), 0.4f, 0.2f), $"drag-node: second drag moves ({rig.SceneBounds})");
        for (int i = 1; i <= 3; i++)
        {
            rig.Bounds = new RectangleF(1 + 0.1f * i, 1, 2, 1);
            rig.Frames(3);
            s.Check(Close(Offset(home), 0.1f * i, 0), $"drag-node: animated Bounds pin overrules the drag, frame {i} ({rig.SceneBounds})");
        }
        rig.Mouse(Protocol.Mouse.Release, 190, 170);
        rig.Bounds = new RectangleF(1, 1, 2, 1);
        rig.Frames(3);
        s.Check(Close(Offset(home), 0, 0), $"drag-node: reset ({rig.SceneBounds})");

        // Moves Element off: only the channels.
        moves = false;
        rig.Frames(2);
        Drag(150, 150, 30, 10, release: true);
        s.Check(Near(delta.Value, 0.3f, 0.1f), $"drag-node: Moves Element off -> Delta reported ({delta.Value})");
        s.Check(Close(Offset(home), 0, 0), $"drag-node: Moves Element off -> element stays ({rig.SceneBounds})");
    }
}
