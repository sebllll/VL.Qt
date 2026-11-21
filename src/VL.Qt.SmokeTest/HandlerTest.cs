using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using VL.Qt.Wire;
using VL.Qt;

// Input handlers as protocol objects: CREATE / REPARENT / SET / BIND / DESTROY / RESET of
// a Hover handler, error codes, EVENT_HOVER / EVENT_POINTER per handler feedback, Text containment mask, id reuse, and the TEST
// export vlqt_test_send_touch (touch tap on a Tap handler clicks, multitouch bookkeeping). Scenarios tap / point / wheel
// (#320): the Tap, Point and Wheel handlers (RunTap, RunPoint, RunWheel).
// Mouse input via vlqt_test_send_mouse; real (OS) pointer input is ignored (VLQT_TEST_SYNTHETIC_INPUT=1, set by QtHost).
[TestFixture, NonParallelizable]
public static partial class HandlerTest
{
    /// <summary>Input handlers as protocol objects: errors, events, id reuse, touch tap.</summary>
    [Test, Category("handlers")]
    public static void Handlers() => Scenario.Run(Run);

    /// <summary>Tap handler.</summary>
    [Test, Category("tap")]
    public static void Tap() => Scenario.Run(RunTap);

    /// <summary>Point handler.</summary>
    [Test, Category("point")]
    public static void PointHandler() => Scenario.Run(RunPoint);

    /// <summary>Point and Tap handlers together.</summary>
    [Test, Category("point-tap")]
    public static void PointTap() => Scenario.Run(RunPointTap);

    /// <summary>Wheel handler.</summary>
    [Test, Category("wheel")]
    public static void WheelHandler() => Scenario.Run(RunWheel);

    /// <summary>Drag handler.</summary>
    [Test, Category("drag")]
    public static void Drag() => Scenario.Run(RunDrag);

    /// <summary>Drag handler inside a row.</summary>
    [Test, Category("drag-row")]
    public static void DragRow() => Scenario.Run(RunDragRow);

    /// <summary>Drag handler on a group.</summary>
    [Test, Category("drag-group")]
    public static void DragGroup() => Scenario.Run(RunDragGroup);

    /// <summary>Nested drag handlers.</summary>
    [Test, Category("drag-nested")]
    public static void DragNested() => Scenario.Run(RunDragNested);

    /// <summary>Multitouch points handler.</summary>
    [Test, Category("touch-points")]
    public static void TouchPoints() => Scenario.Run(RunTouchPoints);

    /// <summary>Pinch handler.</summary>
    [Test, Category("pinch")]
    public static void Pinch() => Scenario.Run(RunPinch);

    /// <summary>Synthetic right click.</summary>
    [Test, Category("right-click")]
    public static void RightClick() => Scenario.Run(RunSyntheticRightClick);

    /// <summary>Frames from an input to the node update that sees it.</summary>
    [Test, Category("input-latency")]
    public static void InputLatency() => Scenario.Run(RunInputLatency);

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload)
    {
        public float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(4 * i));
        public uint U32 => BinaryPrimitives.ReadUInt32LittleEndian(Payload);
        public bool Flag => Payload[0] != 0;
    }

    // u16 event, u16 flags, u32 len, u32 handle, payload, 4-aligned.
    private static List<Rec> Parse(ReadOnlySpan<byte> b, Scenario s)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off < b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length) { s.Check(false, $"handlers: malformed record at {off}"); break; }
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)),
                b.Slice(off + 12, len - 4).ToArray()));
            off += (8 + len + 3) & ~3;
        }
        return list;
    }

    private static bool IsPointerEvent(ushort ev) => ev is Protocol.EventId.Hover or Protocol.EventId.Press or Protocol.EventId.Click or Protocol.EventId.Pointer
        or Protocol.EventId.TapCount or Protocol.EventId.LongPress or Protocol.EventId.Active or Protocol.EventId.Wheel or Protocol.EventId.Drag
        or Protocol.EventId.TouchPoints or Protocol.EventId.Pinch;

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt handler test");
        s.Check(w != SceneHandle.Null, "handlers: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"handlers: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        // One failing command (after optional setup in the same buffer): expected rc and a reason in vlqt_last_error.
        void ExpectApply(Native.Result expected, string what, string reasonPart)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            var err = Native.LastError();
            s.Check(rc == expected && err.Contains(reasonPart, StringComparison.Ordinal), $"handlers: {what} -> {expected} ({rc}: {err})");
            cmd.Reset();
        }
        List<Rec> Frame()
        {
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"handlers: poll ({n})");
            return Parse(buf.AsSpan(0, Math.Max(n, 0)), s).FindAll(r => IsPointerEvent(r.Event));
        }
        void Mouse(int type, float x, float y)
        {
            var rc = NativeTest.vlqt_test_send_mouse(w, type, x, y);
            s.Check(rc == 0, $"handlers: test_send_mouse {type} ({rc})");
        }
        static Rec? Find(List<Rec> recs, ushort ev, uint handle)
        {
            foreach (var r in recs)
                if (r.Event == ev && r.Handle == handle) return r;
            return null;
        }
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));
        bool Flag(List<Rec> recs, ushort ev, uint h, bool v) => Find(recs, ev, h) is { } r && r.Flag == v;
        int Count(uint id) => NativeTest.vlqt_test_handler_count(w, id);

        // A: two hover handlers (H1 bound before the REPARENT, H2 after). B: no handler (transparent).
        const ushort reportPosition = 768; // reserved since #320 (Hover without position): unknown property
        const uint A = 1, B = 2, H1 = 10, H2 = 11, HB = 12;
        const uint S1 = 31, S2 = 32, S2b = 34, SB = 35;
        cmd.Create(A, Protocol.ElementType.Rectangle);
        cmd.SetPlace(A, 100, 100, 200, 100);
        cmd.Reparent(A, 0, 0);
        cmd.Create(B, Protocol.ElementType.Rectangle);
        cmd.SetPlace(B, 400, 100, 100, 100);
        cmd.Reparent(B, 0, 1);
        cmd.Bind(B, SB); // bound element without handler: no pointer events
        cmd.Create(H1, Protocol.ElementType.HoverHandler);
        cmd.Bind(H1, S1);
        cmd.Reparent(H1, A, 7); // index ignored
        cmd.Create(H2, Protocol.ElementType.HoverHandler);
        cmd.Reparent(H2, A, 0);
        cmd.Bind(H2, S2);
        CheckApply("handler scene");
        s.Check(Count(A) == 2 && Count(B) == 0, $"handlers: two handlers at A, none at B ({Count(A)}, {Count(B)})");

        // Error codes and reasons.
        cmd.Create(HB, Protocol.ElementType.HoverHandler);
        CheckApply("create loose handler");
        cmd.Reparent(HB, 0, 0);
        ExpectApply(Native.Result.Invalid, "REPARENT handler to the root", "window root");
        cmd.Reparent(HB, H1, 0);
        ExpectApply(Native.Result.Invalid, "REPARENT handler to a handler", "is an input handler");
        cmd.Reparent(HB, 99, 0);
        ExpectApply(Native.Result.UnknownObject, "REPARENT handler to an unknown id", "unknown parent id");
        cmd.Reparent(H1, B, 0);
        ExpectApply(Native.Result.Invalid, "second REPARENT of an attached handler", "already attached");
        cmd.Reparent(B, H1, 0);
        ExpectApply(Native.Result.Invalid, "REPARENT element to a handler", "is an input handler");
        cmd.SetF32(H1, Protocol.PropertyId.Opacity, 0.5f);
        ExpectApply(Native.Result.UnknownProperty, "element property on a handler", "not supported by input handler");
        cmd.SetBool(H1, reportPosition, true);
        ExpectApply(Native.Result.UnknownProperty, "reserved REPORT_POSITION (768) on a Hover handler", "not supported by input handler");
        cmd.SetI32(H1, Protocol.PropertyId.AcceptedDevices, 1);
        ExpectApply(Native.Result.UnknownProperty, "ACCEPTED_DEVICES on a Hover handler", "not supported by input handler");
        cmd.SetBool(B, reportPosition, true);
        var rcElem = Native.Apply(w, cmd.EndFrame());
        s.Check(rcElem == Native.Result.UnknownProperty, $"handlers: handler property on an element -> UnknownProperty ({rcElem}: {Native.LastError()})");
        cmd.Reset();
        cmd.Create(H1, Protocol.ElementType.HoverHandler);
        ExpectApply(Native.Result.Invalid, "CREATE handler with an existing id", "already exists");
        cmd.Create(HB, Protocol.ElementType.Rectangle);
        ExpectApply(Native.Result.Invalid, "CREATE element with a handler id", "already exists");
        cmd.Destroy(HB); // never attached: frees the id
        CheckApply("destroy loose handler");
        s.Check(Count(B) == 0, "handlers: failed REPARENTs attached nothing to B");

        Mouse(TestProtocol.Mouse.Move, 700, 500); // outside of every item
        s.Pump(2);
        var recs = Frame();
        s.Check(recs.Count == 0, $"handlers: nothing hovered -> no events ({S(recs)})");

        // Hover in: both feedback HOVER 1, no POINTER (positions come from the Point handler).
        Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, S1, true) && Flag(recs, Protocol.EventId.Hover, S2, true),
            $"handlers: two hover handlers at one item -> both HOVER 1 ({S(recs)})");
        s.Check(Find(recs, Protocol.EventId.Pointer, S1) is null && Find(recs, Protocol.EventId.Pointer, S2) is null, $"handlers: hover -> no POINTER ({S(recs)})");
        recs = Frame();
        s.Check(recs.Count == 0, $"handlers: no change -> no records ({S(recs)})");
        Mouse(TestProtocol.Mouse.Move, 160, 170);
        recs = Frame();
        s.Check(recs.Count == 0, $"handlers: move inside -> no records ({S(recs)})");

        // New feedback while hovered: old handle HOVER 0, new handle HOVER 1.
        cmd.Bind(H2, S2b);
        CheckApply("rebind H2");
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, S2, false) && Flag(recs, Protocol.EventId.Hover, S2b, true) && Find(recs, Protocol.EventId.Hover, S1) is null,
            $"handlers: feedback change -> old HOVER 0, new HOVER 1 ({S(recs)})");

        // Over B (no handler): A's feedback HOVER 0, nothing for B.
        Mouse(TestProtocol.Mouse.Move, 450, 150);
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, S1, false) && Flag(recs, Protocol.EventId.Hover, S2b, false) && Find(recs, Protocol.EventId.Hover, SB) is null
            && Find(recs, Protocol.EventId.Pointer, S1) is null, $"handlers: hover out -> HOVER 0, element without handler silent ({S(recs)})");

        // Unbind: no events while hovered; DESTROY of a hovered handler sends HOVER 0.
        cmd.Bind(H1, 0);
        CheckApply("unbind H1");
        Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = Frame();
        s.Check(Find(recs, Protocol.EventId.Hover, S1) is null && Flag(recs, Protocol.EventId.Hover, S2b, true), $"handlers: unbound handler silent ({S(recs)})");
        cmd.Destroy(H2);
        CheckApply("destroy H2");
        recs = Frame();
        s.Check(Count(A) == 1 && Flag(recs, Protocol.EventId.Hover, S2b, false), $"handlers: DESTROY handler -> removed, HOVER 0 ({Count(A)}; {S(recs)})");

        // Text: hit test against the painted text (TextMask) as soon as a handler is attached.
        const uint T = 3, HT = 13, ST = 36;
        cmd.Create(T, Protocol.ElementType.Text);
        cmd.SetPlace(T, 400, 300, 380, 100);
        cmd.SetString(T, Protocol.PropertyId.Text, "Hi");
        cmd.SetF32(T, Protocol.PropertyId.FontSize, 40);
        cmd.SetI32(T, Protocol.PropertyId.HorizontalAlignment, 0x2); // AlignRight
        cmd.Reparent(T, 0, 2);
        cmd.Create(HT, Protocol.ElementType.HoverHandler);
        cmd.Reparent(HT, T, 0);
        cmd.Bind(HT, ST);
        CheckApply("text with hover handler");
        Mouse(TestProtocol.Mouse.Move, 420, 310); // inside the item, left of the right-aligned text
        recs = Frame();
        s.Check(Find(recs, Protocol.EventId.Hover, ST) is null, $"handlers: Text area without text -> not hovered ({S(recs)})");
        Mouse(TestProtocol.Mouse.Move, 765, 315); // on the text
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, ST, true), $"handlers: over the text -> HOVER 1 ({S(recs)})");

        // DESTROY of the element takes its handlers along: ids free again, reusable (also as another kind).
        cmd.Destroy(T);
        cmd.Destroy(A);
        CheckApply("destroy elements with handlers");
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, ST, false), $"handlers: element DESTROY -> handler HOVER 0 ({S(recs)})");
        cmd.Destroy(H1);
        ExpectApply(Native.Result.UnknownObject, "DESTROY of a handler removed with its element", "unknown id");
        cmd.Create(H1, Protocol.ElementType.Rectangle); // handler id now an element
        cmd.SetPlace(H1, 100, 100, 200, 100);
        cmd.Reparent(H1, 0, 0);
        cmd.Create(A, Protocol.ElementType.HoverHandler); // element id now a handler
        cmd.Reparent(A, H1, 0);
        cmd.Bind(A, S1);
        cmd.Create(HT, Protocol.ElementType.HoverHandler);
        cmd.Reparent(HT, H1, 0);
        CheckApply("reuse ids after DESTROY");
        s.Check(Count(H1) == 2, $"handlers: reused ids -> 2 handlers ({Count(H1)})");
        Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, S1, true), $"handlers: handler with reused id reports ({S(recs)})");

        // RESET removes elements, their handlers and loose (never attached) handlers.
        cmd.Create(HB, Protocol.ElementType.HoverHandler);
        cmd.ResetScene();
        CheckApply("reset");
        recs = Frame();
        s.Check(Flag(recs, Protocol.EventId.Hover, S1, false), $"handlers: RESET -> HOVER 0 ({S(recs)})");
        cmd.Create(A, Protocol.ElementType.Rectangle);
        cmd.SetPlace(A, 100, 100, 200, 100);
        cmd.Reparent(A, 0, 0);
        cmd.Create(H1, Protocol.ElementType.HoverHandler);
        cmd.Create(HB, Protocol.ElementType.HoverHandler);
        cmd.Create(HT, Protocol.ElementType.HoverHandler);
        cmd.Reparent(H1, A, 0);
        CheckApply("ids free after RESET");
        s.Check(Count(A) == 1, $"handlers: fresh scene after RESET ({Count(A)})");
        cmd.ResetScene();
        CheckApply("reset 2");
        Frame();

        RunTouch(s, w, cmd, Frame, Find, S);

        Native.vlqt_scene_destroy(w);
        s.Pump(2);
        s.Check(true, "handlers: window destroyed -> no crash");
    }

    // vlqt_test_send_touch: complete QTouchEvent per call; a touch tap on a Tap handler clicks.
    private static void RunTouch(Scenario s, SceneHandle w, CommandWriter cmd, Func<List<Rec>> frame,
        Func<List<Rec>, ushort, uint, Rec?> find, Func<List<Rec>, string> str)
    {
        Native.Result Touch(int type, int id, float x, float y) => NativeTest.vlqt_test_send_touch(w, type, id, x, y);
        const uint E = 1, TE = 2, SE = 40;
        cmd.Create(E, Protocol.ElementType.Rectangle);
        cmd.SetPlace(E, 100, 100, 200, 100);
        cmd.Reparent(E, 0, 0);
        cmd.Create(TE, Protocol.ElementType.TapHandler);
        cmd.Reparent(TE, E, 0);
        cmd.Bind(TE, SE);
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"touch: scene ({rc}) {Native.LastError()}");
        cmd.Reset();
        frame();

        s.Check(NativeTest.vlqt_test_send_touch(SceneHandle.Null, TestProtocol.Touch.Press, 1, 0, 0) == Native.Result.NullHandle, "touch: null window -> NullHandle");
        s.Check(Touch(99, 1, 0, 0) == Native.Result.BadArgument, "touch: unknown type -> BadArgument");
        s.Check(Touch(TestProtocol.Touch.Move, 5, 0, 0) == Native.Result.BadArgument && Touch(TestProtocol.Touch.Release, 5, 0, 0) == Native.Result.BadArgument, "touch: MOVE/RELEASE of an inactive point -> BadArgument");

        // Tap with one finger -> CLICK 1.
        s.Check(Touch(TestProtocol.Touch.Press, 1, 150, 150) == 0, "touch: press");
        s.Check(Touch(TestProtocol.Touch.Press, 1, 150, 150) == Native.Result.BadArgument, "touch: PRESS of an active point -> BadArgument");
        var recs = frame();
        s.Check(find(recs, Protocol.EventId.Press, SE) is { } p && p.Flag, $"touch: press -> PRESS 1 ({str(recs)})");
        s.Check(Touch(TestProtocol.Touch.Release, 1, 151, 150) == 0, "touch: release");
        recs = frame();
        s.Check(find(recs, Protocol.EventId.Click, SE) is { } c && c.U32 == 1, $"touch: tap -> CLICK 1 ({str(recs)})");

        // The same id again (point forgotten after the release), then two fingers.
        s.Check(Touch(TestProtocol.Touch.Press, 1, 150, 150) == 0 && Touch(TestProtocol.Touch.Release, 1, 150, 150) == 0, "touch: second tap, same id");
        recs = frame();
        s.Check(find(recs, Protocol.EventId.Click, SE) is { } c2 && c2.U32 == 1, $"touch: second tap -> CLICK 1 ({str(recs)})");
        s.Check(Touch(TestProtocol.Touch.Press, 1, 150, 150) == 0 && Touch(TestProtocol.Touch.Press, 2, 600, 400) == 0
            && Touch(TestProtocol.Touch.Move, 2, 610, 400) == 0 && Touch(TestProtocol.Touch.Release, 2, 610, 400) == 0
            && Touch(TestProtocol.Touch.Release, 1, 150, 150) == 0, "touch: two points (multitouch) accepted");
        recs = frame();
        s.Check(find(recs, Protocol.EventId.Click, SE) is { } c3 && c3.U32 == 1, $"touch: tap while a second finger is down elsewhere -> CLICK 1 ({str(recs)})");

        cmd.ResetScene();
        rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"touch: reset ({rc})");
        cmd.Reset();
        frame();
    }
﻿
    // One test window with the helpers of the #320 scenarios (tap, point, wheel).
    private sealed class Harness : IDisposable
    {
        private readonly Scenario _s;
        private readonly string _tag;
        private readonly byte[] _buf = new byte[1 << 16];
        public readonly SceneHandle W;
        public readonly CommandWriter Cmd = new();

        public Harness(string tag, Scenario s)
        {
            _tag = tag;
            _s = s;
            W = NativeCalls.CreateWindow(800, 600, $"VL.Qt {tag} test");
            s.Check(W != SceneHandle.Null, $"{tag}: window_create");
            Native.vlqt_window_show(W);
        }

        public void Apply(string what)
        {
            var rc = Native.Apply(W, Cmd.EndFrame());
            _s.Check(rc == 0, $"{_tag}: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            Cmd.Reset();
        }

        public Native.Result ApplyRc()
        {
            var rc = Native.Apply(W, Cmd.EndFrame());
            Cmd.Reset();
            return rc;
        }

        /// <summary>Last EVENT_SCENE_BOUNDS per feedback of bound elements (x, y, w, h), updated by every Frame.</summary>
        public readonly Dictionary<uint, (float X, float Y, float W, float H)> Bounds = new();

        public List<Rec> Frame()
        {
            _s.Pump(1);
            int n = NativeCalls.Poll(W, _buf);
            _s.Check(n >= 0, $"{_tag}: poll ({n})");
            var all = Parse(_buf.AsSpan(0, Math.Max(n, 0)), _s);
            foreach (var r in all)
                if (r.Event == Protocol.EventId.SceneBounds && r.Payload.Length == 16)
                    Bounds[r.Handle] = (r.F(0), r.F(1), r.F(2), r.F(3));
            return all.FindAll(r => IsPointerEvent(r.Event));
        }

        public void Mouse(int type, float x, float y) => _s.Check(NativeTest.vlqt_test_send_mouse(W, type, x, y) == 0, $"{_tag}: test_send_mouse {type}");
        public void Touch(int type, int id, float x, float y) => _s.Check(NativeTest.vlqt_test_send_touch(W, type, id, x, y) == 0, $"{_tag}: test_send_touch {type}");
        public void Wheel(float x, float y, int dx, int dy) => _s.Check(NativeTest.vlqt_test_send_wheel(W, x, y, dx, dy) == 0, $"{_tag}: test_send_wheel");
        public void Click(float x, float y)
        {
            Mouse(TestProtocol.Mouse.Press, x, y);
            Mouse(TestProtocol.Mouse.Release, x, y);
        }
        public int Count(uint id) => NativeTest.vlqt_test_handler_count(W, id);

        /// <summary>Mouse drag from (x, y) by (dx, dy) in four moves (past the drag threshold); one Frame, its records.</summary>
        public List<Rec> Drag(float x, float y, float dx, float dy, bool release = true)
        {
            Mouse(TestProtocol.Mouse.Move, x, y);
            Mouse(TestProtocol.Mouse.Press, x, y);
            for (int i = 1; i <= 4; ++i)
                Mouse(TestProtocol.Mouse.Move, x + dx * i / 4, y + dy * i / 4);
            if (release)
                Mouse(TestProtocol.Mouse.Release, x + dx, y + dy);
            return Frame();
        }

        public void Rect(uint id, float x, float y, float w, float h, int index)
        {
            Cmd.Create(id, Protocol.ElementType.Rectangle);
            Cmd.SetPlace(id, x, y, w, h);
            Cmd.Reparent(id, 0, (uint)index);
        }

        public void Handler(uint id, ushort type, uint owner, uint feedbackId)
        {
            Cmd.Create(id, type);
            Cmd.Reparent(id, owner, 0);
            Cmd.Bind(id, feedbackId);
        }

        public void Dispose()
        {
            Native.vlqt_scene_destroy(W);
            _s.Pump(2);
            _s.Check(true, $"{_tag}: window destroyed -> no crash");
        }
    }

    private static Rec? FindRec(List<Rec> recs, ushort ev, uint handle)
    {
        foreach (var r in recs)
            if (r.Event == ev && r.Handle == handle) return r;
        return null;
    }
    private static bool HasFlag(List<Rec> recs, ushort ev, uint h, bool v) => FindRec(recs, ev, h) is { } r && r.Flag == v;
    private static bool HasU32(List<Rec> recs, ushort ev, uint h, uint v) => FindRec(recs, ev, h) is { } r && r.Payload.Length == 4 && r.U32 == v;
    private static bool HasVec(List<Rec> recs, ushort ev, uint h, float x, float y) => FindRec(recs, ev, h) is { } r && r.Payload.Length == 8
        && r.F(0) == x && r.F(1) == y;
    private static bool None(List<Rec> recs, uint h) => !recs.Exists(r => r.Handle == h);
    private static string Dump(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));

    // Tap handler: PRESS / CLICK / TAP_COUNT / LONG_PRESS, buttons, devices, touch, stacking, feedback change, lifetime.
    private static void RunTap(Scenario s)
    {
        using var h = new Harness("tap", s);
        // A (100..300) with tap TA, B (200..400) on top of A's right half with tap TB.
        const uint A = 1, B = 2, TA = 10, TB = 11;
        const uint SA = 41, SB = 42, SA2 = 43;
        h.Rect(A, 100, 100, 200, 100, 0);
        h.Rect(B, 200, 100, 200, 100, 1);
        h.Cmd.Create(TA, Protocol.ElementType.TapHandler);
        h.Cmd.SetI32(TA, Protocol.PropertyId.AcceptedDevices, 0x7FFFFFFF); // set before the REPARENT: applied on creation
        h.Cmd.Reparent(TA, A, 0);
        h.Cmd.Bind(TA, SA);
        h.Handler(TB, Protocol.ElementType.TapHandler, B, SB);
        h.Apply("scene");
        s.Check(h.Count(A) == 1 && h.Count(B) == 1, $"tap: one handler per item ({h.Count(A)}, {h.Count(B)})");

        // Property errors: buttons on Hover / Wheel, wrong value type.
        h.Cmd.Create(90, Protocol.ElementType.HoverHandler);
        h.Cmd.SetI32(90, Protocol.PropertyId.AcceptedButtons, 1);
        s.Check(h.ApplyRc() == Native.Result.UnknownProperty, $"tap: ACCEPTED_BUTTONS on a Hover handler -> UnknownProperty ({Native.LastError()})");
        h.Cmd.Create(91, Protocol.ElementType.WheelHandler);
        h.Cmd.SetI32(91, Protocol.PropertyId.AcceptedButtons, 1);
        s.Check(h.ApplyRc() == Native.Result.UnknownProperty, $"tap: ACCEPTED_BUTTONS on a Wheel handler -> UnknownProperty ({Native.LastError()})");
        h.Cmd.SetBool(TA, Protocol.PropertyId.AcceptedButtons, true);
        s.Check(h.ApplyRc() == Native.Result.BadArgument, $"tap: ACCEPTED_BUTTONS as bool -> BadArgument ({Native.LastError()})");
        h.Cmd.SetBool(TA, 768, true); // REPORT_POSITION, reserved since #320
        s.Check(h.ApplyRc() == Native.Result.UnknownProperty, $"tap: REPORT_POSITION on a Tap handler -> UnknownProperty ({Native.LastError()})");
        h.Cmd.Destroy(90);
        h.Cmd.Destroy(91);
        h.Apply("destroy loose handlers");

        // Press -> PRESS 1; release -> PRESS 0, CLICK 1, TAP_COUNT 1.
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        h.Frame();
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        var recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA, true) && FindRec(recs, Protocol.EventId.Click, SA) is null, $"tap: press -> PRESS 1 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA, false) && HasU32(recs, Protocol.EventId.Click, SA, 1) && HasU32(recs, Protocol.EventId.TapCount, SA, 1),
            $"tap: release -> PRESS 0, CLICK 1, TAP_COUNT 1 ({Dump(recs)})");
        // Second tap right away: double click.
        h.Click(150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 1) && HasU32(recs, Protocol.EventId.TapCount, SA, 2), $"tap: double click -> CLICK 1, TAP_COUNT 2 ({Dump(recs)})");
        recs = h.Frame();
        s.Check(recs.Count == 0, $"tap: no change -> no records ({Dump(recs)})");
        // The tap count stays until the next tap (manual test): no TAP_COUNT 0 after the double click interval.
        // Also with the mouse moving over the item and leaving it.
        var later = new List<Rec>();
        var until = DateTime.UtcNow.AddSeconds(2);
        for (int i = 0; DateTime.UtcNow < until; i++)
        {
            if (i % 10 == 5)
                h.Mouse(TestProtocol.Mouse.Move, i % 20 == 5 ? 160 : 700, 150);
            later.AddRange(h.Frame());
            System.Threading.Thread.Sleep(20);
        }
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        later.AddRange(h.Frame());
        s.Check(later.FindAll(r => r.Handle == SA).Count == 0, $"tap: 2 s without a tap -> TAP_COUNT stays 2 ({Dump(later)})");
        // Later single tap: TAP_COUNT back to 1; two taps per frame: CLICK delta 2.
        System.Threading.Thread.Sleep(1000); // > double click interval
        h.Click(150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 1) && HasU32(recs, Protocol.EventId.TapCount, SA, 1), $"tap: single tap later -> TAP_COUNT 1 ({Dump(recs)})");
        System.Threading.Thread.Sleep(1000);
        h.Click(150, 150);
        h.Click(150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 2) && HasU32(recs, Protocol.EventId.TapCount, SA, 2), $"tap: two taps per frame -> CLICK 2, TAP_COUNT 2 ({Dump(recs)})");

        // Stacked: only the topmost tap handler clicks.
        h.Click(250, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SB, 1) && FindRec(recs, Protocol.EventId.Click, SA) is null, $"tap: stacked -> only the top item clicks ({Dump(recs)})");

        // Right button: ignored by default, clicks with ACCEPTED_BUTTONS left | right.
        System.Threading.Thread.Sleep(1000);
        h.Mouse(TestProtocol.Mouse.PressRight, 150, 150);
        h.Mouse(TestProtocol.Mouse.ReleaseRight, 150, 150);
        recs = h.Frame();
        s.Check(None(recs, SA), $"tap: right click with the default buttons -> nothing ({Dump(recs)})");
        h.Cmd.SetI32(TA, Protocol.PropertyId.AcceptedButtons, 0x1 | 0x2);
        h.Apply("accepted buttons left | right");
        h.Mouse(TestProtocol.Mouse.PressRight, 150, 150);
        h.Mouse(TestProtocol.Mouse.ReleaseRight, 150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 1), $"tap: right click with ACCEPTED_BUTTONS left | right -> CLICK 1 ({Dump(recs)})");

        // Touch tap clicks; ACCEPTED_DEVICES mouse only: touch ignored, the mouse still clicks.
        h.Touch(TestProtocol.Touch.Press, 1, 150, 150);
        h.Touch(TestProtocol.Touch.Release, 1, 150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 1), $"tap: touch tap -> CLICK 1 ({Dump(recs)})");
        h.Cmd.SetI32(TA, Protocol.PropertyId.AcceptedDevices, 0x1); // QInputDevice::DeviceType::Mouse
        h.Apply("accepted devices mouse");
        h.Touch(TestProtocol.Touch.Press, 1, 150, 150);
        h.Touch(TestProtocol.Touch.Release, 1, 150, 150);
        recs = h.Frame();
        s.Check(FindRec(recs, Protocol.EventId.Click, SA) is null, $"tap: ACCEPTED_DEVICES mouse -> touch tap ignored ({Dump(recs)})");
        h.Click(150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA, 1), $"tap: ACCEPTED_DEVICES mouse -> mouse clicks ({Dump(recs)})");

        // Long press: LONG_PRESS 1 after the threshold.
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        h.Frame();
        System.Threading.Thread.Sleep(1200); // > QStyleHints::mousePressAndHoldInterval (800 ms)
        recs = h.Frame();
        recs.AddRange(h.Frame());
        s.Check(HasU32(recs, Protocol.EventId.LongPress, SA, 1), $"tap: held -> LONG_PRESS 1 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA, false) && FindRec(recs, Protocol.EventId.LongPress, SA) is null,
            $"tap: release after long press -> PRESS 0, no second LONG_PRESS ({Dump(recs)})");

        // New feedback while pressed: old PRESS 0, the running press is cancelled (no PRESS 1 / click for the new feedback).
        System.Threading.Thread.Sleep(1000);
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA, true), $"tap: pressed before the feedback change ({Dump(recs)})");
        h.Cmd.Bind(TA, SA2);
        h.Apply("rebind while pressed");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA, false) && None(recs, SA2), $"tap: feedback change -> old PRESS 0, new silent ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        recs = h.Frame();
        s.Check(FindRec(recs, Protocol.EventId.Click, SA2) is null && None(recs, SA), $"tap: release of the cancelled press -> no click ({Dump(recs)})");
        h.Click(150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SA2, 1), $"tap: next tap after the feedback change clicks ({Dump(recs)})");

        // DESTROY while pressed: PRESS 0, release silent.
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA2, true), $"tap: pressed before DESTROY ({Dump(recs)})");
        h.Cmd.Destroy(TA);
        h.Apply("destroy pressed handler");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Press, SA2, false) && h.Count(A) == 0, $"tap: DESTROY while pressed -> PRESS 0, no handler ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        recs = h.Frame();
        s.Check(recs.Count == 0, $"tap: release after DESTROY -> silent ({Dump(recs)})");

        // Window destroyed while a tap handler is pressed (Dispose).
        h.Mouse(TestProtocol.Mouse.Press, 250, 150);
        h.Frame();
    }

    // Point handler: ACTIVE + POINTER while pressed (mouse, touch), passive grab: clicks reach the Tap handler below.
    private static void RunPoint(Scenario s)
    {
        using var h = new Harness("point", s);
        const uint E = 1, P = 2, TE = 10, PP = 11;
        const uint SE = 41, SP = 42, SP2 = 43;
        h.Rect(E, 100, 100, 200, 100, 0);
        h.Rect(P, 100, 100, 200, 100, 1); // same rectangle, on top
        h.Handler(TE, Protocol.ElementType.TapHandler, E, SE);
        h.Handler(PP, Protocol.ElementType.PointHandler, P, SP);
        h.Apply("scene");
        s.Check(h.Count(P) == 1, $"point: one handler ({h.Count(P)})");

        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        var recs = h.Frame();
        s.Check(None(recs, SP), $"point: hover without button -> not active ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP, true) && HasVec(recs, Protocol.EventId.Pointer, SP, 50, 50), $"point: press -> ACTIVE 1, POINTER (50, 50) ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 160, 170);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Pointer, SP, 60, 70) && FindRec(recs, Protocol.EventId.Active, SP) is null, $"point: drag -> POINTER (60, 70) ({Dump(recs)})");
        recs = h.Frame();
        s.Check(None(recs, SP), $"point: no change -> no records ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 160, 170);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP, false) && FindRec(recs, Protocol.EventId.Pointer, SP) is null, $"point: release -> ACTIVE 0 ({Dump(recs)})");
        s.Check(HasU32(recs, Protocol.EventId.Click, SE, 1), $"point: the click passes through to the Tap handler below ({Dump(recs)})");

        // Touch: a finger on the item.
        h.Touch(TestProtocol.Touch.Press, 1, 120, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP, true) && HasVec(recs, Protocol.EventId.Pointer, SP, 20, 10), $"point: finger -> ACTIVE 1, POINTER (20, 10) ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Move, 1, 130, 110);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Pointer, SP, 30, 10), $"point: finger moves -> POINTER (30, 10) ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Release, 1, 130, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP, false) && HasU32(recs, Protocol.EventId.Click, SE, 1),
            $"point: finger up -> ACTIVE 0, touch tap passes through ({Dump(recs)})");

        // New feedback while active: old ACTIVE 0, new ACTIVE 1 + current POINTER.
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        h.Frame();
        h.Cmd.Bind(PP, SP2);
        h.Apply("rebind while active");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP, false) && HasFlag(recs, Protocol.EventId.Active, SP2, true) && HasVec(recs, Protocol.EventId.Pointer, SP2, 50, 50),
            $"point: feedback change -> old ACTIVE 0, new ACTIVE 1 + POINTER ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        h.Frame();

        // ACCEPTED_BUTTONS right: the left button does nothing, the right one activates.
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedButtons, 0x2);
        h.Apply("accepted buttons right");
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(None(recs, SP2), $"point: ACCEPTED_BUTTONS right -> left press ignored ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 150, 150);
        h.Frame();
        h.Mouse(TestProtocol.Mouse.PressRight, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, true), $"point: ACCEPTED_BUTTONS right -> right press ACTIVE 1 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.ReleaseRight, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false), $"point: right release -> ACTIVE 0 ({Dump(recs)})");

        // ACCEPTED_BUTTONS 0: the mouse is active while it is over the item (hover position, internal
        // HoverHandler), touch while down; switching at runtime leaves no ACTIVE 1 behind.
        h.Mouse(TestProtocol.Mouse.Move, 700, 500);
        h.Frame();
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedButtons, 0);
        h.Apply("accepted buttons none");
        recs = h.Frame();
        s.Check(None(recs, SP2) && h.Count(P) == 2, $"point none: internal hover handler, mouse outside -> nothing ({h.Count(P)}, {Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, true) && HasVec(recs, Protocol.EventId.Pointer, SP2, 50, 50),
            $"point none: mouse over the item without a press -> ACTIVE 1, POINTER (50, 50) ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 160, 170);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Pointer, SP2, 60, 70) && FindRec(recs, Protocol.EventId.Active, SP2) is null,
            $"point none: mouse moves -> POINTER (60, 70) ({Dump(recs)})");
        h.Click(160, 170);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, SE, 1) && FindRec(recs, Protocol.EventId.Active, SP2) is null,
            $"point none: a click passes through, still active ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 700, 500);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false), $"point none: mouse leaves -> ACTIVE 0 ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Press, 1, 120, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, true) && HasVec(recs, Protocol.EventId.Pointer, SP2, 20, 10),
            $"point none: finger -> ACTIVE 1, POINTER (20, 10) ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Release, 1, 120, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false), $"point none: finger up -> ACTIVE 0 ({Dump(recs)})");
        // Runtime switch while the mouse is over the item: Left -> ACTIVE 0 (hover handler removed), None -> ACTIVE 1.
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        h.Frame();
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedButtons, 0x1);
        h.Apply("accepted buttons left while hovered");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false) && h.Count(P) == 1,
            $"point none -> left while hovered: ACTIVE 0, hover handler removed ({h.Count(P)}, {Dump(recs)})");
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedButtons, 0);
        h.Apply("accepted buttons none again");
        h.Mouse(TestProtocol.Mouse.Move, 151, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, true) && HasVec(recs, Protocol.EventId.Pointer, SP2, 51, 50),
            $"point left -> none: hovering mouse active again ({Dump(recs)})");
        // Devices without mouse/touchpad: no hover handler, the mouse does nothing.
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedDevices, 0x2);
        h.Apply("accepted devices touch screen");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false) && h.Count(P) == 1, $"point none + touch only -> ACTIVE 0, no hover handler ({h.Count(P)}, {Dump(recs)})");
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedDevices, 0x7FFFFFFF);
        h.Apply("accepted devices all");
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, true) && h.Count(P) == 2, $"point none + all devices -> active again ({h.Count(P)}, {Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 700, 500);
        h.Frame();
        h.Cmd.SetI32(PP, Protocol.PropertyId.AcceptedButtons, 0x2);
        h.Apply("accepted buttons right again");
        h.Frame();
        // Buttons 0 set before the REPARENT; DESTROY while the hovering mouse makes it active: ACTIVE 0, hover handler gone.
        const uint PN = 12, SPN = 44;
        h.Cmd.Create(PN, Protocol.ElementType.PointHandler);
        h.Cmd.SetI32(PN, Protocol.PropertyId.AcceptedButtons, 0);
        h.Cmd.Reparent(PN, P, 0);
        h.Cmd.Bind(PN, SPN);
        h.Apply("point none created");
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPN, true) && h.Count(P) == 3, $"point none at creation: hover active ({h.Count(P)}, {Dump(recs)})");
        h.Cmd.Destroy(PN);
        h.Apply("destroy point none");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPN, false) && h.Count(P) == 1, $"point none: DESTROY while hovered -> ACTIVE 0 ({h.Count(P)}, {Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 700, 500);
        h.Frame();

        // DESTROY while active: ACTIVE 0.
        h.Mouse(TestProtocol.Mouse.PressRight, 150, 150);
        h.Frame();
        h.Cmd.Destroy(PP);
        h.Apply("destroy active handler");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SP2, false) && h.Count(P) == 0, $"point: DESTROY while active -> ACTIVE 0 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.ReleaseRight, 150, 150);
        h.Frame();
    }

    // Tap + Point at the same item (manual test): both react to the same (default, left) button and to touch. Point
    // alone with the default buttons and with ACCEPTED_BUTTONS left set explicitly. Stacked: the top item still wins the click.
    private static void RunPointTap(Scenario s)
    {
        using var h = new Harness("point-tap", s);
        // X: Tap (created first, like the chain Hover -> Click -> Point) + Point. Y: Point only. Z: Point, SET left.
        // U (under) at 400..600 with a Tap; V on top of U's right half with Tap + Point.
        const uint X = 1, Y = 2, Z = 3, U = 4, V = 5;
        const uint TX = 10, PX = 11, PY = 12, PZ = 13, TU = 14, TV = 15, PV = 16;
        const uint STX = 41, SPX = 42, SPY = 43, SPZ = 44, STU = 45, STV = 46, SPV = 47;
        h.Rect(X, 100, 100, 200, 100, 0);
        h.Rect(Y, 100, 250, 200, 100, 1);
        h.Rect(Z, 100, 400, 200, 100, 2);
        h.Rect(U, 400, 100, 200, 100, 3);
        h.Rect(V, 500, 100, 200, 100, 4);
        h.Handler(TX, Protocol.ElementType.TapHandler, X, STX);
        h.Handler(PX, Protocol.ElementType.PointHandler, X, SPX);
        h.Handler(PY, Protocol.ElementType.PointHandler, Y, SPY);
        h.Cmd.Create(PZ, Protocol.ElementType.PointHandler);
        h.Cmd.SetI32(PZ, Protocol.PropertyId.AcceptedButtons, 0x1);
        h.Cmd.Reparent(PZ, Z, 0);
        h.Cmd.Bind(PZ, SPZ);
        h.Handler(TU, Protocol.ElementType.TapHandler, U, STU);
        h.Handler(TV, Protocol.ElementType.TapHandler, V, STV);
        h.Handler(PV, Protocol.ElementType.PointHandler, V, SPV);
        h.Apply("scene");

        // Mouse: press / drag / release on X -> Point active with the position, Tap presses and clicks.
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        h.Frame();
        h.Mouse(TestProtocol.Mouse.Press, 150, 150);
        var recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPX, true) && HasVec(recs, Protocol.EventId.Pointer, SPX, 50, 50),
            $"point-tap: left press -> Point ACTIVE 1, POINTER (50, 50) ({Dump(recs)})");
        s.Check(HasFlag(recs, Protocol.EventId.Press, STX, true), $"point-tap: left press -> Tap PRESS 1 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Move, 160, 170);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Pointer, SPX, 60, 70), $"point-tap: drag -> POINTER (60, 70) ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 160, 170);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPX, false) && HasU32(recs, Protocol.EventId.Click, STX, 1) && HasFlag(recs, Protocol.EventId.Press, STX, false),
            $"point-tap: release -> Point ACTIVE 0, Tap PRESS 0 + CLICK 1 ({Dump(recs)})");

        // Touch on X: the same.
        System.Threading.Thread.Sleep(600); // no double tap
        h.Touch(TestProtocol.Touch.Press, 1, 120, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPX, true) && HasVec(recs, Protocol.EventId.Pointer, SPX, 20, 10) && HasFlag(recs, Protocol.EventId.Press, STX, true),
            $"point-tap: finger -> Point ACTIVE 1, POINTER (20, 10), Tap PRESS 1 ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Move, 1, 130, 110);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Pointer, SPX, 30, 10), $"point-tap: finger moves -> POINTER (30, 10) ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Release, 1, 130, 110);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPX, false) && HasU32(recs, Protocol.EventId.Click, STX, 1),
            $"point-tap: finger up -> Point ACTIVE 0, Tap CLICK 1 ({Dump(recs)})");

        // Reverse order at R: Point created before the Tap (+ Hover).
        const uint R = 6, PR = 17, TR = 18, HR = 19, SPR = 48, STR = 49, SHR = 50;
        h.Rect(R, 400, 400, 200, 100, 5);
        h.Handler(PR, Protocol.ElementType.PointHandler, R, SPR);
        h.Handler(TR, Protocol.ElementType.TapHandler, R, STR);
        h.Handler(HR, Protocol.ElementType.HoverHandler, R, SHR);
        h.Apply("point before tap");
        h.Mouse(TestProtocol.Mouse.Move, 450, 450);
        h.Frame();
        h.Mouse(TestProtocol.Mouse.Press, 450, 450);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPR, true) && HasFlag(recs, Protocol.EventId.Press, STR, true),
            $"point-tap: Point before Tap -> left press: ACTIVE 1, PRESS 1 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 450, 450);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPR, false) && HasU32(recs, Protocol.EventId.Click, STR, 1),
            $"point-tap: Point before Tap -> release: ACTIVE 0, CLICK 1 ({Dump(recs)})");

        // Point alone, default buttons (Y) and SET left (Z).
        foreach (var (y, feedbackId, name) in new[] { (300f, SPY, "default buttons"), (450f, SPZ, "SET left") })
        {
            h.Mouse(TestProtocol.Mouse.Press, 150, y);
            recs = h.Frame();
            s.Check(HasFlag(recs, Protocol.EventId.Active, feedbackId, true) && HasVec(recs, Protocol.EventId.Pointer, feedbackId, 50, 50),
                $"point-tap: Point alone, {name}: left press -> ACTIVE 1, POINTER (50, 50) ({Dump(recs)})");
            h.Mouse(TestProtocol.Mouse.Release, 150, y);
            recs = h.Frame();
            s.Check(HasFlag(recs, Protocol.EventId.Active, feedbackId, false), $"point-tap: Point alone, {name}: release -> ACTIVE 0 ({Dump(recs)})");
        }

        // Stacked: V (Tap + Point) over U (Tap). The top item clicks and its Point is active, U does not click.
        h.Mouse(TestProtocol.Mouse.Press, 550, 150);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SPV, true) && HasFlag(recs, Protocol.EventId.Press, STV, true) && FindRec(recs, Protocol.EventId.Press, STU) is null,
            $"point-tap: stacked press -> top Point ACTIVE 1 + Tap PRESS 1, lower Tap not pressed ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 550, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, STV, 1) && FindRec(recs, Protocol.EventId.Click, STU) is null && HasFlag(recs, Protocol.EventId.Active, SPV, false),
            $"point-tap: stacked -> only the top item clicks ({Dump(recs)})");
        System.Threading.Thread.Sleep(600);
        h.Touch(TestProtocol.Touch.Press, 1, 550, 150);
        h.Touch(TestProtocol.Touch.Release, 1, 550, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, STV, 1) && FindRec(recs, Protocol.EventId.Click, STU) is null,
            $"point-tap: stacked touch -> only the top item clicks ({Dump(recs)})");
        // The same without Point handlers.
        const uint U2 = 7, V2 = 8, TU2 = 20, TV2 = 21, STU2 = 51, STV2 = 52;
        h.Rect(U2, 400, 250, 200, 100, 6);
        h.Rect(V2, 500, 250, 200, 100, 7);
        h.Handler(TU2, Protocol.ElementType.TapHandler, U2, STU2);
        h.Handler(TV2, Protocol.ElementType.TapHandler, V2, STV2);
        h.Apply("stacked taps");
        System.Threading.Thread.Sleep(600);
        h.Touch(TestProtocol.Touch.Press, 1, 550, 300);
        h.Touch(TestProtocol.Touch.Release, 1, 550, 300);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.EventId.Click, STV2, 1) && FindRec(recs, Protocol.EventId.Click, STU2) is null,
            $"point-tap: stacked touch without Point -> only the top item clicks ({Dump(recs)})");
    }

    // Wheel handler: notches (angleDelta / 120) summed per poll, then once (0, 0); devices; feedback change.
    private static void RunWheel(Scenario s)
    {
        using var h = new Harness("wheel", s);
        const uint R = 1, WR = 10;
        const uint SW = 41, SW2 = 42;
        h.Rect(R, 100, 100, 200, 100, 0);
        h.Handler(WR, Protocol.ElementType.WheelHandler, R, SW);
        h.Apply("scene");
        s.Check(h.Count(R) == 1, $"wheel: one handler ({h.Count(R)})");
        h.Mouse(TestProtocol.Mouse.Move, 150, 150);
        h.Frame();

        h.Wheel(150, 150, 0, 120);
        var recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW, 0, 1), $"wheel: one notch -> WHEEL (0, 1) ({Dump(recs)})");
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW, 0, 0), $"wheel: next poll -> once (0, 0) ({Dump(recs)})");
        recs = h.Frame();
        s.Check(recs.Count == 0, $"wheel: then silent ({Dump(recs)})");
        h.Wheel(150, 150, 60, -120);
        h.Wheel(150, 150, 0, -120);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW, 0.5f, -2) && recs.Count == 1, $"wheel: sum per poll -> WHEEL (0.5, -2) ({Dump(recs)})");
        h.Wheel(150, 150, 0, 120);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW, 0, 1), $"wheel: motion right after motion -> no (0, 0) in between ({Dump(recs)})");
        h.Frame();

        h.Wheel(500, 400, 0, 120);
        recs = h.Frame();
        s.Check(recs.Count == 0, $"wheel: outside the item -> nothing ({Dump(recs)})");

        // Feedback change: unpolled motion is dropped, the old feedback gets (0, 0) after motion.
        h.Wheel(150, 150, 0, 120);
        h.Cmd.Bind(WR, SW2);
        h.Apply("rebind");
        recs = h.Frame();
        s.Check(recs.Count == 0, $"wheel: motion before the feedback change is dropped ({Dump(recs)})");
        h.Wheel(150, 150, 0, 240);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW2, 0, 2), $"wheel: new feedback -> WHEEL (0, 2) ({Dump(recs)})");
        h.Cmd.Bind(WR, SW);
        h.Apply("rebind after motion");
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW2, 0, 0) && None(recs, SW), $"wheel: feedback change after motion -> old (0, 0) ({Dump(recs)})");

        // ACCEPTED_DEVICES touchscreen only: the mouse wheel is ignored.
        h.Cmd.SetI32(WR, Protocol.PropertyId.AcceptedDevices, 0x2);
        h.Apply("accepted devices touchscreen");
        h.Wheel(150, 150, 0, 120);
        recs = h.Frame();
        s.Check(recs.Count == 0, $"wheel: ACCEPTED_DEVICES touchscreen -> mouse wheel ignored ({Dump(recs)})");
        h.Cmd.SetI32(WR, Protocol.PropertyId.AcceptedDevices, 0x1 | 0x4);
        h.Apply("accepted devices mouse | touchpad");
        h.Wheel(150, 150, 0, 120);
        recs = h.Frame();
        s.Check(HasVec(recs, Protocol.EventId.Wheel, SW, 0, 1), $"wheel: ACCEPTED_DEVICES mouse -> WHEEL (0, 1) ({Dump(recs)})");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
    private static bool At(Harness h, uint feedbackId, float x, float y) => h.Bounds.TryGetValue(feedbackId, out var b) && Near(b.X, x) && Near(b.Y, y);
    private static string Pos(Harness h, uint feedbackId) => h.Bounds.TryGetValue(feedbackId, out var b) ? $"{b.X}, {b.Y}, {b.W}, {b.H}" : "no bounds";
    private static bool HasDrag(List<Rec> recs, uint h, float x, float y) => FindRec(recs, Protocol.EventId.Drag, h) is { } r && r.Payload.Length == 8
        && Near(r.F(0), x) && Near(r.F(1), y);

    // Row-major like Stride: rotation by 90 degrees around z.
    // Rotation by 90 degrees around the center of the 100 x 100 rectangles R (150, 150) / R2 (450, 150), anchor Center.
    private static readonly float[] s_rot90At150 = { 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 150, 150, 0, 1 };
    private static readonly float[] s_rot90At450 = { 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 450, 150, 0, 1 };

    // Drag handler: offset in the item's transform, EVENT_ACTIVE / EVENT_DRAG, SET TRANSFORM resets, mouse + touch,
    // TRANSFORM before/after, axis, limits, MOVES_ELEMENT off, property errors.
    private static void RunDrag(Scenario s)
    {
        using var h = new Harness("drag", s);
        const uint R = 1, R2 = 2, D = 10, D2 = 11;
        const uint SR = 31, SR2 = 32, SD = 41, SD2 = 42;
        h.Rect(R, 100, 100, 100, 100, 0);
        h.Cmd.Bind(R, SR);
        h.Handler(D, Protocol.ElementType.DragHandler, R, SD);
        h.Rect(R2, 400, 100, 100, 100, 1);
        h.Cmd.Bind(R2, SR2);
        h.Handler(D2, Protocol.ElementType.DragHandler, R2, SD2);
        h.Apply("scene");
        h.Frame();
        s.Check(h.Count(R) == 1 && At(h, SR, 100, 100), $"drag: one handler, item at its bounds ({h.Count(R)}; {Pos(h, SR)})");

        // Property errors.
        h.Cmd.SetI32(D, Protocol.PropertyId.DragAxis, 3);
        s.Check(h.ApplyRc() == Native.Result.BadArgument, $"drag: DRAG_AXIS 3 -> BadArgument ({Native.LastError()})");
        h.Cmd.SetRect(D, Protocol.PropertyId.DragLimits, 10, 0, -10, 0);
        s.Check(h.ApplyRc() == Native.Result.BadArgument, $"drag: DRAG_LIMITS min > max -> BadArgument ({Native.LastError()})");
        h.Cmd.SetI32(D, Protocol.PropertyId.MovesElement, 1);
        s.Check(h.ApplyRc() == Native.Result.BadArgument, $"drag: MOVES_ELEMENT as i32 -> BadArgument ({Native.LastError()})");
        h.Cmd.Create(90, Protocol.ElementType.TapHandler);
        h.Cmd.SetBool(90, Protocol.PropertyId.MovesElement, false);
        s.Check(h.ApplyRc() == Native.Result.UnknownProperty, $"drag: MOVES_ELEMENT on a Tap handler -> UnknownProperty ({Native.LastError()})");
        h.Cmd.Destroy(90);
        h.Apply("destroy loose handler");

        // Mouse drag: ACTIVE 1 + DRAG while dragging, the item follows, SCENE_BOUNDS shows it.
        var recs = h.Drag(150, 150, 40, 20, release: false);
        s.Check(HasFlag(recs, Protocol.EventId.Active, SD, true) && HasDrag(recs, SD, 40, 20) && At(h, SR, 140, 120),
            $"drag: mouse drag (40, 20) -> ACTIVE 1, DRAG (40, 20), item at 140, 120 ({Dump(recs)}; {Pos(h, SR)})");
        h.Mouse(TestProtocol.Mouse.Release, 190, 170);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SD, false) && FindRec(recs, Protocol.EventId.Drag, SD) is null && At(h, SR, 140, 120),
            $"drag: release -> ACTIVE 0, DRAG stays, item stays ({Dump(recs)}; {Pos(h, SR)})");
        // A second drag adds to the offset; DRAG is the running drag only.
        recs = h.Drag(160, 130, 30, 0);
        s.Check(HasDrag(recs, SD, 30, 0) && At(h, SR, 170, 120), $"drag: second drag (30, 0) -> item at 170, 120 ({Dump(recs)}; {Pos(h, SR)})");
        // SET TRANSFORM (pin wins): back on the pin position, also with the same value as before.
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Apply("transform again");
        h.Frame();
        s.Check(At(h, SR, 100, 100), $"drag: SET TRANSFORM -> offset 0, item at 100, 100 ({Pos(h, SR)})");

        // Touch drag. Touch moves are compressed until the next pump, and Qt sets the translation from the move after
        // the activation on: one move per frame, past the target and back to it.
        h.Touch(TestProtocol.Touch.Press, 1, 150, 150);
        h.Touch(TestProtocol.Touch.Move, 1, 210, 180);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SD, true), $"drag: touch drag -> ACTIVE 1 ({Dump(recs)})");
        h.Touch(TestProtocol.Touch.Move, 1, 190, 170);
        recs = h.Frame();
        s.Check(HasDrag(recs, SD, 40, 20) && At(h, SR, 140, 120), $"drag: touch drag (40, 20) -> item at 140, 120 ({Dump(recs)}; {Pos(h, SR)})");
        // SET TRANSFORM during the drag: back to the pin position, the drag goes on from there.
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Apply("transform during drag");
        h.Frame();
        s.Check(At(h, SR, 100, 100), $"drag: SET TRANSFORM during the drag -> item at 100, 100 ({Pos(h, SR)})");
        h.Touch(TestProtocol.Touch.Move, 1, 200, 170);
        h.Touch(TestProtocol.Touch.Release, 1, 200, 170);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, SD, false) && At(h, SR, 110, 100),
            $"drag: drag goes on after SET TRANSFORM -> item at 110, 100 ({Dump(recs)}; {Pos(h, SR)})");
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Apply("reset");

        // TRANSFORM before the drag: the offset moves in window direction, not in the rotated item's.
        h.Cmd.SetI32(R, Protocol.PropertyId.Anchor, GeometryExtensions.AlignCenter);
        h.Cmd.SetMat4(R, Protocol.PropertyId.Transform, s_rot90At150);
        h.Apply("rotate R");
        recs = h.Drag(150, 150, 40, 0);
        s.Check(HasDrag(recs, SD, 40, 0) && At(h, SR, 140, 100), $"drag: rotated item, drag (40, 0) -> item at 140, 100 ({Dump(recs)}; {Pos(h, SR)})");
        // TRANSFORM after the drag (R2): pin wins, the drag offset is reset, the item is back at its pin position.
        h.Drag(450, 150, 40, 0);
        h.Cmd.SetI32(R2, Protocol.PropertyId.Anchor, GeometryExtensions.AlignCenter);
        h.Cmd.SetMat4(R2, Protocol.PropertyId.Transform, s_rot90At450);
        h.Apply("rotate R2 after its drag");
        h.Frame();
        s.Check(At(h, SR2, 400, 100), $"drag: TRANSFORM after the drag -> offset reset, item at 400, 100 ({Pos(h, SR2)})");

        // Axis x only.
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Cmd.SetI32(D, Protocol.PropertyId.DragAxis, 1);
        h.Apply("axis x");
        recs = h.Drag(150, 150, 40, 20);
        s.Check(HasDrag(recs, SD, 40, 0) && At(h, SR, 140, 100), $"drag: axis x, drag (40, 20) -> DRAG (40, 0), item at 140, 100 ({Dump(recs)}; {Pos(h, SR)})");
        // Limits of the offset (minX, minY, maxX, maxY), both axes.
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Cmd.SetI32(D, Protocol.PropertyId.DragAxis, 0);
        h.Cmd.SetRect(D, Protocol.PropertyId.DragLimits, -10, -10, 25, float.PositiveInfinity);
        h.Apply("limits");
        recs = h.Drag(150, 150, 40, 20);
        s.Check(HasDrag(recs, SD, 25, 20) && At(h, SR, 125, 120), $"drag: limits maxX 25, drag (40, 20) -> DRAG (25, 20), item at 125, 120 ({Dump(recs)}; {Pos(h, SR)})");
        recs = h.Drag(140, 150, -80, 0);
        s.Check(HasDrag(recs, SD, -35, 0) && At(h, SR, 90, 120), $"drag: limits minX -10, drag (-80, 0) -> DRAG (-35, 0), item at 90, 120 ({Dump(recs)}; {Pos(h, SR)})");

        // MOVES_ELEMENT off: reports, moves nothing.
        h.Cmd.SetPlace(R, 100, 100, 100, 100);
        h.Cmd.SetRect(D, Protocol.PropertyId.DragLimits, float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity);
        h.Cmd.SetBool(D, Protocol.PropertyId.MovesElement, false);
        h.Apply("moves element off");
        recs = h.Drag(150, 150, 40, 20);
        s.Check(HasDrag(recs, SD, 40, 20) && At(h, SR, 100, 100), $"drag: MOVES_ELEMENT off -> DRAG (40, 20), item stays ({Dump(recs)}; {Pos(h, SR)})");

        // Feedback change after a drag: the new feedback starts without a drag value; DESTROY sends ACTIVE 0 while dragging.
        h.Cmd.Bind(D, 43);
        h.Apply("rebind");
        recs = h.Frame();
        s.Check(None(recs, 43), $"drag: new feedback after a drag -> nothing ({Dump(recs)})");
        h.Cmd.SetBool(D, Protocol.PropertyId.MovesElement, true);
        h.Apply("moves element on");
        h.Drag(150, 150, 40, 0, release: false);
        h.Cmd.Destroy(D);
        h.Apply("destroy while dragging");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.EventId.Active, 43, false) && h.Count(R) == 0, $"drag: DESTROY while dragging -> ACTIVE 0 ({Dump(recs)})");
        h.Mouse(TestProtocol.Mouse.Release, 190, 150);
        h.Frame();
        s.Check(At(h, SR, 140, 100), $"drag: offset stays after the handler is gone ({Pos(h, SR)})");
    }

    // Drag of a Row child: the child moves, its siblings stay, the layout keeps its place.
    private static void RunDragRow(Scenario s)
    {
        using var h = new Harness("drag-row", s);
        const uint Row = 1, A = 2, B = 3, C = 4, D = 10;
        const uint SRow = 30, SA = 31, SB = 32, SC = 33, SD = 41;
        h.Cmd.Create(Row, Protocol.ElementType.Row);
        h.Cmd.SetPlace(Row, 100, 100, 0, 0);
        h.Cmd.Reparent(Row, 0, 0);
        h.Cmd.Bind(Row, SRow);
        uint i = 0;
        foreach (var (id, feedbackId) in new[] { (A, SA), (B, SB), (C, SC) })
        {
            h.Cmd.Create(id, Protocol.ElementType.Rectangle);
            h.Cmd.SetPlace(id, 0, 0, 50, 50);
            h.Cmd.Reparent(id, Row, i++);
            h.Cmd.Bind(id, feedbackId);
        }
        h.Handler(D, Protocol.ElementType.DragHandler, B, SD);
        h.Apply("scene");
        h.Frame();
        s.Check(At(h, SA, 100, 100) && At(h, SB, 150, 100) && At(h, SC, 200, 100), $"drag-row: layout ({Pos(h, SA)} / {Pos(h, SB)} / {Pos(h, SC)})");
        var recs = h.Drag(175, 125, 20, 80);
        h.Frame(); // a layout pass after the move would show here
        s.Check(HasDrag(recs, SD, 20, 80) && At(h, SB, 170, 180), $"drag-row: dragged child at 170, 180 ({Dump(recs)}; {Pos(h, SB)})");
        s.Check(At(h, SA, 100, 100) && At(h, SC, 200, 100) && h.Bounds.TryGetValue(SRow, out var rb) && Near(rb.W, 150),
            $"drag-row: siblings stay, row keeps its width 150 ({Pos(h, SA)} / {Pos(h, SC)} / {Pos(h, SRow)})");
        // The row moves (its BOUNDS): the child's offset stays (layout changes do not reset it).
        h.Cmd.SetPlace(Row, 100, 300, 0, 0);
        h.Apply("move row");
        h.Frame();
        s.Check(At(h, SB, 170, 380) && At(h, SA, 100, 300), $"drag-row: row moved -> child keeps its offset ({Pos(h, SB)})");
        // The child's own BOUNDS (same size) resets the offset and the Row keeps the child at its slot.
        h.Cmd.SetPlace(B, 0, 0, 50, 50);
        h.Apply("child bounds");
        h.Frame();
        s.Check(At(h, SB, 150, 300), $"drag-row: child BOUNDS -> offset 0, stays in its slot ({Pos(h, SB)})");
    }

    // Group drag: the group moves as a whole (hit area = its size from the children); a child with its own drag
    // moves alone.
    private static void RunDragGroup(Scenario s)
    {
        using var h = new Harness("drag-group", s);
        const uint G = 1, A = 2, B = 3, DG = 10, DB = 11;
        const uint SG = 30, SA = 31, SB = 32, SDG = 41, SDB = 42;
        h.Cmd.Create(G, Protocol.ElementType.Group);
        h.Cmd.SetPlace(G, 100, 100, 0, 0);
        h.Cmd.Reparent(G, 0, 0);
        h.Cmd.Bind(G, SG);
        h.Cmd.Create(A, Protocol.ElementType.Rectangle);
        h.Cmd.SetPlace(A, 0, 0, 100, 100);
        h.Cmd.Reparent(A, G, 0);
        h.Cmd.Bind(A, SA);
        h.Cmd.Create(B, Protocol.ElementType.Rectangle);
        h.Cmd.SetPlace(B, 150, 50, 50, 50);
        h.Cmd.Reparent(B, G, 1);
        h.Cmd.Bind(B, SB);
        h.Handler(DG, Protocol.ElementType.DragHandler, G, SDG);
        h.Handler(DB, Protocol.ElementType.DragHandler, B, SDB);
        h.Apply("scene");
        h.Frame();
        s.Check(h.Bounds.TryGetValue(SG, out var gb) && Near(gb.W, 200) && Near(gb.H, 100), $"drag-group: group size from children ({Pos(h, SG)})");

        // On A (no handler of its own) and on the free group area: the group moves with its children.
        var recs = h.Drag(150, 150, 30, 20);
        s.Check(HasDrag(recs, SDG, 30, 20) && None(recs, SDB) && At(h, SG, 130, 120) && At(h, SA, 130, 120) && At(h, SB, 280, 170),
            $"drag-group: drag on a child without handler -> group moves ({Dump(recs)}; {Pos(h, SG)} / {Pos(h, SB)})");
        recs = h.Drag(260, 140, -30, -20); // free area between A and B (group-local 130, 20)
        s.Check(HasDrag(recs, SDG, -30, -20) && At(h, SG, 100, 100), $"drag-group: drag on free group area -> group moves back ({Dump(recs)}; {Pos(h, SG)})");
        // On B (own drag): only B moves.
        recs = h.Drag(275, 175, 0, 50);
        s.Check(HasDrag(recs, SDB, 0, 50) && FindRec(recs, Protocol.EventId.Drag, SDG) is null && At(h, SB, 250, 200) && At(h, SG, 100, 100),
            $"drag-group: drag on a child with its own drag -> only the child ({Dump(recs)}; {Pos(h, SB)} / {Pos(h, SG)})");
    }

    // Nested drags: the innermost wins (panel with a slider); a child limited to x lets a vertical drag through to
    // the parent's drag (Qt: more than 45 degrees off the enabled axis).
    private static void RunDragNested(Scenario s)
    {
        using var h = new Harness("drag-nested", s);
        const uint P = 1, K = 2, DP = 10, DK = 11;
        const uint SP = 30, SK = 31, SDP = 41, SDK = 42;
        h.Rect(P, 100, 100, 300, 200, 0);
        h.Cmd.Bind(P, SP);
        h.Cmd.Create(K, Protocol.ElementType.Rectangle);
        h.Cmd.SetPlace(K, 50, 50, 50, 50);
        h.Cmd.Reparent(K, P, 0);
        h.Cmd.Bind(K, SK);
        h.Handler(DP, Protocol.ElementType.DragHandler, P, SDP);
        h.Handler(DK, Protocol.ElementType.DragHandler, K, SDK);
        h.Apply("scene");
        h.Frame();

        var recs = h.Drag(175, 175, 40, 10);
        s.Check(HasDrag(recs, SDK, 40, 10) && FindRec(recs, Protocol.EventId.Drag, SDP) is null && At(h, SK, 190, 160) && At(h, SP, 100, 100),
            $"drag-nested: drag on the child -> only the child ({Dump(recs)}; {Pos(h, SK)} / {Pos(h, SP)})");
        recs = h.Drag(300, 250, 20, 20);
        s.Check(HasDrag(recs, SDP, 20, 20) && FindRec(recs, Protocol.EventId.Drag, SDK) is null && At(h, SP, 120, 120) && At(h, SK, 210, 180),
            $"drag-nested: drag on the parent -> parent with child ({Dump(recs)}; {Pos(h, SP)} / {Pos(h, SK)})");

        // Child x only: a vertical drag on the child goes to the parent, a horizontal one stays with the child.
        h.Cmd.SetPlace(P, 100, 100, 300, 200);
        h.Cmd.SetPlace(K, 50, 50, 50, 50);
        h.Cmd.SetI32(DK, Protocol.PropertyId.DragAxis, 1);
        h.Apply("child x only");
        recs = h.Drag(175, 175, 0, 40);
        s.Check(HasDrag(recs, SDP, 0, 40) && FindRec(recs, Protocol.EventId.Drag, SDK) is null && At(h, SP, 100, 140),
            $"drag-nested: child x only, vertical drag -> parent ({Dump(recs)}; {Pos(h, SP)} / {Pos(h, SK)})");
        recs = h.Drag(175, 215, 40, 0);
        s.Check(HasDrag(recs, SDK, 40, 0) && FindRec(recs, Protocol.EventId.Drag, SDP) is null && At(h, SK, 190, 190),
            $"drag-nested: child x only, horizontal drag -> child ({Dump(recs)}; {Pos(h, SK)})");
    }
}
