using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        /// <summary>
        /// Creates the (hidden) window with its initial geometry in DIP, set before show: x/y = outer top-left incl. frame
        /// (NaN = centered on the primary working area; a rect on no screen is centered too), width/height = client size
        /// (&lt; 1 = 600x400). swapInterval 0 = Present never waits, 1 = VSync (fixed at creation). Wayland ignores x/y.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SceneHandle vlqt_window_create(float x, float y, int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, int swapInterval);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_show(SceneHandle w);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_window_hide(SceneHandle w);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_take_close_request(SceneHandle w);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_is_visible(SceneHandle w);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_title(SceneHandle w, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_resize(SceneHandle w, int width, int height);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_icon(SceneHandle w, [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Path);

        /// <summary>Changes the <see cref="Protocol.WindowMode"/> bits in mask to their value in value, live.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_mode(SceneHandle w, uint mask, uint value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_mode(SceneHandle w);

        /// <summary>
        /// Sets the MSAA sample count of the window (&lt; 1 = none), clamped to what the device supports; returns the
        /// count used (&gt;= 1) or a negative <see cref="Result"/> (<see cref="Result.Unsupported"/> on an offscreen scene). A shown window is replaced inside the same handle (vlqt.h).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_set_samples(SceneHandle w, int requested);

        /// <summary>Sample count of the window's swap chain, or the count it will be created with; 0 = offscreen scene, <see cref="Result.NullHandle"/> = null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_window_get_samples(SceneHandle w);

        /// <summary>Takes the diagnostic keys (F2, F5, Shift+F5, F6, F7) of the window (on != 0, default) or lets them reach the scene (0); kept when the window is replaced.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_window_set_keys(SceneHandle w, int on);
    }
}
