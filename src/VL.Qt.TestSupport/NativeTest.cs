using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using VL.Qt.Interop;

namespace VL.Qt
{
    /// <summary>
    /// The test exports of QtNative for the SmokeTest and the unit tests: <c>vlqt_test_*</c> of vlqt.dll
    /// (vlqt_test.h) and of vlqt_testkit.dll (testkit/testkit.h, Windows only). Linked into the test projects, never part of
    /// the library. The unit test ApiDriftTests compares the imports with the headers.
    /// </summary>
    internal static class NativeTest
    {
        private const string Lib = "vlqt";
        private const string Kit = "vlqt_testkit";

        private static IntPtr s_kit;

        static NativeTest()
        {
            // Loads vlqt.dll and sets up the search path of its Qt DLLs before the first import is resolved.
            QtNativeLibrary.EnsureLoaded();
            NativeLibrary.SetDllImportResolver(typeof(NativeTest).Assembly, Resolve);
        }

        // "vlqt" is the library Native loaded (one instance of the DLL); the testkit sits next to it.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name == Lib)
                return QtNativeLibrary.Handle;
            if (name == Kit && OperatingSystem.IsWindows())
            {
                if (s_kit == IntPtr.Zero)
                    s_kit = NativeLibrary.Load(Path.Combine(QtNativeLibrary.Directory, Kit + ".dll"));
                return s_kit;
            }
            return IntPtr.Zero;
        }

        // --- Synthetic input (vlqt_test.h) ---

        /// <summary>Synthetic mouse event (<see cref="Wire.TestProtocol.Mouse"/>), x/y in window DIP.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_send_mouse(SceneHandle w, int type, float x, float y);
        /// <summary>Synthetic touch point event (<see cref="Wire.TestProtocol.Touch"/>) for point id, x/y in window DIP;
        /// <see cref="Native.Result.BadArgument"/> = unknown type or id state.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_send_touch(SceneHandle w, int type, int id, float x, float y);
        /// <summary>Mouse event (Move, Press, Release) queued like platform input, delivered later.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_queue_mouse(SceneHandle w, int type, float x, float y);
        /// <summary>Touch point event queued like platform input, delivered later; <see cref="Native.Result.BadArgument"/> = unknown type
        /// or id state.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_queue_touch(SceneHandle w, int type, int id, float x, float y);
        /// <summary>Synthetic wheel event at x/y (window DIP), angleDelta in eighths of a degree (120 = one notch).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_send_wheel(SceneHandle w, float x, float y, int dx, int dy);
        /// <summary>Key press (Qt::Key, Qt::KeyboardModifiers) to the window, e.g. F5 page cycling.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_send_key(SceneHandle w, int key, int modifiers);
        /// <summary>Key press + release with text (UTF-8, may be null) to the focus item.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_send_key_text(SceneHandle w, int key, int modifiers, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

        // --- Probes (vlqt_test.h) ---

        /// <summary>Grabs the window's frame into a PNG file; <see cref="Native.Result.NoData"/> = no image, <see cref="Native.Result.Io"/>
        /// = not saved.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_window_grab(SceneHandle w, [MarshalAs(UnmanagedType.LPUTF8Str)] string pngPath);
        /// <summary>vlqt_perf_stats computed from given samples and timestamps (ms, ascending) with a fixed budget.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_perf_eval(Native.vlqt_perf_sample[] samples, double[] t_ms, int n, double budget_ms, out Native.vlqt_perf_stats stats);
        /// <summary>vlqt_perf (the ~1 s summary) computed from given samples: the last 60.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_perf_summary_eval(Native.vlqt_perf_sample[] samples, int n, out Native.vlqt_perf perf);
        /// <summary>vlqt_host_pump_stats computed from given host pumps.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_perf_host_eval(Native.vlqt_host_pump[] pumps, int n, out Native.vlqt_host_pump_stats stats);
        /// <summary>Number of pointer handlers (input handlers) of item id, or a negative <see cref="Native.Result"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_handler_count(SceneHandle w, uint id);
        /// <summary>Sets the perf overlay of the window like the keys do (flags as <see cref="Native.vlqt_scene_get_overlay"/>).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_set_overlay(SceneHandle w, int flags);
        /// <summary>Busy-waits ms inside one phase (0 events, 1 sync, 2 render, 3 present, 4 vsync wait) of the next rendered frame.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_perf_spike(SceneHandle w, int phase, float ms);
        /// <summary>Plain text of an F5 page (0 Summary .. 4 Resources) with its NUL, a buffer export (vlqt.h).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_perf_text(SceneHandle w, int page, byte[]? out_, int cap, out int written, out int required);
        /// <summary>
        /// QRhi of window w (null handle = the upload QRhi on the shared device) and its native device
        /// (ID3D11Device* on D3D11); <see cref="Native.Result.NoData"/> = no QRhi yet.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_device_info(SceneHandle w, out IntPtr device, out IntPtr rhi);
        /// <summary>LUID of the adapter of the shared device (LowPart | HighPart &lt;&lt; 32), 0 if there is none yet; never creates the device.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong vlqt_test_device_luid();
        /// <summary>
        /// Makes the shared graphics device lost (<see cref="Wire.TestProtocol.DeviceLost"/>); QtNative rebuilds device,
        /// windows and resources in the next pumps. <see cref="Native.Result.Unsupported"/> = not possible (no shared device, REMOVE
        /// without VLQT_TEST_D3D11ON12=1, HANG, not D3D11), <see cref="Native.Result.BadArgument"/> = unknown mode.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_device_lost(int mode);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Native.Result vlqt_test_res_read_pixels(ulong id, ref byte @out, int cap, out int width, out int height, out int written, out int required);
        /// <summary>Writes a stripe test image (red, green, blue, white) as PNG/JPG by extension.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_write_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int width, int height, int quality);
        /// <summary>References of a font handle, 0 = removal pending, <see cref="Native.Result.UnknownId"/> = removed or unknown.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_font_refs(ulong handle);
        /// <summary>Last read/write fence values and render count of a bound scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_offscreen_state(SceneHandle scene, out ulong readFence, out ulong writeFence, out long renders);

        // --- Test resource kind (vlqt_test.h) ---

        /// <summary>Starts a Pending test resource (decode sleeps decodeMs, upload uses uploadBytes; flags 1 = decode fails, 2 = upload fails).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_res_start(ulong id, int decodeMs, long uploadBytes, int flags);
        /// <summary>State machine transition; <see cref="Native.Result.BadArgument"/> = rejected, <see cref="Native.Result.UnknownId"/> = stale id.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_res_transition(ulong id, int from, int to);
        /// <summary>Element reference +1 / -1.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_res_element_ref(ulong id, int delta);

        /// <summary>Content of a Ready image as RGBA8 premultiplied, a buffer export (<see cref="Native.Result.NoData"/> = not Ready).</summary>
        public static Native.Result ResReadPixels(ulong id, Span<byte> @out, out int width, out int height, out int written, out int required)
            => vlqt_test_res_read_pixels(id, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height, out written, out required);

        // --- Stand-in D3D11 device (vlqt_testkit.dll, testkit/testkit.h, Windows only) ---

        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vlqt_test_last_error();
        /// <summary>D3D11 device + immediate context (one reference each); adapter 0 = the adapter with LUID adapterLuid (0 = default), 1 = WARP.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_d3d11_create_device(int adapter, ulong adapterLuid, out IntPtr device, out IntPtr context);
        /// <summary>Releases context and device; returns the references left on the device.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_release_device(IntPtr device, IntPtr context);
        /// <summary>Shared Texture2D (RT | SR, SHARED | SHARED_NTHANDLE) on device and its NT handle.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_d3d11_create_shared_texture(IntPtr device, int width, int height, int dxgiFormat, out IntPtr texture, out IntPtr ntHandle);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        private static extern Native.Result vlqt_test_d3d11_read_texture(IntPtr texture, ref byte @out, int cap, out int width, out int height, out int dxgiFormat, out int written, out int required);
        /// <summary>COM reference count of obj without the probe's own reference.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_ref_count(IntPtr obj);
        /// <summary>IUnknown::Release; returns the references left.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_release(IntPtr obj);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern Native.Result vlqt_test_close_handle(IntPtr handle);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_process_handle_count();

        /// <summary>Text of the last failure of a testkit function (empty = none).</summary>
        public static string TestkitLastError() => Marshal.PtrToStringUTF8(vlqt_test_last_error()) ?? "";

        /// <summary>
        /// Creates a stand-in device on the adapter of QtNative's shared device (adapter 0; the default adapter if QtNative has
        /// none yet) or on WARP (adapter 1, an adapter mismatch for vlqt_offscreen_bind). Never creates QtNative's device.
        /// </summary>
        public static Native.Result CreateDevice(int adapter, out IntPtr device, out IntPtr context)
            => vlqt_test_d3d11_create_device(adapter, vlqt_test_device_luid(), out device, out context);

        /// <summary>Texture pixels via a staging copy, rows tightly packed, a buffer export.</summary>
        public static Native.Result TestReadTexture(IntPtr texture, Span<byte> @out, out int width, out int height, out int dxgiFormat, out int written, out int required)
            => vlqt_test_d3d11_read_texture(texture, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height, out dxgiFormat, out written, out required);
    }
}
