using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
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

        public static Result ResPoll(Span<byte> @out, out int written, out int required)
            => vlqt_res_poll(ref MemoryMarshal.GetReference(@out), @out.Length, out written, out required);

        /// <summary>Blocks until Ready/Failed or timeout; returns the state (<see cref="Protocol.ResourceState"/>) or <see cref="Result.UnknownId"/>.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_res_wait(ulong id, int timeoutMs);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_res_list(ref vlqt_res_info @out, int cap);

        public static int ResList(Span<vlqt_res_info> @out) => vlqt_res_list(ref MemoryMarshal.GetReference(@out), @out.Length);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vlqt_res_shutdown();
    }
}
