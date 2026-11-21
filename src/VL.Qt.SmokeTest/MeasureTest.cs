using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// Back channel EVENT_MEASURED / EVENT_SCENE_BOUNDS (plan 06b). Values are DIP (logical pixels), the C# FeedbackRouter
// converts them to units. Bound items report on the first poll after BIND, afterwards only on change.
[TestFixture, NonParallelizable]
public static class MeasureTest
{
    /// <summary>Measuring of items and the cost of polling.</summary>
    [Test, Category("measure")]
    public static void Measure() => Scenario.Run(Run);

    internal readonly record struct Rec(ushort Event, uint Handle, float[] V);

    // Own reader (EventReader is internal): u16 event, u16 flags, u32 len, u32 handle, payload, 4-aligned.
    internal static List<Rec> Parse(ReadOnlySpan<byte> b, Scenario s) => Parse(b, s.Check);

    internal static List<Rec> Parse(ReadOnlySpan<byte> b, Action<bool, string> check)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off < b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length) { check(false, $"malformed record at {off}"); break; }
            var v = new float[(len - 4) / 4];
            for (int i = 0; i < v.Length; i++)
                v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off + 12 + 4 * i));
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)), v));
            off += (8 + len + 3) & ~3;
        }
        return list;
    }

    /// <summary>Text of the last EVENT_ERROR record of <paramref name="handle"/> in a poll buffer, null if none.</summary>
    internal static string? ErrorText(ReadOnlySpan<byte> b, uint handle)
    {
        string? text = null;
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length) break;
            if (ev == Protocol.EventId.Error && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)) == handle)
                text = System.Text.Encoding.UTF8.GetString(b.Slice(off + 12, len - 4));
            off += (8 + len + 3) & ~3;
        }
        return text;
    }

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt measure test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 20];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Poll()
        {
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"poll ({n})");
            var recs = Parse(buf.AsSpan(0, Math.Max(n, 0)), s);
            recs.RemoveAll(r => r.Event == Protocol.EventId.WindowGeometry); // window record (handle 0), not an item
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
        cmd.Create(TextId, Protocol.ElementType.Text);
        cmd.SetPlace(TextId, 50, 40, 400, 60);
        cmd.SetString(TextId, Protocol.PropertyId.Text, "Hello");
        cmd.SetF32(TextId, Protocol.PropertyId.FontSize, 40);
        cmd.Reparent(TextId, 0, 0);
        cmd.Bind(TextId, HText);
        cmd.Create(PlainId, Protocol.ElementType.Rectangle); // not bound: never reports
        cmd.SetPlace(PlainId, 600, 40, 50, 50);
        cmd.Reparent(PlainId, 0, 1);
        // Column at (100, 200), padding 5, spacing 10 with three bound 120x40 children (handles 11..13).
        cmd.Create(ColId, Protocol.ElementType.Column);
        cmd.SetPlace(ColId, 100, 200, 0, 0);
        cmd.SetF32(ColId, Protocol.PropertyId.Spacing, 10);
        cmd.SetF32(ColId, Protocol.PropertyId.Padding, 5);
        cmd.Reparent(ColId, 0, 2);
        for (uint i = 0; i < 3; i++)
        {
            cmd.Create(10 + i, Protocol.ElementType.Rectangle);
            cmd.SetPlace(10 + i, 0, 0, 120, 40);
            cmd.Reparent(10 + i, ColId, i);
            cmd.Bind(10 + i, 11 + i);
        }
        CheckApply("measure scene");
        s.Pump(2);

        var recs = Poll();
        var m = Find(recs, Protocol.EventId.Measured, HText);
        s.Check(m is { } mm && mm.V.Length == 2 && mm.V[0] > 40 && mm.V[1] > 30,
            $"Text 'Hello' 40 -> MEASURED > 0 ({S(m)})");
        var tb = Find(recs, Protocol.EventId.SceneBounds, HText);
        s.Check(tb is { } tbx && tbx.V.Length == 4 && tbx.V[0] == 50 && tbx.V[1] == 40 && tbx.V[2] == 400 && tbx.V[3] == 60,
            $"Text SCENE_BOUNDS = bounds (50, 40, 400, 60) ({S(tb)})");
        bool colOk = true;
        for (uint i = 0; i < 3; i++)
        {
            var b = Find(recs, Protocol.EventId.SceneBounds, 11 + i);
            colOk &= b is { } bb && bb.V[0] == 105 && bb.V[1] == 205 + 50 * i && bb.V[2] == 120 && bb.V[3] == 40;
            if (!colOk) { s.Check(false, $"column child {i} SCENE_BOUNDS = layout position ({S(b)})"); break; }
        }
        if (colOk) s.Check(true, "column children SCENE_BOUNDS = layout position (105, 205/255/305)");
        s.Check(recs.Count == 8, $"one MEASURED + SCENE_BOUNDS per bound item, unbound item silent ({recs.Count} records)");

        bool stable = true;
        for (int f = 0; f < 5; f++) { s.Pump(1); stable &= Poll().Count == 0; }
        s.Check(stable, "values stable over 5 frames -> no records");

        // Text change -> new MEASURED in the next poll (same frame after processEvents), bounds unchanged.
        cmd.SetString(TextId, Protocol.PropertyId.Text, "Hello World");
        CheckApply("text change");
        s.Pump(1);
        recs = Poll();
        var m2 = Find(recs, Protocol.EventId.Measured, HText);
        s.Check(m2 is { } x2 && m is { } x1 && x2.V[0] > x1.V[0] && x2.V[1] == x1.V[1] && recs.Count == 1,
            $"text change -> new MEASURED only ({S(m)} -> {S(m2)}, {recs.Count} records)");

        // Layout change: spacing 0 moves children 1 and 2; child 0 stays.
        cmd.SetF32(ColId, Protocol.PropertyId.Spacing, 0);
        CheckApply("spacing change");
        s.Pump(1);
        recs = Poll();
        var c1 = Find(recs, Protocol.EventId.SceneBounds, 12);
        var c2 = Find(recs, Protocol.EventId.SceneBounds, 13);
        s.Check(recs.Count == 2 && c1 is { } y1 && y1.V[1] == 245 && c2 is { } y2 && y2.V[1] == 285,
            $"column relayout -> SCENE_BOUNDS of moved children only ({S(c1)}; {S(c2)}; {recs.Count} records)");

        // Rebind: same handle -> silent; new handle -> current values again; unbind -> silent; rebind after unbind -> resend.
        cmd.Bind(10, 11);
        CheckApply("bind same handle");
        s.Pump(1);
        s.Check(Poll().Count == 0, "BIND again with the same handle -> no records");
        cmd.Bind(10, 21);
        cmd.Bind(11, 0);
        CheckApply("rebind + unbind");
        s.Pump(1);
        recs = Poll();
        s.Check(recs.Count == 2 && Find(recs, Protocol.EventId.Measured, 21) is { } nm && nm.V[0] == 0 && nm.V[1] == 0
            && Find(recs, Protocol.EventId.SceneBounds, 21) is { } nb && nb.V[1] == 205,
            $"new handle gets current values, unbound child silent ({recs.Count} records)");
        cmd.SetPlace(11, 0, 0, 130, 40);
        cmd.Bind(11, 22);
        CheckApply("rebind after unbind");
        s.Pump(1);
        recs = Poll();
        s.Check(recs.Count == 2 && Find(recs, Protocol.EventId.SceneBounds, 22) is { } rb && rb.V[2] == 130,
            $"rebind after unbind -> resend ({recs.Count} records)");

        // Transform: SCENE_BOUNDS is the axis-aligned box in the scene (rotated 90 deg about its center).
        var rot = Stride.Core.Mathematics.Matrix.RotationZ(Stride.Core.Mathematics.MathUtil.PiOverTwo); // ANCHOR Center via SetPlaceXf

        cmd.SetPlaceXf(TextId, 50, 40, 400, 60, rot);
        CheckApply("text transform");
        s.Pump(1);
        var tr = Find(Poll(), Protocol.EventId.SceneBounds, HText);
        s.Check(tr is { } r && Math.Abs(r.V[0] - 220) < 0.01f && Math.Abs(r.V[1] + 130) < 0.01f && Math.Abs(r.V[2] - 60) < 0.01f && Math.Abs(r.V[3] - 400) < 0.01f,
            $"rotated item -> axis-aligned scene box (220, -130, 60, 400) ({S(tr)})");

        // BufferTooSmall with the required size: events stay queued until the buffer is big enough.
        cmd.SetString(TextId, Protocol.PropertyId.Text, "Hi");
        CheckApply("text change 2");
        s.Pump(1);
        int small = NativeCalls.Poll(w, buf.AsSpan(0, 4), out var need);
        int got = NativeCalls.Poll(w, buf);
        s.Check(small == (int)Native.Result.BufferTooSmall && need >= 8 && got == need && Poll().Count == 0, $"poll with small cap -> BufferTooSmall, required {need}, then {got} bytes, then empty");

        // Destroy: bound items vanish silently.
        cmd.Destroy(ColId);
        cmd.Destroy(TextId);
        CheckApply("destroy bound");
        s.Pump(1);
        s.Check(Poll().Count == 0, "destroyed bound items -> no records");

        // Cost: 1000 bound rectangles in groups of 10 (depth 2). Check after processEvents, no change vs. all moved.
        cmd.Create(ColId, Protocol.ElementType.Group);
        cmd.Reparent(ColId, 0, 0);
        for (uint g = 0; g < 100; g++)
        {
            cmd.Create(100 + g, Protocol.ElementType.Group);
            cmd.SetPlace(100 + g, g % 10 * 70, g / 10 * 50, 70, 50);
            cmd.Reparent(100 + g, ColId, g);
            for (uint i = 0; i < 10; i++)
            {
                uint id = 1000 + g * 10 + i;
                cmd.Create(id, Protocol.ElementType.Rectangle);
                cmd.SetPlace(id, i * 7, 0, 6, 6);
                cmd.Reparent(id, 100 + g, i);
                cmd.Bind(id, id);
            }
        }
        CheckApply("1000 bound items");
        s.Pump(2);
        int first = NativeCalls.Poll(w, buf);
        s.Check(first == 1000 * (20 + 28), $"1000 bound items -> 2000 records on the first poll ({first} bytes)");
        var idle = new double[101];
        var moved = new double[101];
        // best of up to 3 rounds of the idle polls (see PerfLimits.Best); the moved polls run once.
        var idleMedian = PerfLimits.Best(0.2, () =>
        {
            for (int f = 0; f < idle.Length; f++)
            {
                s.Pump(1);
                var t = Stopwatch.StartNew();
                int n = NativeCalls.Poll(w, buf);
                idle[f] = t.Elapsed.TotalMilliseconds;
                if (n != 0) { s.Check(false, $"idle poll returned {n}"); break; }
            }
            Array.Sort(idle);
            return idle[50];
        }, cpuBound: true);
        for (int f = 0; f < moved.Length; f++)
        {
            cmd.SetPlace(ColId, 1 - f % 2, 0, 0, 0); // starts at x 0 -> every frame moves
            CheckApply("move all");
            s.Pump(1);
            var t = Stopwatch.StartNew();
            int n = NativeCalls.Poll(w, buf);
            moved[f] = t.Elapsed.TotalMilliseconds;
            if (n != 1000 * 28) { s.Check(false, $"moved poll returned {n}, expected {1000 * 28}"); break; }
        }
        Array.Sort(moved);
        s.Info($"poll 1000 bound items: unchanged median {idleMedian:F3} ms (last round max {idle[^1]:F3}), " +
            $"all bounds changed median {moved[50]:F3} ms (max {moved[^1]:F3})");
        s.Perf("measure.poll_idle_1000_median", idleMedian);
        s.Perf("measure.poll_moved_1000_median", moved[50]);
        PerfLimits.Below(s, idleMedian, 0.2, $"change check < 0.2 ms / 1000 items (median {idleMedian:F3} ms)", cpuBound: true);
        Native.vlqt_scene_destroy(w);

        foreach (int count in new[] { 500, 2000 })
            RunStaticCost(s, count);
        RunPolishWithoutRender(s);
        RunEffectGroups(s);
    }

    // a poll after a pump without a render polishes the scene itself, but only when something changed
    // since the last poll. An apply between two polls without a pump in between (Qt did not render) must still give
    // the new layout: Row after a Button text change (implicit size), Column after a spacing change, Text measure.
    // The next poll without a change reports nothing, and the following render agrees with the polished layout.
    private static void RunPolishWithoutRender(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt measure polish without render");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Poll()
        {
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"poll ({n})");
            var recs = Parse(buf.AsSpan(0, Math.Max(n, 0)), s);
            recs.RemoveAll(r => r.Event == Protocol.EventId.WindowGeometry);
            return recs;
        }
        static Rec? Find(List<Rec> recs, ushort ev, uint handle)
        {
            Rec? found = null;
            foreach (var r in recs)
                if (r.Event == ev && r.Handle == handle) found = r;
            return found;
        }
        static string S(Rec? r) => r is { } x ? string.Join(", ", x.V) : "none";

        const uint RowId = 1, Btn = 2, RowRect = 3, ColId = 4, ColRect0 = 5, ColRect1 = 6, Txt = 7;
        cmd.Create(RowId, Protocol.ElementType.Row);
        cmd.SetPlace(RowId, 100, 100, 0, 0);
        cmd.SetF32(RowId, Protocol.PropertyId.Spacing, 10);
        cmd.Reparent(RowId, 0, 0);
        cmd.Create(Btn, Protocol.ElementType.Button);
        VL.Qt.Styles.BaseStyle.Write(cmd, Btn);
        cmd.SetPlace(Btn, 0, 0, 0, 0);
        cmd.SetString(Btn, Protocol.PropertyId.Text, "A");
        cmd.Reparent(Btn, RowId, 0);
        cmd.Create(RowRect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(RowRect, 0, 0, 30, 20);
        cmd.Reparent(RowRect, RowId, 1);
        cmd.Bind(RowRect, RowRect);
        cmd.Create(ColId, Protocol.ElementType.Column);
        cmd.SetPlace(ColId, 100, 300, 0, 0);
        cmd.SetF32(ColId, Protocol.PropertyId.Spacing, 10);
        cmd.Reparent(ColId, 0, 1);
        cmd.Create(ColRect0, Protocol.ElementType.Rectangle);
        cmd.SetPlace(ColRect0, 0, 0, 40, 20);
        cmd.Reparent(ColRect0, ColId, 0);
        cmd.Create(ColRect1, Protocol.ElementType.Rectangle);
        cmd.SetPlace(ColRect1, 0, 0, 40, 20);
        cmd.Reparent(ColRect1, ColId, 1);
        cmd.Bind(ColRect1, ColRect1);
        cmd.Create(Txt, Protocol.ElementType.Text);
        cmd.SetPlace(Txt, 400, 100, 300, 60);
        cmd.SetString(Txt, Protocol.PropertyId.Text, "Hi");
        cmd.SetF32(Txt, Protocol.PropertyId.FontSize, 40);
        cmd.Reparent(Txt, 0, 2);
        cmd.Bind(Txt, Txt);
        Apply("scene");
        s.Pump(2);
        var recs = Poll();
        var rowRect0 = Find(recs, Protocol.EventId.SceneBounds, RowRect);
        var colRect0 = Find(recs, Protocol.EventId.SceneBounds, ColRect1);
        var text0 = Find(recs, Protocol.EventId.Measured, Txt);
        s.Check(rowRect0 is { } a && a.V[0] > 110 && colRect0 is { } c && c.V[1] == 330 && text0 is { } t && t.V[0] > 0,
            $"first values (Row child {S(rowRect0)}, Column child {S(colRect0)}, Text {S(text0)})");
        bool stable = true;
        for (int f = 0; f < 3; f++) { s.Pump(1); stable &= Poll().Count == 0; }
        s.Check(stable, "static scene -> no records (Qt does not render)");

        // From here no pump between apply and poll: the last pump did not render, so the poll polishes.
        cmd.SetString(Btn, Protocol.PropertyId.Text, "A much longer label");
        Apply("button text");
        recs = Poll();
        var rowRect1 = Find(recs, Protocol.EventId.SceneBounds, RowRect);
        s.Check(rowRect1 is { } b && rowRect0 is { } a0 && b.V[0] > a0.V[0] + 20 && b.V[1] == a0.V[1],
            $"Button text change, no render -> Row moves the next child ({S(rowRect0)} -> {S(rowRect1)})");
        s.Check(Poll().Count == 0, "next poll without a change -> no records");

        cmd.SetF32(ColId, Protocol.PropertyId.Spacing, 30);
        Apply("column spacing");
        recs = Poll();
        var colRect1 = Find(recs, Protocol.EventId.SceneBounds, ColRect1);
        s.Check(colRect1 is { } d && d.V[1] == 350, $"Column spacing change, no render -> child moves to y 350 ({S(colRect1)})");
        s.Check(Poll().Count == 0, "next poll without a change -> no records");

        cmd.SetString(Txt, Protocol.PropertyId.Text, "Hello World");
        Apply("text");
        recs = Poll();
        var text1 = Find(recs, Protocol.EventId.Measured, Txt);
        s.Check(text1 is { } e && text0 is { } t0 && e.V[0] > t0.V[0], $"Text change, no render -> new MEASURED ({S(text0)} -> {S(text1)})");
        s.Check(Poll().Count == 0, "next poll without a change -> no records");

        // Qt renders the changes now: its own polish gives the same layout, so nothing is reported again.
        s.Pump(2);
        recs = Poll();
        s.Check(recs.Count == 0, $"render after the polished polls -> same layout, no records ({recs.Count} records)");
        Native.vlqt_scene_destroy(w);
    }

    // review: an element inside a Group with an effect chain, and inside a Group used as ShaderEffect Input (frame
    // proxy under the root), still reports new SCENE_BOUNDS when the Group moves (TRANSFORM: translation = x/y
    // path, scale = Matrix4x4 path) or the ShaderEffect holding the Group moves.
    private static void RunEffectGroups(Scenario s)
    {
        void Check(bool ok, string what) => s.NeedsRhi(ok, what, "ShaderEffect/effect chain need a QRhi scenegraph");
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var wave);
        Native.vlqt_shader_load(wave, "builtin:wave", Protocol.ShaderStage.Fragment);
        Check(Native.vlqt_res_wait(wave, 10000) == Protocol.ResourceState.Ready, "shader builtin:wave Ready");
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt measure effect groups");
        Check(w != SceneHandle.Null, "window_create (effect groups)");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        Rec? Bounds(uint handle)
        {
            int n = NativeCalls.Poll(w, buf);
            Rec? found = null;
            foreach (var r in Parse(buf.AsSpan(0, Math.Max(n, 0)), Check))
                if (r.Event == Protocol.EventId.SceneBounds && r.Handle == handle) found = r;
            return found;
        }
        static string S(Rec? r) => r is { } x ? string.Join(", ", x.V) : "none";

        const uint ChainGroup = 1, ChainRect = 2, Fx = 3, InputGroup = 4, InputRect = 5;
        cmd.Create(ChainGroup, Protocol.ElementType.Group);
        cmd.Reparent(ChainGroup, 0, 0);
        cmd.Create(ChainRect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(ChainRect, 10, 10, 20, 20);
        cmd.Reparent(ChainRect, ChainGroup, 0);
        cmd.Bind(ChainRect, ChainRect);
        cmd.BeginEffectChain(ChainGroup);
        cmd.BeginMultiLink();
        cmd.EffectBool(Protocol.MultiEffectKey.BlurEnabled, true);
        cmd.EffectF32(Protocol.MultiEffectKey.Blur, 0.5f);
        cmd.EndLink();
        cmd.EndEffectChain();

        cmd.Create(Fx, Protocol.ElementType.Shader);
        cmd.SetPlace(Fx, 300, 50, 10, 10);
        cmd.SetU64(Fx, Protocol.PropertyId.FragmentShader, wave);
        cmd.Reparent(Fx, 0, 1);
        cmd.Create(InputGroup, Protocol.ElementType.Group);
        cmd.Reparent(InputGroup, Fx, 0);
        cmd.Create(InputRect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(InputRect, 10, 10, 20, 20);
        cmd.Reparent(InputRect, InputGroup, 0);
        cmd.Bind(InputRect, InputRect);
        cmd.SetRef(Fx, Protocol.PropertyId.Source, InputGroup);
        cmd.BeginUniforms(Fx, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0);
        cmd.UniformF32("amplitude", 0);
        cmd.UniformF32("frequency", 2);
        cmd.EndUniforms();
        Apply("effect groups");
        s.Pump(3);
        NativeCalls.Poll(w, buf); // first values

        void Move(uint group, uint rect, Stride.Core.Mathematics.Matrix m, string what, Func<float[], bool> expect)
        {
            cmd.SetMat4(group, Protocol.PropertyId.Transform, m);
            Apply(what);
            s.Pump(2);
            var b = Bounds(rect);
            Check(b is { } x && expect(x.V), $"{what} -> new SCENE_BOUNDS of the element inside ({S(b)})");
        }
        var t = Stride.Core.Mathematics.Matrix.Translation(100, 50, 0);
        var scale = Stride.Core.Mathematics.Matrix.Scaling(2, 2, 1);
        Move(ChainGroup, ChainRect, t, "Group with effect chain translated", v => v[0] == 110 && v[1] == 60 && v[2] == 20);
        Move(ChainGroup, ChainRect, scale, "Group with effect chain scaled", v => v[0] == 20 && v[1] == 20 && v[2] == 40);
        Move(InputGroup, InputRect, t, "Group as ShaderEffect Input translated", v => v[2] == 20 && v[0] > 100);
        var before = Bounds(InputRect);
        Move(InputGroup, InputRect, scale, "Group as ShaderEffect Input scaled", v => v[2] == 40);
        Move(Fx, InputRect, Stride.Core.Mathematics.Matrix.Translation(320, 70, 0), "ShaderEffect holding the Group translated", v => v[2] == 40);
        Check(before == null, $"no bounds without a change ({S(before)})");
        Native.vlqt_scene_destroy(w);
        Native.vlqt_res_release(wave);
    }

    // Items measured by one poll = MEASURED / SCENE_BOUNDS records in its buffer (each is queued only for an item whose geometry changed).
    private static int Records(ReadOnlySpan<byte> poll, int n, ushort ev, Scenario s) =>
        Parse(poll.Slice(0, Math.Max(n, 0)), s).FindAll(x => x.Event == ev).Count;

    // cost of the measurement path for a static scene of count bound rectangles (flat in one Group, like the
    // Elements500/2000 profile scenarios), and with one of them moving. Poll time = Stopwatch around vlqt_scene_poll.
    private static void RunStaticCost(Scenario s, int count)
    {
        var w = NativeCalls.CreateWindow(800, 600, $"VL.Qt measure cost {count}");
        s.Check(w != SceneHandle.Null, $"window_create ({count})");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 20];
        const uint group = 1;
        cmd.Create(group, Protocol.ElementType.Group);
        cmd.Reparent(group, 0, 0);
        for (uint i = 0; i < count; i++)
        {
            uint id = 10 + i;
            cmd.Create(id, Protocol.ElementType.Rectangle);
            cmd.SetPlace(id, i % 50 * 16, i / 50 * 14, 12, 10);
            cmd.Reparent(id, group, i);
            cmd.Bind(id, id);
        }
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"{count} bound rectangles ({rc})");
        cmd.Reset();
        s.Pump(2);
        int first = NativeCalls.Poll(w, buf);
        var firstRecs = Parse(buf.AsSpan(0, Math.Max(first, 0)), s);
        int measured = firstRecs.FindAll(x => x.Event == Protocol.EventId.Measured).Count;
        int bounds = firstRecs.FindAll(x => x.Event == Protocol.EventId.SceneBounds).Count;
        s.Check(measured == count && bounds == count, $"{count} bound items -> MEASURED + SCENE_BOUNDS each on the first poll ({measured}, {bounds})");

        const int frames = 201;
        var idle = new double[frames];
        int idleMeasured = 0;
        for (int f = 0; f < frames; f++)
        {
            s.Pump(1);
            var t = Stopwatch.StartNew();
            int n = NativeCalls.Poll(w, buf);
            idle[f] = t.Elapsed.TotalMilliseconds;
            idleMeasured = Math.Max(idleMeasured, Records(buf, n, Protocol.EventId.Measured, s) + Records(buf, n, Protocol.EventId.SceneBounds, s));
            if (n != 0) { s.Check(false, $"static poll returned {n} bytes ({count})"); break; }
        }
        var one = new double[frames];
        int oneMeasured = 0;
        for (int f = 0; f < frames; f++)
        {
            cmd.SetPlace(10, 1 + f % 2, 0, 12, 10); // starts at x 0 -> every frame moves
            Native.Apply(w, cmd.EndFrame());
            cmd.Reset();
            s.Pump(1);
            var t = Stopwatch.StartNew();
            int n = NativeCalls.Poll(w, buf);
            one[f] = t.Elapsed.TotalMilliseconds;
            oneMeasured = Math.Max(oneMeasured, Records(buf, n, Protocol.EventId.SceneBounds, s));
            if (n != 28) { s.Check(false, $"one moved -> one SCENE_BOUNDS, got {n} bytes ({count})"); break; }
        }
        Array.Sort(idle);
        Array.Sort(one);
        s.Info($"poll {count} bound rectangles: static median {idle[frames / 2]:F4} ms (max {idle[^1]:F4}), " +
            $"measured/frame max {idleMeasured}; one moving median {one[frames / 2]:F4} ms (max {one[^1]:F4}), measured/frame max {oneMeasured}");
        s.Check(idleMeasured == 0, $"static scene of {count} -> no item measured ({idleMeasured} per frame)");
        s.Check(oneMeasured == 1, $"one of {count} moving -> only it measured ({oneMeasured} per frame)");
        Native.vlqt_scene_destroy(w);
    }
}
