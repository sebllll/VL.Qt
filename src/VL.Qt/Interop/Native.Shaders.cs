using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        /// <summary>
        /// Starts or replaces the content of a shader resource (<see cref="Protocol.ResourceKind.Shader"/>) from a .qsb file or
        /// "builtin:&lt;name&gt;" (shipped shader), stage <see cref="Protocol.ShaderStage"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_shader_load(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int stage);

        /// <summary>
        /// Starts or replaces the content of a shader resource from full Qt GLSL source (#version 440), baked by QtNative
        /// in a worker (QShaderBaker), cached per hash of the source. Bake errors arrive as RESOURCE_EVENT_ERROR.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_shader_load_source(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage);

        /// <summary>
        /// Synchronous SPIR-V bake + reflection of Qt GLSL source, any thread. A buffer export: writes the UTF-8 text (format in
        /// vlqt.h, with its NUL) to buf. Use <see cref="Shaders.ShaderReflection.Reflect"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_shader_reflect([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int stage, ref byte buf, int bufLen, out int written, out int required);
    }
}
