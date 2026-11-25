using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// QML-08: a root with `required property` is created with its initial properties. The element is created with Source and
// SET_QML_PROPERTIES in one frame (as the generated nodes do); Qt refuses a required property without a value at creation,
// so the value must reach QQmlComponent::createWithInitialProperties. The value is drawn (width of a bar), a later change
// of it is applied as usual, and a reload creates the root again with the values set so far. Grabs: <OutDir>/qml-required_*.png.
[TestFixture, NonParallelizable]
public static class QmlRequiredPropertyTest
{
    /// <summary>QML-08: required properties are set at creation: no error, the value is drawn.</summary>
    [Test, Category("qml-required-property")]
    public static void QmlRequiredProperty() => Scenario.Run(Run);

    private const uint Q = 10, H = 110;

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "qml-required");
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Bar.qml");
        File.WriteAllText(file, "import QtQuick\n\nRectangle {\n    required property real value\n    required property color tint\n"
            + "    implicitHeight: 100\n    width: value\n    color: tint\n}\n");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt QML required property test");
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
            cmd.Clear();
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Q, Protocol.ElementType.Qml);
        cmd.SetString(Q, Protocol.PropertyId.QmlSource, file);
        cmd.SetI32(Q, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.SetMat4(Q, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(100, 100, 0));
        cmd.Reparent(Q, 0, 1000);
        cmd.Bind(Q, H);
        cmd.BeginQmlProperties(Q, 0);
        cmd.QmlPropertyF32("value", 200);
        cmd.QmlPropertyColor("tint", 1, 0, 0, 1);
        cmd.EndQmlProperties();
        Apply("Bar.qml with its required properties in the first frame");
        s.Pump(3);
        Poll();
        s.Check(Err(H) == "", $"the file loads with required properties ('{Err(H)}')");
        using (var a = s.Grab(w, "qml-required_1.png"))
        {
            s.Check(Near(Px(a, 150, 150), 255, 0, 0), $"the bar is red at x=150 ({Px(a, 150, 150)})");
            s.Check(Near(Px(a, 250, 150), 255, 0, 0), $"the bar is 200 wide, red at x=250 ({Px(a, 250, 150)})");
            s.Check(Near(Px(a, 350, 150), 0x20, 0x20, 0x20), $"nothing right of the bar at x=350 ({Px(a, 350, 150)})");
        }

        // A later value is written as before.
        cmd.BeginQmlProperties(Q, 0);
        cmd.QmlPropertyF32("value", 100);
        cmd.EndQmlProperties();
        Apply("value 100");
        s.Pump(3);
        Poll();
        s.Check(Err(H) == "", $"value change ('{Err(H)}')");
        using (var a = s.Grab(w, "qml-required_2.png"))
            s.Check(Near(Px(a, 250, 150), 0x20, 0x20, 0x20), $"the bar is 100 wide: background at x=250 ({Px(a, 250, 150)})");

        // A reload creates the root again with the values set so far (tint stays red, value stays 100).
        cmd.SetI32(Q, Protocol.PropertyId.QmlReload, 1);
        Apply("reload");
        s.Pump(3);
        Poll();
        s.Check(Err(H) == "", $"reload ('{Err(H)}')");
        using (var a = s.Grab(w, "qml-required_3.png"))
        {
            s.Check(Near(Px(a, 150, 150), 255, 0, 0), $"after the reload the bar is red at x=150 ({Px(a, 150, 150)})");
            s.Check(Near(Px(a, 250, 150), 0x20, 0x20, 0x20), $"after the reload it is still 100 wide ({Px(a, 250, 150)})");
        }

        cmd.Destroy(Q);
        Apply("destroy");
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
}
