using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_scene_destroy(SceneHandle w);

        /// <summary>Applies a command buffer (protocol.h). <see cref="Result.Ok"/>, <see cref="Result.NullHandle"/> or the apply range
        /// <see cref="Result.BadLength"/> ... <see cref="Result.Invalid"/>; text: <see cref="LastError"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_scene_apply(SceneHandle w, in byte buf, int len);

        public static Result Apply(SceneHandle w, ReadOnlySpan<byte> buf) => vlqt_scene_apply(w, in MemoryMarshal.GetReference(buf), buf.Length);

        /// <summary>Raw details of the last apply of a scene (vlqt.h); <c>reason</c> is valid only until the next apply: read through
        /// <see cref="GetApplyError"/>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct vlqt_apply_error
        {
            public int rc, command;
            public uint id;
            public ushort op, prop;
            public IntPtr reason;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_scene_get_apply_error(SceneHandle w, out vlqt_apply_error error);

        /// <summary>
        /// Structured last apply error of the scene (failing object id, op, property), with the reason copied. Allocates only
        /// when there is a reason, i.e. after an error.
        /// </summary>
        internal static Result GetApplyError(SceneHandle scene, out ApplyError error)
        {
            var result = vlqt_scene_get_apply_error(scene, out var raw);
            error = result == Result.Ok ? CopyApplyError(in raw) : new ApplyError(0, 0, 0, 0, 0, "");
            return result;
        }

        internal static ApplyError CopyApplyError(in vlqt_apply_error raw)
        {
            // An empty or missing text (success) needs no string.
            var reason = raw.reason == IntPtr.Zero || Marshal.ReadByte(raw.reason) == 0 ? "" : Marshal.PtrToStringUTF8(raw.reason) ?? "";
            return new ApplyError(raw.rc, raw.command, raw.id, raw.op, raw.prop, reason);
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_scene_poll(SceneHandle w, ref byte @out, int cap, out int written, out int required);

        public static Result Poll(SceneHandle w, Span<byte> @out, out int written, out int required)
            => vlqt_scene_poll(w, ref MemoryMarshal.GetReference(@out), @out.Length, out written, out required);

        /// <summary>Size (DIP) and device pixel ratio of a scene (vlqt.h); an offscreen scene has dpr 1.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_scene_metrics
        {
            public float width, height, dpr;
        }

        /// <summary>Live size and dpr of the scene of a window or offscreen scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_get_metrics(SceneHandle w, out vlqt_scene_metrics metrics);

        /// <summary>Number of frames with scene changes in window w (not the renders a grab triggers); &gt;= 0,
        /// <see cref="Result.NullHandle"/> null window.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_scene_get_change_count(SceneHandle w);

        /// <summary>Debug overlay for all elements of the window; per element: <see cref="Protocol.PropertyId.Debug"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_scene_set_debug(SceneHandle w, int on);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_scene_get_debug(SceneHandle w);

        /// <summary>Perf overlay flags of the window (bit0 meter F2, bit1 stats F5, bit3 freeze F6, bits 4..6 = page + 1).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_scene_get_overlay(SceneHandle w);
    }
}
