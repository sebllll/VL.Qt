using System;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        // App fonts (LoadFont node): Qt GUI thread (vvvv main thread) only, reference counted per file in QtNative.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_font_add([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out ulong handle, ref byte families, int familiesCap, out int written, out int required);

        /// <summary>Drops one reference of a font handle; the font is removed at the end of the pump when none is left.
        /// <see cref="Result.WrongThread"/>, <see cref="Result.UnknownId"/>.</summary>
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
    }
}
