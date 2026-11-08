using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using VL.Qt;

// Back channel EV_MEASURED / EV_SCENE_BOUNDS (plan 06b). Values are DIP (logical pixels), the C# SinkRegistry
// converts them to units. Bound items report on the first poll after BIND, afterwards only on change.
static class MeasureTest
{
    internal readonly record struct Rec(ushort Event, uint Handle, uint Index, float[] V);

    // Own reader (EventReader is internal): u16 event, u16 len, u32 handle, u32 index, payload, 4-aligned.
    internal static List<Rec> Parse(ReadOnlySpan<byte> b, Action<bool, string> check)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off < b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length) { check(false, $"06b: malformed record at {off}"); break; }
            var v = new float[(len - 8) / 4];
            for (int i = 0; i < v.Length; i++)
                v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off + 12 + 4 * i));
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)), BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)), v));
            off += (4 + len + 3) & ~3;
        }
        return list;
    }

    /// <summary>Text of the last EV_ERROR record of <paramref name="handle"/> in a poll buffer (#273), null if none.</summary>
    internal static string? ErrorText(ReadOnlySpan<byte> b, uint handle)
    {
        string? text = null;
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length) break;
            if (ev == Protocol.Ev.Error && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)) == handle)
                text = System.Text.Encoding.UTF8.GetString(b.Slice(off + 12, len - 8));
            off += (4 + len + 3) & ~3;
        }
        return text;
    }

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt measure test");
        check(w != IntPtr.Zero, "06b: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 20];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"06b: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Poll()
        {
            int n = Native.Poll(w, buf);
            check(n >= 0, $"06b: poll ({n})");
            var recs = Parse(buf.AsSpan(0, Math.Max(n, 0)), check);
            recs.RemoveAll(r => r.Event == Protocol.Ev.WindowGeometry); // window record (handle 0, #100), not an item
            return recs;
        }
        Rec? Find(List<Rec> recs, ushort ev, uint handle)
        {
            foreach (var r in recs)
                if (r.Event == ev && r.Handle == handle) return r;
            return null;
        }
        static string S(Rec? r) => r is { } x ? string.Join(", ", x.V) : "none";

        const uint TextId = 1, ColId = 2, PlainId = 3;
        const uint HText = 7;
        cmd.Reset();
        cmd.Create(TextId, Protocol.Type.Text);
        cmd.SetPlace(TextId, 50, 40, 400, 60);
        cmd.SetString(TextId, Protocol.Prop.Text, "Hello");
        cmd.SetF32(TextId, Protocol.Prop.FontSize, 40);
        cmd.Reparent(TextId, 0, 0);
        cmd.Bind(TextId, HText, 0);
        cmd.Create(PlainId, Protocol.Type.Rectangle); // not bound: never reports
        cmd.SetPlace(PlainId, 600, 40, 50, 50);
        cmd.Reparent(PlainId, 0, 1);
        // Column at (100, 200), padding 5, spacing 10 with three bound 120x40 children (handles 11..13).
        cmd.Create(ColId, Protocol.Type.Column);
        cmd.SetPlace(ColId, 100, 200, 0, 0);
        cmd.SetF32(ColId, Protocol.Prop.Spacing, 10);
        cmd.SetF32(ColId, Protocol.Prop.Padding, 5);
        cmd.Reparent(ColId, 0, 2);
        for (uint i = 0; i < 3; i++)
        {
            cmd.Create(10 + i, Protocol.Type.Rectangle);
            cmd.SetPlace(10 + i, 0, 0, 120, 40);
            cmd.Reparent(10 + i, ColId, i);
            cmd.Bind(10 + i, 11 + i, 0);
        }
        CheckApply("measure scene");
        pump(2);

        var recs = Poll();
        var m = Find(recs, Protocol.Ev.Measured, HText);
        check(m is { } mm && mm.V.Length == 2 && mm.V[0] > 40 && mm.V[1] > 30 && mm.Index == 0,
            $"06b: Text 'Hello' 40 -> MEASURED > 0 ({S(m)})");
        var tb = Find(recs, Protocol.Ev.SceneBounds, HText);
        check(tb is { } tbx && tbx.V.Length == 4 && tbx.V[0] == 50 && tbx.V[1] == 40 && tbx.V[2] == 400 && tbx.V[3] == 60,
            $"06b: Text SCENE_BOUNDS = bounds (50, 40, 400, 60) ({S(tb)})");
        bool colOk = true;
        for (uint i = 0; i < 3; i++)
        {
            var b = Find(recs, Protocol.Ev.SceneBounds, 11 + i);
            colOk &= b is { } bb && bb.V[0] == 105 && bb.V[1] == 205 + 50 * i && bb.V[2] == 120 && bb.V[3] == 40;
            if (!colOk) { check(false, $"06b: column child {i} SCENE_BOUNDS = layout position ({S(b)})"); break; }
        }
        if (colOk) check(true, "06b: column children SCENE_BOUNDS = layout position (105, 205/255/305)");
        check(recs.Count == 8, $"06b: one MEASURED + SCENE_BOUNDS per bound item, unbound item silent ({recs.Count} records)");

        bool stable = true;
        for (int f = 0; f < 5; f++) { pump(1); stable &= Poll().Count == 0; }
        check(stable, "06b: values stable over 5 frames -> no records");

        // Text change -> new MEASURED in the next poll (same frame after processEvents), bounds unchanged.
        cmd.SetString(TextId, Protocol.Prop.Text, "Hello World");
        CheckApply("text change");
        pump(1);
        recs = Poll();
        var m2 = Find(recs, Protocol.Ev.Measured, HText);
        check(m2 is { } x2 && m is { } x1 && x2.V[0] > x1.V[0] && x2.V[1] == x1.V[1] && recs.Count == 1,
            $"06b: text change -> new MEASURED only ({S(m)} -> {S(m2)}, {recs.Count} records)");

        // Layout change: spacing 0 moves children 1 and 2; child 0 stays.
        cmd.SetF32(ColId, Protocol.Prop.Spacing, 0);
        CheckApply("spacing change");
        pump(1);
        recs = Poll();
        var c1 = Find(recs, Protocol.Ev.SceneBounds, 12);
        var c2 = Find(recs, Protocol.Ev.SceneBounds, 13);
        check(recs.Count == 2 && c1 is { } y1 && y1.V[1] == 245 && c2 is { } y2 && y2.V[1] == 285,
            $"06b: column relayout -> SCENE_BOUNDS of moved children only ({S(c1)}; {S(c2)}; {recs.Count} records)");

        // Rebind: flags only -> silent; new handle -> current values again; unbind -> silent; rebind after unbind -> resend.
        cmd.Bind(10, 11, 0);
        CheckApply("bind flags only");
        pump(1);
        check(Poll().Count == 0, "06b: flag-only BIND (same handle) -> no records");
        cmd.Bind(10, 21, Protocol.Bind.CancelPress);
        cmd.Bind(11, 0, 0);
        CheckApply("rebind + unbind");
        pump(1);
        recs = Poll();
        check(recs.Count == 2 && Find(recs, Protocol.Ev.Measured, 21) is { } nm && nm.V[0] == 0 && nm.V[1] == 0
            && Find(recs, Protocol.Ev.SceneBounds, 21) is { } nb && nb.V[1] == 205,
            $"06b: new handle gets current values, unbound child silent ({recs.Count} records)");
        cmd.SetPlace(11, 0, 0, 130, 40);
        cmd.Bind(11, 22, 0);
        CheckApply("rebind after unbind");
        pump(1);
        recs = Poll();
        check(recs.Count == 2 && Find(recs, Protocol.Ev.SceneBounds, 22) is { } rb && rb.V[2] == 130,
            $"06b: rebind after unbind -> resend ({recs.Count} records)");

        // Transform: SCENE_BOUNDS is the axis-aligned box in the scene (rotated 90 deg about its center).
        var rot = Stride.Core.Mathematics.Matrix.RotationZ(Stride.Core.Mathematics.MathUtil.PiOverTwo); // ANCHOR Center via SetPlaceXf (#88, #469)

        cmd.SetPlaceXf(TextId, 50, 40, 400, 60, rot);
        CheckApply("text transform");
        pump(1);
        var tr = Find(Poll(), Protocol.Ev.SceneBounds, HText);
        check(tr is { } r && Math.Abs(r.V[0] - 220) < 0.01f && Math.Abs(r.V[1] + 130) < 0.01f && Math.Abs(r.V[2] - 60) < 0.01f && Math.Abs(r.V[3] - 400) < 0.01f,
            $"06b: rotated item -> axis-aligned scene box (220, -130, 60, 400) ({S(tr)})");

        // -required: events stay queued until the buffer is big enough.
        cmd.SetString(TextId, Protocol.Prop.Text, "Hi");
        CheckApply("text change 2");
        pump(1);
        int need = Native.Poll(w, buf.AsSpan(0, 4));
        int got = Native.Poll(w, buf);
        check(need <= -8 && got == -need && Poll().Count == 0, $"06b: poll with small cap -> -required ({need}), then {got} bytes, then empty");

        // Destroy: bound items vanish silently.
        cmd.Destroy(ColId);
        cmd.Destroy(TextId);
        CheckApply("destroy bound");
        pump(1);
        check(Poll().Count == 0, "06b: destroyed bound items -> no records");

        // Cost: 1000 bound rectangles in groups of 10 (depth 2). Check after processEvents, no change vs. all moved.
        cmd.Create(ColId, Protocol.Type.Item);
        cmd.Reparent(ColId, 0, 0);
        for (uint g = 0; g < 100; g++)
        {
            cmd.Create(100 + g, Protocol.Type.Item);
            cmd.SetPlace(100 + g, g % 10 * 70, g / 10 * 50, 70, 50);
            cmd.Reparent(100 + g, ColId, g);
            for (uint i = 0; i < 10; i++)
            {
                uint id = 1000 + g * 10 + i;
                cmd.Create(id, Protocol.Type.Rectangle);
                cmd.SetPlace(id, i * 7, 0, 6, 6);
                cmd.Reparent(id, 100 + g, i);
                cmd.Bind(id, id, 0);
            }
        }
        CheckApply("1000 bound items");
        pump(2);
        int first = Native.Poll(w, buf);
        check(first == 1000 * (20 + 28), $"06b: 1000 bound items -> 2000 records on the first poll ({first} bytes)");
        var idle = new double[101];
        var moved = new double[101];
        // #64: best of up to 3 rounds of the idle polls (see PerfLimits.Best); the moved polls run once.
        var idleMedian = PerfLimits.Best(0.2, () =>
        {
            for (int f = 0; f < idle.Length; f++)
            {
                pump(1);
                var t = Stopwatch.StartNew();
                int n = Native.Poll(w, buf);
                idle[f] = t.Elapsed.TotalMilliseconds;
                if (n != 0) { check(false, $"06b: idle poll returned {n}"); break; }
            }
            Array.Sort(idle);
            return idle[50];
        }, cpuBound: true);
        for (int f = 0; f < moved.Length; f++)
        {
            cmd.SetPlace(ColId, 1 - f % 2, 0, 0, 0); // starts at x 0 -> every frame moves
            CheckApply("move all");
            pump(1);
            var t = Stopwatch.StartNew();
            int n = Native.Poll(w, buf);
            moved[f] = t.Elapsed.TotalMilliseconds;
            if (n != 1000 * 28) { check(false, $"06b: moved poll returned {n}, expected {1000 * 28}"); break; }
        }
        Array.Sort(moved);
        Console.WriteLine($"INFO 06b poll 1000 bound items: unchanged median {idleMedian:F3} ms (last round max {idle[^1]:F3}), " +
            $"all bounds changed median {moved[50]:F3} ms (max {moved[^1]:F3})");
        PerfLimits.Below(check, idleMedian, 0.2, $"06b: change check < 0.2 ms / 1000 items (median {idleMedian:F3} ms)", cpuBound: true);
        Native.vlqt_window_destroy(w);
    }
}
