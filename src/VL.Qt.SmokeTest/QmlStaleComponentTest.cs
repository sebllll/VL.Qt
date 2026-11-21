using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// QML-06: QQmlEngine caches compiled components by URL, QtNative clears the cache only when the reload counter of an
// existing item changes. Item 1 loads A.qml (red). A.qml is rewritten (blue, a new property `extra`). Item 2 is created
// with the same Source and reload 0, item 1 is not bumped: item 2 must show the file on disk (blue, `extra` known), not
// the cached component. This is what a generated QML node sees when vvvv rebuilds it after a declaration change while the
// old node did not render (disconnected Group, closed renderer window). Grabs: <OutDir>/qml-stale_*.png.
[TestFixture, NonParallelizable]
public static class QmlStaleComponentTest
{
    /// <summary>QML-06: a new item of a file changed on disk gets the new file, not the cached component.</summary>
    [Test, Category("qml-stale-component")]
    [Ignore("QML-06: a new item with reload 0 gets the cached component of a file that changed on disk (fails: item 2 is red, extra unknown)")]
    public static void QmlStaleComponent() => Scenario.Run(Run);

    private const uint Q1 = 10, Q2 = 20, H1 = 110, H2 = 120;

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "qml-stale");
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "A.qml");
        File.WriteAllText(file, "import QtQuick\n\nRectangle {\n    implicitWidth: 100\n    implicitHeight: 100\n    color: \"#ff0000\"\n}\n");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt QML stale component test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var errors = new Dictionary<uint, string>();
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
                if (ev == Protocol.EventId.Error)
                    errors[BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 8)..])] = System.Text.Encoding.UTF8.GetString(b.Slice(off + 12, len - 4));
                off += (8 + len + 3) & ~3;
            }
        }
        string Err(uint h) => errors.TryGetValue(h, out var e) ? e : "";
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        void Qml(uint id, uint handle, float x)
        {
            cmd.Create(id, Protocol.ElementType.Qml);
            cmd.SetString(id, Protocol.PropertyId.QmlSource, file);
            cmd.SetI32(id, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
            cmd.SetMat4(id, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(x, 100, 0));
            cmd.Reparent(id, 0, 1000);
            cmd.Bind(id, handle);
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        Qml(Q1, H1, 100);
        Apply("item 1 (A.qml, red)");
        s.Pump(3);
        Poll();
        s.Check(Err(H1) == "", $"item 1 loads ('{Err(H1)}')");
        using (var a = s.Grab(w, "qml-stale_1.png"))
            s.Check(Near(Px(a, 150, 150), 255, 0, 0), $"item 1 is red ({Px(a, 150, 150)})");

        // The file changes on disk; item 1 is not reloaded (its counter stays 0).
        File.WriteAllText(file, "import QtQuick\n\nRectangle {\n    implicitWidth: 100\n    implicitHeight: 100\n    color: \"#0000ff\"\n    property real extra: 0\n}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
        Qml(Q2, H2, 400);
        cmd.BeginQmlProperties(Q2, 0);
        cmd.UniformF32("extra", 1);
        cmd.EndQmlProperties();
        Apply("item 2 (same Source, reload 0) with the new property extra");
        s.Pump(3);
        Poll();
        s.Check(Err(H2) == "", $"item 2 knows the new property extra (error '{Err(H2)}')");
        using (var a = s.Grab(w, "qml-stale_2.png"))
        {
            var c = Px(a, 450, 150);
            s.Check(Near(c, 0, 0, 255), $"item 2 shows the file on disk: blue, not the cached red ({c})");
        }

        cmd.Destroy(Q1);
        cmd.Destroy(Q2);
        Apply("destroy");
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
}
