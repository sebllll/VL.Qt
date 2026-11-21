using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// task 5.4 (perf): main-thread time per QtTexture and frame time of offscreen scenes rendered on a Qt render thread
/// (default) or on the main thread (<c>VLQT_OFFSCREEN_THREAD=0</c>), and "same frame" in every frame. Opt-in, not part
/// of --all (about a minute): <c>--only qttextureperf</c>, run alone because the thread count is fixed at the first scene.
/// A frame imitates vvvv + Stride: apply the changes (Update), render_batch (draw start), 6 ms of Stride CPU work, then
/// a GPU read of scene 0's texture on the test device (Stride's draw: Map waits for the GPU). A marker rectangle carries the
/// frame number; after the read every scene's texture must show this frame's marker (same frame, 100 % of the frames).
/// After the measured frames, snapshots of scene 0's marker every 10th frame (snapshots under load) must finish with a
/// marker of a frame since their request while same frame still holds.
/// The process measures the threaded mode (thread count = VLQT_OFFSCREEN_THREAD, default 1) and then starts itself with
/// VLQT_OFFSCREEN_THREAD=0 for the main thread numbers (reference, no time limit). With VLQT_OFFSCREEN_THREAD=0 it
/// measures only the main thread. Limit (threaded, hardware adapter): median render_batch time per QtTexture of the heavy
/// scenes below 0.5 ms (PerfLimits factor applies); a software adapter (WARP, CI runners) waits for the submission on the
/// main thread by design, so there the limit is logged and skipped.
/// </summary>
[TestFixture, NonParallelizable]
public static class QtTexturePerfTest
{
    private const int B8G8R8A8Typeless = 90;
    private const int B8G8R8A8Unorm = 87;
    private const int W = 1280, H = 720;
    private const double MainThreadLimitMs = 0.5;
    // Marker rectangle (id 1) at (0, 0): its red channel is the frame number.
    private const int MarkerSize = 32;

    // A scene: rounded rectangles + texts, 1280x720.
    private readonly record struct Config(string Name, int Scenes, int Rects, int Texts)
    {
        public bool Heavy => Rects >= 1000;
    }

    /// <summary>Median main-thread time per QtTexture in both thread modes (about a minute, own process).</summary>
    [Test, Explicit("about a minute"), Category("qttextureperf"), Category("own-process")]
    public static void QtTexturePerf() => Scenario.RunLocal(s => OwnProcess.Run(s, new[] { "qttextureperf" }));

    internal static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows())
        {
            s.Info("qttextureperf: Windows only (Direct3D 11), skipped");
            return;
        }
        int threads = 1;
        var env = Environment.GetEnvironmentVariable("VLQT_OFFSCREEN_THREAD");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
            threads = n;
        var rc = Native.vlqt_offscreen_set_render_threads(threads);
        s.Check(rc == 0, $"qttextureperf: set_render_threads({threads}) before the first scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        if (rc != 0)
            return;

        bool ok = NativeTest.CreateDevice(0, out var device, out var context) == 0;
        s.Check(ok, "qttextureperf: test device on QtNative's adapter");
        if (!ok)
            return;
        bool software = IsSoftwareAdapter(device, out var adapterName);
        string mode = threads == 0 ? "main thread" : $"{threads} render thread(s)";
        s.Info($"qttextureperf: {mode}, adapter \"{adapterName}\"{(software ? " (software)" : "")}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores");
        // Software adapter: shorter matrix, the scenes render in software.
        var configs = software
            ? new[] { new Config("1 heavy", 1, 2000, 80), new Config("8 light", 8, 100, 4) }
            : new[] { new Config("1 heavy", 1, 2000, 80), new Config("4 heavy", 4, 2000, 80), new Config("8 heavy", 8, 2000, 80), new Config("8 light", 8, 100, 4) };
        int warmup = software ? 10 : 30, frames = software ? 60 : 200;
        foreach (var config in configs)
        {
            var main = Measure(device, context, config, threads, warmup, frames, s);
            if (threads == 0 || !config.Heavy)
                continue;
            if (software)
                s.Info($"qttextureperf: {config.Name}: software adapter, main-thread limit skipped (median {main:F3} ms per QtTexture)");
            else
                PerfLimits.Below(s, main, MainThreadLimitMs, $"qttextureperf: {config.Name}, {mode}: median main-thread time per QtTexture {main:F3} ms < {MainThreadLimitMs} ms");
        }
        NativeTest.vlqt_test_d3d11_release_device(device, context);

        if (threads != 0)
            RunMainThreadChild(s);
    }

    // The thread count cannot change after the first scene: the main thread numbers come from a second process.
    private static void RunMainThreadChild(Scenario s)
    {
        var host = Environment.ProcessPath;
        if (host == null)
            return;
        var cmdline = Environment.GetCommandLineArgs();
        var psi = new ProcessStartInfo(host) { UseShellExecute = false };
        // Run through "dotnet app.dll" (cmdline[0] is the dll), otherwise the apphost is the process itself.
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(cmdline[0]);
        foreach (var a in cmdline.Skip(1))
            psi.ArgumentList.Add(a);
        psi.Environment["VLQT_OFFSCREEN_THREAD"] = "0";
        Console.Out.Flush();
        using var child = Process.Start(psi);
        if (child == null)
        {
            s.Check(false, "qttextureperf: start the main thread run");
            return;
        }
        child.WaitForExit();
        s.Check(child.ExitCode == 0, $"qttextureperf: main thread run (VLQT_OFFSCREEN_THREAD=0), exit code {child.ExitCode}");
    }

    // Returns the median main-thread time of render_batch per QtTexture (ms).
    private static double Measure(IntPtr device, IntPtr context, Config config, int threads, int warmup, int frames, Scenario s)
    {
        int count = config.Scenes, rects = config.Rects, texts = config.Texts;
        string mode = threads == 0 ? "main" : $"thread{threads}";
        var scenes = new SceneHandle[count];
        var textures = new IntPtr[count];
        var cmd = new CommandWriter();
        for (int k = 0; k < count; k++)
        {
            scenes[k] = Native.vlqt_offscreen_create(W, H);
            cmd.Reset();
            cmd.ClearColor(0, 0, 0, 0);
            // id 1: marker (frame number in red); 2..: rounded rectangles; then texts.
            cmd.Create(1, Protocol.ElementType.Rectangle);
            cmd.SetPlace(1, 0, 0, MarkerSize, MarkerSize);
            cmd.Reparent(1, 0, 0);
            for (int i = 0; i < rects; i++)
            {
                uint id = (uint)(2 + i);
                cmd.Create(id, Protocol.ElementType.Rectangle);
                cmd.SetPlace(id, 40 + (i * 37) % (W - 80), 40 + (i * 53) % (H - 80), 30, 20);
                cmd.SetF32(id, Protocol.PropertyId.Radius, 6);
                cmd.SetColor(id, Protocol.PropertyId.Color, (i % 7) / 7f, (i % 5) / 5f, (i % 3) / 3f, 0.8f);
                cmd.Reparent(id, 0, (uint)(1 + i));
            }
            for (int i = 0; i < texts; i++)
            {
                uint id = (uint)(2 + rects + i);
                cmd.Create(id, Protocol.ElementType.Text);
                cmd.SetPlace(id, (i * 97) % (W - 200), (i * 31) % (H - 30), 200, 30);
                cmd.SetString(id, Protocol.PropertyId.Text, $"Text {i}");
                cmd.SetF32(id, Protocol.PropertyId.FontSize, 16);
                cmd.Reparent(id, 0, (uint)(1 + rects + i));
            }
            s.Check(Native.Apply(scenes[k], cmd.EndFrame()) == 0, $"qttextureperf {config.Name}: apply scene {k}");
            NativeTest.vlqt_test_d3d11_create_shared_texture(device, W, H, B8G8R8A8Typeless, out textures[k], out var handle);
            var rc = Native.vlqt_offscreen_bind(scenes[k], device, handle, W, H, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(handle);
            s.Check(rc == 0, $"qttextureperf {config.Name}: bind scene {k} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }

        var apply = new List<double>();
        var batch = new List<double>();
        var read = new List<double>();
        var total = new List<double>();
        var pixels = new byte[W * H * 4];
        int misses = 0, older = 0, renderFailures = 0;
        var sw = new Stopwatch();
        // Snapshots under load: after the measured frames, a snapshot of scene 0's marker every 10th frame (a Snapshot
        // node runs in the Update, before the renderer). The grab rides on the scene's frames, on the render thread when
        // threaded; same frame must hold meanwhile and the snapshot shows a marker of a frame since the request.
        int end = warmup + frames + frames / 2;
        var snapBatch = new List<double>();
        var snapLatency = new List<int>();
        var snapPixels = new byte[MarkerSize * MarkerSize * 4];
        int snapMisses = 0, snapRequested = 0, snapBad = 0, snapRequestFrame = -1;
        s.Check(Native.vlqt_res_create(Protocol.ResourceKind.Snapshot, out var snap) == 0, $"qttextureperf {mode} {config.Name}: snapshot resource");
        var snapErrors = new List<string>();
        // Records the pending snapshot if it has finished (Ready or Failed) by frame f, and checks its pixels.
        void SnapshotDone(int f)
        {
            int state = Native.vlqt_res_wait(snap, 0);
            if (state is not (Protocol.ResourceState.Ready or Protocol.ResourceState.Failed))
                return;
            snapLatency.Add(f - snapRequestFrame);
            int got = NativeCalls.ReadSnapshot(snap, snapPixels, out var desc);
            int r = snapPixels[(MarkerSize / 2 * MarkerSize + MarkerSize / 2) * 4 + 2], a = snapPixels[(MarkerSize / 2 * MarkerSize + MarkerSize / 2) * 4 + 3];
            bool markerSince = Enumerable.Range(snapRequestFrame, f - snapRequestFrame + 1).Any(g => r == g % 251 + 1);
            if (state != Protocol.ResourceState.Ready || got != snapPixels.Length || desc.width != MarkerSize || desc.height != MarkerSize || a != 255 || !markerSince)
            {
                snapBad++;
                if (snapErrors.Count < 3)
                    snapErrors.Add($"frame {snapRequestFrame}->{f}: state {state}, {got} bytes {desc.width}x{desc.height}, red {r} alpha {a} {(state == Protocol.ResourceState.Failed ? QtTextureTest.SnapshotError(snap) : "")}");
            }
            snapRequestFrame = -1;
        }
        // A frame waits on the render thread and the GPU: a hang ends the run with a FAIL line.
        using var watchdog = new QtTextureTest.Watchdog($"qttextureperf {config.Name}", 120);
        for (int f = 0; f < end || (snapRequestFrame >= 0 && f < end + 30); f++)
        {
            bool snapPhase = f >= warmup + frames;
            // The host's pump (before the frame's Update) closes the perf samples: those of the previous frame.
            s.Pump(1);
            if (snapRequestFrame >= 0)
                SnapshotDone(f);
            var t0 = Stopwatch.GetTimestamp();
            // Update: per scene the marker, 50 rectangle colours and 10 texts change.
            byte marker = (byte)(f % 251 + 1);
            for (int k = 0; k < count; k++)
            {
                cmd.Reset();
                cmd.SetColor(1, Protocol.PropertyId.Color, marker / 255f, 0, 0, 1);
                for (int i = 0; i < Math.Min(50, rects); i++)
                {
                    uint id = (uint)(2 + (f * 50 + i) % rects);
                    cmd.SetColor(id, Protocol.PropertyId.Color, ((f + i) % 11) / 11f, (i % 5) / 5f, (f % 3) / 3f, 0.8f);
                }
                for (int i = 0; i < Math.Min(10, texts); i++)
                    cmd.SetString((uint)(2 + rects + (f + i) % texts), Protocol.PropertyId.Text, $"Frame {f} #{i}");
                Native.Apply(scenes[k], cmd.EndFrame());
            }
            if (snapPhase && f < end && f % 10 == 0 && snapRequestFrame < 0 && Native.vlqt_snapshot_start(snap, scenes[0], 1, 1) == 0)
            {
                snapRequestFrame = f;
                snapRequested++;
            }
            var t1 = Stopwatch.GetTimestamp();
            int rendered = Native.OffscreenRenderBatch(context, scenes, f);
            var t2 = Stopwatch.GetTimestamp();
            if (rendered != count)
                renderFailures++;
            // Stride's CPU work of the frame.
            sw.Restart();
            while (sw.Elapsed.TotalMilliseconds < 6) { }
            var t3 = Stopwatch.GetTimestamp();
            // Stride's draw reads scene 0's texture; Map waits for the GPU (and so for Qt's write signal).
            NativeCalls.ReadTexture(textures[0], pixels, out _, out _, out _);
            var t4 = Stopwatch.GetTimestamp();
            for (int k = 0; k < count; k++)
            {
                if (k > 0)
                    NativeCalls.ReadTexture(textures[k], pixels, out _, out _, out _);
                int red = pixels[(8 * W + 8) * 4 + 2]; // BGRA
                if (red == marker)
                    continue;
                if (snapPhase)
                {
                    snapMisses++;
                    continue;
                }
                misses++;
                if (red == (byte)((f - 1) % 251 + 1))
                    older++;
            }
            if (snapPhase)
                snapBatch.Add(Ms(t1, t2));
            else if (f >= warmup)
            {
                apply.Add(Ms(t0, t1));
                batch.Add(Ms(t1, t2));
                read.Add(Ms(t3, t4));
                total.Add(Ms(t0, t4));
            }
        }
        // Perf sample of scene 0 (what PerfStats shows): with a render thread the last frames finished before the poll.
        string perf = Native.vlqt_scene_get_perf_sample(scenes[0], out var p) == 0 ? $"; scene 0 perf sample: sync {p.sync:F2}, render {p.render:F2} ms" : "";
        double perTexture = Median(batch) / count;
        Console.WriteLine(
            $"INFO qttextureperf {mode} {config.Name} ({rects} rects + {texts} texts): main-thread render_batch {Median(batch):F3} ms/frame (p95 {P95(batch):F3}) = {perTexture:F3} ms per QtTexture; "
            + $"apply {Median(apply):F3} ms/frame; Stride read wait {Median(read):F3}; frame {Median(total):F3} (p95 {P95(total):F3}); "
            + $"not this frame: {misses} of {(warmup + frames) * count} reads (previous frame {older}){perf}");
        Console.WriteLine(
            $"INFO qttextureperf {mode} {config.Name}: snapshots under load: {snapLatency.Count} of {snapRequested} finished, latency {(snapLatency.Count > 0 ? $"{snapLatency.Min()}-{snapLatency.Max()}" : "-")} frames; "
            + $"main-thread render_batch meanwhile {(snapBatch.Count > 0 ? Median(snapBatch) : 0):F3} ms/frame (p95 {(snapBatch.Count > 0 ? P95(snapBatch) : 0):F3})");
        s.Check(snapRequested >= 3 && snapLatency.Count == snapRequested && snapBad == 0,
            $"qttextureperf {mode} {config.Name}: snapshots under load finish with a marker since the request ({snapLatency.Count} of {snapRequested}, {snapBad} bad) {string.Join("; ", snapErrors)}");
        s.Check(snapMisses == 0, $"qttextureperf {mode} {config.Name}: same frame while snapshots run ({snapMisses} misses)");
        Native.vlqt_res_release(snap);
        s.Check(renderFailures == 0, $"qttextureperf {mode} {config.Name}: render_batch rendered every scene in every frame ({renderFailures} frames short) {(renderFailures > 0 ? Native.LastError() : "")}");
        s.Check(misses == 0, $"qttextureperf {mode} {config.Name}: every texture holds the frame's marker in every frame ({misses} misses, same frame)");

        for (int k = 0; k < count; k++)
        {
            Native.vlqt_offscreen_unbind(scenes[k]);
            Native.vlqt_scene_destroy(scenes[k]);
            NativeTest.vlqt_test_com_release(textures[k]);
        }
        return perTexture;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescFn(IntPtr self, IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    private static T VTable<T>(IntPtr obj, int index) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    // True if device runs on a software adapter (WARP, vendor Microsoft 0x1414); name = adapter description.
    private static bool IsSoftwareAdapter(IntPtr device, out string name)
    {
        name = "unknown";
        var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(device, in iidDxgiDevice, out var dxgiDevice) != 0)
            return false;
        IntPtr adapter = IntPtr.Zero, desc = Marshal.AllocHGlobal(512);
        try
        {
            // IDXGIDevice::GetAdapter = 7, IDXGIAdapter::GetDesc = 8 (IUnknown 0-2, IDXGIObject 3-6).
            if (VTable<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out adapter) != 0
                || VTable<GetDescFn>(adapter, 8)(adapter, desc) != 0)
                return false;
            // DXGI_ADAPTER_DESC: WCHAR Description[128], UINT VendorId.
            name = Marshal.PtrToStringUni(desc, 128).Split('\0')[0];
            return Marshal.ReadInt32(desc, 256) == 0x1414;
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
            if (adapter != IntPtr.Zero)
                VTable<ReleaseFn>(adapter, 2)(adapter);
            VTable<ReleaseFn>(dxgiDevice, 2)(dxgiDevice);
        }
    }

    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
    private static double Median(List<double> v) => v.OrderBy(x => x).ElementAt(v.Count / 2);
    private static double P95(List<double> v) => v.OrderBy(x => x).ElementAt((int)(v.Count * 0.95));
}
