using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// NRES-01 (W4-52): an Image element of an offscreen scene frees its window texture on the scene's render thread, never
/// on the main thread while that thread may still render a posted frame with it. Two scenes bound to shared textures
/// show one image resource; many times in a row an apply creates the elements, render_batch syncs and posts the render,
/// and the next apply destroys the elements at once. Checks: the image is drawn, QtNative logs no "freed off its
/// render thread" (QtImageItem's check of the thread a texture was made on), the element references go back to zero,
/// the test device is freed. Runs with the process's pool (1 by default), with a pool of 2 (one scene per thread) and
/// in the main thread mode (render_thread::retire drops at once), the last two in a process of their own.
/// </summary>
[TestFixture, NonParallelizable]
public static class ImageRetireTest
{
    private const int B8G8R8A8Typeless = 90;
    private const int B8G8R8A8Unorm = 87;
    private const int Rounds = 300;
    private const int W = 64, H = 48;

    /// <summary>Image elements removed right after render_batch, scenes on the process's render thread pool.</summary>
    [Test, Category("image-retire")]
    public static void ImageRetire() => Scenario.Run(Run);

    /// <summary>The same with a pool of 2 render threads (one scene per thread), in a process of its own.</summary>
    [Test, Category("image-retire-pool-2"), Category("pool-2"), Category("own-process")]
    public static void ImageRetirePool2() => Scenario.RunLocal(s => RunMode(s, "2", "offscreen scenes render on 2 Qt render thread(s)"));

    /// <summary>The same in the main thread mode (VLQT_OFFSCREEN_THREAD=0), in a process of its own.</summary>
    [Test, Category("image-retire-main-thread"), Category("own-process")]
    public static void ImageRetireMainThread() => Scenario.RunLocal(s => RunMode(s, "0", "offscreen scenes render on the main thread"));

    private static void RunMode(Scenario s, string threads, string modeLine)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("image-retire: offscreen scenes are D3D11 only");
            return;
        }
        var env = new Dictionary<string, string>
        {
            ["VLQT_OFFSCREEN_THREAD"] = threads,
            ["VLQT_SMOKE_DEBUG_LAYER"] = QtTextureTest.DebugLayerAvailable() ? "1" : "0",
        };
        OwnProcess.Run(s, new[] { "image-retire" }, env, expectInQtLog: modeLine);
    }

    internal static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("image-retire: offscreen scenes are D3D11 only");
            return;
        }
        var asset = ImageTest.FindAsset();
        s.Check(asset != null, "image-retire: test asset found");
        if (asset == null)
            return;
        if (NativeTest.CreateDevice(0, out var device, out var context) != 0)
        {
            s.Check(false, "image-retire: create test device " + Native.LastError());
            return;
        }

        var stripes = ImageTest.Load(asset, s.Check, "stripes");
        var scenes = new SceneHandle[2];
        var textures = new IntPtr[2];
        var handles = new IntPtr[2];
        var cmd = new CommandWriter();
        var ok = true;
        for (int i = 0; i < scenes.Length; i++)
        {
            scenes[i] = Native.vlqt_offscreen_create(W, H);
            NativeTest.vlqt_test_d3d11_create_shared_texture(device, W, H, B8G8R8A8Typeless, out textures[i], out handles[i]);
            var rc = scenes[i] == SceneHandle.Null || textures[i] == IntPtr.Zero ? Native.Result.NullHandle
                : Native.vlqt_offscreen_bind(scenes[i], device, handles[i], W, H, B8G8R8A8Unorm, 1, out _);
            ok &= rc == Native.Result.Ok;
            s.Check(rc == Native.Result.Ok, $"image-retire: scene {i} created and bound ({rc}) {(rc != Native.Result.Ok ? Native.LastError() : "")}");
            if (scenes[i] != SceneHandle.Null)
            {
                cmd.Reset();
                cmd.ClearColor(0, 0, 0, 0);
                Native.Apply(scenes[i], cmd.EndFrame());
            }
        }

        if (ok && stripes != 0)
        {
            var logStart = QtHost.LogLength;
            var drawn = 0;
            var failed = 0;
            for (int round = 0; round < Rounds; round++)
            {
                // Create: the next sync makes the texture of the element on the scene's render thread.
                foreach (var scene in scenes)
                {
                    cmd.Reset();
                    cmd.Create(1, Protocol.ElementType.Image);
                    cmd.SetPlace(1, 0, 0, W, H);
                    cmd.SetBool(1, Protocol.PropertyId.Smooth, false);
                    cmd.SetU64(1, Protocol.PropertyId.Image, stripes);
                    cmd.Reparent(1, 0, 0);
                    if (Native.Apply(scene, cmd.EndFrame()) != 0)
                        failed++;
                }
                if (Native.OffscreenRenderBatch(context, scenes, 0) != scenes.Length)
                    failed++;
                // Destroy right after render_batch posted the render: the item lets its texture go on the main thread.
                foreach (var scene in scenes)
                {
                    cmd.Reset();
                    cmd.Destroy(1);
                    if (Native.Apply(scene, cmd.EndFrame()) != 0)
                        failed++;
                }
                // Every 50th round: the render before the destroy drew the image (green stripe in the centre).
                if (round % 50 == 49)
                {
                    Native.OffscreenRenderBatch(context, scenes, 0); // the frame without the element
                    s.Check(textures.All(t => Centre(t) is (0, 0, 0, 0)), $"image-retire: round {round}: element gone ({Centre(textures[0])})");
                    foreach (var scene in scenes)
                    {
                        cmd.Reset();
                        cmd.Create(1, Protocol.ElementType.Image);
                        cmd.SetPlace(1, 0, 0, W, H);
                        cmd.SetBool(1, Protocol.PropertyId.Smooth, false);
                        cmd.SetU64(1, Protocol.PropertyId.Image, stripes);
                        cmd.Reparent(1, 0, 0);
                        Native.Apply(scene, cmd.EndFrame());
                    }
                    Native.OffscreenRenderBatch(context, scenes, 0);
                    if (textures.All(t => Centre(t) is ( < 40, > 200, < 40, > 200)))
                        drawn++;
                    foreach (var scene in scenes)
                    {
                        cmd.Reset();
                        cmd.Destroy(1);
                        Native.Apply(scene, cmd.EndFrame());
                    }
                }
            }
            s.Check(failed == 0, $"image-retire: {Rounds} rounds of create, render_batch, destroy without an error ({failed} failed) {(failed > 0 ? Native.LastError() : "")}");
            s.Check(drawn == Rounds / 50, $"image-retire: the image is drawn in both scenes ({drawn} of {Rounds / 50} checks)");
            // The renders and retired textures still queued on the render threads.
            foreach (var scene in scenes)
                NativeTest.vlqt_test_offscreen_state(scene, out _, out _, out _);
            var offThread = QtHost.LogSince(logStart).Split('\n').Count(l => l.Contains("freed off its render thread"));
            s.Check(offThread == 0, $"image-retire: every window texture freed on its render thread ({offThread} freed off it)");
            var list = new Native.vlqt_res_info[256];
            var n = Native.ResList(list);
            var info = list.Take(Math.Max(n, 0)).FirstOrDefault(r => r.id == stripes);
            s.Check(info.element_refs == 0, $"image-retire: no element reference left ({info.element_refs})");
        }

        foreach (var scene in scenes)
            if (scene != SceneHandle.Null)
            {
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_scene_destroy(scene);
            }
        if (stripes != 0)
            Native.vlqt_res_release(stripes);
        for (int i = 0; i < textures.Length; i++)
        {
            if (handles[i] != IntPtr.Zero)
                NativeTest.vlqt_test_close_handle(handles[i]);
            if (textures[i] != IntPtr.Zero)
                NativeTest.vlqt_test_com_release(textures[i]);
        }
        s.Pump(2);
        s.Check(NativeTest.vlqt_test_d3d11_release_device(device, context) == 0, "image-retire: test device freed");
    }

    // Centre pixel (R, G, B, A) of a B8G8R8A8 texture; -1 everywhere if the readback failed.
    private static (int R, int G, int B, int A) Centre(IntPtr texture)
    {
        var pixels = new byte[W * H * 4];
        if (NativeCalls.ReadTexture(texture, pixels, out _, out _, out _) != pixels.Length)
            return (-1, -1, -1, -1);
        int i = (H / 2 * W + W / 2) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }
}
