using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VL.Qt
{
    /// <summary>
    /// The test exports of QtNative for the SmokeTest, the Probe and the unit tests: <c>vlqt_test_*</c> of vlqt.dll
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
            Native.EnsureLoaded();
            NativeLibrary.SetDllImportResolver(typeof(NativeTest).Assembly, Resolve);
        }

        // "vlqt" is the library Native loaded (one instance of the DLL); the testkit sits next to it.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name == Lib)
                return Native.Handle;
            if (name == Kit && OperatingSystem.IsWindows())
            {
                if (s_kit == IntPtr.Zero)
                    s_kit = NativeLibrary.Load(Path.Combine(Native.NativeDir, Kit + ".dll"));
                return s_kit;
            }
            return IntPtr.Zero;
        }

        // --- Synthetic input (vlqt_test.h) ---

        /// <summary>Synthetic mouse event (<see cref="Wire.TestProtocol.Mouse"/>), x/y in window DIP. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_mouse(IntPtr w, int type, float x, float y);
        /// <summary>Synthetic touch point event (<see cref="Wire.TestProtocol.Touch"/>) for point id, x/y in window DIP. 0 = ok, -1 null window, -2 unknown type or id state.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_touch(IntPtr w, int type, int id, float x, float y);
        /// <summary>Mouse event (Move, Press, Release) queued like platform input, delivered later. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_queue_mouse(IntPtr w, int type, float x, float y);
        /// <summary>Touch point event queued like platform input, delivered later. 0 = ok, -2 unknown type or id state.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_queue_touch(IntPtr w, int type, int id, float x, float y);
        /// <summary>Synthetic wheel event at x/y (window DIP), angleDelta in eighths of a degree (120 = one notch). 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_wheel(IntPtr w, float x, float y, int dx, int dy);
        /// <summary>Key press (Qt::Key, Qt::KeyboardModifiers) to the window, e.g. F5 page cycling. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_key(IntPtr w, int key, int modifiers);
        /// <summary>Key press + release with text (UTF-8, may be null) to the focus item. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_send_key_text(IntPtr w, int key, int modifiers, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

        // --- Probes (vlqt_test.h) ---

        /// <summary>Grabs the window's frame into a PNG file. 0 = ok, -1 null window, -2 no image, -3 not saved.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_window_grab(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string pngPath);
        /// <summary>vlqt_perf_stats computed from given samples and timestamps (ms, ascending) with a fixed budget.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_perf_eval(Native.vlqt_perf_sample[] samples, double[] t_ms, int n, double budget_ms, out Native.vlqt_perf_stats stats);
        /// <summary>vlqt_host_stats computed from given host frames.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_perf_host_eval(Native.vlqt_host_frame[] frames, int n, out Native.vlqt_host_stats stats);
        /// <summary>Number of pointer handlers (input handlers) of item id, &lt;0 = error.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_handler_count(IntPtr w, uint id);
        /// <summary>Busy-waits ms inside one phase (0 events, 1 sync, 2 render, 3 present, 4 vsync wait) of the next rendered frame. 0 = ok.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_perf_spike(IntPtr w, int phase, float ms);
        /// <summary>Plain text of an F5 page (0 Summary .. 4 Resources): bytes written, -(length + 1) if cap is too small, -1 null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_perf_text(IntPtr w, int page, byte[]? out_, int cap);
        /// <summary>
        /// QRhi of window w (IntPtr.Zero = the upload QRhi on the shared device) and its native device
        /// (ID3D11Device* on D3D11). 0 = ok, -2 no QRhi yet.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_device_info(IntPtr w, out IntPtr device, out IntPtr rhi);
        /// <summary>LUID of the adapter of the shared device (LowPart | HighPart &lt;&lt; 32), 0 if there is none yet; never creates the device.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong vlqt_test_device_luid();
        /// <summary>
        /// Makes the shared graphics device lost (<see cref="Wire.TestProtocol.DeviceLost"/>); QtNative rebuilds device,
        /// windows and resources in the next pumps. 0 = ok, -2 not possible (no shared device, or REMOVE without
        /// VLQT_TEST_D3D11ON12=1), -7 unknown mode.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_device_lost(int mode);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_test_res_read_pixels(ulong id, ref byte @out, int cap, out int width, out int height);
        /// <summary>Writes a stripe test image (red, green, blue, white) as PNG/JPG by extension.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_write_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int width, int height, int quality);
        /// <summary>References of a font handle, 0 = removal pending, -9 = removed or unknown.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_font_refs(ulong handle);
        /// <summary>Last read/write fence values and render count of a bound scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_offscreen_state(IntPtr scene, out ulong readFence, out ulong writeFence, out long renders);

        // --- Test resource kind (vlqt_test.h) ---

        /// <summary>Starts a Pending test resource (decode sleeps decodeMs, upload uses uploadBytes; flags 1 = decode fails, 2 = upload fails).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_start(ulong id, int decodeMs, long uploadBytes, int flags);
        /// <summary>State machine transition; 0 = done, -7 = rejected, -9 = stale id.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_transition(ulong id, int from, int to);
        /// <summary>Element reference +1 / -1.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_res_element_ref(ulong id, int delta);

        /// <summary>Content of a Ready image as RGBA8 premultiplied; bytes written, -required, -9 not Ready.</summary>
        public static int ResReadPixels(ulong id, Span<byte> @out, out int width, out int height)
            => vlqt_test_res_read_pixels(id, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height);

        // --- Stand-in D3D11 device (vlqt_testkit.dll, testkit/testkit.h, Windows only) ---

        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vlqt_test_last_error();
        /// <summary>D3D11 device + immediate context (one reference each); adapter 0 = the adapter with LUID adapterLuid (0 = default), 1 = WARP.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_create_device(int adapter, ulong adapterLuid, out IntPtr device, out IntPtr context);
        /// <summary>Releases context and device; returns the references left on the device.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_release_device(IntPtr device, IntPtr context);
        /// <summary>Shared Texture2D (RT | SR, SHARED | SHARED_NTHANDLE) on device and its NT handle.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_d3d11_create_shared_texture(IntPtr device, int width, int height, int dxgiFormat, out IntPtr texture, out IntPtr ntHandle);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_test_d3d11_read_texture(IntPtr texture, ref byte @out, int cap, out int width, out int height, out int dxgiFormat);
        /// <summary>COM reference count of obj without the probe's own reference.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_ref_count(IntPtr obj);
        /// <summary>IUnknown::Release; returns the references left.</summary>
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_com_release(IntPtr obj);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_close_handle(IntPtr handle);
        [DllImport(Kit, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_test_process_handle_count();

        /// <summary>Text of the last failure of a testkit function (empty = none).</summary>
        public static string TestkitLastError() => Marshal.PtrToStringUTF8(vlqt_test_last_error()) ?? "";

        /// <summary>
        /// Creates a stand-in device on the adapter of QtNative's shared device (adapter 0; the default adapter if QtNative has
        /// none yet) or on WARP (adapter 1, an adapter mismatch for vlqt_offscreen_bind). Never creates QtNative's device.
        /// </summary>
        public static int CreateDevice(int adapter, out IntPtr device, out IntPtr context)
            => vlqt_test_d3d11_create_device(adapter, vlqt_test_device_luid(), out device, out context);

        /// <summary>Texture pixels via a staging copy, rows tightly packed; bytes written, -required, &lt;0 error.</summary>
        public static int TestReadTexture(IntPtr texture, Span<byte> @out, out int width, out int height, out int dxgiFormat)
            => vlqt_test_d3d11_read_texture(texture, ref MemoryMarshal.GetReference(@out), @out.Length, out width, out height, out dxgiFormat);
    }
}
