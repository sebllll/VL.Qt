using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.IO;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Wire;

// Renderer pin Enable Keyboard Shortcuts (vlqt_window_set_keys): with the keys on (default) F2, F5, Shift+F5, F6 and F7
// work the perf overlay, F11 and Alt+Enter toggle fullscreen, and none of them reaches the scene; with the keys off none of
// them is taken, the overlay and the fullscreen mode stay as they are and every key reaches the scene (assets/KeyProbe.qml
// counts them); on again, the old behaviour is back; the switch survives the window swap of vlqt_window_set_samples. The
// overlay state is the one of vlqt_scene_get_overlay, the fullscreen mode the requested one of vlqt_window_get_mode.
[TestFixture, NonParallelizable]
public static class KeyboardShortcutsTest
{
    /// <summary>The diagnostic and fullscreen keys on and off.</summary>
    [Test, Category("keyshortcuts")]
    public static void KeyboardShortcuts() => Scenario.Run(Run);

    private const int KeyF2 = 0x01000031, KeyF5 = 0x01000034, KeyF6 = 0x01000035, KeyF7 = 0x01000036, KeyF11 = 0x0100003A;
    private const int KeyReturn = 0x01000004, Shift = 0x02000000, Alt = 0x08000000;
    private const int Meter = 1, Stats = 2, Freeze = 8, PageShift = 4;
    private const uint Probe = 10, ProbeHandle = 110;

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Run(Scenario s)
    {
        var qml = FindFile("src", "VL.Qt.SmokeTest", "assets", "KeyProbe.qml");
        s.Check(qml != null, $"keyshortcuts: KeyProbe.qml found ({qml})");
        if (qml == null) return;

        var w = NativeCalls.CreateWindow(400, 300, "VL.Qt keyboard shortcuts test");
        s.Check(w != SceneHandle.Null, "keyshortcuts: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        long count = 0, lastKey = 0; // of KeyProbe.qml, from its EVENT_QML_PROPERTY

        cmd.Clear();
        cmd.ClearColor(0.125f, 0.125f, 0.125f, 1);
        cmd.Create(Probe, Protocol.ElementType.Qml);
        cmd.SetString(Probe, Protocol.PropertyId.QmlSource, qml);
        cmd.SetI32(Probe, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.Reparent(Probe, 0, 0);
        cmd.Bind(Probe, ProbeHandle);
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"keyshortcuts: create KeyProbe ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        cmd.Clear();

        void Poll()
        {
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"keyshortcuts: poll ({n})");
            var b = buf.AsSpan(0, Math.Max(n, 0));
            for (int off = 0; off + 12 <= b.Length;)
            {
                ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
                if (len < 4 || off + 8 + len > b.Length)
                    break;
                if (ev == Protocol.EventId.QmlProperty && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)) == ProbeHandle
                    && QmlEventDecoder.TryDecodeProperty(b.Slice(off + 12, len - 4), out var name, out var value))
                {
                    if (name == "count") count = Convert.ToInt64(value);
                    else if (name == "lastKey") lastKey = Convert.ToInt64(value);
                }
                off += (8 + len + 3) & ~3;
            }
        }
        // A key goes through the event filter of QtNative first (the overlay) and, if not taken, to the focus item.
        void Key(int key, int modifiers = 0)
        {
            s.Check(NativeTest.vlqt_test_send_key(w, key, modifiers) == 0, $"keyshortcuts: send_key {key:X}");
            Poll();
        }
        int Overlay() => Native.vlqt_scene_get_overlay(w);
        void Set(int flags) { NativeTest.vlqt_test_set_overlay(w, flags); Poll(); }
        void Keys(bool on) => s.Check(Native.vlqt_window_set_keys(w, on ? 1 : 0) == Native.Result.Ok, $"keyshortcuts: set_keys({on})");
        bool Fullscreen() => (Native.vlqt_window_get_mode(w) & Protocol.WindowMode.Fullscreen) != 0;

        Poll();
        // Focus for the probe: a click on it (the SpinBox tests do the same); the click is no key.
        NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Move, 50, 50);
        NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Press, 50, 50);
        NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Release, 50, 50);
        Poll();
        Poll();

        // ---- keys on (the default): the overlay takes F2, F5, Shift+F5, F6 and F7, the scene sees none ----------------------
        Key(KeyF2);
        s.Check((Overlay() & Meter) != 0, $"keyshortcuts: on: F2 shows the meter ({Overlay()})");
        Key(KeyF2);
        s.Check((Overlay() & Meter) == 0, "keyshortcuts: on: F2 hides it");
        Key(KeyF5);
        s.Check((Overlay() & Stats) != 0, $"keyshortcuts: on: F5 shows the stats ({Overlay()})");
        Key(KeyF6);
        s.Check((Overlay() & Freeze) != 0, "keyshortcuts: on: F6 freezes the stats");
        Key(KeyF6);
        // F7 switches the host segments process-wide: twice, so the later scenarios find them as they were.
        Key(KeyF7);
        Key(KeyF7);
        Key(KeyF5, Shift);
        s.Check((Overlay() & Stats) == 0, "keyshortcuts: on: Shift+F5 hides the stats");
        Set(0);
        Key(KeyF11);
        s.Check(Fullscreen(), "keyshortcuts: on: F11 switches to fullscreen");
        Key(KeyF11);
        s.Check(!Fullscreen(), "keyshortcuts: on: F11 switches back");
        Key(KeyReturn, Alt);
        s.Check(Fullscreen(), "keyshortcuts: on: Alt+Enter switches to fullscreen");
        Key(KeyReturn, Alt);
        s.Check(!Fullscreen(), "keyshortcuts: on: Alt+Enter switches back");
        s.Check(count == 0, $"keyshortcuts: on: no key reached the scene ({count})");

        // ---- keys off: every key reaches the scene, the overlay does not change -------------------------------------------
        Keys(false);
        Key(KeyF2);
        Key(KeyF5);
        s.Check((Overlay() & (Meter | Stats | Freeze)) == 0, $"keyshortcuts: off: F2 and F5 do not touch the overlay ({Overlay()})");
        s.Check(count == 2 && lastKey == KeyF5, $"keyshortcuts: off: F2 and F5 reach the scene ({count}, last {lastKey:X})");
        Key(KeyF5, Shift);
        s.Check(count == 3 && (Overlay() & Stats) == 0, $"keyshortcuts: off: Shift+F5 reaches the scene ({count})");
        // F6 and F7 belong to the overlay only while the stats are visible: open them by the test export.
        Set(Stats | ((0 + 1) << PageShift));
        Key(KeyF6);
        s.Check(count == 4 && lastKey == KeyF6 && (Overlay() & (Stats | Freeze)) == Stats, $"keyshortcuts: off: F6 reaches the scene, no freeze ({count}, {Overlay()})");
        Key(KeyF7);
        s.Check(count == 5 && lastKey == KeyF7, $"keyshortcuts: off: F7 reaches the scene ({count})");
        s.Check((Overlay() & Stats) != 0, "keyshortcuts: off: an open overlay stays open");
        Set(0);
        Key(KeyF11);
        s.Check(count == 6 && lastKey == KeyF11 && !Fullscreen(), $"keyshortcuts: off: F11 reaches the scene, no fullscreen ({count})");
        Key(KeyReturn, Alt);
        s.Check(count == 7 && lastKey == KeyReturn && !Fullscreen(), $"keyshortcuts: off: Alt+Enter reaches the scene, no fullscreen ({count})");

        // ---- the switch survives the window swap of set_samples ------------------------------------------------------------
        var before = Native.vlqt_window_get_samples(w);
        var used = Native.vlqt_window_set_samples(w, before > 1 ? 1 : 4);
        Poll();
        if (used != before)
        {
            Key(KeyF2);
            Key(KeyF11);
            s.Check((Overlay() & Meter) == 0 && !Fullscreen() && count == 9,
                $"keyshortcuts: off: still off after the window swap ({before} -> {used} samples, {count})");
        }
        else
            s.Skip($"keyshortcuts: the sample count stays {before} on this device, no window swap");

        // ---- on again: the old behaviour -----------------------------------------------------------------------------------
        Keys(true);
        var seen = count;
        Key(KeyF5);
        s.Check((Overlay() & Stats) != 0 && count == seen, $"keyshortcuts: on again: F5 shows the stats, the scene sees nothing ({count})");
        Set(0);

        s.Check(Native.vlqt_window_set_keys(SceneHandle.Null, 0) == Native.Result.NullHandle, "keyshortcuts: set_keys(null) is NullHandle");
        Native.vlqt_scene_destroy(w);
    }
}
