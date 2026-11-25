using System;
using System.Buffers.Binary;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Wire;

// NAPP-10 / W4-84: QML animations follow vlqt_app_advance_animations forward; the driver ignores a time below the current
// one (docs/architecture.md, "Timing and animation").
// A QmlElement animates v from 0 to 100 in 10 s (10 per second of the frame clock). The clock runs 2 s, jumps back by 1 s
// and runs forward again: v holds its value at the jump and while the clock is below 2 s (at 1.5 s still held), and goes on
// forward once the clock passes the old 2 s (0.5 s later: about 5 more). A time that is not finite is refused.
[TestFixture, NonParallelizable]
public static class AnimationScrubTest
{
    /// <summary>NAPP-10: a backward jump of the frame clock is ignored by the driver, a QML animation holds; a non-finite time is refused.</summary>
    [Test, Category("animation-scrub")]
    public static void AnimationScrub() => Scenario.Run(Run);

    private const uint Q = 10, HQ = 110;

    private const string Qml =
        "import QtQuick\n\n" +
        "Rectangle {\n" +
        "    implicitWidth: 100\n" +
        "    implicitHeight: 100\n" +
        "    color: \"red\"\n" +
        "    property real v: 0\n" +
        "    NumberAnimation on v { from: 0; to: 100; duration: 10000 }\n" +
        "}\n";

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "animation-scrub");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Scrub.qml");
        File.WriteAllText(file, Qml);

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt animation scrub test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var v = float.NaN;
        var error = "";

        // Last value of v and the last error of the element in everything polled so far.
        void Poll()
        {
            int n = NativeCalls.Poll(w, buf);
            var b = buf.AsSpan(0, Math.Max(n, 0));
            int off = 0;
            while (off + 12 <= b.Length)
            {
                ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 4)..]);
                if (len < 4 || off + 8 + len > b.Length)
                    break;
                uint handle = BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 8)..]);
                var payload = b.Slice(off + 12, len - 4);
                if (handle == HQ && ev == Protocol.EventId.QmlProperty && QmlEventDecoder.TryDecodeProperty(payload, out var name, out var value) && name == "v" && value is float f)
                    v = f;
                else if (handle == HQ && ev == Protocol.EventId.Error)
                    error = System.Text.Encoding.UTF8.GetString(payload);
                off += (8 + len + 3) & ~3;
            }
        }
        var time = QtHost.Time;
        var latest = time;
        // The frame clock to t in steps of 1/60 s (or one jump), one pump and poll per step.
        void RunTo(double t, bool jump)
        {
            do
            {
                time = jump ? t : Math.Min(t, time + 1.0 / 60);
                latest = Math.Max(latest, time);
                Native.vlqt_app_advance_animations(time);
                Native.vlqt_app_process_events_for(null, 0);
                Poll();
            }
            while (time < t - 1e-9);
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Q, Protocol.ElementType.Qml);
        cmd.SetString(Q, Protocol.PropertyId.QmlSource, file);
        cmd.SetI32(Q, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.Reparent(Q, 0, 0);
        cmd.Bind(Q, HQ);
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"create QmlElement ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        RunTo(time + 2.0 / 60, false);
        s.Check(error == "", $"Scrub.qml loads ('{error}')");
        var start = time;
        var v0 = float.IsNaN(v) ? 0 : v;

        RunTo(start + 2, false);
        var v2 = v;
        s.Check(v2 > v0 + 15 && v2 < v0 + 25, $"2 s of the frame clock: v rose by about 20 ({v0} -> {v2})");

        RunTo(start + 1, true);
        var vBack = v;
        s.Info($"after the jump back to 1 s: v = {vBack}");

        RunTo(start + 1.5, false);
        var vForward = v;
        s.Info($"0.5 s forward again (clock at 1.5 s, still below the old 2 s): v = {vForward}");

        RunTo(start + 2.5, false);
        var vPast = v;
        s.Info($"clock at 2.5 s, 0.5 s past the old time: v = {vPast}");

        s.Check(Math.Abs(vBack - v2) < 3, $"the jump back by 1 s is ignored, v holds its value ({v2} -> {vBack})");
        s.Check(Math.Abs(vForward - v2) < 3, $"at 1.5 s the clock is still below the old 2 s: v is still held ({vForward}, expected about {v2})");
        s.Check(Math.Abs(vPast - (v2 + 5)) < 3, $"0.5 s past the old time v goes on forward ({vPast}, expected about {v2 + 5})");

        // A time of a broken clock is refused before it reaches Qt (cast of NaN / infinity to milliseconds is undefined).
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300 })
        {
            var rcBad = Native.vlqt_app_advance_animations(bad);
            s.Check(rcBad == Native.Result.BadArgument, $"vlqt_app_advance_animations({bad}) is refused with BAD_ARGUMENT ({rcBad})");
        }
        s.Check(Native.vlqt_app_advance_animations(time) == Native.Result.Ok, "the clock still advances after a refused time");

        cmd.Reset();
        cmd.Destroy(Q);
        Native.Apply(w, cmd.EndFrame());
        // Later scenarios go on from the latest time used here: their clock never goes back.
        QtHost.Time = latest + 3;
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
}
