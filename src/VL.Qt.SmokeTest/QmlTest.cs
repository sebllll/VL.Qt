using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using static Pixels;

// QmlElement: a user .qml file in the element tree, raw protocol against QtNative (CREATE, SET QML_SOURCE / QML_RELOAD,
// SET_QML_PROPERTIES, EVENT_QML_PROPERTY / EVENT_QML_SIGNAL / EVENT_ERROR). Test files: assets/QmlTest.qml (red rectangle, white progress
// bar, properties fill / progress / shown / steps / clicks, signals clicked(int) and finished(...)), assets/QmlTypes.qml (every
// value type and every list<T> in a property and in a bound copy), assets/QmlLists.qml (a list in a var property, lists that QML
// makes itself). Files that are edited at run time live in <outDir>/qml/.
// What it covers: the picture, a property set by the patch is seen and is not echoed, what QML changes itself comes back once
// per poll (steps, clicks, the Behavior animation of shown), signals with their arguments, animations follow vlqt_app_advance_animations
// (not the wall clock), a Behavior retargeted by a value set every frame follows it (help example, LFO), every value type both ways,
// every list type both ways (also empty, strings of every padding, 65536 elements, a var list, lists made by QML, list signal
// arguments, the limit), property errors, compile / load errors (syntax, missing module, root is not
// an Item, missing file) and recovery, reload after a file change (also a component next to the file), Auto Size.
// Grabs go to <outDir>/qml_*.png.
[TestFixture, NonParallelizable]
public static class QmlTest
{
    [Test, Category("qml")]
    public static void Qml() => Scenario.Run(Run);

    private const uint Q = 10, T = 20, R = 30, E = 40, O = 50, LS = 70, Extra = 99;
    private const uint HQ = 110, HT = 120, HR = 130, HE = 140, HO = 150, HLS = 170;

    private static bool Near(Color c, int r, int g, int b, int tol = 10) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;
    private static bool IsBg(Color c) => Near(c, 0x20, 0x20, 0x20);
    private static bool IsRed(Color c) => Near(c, 255, 0, 0);
    private static bool IsGreen(Color c) => Near(c, 0, 255, 0);
    private static bool IsBlue(Color c) => Near(c, 0, 0, 255);
    private static bool IsWhite(Color c) => Near(c, 255, 255, 255);
    private static bool IsYellow(Color c) => Near(c, 255, 255, 0);
    private static bool IsMagenta(Color c) => Near(c, 255, 0, 255);
    private static bool IsCyan(Color c) => Near(c, 0, 255, 255);

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = System.IO.Path.Combine(d.FullName, System.IO.Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload);

    private static List<Rec> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length)
                break;
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)), b.Slice(off + 12, len - 4).ToArray()));
            off += (8 + len + 3) & ~3;
        }
        return list;
    }

    private static void Run(Scenario s)
    {
        var qmlTest = FindFile("src", "VL.Qt.SmokeTest", "assets", "QmlTest.qml");
        var qmlTypes = FindFile("src", "VL.Qt.SmokeTest", "assets", "QmlTypes.qml");
        var qmlLists = FindFile("src", "VL.Qt.SmokeTest", "assets", "QmlLists.qml");
        s.Check(qmlTest != null && qmlTypes != null && qmlLists != null, $"test files found ({qmlTest}, {qmlTypes}, {qmlLists})");
        if (qmlTest == null || qmlTypes == null || qmlLists == null) return;
        var work = System.IO.Path.Combine(s.OutDir, "qml");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var testText = File.ReadAllText(qmlTest);

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt QmlElement test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 20]; // a list of 65536 elements is 256 KB
        var errors = new Dictionary<uint, string>(); // last EVENT_ERROR text per handle (empty = cleared)
        var measured = new Dictionary<uint, (float W, float H)>();

        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Clear();
        }
        Native.Result Expect(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            cmd.Clear();
            return rc;
        }
        // One poll: everything that came since the last one (also tracks the last error and the measured size per handle).
        List<Rec> Poll()
        {
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"poll ({n})");
            var recs = Parse(buf.AsSpan(0, Math.Max(n, 0)));
            foreach (var r in recs)
            {
                if (r.Event == Protocol.EventId.Error) errors[r.Handle] = Encoding.UTF8.GetString(r.Payload);
                else if (r.Event == Protocol.EventId.Measured && r.Payload.Length >= 8)
                    measured[r.Handle] = (BinaryPrimitives.ReadSingleLittleEndian(r.Payload), BinaryPrimitives.ReadSingleLittleEndian(r.Payload.AsSpan(4)));
            }
            return recs;
        }
        List<Rec> Frame()
        {
            s.Pump(1);
            return Poll();
        }
        // n frames, every poll on its own: the per-frame lists keep "one EVENT_QML_PROPERTY per property per poll" checkable.
        List<List<Rec>> Frames(int n)
        {
            var all = new List<List<Rec>>();
            for (int i = 0; i < n; i++) all.Add(Frame());
            return all;
        }
        static List<Rec> Flat(List<List<Rec>> frames) => frames.SelectMany(f => f).ToList();

        static List<object> Props(List<Rec> recs, uint h, string name)
        {
            var l = new List<object>();
            foreach (var r in recs)
                if (r.Event == Protocol.EventId.QmlProperty && r.Handle == h && QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out var v) && n == name)
                    l.Add(v);
            return l;
        }
        static List<string> PropNames(List<Rec> recs, uint h)
        {
            var l = new List<string>();
            foreach (var r in recs)
                if (r.Event == Protocol.EventId.QmlProperty && r.Handle == h && QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out _))
                    l.Add(n);
            return l;
        }
        static List<QmlSignal> Signals(List<Rec> recs, uint h)
        {
            var l = new List<QmlSignal>();
            foreach (var r in recs)
                if (r.Event == Protocol.EventId.QmlSignal && r.Handle == h && QmlEventDecoder.TryDecodeSignal(r.Payload, out var s))
                    l.Add(s);
            return l;
        }
        static string S(List<Rec> recs) => string.Join("; ", recs.Select(r => r.Event == Protocol.EventId.QmlProperty && QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out var v) ? $"prop h{r.Handle} {n}={v}"
            : r.Event == Protocol.EventId.QmlSignal && QmlEventDecoder.TryDecodeSignal(r.Payload, out var sg) ? $"signal h{r.Handle} {sg.Name}({string.Join(",", sg.Arguments)})"
            : r.Event == Protocol.EventId.Error ? $"error h{r.Handle} '{Encoding.UTF8.GetString(r.Payload)}'"
            : $"ev{r.Event} h{r.Handle}"));
        string Err(uint h) => errors.TryGetValue(h, out var e) ? e : "";
        static bool Close(object? v, float expected, float tolerance = 0.002f) => v is float f && Math.Abs(f - expected) <= tolerance;

        void Mouse(int type, float x, float y) => s.Check(NativeTest.vlqt_test_send_mouse(w, type, x, y) == 0, $"test_send_mouse {type}");
        void Click(float x, float y)
        {
            Mouse(TestProtocol.Mouse.Move, x, y);
            Mouse(TestProtocol.Mouse.Press, x, y);
            Mouse(TestProtocol.Mouse.Release, x, y);
        }

        void Qml(uint id, uint handle, string path, float x, float y)
        {
            cmd.Create(id, Protocol.ElementType.Qml);
            cmd.SetString(id, Protocol.PropertyId.QmlSource, path);
            cmd.SetI32(id, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
            cmd.SetMat4(id, Protocol.PropertyId.Transform, Matrix.Translation(x, y, 0));
            cmd.Reparent(id, 0, 1000);
            cmd.Bind(id, handle);
        }
        void SetProps(uint id, Action<QmlProps> write)
        {
            cmd.BeginQmlProperties(id, 0);
            write(new QmlProps(cmd));
            cmd.EndQmlProperties();
        }

        cmd.Clear();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);

        // ---- 1. load: picture, implicit size, no events of the initial state --------------------------------------------------
        Qml(Q, HQ, qmlTest, 100, 100);
        CheckApply("create QmlElement (QmlTest.qml)");
        var recs = Flat(Frames(4));
        s.Check(Err(HQ) == "", $"QmlTest.qml loads without error ('{Err(HQ)}')");
        s.Check(measured.TryGetValue(HQ, out var m) && m == (200, 100), $"Auto Size: the element takes the implicit size of the root (200 x 100): {(measured.TryGetValue(HQ, out var mm) ? mm.ToString() : "none")}");
        s.Check(PropNames(recs, HQ).Count == 0 && Signals(recs, HQ).Count == 0, $"no property or signal event for the initial state ({S(recs)})");
        using (var a = s.Grab(w, "qml_loaded.png"))
        {
            s.Check(IsRed(Px(a, 150, 130)) && IsRed(Px(a, 295, 105)) && IsRed(Px(a, 105, 195)), $"red rectangle 100,100 .. 300,200 ({Px(a, 150, 130)})");
            s.Check(IsBg(Px(a, 95, 150)) && IsBg(Px(a, 305, 150)) && IsBg(Px(a, 200, 95)) && IsBg(Px(a, 200, 205)), "nothing outside the root");
            s.Check(IsRed(Px(a, 110, 190)), $"progress 0 = no white bar yet ({Px(a, 110, 190)})");
        }

        // ---- 2. a property of the patch is seen and not echoed; what QML does itself comes back -------------------------------
        SetProps(Q, p => { p.Color("fill", 0, 1, 0, 1); p.F32("progress", 0.5f); });
        CheckApply("SET_QML_PROPERTIES fill + progress");
        recs = Poll(); // QML has already reacted inside the apply
        var names = PropNames(recs, HQ);
        s.Check(!names.Contains("fill") && !names.Contains("progress"), $"no echo of the properties the patch set ({S(recs)})");
        s.Check(Props(recs, HQ, "steps") is [int st1] && st1 == 1,
            $"steps (counted by QML while progress was set) comes back once with 1 ({S(recs)})");
        // Animations follow vlqt_app_advance_animations, not the wall clock: the animation to 0.5 that has just started does not move without it.
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var wallRecs = new List<Rec>();
        while (wall.ElapsedMilliseconds < 400)
        {
            Native.vlqt_app_process_events_for(null, 0); // events and rendering, but the driver's clock stays where it is
            System.Threading.Thread.Sleep(20);
            wallRecs.AddRange(Poll());
        }
        s.Check(Props(wallRecs, HQ, "shown").Count == 0, $"no animation progress on wall time (0.4 s passed, no vlqt_app_advance_animations) ({S(wallRecs)})");
        // The animation runs on the frame clock: every poll has at most one value of shown, it rises, it ends at 0.5.
        var frames = new List<List<Rec>>();
        float shownAt30 = -1;
        for (int i = 0; i < 80; i++)
        {
            frames.Add(Frame());
            if (i == 29) shownAt30 = Props(Flat(frames), HQ, "shown") is { Count: > 0 } l ? (float)l[^1] : -1;
        }
        recs = Flat(frames);
        var shown = Props(recs, HQ, "shown").Cast<float>().ToList();
        s.Check(shown.Count >= 5 && Close(shown[^1], 0.5f), $"Behavior animation on shown reports its steps and ends at 0.5 ({shown.Count} values, last {(shown.Count > 0 ? shown[^1] : -1)})");
        s.Check(shown.Zip(shown.Skip(1), (a, b) => b >= a).All(x => x) && shown.Count > 0 && shown[0] < 0.5f, "shown rises monotonically");
        s.Check(shownAt30 > 0.1f && shownAt30 < 0.4f, $"after 30 frames (0.5 s of the frame clock) the 1 s animation to 0.5 is about half way ({shownAt30})");
        s.Check(frames.All(f => PropNames(f, HQ).GroupBy(n => n).All(g => g.Count() == 1)), "at most one event per property per poll");
        s.Check(!PropNames(recs, HQ).Contains("steps") && !PropNames(recs, HQ).Contains("fill") && !PropNames(recs, HQ).Contains("progress"), $"nothing else changed (steps, fill, progress stay quiet) ({string.Join(",", PropNames(recs, HQ).Distinct())})");
        recs = Flat(Frames(5));
        s.Check(PropNames(recs, HQ).Count == 0, $"an idle QML item sends nothing ({S(recs)})");
        using (var a = s.Grab(w, "qml_progress.png"))
        {
            s.Check(IsGreen(Px(a, 150, 130)) && IsGreen(Px(a, 250, 150)), $"fill = green is seen ({Px(a, 150, 130)})");
            s.Check(IsWhite(Px(a, 110, 190)) && IsWhite(Px(a, 195, 190)) && IsGreen(Px(a, 210, 190)), $"progress 0.5 = white bar over the left half ({Px(a, 195, 190)}, {Px(a, 210, 190)})");
        }
        // Unchanged values again: nothing happens (the C# side does not even send them; here the same value set is no change in QML).
        SetProps(Q, p => p.F32("progress", 0.5f));
        CheckApply("SET_QML_PROPERTIES same value");
        recs = Poll();
        s.Check(PropNames(recs, HQ).Count == 0, $"the same value again is no change ({S(recs)})");

        // ---- 3. signals with their arguments; a signal and a property from the same click --------------------------------------
        Click(200, 140);
        recs = Frame();
        var sig = Signals(recs, HQ);
        s.Check(sig.Count == 1 && sig[0].Name == "clicked" && sig[0].Arguments.Count == 1 && sig[0].Arguments[0] is int n1 && n1 == 1, $"click -> signal clicked(1) ({S(recs)})");
        s.Check(Props(recs, HQ, "clicks") is [int c1] && c1 == 1, $"... and the property clicks = 1, once ({S(recs)})");
        Click(200, 140);
        Click(200, 140); // two clicks in one frame: both signals in order, the property once with the last value
        recs = Frame();
        sig = Signals(recs, HQ);
        s.Check(sig.Count == 2 && sig[0].Arguments[0] is 2 && sig[1].Arguments[0] is 3, $"two clicks in one frame -> clicked(2), clicked(3) in order ({S(recs)})");
        s.Check(Props(recs, HQ, "clicks") is [int c3] && c3 == 3, $"clicks once with the last value 3 ({S(recs)})");
        recs = Flat(Frames(3));
        s.Check(Signals(recs, HQ).Count == 0 && PropNames(recs, HQ).Count == 0, $"signals and changes are sent once ({S(recs)})");

        // A signal with four arguments of different types comes from QML when progress reaches 1 (the patch sets it).
        SetProps(Q, p => p.F32("progress", 1f));
        CheckApply("SET progress 1");
        recs = Poll();
        sig = Signals(recs, HQ);
        s.Check(sig.Count == 1 && sig[0].Name == "finished" && sig[0].Arguments.Count == 4
                && sig[0].Arguments[0] is "done" && Close(sig[0].Arguments[1], 1f) && sig[0].Arguments[2] is true
                && sig[0].Arguments[3] is Color4 tint && Math.Abs(tint.G - 1) < 0.01f && tint.R < 0.01f && tint.B < 0.01f && Math.Abs(tint.A - 1) < 0.01f,
            $"signal finished(string, real, bool, color) with its four arguments ({S(recs)})");
        s.Check(Props(recs, HQ, "steps") is [int st2] && st2 == 2 && !PropNames(recs, HQ).Contains("progress"), $"steps = 2, progress not echoed ({S(recs)})");

        recs = Flat(Frames(80));
        shown = Props(recs, HQ, "shown").Cast<float>().ToList();
        s.Check(shown.Count >= 5 && Close(shown[^1], 1f), $"the animation to 1 runs with vlqt_app_advance_animations and ends at 1 ({shown.Count} values)");
        using (var a = s.Grab(w, "qml_full.png"))
            s.Check(IsWhite(Px(a, 110, 190)) && IsWhite(Px(a, 295, 190)) && IsGreen(Px(a, 150, 150)), $"progress 1 = white bar over the whole width ({Px(a, 295, 190)})");

        // ---- 4. every value type both ways ------------------------------------------------------------------------------------
        Qml(T, HT, qmlTypes, 500, 50);
        CheckApply("create QmlElement (QmlTypes.qml)");
        Flat(Frames(2));
        s.Check(Err(HT) == "", $"QmlTypes.qml loads ('{Err(HT)}')");
        const string text = "hä€😀 text";
        SetProps(T, p =>
        {
            p.F32("r", 0.25f);
            p.I32("i", 7);
            p.Bool("b", true);
            p.String("s", text);
            p.Color("c", 0.2f, 0.4f, 0.6f, 0.8f);
            p.Vec2("v2", 1.5f, -2);
            p.Vec3("v3", 1, 2, 3);
            p.Vec4("v4", 4, 3, 2, 1);
            p.Rect("box", 10, 20, 30, 40);
            p.Vec2("pt", 3, -4);
            p.Vec2("sz", 640, 480);
        });
        CheckApply("SET_QML_PROPERTIES all value types");
        recs = Poll();
        var back = recs.Where(r => r.Event == Protocol.EventId.QmlProperty && r.Handle == HT).Select(r => { QmlEventDecoder.TryDecodeProperty(r.Payload, out var n, out var v); return (n, v); }).ToDictionary(x => x.n, x => x.v);
        s.Check(back.Count == 11 && back.Keys.All(k => k.EndsWith("Out")), $"the eleven bound copies come back and nothing else ({string.Join(",", back.Keys)})");
        s.Check(back.TryGetValue("rOut", out var rOut) && Close(rOut, 0.25f), $"real round trip ({(back.TryGetValue("rOut", out var rv) ? rv : "none")})");
        s.Check(back.TryGetValue("iOut", out var iOut) && iOut is 7, $"int round trip ({(back.TryGetValue("iOut", out var iv) ? iv : "none")})");
        s.Check(back.TryGetValue("bOut", out var bOut) && bOut is true, "bool round trip");
        s.Check(back.TryGetValue("sOut", out var sOut) && sOut is string ss && ss == text, $"string round trip with umlauts, euro sign and an emoji ({(back.TryGetValue("sOut", out var sv) ? sv : "none")})");
        s.Check(back.TryGetValue("cOut", out var cOut) && cOut is Color4 c4 && Math.Abs(c4.R - 0.2f) < 0.002f && Math.Abs(c4.G - 0.4f) < 0.002f && Math.Abs(c4.B - 0.6f) < 0.002f && Math.Abs(c4.A - 0.8f) < 0.002f, $"color round trip ({(back.TryGetValue("cOut", out var cv) ? cv : "none")})");
        s.Check(back.TryGetValue("v2Out", out var v2Out) && v2Out is Vector2 vv2 && vv2 == new Vector2(1.5f, -2), "vector2d round trip");
        s.Check(back.TryGetValue("v3Out", out var v3Out) && v3Out is Vector3 vv3 && vv3 == new Vector3(1, 2, 3), "vector3d round trip");
        s.Check(back.TryGetValue("v4Out", out var v4Out) && v4Out is Vector4 vv4 && vv4 == new Vector4(4, 3, 2, 1), "vector4d round trip");
        s.Check(back.TryGetValue("boxOut", out var boxOut) && boxOut is RectangleF rr && rr == new RectangleF(10, 20, 30, 40), $"rect round trip ({(back.TryGetValue("boxOut", out var bv) ? bv : "none")})");
        s.Check(back.TryGetValue("ptOut", out var ptOut) && ptOut is Vector2 vpt && vpt == new Vector2(3, -4), $"point round trip: a Vector2 into a point property, back as Vector2 ({(back.TryGetValue("ptOut", out var pv) ? pv : "none")})");
        s.Check(back.TryGetValue("szOut", out var szOut) && szOut is Vector2 vsz && vsz == new Vector2(640, 480), $"size round trip ({(back.TryGetValue("szOut", out var zv) ? zv : "none")})");
        s.Check(Signals(recs, HT) is [{ Name: "located" } located] && located.Arguments.Count == 1 && located.Arguments[0] is Vector2 la && la == new Vector2(3, -4), $"a signal argument of the type point arrives as Vector2 ({S(recs)})");
        s.Check(Err(HT) == "", $"no error for the eleven values ('{Err(HT)}')");

        // QVariant converts what it can: a float into an int property, an int into a real property, a bool into a real.
        SetProps(T, p => { p.F32("i", 3f); p.I32("r", 4); });
        CheckApply("SET float -> int property, int -> real property");
        recs = Poll();
        s.Check(Props(recs, HT, "iOut") is [int i3] && i3 == 3 && Props(recs, HT, "rOut") is [float r4] && r4 == 4f && Err(HT) == "",
            $"float 3 into an int property, int 4 into a real property ({S(recs)}; error '{Err(HT)}')");
        // A long string takes the whole way, also past 64 KB (u32 lengths in the commands and in the event records).
        var longText = string.Concat(Enumerable.Repeat("0123456789äöü€", 3700)); // 51800 characters, 70300 bytes of UTF-8
        SetProps(T, p => p.String("s", longText));
        CheckApply("SET long string");
        recs = Poll();
        s.Check(Encoding.UTF8.GetByteCount(longText) > 70000 && Props(recs, HT, "sOut") is [string ls] && ls == longText && Err(HT) == "",
            $"a string of {Encoding.UTF8.GetByteCount(longText)} UTF-8 bytes arrives complete, no error ('{Err(HT)}')");

        // ---- 4b. lists: every list type both ways ---------------------------------------------------------------------------
        // One list in, its bound copy out: once, as a Spread of the element type, equal to what was sent; the list itself is not echoed.
        List<Rec> ListRoundTrip<TE>(uint id, uint handle, string prop, string what, Action<QmlProps> write, TE[] expected, Func<TE, TE, bool>? same = null)
        {
            SetProps(id, write);
            CheckApply($"SET_QML_PROPERTIES {prop} ({what})");
            var got = Poll();
            same ??= (a, b) => EqualityComparer<TE>.Default.Equals(a, b);
            var copy = Props(got, handle, prop + "Out");
            var ok = copy is [Spread<TE> list] && list.Count == expected.Length && list.Zip(expected, same).All(x => x);
            s.Check(ok && !PropNames(got, handle).Contains(prop) && Err(handle) == "",
                $"list {prop}, {what}: round trip as Spread<{typeof(TE).Name}> of {expected.Length}, not echoed ({S(got)}; error '{Err(handle)}')");
            return got;
        }
        static bool Near2(float a, float b) => Math.Abs(a - b) <= 0.002f;
        static bool SameColor(Color4 a, Color4 b) => Near2(a.R, b.R) && Near2(a.G, b.G) && Near2(a.B, b.B) && Near2(a.A, b.A);
        static float[] Floats(IEnumerable<Color4> v) => v.SelectMany(x => new[] { x.R, x.G, x.B, x.A }).ToArray();
        static float[] Floats2(IEnumerable<Vector2> v) => v.SelectMany(x => new[] { x.X, x.Y }).ToArray();
        static float[] Floats3(IEnumerable<Vector3> v) => v.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray();
        static float[] Floats4(IEnumerable<Vector4> v) => v.SelectMany(x => new[] { x.X, x.Y, x.Z, x.W }).ToArray();
        static float[] FloatsR(IEnumerable<RectangleF> v) => v.SelectMany(x => new[] { x.X, x.Y, x.Width, x.Height }).ToArray();
        const ushort F32T = Protocol.ValueType.F32, V2T = Protocol.ValueType.Vec2, V3T = Protocol.ValueType.Vec3, V4T = Protocol.ValueType.Vec4,
            ColorT = Protocol.ValueType.Color, RectT = Protocol.ValueType.Rectf;

        // Reals: list<real> and list<double> are the same pin type; an empty list after a full one is a change and keeps the element type.
        var reals = new[] { 0.25f, -1.5f, 1000f };
        recs = ListRoundTrip(T, HT, "rl", "three reals", p => p.ListF32("rl", F32T, reals), reals);
        s.Check(Signals(recs, HT) is [{ Name: "measured", Arguments: [Spread<float> sm] }] && sm.SequenceEqual(reals), $"a list argument of a signal: measured(list<real>) arrives as a Spread<float> ({S(recs)})");
        recs = ListRoundTrip(T, HT, "rl", "empty", p => p.ListF32("rl", F32T), Array.Empty<float>());
        s.Check(Signals(recs, HT) is [{ Name: "measured", Arguments: [Spread<float> { Count: 0 }] }], $"an empty list argument has the element type of its declaration (floats) ({S(recs)})");
        ListRoundTrip(T, HT, "dl", "three doubles", p => p.ListF32("dl", F32T, reals), reals);
        ListRoundTrip(T, HT, "dl", "empty", p => p.ListF32("dl", F32T), Array.Empty<float>());
        ListRoundTrip(T, HT, "il", "ints", p => p.ListI32("il", 7, -3, int.MaxValue, int.MinValue), new[] { 7, -3, int.MaxValue, int.MinValue });
        ListRoundTrip(T, HT, "il", "empty", p => p.ListI32("il"), Array.Empty<int>());
        // One byte per bool: counts of every remainder modulo 4 (padding of the entry).
        foreach (var bools in new[] { new[] { true }, new[] { true, false }, new[] { false, true, true }, new[] { true, false, true, true }, new[] { true, false, false, true, true } })
        {
            ListRoundTrip(T, HT, "bl", $"{bools.Length} bools", p => p.ListBool("bl", bools), bools);
            ListRoundTrip(T, HT, "bl", "empty", p => p.ListBool("bl"), Array.Empty<bool>());
        }
        // Strings: lengths 0..4, umlauts, euro sign, emoji (UTF-8 of 1 to 4 bytes per character): every padding to 4 bytes, also of the last one.
        var texts = new[] { "", "a", "ab", "abc", "abcd", "hä€😀 text", "ä", "😀", "€€" };
        recs = ListRoundTrip(T, HT, "sl", "strings of every padding", p => p.ListString("sl", texts), texts);
        s.Check(Signals(recs, HT) is [{ Name: "tagged", Arguments: [Spread<string> st] }] && st.SequenceEqual(texts), $"a list of strings as signal argument ({S(recs)})");
        for (int n = 1; n <= 4; n++)
        {
            var one = new[] { new string('x', n) + "ä" };
            ListRoundTrip(T, HT, "sl", $"one string of {Encoding.UTF8.GetByteCount(one[0])} bytes", p => p.ListString("sl", one), one);
        }
        recs = ListRoundTrip(T, HT, "sl", "empty", p => p.ListString("sl"), Array.Empty<string>());
        s.Check(Signals(recs, HT) is [{ Name: "tagged", Arguments: [Spread<string> { Count: 0 }] }], $"an empty list of strings stays a list of strings ({S(recs)})");
        var colors = new[] { new Color4(1, 0, 0, 1), new Color4(0.2f, 0.4f, 0.6f, 0.8f), new Color4(0, 0, 0, 0) };
        ListRoundTrip(T, HT, "cl", "colors", p => p.ListF32("cl", ColorT, Floats(colors)), colors, SameColor);
        ListRoundTrip(T, HT, "cl", "empty", p => p.ListF32("cl", ColorT), Array.Empty<Color4>());
        var points = new[] { new Vector2(1, 2), new Vector2(-3.5f, 4), new Vector2(0, -0.25f) };
        recs = ListRoundTrip(T, HT, "ptl", "points as Vector2", p => p.ListF32("ptl", V2T, Floats2(points)), points);
        s.Check(Signals(recs, HT) is [{ Name: "placed", Arguments: [Spread<Vector2> sp] }] && sp.SequenceEqual(points), $"list<point> as signal argument is a Spread<Vector2> ({S(recs)})");
        recs = ListRoundTrip(T, HT, "ptl", "empty", p => p.ListF32("ptl", V2T), Array.Empty<Vector2>());
        s.Check(Signals(recs, HT) is [{ Name: "placed", Arguments: [Spread<Vector2> { Count: 0 }] }], $"an empty list<point> argument is a Spread<Vector2> ({S(recs)})");
        ListRoundTrip(T, HT, "szl", "sizes as Vector2", p => p.ListF32("szl", V2T, Floats2(points)), points);
        ListRoundTrip(T, HT, "szl", "empty", p => p.ListF32("szl", V2T), Array.Empty<Vector2>());
        ListRoundTrip(T, HT, "v2l", "vector2d", p => p.ListF32("v2l", V2T, Floats2(points)), points);
        ListRoundTrip(T, HT, "v2l", "empty", p => p.ListF32("v2l", V2T), Array.Empty<Vector2>());
        var v3s = new[] { new Vector3(1, 2, 3), new Vector3(-4, 5.5f, 0) };
        ListRoundTrip(T, HT, "v3l", "vector3d", p => p.ListF32("v3l", V3T, Floats3(v3s)), v3s);
        ListRoundTrip(T, HT, "v3l", "empty", p => p.ListF32("v3l", V3T), Array.Empty<Vector3>());
        var v4s = new[] { new Vector4(4, 3, 2, 1), new Vector4(-1, 0.5f, 8, 16), new Vector4(0, 0, 0, 1) };
        ListRoundTrip(T, HT, "v4l", "vector4d", p => p.ListF32("v4l", V4T, Floats4(v4s)), v4s);
        ListRoundTrip(T, HT, "v4l", "empty", p => p.ListF32("v4l", V4T), Array.Empty<Vector4>());
        var rects = new[] { new RectangleF(10, 20, 30, 40), new RectangleF(-1, -2, 0.5f, 0.25f) };
        ListRoundTrip(T, HT, "boxl", "rects", p => p.ListF32("boxl", RectT, FloatsR(rects)), rects);
        ListRoundTrip(T, HT, "boxl", "empty", p => p.ListF32("boxl", RectT), Array.Empty<RectangleF>());
        // Two lists in one command, and a list next to a single value.
        SetProps(T, p => { p.ListI32("il", 1, 2, 3); p.F32("r", 9f); p.ListString("sl", "x", "yz"); });
        CheckApply("SET_QML_PROPERTIES two lists and a real");
        recs = Poll();
        s.Check(Props(recs, HT, "ilOut") is [Spread<int> i123] && i123.SequenceEqual(new[] { 1, 2, 3 }) && Props(recs, HT, "slOut") is [Spread<string> sxy] && sxy.SequenceEqual(new[] { "x", "yz" })
                && Props(recs, HT, "rOut") is [float r9] && r9 == 9f && Err(HT) == "", $"two lists and a real in one command ({S(recs)})");
        // The numbers of the other kind convert like single values: ints into a list<real>, whole reals into a list<int>.
        ListRoundTrip(T, HT, "rl", "ints into a list<real>", p => p.ListI32("rl", 1, 2, 3), new[] { 1f, 2f, 3f });
        ListRoundTrip(T, HT, "il", "reals into a list<int>", p => p.ListF32("il", F32T, 4f, 5f), new[] { 4, 5 });
        // The most a list can have, and the limit of the C# writer (QtNative's own limit has cases in ApplyTest).
        var full = Enumerable.Range(0, Protocol.ListValueMaxCount).ToArray();
        ListRoundTrip(T, HT, "il", $"{full.Length} ints", p => p.ListI32("il", full), full);
        cmd.BeginQmlProperties(T, 0);
        s.Check(Assert.Throws<ArgumentException>(() => cmd.QmlPropertyListI32("il", new int[Protocol.ListValueMaxCount + 1])) != null, "the writer refuses more than 65536 elements");
        cmd.Clear();
        // A list that does not convert: an error line with the name, nothing applied; a good list afterwards clears it.
        SetProps(T, p => p.ListString("rl", "a", "b"));
        CheckApply("SET strings into a list<real>");
        recs = Poll();
        s.Check(Err(HT).StartsWith("rl: ") && Err(HT).Contains("cannot be converted") && Props(recs, HT, "rlOut").Count == 0, $"strings into a list<real>: error line with the name, no change ('{Err(HT)}')");
        SetProps(T, p => p.ListF32("v3l", V2T, 1, 2, 3, 4));
        CheckApply("SET vector2d list into a list<vector3d>");
        Poll();
        s.Check(Err(HT).Contains("v3l: ") && Err(HT).Contains("cannot be converted"), $"vec2 elements into a list<vector3d>: error line ('{Err(HT)}')");
        SetProps(T, p => { p.ListF32("rl", F32T, 5f); p.ListF32("v3l", V3T, 1, 2, 3); });
        CheckApply("SET good lists again");
        recs = Poll();
        s.Check(Err(HT) == "" && Props(recs, HT, "rlOut") is [Spread<float> r5] && r5.SequenceEqual(new[] { 5f }), $"good lists clear the errors ('{Err(HT)}', {S(recs)})");

        // url: the text of a Path. An absolute local path (here with a space and an umlaut) comes back as the file URL of that path;
        // a URL with a scheme and a relative one come back as sent (QML resolves a relative url where it is used, not when it is set).
        static bool SameUrl(string back, string sent) => System.IO.Path.IsPathFullyQualified(sent)
            ? Uri.TryCreate(back, UriKind.Absolute, out var u) && u.IsFile && System.IO.Path.GetFullPath(u.LocalPath) == System.IO.Path.GetFullPath(sent)
            : back == sent;
        var localFile = System.IO.Path.Combine(work, "bilder ä", "a.png");
        foreach (var sent in new[] { localFile, "https://example.com/a.png", "qrc:/img/a.png", "img/a.png", "" })
        {
            SetProps(T, p => p.String("u", sent));
            CheckApply($"SET url '{sent}'");
            recs = Poll();
            s.Check(Props(recs, HT, "uOut") is [string ub] && SameUrl(ub, sent) && Err(HT) == "", $"url '{sent}' round trip ({S(recs)}; error '{Err(HT)}')");
        }
        var urls = new[] { localFile, "https://example.com/b.png", "img/c.png" };
        ListRoundTrip(T, HT, "ul", "urls", p => p.ListString("ul", urls), urls, SameUrl);
        ListRoundTrip(T, HT, "ul", "empty", p => p.ListString("ul"), Array.Empty<string>());

        // date: milliseconds since 1970 UTC both ways (back as local time), no date as long.MinValue; a list of dates.
        var when = new DateTime(2024, 5, 1, 10, 30, 15, 250, DateTimeKind.Utc);
        foreach (var sent in new[] { QmlDateTime.ToWire(when), long.MinValue, QmlDateTime.ToWire(new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc)) })
        {
            SetProps(T, p => p.DateTime("d", sent));
            CheckApply($"SET date {sent}");
            recs = Poll();
            s.Check(Props(recs, HT, "dOut") is [DateTime db] && QmlDateTime.ToWire(db) == sent && Err(HT) == "", $"date {sent} round trip ({S(recs)}; error '{Err(HT)}')");
        }
        var dates = new[] { when.ToLocalTime(), DateTime.UnixEpoch.ToLocalTime() };
        ListRoundTrip(T, HT, "dal", "dates", p => p.ListDateTime("dal", dates.Select(QmlDateTime.ToWire).ToArray()), dates);
        ListRoundTrip(T, HT, "dal", "empty", p => p.ListDateTime("dal"), Array.Empty<DateTime>());
        // font: pixel size, weight and italic as sent; an empty family is the default family of the app, which comes back by name.
        SetProps(T, p => p.Font("fo", "", 24, 700, true));
        CheckApply("SET font");
        recs = Poll();
        s.Check(Props(recs, HT, "foOut") is [VL.Qt.Elements.Font fb] && fb.Size == 0.24f && fb.Weight == 700 && fb.Italic && fb.Family.Length > 0 && Err(HT) == "",
            $"font round trip: 24 px = 0.24 units, weight, italic, the default family by name ({S(recs)}; error '{Err(HT)}')");

        // alias: the patch sets it like a declared property (not echoed) and the target follows; a change of the target inside QML
        // comes back as a change of the alias.
        SetProps(T, p => p.String("at", "alias ä"));
        CheckApply("SET alias at");
        recs = Poll();
        s.Check(Props(recs, HT, "atOut") is [string ao] && ao == "alias ä" && !PropNames(recs, HT).Contains("at") && Err(HT) == "",
            $"an alias sets its target and is not echoed ({S(recs)}; error '{Err(HT)}')");
        SetProps(T, p => p.String("aliasFeed", "from qml"));
        CheckApply("SET aliasFeed");
        recs = Poll();
        s.Check(Props(recs, HT, "at") is [string ai] && ai == "from qml" && Err(HT) == "", $"a change of the target inside QML comes back as the alias ({S(recs)})");

        // ---- 4c. a list in a var property, lists that QML makes itself --------------------------------------------------------
        Qml(LS, HLS, qmlLists, 500, 200);
        CheckApply("create QmlElement (QmlLists.qml)");
        Flat(Frames(2));
        s.Check(Err(HLS) == "", $"QmlLists.qml loads ('{Err(HLS)}')");
        // var: a JavaScript array; each element is converted like a single value, the copy goes back with its element type.
        ListRoundTrip(LS, HLS, "v", "var: reals", p => p.ListF32("v", F32T, reals), reals);
        ListRoundTrip(LS, HLS, "v", "var: ints", p => p.ListI32("v", 3, 2, 1), new[] { 3, 2, 1 });
        ListRoundTrip(LS, HLS, "v", "var: bools", p => p.ListBool("v", true, false, true), new[] { true, false, true });
        ListRoundTrip(LS, HLS, "v", "var: strings", p => p.ListString("v", texts), texts);
        ListRoundTrip(LS, HLS, "v", "var: colors", p => p.ListF32("v", ColorT, Floats(colors)), colors, SameColor);
        ListRoundTrip(LS, HLS, "v", "var: vector2d", p => p.ListF32("v", V2T, Floats2(points)), points);
        ListRoundTrip(LS, HLS, "v", "var: vector3d", p => p.ListF32("v", V3T, Floats3(v3s)), v3s);
        ListRoundTrip(LS, HLS, "v", "var: vector4d", p => p.ListF32("v", V4T, Floats4(v4s)), v4s);
        ListRoundTrip(LS, HLS, "v", "var: rects", p => p.ListF32("v", RectT, FloatsR(rects)), rects);
        // An empty list has no element to look at: it carries the element type F32.
        ListRoundTrip(LS, HLS, "v", "var: empty", p => p.ListI32("v"), Array.Empty<float>());

        // Lists QML makes: each property comes back once with the type of its elements; the patch only sets n.
        SetProps(LS, p => p.I32("n", 3));
        CheckApply("SET n = 3");
        recs = Poll();
        s.Check(Props(recs, HLS, "counted") is [Spread<int> c0] && c0.SequenceEqual(new[] { 0, 3, 6 }), $"list<int> that QML made: 0, 3, 6 ({S(recs)})");
        s.Check(Props(recs, HLS, "halves") is [Spread<float> h0] && h0.SequenceEqual(new[] { 0.5f, 1.5f, 2.5f }), $"var list of reals that QML made ({S(recs)})");
        s.Check(Props(recs, HLS, "strings") is [Spread<string> t0] && t0.SequenceEqual(new[] { "s", "sä€", "sä€ä€" }), $"var list of strings that QML made ({S(recs)})");
        s.Check(Props(recs, HLS, "mixedNumbers") is [Spread<float> mn] && mn.SequenceEqual(new[] { 1f, 2.5f, 3f }), $"integers and reals together go as reals ({S(recs)})");
        s.Check(!PropNames(recs, HLS).Contains("n"), $"n is not echoed ({string.Join(",", PropNames(recs, HLS))})");
        s.Check(Props(recs, HLS, "mixedTypes") is [string mt] && mt == "[1,\"a\"]" && Err(HLS) == "", $"a list of mixed types is a string in JSON form ({S(recs)}; '{Err(HLS)}')");
        sig = Signals(recs, HLS);
        s.Check(sig.Count == 2 && sig[0].Name == "made" && sig[0].Arguments is [Spread<float> mv] && mv.SequenceEqual(new[] { 3f, 3.5f }), $"a list made in a signal: made([3, 3.5]) ({S(recs)})");
        s.Check(sig.Count == 2 && sig[1].Name == "oddOne" && sig[1].Arguments is [string odd] && odd == "[1,\"a\"]", $"a list of mixed types in a signal is a string in JSON form ({S(recs)})");
        SetProps(LS, p => p.I32("n", 0));
        CheckApply("SET n = 0");
        recs = Poll();
        s.Check(Props(recs, HLS, "counted") is [Spread<int> { Count: 0 }] && Props(recs, HLS, "halves") is [Spread<float> { Count: 0 }] && Props(recs, HLS, "strings") is [Spread<float> { Count: 0 }],
            $"empty lists: list<int> keeps its element type, an empty var list is a Spread<float> ({S(recs)})");
        s.Check(Signals(recs, HLS) is [{ Name: "made", Arguments: [Spread<float> { Count: 0 }] }, { Name: "oddOne", Arguments: [Spread<float> { Count: 0 }] }], $"empty list arguments ({S(recs)})");

        // The limit: 65536 elements still come back, 65537 do not; the error line names the property and the limit.
        SetProps(LS, p => p.I32("bigN", Protocol.ListValueMaxCount));
        CheckApply("SET bigN = 65536");
        recs = Poll();
        s.Check(Props(recs, HLS, "big") is [Spread<int> bigList] && bigList.Count == Protocol.ListValueMaxCount && bigList[0] == 0 && bigList[^1] == Protocol.ListValueMaxCount - 1,
            $"a list of 65536 elements made by QML comes back complete ({recs.Count} records)");
        SetProps(LS, p => p.I32("bigN", Protocol.ListValueMaxCount + 1));
        CheckApply("SET bigN = 65537");
        recs = Poll();
        s.Check(Props(recs, HLS, "big").Count == 0 && Err(HLS).Contains("big: ") && Err(HLS).Contains("65537") && Err(HLS).Contains("65536"),
            $"a list over the limit is not sent, the error line names the property, the count and the limit ('{Err(HLS)}')");
        cmd.Destroy(LS);
        CheckApply("destroy the QmlLists element");
        Flat(Frames(2));

        // Property errors: a value that does not convert, an unknown name, a read-only property (width of Item is writable: use a bad name).
        SetProps(T, p => p.String("i", "abc"));
        CheckApply("SET string -> int property (cannot convert)");
        Poll();
        s.Check(Err(HT).StartsWith("i: ") && Err(HT).Contains("convert"), $"a value that cannot be converted: error line with the name ('{Err(HT)}')");
        SetProps(T, p => p.I32("i", 5));
        CheckApply("SET valid value again");
        recs = Poll();
        s.Check(Err(HT) == "" && Props(recs, HT, "iOut") is [int i5] && i5 == 5, $"the error is gone after the name is set again ('{Err(HT)}', {S(recs)})");
        SetProps(T, p => { p.F32("nope", 1); p.F32("r", 1); });
        CheckApply("SET unknown name");
        recs = Poll();
        s.Check(Err(HT).Contains("nope") && Err(HT).Contains("no such property"), $"unknown name: error line ('{Err(HT)}')");
        s.Check(Props(recs, HT, "rOut") is [float r1] && r1 == 1f, "... the other value of the same command is still applied");
        SetProps(T, p => p.F32("r", 2));
        CheckApply("SET another value");
        Poll();
        s.Check(Err(HT).Contains("nope"), $"the error for a name stays until that name is set again or the file is reloaded ('{Err(HT)}')");
        // SET_QML_PROPERTIES on a non QML element is UnknownProperty, an unknown id UnknownObject (protocol.h).
        var other = Extra;
        cmd.Create(other, Protocol.ElementType.Rectangle);
        cmd.Reparent(other, 0, 0);
        CheckApply("create a Rectangle");
        cmd.BeginQmlProperties(other, 0);
        cmd.QmlPropertyF32("a", 1);
        cmd.EndQmlProperties();
        s.Check(Expect("") == Native.Result.UnknownProperty, $"SET_QML_PROPERTIES on a Rectangle -> UnknownProperty ({Native.LastError()})");
        cmd.BeginQmlProperties(4242, 0);
        cmd.QmlPropertyF32("a", 1);
        cmd.EndQmlProperties();
        s.Check(Expect("") == Native.Result.UnknownObject, $"SET_QML_PROPERTIES on an unknown id -> UnknownObject ({Native.LastError()})");
        cmd.Destroy(other);
        CheckApply("destroy the Rectangle");

        // ---- 5. errors of the file: syntax, missing module, root is not an Item, missing file; recovery -----------------------
        string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(work, name);
            File.WriteAllText(path, content);
            return path;
        }
        var syntax = Write("syntax.qml", "import QtQuick\n\nItem {\n    width: 10 +\n}\n");
        var noModule = Write("nomodule.qml", "import QtQuick\nimport NoSuchModule 1.0\n\nItem {}\n");
        var notItem = Write("notitem.qml", "import QtQml\n\nQtObject {}\n");
        var missing = System.IO.Path.Combine(work, "does-not-exist.qml");

        Qml(E, HE, syntax, 500, 300);
        CheckApply("create QmlElement with a syntax error");
        Flat(Frames(2));
        s.Check(Regex.IsMatch(Err(HE), @"syntax\.qml:\d+:\d+: "), $"syntax error: EVENT_ERROR with file:line:column ('{Err(HE)}')");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, noModule);
        CheckApply("SET QML_SOURCE missing module");
        Flat(Frames(2));
        s.Check(Err(HE).Contains("NoSuchModule") && Err(HE).Contains("nomodule.qml:"), $"missing module: EVENT_ERROR names the module and the file ('{Err(HE)}')");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, notItem);
        CheckApply("SET QML_SOURCE root is not an Item");
        Flat(Frames(2));
        s.Check(Err(HE).Contains("root must be an Item"), $"root that is not an Item: error ('{Err(HE)}')");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, missing);
        CheckApply("SET QML_SOURCE missing file");
        Flat(Frames(2));
        s.Check(Err(HE).Contains("does-not-exist.qml") && Err(HE).Contains("not found"), $"missing file: error ('{Err(HE)}')");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, qmlTest);
        CheckApply("SET QML_SOURCE a good file");
        Flat(Frames(2));
        s.Check(Err(HE) == "", $"a good file clears the error ('{Err(HE)}')");
        // Another file forgets the values of the old one: fill and progress are not applied to QmlTypes.qml, which has neither.
        SetProps(E, p => { p.Color("fill", 0, 0, 1, 1); p.F32("progress", 0.5f); });
        CheckApply("SET fill + progress on QmlTest.qml");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, qmlTypes);
        CheckApply("SET QML_SOURCE another file");
        Flat(Frames(2));
        s.Check(Err(HE) == "", $"a new file gets none of the values of the old one ('{Err(HE)}')");
        SetProps(E, p => p.F32("r", 0.75f));
        CheckApply("SET r on QmlTypes.qml");
        recs = Poll();
        s.Check(Props(recs, HE, "rOut") is [float r75] && r75 == 0.75f && Err(HE) == "", $"... and takes its own values ({S(recs)}, '{Err(HE)}')");
        cmd.SetString(E, Protocol.PropertyId.QmlSource, "");
        CheckApply("SET QML_SOURCE empty");
        Flat(Frames(2));
        s.Check(Err(HE) == "", "empty source = nothing loaded, no error");
        cmd.Destroy(E);
        CheckApply("destroy the error element");

        // ---- 6. reload after a file change; a component next to the file ----------------------------------------------------
        var reloadFile = Write("reload.qml", testText);
        var badge = Write("Badge.qml", "import QtQuick\n\nRectangle { color: \"#ff00ff\" }\n");
        var outer = Write("outer.qml", "import QtQuick\n\nItem {\n    implicitWidth: 60\n    implicitHeight: 40\n    Badge { anchors.fill: parent }\n}\n");
        Qml(R, HR, reloadFile, 100, 300);
        Qml(O, HO, outer, 600, 450);
        CheckApply("create the reload elements");
        Flat(Frames(3));
        s.Check(Err(HR) == "" && Err(HO) == "", $"reload.qml and outer.qml load ('{Err(HR)}', '{Err(HO)}')");
        SetProps(R, p => { p.Color("fill", 0, 0, 1, 1); p.F32("progress", 0.5f); });
        CheckApply("SET reload element properties");
        Flat(Frames(80));
        using (var a = s.Grab(w, "qml_before_reload.png"))
        {
            s.Check(IsBlue(Px(a, 150, 330)) && IsWhite(Px(a, 150, 390)) && IsBlue(Px(a, 250, 390)), $"before the reload: blue, white bar half ({Px(a, 150, 330)}, {Px(a, 150, 390)}, {Px(a, 250, 390)})");
            s.Check(IsMagenta(Px(a, 620, 470)), $"Badge.qml next to outer.qml is magenta ({Px(a, 620, 470)})");
        }
        // The file changes on disk: wider implicit size, yellow bar. The patch bumps QML_RELOAD (the node does it when its watcher fires).
        File.WriteAllText(reloadFile, testText.Replace("implicitWidth: 200", "implicitWidth: 300").Replace("color: \"white\"", "color: \"yellow\""));
        File.WriteAllText(badge, "import QtQuick\n\nRectangle { color: \"#00ffff\" }\n");
        // The first reload of a fresh element and the next ones: the same checks.
        void ReloadChecks(string which, int counter)
        {
            cmd.SetI32(R, Protocol.PropertyId.QmlReload, counter);
            cmd.SetI32(O, Protocol.PropertyId.QmlReload, counter);
            CheckApply($"SET QML_RELOAD {counter}");
            var rs = Poll();
            rs.AddRange(Flat(Frames(80)));
            s.Check(Err(HR) == "" && Err(HO) == "", $"{which}: the changed files load ('{Err(HR)}', '{Err(HO)}')");
            s.Check(measured.TryGetValue(HR, out var mr) && mr == (300, 100), $"{which}: the new implicit size 300 x 100 is reported ({(measured.TryGetValue(HR, out var mr2) ? mr2.ToString() : "none")})");
            using (var a = s.Grab(w, $"qml_after_reload{counter}.png"))
            {
                s.Check(IsBlue(Px(a, 150, 330)) && IsBlue(Px(a, 380, 330)), $"{which}: the properties of the patch are applied again (blue, now 300 wide) ({Px(a, 150, 330)}, {Px(a, 380, 330)})");
                s.Check(IsYellow(Px(a, 150, 390)) && IsYellow(Px(a, 245, 390)) && IsBlue(Px(a, 260, 390)), $"{which}: the new file is drawn, yellow bar over half of 300 ({Px(a, 150, 390)}, {Px(a, 245, 390)}, {Px(a, 260, 390)})");
                s.Check(IsCyan(Px(a, 620, 470)), $"{which}: the component next to the file is read again (cyan) ({Px(a, 620, 470)})");
            }
            s.Check(Props(rs, HR, "steps") is [1], $"{which}: the new instance reports its own changes, steps = 1, not the old count ({string.Join(", ", Props(rs, HR, "steps"))})");
        }
        ReloadChecks("first reload (counter 0 -> 1)", 1);
        ReloadChecks("second reload (counter 1 -> 2)", 2);
        // A broken file: error, nothing drawn; the fixed file comes back.
        File.WriteAllText(reloadFile, "import QtQuick\n\nItem {\n    width: 10 +\n}\n");
        cmd.SetI32(R, Protocol.PropertyId.QmlReload, 3);
        CheckApply("SET QML_RELOAD 3 (broken file)");
        Flat(Frames(3));
        s.Check(Regex.IsMatch(Err(HR), @"reload\.qml:\d+:\d+: "), $"reload of a broken file: EVENT_ERROR with file:line ('{Err(HR)}')");
        using (var a = s.Grab(w, "qml_broken.png"))
            s.Check(IsBg(Px(a, 150, 330)) && IsBg(Px(a, 150, 390)), $"nothing of the old instance is drawn ({Px(a, 150, 330)})");
        File.WriteAllText(reloadFile, testText);
        cmd.SetI32(R, Protocol.PropertyId.QmlReload, 4);
        CheckApply("SET QML_RELOAD 4 (fixed file)");
        Flat(Frames(80));
        s.Check(Err(HR) == "", $"the fixed file loads, the error is cleared ('{Err(HR)}')");
        using (var a = s.Grab(w, "qml_fixed.png"))
            s.Check(IsBlue(Px(a, 150, 330)) && IsWhite(Px(a, 150, 390)) && IsBlue(Px(a, 250, 390)), $"the fixed file is drawn with the patch's properties ({Px(a, 150, 330)}, {Px(a, 150, 390)})");
        // The same counter again changes nothing; a new element with the same file still works (engine cache).
        cmd.SetI32(R, Protocol.PropertyId.QmlReload, 4);
        CheckApply("SET QML_RELOAD 4 again");
        recs = Flat(Frames(3));
        s.Check(Err(HR) == "" && PropNames(recs, HR).Count == 0, "the same counter does not reload");

        // ---- 7. Auto Size off: the size of the element is the size of the root -------------------------------------------------
        cmd.SetBool(Q, Protocol.PropertyId.AutoSize, false);
        cmd.SetVec2(Q, Protocol.PropertyId.Size, 300, 150);
        CheckApply("Auto Size off, Size 300 x 150");
        Flat(Frames(3));
        using (var a = s.Grab(w, "qml_size.png"))
        {
            s.Check(IsGreen(Px(a, 390, 150)) && IsBg(Px(a, 410, 150)) && IsBg(Px(a, 390, 255)), $"Auto Size off: the root takes the Size 300 x 150 ({Px(a, 390, 150)}, {Px(a, 410, 150)}, {Px(a, 390, 255)})");
            s.Check(IsWhite(Px(a, 390, 240)) && IsGreen(Px(a, 390, 225)), $"... the full bar sits at the bottom of the 150 high root and is 300 wide ({Px(a, 390, 240)}, {Px(a, 390, 225)})");
        }
        cmd.SetBool(Q, Protocol.PropertyId.AutoSize, true);
        CheckApply("Auto Size on");
        Flat(Frames(3));
        using (var a = s.Grab(w, "qml_autosize.png"))
            s.Check(IsGreen(Px(a, 290, 130)) && IsBg(Px(a, 350, 130)) && IsBg(Px(a, 150, 230)), $"Auto Size on again: back to the implicit size 200 x 100 ({Px(a, 350, 130)}, {Px(a, 150, 230)})");

        // ---- 7b. a value set every frame (an LFO, help patch "HowTo Use a QML file") ---------------------------------------------
        // The help example glides its bar with a Behavior: every new value retargets (restarts) the animation. Its start reached
        // the running animations of Qt only in processEvents, after the clock tick, so an animation restarted every frame was
        // never advanced and the bar stood still. The bar must follow the LFO with a small lag, every frame.
        var example = FindFile("help", "Qt", "QmlElement Example.qml");
        s.Check(example != null, $"help example found ({example})");
        if (example != null)
        {
            const uint L = 60, HL = 160;
            Qml(L, HL, example, 100, 480); // 240 x 56, the bar (22 high) at the bottom: y 514 .. 536
            SetProps(L, p => p.F32("value", 0));
            CheckApply("create QmlElement (QmlElement Example.qml) with value 0");
            Flat(Frames(40));
            s.Check(Err(HL) == "", $"example loads ('{Err(HL)}')");
            var percent = -1;
            var lag = new List<float>();
            var applyFailures = 0;
            for (int i = 1; i <= 180; i++)
            {
                // LFO 0..1 with a period of 4 s (240 frames at 60 fps): its rising part 0 .. 0.75, a new value every frame.
                var v = i / 240f;
                SetProps(L, p => p.F32("value", v));
                if (Expect("SET value") != 0) applyFailures++;
                if (Props(Frame(), HL, "percent") is { Count: > 0 } l) percent = (int)l[^1];
                if (i % 30 == 0) lag.Add(v * 100 - percent);
            }
            s.Check(applyFailures == 0, $"SET value every frame ({applyFailures} failures)");
            // OutCubic over 400 ms starts at 3x the linear speed: the bar trails a slow LFO by a few percent.
            s.Check(lag.All(d => d >= 0 && d < 8), $"the bar follows an LFO set every frame, lag in percent every 30 frames: {string.Join(", ", lag.Select(d => d.ToString("F1")))}");
            using (var a = s.Grab(w, "qml_lfo.png"))
            {
                // value 0.75, percent ~73: accent up to x = 100 + 240 * 0.73, track behind it.
                var accent = Px(a, 100 + (int)(240 * 0.65f), 525);
                var track = Px(a, 100 + (int)(240 * 0.81f), 525);
                s.Check(Near(accent, 0xff, 0x7a, 0x3d, 20) && Near(track, 0x2b, 0x2f, 0x3a, 20), $"the bar is drawn up to about 73 % (accent {accent}, track {track}, percent {percent})");
            }
            cmd.Destroy(L);
            CheckApply("destroy the LFO example");
            Flat(Frames(2));
        }

        // ---- 8. destroy ----------------------------------------------------------------------------------------------------------
        foreach (var id in new[] { Q, T, R, O })
            cmd.Destroy(id);
        CheckApply("destroy the QmlElements");
        Flat(Frames(6));
        using (var a = s.Grab(w, "qml_destroyed.png"))
            s.Check(IsBg(Px(a, 150, 130)) && IsBg(Px(a, 150, 330)) && IsBg(Px(a, 620, 470)), "destroyed: nothing is drawn any more");
        Qml(Q, HQ, qmlTest, 100, 100);
        CheckApply("create again after the destroy");
        Flat(Frames(3));
        using (var a = s.Grab(w, "qml_recreated.png"))
            s.Check(IsRed(Px(a, 150, 130)) && Err(HQ) == "", $"a new element with the same id and file works ({Px(a, 150, 130)}, '{Err(HQ)}')");
        cmd.Destroy(Q);
        CheckApply("destroy");
        Flat(Frames(3));
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }

    /// <summary>Entries of one SET_QML_PROPERTIES in the form of the test.</summary>
    private readonly struct QmlProps
    {
        private readonly CommandWriter _w;
        public QmlProps(CommandWriter w) => _w = w;
        public void F32(string name, float v) => _w.QmlPropertyF32(name, v);
        public void I32(string name, int v) => _w.QmlPropertyI32(name, v);
        public void Bool(string name, bool v) => _w.QmlPropertyBool(name, v);
        public void String(string name, string v) => _w.QmlPropertyString(name, v);
        public void Color(string name, float r, float g, float b, float a) => _w.QmlPropertyColor(name, r, g, b, a);
        public void Vec2(string name, float x, float y) => _w.QmlPropertyVec2(name, x, y);
        public void Vec3(string name, float x, float y, float z) => _w.QmlPropertyVec3(name, x, y, z);
        public void Vec4(string name, float x, float y, float z, float w) => _w.QmlPropertyVec4(name, x, y, z, w);
        public void Rect(string name, float x, float y, float width, float height) => _w.QmlPropertyRect(name, x, y, width, height);
        public void ListF32(string name, ushort elementType, params float[] values) => _w.QmlPropertyListF32(name, elementType, values);
        public void ListI32(string name, params int[] values) => _w.QmlPropertyListI32(name, values);
        public void ListBool(string name, params bool[] values) => _w.QmlPropertyListBool(name, values);
        public void ListString(string name, params string[] values) => _w.QmlPropertyListString(name, values);
        public void ListDateTime(string name, params long[] values) => _w.QmlPropertyListDateTime(name, values);
        public void DateTime(string name, long milliseconds) => _w.QmlPropertyDateTime(name, milliseconds);
        public void Font(string name, string family, float pixelSize, int weight, bool italic) => _w.QmlPropertyFont(name, family, pixelSize, weight, italic);
    }
}