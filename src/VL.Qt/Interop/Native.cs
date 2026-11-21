using System;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    /// <summary>P/Invoke surface of QtNative vlqt.dll / libvlqt.so (C ABI, cdecl, UTF-8), one partial file per group of vlqt.h.</summary>
    internal static partial class Native
    {
        internal const string Lib = "vlqt";

        static Native() => QtNativeLibrary.EnsureLoaded();

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

        public static string LastError() => Marshal.PtrToStringUTF8(vlqt_last_error()) ?? "";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vlqt_last_error();
    }
}
