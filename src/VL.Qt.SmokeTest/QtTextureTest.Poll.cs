using System;
using System.Collections.Generic;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

public static partial class QtTextureTest
{
    // The poll polishes an offscreen scene only when the render_batch before it did not (its change was applied after the
    // batch, or no batch ran): a Column of bound rectangles reports the layout positions of its children in the poll
    // (SCENE_BOUNDS) after each of three paths: no batch (the poll polishes), a batch (the poll skips its own polish), a
    // change without a batch again.
    private static void PollPolish(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        const int w = 200, h = 300;
        const uint column = 1, firstChild = 10, firstHandle = 11;
        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "poll polish: create device"))
            return;
        var scene = Native.vlqt_offscreen_create(w, h);
        IntPtr tex = IntPtr.Zero, handle = IntPtr.Zero;
        if (!check(scene != SceneHandle.Null && NewTarget(device, w, h, B8G8R8A8Typeless, out tex, out handle),
            "poll polish: setup (scene, texture)"))
        {
            NativeTest.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void Apply(string what)
        {
            int rc = (int)Native.Apply(scene, cmd.EndFrame());
            check(rc == 0, $"poll polish: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        // The y of the three children (SCENE_BOUNDS of handles 11 ... 13) in this poll; NaN = no record.
        float[] Ys()
        {
            int n = NativeCalls.Poll(scene, buf);
            var ys = new[] { float.NaN, float.NaN, float.NaN };
            if (n < 0)
            {
                check(false, $"poll polish: poll ({n})");
                return ys;
            }
            foreach (var r in MeasureTest.Parse(buf.AsSpan(0, n), s))
                if (r.Event == Protocol.EventId.SceneBounds && r.Handle >= firstHandle && r.Handle < firstHandle + 3)
                    ys[r.Handle - firstHandle] = r.V[1];
            return ys;
        }
        bool Same(float[] actual, params float[] expected)
        {
            for (int i = 0; i < 3; i++)
                if (!(actual[i] == expected[i]) && !(float.IsNaN(actual[i]) && float.IsNaN(expected[i])))
                    return false;
            return true;
        }
        string Show(float[] ys) => string.Join(", ", ys);

        // Column at (20, 10), padding 5, spacing 10: child i at y = 15 + 50 i.
        cmd.Reset();
        cmd.ClearColor(0, 0, 0, 0);
        cmd.Create(column, Protocol.ElementType.Column);
        cmd.SetPlace(column, 20, 10, 0, 0);
        cmd.SetF32(column, Protocol.PropertyId.Spacing, 10);
        cmd.SetF32(column, Protocol.PropertyId.Padding, 5);
        cmd.Reparent(column, 0, 0);
        for (uint i = 0; i < 3; i++)
        {
            cmd.Create(firstChild + i, Protocol.ElementType.Rectangle);
            cmd.SetPlace(firstChild + i, 0, 0, 120, 40);
            cmd.SetColor(firstChild + i, Protocol.PropertyId.Color, 1, 0, 0, 1);
            cmd.Reparent(firstChild + i, column, i);
            cmd.Bind(firstChild + i, firstHandle + i);
        }
        Apply("apply the column");
        var ys = Ys();
        check(Same(ys, 15, 65, 115), $"poll polish: unbound scene, no batch: the poll polishes, children at y 15, 65, 115 ({Show(ys)})");

        int rc = (int)Native.vlqt_offscreen_bind(scene, device, handle, w, h, B8G8R8A8Unorm, 1, out _);
        NativeTest.vlqt_test_close_handle(handle);
        if (!check(rc == 0, $"poll polish: bind ({rc}) {ErrIf(rc != 0)}"))
        {
            Native.vlqt_scene_destroy(scene);
            NativeTest.vlqt_test_com_release(tex);
            NativeTest.vlqt_test_d3d11_release_device(device, context);
            return;
        }
        int rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        check(rendered == 1, $"poll polish: first batch ({rendered}) {ErrIf(rendered != 1)}");
        ys = Ys();
        check(Same(ys, float.NaN, float.NaN, float.NaN), $"poll polish: nothing changed since the last poll, no records ({Show(ys)})");

        // A change, then a batch: the batch polished the scene, the poll after it skips its own polish and still reports the new layout.
        cmd.SetF32(column, Protocol.PropertyId.Spacing, 0);
        Apply("spacing 0");
        rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        ys = Ys();
        check(rendered == 1 && Same(ys, float.NaN, 55, 95), $"poll polish: change, batch, poll: children 1 and 2 moved to y 55, 95 ({rendered}; {Show(ys)})");

        // A change without a batch before the poll: the poll has to polish.
        cmd.SetF32(column, Protocol.PropertyId.Spacing, 20);
        Apply("spacing 20");
        ys = Ys();
        check(Same(ys, float.NaN, 75, 135), $"poll polish: change, no batch, poll: the poll polishes, y 75, 135 ({Show(ys)})");

        // A batch and then a change before the poll (the batch's polish has not seen it): the poll has to polish.
        rendered = Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        cmd.SetF32(column, Protocol.PropertyId.Spacing, 10);
        Apply("spacing 10 after the batch");
        ys = Ys();
        check(rendered == 1 && Same(ys, float.NaN, 65, 115), $"poll polish: batch, change, poll: the poll polishes, y 65, 115 ({rendered}; {Show(ys)})");

        Native.vlqt_offscreen_unbind(scene);
        Native.vlqt_scene_destroy(scene);
        NativeTest.vlqt_test_com_release(tex);
        s.Pump(2);
        check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "poll polish: device freed");
    }
}
