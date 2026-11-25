using System;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    internal static unsafe partial class Native
    {
        /// <summary>ABI version of the library (VLQT_ABI_VERSION): protocol constants, struct layouts, signatures. Needs no init.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_get_abi_version();

        /// <summary>ABI version of the loaded library; null if it has no vlqt_app_get_abi_version (a library older than the check).</summary>
        public static int? ReadAbiVersion()
        {
            try { return vlqt_app_get_abi_version(); }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>
        /// Initializes Qt (once; a second call is a no-op). <paramref name="log"/> gets Qt's messages on any thread: a static
        /// [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] method that never throws (it would cross native frames).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_init([MarshalAs(UnmanagedType.LPUTF8Str)] string nativeDir, delegate* unmanaged[Cdecl]<int, byte*, byte*, void> log);

        /// <summary>
        /// Load + save file of Qt's pipeline cache for all windows created afterwards; null/empty = off.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_set_pipeline_cache([MarshalAs(UnmanagedType.LPUTF8Str)] string? path);

        /// <summary>
        /// One pump: only <paramref name="windows"/>[0..count) may render (the scenes of one vvvv app); null/0 = every window.
        /// <see cref="Result.Unsupported"/> when called inside the pump (a host callback in processEvents): the nested call does nothing.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_process_events_for(SceneHandle[]? windows, int count);

        /// <summary>
        /// The frame budget (vvvv's frame interval, ms) the windows of the next pumps judge their Qt work against.
        /// <see cref="Result.BadArgument"/> = not in (0, 1000).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_set_frame_budget(double ms);

        /// <summary>
        /// delivers the queued window system events (input) without rendering, at the start of the host frame.
        /// Number of delivered events, <see cref="Result.NotInitialized"/> = app not initialized.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_app_deliver_input();

        /// <summary>
        /// Advances the QML animations to the host time. They follow it forward and hold with it; a jump back does not rewind them.
        /// <see cref="Result.NotInitialized"/> = app not initialized, <see cref="Result.BadArgument"/> = seconds is NaN, infinite or out of range.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_app_advance_animations(double seconds);
    }
}
