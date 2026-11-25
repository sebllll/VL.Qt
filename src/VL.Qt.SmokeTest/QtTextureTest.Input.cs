using System;
using System.Collections.Generic;
using NUnit.Framework;
using System.Reactive;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;

/// <summary>
/// task 6, scenario <c>QtTextureInput</c>: input into an offscreen scene, no window involved. One scene (400 x 300 DIP) bound to
/// a texture of the test D3D11 device, built from the element nodes (Hover, Click, Wheel, Drag, Pinch, Touch Points, Slider,
/// TextInput) and fed through <see cref="OffscreenScene"/>: vvvv notifications (<c>Notify</c>: mouse, wheel, touch, keyboard) and
/// the <c>Send*</c> calls (pen, which vvvv has no notification for, horizontal wheel, text, focus). The frame order is the one of
/// QtTexture: input, update, apply, render batch, s, poll events. Layout in DIP:
/// A (20, 20, 100 x 80) Hover + Click + Wheel; P (140, 20, 100 x 80) three Click nodes (mouse / touch screen / pen);
/// B (260, 20, 120 x 80) Drag; C (20, 120, 120 x 80) Pinch + Touch Points; Slider (160, 130, 220 x 30); TextInput (160, 170, 220 x 30);
/// the rest is empty.
/// </summary>
public static partial class QtTextureTest
{
    private sealed class InputRig : IDisposable
    {
        internal sealed class NoMessage : IDisposable { public void Dispose() { } }

        public const int W = 400, H = 300;
        private static readonly Vector2 s_client = new(W, H);

        private readonly Scenario _s;
        private readonly IntPtr _device, _context, _texture;
        public readonly OffscreenScene R;
        private readonly SceneHandle[] _batch;
        public readonly bool Ok;

        private readonly RectangleNode _rectA = new(_ => new NoMessage()), _rectP = new(_ => new NoMessage()), _rectB = new(_ => new NoMessage()), _rectC = new(_ => new NoMessage());
        private readonly GroupNode _group = new(_ => new NoMessage());
        private readonly SliderNode _slider = new(_ => new NoMessage());
        private readonly InputStringNode _text = new(_ => new NoMessage());
        private readonly HoverNode _hover = new(_ => new NoMessage());
        private readonly ClickNode _clickA = new(_ => new NoMessage()), _clickMouse = new(_ => new NoMessage()), _clickTouch = new(_ => new NoMessage()), _clickPen = new(_ => new NoMessage());
        private readonly WheelNode _wheel = new(_ => new NoMessage());
        private readonly DragNode _drag = new(_ => new NoMessage());
        private readonly PinchNode _pinch = new(_ => new NoMessage());
        private readonly TouchPointsNode _touchPoints = new(_ => new NoMessage());

        // Channels of the nodes: values in, the last author of a write says it came from Qt.
        public readonly IChannel<bool> Hovered = Channel.Create(false), Pressed = Channel.Create(false);
        public readonly IChannel<Unit> Clicked = Channel.Create(Unit.Default), MouseClicked = Channel.Create(Unit.Default),
            TouchClicked = Channel.Create(Unit.Default), PenClicked = Channel.Create(Unit.Default);
        public readonly IChannel<int> TapCount = Channel.Create(0);
        public readonly IChannel<Vector2> WheelDelta = Channel.Create(Vector2.Zero), DragDelta = Channel.Create(Vector2.Zero);
        public readonly IChannel<bool> Dragging = Channel.Create(false), PinchActive = Channel.Create(false);
        public readonly IChannel<float> PinchScale = Channel.Create(1f), PinchRotation = Channel.Create(0f), SliderValue = Channel.Create(0f);
        public readonly IChannel<Spread<TouchPoint>> Points = Channel.Create(Spread<TouchPoint>.Empty);
        public readonly IChannel<string> TextValue = Channel.Create("");

        public int Clicks, MouseClicks, TouchClicks, PenClicks, TextWrites, SliderWrites;
        public readonly List<Vector2> Wheels = new();
        private readonly IDisposable[] _subs;

        public InputRig(Scenario s)
        {
            _s = s;
            if (NativeTest.CreateDevice(0, out _device, out _context) != 0)
            {
                s.Check(false, "input: create test device");
                R = null!;
                _batch = Array.Empty<SceneHandle>();
                _subs = Array.Empty<IDisposable>();
                return;
            }
            NativeTest.vlqt_test_d3d11_create_shared_texture(_device, W, H, B8G8R8A8Typeless, out _texture, out var handle);
            R = new OffscreenScene(new OccurrenceRegistry(), new InteractionRegistry(), W, H);
            _batch = new[] { R.Handle };
            var rc = R.Bind(_device, handle, new Int2(W, H), B8G8R8A8Unorm);
            NativeTest.vlqt_test_close_handle(handle);
            Ok = rc == (int)Native.Result.Ok && R.IsBound;
            s.Check(Ok, $"input: scene created and bound ({rc}) {R.Error}");
            _subs = new[]
            {
                Clicked.Subscribe(_ => Clicks++), MouseClicked.Subscribe(_ => MouseClicks++), TouchClicked.Subscribe(_ => TouchClicks++),
                PenClicked.Subscribe(_ => PenClicks++), WheelDelta.Subscribe(Wheels.Add), TextValue.Subscribe(_ => TextWrites++),
                SliderValue.Subscribe(_ => SliderWrites++),
            };
            // The subscriptions fire once with the initial value.
            Clicks = MouseClicks = TouchClicks = PenClicks = TextWrites = SliderWrites = 0;
            Wheels.Clear();
        }

        // DIP -> units (1 unit = 100 DIP in DIPTopLeft).
        private static Vector2 Sz(float w, float h) => new(w / 100f, h / 100f);
        private static Matrix At(float x, float y) => Matrix.Translation(x / 100f, y / 100f, 0);

        private Element Rect(RectangleNode node, float x, float y, float w, float h, Color4 color, Interaction? interaction)
            => (Element)node.Update(Sz(w, h), At(x, y), RectangleAnchor.TopLeft, color, 0, new Color4(0, 0, 0, 1), 0, 1, false, false, interaction, null, true, out _, out _);

        // The scene of the frame: nodes pull the events of the last poll, then describe the elements.
        private Element Describe()
        {
            var hoverA = _hover.Update(null, Hovered);
            var clickA = _clickA.Update(hoverA, Pressed, Clicked, TapCount, null, PointerButtons.Left, PointerDevices.All);
            var interactionA = _wheel.Update(clickA, WheelDelta, null, PointerDevices.All);
            var interactionP = _clickPen.Update(
                _clickTouch.Update(_clickMouse.Update(null, null, MouseClicked, null, null, PointerButtons.Left, PointerDevices.Mouse), null, TouchClicked, null, null, PointerButtons.Left, PointerDevices.TouchScreen),
                null, PenClicked, null, null, PointerButtons.Left, PointerDevices.Pen);
            var interactionB = _drag.Update(null, Dragging, DragDelta, true, DragAxis.Both, default, default, PointerButtons.Left, PointerDevices.All);
            var interactionC = _touchPoints.Update(
                _pinch.Update(null, PinchActive, PinchScale, PinchRotation, null, null, 2, 2, PointerDevices.All),
                Points, PointerDevices.All, true);
            var children = Spread.Create<IElement?>(
                Rect(_rectA, 20, 20, 100, 80, new Color4(1, 0, 0, 1), interactionA),
                Rect(_rectP, 140, 20, 100, 80, new Color4(0, 0, 1, 1), interactionP),
                Rect(_rectC, 20, 120, 120, 80, new Color4(1, 1, 0, 1), interactionC),
                _slider.Update(false, Sz(220, 30), At(160, 130), RectangleAnchor.TopLeft, SliderValue, 0f, 1f, 0f, SliderOrientation.Horizontal, true, 1, false, false,
                    null, null, null, out _, out _, out _, out _),
                _text.Update(false, Sz(220, 30), At(160, 170), RectangleAnchor.TopLeft, TextValue, "", true, true, 1, false, false,
                    null, null, null, out _, out _, out _, out _),
                Rect(_rectB, 260, 20, 120, 80, new Color4(0, 1, 0, 1), interactionB));
            return (Element)_group.Update(children, true, default, Matrix.Identity, RectangleAnchor.TopLeft, 1, false, false, null, null, null, true, out _, out _);
        }

        /// <summary>One host frame: update, apply, render batch, s, poll events.</summary>
        public void Frame()
        {
            R.Update(Describe(), Space.DIPTopLeft, new Color4(0, 0, 0, 0), true, true);
            R.ApplyCommands();
            int rc = global::VL.Qt.OffscreenScene.RenderBatch(_context, _batch, 0);
            if (rc < 0)
                _s.Check(false, $"input: render batch ({rc}) {(global::VL.Qt.OffscreenScene.Describe((Native.Result)rc))}");
            _s.Pump(1);
            R.PollEvents();
            if (R.Error.Length > 0)
                _s.Check(false, $"input: scene error: {R.Error}");
        }

        /// <summary>Events in frame 1, pulled by the nodes in frame 2.</summary>
        public void Frames(int count = 3) { for (int i = 0; i < count; i++) Frame(); }

        // vvvv notifications, positions in texture pixels.
        public void Move(float x, float y) => R.Input.Notify(new MouseMoveNotification(new Vector2(x, y), s_client, Keys.None, this));
        public void Down(float x, float y, MouseButtons b = MouseButtons.Left) => R.Input.Notify(new MouseDownNotification(new Vector2(x, y), s_client, b, Keys.None, this));
        public void Up(float x, float y, MouseButtons b = MouseButtons.Left) => R.Input.Notify(new MouseUpNotification(new Vector2(x, y), s_client, b, Keys.None, this));
        public void Click(float x, float y) { Move(x, y); Down(x, y); Up(x, y); }
        public void Lost(float x, float y) => R.Input.Notify(new MouseLostNotification(new Vector2(x, y), s_client, Keys.None, this));
        public void Wheel(float x, float y, int delta) => R.Input.Notify(new MouseWheelNotification(new Vector2(x, y), s_client, delta, Keys.None, this));
        public void HWheel(float x, float y, int delta) => R.Input.Notify(new MouseHorizontalWheelNotification(new Vector2(x, y), s_client, delta, Keys.None, this));
        public void Touch(TouchNotificationKind kind, int id, float x, float y)
            => R.Input.Notify(new TouchNotification(kind, new Vector2(x, y), s_client, id, id == 11, new Vector2(10, 10), 7, Keys.None, this));
        public void Key(Keys key)
        {
            R.Input.Notify(new KeyDownNotification(key, Keys.None, this));
            R.Input.Notify(new KeyUpNotification(key, Keys.None, this));
        }
        // Typed characters as vvvv sends them: KeyPress carries the character.
        public void Type(string text)
        {
            foreach (var c in text)
                R.Input.Notify(new KeyPressNotification(c, Keys.None, this));
        }

        /// <summary>The texture as premultiplied B8G8R8A8 rows; null if the readback failed.</summary>
        public byte[]? Pixels()
        {
            var pixels = new byte[W * H * 4];
            return NativeCalls.ReadTexture(_texture, pixels, out _, out _, out _) == pixels.Length ? pixels : null;
        }

        public static string Px(byte[]? pixels, int x, int y)
        {
            if (pixels == null)
                return "read failed";
            int i = (y * W + x) * 4;
            return $"{pixels[i + 2]},{pixels[i + 1]},{pixels[i]},{pixels[i + 3]}";
        }

        public void Dispose()
        {
            if (_device == IntPtr.Zero)
                return;
            foreach (var s in _subs)
                s.Dispose();
            R?.Unbind();
            R?.Dispose();
            _s.Pump(1);
            NativeTest.vlqt_test_com_release(_texture);
            _clickA.Dispose(); _clickMouse.Dispose(); _clickTouch.Dispose(); _clickPen.Dispose();
            _rectA.Dispose(); _rectP.Dispose(); _rectB.Dispose(); _rectC.Dispose();
            _group.Dispose(); _slider.Dispose(); _text.Dispose(); _hover.Dispose(); _wheel.Dispose(); _drag.Dispose(); _pinch.Dispose(); _touchPoints.Dispose();
            NativeTest.vlqt_test_d3d11_release_device(_device, _context);
        }
    }

    // Scenario QtTextureInput: click, hover, wheel, drag, pinch, pen, Slider, TextInput focus.
    /// <summary>Input into an offscreen QtTexture scene: click, hover, wheel, drag, pinch, pen, Slider, TextInput focus.</summary>
    [Test, Category("QtTextureInput")]
    public static void QtTextureInput() => Scenario.Run(RunInput);

    internal static void RunInput(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("QtTextureInput: D3D11 only");
            return;
        }
        s.Info("QtTextureInput: #119 input into an offscreen scene");
        using var rig = new InputRig(s);
        if (!rig.Ok)
            return;
        var r = rig.R;

        rig.Frames();
        var pixels = rig.Pixels();
        s.Check(InputRig.Px(pixels, 60, 60) == "255,0,0,255" && InputRig.Px(pixels, 190, 60) == "0,0,255,255" && InputRig.Px(pixels, 320, 60) == "0,255,0,255"
              && InputRig.Px(pixels, 80, 160) == "255,255,0,255" && InputRig.Px(pixels, 10, 10) == "0,0,0,0",
            $"input: scene rendered (A {InputRig.Px(pixels, 60, 60)}, P {InputRig.Px(pixels, 190, 60)}, B {InputRig.Px(pixels, 320, 60)}, C {InputRig.Px(pixels, 80, 160)}, outside {InputRig.Px(pixels, 10, 10)})");

        InputHover(rig, s);
        InputClick(rig, s);
        InputWheel(rig, s);
        InputDevices(rig, s);
        InputDrag(rig, s);
        InputPinch(rig, s);
        InputSlider(rig, s);
        InputText(rig, s);

        pixels = rig.Pixels();
        if (pixels != null)
            WritePng(System.IO.Path.Combine(s.OutDir, "qttexture_input.png"), pixels, InputRig.W, InputRig.H);
        r.Unbind();
    }

    // Hover enter and leave through mouse moves and MouseLost.
    private static void InputHover(InputRig rig, Scenario s)
    {
        rig.Move(300, 250);
        rig.Frames();
        s.Check(!rig.Hovered.Value, "hover: outside the element -> not hovered");
        rig.Move(50, 50);
        rig.Frames();
        s.Check(rig.Hovered.Value && rig.Hovered.LatestAuthor == "Qt", $"hover: move into A -> Is Hovered, author Qt ({rig.Hovered.Value}, {rig.Hovered.LatestAuthor})");
        rig.Move(60, 70);
        rig.Frames();
        s.Check(rig.Hovered.Value, "hover: still hovered inside A");
        rig.Move(300, 250);
        rig.Frames();
        s.Check(!rig.Hovered.Value, "hover: move out -> not hovered");
        rig.Move(50, 50);
        rig.Frames();
        rig.Lost(50, 50);
        rig.Frames();
        s.Check(!rig.Hovered.Value, "hover: MouseLost while hovered -> not hovered (Leave)");
        rig.Move(300, 250);
        rig.Frames();
    }

    // Press, release, click, double click; clicks outside; right button not accepted.
    private static void InputClick(InputRig rig, Scenario s)
    {
        // Clicks outside every element do nothing.
        rig.Click(300, 250);
        rig.Frames();
        s.Check(rig.Clicks == 0, $"click: outside -> nothing ({rig.Clicks})");

        rig.Move(50, 50);
        rig.Down(50, 50);
        rig.Frames();
        s.Check(rig.Pressed.Value && rig.Pressed.LatestAuthor == "Qt" && rig.Clicks == 0, $"click: press -> Is Pressed, no click yet ({rig.Pressed.Value}, {rig.Clicks})");
        rig.Up(50, 50);
        rig.Frames();
        s.Check(!rig.Pressed.Value && rig.Clicks == 1 && rig.TapCount.Value == 1, $"click: release -> click, Tap Count 1 ({rig.Pressed.Value}, {rig.Clicks}, {rig.TapCount.Value})");

        // A second click on the same spot within the double click interval counts as a double click (Qt synthesizes it).
        rig.Click(50, 50);
        rig.Frames();
        s.Check(rig.Clicks == 2 && rig.TapCount.Value == 2, $"click: second click right after -> Tap Count 2 ({rig.Clicks}, {rig.TapCount.Value})");

        // Right button: Click accepts the left button only.
        Thread.Sleep(700); // > double click interval
        rig.Move(50, 50);
        rig.Down(50, 50, MouseButtons.Right);
        rig.Up(50, 50, MouseButtons.Right);
        rig.Frames();
        s.Check(rig.Clicks == 2 && !rig.Pressed.Value, $"click: right button is not accepted ({rig.Clicks})");

        // Press inside, move out, release outside: no click.
        rig.Move(50, 50);
        rig.Down(50, 50);
        rig.Move(300, 250);
        rig.Up(300, 250);
        rig.Frames();
        s.Check(rig.Clicks == 2 && !rig.Pressed.Value, $"click: released outside -> no click ({rig.Clicks})");
    }

    // The reference for the wheel sign: a real window gets WM_MOUSEWHEEL / WM_MOUSEHWHEEL (+120) sent like the OS does, Qt's
    // Windows plugin turns them into angleDelta, a Wheel node reads that. Null if the message did not reach the node.
    private static void WindowWheelReference(Scenario s, out Vector2? vertical, out Vector2? horizontal)
    {
        vertical = horizontal = null;
#if WINDOWS
        const string title = "VL.Qt QtTextureInput wheel reference";
        // The test windows drop OS input (VLQT_TEST_SYNTHETIC_INPUT, read at window creation): this one must not.
        var synthetic = Environment.GetEnvironmentVariable("VLQT_TEST_SYNTHETIC_INPUT");
        Environment.SetEnvironmentVariable("VLQT_TEST_SYNTHETIC_INPUT", null);
        var w = NativeCalls.CreateWindow(400, 300, title);
        Environment.SetEnvironmentVariable("VLQT_TEST_SYNTHETIC_INPUT", synthetic);
        s.Check(w != SceneHandle.Null, "wheel reference: window_create");
        if (w == SceneHandle.Null)
            return;
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        var buf = new byte[1 << 16];
        using var rect = new RectangleNode(_ => new InputRig.NoMessage());
        using var wheel = new WheelNode(_ => new InputRig.NoMessage());
        var delta = Channel.Create(Vector2.Zero);
        var values = new List<Vector2>();
        using var sub = delta.Subscribe(values.Add);
        values.Clear();
        void Frames(int count = 3)
        {
            for (int i = 0; i < count; i++)
            {
                var e = rect.Update(new Vector2(1, 0.8f), Matrix.Translation(0.2f, 0.2f, 0), RectangleAnchor.TopLeft, new Color4(1, 0, 0, 1), 0,
                    new Color4(0, 0, 0, 1), 0, 1, false, false, wheel.Update(null, delta, null, PointerDevices.All), null, true, out _, out _);
                cmd.Clear();
                rec.Update((Element)e, Space.DIPTopLeft);
                if (cmd.Length > 0)
                    s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "wheel reference: apply " + Native.LastError());
                s.Pump(1);
                int n = NativeCalls.Poll(w, buf);
                rec.FeedbackRouter.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            }
        }
        Frames();
        var hwnd = FindWindowW(null, title);
        s.Check(hwnd != IntPtr.Zero, "wheel reference: window found by title");
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var client) && client.Right > 0)
        {
            // Physical pixels of the point (50, 50) DIP.
            double scale = client.Right / 400.0;
            var pt = new Point32 { X = (int)(50 * scale), Y = (int)(50 * scale) };
            ClientToScreen(hwnd, ref pt);
            var lParam = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            var wParam = (IntPtr)(120 << 16);
            SendMessageW(hwnd, 0x020A, wParam, lParam); // WM_MOUSEWHEEL
            Frames();
            vertical = values.Count > 0 ? values[0] : null;
            values.Clear();
            SendMessageW(hwnd, 0x020E, wParam, lParam); // WM_MOUSEHWHEEL
            Frames();
            horizontal = values.Count > 0 ? values[0] : null;
        }
        cmd.Clear();
        rec.Update(null, Space.DIPTopLeft);
        if (cmd.Length > 0)
            Native.Apply(w, cmd.EndFrame());
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
#endif
    }

#if WINDOWS
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point32 p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect32 r);
#endif

    // Vertical and horizontal wheel through Notify and SendWheel, against what a window delivers for the same OS messages.
    private static void InputWheel(InputRig rig, Scenario s)
    {
        static bool Near(Vector2 a, float x, float y) => Math.Abs(a.X - x) < 1e-3f && Math.Abs(a.Y - y) < 1e-3f;
        string Dump() => string.Join(" ", rig.Wheels);

        WindowWheelReference(s, out var windowV, out var windowH);
        s.Info($"wheel in a window for WM_MOUSEWHEEL +120: {windowV?.ToString() ?? "nothing"}, WM_MOUSEHWHEEL +120: {windowH?.ToString() ?? "nothing"}");
        s.Check(windowV is { } v0 && Near(v0, 0, 1), $"wheel reference: a window turns WM_MOUSEWHEEL +120 into Delta (0, 1) ({windowV})");

        rig.Move(50, 50);
        rig.Frames();
        rig.Wheels.Clear();
        rig.Wheel(50, 50, 120);
        rig.Frames();
        s.Check(rig.Wheels.Count >= 1 && Near(rig.Wheels[0], 0, 1), $"wheel: vvvv MouseWheel +120 (away from the user) -> Delta (0, 1) ({Dump()})");
        s.Check(rig.WheelDelta.LatestAuthor == "Qt", "wheel: author Qt");

        rig.Wheels.Clear();
        rig.Wheel(50, 50, -240);
        rig.Frames();
        s.Check(rig.Wheels.Count >= 1 && Near(rig.Wheels[0], 0, -2), $"wheel: MouseWheel -240 -> Delta (0, -2) ({Dump()})");

        // SendWheel takes Qt's angle delta as it is.
        rig.Wheels.Clear();
        rig.R.SendWheel(new Vector2(50, 50), 120, 0, 0);
        rig.Frames();
        s.Check(rig.Wheels.Count >= 1 && Near(rig.Wheels[0], 1, 0), $"wheel: SendWheel dx +120 -> Delta (1, 0) ({Dump()})");

        // The horizontal wheel notification: no vvvv input source creates one, so there is no window to match; the convention
        // is positive WheelDelta = left, as Qt's angleDelta.x and VL.ImGui (render-texture.md "Input"), passed through unchanged.
        rig.Wheels.Clear();
        rig.HWheel(50, 50, 120);
        rig.Frames();
        s.Check(rig.Wheels.Count >= 1 && Near(rig.Wheels[0], 1, 0),
            $"wheel: MouseHorizontalWheel +120 (left) -> Delta (1, 0), Qt's angleDelta.x positive ({Dump()})");

        rig.Wheels.Clear();
        rig.Wheel(300, 250, 120);
        rig.Frames();
        s.Check(rig.Wheels.Count == 0, $"wheel: outside the element -> nothing ({Dump()})");
    }

    // Mouse, touch and pen are told apart by the device of the pointer event.
    private static void InputDevices(InputRig rig, Scenario s)
    {
        void Counts(string what, int mouse, int touch, int pen)
            => s.Check(rig.MouseClicks == mouse && rig.TouchClicks == touch && rig.PenClicks == pen,
                $"devices: {what} -> clicks mouse/touch/pen {mouse}/{touch}/{pen} ({rig.MouseClicks}/{rig.TouchClicks}/{rig.PenClicks})");

        Thread.Sleep(700);
        rig.Click(190, 60);
        rig.Frames();
        Counts("mouse click", 1, 0, 0);

        Thread.Sleep(700);
        rig.Touch(TouchNotificationKind.TouchDown, 11, 190, 60);
        rig.Touch(TouchNotificationKind.TouchUp, 11, 190, 60);
        rig.Frames();
        Counts("touch tap", 1, 1, 0);

        // vvvv has no pen notification: SendPointer with the pen device. QtNative delivers a pen as a window on Windows gets
        // one (render-texture.md "Input"): tablet events from a stylus, so only the Click node with Devices = Pen takes it.
        Thread.Sleep(700);
        var p = new Vector2(190, 60);
        s.Check(rig.R.SendPointer(Protocol.PointerDevice.Pen, Protocol.Pointer.Press, 3, p, 0, 0) == Native.Result.Ok
              && rig.R.SendPointer(Protocol.PointerDevice.Pen, Protocol.Pointer.Release, 3, p, 0, 0) == Native.Result.Ok, "devices: pen press and release accepted");
        rig.Frames();
        Counts("pen tap (a stylus)", 1, 1, 1);
    }

    // Drag of a rectangle: the element follows the mouse (Scene Bounds and pixels), Delta in units.
    private static void InputDrag(InputRig rig, Scenario s)
    {
        rig.Move(300, 50);
        rig.Frames();
        rig.Down(300, 50);
        for (int i = 1; i <= 5; i++)
        {
            rig.Move(300 - 8 * i, 50 + 5 * i);
            rig.Frame();
        }
        rig.Frames();
        var delta = rig.DragDelta.Value;
        s.Check(rig.Dragging.Value && rig.Dragging.LatestAuthor == "Qt", $"drag: Is Dragging while the button is held ({rig.Dragging.Value})");
        s.Check(Math.Abs(delta.X + 0.4f) < 0.02f && Math.Abs(delta.Y - 0.25f) < 0.02f, $"drag: Delta (-0.4, 0.25) units ({delta})");
        var pixels = rig.Pixels();
        // Moved to (220, 45) .. (340, 125): (230, 115) is green now, (370, 30) is empty.
        s.Check(InputRig.Px(pixels, 230, 115) == "0,255,0,255" && InputRig.Px(pixels, 370, 30) == "0,0,0,0",
            $"drag: element follows in the texture (new place {InputRig.Px(pixels, 230, 115)}, old place {InputRig.Px(pixels, 370, 30)})");
        rig.Up(260, 75);
        rig.Frames();
        s.Check(!rig.Dragging.Value, $"drag: release -> not dragging ({rig.Dragging.Value})");
        pixels = rig.Pixels();
        s.Check(InputRig.Px(pixels, 230, 115) == "0,255,0,255", $"drag: element stays after the release ({InputRig.Px(pixels, 230, 115)})");
    }

    // Two fingers with their own ids: Touch Points reports both, Pinch scales.
    private static void InputPinch(InputRig rig, Scenario s)
    {
        // Fingers 11 and 12 at x = 60 / 100 (y 160), spread to 40 / 120.
        rig.Touch(TouchNotificationKind.TouchDown, 11, 60, 160);
        rig.Touch(TouchNotificationKind.TouchDown, 12, 100, 160);
        rig.Frames();
        var p = rig.Points.Value ?? Spread<TouchPoint>.Empty;
        s.Check(p.Count == 2 && p[0].Id != p[1].Id, $"pinch: two fingers -> two Touch Points with different ids ({p.Count})");
        for (int i = 1; i <= 8; i++)
        {
            rig.Touch(TouchNotificationKind.TouchMove, 11, 60 - 20 * i / 8f, 160);
            rig.Touch(TouchNotificationKind.TouchMove, 12, 100 + 20 * i / 8f, 160);
            rig.Frames(2);
        }
        rig.Frames();
        s.Check(rig.PinchActive.Value && rig.PinchActive.LatestAuthor == "Qt", $"pinch: Is Pinching while two fingers pinch ({rig.PinchActive.Value})");
        s.Check(rig.PinchScale.Value > 1.8f && rig.PinchScale.Value < 2.2f, $"pinch: spread 40 -> 80 px -> Scale about 2 ({rig.PinchScale.Value})");
        s.Check(MathF.Abs(rig.PinchRotation.Value) < 0.05f, $"pinch: no rotation ({rig.PinchRotation.Value})");
        p = rig.Points.Value ?? Spread<TouchPoint>.Empty;
        s.Check(p.Count == 2 && Math.Abs(p[0].Position.X) < 2 && Math.Abs(p[1].Position.X) < 2, $"pinch: Touch Points follow the fingers ({string.Join(" ", p)})");
        rig.Touch(TouchNotificationKind.TouchUp, 11, 40, 160);
        rig.Touch(TouchNotificationKind.TouchUp, 12, 120, 160);
        rig.Frames();
        s.Check(!rig.PinchActive.Value && (rig.Points.Value?.Count ?? 0) == 0, $"pinch: both released -> not active, no points ({rig.PinchActive.Value}, {rig.Points.Value?.Count})");
    }

    // Slider: drag the handle with the mouse.
    private static void InputSlider(InputRig rig, Scenario s)
    {
        rig.Move(300, 250);
        rig.Frames();
        s.Check(rig.SliderValue.Value == 0f, $"slider: starts at 0 ({rig.SliderValue.Value})");
        rig.Move(172, 145);
        rig.Down(172, 145);
        for (int i = 1; i <= 8; i++)
        {
            rig.Move(172 + 12 * i, 145);
            rig.Frame();
        }
        rig.Up(268, 145);
        rig.Frames();
        s.Check(rig.SliderValue.Value > 0.3f && rig.SliderValue.Value < 0.7f && rig.SliderValue.LatestAuthor == "Qt",
            $"slider: drag by 96 px of 220 -> value about 0.45 ({rig.SliderValue.Value}, author {rig.SliderValue.LatestAuthor})");
    }

    // TextInput: a click focuses, keys and text type, Enter commits, Esc and a press outside release the focus.
    private static void InputText(InputRig rig, Scenario s)
    {
        var r = rig.R;
        string Value() => rig.TextValue.Value ?? "";
        // A focusing click at a new spot right of the text each time: two clicks on one spot within the double click interval
        // select a word (and typing then replaces it), and the caret goes to the end of the text.
        int spot = 0;
        void Focus()
        {
            rig.Click(330 + 15 * (spot++ % 4), 185);
            rig.Frames();
        }
        // Typing + Enter into a field that has the focus commits; into one without it nothing arrives.
        int Commit(string typed)
        {
            int before = rig.TextWrites;
            rig.Type(typed);
            rig.Key(Keys.Return);
            rig.Frames();
            return rig.TextWrites - before;
        }

        // Without the focus: typed text is dropped.
        s.Check(Commit("zz") == 0 && Value() == "", $"text: no focus -> keys and text are dropped ({Value()})");

        Focus();
        s.Check(Commit("ab1") == 1 && Value() == "ab1" && rig.TextValue.LatestAuthor == "Qt", $"text: click focuses, typing + Enter commits \"ab1\" ({Value()}, author {rig.TextValue.LatestAuthor})");

        // SendText and SendKey directly, Enter keeps the focus.
        s.Check(r.SendText("xy") == Native.Result.Ok, "text: SendText accepted");
        s.Check(r.SendKey(Protocol.KeyEvent.Press, 0x01000004, 0, false) == Native.Result.Ok && r.SendKey(Protocol.KeyEvent.Release, 0x01000004, 0, false) == Native.Result.Ok,
            "text: SendKey Enter accepted");
        rig.Frames();
        s.Check(Value() == "ab1xy", $"text: SendText + SendKey Enter commit \"ab1xy\", the focus stays ({Value()})");

        // Esc discards the edit and releases the focus.
        rig.Type("99");
        rig.Key(Keys.Escape);
        rig.Frames();
        s.Check(Value() == "ab1xy", $"text: Esc discards the edit ({Value()})");
        s.Check(Commit("5") == 0 && Value() == "ab1xy", $"text: Esc released the focus -> typing is dropped ({Value()})");

        // Press outside the field (empty area of the scene): commits and releases.
        Focus();
        rig.Type("7");
        rig.Click(200, 250);
        rig.Frames();
        s.Check(Value() == "ab1xy7", $"text: press outside commits the edit ({Value()})");
        s.Check(Commit("6") == 0 && Value() == "ab1xy7", $"text: press outside released the focus ({Value()})");

        // Touch tap outside releases as well.
        Focus();
        rig.Type("8");
        rig.Touch(TouchNotificationKind.TouchDown, 21, 200, 250);
        rig.Touch(TouchNotificationKind.TouchUp, 21, 200, 250);
        rig.Frames();
        s.Check(Value() == "ab1xy78" && Commit("6") == 0, $"text: touch tap outside commits and releases ({Value()})");

        // A press outside the texture, and SetFocus(false) (the host window lost the focus).
        Focus();
        rig.Type("9");
        rig.Down(-20, -20);
        rig.Up(-20, -20);
        rig.Frames();
        s.Check(Value() == "ab1xy789" && Commit("6") == 0, $"text: press outside the texture commits and releases ({Value()})");
        Focus();
        s.Check(r.SetFocus(false) == Native.Result.Ok, "text: SetFocus(false) accepted");
        s.Check(Commit("6") == 0, $"text: SetFocus(false) -> typing is dropped ({Value()})");
        s.Check(r.SetFocus(true) == Native.Result.Ok, "text: SetFocus(true) accepted");
        Focus();
        s.Check(Commit("!") == 1 && Value().EndsWith("!"), $"text: focused again by a click ({Value()})");
        // A press inside the focused field keeps the focus.
        Focus();
        s.Check(Commit("?") == 1 && Value().EndsWith("?"), $"text: press inside the field keeps the focus ({Value()})");
    }
}
