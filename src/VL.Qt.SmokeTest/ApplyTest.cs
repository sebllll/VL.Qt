using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using VL.Qt;

// Tests of vlqt_window_apply / vlqt_window_poll (plan 01c). Buffers are built by a tiny local
// encoder so this test does not depend on CommandWriter (01b).
static class ApplyTest
{
    sealed class Buf
    {
        byte[] _b = new byte[1 << 16];
        public int Length;
        public ReadOnlySpan<byte> Span => _b.AsSpan(0, Length);
        public Buf Clear() { Length = 0; return this; }

        Span<byte> Begin(ushort op, int payloadLen)
        {
            var size = (4 + payloadLen + 3) & ~3;
            if (Length + size > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, Length + size));
            var s = _b.AsSpan(Length, size);
            s.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(s, op);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)payloadLen);
            Length += size;
            return s[4..];
        }

        /// <summary>Raw command with an arbitrary declared payloadLen (for error tests).</summary>
        public Buf Raw(ushort op, int declaredLen, int actualLen)
        {
            var s = Begin(op, actualLen);
            BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(Length - ((4 + actualLen + 3) & ~3) + 2), (ushort)declaredLen);
            return this;
        }
        public Buf Create(uint id, ushort type)
        {
            var s = Begin(Protocol.Op.Create, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], type);
            return this;
        }
        public Buf Destroy(uint id) { BinaryPrimitives.WriteUInt32LittleEndian(Begin(Protocol.Op.Destroy, 4), id); return this; }
        public Buf Reparent(uint id, uint parent, uint index)
        {
            var s = Begin(Protocol.Op.Reparent, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], parent);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], index);
            return this;
        }
        public Buf Bind(uint id, uint sink, ushort flags)
        {
            var s = Begin(Protocol.Op.Bind, 10);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], sink);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], flags);
            return this;
        }
        public Buf ClearColor(float r, float g, float b, float a = 1)
        {
            var s = Begin(Protocol.Op.ClearColor, 16);
            WriteFloats(s, r, g, b, a);
            return this;
        }
        Span<byte> SetHeader(uint id, ushort prop, ushort vt, int valueLen)
        {
            var s = Begin(Protocol.Op.Set, 8 + valueLen);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], prop);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], vt);
            return s[8..];
        }
        public Buf SetFloats(uint id, ushort prop, ushort vt, params float[] f) { WriteFloats(SetHeader(id, prop, vt, f.Length * 4), f); return this; }
        public Buf SetBool(uint id, ushort prop, bool b) { SetHeader(id, prop, Protocol.Vt.Bool, 1)[0] = (byte)(b ? 1 : 0); return this; }
        public Buf SetRef(uint id, ushort prop, uint refId) { BinaryPrimitives.WriteUInt32LittleEndian(SetHeader(id, prop, Protocol.Vt.Ref, 4), refId); return this; }
        public Buf SetI32(uint id, ushort prop, int v) { BinaryPrimitives.WriteInt32LittleEndian(SetHeader(id, prop, Protocol.Vt.I32, 4), v); return this; }
        public Buf Size(uint id, float w, float h) => SetFloats(id, Protocol.Prop.Size, Protocol.Vt.Vec2, w, h);
        // #469: no position property any more: SIZE + ANCHOR TopLeft + TRANSFORM translation.
        public Buf Place(uint id, float x, float y, float w, float h) => Size(id, w, h).SetI32(id, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft)
            .SetFloats(id, Protocol.Prop.Transform, Protocol.Vt.Mat4, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, 0, 1);
        public Buf Color(uint id, float r, float g, float b, float a = 1) => SetFloats(id, Protocol.Prop.Color, Protocol.Vt.Color, r, g, b, a);

        static void WriteFloats(Span<byte> s, params float[] f)
        {
            for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(i * 4)..], f[i]);
        }
    }

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);

    static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 8 && Math.Abs(c.G - g) < 8 && Math.Abs(c.B - b) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt apply test");
        check(w != IntPtr.Zero, "apply: window_create");
        Native.vlqt_window_show(w);
        var b = new Buf();
        int Apply(Buf buf) => Native.Apply(w, buf.Span);

        // Scene: grey background, red rect at root, green rect inside an empty ITEM group.
        var rc = Apply(b.Clear()
            .ClearColor(0.125f, 0.125f, 0.125f)
            .Create(1, Protocol.Type.Rectangle).Place(1, 100, 100, 200, 150).Color(1, 1, 0, 0).Reparent(1, 0, 0)
            .Create(2, Protocol.Type.Item).Place(2, 400, 300, 300, 200).Reparent(2, 0, 1)
            .Create(3, Protocol.Type.Rectangle).Place(3, 0, 0, 100, 100).Color(3, 0, 1, 0).Reparent(3, 2, 0)
            .Create(4, Protocol.Type.Rectangle).Place(4, 150, 120, 100, 100).Color(4, 0, 0, 1).Reparent(4, 0, 0)
            .SetFloats(1, Protocol.Prop.Opacity, Protocol.Vt.F32, 1f).SetBool(2, Protocol.Prop.Clip, false)
            .Bind(1, 42, 0));
        check(rc == 0, $"apply: build scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "apply1.png")))
        {
            check(Near(Px(a, 200, 175), 255, 0, 0), $"apply: red rect on top of blue (index 0) ({Px(a, 200, 175)})");
            check(Near(Px(a, 450, 350), 0, 255, 0), $"apply: green rect inside group at 400,300 ({Px(a, 450, 350)})");
            check(Near(Px(a, 5, 5), 32, 32, 32), $"apply: CLEAR_COLOR background ({Px(a, 5, 5)})");
        }

        // Stacking: blue to the top; mat4 row-major like Stride (translation in row 4) moves the group (anchor TopLeft: 400,300 -> 500,300).
        rc = Apply(b.Clear().Reparent(4, 0, 99)
            .SetFloats(2, Protocol.Prop.Transform, Protocol.Vt.Mat4, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 500, 300, 0, 1));
        check(rc == 0, $"apply: reparent to top + transform ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        pump(3);
        using (var a = grab(w, Path.Combine(outDir, "apply2.png")))
        {
            check(Near(Px(a, 200, 175), 0, 0, 255), $"apply: blue on top after REPARENT index 99 ({Px(a, 200, 175)})");
            check(Near(Px(a, 550, 350), 0, 255, 0) && Near(Px(a, 450, 350), 32, 32, 32), $"apply: transform translates group ({Px(a, 550, 350)})");
        }

        // DESTROY is recursive: child 3 is released together with group 2.
        check(Apply(b.Clear().Destroy(2)) == 0, "apply: destroy group");
        rc = Apply(b.Clear().Color(3, 1, 1, 1));
        check(rc == -4, $"apply: child id released by recursive DESTROY ({rc}: {Native.LastError()})");

        // Error cases: rc < 0, message names command index + reason, no crash.
        void Err(string what, Buf buf, int expected, string mustContain)
        {
            var r = Apply(buf);
            var e = Native.LastError();
            check(r == expected && e.Contains(mustContain), $"apply error: {what} -> {r} \"{e}\"");
        }
        Err("truncated buffer", b.Clear().Raw(Protocol.Op.Destroy, 40, 4), -2, "exceeds buffer");
        Err("wrong payloadLen", b.Clear().Raw(Protocol.Op.Destroy, 8, 8), -2, "expected 4");
        Err("wrong value length", b.Clear().Create(5, Protocol.Type.Rectangle).SetFloats(5, Protocol.Prop.Size, Protocol.Vt.Vec2, 1, 2, 3), -2, "command #1");
        Err("unknown op", b.Clear().Raw(99, 0, 0), -3, "unknown opcode");
        Err("unknown id", b.Clear().Place(777, 0, 0, 1, 1), -4, "unknown id 777");
        Err("unknown parent", b.Clear().Reparent(1, 777, 0), -4, "unknown parent id 777");
        Err("unknown ref", b.Clear().SetRef(1, Protocol.Prop.Size, 777), -4, "unknown ref id 777");
        Err("unknown type", b.Clear().Create(6, 999), -5, "unknown type 999");
        Err("unknown property", b.Clear().Create(7, Protocol.Type.Item).Color(7, 1, 1, 1), -6, "not supported by type ITEM");
        Err("value type mismatch", b.Clear().SetFloats(1, Protocol.Prop.Size, Protocol.Vt.F32, 1), -7, "expects value type");
        Err("duplicate id", b.Clear().Create(1, Protocol.Type.Item), -8, "already exists");
        Err("create id 0", b.Clear().Create(0, Protocol.Type.Item), -8, "window root");
        Err("reparent into own subtree", b.Clear().Create(8, Protocol.Type.Item).Reparent(8, 1, 0).Reparent(1, 8, 0), -8, "command #2");
        Err("bind unknown id", b.Clear().Bind(777, 1, 0), -4, "command #0");
        check(Native.Apply(IntPtr.Zero, b.Span) == -1, "apply: null window -> -1");

        // Structured error (#92/#275): object id, op and property of the bad command, reason without prefix.
        Native.vlqt_apply_error ae = default;
        rc = Apply(b.Clear().Create(9, Protocol.Type.Item).SetFloats(9, Protocol.Prop.Size, Protocol.Vt.F32, 1));
        check(rc == -7 && Native.vlqt_window_get_apply_error(w, out ae) == 0 && ae.rc == -7 && ae.command == 1 && ae.id == 9
            && ae.op == Protocol.Op.Set && ae.prop == Protocol.Prop.Size && ae.Reason.StartsWith("property ")
            && Native.LastError().Contains("id 9"), $"apply error id: SET -> id {ae.id} op {ae.op} prop {ae.prop} \"{ae.Reason}\"");
        Err("unknown property id", b.Clear().Color(9, 1, 1, 1), -6, "id 9");
        check(Native.vlqt_window_get_apply_error(w, out ae) == 0 && ae.id == 9 && ae.prop == Protocol.Prop.Color, $"apply error id: -6 -> {ae.id}/{ae.prop}");
        Err("clear color, no id", b.Clear().Raw(Protocol.Op.ClearColor, 4, 4), -2, "expected 16");
        check(Native.vlqt_window_get_apply_error(w, out ae) == 0 && ae.id == 0 && ae.op == Protocol.Op.ClearColor, $"apply error id: CLEAR_COLOR -> id {ae.id}");
        check(Apply(b.Clear().Destroy(9)) == 0 && Native.vlqt_window_get_apply_error(w, out ae) == 0 && ae.rc == 0 && ae.id == 0 && ae.Reason.Length == 0,
            "apply error id: cleared by a successful apply");
        check(Native.vlqt_window_get_apply_error(IntPtr.Zero, out _) == -1, "apply error id: null window -> -1");
        check(Apply(b.Clear()) == 0, "apply: empty buffer ok");

        // Earlier commands of a failing buffer stay applied.
        rc = Apply(b.Clear().Color(4, 1, 1, 0).Place(777, 0, 0, 1, 1).Color(4, 1, 0, 1));
        check(rc == -4 && Native.LastError().Contains("command #1"), $"apply: stops at bad command #1 ({rc})");
        pump(3);
        using (var a = grab(w, Path.Combine(outDir, "apply3.png")))
            check(Near(Px(a, 200, 175), 255, 255, 0), $"apply: command before the error stays applied (yellow) ({Px(a, 200, 175)})");

        // Poll: id 1 is bound (handle 42) -> EV_MEASURED + EV_SCENE_BOUNDS once (06b), plus the first
        // EV_WINDOW_GEOMETRY of the window (40 bytes, #100, client position #465), then nothing.
        var ev = new byte[128];
        int need = Native.Poll(w, Span<byte>.Empty);
        check(need == -(20 + 28 + 40), $"poll: cap 0 -> -required ({need})");
        check(Native.Poll(w, ev) == 20 + 28 + 40, "poll: measured + scene bounds of the bound item + window geometry");
        check(Native.Poll(w, ev) == 0, "poll: 0 bytes (nothing changed)");
        check(Native.Poll(IntPtr.Zero, ev) == -1, "poll: null window -> -1");

        // Perf: 10 000 SETs (SIZE on 100 items) per apply.
        b.Clear();
        for (uint i = 0; i < 100; i++) b.Create(1000 + i, Protocol.Type.Rectangle).Reparent(1000 + i, 0, 1000);
        check(Apply(b) == 0, "apply: create 100 perf items");
        var times = new double[20];
        for (int run = 0; run < times.Length; run++)
        {
            b.Clear();
            for (int i = 0; i < 10_000; i++)
                b.Size(1000 + (uint)(i % 100), 10 + run + i % 7, 10);
            var sw = Stopwatch.StartNew();
            rc = Apply(b);
            times[run] = sw.Elapsed.TotalMilliseconds;
            if (rc != 0) { check(false, "apply perf " + Native.LastError()); break; }
        }
        Array.Sort(times);
        var median = times[times.Length / 2];
        Console.WriteLine($"INFO apply 10000 SET (size, {b.Length} bytes): median {median:F3} ms, min {times[0]:F3}, max {times[^1]:F3}");
        PerfLimits.Below(check, median, 2.0, $"apply: 10000 SET < 2 ms (median {median:F3} ms)");

        b.Clear();
        for (int i = 0; i < 10_000; i++)
            b.Color(1000 + (uint)(i % 100), (i % 256) / 255f, 0.5f, 0.5f);
        var sw2 = Stopwatch.StartNew();
        rc = Apply(b);
        Console.WriteLine($"INFO apply 10000 SET (color via QML property): {sw2.Elapsed.TotalMilliseconds:F3} ms (rc {rc})");

        // RESET (#92): every object goes at once (nested and unparented ones too), ids are free again, the clear color stays.
        b.Clear().ClearColor(0.5f, 0, 0).Create(2000, Protocol.Type.Item).Reparent(2000, 0, 0).Create(2001, Protocol.Type.Rectangle)
            .Reparent(2001, 2000, 0).Place(2001, 0, 0, 800, 600).Color(2001, 0, 1, 0).Create(2002, Protocol.Type.Rectangle);
        check(Apply(b) == 0, "reset: build nested + unparented objects");
        rc = Apply(b.Clear().Raw(Protocol.Op.Reset, 0, 0));
        check(rc == 0, $"reset: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        foreach (uint id in new uint[] { 1, 1000, 2000, 2001, 2002 })
            check(Apply(b.Clear().Destroy(id)) == -4, $"reset: id {id} gone");
        check(Apply(b.Clear().Create(2001, Protocol.Type.Item).Create(1, Protocol.Type.Item)) == 0, "reset: ids reusable");
        Err("reset with payload", b.Clear().Raw(Protocol.Op.Reset, 4, 4), -2, "expected 0");
        pump(3);
        using (var a = grab(w, Path.Combine(outDir, "apply_reset.png")))
            check(Near(Px(a, 400, 300), 128, 0, 0), $"reset: scene empty, clear color kept ({Px(a, 400, 300)})");

        pump(2);
        Native.vlqt_window_destroy(w); // releases all remaining ids
    }
}
