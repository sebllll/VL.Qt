using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VL.Qt
{
    /// <summary>P/Invoke surface of QtNative vlqt.dll / libvlqt.so (C ABI, cdecl, UTF-8).</summary>
    internal static class Native
    {
        private const string Lib = "vlqt";

        /// <summary>
        /// Return codes of every export (vlqt.h <c>vlqt_result</c>): 0 = ok, &lt; 0 = an error whose text is in <see cref="LastError"/>.
        /// An export that also returns a count or a state (&gt;= 0) returns an int; compare it with <c>(int)Result.X</c> or cast
        /// a negative value. The unit test ProtocolDriftTests compares the members with vlqt.h by name and value.
        /// </summary>
        public enum Result
        {
            Ok = 0,
            /// <summary>Null handle, or one of the wrong kind (a window where a scene is needed).</summary>
            NullHandle = -1,
            /// <summary>Apply: truncated buffer, wrong payload length for the op or value type, bad alignment.</summary>
            BadLength = -2,
            /// <summary>Apply: unknown op.</summary>
            UnknownOp = -3,
            /// <summary>Apply: unknown object id.</summary>
            UnknownObject = -4,
            /// <summary>Apply: unknown element type.</summary>
            UnknownType = -5,
            /// <summary>Apply: property, field or state the element type does not have.</summary>
            UnknownProperty = -6,
            /// <summary>Argument out of range or not allowed in this state; apply: value of the wrong type or rejected by the setter.</summary>
            BadArgument = -7,
            /// <summary>Apply: id 0 or already used, cycle, root.</summary>
            Invalid = -8,
            /// <summary>Resource id or font handle unknown, stale or released.</summary>
            UnknownId = -9,
            /// <summary>Nothing to read yet: no pump, no host frame, no snapshot, no preview, no QRhi.</summary>
            NoData = -10,
            /// <summary>Called on another thread than the one the function needs.</summary>
            WrongThread = -11,
            /// <summary>A buffer export: nothing copied, <c>required</c> holds the size.</summary>
            BufferTooSmall = -12,
            /// <summary>File format, platform or graphics API, or not available on an offscreen scene.</summary>
            Unsupported = -13,
            /// <summary>A file could not be read or written.</summary>
            Io = -14,
            /// <summary>vlqt_app_init has not run.</summary>
            NotInitialized = -15,
            /// <summary>Offscreen: the scene has no texture.</summary>
            Unbound = -20,
            /// <summary>Offscreen: the foreign device is on another adapter than QtNative's device.</summary>
            AdapterMismatch = -21,
            /// <summary>Offscreen: OpenSharedResource1 failed or the handle is no usable Texture2D.</summary>
            OpenFailed = -22,
            /// <summary>Offscreen: the render target view format is not allowed or does not fit the texture.</summary>
            FormatUnsupported = -23,
            /// <summary>Offscreen: no ID3D11Device5/ID3D11DeviceContext4, or a fence could not be created, shared or opened.</summary>
            FenceFailed = -24,
            /// <summary>Offscreen: QtNative's device is lost or being rebuilt; try again next frame.</summary>
            DeviceLost = -25,
            /// <summary>Exception or Qt/driver failure inside QtNative.</summary>
            Internal = -100,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf
        {
            public double fps, frame_ms, frame_ms_max, pump_ms, pump_ms_max;
            public double sync_ms, render_ms, present_ms, gpu_ms, gpu_ms_max, swaps_per_pump;
            public int samples;
            public double vsync_ms;
        }

        /// <summary>One pump of a window (vlqt.h vlqt_perf_sample), ms.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_perf_sample
        {
            public float frame, pump, sync, render, present, gpu, events;
            public int swaps;
            public float vsync;
        }

        /// <summary>Percentiles over the ring, counters since start and over the last 10 s, budget (vlqt.h vlqt_perf_stats).</summary>
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

        /// <summary>What the host measured around one pump of a window (vlqt.h vlqt_host_frame), ms.</summary>
        /// <summary>Details of the last apply of a window (vlqt.h); rc 0 = it succeeded.</summary>
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
            /// <summary>vvvv frame start → pump start; end of previous pump → vvvv frame start (0 = unknown).</summary>
            public float vvvv, wait;
        }

        /// <summary>Host frames over the last ~1 s: averages, maxima, rates per second (vlqt.h vlqt_host_stats).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_host_stats
        {
            public double apply_ms, apply_ms_max, reconcile_ms, reconcile_ms_max, host_ms, host_ms_max;
            public double ops, ops_max, gc0_per_s, gc1_per_s, gc2_per_s, gc_pause_ms_per_s;
            public double alloc_bytes, alloc_bytes_max;
            public int samples;
            public double vvvv_ms, vvvv_ms_max, wait_ms, wait_ms_max;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogFn(int level, IntPtr category, IntPtr message);

        public static readonly string NativeDir;

        /// <summary>Full path of the loaded vlqt.dll / libvlqt.so.</summary>
        public static readonly string LibraryPath;

        /// <summary>Handle of the loaded vlqt.dll / libvlqt.so, for the DllImport resolver of the test support.</summary>
        internal static readonly IntPtr Handle;

        /// <summary>File name of QtNative: vlqt.dll (Windows) or libvlqt.so (Linux).</summary>
        private static readonly string FileName = OperatingSystem.IsWindows() ? "vlqt.dll" : "libvlqt.so";

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
            LibraryPath = Path.Combine(NativeDir, FileName);
            Handle = NativeLibrary.Load(LibraryPath);
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
                (name, _, _) => name == Lib ? Handle : IntPtr.Zero);
        }

        private static string FindNativeDir()
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
            // The package keeps the Qt tree in qt/<rid>/, outside runtimes/<rid>/native, which NuGet would flatten.
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

        /// <summary>Windows MAX_PATH (incl. the terminating NUL): Qt does not load plugins from longer paths.</summary>
        public const int maxPath = 260;

        private static readonly Lazy<(int Length, string Path)> s_longestPath = new(FindLongestPath);

        /// <summary>Longest file path in the Qt tree under <see cref="NativeDir"/> (computed once, enumerates the tree).</summary>
        public static (int Length, string Path) LongestPath => s_longestPath.Value;

        /// <summary>
        /// True on Windows when a file in the Qt tree reaches MAX_PATH. Qt then fails to load such plugins and reports them
        /// as "not found" (e.g. module "QtQuick.Controls" plugin ... not found), although the file is there.
        /// </summary>
        public static bool PathsTooLong => OperatingSystem.IsWindows() && LongestPath.Length >= maxPath;

        /// <summary>Hint for messages about Qt plugins that were not found while <see cref="PathsTooLong"/>.</summary>
        public static string PathTooLongHint
            => $"The VL.Qt files are installed under a path that is too long for Windows ({LongestPath.Length} >= {maxPath} characters). Move the package or exported app to a shorter path.";

        private static (int Length, string Path) FindLongestPath()
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

        /// <summary>ABI version of the loaded library; null if it has no vlqt_app_get_abi_version (a library older than the check).</summary>
        public static int? ReadAbiVersion()
        {
            try { return vlqt_app_get_abi_version(); }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string LastError() => Marshal.PtrToStringUTF8(vlqt_last_error()) ?? "";

        /// <summary>ABI version of the library (VLQT_ABI_VERSION): protocol constants, struct layouts, signatures. Needs no init.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_get_abi_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_init([MarshalAs(UnmanagedType.LPUTF8Str)] string nativeDir, LogFn log);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_process_events();
        /// <summary>only <paramref name="windows"/>[0..count) may render in this pump (the windows of one vvvv app).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_process_events_for(IntPtr[] windows, int count);
        /// <summary>
        /// delivers the queued window system events (input) without rendering, at the start of the host frame.
        /// Number of delivered events, <see cref="Result.NotInitialized"/> = app not initialized.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_deliver_input();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_driver_advance(double seconds);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vlqt_last_error();

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
        public static extern Result vlqt_window_set_title(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_resize(IntPtr w, int width, int height);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_icon(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Path);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_hide(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_take_close_request(IntPtr w);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_perf(IntPtr w, out vlqt_perf perf);
        /// <summary>Last completed pump; <see cref="Result.NoData"/> = no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_perf_sample(IntPtr w, out vlqt_perf_sample sample);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_perf_stats(IntPtr w, out vlqt_perf_stats stats);
        /// <summary>host values of the last completed pump; <see cref="Result.NoData"/> = no pump yet.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_report_host_frame(IntPtr w, in vlqt_host_frame frame);
        /// <summary>Last reported host frame; <see cref="Result.NoData"/> = none for the last pump.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_host_frame(IntPtr w, out vlqt_host_frame frame);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_host_stats(IntPtr w, out vlqt_host_stats stats);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_overlay(IntPtr w, int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_overlay(IntPtr w);
        /// <summary>Debug overlay for all elements of the window; per element: <see cref="Protocol.Prop.Debug"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_debug(IntPtr w, int on);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_debug(IntPtr w);
        /// <summary>Changes the <see cref="Protocol.WindowMode"/> bits in mask to their value in value, live.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_mode(IntPtr w, uint mask, uint value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_mode(IntPtr w);

        /// <summary>Size (DIP) and device pixel ratio of a scene (vlqt.h); an offscreen scene has dpr 1.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_scene_metrics
        {
            public float width, height, dpr;
        }

        /// <summary>Live size and dpr of the scene of a window or offscreen scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_scene_metrics(IntPtr w, out vlqt_scene_metrics metrics);
        /// <summary>
        /// Sets the MSAA sample count of the window (&lt; 1 = none), clamped to what the device supports; returns the
        /// count used (&gt;= 1) or a negative <see cref="Result"/> (<see cref="Result.Unsupported"/> on an offscreen scene). A shown window is replaced inside the same handle (vlqt.h).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_samples(IntPtr w, int requested);
        /// <summary>Sample count of the window's swap chain, or the count it will be created with; 0 = offscreen scene, <see cref="Result.NullHandle"/> = null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_samples(IntPtr w);
        /// <summary>Applies a command buffer (protocol.h). <see cref="Result.Ok"/>, <see cref="Result.NullHandle"/> or the apply range <see cref="Result.BadLength"/> ... <see cref="Result.Invalid"/>; text: <see cref="LastError"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_window_apply(IntPtr w, in byte buf, int len);
        /// <summary>Structured last apply error of the window (failing object id, op, property).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_get_apply_error(IntPtr w, out vlqt_apply_error error);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_window_poll(IntPtr w, ref byte @out, int cap, out int written, out int required);

        // Offscreen scenes (QtTexture): main thread only, codes <see cref="Result"/>. The scene handle also works
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
        public static extern Result vlqt_offscreen_bind(IntPtr scene, IntPtr device, IntPtr ntHandle, int width, int height, int format, int samples, out int usedSamples);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_unbind(IntPtr scene);
        /// <summary>1 = bound, 0 = unbound (bind again, e.g. after a device loss of QtNative), <see cref="Result.NullHandle"/> = null handle or no offscreen scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_is_bound(IntPtr scene);
        /// <summary>
        /// Renders the changed, bound scenes of one host frame, ordered against context (the foreign immediate
        /// ID3D11DeviceContext*) by fences. Returns the number of scenes rendered, or a negative <see cref="Result"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_offscreen_render_batch(IntPtr context, in IntPtr scenes, int count, double driverSeconds);

        public static int OffscreenRenderBatch(IntPtr context, ReadOnlySpan<IntPtr> scenes, double driverSeconds)
            => vlqt_offscreen_render_batch(context, in MemoryMarshal.GetReference(scenes), scenes.Length, driverSeconds);
        /// <summary>
        /// Where offscreen scenes render: 0 = main thread, n = 1 … 16 Qt render threads (without the call:
        /// VLQT_OFFSCREEN_THREAD, unset = 1). Only before the first vlqt_offscreen_create; afterwards OK only for the
        /// count in use, else <see cref="Result.Unsupported"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_set_threaded(int threadCount);

        // Input into an offscreen scene: positions in texture pixels, codes <see cref="Result"/> (vlqt.h).
        /// <summary>
        /// Pointer event (Protocol.Pointer) of device (Protocol.PointerDevice) at x/y; mouse: buttons = Qt::MouseButtons held
        /// after the event, id ignored; touch/pen: id of the point, buttons ignored. modifiers = Qt::KeyboardModifiers.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_pointer(IntPtr scene, int device, int type, int id, float x, float y, int buttons, int modifiers);
        /// <summary>Wheel at x/y; angle delta in eighths of a degree (120 = one notch).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_wheel(IntPtr scene, float x, float y, int dx, int dy, int modifiers);
        /// <summary>Key press or release (Protocol.KeyEvent) of a Qt::Key without text; autoRepeat 1 for repeated presses.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_key(IntPtr scene, int type, int key, int modifiers, int autoRepeat);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_offscreen_send_text(IntPtr scene, in byte utf8, int length);

        /// <summary>Typed text as UTF-8 (no NUL needed) to the focus item; a stack span keeps it allocation-free.</summary>
        public static Result OffscreenSendText(IntPtr scene, ReadOnlySpan<byte> utf8)
            => vlqt_offscreen_send_text(scene, in MemoryMarshal.GetReference(utf8), utf8.Length);
        /// <summary>focused 1: FocusIn, 0: FocusOut; no-op if unchanged.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_set_focus(IntPtr scene, int focused);

        /// <summary>One resource of vlqt_res_list (vlqt.h vlqt_res_info).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_res_info
        {
            public ulong id;
            public int kind, state;
            public long bytes;
            public int host_refs, element_refs;
        }

        // Resources: main thread only; Result.Internal, Result.BadArgument (argument or state), Result.UnknownId (unknown or stale id).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_create(int kind, out ulong id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_release(ulong id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_set_budget(int bytes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_res_poll(ref byte @out, int cap, out int written, out int required);
        /// <summary>Blocks until Ready/Failed or timeout; returns the state (<see cref="Protocol.ResState"/>) or <see cref="Result.UnknownId"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_wait(ulong id, int timeoutMs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_res_list(ref vlqt_res_info @out, int cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_res_shutdown();
        /// <summary>Pixels of vlqt_res_set_pixels (vlqt.h vlqt_pixel_desc); format = VL PixelFormat number.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_pixel_desc
        {
            public int width, height, row_bytes, format, premultiplied;
            /// <summary>vlqt_res_set_pixels: <see cref="Protocol.Img"/> flags; 0 when QtNative fills the desc.</summary>
            public int flags;
        }

        /// <summary>Starts or replaces the content of an image resource from a file (flags <see cref="Protocol.Img"/>).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_load_image(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags);

        /// <summary>
        /// Starts or replaces the content of a shader resource (<see cref="Protocol.ResKind.Shader"/>) from a .qsb file or
        /// "builtin:&lt;name&gt;" (shipped shader), stage <see cref="Protocol.ShaderStage"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_load_shader(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int stage);

        /// <summary>
        /// Starts or replaces the content of a shader resource from full Qt GLSL source (#version 440), baked by QtNative
        /// in a worker (QShaderBaker), cached per hash of the source. Bake errors arrive as RES_EV_ERROR.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_load_shader_source(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage);

        /// <summary>
        /// Synchronous SPIR-V bake + reflection of Qt GLSL source, any thread. A buffer export: writes the UTF-8 text (format in
        /// vlqt.h, with its NUL) to buf. Use <see cref="Shaders.ShaderReflection.Reflect"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_shader_reflect([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage, ref byte buf, int bufLen, out int written, out int required);

        /// <summary>
        /// Load + save file of Qt's pipeline cache for all windows created afterwards; null/empty = off.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_set_pipeline_cache([MarshalAs(UnmanagedType.LPUTF8Str)] string? path);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_res_set_pixels(ulong id, in vlqt_pixel_desc desc, in byte data, int len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_image_write(in vlqt_pixel_desc desc, in byte data, int len, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int quality);

        /// <summary>
        /// Writes pixels to an image file, format by extension (vlqt.h vlqt_image_write); any thread, synchronous.
        /// Returns <see cref="Result.Ok"/>, <see cref="Result.BadArgument"/>, <see cref="Result.Unsupported"/> (file format) or
        /// <see cref="Result.Io"/> (write failed), with the reason in error (<see cref="LastError"/>, empty when ok).
        /// </summary>
        public static Result ImageWrite(in vlqt_pixel_desc desc, ReadOnlySpan<byte> data, string path, int quality, out string error)
        {
            var rc = vlqt_image_write(in desc, in MemoryMarshal.GetReference(data), data.Length, path, quality);
            error = rc != Result.Ok ? LastError() : string.Empty;
            return rc;
        }

        /// <summary>Starts or replaces the content of an image resource from pixels (copied synchronously).</summary>
        public static Result ResSetPixels(ulong id, in vlqt_pixel_desc desc, ReadOnlySpan<byte> data)
            => vlqt_res_set_pixels(id, in desc, in MemoryMarshal.GetReference(data), data.Length);

        /// <summary>Starts grabbing element elementId of window w into a snapshot resource (asynchronous).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_snapshot(ulong id, IntPtr w, uint elementId, float scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_res_read_pixels(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc, out int written, out int required);

        /// <summary>
        /// Pixels of the last finished snapshot (BGRA8 premultiplied), a buffer export: <see cref="Result.BufferTooSmall"/> with
        /// <paramref name="required"/> (desc set), <see cref="Result.NoData"/> none yet, <see cref="Result.UnknownId"/>.
        /// </summary>
        public static Result ResReadSnapshot(ulong id, Span<byte> dst, out vlqt_pixel_desc desc, out int written, out int required)
            => vlqt_res_read_pixels(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc, out written, out required);

        // Tooltip previews (package 01).
        /// <summary>Number of frames with scene changes in window w (not the renders a grab triggers); &gt;= 0, <see cref="Result.NullHandle"/> null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_window_change_count(IntPtr w);
        /// <summary>Like vlqt_res_snapshot, scale so that the longest edge is &lt;= maxEdgePx and scale &lt;= 1 (pixels per DIP).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_snapshot_fit(ulong id, IntPtr w, uint elementId, int maxEdgePx);

        // QtImage tooltip preview (package 03).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_res_read_preview(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc, out int written, out int required);

        /// <summary>
        /// Tooltip preview of an image loaded with <see cref="Protocol.Img.Preview"/> (BGRA8 premultiplied, &lt;= 256 px), a
        /// buffer export: <see cref="Result.BufferTooSmall"/> with <paramref name="required"/> (desc set),
        /// <see cref="Result.NoData"/> none, <see cref="Result.UnknownId"/>.
        /// </summary>
        public static Result ResReadPreview(ulong id, Span<byte> dst, out vlqt_pixel_desc desc, out int written, out int required)
            => vlqt_res_read_preview(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc, out written, out required);

        /// <summary>
        /// State of an audio tap (vlqt.h vlqt_audio_tap_state, 96 bytes). Times in ns of <see cref="vlqt_now_ns"/>,
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
            private int _reserved0, _reserved1;
        }

        // Audio taps: init on the main thread; read and now on any thread (VL.Audio's audio thread), no allocation.
        /// <summary>Fixes the format of a Pending audio tap (float, interleaved) and makes it Ready.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_res_audio_tap_init(ulong id, int channels, int sampleRate);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_res_read_audio(ulong id, ref float dst, int maxFrames, int channels, out vlqt_audio_tap_state state);
        [DllImport(Lib, EntryPoint = "vlqt_res_read_audio", CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_res_read_audio_drop(ulong id, IntPtr dst, int maxFrames, int channels, out vlqt_audio_tap_state state);
        /// <summary>The clock of the audio tap times in ns (steady clock: QPC on Windows = Stopwatch.GetTimestamp converted to ns).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_now_ns();

        /// <summary>
        /// Copies and consumes up to dst.Length / channels frames of one segment (interleaved). Returns the frames copied,
        /// <see cref="Result.BadArgument"/> (channels mismatch, not initialised) or <see cref="Result.UnknownId"/> (released id). An empty dst only fills the state.
        /// </summary>
        public static int ResReadAudio(ulong id, Span<float> dst, int channels, out vlqt_audio_tap_state state)
            => vlqt_res_read_audio(id, ref MemoryMarshal.GetReference(dst), channels > 0 ? dst.Length / channels : 0, channels, out state);

        /// <summary>Drops up to maxFrames frames of one segment without copying (catching up). Returns the frames dropped, <see cref="Result.BadArgument"/> or <see cref="Result.UnknownId"/>.</summary>
        public static int ResDropAudio(ulong id, int maxFrames, int channels, out vlqt_audio_tap_state state)
            => vlqt_res_read_audio_drop(id, IntPtr.Zero, maxFrames, channels, out state);

        // App fonts (LoadFont node): Qt GUI thread (vvvv main thread) only, reference counted per file in QtNative.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_font_add([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out ulong handle, ref byte families, int familiesCap, out int written, out int required);
        /// <summary>Drops one reference of a font handle; the font is removed at the end of the pump when none is left. <see cref="Result.WrongThread"/>, <see cref="Result.UnknownId"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_font_release(ulong handle);

        /// <summary>
        /// Registers a font file (or shares its registration) and takes one reference: <see cref="Result.Ok"/> with the handle
        /// and the family names of the file ('\n'-separated, never truncated), else the failure (<see cref="LastError"/>) and
        /// handle 0.
        /// </summary>
        public static Result FontAdd(string path, out ulong handle, out string families)
        {
            Span<byte> buffer = stackalloc byte[1024];
            var rc = vlqt_font_add(path, out handle, ref MemoryMarshal.GetReference(buffer), buffer.Length, out var written, out var required);
            if (rc == Result.BufferTooSmall)
            {
                // The names did not fit: nothing was copied and no reference taken, so one call with the required size is enough.
                var grown = new byte[required];
                rc = vlqt_font_add(path, out handle, ref grown[0], grown.Length, out written, out _);
                families = rc == Result.Ok ? FontFamilies(grown, written) : string.Empty;
                return rc;
            }
            families = rc == Result.Ok ? FontFamilies(buffer, written) : string.Empty;
            return rc;
        }

        // The family names without the terminating NUL (written includes it).
        private static string FontFamilies(ReadOnlySpan<byte> buffer, int written)
            => written > 1 ? System.Text.Encoding.UTF8.GetString(buffer[..(written - 1)]) : string.Empty;

        public static Result ResPoll(Span<byte> @out, out int written, out int required)
            => vlqt_res_poll(ref MemoryMarshal.GetReference(@out), @out.Length, out written, out required);
        public static int ResList(Span<vlqt_res_info> @out) => vlqt_res_list(ref MemoryMarshal.GetReference(@out), @out.Length);

        public static Result Apply(IntPtr w, ReadOnlySpan<byte> buf) => vlqt_window_apply(w, in MemoryMarshal.GetReference(buf), buf.Length);
        public static Result Poll(IntPtr w, Span<byte> @out, out int written, out int required)
            => vlqt_window_poll(w, ref MemoryMarshal.GetReference(@out), @out.Length, out written, out required);
    }
}
