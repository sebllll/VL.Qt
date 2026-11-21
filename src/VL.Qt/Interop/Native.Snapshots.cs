using System;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        /// <summary>Starts grabbing element elementId of window w into a snapshot resource (asynchronous).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_snapshot_start(ulong id, SceneHandle w, uint elementId, float scale);

        /// <summary>Like vlqt_snapshot_start, scale so that the longest edge is &lt;= maxEdgePx and scale &lt;= 1 (pixels per DIP).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_snapshot_start_fit(ulong id, SceneHandle w, uint elementId, int maxEdgePx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_snapshot_read(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc, out int written, out int required);

        /// <summary>
        /// Pixels of the last finished snapshot (BGRA8 premultiplied), a buffer export: <see cref="Result.BufferTooSmall"/> with
        /// <paramref name="required"/> (desc set), <see cref="Result.NoData"/> none yet, <see cref="Result.UnknownId"/>.
        /// </summary>
        public static Result SnapshotRead(ulong id, Span<byte> dst, out vlqt_pixel_desc desc, out int written, out int required)
            => vlqt_snapshot_read(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc, out written, out required);
    }
}
