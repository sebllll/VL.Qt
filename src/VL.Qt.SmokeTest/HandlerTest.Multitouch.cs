using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using VL.Qt;

// Touch Points and Pinch handlers (#321), and the Windows press-and-hold
// right click (QT_QUICK_ALLOW_SYNTHETIC_RIGHT_CLICK=0, set by QtNative). Touch via vlqt_test_send_touch: Qt compresses
// touch moves until the next pump, so gestures move in small steps with one Frame per step.
public static partial class HandlerTest
{
    private readonly record struct TouchPoint(uint Id, float X, float Y, float Pressure, float W, float H);

    /// <summary>Points of an EV_TOUCH_POINTS payload; null if the layout is wrong.</summary>
    private static TouchPoint[]? TouchPoints(Rec r)
    {
        var p = r.Payload;
        if (p.Length < 4) return null;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(p);
        if (p.Length != 4 + 24 * n) return null;
        var list = new TouchPoint[n];
        for (int i = 0; i < n; ++i)
        {
            var s = p.AsSpan(4 + 24 * i);
            list[i] = new TouchPoint(BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[8..]), BinaryPrimitives.ReadSingleLittleEndian(s[12..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[16..]), BinaryPrimitives.ReadSingleLittleEndian(s[20..]));
        }
        return list;
    }

    /// <summary>The last EV_TOUCH_POINTS of the sink in the records; null if none.</summary>
    private static TouchPoint[]? LastTouch(List<Rec> recs, uint h)
    {
        var r = recs.LastOrDefault(r => r.Event == Protocol.Ev.TouchPoints && r.Handle == h && r.Index == 0);
        return r.Payload is null ? null : TouchPoints(r) ?? Array.Empty<TouchPoint>();
    }

    private static bool HasPoints(List<Rec> recs, uint h, params (uint Id, float X, float Y)[] expected)
        => LastTouch(recs, h) is { } pts && pts.Length == expected.Length
            && pts.Zip(expected).All(z => z.First.Id == z.Second.Id && Near(z.First.X, z.Second.X) && Near(z.First.Y, z.Second.Y));

    private static string Pts(List<Rec> recs, uint h) => LastTouch(recs, h) is { } pts
        ? $"[{string.Join(" ", pts.Select(p => $"{p.Id}:{p.X},{p.Y} p{p.Pressure} {p.W}x{p.H}"))}]" : "no TOUCH_POINTS";

    // Touch Points (#321): all points of an item with ids and item-local positions, release one by one, parallel to
    // Tap, occlusion by an element on top (on/off), accepted devices, sink change / DESTROY, property errors.
    private static void RunTouchPoints(Scenario s)
    {
        using var h = new Harness("touch-points", s);
        const uint A = 1, C = 2, TP = 10, TA = 11, TC = 12;
        const uint STP = 41, STA = 42, STC = 43;
        h.Rect(A, 100, 100, 300, 300, 0);
        h.Handler(TA, Protocol.Type.TapHandler, A, STA);
        h.Handler(TP, Protocol.Type.TouchPointsHandler, A, STP);
        h.Rect(C, 300, 100, 100, 100, 1); // on top of A's top-right corner
        h.Handler(TC, Protocol.Type.TapHandler, C, STC);
        h.Apply("scene");
        var recs = h.Frame();
        s.Check(h.Count(A) == 2 && None(recs, STP), $"touch-points: two handlers at A, nothing sent without points ({h.Count(A)}; {Dump(recs)})");

        // Property errors.
        h.Cmd.SetI32(TP, Protocol.Prop.Occlusion, 1);
        s.Check(h.ApplyRc() == -7, $"touch-points: OCCLUSION as i32 -> -7 ({Native.LastError()})");
        h.Cmd.SetBool(TA, Protocol.Prop.Occlusion, false);
        s.Check(h.ApplyRc() == -6, $"touch-points: OCCLUSION on a Tap handler -> -6 ({Native.LastError()})");
        h.Cmd.SetI32(TP, Protocol.Prop.MaximumPointCount, 3);
        s.Check(h.ApplyRc() == -6, $"touch-points: MAXIMUM_POINT_COUNT on Touch Points -> -6 ({Native.LastError()})");
        h.Cmd.SetI32(TP, Protocol.Prop.AcceptedButtons, 1);
        s.Check(h.ApplyRc() == -6, $"touch-points: ACCEPTED_BUTTONS on Touch Points -> -6 ({Native.LastError()})");

        // Two fingers: both ids with item-local positions; a move updates; release shrinks the list down to count 0.
        h.Touch(Protocol.Touch.Press, 1, 150, 150);
        h.Touch(Protocol.Touch.Press, 2, 250, 200);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (1, 50, 50), (2, 150, 100)), $"touch-points: two fingers -> ids 1, 2 item-local ({Pts(recs, STP)})");
        var first = LastTouch(recs, STP);
        s.Check(first is { Length: 2 } && first.All(p => p.Pressure >= 0 && p.W >= 0 && p.H >= 0),
            $"touch-points: pressure and size present ({Pts(recs, STP)})");
        h.Touch(Protocol.Touch.Move, 1, 160, 170);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (1, 60, 70), (2, 150, 100)), $"touch-points: move -> updated position ({Pts(recs, STP)})");
        recs = h.Frame();
        s.Check(None(recs, STP), $"touch-points: no change -> nothing ({Dump(recs)})");
        h.Touch(Protocol.Touch.Release, 1, 160, 170);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (2, 150, 100)), $"touch-points: release 1 -> only 2 left ({Pts(recs, STP)})");
        // A point stays with the handler outside the item.
        h.Touch(Protocol.Touch.Move, 2, 50, 30);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (2, -50, -70)), $"touch-points: point outside the item stays ({Pts(recs, STP)})");
        h.Touch(Protocol.Touch.Release, 2, 50, 30);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP) && FindRec(recs, Protocol.Ev.Click, STA) is null,
            $"touch-points: last release -> count 0, no tap (two fingers) ({Pts(recs, STP)}; {Dump(recs)})");

        // Parallel to Tap at the same item: one finger -> point, then tap.
        h.Touch(Protocol.Touch.Press, 3, 200, 300);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (3, 100, 200)) && HasFlag(recs, Protocol.Ev.Press, STA, true),
            $"touch-points: one finger -> point and Tap pressed ({Pts(recs, STP)}; {Dump(recs)})");
        h.Touch(Protocol.Touch.Release, 3, 200, 300);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP) && HasU32(recs, Protocol.Ev.Click, STA, 1), $"touch-points: release -> count 0, CLICK 1 ({Dump(recs)})");

        // Occlusion (default on): C's Tap takes the press, A's Touch Points does not see the finger.
        h.Touch(Protocol.Touch.Press, 4, 350, 150);
        recs = h.Frame();
        s.Check(None(recs, STP) && HasFlag(recs, Protocol.Ev.Press, STC, true), $"touch-points: finger on C (occlusion on) -> not seen ({Dump(recs)})");
        h.Touch(Protocol.Touch.Release, 4, 350, 150);
        recs = h.Frame();
        s.Check(None(recs, STP) && HasU32(recs, Protocol.Ev.Click, STC, 1), $"touch-points: release on C -> C taps, A silent ({Dump(recs)})");
        h.Cmd.SetBool(TP, Protocol.Prop.Occlusion, false);
        h.Apply("occlusion off");
        h.Touch(Protocol.Touch.Press, 5, 350, 150);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (5, 250, 50)), $"touch-points: finger on C (occlusion off) -> seen ({Pts(recs, STP)})");
        h.Touch(Protocol.Touch.Release, 5, 350, 150);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP) && HasU32(recs, Protocol.Ev.Click, STC, 1), $"touch-points: release -> count 0, C still taps ({Dump(recs)})");

        // Devices: Qt's default takes the mouse as point 0; TouchScreen | Stylus | Airbrush (the node's default) does not.
        h.Mouse(Protocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (0, 50, 50)), $"touch-points: default devices -> mouse is point 0 ({Pts(recs, STP)})");
        // Two devices with the same Qt id (mouse/pen always 0, first finger 0, #351): two points, the second gets a free id.
        h.Touch(Protocol.Touch.Press, 0, 250, 200);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (0, 50, 50), (1, 150, 100)), $"touch-points: mouse + finger with id 0 -> two points 0, 1 ({Pts(recs, STP)})");
        h.Touch(Protocol.Touch.Move, 0, 260, 220);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (0, 50, 50), (1, 160, 120)), $"touch-points: finger moves -> only point 1 changes ({Pts(recs, STP)})");
        h.Touch(Protocol.Touch.Release, 0, 260, 220);
        recs = h.Frame();
        s.Check(HasPoints(recs, STP, (0, 50, 50)), $"touch-points: finger up -> mouse point 0 stays ({Pts(recs, STP)})");
        h.Mouse(Protocol.Mouse.Release, 150, 150);
        h.Frame();
        h.Cmd.SetI32(TP, Protocol.Prop.AcceptedDevices, 2 | 16 | 32);
        h.Apply("devices touch + pen");
        h.Mouse(Protocol.Mouse.Press, 150, 150);
        recs = h.Frame();
        s.Check(None(recs, STP), $"touch-points: devices touch + pen -> mouse ignored ({Dump(recs)})");
        h.Mouse(Protocol.Mouse.Release, 150, 150);
        h.Frame();

        // Sink change while a finger lies: the old sink sees count 0, the new one gets the point.
        h.Touch(Protocol.Touch.Press, 6, 150, 150);
        h.Frame();
        h.Cmd.Bind(TP, 44, 0);
        h.Apply("rebind");
        recs = h.Frame();
        s.Check(HasPoints(recs, STP) && HasPoints(recs, 44, (6, 50, 50)), $"touch-points: sink change -> old count 0, new gets the point ({Dump(recs)})");
        // DESTROY while a finger lies: count 0 to the sink.
        h.Cmd.Destroy(TP);
        h.Apply("destroy");
        recs = h.Frame();
        s.Check(HasPoints(recs, 44) && h.Count(A) == 1, $"touch-points: DESTROY -> count 0, handler gone ({Dump(recs)}; {h.Count(A)})");
        h.Touch(Protocol.Touch.Release, 6, 150, 150);
        recs = h.Frame();
        s.Check(None(recs, 44), $"touch-points: release after DESTROY -> silent ({Dump(recs)})");
    }

    private static bool IsPinch(Rec r, uint h) => r.Event == Protocol.Ev.Pinch && r.Handle == h && r.Payload.Length == 24 && r.Index == 0;
    private static string Pinch(Rec r) => $"({r.F(0):0.###}, {r.F(1):0.#}, {r.F(2):0.#}, {r.F(3):0.#}; pivot {r.F(4):0.#}, {r.F(5):0.#})";
    // Where the gesture (scale, rotation, translation about the pivot) takes a point: pivot + t + s R (p - pivot).
    private static (float X, float Y) Apply(Rec r, float x, float y)
    {
        float s = r.F(0), a = r.F(1) * MathF.PI / 180f, dx = x - r.F(4), dy = y - r.F(5);
        return (r.F(4) + r.F(2) + s * (MathF.Cos(a) * dx - MathF.Sin(a) * dy), r.F(5) + r.F(3) + s * (MathF.Sin(a) * dx + MathF.Cos(a) * dy));
    }
    private static bool Sticks(Rec r, (float X, float Y) start, (float X, float Y) now)
    {
        var p = Apply(r, start.X, start.Y);
        return MathF.Abs(p.X - now.X) < 3 && MathF.Abs(p.Y - now.Y) < 3;
    }
    private static string Pinches(List<Rec> recs, uint h) => string.Join(" ", recs.Where(r => IsPinch(r, h)).Select(Pinch));

    /// <summary>
    /// Moves the given fingers linearly from their start to their end positions in steps of at most ~5 px, one Frame per
    /// step; all records in order.
    /// </summary>
    private static List<Rec> Gesture(Harness h, params (int Id, float X0, float Y0, float X1, float Y1)[] fingers)
    {
        float len = fingers.Max(f => MathF.Max(MathF.Abs(f.X1 - f.X0), MathF.Abs(f.Y1 - f.Y0)));
        int steps = Math.Max(10, (int)MathF.Ceiling(len / 5));
        var all = new List<Rec>();
        for (int i = 1; i <= steps; ++i)
        {
            foreach (var f in fingers)
                h.Touch(Protocol.Touch.Move, f.Id, f.X0 + (f.X1 - f.X0) * i / steps, f.Y0 + (f.Y1 - f.Y0) * i / steps);
            all.AddRange(h.Frame());
        }
        return all;
    }

    /// <summary>Two fingers on a circle around (cx, cy) with radius r, from angle a0 to a1 (degrees), one Frame per step.</summary>
    private static List<Rec> Rotate(Harness h, float cx, float cy, float r, float a0, float a1, int steps)
    {
        var all = new List<Rec>();
        for (int i = 1; i <= steps; ++i)
        {
            float a = (a0 + (a1 - a0) * i / steps) * MathF.PI / 180;
            h.Touch(Protocol.Touch.Move, 1, cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
            h.Touch(Protocol.Touch.Move, 2, cx - r * MathF.Cos(a), cy - r * MathF.Sin(a));
            all.AddRange(h.Frame());
        }
        return all;
    }

    /// <summary>Largest step between consecutive EV_PINCH values (scale, rotation, translation length).</summary>
    private static (float Scale, float Rotation, float Translation) MaxStep(List<Rec> recs, uint h)
    {
        var p = recs.Where(r => IsPinch(r, h)).ToList();
        float s = 0, rot = 0, t = 0;
        for (int i = 1; i < p.Count; ++i)
        {
            s = MathF.Max(s, MathF.Abs(p[i].F(0) - p[i - 1].F(0)));
            rot = MathF.Max(rot, MathF.Abs(p[i].F(1) - p[i - 1].F(1)));
            t = MathF.Max(t, MathF.Sqrt(MathF.Pow(p[i].F(2) - p[i - 1].F(2), 2) + MathF.Pow(p[i].F(3) - p[i - 1].F(3), 2)));
        }
        return (s, rot, t);
    }

    private static Rec? LastPinch(List<Rec> recs, uint h) => recs.LastOrDefault(r => IsPinch(r, h)) is { Payload: not null } r ? r : null;

    // Pinch (#321): spread / rotate / shift, values only while active, last value before ACTIVE 0 (never 1/0/0), the
    // item stays (target null), no tap from two fingers, Touch Points in parallel, a third finger pauses the gesture
    // without a jump (down and up), MAXIMUM_POINT_COUNT, sink change, DESTROY, property errors.
    private static void RunPinch(Scenario s)
    {
        using var h = new Harness("pinch", s);
        const uint P = 1, PH = 10, TA = 11, TP = 12;
        const uint SE = 30, SP = 41, STA = 42, STP = 43;
        h.Rect(P, 100, 100, 400, 400, 0);
        h.Cmd.Bind(P, SE, 0);
        h.Handler(TA, Protocol.Type.TapHandler, P, STA);
        h.Handler(PH, Protocol.Type.PinchHandler, P, SP);
        h.Handler(TP, Protocol.Type.TouchPointsHandler, P, STP);
        h.Apply("scene");
        h.Frame();
        var bounds0 = h.Bounds.GetValueOrDefault(SE);
        s.Check(At(h, SE, 100, 100), $"pinch: element at its bounds ({Pos(h, SE)})");

        // Property errors.
        h.Cmd.SetI32(PH, Protocol.Prop.MinimumPointCount, 1);
        s.Check(h.ApplyRc() == -7, $"pinch: MINIMUM_POINT_COUNT 1 -> -7 ({Native.LastError()})");
        h.Cmd.SetBool(PH, Protocol.Prop.MaximumPointCount, true);
        s.Check(h.ApplyRc() == -7, $"pinch: MAXIMUM_POINT_COUNT as bool -> -7 ({Native.LastError()})");
        h.Cmd.SetBool(PH, Protocol.Prop.Occlusion, false);
        s.Check(h.ApplyRc() == -6, $"pinch: OCCLUSION on Pinch -> -6 ({Native.LastError()})");
        h.Cmd.SetI32(PH, Protocol.Prop.AcceptedButtons, 1);
        s.Check(h.ApplyRc() == -6, $"pinch: ACCEPTED_BUTTONS on Pinch -> -6 ({Native.LastError()})");

        // Spread: ACTIVE 1, scale > 1, the item does not move or scale.
        h.Touch(Protocol.Touch.Press, 1, 250, 300);
        h.Touch(Protocol.Touch.Press, 2, 350, 300);
        // Qt 6.12 activates the pinch as soon as the second finger touches (scale 1), not after the drag threshold.
        var recs = h.Frame();
        recs.AddRange(Gesture(h, (1, 250, 300, 200, 300), (2, 350, 300, 400, 300)));
        var last = LastPinch(recs, SP);
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, true) && last is { } l1 && l1.F(0) > 1.5f && MathF.Abs(l1.F(1)) < 1,
            $"pinch: spread -> ACTIVE 1, scale > 1.5 ({Pinches(recs, SP)})");
        s.Check(last is { } lp && MathF.Abs(lp.F(4) - 300) < 3 && MathF.Abs(lp.F(5) - 300) < 3,
            $"pinch: pivot = centroid at the start (300, 300) ({Pinches(recs, SP)})");
        s.Check(last is { } ls && Sticks(ls, (250, 300), (200, 300)) && Sticks(ls, (350, 300), (400, 300)),
            $"pinch: the transform about the pivot takes both start points to the fingers ({Pinches(recs, SP)})");
        s.Check(h.Bounds.GetValueOrDefault(SE) == bounds0, $"pinch: item geometry unchanged ({Pos(h, SE)})");
        s.Check(LastTouch(recs, STP) is { Length: 2 }, $"pinch: Touch Points sees both fingers during the pinch ({Pts(recs, STP)})");
        // Shift both: translation follows the centroid.
        var before = LastPinch(recs, SP);
        recs = Gesture(h, (1, 200, 300, 230, 280), (2, 400, 300, 430, 280));
        last = LastPinch(recs, SP);
        s.Check(before is { } b0 && last is { } l2 && Near(l2.F(0), b0.F(0)) && MathF.Abs(l2.F(2) - b0.F(2) - 30) < 1 && MathF.Abs(l2.F(3) - b0.F(3) + 20) < 1,
            $"pinch: shift (30, -20) -> translation +30, -20, scale stays ({(before is { } bb ? Pinch(bb) : "-")} -> {Pinches(recs, SP)})");
        s.Check(last is { } lShift && Sticks(lShift, (250, 300), (230, 280)) && Sticks(lShift, (350, 300), (430, 280)),
            $"pinch: after the shift the start points still land on the fingers ({Pinches(recs, SP)})");
        // Release one finger: last PINCH before ACTIVE 0 (or none in this frame), never 1/0/0; no tap.
        var lastValue = last;
        h.Touch(Protocol.Touch.Release, 2, 430, 280);
        recs = h.Frame();
        int iActive = recs.FindIndex(r => r.Event == Protocol.Ev.Active && r.Handle == SP && !r.Flag);
        int iPinch = recs.FindLastIndex(r => IsPinch(r, SP));
        s.Check(iActive >= 0 && iPinch < iActive && !recs.Exists(r => IsPinch(r, SP) && Near(r.F(0), 1) && r.F(1) == 0 && r.F(2) == 0 && r.F(3) == 0),
            $"pinch: release -> ACTIVE 0, no PINCH 1/0/0, last PINCH before ACTIVE 0 ({Dump(recs)})");
        h.Touch(Protocol.Touch.Release, 1, 230, 280);
        recs = h.Frame();
        s.Check(None(recs, SP) && FindRec(recs, Protocol.Ev.Click, STA) is null && h.Bounds.GetValueOrDefault(SE) == bounds0,
            $"pinch: both up -> no more pinch events, no tap, item unchanged ({Dump(recs)}; {Pos(h, SE)})");

        // Rotate 90 degrees clockwise (y down): rotation > 0, values from the activation on (spike: ~75).
        h.Touch(Protocol.Touch.Press, 1, 400, 300);
        h.Touch(Protocol.Touch.Press, 2, 200, 300);
        h.Frame();
        recs = Rotate(h, 300, 300, 100, 0, 90, 18);
        last = LastPinch(recs, SP);
        s.Check(last is { } l3 && l3.F(1) > 60 && l3.F(1) < 95 && MathF.Abs(l3.F(0) - 1) < 0.1f,
            $"pinch: rotate 90 -> rotation 60..95, scale ~1 ({Pinches(recs, SP)})");
        h.Touch(Protocol.Touch.Release, 1, 300, 400);
        h.Touch(Protocol.Touch.Release, 2, 300, 200);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, false), $"pinch: rotation ends ({Dump(recs)})");

        // Third finger during a pinch (max 2): the gesture pauses and goes on without a jump, down and up.
        h.Touch(Protocol.Touch.Press, 1, 250, 300);
        h.Touch(Protocol.Touch.Press, 2, 350, 300);
        h.Frame();
        var seq = Gesture(h, (1, 250, 300, 220, 300), (2, 350, 300, 380, 300));
        var paused = LastPinch(seq, SP);
        h.Touch(Protocol.Touch.Press, 3, 300, 450);
        recs = h.Frame();
        seq.AddRange(recs);
        var withThree = Gesture(h, (1, 220, 300, 200, 320), (2, 380, 300, 400, 320), (3, 300, 450, 320, 430));
        seq.AddRange(withThree);
        s.Check(!withThree.Exists(r => r.Handle == SP && r.Event == Protocol.Ev.Active) && LastPinch(withThree, SP) is null,
            $"pinch: three fingers (max 2) -> gesture paused: still active, no PINCH ({Dump(withThree.FindAll(r => r.Handle == SP))})");
        h.Touch(Protocol.Touch.Release, 3, 320, 430);
        recs = h.Frame();
        seq.AddRange(recs);
        var after = Gesture(h, (1, 200, 320, 170, 320), (2, 400, 320, 430, 320));
        seq.AddRange(after);
        var step = MaxStep(seq, SP);
        var resumed = LastPinch(after, SP);
        s.Check(!seq.Exists(r => r.Handle == SP && r.Event == Protocol.Ev.Active && !r.Flag) && resumed is { } rs && paused is { } ps
                && rs.F(0) > ps.F(0) && step.Scale < 0.2f && step.Rotation < 10 && step.Translation < 20,
            $"pinch: third finger down + up -> no ACTIVE 0, resumes from the paused value, max step scale {step.Scale:0.###} rot {step.Rotation:0.#} tr {step.Translation:0.#} ({Pinches(seq, SP)})");
        h.Touch(Protocol.Touch.Release, 1, 170, 320);
        h.Touch(Protocol.Touch.Release, 2, 430, 320);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, false), $"pinch: gesture ends after the third finger ({Dump(recs)})");

        // Three fingers pressed one after the other in one frame, max 2: Qt activates with the first two, the third
        // ends that at once (finger count changed), the gesture stays paused (no PINCH while three lie).
        h.Touch(Protocol.Touch.Press, 1, 250, 300);
        h.Touch(Protocol.Touch.Press, 2, 350, 300);
        h.Touch(Protocol.Touch.Press, 3, 300, 400);
        recs = h.Frame();
        recs.AddRange(Gesture(h, (1, 250, 300, 200, 300), (2, 350, 300, 400, 300), (3, 300, 400, 300, 450)));
        s.Check(!recs.Exists(r => IsPinch(r, SP) && (r.F(0) != 1 || r.F(1) != 0 || r.F(2) != 0 || r.F(3) != 0)),
            $"pinch: three fingers from the start (max 2) -> no pinch values ({Dump(recs.FindAll(r => r.Handle == SP))})");
        h.Touch(Protocol.Touch.Release, 1, 200, 300);
        h.Touch(Protocol.Touch.Release, 2, 400, 300);
        h.Touch(Protocol.Touch.Release, 3, 300, 450);
        h.Frame();
        h.Frame();
        // MAXIMUM_POINT_COUNT 3: three fingers spread -> active, scale > 1.
        h.Cmd.SetI32(PH, Protocol.Prop.MaximumPointCount, 3);
        h.Apply("max 3");
        h.Touch(Protocol.Touch.Press, 1, 250, 300);
        h.Touch(Protocol.Touch.Press, 2, 350, 300);
        h.Touch(Protocol.Touch.Press, 3, 300, 400);
        recs = h.Frame();
        recs.AddRange(Gesture(h, (1, 250, 300, 200, 300), (2, 350, 300, 400, 300), (3, 300, 400, 300, 450)));
        last = LastPinch(recs, SP);
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, true) && last is { } l4 && l4.F(0) > 1.3f,
            $"pinch: max 3, three fingers spread -> ACTIVE 1, scale > 1.3 ({Pinches(recs, SP)})");
        h.Touch(Protocol.Touch.Release, 3, 300, 450);
        h.Touch(Protocol.Touch.Release, 1, 200, 300);
        h.Touch(Protocol.Touch.Release, 2, 400, 300);
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, false), $"pinch: max 3 ends ({Dump(recs)})");

        // Sink change during a pinch: old ACTIVE 0, new ACTIVE 1 + current value; DESTROY: ACTIVE 0.
        h.Touch(Protocol.Touch.Press, 1, 250, 300);
        h.Touch(Protocol.Touch.Press, 2, 350, 300);
        h.Frame();
        Gesture(h, (1, 250, 300, 220, 300), (2, 350, 300, 380, 300));
        h.Cmd.Bind(PH, 44, 0);
        h.Apply("rebind");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.Ev.Active, SP, false) && HasFlag(recs, Protocol.Ev.Active, 44, true) && LastPinch(recs, 44) is not null,
            $"pinch: sink change -> old ACTIVE 0, new ACTIVE 1 + PINCH ({Dump(recs)})");
        h.Cmd.Destroy(PH);
        h.Apply("destroy");
        recs = h.Frame();
        s.Check(HasFlag(recs, Protocol.Ev.Active, 44, false) && h.Count(P) == 2, $"pinch: DESTROY -> ACTIVE 0, Tap + Touch Points stay ({Dump(recs)}; {h.Count(P)})");
        h.Touch(Protocol.Touch.Release, 1, 220, 300);
        h.Touch(Protocol.Touch.Release, 2, 380, 300);
        recs = h.Frame();
        s.Check(None(recs, 44), $"pinch: release after DESTROY -> silent ({Dump(recs)})");
    }

    // Windows press-and-hold (#321): the OS synthesizes a right click from touch. QtNative sets
    // QT_QUICK_ALLOW_SYNTHETIC_RIGHT_CLICK=0 (unless set before the start), so Qt Quick drops it; a real right click taps.
    private static void RunSyntheticRightClick(Scenario s)
    {
        using var h = new Harness("right-click", s);
        const uint A = 1, TA = 10, STA = 41;
        h.Rect(A, 100, 100, 200, 200, 0);
        h.Handler(TA, Protocol.Type.TapHandler, A, STA);
        h.Cmd.SetI32(TA, Protocol.Prop.AcceptedButtons, 2); // right button
        h.Apply("scene");
        h.Frame();
        var preset = Environment.GetEnvironmentVariable("QT_QUICK_ALLOW_SYNTHETIC_RIGHT_CLICK");
        bool dropped = preset is null or "0";
        h.Mouse(Protocol.Mouse.PressRightSystem, 150, 150);
        h.Mouse(Protocol.Mouse.ReleaseRightSystem, 150, 150);
        var recs = h.Frame();
        s.Check(dropped ? None(recs, STA) : HasU32(recs, Protocol.Ev.Click, STA, 1),
            $"right-click: right click synthesized by the system -> {(dropped ? "dropped" : $"taps (variable preset to {preset})")} ({Dump(recs)})");
        h.Mouse(Protocol.Mouse.PressRight, 150, 150);
        h.Mouse(Protocol.Mouse.ReleaseRight, 150, 150);
        recs = h.Frame();
        s.Check(HasU32(recs, Protocol.Ev.Click, STA, 1), $"right-click: real right click -> CLICK 1 ({Dump(recs)})");
    }
}
