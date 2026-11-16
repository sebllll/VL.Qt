using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Reactive;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;

// #321: Touch Points and Pinch nodes at a Rectangle, node level (nodes -> reconciler -> QtNative ->
// SinkRegistry -> pull -> channels). The rectangle covers (100, 100, 200, 100) DIP. The raw QtNative side is HandlerTest
// (scenarios touch-points, pinch). Qt compresses touch moves until the next pump: gestures move in steps with a frame each.
static partial class InteractionTest
{
    // Scenario "touch-points-node": two fingers -> two TouchPoints with stable ids in units, moves, release one by one.
    public static void RunTouchPointsNode(Action<bool, string> check, Action<int> pump)
    {
        using var node = new TouchPointsNode(_ => new NoMessage());
        using var rig = new Rig("touch-points-node", check, pump);
        var points = Channel.Create(Spread<TouchPoint>.Empty);
        int writes = 0;
        using var sub = points.Subscribe(_ => writes++);
        writes = 0;
        bool occlusion = true;
        rig.Chain = () => node.Update(null, points, PointerDevices.TouchScreen | PointerDevices.Pen, occlusion);

        rig.Frames(3);
        check(rig.Handlers() == 1, $"touch-points-node: one handler ({rig.Handlers()})");
        check(writes == 0, $"touch-points-node: nothing written before a touch ({writes})");

        rig.Touch(Protocol.Touch.Press, 1, 150, 130);
        rig.Touch(Protocol.Touch.Press, 2, 250, 170);
        rig.Frames(3);
        var p = points.Value!;
        check(p.Count == 2 && p[0].Id != p[1].Id && p[0].Id < p[1].Id, $"touch-points-node: two points sorted by id ({Describe(p)})");
        check(p.Count == 2 && Near(p[0].Position, 0.5f, 0.3f) && Near(p[1].Position, 1.5f, 0.7f),
            $"touch-points-node: item-local positions in units ({Describe(p)})");
        check(points.LatestAuthor == "Qt", $"touch-points-node: author Qt ({points.LatestAuthor})");
        var ids = p.Select(x => x.Id).ToArray();

        writes = 0;
        rig.Frames(5);
        check(writes == 0, $"touch-points-node: no write at rest ({writes})");

        rig.Touch(Protocol.Touch.Move, 1, 160, 140);
        rig.Frames(3);
        p = points.Value!;
        check(p.Count == 2 && p.Select(x => x.Id).SequenceEqual(ids) && Near(p[0].Position, 0.6f, 0.4f),
            $"touch-points-node: ids stable while moving, position follows ({Describe(p)})");

        rig.Touch(Protocol.Touch.Release, 1, 160, 140);
        rig.Frames(3);
        p = points.Value!;
        check(p.Count == 1 && p[0].Id == ids[1], $"touch-points-node: release one -> the other stays ({Describe(p)})");
        rig.Touch(Protocol.Touch.Release, 2, 250, 170);
        rig.Frames(3);
        check(points.Value!.Count == 0, $"touch-points-node: release all -> empty ({Describe(points.Value!)})");

        // The mouse is not a point with the default Devices.
        rig.Mouse(Protocol.Mouse.Press, 150, 130);
        rig.Frames(3);
        check(points.Value!.Count == 0, $"touch-points-node: mouse ignored by the default Devices ({Describe(points.Value!)})");
        rig.Mouse(Protocol.Mouse.Release, 150, 130);
        rig.Frames(2);
    }

    static string Describe(Spread<TouchPoint> p)
        => "[" + string.Join(" ", p.Select(x => $"{x.Id}:{x.Position.X:F2},{x.Position.Y:F2}")) + "]";

    // Scenario "pinch-node": spread -> Scale > 1, rotate -> Rotation in cycles (clockwise positive), Is Active, the last
    // value stays after the release, the channel's own value is not overwritten before a gesture.
    public static void RunPinchNode(Action<bool, string> check, Action<int> pump)
    {
        using var node = new PinchNode(_ => new NoMessage());
        using var rig = new Rig("pinch-node", check, pump);
        var active = Channel.Create(false);
        var scale = Channel.Create(5f);
        var rotation = Channel.Create(5f);
        var translation = Channel.Create(new Vector2(5, 5));
        rig.Chain = () => node.Update(null, active, scale, rotation, translation, null, 2, 2, PointerDevices.All);

        rig.Frames(3);
        check(rig.Handlers() >= 1, $"pinch-node: handler created ({rig.Handlers()}; QtNative adds an internal point counter)");
        check(scale.Value == 5f && rotation.Value == 5f && translation.Value == new Vector2(5, 5) && !active.Value,
            "pinch-node: channel values untouched before a gesture");

        // Spread: fingers at x = 150 / 250 -> 120 / 280 (y 150).
        rig.Touch(Protocol.Touch.Press, 1, 150, 150);
        rig.Touch(Protocol.Touch.Press, 2, 250, 150);
        rig.Frames(3);
        for (int i = 1; i <= 8; i++)
        {
            rig.Touch(Protocol.Touch.Move, 1, 150 - 30 * i / 8f, 150);
            rig.Touch(Protocol.Touch.Move, 2, 250 + 30 * i / 8f, 150);
            rig.Frames(2);
        }
        rig.Frames(2);
        check(active.Value && active.LatestAuthor == "Qt", $"pinch-node: Is Active while two fingers pinch ({active.Value})");
        check(scale.Value > 1.5f && scale.Value < 1.8f, $"pinch-node: spread 100 -> 160 px -> Scale about 1.6 ({scale.Value})");
        check(MathF.Abs(rotation.Value) < 0.05f, $"pinch-node: no rotation ({rotation.Value})");

        rig.Touch(Protocol.Touch.Release, 1, 120, 150);
        rig.Touch(Protocol.Touch.Release, 2, 280, 150);
        rig.Frames(3);
        float lastScale = scale.Value;
        check(!active.Value && lastScale > 1.5f, $"pinch-node: release -> Is Active false, Scale keeps the last value ({active.Value}, {lastScale})");
        rig.Frames(3);
        check(scale.Value == lastScale, "pinch-node: nothing more at rest");

        // Rotate 90 degrees clockwise (y down) around (200, 150), radius 50: Rotation about +0.25 cycles.
        rig.Touch(Protocol.Touch.Press, 1, 250, 150);
        rig.Touch(Protocol.Touch.Press, 2, 150, 150);
        rig.Frames(3);
        for (int i = 1; i <= 18; i++)
        {
            float a = MathF.PI / 2 * i / 18;
            rig.Touch(Protocol.Touch.Move, 1, 200 + 50 * MathF.Cos(a), 150 + 50 * MathF.Sin(a));
            rig.Touch(Protocol.Touch.Move, 2, 200 - 50 * MathF.Cos(a), 150 - 50 * MathF.Sin(a));
            rig.Frames(2);
        }
        rig.Frames(2);
        check(rotation.Value > 0.15f && rotation.Value < 0.27f, $"pinch-node: rotate 90 deg clockwise -> Rotation about 0.25 cycles ({rotation.Value})");
        check(MathF.Abs(scale.Value - 1) < 0.1f, $"pinch-node: scale about 1 while rotating ({scale.Value})");
        rig.Touch(Protocol.Touch.Release, 1, 200, 200);
        rig.Touch(Protocol.Touch.Release, 2, 200, 100);
        rig.Frames(3);
        check(!active.Value && rotation.Value > 0.15f, $"pinch-node: Rotation kept after the end ({rotation.Value})");
    }
}
