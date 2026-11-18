using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VL.Qt
{
    /// <summary>P/Invoke surface of QtNative vlqt.dll / libvlqt.so (C ABI, cdecl, UTF-8).</summary>
    internal static class Native
    {
        const string Lib = "vlqt";

        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf
        {
            public double fps, frame_ms, frame_ms_max, pump_ms, pump_ms_max;
            public double sync_ms, render_ms, present_ms, gpu_ms, gpu_ms_max, swaps_per_pump;
            public int samples;
            public double vsync_ms;
        }

        /// <summary>One pump of a window (vlqt.h vlqt_perf_sample, #243), ms.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf_sample
        {
            public float frame, pump, sync, render, present, gpu, events;
            public int swaps;
            public float vsync;
        }

        /// <summary>Percentiles over the ring, counters since start and over the last 10 s, budget (vlqt.h vlqt_perf_stats, #243).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf_stats
        {
            public double budget_ms;
            public double frame_p50, frame_p95, frame_p99, frame_max;
            public double pump_p50, pump_p95, pump_p99, pump_max;
            public int samples, frames;
            public long pumps, over_budget, long_frames, idle, multi_swap;
            public long recent_pumps, recent_over_budget, recent_long_frames, recent_idle, recent_multi_swap;
        }

        /// <summary>What the host measured around one pump of a window (vlqt.h vlqt_host_frame, #245), ms.</summary>
        /// <summary>Details of the last apply of a window (vlqt.h, #92/#275); rc 0 = it succeeded.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_apply_error
        {
            public int rc, command;
            public uint id;
            public ushort op, prop;
            public IntPtr reason;

            /// <summary>Reason text without the "apply: command #n" prefix (allocates; only on errors).</summary>
            public readonly string Reason => Marshal.PtrToStringUTF8(reason) ?? "";
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_host_frame
        {
            public float apply, reconcile, host, interval, gc_pause;
            public int ops, gc0, gc1, gc2;
            public long alloc_bytes;
            /// <summary>#260: vvvv frame start → pump start; end of previous pump → vvvv frame start (0 = unknown).</summary>
            public float vvvv, wait;
        }

        /// <summary>Host frames over the last ~1 s: averages, maxima, rates per second (vlqt.h vlqt_host_stats, #245).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_host_stats
        {
            public double apply_ms, apply_ms_max, reconcile_ms, reconcile_ms_max, host_ms, host_ms_max;
            public double ops, ops_max, gc0_per_s, gc1_per_s, gc2_per_s, gc_pause_ms_per_s;
            public double alloc_bytes, alloc_bytes_max;
            public int samples;
            public double vvvv_ms, vvvv_ms_max, wait_ms, wait_ms_max; // #260
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogFn(int level, IntPtr category, IntPtr message);

        public static readonly string NativeDir;

        /// <summary>File name of QtNative: vlqt.dll (Windows) or libvlqt.so (Linux).</summary>
        static readonly string FileName = OperatingSystem.IsWindows() ? "vlqt.dll" : "libvlqt.so";

        static Native()
        {
            NativeDir = FindNativeDir();
            if (OperatingSystem.IsWindows())
            {
                // Qt DLLs are resolved from NativeDir, not from next to vvvv.exe.
                // Process-local PATH prepend instead of SetDefaultDllDirectories, which would drop PATH
                // from the DLL search of the whole process (other vvvv packages rely on it).
                // QML plugins in qml/** need this to find Qt6Quick*.dll in NativeDir.
                // Linux: libvlqt.so finds Qt via its RUNPATH ($ORIGIN, L2b/L3b); PATH plays no role there.
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!path.StartsWith(NativeDir + ";", StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("PATH", NativeDir + ";" + path);
            }
            var handle = NativeLibrary.Load(Path.Combine(NativeDir, FileName));
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
                (name, _, _) => name == Lib ? handle : IntPtr.Zero);
        }

        static string FindNativeDir()
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException($"VL.Qt: unsupported OS {RuntimeInformation.OSDescription}");
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var a => throw new PlatformNotSupportedException($"VL.Qt: unsupported architecture {a}")
            };
            var rid = os + "-" + arch;
            var asmDir = Path.GetDirectoryName(typeof(Native).Assembly.Location)!;
            // Layouts (docs/decisions/qtnative-deployment.md): the assembly sits in lib/net10.0/ in the repo and the package.
            // The package keeps the Qt tree in qt/<rid>/, outside runtimes/<rid>/native, which NuGet would flatten (#315).
            foreach (var candidate in new[] {
                Path.Combine(asmDir, "runtimes", rid, "native"),               // app output (SmokeTest, export)
                Path.Combine(asmDir, "..", "..", "qt", rid),                   // package (lib/net10.0/ + qt/<rid>/)
                Path.Combine(asmDir, "..", "..", "runtimes", rid, "native") }) // repo sources (lib/net10.0/ + runtimes/)
            {
                if (File.Exists(Path.Combine(candidate, FileName)))
                    return Path.GetFullPath(candidate);
            }
            throw new DllNotFoundException($"VL.Qt: {FileName} for {rid} not found relative to {asmDir}");
        }

        /// <summary>Windows MAX_PATH (incl. the terminating NUL): Qt does not load plugins from longer paths (#313).</summary>
        public const int maxPath = 260;

        static readonly Lazy<(int Length, string Path)> s_longestPath = new(FindLongestPath);

        /// <summary>Longest file path in the Qt tree under <see cref="NativeDir"/> (computed once, enumerates the tree).</summary>
        public static (int Length, string Path) LongestPath => s_longestPath.Value;

        /// <summary>
        /// True on Windows when a file in the Qt tree reaches MAX_PATH. Qt then fails to load such plugins and reports them
        /// as "not found" (e.g. module "QtQuick.Controls" plugin ... not found), although the file is there (#313).
        /// </summary>
        public static bool PathsTooLong => OperatingSystem.IsWindows() && LongestPath.Length >= maxPath;

        /// <summary>Hint for messages about Qt plugins that were not found while <see cref="PathsTooLong"/>.</summary>
        public static string PathTooLongHint
            => $"The VL.Qt files are installed under a path that is too long for Windows ({LongestPath.Length} >= {maxPath} characters). Move the package or exported app to a shorter path.";

        static (int Length, string Path) FindLongestPath()
        {
            var longest = (Length: 0, Path: "");
            try
            {
                foreach (var file in Directory.EnumerateFiles(NativeDir, "*", SearchOption.AllDirectories))
                {
                    if (file.Length > longest.Length)
                        longest = (file.Length, file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // diagnostics only
            }
            return longest;
        }

        /// <summary>Forces the static constructor (DLL setup) to run.</summary>
        public static void EnsureLoaded() { }

        public static string LastError() => Marshal.PtrToStringUTF8(vlqt_last_error()) ?? "";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_init([MarshalAs(UnmanagedType.LPUTF8Str)] string nativeDir, LogFn log);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_app_process_events();
        /// <summary>#314: only <paramref name="windows"/>[0..count) may render in this pump (the windows of one vvvv app).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_app_process_events_for(IntPtr[] windows, int count);
        /// <summary>
        /// #24: delivers the queued window system events (input) without rendering, at the start of the host frame.
        /// Number of delivered events, -1 = app not initialized.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_deliver_input();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_driver_advance(double seconds);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr vlqt_last_error();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vlqt_window_create(int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        /// <summary>
        /// Creates the (hidden) window with its initial geometry in DIP, set before show: x/y = outer top-left incl. frame
        /// (NaN = centered on the primary working area; a rect on no screen is centered too), width/height = client size
        /// (&lt; 1 = 600x400). swapInterval 0 = Present never waits, 1 = VSync (fixed at creation). Wayland ignores x/y.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vlqt_window_create_at(float x, float y, int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, int swapInterval);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_destroy(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_show(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_is_visible(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_title(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_resize(IntPtr w, int width, int height);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_icon(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Path);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_hide(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_take_close_request(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_grab(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string pngPath);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_perf(IntPtr w, out vlqt_perf perf);
        /// <summary>Last completed pump. 0 = ok, -1 null window, -2 no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_perf_sample(IntPtr w, out vlqt_perf_sample sample);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_perf_stats(IntPtr w, out vlqt_perf_stats stats);
        /// <summary>Test: vlqt_perf_stats computed from given samples and timestamps (ms, ascending) with a fixed budget.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_perf_eval(vlqt_perf_sample[] samples, double[] t_ms, int n, double budget_ms, out vlqt_perf_stats stats);
        /// <summary>#245: host values of the last completed pump. 0 = ok, -1 null window/in, -2 no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_report_host_frame(IntPtr w, in vlqt_host_frame frame);
        /// <summary>Last reported host frame. 0 = ok, -1 null window, -2 none for the last pump.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_host_frame(IntPtr w, out vlqt_host_frame frame);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_host_stats(IntPtr w, out vlqt_host_stats stats);
        /// <summary>Test: vlqt_host_stats computed from given host frames.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_perf_host_eval(vlqt_host_frame[] frames, int n, out vlqt_host_stats stats);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_overlay(IntPtr w, int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_overlay(IntPtr w);
        /// <summary>Debug overlay for all elements of the window (#279); per element: <see cref="Protocol.Prop.Debug"/>. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_debug(IntPtr w, int on);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_debug(IntPtr w);
        /// <summary>Changes the <see cref="Protocol.WindowMode"/> bits in mask to their value in value, live. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_mode(IntPtr w, uint mask, uint value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint vlqt_window_get_mode(IntPtr w);

        /// <summary>Size (DIP) and device pixel ratio of a scene (vlqt.h, #143); an offscreen scene has dpr 1.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_scene_metrics
        {
            public float width, height, dpr;
        }

        /// <summary>Live size and dpr of the scene of a window or offscreen scene (#143). 0 = ok, -1 null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_scene_metrics(IntPtr w, out vlqt_scene_metrics metrics);
        /// <summary>
        /// Sets the MSAA sample count of the window (#118; &lt; 1 = none), clamped to what the device supports; returns the
        /// count used (&gt;= 1) or &lt;0 = error. A shown window is replaced inside the same handle (vlqt.h).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_samples(IntPtr w, int requested);
        /// <summary>Sample count of the window's swap chain, or the count it will be created with; 0 = null or offscreen scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_samples(IntPtr w);
        /// <summary>Applies a command buffer (protocol.h). 0 = ok, &lt;0 = error, see <see cref="LastError"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_window_apply(IntPtr w, in byte buf, int len);
        /// <summary>Structured last apply error of the window (failing object id, op, property). 0 = ok, -1 null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_apply_error(IntPtr w, out vlqt_apply_error error);
        /// <summary>Copies queued events into out; returns bytes written, 0 = none, -required if out is too small, -1 = null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_window_poll(IntPtr w, ref byte @out, int cap);

        /// <summary>TEST ONLY: synthetic mouse event (<see cref="Protocol.Mouse"/>), x/y in window DIP. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_mouse(IntPtr w, int type, float x, float y);
        /// <summary>TEST ONLY: synthetic wheel event at x/y (window DIP), angleDelta in eighths of a degree (120 = one notch). 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_wheel(IntPtr w, float x, float y, int dx, int dy);
        /// <summary>TEST ONLY: number of pointer handlers (input handlers) of item id, &lt;0 = error.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_handler_count(IntPtr w, uint id);
        /// <summary>
        /// TEST ONLY: synthetic touch point event (<see cref="Protocol.Touch"/>) for point id, x/y in window DIP. QtNative keeps
        /// the active points per window and sends the complete QTouchEvent. 0 = ok, -1 null window, -2 unknown type or id state.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_touch(IntPtr w, int type, int id, float x, float y);
        /// <summary>TEST ONLY (#24): mouse event (Move, Press, Release) queued like platform input, delivered later. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_queue_mouse(IntPtr w, int type, float x, float y);
        /// <summary>TEST ONLY (#24): touch point event queued like platform input, delivered later. 0 = ok, -2 unknown type or id state.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_queue_touch(IntPtr w, int type, int id, float x, float y);
        /// <summary>
        /// TEST ONLY: QRhi of window w (IntPtr.Zero = the upload QRhi on the shared device) and its native device
        /// (ID3D11Device* on D3D11). 0 = ok, -2 no QRhi yet.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_device_info(IntPtr w, out IntPtr device, out IntPtr rhi);
        /// <summary>
        /// TEST ONLY (#367): makes the shared graphics device lost (<see cref="Protocol.DeviceLost"/>); QtNative rebuilds
        /// device, windows and resources in the next pumps. 0 = ok, -2 not possible (no shared device, or REMOVE without
        /// VLQT_TEST_D3D11ON12=1), -7 unknown mode.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_device_lost(int mode);

        // Offscreen scenes (#116, QtTexture): main thread only, codes Protocol.Offscreen. The scene handle also works
        // with vlqt_window_destroy/_apply/_get_apply_error/_poll/_set_debug/_get_perf* (list in vlqt.h).
        /// <summary>Creates an unbound offscreen scene of width x height DIP; IntPtr.Zero = error. Free with vlqt_window_destroy.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vlqt_offscreen_create(int width, int height);
        /// <summary>
        /// Binds the scene to the texture behind ntHandle (shared NT handle of an ID3D11Texture2D of device, an ID3D11Device*);
        /// format = DXGI_FORMAT the caller samples the texture with (SRGB and float: linear values, vlqt.h); samples = MSAA
        /// count (&lt; 2 = none), clamped, the count used in usedSamples. A change of the count is a new bind. The caller may
        /// close ntHandle after the call.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_bind(IntPtr scene, IntPtr device, IntPtr ntHandle, int width, int height, int format, int samples, out int usedSamples);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_unbind(IntPtr scene);
        /// <summary>1 = bound, 0 = unbound (bind again, e.g. after a device loss of QtNative), &lt;0 = error.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_is_bound(IntPtr scene);
        /// <summary>
        /// Renders the changed, bound scenes of one host frame, ordered against context (the foreign immediate
        /// ID3D11DeviceContext*) by fences. Returns the number of scenes rendered, &lt;0 = error.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_offscreen_render_batch(IntPtr context, in IntPtr scenes, int count, double driverSeconds);

        public static int OffscreenRenderBatch(IntPtr context, ReadOnlySpan<IntPtr> scenes, double driverSeconds)
            => vlqt_offscreen_render_batch(context, in MemoryMarshal.GetReference(scenes), scenes.Length, driverSeconds);
        /// <summary>
        /// Where offscreen scenes render: 0 = main thread, n = 1 … 16 Qt render threads (without the call:
        /// VLQT_OFFSCREEN_THREAD, unset = 1). Only before the first vlqt_offscreen_create; afterwards OK only for the
        /// count in use, else Protocol.Offscreen.NotSupported.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_set_threaded(int threadCount);

        // Input into an offscreen scene (#119): positions in texture pixels, codes Protocol.Offscreen (vlqt.h).
        /// <summary>
        /// Pointer event (Protocol.Pointer) of device (Protocol.PointerDevice) at x/y; mouse: buttons = Qt::MouseButtons held
        /// after the event, id ignored; touch/pen: id of the point, buttons ignored. modifiers = Qt::KeyboardModifiers.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_send_pointer(IntPtr scene, int device, int type, int id, float x, float y, int buttons, int modifiers);
        /// <summary>Wheel at x/y; angle delta in eighths of a degree (120 = one notch).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_send_wheel(IntPtr scene, float x, float y, int dx, int dy, int modifiers);
        /// <summary>Key press or release (Protocol.KeyEvent) of a Qt::Key without text; autoRepeat 1 for repeated presses.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_send_key(IntPtr scene, int type, int key, int modifiers, int autoRepeat);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_offscreen_send_text(IntPtr scene, in byte utf8, int length);

        /// <summary>Typed text as UTF-8 (no NUL needed) to the focus item; a stack span keeps it allocation-free.</summary>
        public static int OffscreenSendText(IntPtr scene, ReadOnlySpan<byte> utf8)
            => vlqt_offscreen_send_text(scene, in MemoryMarshal.GetReference(utf8), utf8.Length);
        /// <summary>focused 1: FocusIn, 0: FocusOut; no-op if unchanged.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_set_focus(IntPtr scene, int focused);

        // TEST ONLY (#116): a second D3D11 device standing in for Stride's, shared textures and leak probes (vlqt.h).
        /// <summary>TEST ONLY: D3D11 device + immediate context (one reference each); adapter 0 = QtNative's, 1 = WARP.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_create_device(int adapter, out IntPtr device, out IntPtr context);
        /// <summary>TEST ONLY: releases context and device; returns the references left on the device.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_release_device(IntPtr device, IntPtr context);
        /// <summary>TEST ONLY: shared Texture2D (RT | SR, SHARED | SHARED_NTHANDLE) on device and its NT handle.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_create_shared_texture(IntPtr device, int width, int height, int dxgiFormat, out IntPtr texture, out IntPtr ntHandle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_test_d3d11_read_texture(IntPtr texture, ref byte @out, int cap, out int width, out int height, out int dxgiFormat);
        /// <summary>TEST ONLY: COM reference count of obj without the probe's own reference.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_ref_count(IntPtr obj);
        /// <summary>TEST ONLY: IUnknown::Release; returns the references left.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_release(IntPtr obj);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_close_handle(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_process_handle_count();
        /// <summary>TEST ONLY: last read/write fence values and render count of a bound scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_offscreen_state(IntPtr scene, out ulong readFence, out ulong writeFence, out long renders);

        /// <summary>TEST ONLY: texture pixels via a staging copy, rows tightly packed; bytes written, -required, &lt;0 error.</summary>
        public static int TestReadTexture(IntPtr texture, Span<byte> @out, out int width, out int height, out int dxgiFormat)
            => vlqt_test_d3d11_read_texture(texture, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height, out dxgiFormat);

        /// <summary>One resource of vlqt_res_list (vlqt.h vlqt_res_info, #359).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_res_info
        {
            public ulong id;
            public int kind, state;
            public long bytes;
            public int host_refs, element_refs;
        }

        // Resources (#359): main thread only; 0 = ok, -1 internal error, -7 bad argument/state, -9 unknown or stale id.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_create(int kind, out ulong id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_release(ulong id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_set_budget(int bytes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_poll(ref byte @out, int cap);
        /// <summary>Blocks until Ready/Failed or timeout; returns the state (<see cref="Protocol.ResState"/>) or -9.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_wait(ulong id, int timeoutMs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_list(ref vlqt_res_info @out, int cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_res_shutdown();
        /// <summary>Pixels of vlqt_res_set_pixels (vlqt.h vlqt_pixel_desc, #360); format = VL PixelFormat number.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_pixel_desc
        {
            public int width, height, row_bytes, format, premultiplied;
            /// <summary>vlqt_res_set_pixels: <see cref="Protocol.Img"/> flags (#546); 0 when QtNative fills the desc.</summary>
            public int flags;
        }

        /// <summary>Starts or replaces the content of an image resource from a file (flags <see cref="Protocol.Img"/>), #360.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_load_image(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags);

        /// <summary>
        /// Starts or replaces the content of a shader resource (<see cref="Protocol.ResKind.Shader"/>) from a .qsb file or
        /// "builtin:&lt;name&gt;" (shipped shader), stage <see cref="Protocol.ShaderStage"/>, #437. 0 / -7 / -9.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_load_shader(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int stage);

        /// <summary>
        /// Starts or replaces the content of a shader resource from full Qt GLSL source (#version 440), baked by QtNative
        /// in a worker (QShaderBaker), cached per hash of the source, #483. 0 / -7 / -9; bake errors arrive as RES_EV_ERROR.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_load_shader_source(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage);

        /// <summary>
        /// Synchronous SPIR-V bake + reflection of Qt GLSL source (#488), any thread. Writes UTF-8 text (format in vlqt.h)
        /// to buf and returns the required length without terminator (> bufLen: call again with a larger buffer), -7 on
        /// bad arguments. Use <see cref="Shaders.ShaderReflection.Reflect"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_shader_reflect([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage, ref byte buf, int bufLen);

        /// <summary>
        /// Load + save file of Qt's pipeline cache for all windows created afterwards (#488); null/empty = off. 0 / -7.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_set_pipeline_cache([MarshalAs(UnmanagedType.LPUTF8Str)] string? path);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_set_pixels(ulong id, in vlqt_pixel_desc desc, in byte data, int len);
        /// <summary>TEST ONLY: content of a Ready image as RGBA8 premultiplied; bytes written, -required, -9 not Ready.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_test_res_read_pixels(ulong id, ref byte @out, int cap, out int width, out int height);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_image_write(in vlqt_pixel_desc desc, in byte data, int len, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int quality, ref byte err, int errCap);

        /// <summary>
        /// Writes pixels to an image file, format by extension (vlqt.h vlqt_image_write, #157); any thread, synchronous.
        /// Returns 0, or -1 bad arguments / -2 unsupported file format / -3 write failed with the reason in error.
        /// </summary>
        public static int ImageWrite(in vlqt_pixel_desc desc, ReadOnlySpan<byte> data, string path, int quality, out string error)
        {
            Span<byte> err = stackalloc byte[512];
            err.Clear();
            var rc = vlqt_image_write(in desc, in MemoryMarshal.GetReference(data), data.Length, path, quality, ref MemoryMarshal.GetReference(err), err.Length);
            var end = err.IndexOf((byte)0);
            error = rc < 0 ? System.Text.Encoding.UTF8.GetString(end < 0 ? err : err[..end]) : string.Empty;
            return rc;
        }

        /// <summary>TEST ONLY: writes a stripe test image (red, green, blue, white) as PNG/JPG by extension.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_write_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int width, int height, int quality);

        /// <summary>Starts or replaces the content of an image resource from pixels (copied synchronously), #360.</summary>
        public static int ResSetPixels(ulong id, in vlqt_pixel_desc desc, ReadOnlySpan<byte> data)
            => vlqt_res_set_pixels(id, in desc, in MemoryMarshal.GetReference(data), data.Length);
        public static int ResReadPixels(ulong id, Span<byte> @out, out int width, out int height)
            => vlqt_test_res_read_pixels(id, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height);

        /// <summary>TEST ONLY: starts a Pending test resource (decode sleeps decodeMs, upload uses uploadBytes; flags 1 = decode fails, 2 = upload fails).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_start(ulong id, int decodeMs, long uploadBytes, int flags);
        /// <summary>TEST ONLY: state machine transition; 0 = done, -7 = rejected, -9 = stale id.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_transition(ulong id, int from, int to);
        /// <summary>TEST ONLY: element reference +1 / -1.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_element_ref(ulong id, int delta);

        /// <summary>Starts grabbing element elementId of window w into a snapshot resource (asynchronous, #363); 0 / -7 / -9.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_snapshot(ulong id, IntPtr w, uint elementId, float scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_read_pixels(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc);

        /// <summary>Pixels of the last finished snapshot (BGRA8 premultiplied, #363): bytes written, -required (desc set), -7 none yet, -9.</summary>
        public static int ResReadSnapshot(ulong id, Span<byte> dst, out vlqt_pixel_desc desc)
            => vlqt_res_read_pixels(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc);

        // Tooltip previews (#543, package 01 #544).
        /// <summary>Number of frames with scene changes in window w (not the renders a grab triggers); &gt;= 0, -9 null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_window_change_count(IntPtr w);
        /// <summary>Like vlqt_res_snapshot, scale so that the longest edge is &lt;= maxEdgePx and scale &lt;= 1 (pixels per DIP); 0 / -7 / -9.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_snapshot_fit(ulong id, IntPtr w, uint elementId, int maxEdgePx);

        // QtImage tooltip preview (#543, package 03 #546).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_read_preview(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc);

        /// <summary>Tooltip preview of an image loaded with <see cref="Protocol.Img.Preview"/> (BGRA8 premultiplied, &lt;= 256 px, #546): bytes written, -required (desc set), -7 none, -9.</summary>
        public static int ResReadPreview(ulong id, Span<byte> dst, out vlqt_pixel_desc desc)
            => vlqt_res_read_preview(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc);

        /// <summary>
        /// State of an audio tap (vlqt.h vlqt_audio_tap_state, 96 bytes, #22). Times in ns of <see cref="vlqt_now_ns"/>,
        /// media times in us on Qt's audio axis (continues across loop wraps). "First frame" = the first frame the read
        /// delivered or dropped, else the next one in the ring. Contract: protocol.h "Audio tap".
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_audio_tap_state
        {
            /// <summary>vlqt_now_ns at the call.</summary>
            public long now_ns;
            /// <summary>When the first frame is due (emit time + k / (sample_rate * rate)); 0 = no frame.</summary>
            public long due_ns;
            /// <summary>Media time of the first frame; 0 = no frame.</summary>
            public long media_us;
            /// <summary>Diagnostics: startTime of the last video frame at the sink (restarts at a loop wrap).</summary>
            public long video_media_us;
            /// <summary>Diagnostics: vlqt_now_ns when that frame reached the sink; 0 = none yet.</summary>
            public long video_ns;
            public ulong overrun_frames;
            public ulong underrun_frames;
            /// <summary>Playback rate of the first frame's segment; 0 = no frame.</summary>
            public double rate;
            /// <summary>Segment counter of the first frame; a change means flush and re-lock.</summary>
            public uint discontinuity;
            /// <summary>Frames of the current segment left in the ring after the call.</summary>
            public int available;
            public int capacity;
            public int channels;
            public int sample_rate;
            /// <summary><see cref="Protocol.AudioTap"/> flags.</summary>
            public int flags;
            int _reserved0, _reserved1;
        }

        // Audio taps (#22): init on the main thread; read and now on any thread (VL.Audio's audio thread), no allocation.
        /// <summary>Fixes the format of a Pending audio tap (float, interleaved) and makes it Ready. 0 / -7 / -9.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_audio_tap_init(ulong id, int channels, int sampleRate);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_read_audio(ulong id, ref float dst, int maxFrames, int channels, out vlqt_audio_tap_state state);
        [DllImport(Lib, EntryPoint = "vlqt_res_read_audio", CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_res_read_audio_drop(ulong id, IntPtr dst, int maxFrames, int channels, out vlqt_audio_tap_state state);
        /// <summary>The clock of the audio tap times in ns (steady clock: QPC on Windows = Stopwatch.GetTimestamp converted to ns).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_now_ns();

        /// <summary>
        /// Copies and consumes up to dst.Length / channels frames of one segment (interleaved). Returns the frames copied,
        /// -7 (channels mismatch, not initialised) or -9 (released id). An empty dst only fills the state.
        /// </summary>
        public static int ResReadAudio(ulong id, Span<float> dst, int channels, out vlqt_audio_tap_state state)
            => vlqt_res_read_audio(id, ref MemoryMarshal.GetReference(dst), channels > 0 ? dst.Length / channels : 0, channels, out state);

        /// <summary>Drops up to maxFrames frames of one segment without copying (catching up). Returns the frames dropped, -7 or -9.</summary>
        public static int ResDropAudio(ulong id, int maxFrames, int channels, out vlqt_audio_tap_state state)
            => vlqt_res_read_audio_drop(id, IntPtr.Zero, maxFrames, channels, out state);

        // App fonts (#15, LoadFont node): Qt GUI thread (vvvv main thread) only, reference counted per file in QtNative.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern int vlqt_font_add([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out ulong handle, ref byte families, int familiesCap);
        /// <summary>Drops one reference of a font handle; the font is removed at the end of the pump when none is left. 0 / -1 wrong thread / -9.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_font_release(ulong handle);
        /// <summary>Test: references of a font handle, 0 = removal pending, -9 = removed or unknown.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_font_refs(ulong handle);

        /// <summary>
        /// Registers a font file (or shares its registration) and takes one reference: 0 with the handle and the family
        /// names of the file ('\n'-separated), -1 on error (<see cref="LastError"/>).
        /// </summary>
        public static int FontAdd(string path, out ulong handle, out string families)
        {
            Span<byte> buffer = stackalloc byte[1024];
            var rc = vlqt_font_add(path, out handle, ref MemoryMarshal.GetReference(buffer), buffer.Length);
            var length = buffer.IndexOf((byte)0);
            families = rc == 0 && length > 0 ? System.Text.Encoding.UTF8.GetString(buffer[..length]) : string.Empty;
            return rc;
        }

        public static int ResPoll(Span<byte> @out) => vlqt_res_poll(ref MemoryMarshal.GetReference(@out), @out.Length);
        public static int ResList(Span<vlqt_res_info> @out) => vlqt_res_list(ref MemoryMarshal.GetReference(@out), @out.Length);

        public static int Apply(IntPtr w, ReadOnlySpan<byte> buf) => vlqt_window_apply(w, in MemoryMarshal.GetReference(buf), buf.Length);
        public static int Poll(IntPtr w, Span<byte> @out) => vlqt_window_poll(w, ref MemoryMarshal.GetReference(@out), @out.Length);
    }
}
