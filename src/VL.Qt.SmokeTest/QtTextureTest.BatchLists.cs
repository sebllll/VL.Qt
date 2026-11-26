using NUnit.Framework;
using System;
using System.Collections.Generic;
using VL.Qt.Interop;
using VL.Qt;

/// <summary>
/// The batch lists of the render threads have a fixed address. With a pool of 2 render threads scene A
/// (first thread) renders for a few frames; then scene B (second thread, created before but not bound yet) is bound and
/// renders for the first time, right after a batch that posted A's render. Checks: every frame of both scenes shows its
/// colour (both threads render in every batch, no stale or missing frame after the late first batch of the second
/// thread). Own process: the batch lists are per process; the pool size comes from vlqt_offscreen_set_render_threads,
/// as the pin Render Threads sets it. finishPostedRenders (ORCH_182) already drains a posted job before the next batch
/// builds its lists, so this run guards the fixed address as an invariant; it does not reproduce the former
/// use-after-free on its own.
/// </summary>
public static partial class QtTextureTest
{
    /// <summary>The second render thread is first used after the first one rendered, pool of 2 (own process).</summary>
    [Test, Category("batch-lists-pool-2"), Category("pool-2"), Category("own-process")]
    public static void BatchListsPool2() => Scenario.RunLocal(s =>
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("batch-lists-pool-2: offscreen scenes are D3D11 only");
            return;
        }
        var env = new Dictionary<string, string> { ["VLQT_SMOKE_DEBUG_LAYER"] = DebugLayerAvailable() ? "1" : "0" };
        OwnProcess.Run(s, new[] { "batch-lists" }, env, expectInQtLog: "offscreen scenes created from now on render on 2 Qt render thread(s)");
    });

    internal static void RunBatchLists(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("batch-lists: offscreen scenes are D3D11 only");
            return;
        }
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        var rcThreads = Native.vlqt_offscreen_set_render_threads(2);
        if (!check(rcThreads == 0, $"batch-lists: set_render_threads(2) before the first scene ({rcThreads}) {(rcThreads != 0 ? Native.LastError() : "")}"))
            return;
        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "batch-lists: create test device"))
            return;
        const int w = 64, h = 48;
        var colours = new[] { (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f) };
        var box = new[] { new Box(8, 8, 32, 24, 1, 0, 0, 1) };
        // Scenes get their thread at create, round robin: A the first, B the second.
        var a = NewScene(w, h, (0, 0, 0, 0), box, s, "batch-lists: scene A");
        var b = NewScene(w, h, (0, 0, 0, 0), box, s, "batch-lists: scene B");
        IntPtr texA = default, handleA = default, texB = default, handleB = default;
        if (a == SceneHandle.Null || b == SceneHandle.Null
            || !check(NewTarget(device, w, h, B8G8R8A8Typeless, out texA, out handleA), "batch-lists: texture A")
            || !check(NewTarget(device, w, h, B8G8R8A8Typeless, out texB, out handleB), "batch-lists: texture B"))
        {
            NativeTest.vlqt_test_d3d11_release_device(device, context);
            return;
        }

        bool Shows(IntPtr texture, (float R, float G, float B) c)
        {
            var px = Read(texture, w, h, s_bgra);
            return px != null && Near(Rgba(px, w, 24, 20, s_bgra), new[] { (int)(c.R * 255), (int)(c.G * 255), (int)(c.B * 255), 255 }, 0);
        }

        var rcA = Native.vlqt_offscreen_bind(a, device, handleA, w, h, B8G8R8A8Unorm, 1, out _);
        NativeTest.vlqt_test_close_handle(handleA);
        check(rcA == 0, $"batch-lists: bind A ({rcA}) {(rcA != 0 ? Native.LastError() : "")}");

        // A alone for a few frames: only the first thread has a batch; B is not bound and is skipped.
        int wrongBatch = 0, wrongColour = 0;
        for (int i = 0; i < 6; i++)
        {
            var c = colours[i % colours.Length];
            Recolor(a, 1, c.Item1, c.Item2, c.Item3, 1, s, "batch-lists");
            if (Native.OffscreenRenderBatch(context, new[] { a, b }, 0) != 1)
                wrongBatch++;
            if (!Shows(texA, c))
                wrongColour++;
        }
        check(wrongBatch == 0 && wrongColour == 0, $"batch-lists: A alone renders every frame (wrong batch count {wrongBatch}, wrong colour {wrongColour})");

        // Late: B is bound and joins the batch, the first one with the second thread.
        var rcB = Native.vlqt_offscreen_bind(b, device, handleB, w, h, B8G8R8A8Unorm, 1, out _);
        NativeTest.vlqt_test_close_handle(handleB);
        check(rcB == 0, $"batch-lists: bind B late ({rcB}) {(rcB != 0 ? Native.LastError() : "")}");
        if (rcB == 0)
        {
            const int frames = 120;
            int wrongCount = 0, wrongA = 0, wrongB = 0;
            for (int i = 0; i < frames; i++)
            {
                var ca = colours[i % colours.Length];
                var cb = colours[(i + 1) % colours.Length];
                Recolor(a, 1, ca.Item1, ca.Item2, ca.Item3, 1, s, "batch-lists");
                Recolor(b, 1, cb.Item1, cb.Item2, cb.Item3, 1, s, "batch-lists");
                if (Native.OffscreenRenderBatch(context, new[] { a, b }, 0) != 2)
                    wrongCount++;
                if (!Shows(texA, ca))
                    wrongA++;
                if (!Shows(texB, cb))
                    wrongB++;
            }
            check(wrongCount == 0, $"batch-lists: both scenes render in each of {frames} batches after the late bind ({wrongCount} batches wrong)");
            check(wrongA == 0 && wrongB == 0, $"batch-lists: every frame shows its colour (A wrong {wrongA}, B wrong {wrongB})");
        }

        foreach (var scene in new[] { a, b })
        {
            Native.vlqt_offscreen_unbind(scene);
            Native.vlqt_scene_destroy(scene);
        }
        NativeTest.vlqt_test_com_release(texA);
        NativeTest.vlqt_test_com_release(texB);
        s.Pump(2);
        check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "batch-lists: test device freed");
    }
}
