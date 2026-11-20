using System;
using System.Buffers.Binary;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Elements;

// NAPP-10 (b): QML animations follow vlqt_driver_advance. docs/architecture.md promises "same tree and time, same output"
// and scrubbing; QUnifiedTimer advances animations only by positive deltas, so a clock that jumps back may not rewind them.
// A QmlElement animates v from 0 to 100 in 10 s (10 per second of the frame clock). The clock runs 2 s, jumps back by 1 s
// and runs forward again by 0.5 s: with scrubbing v goes back by about 10 and then rises again. Seen (W1-15): v keeps its
// value at the jump and then rises with every positive delta from there (it does not wait until the clock passes 2 s).
[TestFixture, NonParallelizable]
public static class AnimationScrubTest
{
    /// <summary>NAPP-10: a backward jump of the frame clock rewinds a QML animation.</summary>
    [Test, Category("animation-scrub")]
    [Ignore("NAPP-10: a backward jump of vlqt_driver_advance does not rewind QML animations, they keep their value and go on forward from it (fails: v 20.17 after the jump back to 1 s, 25.17 at 1.5 s)")]
    public static void AnimationScrub() => Scenario.Run(Run);

    const uint Q = 10, HQ = 110;

    const string Qml =
        "import QtQuick\n\n" +
        "Rectangle {\n" +
        "    implicitWidth: 100\n" +
        "    implicitHeight: 100\n" +
        "    color: \"red\"\n" +
        "    property real v: 0\n" +
        "    NumberAnimation on v { from: 0; to: 100; duration: 10000 }\n" +
        "}\n";

    static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "animation-scrub");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Scrub.qml");
        File.WriteAllText(file, Qml);

        var w = Native.vlqt_window_create(800, 600, "VL.Qt animation scrub test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var v = float.NaN;
        var error = "";

        // Last value of v and the last error of the element in everything polled so far.
        void Poll()
        {
            int n = Native.Poll(w, buf);
            var b = buf.AsSpan(0, Math.Max(n, 0));
            int off = 0;
            while (off + 12 <= b.Length)
            {
                ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(b[(off + 2)..]);
                if (len < 8 || off + 4 + len > b.Length)
                    break;
                uint handle = BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 4)..]);
                var payload = b.Slice(off + 12, len - 8);
                if (handle == HQ && ev == Protocol.Ev.QmlProperty && QmlEvents.TryDecodeProperty(payload, out var name, out var value) && name == "v" && value is float f)
                    v = f;
                else if (handle == HQ && ev == Protocol.Ev.Error)
                    error = System.Text.Encoding.UTF8.GetString(payload);
                off += (4 + len + 3) & ~3;
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
                Native.vlqt_driver_advance(time);
                Native.vlqt_app_process_events();
                Poll();
            }
            while (time < t - 1e-9);
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Q, Protocol.Type.Qml);
        cmd.SetString(Q, Protocol.Prop.QmlSource, file);
        cmd.SetI32(Q, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.Reparent(Q, 0, 0);
        cmd.Bind(Q, HQ, 0);
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
        s.Info($"0.5 s forward again (clock at 1.5 s): v = {vForward}");

        s.Check(Math.Abs(vBack - (v2 - 10)) < 3, $"the jump back by 1 s rewinds v by about 10 ({v2} -> {vBack})");
        s.Check(Math.Abs(vForward - (v2 - 5)) < 3, $"0.5 s forward after the jump: v follows the clock ({vForward}, expected about {v2 - 5})");

        cmd.Reset();
        cmd.Destroy(Q);
        Native.Apply(w, cmd.EndFrame());
        // Later scenarios go on from the latest time used here: their clock never goes back.
        QtHost.Time = latest + 3;
        s.Pump(2);
        Native.vlqt_window_destroy(w);
    }
}
